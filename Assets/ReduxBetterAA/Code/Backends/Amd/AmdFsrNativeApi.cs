using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ReduxBetterAA.Backends.Amd
{
    // Optional, versioned bridge. Unity retains camera/presentation ownership;
    // every GPU operation runs on its render thread. No GPU-name heuristics.
    internal sealed class AmdFsrNativeApi : IDisposable
    {
        internal const uint AbiVersion = 1;
        internal const int StatusSize = 680;
        private const uint HighDynamicRangeFlag = 1u << 0;
        private const uint InvertedDepthFlag = 1u << 3;
        private const uint AutoExposureFlag = 1u << 5;
        private const string ShaderAddress = "Assets/ReduxBetterAA/Shaders/FsrBridgeInputs.shader";
        private static NativeExports _exports;
        private static bool _probed;
        private static bool _available;
        private static bool _nativeLoadAttempted;
        private static bool _nativeContextProbeAttempted;
        private static string _provider = string.Empty;
        private static string _probeReason = string.Empty;
        private static readonly List<IntPtr> Retiring = new List<IntPtr>();
        private static readonly IntPtr RetirementStatus = NewStatus();
        private readonly IntPtr _status = NewStatus();
        private IntPtr _context;
        private uint _contextFlags;
        private IntPtr _colorPointer, _depthPointer, _motionPointer, _exposurePointer, _outputPointer;
        private RenderTexture _lastOutput;
        private long _displayPixels;
        private bool _providerVerified;
        internal static readonly uint DispatchSize = (uint)Marshal.SizeOf<DispatchDescription>();
        private RenderTexture _color, _depth, _motion, _exposure;
        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderRequested, _disposed;
        private Material _material;
        private Action _availabilityChanged;
        private string _failure = string.Empty;

        public static string ProviderName => _provider;
        internal static bool ProbeCompleted => _probed;
        internal static bool NativeLoadAttempted => _nativeLoadAttempted;
        internal static bool NativeBridgeLoaded => _exports != null;
        internal static bool NativeContextProbeAttempted => _nativeContextProbeAttempted;
        internal static bool ProviderAvailable => _available;
        internal static string ProbeReason => _probeReason;
        public bool ContextCreated => _context != IntPtr.Zero;
        public bool ContextUsesHdr => ContextCreated && (_contextFlags & HighDynamicRangeFlag) != 0;
        public bool ContextUsesAutoExposure => ContextCreated && (_contextFlags & AutoExposureFlag) != 0;
        public bool Ready => _material != null;
        public string FailureReason => _failure;
        // Known converted inputs plus three native interop slot sets. AMD's
        // internal history and driver overhead are not available through this ABI.
        public long EstimatedMemoryBytes => _color == null ? 0 :
            (long)_color.width * _color.height * 16L + 4L +
            3L * (18L * _color.width * _color.height + 8L * _displayPixels + 4L);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct CreateDescription
        {
            public uint Size, Abi, RenderWidth, RenderHeight, OutputWidth, OutputHeight, Flags, GraphicsApi;
            [MarshalAs(UnmanagedType.LPWStr)] public string RuntimeDirectory;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DispatchDescription
        {
            public uint Size, Abi;
            public IntPtr Context;
            public ulong FrameId;
            public IntPtr Color, Depth, Motion, Output, Reactive, Exposure, Composition;
            public float JitterX, JitterY, MotionScaleX, MotionScaleY, DeltaMs, PreExposure,
                Near, Far, Fov, ViewSpaceToMeters, Sharpness;
            public uint Reset, Sharpen, Reserved;
        }

        private static IntPtr NewStatus()
        {
            IntPtr pointer = Marshal.AllocHGlobal(StatusSize);
            Marshal.WriteInt32(pointer, 0, StatusSize);
            Marshal.WriteInt32(pointer, 4, (int)AbiVersion);
            return pointer;
        }

        internal static string CanonicalProvider(string actualName)
        {
            // The pinned SDK reports semantic versions (e.g. 3.1.5 or 4.1.1).
            if (!Version.TryParse(actualName, out Version version)) return string.Empty;
            if (version.Major == 4 && version.Minor == 1) return "FSR 4.1";
            if (version.Major == 3 && version.Minor == 1) return "FSR 3.1";
            return string.Empty;
        }

        internal static bool TryResolveProvider(uint result, int state, string actualName, out string provider)
        {
            provider = result == 0 && state == 1 ? CanonicalProvider(actualName) : string.Empty;
            return provider.Length > 0;
        }

        public static bool Probe(out string provider, out string reason)
        {
            if (!_probed)
            {
                _probed = true;
                RenderTexture representative = null;
                IntPtr status = NewStatus();
                try
                {
                    if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                        throw new NotSupportedException("the FSR bridge currently requires Direct3D 11");
                    _nativeLoadAttempted = true;
                    _exports = new NativeExports();
                    if (_exports.GetAbi() != AbiVersion) throw new NotSupportedException("FSR bridge ABI mismatch");
                    representative = NewTexture(4, 4, RenderTextureFormat.ARGBHalf, "FSR adapter probe");
                    CreateDescription description = Describe(512, 512, 512, 512);
                    _nativeContextProbeAttempted = true;
                    uint result = _exports.Probe(ref description, representative.GetNativeTexturePtr(), status);
                    string actual = Marshal.PtrToStringAnsi(IntPtr.Add(status, 40)) ?? string.Empty;
                    _available = TryResolveProvider(result, Marshal.ReadInt32(status, 8), actual, out _provider);
                    _probeReason = _available ? "selected SDK provider: " + actual :
                        "FSR context probe failed: " + Marshal.PtrToStringAnsi(IntPtr.Add(status, 168));
                    if (result == 0 && Marshal.ReadInt32(status, 8) == 1 && _provider.Length == 0)
                        _probeReason = "unrecognized FSR provider: " + actual;
                }
                catch (Exception exception)
                {
                    _available = false;
                    _provider = string.Empty;
                    _probeReason = exception.GetType().Name + ": " + exception.Message;
                }
                finally { TemporalTextures.Release(ref representative); Marshal.FreeHGlobal(status); }
            }
            provider = _provider;
            reason = _probeReason;
            return _available;
        }

        public void Initialize(Action availabilityChanged)
        {
            _availabilityChanged = availabilityChanged;
            if (_shaderRequested || !Probe(out _, out _)) return;
            _shaderRequested = true;
            _shaderHandle = Addressables.LoadAssetAsync<Shader>(ShaderAddress);
            _shaderHandle.Completed += OnShaderLoaded;
        }

        private void OnShaderLoaded(AsyncOperationHandle<Shader> handle)
        {
            if (_disposed) return;
            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null && handle.Result.isSupported)
                _material = new Material(handle.Result) { hideFlags = HideFlags.HideAndDontSave };
            else _failure = "FSR input conversion shader is unavailable";
            _availabilityChanged?.Invoke();
        }

        public bool TryCreateContext(CommandBuffer commands, int width, int height, int outputWidth, int outputHeight,
            bool autoExposure, out string reason)
        {
            DestroyContext(commands);
            if (!Ready || !Probe(out _, out reason)) { reason = Ready ? _probeReason : "FSR input conversion shader is loading or unavailable"; return false; }
            try
            {
                _color = NewTexture(width, height, RenderTextureFormat.ARGBHalf, "FSR bridge linear color");
                _depth = NewTexture(width, height, RenderTextureFormat.RFloat, "FSR bridge device depth");
                _motion = NewTexture(width, height, RenderTextureFormat.RGHalf, "FSR bridge motion");
                _exposure = NewTexture(1, 1, RenderTextureFormat.RFloat, "FSR bridge exposure");
                _colorPointer = _color.GetNativeTexturePtr(); _depthPointer = _depth.GetNativeTexturePtr();
                _motionPointer = _motion.GetNativeTexturePtr(); _exposurePointer = _exposure.GetNativeTexturePtr();
                _providerVerified = false;
                _displayPixels = (long)outputWidth * outputHeight;
                CreateDescription description = Describe(width, height, outputWidth, outputHeight, autoExposure);
                uint result = _exports.Create(ref description, out _context, IntPtr.Add(_status, 168), 512);
                if (result != 0 || _context == IntPtr.Zero)
                    throw new InvalidOperationException(Marshal.PtrToStringAnsi(IntPtr.Add(_status, 168)));
                _contextFlags = description.Flags;
                reason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                reason = _failure = exception.GetType().Name + ": " + exception.Message;
                DestroyContext(commands);
                return false;
            }
        }

        public bool Execute(CommandBuffer commands, Texture source, RenderTexture output, Texture depth,
            Texture motion, Vector2 rasterJitter, Camera camera, float preExposure,
            in Fsr2Config config, bool reset, out string reason)
        {
            reason = string.Empty;
            if (!CheckStatus(out reason)) return false;
            commands.Clear();
            commands.Blit(source, _color);
            commands.Blit(depth, _depth, _material, 0);
            commands.Blit(motion, _motion);
            if (!ContextUsesAutoExposure)
                commands.Blit(Texture2D.whiteTexture, _exposure, _material, 1);
            commands.Blit(source, output);
            if (_lastOutput != output) { _outputPointer = output.GetNativeTexturePtr(); _lastOutput = output; }
            Vector2 jitter = ToDispatchJitter(rasterJitter);
            var dispatch = new DispatchDescription
            {
                Size = DispatchSize, Abi = AbiVersion, Context = _context,
                FrameId = (ulong)Time.frameCount,
                Color = _colorPointer, Depth = _depthPointer,
                Motion = _motionPointer, Output = _outputPointer,
                Exposure = ContextUsesAutoExposure ? IntPtr.Zero : _exposurePointer,
                JitterX = jitter.x, JitterY = jitter.y,
                MotionScaleX = _color.width, MotionScaleY = _color.height,
                DeltaMs = Mathf.Max(0.01f, Time.unscaledDeltaTime * 1000.0f), PreExposure = preExposure,
                Near = SystemInfo.usesReversedZBuffer ? camera.farClipPlane : camera.nearClipPlane,
                Far = SystemInfo.usesReversedZBuffer ? camera.nearClipPlane : camera.farClipPlane, Fov = camera.fieldOfView * Mathf.Deg2Rad,
                ViewSpaceToMeters = 1.0f, Sharpness = config.Sharpness,
                Reset = reset ? 1u : 0u, Sharpen = config.EnableSharpening ? 1u : 0u
            };
            uint prepared = _exports.Prepare(ref dispatch, out IntPtr packet);
            if (prepared != 0)
            {
                commands.Clear();
                reason = _failure = "FSR dispatch queue rejected submission (" + prepared + ")";
                return false;
            }
            commands.IssuePluginEventAndData(_exports.RenderEvent, 1, packet);
            Graphics.ExecuteCommandBuffer(commands);
            commands.Clear();
            return true;
        }

        private bool CheckStatus(out string reason)
        {
            reason = string.Empty;
            if (_context == IntPtr.Zero) { reason = "FSR context is unavailable"; return false; }
            if (_exports.GetStatus(_context, _status) != 0) { reason = "FSR status query failed"; return false; }
            int state = Marshal.ReadInt32(_status, 8);
            if (state == 1 && !_providerVerified)
            {
                string actual = CanonicalProvider(Marshal.PtrToStringAnsi(IntPtr.Add(_status, 40)));
                if (actual != _provider) { reason = _failure = "FSR context selected a different provider than the settings probe"; return false; }
                _providerVerified = true;
            }
            if (state == 2 || state == 3)
            {
                reason = _failure = Marshal.PtrToStringAnsi(IntPtr.Add(_status, 168)) ?? "FSR context stopped";
                return false;
            }
            return true;
        }

        public void DestroyContext(CommandBuffer commands)
        {
            if (_context != IntPtr.Zero && commands != null)
            {
                commands.Clear();
                commands.IssuePluginEventAndData(_exports.RenderEvent, 2, _context);
                Graphics.ExecuteCommandBuffer(commands);
                commands.Clear();
                Retiring.Add(_context);
                _context = IntPtr.Zero;
            }
            // Native packets retain their COM resource references through GPU
            // retirement. No queued callback reads a managed Unity object.
            _lastOutput = null;
            _outputPointer = IntPtr.Zero;
            TemporalTextures.Release(ref _color);
            TemporalTextures.Release(ref _depth);
            TemporalTextures.Release(ref _motion);
            TemporalTextures.Release(ref _exposure);
            CollectRetired();
        }

        public static void CollectRetired()
        {
            for (int i = Retiring.Count - 1; i >= 0; --i)
                if (_exports.GetStatus(Retiring[i], RetirementStatus) == 0 && Marshal.ReadInt32(RetirementStatus, 8) == 3 &&
                    _exports.Release(Retiring[i]) == 0) Retiring.RemoveAt(i);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            using (var commands = new CommandBuffer()) DestroyContext(commands);
            if (_material != null) UnityEngine.Object.Destroy(_material);
            if (_shaderRequested) { _shaderHandle.Completed -= OnShaderLoaded; Addressables.Release(_shaderHandle); }
            Marshal.FreeHGlobal(_status);
        }

        // The converted linear color can exceed one after PPv2. Keep the wide
        // input range independently of the selected exposure policy.
        internal static uint GetContextFlags(bool reversedDepth, bool autoExposure = false) =>
            HighDynamicRangeFlag | (reversedDepth ? InvertedDepthFlag : 0u) |
            (autoExposure ? AutoExposureFlag : 0u);

        internal static Vector2 ToDispatchJitter(Vector2 projectionJitterPixels)
        {
            // PPv2 increases Unity's projection offsets for this raster sample.
            // FSR dispatch describes the opposite projection translation.
            return -projectionJitterPixels;
        }

        internal static string FindRuntimeLibrary(string runtimeDirectory)
        {
            string library = Path.Combine(runtimeDirectory, "ReduxBetterAA.FsrBridge.dll");
            if (!File.Exists(library))
                throw new FileNotFoundException("bundled FSR native libraries are missing; reinstall the complete Better AA ZIP", library);
            return library;
        }

        private static CreateDescription Describe(int width, int height, int outputWidth, int outputHeight,
            bool autoExposure = false) => new CreateDescription
        {
            Size = (uint)Marshal.SizeOf<CreateDescription>(), Abi = AbiVersion,
            RenderWidth = (uint)width, RenderHeight = (uint)height,
            OutputWidth = (uint)outputWidth, OutputHeight = (uint)outputHeight,
            Flags = GetContextFlags(SystemInfo.usesReversedZBuffer, autoExposure), GraphicsApi = 11,
            RuntimeDirectory = _exports.RuntimeDirectory
        };

        private static RenderTexture NewTexture(int width, int height, RenderTextureFormat format, string name)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            if (!texture.Create()) { UnityEngine.Object.Destroy(texture); throw new InvalidOperationException(name + " allocation failed"); }
            return texture;
        }

        private sealed class NativeExports
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint GetAbiDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ProbeDelegate(ref CreateDescription description, IntPtr texture, IntPtr status);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint CreateDelegate(ref CreateDescription description, out IntPtr context, IntPtr error, uint capacity);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint PrepareDelegate(ref DispatchDescription description, out IntPtr packet);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr RenderEventDelegate();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint StatusDelegate(IntPtr context, IntPtr status);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ReleaseDelegate(IntPtr context);
            internal readonly GetAbiDelegate GetAbi;
            internal readonly ProbeDelegate Probe;
            internal readonly CreateDelegate Create;
            internal readonly PrepareDelegate Prepare;
            internal readonly StatusDelegate GetStatus;
            internal readonly ReleaseDelegate Release;
            internal readonly IntPtr RenderEvent;
            internal readonly string RuntimeDirectory;
            private readonly IntPtr _module;
            internal NativeExports()
            {
                RuntimeDirectory = Path.Combine(Path.GetDirectoryName(typeof(AmdFsrNativeApi).Assembly.Location), "native");
                string library = FindRuntimeLibrary(RuntimeDirectory);
                _module = LoadLibraryEx(library, IntPtr.Zero, 0x100u | 0x1000u);
                if (_module == IntPtr.Zero) throw new InvalidOperationException("FSR bridge load failed: Win32 " + Marshal.GetLastWin32Error());
                GetAbi = Bind<GetAbiDelegate>("RbaFsrGetAbiVersion"); Probe = Bind<ProbeDelegate>("RbaFsrProbe");
                Create = Bind<CreateDelegate>("RbaFsrCreate"); Prepare = Bind<PrepareDelegate>("RbaFsrPrepareDispatch");
                GetStatus = Bind<StatusDelegate>("RbaFsrGetStatus"); Release = Bind<ReleaseDelegate>("RbaFsrRelease");
                RenderEvent = Bind<RenderEventDelegate>("RbaFsrGetRenderEventFunc")();
                if (RenderEvent == IntPtr.Zero) throw new InvalidOperationException("FSR render callback is unavailable");
                // Kept loaded for process lifetime: Unity may still hold callbacks.
            }
            private T Bind<T>(string name) where T : Delegate
            {
                IntPtr function = GetProcAddress(_module, name);
                if (function == IntPtr.Zero) throw new EntryPointNotFoundException(name);
                return Marshal.GetDelegateForFunctionPointer<T>(function);
            }
            [DllImport("kernel32", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
            [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
            private static extern IntPtr GetProcAddress(IntPtr module, string name);
        }
    }
}
