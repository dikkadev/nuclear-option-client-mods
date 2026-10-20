using NuclearMinimap;

internal static class TapHoldChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("short tap releases one normal action without zoom", () => {
            var gesture = new TapHoldGesture();
            Expect(false, gesture.Update(true, 0, .3f)); Expect(false, gesture.Holding);
            Expect(false, gesture.Update(true, .2f, .3f)); Expect(false, gesture.Holding);
            Expect(true, gesture.Update(false, .25f, .3f)); Expect(false, gesture.Holding);
            Expect(false, gesture.Update(false, .26f, .3f));
        });
        test("long press zooms after threshold and never replays a tap", () => {
            var gesture = new TapHoldGesture();
            gesture.Update(true, 0, .3f); gesture.Update(true, .29f, .3f); Expect(false, gesture.Holding);
            gesture.Update(true, .3f, .3f); Expect(true, gesture.Holding);
            gesture.Update(true, 3, .3f); Expect(true, gesture.Holding);
            Expect(false, gesture.Update(false, 3.1f, .3f)); Expect(false, gesture.Holding);
        });
        test("release at hold threshold cannot become a tap on a slow frame", () => {
            var gesture = new TapHoldGesture();
            gesture.Update(true, 0, .3f);
            Expect(false, gesture.Update(false, .3f, .3f));
        });
        test("context loss cancels pending taps and held zoom until release", () => {
            foreach (float elapsed in new[] { .1f, .8f }) {
                var gesture = new TapHoldGesture();
                gesture.Update(true, 0, .3f); gesture.Update(true, elapsed, .3f); gesture.Cancel();
                Expect(false, gesture.Update(true, 1, .3f)); Expect(false, gesture.Holding);
                Expect(false, gesture.Update(false, 1.1f, .3f));
                gesture.Update(true, 2, .3f); Expect(true, gesture.Update(false, 2.1f, .3f));
            }
        });
        test("hold threshold remains configurable and rejects invalid timing", () => {
            foreach (float threshold in new[] { .1f, .3f, 1f }) {
                var gesture = new TapHoldGesture(); gesture.Update(true, 0, threshold);
                gesture.Update(true, threshold, threshold); Expect(true, gesture.Holding);
            }
            var invalid = new TapHoldGesture(); invalid.Update(true, 0, float.NaN);
            invalid.Update(true, .3f, float.NaN); Expect(true, invalid.Holding);
            Expect(false, invalid.Update(false, float.NaN, .3f));
            Expect(false, invalid.Update(false, 1, .3f));
            var backwards = new TapHoldGesture(); backwards.Update(true, 2, .3f);
            Expect(false, backwards.Update(false, 1, .3f));
        });
    }

    private static void Expect(bool expected, bool actual)
    {
        if (expected != actual) throw new Exception($"Expected {expected}, got {actual}");
    }
}
