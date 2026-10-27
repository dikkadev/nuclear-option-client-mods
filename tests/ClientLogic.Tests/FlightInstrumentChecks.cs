using NuclearMinimap;

internal static class FlightInstrumentChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("flight instruments read compass heading from world forward", () => {
            Near(0f, FlightInstrumentMath.Heading(0f, 1f));
            Near(90f, FlightInstrumentMath.Heading(1f, 0f));
            Near(180f, FlightInstrumentMath.Heading(0f, -1f));
            Near(270f, FlightInstrumentMath.Heading(-1f, 0f));
            Near(45f, FlightInstrumentMath.Heading(.5f, .5f));
            Near(0f, FlightInstrumentMath.Heading(0f, 0f));
            Near(0f, FlightInstrumentMath.Heading(float.NaN, 1f));
        });
        test("flight instruments read pitch and signed bank, including inverted flight", () => {
            Near(0f, FlightInstrumentMath.Pitch(0f));
            Near(30f, FlightInstrumentMath.Pitch(.5f));
            Near(-90f, FlightInstrumentMath.Pitch(-1.2f));
            Near(0f, FlightInstrumentMath.Bank(0f, 1f));
            // Right wing down: the right vector points below the horizon.
            Near(30f, FlightInstrumentMath.Bank(-.5f, MathF.Sqrt(.75f)));
            Near(-30f, FlightInstrumentMath.Bank(.5f, MathF.Sqrt(.75f)));
            Near(90f, FlightInstrumentMath.Bank(-1f, 0f));
            Near(180f, MathF.Abs(FlightInstrumentMath.Bank(0f, -1f)));
            Near(135f, FlightInstrumentMath.Bank(-MathF.Sqrt(.5f), -MathF.Sqrt(.5f)));
        });
        test("flight instrument text uses three-digit headings and side-marked bank", () => {
            Equal("000", FlightInstrumentMath.HeadingText(359.6f));
            Equal("007", FlightInstrumentMath.HeadingText(7.2f));
            Equal("270", FlightInstrumentMath.HeadingText(-90f));
            Equal("N", FlightInstrumentMath.TapeLabel(360));
            Equal("E", FlightInstrumentMath.TapeLabel(90));
            Equal("03", FlightInstrumentMath.TapeLabel(30));
            Equal("33", FlightInstrumentMath.TapeLabel(-30));
            Equal("0", FlightInstrumentMath.BankText(.4f));
            Equal("R30", FlightInstrumentMath.BankText(29.6f));
            Equal("L15", FlightInstrumentMath.BankText(-15f));
            Equal("R180", FlightInstrumentMath.BankText(180f));
        });
        test("heading tape marks wrap through north in order", () => {
            var ticks = new List<(int degrees, float offset)>();
            FlightInstrumentMath.TapeTicks(358f, 10f, 5, ticks);
            Equal("350,355,0,5", string.Join(",", ticks.Select(t => t.degrees)));
            Near(-8f, ticks[0].offset); Near(7f, ticks[^1].offset);
            FlightInstrumentMath.TapeTicks(2f, 10f, 5, ticks);
            Equal("355,0,5,10", string.Join(",", ticks.Select(t => t.degrees)));
            Near(-7f, ticks[0].offset);
            FlightInstrumentMath.TapeTicks(0f, 0f, 5, ticks);
            Equal(0, ticks.Count);
            Near(-10f, FlightInstrumentMath.Offset(5f, 355f));
            Near(10f, FlightInstrumentMath.Offset(355f, 5f));
        });
        test("attitude ball clips sky and ground into complementary halves", () => {
            var square = new List<(float x, float y)> { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) };
            var sky = new List<(float x, float y)>(); var ground = new List<(float x, float y)>();
            FlightInstrumentMath.ClipHalfPlane(square, 0f, 1f, .5f, sky);
            FlightInstrumentMath.ClipHalfPlane(square, 0f, -1f, -.5f, ground);
            Near(1f, Area(sky)); Near(3f, Area(ground));
            FlightInstrumentMath.ClipHalfPlane(square, 0f, 1f, 2f, sky);
            Equal(0, sky.Count);
            FlightInstrumentMath.ClipHalfPlane(square, 0f, 1f, -2f, sky);
            Near(4f, Area(sky));
            // Nose up moves the horizon and the zero-pitch ladder line below the centre.
            Near(-50f, FlightInstrumentMath.LadderOffset(10f, 0f, 5f));
            Near(0f, FlightInstrumentMath.LadderOffset(10f, 10f, 5f));
        });
        test("attitude ball sits inside the selected corner", () => {
            var value = FlightInstrumentMath.CornerCenter(InstrumentCorner.BottomRight, -100f, -50f, 100f, 50f, 20f, 5f);
            Near(75f, value.x); Near(-25f, value.y);
            value = FlightInstrumentMath.CornerCenter(InstrumentCorner.TopLeft, -100f, -50f, 100f, 50f, 20f, 5f);
            Near(-75f, value.x); Near(25f, value.y);
            if (!FlightInstrumentMath.IsTop(InstrumentCorner.TopRight) || FlightInstrumentMath.IsTop(InstrumentCorner.BottomLeft))
                throw new Exception("Corner classification is wrong");
        });
    }

    private static float Area(List<(float x, float y)> polygon)
    {
        float sum = 0;
        for (int i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
            sum += a.x * b.y - b.x * a.y;
        }
        return MathF.Abs(sum) * .5f;
    }

    private static void Near(float expected, float actual)
    {
        if (MathF.Abs(expected - actual) > .001f) throw new Exception($"Expected {expected}, got {actual}");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
    }
}
