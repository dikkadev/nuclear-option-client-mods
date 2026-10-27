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
        internal ConfigEntry<TextureDetail> Detail { get; private set; } = null!;
        internal ConfigEntry<bool> UseOnExpandedMap { get; private set; } = null!;
        internal ConfigEntry<bool> UseOnMinimizedMap { get; private set; } = null!;
        internal ConfigEntry<float> ExpandedMapOpacity { get; private set; } = null!;
        internal ConfigEntry<float> MinimizedMapOpacity { get; private set; } = null!;
        internal ConfigEntry<float> NativeMapOpacity { get; private set; } = null!;

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
            const string general = "General", visibility = "Visibility";

            Enabled = Config.Bind(general, "Enabled", true,
                "Replace the native map image with satellite imagery when matching data is installed. Disabling restores the native map and frees the texture.");
            DataFolder = Config.Bind(general, "Data Folder", "",
                "Folder containing .nosat satellite map files. Empty searches BepInEx/NuclearSatelliteMap and the plugin's maps folder. A relative path is relative to the BepInEx folder.");
            Detail = Config.Bind(general, "Texture Detail", TextureDetail.Full,
                "Full uses the data's full resolution (5 m per pixel: about 341 MiB of GPU memory for Heartland and 683 MiB for Ignus). Half skips the finest level for 10 m per pixel at a quarter of the GPU memory. Changing it reloads the map.");

            UseOnExpandedMap = Config.Bind(visibility, "Use On Expanded Map", true,
                "Show satellite imagery on the expanded (full-screen) map. When off, that view shows the native map.");
            UseOnMinimizedMap = Config.Bind(visibility, "Use On Minimized Map", true,
                "Show satellite imagery on the minimized HUD map and its cockpit copy. When off, that view shows the native map.");
            ExpandedMapOpacity = Config.Bind(visibility, "Expanded Map Opacity", .8f,
                new ConfigDescription("Overall opacity of the satellite layer on the expanded map. Water is always transparent.", new AcceptableValueRange<float>(0f, 1f)));
            MinimizedMapOpacity = Config.Bind(visibility, "Minimized Map Opacity", 1f,
                new ConfigDescription("Overall opacity of the satellite layer on the minimized HUD map. Water is always transparent.", new AcceptableValueRange<float>(0f, 1f)));
            NativeMapOpacity = Config.Bind(visibility, "Native Map Opacity", 0f,
                new ConfigDescription("Opacity of the native map image behind the satellite layer, visible through water and any transparency. 0 hides it; 1 keeps the theme's own opacity.", new AcceptableValueRange<float>(0f, 1f)));

            DataFolder.SettingChanged += (sender, args) => layer.DataFolderChanged();
        }
    }
}
