using System;

namespace NuclearMapPan
{
    // Pure speed math, linked into the logic checks without Unity.
    internal static class PanMath
    {
        // Factor for both native Move Map axis values this frame. The game moves the map by
        // axis * 300 units per second / zoom, linear in stick deflection.
        // Controller: the stick curve below, applied to the combined stick direction, so a
        // diagonal is not faster than a straight push. Keys are digital, so they get a plain multiplier.
        internal static float Scale(float horizontal, float vertical, bool controller,
            float crossAt, float gameSpeedAt, float topSpeed, float belly, float keyboardSpeed)
        {
            if (!controller) return keyboardSpeed;
            float magnitude = (float)Math.Sqrt(horizontal * horizontal + vertical * vertical);
            if (!(magnitude > 0f)) return 1f;
            float deflection = Math.Min(magnitude, 1f);
            return Speed(deflection, crossAt, gameSpeedAt, topSpeed, belly) / magnitude;
        }

        // Map speed (1 = the game's speed at full stick) for a stick deflection 0..1.
        // A monotone cubic through (0, 0), (crossAt, crossAt) on the game line,
        // (gameSpeedAt, 1) and (1, topSpeed). Belly sets the start slope: 1 starts flat,
        // 0 starts along the game line. The slopes keep the curve rising and never below zero.
        // Same curve as the user's Desmos graph "NuclearMapPan stick curve".
        internal static float Speed(float deflection, float crossAt, float gameSpeedAt, float topSpeed, float belly)
        {
            float c = Clamp(crossAt, 0.05f, 0.9f);
            float a = Clamp(gameSpeedAt, c + 0.05f, 0.95f);
            float top = Math.Max(topSpeed, 1.05f);
            float x = Clamp(deflection, 0f, 1f);

            float d1 = (1f - c) / (a - c);
            float d2 = (top - 1f) / (1f - a);
            float m0 = 1f - Clamp(belly, 0f, 1f);
            float m1 = 2f * d1 / (1f + d1);
            float m2 = 2f * d1 * d2 / (d1 + d2);
            float m3 = d2;

            if (x < c) return Hermite(x / c, 0f, c, m0, m1, c);
            if (x < a) return Hermite((x - c) / (a - c), c, 1f, m1, m2, a - c);
            return Hermite((x - a) / (1f - a), 1f, top, m2, m3, 1f - a);
        }

        private static float Hermite(float t, float y0, float y1, float m0, float m1, float width)
        {
            float t2 = t * t, t3 = t2 * t;
            return (2f * t3 - 3f * t2 + 1f) * y0 + (t3 - 2f * t2 + t) * width * m0
                + (3f * t2 - 2f * t3) * y1 + (t3 - t2) * width * m1;
        }

        private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
    }
}
