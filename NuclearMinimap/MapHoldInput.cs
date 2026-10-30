using NuclearBindings;

namespace NuclearMinimap
{
    internal static class MapHoldInput
    {
        // The shared binding context plus any open game UI except the map itself.
        internal static bool IsBlocked() =>
            InputContext.Blocked(false) || (CursorManager.GetFlags() & ~CursorFlags.Map) != CursorFlags.None;

        internal static bool ConfigurationWindowOpen() => InputContext.ConfigurationWindowOpen();
    }
}
