using NuclearMapTrails;

internal static class MapTrailChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("map trail samples follow the interval and expire by age", () => {
            var history = new TrailHistory();
            Expect(history.Due(0f, .5f));
            history.Add(0f, 1000f, 0f, 0f);
            Expect(!history.Due(.4f, .5f));
            Expect(history.Due(.5f, .5f));
            history.Add(100f, 1000f, 0f, 1f);
            history.Add(200f, 1000f, 0f, 2f);
            history.Prune(61.5f, 60f);
            Expect(history.Samples.Count == 1 && history.Samples[0].X == 200f);
            history.Prune(70f, 60f);
            Expect(history.IsEmpty);
        });
        test("map trail runs break on lost tracks and impossible jumps", () => {
            var history = new TrailHistory();
            history.Add(0f, 0f, 0f, 0f);
            history.Add(300f, 0f, 0f, 1f);
            history.Break();
            Expect(history.Due(1.1f, 5f));
            history.Add(400f, 0f, 0f, 1.1f);
            history.Add(20000f, 0f, 0f, 2f);
            Expect(history.Samples[0].StartsRun && !history.Samples[1].StartsRun);
            Expect(history.Samples[2].StartsRun && history.Samples[3].StartsRun);
        });
        test("map trail ignores non-advancing time and bounds memory", () => {
            var history = new TrailHistory();
            history.Add(0f, 0f, 0f, 1f);
            history.Add(1f, 0f, 0f, 1f);
            Expect(history.Samples.Count == 1);
            for (int i = 0; i < TrailHistory.MaxSamples + 10; i++) history.Add(i, 0f, 0f, 2f + i);
            Expect(history.Samples.Count == TrailHistory.MaxSamples);
        });
        test("map trail team policy keeps enemies opt-in", () => {
            Expect(TrailPolicy.ShouldTrack(TrailRelation.Friendly, false, TrailTeams.Friendly, true));
            Expect(!TrailPolicy.ShouldTrack(TrailRelation.Enemy, false, TrailTeams.Friendly, true));
            Expect(TrailPolicy.ShouldTrack(TrailRelation.Enemy, false, TrailTeams.AllVisible, true));
            Expect(TrailPolicy.ShouldTrack(TrailRelation.Spectator, false, TrailTeams.Friendly, true));
            Expect(!TrailPolicy.ShouldTrack(TrailRelation.NoFaction, false, TrailTeams.AllVisible, true));
            Expect(!TrailPolicy.ShouldTrack(TrailRelation.Friendly, true, TrailTeams.AllVisible, false));
        });
        test("map trail opacity fades with age and brightness with altitude", () => {
            Near(.85f, TrailPolicy.Opacity(0f, 60f, .85f, 0f, true));
            Near(.425f, TrailPolicy.Opacity(30f, 60f, .85f, 0f, true));
            Near(0f, TrailPolicy.Opacity(90f, 60f, .85f, 0f, true));
            Near(.85f, TrailPolicy.Opacity(30f, 60f, .85f, 0f, false));
            Near(.45f, TrailPolicy.Brightness(-50f, 0f, 8000f, .45f, true));
            Near(.725f, TrailPolicy.Brightness(4000f, 0f, 8000f, .45f, true));
            Near(1f, TrailPolicy.Brightness(12000f, 0f, 8000f, .45f, true));
            Near(1f, TrailPolicy.Brightness(0f, 0f, 8000f, .45f, false));
            Near(.45f, TrailPolicy.Brightness(100f, 500f, 500f, .45f, true));
        });
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expected condition to hold");
    }

    private static void Near(float expected, float actual)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > .001f)
            throw new Exception($"Expected {expected}, got {actual}");
    }
}
