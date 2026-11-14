using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearMapFocus
{
    // The camera states read the same mouse input as the expanded map and as windows:
    //   CameraTVState (flyby) reads Zoom View, Pan View and Tilt View with no condition at all.
    //   CameraCockpitState reads Zoom View unless the map is expanded, so it zooms behind menus and F1.
    //   CameraCockpitState (virtual joystick), CameraFreeState, CameraControlledState and
    //   CameraSelectionState turn the camera while Free Look is held, also over the expanded map.
    // CameraChaseState and CameraOrbitState already test Cursor.visible and are left alone.
    // The transpiler puts a CameraInput call behind each of those reads, which turns the value
    // into the neutral one while the input belongs to the map or a window. The read itself stays,
    // so another mod that patches the same read (the MapMouseSelect fork does) still finds it.
    // One patch class per method: the client API check reads the targets from the attributes.
    [HarmonyPatch(typeof(CameraCockpitState), "UpdateState")]
    internal static class CockpitCameraPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => CameraInputPatch.Apply(instructions, "CameraCockpitState.UpdateState", 6);
    }

    [HarmonyPatch(typeof(CameraTVState), "UpdateState")]
    internal static class FlybyCameraPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => CameraInputPatch.Apply(instructions, "CameraTVState.UpdateState", 3);
    }

    [HarmonyPatch(typeof(CameraFreeState), "UpdateState")]
    internal static class FreeCameraPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => CameraInputPatch.Apply(instructions, "CameraFreeState.UpdateState", 3);
    }

    [HarmonyPatch(typeof(CameraControlledState), "UpdateState")]
    internal static class ControlledCameraPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => CameraInputPatch.Apply(instructions, "CameraControlledState.UpdateState", 3);
    }

    [HarmonyPatch(typeof(CameraSelectionState), "MoveCamera")]
    internal static class SelectionCameraPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            => CameraInputPatch.Apply(instructions, "CameraSelectionState.MoveCamera", 3);
    }

    internal static class CameraInputPatch
    {
        // Adds the calls only when the number of reads is the expected one; otherwise the method stays as it is.
        internal static IEnumerable<CodeInstruction> Apply(IEnumerable<CodeInstruction> instructions, string method, int expected)
        {
            var getAxis = AccessTools.Method(typeof(Player), nameof(Player.GetAxis), new[] { typeof(string) });
            var getButton = AccessTools.Method(typeof(Player), nameof(Player.GetButton), new[] { typeof(string) });
            var axis = AccessTools.Method(typeof(CameraInput), nameof(CameraInput.Axis));
            var button = AccessTools.Method(typeof(CameraInput), nameof(CameraInput.Button));
            var list = instructions.ToList();
            var reads = new List<(int Index, string Action, MethodInfo Filter)>();
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i - 1].opcode != OpCodes.Ldstr || !(list[i - 1].operand is string action)) continue;
                if (list[i].Calls(getAxis) && (action == CameraInput.Zoom || action == CameraInput.Pan || action == CameraInput.Tilt))
                    reads.Add((i, action, axis));
                else if (list[i].Calls(getButton) && action == CameraInput.FreeLook)
                    reads.Add((i, action, button));
            }
            if (reads.Count != expected)
            {
                Plugin.Log?.LogError($"{method} has {reads.Count} camera input reads instead of {expected}; that camera keeps the game's input.");
                return list;
            }
            // Back to front, so the earlier indices stay valid.
            for (int n = reads.Count - 1; n >= 0; n--)
            {
                list.InsertRange(reads[n].Index + 1, new[]
                {
                    new CodeInstruction(OpCodes.Ldstr, reads[n].Action),
                    new CodeInstruction(OpCodes.Call, reads[n].Filter),
                });
            }
            return list;
        }
    }

    internal static class CameraInput
    {
        internal const string Zoom = "Zoom View";
        internal const string Pan = "Pan View";
        internal const string Tilt = "Tilt View";
        internal const string FreeLook = "Free Look";

        private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";
        private static BaseUnityPlugin? configurationManager;
        private static PropertyInfo? displayingWindow;
        private static bool loggedZoom;
        private static bool loggedLook;
        private static bool loggedFailure;

        // value is what the game just read for this action.
        internal static float Axis(float value, string action)
        {
            var settings = Plugin.Instance;
            if (value == 0f || settings == null || !settings.Enabled.Value) return value;
            try
            {
                if (action == Zoom)
                {
                    if (!BlockZoom(settings)) return value;
                    if (!loggedZoom)
                    {
                        loggedZoom = true;
                        Plugin.Log?.LogInfo("Kept the mouse wheel from zooming the camera (first time this session).");
                    }
                    return 0f;
                }
                if (!BlockLook(settings)) return value;
                LogLook();
                return 0f;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return value;
            }
        }

        internal static bool Button(bool value, string action)
        {
            var settings = Plugin.Instance;
            if (!value || settings == null || !settings.Enabled.Value) return value;
            try
            {
                if (!BlockLook(settings)) return value;
                LogLook();
                return false;
            }
            catch (Exception exception)
            {
                LogFailure(exception);
                return value;
            }
        }

        private static bool BlockZoom(Settings settings)
            => (settings.NoZoomOnMap.Value && DynamicMap.mapMaximized)
                || (settings.NoZoomUnderWindows.Value && (Cursor.visible || ConfigurationWindowOpen()));

        private static bool BlockLook(Settings settings)
            => settings.NoLookOnMap.Value && DynamicMap.mapMaximized;

        // The same test as NuclearOption.Bindings: the public DisplayingWindow property, when that plugin is loaded.
        private static bool ConfigurationWindowOpen()
        {
            if (configurationManager == null)
            {
                if (!Chainloader.PluginInfos.TryGetValue(ConfigurationManagerGuid, out var info) || info.Instance == null) return false;
                configurationManager = info.Instance;
                displayingWindow = configurationManager.GetType().GetProperty("DisplayingWindow", BindingFlags.Public | BindingFlags.Instance);
            }
            if (displayingWindow == null || displayingWindow.PropertyType != typeof(bool)) return false;
            return displayingWindow.GetValue(configurationManager, null) is bool open && open;
        }

        private static void LogLook()
        {
            if (loggedLook) return;
            loggedLook = true;
            Plugin.Log?.LogInfo("Kept the camera from turning while the expanded map is open (first time this session).");
        }

        private static void LogFailure(Exception exception)
        {
            if (loggedFailure) return;
            loggedFailure = true;
            Plugin.Log?.LogError($"Camera input rule failed; the camera keeps the game's input: {exception}");
        }
    }
}