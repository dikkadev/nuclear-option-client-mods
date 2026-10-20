using NuclearMinimap;

internal static class MapZoomChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("map zoom changes only while exactly one direction is held", () => {
            float zoomed = MapZoom.Step(2f, true, false, .05f, 1f);
            if (zoomed <= 2f) throw new Exception("Held zoom-in did not increase zoom");
            Near(zoomed, MapZoom.Step(zoomed, false, false, .05f, 1f));
            Near(zoomed, MapZoom.Step(zoomed, true, true, .05f, 1f));
            Near(2f, MapZoom.Step(zoomed, false, true, .05f, 1f));
        });
        test("map zoom respects wider minimap bounds during sustained holds", () => {
            float inward = 2f, outward = 2f;
            for (int frame = 0; frame < 200; frame++) {
                inward = MapZoom.Step(inward, true, false, .05f, 5f);
                outward = MapZoom.Step(outward, false, true, .05f, 5f);
            }
            Near(64f, inward); Near(.125f, outward);
            Near(64f, MapZoom.Step(64f, true, false, .05f, 1f));
            Near(.125f, MapZoom.Step(.125f, false, true, .05f, 1f));
        });
        test("map zoom rate is consistent across normal frame intervals", () => {
            float fastFrames = 2f, slowFrames = 2f;
            for (int frame = 0; frame < 100; frame++) fastFrames = MapZoom.Step(fastFrames, true, false, .01f, 1f);
            for (int frame = 0; frame < 20; frame++) slowFrames = MapZoom.Step(slowFrames, true, false, .05f, 1f);
            Near(4f, fastFrames); Near(fastFrames, slowFrames);
        });
        test("map zoom caps resume delay and ignores invalid elapsed time", () => {
            Near(MapZoom.Step(2f, true, false, .05f, 1f), MapZoom.Step(2f, true, false, 30f, 1f));
            foreach (float delta in new[] { -1f, 0f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Near(2f, MapZoom.Step(2f, true, false, delta, 1f));
        });
        test("map zoom recovers invalid source values and clamps external scales", () => {
            foreach (float zoom in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Near(2f, MapZoom.Step(zoom, false, false, .05f, 1f));
            Near(.125f, MapZoom.Step(-20f, false, false, .05f, 1f));
            Near(64f, MapZoom.Step(100f, false, false, .05f, 1f));
        });
        test("map zoom sanitizes invalid and out-of-range configured rates", () => {
            foreach (float rate in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                Near(MapZoom.Step(2f, true, false, .05f, 1f), MapZoom.Step(2f, true, false, .05f, rate));
            Near(MapZoom.Step(2f, true, false, .05f, .1f), MapZoom.Step(2f, true, false, .05f, -10f));
            Near(MapZoom.Step(2f, true, false, .05f, 5f), MapZoom.Step(2f, true, false, .05f, 100f));
        });
        test("map orientation retains native heading or fixes north", () => {
            Near(273f, MapZoom.Angle(273f, MapOrientation.HeadingUp));
            Near(0f, MapZoom.Angle(273f, MapOrientation.NorthUp));
        });
        test("ownship stays centered across heading, map factor and zoom", () => {
            foreach (float angle in new[] { 0f, 45f, 90f, 187f, 273f, 359f })
            foreach (float scale in new[] { .125f, 1f, 2f, 10f, 40f, 64f })
            foreach (float factor in new[] { .001f, .01f, .1f })
            {
                float x = 45000f * factor, z = -27000f * factor;
                var offset = MapCenter.Offset(x, z, angle, scale);
                // Independent System.Numerics transform models the map's ownship point.
                var transform = System.Numerics.Matrix3x2.CreateScale(scale) *
                    System.Numerics.Matrix3x2.CreateRotation(angle * MathF.PI / 180f);
                var ownship = System.Numerics.Vector2.Transform(new System.Numerics.Vector2(x, z), transform);
                if ((ownship + new System.Numerics.Vector2(offset.x, offset.y)).Length() > .05f)
                    throw new Exception($"Ownship left center at heading {angle}, zoom {scale}, factor {factor}");
            }
        });
    }

    private static void Near(float expected, float actual)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > .001f)
            throw new Exception($"Expected {expected} ± .001, got {actual}");
    }
}
