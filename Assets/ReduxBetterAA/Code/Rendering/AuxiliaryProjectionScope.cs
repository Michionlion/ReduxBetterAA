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
        private ProjectionOverride _projection;

        public void Apply(Camera camera, IProjectionJitterSource source)
        {
            Restore();
            if (camera == null || source == null ||
                !source.TryGetRasterProjection(camera, out Matrix4x4 projection)) return;
            if (camera.projectionMatrix.Equals(projection)) return;
            _projection.Apply(camera, projection);
        }

        public void Restore()
        {
            _projection.Restore();
        }
    }
}
