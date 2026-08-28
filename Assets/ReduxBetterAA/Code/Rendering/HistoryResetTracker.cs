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
        private const float TeleportRearmDistance = 250.0f;
        // Reset on a discontinuity, not on ordinary zoom animation. KSP can
        // update projection parameters in small increments for many frames;
        // clearing on every epsilon-sized change defeats temporal accumulation.
        private const float FieldOfViewResetDegrees = 5.0f;
        private const float OrthographicSizeResetFraction = 0.10f;
        private const float MinimumOrthographicSizeReset = 0.01f;
        private const float AspectResetTolerance = 0.001f;

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
        private bool _transformDiscontinuityLatched;

        public void Clear()
        {
            _hasPrevious = false;
            _camera = null;
            _transformDiscontinuityLatched = false;
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
                bool projectionChanged = _orthographic != camera.orthographic ||
                    Mathf.Abs(_aspect - camera.aspect) > AspectResetTolerance;
                if (!projectionChanged && camera.orthographic)
                {
                    float threshold = Mathf.Max(
                        MinimumOrthographicSizeReset,
                        Mathf.Abs(_orthographicSize) *
                            OrthographicSizeResetFraction
                    );
                    projectionChanged =
                        Mathf.Abs(_orthographicSize - camera.orthographicSize) >
                        threshold;
                }
                else if (!projectionChanged)
                {
                    projectionChanged =
                        Mathf.Abs(_fieldOfView - camera.fieldOfView) >
                        FieldOfViewResetDegrees;
                }
                if (projectionChanged)
                {
                    reasons |= HistoryResetReason.ProjectionChanged;
                }

                // A large rotation after a slow or stalled frame is not evidence of
                // a cut. KSP reports real camera selection changes explicitly, so
                // keep normal fast pans continuous instead of clearing history.
                float translationSquared =
                    (camera.transform.position - _position).sqrMagnitude;
                if (!suppressTransformDiscontinuity &&
                    translationSquared > TeleportDistance * TeleportDistance)
                {
                    if (!_transformDiscontinuityLatched)
                    {
                        reasons |= HistoryResetReason.Teleport;
                    }
                    _transformDiscontinuityLatched = true;
                }
                else if (translationSquared <=
                         TeleportRearmDistance * TeleportRearmDistance)
                {
                    _transformDiscontinuityLatched = false;
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
