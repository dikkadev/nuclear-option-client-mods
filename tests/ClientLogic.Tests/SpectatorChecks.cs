using NuclearSpectator;

internal static class SpectatorChecks
{
    internal static void Run(Action<string, Action> test)
    {
        test("spectator cycling wraps in both directions", () => {
            Expect(SpectatorCycle.Step(3, 0, 1) == 1);
            Expect(SpectatorCycle.Step(3, 2, 1) == 0);
            Expect(SpectatorCycle.Step(3, 0, -1) == 2);
            Expect(SpectatorCycle.Step(3, 1, -1) == 0);
            Expect(SpectatorCycle.Step(1, 0, 1) == 0);
        });
        test("spectator cycling starts at an end when nothing listed is followed", () => {
            Expect(SpectatorCycle.Step(4, -1, 1) == 0);
            Expect(SpectatorCycle.Step(4, -1, -1) == 3);
            Expect(SpectatorCycle.Step(4, 7, 1) == 0);
            Expect(SpectatorCycle.Step(0, -1, 1) == -1);
            Expect(SpectatorCycle.Step(0, 0, -1) == -1);
        });
        test("spectator cycling orders players by name before AI", () => {
            var keys = new List<CycleKey>
            {
                new CycleKey(false, "Chicane", 9),
                new CycleKey(true, "zulu", 1),
                new CycleKey(true, "Alpha", 5),
                new CycleKey(true, "alpha", 2),
                new CycleKey(false, "Brawler", 3),
                new CycleKey(true, "Alpha", 4),
            };
            keys.Sort(SpectatorCycle.Compare);
            Expect(string.Join(",", keys.Select(k => k.Name + k.Id)) == "Alpha4,Alpha5,alpha2,zulu1,Brawler3,Chicane9");
        });
    }

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expectation failed.");
    }
}
