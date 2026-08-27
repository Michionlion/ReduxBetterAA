using System;
using System.Collections.Generic;
using KSP.Sim.impl;
using ReduxLib.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    /// <summary>
    /// Opt-in diagnostic that asks Unity to interpolate KSP physics-body render
    /// transforms between fixed updates. This changes the rendered pose that
    /// produces color, depth, and motion vectors together; it deliberately does
    /// not smooth the motion-vector texture independently.
    /// </summary>
    internal sealed class KspPhysicsRenderInterpolation : IDisposable
    {
        private const float RefreshIntervalSeconds = 1.0f;
        private const int RefreshRetries = 5;

        public static KspPhysicsRenderInterpolation Current;

        private readonly ReduxLogger _logger;
        private readonly Action _onMotionInputChanged;
        private readonly Dictionary<Rigidbody, RigidbodyInterpolation> _originalModes =
            new Dictionary<Rigidbody, RigidbodyInterpolation>(512);
        private readonly List<Rigidbody> _destroyedBodies = new List<Rigidbody>(64);

        private bool _enabled;
        private bool _initialized;
        private bool _disposed;
        private bool _motionInputChangedPending;
        private bool _statusDirty;
        private float _refreshAfter;
        private int _remainingRefreshRetries;
        private string _status = "Disabled; stock Rigidbody modes are unchanged.";

        public KspPhysicsRenderInterpolation(
            ReduxLogger logger,
            Action onMotionInputChanged)
        {
            _logger = logger;
            _onMotionInputChanged = onMotionInputChanged;
        }

        internal KspPhysicsRenderInterpolation()
            : this(null, null)
        {
        }

        public bool Enabled => _enabled;
        public int TrackedBodyCount => _originalModes.Count;
        public string Status
        {
            get
            {
                if (_statusDirty)
                {
                    UpdateStatus();
                }
                return _status;
            }
        }

        public void Initialize()
        {
            if (_initialized || _disposed)
            {
                return;
            }
            _initialized = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;
        }

        public void Tick()
        {
            if (_disposed)
            {
                return;
            }

            if (_enabled && _remainingRefreshRetries > 0 &&
                Time.unscaledTime >= _refreshAfter)
            {
                RefreshNow();
                _remainingRefreshRetries--;
                _refreshAfter = Time.unscaledTime + RefreshIntervalSeconds;
            }

            if (_statusDirty)
            {
                UpdateStatus();
            }
            if (_motionInputChangedPending)
            {
                _motionInputChangedPending = false;
                _onMotionInputChanged?.Invoke();
            }
        }

        public bool SetEnabled(bool enabled)
        {
            if (_disposed || _enabled == enabled)
            {
                return false;
            }

            _enabled = enabled;
            if (_enabled)
            {
                ScheduleRefresh(false);
                RefreshNow();
                _logger?.LogInfo(
                    "[ReduxBetterAA/Motion] Experimental KSP physics render interpolation enabled."
                );
            }
            else
            {
                _remainingRefreshRetries = 0;
                int restored = RestoreAllInternal();
                if (restored > 0)
                {
                    _motionInputChangedPending = true;
                }
                _logger?.LogInfo(
                    "[ReduxBetterAA/Motion] Experimental KSP physics render interpolation disabled; original modes restored."
                );
            }

            _statusDirty = true;
            UpdateStatus();
            return true;
        }

        public int RefreshNow()
        {
            if (_disposed || !_enabled)
            {
                return 0;
            }

            RemoveDestroyedBodies();
            RigidbodyBehavior[] behaviors =
                Resources.FindObjectsOfTypeAll<RigidbodyBehavior>();
            int changed = 0;
            for (int index = 0; index < behaviors.Length; index++)
            {
                RigidbodyBehavior behavior = behaviors[index];
                if (behavior == null || !behavior.gameObject.scene.IsValid() ||
                    !behavior.gameObject.scene.isLoaded)
                {
                    continue;
                }

                if (TryApply(behavior.activeRigidBody))
                {
                    changed++;
                }
            }

            if (changed > 0)
            {
                _motionInputChangedPending = true;
            }
            _statusDirty = true;
            return changed;
        }

        /// <summary>
        /// Called by the StartPhysX postfix so bodies created after discovery do
        /// not require a hierarchy scan.
        /// </summary>
        public bool Apply(Rigidbody body)
        {
            if (!TryApply(body))
            {
                return false;
            }

            _motionInputChangedPending = true;
            _statusDirty = true;
            return true;
        }

        /// <summary>
        /// Called before StopPhysX destroys or detaches the active body.
        /// </summary>
        public bool Restore(Rigidbody body)
        {
            if (body == null || !_originalModes.TryGetValue(
                    body,
                    out RigidbodyInterpolation originalMode))
            {
                return false;
            }

            if (body.interpolation == RigidbodyInterpolation.Interpolate)
            {
                body.interpolation = originalMode;
            }
            _originalModes.Remove(body);
            _motionInputChangedPending = true;
            _statusDirty = true;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_initialized)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                SceneManager.activeSceneChanged -= OnActiveSceneChanged;
                _initialized = false;
            }
            RestoreAllInternal();
            _remainingRefreshRetries = 0;
            _motionInputChangedPending = false;
            _status = "Disposed; original Rigidbody modes were restored.";
            _statusDirty = false;
        }

        private bool TryApply(Rigidbody body)
        {
            if (_disposed || !_enabled || body == null ||
                _originalModes.ContainsKey(body) ||
                body.interpolation != RigidbodyInterpolation.None)
            {
                return false;
            }

            _originalModes.Add(body, body.interpolation);
            body.interpolation = RigidbodyInterpolation.Interpolate;
            return true;
        }

        private int RestoreAllInternal()
        {
            int restored = 0;
            foreach (KeyValuePair<Rigidbody, RigidbodyInterpolation> pair in
                     _originalModes)
            {
                Rigidbody body = pair.Key;
                if (body != null &&
                    body.interpolation == RigidbodyInterpolation.Interpolate)
                {
                    body.interpolation = pair.Value;
                    restored++;
                }
            }
            _originalModes.Clear();
            _destroyedBodies.Clear();
            return restored;
        }

        private void RemoveDestroyedBodies()
        {
            _destroyedBodies.Clear();
            foreach (KeyValuePair<Rigidbody, RigidbodyInterpolation> pair in
                     _originalModes)
            {
                if (pair.Key == null)
                {
                    _destroyedBodies.Add(pair.Key);
                }
            }
            for (int index = 0; index < _destroyedBodies.Count; index++)
            {
                _originalModes.Remove(_destroyedBodies[index]);
            }
            _destroyedBodies.Clear();
        }

        private void ScheduleRefresh(bool immediate)
        {
            _remainingRefreshRetries = RefreshRetries;
            _refreshAfter = immediate
                ? Time.unscaledTime
                : Time.unscaledTime + RefreshIntervalSeconds;
        }

        private void RestoreAndScheduleRefresh()
        {
            if (!_enabled || _disposed)
            {
                return;
            }
            int restored = RestoreAllInternal();
            if (restored > 0)
            {
                _motionInputChangedPending = true;
            }
            ScheduleRefresh(true);
            _statusDirty = true;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_enabled)
            {
                ScheduleRefresh(true);
            }
        }

        private void OnSceneUnloaded(Scene scene)
        {
            RestoreAndScheduleRefresh();
        }

        private void OnActiveSceneChanged(Scene previous, Scene next)
        {
            RestoreAndScheduleRefresh();
        }

        private void UpdateStatus()
        {
            _status = _enabled
                ? _originalModes.Count > 0
                    ? "Enabled; interpolating " + _originalModes.Count +
                      " active KSP physics Rigidbody(s)."
                    : "Enabled; waiting for active KSP physics Rigidbodies."
                : "Disabled; stock Rigidbody modes are unchanged.";
            _statusDirty = false;
        }
    }
}
