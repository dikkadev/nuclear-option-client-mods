namespace NuclearSpectator
{
    // Your own aircraft, while it is alive. After it is destroyed the game keeps it as the
    // local player's aircraft and on CombatHUD until the server removes it, about 30 s
    // later; for the spectator that already counts as having no aircraft.
    internal static class OwnAircraft
    {
        internal static bool Flying() => GameManager.GetLocalAircraft(out var aircraft) && !aircraft.disabled;

        internal static bool OnHud(CombatHUD hud) => hud.aircraft != null && !hud.aircraft.disabled;
    }
}
