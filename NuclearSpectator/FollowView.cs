using HarmonyLib;
using System;

namespace NuclearSpectator
{
    // The game always starts following an aircraft in the outside (orbit) view. While you
    // have no aircraft of your own, this picks the cockpit view instead when you start
    // following from nothing, or switch from another aircraft's cockpit view. It covers
    // every native path (map icon, scoreboard, spectate controls) and the mod's own.
    internal static class FollowView
    {
        // Set while the mod follows a new aircraft for one whose view it remembers.
        internal static FollowedView? PreviousOverride;

        // Counts follow changes the mod did not make, so a pending Follow Nearest switch
        // yields to them (spawn menu, map, scoreboard, aircraft keys). The game's own steps
        // of a loss do not count; they can arrive after the loss is seen: the hand-off to
        // the ejected pilot (a FixedUpdate later on the host), the drop of a disabled
        // aircraft (its SyncVar can trail the pilot-death RPC) and the drop of that pilot.
        internal static int OtherFollows { get; private set; }

        internal static FollowedView Current(CameraStateManager cam)
        {
            if (!(cam.followingUnit is Aircraft aircraft) || !Alive(aircraft)) return FollowedView.None;
            return cam.currentState == cam.cockpitState ? FollowedView.Cockpit : FollowedView.Outside;
        }

        // Aircraft.ejected is set only where the aircraft is simulated; the pilot flags are
        // set on every client (PilotDismounted.Setup calls Pilot.SetEjected).
        internal static bool Alive(Aircraft? aircraft)
        {
            if (aircraft == null || aircraft.disabled || aircraft.HasEjected()) return false;
            var pilots = aircraft.pilots;
            var pilot = pilots != null && pilots.Length > 0 ? pilots[0] : null;
            return pilot == null || (!pilot.ejected && !pilot.dead);
        }

        internal static void NoteFollow(CameraStateManager cam, Unit unit)
        {
            if (PreviousOverride != null) return;
            var followed = cam.followingUnit;
            if (unit == null && (followed is PilotDismounted || (followed is Aircraft gone && (gone == null || gone.disabled)))) return;
            if (unit is PilotDismounted pilot && pilot.pilotNumber == 0 && followed is Aircraft parent && parent != null
                && pilot.parentUnit == parent.persistentID) return;
            OtherFollows++;
        }

        internal static bool Spectating()
            => (GameManager.gameState == GameState.SinglePlayer || GameManager.gameState == GameState.Multiplayer)
                && !OwnAircraft.Flying();

        internal static void Apply(CameraStateManager cam, Unit unit, FollowedView previous)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value || !Spectating()) return;
            if (!(unit is Aircraft aircraft) || !Alive(aircraft) || aircraft.cockpitViewPoint == null) return;
            if (!ReferenceEquals(cam.followingUnit, aircraft) || cam.currentState != cam.orbitState) return;
            if (!SpectatorCycle.StartInCockpit(previous, plugin.StartInCockpit.Value, plugin.KeepCockpitView.Value)) return;
            cam.SwitchState(cam.cockpitState);
        }
    }

    [HarmonyPatch(typeof(CameraStateManager), nameof(CameraStateManager.SetFollowingUnit))]
    internal static class SetFollowingUnitPatch
    {
        private static void Prefix(CameraStateManager __instance, Unit unit, out FollowedView __state)
        {
            __state = FollowedView.None;
            try
            {
                FollowView.NoteFollow(__instance, unit);
                __state = FollowView.PreviousOverride ?? FollowView.Current(__instance);
            }
            catch (Exception error) { Plugin.Report("follow view", error); }
        }

        private static void Postfix(CameraStateManager __instance, Unit unit, FollowedView __state)
        {
            try { FollowView.Apply(__instance, unit, __state); }
            catch (Exception error) { Plugin.Report("follow view", error); }
        }
    }
}
