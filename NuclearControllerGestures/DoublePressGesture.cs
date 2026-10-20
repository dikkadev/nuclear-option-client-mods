using System;

namespace NuclearControllerGestures
{
    // One instance belongs to one physical control. The caller cancels on context changes.
    internal sealed class DoublePressGesture
    {
        private bool requireNeutral = true;
        private bool held;
        private bool pending;
        private bool secondPress;
        private bool hasTime;
        private double lastTime;
        private double downTime;
        private double firstRelease;
        private double cooldownUntil = double.NegativeInfinity;

        internal void Cancel()
        {
            requireNeutral = true;
            held = pending = secondPress = false;
        }

        internal bool Update(bool isHeld, double unscaledTime, double window, double maxPress, double cooldown)
        {
            if (!Finite(unscaledTime) || (hasTime && unscaledTime < lastTime) ||
                !Finite(window) || window <= 0 || !Finite(maxPress) || maxPress <= 0 ||
                !Finite(cooldown) || cooldown < 0)
            {
                Cancel();
                hasTime = false;
                cooldownUntil = double.NegativeInfinity;
                return false;
            }
            lastTime = unscaledTime;
            hasTime = true;

            // Even a release during cooldown is insufficient: observe neutral after it ends.
            if (unscaledTime < cooldownUntil)
            {
                Cancel();
                return false;
            }
            if (requireNeutral)
            {
                if (!isHeld) requireNeutral = false;
                return false;
            }

            if (!held && pending && unscaledTime - firstRelease > window) pending = false;
            if (isHeld)
            {
                if (!held)
                {
                    held = true;
                    downTime = unscaledTime;
                    secondPress = pending;
                }
                else if (unscaledTime - downTime > maxPress) Cancel();
                return false;
            }
            if (!held) return false;

            held = false;
            if (unscaledTime - downTime > maxPress)
            {
                pending = secondPress = false;
                return false;
            }
            if (secondPress)
            {
                Cancel();
                cooldownUntil = unscaledTime + cooldown;
                return true;
            }
            pending = true;
            firstRelease = unscaledTime;
            return false;
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
