using System;
using System.Reflection;
using HarmonyLib;
using KSP.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    // A submission token belongs to one acquisition, graph and real rendered frame.
    // The backend owns the submitted texture; it must release this claim before
    // disposing that texture. Generated frames must never call TryGetFrame.
    internal readonly struct SceneOutputFrame
    {
        internal readonly int OwnerId, Sequence;
        internal readonly ulong CameraId;
        internal readonly int FrameId, Generation;
        internal readonly int RenderWidth, RenderHeight, OutputWidth, OutputHeight;

        internal SceneOutputFrame(int ownerId, ulong cameraId, int sequence, int frameId,
            int generation, int renderWidth, int renderHeight, int outputWidth, int outputHeight)
        {
            OwnerId = ownerId; CameraId = cameraId; Sequence = sequence;
            FrameId = frameId; Generation = generation;
            RenderWidth = renderWidth; RenderHeight = renderHeight;
            OutputWidth = outputWidth; OutputHeight = outputHeight;
        }

        internal bool Matches(in SceneOutputFrame current) => OwnerId != 0 &&
            OwnerId == current.OwnerId && CameraId == current.CameraId &&
            Sequence == current.Sequence && FrameId == current.FrameId &&
            Generation == current.Generation && RenderWidth == current.RenderWidth &&
            RenderHeight == current.RenderHeight && OutputWidth == current.OutputWidth &&
            OutputHeight == current.OutputHeight;
    }

    // Adapter for one inspected Redux presenter, not an alternative scene stack.
    // All private API access and its exact-build gate live here. Redux retains
    // allocation, source-camera mapping, raycasters and final UI presentation.
    internal sealed class ReduxSceneOutput : IDisposable
    {
        private static readonly Guid SupportedModule = new Guid("7d0cb6df-eda7-43ac-8386-a1c00da65d32");
        private static readonly PresenterAccess Access = PresenterAccess.TryCreate();
        private static int _nextOwnerId;
        internal static ReduxSceneOutput Current { get; private set; }
        internal static bool Applying { get; private set; }
        internal static bool CompatibleAssembly => Access != null;

        private RenderScalePresenter _presenter;
        private Camera _resolveCamera, _presentCamera, _closeVesselCamera;
        private Camera[] _sourceCameras;
        private RenderTexture _sceneTarget, _submittedOutput;
        private CommandBuffer _presentBuffer;
        private SceneOutputFrame _frame, _submittedFrame;
        private int _ownerId, _generation, _sequence, _lastSceneFrame = -1;
        private int _displayWidth, _displayHeight;
        private bool _ready, _subscribed;

        internal bool NativeCloseVessel { get; set; }
        internal bool Active => Current == this && _presenter != null;
        internal bool ReacquisitionPending { get; private set; }
        internal string FailureReason { get; private set; } = string.Empty;
        // No FG provider may interpret scene-only vectors as covering this pass.
        internal bool FrameGenerationInputsComplete => false;

        internal bool TryAcquire(Camera resolveCamera, out string reason)
        {
            Dispose();
            if (Current != null) return Fail("Another scene-output owner is active", out reason);
            if (!CompatibleAssembly || Application.unityVersion != "6000.5.8f1")
                return Fail("Scene output requires the inspected Redux 0.2.9.0.104521 / Unity 6000.5.8f1 player", out reason);
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                return Fail("Scene-output transport is currently gated to D3D11", out reason);
            if (resolveCamera == null || !resolveCamera.enabled || !resolveCamera.gameObject.activeInHierarchy)
                return Fail("No active scene resolve camera", out reason);
            var presenters = Resources.FindObjectsOfTypeAll<RenderScalePresenter>();
            RenderScalePresenter match = null;
            for (int i = 0; i < presenters.Length; i++)
            {
                var presenter = presenters[i];
                if (presenter == null || !presenter.gameObject.scene.IsValid()) continue;
                var sources = Access.Sources(presenter);
                if (!Contains(sources, resolveCamera)) continue;
                if (match != null) return Fail("The resolve camera belongs to multiple Redux presenters", out reason);
                match = presenter;
            }
            if (match == null) return Fail("Redux has no presenter for this scene camera", out reason);
            _presenter = match;
            _resolveCamera = resolveCamera;
            _presentCamera = Access.PresentCamera(match);
            _sourceCameras = Access.Sources(match);
            if (!ValidateSourceGraph(out reason)) { ClearClaim(); return false; }
            Transform close = resolveCamera.transform.Find("[PhysicsSpace] FlightCamera Close Vessel");
            _closeVesselCamera = close == null ? null : close.GetComponent<Camera>();
            _ownerId = ++_nextOwnerId;
            if (_ownerId == 0) _ownerId = ++_nextOwnerId;
            _generation++;
            Current = this;
            Camera.onPreCull += BeforeCameraRender;
            Camera.onPostRender += AfterCameraRender;
            _subscribed = true;
            reason = FailureReason = string.Empty;
            return true;
        }

        // Call after RenderScaleOwnership.Apply. Setter no-ops at an unchanged
        // percentage, so force the original allocation path once per acquisition.
        internal bool EnsureReady(out string reason)
        {
            if (!Active) return Fail("No scene-output claim", out reason);
            Applying = true;
            try { Access.ApplyScale(_presenter); }
            catch (Exception error) { return Fail("Redux scene target setup failed: " + error.Message, out reason); }
            finally { Applying = false; }
            _sceneTarget = Access.Target(_presenter);
            _presentBuffer = Access.Buffer(_presenter);
            if (!ValidateTargets(out reason)) return false;
            PutPresentationFirst(_presentCamera, _presentBuffer);
            _displayWidth = Screen.width;
            _displayHeight = Screen.height;
            _ready = true;
            ReacquisitionPending = false;
            reason = FailureReason = string.Empty;
            return true;
        }

        internal bool TryGetFrame(Camera camera, out SceneOutputFrame frame, out string reason)
        {
            frame = default;
            if (Active && camera == _resolveCamera) DetectOutputResize();
            if (!Active || !_ready || camera != _resolveCamera)
                return Fail("No ready scene output for this camera", out reason);
            if (!ValidateTargets(out reason) || !ValidateCloseVessel(out reason)) return false;
            _frame = new SceneOutputFrame(_ownerId, EntityId.ToULong(camera.GetEntityId()), ++_sequence,
                Time.frameCount, _generation, _sceneTarget.width, _sceneTarget.height,
                Screen.width, Screen.height);
            frame = _frame;
            reason = string.Empty;
            return true;
        }

        internal bool Submit(in SceneOutputFrame frame, RenderTexture output, out string reason)
        {
            if (Active) DetectOutputResize();
            if (!Active || !_ready || !frame.Matches(in _frame) ||
                frame.FrameId != Time.frameCount || frame.Generation != _generation)
                return Fail("Scene output submission belongs to an expired frame or graph", out reason);
            if (!ValidateTargets(out reason) || !ValidateCloseVessel(out reason)) return false;
            if (frame.OutputWidth != Screen.width || frame.OutputHeight != Screen.height ||
                !TemporalTextures.Matches(output, frame.OutputWidth, frame.OutputHeight) ||
                output == _sceneTarget || output.antiAliasing != 1)
                return Fail("Reconstruction output is not a separate created display-sized texture", out reason);
            _submittedOutput = output;
            _submittedFrame = frame;
            reason = FailureReason = string.Empty;
            return true;
        }

        // Diagnostics observe the existing submission without minting a new token
        // or changing runtime failure state. The image-effect capture hook runs
        // before Camera.onPostRender, so that later presentation check is omitted.
        internal bool TryGetSubmittedOutput(Camera camera, out RenderTexture output, out SceneOutputFrame frame)
        {
            output = null;
            frame = default;
            if (!Active || !_ready || ReacquisitionPending || camera != _resolveCamera ||
                !_submittedFrame.Matches(in _frame) || _submittedFrame.FrameId != Time.frameCount ||
                _submittedFrame.Generation != _generation || _submittedFrame.OutputWidth != Screen.width ||
                _submittedFrame.OutputHeight != Screen.height || !TryValidateTargets(out _) ||
                (_closeVesselCamera != null && _closeVesselCamera.isActiveAndEnabled) ||
                !TemporalTextures.Matches(_submittedOutput, Screen.width, Screen.height) ||
                _submittedOutput == _sceneTarget || _submittedOutput.antiAliasing != 1) return false;
            output = _submittedOutput;
            frame = _submittedFrame;
            return true;
        }

        private void BeforeCameraRender(Camera camera)
        {
            if (!Active || camera != _presentCamera || _presentBuffer == null) return;
            DetectOutputResize();
            bool currentScene = _ready && _lastSceneFrame == Time.frameCount && ValidateTargets(out _);
            bool valid = currentScene &&
                _submittedFrame.Matches(in _frame) && _submittedFrame.FrameId == Time.frameCount &&
                _submittedFrame.Generation == _generation &&
                ValidateCloseVessel(out _) && TemporalTextures.Matches(_submittedOutput, Screen.width, Screen.height);
            _presentBuffer.Clear();
            if (valid)
                _presentBuffer.Blit(_submittedOutput, BuiltinRenderTextureType.CameraTarget);
            else
            {
                // A current scene fallback is preferable to replaying old history.
                if (currentScene)
                    _presentBuffer.Blit(_sceneTarget, BuiltinRenderTextureType.CameraTarget);
                else
                {
                    _presentBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                    _presentBuffer.ClearRenderTarget(false, true, Color.black);
                }
                if (string.IsNullOrEmpty(FailureReason)) FailureReason = "No reconstruction output for the current rendered frame";
                TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.RenderScaleChanged);
            }
        }

        private void AfterCameraRender(Camera camera)
        {
            if (Active && camera == _resolveCamera)
                _lastSceneFrame = _ready && camera.targetTexture == _sceneTarget && ValidateTargets(out _)
                    ? Time.frameCount : -1;
        }

        private bool ValidateSourceGraph(out string reason)
        {
            bool valid = TryValidateSourceGraph(out reason);
            if (!valid) FailureReason = reason;
            return valid;
        }

        private bool TryValidateSourceGraph(out string reason)
        {
            if (_presentCamera == null || _sourceCameras == null || _sourceCameras.Length == 0 ||
                !Contains(_sourceCameras, _resolveCamera))
                return Reject("Redux presenter camera graph is incomplete", out reason);
            for (int i = 0; i < _sourceCameras.Length; i++)
            {
                Camera source = _sourceCameras[i];
                if (source == null || source.rect != new Rect(0, 0, 1, 1) ||
                    source.targetDisplay != 0 || source.depth > _resolveCamera.depth)
                    return Reject("Redux scene graph is not a full-screen ordered camera stack", out reason);
            }
            if (_presentCamera.depth <= _resolveCamera.depth || _presentCamera.targetTexture != null ||
                _presentCamera.targetDisplay != 0 || _presentCamera.rect != new Rect(0, 0, 1, 1))
                return Reject("Redux presentation camera no longer follows the scene camera", out reason);
            reason = string.Empty;
            return true;
        }

        private bool ValidateTargets(out string reason)
        {
            bool valid = TryValidateTargets(out reason);
            if (!valid) FailureReason = reason;
            return valid;
        }

        private bool TryValidateTargets(out string reason)
        {
            if (!Active || !Access.RenderingEnabled(_presenter) || !_presentCamera.enabled ||
                !ReferenceEquals(Access.Sources(_presenter), _sourceCameras) ||
                Access.PresentCamera(_presenter) != _presentCamera || Access.Target(_presenter) != _sceneTarget ||
                Access.Buffer(_presenter) != _presentBuffer || !TemporalTextures.IsCreated(_sceneTarget) ||
                _presentBuffer == null || Screen.width <= 0 || Screen.height <= 0)
                return Reject("Redux scene target or presenter changed", out reason);
            if (!TryValidateSourceGraph(out reason)) return false;
            int scale = Access.Scale(_presenter);
            if (scale < 50 || scale > 100 || _sceneTarget.width != Mathf.CeilToInt(Screen.width * (scale / 100f)) ||
                _sceneTarget.height != Mathf.CeilToInt(Screen.height * (scale / 100f)))
                return Reject("Redux scene dimensions do not match the reconstruction scale", out reason);
            for (int i = 0; i < _sourceCameras.Length; i++)
                if (_sourceCameras[i].targetTexture != _sceneTarget)
                    return Reject("Another owner changed a scene camera target", out reason);
            reason = string.Empty;
            return true;
        }

        private void DetectOutputResize()
        {
            if (!_ready || ReacquisitionPending || Screen.width <= 0 || Screen.height <= 0 ||
                (Screen.width == _displayWidth && Screen.height == _displayHeight)) return;
            // Screen dimensions can change before Redux's LateUpdate rebuilds
            // its target. Only a still-owned graph is eligible for that retry;
            // a foreign camera target remains a failure instead of being stolen.
            if (!ReferenceEquals(Access.Sources(_presenter), _sourceCameras) ||
                Access.PresentCamera(_presenter) != _presentCamera || Access.Target(_presenter) != _sceneTarget ||
                Access.Buffer(_presenter) != _presentBuffer || !TemporalTextures.IsCreated(_sceneTarget) ||
                !ValidateSourceGraph(out _)) return;
            for (int i = 0; i < _sourceCameras.Length; i++)
                if (_sourceCameras[i].targetTexture != _sceneTarget) return;
            InvalidateGraph("Output resolution changed; reconstruction must reacquire its graph", true);
        }

        private bool ValidateCloseVessel(out string reason)
        {
            if (_closeVesselCamera != null && _closeVesselCamera.enabled && _closeVesselCamera.gameObject.activeInHierarchy)
                return Fail(NativeCloseVessel
                    ? "Native close-vessel composition requires a verified pre-tone-map color and depth/motion contract"
                    : "The active close-vessel camera is outside the reconstructed scene graph", out reason);
            reason = string.Empty;
            return true;
        }

        internal static bool TryOverrideRenderScale(RenderScalePresenter presenter, out bool enabled)
        {
            enabled = false;
            var owner = Current;
            if (owner == null || owner._presenter != presenter || Access == null) return false;
            int scale = Access.Scale(presenter);
            enabled = Access.RenderingEnabled(presenter) && scale >= 50 && scale <= 100 &&
                ReferenceEquals(Access.Sources(presenter), owner._sourceCameras);
            // Preserve Redux's ordinary supersampling behavior for external writes.
            return scale <= 100;
        }

        internal static void PresenterChanging(RenderScalePresenter presenter)
        {
            var owner = Current;
            if (owner == null || owner._presenter != presenter) return;
            owner.InvalidateGraph("Redux presenter changed; reconstruction must reacquire its graph",
                !Applying && !RenderScaleOwnership.Applying);
        }

        private void InvalidateGraph(string reason, bool reacquire)
        {
            _generation++;
            _ready = false;
            _submittedOutput = null;
            _frame = _submittedFrame = default;
            _lastSceneFrame = -1;
            FailureReason = reason;
            if (reacquire)
            {
                ReacquisitionPending = true;
                TemporalCoordinator.Current?.MarkDirty(HistoryResetReason.RenderScaleChanged);
            }
        }

        internal static void PresenterBufferRebuilt(RenderScalePresenter presenter)
        {
            var owner = Current;
            if (owner == null || owner._presenter != presenter) return;
            owner._sceneTarget = Access.Target(presenter);
            owner._presentBuffer = Access.Buffer(presenter);
            if (owner._presentBuffer != null) PutPresentationFirst(owner._presentCamera, owner._presentBuffer);
        }

        // Called only when buffers change. Mutating the existing buffer each frame
        // preserves ordering; reattachment after Redux rebuild must precede UI blur.
        internal static void PutPresentationFirst(Camera camera, CommandBuffer present)
        {
            if (camera == null || present == null) return;
            int originalCount = camera.GetCommandBuffers(CameraEvent.AfterEverything).Length;
            // GetCommandBuffers returns new managed wrappers. Reference equality
            // (or a non-unique name) cannot identify the attached native buffer.
            // Unity's removal API uses its native identity and preserves the
            // relative order of every remaining consumer.
            camera.RemoveCommandBuffer(CameraEvent.AfterEverything, present);
            CommandBuffer[] buffers = camera.GetCommandBuffers(CameraEvent.AfterEverything);
            if (buffers.Length == originalCount) return;
            camera.RemoveCommandBuffers(CameraEvent.AfterEverything);
            camera.AddCommandBuffer(CameraEvent.AfterEverything, present);
            for (int i = 0; i < buffers.Length; i++)
                camera.AddCommandBuffer(CameraEvent.AfterEverything, buffers[i]);
        }

        public void Dispose()
        {
            bool owned = Current == this;
            if (_subscribed)
            {
                Camera.onPreCull -= BeforeCameraRender;
                Camera.onPostRender -= AfterCameraRender;
                _subscribed = false;
            }
            // Remove backend texture references before its resources are destroyed.
            if (owned && _presenter != null && Access.Buffer(_presenter) == _presentBuffer && _presentBuffer != null)
            {
                _presentBuffer.Clear();
                if (TemporalTextures.IsCreated(Access.Target(_presenter)))
                    _presentBuffer.Blit(Access.Target(_presenter), BuiltinRenderTextureType.CameraTarget);
            }
            if (owned) Current = null;
            // Restore only while the targets are still the ones Redux applied.
            // RenderScaleOwnership separately restores its claimed percentage.
            bool restore = owned && _presenter != null && _sourceCameras != null &&
                ReferenceEquals(Access.Sources(_presenter), _sourceCameras) && Access.Scale(_presenter) <= 100;
            if (restore)
                for (int i = 0; i < _sourceCameras.Length; i++)
                    if (_sourceCameras[i] == null || _sourceCameras[i].targetTexture != Access.Target(_presenter)) restore = false;
            if (restore)
            {
                RenderTexture previousTarget = Access.Target(_presenter);
                Applying = true;
                try
                {
                    Access.ApplyScale(_presenter);
                    // CopyFrom can leave the late camera pointing at the released
                    // scene target until Redux next applies its camera shot.
                    if (_closeVesselCamera != null && _closeVesselCamera.targetTexture == previousTarget &&
                        _resolveCamera != null)
                        _closeVesselCamera.targetTexture = _resolveCamera.targetTexture;
                }
                catch (Exception error) { Debug.LogException(error); }
                finally { Applying = false; }
            }
            ClearClaim();
        }

        private void ClearClaim()
        {
            _presenter = null; _resolveCamera = _presentCamera = _closeVesselCamera = null;
            _sourceCameras = null; _sceneTarget = _submittedOutput = null; _presentBuffer = null;
            _frame = _submittedFrame = default; _ready = false; _lastSceneFrame = -1;
            _displayWidth = _displayHeight = 0;
            ReacquisitionPending = false;
            _ownerId = 0;
        }

        private bool Fail(string value, out string reason) { reason = FailureReason = value; return false; }
        private static bool Reject(string value, out string reason) { reason = value; return false; }
        private static bool Contains(Camera[] cameras, Camera camera)
        {
            if (cameras == null) return false;
            for (int i = 0; i < cameras.Length; i++) if (cameras[i] == camera) return true;
            return false;
        }

        private sealed class PresenterAccess
        {
            internal readonly AccessTools.FieldRef<RenderScalePresenter, Camera[]> Sources;
            internal readonly AccessTools.FieldRef<RenderScalePresenter, Camera> PresentCamera;
            internal readonly AccessTools.FieldRef<RenderScalePresenter, RenderTexture> Target;
            internal readonly AccessTools.FieldRef<RenderScalePresenter, CommandBuffer> Buffer;
            internal readonly AccessTools.FieldRef<RenderScalePresenter, int> Scale;
            internal readonly AccessTools.FieldRef<RenderScalePresenter, bool> RenderingEnabled;
            internal readonly Action<RenderScalePresenter> ApplyScale;

            private PresenterAccess()
            {
                Sources = AccessTools.FieldRefAccess<RenderScalePresenter, Camera[]>("_sourceCameras");
                PresentCamera = AccessTools.FieldRefAccess<RenderScalePresenter, Camera>("_presentCamera");
                Target = AccessTools.FieldRefAccess<RenderScalePresenter, RenderTexture>("_renderTarget");
                Buffer = AccessTools.FieldRefAccess<RenderScalePresenter, CommandBuffer>("_presentBuffer");
                Scale = AccessTools.FieldRefAccess<RenderScalePresenter, int>("_renderScalePercent");
                RenderingEnabled = AccessTools.FieldRefAccess<RenderScalePresenter, bool>("_renderingEnabled");
                ApplyScale = (Action<RenderScalePresenter>)typeof(RenderScalePresenter).GetMethod("ApplyRenderScale",
                    BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate(typeof(Action<RenderScalePresenter>));
            }

            internal static PresenterAccess TryCreate()
            {
                try
                {
                    return typeof(RenderScalePresenter).Module.ModuleVersionId == SupportedModule ? new PresenterAccess() : null;
                }
                catch { return null; }
            }
        }
    }
}
