using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;
using UnityEngine;

namespace NuclearMapTrails
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private Harmony? _harmony;

        internal ConfigEntry<bool> ShowTrails { get; private set; } = null!;
        internal ConfigEntry<TrailTeams> Teams { get; private set; } = null!;
        internal ConfigEntry<bool> ShowOwnTrail { get; private set; } = null!;
        internal ConfigEntry<bool> ShowOnlyOnMaximizedMap { get; private set; } = null!;
        internal ConfigEntry<float> TrailDuration { get; private set; } = null!;
        internal ConfigEntry<float> SampleInterval { get; private set; } = null!;
        internal ConfigEntry<bool> FadeOut { get; private set; } = null!;
        internal ConfigEntry<float> NewestOpacity { get; private set; } = null!;
        internal ConfigEntry<float> OldestOpacity { get; private set; } = null!;
        internal ConfigEntry<bool> AltitudeBrightness { get; private set; } = null!;
        internal ConfigEntry<float> LowAltitude { get; private set; } = null!;
        internal ConfigEntry<float> HighAltitude { get; private set; } = null!;
        internal ConfigEntry<float> LowAltitudeBrightness { get; private set; } = null!;
        internal ConfigEntry<float> LineWidth { get; private set; } = null!;
        internal ConfigEntry<TrailColorMode> ColorMode { get; private set; } = null!;
        internal ConfigEntry<Color> CustomFriendlyColor { get; private set; } = null!;
        internal ConfigEntry<Color> CustomEnemyColor { get; private set; } = null!;

        private void Awake()
        {
            Instance = this;
            BindConfig();

            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Harmony installation owns {_harmony.GetPatchedMethods().Count()} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={System.Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");

            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        private void Start()
            => Logger.LogInfo($"Plugin Start reached at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s.");

        private void OnDestroy()
        {
            Logger.LogInfo($"Plugin OnDestroy cleanup at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s.");
            _harmony?.UnpatchSelf();
            TrailLayer.DestroyCurrent();
        }

        private void BindConfig()
        {
            const string section = "Map Trails";

            ShowTrails = Config.Bind(
                section,
                "Show Trails",
                true,
                "Draw recent flight paths behind player-controlled aircraft on the dynamic map. Disabling clears the recorded trails."
            );

            Teams = Config.Bind(
                section,
                "Teams",
                TrailTeams.Friendly,
                "Friendly: trails only for players on your own team (fair default). AllVisible: also enemy players, recorded only while your team actively observes them, from the same track data as the native map icon. Spectators always see every player."
            );

            ShowOwnTrail = Config.Bind(
                section,
                "Show Own Trail",
                true,
                "Draw a trail behind your own aircraft."
            );

            ShowOnlyOnMaximizedMap = Config.Bind(
                section,
                "Show Only On Maximized Map",
                false,
                "Only draw trails when the dynamic map is maximized. Trails are still recorded while hidden."
            );

            TrailDuration = Config.Bind(
                section,
                "Trail Duration",
                90f,
                new ConfigDescription("How many seconds of flight path each trail keeps.", new AcceptableValueRange<float>(5f, 600f))
            );

            SampleInterval = Config.Bind(
                section,
                "Sample Interval",
                0.5f,
                new ConfigDescription("Seconds between recorded trail points. Lower values make smoother turns.", new AcceptableValueRange<float>(0.1f, 5f))
            );

            FadeOut = Config.Bind(
                section,
                "Fade Out",
                true,
                "Fade each trail from Newest Opacity at the aircraft to Oldest Opacity at its tail. When disabled, the whole trail uses Newest Opacity."
            );

            NewestOpacity = Config.Bind(
                section,
                "Newest Opacity",
                0.85f,
                new ConfigDescription("Trail opacity next to the aircraft.", new AcceptableValueRange<float>(0f, 1f))
            );

            OldestOpacity = Config.Bind(
                section,
                "Oldest Opacity",
                0f,
                new ConfigDescription("Trail opacity at the oldest point when Fade Out is enabled.", new AcceptableValueRange<float>(0f, 1f))
            );

            AltitudeBrightness = Config.Bind(
                section,
                "Altitude Brightness",
                true,
                "Shade each trail point by the aircraft's altitude above sea level at that moment: darker when low, full color when high."
            );

            LowAltitude = Config.Bind(
                section,
                "Low Altitude",
                0f,
                new ConfigDescription("Altitude in metres at and below which trail points use Low Altitude Brightness.", new AcceptableValueRange<float>(0f, 20000f))
            );

            HighAltitude = Config.Bind(
                section,
                "High Altitude",
                8000f,
                new ConfigDescription("Altitude in metres at and above which trail points use full color.", new AcceptableValueRange<float>(0f, 20000f))
            );

            LowAltitudeBrightness = Config.Bind(
                section,
                "Low Altitude Brightness",
                0.60f,
                new ConfigDescription("Color brightness multiplier for low trail points; 1 disables the darkening.", new AcceptableValueRange<float>(0f, 1f))
            );

            LineWidth = Config.Bind(
                section,
                "Line Width",
                2f,
                new ConfigDescription("Trail width in UI pixels; it stays constant while zooming.", new AcceptableValueRange<float>(0.5f, 8f))
            );

            ColorMode = Config.Bind(
                section,
                "Color Mode",
                TrailColorMode.IconColor,
                "IconColor: follow the aircraft's current map icon color, including theme and team changes. FactionColor: the faction's own team color. Custom: the two custom colors below."
            );

            CustomFriendlyColor = Config.Bind(
                section,
                "Custom Friendly Color",
                new Color(0.3f, 0.75f, 1f, 1f),
                "Trail color for your team and your own aircraft when Color Mode is Custom."
            );

            CustomEnemyColor = Config.Bind(
                section,
                "Custom Enemy Color",
                new Color(1f, 0.35f, 0.3f, 1f),
                "Trail color for enemy players when Color Mode is Custom."
            );

            ShowTrails.SettingChanged += (sender, args) => TrailLayer.ClearCurrentIfDisabled();
            Teams.SettingChanged += (sender, args) => TrailLayer.ClearCurrent();
        }
    }
}
