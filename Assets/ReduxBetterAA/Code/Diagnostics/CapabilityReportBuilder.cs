using System;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using SpaceWarp2.API.Mods;
using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    internal static class CapabilityReportBuilder
    {
        internal static RuntimeRecord CaptureRuntime(SpaceWarpPluginDescriptor metadata)
        {
            string reduxVersion = "Unavailable";
            var plugins = PluginList.AllEnabledAndActivePlugins;
            var mods = new System.Collections.Generic.List<ModVersionRecord>();
            for (int index = 0; index < plugins.Count; index++)
            {
                SpaceWarpPluginDescriptor descriptor = plugins[index];
                if (descriptor == null || descriptor.SWInfo == null)
                {
                    continue;
                }
                mods.Add(new ModVersionRecord { id = descriptor.Guid, version = descriptor.SWInfo.Version });
                if (string.Equals(
                        descriptor.Guid,
                        "Ksp2Redux",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        descriptor.Name,
                        "KSP2 Redux",
                        StringComparison.OrdinalIgnoreCase))
                {
                    reduxVersion = descriptor.SWInfo.Version;
                }
            }

            Version assemblyVersion = typeof(Phase1ProbeService).Assembly.GetName().Version;
            return new RuntimeRecord
            {
                mods = mods.ToArray(),
                modAssemblySha256 = IssueReportArchive.HashFile(typeof(Phase1ProbeService).Assembly.Location),
                modVersion = metadata?.SWInfo?.Version ?? assemblyVersion.ToString(),
                gameVersion = Application.version,
                reduxVersion = reduxVersion,
                unityVersion = Application.unityVersion,
                operatingSystem = SystemInfo.operatingSystem,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceName = SystemInfo.graphicsDeviceName,
                graphicsDeviceVendor = SystemInfo.graphicsDeviceVendor,
                graphicsDeviceId = SystemInfo.graphicsDeviceID,
                graphicsDeviceVendorId = SystemInfo.graphicsDeviceVendorID,
                graphicsMemoryMb = SystemInfo.graphicsMemorySize,
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded
            };
        }

        private static FrameGenerationRecord CaptureFrameGeneration() => new FrameGenerationRecord
        {
            requested = FrameGenerationPolicy.Label(FrameGenerationAvailability.Requested),
            selected = FrameGenerationPolicy.Label(FrameGenerationAvailability.Selected),
            active = FrameGenerationAvailability.Active,
            availableModes = FrameGenerationAvailability.BuildChoices(),
            unavailableReason = FrameGenerationAvailability.UnavailableReason,
            renderedFramesPerSecond = null,
            displayedFramesPerSecond = null,
            inputLatencyMilliseconds = null
        };

        internal static TemporalBackendRecord CaptureTemporalBackend()
        {
            TemporalCoordinator coordinator = TemporalCoordinator.Current;
            if (coordinator == null)
            {
                return new TemporalBackendRecord
                {
                    requestedBackend = "Off",
                    selectedBackend = "Off",
                    active = false,
                    status = "Temporal coordinator unavailable",
                    fallbackReason = "Temporal coordinator unavailable",
                    frameGeneration = CaptureFrameGeneration(),
                    lastResetReason = HistoryResetReason.None.ToString()
                };
            }

            CustomTaaConfig custom = coordinator.CustomConfig;
            DlaaConfig dlaa = coordinator.DlaaConfig;
            Fsr2Config fsr2 = coordinator.Fsr2Config;
            MotionVectorMatrixSnapshot matrix =
                coordinator.MotionVectorMatrixSnapshot;
            VegetationMotionCompatibility vegetationRepair =
                VegetationMotionCompatibility.Current;
            return new TemporalBackendRecord
            {
                supersamplingPercent = coordinator.SupersamplingPercent,
                appliedRenderScalePercent = coordinator.AppliedRenderScalePercent,
                frameGeneration = CaptureFrameGeneration(),
                requestedBackend = coordinator.RequestedBackend.ToString(),
                selectedBackend = coordinator.SelectedBackend,
                active = coordinator.Active,
                resolveCamera = coordinator.ResolveCameraName,
                sharedJitterCamera = coordinator.SharedJitterCameraName,
                projectionJitterSupported =
                    coordinator.ProjectionJitterSupported,
                jitterTransparentRendering = coordinator.JitterTransparentRendering,
                mapViewAaEnabled = coordinator.MapViewAaEnabled,
                mapViewAaOverrideActive =
                    coordinator.MapViewAaOverrideActive,
                status = coordinator.Status,
                fallbackReason = coordinator.Requested && !coordinator.Active &&
                    !coordinator.MapViewAaOverrideActive
                    ? coordinator.Status
                    : string.Empty,
                lastResetReason = coordinator.LastResetReason.ToString(),
                customEstimatedMemoryBytes = coordinator.CustomEstimatedMemoryBytes,
                dlaaEstimatedMemoryBytes = coordinator.DlaaEstimatedMemoryBytes,
                fsr2EstimatedMemoryBytes = coordinator.Fsr2EstimatedMemoryBytes,
                motionVectorSanitizerEstimatedMemoryBytes =
                    coordinator.MotionVectorSanitizerEstimatedMemoryBytes,
                depthDisocclusionMaskEstimatedMemoryBytes =
                    coordinator.DepthDisocclusionMaskEstimatedMemoryBytes,
                vendorMotionRejectionPixels =
                    MotionVectorSanitizer.MaximumMotionPixels,
                vegetationMotionRepairEnabled = vegetationRepair != null &&
                    vegetationRepair.Enabled,
                vegetationMotionRepairAvailable = vegetationRepair != null &&
                    vegetationRepair.Available,
                vegetationMotionReroutedCalls = vegetationRepair == null
                    ? 0L
                    : vegetationRepair.ReroutedCalls,
                vegetationMotionRepairStatus = vegetationRepair == null
                    ? "Vegetation motion repair unavailable"
                    : vegetationRepair.Status,
                motionVectorSanitizerEnabled =
                    coordinator.MotionVectorSanitizerEnabled,
                motionVectorSanitizerStatus =
                    coordinator.MotionVectorSanitizerStatus,
                motionMatrix = CaptureMotionMatrix(in matrix),
                depthDisocclusionMaskStatus =
                    coordinator.DepthDisocclusionMaskStatus,
                custom = new CustomTaaSettingsRecord
                {
                    jitterSpread = custom.JitterSpread,
                    sequenceLength = custom.SequenceLength,
                    stationaryHistory = custom.StationaryHistory,
                    movingHistory = custom.MovingHistory,
                    motionResponsePixels = custom.MotionResponsePixels,
                    maximumMotionPixels = custom.MaximumMotionPixels,
                    depthThreshold = custom.DepthThreshold,
                    depthEdgeStability = custom.DepthEdgeStability,
                    varianceGamma = custom.VarianceGamma,
                    reactiveScale = custom.ReactiveScale,
                    sharpening = custom.Sharpening,
                    noDepthHistory = custom.NoDepthHistory,
                    debugView = custom.DebugView.ToString()
                },
                dlaa = new DlaaSettingsRecord
                {
                    jitterSpread = dlaa.JitterSpread,
                    sequenceLength = dlaa.SequenceLength,
                    sharpness = dlaa.Sharpness,
                    preExposure = dlaa.PreExposure,
                    autoExposure = dlaa.AutoExposure,
                    preferPpv2Exposure = dlaa.PreferPpv2Exposure,
                    effectiveExposureSource = coordinator.DlaaExposureSource,
                    effectivePreExposure = coordinator.DlaaEffectivePreExposure,
                    invertMotionX = dlaa.InvertMotionX,
                    invertMotionY = dlaa.InvertMotionY,
                    preset = dlaa.Preset.ToString(),
                    allowSupersampling = dlaa.AllowSupersampling,
                    managedSurfaceAvailable =
                        coordinator.DlaaManagedSurfaceAvailable,
                    contextCreated = coordinator.DlaaContextCreated,
                    contextUsesHdr = coordinator.DlaaContextUsesHdr,
                    deviceVersion = coordinator.DlaaDeviceVersion,
                    inputWidth = coordinator.DlaaInputWidth,
                    inputHeight = coordinator.DlaaInputHeight,
                    outputWidth = coordinator.DlaaOutputWidth,
                    outputHeight = coordinator.DlaaOutputHeight,
                    outputGraphicsFormat = coordinator.DlaaOutputGraphicsFormat,
                    outputRandomWrite = coordinator.DlaaOutputRandomWrite,
                    nativeResolution = coordinator.DlaaInputWidth > 0 &&
                        coordinator.DlaaInputWidth == coordinator.DlaaOutputWidth &&
                        coordinator.DlaaInputHeight == coordinator.DlaaOutputHeight,
                    lastFailure = coordinator.DlaaLastFailure
                },
                fsr2 = new Fsr2SettingsRecord
                {
                    jitterSpread = fsr2.JitterSpread,
                    sequenceLength = fsr2.SequenceLength,
                    enableSharpening = fsr2.EnableSharpening,
                    sharpness = fsr2.Sharpness,
                    preExposure = fsr2.PreExposure,
                    autoExposure = fsr2.AutoExposure,
                    preferPpv2Exposure = fsr2.PreferPpv2Exposure,
                    effectiveExposureSource = coordinator.Fsr2ExposureSource,
                    effectivePreExposure = coordinator.Fsr2EffectivePreExposure,
                    projectionJitterPixels = new[]
                    {
                        coordinator.Fsr2ProjectionJitterPixels.x,
                        coordinator.Fsr2ProjectionJitterPixels.y
                    },
                    dispatchJitterPixels = new[]
                    {
                        coordinator.Fsr2DispatchJitterPixels.x,
                        coordinator.Fsr2DispatchJitterPixels.y
                    },
                    invertMotionX = fsr2.InvertMotionX,
                    invertMotionY = fsr2.InvertMotionY,
                    managedSurfaceAvailable =
                        coordinator.Fsr2ManagedSurfaceAvailable,
                    contextCreated = coordinator.Fsr2ContextCreated,
                    contextUsesHdr = coordinator.Fsr2ContextUsesHdr,
                    deviceVersion = coordinator.Fsr2DeviceVersion,
                    inputWidth = coordinator.Fsr2InputWidth,
                    inputHeight = coordinator.Fsr2InputHeight,
                    outputWidth = coordinator.Fsr2OutputWidth,
                    outputHeight = coordinator.Fsr2OutputHeight,
                    outputGraphicsFormat = coordinator.Fsr2OutputGraphicsFormat,
                    outputRandomWrite = coordinator.Fsr2OutputRandomWrite,
                    nativeResolution = coordinator.Fsr2InputWidth > 0 &&
                        coordinator.Fsr2InputWidth == coordinator.Fsr2OutputWidth &&
                        coordinator.Fsr2InputHeight == coordinator.Fsr2OutputHeight,
                    lastFailure = coordinator.Fsr2LastFailure
                },
                performance = new PerformanceProfilesRecord
                {
                    off = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.Off
                    ),
                    fxaaLow = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.FxaaLow
                    ),
                    smaa = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.Smaa
                    ),
                    fxaaHigh = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.FxaaHigh
                    ),
                    custom = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.CustomTaa
                    ),
                    dlaa = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.NvidiaDlaa
                    ),
                    fsr2 = CapturePerformanceProfile(
                        coordinator,
                        BackendSelection.AmdFsr2
                    ),
                    supersampling = CapturePerformanceProfile(coordinator, BackendSelection.Supersampling),
                    dlss = CapturePerformanceProfile(coordinator, BackendSelection.NvidiaDlss),
                    fsrUpscaling = CapturePerformanceProfile(coordinator, BackendSelection.AmdFsrUpscaling)
                }
            };
        }

        private static MotionMatrixRecord CaptureMotionMatrix(
            in MotionVectorMatrixSnapshot snapshot)
        {
            return new MotionMatrixRecord
            {
                frame = snapshot.Frame,
                valid = snapshot.Valid,
                unityCurrentVsTrackedCurrentMaxAbs =
                    snapshot.UnityCurrentVsTrackedCurrentMaxAbs,
                unityPreviousVsTrackedPreviousMaxAbs =
                    snapshot.UnityPreviousVsTrackedPreviousMaxAbs,
                unityPreviousVsCurrentMaxAbs =
                    snapshot.UnityPreviousVsCurrentMaxAbs,
                trackedPreviousVsCurrentMaxAbs =
                    snapshot.TrackedPreviousVsCurrentMaxAbs,
                fieldOfView = snapshot.FieldOfView,
                nearClipPlane = snapshot.NearClipPlane,
                farClipPlane = snapshot.FarClipPlane,
                aspect = snapshot.Aspect,
                currentJitterPixels = new[]
                {
                    snapshot.CurrentJitterPixels.x,
                    snapshot.CurrentJitterPixels.y
                },
                currentJitterNormalized = new[]
                {
                    snapshot.CurrentJitterNormalized.x,
                    snapshot.CurrentJitterNormalized.y
                },
                cameraPosition = new[]
                {
                    snapshot.CameraPosition.x,
                    snapshot.CameraPosition.y,
                    snapshot.CameraPosition.z
                },
                cameraRotation = new[]
                {
                    snapshot.CameraRotation.x,
                    snapshot.CameraRotation.y,
                    snapshot.CameraRotation.z,
                    snapshot.CameraRotation.w
                },
                unityNonJitteredViewProjection = MatrixValues(
                    snapshot.UnityNonJitteredViewProjection
                ),
                unityPreviousViewProjection = MatrixValues(
                    snapshot.UnityPreviousViewProjection
                ),
                trackedCurrentViewProjection = MatrixValues(
                    snapshot.TrackedCurrentViewProjection
                ),
                trackedPreviousViewProjection = MatrixValues(
                    snapshot.TrackedPreviousViewProjection
                )
            };
        }

        private static float[] MatrixValues(Matrix4x4 matrix)
        {
            var values = new float[16];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = matrix[index];
            }
            return values;
        }

        private static PerformanceProfileRecord CapturePerformanceProfile(
            TemporalCoordinator coordinator,
            BackendSelection mode)
        {
            PerformanceProfileSnapshot snapshot =
                coordinator.GetPerformanceProfile(mode);
            return new PerformanceProfileRecord
            {
                state = snapshot.State.ToString(),
                samples = snapshot.Samples,
                targetSamples = snapshot.TargetSamples,
                averageCpuFrameMilliseconds =
                    snapshot.AverageCpuFrameMilliseconds,
                peakCpuFrameMilliseconds = snapshot.PeakCpuFrameMilliseconds,
                averageGpuFrameMilliseconds =
                    snapshot.AverageGpuFrameMilliseconds,
                peakGpuFrameMilliseconds = snapshot.PeakGpuFrameMilliseconds,
                gpuSamples = snapshot.GpuSamples,
                averageResolveCpuMilliseconds =
                    snapshot.AverageResolveCpuMilliseconds,
                peakResolveCpuMilliseconds =
                    snapshot.PeakResolveCpuMilliseconds,
                resolveSamples = snapshot.ResolveSamples,
                gpuSource = snapshot.GpuSource.ToString(),
                frameTimingEnabledAtStart = snapshot.FrameTimingEnabledAtStart,
                runtimeGpuRecorderAvailable = snapshot.RuntimeGpuRecorderAvailable,
                cpuTimingSamples = snapshot.CpuTimingSamples,
                cpuFallbackSamples = snapshot.CpuFallbackSamples,
                timingRecords = snapshot.TimingRecords,
                duplicateTimingRecords = snapshot.DuplicateTimingRecords,
                invalidTimingRecords = snapshot.InvalidTimingRecords,
                invalidGpuSamples = snapshot.InvalidGpuSamples,
                gpuUnavailableReason = snapshot.GpuUnavailableReason,
                timingRecorderError = snapshot.TimingRecorderError
            };
        }

        internal static EvidenceRecord BuildEvidence(CameraGraph graph)
        {
            bool presenterTargetPresent = false;
            bool presenterActive = false;
            ulong presentationCameraId = 0;
            float presentationDepth = float.MinValue;
            for (int index = 0; index < graph.presenters.Length; index++)
            {
                PresenterRecord presenter = graph.presenters[index];
                presenterTargetPresent |= presenter.renderTarget != null && presenter.renderTarget.present;
                presenterActive |= presenter.renderingEnabled;
                if (presenter.presentationCameraId != 0)
                {
                    presentationCameraId = presenter.presentationCameraId;
                }
            }

            bool uiAfterPresentation = false;
            bool motionRequested = false;
            bool sceneDepthAttached = false;
            for (int index = 0; index < graph.cameras.Length; index++)
            {
                CameraRecord camera = graph.cameras[index];
                if (camera.instanceId == presentationCameraId)
                {
                    presentationDepth = camera.depth;
                }
                if (camera.depthTextureMode.IndexOf("MotionVectors", StringComparison.Ordinal) >= 0 ||
                    camera.postProcessCameraFlags.IndexOf("MotionVectors", StringComparison.Ordinal) >= 0)
                {
                    motionRequested = true;
                }
                if ((camera.role.IndexOf("ScaledSpaceStack", StringComparison.Ordinal) >= 0 ||
                     camera.role.IndexOf("PhysicsSpaceStack", StringComparison.Ordinal) >= 0) &&
                    camera.targetTexture != null && camera.targetTexture.present &&
                    camera.targetTexture.depthBits > 0)
                {
                    sceneDepthAttached = true;
                }
            }

            if (presentationDepth > float.MinValue)
            {
                for (int index = 0; index < graph.cameras.Length; index++)
                {
                    CameraRecord camera = graph.cameras[index];
                    if (camera.role.IndexOf("UIOrOverlayCandidate", StringComparison.Ordinal) >= 0 &&
                        camera.enabled && camera.depth > presentationDepth)
                    {
                        uiAfterPresentation = true;
                        break;
                    }
                }
            }

            return new EvidenceRecord
            {
                finalSceneColorCandidate = presenterTargetPresent
                    ? "RenderScalePresenter shared color target; presented by its camera at AfterEverything"
                    : presenterActive
                        ? "RenderScalePresenter active but its shared target was unavailable during capture"
                        : "No active RenderScalePresenter target in this capture",
                uiCompositionCandidate = uiAfterPresentation
                    ? "At least one UI/overlay candidate renders after the presentation camera"
                    : "Not yet demonstrated by camera depth ordering",
                depthStatus = sceneDepthAttached
                    ? "A scene-stack target has a depth attachment; visual coverage still requires capture"
                    : "No shared scene-stack depth attachment demonstrated in this capture",
                motionVectorStatus = motionRequested
                    ? "At least one camera requests motion vectors; visual coverage still requires capture"
                    : "No camera requested motion vectors during this capture",
                resolvePlacementStatus =
                    "One selected AA backend resolves on the final scene camera before UI; verify the actual input/output capture stages"
            };
        }

    }
}
