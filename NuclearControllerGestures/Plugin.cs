using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearControllerGestures
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin? Instance;
        internal readonly NativeActionPulse Pulse = new NativeActionPulse();
        private readonly Dictionary<ControllerBinding, ConfigEntry<string>> bindings = new Dictionary<ControllerBinding, ConfigEntry<string>>();
        private readonly Dictionary<Joystick, Dictionary<ControllerBinding, DoublePressGesture>> sources = new Dictionary<Joystick, Dictionary<ControllerBinding, DoublePressGesture>>();
        private ConfigEntry<bool> enabledSetting = null!;
        private ConfigEntry<float> gap = null!, duration = null!, cooldown = null!;
        private Harmony? harmony;
        private Rewired.Player? player;
        private Aircraft? aircraft;
        private bool initialized, subscribed;
        private int warnings;

        private void Awake()
        {
            Instance = this;
            enabledSetting = Config.Bind("General", "Enabled", true, "Enable independent controller double presses. Normal game bindings continue to run.");
            gap = Config.Bind("Timing", "Maximum Gap", .30f, new ConfigDescription("Maximum seconds from first release to second press.", new AcceptableValueRange<float>(.05f, 1f)));
            duration = Config.Bind("Timing", "Maximum Press Duration", .25f, new ConfigDescription("Each press must be released within this many seconds. Holds cancel the gesture.", new AcceptableValueRange<float>(.05f, 1f)));
            cooldown = Config.Bind("Timing", "Cooldown", .30f, new ConfigDescription("Seconds after a recognized double press before another gesture can begin. Release to neutral after cooldown.", new AcceptableValueRange<float>(0f, 2f)));
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
                foreach (ControllerBinding binding in Enum.GetValues(typeof(ControllerBinding)))
                    if (binding != ControllerBinding.Unbound)
                        bindings.Add(binding, Config.Bind("Double Press Actions", binding.ToString(), "Unbound", new ConfigDescription("Native button action after two short presses and releases on this exact control. Unbound disables it; normal bindings remain active.", new AcceptableValueList<string>(names))));
                initialized = true;
            }
            if (!subscribed) { ReInput.InputSourceUpdateEvent += Refresh; subscribed = true; }
            if (Blocked()) Cancel();
        }

        private static Aircraft? LocalAircraft() => GameManager.GetLocalAircraft(out var value) && value != null && !value.disabled && !value.HasEjected() ? value : null;
        private bool Blocked() => !enabledSetting.Value || !isActiveAndEnabled || GestureInput.IsBlocked() || DynamicMap.mapMaximized || !GameManager.flightControlsEnabled || LocalAircraft() == null;

        private void Refresh()
        {
            try
            {
                if (!initialized || !ReInput.isReady || Blocked()) { Cancel(); return; }
                var currentPlayer = GameManager.playerInput;
                var currentAircraft = LocalAircraft();
                if (currentPlayer == null) { Cancel(); return; }
                if (!ReferenceEquals(player, currentPlayer) || aircraft != currentAircraft)
                { Cancel(); sources.Clear(); player = currentPlayer; aircraft = currentAircraft; }
                Pulse.Update();
                var assigned = new HashSet<Joystick>(player.controllers.Joysticks);
                var removedSources = sources.Keys.Where(j => !assigned.Contains(j)).ToArray();
                if (removedSources.Length != 0) Cancel();
                foreach (var removed in removedSources) sources.Remove(removed);
                foreach (var joystick in assigned)
                {
                    if (!sources.TryGetValue(joystick, out var detectors))
                    {
                        detectors = new Dictionary<ControllerBinding, DoublePressGesture>();
                        foreach (var binding in bindings.Keys) detectors.Add(binding, new DoublePressGesture());
                        sources.Add(joystick, detectors);
                    }
                    foreach (var pair in bindings)
                    {
                        var detector = detectors[pair.Key];
                        bool held = GestureInput.IsHeld(joystick, pair.Key);
                        if (pair.Key >= ControllerBinding.DPadUp && pair.Key <= ControllerBinding.DPadRight &&
                            (GestureInput.InvalidDPad(joystick) || (!held && GestureInput.DPadNonNeutral(joystick)))) { detector.Cancel(); continue; }
                        if (pair.Value.Value == "Unbound") { detector.Cancel(); continue; }
                        if (detector.Update(held, Time.unscaledTime, gap.Value, duration.Value, cooldown.Value)) Emit(pair.Value.Value);
                    }
                }
            }
            catch (Exception exception)
            {
                Cancel();
                if (warnings++ < 5) Logger.LogWarning("Controller gesture cancelled: " + exception);
            }
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
            foreach (var detectors in sources.Values) foreach (var detector in detectors.Values) detector.Cancel();
            Pulse.Dispose();
        }
        private void OnDisable() => Cancel();
        private void OnDestroy()
        {
            if (subscribed) ReInput.InputSourceUpdateEvent -= Refresh;
            Cancel(); sources.Clear(); harmony?.UnpatchSelf();
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
