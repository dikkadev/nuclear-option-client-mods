namespace NuclearMinimap
{
    public enum HudMinimapMode
    {
        AlwaysShow,
        AlwaysHide,
        HideWhenMfdVisible
    }

    internal static class MapPresentation
    {
        internal static bool ShouldHideHud(HudMinimapMode mode, bool featureEnabled, bool mapMaximized, bool mfdVisible)
        {
            if (!featureEnabled || mapMaximized) return false;
            return mode == HudMinimapMode.AlwaysHide ||
                (mode == HudMinimapMode.HideWhenMfdVisible && mfdVisible);
        }

        internal static bool MfdVisible(bool sourceReady, bool cockpitView, bool pageActive,
            bool targetFeed, bool landingFeed, bool mapMaximized)
        {
            return sourceReady && cockpitView && pageActive && !targetFeed && !landingFeed && !mapMaximized;
        }

        // Inputs are local rectangle coordinates, after removing the source transform.
        // Keeping the origin explicit supports asymmetric source/destination pivots.
        internal static float MapCoordinate(float value, float sourceMin, float sourceSize,
            float destinationMin, float destinationSize)
        {
            return destinationMin + (value - sourceMin) / sourceSize * destinationSize;
        }
    }
}
