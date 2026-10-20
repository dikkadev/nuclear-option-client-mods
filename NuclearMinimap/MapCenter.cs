using System;

namespace NuclearMinimap
{
    internal static class MapCenter
    {
        // Cancel the rotated/scaled position of ownship in the minimized viewport.
        internal static (float x, float y) Offset(float mapX, float mapZ, float angle, float scale)
        {
            double radians = angle * Math.PI / 180d;
            double cosine = Math.Cos(radians), sine = Math.Sin(radians);
            return ((float)((-mapX * cosine + mapZ * sine) * scale),
                (float)((-mapX * sine - mapZ * cosine) * scale));
        }
    }
}
