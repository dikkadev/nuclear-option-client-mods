using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NuclearNightVision
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
            Instance.Enabled.SettingChanged += (sender, args) => { if (!Instance.Enabled.Value) NightVisionFade.Restore(); };

            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Harmony installation owns {harmony.GetPatchedMethods().Count()} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");

            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        // The patch keeps working without this component, so nothing is torn down here.
        private void OnDestroy()
            => Logger.LogInfo($"Plugin component destroyed at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s; the night vision fade keeps running.");
    }

    internal sealed class Settings
    {
        internal ConfigEntry<bool> Enabled { get; }
        internal ConfigEntry<float> FadeInSeconds { get; }
        internal ConfigEntry<float> FadeOutSeconds { get; }

        internal Settings(ConfigFile config)
        {
            const string section = "Night Vision";
            Enabled = config.Bind(section, "Enabled", true,
                "Fade between the normal view and night vision instead of switching instantly.");
            FadeInSeconds = config.Bind(section, "Fade In Seconds", 0.6f, new ConfigDescription(
                "Seconds to fade from the normal view into night vision. Use 0 for the game's instant switch.",
                new AcceptableValueRange<float>(0f, 5f)));
            FadeOutSeconds = config.Bind(section, "Fade Out Seconds", 0.4f, new ConfigDescription(
                "Seconds to fade from night vision back to the normal view. Use 0 for the game's instant switch.",
                new AcceptableValueRange<float>(0f, 5f)));
        }
    }
}
