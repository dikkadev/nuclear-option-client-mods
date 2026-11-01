using System;

namespace NuclearSpectator
{
    // Engine-independent ordering for aircraft cycling; linked into the logic checks.
    public readonly struct CycleKey
    {
        public CycleKey(bool isPlayer, string name, int id)
        {
            IsPlayer = isPlayer;
            Name = name ?? string.Empty;
            Id = id;
        }

        public bool IsPlayer { get; }
        public string Name { get; }
        public int Id { get; }
    }

    // The view of the aircraft followed so far; None when no live aircraft was followed.
    public enum FollowedView { None, Outside, Cockpit }

    // Hide All UI steps, in toggle order.
    public enum HideLevel { None, GameUi, All }

    public static class SpectatorCycle
    {
        // Whether a newly followed aircraft starts in its cockpit view. Coming from another
        // aircraft keeps that choice; coming from nothing uses the default.
        public static bool StartInCockpit(FollowedView previous, bool cockpitByDefault, bool keepCockpitView)
        {
            switch (previous)
            {
                case FollowedView.Cockpit: return keepCockpitView;
                case FollowedView.Outside: return false;
                default: return cockpitByDefault;
            }
        }

        public static HideLevel NextHideLevel(HideLevel level)
            => level == HideLevel.None ? HideLevel.GameUi : level == HideLevel.GameUi ? HideLevel.All : HideLevel.None;

        // Cockpit movement speed in metres per second. Stick deflection (0..1) follows a power
        // curve, so small deflections move slowly and only full stick reaches the top speed.
        // The fast ramp (0..1) eases in the fast multiplier.
        public static float MoveSpeed(float deflection, float maxSpeed, float curve, float fastMultiplier, float fastRamp)
        {
            double stick = Math.Max(0f, Math.Min(1f, deflection));
            double ramp = Math.Max(0f, Math.Min(1f, fastRamp));
            ramp = ramp * ramp * (3 - 2 * ramp);
            double fast = 1 + (Math.Max(1f, fastMultiplier) - 1) * ramp;
            return (float)(maxSpeed * Math.Pow(stick, Math.Max(1f, curve)) * fast);
        }

        // Players first, then by name ignoring case; the id keeps equal names stable.
        public static int Compare(CycleKey a, CycleKey b)
        {
            if (a.IsPlayer != b.IsPlayer) return a.IsPlayer ? -1 : 1;
            int byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0) return byName;
            byName = string.CompareOrdinal(a.Name, b.Name);
            return byName != 0 ? byName : a.Id.CompareTo(b.Id);
        }

        // Index to follow next in a sorted list of `count` aircraft, wrapping at both ends.
        // `current` is the followed aircraft's index, or -1 when it is not in the list.
        public static int Step(int count, int current, int direction)
        {
            if (count <= 0) return -1;
            int step = direction < 0 ? -1 : 1;
            if (current < 0 || current >= count) return step > 0 ? 0 : count - 1;
            return ((current + step) % count + count) % count;
        }
    }
}
