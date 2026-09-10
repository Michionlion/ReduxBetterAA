using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Backends
{
    // The coordinator owns Redux's scale; this backend owns the unfiltered scene.
    internal sealed class SupersamplingBackend : ITemporalBackend
    {
        private readonly DisabledBackend _scene = new DisabledBackend();
        public string Id => "Supersampling";
        public bool Active { get; private set; }
        public bool ProbeSupport(TemporalCameraSet cameras, out string reason)
        {
            reason = cameras == null || cameras.SceneKind == TemporalSceneKind.Unsupported ||
                cameras.ResolveCamera == null || cameras.ResolveLayer == null
                ? "No supported scene output is available" : string.Empty;
            if (reason.Length == 0 && cameras.RenderScalePercent <= 100)
                reason = "Redux has no supersampled scene presenter";
            if (reason.Length == 0 && (cameras.ResolveCamera.targetTexture == null ||
                !cameras.ResolveCamera.targetTexture.IsCreated()))
                reason = "Redux did not create the supersampled scene target";
            return reason.Length == 0;
        }
        public bool Configure(TemporalCameraSet cameras, out string reason)
        {
            Deactivate();
            Active = ProbeSupport(cameras, out reason) && _scene.Configure(cameras, out reason);
            return Active;
        }
        public void Tick(uint frameIndex) { }
        public void ResetHistory(HistoryResetReason reason) { }
        public void Deactivate() { _scene.Deactivate(); Active = false; }
        public void Dispose() => Deactivate();
    }
}
