using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Backends
{
    // Off suppresses scene AA, but allocates no temporal targets and adds no depth flags.
    internal sealed class DisabledBackend : ITemporalBackend
    {
        private SceneCameraState _resolve, _shared;
        public string Id => "Off";
        public bool Active => false;
        public bool ProbeSupport(TemporalCameraSet cameras, out string reason)
        {
            reason = string.Empty;
            return true;
        }
        public bool Configure(TemporalCameraSet cameras, out string reason)
        {
            Deactivate();
            reason = string.Empty;
            if (cameras == null) return true;
            if (cameras.SceneKind != TemporalSceneKind.Unsupported &&
                cameras.ResolveLayer == null && cameras.SharedJitterLayer == null)
            {
                reason = "The supported scene has not exposed a PostProcessLayer yet";
                return false;
            }
            _resolve.Capture(null, cameras.ResolveLayer, requestDepth: false);
            if (cameras.SharedJitterLayer != cameras.ResolveLayer)
                _shared.Capture(null, cameras.SharedJitterLayer, requestDepth: false);
            return true;
        }
        public void Tick(uint frameIndex) { }
        public void ResetHistory(HistoryResetReason reason) { }
        public void Deactivate() { _shared.Restore(); _resolve.Restore(); }
        public void Dispose() => Deactivate();
    }
}
