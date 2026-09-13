using UnityEngine;

namespace ReduxBetterAA.Configuration
{
    // Native is reconstruction at display size; quality modes render fewer pixels.
    // Values are our persisted policy, never cast directly to vendor API enums.
    internal enum ReconstructionQuality { Native = 0, Quality = 1, Balanced = 2, Performance = 3 }

    internal static class ReconstructionPolicy
    {
        internal static ReconstructionQuality Normalize(ReconstructionQuality quality) =>
            quality >= ReconstructionQuality.Native && quality <= ReconstructionQuality.Performance
                ? quality : ReconstructionQuality.Quality;

        internal static int RenderPercent(ReconstructionQuality quality)
        {
            switch (Normalize(quality))
            {
                case ReconstructionQuality.Quality: return 67;
                case ReconstructionQuality.Balanced: return 59;
                case ReconstructionQuality.Performance: return 50;
                default: return 100;
            }
        }

        internal static ReconstructionQuality Parse(string value)
        {
            switch (value)
            {
                case "Balanced": return ReconstructionQuality.Balanced;
                case "Performance": return ReconstructionQuality.Performance;
                case "Native": return ReconstructionQuality.Native;
                default: return ReconstructionQuality.Quality;
            }
        }

        internal static int JitterPhaseCount(int renderWidth, int outputWidth) =>
            renderWidth <= 0 || outputWidth < renderWidth ? 8 :
            Mathf.Max(8, (int)(8.0f * outputWidth * outputWidth / renderWidth / renderWidth));
    }
}
