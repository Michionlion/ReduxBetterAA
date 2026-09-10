using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Rendering
{
    // A reversible claim on one camera's depth flags and PPv2 AA mode.
    // Keep projection ownership separate: it lasts one render, not one backend.
    internal struct SceneCameraState
    {
        private Camera _camera;
        private PostProcessLayer _layer;
        private DepthTextureMode _depthMode;
        private PostProcessLayer.Antialiasing _aaMode;
        private PostProcessLayer.Antialiasing _appliedAa;
        private DepthTextureMode _appliedDepth;

        public void Capture(Camera camera, PostProcessLayer layer,
            PostProcessLayer.Antialiasing mode = PostProcessLayer.Antialiasing.None,
            bool requestDepth = true)
        {
            Restore();
            _camera = camera;
            _layer = layer;
            if (_camera != null)
            {
                _depthMode = _camera.depthTextureMode;
                _appliedDepth = requestDepth ? _depthMode | DepthTextureMode.Depth | DepthTextureMode.MotionVectors : _depthMode;
                _camera.depthTextureMode = _appliedDepth;
            }
            if (_layer != null)
            {
                _aaMode = _layer.antialiasingMode;
                _appliedAa = mode;
                _layer.antialiasingMode = mode;
                _layer.ResetHistory();
            }
        }

        public void Restore()
        {
            if (_layer != null && _layer.antialiasingMode == _appliedAa)
            {
                _layer.antialiasingMode = _aaMode;
                _layer.ResetHistory();
            }
            if (_camera != null && _camera.depthTextureMode ==
                _appliedDepth)
                _camera.depthTextureMode = _depthMode;
            _camera = null;
            _layer = null;
        }
    }
}
