using NuclearOption.MissionEditorScripts;
using NuclearOption.Networking;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearSpectator
{
    // Next/previous aircraft keys for spectators. The game itself only offers jumping to
    // the nearest enemy aircraft.
    internal static class AircraftCycler
    {
        private static readonly List<(CycleKey key, Aircraft aircraft)> Candidates = new List<(CycleKey, Aircraft)>();

        internal static void Update()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value) return;
            // Read both every frame: a skipped frame makes a binding wait for release.
            bool next = plugin.NextAircraft.IsDown();
            bool previous = plugin.PreviousAircraft.IsDown() && !next;
            if (!next && !previous) return;
            if (InputFieldChecker.InsideInputField || !Application.isFocused || !GameManager.flightControlsEnabled) return;
            if (GameManager.GetLocalAircraft(out _)) return;
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.currentState == null) return;

            Collect(plugin.IncludeAi.Value);
            int current = -1;
            for (int i = 0; i < Candidates.Count; i++)
                if (ReferenceEquals(Candidates[i].aircraft, cam.followingUnit)) current = i;
            int index = SpectatorCycle.Step(Candidates.Count, current, next ? 1 : -1);
            if (index < 0 || index == current) { Candidates.Clear(); return; }
            var target = Candidates[index].aircraft;
            Candidates.Clear();

            bool wasCockpit = cam.currentState == cam.cockpitState;
            cam.SetFollowingUnit(target);
            if (wasCockpit && plugin.KeepCockpitView.Value && target.cockpitViewPoint != null && !target.HasEjected()
                && ReferenceEquals(cam.followingUnit, target))
                cam.SwitchState(cam.cockpitState);
        }

        // Your team's aircraft when you are in a team (the camera would drop enemies it
        // cannot track), every aircraft when you only spectate.
        private static void Collect(bool includeAi)
        {
            Candidates.Clear();
            bool teamOnly = GameManager.GetLocalHQ(out var localHq);
            foreach (var unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft == null || aircraft.disabled || aircraft.HasEjected()) continue;
                if (teamOnly && aircraft.NetworkHQ != localHq) continue;
                var player = aircraft.Player;
                if (player == null && !includeAi) continue;
                string name = player != null ? player.GetDisplayName(PlayerNameContext.Other) ?? string.Empty : aircraft.definition.unitName;
                Candidates.Add((new CycleKey(player != null, name, aircraft.GetInstanceID()), aircraft));
            }
            Candidates.Sort((a, b) => SpectatorCycle.Compare(a.key, b.key));
        }
    }
}
