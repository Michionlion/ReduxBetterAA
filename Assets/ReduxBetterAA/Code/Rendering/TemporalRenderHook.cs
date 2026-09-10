using System;
using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    internal interface ISceneResolve
    {
        bool Active { get; }
        void Render(RenderTexture source, RenderTexture destination);
    }

    // Installed at the same component position as the former backend-specific hooks.
    // DefaultExecutionOrder does not order OnRenderImage; observe input here instead.
    [DefaultExecutionOrder(10000)]
    internal sealed class TemporalRenderHook : MonoBehaviour
    {
        internal ISceneResolve Owner;
        internal Action<RenderTexture> CaptureInput;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            Render(source, destination);
        }

        internal void Render(RenderTexture source, RenderTexture destination)
        {
            // A one-shot observer cannot prevent the scene from rendering.
            Action<RenderTexture> capture = CaptureInput;
            CaptureInput = null;
            try { capture?.Invoke(source); }
            catch (Exception exception) { Debug.LogException(exception); }

            if (Owner != null && Owner.Active)
                Owner.Render(source, destination);
            else
                Graphics.Blit(source, destination);
        }

        internal static TemporalRenderHook Attach(Camera camera, ISceneResolve owner)
        {
            var hook = camera.gameObject.AddComponent<TemporalRenderHook>();
            hook.hideFlags = HideFlags.HideAndDontSave;
            hook.Owner = owner;
            return hook;
        }

        internal static void Detach(ref TemporalRenderHook hook)
        {
            if (hook != null)
            {
                hook.enabled = false;
                hook.Owner = null;
                hook.CaptureInput = null;
                Destroy(hook);
            }
            hook = null;
        }
    }
}
