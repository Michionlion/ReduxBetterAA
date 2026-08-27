using UnityEngine;

namespace ReduxBetterAA.Backends
{
    [DefaultExecutionOrder(10000)]
    internal sealed class AmdFsr2RenderHook : MonoBehaviour
    {
        public AmdFsr2Backend Owner;

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
