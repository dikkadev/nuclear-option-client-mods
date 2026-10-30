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

    public static class SpectatorCycle
    {
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
