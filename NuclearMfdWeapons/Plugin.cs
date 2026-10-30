using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearBindings;
using UnityEngine;
namespace NuclearMfdWeapons
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin? Instance;
        internal ConfigEntry<bool> Enabled = null!, Stock = null!, Replace = null!, Preview = null!, Capacitor = null!;
        internal ConfigEntry<float> Duration = null!;
        internal InputBinding Toggle = null!;
        private Harmony? harmony;
        private DisplayController? display;
        private TacScreen? screen;
        private Aircraft? aircraft;
        private bool custom = true, dirty, failed;
        private int warnings;
        private int readyMessages;
        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Enable cockpit weapon display.");
            Stock = Config.Bind("Display", "Show Stock Weapon HUD", false, "Keep the stock weapon and countermeasure readouts and aircraft outline while custom content is visible.");
            Capacitor = Config.Bind("Display", "Show Capacitor HUD", false, "Show the native capacitor indicator at the top-right screen edge while custom content is visible. Independent of Show Stock Weapon HUD.");
            Replace = Config.Bind("Display", "Replace Cockpit Content", true, "Replace the selected cockpit panel; Chicane keeps its flight instruments.");
            Preview = Config.Bind("Display", "Loadout Preview", true, "Briefly show all stations on initial display and weapon changes.");
            Duration = Config.Bind("Display", "Loadout Preview Duration", 1f, new ConfigDescription("Seconds to show loadout preview.", new AcceptableValueRange<float>(0.1f, 10f)));
            Toggle = InputBinding.Bind(Config, Logger, "Controls", "Toggle Cockpit Page", "F8", "Switch custom/stock cockpit page.");
            Enabled.SettingChanged += Changed; Stock.SettingChanged += Changed; Capacitor.SettingChanged += Changed; Replace.SettingChanged += Changed; Preview.SettingChanged += Changed; Duration.SettingChanged += Changed; Toggle.Entry.SettingChanged += Changed;
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID); harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Loaded NuclearMfdWeapons " + MyPluginInfo.PLUGIN_VERSION + " build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId + " owned patches=" + harmony.GetPatchedMethods().Count());
        }
        private void Changed(object sender, EventArgs e) { dirty = true; failed = false; }
        internal void Initialize(TacScreen value, Aircraft owner)
        {
            try { Clear(); screen = value; aircraft = owner; custom = true; failed = false; Create(); }
            catch (Exception e) { Fail(e); }
        }
        private void Create()
        {
            if (!isActiveAndEnabled || !Enabled.Value || failed || screen == null || aircraft == null || SceneSingleton<CombatHUD>.i == null || SceneSingleton<CombatHUD>.i.aircraft != aircraft || aircraft.disabled || aircraft.HasEjected()) return;
            display = DisplayController.Create(screen, aircraft, this);
        }
        // Apply stock visibility after game Update weapon-selection callbacks and before rendering.
        private void LateUpdate()
        {
            try
            {
                if (screen == null || aircraft == null || SceneSingleton<CombatHUD>.i == null || SceneSingleton<CombatHUD>.i.aircraft != aircraft || aircraft.disabled || aircraft.HasEjected()) { Clear(); return; }
                if (dirty) { DisposeDisplay(); dirty = false; Create(); }
                if (!Enabled.Value) { DisposeDisplay(); return; }
                if (display == null && !failed) Create();
                if (Toggle.IsDown()) custom = !custom;
                display?.Refresh(custom);
            }
            catch (Exception e) { Fail(e); }
        }
        internal void ScreenDestroyed(TacScreen value) { if (value == screen) Clear(); }
        private void DisposeDisplay()
        {
            var previous = display; display = null;
            try { previous?.Dispose(); } catch (Exception e) { if (warnings++ < 5) Logger.LogWarning("Cockpit weapons cleanup: " + e.Message); }
        }
        private void Clear() { DisposeDisplay(); screen = null; aircraft = null; }
        private void Fail(Exception e) { DisposeDisplay(); failed = true; if (warnings++ < 5) Logger.LogWarning("Cockpit weapons restored stock UI: " + e); }
        internal void Unavailable(string reason) { failed = true; if (warnings++ < 5) Logger.LogInfo("Cockpit weapons unavailable: " + reason); }
        internal void DisplayReady(string mapping) { if (readyMessages++ < 10) Logger.LogInfo("Cockpit weapons display ready: " + mapping); }
        private void OnDisable() { DisposeDisplay(); }
        private void OnEnable() { dirty = true; }
        private void OnDestroy()
        {
            Enabled.SettingChanged -= Changed; Stock.SettingChanged -= Changed; Capacitor.SettingChanged -= Changed; Replace.SettingChanged -= Changed;
            Preview.SettingChanged -= Changed; Duration.SettingChanged -= Changed; Toggle.Entry.SettingChanged -= Changed;
            Clear(); harmony?.UnpatchSelf(); Instance = null;
        }
    }
    [HarmonyPatch(typeof(TacScreen), "Initialize")]
    internal static class InitializePatch
    {
        private static void Postfix(TacScreen __instance, Aircraft aircraft) { Plugin.Instance?.Initialize(__instance, aircraft); }
    }
    [HarmonyPatch(typeof(TacScreen), "OnDestroy")]
    internal static class DestroyPatch
    {
        private static void Prefix(TacScreen __instance) { Plugin.Instance?.ScreenDestroyed(__instance); }
    }
}
