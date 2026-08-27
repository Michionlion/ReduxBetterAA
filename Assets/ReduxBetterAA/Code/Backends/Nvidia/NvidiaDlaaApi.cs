using System;
using System.Reflection;
using System.Reflection.Emit;
using ReduxBetterAA.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Backends.Nvidia
{
    /// <summary>
    /// Cached reflection bridge for UnityEngine.NVIDIAModule. The core mod never
    /// carries a static reference to the optional managed or native NVIDIA module.
    /// Dynamic delegates keep the render path free of MethodInfo.Invoke allocations.
    /// </summary>
    internal sealed class NvidiaDlaaApi
    {
        private const string AssemblyName = "UnityEngine.NVIDIAModule";
        private const uint ExpectedDeviceVersion = 0x06;
        private const int FeatureDlss = 0;
        private const int QualityDlaa = 4;
        private const int FlagIsHdr = 1 << 0;
        private const int FlagMotionVectorsLowResolution = 1 << 1;
        private const int FlagDepthInverted = 1 << 3;
        private const int FlagSharpening = 1 << 4;
        private const int FlagAutoExposure = 1 << 5;
        private const BindingFlags PublicStatic =
            BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance =
            BindingFlags.Public | BindingFlags.Instance;

        private delegate object CreateFeatureDelegate(
            object device,
            CommandBuffer commandBuffer,
            object initializationData);
        private delegate void DestroyFeatureDelegate(
            object device,
            CommandBuffer commandBuffer,
            object context);
        private delegate void ExecuteDlssDelegate(
            object device,
            CommandBuffer commandBuffer,
            object context,
            object textureTable);
        private delegate void SetFloatDelegate(object instance, float value);
        private delegate void SetIntDelegate(object instance, int value);
        private delegate void SetUIntDelegate(object instance, uint value);
        private delegate void SetTextureDelegate(object instance, Texture value);

        private Type _deviceType;
        private Type _featureType;
        private Type _qualityType;
        private Type _presetType;
        private Type _flagsType;
        private Type _initializationType;
        private Type _contextType;
        private Type _executionType;
        private Type _textureTableType;
        private MethodInfo _pluginIsLoaded;
        private MethodInfo _pluginLoad;
        private MethodInfo _createGraphicsDevice;
        private MethodInfo _isFeatureAvailable;
        private PropertyInfo _deviceVersion;
        private CreateFeatureDelegate _createFeature;
        private DestroyFeatureDelegate _destroyFeature;
        private ExecuteDlssDelegate _executeDlss;
        private MethodInfo _executionDataGetter;
        private SetFloatDelegate _setSharpness;
        private SetFloatDelegate _setMotionScaleX;
        private SetFloatDelegate _setMotionScaleY;
        private SetFloatDelegate _setJitterX;
        private SetFloatDelegate _setJitterY;
        private SetFloatDelegate _setPreExposure;
        private SetIntDelegate _setReset;
        private SetUIntDelegate _setSubrectOffsetX;
        private SetUIntDelegate _setSubrectOffsetY;
        private SetUIntDelegate _setSubrectWidth;
        private SetUIntDelegate _setSubrectHeight;
        private SetUIntDelegate _setInvertX;
        private SetUIntDelegate _setInvertY;
        private SetTextureDelegate _setColorInput;
        private SetTextureDelegate _setColorOutput;
        private SetTextureDelegate _setDepth;
        private SetTextureDelegate _setMotionVectors;
        private SetTextureDelegate _setBiasColorMask;

        private object _device;
        private object _context;
        private object _textureTable;
        private bool _managedSurfaceBound;
        private bool _initialized;
        private uint _version;

        public bool ContextCreated => _context != null;
        public uint DeviceVersion => _version;

        public bool TryBindManagedSurface(out string failureReason)
        {
            if (_managedSurfaceBound)
            {
                failureReason = string.Empty;
                return true;
            }

            try
            {
                Type pluginType = RequireType("UnityEngine.NVIDIA.NVUnityPlugin");
                _deviceType = RequireType("UnityEngine.NVIDIA.GraphicsDevice");
                _featureType = RequireType(
                    "UnityEngine.NVIDIA.GraphicsDeviceFeature"
                );
                _qualityType = RequireType("UnityEngine.NVIDIA.DLSSQuality");
                _presetType = RequireType("UnityEngine.NVIDIA.DLSSPreset");
                _flagsType = RequireType("UnityEngine.NVIDIA.DLSSFeatureFlags");
                _initializationType = RequireType(
                    "UnityEngine.NVIDIA.DLSSCommandInitializationData"
                );
                _contextType = RequireType("UnityEngine.NVIDIA.DLSSContext");
                _executionType = RequireType(
                    "UnityEngine.NVIDIA.DLSSCommandExecutionData"
                );
                _textureTableType = RequireType(
                    "UnityEngine.NVIDIA.DLSSTextureTable"
                );

                _pluginIsLoaded = RequireMethod(pluginType, "IsLoaded", PublicStatic);
                _pluginLoad = RequireMethod(pluginType, "Load", PublicStatic);
                _createGraphicsDevice = _deviceType.GetMethod(
                    "CreateGraphicsDevice",
                    PublicStatic,
                    null,
                    Type.EmptyTypes,
                    null
                );
                if (_createGraphicsDevice == null)
                {
                    throw new MissingMethodException(
                        _deviceType.FullName,
                        "CreateGraphicsDevice()"
                    );
                }
                _deviceVersion = RequireProperty(_deviceType, "version", PublicStatic);
                _isFeatureAvailable = RequireMethod(
                    _deviceType,
                    "IsFeatureAvailable",
                    PublicInstance
                );

                MethodInfo createFeature = RequireMethod(
                    _deviceType,
                    "CreateFeature",
                    PublicInstance
                );
                MethodInfo destroyFeature = RequireMethod(
                    _deviceType,
                    "DestroyFeature",
                    PublicInstance
                );
                MethodInfo executeDlss = RequireMethod(
                    _deviceType,
                    "ExecuteDLSS",
                    PublicInstance
                );
                PropertyInfo executionData = RequireProperty(
                    _contextType,
                    "executeData",
                    PublicInstance
                );

                _createFeature = CreateFeatureInvoker(createFeature);
                _destroyFeature = CreateDestroyInvoker(destroyFeature);
                _executeDlss = CreateExecuteInvoker(executeDlss);
                _executionDataGetter = executionData.GetGetMethod(false);
                if (_executionDataGetter == null ||
                    !_executionDataGetter.ReturnType.IsByRef ||
                    _executionDataGetter.ReturnType.GetElementType() != _executionType)
                {
                    throw new MissingMethodException(
                        _contextType.FullName,
                        "get_executeData"
                    );
                }

                _setSharpness = CreateFloatSetter("sharpness");
                _setMotionScaleX = CreateFloatSetter("mvScaleX");
                _setMotionScaleY = CreateFloatSetter("mvScaleY");
                _setJitterX = CreateFloatSetter("jitterOffsetX");
                _setJitterY = CreateFloatSetter("jitterOffsetY");
                _setPreExposure = CreateFloatSetter("preExposure");
                _setReset = CreateIntSetter("reset");
                _setSubrectOffsetX = CreateUIntSetter("subrectOffsetX");
                _setSubrectOffsetY = CreateUIntSetter("subrectOffsetY");
                _setSubrectWidth = CreateUIntSetter("subrectWidth");
                _setSubrectHeight = CreateUIntSetter("subrectHeight");
                _setInvertX = CreateUIntSetter("invertXAxis");
                _setInvertY = CreateUIntSetter("invertYAxis");
                _setColorInput = CreateTextureSetter("colorInput");
                _setColorOutput = CreateTextureSetter("colorOutput");
                _setDepth = CreateTextureSetter("depth");
                _setMotionVectors = CreateTextureSetter("motionVectors");
                _setBiasColorMask = CreateTextureSetter("biasColorMask");

                _managedSurfaceBound = true;
                failureReason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                failureReason = DescribeException(exception);
                return false;
            }
        }

        public bool TryInitialize(out string failureReason)
        {
            if (_initialized)
            {
                failureReason = string.Empty;
                return true;
            }
            if (!TryBindManagedSurface(out failureReason))
            {
                failureReason = "managed NVIDIA API unavailable: " + failureReason;
                return false;
            }

            try
            {
                bool loaded = InvokeBoolean(_pluginIsLoaded, null);
                if (!loaded)
                {
                    loaded = InvokeBoolean(_pluginLoad, null);
                }
                if (!loaded)
                {
                    failureReason =
                        "native Unity NVIDIA plugin did not load (no proprietary binaries are bundled)";
                    return false;
                }

                object version = _deviceVersion.GetValue(null, null);
                _version = version is uint uintVersion ? uintVersion : 0;
                if (_version != ExpectedDeviceVersion)
                {
                    failureReason = "Unity NVIDIA device API version " + _version +
                        " does not match expected version " + ExpectedDeviceVersion;
                    return false;
                }

                _device = _createGraphicsDevice.Invoke(null, null);
                if (_device == null)
                {
                    failureReason = "Unity NVIDIA graphics device creation returned null";
                    return false;
                }

                object feature = Enum.ToObject(_featureType, FeatureDlss);
                if (!InvokeBoolean(_isFeatureAvailable, _device, feature))
                {
                    failureReason = "Unity NVIDIA DLSS/DLAA feature reports unavailable";
                    return false;
                }

                _initialized = true;
                failureReason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                failureReason = DescribeException(exception);
                return false;
            }
        }

        public bool TryCreateContext(
            CommandBuffer commandBuffer,
            int width,
            int height,
            bool hdr,
            bool autoExposure,
            DlaaPreset preset,
            out string failureReason)
        {
            DestroyContext(commandBuffer);
            if (!_initialized)
            {
                failureReason = "Unity NVIDIA graphics device is not initialized";
                return false;
            }

            try
            {
                object initialization = Activator.CreateInstance(_initializationType);
                SetInitializationProperty(initialization, "inputRTWidth", (uint)width);
                SetInitializationProperty(initialization, "inputRTHeight", (uint)height);
                SetInitializationProperty(initialization, "outputRTWidth", (uint)width);
                SetInitializationProperty(initialization, "outputRTHeight", (uint)height);
                SetInitializationProperty(
                    initialization,
                    "quality",
                    Enum.ToObject(_qualityType, QualityDlaa)
                );
                SetInitializationProperty(
                    initialization,
                    "presetDlaaMode",
                    Enum.ToObject(_presetType, (int)preset)
                );

                int flags = FlagMotionVectorsLowResolution | FlagSharpening;
                if (hdr)
                {
                    flags |= FlagIsHdr;
                }
                if (SystemInfo.usesReversedZBuffer)
                {
                    flags |= FlagDepthInverted;
                }
                if (autoExposure)
                {
                    flags |= FlagAutoExposure;
                }
                SetInitializationProperty(
                    initialization,
                    "featureFlags",
                    Enum.ToObject(_flagsType, flags)
                );

                commandBuffer.Clear();
                _context = _createFeature(_device, commandBuffer, initialization);
                if (_context == null)
                {
                    commandBuffer.Clear();
                    failureReason = "Unity NVIDIA CreateFeature returned null";
                    return false;
                }
                Graphics.ExecuteCommandBuffer(commandBuffer);
                commandBuffer.Clear();
                _textureTable = Activator.CreateInstance(_textureTableType);
                if (_textureTable == null)
                {
                    DestroyContext(commandBuffer);
                    failureReason = "Unity NVIDIA context data was unavailable";
                    return false;
                }

                failureReason = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                DestroyContext(commandBuffer);
                failureReason = DescribeException(exception);
                return false;
            }
        }

        public void Execute(
            CommandBuffer commandBuffer,
            Texture colorInput,
            RenderTexture colorOutput,
            Texture depth,
            Texture motionVectors,
            Texture biasColorMask,
            int width,
            int height,
            Vector2 jitterPixels,
            float preExposure,
            in DlaaConfig config,
            bool reset)
        {
            _setSharpness(_context, config.Sharpness);
            // MotionVectorSanitizer performs the explicit component transform.
            // These values now only convert normalized UV motion to pixels.
            _setMotionScaleX(_context, width);
            _setMotionScaleY(_context, height);
            _setJitterX(_context, -jitterPixels.x);
            _setJitterY(_context, -jitterPixels.y);
            _setPreExposure(_context, preExposure);
            _setReset(_context, reset ? 1 : 0);
            _setSubrectOffsetX(_context, 0);
            _setSubrectOffsetY(_context, 0);
            _setSubrectWidth(_context, (uint)width);
            _setSubrectHeight(_context, (uint)height);
            // Unity's names are misleading: NGX uses these only to orient its
            // optional on-screen indicator, not to transform motion vectors.
            _setInvertX(_context, 0u);
            _setInvertY(_context, SystemInfo.graphicsUVStartsAtTop ? 1u : 0u);

            _setColorInput(_textureTable, colorInput);
            _setColorOutput(_textureTable, colorOutput);
            _setDepth(_textureTable, depth);
            _setMotionVectors(_textureTable, motionVectors);
            _setBiasColorMask(_textureTable, biasColorMask);

            commandBuffer.Clear();
            // Fail open if the native command is accepted but does not write an
            // output. This also establishes an explicit graphics transition from
            // render target to the UAV state required by Unity's NVIDIA plugin.
            commandBuffer.Blit(colorInput, colorOutput);
            _executeDlss(_device, commandBuffer, _context, _textureTable);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Clear();
        }

        public void DestroyContext(CommandBuffer commandBuffer)
        {
            if (_context == null)
            {
                return;
            }

            object context = _context;
            _context = null;
            _textureTable = null;
            try
            {
                if (_device != null && commandBuffer != null)
                {
                    commandBuffer.Clear();
                    _destroyFeature(_device, commandBuffer, context);
                    Graphics.ExecuteCommandBuffer(commandBuffer);
                    commandBuffer.Clear();
                }
            }
            catch
            {
                if (commandBuffer != null)
                {
                    commandBuffer.Clear();
                }
            }
        }

        private SetFloatDelegate CreateFloatSetter(string propertyName)
        {
            return (SetFloatDelegate)CreateExecutionSetter(
                propertyName,
                typeof(float),
                typeof(SetFloatDelegate)
            );
        }

        private SetIntDelegate CreateIntSetter(string propertyName)
        {
            return (SetIntDelegate)CreateExecutionSetter(
                propertyName,
                typeof(int),
                typeof(SetIntDelegate)
            );
        }

        private SetUIntDelegate CreateUIntSetter(string propertyName)
        {
            return (SetUIntDelegate)CreateExecutionSetter(
                propertyName,
                typeof(uint),
                typeof(SetUIntDelegate)
            );
        }

        private SetTextureDelegate CreateTextureSetter(string propertyName)
        {
            return (SetTextureDelegate)CreateBoxedValueSetter(
                _textureTableType,
                propertyName,
                typeof(Texture),
                typeof(SetTextureDelegate)
            );
        }

        private Delegate CreateExecutionSetter(
            string propertyName,
            Type valueType,
            Type delegateType)
        {
            PropertyInfo property = RequireProperty(
                _executionType,
                propertyName,
                PublicInstance
            );
            MethodInfo setter = property.GetSetMethod(false);
            if (setter == null || property.PropertyType != valueType)
            {
                throw new MissingMethodException(
                    _executionType.FullName,
                    "set_" + propertyName
                );
            }

            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_Set_" + propertyName,
                typeof(void),
                new[] { typeof(object), valueType },
                typeof(NvidiaDlaaApi),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _contextType);
            il.Emit(OpCodes.Callvirt, _executionDataGetter);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, setter);
            il.Emit(OpCodes.Ret);
            return dynamicMethod.CreateDelegate(delegateType);
        }

        private static Delegate CreateBoxedValueSetter(
            Type declaringType,
            string propertyName,
            Type valueType,
            Type delegateType)
        {
            PropertyInfo property = RequireProperty(
                declaringType,
                propertyName,
                PublicInstance
            );
            MethodInfo setter = property.GetSetMethod(false);
            if (setter == null || property.PropertyType != valueType ||
                !declaringType.IsValueType)
            {
                throw new MissingMethodException(
                    declaringType.FullName,
                    "set_" + propertyName
                );
            }

            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_Set_" + propertyName,
                typeof(void),
                new[] { typeof(object), valueType },
                typeof(NvidiaDlaaApi),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Unbox, declaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, setter);
            il.Emit(OpCodes.Ret);
            return dynamicMethod.CreateDelegate(delegateType);
        }

        private CreateFeatureDelegate CreateFeatureInvoker(MethodInfo method)
        {
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_Nvidia_CreateFeature",
                typeof(object),
                new[] { typeof(object), typeof(CommandBuffer), typeof(object) },
                typeof(NvidiaDlaaApi),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            LocalBuilder initialization = il.DeclareLocal(_initializationType);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Unbox_Any, _initializationType);
            il.Emit(OpCodes.Stloc, initialization);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _deviceType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldloca, initialization);
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
            return (CreateFeatureDelegate)dynamicMethod.CreateDelegate(
                typeof(CreateFeatureDelegate)
            );
        }

        private DestroyFeatureDelegate CreateDestroyInvoker(MethodInfo method)
        {
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_Nvidia_DestroyFeature",
                typeof(void),
                new[] { typeof(object), typeof(CommandBuffer), typeof(object) },
                typeof(NvidiaDlaaApi),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _deviceType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Castclass, _contextType);
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
            return (DestroyFeatureDelegate)dynamicMethod.CreateDelegate(
                typeof(DestroyFeatureDelegate)
            );
        }

        private ExecuteDlssDelegate CreateExecuteInvoker(MethodInfo method)
        {
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_Nvidia_ExecuteDLSS",
                typeof(void),
                new[]
                {
                    typeof(object),
                    typeof(CommandBuffer),
                    typeof(object),
                    typeof(object)
                },
                typeof(NvidiaDlaaApi),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            LocalBuilder textures = il.DeclareLocal(_textureTableType);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Unbox_Any, _textureTableType);
            il.Emit(OpCodes.Stloc, textures);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _deviceType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Castclass, _contextType);
            il.Emit(OpCodes.Ldloca, textures);
            il.Emit(OpCodes.Callvirt, method);
            il.Emit(OpCodes.Ret);
            return (ExecuteDlssDelegate)dynamicMethod.CreateDelegate(
                typeof(ExecuteDlssDelegate)
            );
        }

        private void SetInitializationProperty(
            object initialization,
            string propertyName,
            object value)
        {
            PropertyInfo property = RequireProperty(
                _initializationType,
                propertyName,
                PublicInstance
            );
            property.SetValue(initialization, value, null);
        }

        private static Type RequireType(string name)
        {
            Type type = Type.GetType(name + ", " + AssemblyName, false);
            if (type == null)
            {
                throw new TypeLoadException(name);
            }
            return type;
        }

        private static MethodInfo RequireMethod(
            Type type,
            string name,
            BindingFlags flags)
        {
            MethodInfo method = type.GetMethod(name, flags);
            if (method == null)
            {
                throw new MissingMethodException(type.FullName, name);
            }
            return method;
        }

        private static PropertyInfo RequireProperty(
            Type type,
            string name,
            BindingFlags flags)
        {
            PropertyInfo property = type.GetProperty(name, flags);
            if (property == null)
            {
                throw new MissingMemberException(type.FullName, name);
            }
            return property;
        }

        private static bool InvokeBoolean(
            MethodInfo method,
            object instance,
            params object[] arguments)
        {
            object value = method.Invoke(instance, arguments);
            return value is bool result && result;
        }

        private static string DescribeException(Exception exception)
        {
            Exception root = exception is TargetInvocationException invocation &&
                             invocation.InnerException != null
                ? invocation.InnerException
                : exception;
            return root.GetType().Name + ": " + root.Message;
        }
    }
}
