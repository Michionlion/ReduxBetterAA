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
        private Matrix4x4 _appliedProjection;
        private bool _transparentJitter;
        private bool _appliedTransparentJitter;
        private bool _applied;
        private int _appliedFrame;

        public void Apply(Camera camera, Vector2 jitter,
            System.Func<Camera, Vector2, Matrix4x4> jitterFunction = null,
            bool jitterTransparentRendering = false)
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
            camera.projectionMatrix = jitterFunction != null ? jitterFunction(camera, jitter) : camera.orthographic
                ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
            _appliedProjection = camera.projectionMatrix;
            _appliedTransparentJitter = jitterTransparentRendering;
            camera.useJitteredProjectionMatrixForTransparentRendering = _appliedTransparentJitter;
        }

        public void Restore()
        {
            if (!_applied)
                return;
            if (_camera != null)
            {
                if (_camera.projectionMatrix == _appliedProjection) _camera.projectionMatrix = Projection;
                if (_camera.nonJitteredProjectionMatrix == Projection)
                    _camera.nonJitteredProjectionMatrix = _nonJitteredProjection;
                if (_camera.useJitteredProjectionMatrixForTransparentRendering == _appliedTransparentJitter)
                    _camera.useJitteredProjectionMatrixForTransparentRendering = _transparentJitter;
            }
            _applied = false;
            _camera = null;
        }
    }
}
