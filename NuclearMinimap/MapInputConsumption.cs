using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearBindings;
using Rewired;
using UnityEngine;

namespace NuclearMinimap
{
    // Reserve individual physical bindings before Rewired evaluates player actions.
    // Reading the raw controller/template remains available to the zoom sources.
    internal sealed class MapInputConsumption : IDisposable
    {
        private static MapInputConsumption? active;
        private readonly Dictionary<ActionElementMap, bool> disabled = new Dictionary<ActionElementMap, bool>();
        private readonly HashSet<ActionElementMap> desired = new HashSet<ActionElementMap>();
        private readonly List<ControllerTemplateElementTarget> targets = new List<ControllerTemplateElementTarget>();
        private Func<bool>? available;
        private Func<IReadOnlyList<BindingRow>>? zoomIn, zoomOut;
        private Func<bool>? consumeNative;
        private Func<float>? holdThreshold;
        private Action<bool, string>? reportFired;
        private Action<Exception>? reportFailure;
        private bool failureReported;
        private readonly Dictionary<string, Source> sources = new Dictionary<string, Source>();
        private readonly MapNativeTapReplay replay = new MapNativeTapReplay();
        private string configuration = "";
        private string topology = "";
        private Aircraft? aircraft;
        private Rewired.Player? inputPlayer;
        internal bool ZoomInHeld { get; private set; }
        internal bool ZoomOutHeld { get; private set; }

        private sealed class Source
        {
            internal readonly TapHoldGesture Gesture = new TapHoldGesture();
            internal readonly List<MapNativeTapReplay.Mapping> Mappings = new List<MapNativeTapReplay.Mapping>();
            internal BindingRow? Key;
            internal Joystick? Joystick;
            internal Controller? PhysicalController;
            internal BindingRow? PhysicalBinding;
            internal GamepadControl Binding;
            internal Controller.Button? Button;
            internal string Description = "";
            internal bool In, Out, Seen, Down, Reserved, WasHolding;
            internal Source() { Gesture.Cancel(); }
            internal bool RawDown()
            {
                if (PhysicalController != null && PhysicalBinding != null) return BindingInput.DeviceDown(PhysicalController, PhysicalBinding);
                if (Joystick == null) return Key != null && BindingInput.KeyRowHeld(Key);
                if (Button != null) return Button.value;
                var pad = Joystick.GetTemplate<IGamepadTemplate>();
                var element = pad != null ? BindingInput.TemplateElement(pad, Binding) : null;
                if (element is IControllerTemplateButton button) return button.value;
                return element is IControllerTemplateAxis axis && axis.value >= .5f;
            }
        }

        internal void Start(Func<bool> available, Func<IReadOnlyList<BindingRow>> zoomIn, Func<IReadOnlyList<BindingRow>> zoomOut,
            Func<bool> consumeNative, Func<float> holdThreshold, Action<bool, string>? reportFired = null, Action<Exception>? reportFailure = null)
        {
            Dispose();
            this.available = available; this.zoomIn = zoomIn; this.zoomOut = zoomOut;
            this.consumeNative = consumeNative; this.holdThreshold = holdThreshold;
            this.reportFired = reportFired; this.reportFailure = reportFailure; failureReported = false;
            active = this;
            ReInput.InputSourceUpdateEvent += Refresh;
        }

