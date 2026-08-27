using System.Reflection;
using KSP.Game;
using KSP.Map;
using KSP.OAB;
using KSP.Rendering;
using KSP.Rendering.impl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace ReduxBetterAA.Rendering
{
    internal enum TemporalSceneKind
    {
        Unsupported = 0,
        Flight = 1,
        Map = 2,
        Vab = 3,
        KerbalSpaceCenter = 4,
        MainMenu = 5
    }

    internal sealed class TemporalCameraSet
    {
        public TemporalSceneKind SceneKind;
        public Camera ResolveCamera;
        public PostProcessLayer ResolveLayer;
        public Camera SharedJitterCamera;
        public PostProcessLayer SharedJitterLayer;
        public int RenderScalePercent;
    }

    internal static class TemporalCameraDiscovery
    {
        private const BindingFlags InstancePrivate =
            BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo PresenterScale =
            typeof(RenderScalePresenter).GetField("_renderScalePercent", InstancePrivate);

        public static TemporalCameraSet Discover()
        {
            string state = ReadGameState();
            if (state == "FlightView")
            {
                return DiscoverFlight(TemporalSceneKind.Flight);
            }
            if (state == "KerbalSpaceCenter")
            {
                // KSC reuses the live scaled/physics flight stacks. The prior
                // state gate discarded them even though their color, depth, and
                // motion buffers remain valid.
                return DiscoverFlight(TemporalSceneKind.KerbalSpaceCenter);
            }
            if (state == "Map3DView")
            {
                return DiscoverMap();
            }
            if (state == "VehicleAssemblyBuilder")
            {
                return DiscoverVab();
            }
            if (state == "MainMenu")
            {
                return DiscoverMainMenu();
            }

            return new TemporalCameraSet
            {
                SceneKind = TemporalSceneKind.Unsupported,
                RenderScalePercent = ReadRenderScalePercent()
            };
        }

        private static TemporalCameraSet DiscoverFlight(TemporalSceneKind sceneKind)
        {
            Camera resolveCamera = null;
            PostProcessLayer resolveLayer = null;
            FlightCameraRenderStack_Physics[] physicsStacks =
                Resources.FindObjectsOfTypeAll<FlightCameraRenderStack_Physics>();
            for (int index = 0; index < physicsStacks.Length; index++)
            {
                FlightCameraRenderStack_Physics stack = physicsStacks[index];
                if (!IsUsable(stack))
                {
                    continue;
                }

                Camera camera = stack.GetMainRenderCamera();
                if (!IsUsable(camera))
                {
                    continue;
                }
                resolveCamera = camera;
                resolveLayer = stack.GetPostProcessLayer();
                break;
            }

            Camera sharedJitterCamera = null;
            PostProcessLayer sharedJitterLayer = null;
            FlightCameraRenderStack_Scaled[] scaledStacks =
                Resources.FindObjectsOfTypeAll<FlightCameraRenderStack_Scaled>();
            for (int index = 0; index < scaledStacks.Length; index++)
            {
                FlightCameraRenderStack_Scaled stack = scaledStacks[index];
                if (!IsUsable(stack))
                {
                    continue;
                }

                Camera camera = stack.GetMainRenderCamera();
                if (!IsUsable(camera))
                {
                    continue;
                }
                sharedJitterCamera = camera;
                sharedJitterLayer = stack.GetPostProcessLayer();
                break;
            }

            return new TemporalCameraSet
            {
                SceneKind = sceneKind,
                ResolveCamera = resolveCamera,
                ResolveLayer = resolveLayer,
                SharedJitterCamera = sharedJitterCamera,
                SharedJitterLayer = sharedJitterLayer,
                RenderScalePercent = ReadRenderScalePercent()
            };
        }

        private static TemporalCameraSet DiscoverMap()
        {
            Camera camera = null;
            MapCamera[] mapCameras = Resources.FindObjectsOfTypeAll<MapCamera>();
            for (int index = 0; index < mapCameras.Length; index++)
            {
                MapCamera mapCamera = mapCameras[index];
                if (!IsUsable(mapCamera) || !IsUsable(mapCamera.UnityCamera))
                {
                    continue;
                }
                camera = mapCamera.UnityCamera;
                break;
            }

            return new TemporalCameraSet
            {
                SceneKind = TemporalSceneKind.Map,
                ResolveCamera = camera,
                ResolveLayer = camera == null ? null : camera.GetComponent<PostProcessLayer>(),
                RenderScalePercent = ReadRenderScalePercent()
            };
        }

        private static TemporalCameraSet DiscoverVab()
        {
            Camera camera = null;
            ObjectAssemblyCameraManager[] managers =
                Resources.FindObjectsOfTypeAll<ObjectAssemblyCameraManager>();
            for (int index = 0; index < managers.Length; index++)
            {
                ObjectAssemblyCameraManager manager = managers[index];
                if (!IsUsable(manager) || !IsUsable(manager.Camera))
                {
                    continue;
                }
                camera = manager.Camera;
                break;
            }

            return new TemporalCameraSet
            {
                SceneKind = TemporalSceneKind.Vab,
                ResolveCamera = camera,
                ResolveLayer = camera == null ? null : camera.GetComponent<PostProcessLayer>(),
                RenderScalePercent = ReadRenderScalePercent()
            };
        }

        private static TemporalCameraSet DiscoverMainMenu()
        {
            Camera camera = null;
            int bestScore = int.MinValue;
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera candidate = cameras[index];
                int score = ScoreMainMenuCamera(candidate);
                if (score > bestScore)
                {
                    camera = candidate;
                    bestScore = score;
                }
            }

            Camera backgroundCamera = null;
            bestScore = int.MinValue;
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera candidate = cameras[index];
                int score = ScoreMainMenuBackgroundCamera(candidate, camera);
                if (score > bestScore)
                {
                    backgroundCamera = candidate;
                    bestScore = score;
                }
            }

            return new TemporalCameraSet
            {
                SceneKind = TemporalSceneKind.MainMenu,
                ResolveCamera = camera,
                ResolveLayer = camera == null
                    ? null
                    : camera.GetComponent<PostProcessLayer>(),
                SharedJitterCamera = backgroundCamera,
                SharedJitterLayer = backgroundCamera == null
                    ? null
                    : backgroundCamera.GetComponent<PostProcessLayer>(),
                RenderScalePercent = ReadRenderScalePercent()
            };
        }

        internal static int ScoreMainMenuBackgroundCamera(
            Camera camera,
            Camera resolveCamera)
        {
            if (!IsUsable(camera) || !IsUsable(resolveCamera) ||
                camera == resolveCamera || camera.cameraType != CameraType.Game)
            {
                return int.MinValue;
            }

            string name = camera.name ?? string.Empty;
            int score;
            if (string.Equals(
                    name,
                    "Skybox",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                score = 1000;
            }
            else if (name.IndexOf(
                         "Skybox",
                         System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score = 500;
            }
            else
            {
                return int.MinValue;
            }

            // The menu background must contribute before Camera.Scaled and to
            // the same output. Camera.Scaled clears depth but preserves this
            // camera's color, so both projections need the same pixel jitter.
            if (camera.depth >= resolveCamera.depth ||
                camera.targetTexture != resolveCamera.targetTexture)
            {
                return int.MinValue;
            }
            if (camera.pixelWidth == resolveCamera.pixelWidth &&
                camera.pixelHeight == resolveCamera.pixelHeight)
            {
                score += 100;
            }
            if (camera.pixelRect == resolveCamera.pixelRect)
            {
                score += 50;
            }
            if (camera.clearFlags == CameraClearFlags.Skybox)
            {
                score += 25;
            }
            int skyboxLayer = LayerMask.NameToLayer("Render.Skybox");
            if (skyboxLayer >= 0 &&
                (camera.cullingMask & (1 << skyboxLayer)) != 0)
            {
                score += 25;
            }
            return score;
        }

        internal static int ScoreMainMenuCamera(Camera camera)
        {
            if (!IsUsable(camera) || camera.cameraType != CameraType.Game)
            {
                return int.MinValue;
            }

            string name = camera.name ?? string.Empty;
            if (name.IndexOf("Flow", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("UI", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Overlay", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Skybox", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("SkySphere", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Flare", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("DEBUG", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Render Scale Present", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return int.MinValue;
            }

            int score = 0;
            if (string.Equals(
                    name,
                    "Camera.Scaled",
                    System.StringComparison.OrdinalIgnoreCase))
            {
                score += 1000;
            }
            else if (name.IndexOf(
                         "Scaled",
                         System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 500;
            }
            if (camera.GetComponent<PostProcessLayer>() != null)
            {
                score += 200;
            }
            if (camera.targetTexture == null)
            {
                score += 100;
            }
            if (camera.pixelWidth == Screen.width &&
                camera.pixelHeight == Screen.height)
            {
                score += 50;
            }
            return score;
        }

        private static int ReadRenderScalePercent()
        {
            RenderScalePresenter[] presenters =
                Resources.FindObjectsOfTypeAll<RenderScalePresenter>();
            for (int index = 0; index < presenters.Length; index++)
            {
                RenderScalePresenter presenter = presenters[index];
                if (IsUsable(presenter))
                {
                    object value = PresenterScale?.GetValue(presenter);
                    return value is int percent ? percent : 100;
                }
            }
            return 100;
        }

        private static bool IsUsable(Component component)
        {
            return component != null &&
                   component.gameObject.scene.IsValid() &&
                   component.gameObject.activeInHierarchy;
        }

        private static bool IsUsable(Camera camera)
        {
            return camera != null &&
                   camera.gameObject.scene.IsValid() &&
                   camera.isActiveAndEnabled;
        }

        private static string ReadGameState()
        {
            GameManager manager = GameManager.Instance;
            if (manager == null || manager.Game == null ||
                manager.Game.GlobalGameState == null)
            {
                return GameState.Invalid.ToString();
            }
            return manager.Game.GlobalGameState.GetGameState().GameState.ToString();
        }
    }
}
