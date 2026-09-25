using System;
using System.Collections.Generic;
using HarmonyLib;
using KSP.Rendering.Planets;
using ReduxBetterAA.Patches;
using UnityEngine;
using ReduxLogger = ReduxLib.Logging.ILogger;

namespace ReduxBetterAA.Rendering
{
    internal readonly struct TerrainMotionFrame
    {
        internal readonly Texture Depth;
        internal readonly Matrix4x4 PreviousWorldFromCurrent;
        internal readonly int Frame, PreviousFrame;

        internal TerrainMotionFrame(Texture depth, Matrix4x4 previousWorldFromCurrent, int frame, int previousFrame)
        {
            Depth = depth;
            PreviousWorldFromCurrent = previousWorldFromCurrent;
            Frame = frame;
            PreviousFrame = previousFrame;
        }
    }

    // Observes the existing procedural terrain submission. The renderer owns the
    // borrowed depth texture; this service never changes it or submits GPU work.
    internal sealed class TerrainMotionCompatibility : IDisposable
    {
        private const string HarmonyId = "ReduxBetterAA.TerrainMotionCompatibility";
        private const int MaximumRenderers = 64;
        private static readonly int DepthId = Shader.PropertyToID("_PQSDepthTexture");
        private readonly Dictionary<PQSRenderer, Entry> _entries = new Dictionary<PQSRenderer, Entry>(MaximumRenderers);
        private readonly ReduxLogger _logger;
        private Harmony _harmony;
        private uint _epoch = 1;
        private int _ownerThread;
        private bool _installed, _disposed, _failed;

        internal static TerrainMotionCompatibility Current { get; private set; }
        internal string Status { get; private set; } = "Terrain motion observation is not initialized.";
        internal bool Available => _installed && !_disposed && !_failed;

        internal enum Boundary { Generation, Depth, Color }

        internal sealed class Entry
        {
            internal PQSRenderer Renderer;
            internal int GenerationFrame = -1;
            internal ulong Generation;
            internal bool GenerationValid;
            internal Matrix4x4 World;
            internal Draw Current = new Draw { Frame = -1 }, Previous = new Draw { Frame = -1 };
        }

        internal struct Draw
        {
            internal int Frame;
            internal ulong Generation;
            internal Camera Camera;
            internal Material Material;
            internal RenderTexture Depth;
            internal Matrix4x4 World;
            internal int DepthCalls, ColorCalls;
            internal bool DepthComplete, ColorComplete, Rejected;
            internal bool Complete => !Rejected && DepthCalls == 1 && ColorCalls == 1 && DepthComplete && ColorComplete;
        }

        internal readonly struct Observation
        {
            internal readonly TerrainMotionCompatibility Service;
            internal readonly Entry Entry;
            internal readonly uint Epoch;
            internal readonly int Frame;
            internal readonly ulong Generation;
            internal readonly Matrix4x4 World;
            internal readonly Boundary Kind;

            internal Observation(TerrainMotionCompatibility service, Entry entry, uint epoch, int frame, Boundary kind, Matrix4x4 world)
            {
                Service = service; Entry = entry; Epoch = epoch; Frame = frame;
                Generation = entry.Generation; World = world; Kind = kind;
            }
        }

        internal TerrainMotionCompatibility(ReduxLogger logger) { _logger = logger; }

        internal void Initialize()
        {
            if (_disposed || _installed || _failed) return;
            if (Current != null && !ReferenceEquals(Current, this))
            {
                Status = "Unavailable: another terrain motion observer owns the boundary.";
                return;
            }
            _ownerThread = Environment.CurrentManagedThreadId;
            try
            {
                string reason = "The audited Unity renderer version is unavailable.";
                if (Application.unityVersion == "6000.5.8f1" || Application.unityVersion == "6000.6.0f1")
                    _harmony = new Harmony(HarmonyId);
                if (_harmony == null || !TerrainMotionCompatibilityPatch.TryInstall(_harmony, out reason))
                {
                    Status = "Unavailable: " + reason;
                    RemoveOwnedPatches();
                    _failed = true;
                    _logger?.LogWarning("[ReduxBetterAA/Motion] " + Status);
                    return;
                }
                _installed = true;
                Current = this;
                Status = "Terrain motion observation is ready.";
            }
            catch (Exception exception)
            {
                RemoveOwnedPatches();
                _failed = true;
                Status = "Unavailable: terrain motion boundary installation failed (" + exception.GetType().Name + ").";
                _logger?.LogWarning("[ReduxBetterAA/Motion] " + Status);
            }
        }

