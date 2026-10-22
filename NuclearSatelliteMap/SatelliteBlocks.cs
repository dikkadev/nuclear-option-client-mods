using System;

namespace NuclearSatelliteMap
{
    internal readonly struct ColorAdjustment : IEquatable<ColorAdjustment>
    {
        internal readonly float Brightness, Contrast, Saturation, Gamma, TintR, TintG, TintB, WaterOpacity, LandOpacity;

        internal ColorAdjustment(float brightness, float contrast, float saturation, float gamma,
            float tintR, float tintG, float tintB, float waterOpacity, float landOpacity)
        {
            Brightness = brightness; Contrast = contrast; Saturation = saturation; Gamma = gamma;
            TintR = tintR; TintG = tintG; TintB = tintB; WaterOpacity = waterOpacity; LandOpacity = landOpacity;
        }

        internal static ColorAdjustment Identity => new ColorAdjustment(1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, 1f);

        public bool Equals(ColorAdjustment other) =>
            Brightness == other.Brightness && Contrast == other.Contrast && Saturation == other.Saturation &&
            Gamma == other.Gamma && TintR == other.TintR && TintG == other.TintG && TintB == other.TintB &&
            WaterOpacity == other.WaterOpacity && LandOpacity == other.LandOpacity;
        public override bool Equals(object? obj) => obj is ColorAdjustment other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Brightness, Contrast, Saturation, Gamma), TintR, TintG, TintB, WaterOpacity, LandOpacity);
    }

    // BC3 (DXT5) block adjustment. Each 4x4 block interpolates linearly between two endpoints,
    // so remapping only the endpoints through lookup tables applies a per-pixel color/alpha
    // adjustment without decoding or re-encoding the texture. Affine adjustments are exact up to
    // endpoint quantization; gamma and clamping are applied at the endpoints as an approximation.
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

        internal static float AdjustChannel(float value, float gamma, float contrast, float brightness)
        {
            value = (float)Math.Pow(Math.Max(0f, value), 1f / gamma);
            value = (value - .5f) * contrast + .5f;
            return value * brightness;
        }

        // Order: gamma, contrast, brightness, saturation, then tint, so a tint also colors grayscale.
        internal static void AdjustColor(in ColorAdjustment a, ref float r, ref float g, ref float b)
        {
            r = AdjustChannel(r, a.Gamma, a.Contrast, a.Brightness);
            g = AdjustChannel(g, a.Gamma, a.Contrast, a.Brightness);
            b = AdjustChannel(b, a.Gamma, a.Contrast, a.Brightness);
            float luma = .2126f * r + .7152f * g + .0722f * b;
            r = Clamp01((luma + (r - luma) * a.Saturation) * a.TintR);
            g = Clamp01((luma + (g - luma) * a.Saturation) * a.TintG);
            b = Clamp01((luma + (b - luma) * a.Saturation) * a.TintB);
        }

        internal static ushort[] BuildColorLut(in ColorAdjustment a)
        {
            var lut = new ushort[65536];
            for (int value = 0; value < lut.Length; value++)
            {
                int r5 = value >> 11, g6 = (value >> 5) & 63, b5 = value & 31;
                float r = ((r5 << 3) | (r5 >> 2)) / 255f, g = ((g6 << 2) | (g6 >> 4)) / 255f, b = ((b5 << 3) | (b5 >> 2)) / 255f;
                AdjustColor(a, ref r, ref g, ref b);
                lut[value] = (ushort)((int)Math.Round(r * 31f) << 11 | (int)Math.Round(g * 63f) << 5 | (int)Math.Round(b * 31f));
            }
            return lut;
        }

        // Source alpha is land coverage: 0 is water, 255 is land.
        internal static byte[] BuildAlphaLut(float waterOpacity, float landOpacity)
        {
            var lut = new byte[256];
            for (int value = 0; value < lut.Length; value++)
                lut[value] = (byte)Math.Round(255f * Clamp01(waterOpacity + (landOpacity - waterOpacity) * value / 255f));
            return lut;
        }

        internal static bool IsIdentity(ushort[] colorLut, byte[] alphaLut)
        {
            for (int i = 0; i < colorLut.Length; i++) if (colorLut[i] != i) return false;
            for (int i = 0; i < alphaLut.Length; i++) if (alphaLut[i] != i) return false;
            return true;
        }

        internal static unsafe void Apply(Span<byte> data, ushort[] colorLut, byte[] alphaLut)
        {
            if (data.Length % BlockBytes != 0) throw new ArgumentException("BC3 data must contain whole blocks.");
            fixed (byte* start = data)
            fixed (ushort* colors = colorLut)
            fixed (byte* alphas = alphaLut)
            {
                byte* end = start + data.Length;
                for (byte* block = start; block < end; block += BlockBytes) ApplyBlock(block, colors, alphas);
            }
        }

        // Callers rely on the generator's canonical blocks: 8-value alpha mode (a0 > a1) or a constant
        // block with zero indices, and 4-color mode (c0 > c1) or a constant block with zero indices.
        private static unsafe void ApplyBlock(byte* block, ushort* colors, byte* alphas)
        {
            byte a0 = alphas[block[0]], a1 = alphas[block[1]];
            if (a0 == a1)
            {
                for (int i = 2; i < 8; i++) block[i] = 0;
            }
            else if (a0 < a1)
            {
                (a0, a1) = (a1, a0);
                ulong bits = 0;
                for (int i = 0; i < 6; i++) bits |= (ulong)block[2 + i] << (8 * i);
                ulong swapped = 0;
                for (int pixel = 0; pixel < 16; pixel++)
                {
                    ulong index = (bits >> (3 * pixel)) & 7;
                    // 8-value mode: 0 and 1 are endpoints, 2..7 interpolate from a0 to a1.
                    index = index < 2 ? index ^ 1 : 9 - index;
                    swapped |= index << (3 * pixel);
                }
                for (int i = 0; i < 6; i++) block[2 + i] = (byte)(swapped >> (8 * i));
            }
            block[0] = a0; block[1] = a1;

            ushort c0 = colors[block[8] | block[9] << 8], c1 = colors[block[10] | block[11] << 8];
            if (c0 == c1)
            {
                block[12] = block[13] = block[14] = block[15] = 0;
            }
            else if (c0 < c1)
            {
                (c0, c1) = (c1, c0);
                // 4-color mode: swapping endpoints maps indices 0<->1 and 2<->3.
                for (int i = 12; i < 16; i++) block[i] ^= 0x55;
            }
            block[8] = (byte)c0; block[9] = (byte)(c0 >> 8);
            block[10] = (byte)c1; block[11] = (byte)(c1 >> 8);
        }

        private static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;
    }
}
