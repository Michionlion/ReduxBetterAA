using System.Collections.Generic;
using KSP.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReduxBetterAA.Rendering
{
    // Map icons are scene sprites, not overlay canvases. Remove only their
    // normal draw and replay the original material after AA, with map depth.
    [DefaultExecutionOrder(20000)]
    internal sealed class MapIconOverlay : MonoBehaviour
    {
        internal static MapIconOverlay Current { get; private set; }
        private Camera _camera;
        private CommandBuffer _commands;
        private readonly Plane[] _frustum = new Plane[6];
        private readonly List<SpriteRenderer> _sprites = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _claimed = new List<SpriteRenderer>();
        internal int RegisteredCount => _sprites.Count;

        internal static MapIconOverlay Attach(Camera camera)
        {
            var overlay = camera.gameObject.AddComponent<MapIconOverlay>();
            overlay.hideFlags = HideFlags.HideAndDontSave;
            overlay._camera = camera;
            overlay._commands = new CommandBuffer { name = "Better AA map icons (after AA)" };
            camera.AddCommandBuffer(CameraEvent.AfterImageEffects, overlay._commands);
            Current = overlay;
            // Once per graph/backend acquisition; new icons register at Configure.
            var icons = Resources.FindObjectsOfTypeAll<Map3DFocusItemIcon>();
            for (int i = 0; i < icons.Length; i++)
                overlay.Register(icons[i].GetComponent<SpriteRenderer>());
            return overlay;
        }

        internal void Register(SpriteRenderer sprite)
        {
            if (sprite == null || sprite.gameObject.layer != 27 ||
                sprite.sharedMaterial == null ||
                sprite.sharedMaterial.shader.name != "KSP2/UI/Sprite (Depth Offset)" ||
                _sprites.Contains(sprite)) return;
            _sprites.Add(sprite);
            if (_claimed.Capacity < _sprites.Count) _claimed.Capacity = _sprites.Count;
        }

        private void OnPreCull()
        {
            if (_commands == null) return;
            Restore();
            _commands.Clear();
            GeometryUtility.CalculateFrustumPlanes(_camera.nonJitteredProjectionMatrix * _camera.worldToCameraMatrix, _frustum);
            _commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget, BuiltinRenderTextureType.Depth);
            _commands.SetViewProjectionMatrices(_camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(_camera.nonJitteredProjectionMatrix, _camera.targetTexture != null));
            for (int i = _sprites.Count - 1; i >= 0; i--)
                if (_sprites[i] == null) _sprites.RemoveAt(i);
            // Small, allocation-free stable sort using Unity's sprite priorities.
            for (int i = 1; i < _sprites.Count; i++)
            {
                var sprite = _sprites[i];
                int j = i - 1;
                while (j >= 0 && Compare(_sprites[j], sprite) > 0)
                { _sprites[j + 1] = _sprites[j]; j--; }
                _sprites[j + 1] = sprite;
            }
            for (int i = 0; i < _sprites.Count; i++)
            {
                var sprite = _sprites[i];
                if (!sprite.enabled || !sprite.gameObject.activeInHierarchy || sprite.forceRenderingOff ||
                    !GeometryUtility.TestPlanesAABB(_frustum, sprite.bounds) ||
                    (_camera.cullingMask & (1 << sprite.gameObject.layer)) == 0 || sprite.sharedMaterial == null) continue;
                sprite.forceRenderingOff = true;
                _claimed.Add(sprite);
                _commands.DrawRenderer(sprite, sprite.sharedMaterial);
            }
            _commands.SetViewProjectionMatrices(_camera.worldToCameraMatrix,
                GL.GetGPUProjectionMatrix(_camera.projectionMatrix, _camera.targetTexture != null));
        }

        private int Compare(SpriteRenderer a, SpriteRenderer b)
        {
            int order = SortingLayer.GetLayerValueFromID(a.sortingLayerID).CompareTo(SortingLayer.GetLayerValueFromID(b.sortingLayerID));
            if (order != 0) return order;
            order = a.sortingOrder.CompareTo(b.sortingOrder);
            if (order != 0) return order;
            // Map uses perspective distance sorting. Preserve equal-key order.
            Vector3 position = _camera.transform.position;
            return (b.bounds.center - position).sqrMagnitude.CompareTo((a.bounds.center - position).sqrMagnitude);
        }

        private void OnPostRender() { Restore(); }
        private void Restore()
        {
            for (int i = 0; i < _claimed.Count; i++)
                if (_claimed[i] != null && _claimed[i].forceRenderingOff) _claimed[i].forceRenderingOff = false;
            _claimed.Clear();
        }

        internal void Shutdown()
        {
            Restore();
            if (_commands != null)
            {
                if (_camera != null) _camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, _commands);
                _commands.Release();
                _commands = null;
            }
            if (Current == this) Current = null;
            _sprites.Clear();
        }
        private void OnDisable() { Shutdown(); }
        private void OnDestroy() { Shutdown(); }
        internal static void Detach(ref MapIconOverlay overlay)
        {
            if (overlay != null) { overlay.Shutdown(); overlay.enabled = false; Destroy(overlay); }
            overlay = null;
        }
    }
}
