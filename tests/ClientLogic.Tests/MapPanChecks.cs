using NuclearMapPan;

internal static class MapPanChecks
{
    private const float C = 0.55f, A = 0.75f, Top = 2.75f, Belly = 0.45f;

    internal static void Run(Action<string, Action> test)
    {
        test("map pan curve passes through the chosen points", () => {
            Expect(PanMath.Speed(0f, C, A, Top, Belly) == 0f);
            Expect(Near(PanMath.Speed(C, C, A, Top, Belly), C));
            Expect(Near(PanMath.Speed(A, C, A, Top, Belly), 1f));
            Expect(Near(PanMath.Speed(1f, C, A, Top, Belly), Top));
        });
        test("map pan curve always rises and stays at or above zero", () => {
            foreach (var (c, a, top, belly) in new[] { (C, A, Top, Belly), (0.05f, 0.1f, 10f, 1f), (0.9f, 0.95f, 1.05f, 0f), (0.3f, 0.5f, 6f, 1f) })
            {
                float previous = 0f;
                for (int i = 1; i <= 1000; i++)
                {
                    float speed = PanMath.Speed(i / 1000f, c, a, top, belly);
                    Expect(speed >= previous - 1e-5f);
                    previous = speed;
                }
            }
        });
        test("map pan belly sets the start: 1 flat, 0 along the game line", () => {
            Expect(PanMath.Speed(0.01f, C, A, Top, 1f) < 0.001f);
            Expect(Math.Abs(PanMath.Speed(0.01f, C, A, Top, 0f) - 0.01f) < 0.001f);
        });
        test("map pan full stick in every direction gives the top speed, keys their multiple", () => {
            Expect(Near(PanMath.Scale(1f, 0f, true, C, A, Top, Belly, 1f), Top));
            Expect(Near(PanMath.Scale(1f, 1f, true, C, A, Top, Belly, 1f) * (float)Math.Sqrt(2f), Top));
            Expect(PanMath.Scale(1f, 1f, false, C, A, Top, Belly, 1.5f) == 1.5f);
            Expect(PanMath.Scale(0f, 0f, true, C, A, Top, Belly, 1f) == 1f);
        });
    }

    private static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 1e-4f;

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expectation failed.");
    }
}
