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
        private ProjectionOverride _projection;
        private bool _transparentJitter;
        private bool _appliedTransparentJitter;
        private bool _applied;
        private int _appliedFrame;

        public Matrix4x4 GetRasterProjection(Camera camera, Vector2 jitter)
        {
            if (_applied && _appliedFrame == Time.frameCount && _camera == camera &&
                _projection.Owns(camera)) return camera.projectionMatrix;
            return camera.orthographic
                ? RuntimeUtilities.GetJitteredOrthographicProjectionMatrix(camera, jitter)
                : RuntimeUtilities.GetJitteredPerspectiveProjectionMatrix(camera, jitter);
        }

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
            // Compute before claiming state, so a failed provider cannot leave a
            // partial non-jittered/transparent override behind.
            Matrix4x4 raster = jitterFunction != null ? jitterFunction(camera, jitter) : GetRasterProjection(camera, jitter);
            Matrix4x4 nonJittered = camera.nonJitteredProjectionMatrix;
            _projection.Apply(camera, raster);
            _camera = camera;
            _applied = true;
            _appliedFrame = Time.frameCount;
            Projection = _projection.Original;
            _nonJitteredProjection = nonJittered;
            _transparentJitter = camera.useJitteredProjectionMatrixForTransparentRendering;
            camera.nonJitteredProjectionMatrix = Projection;
            _appliedTransparentJitter = jitterTransparentRendering;
            camera.useJitteredProjectionMatrixForTransparentRendering = _appliedTransparentJitter;
        }

        public void Restore()
        {
            if (!_applied)
                return;
            if (_camera != null)
            {
                if (_camera.nonJitteredProjectionMatrix.Equals(Projection))
                    _camera.nonJitteredProjectionMatrix = _nonJitteredProjection;
                if (_camera.useJitteredProjectionMatrixForTransparentRendering == _appliedTransparentJitter)
                    _camera.useJitteredProjectionMatrixForTransparentRendering = _transparentJitter;
            }
            _projection.Restore();
            _applied = false;
            _camera = null;
        }
    }
}
