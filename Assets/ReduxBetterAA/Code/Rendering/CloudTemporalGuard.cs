namespace ReduxBetterAA.Rendering
{
    // Observes stock cloud transitions; never owns or changes cloud history.
    internal sealed class CloudTemporalGuard
    {
        internal const int SettleFrameCount = 120;
        private readonly bool _enableSuspension;

        internal CloudTemporalGuard(bool enableSuspension)
        {
            _enableSuspension = enableSuspension;
        }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public uint ResizeCount { get; private set; }
        public int SettleFramesRemaining { get; private set; }
        public bool BypassActive { get; private set; }

        // True only at a bypass boundary, when DLAA must reset its own history.
        public bool Observe(int width, int height, bool temporalUpscaling,
            int cameraWidth, int cameraHeight)
        {
            if (width <= 0 || height <= 0)
                return false;
            bool resized = Width > 0 && Height > 0 && (Width != width || Height != height);
            if (resized)
                ResizeCount++;
            if (!_enableSuspension)
            {
                // Keep diagnostics observable without suspending DLAA or jitter.
                Width = width;
                Height = height;
                return false;
            }
            bool quarterResolution = width <= cameraWidth / 4 && height <= cameraHeight / 4;
            if (temporalUpscaling)
                SettleFramesRemaining = SettleFrameCount;
            else if (!quarterResolution)
                SettleFramesRemaining = 0;
            else if (resized || (Width == 0 && Height == 0))
                SettleFramesRemaining = SettleFrameCount;
            else if (SettleFramesRemaining > 0)
                SettleFramesRemaining--;

            bool bypass = temporalUpscaling || (quarterResolution && SettleFramesRemaining > 0);
            bool changed = bypass != BypassActive;
            BypassActive = bypass;
            Width = width;
            Height = height;
            return changed;
        }

        public void Clear()
        {
            Width = Height = SettleFramesRemaining = 0;
            ResizeCount = 0;
            BypassActive = false;
        }
    }
}
