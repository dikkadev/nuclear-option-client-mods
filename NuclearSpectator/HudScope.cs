using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace NuclearSpectator
{
    // Native HUD and MFD components find "their" aircraft through CombatHUD.aircraft or
    // GameManager.GetLocalAircraft, which are empty while you spectate. The patches below
    // rewrite exactly those reads to call Resolve*, which returns the native value, or the
    // spectated aircraft while a spectator-owned component (or the scene helmet display)
    // runs. Nothing writes CombatHUD.aircraft, so the rest of the game never sees a change.
    public static class HudScope
    {
        internal readonly struct Token
        {
            internal Token(Aircraft? previous) { Entered = true; Previous = previous; }
            internal readonly bool Entered;
            internal readonly Aircraft? Previous;
        }

        private static readonly FieldInfo HudAircraftField = AccessTools.Field(typeof(CombatHUD), nameof(CombatHUD.aircraft));
        private static readonly MethodInfo LocalAircraftMethod = AccessTools.Method(typeof(GameManager), nameof(GameManager.GetLocalAircraft));
        private static readonly MethodInfo ResolveHudMethod = AccessTools.Method(typeof(HudScope), nameof(ResolveHudAircraft));
        private static readonly MethodInfo ResolveLocalMethod = AccessTools.Method(typeof(HudScope), nameof(ResolveLocalAircraft));
        private static readonly Dictionary<string, int> Rewrites = new Dictionary<string, int>();

        private static Aircraft? session;
        private static List<GameObject>? roots;
        private static Aircraft? current;
        private static bool helmetIncluded;

        internal static void Begin(Aircraft aircraft, List<GameObject> ownedRoots, bool helmet)
        {
            session = aircraft;
            roots = ownedRoots;
            helmetIncluded = helmet;
        }

        internal static void End()
        {
            session = null;
            roots = null;
            current = null;
            helmetIncluded = false;
        }

        internal static Token Enter(Component instance)
        {
            if (session == null || session.disabled || instance == null) return default;
            if (!(helmetIncluded && instance is HeadMountedDisplay) && !IsOwned(instance.transform)) return default;
            var token = new Token(current);
            current = session;
            return token;
        }

        internal static void Exit(Token token)
        {
            if (token.Entered) current = token.Previous;
        }

        internal static bool Owns(Component instance) => session != null && instance != null && IsOwned(instance.transform);

        private static bool IsOwned(Transform transform)
        {
            if (roots == null) return false;
            foreach (var root in roots)
                if (root != null && transform.IsChildOf(root.transform)) return true;
            return false;
        }

        public static Aircraft ResolveHudAircraft(CombatHUD hud)
        {
            var aircraft = hud.aircraft;
            return aircraft != null ? aircraft : current!;
        }

        public static bool ResolveLocalAircraft(out Aircraft localAircraft)
        {
            if (GameManager.GetLocalAircraft(out localAircraft)) return true;
            localAircraft = current!;
            return localAircraft != null;
        }

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, string target)
        {
            var list = instructions.ToList();
            int count = 0;
            foreach (var instruction in list)
            {
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, HudAircraftField))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = ResolveHudMethod;
                    count++;
                }
                else if (instruction.opcode == OpCodes.Call && Equals(instruction.operand, LocalAircraftMethod))
                {
                    instruction.operand = ResolveLocalMethod;
                    count++;
                }
            }
            Rewrites[target] = count;
            return list;
        }

        // Other players' aircraft never get a TargetCam (it needs network authority), and
        // TacScreen.Initialize subscribes to it unconditionally.
        public static void SubscribeCamToggle(TargetCam targetCam, Action<TargetCam.OnCamToggle> handler)
        {
            if (targetCam != null) targetCam.onCamToggle += handler;
        }

        internal static IEnumerable<CodeInstruction> GuardCamToggle(IEnumerable<CodeInstruction> instructions)
        {
            var add = AccessTools.Method(typeof(TargetCam), "add_onCamToggle");
            var guard = AccessTools.Method(typeof(HudScope), nameof(SubscribeCamToggle));
            var list = instructions.ToList();
            int count = 0;
            foreach (var instruction in list)
            {
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && Equals(instruction.operand, add))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = guard;
                    count++;
                }
            }
            Rewrites["TacScreen.Initialize"] = count;
            return list;
        }

        internal static string DescribeRewrites()
            => Rewrites.Count == 0 ? "none" : string.Join(", ", Rewrites.Select(item => $"{item.Key}={item.Value}"));
    }

    [HarmonyPatch(typeof(HUDAppManager), "Start")]
    internal static class HudAppManagerStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "HUDAppManager.Start");
    }

    [HarmonyPatch(typeof(HUDAppManager), "Update")]
    internal static class HudAppManagerUpdatePatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
    }

    [HarmonyPatch(typeof(MFDAppManager), "Start")]
    internal static class MfdAppManagerStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "MFDAppManager.Start");
    }

    [HarmonyPatch(typeof(MFDAppManager), "Update")]
    internal static class MfdAppManagerUpdatePatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
    }

    // Refreshed by an app manager or the helmet display, which open the scope.
    [HarmonyPatch(typeof(ArtificialHorizon), "Refresh")]
    internal static class ArtificialHorizonRefreshPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "ArtificialHorizon.Refresh");
    }

    [HarmonyPatch(typeof(PylonIndicator), "Start")]
    internal static class PylonIndicatorStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "PylonIndicator.Start");
    }

    [HarmonyPatch(typeof(EngineTelemetry), "Start")]
    internal static class EngineTelemetryStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "EngineTelemetry.Start");
    }

    [HarmonyPatch(typeof(RadarWarning), "Start")]
    internal static class RadarWarningStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "RadarWarning.Start");
    }

    [HarmonyPatch(typeof(HeadMountedDisplay), "Start")]
    internal static class HeadMountedDisplayStartPatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "HeadMountedDisplay.Start");
    }

    [HarmonyPatch(typeof(HeadMountedDisplay), "Update")]
    internal static class HeadMountedDisplayUpdatePatch
    {
        private static void Prefix(Component __instance, out HudScope.Token __state) => __state = HudScope.Enter(__instance);
        private static Exception? Finalizer(Exception? __exception, HudScope.Token __state) { HudScope.Exit(__state); return __exception; }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.Rewrite(instructions, "HeadMountedDisplay.Update");
    }

    [HarmonyPatch(typeof(TacScreen), nameof(TacScreen.Initialize))]
    internal static class TacScreenInitializePatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => HudScope.GuardCamToggle(instructions);
    }

    // The damage handler ends with the pilot's action report, which a spectator client
    // lacks. Its exception would otherwise abort the native damage code that raised the
    // event (Turbojet.KillEngine), so swallow it for the spectator's own copy only.
    [HarmonyPatch(typeof(StatusDisplay), "StatusDisplay_OnReportDamage")]
    internal static class StatusDisplayDamagePatch
    {
        private static Exception? Finalizer(Exception? __exception, StatusDisplay __instance)
        {
            if (__exception == null || !HudScope.Owns(__instance)) return __exception;
            Plugin.Once("status-damage", "Spectator damage display: ignored the pilot's action report, which this client does not have (" + __exception.GetType().Name + ").");
            return null;
        }
    }
}