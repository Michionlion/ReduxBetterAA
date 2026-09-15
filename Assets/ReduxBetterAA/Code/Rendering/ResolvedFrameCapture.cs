using System;
using ReduxBetterAA.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    // Independent of the SR render-scale claim. A new backend acquisition has a
    // new ProducerId; Generation changes on extent or SR graph changes. FrameId
    // is Unity's real Time.frameCount and permits a later final-backbuffer join.
    internal readonly struct ResolvedFrameToken
    {
        internal readonly int ProducerId, Generation, Sequence, FrameId;
        internal readonly ulong CameraId;
        internal ResolvedFrameToken(int producerId, int generation, int sequence, int frameId, ulong cameraId)
        { ProducerId = producerId; Generation = generation; Sequence = sequence; FrameId = frameId; CameraId = cameraId; }
    }

    // Snapshot at pre-cull, before another camera or projection restoration can
    // change these values. GPU matrices use Unity column-vector multiplication:
    // clip = GpuProjection * WorldToView * world. No world-unit scale, complete
    // geometry coverage, final presentation extent or exposure transfer is inferred.
    internal readonly struct ResolvedFrameCamera
    {
        internal readonly Matrix4x4 NonJitteredProjection, GpuProjection, WorldToView, ViewProjection;
        internal readonly Vector2 JitterRenderPixels;
        internal readonly Rect Viewport;
        internal readonly float NearPlane, FarPlane, VerticalFovRadians, AspectRatio;
        internal readonly double RealtimeSeconds;
        internal readonly bool Orthographic, ReversedDepth, ProjectionRendersIntoTexture;

        internal ResolvedFrameCamera(Camera camera, Matrix4x4 nonJitteredProjection,
            Vector2 jitterRenderPixels, double realtimeSeconds)
        {
            NonJitteredProjection = nonJitteredProjection;
            ProjectionRendersIntoTexture = camera.targetTexture != null;
            GpuProjection = GL.GetGPUProjectionMatrix(nonJitteredProjection, ProjectionRendersIntoTexture);
            WorldToView = camera.worldToCameraMatrix;
            ViewProjection = GpuProjection * WorldToView;
            JitterRenderPixels = jitterRenderPixels;
            Viewport = camera.rect;
            NearPlane = camera.nearClipPlane; FarPlane = camera.farClipPlane;
            VerticalFovRadians = camera.fieldOfView * Mathf.Deg2Rad; AspectRatio = camera.aspect;
            RealtimeSeconds = realtimeSeconds;
            Orthographic = camera.orthographic; ReversedDepth = SystemInfo.usesReversedZBuffer;
        }
    }

    internal readonly struct ResolvedFrameRequest
    {
        internal readonly ResolvedFrameToken Token;
        internal readonly BackendSelection Backend;
        internal readonly ResolvedFrameCamera Camera;
        internal readonly Matrix4x4 PreviousViewProjection;
        internal readonly RenderTextureDescriptor ColorDescriptor;
        internal readonly int RenderWidth, RenderHeight;
        internal readonly SceneOutputFrame AcceptedSceneOutput;
        internal readonly HistoryResetReason ResetReason;
        internal readonly bool ResetHistory, NeedsFinalColorTarget, SuperResolution, LinearColorSpace;
        // This is the scalar passed to the AA/SR dispatch, not a measurement of
        // the final image's exposure. PPv2/output transforms still need matching.
        internal readonly float DispatchPreExposure, FrameTimeMilliseconds;
        // Sanitized motion already includes these component signs; never apply
        // them again as though this were the original Unity motion texture.
        internal readonly Vector2 MotionComponentSigns;

        internal ResolvedFrameRequest(in ResolvedFrameToken token, BackendSelection backend,
            in ResolvedFrameCamera camera, Matrix4x4 previousViewProjection,
            RenderTextureDescriptor colorDescriptor, int renderWidth, int renderHeight,
            in SceneOutputFrame acceptedSceneOutput, HistoryResetReason resetReason,
            bool resetHistory, bool needsFinalColorTarget, bool superResolution,
            float dispatchPreExposure, float frameTimeMilliseconds, Vector2 motionComponentSigns)
        {
            Token = token; Backend = backend; Camera = camera; PreviousViewProjection = previousViewProjection;
            ColorDescriptor = colorDescriptor; RenderWidth = renderWidth; RenderHeight = renderHeight;
            AcceptedSceneOutput = acceptedSceneOutput; ResetReason = resetReason;
            ResetHistory = resetHistory; NeedsFinalColorTarget = needsFinalColorTarget; SuperResolution = superResolution;
            DispatchPreExposure = dispatchPreExposure; FrameTimeMilliseconds = frameTimeMilliseconds;
            MotionComponentSigns = motionComponentSigns;
            LinearColorSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
        }
    }

    // Valid only inside Capture. These are backend/Unity-owned pixels, not leases.
    // Depth is the local current raw device depth, never TAA's linearized history.
    // Motion is render-sized normalized UV in the documented component convention.
    // ResolvedColor is the actual AA/SR result at this hook, before later overlays,
    // close-vessel composition and any final-backbuffer scaling/color conversion.
    internal readonly struct BorrowedResolvedFrame
    {
        internal readonly ResolvedFrameRequest Request;
        internal readonly RenderTexture ResolvedColor;
        internal readonly Texture RawDeviceDepth, SanitizedMotion;
        internal BorrowedResolvedFrame(in ResolvedFrameRequest request, RenderTexture color, Texture depth, Texture motion)
        { Request = request; ResolvedColor = color; RawDeviceDepth = depth; SanitizedMotion = motion; }
    }

    internal interface IResolvedFrameConsumer
    {
        // The consumer explicitly decides scene/camera eligibility and capacity.
        // false means decline, with no outstanding borrow. A throwing TryBegin is
        // responsible for unwinding its own partial acquisition. The optional TAA
        // target must have matching color storage/encoding and remain writable by
        // the producer until End. The consumer can reuse one preallocated slot.
        bool TryBegin(in ResolvedFrameRequest request, out RenderTexture finalColorTarget);
        // Enqueue all source GPU copies/dependencies here, in producer order.
        // Holding managed/COM references after return does not protect the pixels.
        void Capture(in BorrowedResolvedFrame frame);
        // Called exactly once for every accepted TryBegin, including rejection or
        // failure. captured only means Capture returned successfully, not that GPU
        // work retired. Keep destination slots/fences alive until actual retirement.
        void End(in ResolvedFrameRequest request, bool captured);
        // Idempotent backend lifecycle notification; it does not retire GPU work.
        void ProducerStopped(int producerId);
    }

    internal sealed class ResolvedFrameRegistration : IDisposable
    {
        internal readonly IResolvedFrameConsumer Consumer;
        internal bool Active { get; private set; } = true;
        internal string FailureReason { get; private set; } = string.Empty;
        internal ResolvedFrameRegistration(IResolvedFrameConsumer consumer) { Consumer = consumer; }
        internal void Fail(Exception error) { FailureReason = error.GetType().Name + ": " + error.Message; Dispose(); }
        public void Dispose()
        {
            if (!Active) return;
            Active = false;
            ResolvedFrameCapture.Unregister(this);
        }
    }

    // Main/render-callback thread only, like the AA backends themselves. No
    // consumer means no target allocation, GPU copy, descriptor or camera snapshot.
    // Capture exceptions are isolated from the backend's normal rendering path.
    internal sealed class ResolvedFrameCapture
    {
        private static ResolvedFrameRegistration _registration;
        private static int _nextProducerId;
        private Camera _camera;
        private BackendSelection _backend;
        private int _producerId, _generation, _sequence, _snapshotFrame = -1, _usedSequence;
        private int _lastPublishedFrame = -1, _lastRenderWidth, _lastRenderHeight, _lastOutputWidth, _lastOutputHeight;
        private int _lastSceneOwner, _lastSceneGeneration;
        private int _lifecycleEpoch, _borrowSerial, _resetVersion, _borrowResetVersion;
        private ResolvedFrameRegistration _snapshotRegistration, _lastRegistration, _borrowRegistration;
        private RenderTexture _borrowColor;
        private ResolvedFrameCamera _snapshot;
        private Matrix4x4 _previousViewProjection;
        private double _previousRealtime;
        private HistoryResetReason _resetReason;
        private ResolvedFrameRequest _borrowRequest;

        internal static bool HasConsumer => _registration != null && _registration.Active;
        internal static bool TryRegister(IResolvedFrameConsumer consumer, out ResolvedFrameRegistration registration)
        {
            registration = null;
            if (consumer == null || HasConsumer) return false;
            registration = _registration = new ResolvedFrameRegistration(consumer);
            return true;
        }
        internal static void Unregister(ResolvedFrameRegistration registration)
        { if (ReferenceEquals(_registration, registration)) _registration = null; }

        internal void Configure(Camera camera, BackendSelection backend)
        {
            Deactivate();
            _camera = camera; _backend = backend;
            _producerId = ++_nextProducerId;
            if (_producerId == 0) _producerId = ++_nextProducerId;
            _generation = 1; _sequence = 0; _usedSequence = 0;
            _resetReason = HistoryResetReason.FirstFrame;
        }

        internal void Reset(HistoryResetReason reason) { _resetReason |= reason; ++_resetVersion; }

        internal void Snapshot(Camera camera, Matrix4x4 projection, Vector2 jitterPixels)
        {
            if (!HasConsumer || camera == null || camera != _camera) return;
            Snapshot(camera, projection, jitterPixels, Time.frameCount, Time.realtimeSinceStartupAsDouble);
        }

        // Explicit clock overload keeps identity/reset tests deterministic without
        // simulating rendering or interpreting paused simulation deltaTime as FPS.
        internal void Snapshot(Camera camera, Matrix4x4 projection, Vector2 jitterPixels, int frameId, double realtimeSeconds)
        {
            if (!HasConsumer || camera == null || camera != _camera) return;
            if (_snapshotRegistration != null && !ReferenceEquals(_snapshotRegistration, _registration))
            {
                int epoch = _lifecycleEpoch;
                NotifyStopped(_snapshotRegistration, _producerId);
                if (epoch != _lifecycleEpoch || !HasConsumer || camera != _camera) return;
                ++_generation;
                _lastPublishedFrame = -1;
                _lastRegistration = null;
            }
            _snapshotRegistration = _registration;
            _snapshot = new ResolvedFrameCamera(camera, projection, jitterPixels, realtimeSeconds);
            _snapshotFrame = frameId;
            ++_sequence;
        }

        internal bool TryBegin(RenderTexture color, int renderWidth, int renderHeight,
            bool resetHistory, float dispatchPreExposure, Vector2 motionSigns,
            bool needsFinalColorTarget, bool superResolution, in SceneOutputFrame acceptedSceneOutput,
            out RenderTexture finalColorTarget, int frameId = -1)
        {
            finalColorTarget = null;
            if (!HasConsumer || _camera == null || _borrowRegistration != null) return false;
            if (frameId < 0) frameId = Time.frameCount;
            if (!ReferenceEquals(_snapshotRegistration, _registration) || _snapshotFrame != frameId ||
                _usedSequence == _sequence || _lastPublishedFrame >= frameId || color == null || !color.IsCreated()) return false;
            _usedSequence = _sequence;
            if (superResolution && (acceptedSceneOutput.OwnerId == 0 ||
                acceptedSceneOutput.FrameId != frameId || acceptedSceneOutput.CameraId != EntityId.ToULong(_camera.GetEntityId()) ||
                acceptedSceneOutput.RenderWidth != renderWidth || acceptedSceneOutput.RenderHeight != renderHeight ||
                acceptedSceneOutput.OutputWidth != color.width || acceptedSceneOutput.OutputHeight != color.height)) return false;

            bool changed = _lastPublishedFrame >= 0 && (_lastRenderWidth != renderWidth || _lastRenderHeight != renderHeight ||
                _lastOutputWidth != color.width || _lastOutputHeight != color.height ||
                _lastSceneOwner != acceptedSceneOutput.OwnerId || _lastSceneGeneration != acceptedSceneOutput.Generation);
            if (changed) { ++_generation; _resetReason |= HistoryResetReason.ResolutionChanged; }
            bool first = !ReferenceEquals(_lastRegistration, _registration) || _lastPublishedFrame < 0;
            bool gap = !first && frameId != _lastPublishedFrame + 1;
            var reason = _resetReason;
            if (first) reason |= HistoryResetReason.FirstFrame;
            if (gap) reason |= HistoryResetReason.InvalidInput;
            double elapsed = first ? 0 : _snapshot.RealtimeSeconds - _previousRealtime;
            if (elapsed <= 0 || double.IsNaN(elapsed) || double.IsInfinity(elapsed)) elapsed = 0;
            bool reset = resetHistory || reason != HistoryResetReason.None || elapsed == 0;
            var token = new ResolvedFrameToken(_producerId, _generation, _sequence, frameId, EntityId.ToULong(_camera.GetEntityId()));
            var request = new ResolvedFrameRequest(in token, _backend, in _snapshot,
                first ? _snapshot.ViewProjection : _previousViewProjection, color.descriptor,
                renderWidth, renderHeight, in acceptedSceneOutput, reason, reset, needsFinalColorTarget,
                superResolution, dispatchPreExposure, (float)(elapsed * 1000), motionSigns);
            var registration = _registration;
            int lifecycleEpoch = _lifecycleEpoch, resetVersion = _resetVersion;
            try
            {
                if (!registration.Consumer.TryBegin(in request, out finalColorTarget)) { finalColorTarget = null; return false; }
                // A consumer can trigger a mode/scene change synchronously. Its
                // accepted old request still needs End, but cannot enter the new
                // producer's borrow or temporal history.
                if (lifecycleEpoch != _lifecycleEpoch || request.Token.Sequence != _sequence)
                { SafeEnd(registration, in request, false); finalColorTarget = null; return false; }
                _borrowRegistration = registration;
                _borrowRequest = request;
                _borrowColor = needsFinalColorTarget ? finalColorTarget : color;
                _borrowResetVersion = resetVersion;
                ++_borrowSerial;
                if (!registration.Active || (needsFinalColorTarget && !ValidTarget(finalColorTarget, color)))
                { Abort(); finalColorTarget = null; return false; }
                return true;
            }
            catch (Exception error)
            {
                registration.Fail(error);
                if (lifecycleEpoch == _lifecycleEpoch && ReferenceEquals(_borrowRegistration, registration) &&
                    _borrowRequest.Token.Sequence == request.Token.Sequence) Abort();
                finalColorTarget = null;
                return false;
            }
        }

        internal static bool ValidTarget(RenderTexture target, RenderTexture reference) =>
            target != null && target != reference && target.IsCreated() && reference != null &&
            target.width == reference.width && target.height == reference.height &&
            target.graphicsFormat == reference.graphicsFormat && target.sRGB == reference.sRGB &&
            target.dimension == TextureDimension.Tex2D && target.volumeDepth == 1 &&
            target.antiAliasing == 1 && !target.useDynamicScale && !target.useMipMap &&
            target.descriptor.depthStencilFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None;

        internal void Publish(RenderTexture color, Texture rawDepth, Texture sanitizedMotion)
        {
            var registration = _borrowRegistration;
            if (registration == null) return;
            int lifecycleEpoch = _lifecycleEpoch, serial = _borrowSerial;
            bool captured = false;
            try
            {
                if (!registration.Active || color != _borrowColor || !TemporalTextures.Matches(color, _borrowRequest.ColorDescriptor.width,
                    _borrowRequest.ColorDescriptor.height) || !TemporalTextures.Matches(rawDepth, _borrowRequest.RenderWidth,
                    _borrowRequest.RenderHeight) || !TemporalTextures.Matches(sanitizedMotion, _borrowRequest.RenderWidth,
                    _borrowRequest.RenderHeight) || color.graphicsFormat != _borrowRequest.ColorDescriptor.graphicsFormat) return;
                var frame = new BorrowedResolvedFrame(in _borrowRequest, color, rawDepth, sanitizedMotion);
                registration.Consumer.Capture(in frame);
                captured = registration.Active && lifecycleEpoch == _lifecycleEpoch &&
                    serial == _borrowSerial && ReferenceEquals(registration, _borrowRegistration);
                if (!captured) return;
                _lastRegistration = registration;
                _lastPublishedFrame = _borrowRequest.Token.FrameId;
                _previousRealtime = _borrowRequest.Camera.RealtimeSeconds;
                _previousViewProjection = _borrowRequest.Camera.ViewProjection;
                _lastRenderWidth = _borrowRequest.RenderWidth; _lastRenderHeight = _borrowRequest.RenderHeight;
                _lastOutputWidth = color.width; _lastOutputHeight = color.height;
                _lastSceneOwner = _borrowRequest.AcceptedSceneOutput.OwnerId;
                _lastSceneGeneration = _borrowRequest.AcceptedSceneOutput.Generation;
                if (_borrowResetVersion == _resetVersion) _resetReason = HistoryResetReason.None;
            }
            catch (Exception error) { registration.Fail(error); }
            finally
            {
                if (serial == _borrowSerial && ReferenceEquals(registration, _borrowRegistration)) End(captured);
            }
        }

        internal void PublishResolved(RenderTexture color, Texture depth, Texture motion, bool reset,
            float dispatchPreExposure, Vector2 motionSigns, bool superResolution, in SceneOutputFrame acceptedSceneOutput)
        {
            if (!HasConsumer) return;
            if (TryBegin(color, depth.width, depth.height, reset, dispatchPreExposure, motionSigns,
                false, superResolution, in acceptedSceneOutput, out _)) Publish(color, depth, motion);
        }

        internal void Abort() { End(false); }
        private void End(bool captured)
        {
            var registration = _borrowRegistration;
            if (registration == null) return;
            var request = _borrowRequest;
            _borrowRegistration = null; // Clear before invoking external/reentrant code.
            _borrowColor = null;
            SafeEnd(registration, in request, captured);
        }

        private static void SafeEnd(ResolvedFrameRegistration registration, in ResolvedFrameRequest request, bool captured)
        {
            try { registration.Consumer.End(in request, captured); }
            catch (Exception error) { registration.Fail(error); }
        }

        internal void Deactivate()
        {
            int epoch = ++_lifecycleEpoch;
            Abort();
            if (epoch != _lifecycleEpoch) return;
            var registration = _snapshotRegistration;
            int producerId = _producerId;
            _camera = null; _producerId = 0; _snapshotFrame = -1; _lastPublishedFrame = -1;
            _lastRegistration = null; _snapshotRegistration = null;
            NotifyStopped(registration, producerId);
        }

        private static void NotifyStopped(ResolvedFrameRegistration registration, int producerId)
        {
            if (producerId == 0 || registration == null) return;
            try { registration.Consumer.ProducerStopped(producerId); }
            catch (Exception error) { registration.Fail(error); }
        }
    }
}
