using System;
using System.Collections.Generic;
using System.Reflection;
using AwesomeTechnologies.VegetationSystem;
using HarmonyLib;
using ReduxBetterAA.Patches;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Rendering;
using UnityEngine.ResourceManagement.AsyncOperations;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    /// <summary>
    /// Replaces the legacy direct indirect-vegetation submission with Unity's
    /// supported RenderMeshIndirect API. Static vegetation uses camera-only
    /// motion. An exact Unity 6000.4 motion-shader override then excludes only
    /// indirect object passes whose injected previous transform is identity
    /// while their current transform is camera-centred. The valid full-screen
    /// camera motion underneath remains untouched.
    /// </summary>
    internal sealed class VegetationMotionCompatibility : IDisposable
    {
        internal const bool DefaultEnabled = true;
        private const string HarmonyId =
            "ReduxBetterAA.VegetationMotionCompatibility";
        private const string MotionVectorRepairShaderAddress =
            "Assets/ReduxBetterAA/Shaders/VegetationMotionVectorRepair.shader";

        public static VegetationMotionCompatibility Current;

        private readonly ReduxLogger _logger;
        private readonly Action _motionInputChanged;
        private Harmony _harmony;
        private MethodBase _patchedMethod;
        private AsyncOperationHandle<Shader> _shaderHandle;
        private bool _shaderHandleValid;
        private Shader _repairShader;
        private Shader _originalMotionVectorShader;
        private BuiltinShaderMode _originalMotionVectorShaderMode;
        private bool _enabled = DefaultEnabled;
        private bool _patchInstalled;
        private bool _repairShaderInstalled;
        private bool _diagnosticMotionVectorOverrideActive;
        private bool _customShaderConflict;
        private bool _customShaderConflictLogged;
        private bool _shaderLoadFailed;
        private bool _runtimeFailed;
        private bool _transientBypassLogged;
        private bool _disposed;
        private long _reroutedCalls;
        private long _transientBypasses;
        private string _status = "Vegetation camera-motion repair is loading.";

        public VegetationMotionCompatibility(
            ReduxLogger logger,
            Action motionInputChanged)
        {
            _logger = logger;
            _motionInputChanged = motionInputChanged;
        }

        public bool Enabled => _enabled;
        public bool Available => _patchInstalled && RepairShaderReady &&
            !_customShaderConflict && !_runtimeFailed;
        public long ReroutedCalls => _reroutedCalls;
        public long TransientBypasses => _transientBypasses;
        public string Status => _status;

        public void Initialize()
        {
            if (_disposed || _patchInstalled)
            {
                return;
            }

            _harmony = new Harmony(HarmonyId);
            string reason;
            try
            {
                if (!VegetationMotionCompatibilityPatch.TryInstall(
                        _harmony,
                        out _patchedMethod,
                        out reason))
                {
                    _enabled = false;
                    _status = "Unavailable: " + reason;
                    _logger.LogWarning(
                        "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                        "repair is unavailable: " + reason + "."
                    );
                    return;
                }
            }
            catch (Exception exception)
            {
                _enabled = false;
                _status = "Unavailable: Harmony patch failed (" +
                    exception.GetType().Name + ").";
                _logger.LogError(
                    "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                    "repair failed to install: " + exception.GetType().Name +
                    ": " + exception.Message
                );
                return;
            }

            _patchInstalled = true;
            _originalMotionVectorShaderMode = GraphicsSettings.GetShaderMode(
                BuiltinShaderType.MotionVectors
            );
            _originalMotionVectorShader = GraphicsSettings.GetCustomShader(
                BuiltinShaderType.MotionVectors
            );
            if (_originalMotionVectorShaderMode == BuiltinShaderMode.UseCustom)
            {
                _customShaderConflict = true;
                LogCustomShaderConflictOnce();
            }
            _shaderHandle = Addressables.LoadAssetAsync<Shader>(
                MotionVectorRepairShaderAddress
            );
            _shaderHandleValid = true;
            _shaderHandle.Completed += OnRepairShaderLoaded;
            UpdateStatus();
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                "reroute installed; exact object-history exclusion is loading."
            );
        }

        public bool SetEnabled(bool enabled)
        {
            if (_disposed || !_patchInstalled || _shaderLoadFailed ||
                _runtimeFailed)
            {
                return false;
            }
            if (_enabled == enabled)
            {
                return false;
            }

            _enabled = enabled;
            RefreshMotionVectorShaderState();
            UpdateStatus();
            _logger.LogInfo(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion " +
                "repair " + (enabled ? "enabled." : "disabled.")
            );
            return true;
        }

        /// <summary>
        /// Coordinates the TestHarness-only built-in motion shader probe with
        /// the production override. Diagnostic modes intentionally retain the
        /// RenderMeshIndirect reroute so they can inspect its object pass.
        /// </summary>
        internal void SetDiagnosticMotionVectorOverrideActive(bool active)
        {
            if (_disposed)
            {
                return;
            }
            _diagnosticMotionVectorOverrideActive = active;
            _repairShaderInstalled = !active && RepairShaderReady &&
                GraphicsSettings.GetShaderMode(
                    BuiltinShaderType.MotionVectors
                ) == BuiltinShaderMode.UseCustom &&
                GraphicsSettings.GetCustomShader(
                    BuiltinShaderType.MotionVectors
                ) == _repairShader;
            if (!active)
            {
                RefreshMotionVectorShaderState();
            }
            UpdateStatus();
        }

        /// <summary>
        /// Returns true when Harmony should execute the original method.
        /// Command-buffer draws remain owned by the original vegetation renderer;
        /// the diagnosed flight failure uses this method's direct Graphics branch.
        /// </summary>
        internal bool TryRender(
            VegetationItemModelInfo vegetationItemModelInfo,
            Bounds cellBounds,
            int cameraIndex,
            int lodIndex,
            Camera selectedCamera,
            ShadowCastingMode shadowCastingMode,
            int layer,
            bool shadows,
            CommandBuffer commandBuffer,
            int visibleShaderDataBufferId,
            int indirectShaderDataBufferId)
        {
            if (!_enabled || !_patchInstalled || _runtimeFailed ||
                !MotionShaderSupportsReroute ||
                commandBuffer != null || vegetationItemModelInfo == null)
            {
                return true;
            }

            bool submitted = false;
            try
            {
                MaterialPropertyBlock properties =
                    vegetationItemModelInfo.GetLODMaterialPropertyBlock(lodIndex);
                GraphicsBuffer visibleBuffer =
                    vegetationItemModelInfo.GetLODVisibleBuffer(
                        lodIndex,
                        cameraIndex,
                        shadows);
                Mesh mesh = vegetationItemModelInfo.GetLODMesh(lodIndex);
                Material[] materials =
                    vegetationItemModelInfo.GetLODMaterials(lodIndex);
                List<GraphicsBuffer> argumentBuffers =
                    vegetationItemModelInfo.GetLODArgsBufferList(
                        lodIndex,
                        cameraIndex,
                        shadows);

                if (properties == null || visibleBuffer == null || mesh == null ||
                    materials == null || argumentBuffers == null)
                {
                    return true;
                }

                int drawCount = Mathf.Min(mesh.subMeshCount, materials.Length);
                if (drawCount <= 0 || argumentBuffers.Count < drawCount)
                {
                    return true;
                }
                for (int materialIndex = 0;
                    materialIndex < drawCount;
                    materialIndex++)
                {
                    if (materials[materialIndex] == null ||
                        argumentBuffers[materialIndex] == null)
                    {
                        return true;
                    }
                }

                properties.Clear();
                if (vegetationItemModelInfo.ShaderControler != null &&
                    vegetationItemModelInfo.ShaderControler.Settings.SampleWind)
                {
                    var windSamplers =
                        vegetationItemModelInfo.WindSamplerMeshRendererList;
                    MeshRenderer windSampler = windSamplers != null &&
                        cameraIndex >= 0 && cameraIndex < windSamplers.Count
                            ? windSamplers[cameraIndex]
                            : null;
                    if (windSampler != null)
                    {
                        windSampler.GetPropertyBlock(properties);
                    }
                }

                properties.SetBuffer(
                    visibleShaderDataBufferId,
                    visibleBuffer);
                properties.SetBuffer(
                    indirectShaderDataBufferId,
                    visibleBuffer);

                for (int materialIndex = 0;
                    materialIndex < drawCount;
                    materialIndex++)
                {
                    var renderParams = new RenderParams(materials[materialIndex])
                    {
                        camera = selectedCamera,
                        layer = layer,
                        lightProbeUsage = LightProbeUsage.Off,
                        matProps = properties,
                        motionVectorMode = MotionVectorGenerationMode.Camera,
                        receiveShadows = true,
                        shadowCastingMode = shadowCastingMode,
                        worldBounds = cellBounds
                    };
                    Graphics.RenderMeshIndirect(
                        renderParams,
                        mesh,
                        argumentBuffers[materialIndex],
                        1,
                        0);
                    submitted = true;
                }

                _reroutedCalls++;
                return false;
            }
            catch (ArgumentOutOfRangeException exception) when (!submitted)
            {
                // Vegetation camera/model lists are briefly rebuilt during
                // scene startup. The original submission owns that transient
                // state, so bypass this draw without permanently disabling the
                // compatibility path for the stable flight renderer.
                _transientBypasses++;
                if (!_transientBypassLogged)
                {
                    _transientBypassLogged = true;
                    _logger.LogWarning(
                        "[ReduxBetterAA/Motion] Indirect vegetation repair " +
                        "bypassed an incomplete startup draw (" +
                        exception.GetType().Name + "); later draws remain " +
                        "eligible."
                    );
                }
                return true;
            }
            catch (Exception exception)
            {
                FailRuntime(exception);
                // Avoid double-submitting submeshes if Unity failed after one
                // RenderMeshIndirect command was already accepted this frame.
                return !submitted;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _enabled = false;
            RestoreOriginalMotionVectorShader();
            if (_harmony != null && _patchedMethod != null)
            {
                _harmony.Unpatch(
                    _patchedMethod,
                    HarmonyPatchType.Prefix,
                    HarmonyId);
            }
            _patchedMethod = null;
            _harmony = null;
            _patchInstalled = false;
            if (_shaderHandleValid)
            {
                _shaderHandle.Completed -= OnRepairShaderLoaded;
                Addressables.Release(_shaderHandle);
                _shaderHandleValid = false;
            }
            _repairShader = null;
            _originalMotionVectorShader = null;
            _status = "Disposed; original vegetation rendering restored.";
        }

        private void FailRuntime(Exception exception)
        {
            if (_runtimeFailed)
            {
                return;
            }
            _runtimeFailed = true;
            _enabled = false;
            RestoreOriginalMotionVectorShader();
            _status = "Disabled after a runtime failure: " +
                exception.GetType().Name + ". Original rendering restored.";
            _logger.LogError(
                "[ReduxBetterAA/Motion] Indirect vegetation camera-motion repair " +
                "failed closed; original rendering resumes next draw: " +
                exception.GetType().Name + ": " + exception.Message
            );
        }

        private void UpdateStatus()
        {
            if (_runtimeFailed)
            {
                return;
            }
            if (!_patchInstalled)
            {
                return;
            }
            if (_shaderLoadFailed)
            {
                _status = "Unavailable: exact vegetation motion shader failed " +
                    "to load; original rendering is active.";
                return;
            }
            if (!RepairShaderReady)
            {
                _status = "Loading exact vegetation object-history exclusion; " +
                    "original rendering remains active.";
                return;
            }
            if (_customShaderConflict)
            {
                _status = "Unavailable: another custom built-in motion-vector " +
                    "shader owns the global slot; original rendering is active.";
                return;
            }
            if (!_enabled)
            {
                _status = "Disabled: original DrawMeshInstancedIndirect " +
                    "vegetation submission is active.";
                return;
            }
            if (_diagnosticMotionVectorOverrideActive)
            {
                _status = "Active: vegetation uses RenderMeshIndirect while " +
                    "the diagnostic motion shader temporarily owns the pass.";
                return;
            }
            _status = _repairShaderInstalled
                ? "Active: vegetation uses RenderMeshIndirect with camera " +
                  "motion and exact invalid object-history exclusion."
                : "Suspended: exact motion shader is not installed; original " +
                  "vegetation rendering is active.";
        }

        private bool RepairShaderReady => _repairShader != null &&
            _repairShader.isSupported;

        private bool MotionShaderSupportsReroute =>
            _diagnosticMotionVectorOverrideActive || _repairShaderInstalled;

        private void OnRepairShaderLoaded(
            AsyncOperationHandle<Shader> operation)
        {
            if (_disposed)
            {
                return;
            }
            if (operation.Status == AsyncOperationStatus.Succeeded &&
                operation.Result != null && operation.Result.isSupported)
            {
                _repairShader = operation.Result;
                bool wasInstalled = _repairShaderInstalled;
                RefreshMotionVectorShaderState();
                UpdateStatus();
                _logger.LogInfo(
                    "[ReduxBetterAA/Motion] Exact indirect-vegetation " +
                    "object-history exclusion loaded" +
                    (_repairShaderInstalled ? " and installed." : ".")
                );
                if (!wasInstalled && _repairShaderInstalled)
                {
                    _motionInputChanged?.Invoke();
                }
                return;
            }

            _shaderLoadFailed = true;
            _enabled = false;
            RestoreOriginalMotionVectorShader();
            UpdateStatus();
            _logger.LogError(
                "[ReduxBetterAA/Motion] Exact indirect-vegetation motion " +
                "shader failed to load; original renderer remains active."
            );
        }

        private void RefreshMotionVectorShaderState()
        {
            if (_disposed || _diagnosticMotionVectorOverrideActive)
            {
                return;
            }

            Shader current = GraphicsSettings.GetCustomShader(
                BuiltinShaderType.MotionVectors
            );
            BuiltinShaderMode currentMode = GraphicsSettings.GetShaderMode(
                BuiltinShaderType.MotionVectors
            );
            bool shouldInstall = _enabled && _patchInstalled &&
                RepairShaderReady && !_customShaderConflict && !_runtimeFailed;
            if (shouldInstall)
            {
                if (currentMode == BuiltinShaderMode.UseCustom &&
                    current == _repairShader)
                {
                    _repairShaderInstalled = true;
                    return;
                }
                if (currentMode == _originalMotionVectorShaderMode &&
                    current == _originalMotionVectorShader)
                {
                    GraphicsSettings.SetCustomShader(
                        BuiltinShaderType.MotionVectors,
                        _repairShader
                    );
                    GraphicsSettings.SetShaderMode(
                        BuiltinShaderType.MotionVectors,
                        BuiltinShaderMode.UseCustom
                    );
                    _repairShaderInstalled =
                        GraphicsSettings.GetShaderMode(
                            BuiltinShaderType.MotionVectors
                        ) == BuiltinShaderMode.UseCustom &&
                        GraphicsSettings.GetCustomShader(
                            BuiltinShaderType.MotionVectors
                        ) == _repairShader;
                    return;
                }

                _repairShaderInstalled = false;
                _customShaderConflict = true;
                LogCustomShaderConflictOnce();
                return;
            }

            if (currentMode == BuiltinShaderMode.UseCustom &&
                current == _repairShader)
            {
                GraphicsSettings.SetCustomShader(
                    BuiltinShaderType.MotionVectors,
                    _originalMotionVectorShader
                );
                GraphicsSettings.SetShaderMode(
                    BuiltinShaderType.MotionVectors,
                    _originalMotionVectorShaderMode
                );
            }
            _repairShaderInstalled = false;
        }

        private void RestoreOriginalMotionVectorShader()
        {
            if (_repairShader != null &&
                GraphicsSettings.GetShaderMode(
                    BuiltinShaderType.MotionVectors
                ) == BuiltinShaderMode.UseCustom &&
                GraphicsSettings.GetCustomShader(
                    BuiltinShaderType.MotionVectors
                ) == _repairShader)
            {
                GraphicsSettings.SetCustomShader(
                    BuiltinShaderType.MotionVectors,
                    _originalMotionVectorShader
                );
                GraphicsSettings.SetShaderMode(
                    BuiltinShaderType.MotionVectors,
                    _originalMotionVectorShaderMode
                );
            }
            _repairShaderInstalled = false;
        }

        private void LogCustomShaderConflictOnce()
        {
            if (_customShaderConflictLogged)
            {
                return;
            }
            _customShaderConflictLogged = true;
            string shaderName = _originalMotionVectorShader == null
                ? "<null>"
                : _originalMotionVectorShader.name;
            _logger.LogWarning(
                "[ReduxBetterAA/Motion] Exact vegetation motion repair will " +
                "not replace another mod's custom built-in motion-vector " +
                "shader (mode " + _originalMotionVectorShaderMode +
                ", shader " + shaderName + "); the original vegetation " +
                "renderer remains active."
            );
        }
    }
}
