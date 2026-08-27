using UnityEngine;

namespace ReduxBetterAA.Backends
{
    [DefaultExecutionOrder(10000)]
    internal sealed class NvidiaDlaaRenderHook : MonoBehaviour
    {
        public NvidiaDlaaBackend Owner;

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (Owner != null && Owner.Active)
            {
                Owner.Render(source, destination);
                return;
            }
            Graphics.Blit(source, destination);
        }
    }
}
