using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Rewired;
using UnityEngine;

namespace NuclearMinimap
{
    // ConfigurationManager requires this type name and reads public tag fields by name.
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase>? CustomDrawer;
        public bool? HideDefaultButton = true;
        public bool? HideSettingName = true;
        public int? Order = 100;
    }

    internal sealed class DeviceZoomDrawer
    {
        private readonly ConfigEntry<string> entry;
        private Controller? selected, capturing;
        private bool inward, armed;
        private float deadline, neutralSince, lastDraw;
        private readonly Dictionary<int, float> baseline = new Dictionary<int, float>();
        private readonly CaptureButtonEdges buttonEdges = new CaptureButtonEdges();
        private string status = "";
        private string? pending;
        private bool? requestedCapture;
        private bool requestedCancel;
        private string capturedConfiguration = "";
        private GUIStyle? wrappedLabel, wrappedButton;

        private void Label(string text) => GUILayout.Label(text, wrappedLabel!);

        internal DeviceZoomDrawer(ConfigEntry<string> entry) { this.entry = entry; }
        internal bool Capturing => capturing != null;

        internal void Draw(ConfigEntryBase _)
        {
            lastDraw = Time.realtimeSinceStartup;
            wrappedLabel = new GUIStyle(GUI.skin.label) { wordWrap = true };
            wrappedButton = new GUIStyle(GUI.skin.button) { wordWrap = true };
            var devices = DeviceZoomInput.Devices();
            if (selected != null && !devices.Contains(selected)) selected = null;
            GUILayout.BeginVertical();
            Label("Device zoom bindings");
            Label("Select a device, then add a binding. You can add more than one.");
            // A wrapping toolbar works at the manager's narrow width without a popup window.
            var names = new string[devices.Count];
            int current = -1;
            for (int i = 0; i < devices.Count; i++)
            {
                names[i] = devices[i].name + " [" + devices[i].type + " " + devices[i].id + "]";
                if (ReferenceEquals(selected, devices[i])) current = i;
            }
            bool previous = GUI.enabled;
            GUI.enabled = previous && capturing == null;
            if (names.Length > 0)
            {
                int chosen = GUILayout.SelectionGrid(current, names, 1, wrappedButton);
                if (chosen >= 0 && chosen < devices.Count) selected = devices[chosen];
            }
            else Label("No input devices are available.");
            GUI.enabled = previous;
            if (capturing != null)
            {
                GUIUtility.keyboardControl = -1;
                Label((armed ? "Press a button or move an axis for " : "Let axes settle, then press a fresh button for ") +
                    (inward ? "Zoom in" : "Zoom out") + " on " + capturing.name + ".");
                Label("Time left: " + Math.Max(0, Mathf.CeilToInt(deadline - Time.realtimeSinceStartup)) + " seconds");
                if (GUILayout.Button("Cancel capture")) requestedCancel = true;
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = previous && selected != null && pending == null;
                if (GUILayout.Button("Add zoom in")) requestedCapture = true;
                if (GUILayout.Button("Add zoom out")) requestedCapture = false;
                GUI.enabled = previous;
                GUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(status)) Label(status);
            var rows = DeviceZoomBindings.Parse(entry.Value, out var error);
            if (error.Length > 0)
            {
                Label("Saved bindings are invalid: " + error + " Custom bindings are inactive.");
                if (GUILayout.Button("Clear invalid custom bindings")) Queue(new List<DeviceZoomBinding>());
            }
            else
            {
                if (rows.Count == 0) Label("No device bindings saved. Existing keyboard and gamepad defaults are active.");
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var resolved = DeviceZoomInput.Resolve(row, devices);
                    GUILayout.BeginVertical(GUI.skin.box);
                    bool valid = resolved != null && DeviceZoomInput.Valid(resolved, row);
                    Label((row.Inward ? "Zoom in — " : "Zoom out — ") + row.DeviceName +
                        (resolved == null ? " (not connected or ambiguous)" : valid ? "" : " (control unavailable)"));
                    GUILayout.BeginHorizontal();
                    Label(row.ControlName + (row.IsAxis ? (row.AxisSign > 0 ? " +" : " −") : ""));
                    GUI.enabled = previous && pending == null && capturing == null;
                    if (GUILayout.Button("Remove", GUILayout.ExpandWidth(false)))
                    { var remaining = new List<DeviceZoomBinding>(rows); remaining.RemoveAt(i); Queue(remaining); }
                    GUI.enabled = previous;
                    GUILayout.EndHorizontal();
                    if (valid && row.IsAxis && AxisKeepsNative(row, resolved!))
                        Label("This axis also has native controls. Those controls remain active while zooming.");
                    GUILayout.EndVertical();
                }
            }
            Label("Usable custom rows replace the old zoom defaults on that device. Remove all its rows to restore those defaults.");
            Label("Axis directions activate past halfway. Mouse buttons and keyboard keys are supported; mouse movement and wheel pulses cannot be held.");
            GUILayout.EndVertical();
        }

        private void Begin(bool zoomIn)
        {
            if (selected == null || !Application.isFocused || !MapHoldInput.ConfigurationWindowOpen()) return;
            capturing = selected; inward = zoomIn; armed = false; status = "";
            capturedConfiguration = entry.Value;
            deadline = Time.realtimeSinceStartup + 15f; neutralSince = Time.realtimeSinceStartup;
            baseline.Clear(); SampleAxes(capturing, baseline);
            buttonEdges.Clear();
            foreach (var button in capturing.Buttons) buttonEdges.Observe(button.id, button.value, false);
        }

        internal void Update()
        {
            // Mutate config between GUI passes, never halfway through an IMGUI layout.
            if (pending != null) { entry.Value = pending; pending = null; }
            if (requestedCancel) { requestedCancel = false; Cancel("Capture cancelled."); }
            if (requestedCapture.HasValue) { var direction = requestedCapture.Value; requestedCapture = null; Begin(direction); }
            if (capturing == null) return;
            if (capturedConfiguration != entry.Value) { Cancel("Capture cancelled: saved bindings changed."); return; }
            if (!Application.isFocused || !MapHoldInput.ConfigurationWindowOpen() || Time.realtimeSinceStartup - lastDraw > 1f)
            { Cancel("Capture cancelled: binding menu closed or focus lost."); return; }
            if (!DeviceZoomInput.Devices().Contains(capturing)) { Cancel("Capture cancelled: device disconnected."); return; }
            if (Time.realtimeSinceStartup >= deadline) { Cancel("Capture timed out. Select Add to try again."); return; }
            var axes = new Dictionary<int, float>(); SampleAxes(capturing, axes);
            int pressed = -1;
            foreach (var button in capturing.Buttons)
                if (buttonEdges.Observe(button.id, button.value, armed) && pressed < 0) pressed = button.id;
            if (!armed)
            {
                bool released = true;
                foreach (var value in axes)
                    if (!baseline.TryGetValue(value.Key, out var before) || Math.Abs(before - value.Value) > .1f) released = false;
                if (!released) { neutralSince = Time.realtimeSinceStartup; baseline.Clear(); foreach (var axis in axes) baseline.Add(axis.Key, axis.Value); }
                else if (Time.realtimeSinceStartup - neutralSince >= .3f) armed = true;
                return;
            }
            if (pressed >= 0) { Save(pressed, false, 1); return; }
            foreach (var axis in axes)
            {
                if (Math.Abs(axis.Value) <= .25f) baseline[axis.Key] = axis.Value;
                if (!baseline.TryGetValue(axis.Key, out var start)) continue;
                int sign = DeviceZoomBindings.CaptureAxisSign(start, axis.Value);
                if (sign != 0) { Save(axis.Key, true, sign); return; }
            }
        }

        private static void SampleAxes(Controller controller, Dictionary<int, float> values)
        {
            // Relative mouse axes do not express a sustained hold.
            if (!(controller is Joystick joystick)) return;
            foreach (var axis in joystick.Axes)
            {
                float value = joystick.GetAxisById(axis.id);
                if (!float.IsNaN(value) && !float.IsInfinity(value)) values[axis.id] = value;
            }
        }

        private void Save(int id, bool axis, int sign)
        {
            if (capturing == null) return;
            var row = DeviceZoomInput.Create(capturing, id, axis, sign, inward);
            // Do not create a silently inactive row for indistinguishable devices.
            if (!ReferenceEquals(DeviceZoomInput.Resolve(row, DeviceZoomInput.Devices()), capturing))
            { Cancel("Cannot identify this device uniquely. Disconnect an identical device and try again."); return; }
            var rows = DeviceZoomBindings.Parse(entry.Value, out var error);
            if (error.Length > 0) { Cancel("Clear invalid saved bindings before adding a binding."); return; }
            foreach (var existing in rows)
                if (existing.Key == row.Key)
                { Cancel(existing.Inward == inward ? "This control is already bound." : "This control is already bound to the other zoom direction. Remove it first."); return; }
            rows.Add(row); Queue(rows);
            Cancel("Saved " + (inward ? "Zoom in" : "Zoom out") + ": " + row.DeviceName + " / " + row.ControlName + (axis ? (sign > 0 ? " +" : " −") : "") + ".");
        }

        private void Queue(IEnumerable<DeviceZoomBinding> rows) => pending = DeviceZoomBindings.Serialize(rows);
        internal void Cancel(string message = "") { capturing = null; baseline.Clear(); buttonEdges.Clear(); armed = false; requestedCapture = null; requestedCancel = false; status = message; }

        private static bool AxisKeepsNative(DeviceZoomBinding row, Controller controller)
        {
            if (!ReInput.isReady) return false;
            var player = ReInput.players.GetPlayer(0);
            if (player == null) return false;
            foreach (var map in player.controllers.maps.GetAllMaps())
            {
                if (!map.enabled || map.controllerType != controller.type || map.controllerId != controller.id) continue;
                foreach (var element in map.AllMaps)
                {
                    if (!element.enabled || element.elementIdentifierId != row.ElementId || element.elementType != ControllerElementType.Axis) continue;
                    var action = ReInput.mapping.GetAction(element.actionId);
                    if (element.axisRange == AxisRange.Full || action == null || action.type != InputActionType.Button) return true;
                }
            }
            return false;
        }
    }
}
