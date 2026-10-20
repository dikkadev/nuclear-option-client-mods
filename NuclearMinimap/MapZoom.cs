using System;

namespace NuclearMinimap
{
    public enum MapOrientation
    {
        HeadingUp,
        NorthUp
    }

    internal static class MapZoom
    {
        internal const float Default = 2f;
        internal const float Minimum = .125f;
        internal const float Maximum = 64f;

        internal static float Step(float current, bool inHeld, bool outHeld,
            float delta, float doublingsPerSecond)
        {
            current = Finite(current) ? Clamp(current, Minimum, Maximum) : Default;
            if (inHeld == outHeld) return current;
            delta = Finite(delta) ? Clamp(delta, 0f, .05f) : 0f;
            float rate = Finite(doublingsPerSecond)
                ? Clamp(doublingsPerSecond, .1f, 5f) : 1f;
            float direction = inHeld ? 1f : -1f;
            return Clamp((float)(current * Math.Pow(2d, direction * rate * delta)), Minimum, Maximum);
        }

        internal static float Angle(float yaw, MapOrientation orientation)
        {
            return orientation == MapOrientation.NorthUp ? 0f : yaw;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Clamp(float value, float minimum, float maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
    }
}
