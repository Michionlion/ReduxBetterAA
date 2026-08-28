using System;
using System.Collections.Generic;
using System.Reflection;
using AwesomeTechnologies.VegetationSystem;
using HarmonyLib;
using ReduxBetterAA.Patches;
using UnityEngine;
using UnityEngine.Rendering;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    /// <summary>
    /// Replaces the legacy direct indirect-vegetation submission with Unity's
    /// supported RenderMeshIndirect API. Static vegetation uses camera-only
    /// motion, preventing Unity's Built-in object pass from inventing history
    /// for GPU instances that do not provide previous transforms.
    /// </summary>
    internal sealed class VegetationMotionCompatibility : IDisposable
    {
        internal const bool DefaultEnabled = true;
        private const string HarmonyId =
            "ReduxBetterAA.VegetationMotionCompatibility";

        public static VegetationMotionCompatibility Current;

        private readonly ReduxLogger _logger;
        private Harmony _harmony;
        private MethodBase _patchedMethod;
        private bool _enabled = DefaultEnabled;
        private bool _patchInstalled;
        private bool _runtimeFailed;
        private bool _transientBypassLogged;
        private bool _disposed;
        private long _reroutedCalls;
        private long _transientBypasses;
        private string _status = "Vegetation camera-motion repair is loading.";

        public VegetationMotionCompatibility(ReduxLogger logger)
        {
            _logger = logger;
        }

        public bool Enabled => _enabled;
        public bool Available => _patchInstalled && !_runtimeFailed;
        public long ReroutedCalls => _reroutedCalls;
        public long TransientBypasses => _transientBypasses;
        public string Status => _status;

        public void Initialize()
        {
            if (_disposed || _patchInstalled)
            {
                return;
            }

            _harmony = new Harmony(HarmonyId);
            string reason;
            try
            {
                if (!VegetationMotionCompatibilityPatch.TryInstall(
                        _harmony,
                        out _patchedMethod,
                        out reason))
                {
                    _enabled = false;
                    _status = "Unavailable: " + reason;
                    _logger.LogWarning(
                        "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                        "repair is unavailable: " + reason + "."
                    );
                    return;
                }
            }
            catch (Exception exception)
            {
                _enabled = false;
                _status = "Unavailable: Harmony patch failed (" +
                    exception.GetType().Name + ").";
                _logger.LogError(
                    "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                    "repair failed to install: " + exception.GetType().Name +
                    ": " + exception.Message
                );
                return;
            }

            _patchInstalled = true;
            UpdateStatus();
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                "repair installed and enabled."
            );
        }

        public bool SetEnabled(bool enabled)
        {
            if (_disposed || !_patchInstalled || _runtimeFailed)
            {
                return false;
            }
            if (_enabled == enabled)
            {
                return false;
            }

            _enabled = enabled;
            UpdateStatus();
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                "repair " + (enabled ? "enabled." : "disabled.")
            );
            return true;
        }

        /// <summary>
        /// Returns true when Harmony should execute the original method.
        /// Command-buffer draws remain owned by the original vegetation renderer;
        /// the diagnosed flight failure uses this method's direct Graphics branch.
        /// </summary>
        internal bool TryRender(
            VegetationItemModelInfo vegetationItemModelInfo,
            Bounds cellBounds,
            int cameraIndex,
            int lodIndex,
            Camera selectedCamera,
            ShadowCastingMode shadowCastingMode,
            int layer,
            bool shadows,
            CommandBuffer commandBuffer,
            int visibleShaderDataBufferId,
            int indirectShaderDataBufferId)
        {
            if (!_enabled || !_patchInstalled || _runtimeFailed ||
                commandBuffer != null || vegetationItemModelInfo == null)
            {
                return true;
            }

            bool submitted = false;
            try
            {
                MaterialPropertyBlock properties =
                    vegetationItemModelInfo.GetLODMaterialPropertyBlock(lodIndex);
                GraphicsBuffer visibleBuffer =
                    vegetationItemModelInfo.GetLODVisibleBuffer(
                        lodIndex,
                        cameraIndex,
                        shadows);
                Mesh mesh = vegetationItemModelInfo.GetLODMesh(lodIndex);
                Material[] materials =
                    vegetationItemModelInfo.GetLODMaterials(lodIndex);
                List<GraphicsBuffer> argumentBuffers =
                    vegetationItemModelInfo.GetLODArgsBufferList(
                        lodIndex,
                        cameraIndex,
                        shadows);

                if (properties == null || visibleBuffer == null || mesh == null ||
                    materials == null || argumentBuffers == null)
                {
                    return true;
                }

                int drawCount = Mathf.Min(mesh.subMeshCount, materials.Length);
                if (drawCount <= 0 || argumentBuffers.Count < drawCount)
                {
                    return true;
                }
                for (int materialIndex = 0;
                    materialIndex < drawCount;
                    materialIndex++)
                {
                    if (materials[materialIndex] == null ||
                        argumentBuffers[materialIndex] == null)
                    {
                        return true;
                    }
                }

                properties.Clear();
                if (vegetationItemModelInfo.ShaderControler != null &&
                    vegetationItemModelInfo.ShaderControler.Settings.SampleWind)
                {
                    var windSamplers =
                        vegetationItemModelInfo.WindSamplerMeshRendererList;
                    MeshRenderer windSampler = windSamplers != null &&
                        cameraIndex >= 0 && cameraIndex < windSamplers.Count
                            ? windSamplers[cameraIndex]
                            : null;
                    if (windSampler != null)
                    {
                        windSampler.GetPropertyBlock(properties);
                    }
                }

                properties.SetBuffer(
                    visibleShaderDataBufferId,
                    visibleBuffer);
                properties.SetBuffer(
                    indirectShaderDataBufferId,
                    visibleBuffer);

                for (int materialIndex = 0;
                    materialIndex < drawCount;
                    materialIndex++)
                {
                    var renderParams = new RenderParams(materials[materialIndex])
                    {
                        camera = selectedCamera,
                        layer = layer,
                        lightProbeUsage = LightProbeUsage.Off,
                        matProps = properties,
                        motionVectorMode = MotionVectorGenerationMode.Camera,
                        receiveShadows = true,
                        shadowCastingMode = shadowCastingMode,
                        worldBounds = cellBounds
                    };
                    Graphics.RenderMeshIndirect(
                        renderParams,
                        mesh,
                        argumentBuffers[materialIndex],
                        1,
                        0);
                    submitted = true;
                }

                _reroutedCalls++;
                return false;
            }
            catch (ArgumentOutOfRangeException exception) when (!submitted)
            {
                // Vegetation camera/model lists are briefly rebuilt during
                // scene startup. The original submission owns that transient
                // state, so bypass this draw without permanently disabling the
                // compatibility path for the stable flight renderer.
                _transientBypasses++;
                if (!_transientBypassLogged)
                {
                    _transientBypassLogged = true;
                    _logger.LogWarning(
                        "[ReduxBetterAA/Motion] Indirect vegetation repair " +
                        "bypassed an incomplete startup draw (" +
                        exception.GetType().Name + "); later draws remain " +
                        "eligible."
                    );
                }
                return true;
            }
            catch (Exception exception)
            {
                FailRuntime(exception);
                // Avoid double-submitting submeshes if Unity failed after one
                // RenderMeshIndirect command was already accepted this frame.
                return !submitted;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _enabled = false;
            if (_harmony != null && _patchedMethod != null)
            {
                _harmony.Unpatch(
                    _patchedMethod,
                    HarmonyPatchType.Prefix,
                    HarmonyId);
            }
            _patchedMethod = null;
            _harmony = null;
            _patchInstalled = false;
            _status = "Disposed; original vegetation rendering restored.";
        }

        private void FailRuntime(Exception exception)
        {
            if (_runtimeFailed)
            {
                return;
            }
            _runtimeFailed = true;
            _enabled = false;
            _status = "Disabled after a runtime failure: " +
                exception.GetType().Name + ". Original rendering restored.";
            _logger.LogError(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion repair " +
                "failed closed; original rendering resumes next draw: " +
                exception.GetType().Name + ": " + exception.Message
            );
        }

        private void UpdateStatus()
        {
            _status = _enabled
                ? "Active: direct indirect vegetation uses RenderMeshIndirect " +
                  "with camera-only motion."
                : "Disabled: original DrawMeshInstancedIndirect vegetation " +
                  "submission is active.";
        }
    }
}
