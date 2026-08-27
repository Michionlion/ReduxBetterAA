using System;
using System.Reflection;
using System.Reflection.Emit;
using ReduxLib.Logging;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    /// <summary>
    /// Reads PPv2's one-pixel auto-exposure result asynchronously. PPv2 owns the
    /// texture; this class only caches reflection delegates and the most recent
    /// finite scalar, so the render thread never blocks on a GPU readback.
    /// </summary>
    internal sealed class Ppv2ExposureReader : IDisposable
    {
        internal const float ReadbackIntervalSeconds = 0.1f;

        private const BindingFlags PrivateInstance =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private delegate object RendererGetter(PostProcessBundle bundle);
        private delegate RenderTexture ExposureTextureGetter(object renderer);

        private readonly ReduxLogger _logger;
        private readonly Action<AsyncGPUReadbackRequest> _readbackCompleted;
        private RendererGetter _rendererGetter;
        private ExposureTextureGetter _textureGetter;
        private PostProcessBundle _bundle;
        private Type _textureRendererType;
        private int _generation;
        private int _pendingGeneration;
        private int _lastRequestFrame = -1;
        private float _nextReadbackTime;
        private float _exposure = 1.0f;
        private bool _readbackPending;
        private bool _hasExposure;
        private bool _sourceAvailable;
        private bool _active;
        private bool _reportedSource;
        private string _unavailableReason = "PPv2 exposure has not been configured.";

        public Ppv2ExposureReader(ReduxLogger logger)
        {
            _logger = logger;
            _readbackCompleted = OnReadbackCompleted;
        }

        public bool HasExposure => _hasExposure;
        public bool SourceAvailable => _sourceAvailable;
        public float Exposure => _exposure;
        public string UnavailableReason => _unavailableReason;

        public void Configure(PostProcessLayer layer)
        {
            Deactivate();
            _generation++;
            if (layer == null)
            {
                _unavailableReason = "The resolve camera has no PPv2 layer.";
                return;
            }
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                _unavailableReason = "Asynchronous GPU readback is unsupported.";
                return;
            }

            try
            {
                _bundle = layer.GetBundle<AutoExposure>();
                FieldInfo rendererField = typeof(PostProcessBundle).GetField(
                    "m_Renderer",
                    PrivateInstance
                );
                if (_bundle == null || rendererField == null)
                {
                    _bundle = null;
                    _unavailableReason = "PPv2 auto-exposure internals do not match 3.2.2.";
                    return;
                }

                _rendererGetter = CreateRendererGetter(rendererField);
                _active = true;
                _unavailableReason =
                    "Waiting for PPv2 auto exposure to render its first sample.";
            }
            catch (Exception exception)
            {
                _bundle = null;
                _rendererGetter = null;
                _unavailableReason = "PPv2 exposure binding failed: " +
                    exception.GetType().Name;
            }
        }

        public bool TryGetExposure(out float exposure)
        {
            exposure = 1.0f;
            if (!_active || _bundle == null || _rendererGetter == null)
            {
                return false;
            }

            AutoExposure settings = _bundle.settings as AutoExposure;
            if (settings == null || !settings.enabled.value)
            {
                _hasExposure = false;
                _sourceAvailable = false;
                _unavailableReason = "PPv2 auto exposure is disabled by the active volume.";
                return false;
            }

            object renderer = _rendererGetter(_bundle);
            if (renderer == null)
            {
                _sourceAvailable = false;
                _unavailableReason =
                    "Waiting for PPv2 auto exposure to render its first sample.";
                return false;
            }
            if (!EnsureTextureGetter(renderer.GetType()))
            {
                return false;
            }

            RenderTexture texture = _textureGetter(renderer);
            if (texture == null || !texture.IsCreated() ||
                texture.width != 1 || texture.height != 1)
            {
                _sourceAvailable = false;
                _unavailableReason = "PPv2 did not expose a valid 1x1 exposure texture.";
                return false;
            }

            _sourceAvailable = true;
            RequestReadback(texture);
            if (!_hasExposure)
            {
                return false;
            }

            exposure = _exposure;
            return true;
        }

        public void Deactivate()
        {
            _generation++;
            _active = false;
            _bundle = null;
            _hasExposure = false;
            _sourceAvailable = false;
            _exposure = 1.0f;
            _reportedSource = false;
            _unavailableReason = "PPv2 exposure reader is inactive.";
        }

        public void Dispose()
        {
            Deactivate();
            _rendererGetter = null;
            _textureGetter = null;
            _textureRendererType = null;
        }

        internal static bool IsUsableExposure(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) &&
                   value > 0.0001f && value < 65504.0f;
        }

        private bool EnsureTextureGetter(Type rendererType)
        {
            if (_textureGetter != null && _textureRendererType == rendererType)
            {
                return true;
            }

            FieldInfo exposureField = rendererType.GetField(
                "m_CurrentAutoExposure",
                PrivateInstance
            );
            if (exposureField == null ||
                !typeof(RenderTexture).IsAssignableFrom(exposureField.FieldType))
            {
                _unavailableReason =
                    "PPv2 auto-exposure renderer does not expose its current texture.";
                return false;
            }

            _textureGetter = CreateTextureGetter(rendererType, exposureField);
            _textureRendererType = rendererType;
            return true;
        }

        private void RequestReadback(Texture texture)
        {
            int frame = Time.frameCount;
            float now = Time.unscaledTime;
            if (!ShouldScheduleReadback(
                    _readbackPending,
                    frame,
                    _lastRequestFrame,
                    now,
                    _nextReadbackTime))
            {
                return;
            }

            try
            {
                _lastRequestFrame = frame;
                _nextReadbackTime = now + ReadbackIntervalSeconds;
                _readbackPending = true;
                _pendingGeneration = _generation;
                AsyncGPUReadback.Request(
                    texture,
                    0,
                    TextureFormat.RFloat,
                    _readbackCompleted
                );
            }
            catch (Exception exception)
            {
                _readbackPending = false;
                _unavailableReason = "PPv2 exposure readback failed: " +
                    exception.GetType().Name;
            }
        }

        internal static bool ShouldScheduleReadback(
            bool pending,
            int frame,
            int lastRequestFrame,
            float now,
            float nextReadbackTime)
        {
            return !pending && frame != lastRequestFrame &&
                   now >= nextReadbackTime;
        }

        private void OnReadbackCompleted(AsyncGPUReadbackRequest request)
        {
            int requestGeneration = _pendingGeneration;
            _readbackPending = false;
            if (!_active || requestGeneration != _generation || request.hasError)
            {
                if (_active && request.hasError)
                {
                    _unavailableReason =
                        "Unity reported a PPv2 exposure readback error.";
                }
                return;
            }

            NativeArray<float> data = request.GetData<float>();
            if (data.Length < 1 || !IsUsableExposure(data[0]))
            {
                _unavailableReason = "PPv2 returned an invalid exposure value.";
                return;
            }

            _exposure = data[0];
            _hasExposure = true;
            _unavailableReason = string.Empty;
            if (!_reportedSource)
            {
                _reportedSource = true;
                _logger.LogInfo(
                    "[ReduxBetterAA/Exposure] Using PPv2's asynchronous 1x1 " +
                    "auto-exposure result for vendor pre-exposure."
                );
            }
        }

        private static RendererGetter CreateRendererGetter(FieldInfo field)
        {
            var method = new DynamicMethod(
                "ReduxBetterAA_GetPpv2Renderer",
                typeof(object),
                new[] { typeof(PostProcessBundle) },
                typeof(Ppv2ExposureReader),
                true
            );
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (RendererGetter)method.CreateDelegate(typeof(RendererGetter));
        }

        private static ExposureTextureGetter CreateTextureGetter(
            Type rendererType,
            FieldInfo field)
        {
            var method = new DynamicMethod(
                "ReduxBetterAA_GetPpv2ExposureTexture",
                typeof(RenderTexture),
                new[] { typeof(object) },
                typeof(Ppv2ExposureReader),
                true
            );
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, rendererType);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            return (ExposureTextureGetter)method.CreateDelegate(
                typeof(ExposureTextureGetter)
            );
        }
    }
}
