using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    // Matrix values do not identify ownership: an explicit matrix can equal the
    // automatic one. Bind Unity's read-only mode getter once, without boxing in
    // rendering callbacks. If unavailable, temporal backends decline activation.
    internal struct ProjectionOverride
    {
        private static readonly Func<Camera, bool> IsExplicit = BindModeReader();
        internal static bool Available => IsExplicit != null;
        private Camera _camera;
        private Matrix4x4 _original, _applied;
        private bool _automatic;
        internal Matrix4x4 Original => _original;
        internal bool Owns(Camera camera) => _camera == camera && camera != null &&
            IsExplicit(camera) && camera.projectionMatrix.Equals(_applied);

        private static Func<Camera, bool> BindModeReader()
        {
            try
            {
                var property = typeof(Camera).GetProperty("projectionMatrixMode", BindingFlags.Instance | BindingFlags.NonPublic);
                var getter = property?.GetGetMethod(true);
                if (getter == null || !property.PropertyType.IsEnum ||
                    !Enum.IsDefined(property.PropertyType, "Explicit") ||
                    !Enum.IsDefined(property.PropertyType, "Implicit") ||
                    !Enum.IsDefined(property.PropertyType, "PhysicalPropertiesBased")) return null;
                var camera = Expression.Parameter(typeof(Camera), "camera");
                return Expression.Lambda<Func<Camera, bool>>(Expression.Equal(Expression.Call(camera, getter),
                    Expression.Constant(Enum.Parse(property.PropertyType, "Explicit"), property.PropertyType)), camera).Compile();
            }
            catch { return null; }
        }

        internal void Apply(Camera camera, Matrix4x4 projection)
        {
            Restore();
            if (!Available) throw new NotSupportedException("Unity projection ownership is unavailable.");
            _automatic = !IsExplicit(camera);
            _original = camera.projectionMatrix;
            _applied = projection;
            _camera = camera;
            camera.projectionMatrix = projection;
        }

        internal void Restore()
        {
            if (Owns(_camera))
            {
                if (_automatic) _camera.ResetProjectionMatrix();
                else _camera.projectionMatrix = _original;
            }
            _camera = null;
        }
    }
}
