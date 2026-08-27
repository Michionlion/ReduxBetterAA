using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Backends
{
    internal sealed class DisabledBackend : ITemporalBackend
    {
        public string Id => "Off";
        public bool Active => false;

        public bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason)
        {
            unsupportedReason = string.Empty;
            return true;
        }

        public bool Configure(TemporalCameraSet cameras, out string failureReason)
        {
            failureReason = string.Empty;
            return true;
        }

        public void Tick(uint frameIndex)
        {
        }

        public void ResetHistory(HistoryResetReason reason)
        {
        }

        public void Deactivate()
        {
        }

        public void Dispose()
        {
        }
    }
}
