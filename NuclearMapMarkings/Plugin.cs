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
        internal ConfigEntry<float> ShortPathMaxLength { get; private set; } = null!;
        internal ConfigEntry<float> LongPathMaxLength { get; private set; } = null!;
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
            MinApproachLength = Config.Bind(runways, "Min Approach Length", 2f,
                new ConfigDescription("Approach centerline length in kilometres for runways up to Short Runway.", new AcceptableValueRange<float>(0.5f, 20f)));
            MaxApproachLength = Config.Bind(runways, "Max Approach Length", 5f,
                new ConfigDescription("Approach centerline length in kilometres for runways from Long Runway on. Lines between grow with runway length, more slowly for longer runways.", new AcceptableValueRange<float>(0.5f, 20f)));
            ShortRunway = Config.Bind(runways, "Short Runway", 600f,
                new ConfigDescription("Runway length in metres at and below which the approach uses Min Approach Length.", new AcceptableValueRange<float>(100f, 5000f)));
            LongRunway = Config.Bind(runways, "Long Runway", 2000f,
                new ConfigDescription("Runway length in metres at and above which the approach uses Max Approach Length.", new AcceptableValueRange<float>(200f, 6000f)));
            TickSpacing = Config.Bind(runways, "Tick Spacing", 1f,
                new ConfigDescription("Distance between cross ticks on approach centerlines, in kilometres.", new AcceptableValueRange<float>(0.5f, 10f)));
            RunwayOpacity = Config.Bind(runways, "Opacity", 0.8f,
                new ConfigDescription("Runway and approach line opacity.", new AcceptableValueRange<float>(0.05f, 1f)));

            RingsOnFullMap = Config.Bind(rings, "Show On Full Map", true,
                "Draw range rings around your aircraft on the full map.");
            RingsOnMinimap = Config.Bind(rings, "Show On Minimap", true,
                "Draw range rings around your aircraft on the minimap, with lines 1.5 times as wide.");
            RingDistances = Config.Bind(rings, "Distances", "10, 20",
                "Ring radii in kilometres, separated by commas, for example 10, 20, 40. Up to 8 rings, each at most 500 km. Rings are gray dashed lines; each ring further out has wider gaps.");
            RingOpacity = Config.Bind(rings, "Opacity", 0.5f,
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
            ShortPathMaxLength = Config.Bind(paths, "Short Path Max Length", 2.5f,
                new ConfigDescription("Upper limit for short paths in kilometres. Paths grow with speed almost linearly when slow and level off towards this length. 0 means no limit.", new AcceptableValueRange<float>(0f, 100f)));
            LongPathMaxLength = Config.Bind(paths, "Long Path Max Length", 8f,
                new ConfigDescription("Upper limit for long paths in kilometres. Paths grow with speed almost linearly when slow and level off towards this length; dots stay 10 seconds apart, so faster units show fewer dots. 0 means no limit.", new AcceptableValueRange<float>(0f, 100f)));
            TurnFadeSeconds = Config.Bind(paths, "Turn Fade Seconds", 6f,
                new ConfigDescription("How quickly a predicted turn eases out, in seconds. The path bends by at most the current turn rate times this value, so a sustained turn is not drawn as a loop. 0 draws straight lines. Vehicles and ships use three times this.", new AcceptableValueRange<float>(0f, 30f)));
            PathOpacity = Config.Bind(paths, "Opacity", 0.8f,
                new ConfigDescription("Predicted path opacity.", new AcceptableValueRange<float>(0.05f, 1f)));

            UpgradeDefaults();

            RingDistancesKm = MarkingsGeometry.ParseDistances(RingDistances.Value, 500f);
            RingDistances.SettingChanged += (sender, args) => RingDistancesKm = MarkingsGeometry.ParseDistances(RingDistances.Value, 500f);
            // Markings redraw only when something changes; any setting change counts.
            Config.SettingChanged += (sender, args) => SettingsRevision++;
        }

        internal int SettingsRevision { get; private set; }

        // 0.1.3 and 0.1.5 changed defaults after client tests. Saved settings that still hold the previous default
        // move to the new default once; values the player changed are kept.
        private void UpgradeDefaults()
        {
            var revision = Config.Bind("Internal", "Defaults Revision", 0,
                new ConfigDescription("Marks which default changes were applied to this file. Do not edit.", null,
                    new ConfigurationManagerAttributes { Browsable = false }));
            if (revision.Value < 1)
            {
                Replace(MinApproachLength, 3f, 2f);
                Replace(MaxApproachLength, 7.5f, 5f);
                Replace(RingOpacity, 0.35f, 0.6f);
                if (!RingsOnFullMap.Value)
                {
                    RingsOnFullMap.Value = true;
                }

                revision.Value = 1;
                Logger.LogInfo("Moved settings that still held 0.1.2 defaults to the 0.1.3 defaults.");
            }

            if (revision.Value < 2)
            {
                if (MarkingsGeometry.ParseDistances(RingDistances.Value, 500f).SequenceEqual(new[] { 10f, 20f, 40f }))
                {
                    RingDistances.Value = "10, 20";
                }

                Replace(RingOpacity, 0.6f, 0.5f);
                revision.Value = 2;
                Logger.LogInfo("Moved settings that still held 0.1.4 range ring defaults to the 0.1.5 defaults.");
            }
        }

        private static void Replace(ConfigEntry<float> entry, float previous, float current)
        {
            if (Mathf.Abs(entry.Value - previous) < 1e-4f)
            {
                entry.Value = current;
            }
        }
    }

    // Read by name by BepInEx Configuration Manager.
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
    }
}
