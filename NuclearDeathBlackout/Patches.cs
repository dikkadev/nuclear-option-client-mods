using HarmonyLib;

namespace NuclearDeathBlackout
{
    [HarmonyPatch(typeof(Unit), nameof(Unit.UnitDisabled))]
    internal static class UnitDisabledPatch
    {
        private static Aircraft? _localAircraft;
        private static Aircraft? _lastTriggeredAircraft;

        internal static void ObserveLocalAircraft()
        {
            var aircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            // Preserve it only until the next observation, allowing the same-frame
            // RemoveAircraft -> UnitDisabled sequence without retaining exited cockpits.
            _localAircraft = aircraft;
            if (aircraft != null)
            {
                TryTrigger(aircraft);
            }
        }

        internal static void Reset()
        {
            _localAircraft = null;
            _lastTriggeredAircraft = null;
        }

        private static void Postfix(Unit __instance, bool nowDisabled)
        {
            if (!nowDisabled || !Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Enabled.Value)
            {
                return;
            }

            if (!(__instance is Aircraft aircraft)
                || (!GameManager.IsLocalAircraft(aircraft) && aircraft != _localAircraft))
            {
                return;
            }

            TryTrigger(aircraft);
        }

        private static void TryTrigger(Aircraft aircraft)
        {
            if (!Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.Enabled.Value || aircraft == _lastTriggeredAircraft
                || aircraft.HasEjected() || !IsMainPilotDead(aircraft))
            {
                return;
            }

            _lastTriggeredAircraft = aircraft;
            DeathBlackoutController.PlayBlackout();
        }

        private static bool IsMainPilotDead(Aircraft aircraft)
        {
            if (aircraft.pilots == null || aircraft.pilots.Length == 0 || aircraft.pilots[0] == null)
            {
                return false;
            }

            return aircraft.pilots[0].dead;
        }
    }
}
