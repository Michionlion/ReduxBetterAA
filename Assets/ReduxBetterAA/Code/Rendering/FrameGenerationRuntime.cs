using System;
using System.Collections;
using System.IO;
using ReduxBetterAA.Configuration;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ReduxBetterAA.Rendering
{
    // Lives separately from the mod entry point so unloading can continue the
    // main-thread HWND retirement and EOF pump until all native leases retire.
    internal sealed class FrameGenerationRuntime : MonoBehaviour, IResolvedFrameConsumer
    {
        private static readonly System.Collections.Generic.List<MotionVectorSanitizer> QuarantinedMotion =
            new System.Collections.Generic.List<MotionVectorSanitizer>();
        private sealed class Slot
        {
            internal readonly FrameGenerationSurfaces Surfaces = new FrameGenerationSurfaces();
            internal ulong Ticket;
            internal int FrameId = -1;
        }
        private readonly Slot[] _slots = { new Slot(), new Slot(), new Slot() };
        private readonly ulong[] _eofTickets = new ulong[8];
        private readonly WaitForEndOfFrame _waitEndOfFrame = new WaitForEndOfFrame();
        private FrameGenerationNative _native;
        private FrameGenerationNative.Status _status;
        private FrameGenerationPlayerLoop _loop;
        private ResolvedFrameRegistration _registration;
        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderRequested, _probeComplete, _shutdown, _faulted, _initialized;
        private bool _runtimeInstalled, _hostReady, _nvidiaInstalled, _amdInstalled, _probeDraining;
        private bool _nativeOwnerReady;
        private uint _completedProbes, _renderWidth, _renderHeight, _configuredWidth, _configuredHeight;
        private bool _reversedDepth, _configuredReversedDepth;
        private ulong _deviceGeneration;
        private Material _material;
        private MotionVectorSanitizer _frameGenerationMotion;
        private Slot _borrow;
        private Camera _camera, _closeVessel;
        private int _borrowFrame=-1, _lastCaptureFrame=-1, _shutdownFrame=-1;
        private int _simulationFrame=-1, _renderFrame=-1;
        private ulong _lastCaptureTicket;
        private uint _mode=uint.MaxValue;
        private FrameGenerationMode _lastRequested;
        private string _reason=string.Empty;
        private float _nextDiagnosticRefresh;
        private Coroutine _eofPump;
        private CommandBuffer _events;

        internal static FrameGenerationRuntime Create()
        {
            var api=FrameGenerationNative.TryBind(out string reason);
            if (api == null) { FrameGenerationAvailability.SetRuntimeState(false,reason); return null; }
            var host=new GameObject("Redux Better AA frame generation") { hideFlags=HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            var runtime=host.AddComponent<FrameGenerationRuntime>();
            runtime.Initialize(api);
            return runtime;
        }
        private void Initialize(FrameGenerationNative api)
        {
            _native=api;
            _events=new CommandBuffer { name="Redux Better AA frame-generation events" };
            _nvidiaInstalled=File.Exists(api.ProviderPath) && Directory.Exists(api.RuntimeDirectory);
            _amdInstalled=api.SupportsAmd && File.Exists(api.AmdProviderPath) && Directory.Exists(api.AmdRuntimeDirectory);
            _runtimeInstalled=_nvidiaInstalled || _amdInstalled;
            _loop=new FrameGenerationPlayerLoop(BeginSimulation,BeginRender);
            _hostReady=_loop.Install();
            if (!_hostReady) { Fail("The current Unity PlayerLoop has no unique FG marker boundaries."); }
            if (!ResolvedFrameCapture.TryRegister(this,out _registration)) Fail("Another resolved-frame consumer owns the capture input.");
            _hostReady &= _registration != null;
            // Initialization can occur after this frame's Update. Establish the
            // native owner before the first coroutine EOF, without enabling FG.
            TickNativeOwner(false);
            _initialized=true;
            _eofPump=StartCoroutine(EndOfFrames());
        }

        private void Update()
        {
            if (!_initialized || _native == null) return;
            try { Tick(); }
            catch (Exception error) { Fail("Frame-generation host failed: "+error.Message); }
        }
        private void Tick()
        {
            RetireTickets();
            if (!_native.ReadStatus(out _status))
            { _nativeOwnerReady=false; Fail("Could not read frame-generation runtime status."); return; }
            if (_deviceGeneration != 0 && _deviceGeneration != _status.DeviceGeneration)
            {
                _completedProbes=0; _probeComplete=false; _probeDraining=false;
                FrameGenerationAvailability.SetNvidiaSupport(0);
                FrameGenerationAvailability.SetAmdSupport(0,0,0,0,0,0);
            }
            _deviceGeneration=_status.DeviceGeneration;
            var requested=FrameGenerationAvailability.Requested;
            if (requested != _lastRequested) { _lastRequested=requested; if (_hostReady) _faulted=false; }
            if (!_shutdown && _registration != null && !_registration.Active)
            { _hostReady=false; Fail("FG capture stopped: "+_registration.FailureReason); }
            bool probeReady=IsProbe(_mode) && _status.State == 2 && _status.ChildLeaseRetained == 0 &&
                _status.ProviderPending == 0 && _status.ChildWindow == 0;
            if (IsProbe(_mode) && (probeReady || _status.State == 5))
            {
                _completedProbes |= ProbeBit(_mode);
                _probeDraining=true;
                if (probeReady)
                {
                    if (_mode == 5) FrameGenerationAvailability.SetNvidiaSupport(_status.NvidiaMultiplierMask);
                    var extended=_native.ExtendedStatus;
                    if (_native.SupportsAmd) FrameGenerationAvailability.SetAmdSupport(extended.AmdBestMask,
                        extended.AmdBestFamily,extended.AmdBestVersion,extended.AmdCompatibilityMask,
                        extended.AmdCompatibilityFamily,extended.AmdCompatibilityVersion);
                }
                else _reason=_native.ReadReason();
            }
            if (_probeDraining && _mode == 0 && _status.State == 0 && _status.ChildWindow == 0 &&
                _status.ProviderPending == 0 && _status.InputPoolPending == 0) _probeDraining=false;
            _probeComplete=NextProbe() == 0;
            if (_status.State == 5 && !IsProbe(_mode) && !_probeDraining)
            {
                Fail(_native.ReadReason());
            }
            if (Time.unscaledTime >= _nextDiagnosticRefresh)
            {
                _nextDiagnosticRefresh=Time.unscaledTime+1;
                if (!_shutdown && !_faulted && !_loop.IsInstalled) Fail("Another PlayerLoop change removed the FG markers.");
                if (!_faulted) _reason=_native.ReadReason();
            }
            bool installed=_runtimeInstalled;
            uint desired=_shutdown || _faulted || !installed || _probeDraining ? 0u : !_probeComplete ? NextProbe() :
                ToNativeMode(FrameGenerationAvailability.Selected);
            if (IsAmd(desired) && (_renderWidth == 0 || _renderHeight == 0)) desired=0;
            uint width=IsAmd(desired) ? _renderWidth : 0, height=IsAmd(desired) ? _renderHeight : 0;
            bool reversed=IsAmd(desired) && _reversedDepth;
            if (_mode != desired || _configuredWidth != width || _configuredHeight != height || _configuredReversedDepth != reversed)
            {
                uint result=_native.SetMode(desired,width,height,reversed);
                if (result > 2) { Fail("Frame-generation configuration rejected: "+result); return; }
                _mode=desired; _configuredWidth=width; _configuredHeight=height; _configuredReversedDepth=reversed;
            }
            bool eligible=!_shutdown && !_faulted && SceneEligible();
            TickNativeOwner(eligible);
            if (!_nativeOwnerReady) return;
            // This owner tick can hide a completed image as its real-frame age
            // advances. Report the resulting visibility, not the pre-tick read.
            if (!_native.ReadStatus(out _status) || !HasRegisteredNativeEvents(in _status))
            { _nativeOwnerReady=false; Fail("Could not read frame-generation status after the owner tick."); return; }
            if (IsActiveMode(desired) && !_shaderRequested) LoadShader();
            // The native host validates completed-image freshness against the
            // render EOF frontier, bounded queue lag and monotonic time. A
            // second main-thread age limit mislabels healthy queued frames.
            bool active=IsActiveMode(desired) && eligible && _status.State == 3 && _status.ChildVisible != 0 &&
                _status.GeneratedPresentationObserved != 0;
            string reason = !installed ? "The optional frame-generation provider runtime is not installed." :
                requested == FrameGenerationMode.Off ? string.Empty :
                !eligible ? "Frame generation is suspended for the current scene or camera." : _reason;
            FrameGenerationAvailability.SetRuntimeState(active,reason);
            if (_shutdown && Time.frameCount > _shutdownFrame+2 && _status.State == 0 &&
                _status.ChildWindow == 0 && _status.ChildLeaseRetained == 0 && _status.ProviderPending == 0 &&
                _status.InputPoolPending == 0 && _status.PendingTickets == 0 && TicketsRetired())
                Destroy(gameObject);
        }

        private bool SceneEligible()
        {
            var coordinator=TemporalCoordinator.Current;
            if (coordinator == null || !coordinator.FrameGenerationCameraEligible || coordinator.ResolveCamera == null) return false;
            var camera=coordinator.ResolveCamera;
            if (_camera != camera)
            {
                _camera=camera;
                var close=camera.transform.Find("[PhysicsSpace] FlightCamera Close Vessel");
                _closeVessel=close == null ? null : close.GetComponent<Camera>();
            }
            if (!camera.isActiveAndEnabled || (HDROutputSettings.main != null && HDROutputSettings.main.active) ||
                (_closeVessel != null && _closeVessel.isActiveAndEnabled) || camera.orthographic ||
                camera.rect != new Rect(0,0,1,1)) return false;
            // Check the live state as well as the coordinator's cached graph.
            var state=TemporalCameraDiscovery.ReadCurrentGameState();
            return state == KSP.Game.GameState.FlightView || state == KSP.Game.GameState.KerbalSpaceCenter;
        }

        private void TickNativeOwner(bool eligible)
        {
            _nativeOwnerReady=false;
            uint frame=unchecked((uint)Time.frameCount);
            if (frame == 0) return; // The native ABI requires a real nonzero frame.
            // The first EOF can precede Update after late initialization. Tick
            // binds the owner but does not return the registered event IDs.
            if (!HasRegisteredNativeEvents(in _status) &&
                (!_native.ReadStatus(out _status) || !HasRegisteredNativeEvents(in _status)))
            {
                _status=default;
                Fail("Frame-generation registered render events are unavailable.");
                return;
            }
            uint result=_native.Tick(frame,eligible ? 1u : 0u);
            _nativeOwnerReady=NativeOwnerTickSucceeded(result);
            if (!_nativeOwnerReady) Fail("Frame-generation owner tick rejected: "+result);
        }

        internal static bool NativeOwnerTickSucceeded(uint result) => result == 0 || result == 1;

        internal static bool HasRegisteredNativeEvents(in FrameGenerationNative.Status status) =>
            status.Size == FrameGenerationNative.StatusBytes && status.Version == FrameGenerationNative.Abi &&
            status.Loaded == 1 && status.Renderer == 2 && status.EndOfFrameEventId > 0 &&
            status.EndOfFrameEventId <= int.MaxValue && status.CaptureEventId < status.EndOfFrameEventId &&
            status.EndOfFrameEventId-status.CaptureEventId == 1;

        internal static bool CanQueueNativeEndOfFrame(bool ownerReady,uint frame,in FrameGenerationNative.Status status) =>
            ownerReady && frame != 0 && HasRegisteredNativeEvents(in status);

        public bool TryBegin(in ResolvedFrameRequest request, out RenderTexture finalColorTarget)
        {
            finalColorTarget=null;
            bool sceneEligible=SceneEligible();
            // AMD contexts need the producer's actual render extent. Observe it
            // even before activation; do not guess it from the physical window.
            if (sceneEligible && request.Token.FrameId == Time.frameCount && request.RenderWidth > 0 && request.RenderHeight > 0 &&
                request.Token.CameraId == EntityId.ToULong(_camera.GetEntityId()))
            { _renderWidth=(uint)request.RenderWidth; _renderHeight=(uint)request.RenderHeight; _reversedDepth=request.Camera.ReversedDepth; }
            if (_shutdown || _faulted || !IsActiveMode(_mode) || _material == null ||
                _frameGenerationMotion == null || !_frameGenerationMotion.Ready || _borrow != null ||
                _status.State < 2 || _status.State > 3 || _status.SourceFormat != 28 || _status.SourceWindowed != 1 ||
                !sceneEligible || request.Token.FrameId != Time.frameCount || (!IsAmd(_mode) && request.Token.FrameId != _renderFrame) ||
                (IsAmd(_mode) && (_configuredWidth != request.RenderWidth || _configuredHeight != request.RenderHeight ||
                    _configuredReversedDepth != request.Camera.ReversedDepth)) ||
                request.Token.FrameId == _lastCaptureFrame ||
                request.Token.CameraId != EntityId.ToULong(_camera.GetEntityId()) || request.Camera.Orthographic ||
                request.Camera.Viewport != new Rect(0,0,1,1) || request.RenderWidth < 1 || request.RenderHeight < 1 ||
                _status.SourceWidth == 0 || _status.SourceHeight == 0 ||
                !SameAspect(request.ColorDescriptor.width,request.ColorDescriptor.height,(int)_status.SourceWidth,(int)_status.SourceHeight)) return false;
            for (int i=0;i<_slots.Length;i++)
            {
                var slot=_slots[i];
                if (slot.Ticket != 0 || slot.Surfaces.BorrowedByNative) continue;
                if (!slot.Surfaces.Ensure(in request,(int)_status.SourceWidth,(int)_status.SourceHeight)) continue;
                _borrow=slot; _borrowFrame=request.Token.FrameId;
                finalColorTarget=request.NeedsFinalColorTarget ? slot.Surfaces.TaaFinal : null;
                return true;
            }
            return false;
        }
        public void Capture(in BorrowedResolvedFrame frame)
        {
            var slot=_borrow;
            if (slot == null || _borrowFrame != frame.Request.Token.FrameId || !SceneEligible())
                throw new InvalidOperationException("FG camera changed during capture");
            slot.Surfaces.CaptureColorAndDepth(in frame,_material);
            if (!_frameGenerationMotion.TrySanitizeFrame(in frame,slot.Surfaces.Depth,true,out Texture protectedMotion))
                throw new InvalidOperationException("Frame-generation motion protection could not validate its snapshot");
            // All draws and the intermediate->slot copy precede this ticket's
            // native event on Unity's D3D11 queue. The intermediate can therefore
            // be reused next frame; only the immutable slot is borrowed natively.
            slot.Surfaces.CaptureMotion(protectedMotion,_material);
            var request=frame.Request;
            var capture=new FrameGenerationNative.Capture { Size=368, Version=FrameGenerationNative.Abi,
                RealFrameId=unchecked((uint)request.Token.FrameId), SceneEligible=1,
                RenderWidth=(uint)request.RenderWidth, RenderHeight=(uint)request.RenderHeight,
                DisplayWidth=(uint)slot.Surfaces.Hudless.width, DisplayHeight=(uint)slot.Surfaces.Hudless.height,
                ColorTransfer=1, MotionFormat=1, HudlessColor=slot.Surfaces.HudlessPointer,
                RawDepth=slot.Surfaces.DepthPointer, NormalizedMotion=slot.Surfaces.MotionPointer,
                View=new FrameGenerationNative.Camera(in request) };
            uint result=_native.PrepareCapture(ref capture,out ulong ticket,out IntPtr packet);
            if (result != 0)
            {
                if (result > 2) Fail("Frame-generation capture rejected: "+result);
                return; // Backpressure declines the frame, preserving AA.
            }
            slot.Ticket=ticket; slot.FrameId=request.Token.FrameId;
            slot.Surfaces.BeginNativeBorrow();
            QueueRenderEvent((int)_status.CaptureEventId,packet);
            _lastCaptureTicket=ticket; _lastCaptureFrame=request.Token.FrameId;
        }
        public void End(in ResolvedFrameRequest request,bool captured)
        { if (_borrowFrame == request.Token.FrameId) { _borrow=null; _borrowFrame=-1; } }
        public void ProducerStopped(int producerId)
        { _borrow=null; _borrowFrame=-1; _lastCaptureTicket=0; _lastCaptureFrame=-1; }

        private IEnumerator EndOfFrames()
        {
            while (true)
            {
                yield return _waitEndOfFrame;
                if (_native == null) yield break;
                QueueEndOfFrame();
            }
        }
        private void QueueEndOfFrame()
        {
            uint frame=unchecked((uint)Time.frameCount);
            if (!CanQueueNativeEndOfFrame(_nativeOwnerReady,frame,in _status)) return;
            // The native owner needs a real EOF even while Off, in menus and
            // during retirement; it must never reuse yesterday's ownership ticket.
            for (int i=0;i<_eofTickets.Length;i++)
            {
                if (_eofTickets[i] != 0) continue;
                ulong capture=!_shutdown && !_faulted && _lastCaptureFrame == Time.frameCount ? _lastCaptureTicket : 0;
                uint result=_native.PrepareEndOfFrame(frame,capture,out ulong ticket,out IntPtr packet);
                if (result == 0)
                {
                    _eofTickets[i]=ticket;
                    QueueRenderEvent((int)_status.EndOfFrameEventId,packet);
                }
                else if (result != 2) Fail("Frame-generation EOF packet rejected: "+result);
                return;
            }
            Fail("Frame-generation EOF queue has not retired; presentation is draining.");
        }
        private void QueueRenderEvent(int eventId, IntPtr packet)
        {
            _events.Clear();
            _events.IssuePluginEventAndData(_native.RenderEvent,eventId,packet);
            Graphics.ExecuteCommandBuffer(_events);
            _events.Clear();
        }
        private void BeginSimulation()
        {
            _simulationFrame=-1; _renderFrame=-1;
            if (!_shutdown && !_faulted && _mode >= 1 && _mode <= 3 &&
                _native.BeginSimulation(unchecked((uint)Time.frameCount)) == 0) _simulationFrame=Time.frameCount;
        }
        private void BeginRender()
        {
            if (_shutdown || _faulted || _mode < 1 || _mode > 3 || _simulationFrame != Time.frameCount) return;
            uint frame=unchecked((uint)Time.frameCount);
            if (_native.EndSimulation(frame) == 0 && _native.BeginRender(frame) == 0) _renderFrame=Time.frameCount;
        }
        private void RetireTickets()
        {
            for (int i=0;i<_slots.Length;i++)
            {
                var slot=_slots[i]; if (slot.Ticket == 0) continue;
                var status=FrameGenerationNative.TicketStatus.New;
                if (_native.QueryTicket(slot.Ticket,ref status) != 0) continue;
                if (status.SourcePixelsRetired != 0) slot.Surfaces.AcknowledgeSourceRetirement();
                if (status.FullyRetired != 0 && _native.ReleaseTicket(slot.Ticket) == 0) slot.Ticket=0;
            }
            for (int i=0;i<_eofTickets.Length;i++)
            {
                if (_eofTickets[i] == 0) continue;
                var status=FrameGenerationNative.TicketStatus.New;
                if (_native.QueryTicket(_eofTickets[i],ref status) == 0 && status.FullyRetired != 0 &&
                    _native.ReleaseTicket(_eofTickets[i]) == 0) _eofTickets[i]=0;
            }
        }
        private bool TicketsRetired()
        {
            foreach (var slot in _slots) if (slot.Ticket != 0 || slot.Surfaces.BorrowedByNative) return false;
            foreach (ulong ticket in _eofTickets) if (ticket != 0) return false;
            return true;
        }
        private void LoadShader()
        {
            _shaderRequested=true;
            _frameGenerationMotion=new MotionVectorSanitizer(null, () =>
            {
                if (!_shutdown && _frameGenerationMotion != null && !_frameGenerationMotion.Ready)
                    Fail("Frame-generation motion-protection shader is unavailable.");
            });
            _frameGenerationMotion.Initialize();
            _shaderHandle=Addressables.LoadAssetAsync<Shader>("Assets/ReduxBetterAA/Shaders/FrameGenerationInputs.shader");
            _shaderHandle.Completed += ShaderLoaded;
        }
        private void ShaderLoaded(AsyncOperationHandle<Shader> result)
        {
            if (_shutdown) return;
            if (result.Status != AsyncOperationStatus.Succeeded || result.Result == null || !result.Result.isSupported)
            { Fail("Frame-generation input conversion shader is unavailable."); return; }
            _material=new Material(result.Result) { hideFlags=HideFlags.HideAndDontSave };
        }
        private void Fail(string reason)
        {
            _faulted=true; _reason=string.IsNullOrWhiteSpace(reason) ? "Frame-generation runtime is unavailable." : reason;
            _native?.SetMode(0); _mode=0;
            FrameGenerationAvailability.SetRuntimeState(false,_reason);
        }
        internal void RequestShutdown()
        {
            if (_shutdown) return;
            _shutdown=true; _shutdownFrame=Time.frameCount;
            _registration?.Dispose(); _registration=null;
            _loop?.Dispose();
            _native?.SetMode(0); _mode=0;
            FrameGenerationAvailability.SetRuntimeState(false,"Frame generation is draining.");
        }
        private void OnDestroy()
        {
            RequestShutdown();
            if (_eofPump != null) StopCoroutine(_eofPump);
            // Healthy shutdown reaches here after source-copy retirement. A
            // forced unload with live tickets also retains the intermediate
            // shader/textures, whose earlier Unity draws feed those copies.
            if (_frameGenerationMotion != null)
            {
                if (TicketsRetired()) _frameGenerationMotion.Dispose();
                else QuarantinedMotion.Add(_frameGenerationMotion);
                _frameGenerationMotion=null;
            }
            foreach (var slot in _slots) if (!slot.Surfaces.Release()) slot.Surfaces.Quarantine();
            if (_material != null) Destroy(_material);
            if (_shaderRequested) { _shaderHandle.Completed -= ShaderLoaded; Addressables.Release(_shaderHandle); }
            _native?.Dispose(); _native=null;
            _events?.Release(); _events=null;
            FrameGenerationAvailability.SetNvidiaSupport(0);
            FrameGenerationAvailability.SetAmdSupport(0,0,0,0,0,0);
            FrameGenerationAvailability.SetRuntimeState(false,"Frame-generation runtime is stopped.");
        }
        internal static bool SameAspect(int w,int h,int physicalW,int physicalH) => w > 0 && h > 0 && physicalW > 0 && physicalH > 0 &&
            Math.Abs((double)w/h-(double)physicalW/physicalH) < 0.001;
        private uint NextProbe()
        {
            if (_nvidiaInstalled && (_completedProbes & 1u) == 0) return 5;
            if (_amdInstalled && (_completedProbes & 2u) == 0) return 6;
            if (_amdInstalled && (_completedProbes & 4u) == 0) return 8;
            return 0;
        }
        private static bool IsProbe(uint mode) => mode == 5 || mode == 6 || mode == 8;
        private static uint ProbeBit(uint mode) => mode == 5 ? 1u : mode == 6 ? 2u : mode == 8 ? 4u : 0u;
        private static bool IsAmd(uint mode) => mode == 4 || mode == 7;
        private static bool IsActiveMode(uint mode) => mode >= 1 && mode <= 4 || mode == 7;
        private uint ToNativeMode(FrameGenerationMode mode)
        {
            switch (mode) { case FrameGenerationMode.Dlss2x:return 1; case FrameGenerationMode.Dlss3x:return 2;
                case FrameGenerationMode.Dlss4x:return 3; case FrameGenerationMode.Fsr4_2x:return 4;
                case FrameGenerationMode.Fsr31_2x:return _native.ExtendedStatus.AmdCompatibilityMask != 0 ? 7u : 4u;
                default:return 0; }
        }
    }
}
