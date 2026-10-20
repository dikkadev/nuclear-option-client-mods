using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace NuclearConnectivity
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;

        private Harmony? _harmony;

        internal ConfigEntry<bool> EnableSteamTimeline { get; private set; } = null!;
        internal ConfigEntry<bool> MarkPlayerDeath { get; private set; } = null!;
        internal ConfigEntry<bool> MarkAirToAirKills { get; private set; } = null!;
        internal ConfigEntry<bool> MarkAirToGroundKills { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

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
            SteamTimelineUnitDisabledHooks.Reset();
        }

        private void Update() => SteamTimelineUnitDisabledHooks.ObserveLocalAircraft();

        private void BindConfig()
        {
            EnableSteamTimeline = Config.Bind(
                "Steam Timeline",
                "Enable Steam Timeline",
                true,
                "Enable Steam Timeline markers for supported NuclearConnectivity events."
            );

            MarkPlayerDeath = Config.Bind(
                "Steam Timeline",
                "Mark Player Death",
                true,
                "Add a Steam Timeline marker when your aircraft is lost."
            );

            MarkAirToAirKills = Config.Bind(
                "Steam Timeline",
                "Mark Air-to-Air Kills",
                true,
                "Add a Steam Timeline marker when you get credited with an aircraft kill."
            );

            MarkAirToGroundKills = Config.Bind(
                "Steam Timeline",
                "Mark Air-to-Ground Kills",
                false,
                "Add a Steam Timeline marker when you get credited with a ground, ship, or structure kill."
            );
        }
    }
}
