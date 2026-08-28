using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Backends
{
    internal sealed class DisabledBackend : ITemporalBackend
    {
        private PostProcessLayer _resolveLayer;
        private PostProcessLayer _sharedLayer;
        private PostProcessLayer.Antialiasing _originalResolveMode;
        private PostProcessLayer.Antialiasing _originalSharedMode;
        private bool _configured;

        public string Id => "Off";
        public bool Active => false;

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            unsupportedReason = string.Empty;
            return true;
        }

        public bool Configure(TemporalCameraSet cameras, out string failureReason)
        {
            Deactivate();
            if (cameras != null &&
                cameras.SceneKind != TemporalSceneKind.Unsupported &&
                cameras.ResolveLayer == null &&
                cameras.SharedJitterLayer == null)
            {
                failureReason =
                    "the supported scene has not exposed a PostProcessLayer yet";
                return false;
            }
            _resolveLayer = cameras == null ? null : cameras.ResolveLayer;
            _sharedLayer = cameras == null ? null : cameras.SharedJitterLayer;
            if (_resolveLayer != null)
            {
                _originalResolveMode = _resolveLayer.antialiasingMode;
                _resolveLayer.antialiasingMode =
                    PostProcessLayer.Antialiasing.None;
                _resolveLayer.ResetHistory();
            }
            if (_sharedLayer != null && _sharedLayer != _resolveLayer)
            {
                _originalSharedMode = _sharedLayer.antialiasingMode;
                _sharedLayer.antialiasingMode =
                    PostProcessLayer.Antialiasing.None;
                _sharedLayer.ResetHistory();
            }
            _configured = true;
            failureReason = string.Empty;
            return true;
        }

        public void Tick(uint frameIndex)
        {
        }

        public void ResetHistory(HistoryResetReason reason)
        {
        }

        public void Deactivate()
        {
            if (!_configured)
            {
                return;
            }
            if (_resolveLayer != null)
            {
                _resolveLayer.antialiasingMode = _originalResolveMode;
                _resolveLayer.ResetHistory();
            }
            if (_sharedLayer != null && _sharedLayer != _resolveLayer)
            {
                _sharedLayer.antialiasingMode = _originalSharedMode;
                _sharedLayer.ResetHistory();
            }
            _resolveLayer = null;
            _sharedLayer = null;
            _configured = false;
        }

        public void Dispose()
        {
            Deactivate();
        }
    }
}
