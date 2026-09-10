using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Rendering
{
    // One owner per camera. Shared by Custom, DLAA and FSR2 so aborted frames
    // cannot leave a jittered projection behind in only one backend.
    internal struct CameraProjectionState
    {
        public Matrix4x4 Projection { get; private set; }
        private Camera _camera;
        private Matrix4x4 _nonJitteredProjection;
        private bool _transparentJitter;
        private bool _applied;
        private int _appliedFrame;

        public void Apply(Camera camera, Vector2 jitter)
        {
            if (_applied)
            {
                if (_appliedFrame == Time.frameCount && _camera == camera)
                    return;
                Restore();
            }
            _camera = camera;
            _applied = true;
            _appliedFrame = Time.frameCount;
            Projection = camera.projectionMatrix;
            _nonJitteredProjection = camera.nonJitteredProjectionMatrix;
            _transparentJitter = camera.useJitteredProjectionMatrixForTransparentRendering;
            camera.nonJitteredProjectionMatrix = Projection;
            camera.projectionMatrix = camera.orthographic
                ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
            camera.useJitteredProjectionMatrixForTransparentRendering = false;
        }

        public void Restore()
        {
            if (!_applied)
                return;
            if (_camera != null)
            {
                _camera.projectionMatrix = Projection;
                _camera.nonJitteredProjectionMatrix = _nonJitteredProjection;
                _camera.useJitteredProjectionMatrixForTransparentRendering = _transparentJitter;
            }
            _applied = false;
            _camera = null;
        }
    }
}
