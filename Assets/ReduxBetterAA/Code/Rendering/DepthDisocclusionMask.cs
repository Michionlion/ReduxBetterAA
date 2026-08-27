using System;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    /// <summary>
    /// Marks moving solid-geometry depth discontinuities for vendor temporal
    /// backends. No-depth regions remain unmarked so transparent and volumetric
    /// history is deliberately preserved.
    /// </summary>
    internal sealed class DepthDisocclusionMask : IDisposable
    {
        private const string ShaderAddress =
            "Assets/ReduxBetterAA/Shaders/DepthDisocclusionMask.shader";

        private static readonly int DepthTexture =
            Shader.PropertyToID("_DepthTexture");
        private static readonly int MotionTexture =
            Shader.PropertyToID("_MotionTexture");
        private static readonly int SourceDimensions =
            Shader.PropertyToID("_SourceDimensions");

        private readonly ReduxLogger _logger;
        private readonly Action _availabilityChanged;
        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderHandleValid;
        private Shader _shader;
        private Material _material;
        private RenderTexture _mask;
        private int _width;
        private int _height;
        private bool _disposed;
        private string _status = "Depth-disocclusion mask is loading.";

        public DepthDisocclusionMask(
            ReduxLogger logger,
            Action availabilityChanged)
        {
            _logger = logger;
            _availabilityChanged = availabilityChanged;
        }

        public bool Ready => _shader != null && _shader.isSupported;
        public string Status => _status;
        public long EstimatedMemoryBytes => _mask == null
            ? 0L
            : (long)_mask.width * _mask.height * 4L;

        public void Initialize()
        {
            if (_shaderHandleValid || _disposed)
            {
                return;
            }
            _shaderHandle = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            _shaderHandleValid = true;
            _shaderHandle.Completed += OnShaderLoaded;
        }

        public bool TryGenerate(
            Texture color,
            Texture depth,
            Texture motion,
            int width,
            int height,
            out Texture mask)
        {
            mask = null;
            if (_disposed || !Ready || color == null || depth == null ||
                motion == null || width <= 0 || height <= 0 ||
                !EnsureResources(width, height))
            {
                return false;
            }

            _material.SetTexture(DepthTexture, depth);
            _material.SetTexture(MotionTexture, motion);
            _material.SetVector(
                SourceDimensions,
                new Vector4(width, height, 1.0f / width, 1.0f / height)
            );
            Graphics.Blit(color, _mask, _material);
            mask = _mask;
            return true;
        }

        public void ReleaseResources()
        {
            if (_mask != null)
            {
                _mask.Release();
                UnityEngine.Object.Destroy(_mask);
                _mask = null;
            }
            _width = 0;
            _height = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReleaseResources();
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
                _material = null;
            }
            if (_shaderHandleValid)
            {
                _shaderHandle.Completed -= OnShaderLoaded;
                Addressables.Release(_shaderHandle);
                _shaderHandleValid = false;
            }
            _shader = null;
        }

        private bool EnsureResources(int width, int height)
        {
            if (_mask != null && _width == width && _height == height)
            {
                return true;
            }

            ReleaseResources();
            _mask = new RenderTexture(
                width,
                height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear
            )
            {
                name = "Redux Better AA Moving Depth-Edge Bias Mask",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = false,
                autoGenerateMips = false
            };
            _mask.Create();
            if (!_mask.IsCreated())
            {
                ReleaseResources();
                _status = "Moving depth-edge bias-mask creation failed.";
                return false;
            }

            _width = width;
            _height = height;
            _status = "Active: moving solid depth edges only; no-depth transparency history preserved.";
            _logger.LogInfo(
                "[ReduxBetterAA/Disocclusion] Vendor bias mask created for " +
                width + "x" + height + "."
            );
            return true;
        }

        private void OnShaderLoaded(AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded &&
                operation.Result != null && operation.Result.isSupported)
            {
                _shader = operation.Result;
                _material = new Material(_shader)
                {
                    name = "Redux Better AA Depth Disocclusion Mask Material",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _status = "Ready: moving solid depth edges will bias vendor history.";
                _logger.LogInfo(
                    "[ReduxBetterAA/Disocclusion] Vendor depth-edge mask loaded."
                );
            }
            else
            {
                _status = "Depth-disocclusion mask shader is unavailable; vendor AA continues without it.";
                _logger.LogWarning(
                    "[ReduxBetterAA/Disocclusion] Depth-edge mask failed to load; continuing without it."
                );
            }
            _availabilityChanged?.Invoke();
        }
    }
}
