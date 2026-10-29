using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NuclearMapMarkings
{
    internal enum RunwayBases
    {
        FriendlyAndNeutral,
        Friendly,
        All,
    }

    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private Harmony? _harmony;

        internal ConfigEntry<bool> Enabled { get; private set; } = null!;
        internal ConfigEntry<float> LineWidth { get; private set; } = null!;

        internal ConfigEntry<bool> RunwaysOnFullMap { get; private set; } = null!;
        internal ConfigEntry<bool> RunwaysOnMinimap { get; private set; } = null!;
        internal ConfigEntry<RunwayBases> RunwayBases { get; private set; } = null!;
        internal ConfigEntry<bool> ShowApproaches { get; private set; } = null!;
        internal ConfigEntry<float> MinApproachLength { get; private set; } = null!;
        internal ConfigEntry<float> MaxApproachLength { get; private set; } = null!;
        internal ConfigEntry<float> ShortRunway { get; private set; } = null!;
        internal ConfigEntry<float> LongRunway { get; private set; } = null!;
        internal ConfigEntry<float> TickSpacing { get; private set; } = null!;
        internal ConfigEntry<float> RunwayOpacity { get; private set; } = null!;

        internal ConfigEntry<bool> RingsOnFullMap { get; private set; } = null!;
        internal ConfigEntry<bool> RingsOnMinimap { get; private set; } = null!;
        internal ConfigEntry<string> RingDistances { get; private set; } = null!;
        internal ConfigEntry<float> RingOpacity { get; private set; } = null!;

        internal ConfigEntry<bool> PathsOnFullMap { get; private set; } = null!;
        internal ConfigEntry<bool> PathsOnMinimap { get; private set; } = null!;
        internal ConfigEntry<bool> ShortPathsForAll { get; private set; } = null!;
        internal ConfigEntry<float> ShortPathSeconds { get; private set; } = null!;
        internal ConfigEntry<float> LongPathSeconds { get; private set; } = null!;
        internal ConfigEntry<float> TurnFadeSeconds { get; private set; } = null!;
        internal ConfigEntry<float> PathOpacity { get; private set; } = null!;

        internal List<float> RingDistancesKm { get; private set; } = new List<float>();

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
            MarkingsLayer.DestroyCurrent();
        }

        private void BindConfig()
        {
            const string general = "General";
            const string runways = "Runways";
            const string rings = "Range Rings";
            const string paths = "Predicted Paths";

            Enabled = Config.Bind(general, "Enabled", true,
                "Master switch for all map markings. The minimap setting covers the HUD minimap and the NuclearMinimap cockpit copy.");
            LineWidth = Config.Bind(general, "Line Width", 1.5f,
                new ConfigDescription("Marking line width in UI pixels; it stays constant while zooming.", new AcceptableValueRange<float>(0.5f, 6f)));

            RunwaysOnFullMap = Config.Bind(runways, "Show On Full Map", true,
                "Draw runways to scale with dashed approach centerlines on the full map.");
            RunwaysOnMinimap = Config.Bind(runways, "Show On Minimap", true,
                "Draw runways and approach centerlines on the minimap.");
            RunwayBases = Config.Bind(runways, "Bases", NuclearMapMarkings.RunwayBases.FriendlyAndNeutral,
                "Which airbases get runway markings. Spectators always see every base.");
            ShowApproaches = Config.Bind(runways, "Show Approach Lines", true,
                "Draw dashed approach centerlines. When disabled, only the runways are drawn.");
            MinApproachLength = Config.Bind(runways, "Min Approach Length", 3f,
                new ConfigDescription("Approach centerline length in kilometres for runways up to Short Runway.", new AcceptableValueRange<float>(0.5f, 20f)));
            MaxApproachLength = Config.Bind(runways, "Max Approach Length", 7.5f,
                new ConfigDescription("Approach centerline length in kilometres for runways from Long Runway on. Lines between grow with runway length, more slowly for longer runways.", new AcceptableValueRange<float>(0.5f, 20f)));
            ShortRunway = Config.Bind(runways, "Short Runway", 600f,
                new ConfigDescription("Runway length in metres at and below which the approach uses Min Approach Length.", new AcceptableValueRange<float>(100f, 5000f)));
            LongRunway = Config.Bind(runways, "Long Runway", 2000f,
                new ConfigDescription("Runway length in metres at and above which the approach uses Max Approach Length.", new AcceptableValueRange<float>(200f, 6000f)));
            TickSpacing = Config.Bind(runways, "Tick Spacing", 1f,
                new ConfigDescription("Distance between cross ticks on approach centerlines, in kilometres.", new AcceptableValueRange<float>(0.5f, 10f)));
            RunwayOpacity = Config.Bind(runways, "Opacity", 0.8f,
                new ConfigDescription("Runway and approach line opacity.", new AcceptableValueRange<float>(0.05f, 1f)));

            RingsOnFullMap = Config.Bind(rings, "Show On Full Map", false,
                "Draw range rings around your aircraft on the full map.");
            RingsOnMinimap = Config.Bind(rings, "Show On Minimap", true,
                "Draw range rings around your aircraft on the minimap.");
            RingDistances = Config.Bind(rings, "Distances", "10, 20, 40",
                "Ring radii in kilometres, separated by commas. Up to 8 rings, each at most 500 km.");
            RingOpacity = Config.Bind(rings, "Opacity", 0.35f,
                new ConfigDescription("Range ring opacity.", new AcceptableValueRange<float>(0.05f, 1f)));

            PathsOnFullMap = Config.Bind(paths, "Show On Full Map", true,
                "Draw predicted paths on the full map.");
            PathsOnMinimap = Config.Bind(paths, "Show On Minimap", true,
                "Draw predicted paths on the minimap.");
            ShortPathsForAll = Config.Bind(paths, "Short Paths For All Units", true,
                "Draw a short path on every moving aircraft, vehicle and ship the map shows. Enemies are included only while your team observes them. Missiles never get paths.");
            ShortPathSeconds = Config.Bind(paths, "Short Path Seconds", 10f,
                new ConfigDescription("Prediction length for other aircraft in seconds. Vehicles and ships use three times this.", new AcceptableValueRange<float>(2f, 60f)));
            LongPathSeconds = Config.Bind(paths, "Long Path Seconds", 60f,
                new ConfigDescription("Prediction length for your aircraft, selected map units and your HUD targets, with a dot every 10 seconds. Vehicles and ships use three times this, with a dot every 30 seconds.", new AcceptableValueRange<float>(10f, 180f)));
            TurnFadeSeconds = Config.Bind(paths, "Turn Fade Seconds", 6f,
                new ConfigDescription("How quickly a predicted turn eases out, in seconds. The path bends by at most the current turn rate times this value, so a sustained turn is not drawn as a loop. 0 draws straight lines. Vehicles and ships use three times this.", new AcceptableValueRange<float>(0f, 30f)));
            PathOpacity = Config.Bind(paths, "Opacity", 0.8f,
                new ConfigDescription("Predicted path opacity.", new AcceptableValueRange<float>(0.05f, 1f)));

            RingDistancesKm = MarkingsGeometry.ParseDistances(RingDistances.Value, 500f);
            RingDistances.SettingChanged += (sender, args) => RingDistancesKm = MarkingsGeometry.ParseDistances(RingDistances.Value, 500f);
        }
    }
}
