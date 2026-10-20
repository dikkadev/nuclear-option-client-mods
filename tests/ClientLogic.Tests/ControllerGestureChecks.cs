using NuclearControllerGestures;

internal static class ControllerGestureChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("double press fires only on second short release", () => {
            var g = Ready(); Step(g, true, .1); Step(g, true, .15); Step(g, false, .2);
            Step(g, true, .3); Step(g, true, .35); Step(g, false, .4, true); Step(g, false, .41);
        });
        test("double press accepts exact duration and interval boundaries", () => {
            var g = Ready(); Step(g, true, 0, window: .5, max: .25);
            Step(g, false, .25, window: .5, max: .25);
            Step(g, true, .75, window: .5, max: .25);
            Step(g, false, 1, true, window: .5, max: .25);
        });
        test("second release may follow interval expiry when down was timely", () => {
            var g = Ready(); Step(g, true, .1); Step(g, false, .2);
            Step(g, true, .49); Step(g, false, .7, true);
        });
        test("slow taps start a fresh pair after window expires", () => {
            var g = Ready(); Tap(g, .1, .2); Tap(g, .6, .7);
            Step(g, true, .8); Step(g, false, .9, true);
        });
        test("long first press cannot seed double press", () => {
            var g = Ready(); Step(g, true, .1); Step(g, false, .351);
            Tap(g, .4, .5); Step(g, true, .6); Step(g, false, .7, true);
        });
        test("long second press cancels the pair", () => {
            var g = Ready(); Tap(g, .1, .2); Step(g, true, .3); Step(g, false, .551);
            Tap(g, .6, .7); Step(g, true, .8); Step(g, false, .9, true);
        });
        test("sampled long hold requires release and never repeats", () => {
            var g = Ready(); Tap(g, .1, .2); Step(g, true, .3);
            Step(g, true, .6); Step(g, true, 1); Step(g, false, 1.1);
            Tap(g, 1.2, 1.3); Step(g, true, 1.4); Step(g, false, 1.5, true);
        });
        test("initial held input requires neutral before collecting taps", () => {
            var g = new DoublePressGesture(); Step(g, true, 0); Step(g, true, .1);
            Step(g, false, .2); Tap(g, .3, .4); Step(g, true, .5); Step(g, false, .6, true);
        });
        test("triple burst fires once and cooldown taps are discarded", () => {
            var g = Ready(); Tap(g, .1, .2); Step(g, true, .3); Step(g, false, .4, true);
            Tap(g, .45, .5); Step(g, false, .7);
            Tap(g, .8, .9); Step(g, true, 1); Step(g, false, 1.1, true);
        });
        test("held input through cooldown needs a fresh neutral observation", () => {
            var g = Ready(); Tap(g, .1, .2); Step(g, true, .3); Step(g, false, .4, true);
            Step(g, true, .5); Step(g, true, .8); Step(g, false, .9);
            Tap(g, 1, 1.1); Step(g, true, 1.2); Step(g, false, 1.3, true);
        });
        test("focus cancellation drops pending first and second presses", () => {
            foreach (bool secondDown in new[] { false, true }) {
                var g = Ready(); Tap(g, .1, .2); if (secondDown) Step(g, true, .3);
                g.Cancel(); Step(g, true, .4); Step(g, false, .5);
                Tap(g, .6, .7); Step(g, true, .8); Step(g, false, .9, true);
            }
        });
        test("invalid and backwards clocks cancel and require neutral", () => {
            foreach (double time in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, .1 }) {
                var g = Ready(); Tap(g, .1, .2); Step(g, true, time);
                Step(g, true, .3); Step(g, false, .4);
                Tap(g, .5, .6); Step(g, true, .7); Step(g, false, .8, true);
            }
        });
        test("invalid timing settings cancel pending cycles", () => {
            foreach (var settings in new[] { (0d, .25, .3), (.3, 0d, .3), (.3, .25, -1d),
                (double.NaN, .25, .3), (.3, double.PositiveInfinity, .3), (.3, .25, double.NaN) }) {
                var g = Ready(); Tap(g, .1, .2);
                Step(g, true, .3, window: settings.Item1, max: settings.Item2, cooldown: settings.Item3);
                Step(g, false, .4); Tap(g, .5, .6); Step(g, true, .7); Step(g, false, .8, true);
            }
        });
        test("physical control instances cannot combine their first taps", () => {
            var a = Ready(); var b = Ready(); Tap(a, .1, .2); Tap(b, .3, .4);
            Step(a, true, .35); Step(a, false, .45, true); Step(b, false, .45);
            Step(b, true, .5); Step(b, false, .6, true);
        });
        test("zero cooldown still requires neutral after a successful pair", () => {
            var g = Ready(); Tap(g, .1, .2); Step(g, true, .3, cooldown: 0);
            Step(g, false, .4, true, cooldown: 0); Step(g, true, .5, cooldown: 0);
            Step(g, false, .6, cooldown: 0); Tap(g, .7, .8);
            Step(g, true, .9); Step(g, false, 1, true);
        });
    }

    private static DoublePressGesture Ready()
    {
        var result = new DoublePressGesture(); Step(result, false, 0); return result;
    }
    private static void Tap(DoublePressGesture g, double down, double up)
    {
        Step(g, true, down); Step(g, false, up);
    }
    private static void Step(DoublePressGesture g, bool held, double time, bool expected = false,
        double window = .3, double max = .25, double cooldown = .3)
    {
        bool actual = g.Update(held, time, window, max, cooldown);
        if (expected != actual) throw new Exception($"At {time}, held={held}: expected {expected}, got {actual}");
    }
}
