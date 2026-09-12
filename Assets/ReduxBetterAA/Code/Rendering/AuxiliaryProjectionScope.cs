using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    internal interface IProjectionJitterSource
    {
        bool TryGetRasterProjection(Camera camera, out Matrix4x4 projection);
    }

    // An immediate auxiliary draw needs this frame's raster projection before
    // Camera.onPreCull. Restore it before returning to the game's update loop.
    internal struct AuxiliaryProjectionScope
    {
        private Camera _camera;
        private Matrix4x4 _original;
        private Matrix4x4 _applied;

        public void Apply(Camera camera, IProjectionJitterSource source)
        {
            if (camera == null || source == null ||
                !source.TryGetRasterProjection(camera, out Matrix4x4 projection)) return;
            if (camera.projectionMatrix == projection) return;
            _camera = camera;
            _original = camera.projectionMatrix;
            _applied = projection;
            camera.projectionMatrix = projection;
        }

        public void Restore()
        {
            if (_camera != null && _camera.projectionMatrix == _applied)
                _camera.projectionMatrix = _original;
            _camera = null;
        }
    }
}
