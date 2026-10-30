using HarmonyLib;
using System;
using UnityEngine;

namespace NuclearSpectator
{
    // Per-frame work. Driven from two places, at most once per frame: the plugin
    // component's LateUpdate and a postfix on the game's own camera LateUpdate. A
    // separate DontDestroyOnLoad host object (0.1.0-0.1.3) never ran on the user's
    // client, while the game's camera code and patches did.
    internal static class SpectatorRuntime
    {
        private static readonly CockpitSession Cockpit = new CockpitSession();
        private static readonly InfoPanel Info = new InfoPanel();
        private static readonly CleanView CleanView = new CleanView();
        private static int lastFrame = -1;
        private static bool announced;

        internal static void Tick(string driver)
        {
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            Plugin.Once("runtime:" + driver, $"Spectator runtime ticking from {driver} at frame {Time.frameCount}.");
            if (!announced) Announce();
            try { AircraftCycler.Update(); }
            catch (Exception error) { Plugin.Report("aircraft cycling", error); }
            Cockpit.Update();
            try { PlaceholderHud.Update(Cockpit.HudActive); }
            catch (Exception error) { Plugin.Report("empty HUD hiding", error); }
            Info.Update();
            try { CleanView.Update(); }
            catch (Exception error) { Plugin.Report("hide all UI", error); CleanView.Dispose(); }
        }

        // One in-game message per process, so a test shows at a glance which build runs.
        private static void Announce()
        {
            var ui = SceneSingleton<GameplayUI>.i;
            if (ui == null || SceneSingleton<CameraStateManager>.i == null) return;
            announced = true;
            var key = Plugin.Instance?.HideAllUi.ToString() ?? "";
            try { ui.GameMessage($"NuclearSpectator {MyPluginInfo.PLUGIN_VERSION} is running. Hide All UI: {key}"); }
            catch (Exception error) { Plugin.Report("start message", error); }
            Plugin.Log?.LogInfo($"Spectator runtime active in a mission at frame {Time.frameCount}.");
        }
    }

    [HarmonyPatch(typeof(CameraStateManager), "LateUpdate")]
    internal static class CameraLateUpdatePatch
    {
        private static void Postfix()
        {
            try { SpectatorRuntime.Tick("camera"); }
            catch (Exception error) { Plugin.Report("spectator runtime", error); }
        }
    }
}
