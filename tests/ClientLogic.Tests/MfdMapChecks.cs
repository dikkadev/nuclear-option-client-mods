using NuclearMinimap;

internal static class MfdMapChecks
{
    internal static void Run(Action<string, Action> test)
    {
        void Equal<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception($"Expected '{expected}', got '{actual}'");
        }
        void Near(float expected, float actual)
        {
            if (!float.IsFinite(actual) || Math.Abs(expected - actual) > .001f)
                throw new Exception($"Expected {expected}, got {actual}");
        }

        test("MFD map HUD always-show preserves HUD with visible replacement", () => {
            Equal(false, MapPresentation.ShouldHideHud(HudMinimapMode.AlwaysShow, true, false, true));
            Equal(false, MapPresentation.ShouldHideHud(HudMinimapMode.AlwaysShow, true, false, false));
        });
        test("MFD map always-hide remains independent of cockpit availability", () => {
            Equal(true, MapPresentation.ShouldHideHud(HudMinimapMode.AlwaysHide, true, false, true));
            Equal(true, MapPresentation.ShouldHideHud(HudMinimapMode.AlwaysHide, true, false, false));
        });
        test("MFD map conditional hiding restores HUD when MFD disappears", () => {
            Equal(true, MapPresentation.ShouldHideHud(HudMinimapMode.HideWhenMfdVisible, true, false, true));
            Equal(false, MapPresentation.ShouldHideHud(HudMinimapMode.HideWhenMfdVisible, true, false, false));
        });
        test("MFD map disabling feature and expanding map override all hiding policies", () => {
            foreach (HudMinimapMode mode in Enum.GetValues<HudMinimapMode>())
                foreach (bool visible in new[] { false, true })
                {
                    Equal(false, MapPresentation.ShouldHideHud(mode, false, false, visible));
                    Equal(false, MapPresentation.ShouldHideHud(mode, true, true, visible));
                }
        });
        test("MFD map requires source cockpit and active page independently", () => {
            Equal(true, MapPresentation.MfdVisible(true, true, true, false, false, false));
            Equal(false, MapPresentation.MfdVisible(false, true, true, false, false, false));
            Equal(false, MapPresentation.MfdVisible(true, false, true, false, false, false));
            Equal(false, MapPresentation.MfdVisible(true, true, false, false, false, false));
        });
        test("MFD map target landing and fullscreen feeds restore conditional HUD", () => {
            foreach (var feed in new[] { (true, false, false), (false, true, false), (false, false, true) })
            {
                bool visible = MapPresentation.MfdVisible(true, true, true, feed.Item1, feed.Item2, feed.Item3);
                Equal(false, visible);
                Equal(false, MapPresentation.ShouldHideHud(HudMinimapMode.HideWhenMfdVisible, true, feed.Item3, visible));
            }
        });
        test("MFD map native 300 viewport maps corners and center to 512 radar", () => {
            Near(-256, MapPresentation.MapCoordinate(-150, -150, 300, -256, 512));
            Near(0, MapPresentation.MapCoordinate(0, -150, 300, -256, 512));
            Near(256, MapPresentation.MapCoordinate(150, -150, 300, -256, 512));
        });
        test("MFD map asymmetric pivots and offscreen geometry preserve clipping coordinates", () => {
            Near(-128, MapPresentation.MapCoordinate(-60, -60, 300, -128, 512));
            Near(384, MapPresentation.MapCoordinate(240, -60, 300, -128, 512));
            Near(0, MapPresentation.MapCoordinate(15, -60, 300, -128, 512));
            // Do not clamp vertices: the destination mask clips spanning triangles.
            Near(-384, MapPresentation.MapCoordinate(-210, -60, 300, -128, 512));
        });
    }
}
