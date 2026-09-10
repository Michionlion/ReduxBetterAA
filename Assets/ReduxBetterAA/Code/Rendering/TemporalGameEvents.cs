using System;
using KSP.Game;
using KSP.Messages;

namespace ReduxBetterAA.Rendering
{
    // Exact game messages cover same-scene discontinuities that camera heuristics miss.
    internal sealed class TemporalGameEvents : IDisposable
    {
        private readonly Action<HistoryResetReason> _reset;
        private MessageCenter _messages;
        private SubscriptionHandle _loaded, _reverted, _vesselChanged;

        public TemporalGameEvents(Action<HistoryResetReason> reset) { _reset = reset; }

        public void Refresh()
        {
            MessageCenter current = GameManager.Instance?.Game?.Messages;
            if (ReferenceEquals(current, _messages))
                return;
            Dispose();
            if (current == null)
                return;
            _messages = current;
            try
            {
                _loaded = current.Subscribe<GameLoadFinishedMessage>(OnLoaded);
                _reverted = current.Subscribe<VehicleRevertCompleteMessage>(OnReverted);
                _vesselChanged = current.Subscribe<VesselChangedMessage>(OnVesselChanged);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void OnLoaded(MessageCenterMessage message)
        {
            if (((GameLoadFinishedMessage)message).IsSuccess)
                _reset(HistoryResetReason.QuickloadOrRevert);
        }
        private void OnReverted(MessageCenterMessage message) => _reset(HistoryResetReason.QuickloadOrRevert);
        private void OnVesselChanged(MessageCenterMessage message) => _reset(HistoryResetReason.VesselChanged);

        public void Dispose()
        {
            _loaded.Release();
            _reverted.Release();
            _vesselChanged.Release();
            _loaded = _reverted = _vesselChanged = default;
            _messages = null;
        }
    }
}
