using UnityEngine;

namespace ReduxBetterAA.Diagnostics
{
    [DefaultExecutionOrder(10001)]
    internal sealed class IssueOutputCaptureHook : MonoBehaviour
    {
        internal IssueReportCapture Owner;
        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            try { Owner?.CaptureOutput(source); }
            finally { Graphics.Blit(source, destination); }
        }
    }
}
