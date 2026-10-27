using System;

namespace NuclearSatelliteMap
{
    // BC3 (DXT5) layout sizes for the .nosat payload.
    internal static class SatelliteBlocks
    {
        internal const int BlockBytes = 16;

        // Matches Unity's mip chain for Texture2D(width, height, DXT5, mipChain: true).
        internal static int FullMipCount(int width, int height)
        {
            int size = Math.Max(width, height), count = 1;
            while (size > 1) { size >>= 1; count++; }
            return count;
        }

        internal static long Bc3Size(int width, int height, int mipCount)
        {
            long total = 0;
            for (int level = 0; level < mipCount; level++)
            {
                long w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
                total += ((w + 3) / 4) * ((h + 3) / 4) * BlockBytes;
            }
            return total;
        }
    }
}
