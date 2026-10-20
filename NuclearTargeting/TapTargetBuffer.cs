using System;
using System.Collections.Generic;

namespace NuclearTargeting
{
    internal static class TapTargetBuffer
    {
        // Selection order is newest first. Planning does not mutate the live selection.
        internal static List<T> Plan<T>(IReadOnlyList<T> newestFirst, T target, int capacity)
        {
            int limit = Math.Max(1, capacity);
            var desired = new List<T> { target };
            var seen = new HashSet<T>(EqualityComparer<T>.Default) { target };
            for (int i = 0; i < newestFirst.Count && desired.Count < limit; i++)
            {
                T existing = newestFirst[i];
                if (seen.Add(existing)) desired.Add(existing);
            }
            return desired;
        }
    }
}