        internal void Invalidate()
        {
            ++_epoch;
            _entries.Clear();
        }

        internal void FailObservation()
        {
            _failed = true;
            Invalidate();
            Status = "Unavailable: terrain motion observation failed; ordinary motion is retained.";
        }

        internal Observation Begin(PQSRenderer renderer, Camera camera, Material material, Boundary kind)
        {
            if (!Available || Environment.CurrentManagedThreadId != _ownerThread || renderer == null) return default;
            Entry entry;
            if (!_entries.TryGetValue(renderer, out entry))
            {
                if (_entries.Count == MaximumRenderers) return default;
                entry = new Entry { Renderer = renderer };
                _entries.Add(renderer, entry);
            }
            int frame = Time.frameCount;
            Matrix4x4 world = renderer.transform.localToWorldMatrix;
            BeginRecord(entry, camera, material, kind, frame, world);
            if (kind != Boundary.Generation && !ReferenceEquals(renderer.SourceCamera, camera)) entry.Current.Rejected = true;
            return new Observation(this, entry, _epoch, frame, kind, world);
        }

        // The same state transitions are tested without executing game methods.
        internal static Observation BeginRecord(Entry entry, Camera camera, Material material, Boundary kind, int frame, Matrix4x4 world)
        {
            if (kind == Boundary.Generation)
            {
                ++entry.Generation;
                entry.GenerationFrame = frame;
                entry.GenerationValid = false;
                if (entry.Current.Frame == frame) entry.Current.Rejected = true;
            }
            else
            {
                if (entry.Current.Frame != frame)
                {
                    entry.Previous = entry.Current;
                    entry.Current = new Draw { Frame = frame, Camera = camera, Material = material,
                        Generation = entry.Generation, World = entry.World };
                }
                if (kind == Boundary.Depth) ++entry.Current.DepthCalls;
                else ++entry.Current.ColorCalls;
                if (camera == null || !camera.isActiveAndEnabled || material == null ||
                    !ReferenceEquals(entry.Current.Camera, camera) ||
                    !ReferenceEquals(entry.Current.Material, material) || entry.Current.Generation != entry.Generation ||
                    !entry.GenerationValid || entry.GenerationFrame != frame)
                    entry.Current.Rejected = true;
            }
            return new Observation(null, entry, 0, frame, kind, world);
        }

        internal void Complete(in Observation observation, bool ranOriginal)
        {
            if (!IsCurrent(observation)) return;
            Entry entry = observation.Entry;
            CompleteRecord(observation, ranOriginal && Time.frameCount == observation.Frame,
                entry.Renderer.transform.localToWorldMatrix, entry.Renderer.DepthBuffer);
        }

        internal static void CompleteRecord(in Observation observation, bool ranOriginal, Matrix4x4 world, RenderTexture depth)
        {
            Entry entry = observation.Entry;
            bool stable = ranOriginal &&
                SameFinite(observation.World, world) && observation.Generation == entry.Generation;
            if (observation.Kind == Boundary.Generation)
            {
                entry.GenerationValid = stable && IsAffine(world);
                entry.World = world;
                if (!stable) entry.Current.Rejected = true;
                return;
            }
            if (!stable || !entry.GenerationValid || !SameFinite(entry.World, world) ||
                entry.Current.Frame != observation.Frame || entry.Current.Generation != observation.Generation)
            {
                entry.Current.Rejected = true;
                return;
            }
            if (observation.Kind == Boundary.Depth)
            {
                entry.Current.Depth = depth;
                entry.Current.DepthComplete = entry.Current.Depth != null;
            }
            else entry.Current.ColorComplete = true;
        }

