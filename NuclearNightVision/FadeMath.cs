using System;

namespace NuclearNightVision
{
    // Pure fade timing, kept free of Unity types so the logic checks can run it.
    internal static class FadeMath
    {
        // Moves the night vision amount (0 normal view, 1 night vision) towards the target.
        // A full fade takes the configured seconds; a reversal mid-fade starts from the current amount.
        internal static float Step(float amount, bool nightVision, float deltaSeconds, float fadeInSeconds, float fadeOutSeconds)
        {
            float target = nightVision ? 1f : 0f;
            float seconds = nightVision ? fadeInSeconds : fadeOutSeconds;
            if (seconds <= 0f || float.IsNaN(seconds)) return target;
            float step = Math.Max(0f, deltaSeconds) / seconds;
            return nightVision ? Math.Min(target, amount + step) : Math.Max(target, amount - step);
        }

        // Smoothstep: starts and ends gently, so neither view snaps in or out.
        internal static float Ease(float amount)
        {
            float t = Math.Min(1f, Math.Max(0f, amount));
            return t * t * (3f - 2f * t);
        }
    }
}
