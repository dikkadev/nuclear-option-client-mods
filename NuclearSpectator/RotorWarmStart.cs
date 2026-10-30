using HarmonyLib;
using System;

namespace NuclearSpectator
{
    // A client creates other players' aircraft at their spawn point. Rotors and
    // propellers choose their start state in Unit.onInitialize, before the first network
    // position arrives, so a helicopter that is already flying when it first appears
    // starts with folded, stopped blades and needs about half a minute to spin up.
    // The first network position calls SpawnedInPosition, which measures the real
    // radar altitude and already warm-starts the turbines; repeat the rotor and
    // propeller start then, with the same airborne test the game uses.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.SpawnedInPosition))]
    internal static class RotorWarmStartPatch
    {
        private static readonly Action<RotorShaft>? RotorStart = Initializer<RotorShaft>("RotorShaft_OnSpawnedInPosition");
        private static readonly Action<ConstantSpeedProp>? PropStart = Initializer<ConstantSpeedProp>("ConstantSpeedProp_OnInitialize");

        internal static bool Available => RotorStart != null && PropStart != null;

        private static Action<T>? Initializer<T>(string name)
        {
            var method = AccessTools.Method(typeof(T), name, Type.EmptyTypes);
            return method == null ? null : AccessTools.MethodDelegate<Action<T>>(method);
        }

        private static void Postfix(Aircraft __instance)
        {
            try { Apply(__instance); }
            catch (Exception error) { Plugin.Report("rotor warm start", error); }
        }

        private static void Apply(Aircraft aircraft)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value || !plugin.FixRotors.Value) return;
            if (aircraft == null || aircraft.LocalSim || aircraft.disabled) return;
            if (!(aircraft.radarAlt > aircraft.definition.spawnOffset.y + 1f)) return;
            int rotors = 0, props = 0;
            if (RotorStart != null)
                foreach (var shaft in aircraft.GetComponentsInChildren<RotorShaft>(true)) { RotorStart(shaft); rotors++; }
            if (PropStart != null)
                foreach (var prop in aircraft.GetComponentsInChildren<ConstantSpeedProp>(true)) { PropStart(prop); props++; }
            if (rotors + props > 0)
                Plugin.Once("rotors:" + aircraft.definition.unitName,
                    $"Started {rotors} rotor(s) and {props} propeller(s) at flight speed on a remote {aircraft.definition.unitName} that first appeared in flight.");
        }
    }
}
