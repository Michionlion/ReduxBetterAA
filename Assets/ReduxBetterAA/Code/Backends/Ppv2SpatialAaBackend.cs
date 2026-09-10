using ReduxBetterAA.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Backends
{
    internal sealed class Ppv2SpatialAaBackend : ITemporalBackend
    {
        private readonly string _id;
        private readonly PostProcessLayer.Antialiasing _mode;
        private readonly bool _fastMode;
        private SceneCameraState _resolve, _shared;
        private PostProcessLayer _layer;
        private FastApproximateAntialiasing _fxaa;
        private SubpixelMorphologicalAntialiasing _smaa;
        private bool _originalFast, _createdSettings;
        private SubpixelMorphologicalAntialiasing.Quality _originalQuality;
        public Ppv2SpatialAaBackend(string id, PostProcessLayer.Antialiasing mode, bool fastMode)
        { _id = id; _mode = mode; _fastMode = fastMode; }
        public string Id => _id;
        public bool Active { get; private set; }
        public bool ProbeSupport(TemporalCameraSet cameras, out string reason)
        {
            reason = string.Empty;
            if (cameras == null || cameras.SceneKind == TemporalSceneKind.Unsupported ||
                cameras.ResolveCamera == null || cameras.ResolveLayer == null ||
                !cameras.ResolveCamera.isActiveAndEnabled || !cameras.ResolveLayer.enabled)
                reason = "No enabled scene camera and PostProcessLayer";
            else if (_mode == PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing &&
                !new SubpixelMorphologicalAntialiasing().IsSupported())
                reason = "PPv2 SMAA reports unsupported stereo capabilities";
            return reason.Length == 0;
        }
        public bool Configure(TemporalCameraSet cameras, out string reason)
        {
            Deactivate();
            if (!ProbeSupport(cameras, out reason)) return false;
            _layer = cameras.ResolveLayer;
            _resolve.Capture(null, _layer, _mode, false);
            if (cameras.SharedJitterLayer != _layer)
                _shared.Capture(null, cameras.SharedJitterLayer, requestDepth: false);
            if (_mode == PostProcessLayer.Antialiasing.FastApproximateAntialiasing)
            {
                _createdSettings = _layer.fastApproximateAntialiasing == null;
                _fxaa = _layer.fastApproximateAntialiasing ?? new FastApproximateAntialiasing();
                _layer.fastApproximateAntialiasing = _fxaa;
                _originalFast = _fxaa.fastMode;
                _fxaa.fastMode = _fastMode;
            }
            else
            {
                _createdSettings = _layer.subpixelMorphologicalAntialiasing == null;
                _smaa = _layer.subpixelMorphologicalAntialiasing ?? new SubpixelMorphologicalAntialiasing();
                _layer.subpixelMorphologicalAntialiasing = _smaa;
                _originalQuality = _smaa.quality;
                _smaa.quality = SubpixelMorphologicalAntialiasing.Quality.High;
            }
            Active = true;
            return true;
        }
        public void Tick(uint frameIndex) { }
        public void ResetHistory(HistoryResetReason reason) { }
        public void Deactivate()
        {
            if (_layer != null)
            {
                if (_fxaa != null && ReferenceEquals(_layer.fastApproximateAntialiasing, _fxaa) && _fxaa.fastMode == _fastMode)
                {
                    _fxaa.fastMode = _originalFast;
                    if (_createdSettings) _layer.fastApproximateAntialiasing = null;
                }
                if (_smaa != null && ReferenceEquals(_layer.subpixelMorphologicalAntialiasing, _smaa) &&
                    _smaa.quality == SubpixelMorphologicalAntialiasing.Quality.High)
                {
                    _smaa.quality = _originalQuality;
                    if (_createdSettings) _layer.subpixelMorphologicalAntialiasing = null;
                }
            }
            _shared.Restore(); _resolve.Restore();
            _layer = null; _fxaa = null; _smaa = null; _createdSettings = false; Active = false;
        }
        public void Dispose() => Deactivate();
    }
}
