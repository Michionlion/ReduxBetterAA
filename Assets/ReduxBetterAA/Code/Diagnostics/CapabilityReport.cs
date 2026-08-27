using System;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Diagnostics
{
    [Serializable]
    public sealed class Phase1Report
    {
        public int schemaVersion;
        public string capturedUtc;
        public string captureReason;
        public RuntimeRecord runtime;
        public CapabilityRecord capabilities;
        public CameraGraph cameraGraph;
        public EvidenceRecord evidence;
        public MotionCadenceRecord motionCadence;
        public TemporalBackendRecord temporal;
    }

    [Serializable]
    public sealed class RuntimeRecord
    {
        public string modVersion;
        public string gameVersion;
        public string reduxVersion;
        public string unityVersion;
        public string operatingSystem;
        public string graphicsApi;
        public string graphicsDeviceName;
        public string graphicsDeviceVendor;
        public int graphicsDeviceId;
        public int graphicsDeviceVendorId;
        public int graphicsMemoryMb;
        public string graphicsDeviceVersion;
        public bool graphicsMultiThreaded;
    }

    [Serializable]
    public sealed class CapabilityRecord
    {
        public bool supportsRenderTextures;
        public bool supportsMotionVectors;
        public string motionVectorSupportSource;
        public bool supportsAsyncGpuReadback;
        public VendorModuleRecord nvidia;
        public VendorModuleRecord amd;
    }

    [Serializable]
    public sealed class VendorModuleRecord
    {
        public string vendor;
        public bool managedAssemblyPresent;
        public string managedAssemblyVersion;
        public bool apiTypesPresent;
        public bool pluginWasLoaded;
        public bool pluginLoadAttempted;
        public bool pluginLoadSucceeded;
        public bool graphicsDeviceAvailable;
        public uint graphicsDeviceVersion;
        public bool featureQueryAttempted;
        public bool featureAvailable;
        public bool nativeFeatureCreationAttempted;
        public string featureName;
        public string status;
        public string errorType;
        public string errorMessage;
    }

    [Serializable]
    public sealed class EvidenceRecord
    {
        public string finalSceneColorCandidate;
        public string uiCompositionCandidate;
        public string depthStatus;
        public string motionVectorStatus;
        public string resolvePlacementStatus;
    }

    [Serializable]
    public sealed class MotionCadenceRecord
    {
        public float fixedDeltaTimeMilliseconds;
        public float fixedUpdateHz;
        public bool experimentalRenderInterpolationEnabled;
        public int interpolatedKspPhysicsBodies;
        public string interpolationStatus;
    }

    [Serializable]
    public sealed class TemporalBackendRecord
    {
        public string requestedBackend;
        public string selectedBackend;
        public bool active;
        public string resolveCamera;
        public string sharedJitterCamera;
        public string status;
        public string fallbackReason;
        public string lastResetReason;
        public long customEstimatedMemoryBytes;
        public long dlaaEstimatedMemoryBytes;
        public long fsr2EstimatedMemoryBytes;
        public long motionVectorSanitizerEstimatedMemoryBytes;
        public long depthDisocclusionMaskEstimatedMemoryBytes;
        public float vendorMotionRejectionPixels;
        public string motionVectorSanitizerStatus;
        public MotionMatrixRecord motionMatrix;
        public string depthDisocclusionMaskStatus;
        public Ppv2SettingsRecord ppv2;
        public CustomTaaSettingsRecord custom;
        public DlaaSettingsRecord dlaa;
        public Fsr2SettingsRecord fsr2;
        public PerformanceProfilesRecord performance;
    }

    [Serializable]
    public sealed class MotionMatrixRecord
    {
        public int frame;
        public bool valid;
        public float unityCurrentVsTrackedCurrentMaxAbs;
        public float unityPreviousVsTrackedPreviousMaxAbs;
        public float unityPreviousVsCurrentMaxAbs;
        public float trackedPreviousVsCurrentMaxAbs;
        public float fieldOfView;
        public float nearClipPlane;
        public float farClipPlane;
        public float aspect;
        public float[] currentJitterPixels;
        public float[] currentJitterNormalized;
        public float[] cameraPosition;
        public float[] cameraRotation;
        public float[] unityNonJitteredViewProjection;
        public float[] unityPreviousViewProjection;
        public float[] trackedCurrentViewProjection;
        public float[] trackedPreviousViewProjection;
    }

    [Serializable]
    public sealed class Ppv2SettingsRecord
    {
        public float jitterSpread;
        public float sharpness;
        public float stationaryBlending;
        public float motionBlending;
    }

    [Serializable]
    public sealed class CustomTaaSettingsRecord
    {
        public float jitterSpread;
        public int sequenceLength;
        public float stationaryHistory;
        public float movingHistory;
        public float motionResponsePixels;
        public float maximumMotionPixels;
        public float depthThreshold;
        public float depthEdgeStability;
        public float varianceGamma;
        public float reactiveScale;
        public float sharpening;
        public float noDepthHistory;
        public string debugView;
    }

    [Serializable]
    public sealed class DlaaSettingsRecord
    {
        public float jitterSpread;
        public int sequenceLength;
        public float sharpness;
        public float preExposure;
        public bool autoExposure;
        public bool preferPpv2Exposure;
        public string effectiveExposureSource;
        public float effectivePreExposure;
        public bool invertMotionX;
        public bool invertMotionY;
        public string preset;
        public bool allowSupersampling;
        public bool managedSurfaceAvailable;
        public bool contextCreated;
        public uint deviceVersion;
        public int inputWidth;
        public int inputHeight;
        public int outputWidth;
        public int outputHeight;
        public string outputGraphicsFormat;
        public bool outputRandomWrite;
        public bool nativeResolution;
        public string lastFailure;
    }

    [Serializable]
    public sealed class Fsr2SettingsRecord
    {
        public float jitterSpread;
        public int sequenceLength;
        public bool enableSharpening;
        public float sharpness;
        public float preExposure;
        public bool autoExposure;
        public bool preferPpv2Exposure;
        public string effectiveExposureSource;
        public float effectivePreExposure;
        public float[] projectionJitterPixels;
        public float[] dispatchJitterPixels;
        public bool invertMotionX;
        public bool invertMotionY;
        public bool managedSurfaceAvailable;
        public bool contextCreated;
        public uint deviceVersion;
        public int inputWidth;
        public int inputHeight;
        public int outputWidth;
        public int outputHeight;
        public string outputGraphicsFormat;
        public bool outputRandomWrite;
        public bool nativeResolution;
        public string lastFailure;
    }

    [Serializable]
    public sealed class PerformanceProfilesRecord
    {
        public PerformanceProfileRecord off;
        public PerformanceProfileRecord fxaaLow;
        public PerformanceProfileRecord smaa;
        public PerformanceProfileRecord fxaaHigh;
        public PerformanceProfileRecord ppv2;
        public PerformanceProfileRecord custom;
        public PerformanceProfileRecord dlaa;
        public PerformanceProfileRecord fsr2;
    }

    [Serializable]
    public sealed class PerformanceProfileRecord
    {
        public string state;
        public int samples;
        public int targetSamples;
        public double averageCpuFrameMilliseconds;
        public double peakCpuFrameMilliseconds;
        public double averageGpuFrameMilliseconds;
        public double peakGpuFrameMilliseconds;
        public int gpuSamples;
        public double averageResolveCpuMilliseconds;
        public double peakResolveCpuMilliseconds;
        public int resolveSamples;
    }
}
