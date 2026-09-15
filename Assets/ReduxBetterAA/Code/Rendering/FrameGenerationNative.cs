using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    // Let Unity's native-plugin loader deliver UnityPluginLoad, then verify the
    // exact installed path. A DLL added to an existing player is not necessarily
    // in its build-time preload list, even with the GfxPlugin prefix.
    internal sealed class FrameGenerationNative : IDisposable
    {
        internal const uint Abi = 1, StatusBytes = 792;
        private const string Library = "GfxPluginReduxBetterAAFrameGeneration.dll";
        internal readonly string ProviderPath, RuntimeDirectory, AmdProviderPath, AmdRuntimeDirectory;
        internal readonly IntPtr RenderEvent;
        internal readonly ConfigureDelegate Configure;
        internal readonly TickDelegate Tick;
        internal readonly MarkerDelegate BeginSimulation, EndSimulation, BeginRender;
        internal readonly PrepareCaptureDelegate PrepareCapture;
        internal readonly PrepareEofDelegate PrepareEndOfFrame;
        internal readonly QueryTicketDelegate QueryTicket;
        internal readonly ReleaseTicketDelegate ReleaseTicket;
        private readonly GetStatusDelegate _getStatus;
        private readonly GetStatusDelegate _getStatusV2;
        private readonly ConfigureV2Delegate _configureV2;
        internal bool SupportsAmd => _configureV2 != null && _getStatusV2 != null;
        internal StatusV2 ExtendedStatus { get; private set; }
        private IntPtr _statusBuffer;

        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        internal struct Config
        {
            internal uint Size, Version, Mode, VerifiedContracts;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ProviderPath, RuntimeDirectory;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8, CharSet = CharSet.Unicode)]
        internal struct ConfigV2
        {
            internal uint Size, Version, Mode, VerifiedContracts;
            [MarshalAs(UnmanagedType.LPWStr)] internal string NvidiaProviderPath, NvidiaRuntimeDirectory, AmdProviderPath, AmdRuntimeDirectory;
            internal uint RenderWidth, RenderHeight, ReversedDepth, PreferCompatibility;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct RowMatrix
        {
            internal float M00, M01, M02, M03, M10, M11, M12, M13;
            internal float M20, M21, M22, M23, M30, M31, M32, M33;
            internal RowMatrix(Matrix4x4 m)
            {
                M00=m.m00; M01=m.m01; M02=m.m02; M03=m.m03;
                M10=m.m10; M11=m.m11; M12=m.m12; M13=m.m13;
                M20=m.m20; M21=m.m21; M22=m.m22; M23=m.m23;
                M30=m.m30; M31=m.m31; M32=m.m32; M33=m.m33;
            }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Camera
        {
            internal RowMatrix Projection, WorldToView, ViewProjection, PreviousViewProjection;
            internal float JitterX, JitterY, Near, Far, Fov, Aspect, Meters, FrameMs, MotionScaleX, MotionScaleY;
            internal uint ReversedDepth, Reset;
            internal Camera(in ResolvedFrameRequest request)
            {
                var c = request.Camera;
                Projection=new RowMatrix(NativeClip(c.GpuProjection,c.ProjectionRendersIntoTexture)); WorldToView=new RowMatrix(c.WorldToView);
                ViewProjection=new RowMatrix(NativeClip(c.ViewProjection,c.ProjectionRendersIntoTexture));
                PreviousViewProjection=new RowMatrix(NativeClip(request.PreviousViewProjection,c.ProjectionRendersIntoTexture));
                // PPv2 adds +2*jitter/extent to P02/P12 with clip.w=-view.z.
                // Native top-left raster displacement is therefore (-x,+y).
                JitterX=-c.JitterRenderPixels.x; JitterY=c.JitterRenderPixels.y;
                Near=c.NearPlane; Far=c.FarPlane; Fov=c.VerticalFovRadians; Aspect=c.AspectRatio;
                Meters=1; // The gated flight physics camera uses meters, not scaled-space units.
                FrameMs=request.FrameTimeMilliseconds;
                // The sanitizer starts with current-previous UV displacement,
                // then applies these configurable signs. Convert those stored
                // values into previous-current top-left UVs. Spatial Y reversal
                // in the transport also reverses the normalized Y displacement.
                MotionScaleX=-request.MotionComponentSigns.x;
                MotionScaleY=request.MotionComponentSigns.y;
                ReversedDepth=c.ReversedDepth ? 1u : 0u; Reset=request.ResetHistory ? 1u : 0u;
            }
            internal static Matrix4x4 NativeClip(Matrix4x4 matrix,bool rendersIntoTexture)
            {
                // Undo the D3D render-target projection inversion for resources
                // transported into the native backbuffer's top-left row order.
                if (rendersIntoTexture) matrix.SetRow(1,-matrix.GetRow(1));
                return matrix;
            }
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Capture
        {
            internal uint Size, Version, RealFrameId, SceneEligible;
            internal uint RenderWidth, RenderHeight, DisplayWidth, DisplayHeight, ColorTransfer, MotionFormat;
            internal IntPtr HudlessColor, RawDepth, NormalizedMotion;
            internal Camera View;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct TicketStatus
        {
            internal uint Size, Version, Result, Kind;
            internal ulong Ticket;
            internal uint RealFrameId, EventConsumed, SourcePixelsRetired, FullyRetired;
            internal static TicketStatus New => new TicketStatus { Size=40, Version=Abi };
        }
        // The fixed ANSI reason follows this 280-byte header. Reading the numeric
        // status does not allocate a new managed string every rendered frame.
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        internal struct Status
        {
            internal uint Size, Version, Loaded, Renderer, RequestedMode, State, Result, NvidiaMultiplierMask;
            internal uint ActualMultiplier, SceneEligible, CaptureEventId, EndOfFrameEventId;
            internal uint RenderThreadId, LastQueryThreadId, SourceWidth, SourceHeight;
            internal uint SourceFormat, SourceSwapEffect, SourceFlags, SourceWindowed;
            internal uint SourceSyncInterval, SourcePresentFlags, ChildVisible, ChildLeaseRetained;
            internal uint ChildAcceptingPresents, ChildReason, ProviderResult, ProviderPending;
            internal ulong SourceWindow, ChildWindow, WindowEpoch, DeviceGeneration;
            internal ulong FinalRealFrameId, LastCapturedFrameId, LastSubmittedFrameId, LastRetiredFrameId;
            internal ulong PresentQueries, SourcePresentAttempts, SourcePresentSuccesses, SourcePresentFailures;
            internal ulong DuplicateQueries, WrongThreadQueries, SourceCountSingleSteps, SourceCountMismatches;
            internal ulong SourceUnexpectedCalls, ProviderRealPresents, ProviderSdkPresentations;
            internal uint GeneratedPresentationObserved, PendingTickets, InputPoolPending, Reserved;
        }

        // Keep the native base's 512-byte reason inline without allocating a
        // managed byte array on every status read.
        [StructLayout(LayoutKind.Explicit, Size = 904)]
        internal struct StatusV2
        {
            [FieldOffset(0)] internal uint Size;
            [FieldOffset(4)] internal uint Version;
            [FieldOffset(8)] internal Status Base;
            [FieldOffset(800)] internal uint AmdBestMask;
            [FieldOffset(804)] internal uint AmdCompatibilityMask;
            [FieldOffset(808)] internal uint AmdBestFamily;
            [FieldOffset(812)] internal uint AmdCompatibilityFamily;
            [FieldOffset(816)] internal uint AmdActualFamily;
            [FieldOffset(820)] internal uint CompletedDiscoveryMask;
            [FieldOffset(824)] internal ulong AmdBestVersion;
            [FieldOffset(832)] internal ulong AmdCompatibilityVersion;
            [FieldOffset(840)] internal ulong AmdActualVersion;
            [FieldOffset(848)] internal ulong AmdAcceptedRealFrames;
            [FieldOffset(856)] internal ulong AmdGeneratedDispatches;
            [FieldOffset(864)] internal ulong AmdNativePresentCountDelta;
            [FieldOffset(872)] internal ulong AmdLastAcceptedFrameId;
            [FieldOffset(880)] internal ulong AmdLastRetiredFrameId;
            [FieldOffset(888)] internal uint AmdWorkerPending;
            [FieldOffset(892)] internal uint AmdPresentPermitHeld;
            [FieldOffset(896)] internal uint AmdSdkResult;
            [FieldOffset(900)] internal uint AmdNativePresentCountValid;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ConfigureDelegate(ref Config config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ConfigureV2Delegate(ref ConfigV2 config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint TickDelegate(uint frame, uint eligible);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint MarkerDelegate(uint frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint PrepareCaptureDelegate(ref Capture capture, out ulong ticket, out IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint PrepareEofDelegate(uint frame, ulong capture, out ulong ticket, out IntPtr packet);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint QueryTicketDelegate(ulong ticket, ref TicketStatus status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ReleaseTicketDelegate(ulong ticket);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint GetStatusDelegate(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr EventDelegate();

        internal static FrameGenerationNative TryBind(out string reason)
        {
            reason = "The optional frame-generation runtime is not installed.";
            if (Application.platform != RuntimePlatform.WindowsPlayer || Application.unityVersion != "6000.5.8f1" ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || !ReduxSceneOutput.CompatibleAssembly)
            { reason="Frame generation requires the inspected Redux/Unity D3D11 player."; return null; }
            try
            {
                string expected = Path.GetFullPath(Path.Combine(Application.dataPath, "Plugins", "x86_64", Library));
                var module = GetModuleHandle(Library);
                if (module == IntPtr.Zero)
                {
                    if (!File.Exists(expected)) return null;
                    // A plain LoadLibrary would omit Unity's interface registry.
                    // Unity calls the plugin's initialization export on P/Invoke.
                    if (InitializeThroughUnity() == IntPtr.Zero)
                        throw new InvalidOperationException("Unity could not initialize the FG plugin");
                    module = GetModuleHandle(Library);
                    if (module == IntPtr.Zero) throw new InvalidOperationException("Unity did not load the installed FG plugin");
                }
                var path = new StringBuilder(32768);
                if (GetModuleFileName(module, path, path.Capacity) == 0) throw new InvalidOperationException("Could not identify the preloaded FG module");
                if (!string.Equals(Path.GetFullPath(path.ToString()), expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("FG module was not loaded from the expected player plugin path");
                var api = new FrameGenerationNative(module);
                if (!api.ReadStatus(out var status) || status.Loaded != 1 || status.Version != Abi)
                { api.Dispose(); throw new InvalidOperationException("Unity did not initialize the FG plugin ABI"); }
                reason=string.Empty;
                return api;
            }
            catch (Exception error) { reason=error.Message; return null; }
        }
        private FrameGenerationNative(IntPtr module)
        {
            Configure=Bind<ConfigureDelegate>(module,"RbaFgConfigure"); Tick=Bind<TickDelegate>(module,"RbaFgTick");
            BeginSimulation=Bind<MarkerDelegate>(module,"RbaFgBeginSimulation"); EndSimulation=Bind<MarkerDelegate>(module,"RbaFgEndSimulation");
            BeginRender=Bind<MarkerDelegate>(module,"RbaFgBeginRender"); PrepareCapture=Bind<PrepareCaptureDelegate>(module,"RbaFgPrepareCapture");
            PrepareEndOfFrame=Bind<PrepareEofDelegate>(module,"RbaFgPrepareEndOfFrame"); QueryTicket=Bind<QueryTicketDelegate>(module,"RbaFgQueryTicket");
            ReleaseTicket=Bind<ReleaseTicketDelegate>(module,"RbaFgReleaseTicket"); _getStatus=Bind<GetStatusDelegate>(module,"RbaFgGetStatus");
            if (GetProcAddress(module,"RbaFgConfigureV2") != IntPtr.Zero && GetProcAddress(module,"RbaFgGetStatusV2") != IntPtr.Zero)
            {
                _configureV2=Bind<ConfigureV2Delegate>(module,"RbaFgConfigureV2");
                _getStatusV2=Bind<GetStatusDelegate>(module,"RbaFgGetStatusV2");
            }
            RenderEvent=Bind<EventDelegate>(module,"RbaFgGetRenderEventFunc")();
            if (RenderEvent == IntPtr.Zero) throw new InvalidOperationException("FG render event callback is missing");
            RuntimeDirectory=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(FrameGenerationNative).Assembly.Location), "native", "frame-generation", "streamline"));
            ProviderPath=Path.Combine(Path.GetDirectoryName(RuntimeDirectory), "ReduxBetterAA.StreamlineProvider.dll");
            AmdRuntimeDirectory=Path.Combine(Path.GetDirectoryName(RuntimeDirectory),"amd");
            AmdProviderPath=Path.Combine(Path.GetDirectoryName(RuntimeDirectory),"ReduxBetterAA.AmdFrameGeneration.dll");
            _statusBuffer=Marshal.AllocHGlobal(SupportsAmd ? 904 : (int)StatusBytes);
        }
        internal bool ReadStatus(out Status status)
        {
            status=default;
            if (_statusBuffer == IntPtr.Zero) return false;
            if (SupportsAmd)
            {
                Marshal.WriteInt32(_statusBuffer,0,904); Marshal.WriteInt32(_statusBuffer,4,2);
                if (_getStatusV2(_statusBuffer) != 0) return false;
                ExtendedStatus=Marshal.PtrToStructure<StatusV2>(_statusBuffer);
                status=ExtendedStatus.Base;
                return ExtendedStatus.Size == 904 && ExtendedStatus.Version == 2 && status.Size == StatusBytes && status.Version == Abi;
            }
            Marshal.WriteInt32(_statusBuffer,0,(int)StatusBytes); Marshal.WriteInt32(_statusBuffer,4,(int)Abi);
            if (_getStatus(_statusBuffer) != 0) return false;
            status=Marshal.PtrToStructure<Status>(_statusBuffer);
            return status.Size == StatusBytes && status.Version == Abi;
        }
        internal string ReadReason()
        {
            if (_statusBuffer == IntPtr.Zero) return string.Empty;
            string reason=Marshal.PtrToStringAnsi(IntPtr.Add(_statusBuffer,SupportsAmd ? 288 : 280),512) ?? string.Empty;
            int end=reason.IndexOf('\0');
            return end < 0 ? reason : reason.Substring(0,end);
        }
        internal uint SetMode(uint mode,uint renderWidth=0,uint renderHeight=0,bool reversedDepth=false)
        {
            if (SupportsAmd)
            {
                var configV2=new ConfigV2 { Size=64,Version=2,Mode=mode,VerifiedContracts=15,
                    NvidiaProviderPath=ProviderPath,NvidiaRuntimeDirectory=RuntimeDirectory,
                    AmdProviderPath=AmdProviderPath,AmdRuntimeDirectory=AmdRuntimeDirectory,
                    RenderWidth=renderWidth,RenderHeight=renderHeight,ReversedDepth=reversedDepth ? 1u : 0u,
                    PreferCompatibility=mode == 7 || mode == 8 ? 1u : 0u };
                return _configureV2(ref configV2);
            }
            var config=new Config { Size=32, Version=Abi, Mode=mode, VerifiedContracts=15,
                ProviderPath=ProviderPath, RuntimeDirectory=RuntimeDirectory };
            return Configure(ref config);
        }
        public void Dispose()
        {
            if (_statusBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_statusBuffer);
            _statusBuffer=IntPtr.Zero;
            // Unity and native tickets retain module callbacks until process exit.
        }
        private static T Bind<T>(IntPtr module, string name) where T : Delegate
        {
            var pointer=GetProcAddress(module,name);
            if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer<T>(pointer);
        }
        [DllImport("kernel32",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("GfxPluginReduxBetterAAFrameGeneration",EntryPoint="RbaFgGetRenderEventFunc",CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr InitializeThroughUnity();
        [DllImport("kernel32",EntryPoint="GetModuleFileNameW",CharSet=CharSet.Unicode)] private static extern uint GetModuleFileName(IntPtr module,StringBuilder path,int size);
        [DllImport("kernel32",CharSet=CharSet.Ansi,ExactSpelling=true)] private static extern IntPtr GetProcAddress(IntPtr module,string name);
    }
}
