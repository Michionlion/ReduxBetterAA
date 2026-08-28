using System.Reflection;
using NUnit.Framework;
using ReduxBetterAA.Backends;
using ReduxBetterAA.Backends.Amd;
using ReduxBetterAA.Backends.Nvidia;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Diagnostics;
using ReduxBetterAA.Patches;
using ReduxBetterAA.Rendering;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.PostProcessing;

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
                new[] { "Off", "FXAA Low", "FXAA High", "SMAA", "TAA" },
                UserSettingsPolicy.BuildModeChoices(false, false)
            );
            CollectionAssert.AreEqual(
                new[]
                {
                    "Off", "FXAA Low", "FXAA High", "SMAA", "TAA",
                    "NVIDIA DLAA", "FSR 2 Native AA"
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
                Is.EqualTo(BackendSelection.FxaaHigh)
            );
            Assert.That(
                UserSettingsPolicy.NextBackend(
                    BackendSelection.FxaaHigh,
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
            Assert.That(config.Sharpness, Is.EqualTo(1.0f));
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
            Assert.That(
                TemporalBackendConfig.ConservativePpv2.Sharpness,
                Is.EqualTo(0.15f)
            );
            Assert.That(
                CustomTaaConfig.Conservative.Sharpening,
                Is.EqualTo(0.15f)
            );
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
            Assert.That(Fsr2Config.Conservative.EnableSharpening, Is.True);
            Assert.That(Fsr2Config.Conservative.Sharpness, Is.EqualTo(0.15f));
        }

        [Test]
        public void UserSettingMigrationNormalizesLegacyAndUnsupportedValues()
        {
            Assert.That(
                UserSettingsPolicy.NormalizeMode("PPv2 TAA", false, false),
                Is.EqualTo(UserSettingsPolicy.ModeTaa)
            );
            Assert.That(
                UserSettingsPolicy.NormalizeMode("Custom TAA", false, false),
                Is.EqualTo(UserSettingsPolicy.ModeTaa)
            );
            Assert.That(
                UserSettingsPolicy.NormalizeMode("FSR 2", false, true),
                Is.EqualTo(UserSettingsPolicy.ModeFsr2)
            );
            Assert.That(
                UserSettingsPolicy.ParseBackend(
                    UserSettingsPolicy.ModeDlaa,
                    false,
                    true
                ),
                Is.EqualTo(BackendSelection.Off)
            );
            Assert.That(
                UserSettingsPolicy.NormalizeDlaaPreset("Default"),
                Is.EqualTo("K")
            );
        }

        [Test]
        public void OutputOnlySettingsDoNotDiscardTemporalHistory()
        {
            CustomTaaConfig custom = CustomTaaConfig.Conservative;
            CustomTaaConfig customSharpened = custom.WithUserSettings(
                custom.StationaryHistory,
                0.7f
            );
            CustomTaaConfig customTemporal = custom.WithUserSettings(
                custom.StationaryHistory - 0.1f,
                custom.Sharpening
            );
            var customDebugView = new CustomTaaConfig(
                custom.JitterSpread,
                custom.SequenceLength,
                custom.StationaryHistory,
                custom.MovingHistory,
                custom.MotionResponsePixels,
                custom.MaximumMotionPixels,
                custom.DepthThreshold,
                custom.DepthEdgeStability,
                custom.VarianceGamma,
                custom.ReactiveScale,
                custom.Sharpening,
                custom.NoDepthHistory,
                CustomTaaDebugView.HistoryColor
            );
            Assert.That(
                custom.RequiresHistoryReset(in customSharpened),
                Is.False
            );
            Assert.That(
                custom.RequiresHistoryReset(in customTemporal),
                Is.True
            );
            Assert.That(
                custom.RequiresHistoryReset(in customDebugView),
                Is.False
            );

            DlaaConfig dlaa = DlaaConfig.Conservative;
            DlaaConfig dlaaSharpened = dlaa.WithUserSettings(
                0.7f,
                dlaa.PreExposure,
                dlaa.AutoExposure,
                dlaa.Preset,
                dlaa.AllowSupersampling
            );
            Assert.That(
                dlaa.RequiresHistoryReset(in dlaaSharpened),
                Is.False
            );

            Fsr2Config fsr2 = Fsr2Config.Conservative;
            Fsr2Config fsr2Unsharpened = fsr2.WithUserSettings(
                0.0f,
                fsr2.PreExposure,
                fsr2.AutoExposure
            );
            Assert.That(
                fsr2.RequiresHistoryReset(in fsr2Unsharpened),
                Is.False
            );
            Assert.That(fsr2Unsharpened.EnableSharpening, Is.False);
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
        public void VegetationRepairIsDefaultAndExactSignatureIsAvailable()
        {
            Assert.That(VegetationMotionCompatibility.DefaultEnabled, Is.True);
            Assert.That(MotionVectorSanitizer.DefaultEnabled, Is.False);

            MethodInfo target;
            string reason;
            Assert.That(
                VegetationMotionCompatibilityPatch.TryResolveTarget(
                    out target,
                    out reason),
                Is.True,
                reason
            );
            Assert.That(target, Is.Not.Null);
            Assert.That(target.ReturnType, Is.EqualTo(typeof(void)));
            Assert.That(target.GetParameters(), Has.Length.EqualTo(9));
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

        [TestCase((int)TemporalSceneKind.Flight, true)]
        [TestCase((int)TemporalSceneKind.KerbalSpaceCenter, true)]
        [TestCase((int)TemporalSceneKind.Vab, true)]
        [TestCase((int)TemporalSceneKind.Map, false)]
        [TestCase((int)TemporalSceneKind.MainMenu, false)]
        [TestCase((int)TemporalSceneKind.Unsupported, false)]
        public void ProjectionJitterPolicyMatchesObservedSceneRenderPaths(
            int sceneKindValue,
            bool expected)
        {
            var cameras = new TemporalCameraSet
            {
                SceneKind = (TemporalSceneKind)sceneKindValue
            };

            Assert.That(cameras.ProjectionJitterSupported, Is.EqualTo(expected));
        }

        [Test]
        public void MapViewAaCanBeDisabledWithoutChangingTheRequestedMode()
        {
            Assert.That(
                TemporalCoordinator.EffectiveBackendForScene(
                    BackendSelection.NvidiaDlaa,
                    TemporalSceneKind.Map,
                    false
                ),
                Is.EqualTo(BackendSelection.Off)
            );
            Assert.That(
                TemporalCoordinator.EffectiveBackendForScene(
                    BackendSelection.NvidiaDlaa,
                    TemporalSceneKind.Map,
                    true
                ),
                Is.EqualTo(BackendSelection.NvidiaDlaa)
            );
            Assert.That(
                TemporalCoordinator.EffectiveBackendForScene(
                    BackendSelection.NvidiaDlaa,
                    TemporalSceneKind.Flight,
                    false
                ),
                Is.EqualTo(BackendSelection.NvidiaDlaa)
            );
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
                0.5f,
                2.0f,
                original.AutoExposure,
                true,
                true
            );
            var immutableChange = new Fsr2Config(
                original.JitterSpread,
                original.SequenceLength,
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
        public void MotionSignReferenceUsesActualRenderTexturePath()
        {
            Assert.That(
                MotionSignDiagnosticPolicy.UseRenderTextureProjection(false, false),
                Is.False
            );
            Assert.That(
                MotionSignDiagnosticPolicy.UseRenderTextureProjection(true, false),
                Is.True
            );
            Assert.That(
                MotionSignDiagnosticPolicy.UseRenderTextureProjection(false, true),
                Is.True
            );
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
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None),
                    "ordinary animated zoom must preserve accumulation"
                );

                camera.fieldOfView += 10.0f;
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

        [Test]
        public void HistoryTrackerReportsOneResetForContinuousLargeTranslation()
        {
            var gameObject = new GameObject("HistoryTranslationTestCamera");
            try
            {
                Camera camera = gameObject.AddComponent<Camera>();
                var tracker = new HistoryResetTracker();
                tracker.Evaluate(camera, 100);

                camera.transform.position = new Vector3(1500.0f, 0.0f, 0.0f);
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.Teleport)
                );

                camera.transform.position = new Vector3(3000.0f, 0.0f, 0.0f);
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None),
                    "continuous orbital-camera translation must not clear every frame"
                );

                camera.transform.position = new Vector3(3010.0f, 0.0f, 0.0f);
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.None)
                );

                camera.transform.position = new Vector3(4510.0f, 0.0f, 0.0f);
                Assert.That(
                    tracker.Evaluate(camera, 100),
                    Is.EqualTo(HistoryResetReason.Teleport),
                    "a new discontinuity after stable motion must still reset"
                );
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Ppv2ModeOwnersRestoreTheExactPreModLayerState()
        {
            var resolveObject = new GameObject("AaOwnerResolveCamera");
            var sharedObject = new GameObject("AaOwnerSharedCamera");
            try
            {
                Camera resolveCamera = resolveObject.AddComponent<Camera>();
                Camera sharedCamera = sharedObject.AddComponent<Camera>();
                PostProcessLayer resolveLayer =
                    resolveObject.AddComponent<PostProcessLayer>();
                PostProcessLayer sharedLayer =
                    sharedObject.AddComponent<PostProcessLayer>();
                resolveLayer.antialiasingMode =
                    PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                sharedLayer.antialiasingMode =
                    PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing;
                resolveLayer.fastApproximateAntialiasing = null;
                resolveLayer.subpixelMorphologicalAntialiasing = null;

                var cameras = new TemporalCameraSet
                {
                    SceneKind = TemporalSceneKind.Flight,
                    ResolveCamera = resolveCamera,
                    ResolveLayer = resolveLayer,
                    SharedJitterCamera = sharedCamera,
                    SharedJitterLayer = sharedLayer,
                    RenderScalePercent = 100
                };

                var disabled = new DisabledBackend();
                string failure;
                Assert.That(disabled.Configure(cameras, out failure), Is.True);
                Assert.That(
                    resolveLayer.antialiasingMode,
                    Is.EqualTo(PostProcessLayer.Antialiasing.None)
                );
                Assert.That(
                    sharedLayer.antialiasingMode,
                    Is.EqualTo(PostProcessLayer.Antialiasing.None)
                );
                disabled.Deactivate();
                Assert.That(
                    resolveLayer.antialiasingMode,
                    Is.EqualTo(
                        PostProcessLayer.Antialiasing.FastApproximateAntialiasing
                    )
                );
                Assert.That(
                    sharedLayer.antialiasingMode,
                    Is.EqualTo(
                        PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing
                    )
                );

                var spatial = new Ppv2SpatialAaBackend(
                    "FXAA High",
                    PostProcessLayer.Antialiasing.FastApproximateAntialiasing,
                    false
                );
                Assert.That(spatial.Configure(cameras, out failure), Is.True);
                Assert.That(resolveLayer.fastApproximateAntialiasing, Is.Not.Null);
                Assert.That(resolveLayer.subpixelMorphologicalAntialiasing, Is.Not.Null);
                spatial.Deactivate();
                Assert.That(resolveLayer.fastApproximateAntialiasing, Is.Null);
                Assert.That(resolveLayer.subpixelMorphologicalAntialiasing, Is.Null);
                Assert.That(
                    resolveLayer.antialiasingMode,
                    Is.EqualTo(
                        PostProcessLayer.Antialiasing.FastApproximateAntialiasing
                    )
                );
                Assert.That(
                    sharedLayer.antialiasingMode,
                    Is.EqualTo(
                        PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing
                    )
                );
            }
            finally
            {
                Object.DestroyImmediate(resolveObject);
                Object.DestroyImmediate(sharedObject);
            }
        }

        [Test]
        public void GraphicsSettingsPatchUsesHarmonyPositionalArgument()
        {
            MethodInfo prefix = typeof(StockGraphicsAntialiasingApplyPatch)
                .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(prefix, Is.Not.Null);
            ParameterInfo[] parameters = prefix.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].Name, Is.EqualTo("__0"));
            Assert.That(
                parameters[0].ParameterType,
                Is.EqualTo(typeof(int).MakeByRefType())
            );
        }

        [Test]
        public void BetterAaBundleIdentityIncludesEntryGuids()
        {
            const string SchemaPath =
                "Assets/AddressableAssetsData/AssetGroups/Schemas/" +
                "addressables_ReduxBetterAA_all_BundledAssetGroupSchema.asset";
            BundledAssetGroupSchema schema =
                AssetDatabase.LoadAssetAtPath<BundledAssetGroupSchema>(SchemaPath);

            Assert.That(schema, Is.Not.Null);
            Assert.That(
                schema.InternalBundleIdMode,
                Is.EqualTo(
                    BundledAssetGroupSchema.BundleInternalIdMode
                        .GroupGuidProjectIdEntriesHash
                )
            );
        }

    }
}
