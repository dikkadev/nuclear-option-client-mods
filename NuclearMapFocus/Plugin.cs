using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearMapFocus
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        // Settings live outside the component: a destroyed plugin component reads as null,
        // which stopped every feature of NuclearSpectator on the user's client.
        internal static Settings? Instance { get; private set; }
        internal static ManualLogSource? Log { get; private set; }

        private void Awake()
        {
            Log = Logger;
            Instance = new Settings(Config);

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Harmony installation owns {harmony.GetPatchedMethods().Count()} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");

            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        // The patches keep working without this component, so nothing is torn down here.
        private void OnDestroy()
            => Logger.LogInfo($"Plugin component destroyed at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s; the camera input rules keep working.");
    }

    internal sealed class Settings
    {
        internal ConfigEntry<bool> Enabled { get; }
        internal ConfigEntry<bool> NoZoomOnMap { get; }
        internal ConfigEntry<bool> NoLookOnMap { get; }
        internal ConfigEntry<bool> NoZoomUnderWindows { get; }

        internal Settings(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Keep mouse input for the map and for windows away from the camera. Off keeps the game's behaviour.");
            NoZoomOnMap = config.Bind("Expanded Map", "Wheel Zooms Only The Map", true,
                "While the expanded map is open, the mouse wheel zooms the map and not the camera behind it.");
            NoLookOnMap = config.Bind("Expanded Map", "No Camera Look", true,
                "While the expanded map is open, the camera does not turn: Free Look (right mouse button by default) and the flyby view's mouse look do nothing.");
            NoZoomUnderWindows = config.Bind("Windows", "Wheel Does Not Zoom Under Windows", true,
                "The mouse wheel does not zoom the camera while a menu or dialog shows the mouse cursor, or while the Configuration Manager window (F1) is open.");
        }
    }
}