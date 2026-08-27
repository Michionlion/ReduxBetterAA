using System;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    [Flags]
    internal enum HistoryResetReason
    {
        None = 0,
        FirstFrame = 1 << 0,
        BackendChanged = 1 << 1,
        SceneChanged = 1 << 2,
        CameraCut = 1 << 3,
        ProjectionChanged = 1 << 4,
        ResolutionChanged = 1 << 5,
        RenderScaleChanged = 1 << 6,
        QuickloadOrRevert = 1 << 7,
        VesselChanged = 1 << 8,
        OriginRebased = 1 << 9,
        Teleport = 1 << 10,
        InvalidInput = 1 << 11,
        SettingsChanged = 1 << 12,
        Manual = 1 << 13
    }

    internal sealed class HistoryResetTracker
    {
        private const float TeleportDistance = 1000.0f;
        private const float ProjectionTolerance = 0.0001f;

        private bool _hasPrevious;
        private Camera _camera;
        private Vector3 _position;
        private bool _orthographic;
        private float _fieldOfView;
        private float _orthographicSize;
        private float _aspect;
        private int _width;
        private int _height;
        private int _renderScalePercent;

        public void Clear()
        {
            _hasPrevious = false;
            _camera = null;
        }

        public HistoryResetReason Evaluate(
            Camera camera,
            int renderScalePercent,
            bool suppressTransformDiscontinuity = false)
        {
            if (camera == null || !camera.isActiveAndEnabled)
            {
                Clear();
                return HistoryResetReason.InvalidInput;
            }

            RenderTexture target = camera.targetTexture;
            int width = target == null ? camera.pixelWidth : target.width;
            int height = target == null ? camera.pixelHeight : target.height;
            if (width <= 0 || height <= 0)
            {
                Clear();
                return HistoryResetReason.InvalidInput;
            }

            HistoryResetReason reasons = HistoryResetReason.None;
            if (!_hasPrevious)
            {
                reasons |= HistoryResetReason.FirstFrame;
            }
            else
            {
                if (_camera != camera)
                {
                    reasons |= HistoryResetReason.CameraCut;
                }
                if (_width != width || _height != height)
                {
                    reasons |= HistoryResetReason.ResolutionChanged;
                }
                if (_renderScalePercent != renderScalePercent)
                {
                    reasons |= HistoryResetReason.RenderScaleChanged;
                }
                if (_orthographic != camera.orthographic ||
                    Mathf.Abs(_fieldOfView - camera.fieldOfView) > ProjectionTolerance ||
                    Mathf.Abs(_orthographicSize - camera.orthographicSize) > ProjectionTolerance ||
                    Mathf.Abs(_aspect - camera.aspect) > ProjectionTolerance)
                {
                    reasons |= HistoryResetReason.ProjectionChanged;
                }

                // A large rotation after a slow or stalled frame is not evidence of
                // a cut. KSP reports real camera selection changes explicitly, so
                // keep normal fast pans continuous instead of clearing history.
                if (!suppressTransformDiscontinuity &&
                    (camera.transform.position - _position).sqrMagnitude >
                    TeleportDistance * TeleportDistance)
                {
                    reasons |= HistoryResetReason.Teleport;
                }
            }

            _hasPrevious = true;
            _camera = camera;
            _position = camera.transform.position;
            _orthographic = camera.orthographic;
            _fieldOfView = camera.fieldOfView;
            _orthographicSize = camera.orthographicSize;
            _aspect = camera.aspect;
            _width = width;
            _height = height;
            _renderScalePercent = renderScalePercent;
            return reasons;
        }
    }
}
