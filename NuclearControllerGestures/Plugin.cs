using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearBindings;
using Rewired;
using UnityEngine;

namespace NuclearControllerGestures
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const int Slots = 8;
        internal static Plugin? Instance;
        internal readonly NativeActionPulse Pulse = new NativeActionPulse();
        private readonly InputBinding[] controls = new InputBinding[Slots];
        private readonly ConfigEntry<string>?[] actions = new ConfigEntry<string>?[Slots];
        // One detector per slot and physical control, so presses on different controls never combine.
        private readonly Dictionary<string, DoublePressGesture> detectors = new Dictionary<string, DoublePressGesture>();
        private readonly HashSet<string> seen = new HashSet<string>();
        private ConfigEntry<bool> enabledSetting = null!;
        private ConfigEntry<float> gap = null!, duration = null!, cooldown = null!;
        private Harmony? harmony;
        private Rewired.Player? player;
        private Aircraft? aircraft;
        private string topology = "";
        private bool initialized, subscribed;
        private int warnings;

        private static string Section(int slot) => "Double Press " + (slot + 1);

        private void Awake()
        {
            Instance = this;
            enabledSetting = Config.Bind("General", "Enabled", true, "Enable independent controller double presses. Normal game bindings continue to run.");
            gap = Config.Bind("Timing", "Maximum Gap", .30f, new ConfigDescription("Maximum seconds from first release to second press.", new AcceptableValueRange<float>(.05f, 1f)));
            duration = Config.Bind("Timing", "Maximum Press Duration", .25f, new ConfigDescription("Each press must be released within this many seconds. Holds cancel the gesture.", new AcceptableValueRange<float>(.05f, 1f)));
            cooldown = Config.Bind("Timing", "Cooldown", .30f, new ConfigDescription("Seconds after a recognized double press before another gesture can begin. Release to neutral after cooldown.", new AcceptableValueRange<float>(0f, 2f)));
            for (int i = 0; i < Slots; i++)
                controls[i] = InputBinding.Bind(Config, Logger, Section(i), "Control", "",
                    "Press this control twice quickly to run this double press's Action. The control's normal game bindings still run on both presses.");
            Config.SettingChanged += (_, __) => Cancel();
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Loaded NuclearControllerGestures " + MyPluginInfo.PLUGIN_VERSION + " build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId);
        }

        private void Update()
        {
            if (!ReInput.isReady) { Cancel(); return; }
            if (!initialized)
            {
                // Resolve the game's own names after its Rewired definitions are available.
                var names = new[] { "Unbound" }.Concat(ReInput.mapping.UserAssignableActions.Where(a => a.type == InputActionType.Button).Select(a => a.name).Distinct().OrderBy(n => n, StringComparer.Ordinal)).ToArray();
                for (int i = 0; i < Slots; i++)
                    actions[i] = Config.Bind(Section(i), "Action", "Unbound", new ConfigDescription("Native button action after two short presses and releases of Control. Unbound disables this double press; normal bindings remain active.", new AcceptableValueList<string>(names)));
                MigrateNamedControls(names);
                initialized = true;
            }
            if (!subscribed) { ReInput.InputSourceUpdateEvent += Refresh; subscribed = true; }
            if (Blocked()) Cancel();
        }

        // 0.1.x had one action setting per named gamepad control under [Double Press Actions].
        private void MigrateNamedControls(string[] names)
        {
            var old = GestureMigration.Read(name => BindingMigration.TakeLegacy(Config, "Double Press Actions", name));
            if (old.Count == 0) return;
            int slot = 0;
            foreach (var (control, action) in old)
            {
                while (slot < Slots && (controls[slot].Rows.Count > 0 || actions[slot]!.Value != "Unbound")) slot++;
                if (slot >= Slots) { Logger.LogWarning("No free double press for the old " + control + " = " + action + " setting; it was not kept."); continue; }
                if (Array.IndexOf(names, action) < 0) { Logger.LogWarning("The old " + control + " = " + action + " setting names an unknown action; it was not kept."); continue; }
                controls[slot].Entry.Value = control;
                actions[slot]!.Value = action;
                Logger.LogInfo("Moved the old " + control + " = " + action + " setting to [" + Section(slot) + "].");
            }
            Config.Save();
        }

        private static Aircraft? LocalAircraft() => GameManager.GetLocalAircraft(out var value) && value != null && !value.disabled && !value.HasEjected() ? value : null;
        private bool Blocked() => !enabledSetting.Value || !isActiveAndEnabled || GestureInput.IsBlocked() || !GameManager.flightControlsEnabled || LocalAircraft() == null ||
            controls.Any(c => c.Capturing);

        private void Refresh()
        {
            try
            {
                if (!initialized || !ReInput.isReady || Blocked()) { Cancel(); return; }
                var currentPlayer = GameManager.playerInput;
                var currentAircraft = LocalAircraft();
                if (currentPlayer == null) { Cancel(); return; }
                var currentTopology = BindingInput.Topology;
                if (!ReferenceEquals(player, currentPlayer) || aircraft != currentAircraft || currentTopology != topology)
                { Cancel(); detectors.Clear(); player = currentPlayer; aircraft = currentAircraft; topology = currentTopology; }
                Pulse.Update();
                seen.Clear();
                var devices = BindingInput.Devices();
                for (int slot = 0; slot < Slots; slot++)
                {
                    var action = actions[slot]?.Value;
                    var rows = controls[slot].Rows;
                    if (action == null || action == "Unbound" || rows.Count == 0) continue;
                    var custom = new HashSet<Controller>();
                    foreach (var row in rows)
                    {
                        if (row.Kind == RowKind.Key) Track(slot, "key:" + row.Identity, BindingInput.KeyRowHeld(row), "Keyboard/mouse: " + row.Describe(), action);
                        if (row.Kind != RowKind.Device) continue;
                        var device = BindingInput.Resolve(row, devices);
                        if (device == null || !BindingInput.Valid(device, row)) continue;
                        custom.Add(device);
                        Track(slot, "device:" + device.id + ":" + row.Identity, BindingInput.DeviceDown(device, row), row.Describe() + " (device " + device.name.Trim() + ")", action);
                    }
                    // Device-specific rows replace "any gamepad" rows on that device.
                    foreach (var row in rows)
                    {
                        if (row.Kind != RowKind.Gamepad) continue;
                        foreach (var joystick in player.controllers.Joysticks)
                        {
                            if (custom.Contains(joystick)) continue;
                            var key = "pad:" + joystick.id + ":" + row.Pad;
                            bool held = GestureInput.IsHeld(joystick, row.Pad);
                            if (GestureInput.IsDPad(row.Pad) && (GestureInput.InvalidDPad(joystick) || (!held && GestureInput.DPadNonNeutral(joystick))))
                            { seen.Add(slot + ":" + key); Detector(slot + ":" + key).Cancel(); continue; }
                            Track(slot, key, held, row.Describe() + " (device " + joystick.name.Trim() + ")", action);
                        }
                    }
                }
                foreach (var stale in detectors.Keys.Where(k => !seen.Contains(k)).ToArray()) detectors.Remove(stale);
            }
            catch (Exception exception)
            {
                Cancel();
                if (warnings++ < 5) Logger.LogWarning("Controller gesture cancelled: " + exception);
            }
        }

        private DoublePressGesture Detector(string key)
        {
            if (!detectors.TryGetValue(key, out var detector)) { detector = new DoublePressGesture(); detectors.Add(key, detector); }
            return detector;
        }

        private void Track(int slot, string source, bool held, string description, string action)
        {
            var key = slot + ":" + source;
            seen.Add(key);
            if (!Detector(key).Update(held, Time.unscaledTime, gap.Value, duration.Value, cooldown.Value)) return;
            controls[slot].LogFired(description + " (double press, " + action + ")");
            Emit(action);
        }

        private void Emit(string name)
        {
            var action = ReInput.mapping.GetAction(name);
            if (player == null || action == null || action.type != InputActionType.Button || !action.userAssignable)
            { WarnUnavailable("Native button action is unavailable: " + name); return; }
            var maps = player.controllers.maps.GetAllMaps().Where(m => m.enabled && m.controllerType != ControllerType.Custom).ToArray();
            var category = maps.FirstOrDefault(m => m.ElementMaps.Any(e => e.actionId == action.id));
            if (category == null)
                category = maps.FirstOrDefault(m => m.ElementMaps.Any(e => ReInput.mapping.GetAction(e.actionId)?.name == "Eject"));
            if (category == null)
            {
                var gameplay = new HashSet<string>(StringComparer.Ordinal) { "Pitch", "Roll", "Throttle", "Fire", "Gear", "Radar" };
                category = maps.FirstOrDefault(m => m.ElementMaps.Any(e => gameplay.Contains(ReInput.mapping.GetAction(e.actionId)?.name ?? "")));
            }
            if (category == null || ReInput.mapping.CustomControllerLayouts.Count == 0)
            { WarnUnavailable("Cannot dispatch " + name + ": no enabled native gameplay map category or custom-controller layout is available."); return; }
            Pulse.Emit(player, new List<NativeActionPulse.Mapping> { new NativeActionPulse.Mapping(action.id, category.categoryId, ReInput.mapping.CustomControllerLayouts[0].id) });
        }

        private void WarnUnavailable(string message)
        {
            if (warnings++ < 5) Logger.LogWarning(message);
        }

        private void Cancel()
        {
            foreach (var detector in detectors.Values) detector.Cancel();
            Pulse.Dispose();
        }
        private void OnDisable() { Cancel(); foreach (var control in controls) control?.CancelCapture(); }
        private void OnDestroy()
        {
            if (subscribed) ReInput.InputSourceUpdateEvent -= Refresh;
            Cancel(); detectors.Clear(); harmony?.UnpatchSelf();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
