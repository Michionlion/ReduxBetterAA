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

        public void Capture(Camera camera, PostProcessLayer layer)
        {
            Restore();
            _camera = camera;
            _layer = layer;
            if (_camera != null)
            {
                _depthMode = _camera.depthTextureMode;
                _camera.depthTextureMode |= DepthTextureMode.Depth | DepthTextureMode.MotionVectors;
            }
            if (_layer != null)
            {
                _aaMode = _layer.antialiasingMode;
                _layer.antialiasingMode = PostProcessLayer.Antialiasing.None;
                _layer.ResetHistory();
            }
        }

        public void Restore()
        {
            if (_layer != null)
            {
                _layer.antialiasingMode = _aaMode;
                _layer.ResetHistory();
            }
            if (_camera != null)
                _camera.depthTextureMode = _depthMode;
            _camera = null;
            _layer = null;
        }
    }
}
