using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearMapPan
{
    // DynamicMap.MapControls (expanded map only) reads player.GetAxis("Move Map Horizontal")
    // and ("Move Map Vertical") and moves positionOffset by axis * 300 * unscaledDeltaTime / zoom.
    // The transpiler sends those two reads through MapPan.GetAxis, so the native code still
    // applies frame time, zoom, map edge clamping and onMapChanged.
    [HarmonyPatch(typeof(DynamicMap), "MapControls")]
    internal static class MapControlsPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getAxis = AccessTools.Method(typeof(Player), nameof(Player.GetAxis), new[] { typeof(string) });
            var replacement = AccessTools.Method(typeof(MapPan), nameof(MapPan.GetAxis));
            var list = instructions.ToList();
            int replaced = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (!list[i].Calls(getAxis) || list[i - 1].opcode != OpCodes.Ldstr) continue;
                var action = list[i - 1].operand as string;
                if (action != MapPan.Horizontal && action != MapPan.Vertical) continue;
                list[i].opcode = OpCodes.Call;
                list[i].operand = replacement;
                replaced++;
            }
            if (replaced != 2)
            {
                Plugin.Log?.LogError($"DynamicMap.MapControls has {replaced} Move Map axis reads instead of 2; the map keeps the game's speed.");
                return instructions;
            }
            return list;
        }
    }

    internal static class MapPan
    {
        internal const string Horizontal = "Move Map Horizontal";
        internal const string Vertical = "Move Map Vertical";

        private static int frame = -1;
        private static Player? framePlayer;
        private static float frameScale = 1f;
        private static bool loggedController;
        private static bool loggedKeyboard;
        private static bool loggedFailure;

        internal static float GetAxis(Player player, string action)
        {
            float value = player.GetAxis(action);
            var settings = Plugin.Instance;
            if (value == 0f || settings == null || !settings.Enabled.Value) return value;
            try
            {
                if (frame != Time.frameCount || !ReferenceEquals(framePlayer, player))
                {
                    frame = Time.frameCount;
                    framePlayer = player;
                    frameScale = FrameScale(player, settings);
                }
                return value * frameScale;
            }
            catch (Exception exception)
            {
                if (!loggedFailure)
                {
                    loggedFailure = true;
                    Plugin.Log?.LogError($"Map move speed failed; the map keeps the game's speed: {exception}");
                }
                return value;
            }
        }

        private static float FrameScale(Player player, Settings settings)
        {
            float horizontal = player.GetAxis(Horizontal);
            float vertical = player.GetAxis(Vertical);
            bool controller = FromJoystick(player, Horizontal) || FromJoystick(player, Vertical);
            float scale = PanMath.Scale(horizontal, vertical, controller,
                settings.CrossAt.Value, settings.GameSpeedAt.Value, settings.TopSpeed.Value, settings.Belly.Value,
                settings.KeyboardSpeed.Value);
            if (controller && !loggedController)
            {
                loggedController = true;
                Plugin.Log?.LogInfo($"Expanded map moved with a controller: x{settings.TopSpeed.Value:0.##} at full stick, matches the game at {settings.CrossAt.Value:0.##}, game top speed at {settings.GameSpeedAt.Value:0.##}, belly {settings.Belly.Value:0.##}.");
            }
            else if (!controller && !loggedKeyboard)
            {
                loggedKeyboard = true;
                Plugin.Log?.LogInfo($"Expanded map moved with keys: x{settings.KeyboardSpeed.Value:0.##}.");
            }
            return scale;
        }

        private static bool FromJoystick(Player player, string action)
        {
            var sources = player.GetCurrentInputSources(action);
            for (int i = 0; i < sources.Count; i++)
                if (sources[i].controllerType == ControllerType.Joystick) return true;
            return false;
        }
    }
}
