using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace NuclearTrackAge
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private Harmony? _harmony;

        internal ConfigEntry<bool> Enabled { get; private set; } = null!;
        internal ConfigEntry<bool> ShowOnlyOutdatedTracks { get; private set; } = null!;
        internal ConfigEntry<float> MinimumAgeSeconds { get; private set; } = null!;
        internal ConfigEntry<float> StaleThresholdSeconds { get; private set; } = null!;
        internal ConfigEntry<bool> ShowInDynamicMapMarkers { get; private set; } = null!;
        internal ConfigEntry<bool> ShowInTargetList { get; private set; } = null!;
        internal ConfigEntry<bool> ShowInTargetCamera { get; private set; } = null!;
        internal ConfigEntry<bool> ShowInHudTargetInfo { get; private set; } = null!;

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
            => Logger.LogInfo($"Plugin Start reached at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");

        private void OnDestroy()
        {
            Logger.LogInfo($"Plugin OnDestroy cleanup at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");
            _harmony?.UnpatchSelf();
        }

        private void BindConfig()
        {
            Enabled = Config.Bind(
                "Track Age",
                "Enabled",
                true,
                "Show how old enemy target tracking data is in target UI readouts."
            );

            ShowOnlyOutdatedTracks = Config.Bind(
                "Track Age",
                "Show Only Outdated Tracks",
                true,
                "Only show an age label after the game considers the target position stale. Disable to show ages for fresh tracks too."
            );

            MinimumAgeSeconds = Config.Bind(
                "Track Age",
                "Minimum Age Seconds",
                4f,
                new ConfigDescription(
                    "Do not show an age label until the track is at least this old.",
                    new AcceptableValueRange<float>(0f, 3600f)
                )
            );

            StaleThresholdSeconds = Config.Bind(
                "Track Age",
                "Stale Threshold Seconds",
                35f,
                new ConfigDescription(
                    "Age in seconds at which labels switch from AGE to STALE.",
                    new AcceptableValueRange<float>(1f, 7200f)
                )
            );

            ShowInDynamicMapMarkers = Config.Bind(
                "Surfaces",
                "Show In Dynamic Map Markers",
                true,
                "Append track age to target info labels on the maximized dynamic map."
            );

            ShowInTargetList = Config.Bind(
                "Surfaces",
                "Show In Target List",
                true,
                "Append track age to rows in the target selector list."
            );

            ShowInTargetCamera = Config.Bind(
                "Surfaces",
                "Show In Target Camera",
                true,
                "Append track age to the target camera grid readout."
            );

            ShowInHudTargetInfo = Config.Bind(
                "Surfaces",
                "Show In HUD Target Info",
                true,
                "Append track age to the small HUD target info readout for the selected target."
            );
        }
    }
}
