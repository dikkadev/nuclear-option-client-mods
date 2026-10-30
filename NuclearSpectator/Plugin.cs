using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearBindings;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NuclearSpectator
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        // Plain objects, not the plugin component: BepInEx can destroy its shared manager
        // object (and this component) early, and the mod must keep running after that.
        internal static Settings? Instance { get; private set; }
        internal static ManualLogSource? Log { get; private set; }

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static Harmony? harmony;

        private void Awake()
        {
            Log = Logger;
            Instance = new Settings(Config, Logger);

            try
            {
                harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
                harmony.PatchAll(typeof(Plugin).Assembly);
            }
            catch (Exception error)
            {
                Logger.LogError("Harmony patching failed; features that need patches may be inactive: " + error);
            }
            Logger.LogInfo($"Harmony installation owns {harmony?.GetPatchedMethods().Count() ?? 0} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");
            Logger.LogInfo("Spectator HUD rewrites: " + HudScope.DescribeRewrites());
            try
            {
                if (!RotorWarmStartPatch.Available)
                    Logger.LogWarning("Rotor warm start is unavailable: a native rotor or propeller initializer was not found in this game version.");
            }
            catch (Exception error) { Logger.LogError("Rotor warm start setup failed: " + error); }

            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        private void Start()
            => Logger.LogInfo($"Plugin Start reached at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s.");

        // Also ticks without the camera patch, for example if patching failed.
        private void LateUpdate()
        {
            try { SpectatorRuntime.Tick("plugin"); }
            catch (Exception error) { Report("spectator runtime", error); }
        }

        // Deliberately keeps patches: this also runs when BepInEx destroys its shared
        // manager object during normal play, and the camera patch keeps the runtime going.
        private void OnDestroy()
            => Logger.LogInfo($"Plugin component destroyed at frame {Time.frameCount}, realtime {Time.realtimeSinceStartup:F2}s; the spectator runtime keeps running.");

        // Logs each failure scope once per process, with its stack.
        internal static void Report(string scope, Exception error)
        {
            if (Reported.Add("error:" + scope)) Log?.LogError($"{scope} failed: {error}");
        }

        internal static void Once(string key, string message)
        {
            if (Reported.Add(key)) Log?.LogInfo(message);
        }
    }

    internal sealed class Settings
    {
        private const float DefaultMoveSpeed = 7f;

        internal ConfigEntry<bool> Enabled { get; private set; } = null!;

        internal ConfigEntry<bool> ShowHud { get; private set; } = null!;
        internal ConfigEntry<bool> ShowScreens { get; private set; } = null!;
        internal ConfigEntry<bool> AnimateControls { get; private set; } = null!;
        internal ConfigEntry<bool> ShowTargetCamera { get; private set; } = null!;
        internal ConfigEntry<bool> HideEmptyHud { get; private set; } = null!;
        internal InputBinding HideAllUi { get; private set; } = null!;

        internal ConfigEntry<bool> CockpitMove { get; private set; } = null!;
        internal ConfigEntry<float> MoveSpeed { get; private set; } = null!;
        internal ConfigEntry<float> MaxMoveDistance { get; private set; } = null!;
        internal ConfigEntry<float> ExteriorDistance { get; private set; } = null!;
        internal ConfigEntry<bool> TriggersMoveVertically { get; private set; } = null!;
        internal InputBinding LockHorizon { get; private set; } = null!;

        internal ConfigEntry<bool> FixRotors { get; private set; } = null!;

        internal ConfigEntry<bool> ShowInfo { get; private set; } = null!;
        internal ConfigEntry<bool> InfoInCockpit { get; private set; } = null!;
        internal ConfigEntry<int> InfoFontSize { get; private set; } = null!;

        internal InputBinding NextAircraft { get; private set; } = null!;
        internal InputBinding PreviousAircraft { get; private set; } = null!;
        internal ConfigEntry<bool> IncludeAi { get; private set; } = null!;
        internal ConfigEntry<bool> KeepCockpitView { get; private set; } = null!;

        // Changes whenever a cockpit setting changes, so the current cockpit is rebuilt.
        internal int CockpitVersion { get; private set; }

        internal Settings(ConfigFile Config, ManualLogSource log)
        {
            const string general = "General";
            const string cockpit = "Cockpit View";
            const string movement = "Cockpit Movement";
            const string rotors = "Rotors";
            const string info = "Info Panel";
            const string cycling = "Aircraft Cycling";

            Enabled = Config.Bind(general, "Enabled", true,
                "Master switch for every spectator improvement.");
            HideAllUi = InputBinding.Bind(Config, log, general, "Hide All UI", "F10",
                "Toggle hiding every on-screen interface (HUD, chat, kill feed, map, panels) for clean video and screenshots. Cockpit screens stay on. Also works while you fly. The pause menu and full map still show.", allowFullMap: true);

            ShowHud = Config.Bind(cockpit, "Show HUD", true,
                "In the cockpit view of an aircraft you spectate, show its HUD: compass, pitch ladder, the helmet readouts, the aircraft's own HUD gauges and its damage display. The weapon and target HUD stays hidden.");
            ShowScreens = Config.Bind(cockpit, "Show Cockpit Screens", true,
                "In the cockpit view of an aircraft you spectate, run its cockpit MFD screens (radar, clock and the aircraft's instrument pages).");
            AnimateControls = Config.Bind(cockpit, "Animate Stick And Throttle", true,
                "In the cockpit view of an aircraft you spectate, move the cockpit stick and throttle with the pilot's inputs.");
            ShowTargetCamera = Config.Bind(cockpit, "Show Target Camera", true,
                "On the cockpit screens of an aircraft you spectate, show a target camera aimed at the pilot's first selected target. The game's target information overlay is not shown. Not used with NuclearMinimap installed: its cockpit map takes that screen area the whole time you spectate.");
            HideEmptyHud = Config.Bind(cockpit, "Hide Empty HUD", true,
                "While you have no aircraft of your own, never show the HUD with placeholder text (for example after closing the map in a cockpit view). The real HUD of a spectated aircraft still shows.");

            CockpitMove = Config.Bind(movement, "Enabled", true,
                "In the cockpit view of an aircraft you spectate, move the view with the free-camera move controls (left stick, WASD; Left Shift is faster). It stays attached to the aircraft. The view reset (Center) control returns to the pilot's eye point.");
            MoveSpeed = Config.Bind(movement, "Move Speed", DefaultMoveSpeed,
                new ConfigDescription("Speed at full stick, in metres per second.", new AcceptableValueRange<float>(0.5f, 50f)));
            MaxMoveDistance = Config.Bind(movement, "Max Distance", 25f,
                new ConfigDescription("How far from the pilot's eye point the view can move, in metres.", new AcceptableValueRange<float>(1f, 200f)));
            ExteriorDistance = Config.Bind(movement, "Show Exterior Beyond", 1.5f,
                new ConfigDescription("Beyond this distance from the pilot's eye point, show the aircraft's outside and its pilot instead of the cockpit interior, in metres.", new AcceptableValueRange<float>(0.5f, 50f)));
            TriggersMoveVertically = Config.Bind(movement, "Triggers Move Up And Down", true,
                "On a gamepad, the right trigger moves the view up and the left trigger down.");
            LockHorizon = InputBinding.Bind(Config, log, movement, "Lock Horizon", "",
                "Toggle a level view in the spectator cockpit: it follows the aircraft's heading but not its pitch or roll, and mouse look and head tracking turn relative to the horizon. Unbound by default.");
            // 0.1.0-0.1.2 defaulted to 5 m/s; the user asked for 7 as the default.
            if (MoveSpeed.Value == 5f) MoveSpeed.Value = DefaultMoveSpeed;

            FixRotors = Config.Bind(rotors, "Fix Rotors Of Aircraft Already In Flight", true,
                "When another player's helicopter or propeller aircraft first appears on your client while already flying (for example after you join or start spectating), start its rotors at flight speed. Without this, the game starts them folded and stopped, and they take about half a minute to spin up while the aircraft flies.");

            ShowInfo = Config.Bind(info, "Show Info Panel", true,
                "While you spectate an aircraft, show its pilot, type, speed, altitude and heading at the bottom of the screen.");
            InfoInCockpit = Config.Bind(info, "Show In Cockpit View", false,
                "Also show the info panel in the cockpit view, where the HUD already shows most of this.");
            InfoFontSize = Config.Bind(info, "Font Size", 22,
                new ConfigDescription("Info panel text size.", new AcceptableValueRange<int>(10, 48)));

            NextAircraft = InputBinding.Bind(Config, log, cycling, "Next Aircraft", "",
                "While you have no aircraft of your own, follow the next aircraft (players sorted by name). Unbound by default.", allowFullMap: true);
            PreviousAircraft = InputBinding.Bind(Config, log, cycling, "Previous Aircraft", "",
                "While you have no aircraft of your own, follow the previous aircraft. Unbound by default.", allowFullMap: true);
            IncludeAi = Config.Bind(cycling, "Include AI Aircraft", false,
                "Also cycle through AI aircraft, after the player aircraft.");
            KeepCockpitView = Config.Bind(cycling, "Keep Cockpit View", true,
                "When you switch aircraft from the cockpit view, stay in the cockpit view of the next aircraft.");

            Enabled.SettingChanged += OnCockpitSettingChanged;
            ShowHud.SettingChanged += OnCockpitSettingChanged;
            ShowScreens.SettingChanged += OnCockpitSettingChanged;
            ShowTargetCamera.SettingChanged += OnCockpitSettingChanged;
            AnimateControls.SettingChanged += OnCockpitSettingChanged;
        }

        private void OnCockpitSettingChanged(object sender, EventArgs args) => CockpitVersion++;
    }
}