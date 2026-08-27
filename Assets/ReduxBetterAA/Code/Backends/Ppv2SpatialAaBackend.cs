using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Backends
{
    /// <summary>
    /// Owns one of the spatial anti-aliasing effects already shipped in the
    /// game's PPv2 runtime.  This backend deliberately does not add jitter or
    /// history; it only selects the requested spatial effect on the final scene
    /// layer and prevents a second effect on the shared camera layer.
    /// </summary>
    internal sealed class Ppv2SpatialAaBackend : ITemporalBackend
    {
        private readonly string _id;
        private readonly PostProcessLayer.Antialiasing _mode;
        private readonly bool _fastMode;

        private PostProcessLayer _resolveLayer;
        private PostProcessLayer _sharedLayer;
        private PostProcessLayer.Antialiasing _originalResolveMode;
        private PostProcessLayer.Antialiasing _originalSharedMode;
        private bool _originalFastMode;
        private bool _originalKeepAlpha;
        private SubpixelMorphologicalAntialiasing.Quality _originalSmaaQuality;
        private bool _active;

        public Ppv2SpatialAaBackend(
            string id,
            PostProcessLayer.Antialiasing mode,
            bool fastMode)
        {
            _id = id;
            _mode = mode;
            _fastMode = fastMode;
        }

        public string Id => _id;
        public bool Active => _active;

        public bool ProbeSupport(
            TemporalCameraSet cameras,
            out string unsupportedReason)
        {
            if (cameras == null ||
                cameras.SceneKind == TemporalSceneKind.Unsupported)
            {
                unsupportedReason =
                    "the active game state has no supported scene output";
                return false;
            }
            if (cameras.ResolveCamera == null ||
                cameras.ResolveLayer == null)
            {
                unsupportedReason =
                    "the final scene camera or PostProcessLayer is unavailable";
                return false;
            }
            if (!cameras.ResolveCamera.isActiveAndEnabled ||
                !cameras.ResolveLayer.enabled)
            {
                unsupportedReason =
                    "the final scene camera or PostProcessLayer is disabled";
                return false;
            }
            if (_mode == PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing &&
                !new SubpixelMorphologicalAntialiasing().IsSupported())
            {
                unsupportedReason =
                    "PPv2 SMAA reports unsupported stereo capabilities";
                return false;
            }

            unsupportedReason = string.Empty;
            return true;
        }

        public bool Configure(
            TemporalCameraSet cameras,
            out string failureReason)
        {
            Deactivate();
            if (!ProbeSupport(cameras, out failureReason))
            {
                return false;
            }

            _resolveLayer = cameras.ResolveLayer;
            _sharedLayer = cameras.SharedJitterLayer;
            _originalResolveMode = _resolveLayer.antialiasingMode;

            if (_resolveLayer.fastApproximateAntialiasing == null)
            {
                _resolveLayer.fastApproximateAntialiasing =
                    new FastApproximateAntialiasing();
            }
            _originalFastMode =
                _resolveLayer.fastApproximateAntialiasing.fastMode;
            _originalKeepAlpha =
                _resolveLayer.fastApproximateAntialiasing.keepAlpha;

            if (_resolveLayer.subpixelMorphologicalAntialiasing == null)
            {
                _resolveLayer.subpixelMorphologicalAntialiasing =
                    new SubpixelMorphologicalAntialiasing();
            }
            _originalSmaaQuality =
                _resolveLayer.subpixelMorphologicalAntialiasing.quality;

            if (_sharedLayer != null && _sharedLayer != _resolveLayer)
            {
                _originalSharedMode = _sharedLayer.antialiasingMode;
                _sharedLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
            }

            _resolveLayer.fastApproximateAntialiasing.fastMode = _fastMode;
            // The game's existing High spatial choice is the PPv2 FXAA
            // quality variant.  SMAA is an additional PPv2 effect and uses its
            // shipped high-quality preset, matching the package default.
            _resolveLayer.subpixelMorphologicalAntialiasing.quality =
                SubpixelMorphologicalAntialiasing.Quality.High;
            _resolveLayer.antialiasingMode = _mode;
            _active = true;
            return true;
        }

        public void Tick(uint frameIndex)
        {
        }

        public void ResetHistory(HistoryResetReason reason)
        {
            // Spatial AA has no temporal history to reset.
        }

        public void Deactivate()
        {
            if (_resolveLayer != null)
            {
                _resolveLayer.antialiasingMode = _originalResolveMode;
                if (_resolveLayer.fastApproximateAntialiasing != null)
                {
                    _resolveLayer.fastApproximateAntialiasing.fastMode =
                        _originalFastMode;
                    _resolveLayer.fastApproximateAntialiasing.keepAlpha =
                        _originalKeepAlpha;
                }
                if (_resolveLayer.subpixelMorphologicalAntialiasing != null)
                {
                    _resolveLayer.subpixelMorphologicalAntialiasing.quality =
                        _originalSmaaQuality;
                }
            }
            if (_sharedLayer != null && _sharedLayer != _resolveLayer)
            {
                _sharedLayer.antialiasingMode = _originalSharedMode;
            }

            _resolveLayer = null;
            _sharedLayer = null;
            _active = false;
        }

        public void Dispose()
        {
            Deactivate();
        }
    }
}
