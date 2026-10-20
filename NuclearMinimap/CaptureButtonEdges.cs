using System.Collections.Generic;

namespace NuclearMinimap
{
    // Observe during settling too, so an early or maintained press cannot become a capture.
    internal sealed class CaptureButtonEdges
    {
        private readonly HashSet<int> held = new HashSet<int>();
        internal bool Observe(int id, bool down, bool ready)
        {
            if (!down) { held.Remove(id); return false; }
            return held.Add(id) && ready;
        }
        internal void Clear() => held.Clear();
    }
}
