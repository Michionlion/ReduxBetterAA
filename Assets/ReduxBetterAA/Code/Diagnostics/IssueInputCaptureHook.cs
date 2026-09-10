using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    [DefaultExecutionOrder(9999)]
    internal sealed class IssueInputCaptureHook : MonoBehaviour
    {
        internal IssueReportCapture Owner;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            try { Owner?.CaptureInput(source); }
            finally { Graphics.Blit(source, destination); }
        }
    }
}
