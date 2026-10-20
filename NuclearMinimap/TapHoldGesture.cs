using System;

namespace NuclearMinimap
{
    // One physical press: decide at release, or cross the hold threshold once.
    internal sealed class TapHoldGesture
    {
        private bool pressed, cancelled;
        private float started;
        internal bool Holding { get; private set; }

        internal bool Update(bool down, float now, float threshold)
        {
            if (float.IsNaN(now) || float.IsInfinity(now)) { Cancel(); return false; }
            threshold = float.IsNaN(threshold) || float.IsInfinity(threshold) ? .3f : Math.Max(.1f, Math.Min(1f, threshold));
            if (cancelled)
            {
                if (!down) cancelled = false;
                return false;
            }
            if (down && !pressed) { pressed = true; started = now; Holding = false; }
            if (!pressed) return false;
            float elapsed = now - started;
            if (elapsed < 0) { Cancel(); return false; }
            if (elapsed >= threshold) Holding = true;
            if (down) return false;
            bool tap = !Holding;
            pressed = false; Holding = false;
            return tap;
        }

        // Mode/focus/config changes discard a press; require release before rearming.
        internal void Cancel() { pressed = false; Holding = false; cancelled = true; }
    }
}
