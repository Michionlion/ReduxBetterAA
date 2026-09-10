using System;
using System.Collections.Generic;
using System.Reflection;
using KSP.Rendering;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    // Redux retains target allocation, presentation and pointer-coordinate handling.
    // We only own its requested scale. Never save over the stock graphics profile.
    internal sealed class RenderScaleOwnership : IDisposable
    {
        private static readonly FieldInfo Scale = typeof(RenderScalePresenter).GetField(
            "_renderScalePercent", BindingFlags.Instance | BindingFlags.NonPublic);
        private struct Claim { internal int Original, Applied; internal bool External; }
        private readonly Dictionary<RenderScalePresenter, Claim> _claims =
            new Dictionary<RenderScalePresenter, Claim>();
        internal static bool Applying { get; private set; }
        internal int AppliedPercent { get; private set; } = 100;
        internal bool Conflict { get; private set; }

        internal bool Reclaim()
        {
            bool hadConflict = Conflict;
            foreach (var presenter in new List<RenderScalePresenter>(_claims.Keys))
            {
                Claim claim = _claims[presenter];
                if (!claim.External) continue;
                if (presenter != null) claim.Original = claim.Applied = (int)Scale.GetValue(presenter);
                claim.External = false;
                _claims[presenter] = claim;
            }
            Conflict = false;
            return hadConflict;
        }

        internal static int ClampPercent(int requested, int width, int height, int textureLimit)
        {
            int limit = Mathf.Min(200, textureLimit * 100 / Mathf.Max(1, Mathf.Max(width, height)));
            return Mathf.Clamp(requested, 100, Mathf.Max(100, limit));
        }

        // Called on mode/scene changes, never from the rendering hot path.
        internal bool Apply(int requested)
        {
            AppliedPercent = ClampPercent(requested, Screen.width, Screen.height, SystemInfo.maxTextureSize);
            if (Scale == null) return requested == 100;
            foreach (var presenter in new List<RenderScalePresenter>(_claims.Keys))
                if (presenter == null) _claims.Remove(presenter);
            Applying = true;
            try
            {
                foreach (var presenter in Resources.FindObjectsOfTypeAll<RenderScalePresenter>())
                {
                    if (presenter == null || !presenter.gameObject.scene.IsValid()) continue;
                    int current = (int)Scale.GetValue(presenter);
                    Claim claim;
                    if (!_claims.TryGetValue(presenter, out claim))
                        claim.Original = current;
                    else if (current != claim.Applied || claim.External)
                    {
                        claim.External = true;
                        _claims[presenter] = claim;
                        Conflict = true;
                        continue;
                    }
                    claim.Applied = AppliedPercent;
                    _claims[presenter] = claim;
                    if (current != AppliedPercent) presenter.SetRenderScalePercent(AppliedPercent);
                }
            }
            finally { Applying = false; }
            return !Conflict;
        }

        public void Dispose()
        {
            Applying = true;
            try
            {
                foreach (var entry in _claims)
                    if (!entry.Value.External && entry.Key != null && (int)Scale.GetValue(entry.Key) == entry.Value.Applied)
                        entry.Key.SetRenderScalePercent(entry.Value.Original);
                _claims.Clear();
            }
            finally { Applying = false; }
        }
    }
}
