using NuclearNightVision;

internal static class NightVisionChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("night vision fade takes the configured seconds in each direction", () => {
            // Binary-exact steps: 0.5 s in and 0.25 s out at 0.125 s per frame.
            float amount = 0f;
            for (int frame = 0; frame < 3; frame++) amount = FadeMath.Step(amount, true, 0.125f, 0.5f, 0.25f);
            Expect(amount == 0.75f);
            amount = FadeMath.Step(amount, true, 0.125f, 0.5f, 0.25f);
            Expect(amount == 1f);
            amount = FadeMath.Step(amount, false, 0.125f, 0.5f, 0.25f);
            Expect(amount == 0.5f);
            amount = FadeMath.Step(amount, false, 0.125f, 0.5f, 0.25f);
            Expect(amount == 0f);
        });
        test("night vision fade reverses from where it is and never overshoots", () => {
            float amount = FadeMath.Step(0f, true, 0.3f, 0.6f, 0.6f);
            amount = FadeMath.Step(amount, false, 0.15f, 0.6f, 0.6f);
            Expect(Math.Abs(amount - 0.25f) < 1e-5f);
            Expect(FadeMath.Step(0.9f, true, 10f, 0.6f, 0.6f) == 1f);
            Expect(FadeMath.Step(0.1f, false, 10f, 0.6f, 0.6f) == 0f);
            Expect(FadeMath.Step(0.5f, true, -1f, 0.6f, 0.6f) == 0.5f);
        });
        test("zero fade seconds keep the game's instant switch", () => {
            Expect(FadeMath.Step(0f, true, 0.001f, 0f, 0.4f) == 1f);
            Expect(FadeMath.Step(1f, false, 0.001f, 0.6f, 0f) == 0f);
        });
        test("night vision easing starts and ends gently", () => {
            Expect(FadeMath.Ease(0f) == 0f && FadeMath.Ease(1f) == 1f);
            Expect(Math.Abs(FadeMath.Ease(0.5f) - 0.5f) < 1e-6f);
            Expect(FadeMath.Ease(0.1f) < 0.1f && FadeMath.Ease(0.9f) > 0.9f);
            Expect(FadeMath.Ease(-1f) == 0f && FadeMath.Ease(2f) == 1f);
        });
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expectation failed.");
    }
}
