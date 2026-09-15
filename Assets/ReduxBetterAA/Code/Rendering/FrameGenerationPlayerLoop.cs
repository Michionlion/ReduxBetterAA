using System;
using System.Collections.Generic;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace ReduxBetterAA.Rendering
{
    // These are actual CPU simulation/render-submission boundaries. GPU work and
    // the end-of-render marker remain on the native render thread. Removing this
    // owner never restores an old copy of another mod's PlayerLoop.
    internal sealed class FrameGenerationPlayerLoop : IDisposable
    {
        private sealed class BeginSimulationMarker { }
        private sealed class BeginRenderMarker { }
        private readonly PlayerLoopSystem.UpdateFunction _beginSimulation, _beginRender;
        private bool _installed;

        internal FrameGenerationPlayerLoop(Action beginSimulation, Action beginRender)
        {
            if (beginSimulation == null || beginRender == null) throw new ArgumentNullException();
            _beginSimulation = () => beginSimulation();
            _beginRender = () => beginRender();
        }

        internal bool Install()
        {
            if (_installed) return true;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            if (!TryInsert(ref loop, _beginSimulation, _beginRender)) return false;
            PlayerLoop.SetPlayerLoop(loop);
            return _installed = true;
        }

        internal bool IsInstalled
        {
            get
            {
                if (!_installed) return false;
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                return CountDelegate(in loop, _beginSimulation) == 1 && CountDelegate(in loop, _beginRender) == 1;
            }
        }

        public void Dispose()
        {
            if (!_installed) return;
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveOwned(ref loop, _beginSimulation, _beginRender);
            PlayerLoop.SetPlayerLoop(loop);
            _installed = false;
        }

        internal static bool TryInsert(ref PlayerLoopSystem loop,
            PlayerLoopSystem.UpdateFunction beginSimulation, PlayerLoopSystem.UpdateFunction beginRender)
        {
            if (beginSimulation == null || beginRender == null ||
                CountType(in loop, typeof(EarlyUpdate)) != 1 ||
                CountType(in loop, typeof(PreLateUpdate)) != 1 ||
                CountType(in loop, typeof(BeginSimulationMarker)) != 0 ||
                CountType(in loop, typeof(BeginRenderMarker)) != 0) return false;
            Insert(ref loop, typeof(EarlyUpdate), new PlayerLoopSystem {
                type = typeof(BeginSimulationMarker), updateDelegate = beginSimulation }, true);
            Insert(ref loop, typeof(PreLateUpdate), new PlayerLoopSystem {
                type = typeof(BeginRenderMarker), updateDelegate = beginRender }, false);
            return true;
        }

        private static void Insert(ref PlayerLoopSystem node, Type parent, PlayerLoopSystem marker, bool first)
        {
            if (node.type == parent)
            {
                var children = node.subSystemList ?? Array.Empty<PlayerLoopSystem>();
                var result = new PlayerLoopSystem[children.Length + 1];
                Array.Copy(children, 0, result, first ? 1 : 0, children.Length);
                result[first ? 0 : children.Length] = marker;
                node.subSystemList = result;
                return;
            }
            if (node.subSystemList == null) return;
            // Clone before editing: a rejected/foreign loop snapshot must not be
            // changed through shared child arrays.
            node.subSystemList = (PlayerLoopSystem[])node.subSystemList.Clone();
            for (int i = 0; i < node.subSystemList.Length; i++) Insert(ref node.subSystemList[i], parent, marker, first);
        }

        internal static void RemoveOwned(ref PlayerLoopSystem node,
            PlayerLoopSystem.UpdateFunction beginSimulation, PlayerLoopSystem.UpdateFunction beginRender)
        {
            if (node.subSystemList == null) return;
            var kept = new List<PlayerLoopSystem>(node.subSystemList.Length);
            foreach (var child in node.subSystemList)
            {
                if ((child.type == typeof(BeginSimulationMarker) && child.updateDelegate == beginSimulation) ||
                    (child.type == typeof(BeginRenderMarker) && child.updateDelegate == beginRender)) continue;
                var copy = child;
                RemoveOwned(ref copy, beginSimulation, beginRender);
                kept.Add(copy);
            }
            node.subSystemList = kept.ToArray();
        }

        private static int CountType(in PlayerLoopSystem node, Type type)
        {
            int count = node.type == type ? 1 : 0;
            if (node.subSystemList != null)
                foreach (var child in node.subSystemList) count += CountType(in child, type);
            return count;
        }
        private static int CountDelegate(in PlayerLoopSystem node, PlayerLoopSystem.UpdateFunction callback)
        {
            int count = node.updateDelegate == callback ? 1 : 0;
            if (node.subSystemList != null)
                foreach (var child in node.subSystemList) count += CountDelegate(in child, callback);
            return count;
        }
    }
}
