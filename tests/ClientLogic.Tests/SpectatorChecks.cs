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
        test("spectator starts in the cockpit only from nothing or from a cockpit", () => {
            Expect(SpectatorCycle.StartInCockpit(FollowedView.None, true, true));
            Expect(!SpectatorCycle.StartInCockpit(FollowedView.None, false, true));
            Expect(!SpectatorCycle.StartInCockpit(FollowedView.Outside, true, true));
            Expect(SpectatorCycle.StartInCockpit(FollowedView.Cockpit, false, true));
            Expect(!SpectatorCycle.StartInCockpit(FollowedView.Cockpit, true, false));
        });
        test("hide all UI steps through game UI, everything and nothing", () => {
            Expect(SpectatorCycle.NextHideLevel(HideLevel.None) == HideLevel.GameUi);
            Expect(SpectatorCycle.NextHideLevel(HideLevel.GameUi) == HideLevel.All);
            Expect(SpectatorCycle.NextHideLevel(HideLevel.All) == HideLevel.None);
        });
        test("cockpit movement speed curves with the stick and ramps into fast mode", () => {
            Expect(Near(SpectatorCycle.MoveSpeed(1f, 10f, 2f, 4f, 0f), 10f));
            Expect(Near(SpectatorCycle.MoveSpeed(0.5f, 10f, 2f, 4f, 0f), 2.5f));
            Expect(Near(SpectatorCycle.MoveSpeed(0f, 10f, 2f, 4f, 1f), 0f));
            Expect(Near(SpectatorCycle.MoveSpeed(1f, 10f, 2f, 4f, 1f), 40f));
            Expect(Near(SpectatorCycle.MoveSpeed(1f, 10f, 2f, 4f, 0.5f), 25f));
            Expect(Near(SpectatorCycle.MoveSpeed(2f, 10f, 2f, 4f, 2f), 40f));
            Expect(Near(SpectatorCycle.MoveSpeed(0.5f, 10f, 1f, 1f, 1f), 5f));
        });
    }

    private static bool Near(float value, float expected) => Math.Abs(value - expected) < 0.001f;

    private static void Expect(bool condition)
    {
        if (!condition) throw new Exception("Expectation failed.");
    }
}