        internal void Reject(in Observation observation)
        {
            if (!IsCurrent(observation)) return;
            RejectRecord(observation);
        }

        internal static void RejectRecord(in Observation observation)
        {
            if (observation.Entry == null) return;
            observation.Entry.Current.Rejected = true;
            if (observation.Kind == Boundary.Generation) observation.Entry.GenerationValid = false;
        }

        private bool IsCurrent(in Observation observation) => Available && ReferenceEquals(observation.Service, this) &&
            observation.Epoch == _epoch && observation.Entry != null && Environment.CurrentManagedThreadId == _ownerThread;

        internal bool TryGetFrame(Camera camera, int frame, out TerrainMotionFrame result)
        {
            result = default;
            if (!Available || Environment.CurrentManagedThreadId != _ownerThread || camera == null ||
                !camera.isActiveAndEnabled || frame <= 0 || frame != Time.frameCount) return false;
            Entry selected = null;
            foreach (Entry entry in _entries.Values)
            {
                if (entry.Current.Frame != frame || !ReferenceEquals(entry.Current.Camera, camera)) continue;
                if (selected != null) return false;
                selected = entry;
            }
            if (selected == null || selected.Renderer == null || !ReferenceEquals(selected.Renderer.SourceCamera, camera) ||
                !ReferenceEquals(selected.Renderer.DepthBuffer, selected.Current.Depth)) return false;
            return TryResolveRecord(selected, camera, frame, Shader.GetGlobalTexture(DepthId), out result);
        }

        internal static bool TryResolveRecord(Entry selected, Camera camera, int frame, Texture activeDepth, out TerrainMotionFrame result)
        {
            result = default;
            if (selected == null || camera == null || !camera.isActiveAndEnabled || frame <= 0 || selected.Current.Frame != frame ||
                !ReferenceEquals(selected.Current.Camera, camera) || !selected.Current.Complete || !selected.Previous.Complete ||
                selected.Previous.Frame != frame - 1 || !ReferenceEquals(selected.Previous.Camera, camera) ||
                !ReferenceEquals(selected.Previous.Material, selected.Current.Material) ||
                !ReferenceEquals(selected.Current.Depth, selected.Previous.Depth) || selected.Current.Depth == null ||
                !selected.Current.Depth.IsCreated() ||
                !ReferenceEquals(selected.Current.Depth, activeDepth)) return false;
            Matrix4x4 previousWorldFromCurrent;
            if (!TryPreviousWorldFromCurrent(selected.Previous.World, selected.Current.World, out previousWorldFromCurrent)) return false;
            result = new TerrainMotionFrame(selected.Current.Depth, previousWorldFromCurrent, frame, selected.Previous.Frame);
            return true;
        }

        internal static bool TryPreviousWorldFromCurrent(Matrix4x4 previous, Matrix4x4 current, out Matrix4x4 result)
        {
            result = default;
            if (!IsAffine(previous) || !IsAffine(current) || Mathf.Abs(current.determinant) < 0.000001f) return false;
            result = previous * current.inverse;
            return IsAffine(result);
        }

        private static bool IsAffine(Matrix4x4 matrix) => SameFinite(matrix, matrix) &&
            matrix.m30 == 0f && matrix.m31 == 0f && matrix.m32 == 0f && Mathf.Abs(matrix.m33 - 1f) < 0.0001f;

        private static bool SameFinite(Matrix4x4 left, Matrix4x4 right)
        {
            for (int i = 0; i < 16; ++i)
                if (float.IsNaN(left[i]) || float.IsInfinity(left[i]) || left[i] != right[i]) return false;
            return true;
        }

        private void RemoveOwnedPatches()
        {
            Harmony owned = _harmony;
            _harmony = null;
            // If Harmony cannot remove a boundary, its callbacks stay passive:
            // Current is unpublished before disposal and never published on failure.
            try { owned?.UnpatchAll(HarmonyId); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(Current, this)) Current = null;
            RemoveOwnedPatches();
            _installed = false;
            Invalidate();
        }
    }
}
