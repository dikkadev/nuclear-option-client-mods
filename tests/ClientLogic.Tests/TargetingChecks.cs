using NuclearTargeting;

internal static class TargetingChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("tap capacity one replaces the previous target", () =>
            Expect(new[] { "new" }, TapTargetBuffer.Plan(new[] { "old" }, "new", 1)));
        test("tap at capacity evicts the oldest and retains other selection order", () =>
            Expect(new[] { "new", "recent", "middle" },
                TapTargetBuffer.Plan(new[] { "recent", "middle", "oldest" }, "new", 3)));
        test("reselect refreshes recency without evicting another target", () => {
            var previous = new[] { "recent", "middle", "oldest" };
            var refreshed = TapTargetBuffer.Plan(previous, "middle", 3);
            Expect(new[] { "middle", "recent", "oldest" }, refreshed);
            Expect(new[] { "next", "middle", "recent" }, TapTargetBuffer.Plan(refreshed, "next", 3));
            Expect(new[] { "recent", "middle", "oldest" }, previous);
        });
        test("tap normalizes duplicate targets without consuming slots", () =>
            Expect(new[] { "selected", "recent", "oldest" },
                TapTargetBuffer.Plan(new[] { "recent", "selected", "recent", "oldest", "selected" }, "selected", 3)));
        test("valid tap shrinks an already over capacity list", () =>
            Expect(new[] { "oldest", "recent" },
                TapTargetBuffer.Plan(new[] { "recent", "middle", "older", "oldest" }, "oldest", 2)));
        test("air and ground tap histories remain independent", () => {
            var air = TapTargetBuffer.Plan(Array.Empty<string>(), "air1", 2);
            var ground = TapTargetBuffer.Plan(Array.Empty<string>(), "ground1", 1);
            air = TapTargetBuffer.Plan(air, "air2", 2);
            ground = TapTargetBuffer.Plan(ground, "ground2", 1);
            air = TapTargetBuffer.Plan(air, "air3", 2);
            Expect(new[] { "air3", "air2" }, air);
            Expect(new[] { "ground2" }, ground);
        });
        test("lowered capacity takes effect on the next tap", () => {
            var selected = TapTargetBuffer.Plan(new[] { "b", "a" }, "c", 3);
            Expect(new[] { "d", "c" }, TapTargetBuffer.Plan(selected, "d", 2));
            Expect(new[] { "a" }, TapTargetBuffer.Plan(selected, "a", 1));
        });
        test("zero and negative tap capacity clamp to one", () => {
            foreach (int capacity in new[] { 0, -1, int.MinValue })
                Expect(new[] { "new" }, TapTargetBuffer.Plan(new[] { "old" }, "new", capacity));
        });
        test("tap uses default equality for value targets", () =>
            Expect(new[] { 3, 1, 2 }, TapTargetBuffer.Plan(new[] { 1, 1, 2, 3 }, 3, 3)));
    }

    private static void Expect<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
    }
}
