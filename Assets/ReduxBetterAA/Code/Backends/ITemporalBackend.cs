using System;
using ReduxBetterAA.Configuration;
using ReduxBetterAA.Rendering;

namespace ReduxBetterAA.Backends
{
    internal interface ITemporalBackend : IDisposable
    {
        string Id { get; }
        bool Active { get; }
        bool ProbeSupport(TemporalCameraSet cameras, out string unsupportedReason);
        bool Configure(TemporalCameraSet cameras, out string failureReason);
        void Tick(uint frameIndex);
        void ResetHistory(HistoryResetReason reason);
        void Deactivate();
    }
}
