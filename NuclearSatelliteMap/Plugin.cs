using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace NuclearSatelliteMap
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal ManualLogSource Log => Logger;
        private SatelliteMapLayer layer = null!;

        internal ConfigEntry<bool> Enabled { get; private set; } = null!;
        internal ConfigEntry<string> DataFolder { get; private set; } = null!;
        internal ConfigEntry<bool> UseOnExpandedMap { get; private set; } = null!;
        internal ConfigEntry<bool> UseOnMinimizedMap { get; private set; } = null!;
        internal ConfigEntry<float> ExpandedMapOpacity { get; private set; } = null!;
        internal ConfigEntry<float> MinimizedMapOpacity { get; private set; } = null!;
        internal ConfigEntry<float> LandOpacity { get; private set; } = null!;
        internal ConfigEntry<float> WaterOpacity { get; private set; } = null!;
        internal ConfigEntry<float> NativeMapOpacity { get; private set; } = null!;
        internal ConfigEntry<float> Brightness { get; private set; } = null!;
        internal ConfigEntry<float> Contrast { get; private set; } = null!;
        internal ConfigEntry<float> Saturation { get; private set; } = null!;
        internal ConfigEntry<float> Gamma { get; private set; } = null!;
        internal ConfigEntry<Color> Tint { get; private set; } = null!;

        private void Awake()
        {
            BindConfig();
            layer = new SatelliteMapLayer(this);
            Logger.LogInfo($"Effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={System.Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)}"))}");
            Logger.LogInfo($"Satellite data folders: {string.Join(", ", layer.DataFolders())}");
            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        private void Update() => layer.Update();

        private void OnDestroy() => layer.Shutdown();

        private void BindConfig()
        {
            const string general = "General", visibility = "Visibility", color = "Color";

            Enabled = Config.Bind(general, "Enabled", true,
                "Replace the native map image with satellite imagery when matching data is installed. Disabling restores the native map and frees the texture.");
            DataFolder = Config.Bind(general, "Data Folder", "",
                "Folder containing .nosat satellite map files. Empty searches BepInEx/NuclearSatelliteMap and the plugin's maps folder. A relative path is relative to the BepInEx folder.");

            UseOnExpandedMap = Config.Bind(visibility, "Use On Expanded Map", true,
                "Show satellite imagery on the expanded (full-screen) map. When off, that view shows the native map.");
            UseOnMinimizedMap = Config.Bind(visibility, "Use On Minimized Map", true,
                "Show satellite imagery on the minimized HUD map and its cockpit copy. When off, that view shows the native map.");
            ExpandedMapOpacity = Config.Bind(visibility, "Expanded Map Opacity", 1f,
                new ConfigDescription("Overall opacity of the satellite layer on the expanded map. Applies immediately.", new AcceptableValueRange<float>(0f, 1f)));
            MinimizedMapOpacity = Config.Bind(visibility, "Minimized Map Opacity", 1f,
                new ConfigDescription("Overall opacity of the satellite layer on the minimized HUD map. Applies immediately.", new AcceptableValueRange<float>(0f, 1f)));
            LandOpacity = Config.Bind(visibility, "Land Opacity", 1f,
                new ConfigDescription("Opacity of land in the satellite image, before the overall opacity. Reprocesses the texture.", new AcceptableValueRange<float>(0f, 1f)));
            WaterOpacity = Config.Bind(visibility, "Water Opacity", 0f,
                new ConfigDescription("Opacity of water in the satellite image. 0 makes water fully transparent; higher values show the rendered sea. Reprocesses the texture.", new AcceptableValueRange<float>(0f, 1f)));
            NativeMapOpacity = Config.Bind(visibility, "Native Map Opacity", 0f,
                new ConfigDescription("Opacity of the native map image behind the satellite layer, visible through water and any transparency. 0 hides it; 1 keeps the theme's own opacity. Applies immediately.", new AcceptableValueRange<float>(0f, 1f)));

            Brightness = Config.Bind(color, "Brightness", 1f,
                new ConfigDescription("Color multiplier. Below 1 darkens the imagery so icons stand out. Reprocesses the texture.", new AcceptableValueRange<float>(0f, 3f)));
            Contrast = Config.Bind(color, "Contrast", 1f,
                new ConfigDescription("Contrast around middle gray. Below 1 flattens the imagery. Reprocesses the texture.", new AcceptableValueRange<float>(0f, 3f)));
            Saturation = Config.Bind(color, "Saturation", 1f,
                new ConfigDescription("Color saturation. 0 is grayscale; above 1 is more vivid. Reprocesses the texture.", new AcceptableValueRange<float>(0f, 3f)));
            Gamma = Config.Bind(color, "Gamma", 1f,
                new ConfigDescription("Midtone gamma. Above 1 brightens dark areas; below 1 darkens them. Reprocesses the texture.", new AcceptableValueRange<float>(.2f, 5f)));
            Tint = Config.Bind(color, "Tint", Color.white,
                "Color multiplied into the imagery after the other color settings; white leaves it unchanged. Its alpha is ignored. Reprocesses the texture.");

            DataFolder.SettingChanged += (sender, args) => layer.DataFolderChanged();
            // Any change restarts the debounce; only processed settings trigger a reload.
            Config.SettingChanged += (sender, args) => layer.SettingsChanged();
        }
    }
}
