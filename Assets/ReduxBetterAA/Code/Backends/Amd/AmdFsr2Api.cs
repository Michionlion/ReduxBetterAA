using System;
using System.Reflection;
using System.Reflection.Emit;
using ReduxBetterAA.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Backends.Amd
{
    /// <summary>
    /// Cached reflection bridge for UnityEngine.AMDModule. The optional managed
    /// and native AMD modules never become static dependencies of the core mod.
    /// Dynamic delegates keep reflection and boxing out of the render hot path.
    /// </summary>
    internal sealed class AmdFsr2Api
    {
        private const string AssemblyName = "UnityEngine.AMDModule";
        private const int FlagHdr = 1 << 0;
        private const int FlagDisplayResolutionMotionVectors = 1 << 1;
        private const int FlagDepthInverted = 1 << 3;
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
        private delegate void ExecuteFsr2Delegate(
            object device,
            CommandBuffer commandBuffer,
            object context,
            object textureTable);
        private delegate void SetFloatDelegate(object context, float value);
        private delegate void SetIntDelegate(object context, int value);
        private delegate void SetUIntDelegate(object context, uint value);
        private delegate void SetTextureDelegate(object table, Texture value);

        private Type _deviceType;
        private Type _flagsType;
        private Type _initializationType;
        private Type _contextType;
        private Type _executionType;
        private Type _textureTableType;
        private MethodInfo _pluginIsLoaded;
        private MethodInfo _pluginLoad;
        private MethodInfo _createGraphicsDevice;
        private PropertyInfo _deviceProperty;
        private PropertyInfo _deviceVersion;
        private MethodInfo _executionDataGetter;
        private CreateFeatureDelegate _createFeature;
        private DestroyFeatureDelegate _destroyFeature;
        private ExecuteFsr2Delegate _executeFsr2;
        private SetFloatDelegate _setJitterX;
        private SetFloatDelegate _setJitterY;
        private SetFloatDelegate _setMotionScaleX;
        private SetFloatDelegate _setMotionScaleY;
        private SetUIntDelegate _setRenderWidth;
        private SetUIntDelegate _setRenderHeight;
        private SetIntDelegate _setEnableSharpening;
        private SetFloatDelegate _setSharpness;
        private SetFloatDelegate _setFrameTimeDelta;
        private SetFloatDelegate _setPreExposure;
        private SetIntDelegate _setReset;
        private SetFloatDelegate _setCameraNear;
        private SetFloatDelegate _setCameraFar;
        private SetFloatDelegate _setCameraFov;
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
                Type pluginType = RequireType("UnityEngine.AMD.AMDUnityPlugin");
                _deviceType = RequireType("UnityEngine.AMD.GraphicsDevice");
                _flagsType = RequireType("UnityEngine.AMD.FfxFsr2InitializationFlags");
                _initializationType = RequireType(
                    "UnityEngine.AMD.FSR2CommandInitializationData"
                );
                _contextType = RequireType("UnityEngine.AMD.FSR2Context");
                _executionType = RequireType(
                    "UnityEngine.AMD.FSR2CommandExecutionData"
                );
                _textureTableType = RequireType("UnityEngine.AMD.FSR2TextureTable");

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
                _deviceProperty = RequireProperty(_deviceType, "device", PublicStatic);
                _deviceVersion = RequireProperty(_deviceType, "version", PublicStatic);

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
                MethodInfo executeFsr2 = RequireMethod(
                    _deviceType,
                    "ExecuteFSR2",
                    PublicInstance
                );
                PropertyInfo executionData = RequireProperty(
                    _contextType,
                    "executeData",
                    PublicInstance
                );
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

                _createFeature = CreateFeatureInvoker(createFeature);
                _destroyFeature = CreateDestroyInvoker(destroyFeature);
                _executeFsr2 = CreateExecuteInvoker(executeFsr2);
                _setJitterX = CreateFloatSetter("jitterOffsetX");
                _setJitterY = CreateFloatSetter("jitterOffsetY");
                _setMotionScaleX = CreateFloatSetter("MVScaleX");
                _setMotionScaleY = CreateFloatSetter("MVScaleY");
                _setRenderWidth = CreateUIntSetter("renderSizeWidth");
                _setRenderHeight = CreateUIntSetter("renderSizeHeight");
                _setEnableSharpening = CreateIntSetter("enableSharpening");
                _setSharpness = CreateFloatSetter("sharpness");
                _setFrameTimeDelta = CreateFloatSetter("frameTimeDelta");
                _setPreExposure = CreateFloatSetter("preExposure");
                _setReset = CreateIntSetter("reset");
                _setCameraNear = CreateFloatSetter("cameraNear");
                _setCameraFar = CreateFloatSetter("cameraFar");
                _setCameraFov = CreateFloatSetter("cameraFovAngleVertical");
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
                failureReason = "managed AMD FSR2 API unavailable: " + failureReason;
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
                        "native Unity AMD plugin did not load (not included in the normal mod package)";
                    return false;
                }

                _createGraphicsDevice.Invoke(null, null);
                _device = _deviceProperty.GetValue(null, null);
                if (_device == null)
                {
                    failureReason = "Unity AMD graphics device creation returned null";
                    return false;
                }
                object version = _deviceVersion.GetValue(null, null);
                _version = version is uint uintVersion ? uintVersion : 0;
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
            out string failureReason)
        {
            DestroyContext(commandBuffer);
            if (!_initialized)
            {
                failureReason = "Unity AMD graphics device is not initialized";
                return false;
            }

            try
            {
                object initialization = Activator.CreateInstance(_initializationType);
                SetInitializationField(initialization, "maxRenderSizeWidth", (uint)width);
                SetInitializationField(initialization, "maxRenderSizeHeight", (uint)height);
                SetInitializationField(initialization, "displaySizeWidth", (uint)width);
                SetInitializationField(initialization, "displaySizeHeight", (uint)height);
                int flags = FlagDisplayResolutionMotionVectors;
                if (hdr)
                {
                    flags |= FlagHdr;
                }
                if (SystemInfo.usesReversedZBuffer)
                {
                    flags |= FlagDepthInverted;
                }
                if (autoExposure)
                {
                    flags |= FlagAutoExposure;
                }
                SetInitializationField(
                    initialization,
                    "ffxFsrFlags",
                    Enum.ToObject(_flagsType, flags)
                );

                commandBuffer.Clear();
                _context = _createFeature(_device, commandBuffer, initialization);
                if (_context == null)
                {
                    commandBuffer.Clear();
                    failureReason = "Unity AMD CreateFeature returned null";
                    return false;
                }
                Graphics.ExecuteCommandBuffer(commandBuffer);
                commandBuffer.Clear();
                _textureTable = Activator.CreateInstance(_textureTableType);
                if (_textureTable == null)
                {
                    DestroyContext(commandBuffer);
                    failureReason = "Unity AMD FSR2 texture table was unavailable";
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
            Camera camera,
            float frameDeltaMilliseconds,
            float preExposure,
            in Fsr2Config config,
            bool reset)
        {
            Vector2 dispatchJitter = ToDispatchJitter(jitterPixels);
            _setJitterX(_context, dispatchJitter.x);
            _setJitterY(_context, dispatchJitter.y);
            // MotionVectorSanitizer performs the explicit component transform.
            // These values now only convert normalized UV motion to pixels.
            _setMotionScaleX(_context, width);
            _setMotionScaleY(_context, height);
            _setRenderWidth(_context, (uint)width);
            _setRenderHeight(_context, (uint)height);
            _setEnableSharpening(_context, config.EnableSharpening ? 1 : 0);
            _setSharpness(_context, config.Sharpness);
            _setFrameTimeDelta(_context, Mathf.Max(0.01f, frameDeltaMilliseconds));
            _setPreExposure(_context, preExposure);
            _setReset(_context, reset ? 1 : 0);
            _setCameraNear(_context, camera.nearClipPlane);
            _setCameraFar(_context, camera.farClipPlane);
            _setCameraFov(_context, camera.fieldOfView * Mathf.Deg2Rad);

            _setColorInput(_textureTable, colorInput);
            _setColorOutput(_textureTable, colorOutput);
            _setDepth(_textureTable, depth);
            _setMotionVectors(_textureTable, motionVectors);
            _setBiasColorMask(_textureTable, biasColorMask);

            commandBuffer.Clear();
            // Preserve a valid image when the native command is accepted but
            // writes nothing, and establish the UAV transition explicitly.
            commandBuffer.Blit(colorInput, colorOutput);
            _executeFsr2(_device, commandBuffer, _context, _textureTable);
            Graphics.ExecuteCommandBuffer(commandBuffer);
            commandBuffer.Clear();
        }

        internal static Vector2 ToDispatchJitter(Vector2 projectionJitterPixels)
        {
            // RuntimeUtilities applies the supplied sample by increasing
            // Unity's projection offsets. FSR2's unit-pixel dispatch value
            // describes the opposite projection translation, so both axes
            // must be negated to describe the sample actually rendered.
            return -projectionJitterPixels;
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

        private SetFloatDelegate CreateFloatSetter(string fieldName)
        {
            return (SetFloatDelegate)CreateExecutionFieldSetter(
                fieldName,
                typeof(float),
                typeof(SetFloatDelegate)
            );
        }

        private SetIntDelegate CreateIntSetter(string fieldName)
        {
            return (SetIntDelegate)CreateExecutionFieldSetter(
                fieldName,
                typeof(int),
                typeof(SetIntDelegate)
            );
        }

        private SetUIntDelegate CreateUIntSetter(string fieldName)
        {
            return (SetUIntDelegate)CreateExecutionFieldSetter(
                fieldName,
                typeof(uint),
                typeof(SetUIntDelegate)
            );
        }

        private Delegate CreateExecutionFieldSetter(
            string fieldName,
            Type valueType,
            Type delegateType)
        {
            FieldInfo field = RequireField(_executionType, fieldName, PublicInstance);
            if (field.FieldType != valueType)
            {
                throw new MissingFieldException(_executionType.FullName, fieldName);
            }
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_AMD_Set_" + fieldName,
                typeof(void),
                new[] { typeof(object), valueType },
                typeof(AmdFsr2Api),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, _contextType);
            il.Emit(OpCodes.Callvirt, _executionDataGetter);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return dynamicMethod.CreateDelegate(delegateType);
        }

        private SetTextureDelegate CreateTextureSetter(string propertyName)
        {
            PropertyInfo property = RequireProperty(
                _textureTableType,
                propertyName,
                PublicInstance
            );
            MethodInfo setter = property.GetSetMethod(false);
            if (setter == null || property.PropertyType != typeof(Texture) ||
                !_textureTableType.IsValueType)
            {
                throw new MissingMethodException(
                    _textureTableType.FullName,
                    "set_" + propertyName
                );
            }
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_AMD_Set_" + propertyName,
                typeof(void),
                new[] { typeof(object), typeof(Texture) },
                typeof(AmdFsr2Api),
                true
            );
            ILGenerator il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Unbox, _textureTableType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, setter);
            il.Emit(OpCodes.Ret);
            return (SetTextureDelegate)dynamicMethod.CreateDelegate(
                typeof(SetTextureDelegate)
            );
        }

        private CreateFeatureDelegate CreateFeatureInvoker(MethodInfo method)
        {
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_AMD_CreateFeature",
                typeof(object),
                new[] { typeof(object), typeof(CommandBuffer), typeof(object) },
                typeof(AmdFsr2Api),
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
                "ReduxBetterAA_AMD_DestroyFeature",
                typeof(void),
                new[] { typeof(object), typeof(CommandBuffer), typeof(object) },
                typeof(AmdFsr2Api),
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

        private ExecuteFsr2Delegate CreateExecuteInvoker(MethodInfo method)
        {
            var dynamicMethod = new DynamicMethod(
                "ReduxBetterAA_AMD_ExecuteFSR2",
                typeof(void),
                new[]
                {
                    typeof(object),
                    typeof(CommandBuffer),
                    typeof(object),
                    typeof(object)
                },
                typeof(AmdFsr2Api),
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
            return (ExecuteFsr2Delegate)dynamicMethod.CreateDelegate(
                typeof(ExecuteFsr2Delegate)
            );
        }

        private void SetInitializationField(
            object initialization,
            string fieldName,
            object value)
        {
            FieldInfo field = RequireField(
                _initializationType,
                fieldName,
                PublicInstance
            );
            field.SetValue(initialization, value);
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

        private static FieldInfo RequireField(
            Type type,
            string name,
            BindingFlags flags)
        {
            FieldInfo field = type.GetField(name, flags);
            if (field == null)
            {
                throw new MissingFieldException(type.FullName, name);
            }
            return field;
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
