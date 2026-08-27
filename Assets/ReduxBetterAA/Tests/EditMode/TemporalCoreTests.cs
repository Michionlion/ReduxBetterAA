using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Backends.Amd;
using ReduxBetterAA.Backends.Nvidia;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ReduxBetterAA.Tests
{
    public sealed class TemporalCoreTests
    {
        [Test]
        public void Ppv2JitterMatchesFirstTwoHaltonSamples()
        {
            Vector2 first = SharedJitterSequence.GetPpv2Offset(0, 0.75f);
            Vector2 second = SharedJitterSequence.GetPpv2Offset(1, 0.75f);

            Assert.That(first.x, Is.EqualTo(0.0f).Within(0.000001f));
            Assert.That(first.y, Is.EqualTo(-0.125f).Within(0.000001f));
            Assert.That(second.x, Is.EqualTo(-0.1875f).Within(0.000001f));
            Assert.That(second.y, Is.EqualTo(0.125f).Within(0.000001f));
        }

        [Test]
        public void Ppv2JitterIsDeterministic()
        {
            for (int index = 0; index < 8; index++)
            {
                Assert.That(
                    SharedJitterSequence.GetPpv2Offset(index, 0.75f),
                    Is.EqualTo(SharedJitterSequence.GetPpv2Offset(index, 0.75f))
                );
            }
        }

        [Test]
        public void UserModeChoicesHidePpv2AndUnsupportedVendors()
        {
            CollectionAssert.AreEqual(
                new[] { "Off", "FXAA Low", "SMAA", "FXAA High", "TAA" },
                UserSettingsPolicy.BuildModeChoices(false, false)
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "Off", "FXAA Low", "SMAA", "FXAA High", "TAA",
                    "NVIDIA DLAA", "FSR 2"
                },
                UserSettingsPolicy.BuildModeChoices(true, true)
            );
        }

        [Test]
        public void UserModeCycleIncludesSpatialModesAndPrefersDlaa()
        {
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.Off,
                    true,
                    true
                ),
                Is.EqualTo(BackendSelection.FxaaLow)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.FxaaLow,
                    true,
                    true
                ),
                Is.EqualTo(BackendSelection.Smaa)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.Smaa,
                    true,
                    true
                ),
                Is.EqualTo(BackendSelection.FxaaHigh)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.FxaaHigh,
                    true,
                    true
                ),
                Is.EqualTo(BackendSelection.CustomTaa)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.CustomTaa,
                    true,
                    true
                ),
                Is.EqualTo(BackendSelection.NvidiaDlaa)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.Ppv2Taa,
                    false,
                    true
                ),
                Is.EqualTo(BackendSelection.AmdFsr2)
            );
        }

        [Test]
        public void TemporalConfigClampsToPpv2SupportedRanges()
        {
            var config = new TemporalBackendConfig(-1.0f, 4.0f, -0.5f, 2.0f);

            Assert.That(config.JitterSpread, Is.EqualTo(0.1f));
            Assert.That(config.Sharpness, Is.EqualTo(3.0f));
            Assert.That(config.StationaryBlending, Is.EqualTo(0.0f));
            Assert.That(config.MotionBlending, Is.EqualTo(0.99f));
        }

        [Test]
        public void CustomJitterRepeatsAtConfiguredSequenceLength()
        {
            Vector2 first = SharedJitterSequence.GetCustomOffset(0, 0.75f, 8);
            Vector2 repeated = SharedJitterSequence.GetCustomOffset(8, 0.75f, 8);

            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(
                SharedJitterSequence.GetCustomOffset(1, 0.75f, 8),
                Is.Not.EqualTo(first)
            );
        }

        [Test]
        public void CustomConfigClampsUnsafeValues()
        {
            var config = new CustomTaaConfig(
                -1.0f,
                100,
                2.0f,
                -1.0f,
                0.0f,
                1000.0f,
                0.0f,
                2.0f,
                10.0f,
                20.0f,
                2.0f,
                -1.0f,
                (CustomTaaDebugView)100
            );

            Assert.That(config.JitterSpread, Is.EqualTo(0.1f));
            Assert.That(config.SequenceLength, Is.EqualTo(32));
            Assert.That(config.StationaryHistory, Is.EqualTo(0.99f));
            Assert.That(config.MovingHistory, Is.EqualTo(0.0f));
            Assert.That(config.MotionResponsePixels, Is.EqualTo(0.5f));
            Assert.That(config.MaximumMotionPixels, Is.EqualTo(512.0f));
            Assert.That(config.DepthThreshold, Is.EqualTo(0.0001f));
            Assert.That(config.DepthEdgeStability, Is.EqualTo(1.0f));
            Assert.That(config.VarianceGamma, Is.EqualTo(3.0f));
            Assert.That(config.ReactiveScale, Is.EqualTo(10.0f));
            Assert.That(config.Sharpening, Is.EqualTo(1.0f));
            Assert.That(config.NoDepthHistory, Is.EqualTo(0.0f));
            Assert.That(config.DebugView, Is.EqualTo(CustomTaaDebugView.FinalResolve));
        }

        [Test]
        public void DlaaConfigClampsValuesAndRejectsUnknownPreset()
        {
            var config = new DlaaConfig(
                -2.0f,
                100,
                5.0f,
                0.0f,
                true,
                true,
                false,
                (DlaaPreset)3,
                true
            );

            Assert.That(config.JitterSpread, Is.EqualTo(0.1f));
            Assert.That(config.SequenceLength, Is.EqualTo(32));
            Assert.That(config.Sharpness, Is.EqualTo(1.0f));
            Assert.That(config.PreExposure, Is.EqualTo(0.01f));
            Assert.That(config.AutoExposure, Is.True);
            Assert.That(config.PreferPpv2Exposure, Is.True);
            Assert.That(config.InvertMotionX, Is.True);
            Assert.That(config.InvertMotionY, Is.False);
            Assert.That(config.Preset, Is.EqualTo(DlaaPreset.K));
            Assert.That(config.AllowSupersampling, Is.True);
        }

        [Test]
        public void DlaaContextRecreationOnlyTracksImmutableSettings()
        {
            DlaaConfig original = DlaaConfig.Conservative;
            var dynamicOnly = new DlaaConfig(
                1.0f,
                16,
                0.5f,
                2.0f,
                original.AutoExposure,
                true,
                false,
                original.Preset
            );
            var immutableChange = new DlaaConfig(
                original.JitterSpread,
                original.SequenceLength,
                original.Sharpness,
                original.PreExposure,
                false,
                original.InvertMotionX,
                original.InvertMotionY,
                DlaaPreset.F,
                true
            );

            Assert.That(original.RequiresContextRecreation(in dynamicOnly), Is.False);
            Assert.That(original.RequiresContextRecreation(in immutableChange), Is.True);
        }

        [Test]
        public void VendorDefaultsUseAutomaticExposureAndDlaaPresetK()
        {
            Assert.That(DlaaConfig.Conservative.AutoExposure, Is.True);
            Assert.That(DlaaConfig.Conservative.PreferPpv2Exposure, Is.True);
            Assert.That(DlaaConfig.Conservative.InvertMotionX, Is.True);
            Assert.That(DlaaConfig.Conservative.InvertMotionY, Is.True);
            Assert.That(DlaaConfig.Conservative.Preset, Is.EqualTo(DlaaPreset.K));
            Assert.That(DlaaConfig.Conservative.AllowSupersampling, Is.False);
            Assert.That(Fsr2Config.Conservative.AutoExposure, Is.True);
            Assert.That(Fsr2Config.Conservative.PreferPpv2Exposure, Is.True);
            Assert.That(Fsr2Config.Conservative.InvertMotionX, Is.True);
            Assert.That(Fsr2Config.Conservative.InvertMotionY, Is.True);
        }

        [Test]
        public void MotionSanitizerRejectsLaunchpadScaleOutliers()
        {
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(32.0f / 1920.0f, 0.0f),
                    1920,
                    1080
                ),
                Is.True
            );
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(100.0f / 1920.0f, 0.0f),
                    1920,
                    1080
                ),
                Is.True
            );
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(220.0f / 1920.0f, 0.0f),
                    1920,
                    1080
                ),
                Is.True
            );
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(300.0f / 1920.0f, 0.0f),
                    1920,
                    1080
                ),
                Is.False
            );
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(1300.0f / 1920.0f, 0.0f),
                    1920,
                    1080
                ),
                Is.False
            );
            Assert.That(
                MotionVectorSanitizer.IsMotionUsable(
                    new Vector2(float.NaN, 0.0f),
                    1920,
                    1080
                ),
                Is.False
            );
        }

        [Test]
        public void MotionSanitizerRejectsFiniteQuadrantFieldDisagreement()
        {
            Vector2 cameraMotion = new Vector2(
                2.0f / 1920.0f,
                -1.0f / 1080.0f
            );
            Assert.That(
                MotionVectorSanitizer.DisagreesWithCameraMotion(
                    new Vector2(40.0f / 1920.0f, 8.0f / 1080.0f),
                    cameraMotion,
                    1920,
                    1080
                ),
                Is.False
            );
            Assert.That(
                MotionVectorSanitizer.DisagreesWithCameraMotion(
                    new Vector2(180.0f / 1920.0f, -120.0f / 1080.0f),
                    cameraMotion,
                    1920,
                    1080
                ),
                Is.True
            );
        }

        [Test]
        public void MainMenuCameraScoringPrefersSceneCameraAndRejectsUi()
        {
            var sceneObject = new GameObject("Camera.Scaled");
            var uiObject = new GameObject("FlowCamera");
            try
            {
                Camera sceneCamera = sceneObject.AddComponent<Camera>();
                Camera uiCamera = uiObject.AddComponent<Camera>();

                Assert.That(
                    TemporalCameraDiscovery.ScoreMainMenuCamera(sceneCamera),
                    Is.GreaterThan(int.MinValue)
                );
                Assert.That(
                    TemporalCameraDiscovery.ScoreMainMenuCamera(uiCamera),
                    Is.EqualTo(int.MinValue)
                );
            }
            finally
            {
                Object.DestroyImmediate(sceneObject);
                Object.DestroyImmediate(uiObject);
            }
        }

        [Test]
        public void MainMenuBackgroundScoringSelectsPredecessorSkybox()
        {
            var resolveObject = new GameObject("Camera.Scaled");
            var skyboxObject = new GameObject("Skybox");
            var lateSkyboxObject = new GameObject("Skybox.Late");
            try
            {
                Camera resolveCamera = resolveObject.AddComponent<Camera>();
                Camera skyboxCamera = skyboxObject.AddComponent<Camera>();
                Camera lateSkyboxCamera = lateSkyboxObject.AddComponent<Camera>();
                resolveCamera.depth = -1.0f;
                skyboxCamera.depth = -3.0f;
                lateSkyboxCamera.depth = 1.0f;

                Assert.That(
                    TemporalCameraDiscovery.ScoreMainMenuBackgroundCamera(
                        skyboxCamera,
                        resolveCamera
                    ),
                    Is.GreaterThan(int.MinValue)
                );
                Assert.That(
                    TemporalCameraDiscovery.ScoreMainMenuBackgroundCamera(
                        lateSkyboxCamera,
                        resolveCamera
                    ),
                    Is.EqualTo(int.MinValue)
                );
                Assert.That(
                    TemporalCameraDiscovery.ScoreMainMenuBackgroundCamera(
                        resolveCamera,
                        resolveCamera
                    ),
                    Is.EqualTo(int.MinValue)
                );
            }
            finally
            {
                Object.DestroyImmediate(resolveObject);
                Object.DestroyImmediate(skyboxObject);
                Object.DestroyImmediate(lateSkyboxObject);
            }
        }

        [Test]
        public void Ppv2ExposureReaderRejectsInvalidGpuValues()
        {
            Assert.That(Ppv2ExposureReader.IsUsableExposure(1.0f), Is.True);
            Assert.That(Ppv2ExposureReader.IsUsableExposure(0.0f), Is.False);
            Assert.That(Ppv2ExposureReader.IsUsableExposure(float.NaN), Is.False);
            Assert.That(
                Ppv2ExposureReader.IsUsableExposure(float.PositiveInfinity),
                Is.False
            );
        }

        [Test]
        public void Ppv2ExposureReadbackIsRateLimitedAndNeverOverlaps()
        {
            Assert.That(
                Ppv2ExposureReader.ShouldScheduleReadback(
                    false, 100, 99, 1.0f, 1.0f
                ),
                Is.True
            );
            Assert.That(
                Ppv2ExposureReader.ShouldScheduleReadback(
                    true, 101, 100, 1.2f, 1.1f
                ),
                Is.False
            );
            Assert.That(
                Ppv2ExposureReader.ShouldScheduleReadback(
                    false, 101, 100, 1.05f, 1.1f
                ),
                Is.False
            );
        }

        [Test]
        public void NvidiaManagedSurfaceBindsWithoutCreatingNativeFeature()
        {
            var api = new NvidiaDlaaApi();

            bool bound = api.TryBindManagedSurface(out string reason);

            Assert.That(bound, Is.True, reason);
            Assert.That(api.ContextCreated, Is.False);
        }

        [Test]
        public void DlaaOutputDescriptorIsLinearRandomWriteResource()
        {
            var source = new RenderTextureDescriptor(1920, 1080)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB,
                depthBufferBits = 24,
                msaaSamples = 4,
                useMipMap = true,
                autoGenerateMips = true,
                useDynamicScale = true
            };

            RenderTextureDescriptor output =
                NvidiaDlaaBackend.BuildOutputDescriptor(source);

            Assert.That(output.graphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_UNorm));
            Assert.That(output.enableRandomWrite, Is.True);
            Assert.That(output.depthBufferBits, Is.EqualTo(0));
            Assert.That(output.msaaSamples, Is.EqualTo(1));
            Assert.That(output.useMipMap, Is.False);
            Assert.That(output.autoGenerateMips, Is.False);
            Assert.That(output.useDynamicScale, Is.False);
        }

        [Test]
        public void Fsr2ConfigClampsUnsafeValues()
        {
            var config = new Fsr2Config(
                -2.0f,
                100,
                true,
                5.0f,
                0.0f,
                true,
                true,
                false
            );

            Assert.That(config.JitterSpread, Is.EqualTo(0.1f));
            Assert.That(config.SequenceLength, Is.EqualTo(32));
            Assert.That(config.EnableSharpening, Is.True);
            Assert.That(config.Sharpness, Is.EqualTo(1.0f));
            Assert.That(config.PreExposure, Is.EqualTo(0.01f));
            Assert.That(config.AutoExposure, Is.True);
            Assert.That(config.PreferPpv2Exposure, Is.True);
            Assert.That(config.InvertMotionX, Is.True);
            Assert.That(config.InvertMotionY, Is.False);
        }

        [Test]
        public void Fsr2ContextRecreationOnlyTracksInitializationFlags()
        {
            Fsr2Config original = Fsr2Config.Conservative;
            var dynamicOnly = new Fsr2Config(
                1.0f,
                16,
                true,
                0.5f,
                2.0f,
                original.AutoExposure,
                true,
                true
            );
            var immutableChange = new Fsr2Config(
                original.JitterSpread,
                original.SequenceLength,
                original.EnableSharpening,
                original.Sharpness,
                original.PreExposure,
                false,
                original.InvertMotionX,
                original.InvertMotionY
            );

            Assert.That(original.RequiresContextRecreation(in dynamicOnly), Is.False);
            Assert.That(original.RequiresContextRecreation(in immutableChange), Is.True);
        }

        [Test]
        public void AmdManagedSurfaceBindsWithoutCreatingNativeFeature()
        {
            var api = new AmdFsr2Api();

            bool bound = api.TryBindManagedSurface(out string reason);

            Assert.That(bound, Is.True, reason);
            Assert.That(api.ContextCreated, Is.False);
        }

        [Test]
        public void Fsr2DispatchJitterNegatesPpv2ProjectionSample()
        {
            var projectionJitter = new Vector2(0.375f, -0.625f);

            Vector2 dispatchJitter =
                AmdFsr2Api.ToDispatchJitter(projectionJitter);

            Assert.That(dispatchJitter.x, Is.EqualTo(-0.375f));
            Assert.That(dispatchJitter.y, Is.EqualTo(0.625f));
        }

        [Test]
        public void Fsr2OutputDescriptorIsLinearRandomWriteResource()
        {
            var source = new RenderTextureDescriptor(1920, 1080)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_SRGB,
                depthBufferBits = 24,
                msaaSamples = 4,
                useMipMap = true,
                autoGenerateMips = true,
                useDynamicScale = true
            };

            RenderTextureDescriptor output =
                AmdFsr2Backend.BuildOutputDescriptor(source);

            Assert.That(output.graphicsFormat, Is.EqualTo(GraphicsFormat.R8G8B8A8_UNorm));
            Assert.That(output.enableRandomWrite, Is.True);
            Assert.That(output.depthBufferBits, Is.EqualTo(0));
            Assert.That(output.msaaSamples, Is.EqualTo(1));
            Assert.That(output.useMipMap, Is.False);
            Assert.That(output.autoGenerateMips, Is.False);
            Assert.That(output.useDynamicScale, Is.False);
        }

        [Test]
        public void PhysicsRenderInterpolationIsOptInAndRestoresOriginalMode()
        {
            var gameObject = new GameObject("PhysicsRenderInterpolationTest");
            var interpolation = new KspPhysicsRenderInterpolation();
            try
            {
                Rigidbody body = gameObject.AddComponent<Rigidbody>();
                body.interpolation = RigidbodyInterpolation.None;

                Assert.That(interpolation.Apply(body), Is.False);
                Assert.That(body.interpolation, Is.EqualTo(RigidbodyInterpolation.None));

                Assert.That(interpolation.SetEnabled(true), Is.True);
                Assert.That(interpolation.Apply(body), Is.True);
                Assert.That(
                    body.interpolation,
                    Is.EqualTo(RigidbodyInterpolation.Interpolate)
                );

                Assert.That(interpolation.SetEnabled(false), Is.True);
                Assert.That(body.interpolation, Is.EqualTo(RigidbodyInterpolation.None));

                interpolation.SetEnabled(true);
                Assert.That(interpolation.Apply(body), Is.True);
                interpolation.Dispose();
                Assert.That(body.interpolation, Is.EqualTo(RigidbodyInterpolation.None));
            }
            finally
            {
                interpolation.Dispose();
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void PhysicsRenderInterpolationDoesNotOverrideExistingMode()
        {
            var gameObject = new GameObject("PhysicsRenderInterpolationExistingModeTest");
            var interpolation = new KspPhysicsRenderInterpolation();
            try
            {
                Rigidbody body = gameObject.AddComponent<Rigidbody>();
                body.interpolation = RigidbodyInterpolation.Extrapolate;
                interpolation.SetEnabled(true);

                Assert.That(interpolation.Apply(body), Is.False);
                Assert.That(
                    body.interpolation,
                    Is.EqualTo(RigidbodyInterpolation.Extrapolate)
                );

                interpolation.Dispose();
                Assert.That(
                    body.interpolation,
                    Is.EqualTo(RigidbodyInterpolation.Extrapolate)
                );
            }
            finally
            {
                interpolation.Dispose();
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void PerformanceProfileStopsWhenRequestedBackendFallsBack()
        {
            var profiler = new BackendPerformanceProfiler();
            profiler.Start(BackendSelection.AmdFsr2);

            profiler.Tick(
                BackendSelection.AmdFsr2,
                BackendSelection.CustomTaa
            );

            PerformanceProfileSnapshot snapshot = profiler.GetSnapshot(
                BackendSelection.AmdFsr2
            );
            Assert.That(
                snapshot.State,
                Is.EqualTo(PerformanceProfileState.BackendUnavailable)
            );
            Assert.That(snapshot.Samples, Is.EqualTo(0));
        }

        [Test]
        public void PerformanceProfileCanBeCancelledDuringWarmup()
        {
            var profiler = new BackendPerformanceProfiler();
            profiler.Start(BackendSelection.Off);

            profiler.Cancel();

            PerformanceProfileSnapshot snapshot = profiler.GetSnapshot(
                BackendSelection.Off
            );
            Assert.That(
                snapshot.State,
                Is.EqualTo(PerformanceProfileState.Cancelled)
            );
            Assert.That(snapshot.Running, Is.False);
        }

        [Test]
        public void HistoryTrackerReportsFirstFrameProjectionAndResolutionChanges()
        {
            var gameObject = new GameObject("HistoryResetTrackerTestCamera");
            var firstTarget = new RenderTexture(64, 64, 0);
            var secondTarget = new RenderTexture(32, 64, 0);
            try
            {
                Camera camera = gameObject.AddComponent<Camera>();
                camera.targetTexture = firstTarget;
                var tracker = new HistoryResetTracker();

                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.FirstFrame)
                );
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None)
                );

                camera.fieldOfView += 1.0f;
                HistoryResetReason projectionReasons = tracker.Evaluate(camera, 100);
                Assert.That(
                    (projectionReasons & HistoryResetReason.ProjectionChanged) != 0,
                    Is.True
                );

                camera.targetTexture = secondTarget;
                HistoryResetReason outputReasons = tracker.Evaluate(camera, 200);
                Assert.That(
                    (outputReasons & HistoryResetReason.ResolutionChanged) != 0,
                    Is.True
                );
                Assert.That(
                    (outputReasons & HistoryResetReason.RenderScaleChanged) != 0,
                    Is.True
                );
            }
            finally
            {
                Camera camera = gameObject.GetComponent<Camera>();
                if (camera != null)
                {
                    camera.targetTexture = null;
                }
                Object.DestroyImmediate(firstTarget);
                Object.DestroyImmediate(secondTarget);
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HistoryTrackerDoesNotTreatFastContinuousRotationAsCameraCut()
        {
            var gameObject = new GameObject("HistoryRotationTestCamera");
            try
            {
                Camera camera = gameObject.AddComponent<Camera>();
                var tracker = new HistoryResetTracker();
                tracker.Evaluate(camera, 100);

                camera.transform.rotation = Quaternion.Euler(0.0f, 90.0f, 0.0f);

                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None)
                );
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void HistoryTrackerCanSuppressOriginRebaseTranslation()
        {
            var gameObject = new GameObject("HistoryOriginTestCamera");
            try
            {
                Camera camera = gameObject.AddComponent<Camera>();
                var tracker = new HistoryResetTracker();
                tracker.Evaluate(camera, 100);

                camera.transform.position = new Vector3(1500.0f, 0.0f, 0.0f);

                Assert.That(
                    tracker.Evaluate(camera, 100, true),
                    Is.EqualTo(HistoryResetReason.None)
                );
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None)
                );
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

    }
}