        internal void Refresh()
        {
            try
            {
                desired.Clear();
                ZoomInHeld = ZoomOutHeld = false;
                var inward = zoomIn!();
                var outward = zoomOut!();
                string settings = BindingText.Serialize(inward) + "|" + BindingText.Serialize(outward) + "|" + consumeNative!() + "|" + holdThreshold!();
                if (settings != configuration) { Cancel(); sources.Clear(); configuration = settings; }
                if (!ReInput.isReady || available == null || !available()) { Cancel(); Restore(); return; }
                replay.Update();
                var player = ReInput.players.GetPlayer(0);
                var currentAircraft = SceneSingleton<CombatHUD>.i != null ? SceneSingleton<CombatHUD>.i.aircraft : null;
                if (!ReferenceEquals(aircraft, currentAircraft) || !ReferenceEquals(inputPlayer, player))
                { Cancel(); sources.Clear(); aircraft = currentAircraft; inputPlayer = player; }
                if (player != null)
                {
                    var devices = BindingInput.Devices();
                    var connected = BindingInput.Topology;
                    if (connected != topology) { Cancel(); sources.Clear(); topology = connected; }
                    foreach (var source in sources.Values) { source.Seen = false; source.In = source.Out = false; }
                    var customDevices = new HashSet<Controller>();
                    AddRows(inward, true, devices, customDevices);
                    AddRows(outward, false, devices, customDevices);
                    // Device-specific rows replace "any gamepad" rows on that device.
                    foreach (var joystick in player.controllers.Joysticks)
                        if (!customDevices.Contains(joystick))
                        {
                            foreach (var row in inward) if (row.Kind == RowKind.Gamepad) AddController(joystick, row.Pad, true);
                            foreach (var row in outward) if (row.Kind == RowKind.Gamepad) AddController(joystick, row.Pad, false);
                        }
                    var removed = new List<string>();
                    foreach (var item in sources)
                    {
                        var source = item.Value;
                        if (!source.Seen) { source.Gesture.Cancel(); removed.Add(item.Key); continue; }
                        bool down = source.RawDown();
                        bool mainStillDown = source.Key != null && BindingInput.KeyMainHeld(source.Key);
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
                        if (tap) reportFired?.Invoke(source.In, source.Description + " (tap)");
                        if (tap && consumeNative!()) replay.Emit(player, source.Mappings);
                        if (!down && !mainStillDown) { source.Reserved = false; source.Mappings.Clear(); }
                        source.Down = down;
                        if (source.Gesture.Holding) { ZoomInHeld |= source.In; ZoomOutHeld |= source.Out; }
                        if (source.Gesture.Holding && !source.WasHolding) reportFired?.Invoke(source.In, source.Description + " (hold)");
                        source.WasHolding = source.Gesture.Holding;
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

        private void AddRows(IReadOnlyList<BindingRow> rows, bool inward, List<Controller> devices, HashSet<Controller> customDevices)
        {
            foreach (var row in rows)
            {
                if (row.Kind == RowKind.Key)
                {
                    var key = GetSource("keyboard:" + row.Identity, inward);
                    key.Key = row; key.Description = "Keyboard/mouse: " + row.Describe();
                    continue;
                }
                if (row.Kind != RowKind.Device) continue;
                var device = BindingInput.Resolve(row, devices);
                if (device == null || !BindingInput.Valid(device, row)) continue;
                customDevices.Add(device);
                var source = GetSource("physical:" + device.type + ":" + device.id + ":" + row.ElementId + ":" + row.IsAxis + ":" + row.AxisSign, inward);
                source.PhysicalController = device; source.PhysicalBinding = row; source.Description = row.Describe() + " (device " + device.name.Trim() + ")";
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
                return element.elementType == ControllerElementType.Axis && BindingText.CanReserveAxis(
                    action != null && action.type == InputActionType.Button, element.axisRange == AxisRange.Full, mappingSign, binding.AxisSign);
            }
            if (source.Joystick == null) return source.Key != null && map.controllerType == ControllerType.Keyboard && element.keyCode == source.Key.Key;
            if (map.controllerType != ControllerType.Joystick || map.controllerId != source.Joystick.id) return false;
            return source.Button != null ? Same(element, source.Button) : ControllerMatches(source.Joystick, element, source.Binding);
        }
        private Source GetSource(string key, bool inward)
        {
            if (!sources.TryGetValue(key, out var source)) { source = new Source(); sources.Add(key, source); }
            source.Seen = true; if (inward) source.In = true; else source.Out = true;
            return source;
        }
        private void AddController(Joystick joystick, GamepadControl binding, bool inward)
        {
            if (joystick.GetTemplate<IGamepadTemplate>() != null)
            {
                var source = GetSource("joystick:" + joystick.id + ":" + binding, inward);
                source.Joystick = joystick; source.Binding = binding; source.Description = "Gamepad " + BindingText.PadLabel(binding) + " (device " + joystick.name.Trim() + ")";
                return;
            }
            foreach (var pad in joystick.DirectionalPads)
            { AddButton(joystick, binding, inward, pad.buttonUp, GamepadControl.DPadUp); AddButton(joystick, binding, inward, pad.buttonDown, GamepadControl.DPadDown); AddButton(joystick, binding, inward, pad.buttonLeft, GamepadControl.DPadLeft); AddButton(joystick, binding, inward, pad.buttonRight, GamepadControl.DPadRight); }
            foreach (var hat in joystick.Hats)
            {
                AddButton(joystick, binding, inward, hat.buttonUp, GamepadControl.DPadUp); AddButton(joystick, binding, inward, hat.buttonDown, GamepadControl.DPadDown);
                AddButton(joystick, binding, inward, hat.buttonLeft, GamepadControl.DPadLeft); AddButton(joystick, binding, inward, hat.buttonRight, GamepadControl.DPadRight);
                AddButton(joystick, binding, inward, hat.buttonUpLeft, GamepadControl.DPadUp, GamepadControl.DPadLeft);
                AddButton(joystick, binding, inward, hat.buttonUpRight, GamepadControl.DPadUp, GamepadControl.DPadRight);
                AddButton(joystick, binding, inward, hat.buttonDownLeft, GamepadControl.DPadDown, GamepadControl.DPadLeft);
                AddButton(joystick, binding, inward, hat.buttonDownRight, GamepadControl.DPadDown, GamepadControl.DPadRight);
            }
        }
        private void AddButton(Joystick joystick, GamepadControl configured, bool inward, Controller.Button? button, GamepadControl direction, GamepadControl? other = null)
        {
            if (button == null || (configured != direction && configured != other)) return;
            var source = GetSource("button:" + joystick.id + ":" + button.id, inward); source.Joystick = joystick; source.Button = button;
            source.Description = "Gamepad " + BindingText.PadLabel(configured) + " (device " + joystick.name.Trim() + ", " + button.name + ")";
        }
        private void Cancel()
        {
            ZoomInHeld = ZoomOutHeld = false;
            foreach (var source in sources.Values) { source.Gesture.Cancel(); source.Mappings.Clear(); source.Down = false; source.Reserved = false; source.WasHolding = false; }
            replay.Dispose();
        }

        private bool ControllerMatches(Joystick joystick, ActionElementMap element, GamepadControl binding)
        {
            var template = joystick.GetTemplate<IGamepadTemplate>();
            if (template != null)
            {
                var selected = BindingInput.TemplateElement(template, binding);
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
                if ((binding == GamepadControl.DPadUp || binding == GamepadControl.DPadLeft) && Same(element, hat.buttonUpLeft)) return true;
                if ((binding == GamepadControl.DPadUp || binding == GamepadControl.DPadRight) && Same(element, hat.buttonUpRight)) return true;
                if ((binding == GamepadControl.DPadDown || binding == GamepadControl.DPadLeft) && Same(element, hat.buttonDownLeft)) return true;
                if ((binding == GamepadControl.DPadDown || binding == GamepadControl.DPadRight) && Same(element, hat.buttonDownRight)) return true;
            }
            return false;
        }

        private static bool Same(ActionElementMap element, Controller.Button? button) => button != null && element.elementType == ControllerElementType.Button && element.elementIdentifierId == button.id;
        private static bool Matches(ActionElementMap element, GamepadControl binding, Controller.Button? up, Controller.Button? down, Controller.Button? left, Controller.Button? right) =>
            (binding == GamepadControl.DPadUp && Same(element, up)) || (binding == GamepadControl.DPadDown && Same(element, down)) ||
            (binding == GamepadControl.DPadLeft && Same(element, left)) || (binding == GamepadControl.DPadRight && Same(element, right));

        internal void Restore()
        {
            foreach (var item in disabled) item.Key.enabled = item.Value;
            disabled.Clear(); desired.Clear();
        }

        public void Dispose()
        {
            ReInput.InputSourceUpdateEvent -= Refresh;
            Cancel(); Restore(); available = null; sources.Clear(); configuration = topology = ""; aircraft = null; inputPlayer = null;
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
