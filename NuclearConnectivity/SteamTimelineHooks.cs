using HarmonyLib;
using UnityEngine;

namespace NuclearConnectivity
{
    internal static class SteamTimelineMarkers
    {
        private static float _lastPlayerDeathMarkerTime = -999f;
        private static float _lastCreditMarkerTime = -999f;
        private static PersistentID _lastCreditKilledID;

        internal static void AddCreditedKillMarker(PersistentUnit killedUnit, float creditAwarded, FactionHQ.RewardType actionType)
        {
            if (!Plugin.Instance.EnableSteamTimeline.Value || actionType != FactionHQ.RewardType.Kill)
            {
                return;
            }

            if (IsDuplicateCreditMarker(killedUnit.id))
            {
                return;
            }

            SteamTimelineBridge.SetGameModePlaying();

            if (killedUnit.unit is Aircraft)
            {
                if (Plugin.Instance.MarkAirToAirKills.Value)
                {
                    SteamTimelineBridge.AddMarker(
                        "Air Kill",
                        killedUnit.unitName,
                        "steam_attack",
                        SteamTimelineBridge.TimelineClipPriority.Standard
                    );
                }

                return;
            }

            if (Plugin.Instance.MarkAirToGroundKills.Value)
            {
                SteamTimelineBridge.AddMarker(
                    "Ground Kill",
                    killedUnit.unitName,
                    "steam_marker",
                    SteamTimelineBridge.TimelineClipPriority.Standard
                );
            }
        }

        internal static void AddLocalAircraftDisabledMarker(Aircraft aircraft)
        {
            if (!Plugin.Instance.EnableSteamTimeline.Value || !Plugin.Instance.MarkPlayerDeath.Value)
            {
                return;
            }

            if (aircraft.HasEjected() || !IsMainPilotDead(aircraft))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now - _lastPlayerDeathMarkerTime < 5f)
            {
                return;
            }

            _lastPlayerDeathMarkerTime = now;
            SteamTimelineBridge.SetGameModePlaying();
            SteamTimelineBridge.AddMarker(
                "Crash",
                "Aircraft lost.",
                "steam_caution",
                SteamTimelineBridge.TimelineClipPriority.Featured
            );
        }

        private static bool IsDuplicateCreditMarker(PersistentID killedID)
        {
            float now = Time.realtimeSinceStartup;
            if (killedID.Equals(_lastCreditKilledID) && now - _lastCreditMarkerTime < 5f)
            {
                return true;
            }

            _lastCreditKilledID = killedID;
            _lastCreditMarkerTime = now;
            return false;
        }

        private static bool IsMainPilotDead(Aircraft aircraft)
        {
            return aircraft.pilots != null
                && aircraft.pilots.Length > 0
                && aircraft.pilots[0] != null
                && aircraft.pilots[0].dead;
        }
    }

    [HarmonyPatch(typeof(KillDisplay), nameof(KillDisplay.DisplayKill))]
    internal static class SteamTimelineKillDisplayHooks
    {
        private static void Postfix(PersistentUnit killedUnit, float creditGiven, FactionHQ.RewardType actionType)
        {
            if (killedUnit == null)
            {
                return;
            }

            SteamTimelineMarkers.AddCreditedKillMarker(killedUnit, creditGiven, actionType);
        }
    }

    [HarmonyPatch(typeof(Unit), nameof(Unit.UnitDisabled))]
    internal static class SteamTimelineUnitDisabledHooks
    {
        private static Aircraft? _localAircraft;

        internal static void ObserveLocalAircraft()
        {
            var aircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            // Clear on cockpit exit/control transfer at the next observation.
            // Same-frame HUD removal and disable can still use the remembered reference.
            _localAircraft = aircraft;
        }

        internal static void Reset() => _localAircraft = null;

        private static void Postfix(Unit __instance, bool nowDisabled)
        {
            if (!nowDisabled || !(__instance is Aircraft aircraft)
                || (!GameManager.IsLocalAircraft(aircraft) && aircraft != _localAircraft))
            {
                return;
            }

            SteamTimelineMarkers.AddLocalAircraftDisabledMarker(aircraft);
        }
    }
}
