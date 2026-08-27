using UnityEngine;

namespace ReduxBetterAA.Rendering
{
    internal static class SharedJitterSequence
    {
        public static Vector2 GetPpv2Offset(int sampleIndex, float jitterSpread)
        {
            int sample = (sampleIndex & 1023) + 1;
            return new Vector2(
                Halton(sample, 2) - 0.5f,
                Halton(sample, 3) - 0.5f
            ) * jitterSpread;
        }

        public static Vector2 GetCustomOffset(
            uint frameIndex,
            float jitterSpread,
            int sequenceLength)
        {
            int length = Mathf.Clamp(sequenceLength, 4, 32);
            int sampleIndex = (int)(frameIndex % (uint)length);
            int sample = sampleIndex + 1;
            return new Vector2(
                Halton(sample, 2) - 0.5f,
                Halton(sample, 3) - 0.5f
            ) * jitterSpread;
        }

        private static float Halton(int index, int radix)
        {
            float result = 0.0f;
            float fraction = 1.0f / radix;
            while (index > 0)
            {
                result += (index % radix) * fraction;
                index /= radix;
                fraction /= radix;
            }
            return result;
        }
    }
}
