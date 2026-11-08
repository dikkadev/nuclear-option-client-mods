using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearMapPan
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

        // The patch keeps working without this component, so nothing is torn down here.
        private void OnDestroy()
            => Logger.LogInfo($"Plugin component destroyed at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s; the map speed keeps working.");
    }

    internal sealed class Settings
    {
        internal ConfigEntry<bool> Enabled { get; }
        internal ConfigEntry<float> TopSpeed { get; }
        internal ConfigEntry<float> CrossAt { get; }
        internal ConfigEntry<float> GameSpeedAt { get; }
        internal ConfigEntry<float> Belly { get; }
        internal ConfigEntry<float> KeyboardSpeed { get; }

        internal Settings(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Change how fast the expanded map moves. Off keeps the game's speed.");
            TopSpeed = config.Bind("Controller", "Top Speed", 2.75f, new ConfigDescription(
                "Map speed at full stick, as a multiple of the game's speed at full stick.",
                new AcceptableValueRange<float>(1.05f, 10f)));
            CrossAt = config.Bind("Controller", "Matches Game At", 0.55f, new ConfigDescription(
                "Stick deflection (0 to 1) where the map moves exactly as fast as in the game. Below it the map is slower than the game, above it faster.",
                new AcceptableValueRange<float>(0.05f, 0.9f)));
            GameSpeedAt = config.Bind("Controller", "Game Top Speed At", 0.75f, new ConfigDescription(
                "Stick deflection (0 to 1) where the map reaches the game's full-stick speed. Keep it above Matches Game At.",
                new AcceptableValueRange<float>(0.1f, 0.95f)));
            Belly = config.Bind("Controller", "Belly", 0.45f, new ConfigDescription(
                "How slowly the map starts near the stick centre. 1 starts flat for fine positioning; 0 starts at the game's speed.",
                new AcceptableValueRange<float>(0f, 1f)));
            KeyboardSpeed = config.Bind("Keyboard", "Speed", 1f, new ConfigDescription(
                "How fast keys bound to Move Map move the expanded map, as a multiple of the game's speed. Dragging with the mouse is not changed.",
                new AcceptableValueRange<float>(0.25f, 10f)));

            // 0.1.0 had Controller / Speed and Stick Curve; drop them from saved files.
            foreach (var key in new[] { "Speed", "Stick Curve" })
            {
                var definition = new ConfigDefinition("Controller", key);
                config.Bind(definition, "", new ConfigDescription("Removed setting."));
                config.Remove(definition);
            }
            config.Save();
        }
    }
}
