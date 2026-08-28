using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.Game;
using KSP.Map;
using KSP.OAB;
using KSP.Rendering;
using KSP.Rendering.impl;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace ReduxBetterAA.Rendering
{
    internal sealed class CameraDiscoveryResult
    {
        public CameraGraph Graph;
        public Camera[] DebugCandidates;
    }

    internal static class CameraDiscovery
    {
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly CameraEvent[] CameraEvents =
            (CameraEvent[])Enum.GetValues(typeof(CameraEvent));

        private static readonly Comparison<Camera> CameraOrder = CompareCameras;
        private static readonly FieldInfo PresenterSources =
            typeof(RenderScalePresenter).GetField("_sourceCameras", InstancePrivate);
        private static readonly FieldInfo PresenterCamera =
            typeof(RenderScalePresenter).GetField("_presentCamera", InstancePrivate);
        private static readonly FieldInfo PresenterBuffer =
            typeof(RenderScalePresenter).GetField("_presentBuffer", InstancePrivate);
        private static readonly FieldInfo PresenterTarget =
            typeof(RenderScalePresenter).GetField("_renderTarget", InstancePrivate);
        private static readonly FieldInfo PresenterScale =
            typeof(RenderScalePresenter).GetField("_renderScalePercent", InstancePrivate);
        private static readonly FieldInfo PresenterEnabled =
            typeof(RenderScalePresenter).GetField("_renderingEnabled", InstancePrivate);
        private static readonly FieldInfo PresenterOriginalTargets =
            typeof(RenderScalePresenter).GetField("_originalTargets", InstancePrivate);
        private static readonly FieldInfo ActiveCameraGroup =
            typeof(PostProcessingSystem).GetField("_activeCameraGroup", InstancePrivate);

        public static CameraDiscoveryResult Capture(int revision)
        {
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            Array.Sort(cameras, CameraOrder);

            FlightCameraRenderStack_Scaled[] scaledStacks =
                Resources.FindObjectsOfTypeAll<FlightCameraRenderStack_Scaled>();
            FlightCameraRenderStack_Physics[] physicsStacks =
                Resources.FindObjectsOfTypeAll<FlightCameraRenderStack_Physics>();
            RenderScalePresenter[] presenters =
                Resources.FindObjectsOfTypeAll<RenderScalePresenter>();
            MapCamera[] mapCameras = Resources.FindObjectsOfTypeAll<MapCamera>();
            ObjectAssemblyCameraManager[] oabManagers =
                Resources.FindObjectsOfTypeAll<ObjectAssemblyCameraManager>();

            var scaledCameraIds = CollectStackCameraIds(scaledStacks);
            var physicsCameraIds = CollectStackCameraIds(physicsStacks);
            var mapCameraIds = CollectMapCameraIds(mapCameras);
            var oabCameraIds = CollectOabCameraIds(oabManagers);
            var presenterSources = new Dictionary<ulong, ulong>();
            var presentationCameraIds = new HashSet<ulong>();
            PresenterRecord[] presenterRecords = BuildPresenters(
                presenters,
                presenterSources,
                presentationCameraIds
            );

            var cameraRecords = new CameraRecord[cameras.Length];
            var candidates = new List<Camera>(cameras.Length);
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera camera = cameras[index];
                cameraRecords[index] = BuildCameraRecord(
                    camera,
                    scaledCameraIds,
                    physicsCameraIds,
                    mapCameraIds,
                    oabCameraIds,
                    presenterSources,
                    presentationCameraIds
                );

                if (IsDebugCandidate(camera))
                {
                    candidates.Add(camera);
                }
            }

            CameraStackRecord[] stackRecords = BuildStacks(scaledStacks, physicsStacks);
            var graph = new CameraGraph
            {
                revision = revision,
                activeScene = SceneManager.GetActiveScene().name,
                gameState = ReadGameState(),
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                activeCameraGroup = ReadActiveCameraGroup(),
                cameras = cameraRecords,
                stacks = stackRecords,
                presenters = presenterRecords
            };

            return new CameraDiscoveryResult
            {
                Graph = graph,
                DebugCandidates = candidates.ToArray()
            };
        }

        public static Camera[] CaptureDebugCandidates()
        {
            Camera[] cameras = Resources.FindObjectsOfTypeAll<Camera>();
            Array.Sort(cameras, CameraOrder);
            var candidates = new List<Camera>(cameras.Length);
            for (int index = 0; index < cameras.Length; index++)
            {
                Camera camera = cameras[index];
                if (IsDebugCandidate(camera))
                {
                    candidates.Add(camera);
                }
            }
            return candidates.ToArray();
        }

        private static CameraRecord BuildCameraRecord(
            Camera camera,
            HashSet<ulong> scaledCameraIds,
            HashSet<ulong> physicsCameraIds,
            HashSet<ulong> mapCameraIds,
            HashSet<ulong> oabCameraIds,
            Dictionary<ulong, ulong> presenterSources,
            HashSet<ulong> presentationCameraIds)
        {
            ulong cameraId = EntityId.ToULong(camera.GetEntityId());
            PostProcessLayer postLayer = camera.GetComponent<PostProcessLayer>();
            ulong presentationCameraId;
            presenterSources.TryGetValue(cameraId, out presentationCameraId);

            return new CameraRecord
            {
                instanceId = cameraId,
                name = camera.name,
                hierarchy = GetHierarchy(camera.transform),
                scene = camera.gameObject.scene.name,
                role = GetRole(
                    camera,
                    cameraId,
                    scaledCameraIds,
                    physicsCameraIds,
                    mapCameraIds,
                    oabCameraIds,
                    presenterSources,
                    presentationCameraIds
                ),
                enabled = camera.enabled,
                activeInHierarchy = camera.gameObject.activeInHierarchy,
                cameraType = camera.cameraType.ToString(),
                depth = camera.depth,
                nearClipPlane = camera.nearClipPlane,
                farClipPlane = camera.farClipPlane,
                fieldOfView = camera.fieldOfView,
                orthographic = camera.orthographic,
                orthographicSize = camera.orthographicSize,
                clearFlags = camera.clearFlags.ToString(),
                backgroundColor = ColorUtility.ToHtmlStringRGBA(camera.backgroundColor),
                cullingMask = camera.cullingMask,
                cullingMaskHex = "0x" + unchecked((uint)camera.cullingMask).ToString("X8"),
                eventMask = camera.eventMask,
                renderingPath = camera.renderingPath.ToString(),
                actualRenderingPath = camera.actualRenderingPath.ToString(),
                depthTextureMode = camera.depthTextureMode.ToString(),
                allowHdr = camera.allowHDR,
                allowMsaa = camera.allowMSAA,
                allowDynamicResolution = camera.allowDynamicResolution,
                forceIntoRenderTexture = camera.forceIntoRenderTexture,
                useOcclusionCulling = camera.useOcclusionCulling,
                pixelRect = FormatRect(camera.pixelRect),
                pixelWidth = camera.pixelWidth,
                pixelHeight = camera.pixelHeight,
                targetTexture = DescribeTexture(camera.targetTexture),
                postProcessLayerPresent = postLayer != null,
                postProcessLayerEnabled = postLayer != null && postLayer.enabled,
                postProcessAntialiasing = postLayer == null
                    ? "Unavailable"
                    : postLayer.antialiasingMode.ToString(),
                postProcessCameraFlags = postLayer == null
                    ? "Unavailable"
                    : postLayer.cameraDepthFlags.ToString(),
                components = GetComponentNames(camera),
                commandBuffers = GetCommandBuffers(camera),
                presentationCameraId = presentationCameraId
            };
        }

        private static CameraStackRecord[] BuildStacks(
            FlightCameraRenderStack_Scaled[] scaledStacks,
            FlightCameraRenderStack_Physics[] physicsStacks)
        {
            var records = new CameraStackRecord[scaledStacks.Length + physicsStacks.Length];
            int index = 0;
            for (int i = 0; i < scaledStacks.Length; i++)
            {
                records[index++] = DescribeStack(scaledStacks[i]);
            }
            for (int i = 0; i < physicsStacks.Length; i++)
            {
                records[index++] = DescribeStack(physicsStacks[i]);
            }
            return records;
        }

        private static CameraStackRecord DescribeStack(ICameraRenderStack stack)
        {
            var component = stack as Component;
            Camera[] renderCameras = stack.GetRenderCameras(true) ?? Array.Empty<Camera>();
            PostProcessLayer postLayer = stack.GetPostProcessLayer();
            return new CameraStackRecord
            {
                instanceId = component == null
                    ? 0UL
                    : EntityId.ToULong(component.GetEntityId()),
                name = component == null ? stack.GetType().Name : component.name,
                type = stack.GetType().FullName,
                renderSpace = stack.RenderSpace.ToString(),
                active = component != null && component.gameObject.activeInHierarchy,
                mainCameraId = GetId(stack.GetMainRenderCamera()),
                cubemapCameraId = GetId(stack.GetCubemapRenderCamera()),
                debugCameraId = GetId(stack.GetDebugRenderCamera()),
                renderCameraIds = GetCameraIds(renderCameras),
                postProcessLayerId = GetId(postLayer),
                postProcessAntialiasing = postLayer == null
                    ? "Unavailable"
                    : postLayer.antialiasingMode.ToString()
            };
        }

        private static PresenterRecord[] BuildPresenters(
            RenderScalePresenter[] presenters,
            Dictionary<ulong, ulong> sourceAssociations,
            HashSet<ulong> presentationCameraIds)
        {
            var records = new PresenterRecord[presenters.Length];
            for (int index = 0; index < presenters.Length; index++)
            {
                RenderScalePresenter presenter = presenters[index];
                Camera[] sources = ReadField<Camera[]>(PresenterSources, presenter) ?? Array.Empty<Camera>();
                Camera presentCamera = ReadField<Camera>(PresenterCamera, presenter);
                RenderTexture target = ReadField<RenderTexture>(PresenterTarget, presenter);
                CommandBuffer buffer = ReadField<CommandBuffer>(PresenterBuffer, presenter);
                ulong presentCameraId = GetId(presentCamera);
                if (presentCameraId != 0)
                {
                    presentationCameraIds.Add(presentCameraId);
                }

                for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                {
                    Camera source = sources[sourceIndex];
                    if (source != null)
                    {
                        sourceAssociations[EntityId.ToULong(source.GetEntityId())] = presentCameraId;
                    }
                }

                var originals = ReadField<System.Collections.IDictionary>(
                    PresenterOriginalTargets,
                    presenter
                );
                records[index] = new PresenterRecord
                {
                    instanceId = EntityId.ToULong(presenter.GetEntityId()),
                    name = presenter.name,
                    active = presenter.gameObject.activeInHierarchy,
                    renderingEnabled = ReadField<bool>(PresenterEnabled, presenter),
                    renderScalePercent = ReadField<int>(PresenterScale, presenter),
                    sourceCameraIds = GetCameraIds(sources),
                    presentationCameraId = presentCameraId,
                    presentationEvent = CameraEvent.AfterEverything.ToString(),
                    presentationCommandBuffer = buffer == null ? string.Empty : buffer.name,
                    renderTarget = DescribeTexture(target),
                    savedOriginalTargetCount = originals == null ? 0 : originals.Count
                };
            }
            return records;
        }

        private static HashSet<ulong> CollectStackCameraIds<T>(T[] stacks)
            where T : UnityEngine.Object, ICameraRenderStack
        {
            var ids = new HashSet<ulong>();
            for (int i = 0; i < stacks.Length; i++)
            {
                Camera[] cameras = stacks[i].GetRenderCameras(true);
                if (cameras == null)
                {
                    continue;
                }
                for (int cameraIndex = 0; cameraIndex < cameras.Length; cameraIndex++)
                {
                    Camera camera = cameras[cameraIndex];
                    if (camera != null)
                    {
                        ids.Add(EntityId.ToULong(camera.GetEntityId()));
                    }
                }
            }
            return ids;
        }

        private static HashSet<ulong> CollectMapCameraIds(MapCamera[] mapCameras)
        {
            var ids = new HashSet<ulong>();
            for (int i = 0; i < mapCameras.Length; i++)
            {
                Camera camera = mapCameras[i].UnityCamera;
                if (camera != null)
                {
                    ids.Add(EntityId.ToULong(camera.GetEntityId()));
                }
            }
            return ids;
        }

        private static HashSet<ulong> CollectOabCameraIds(ObjectAssemblyCameraManager[] managers)
        {
            var ids = new HashSet<ulong>();
            for (int i = 0; i < managers.Length; i++)
            {
                Camera camera = managers[i].Camera;
                if (camera != null)
                {
                    ids.Add(EntityId.ToULong(camera.GetEntityId()));
                }
            }
            return ids;
        }

        private static string GetRole(
            Camera camera,
            ulong cameraId,
            HashSet<ulong> scaledCameraIds,
            HashSet<ulong> physicsCameraIds,
            HashSet<ulong> mapCameraIds,
            HashSet<ulong> oabCameraIds,
            Dictionary<ulong, ulong> presenterSources,
            HashSet<ulong> presentationCameraIds)
        {
            var roles = new List<string>(4);
            if (scaledCameraIds.Contains(cameraId)) roles.Add("ScaledSpaceStack");
            if (physicsCameraIds.Contains(cameraId)) roles.Add("PhysicsSpaceStack");
            if (mapCameraIds.Contains(cameraId)) roles.Add("Map");
            if (oabCameraIds.Contains(cameraId)) roles.Add("OAB");
            if (presenterSources.ContainsKey(cameraId)) roles.Add("PresentationSource");
            if (presentationCameraIds.Contains(cameraId)) roles.Add("Presentation");
            if (LooksLikeUiCamera(camera)) roles.Add("UIOrOverlayCandidate");
            if (roles.Count == 0) roles.Add("Other");
            return string.Join("|", roles);
        }

        private static bool LooksLikeUiCamera(Camera camera)
        {
            string name = camera.name;
            if (name.IndexOf("UI", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Overlay", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            return uiLayer >= 0 && (camera.cullingMask & (1 << uiLayer)) != 0;
        }

        private static bool IsDebugCandidate(Camera camera)
        {
            return camera != null &&
                   camera.cameraType == CameraType.Game &&
                   camera.gameObject.activeInHierarchy &&
                   camera.enabled;
        }

        private static CommandBufferRecord[] GetCommandBuffers(Camera camera)
        {
            var records = new List<CommandBufferRecord>();
            for (int eventIndex = 0; eventIndex < CameraEvents.Length; eventIndex++)
            {
                CameraEvent cameraEvent = CameraEvents[eventIndex];
                CommandBuffer[] buffers = camera.GetCommandBuffers(cameraEvent);
                if (buffers.Length == 0)
                {
                    continue;
                }

                var names = new string[buffers.Length];
                for (int bufferIndex = 0; bufferIndex < buffers.Length; bufferIndex++)
                {
                    names[bufferIndex] = buffers[bufferIndex].name;
                }
                records.Add(new CommandBufferRecord
                {
                    cameraEvent = cameraEvent.ToString(),
                    names = names
                });
            }
            return records.ToArray();
        }

        private static string[] GetComponentNames(Camera camera)
        {
            Component[] components = camera.GetComponents<Component>();
            var names = new string[components.Length];
            for (int index = 0; index < components.Length; index++)
            {
                Component component = components[index];
                names[index] = component == null ? "MissingScript" : component.GetType().FullName;
            }
            return names;
        }

        private static TextureRecord DescribeTexture(RenderTexture texture)
        {
            if (texture == null)
            {
                return new TextureRecord { present = false };
            }
            return new TextureRecord
            {
                present = true,
                instanceId = EntityId.ToULong(texture.GetEntityId()),
                name = texture.name,
                width = texture.width,
                height = texture.height,
                dimension = texture.dimension.ToString(),
                format = texture.format.ToString(),
                graphicsFormat = texture.graphicsFormat.ToString(),
                depthBits = texture.depth,
                antiAliasing = texture.antiAliasing,
                created = texture.IsCreated()
            };
        }

        private static string ReadActiveCameraGroup()
        {
            PostProcessingSystem[] systems = Resources.FindObjectsOfTypeAll<PostProcessingSystem>();
            if (systems.Length == 0 || ActiveCameraGroup == null)
            {
                return "Unavailable";
            }
            object value = ActiveCameraGroup.GetValue(systems[0]);
            return value == null ? "Unavailable" : value.ToString();
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

        private static T ReadField<T>(FieldInfo field, object instance)
        {
            if (field == null || instance == null)
            {
                return default(T);
            }
            object value = field.GetValue(instance);
            return value is T typed ? typed : default(T);
        }

        private static ulong[] GetCameraIds(Camera[] cameras)
        {
            var ids = new ulong[cameras.Length];
            for (int index = 0; index < cameras.Length; index++)
            {
                ids[index] = GetId(cameras[index]);
            }
            return ids;
        }

        private static ulong GetId(UnityEngine.Object value)
        {
            return value == null ? 0UL : EntityId.ToULong(value.GetEntityId());
        }

        private static string GetHierarchy(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }
            var names = new List<string>(8);
            Transform current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private static string FormatRect(Rect rect)
        {
            return rect.x.ToString("R") + "," +
                   rect.y.ToString("R") + "," +
                   rect.width.ToString("R") + "," +
                   rect.height.ToString("R");
        }

        private static int CompareCameras(Camera left, Camera right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return 1;
            if (right == null) return -1;
            int depthComparison = left.depth.CompareTo(right.depth);
            return depthComparison != 0
                ? depthComparison
                : string.Compare(left.name, right.name, StringComparison.Ordinal);
        }
    }
}
