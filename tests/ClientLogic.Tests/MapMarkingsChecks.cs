using NuclearMapMarkings;

internal static class MapMarkingsChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("map markings approach ends follow landing and reversible flags", () => {
            var ends = new ApproachEnd[2];
            var start = new MapPoint(0f, 0f);
            var end = new MapPoint(0f, 2000f);
            Expect(MarkingsGeometry.ApproachEnds(start, end, landing: false, reversible: true, ends) == 0);
            Expect(MarkingsGeometry.ApproachEnds(start, end, landing: true, reversible: false, ends) == 1);
            // Landing north at Start: the approach line extends south of the threshold.
            Near(0f, ends[0].Threshold.Z);
            Near(-15000f, ends[0].PointAt(15000f).Z);
            Expect(MarkingsGeometry.ApproachEnds(start, end, landing: true, reversible: true, ends) == 2);
            Near(2000f, ends[1].Threshold.Z);
            Near(17000f, ends[1].PointAt(15000f).Z);
            Expect(MarkingsGeometry.ApproachEnds(start, start, landing: true, reversible: true, ends) == 0);
        });
        test("map markings approach length follows runway length up to the cap", () => {
            Near(3000f, MarkingsGeometry.ApproachLength(400f, 600f, 2000f, 3000f, 7500f));
            Near(3000f, MarkingsGeometry.ApproachLength(600f, 600f, 2000f, 3000f, 7500f));
            Near(7500f, MarkingsGeometry.ApproachLength(2000f, 600f, 2000f, 3000f, 7500f));
            Near(7500f, MarkingsGeometry.ApproachLength(4000f, 600f, 2000f, 3000f, 7500f));
            // Geometric midpoint of the runway range gives the arithmetic midpoint of the line range.
            Near(5250f, MarkingsGeometry.ApproachLength((float)Math.Sqrt(600.0 * 2000.0), 600f, 2000f, 3000f, 7500f), 0.5f);
            float a = MarkingsGeometry.ApproachLength(1000f, 600f, 2000f, 3000f, 7500f);
            float b = MarkingsGeometry.ApproachLength(1500f, 600f, 2000f, 3000f, 7500f);
            Expect(a < b && b - a < a - 3000f);
            Near(0f, MarkingsGeometry.ApproachLength(0f, 600f, 2000f, 3000f, 7500f));
            Near(7500f, MarkingsGeometry.ApproachLength(1000f, 600f, 600f, 3000f, 7500f));
        });
        test("map markings ticks fall on whole spacings inside the approach", () => {
            var ticks = new List<float>();
            MarkingsGeometry.TickDistances(15000f, 2000f, ticks);
            Expect(ticks.Count == 7 && ticks[0] == 2000f && ticks[6] == 14000f);
            MarkingsGeometry.TickDistances(10000f, 2000f, ticks);
            Expect(ticks.Count == 5 && ticks[4] == 10000f);
            MarkingsGeometry.TickDistances(0f, 2000f, ticks);
            Expect(ticks.Count == 0);
            MarkingsGeometry.TickDistances(15000f, 0f, ticks);
            Expect(ticks.Count == 0);
        });
        test("map markings range ring dashes close evenly and stay bounded", () => {
            // 1400 px round with an 8 + 6 px pattern: exactly 100 dashes.
            Expect(MarkingsGeometry.RingDashCount(1400f, 8f, 6f, 2048) == 100);
            Expect(MarkingsGeometry.RingDashCount(1410f, 8f, 6f, 2048) == 101);
            // Tiny rings keep a few dashes; huge rings stop at the cap.
            Expect(MarkingsGeometry.RingDashCount(10f, 8f, 6f, 2048) == 4);
            Expect(MarkingsGeometry.RingDashCount(1e7f, 8f, 6f, 2048) == 2048);
            // No gap, no dash or no size means a solid ring.
            Expect(MarkingsGeometry.RingDashCount(1400f, 8f, 0f, 2048) == 0);
            Expect(MarkingsGeometry.RingDashCount(1400f, 0f, 6f, 2048) == 0);
            Expect(MarkingsGeometry.RingDashCount(0f, 8f, 6f, 2048) == 0);
            Expect(MarkingsGeometry.RingDashCount(float.PositiveInfinity, 8f, 6f, 2048) == 0);
        });
        test("map markings outer range rings get wider gaps", () => {
            Near(6f, MarkingsGeometry.RingGap(6f, 0));
            Near(12f, MarkingsGeometry.RingGap(6f, 1));
            Near(18f, MarkingsGeometry.RingGap(6f, 2));
            Near(6f, MarkingsGeometry.RingGap(6f, -1));
        });
        test("map markings ring distances parse, sort, dedupe and bound", () => {
            var values = MarkingsGeometry.ParseDistances("40, 10;20 x -5 10 900", 500f);
            Expect(values.Count == 3 && values[0] == 10f && values[1] == 20f && values[2] == 40f);
            Expect(MarkingsGeometry.ParseDistances("", 500f).Count == 0);
            Expect(MarkingsGeometry.ParseDistances("1 2 3 4 5 6 7 8 9 10", 500f).Count == 8);
        });
        test("map markings straight prediction moves at constant speed", () => {
            var points = new List<MapPoint>();
            MarkingsGeometry.PredictPath(new MapPoint(0f, 0f), new MapPoint(200f, 0f), 0f, 6f, 60f, 1f, points);
            Expect(points.Count == 61);
            Near(12000f, points[60].X);
            Near(0f, points[60].Z);
        });
        test("map markings path length levels off below its maximum", () => {
            // Slow: nearly linear (50 m/s for 60 s would be 3 km).
            float slow = 50f * MarkingsGeometry.PathHorizon(50f, 60f, 8000f);
            Expect(slow > 2400f && slow < 3000f);
            // Mach 1 for 60 s would be 20.4 km; the path stays under 8 km.
            float fast = 340f * MarkingsGeometry.PathHorizon(340f, 60f, 8000f);
            Expect(fast > 7000f && fast < 8000f);
            float faster = 600f * MarkingsGeometry.PathHorizon(600f, 60f, 8000f);
            Expect(faster > fast && faster < 8000f);
            // No limit, no speed and invalid time.
            Near(60f, MarkingsGeometry.PathHorizon(340f, 60f, 0f));
            Near(60f, MarkingsGeometry.PathHorizon(0f, 60f, 8000f));
            Near(0f, MarkingsGeometry.PathHorizon(340f, 0f, 8000f));
        });
        test("map markings constant-turn prediction follows an exact arc", () => {
            var points = new List<MapPoint>();
            float rate = (float)(Math.PI / 30.0); // a full circle in 60 s
            MarkingsGeometry.PredictPath(new MapPoint(0f, 0f), new MapPoint(200f, 0f), rate, float.PositiveInfinity, 60f, 1f, points);
            float radius = 200f / rate;
            // Positive rate turns from +x toward +z: the circle centre is at (0, radius).
            foreach (var point in points) Near(radius, (point - new MapPoint(0f, radius)).Length, 0.5f);
            Near(0f, points[60].X, 0.5f);
            Near(0f, points[60].Z, 0.5f);
            Near(2f * radius, points[30].Z, 0.5f);
        });
        test("map markings fading turn bends at most rate times fade", () => {
            var points = new List<MapPoint>();
            float rate = 0.2f;
            MarkingsGeometry.PredictPath(new MapPoint(0f, 0f), new MapPoint(200f, 0f), rate, 6f, 60f, 1f, points);
            var last = points[60] - points[59];
            double heading = Math.Atan2(last.Z, last.X);
            Expect(heading > 0.9 * rate * 6f && heading <= rate * 6f + 1e-3);
            Near((float)(rate * 6.0), (float)MarkingsGeometry.HeadingChange(rate, 6f, 1000.0), 1e-4f);
            Near(0f, (float)MarkingsGeometry.HeadingChange(rate, 0f, 30.0));
            // Fade 0 draws a straight line even with a measured turn.
            MarkingsGeometry.PredictPath(new MapPoint(0f, 0f), new MapPoint(200f, 0f), rate, 0f, 60f, 1f, points);
            Near(12000f, points[60].X, 0.5f);
            Near(0f, points[60].Z, 0.5f);
        });
        test("map markings motion estimate treats small jitter as straight", () => {
            var motion = new MotionEstimate();
            var random = new Random(7);
            for (int i = 0; i <= 40; i++)
            {
                float t = i * 0.25f;
                motion.Add(new MapPoint(200f * t, (float)(random.NextDouble() - 0.5) * 6f), t, UnitMotionKind.Aircraft);
            }
            Expect(motion.TryGet(out _, out float turn));
            Near(0f, turn);
        });
        test("map markings motion estimate measures speed and turn", () => {
            var motion = new MotionEstimate();
            Expect(!motion.TryGet(out _, out _));
            float rate = 0.1f;
            float radius = 200f / rate;
            for (int i = 0; i <= 12; i++)
            {
                float t = i * 0.25f;
                motion.Add(new MapPoint(radius * (float)Math.Sin(rate * t), radius - radius * (float)Math.Cos(rate * t)), t, UnitMotionKind.Aircraft);
            }
            Expect(motion.TryGet(out var velocity, out float turn));
            Near(200f, velocity.Length, 2f);
            Near(rate, turn, 0.01f);
        });
        test("map markings motion estimate clamps turns and resets on jumps", () => {
            var motion = new MotionEstimate();
            // A ground unit reversing direction must not report a huge turn rate.
            for (int i = 0; i <= 4; i++) motion.Add(new MapPoint(i * 2.5f, 0f), i * 0.25f, UnitMotionKind.Surface);
            for (int i = 1; i <= 4; i++) motion.Add(new MapPoint(10f, i * 2.5f), 1f + i * 0.25f, UnitMotionKind.Surface);
            Expect(motion.TryGet(out _, out float turn));
            Expect(Math.Abs(turn) <= MarkingsGeometry.MaxSurfaceTurnRate + 1e-4f);
            motion.Add(new MapPoint(1_000_000f, 0f), 2.25f, UnitMotionKind.Surface);
            Expect(!motion.TryGet(out _, out _));
        });
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expected condition to hold");
    }

    private static void Near(float expected, float actual, float tolerance = .001f)
    {
        if (!float.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new Exception($"Expected {expected}, got {actual}");
    }
}
