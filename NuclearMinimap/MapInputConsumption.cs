using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace NuclearMinimap
{
    // Reserve individual physical bindings before Rewired evaluates player actions.
    // Reading the raw controller/template remains available to MapHoldInput.
    internal sealed class MapInputConsumption : IDisposable
    {
        private static MapInputConsumption? active;
        private readonly Dictionary<ActionElementMap, bool> disabled = new Dictionary<ActionElementMap, bool>();
        private readonly HashSet<ActionElementMap> desired = new HashSet<ActionElementMap>();
        private readonly List<ControllerTemplateElementTarget> targets = new List<ControllerTemplateElementTarget>();
        private Func<bool>? available;
        private Func<KeyboardShortcut>? keyboardIn, keyboardOut;
        private Func<ControllerMapBinding>? controllerIn, controllerOut;
        private Func<string>? deviceBindings;
        private Func<bool>? consumeNative;
        private Func<float>? holdThreshold;
        private Action<Exception>? reportFailure;
        private bool failureReported;
        private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
        private readonly MapNativeTapReplay replay = new MapNativeTapReplay();
        private string configuration = "";
        private string deviceConfiguration = "", topology = "";
        private List<DeviceZoomBinding> bindings = new List<DeviceZoomBinding>();
        private Aircraft? aircraft;
        private Rewired.Player? inputPlayer;
        internal bool ZoomInHeld { get; private set; }
        internal bool ZoomOutHeld { get; private set; }

        private sealed class Source
        {
            internal readonly TapHoldGesture Gesture = new TapHoldGesture();
            internal readonly List<MapNativeTapReplay.Mapping> Mappings = new List<MapNativeTapReplay.Mapping>();
            internal KeyboardShortcut Keyboard;
            internal Joystick? Joystick;
            internal Controller? PhysicalController;
            internal DeviceZoomBinding? PhysicalBinding;
            internal ControllerMapBinding Binding;
            internal Controller.Button? Button;
            internal bool In, Out, Seen, Down, Reserved;
            internal Source() { Gesture.Cancel(); }
            internal bool RawDown()
            {
                if (PhysicalController != null && PhysicalBinding != null) return DeviceZoomInput.Down(PhysicalController, PhysicalBinding);
                if (Joystick == null) return Keyboard.MainKey != KeyCode.None && Keyboard.IsPressed();
                if (Button != null) return Button.value;
                var pad = Joystick.GetTemplate<IGamepadTemplate>();
                var element = pad != null ? TemplateElement(pad, Binding) : null;
                if (element is IControllerTemplateButton button) return button.value;
                return element is IControllerTemplateAxis axis && axis.value >= .5f;
            }
        }

        internal void Start(Func<bool> available, Func<KeyboardShortcut> keyboardIn, Func<KeyboardShortcut> keyboardOut,
            Func<ControllerMapBinding> controllerIn, Func<ControllerMapBinding> controllerOut,
            Func<string> deviceBindings, Func<bool> consumeNative, Func<float> holdThreshold, Action<Exception>? reportFailure = null)
        {
            Dispose();
            this.available = available; this.keyboardIn = keyboardIn; this.keyboardOut = keyboardOut;
            this.controllerIn = controllerIn; this.controllerOut = controllerOut;
            this.deviceBindings = deviceBindings;
            this.consumeNative = consumeNative; this.holdThreshold = holdThreshold;
            this.reportFailure = reportFailure; failureReported = false;
            active = this;
            ReInput.InputSourceUpdateEvent += Refresh;
        }

        internal void Refresh()
        {
            try
            {
                desired.Clear();
                ZoomInHeld = ZoomOutHeld = false;
                string custom = deviceBindings!();
                string settings = keyboardIn!().ToString() + "|" + keyboardOut!().ToString() + "|" + controllerIn!() + "|" + controllerOut!() + "|" + consumeNative!() + "|" + holdThreshold!() + "|" + custom;
                if (settings != configuration) { Cancel(); sources.Clear(); configuration = settings; }
                if (custom != deviceConfiguration)
                {
                    bindings = DeviceZoomBindings.Parse(custom, out _);
                    deviceConfiguration = custom;
                }
                if (!ReInput.isReady || available == null || !available()) { Cancel(); Restore(); return; }
                replay.Update();
                var player = ReInput.players.GetPlayer(0);
                var currentAircraft = SceneSingleton<CombatHUD>.i != null ? SceneSingleton<CombatHUD>.i.aircraft : null;
                if (!ReferenceEquals(aircraft, currentAircraft) || !ReferenceEquals(inputPlayer, player))
                { Cancel(); sources.Clear(); aircraft = currentAircraft; inputPlayer = player; }
                if (player != null)
                {
                    var devices = DeviceZoomInput.Devices();
                    string connected = "";
                    foreach (var device in devices) connected += device.type + ":" + device.id + ":" + RuntimeHelpers.GetHashCode(device) + ";";
                    if (connected != topology) { Cancel(); sources.Clear(); topology = connected; }
                    foreach (var source in sources.Values) { source.Seen = false; source.In = source.Out = false; }
                    var customDevices = new HashSet<Controller>();
                    foreach (var binding in bindings)
                    {
                        var device = DeviceZoomInput.Resolve(binding, devices);
                        if (device == null || !DeviceZoomInput.Valid(device, binding)) continue;
                        customDevices.Add(device);
                        var source = GetSource("physical:" + device.type + ":" + device.id + ":" + binding.ElementId + ":" + binding.IsAxis + ":" + binding.AxisSign, binding.Inward);
                        source.PhysicalController = device; source.PhysicalBinding = binding;
                    }
                    // Device-specific rows replace broad defaults on that device.
                    if (!customDevices.Contains(ReInput.controllers.Keyboard))
                    { AddKeyboard(keyboardIn!(), true); AddKeyboard(keyboardOut!(), false); }
                    foreach (var joystick in player.controllers.Joysticks)
                        if (!customDevices.Contains(joystick))
                        { AddController(joystick, controllerIn!(), true); AddController(joystick, controllerOut!(), false); }
                    var removed = new List<string>();
                    foreach (var item in sources)
                    {
                        var source = item.Value;
                        if (!source.Seen) { source.Gesture.Cancel(); removed.Add(item.Key); continue; }
                        bool down = source.RawDown();
                        bool mainStillDown = source.PhysicalController == null && source.Joystick == null && source.Keyboard.MainKey != KeyCode.None && Input.GetKey(source.Keyboard.MainKey);
                        if (source.Down && !down && mainStillDown) { source.Gesture.Cancel(); source.Mappings.Clear(); }
                        if (down && !source.Down)
                        {
                            source.Mappings.Clear();
                            foreach (var map in player.controllers.maps.GetAllMaps())
                                if (map.enabled)
                                    foreach (var element in map.AllMaps)
                                        if (OriginalEnabled(element) && SourceMatches(source, map, element) && NativeModifiersHeld(element))
                                            source.Mappings.Add(new MapNativeTapReplay.Mapping(element));
                            source.Reserved = true;
                        }
                        bool tap = source.Gesture.Update(down, Time.realtimeSinceStartup, holdThreshold!());
                        if (tap && consumeNative!()) replay.Emit(player, source.Mappings);
                        if (!down && !mainStillDown) { source.Reserved = false; source.Mappings.Clear(); }
                        source.Down = down;
                        if (source.Gesture.Holding) { ZoomInHeld |= source.In; ZoomOutHeld |= source.Out; }
                    }
                    foreach (var key in removed) sources.Remove(key);
                    if (consumeNative!())
                        foreach (var map in player.controllers.maps.GetAllMaps())
                        {
                            if (!map.enabled) continue;
                            foreach (var element in map.AllMaps)
                                foreach (var source in sources.Values)
                                    if ((source.PhysicalController != null || source.Joystick != null || source.Reserved) && SourceMatches(source, map, element)) { desired.Add(element); break; }
                        }
                }
                var restore = new List<ActionElementMap>();
                foreach (var item in disabled) if (!desired.Contains(item.Key)) restore.Add(item.Key);
                foreach (var element in restore) { element.enabled = disabled[element]; disabled.Remove(element); }
                foreach (var element in desired)
                {
                    if (!disabled.ContainsKey(element)) disabled.Add(element, element.enabled);
                    element.enabled = false;
                }
            }
            catch (Exception error)
            {
                Cancel(); Restore();
                if (!failureReported) { failureReported = true; reportFailure?.Invoke(error); }
            }
        }

        private bool OriginalEnabled(ActionElementMap element) => disabled.TryGetValue(element, out var original) ? original : element.enabled;
        private static bool NativeModifiersHeld(ActionElementMap element)
        {
            if (!element.hasModifiers) return true;
            var keyboard = ReInput.controllers.Keyboard;
            return (element.modifierKey1 == ModifierKey.None || keyboard.GetModifierKey(element.modifierKey1)) &&
                (element.modifierKey2 == ModifierKey.None || keyboard.GetModifierKey(element.modifierKey2)) &&
                (element.modifierKey3 == ModifierKey.None || keyboard.GetModifierKey(element.modifierKey3));
        }
        private bool SourceMatches(Source source, ControllerMap map, ActionElementMap element)
        {
            if (source.PhysicalController != null && source.PhysicalBinding != null)
            {
                var binding = source.PhysicalBinding;
                if (map.controllerType != source.PhysicalController.type || map.controllerId != source.PhysicalController.id || element.elementIdentifierId != binding.ElementId) return false;
                if (!binding.IsAxis) return element.elementType == ControllerElementType.Button;
                // A single signed input cannot own the other pole or synthesize an analog action.
                var action = ReInput.mapping.GetAction(element.actionId);
                int mappingSign = element.axisRange == AxisRange.Positive ? 1 : element.axisRange == AxisRange.Negative ? -1 : 0;
                return element.elementType == ControllerElementType.Axis && DeviceZoomBindings.CanReserveAxis(
                    action != null && action.type == InputActionType.Button, element.axisRange == AxisRange.Full, mappingSign, binding.AxisSign);
            }
            if (source.Joystick == null) return map.controllerType == ControllerType.Keyboard && element.keyCode == source.Keyboard.MainKey;
            if (map.controllerType != ControllerType.Joystick || map.controllerId != source.Joystick.id) return false;
            return source.Button != null ? Same(element, source.Button) : ControllerMatches(source.Joystick, element, source.Binding);
        }
        private void AddKeyboard(KeyboardShortcut shortcut, bool inward)
        {
            if (shortcut.MainKey == KeyCode.None) return;
            var source = GetSource("keyboard:" + shortcut, inward);
            source.Keyboard = shortcut;
        }
        private Source GetSource(string key, bool inward)
        {
            if (!sources.TryGetValue(key, out var source)) { source = new Source(); sources.Add(key, source); }
            source.Seen = true; if (inward) source.In = true; else source.Out = true;
            return source;
        }
        private void AddController(Joystick joystick, ControllerMapBinding binding, bool inward)
        {
            if (binding == ControllerMapBinding.Unbound) return;
            if (joystick.GetTemplate<IGamepadTemplate>() != null)
            {
                var source = GetSource("joystick:" + joystick.id + ":" + binding, inward);
                source.Joystick = joystick; source.Binding = binding;
                return;
            }
            foreach (var pad in joystick.DirectionalPads)
            { AddButton(joystick, binding, inward, pad.buttonUp, ControllerMapBinding.DPadUp); AddButton(joystick, binding, inward, pad.buttonDown, ControllerMapBinding.DPadDown); AddButton(joystick, binding, inward, pad.buttonLeft, ControllerMapBinding.DPadLeft); AddButton(joystick, binding, inward, pad.buttonRight, ControllerMapBinding.DPadRight); }
            foreach (var hat in joystick.Hats)
            {
                AddButton(joystick, binding, inward, hat.buttonUp, ControllerMapBinding.DPadUp); AddButton(joystick, binding, inward, hat.buttonDown, ControllerMapBinding.DPadDown);
                AddButton(joystick, binding, inward, hat.buttonLeft, ControllerMapBinding.DPadLeft); AddButton(joystick, binding, inward, hat.buttonRight, ControllerMapBinding.DPadRight);
                AddButton(joystick, binding, inward, hat.buttonUpLeft, ControllerMapBinding.DPadUp, ControllerMapBinding.DPadLeft);
                AddButton(joystick, binding, inward, hat.buttonUpRight, ControllerMapBinding.DPadUp, ControllerMapBinding.DPadRight);
                AddButton(joystick, binding, inward, hat.buttonDownLeft, ControllerMapBinding.DPadDown, ControllerMapBinding.DPadLeft);
                AddButton(joystick, binding, inward, hat.buttonDownRight, ControllerMapBinding.DPadDown, ControllerMapBinding.DPadRight);
            }
        }
        private void AddButton(Joystick joystick, ControllerMapBinding configured, bool inward, Controller.Button? button, ControllerMapBinding direction, ControllerMapBinding other = ControllerMapBinding.Unbound)
        {
            if (button == null || (configured != direction && configured != other)) return;
            var source = GetSource("button:" + joystick.id + ":" + button.id, inward); source.Joystick = joystick; source.Button = button;
        }
        private void Cancel()
        {
            ZoomInHeld = ZoomOutHeld = false;
            foreach (var source in sources.Values) { source.Gesture.Cancel(); source.Mappings.Clear(); source.Down = false; source.Reserved = false; }
            replay.Dispose();
        }

        private bool ControllerMatches(Joystick joystick, ActionElementMap element, ControllerMapBinding binding)
        {
            if (binding == ControllerMapBinding.Unbound) return false;
            var template = joystick.GetTemplate<IGamepadTemplate>();
            if (template != null)
            {
                var selected = TemplateElement(template, binding);
                if (selected == null) return false;
                targets.Clear();
                template.GetElementTargets(new ControllerElementTarget(element), targets);
                bool found = false;
                foreach (var target in targets)
                {
                    if (target.element == null) continue;
                    // A full physical axis can cover multiple template controls. Do not
                    // disable the entire mapping if that would reserve an unrelated control.
                    if (element.elementType == ControllerElementType.Axis && element.axisRange == AxisRange.Full && target.element.id != selected.id)
                        return false;
                    if (target.element.id == selected.id &&
                        (selected.type != ControllerTemplateElementType.Axis || target.axisRange != AxisRange.Negative)) found = true;
                }
                return found;
            }
            foreach (var pad in joystick.DirectionalPads)
                if (Matches(element, binding, pad.buttonUp, pad.buttonDown, pad.buttonLeft, pad.buttonRight)) return true;
            foreach (var hat in joystick.Hats)
            {
                if (Matches(element, binding, hat.buttonUp, hat.buttonDown, hat.buttonLeft, hat.buttonRight)) return true;
                if ((binding == ControllerMapBinding.DPadUp || binding == ControllerMapBinding.DPadLeft) && Same(element, hat.buttonUpLeft)) return true;
                if ((binding == ControllerMapBinding.DPadUp || binding == ControllerMapBinding.DPadRight) && Same(element, hat.buttonUpRight)) return true;
                if ((binding == ControllerMapBinding.DPadDown || binding == ControllerMapBinding.DPadLeft) && Same(element, hat.buttonDownLeft)) return true;
                if ((binding == ControllerMapBinding.DPadDown || binding == ControllerMapBinding.DPadRight) && Same(element, hat.buttonDownRight)) return true;
            }
            return false;
        }

        private static bool Same(ActionElementMap element, Controller.Button? button) => button != null && element.elementType == ControllerElementType.Button && element.elementIdentifierId == button.id;
        private static bool Matches(ActionElementMap element, ControllerMapBinding binding, Controller.Button? up, Controller.Button? down, Controller.Button? left, Controller.Button? right) =>
            (binding == ControllerMapBinding.DPadUp && Same(element, up)) || (binding == ControllerMapBinding.DPadDown && Same(element, down)) ||
            (binding == ControllerMapBinding.DPadLeft && Same(element, left)) || (binding == ControllerMapBinding.DPadRight && Same(element, right));

        private static IControllerTemplateElement? TemplateElement(IGamepadTemplate pad, ControllerMapBinding binding)
        {
            switch (binding)
            {
                case ControllerMapBinding.DPadUp: return pad.dPad?.up;
                case ControllerMapBinding.DPadDown: return pad.dPad?.down;
                case ControllerMapBinding.DPadLeft: return pad.dPad?.left;
                case ControllerMapBinding.DPadRight: return pad.dPad?.right;
                case ControllerMapBinding.ButtonSouth: return pad.a;
                case ControllerMapBinding.ButtonEast: return pad.b;
                case ControllerMapBinding.ButtonWest: return pad.x;
                case ControllerMapBinding.ButtonNorth: return pad.y;
                case ControllerMapBinding.LeftShoulder: return pad.leftBumper;
                case ControllerMapBinding.RightShoulder: return pad.rightBumper;
                case ControllerMapBinding.LeftTrigger: return pad.leftTrigger;
                case ControllerMapBinding.RightTrigger: return pad.rightTrigger;
                case ControllerMapBinding.LeftStickButton: return pad.leftStick?.press;
                case ControllerMapBinding.RightStickButton: return pad.rightStick?.press;
                case ControllerMapBinding.Select: return pad.back;
                case ControllerMapBinding.Start: return pad.start;
                default: return null;
            }
        }

        internal void Restore()
        {
            foreach (var item in disabled) item.Key.enabled = item.Value;
            disabled.Clear(); desired.Clear();
        }

        public void Dispose()
        {
            ReInput.InputSourceUpdateEvent -= Refresh;
            Cancel(); Restore(); available = null; sources.Clear(); configuration = deviceConfiguration = topology = ""; bindings.Clear(); aircraft = null; inputPlayer = null;
            if (ReferenceEquals(active, this)) active = null;
        }

        // Native remapping/save code must never serialize our transient reservations.
        // Reapply at the next source update, before evaluating any new action state.
        [HarmonyPatch(typeof(ControllerMap), nameof(ControllerMap.ToXmlString))]
        private static class SaveXmlPatch
        {
            private static void Prefix() => active?.Restore();
        }
        [HarmonyPatch(typeof(ControllerMap), nameof(ControllerMap.ToJsonString))]
        private static class SaveJsonPatch
        {
            private static void Prefix() => active?.Restore();
        }
        [HarmonyPatch(typeof(Rewired.Player), nameof(Rewired.Player.GetSaveData), new[] { typeof(bool) })]
        private static class SavePlayerPatch
        {
            private static void Prefix() => active?.Restore();
            private static void Postfix(ref PlayerSaveData __result) => active?.replay.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(Rewired.Player.ControllerHelper.MapHelper), "GetMapSaveData", new[] { typeof(ControllerType), typeof(int), typeof(bool) })]
        private static class SaveMapCollectionPatch
        {
            private static void Prefix() => active?.Restore();
            private static void Postfix(ref ControllerMapSaveData[] __result) => active?.replay.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(Rewired.Player.ControllerHelper.MapHelper), "GetAllMapSaveData", new[] { typeof(ControllerType), typeof(bool) })]
        private static class SaveTypedMapCollectionPatch
        {
            private static void Prefix() => active?.Restore();
            private static void Postfix(ref ControllerMapSaveData[] __result) => active?.replay.FilterSave(ref __result);
        }
        [HarmonyPatch]
        private static class SaveAllMapCollectionPatch
        {
            private static MethodBase TargetMethod()
            {
                // A generic overload has the same argument types. Select the native array
                // overload explicitly rather than leave Harmony with an ambiguous lookup.
                foreach (var method in typeof(Rewired.Player.ControllerHelper.MapHelper).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (method.Name != "GetAllMapSaveData" || method.IsGenericMethod || method.ReturnType != typeof(ControllerMapSaveData[])) continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool)) return method;
                }
                throw new MissingMethodException("Native Rewired GetAllMapSaveData(bool) array overload is missing.");
            }
            private static void Prefix() => active?.Restore();
            private static void Postfix(ref ControllerMapSaveData[] __result) => active?.replay.FilterSave(ref __result);
        }
        [HarmonyPatch(typeof(ControllerMap), MethodType.Constructor, new[] { typeof(ControllerMap) })]
        private static class CopyMapPatch
        {
            private static void Prefix(ControllerMap __0, out bool __state)
            { active?.Restore(); __state = active?.replay.Owns(__0) == true; }
            private static void Postfix(ControllerMap __instance, bool __state)
            { if (__state) active?.replay.RememberCopy(__instance); }
        }
    }
}
