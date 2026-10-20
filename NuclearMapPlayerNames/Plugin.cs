using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace NuclearMapPlayerNames
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private Harmony? _harmony;

        internal ConfigEntry<bool> ShowPlayerNames { get; private set; } = null!;
        internal ConfigEntry<bool> ShowOnlyOnMaximizedMap { get; private set; } = null!;
        internal ConfigEntry<bool> HideLocalPlayerName { get; private set; } = null!;
        internal ConfigEntry<int> FontSize { get; private set; } = null!;
        internal ConfigEntry<float> TextScale { get; private set; } = null!;
        internal ConfigEntry<float> OffsetX { get; private set; } = null!;
        internal ConfigEntry<float> OffsetY { get; private set; } = null!;

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
            ShowPlayerNames = Config.Bind(
                "Map Player Names",
                "Show Player Names",
                true,
                "Show multiplayer usernames next to player-controlled aircraft on the dynamic map. Disable this to restore the stock hover-only behavior."
            );

            ShowOnlyOnMaximizedMap = Config.Bind(
                "Map Player Names",
                "Show Only On Maximized Map",
                false,
                "Only show player names when the dynamic map is maximized."
            );

            HideLocalPlayerName = Config.Bind(
                "Map Player Names",
                "Hide Local Player Name",
                true,
                "Do not show a map label for your own aircraft."
            );

            FontSize = Config.Bind(
                "Map Player Names",
                "Font Size",
                12,
                new ConfigDescription("Rendered username font size. Use Text Scale for the apparent map size; very small rendered fonts are hard to read in Unity UI.", new AcceptableValueRange<int>(8, 18))
            );

            TextScale = Config.Bind(
                "Map Player Names",
                "Text Scale",
                0.6f,
                new ConfigDescription("Visual scale for username labels. Lower values make labels smaller while keeping the rendered font legible.", new AcceptableValueRange<float>(0.25f, 1f))
            );

            OffsetX = Config.Bind(
                "Map Player Names",
                "Offset X",
                2f,
                "Horizontal label offset from the map icon, in UI pixels."
            );

            OffsetY = Config.Bind(
                "Map Player Names",
                "Offset Y",
                4f,
                "Vertical label offset from the map icon, in UI pixels."
            );
        }
    }
}
