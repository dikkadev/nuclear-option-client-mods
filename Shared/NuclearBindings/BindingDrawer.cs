using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Rewired;
using UnityEngine;

namespace NuclearBindings
{
    // The F1 (ConfigurationManager) editor for one binding. Capture listens to every device at
    // once: it waits for released buttons and settled axes, then takes the first fresh press.
    internal sealed class BindingDrawer
    {
        private const float CaptureSeconds = 15f;
        private static readonly KeyCode[] Keys = BuildKeys();

        private sealed class JoystickCapture
        {
            internal readonly Dictionary<int, float> Baseline = new Dictionary<int, float>();
            internal readonly CaptureButtonEdges Buttons = new CaptureButtonEdges();
            internal readonly CaptureButtonEdges Template = new CaptureButtonEdges();
            internal float NeutralSince;
            internal bool Armed;
        }

        private readonly InputBinding binding;
        private readonly CaptureButtonEdges keys = new CaptureButtonEdges();
        private readonly Dictionary<Controller, JoystickCapture> joysticks = new Dictionary<Controller, JoystickCapture>();
        private bool capturing, requestedCapture, requestedCancel, otherKeySincePress;
        private KeyCode pendingModifier = KeyCode.None;
        private float deadline, lastDraw = -10f;
        private int tickFrame = -1;
        private string status = "", capturedConfiguration = "";
        private string? pending;
        private GUIStyle? wrappedLabel;

        internal BindingDrawer(InputBinding binding) { this.binding = binding; }
        internal bool Capturing => capturing;

        private static KeyCode[] BuildKeys()
        {
            var result = new List<KeyCode>();
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                // Left/right click operate F1 itself; Escape cancels; joystick keys are read through Rewired.
                if (key == KeyCode.None || key == KeyCode.Mouse0 || key == KeyCode.Mouse1 || key == KeyCode.Escape || key >= KeyCode.JoystickButton0) continue;
                if (!result.Contains(key)) result.Add(key);
            }
            return result.ToArray();
        }

        private void Label(string text) => GUILayout.Label(text, wrappedLabel!);

        internal void Draw(ConfigEntryBase _)
        {
            // Mutate settings and sample devices once per frame, before this drawer's layout.
            if (Event.current.type == EventType.Layout && tickFrame != Time.frameCount)
            {
                tickFrame = Time.frameCount;
                try { Tick(); }
                catch (Exception error) { Cancel("Capture failed: " + error.Message); }
            }
            lastDraw = Time.realtimeSinceStartup;
            wrappedLabel ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
            bool previous = GUI.enabled;
            GUILayout.BeginVertical();
            var rows = binding.Rows;
            if (rows.Count == 0) Label("Not bound");
            var connected = BindingInput.Devices();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                GUILayout.BeginHorizontal();
                Label(row.Describe() + Availability(row, connected));
                GUI.enabled = previous && pending == null && !capturing;
                if (GUILayout.Button("Remove", GUILayout.ExpandWidth(false)))
                {
                    var remaining = new List<BindingRow>(rows); remaining.RemoveAt(i);
                    pending = BindingText.Serialize(remaining); status = "";
                }
                GUI.enabled = previous;
                GUILayout.EndHorizontal();
                var note = Note(row);
                if (note.Length > 0) Label(note);
            }
            if (capturing)
            {
                GUIUtility.keyboardControl = -1;
                Label("Press a key, mouse button or controller control now. Escape cancels. " + Math.Max(0, Mathf.CeilToInt(deadline - Time.realtimeSinceStartup)) + " s");
                if (GUILayout.Button("Cancel")) requestedCancel = true;
            }
            else
            {
                GUI.enabled = previous && pending == null;
                if (GUILayout.Button("Add binding")) requestedCapture = true;
                GUI.enabled = previous;
            }
            if (status.Length > 0) Label(status);
            GUILayout.EndVertical();
        }

        private string Note(BindingRow row)
        {
            try { return binding.RowNote?.Invoke(row) ?? ""; }
            catch (Exception) { return ""; }
        }

        private static string Availability(BindingRow row, List<Controller> connected)
        {
            switch (row.Kind)
            {
                case RowKind.Device:
                    if (!ReInput.isReady) return "";
                    var device = BindingInput.Resolve(row, connected);
                    if (device == null) return " (not connected)";
                    return BindingInput.Valid(device, row) ? "" : " (control unavailable)";
                case RowKind.Gamepad:
                    if (!ReInput.isReady) return "";
                    foreach (var joystick in BindingInput.PlayerJoysticks()) if (joystick.GetTemplate<IGamepadTemplate>() != null) return "";
                    return " (no gamepad connected)";
                case RowKind.Invalid: return " (ignored)";
                default: return "";
            }
        }

        private void Tick()
        {
            if (pending != null) { binding.Entry.Value = pending; pending = null; }
            if (requestedCancel) { requestedCancel = false; Cancel("Capture cancelled."); }
            if (requestedCapture) { requestedCapture = false; Begin(); }
            if (!capturing) return;
            if (capturedConfiguration != binding.Entry.Value) { Cancel("Capture cancelled: the setting changed."); return; }
            if (!Application.isFocused || !InputContext.ConfigurationWindowOpen() || Time.realtimeSinceStartup - lastDraw > 1f)
            { Cancel("Capture cancelled: the settings window closed or lost focus."); return; }
            if (Time.realtimeSinceStartup >= deadline) { Cancel("Nothing was pressed in " + (int)CaptureSeconds + " seconds. Select Add binding to try again."); return; }
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel("Capture cancelled."); return; }
            if (TickKeys()) return;
            TickJoysticks();
        }

        private void Begin()
        {
            if (!Application.isFocused || !InputContext.ConfigurationWindowOpen()) return;
            capturing = true; status = ""; capturedConfiguration = binding.Entry.Value;
            deadline = Time.realtimeSinceStartup + CaptureSeconds;
            keys.Clear(); pendingModifier = KeyCode.None; otherKeySincePress = false;
            // Keys already held when capture starts must be released and pressed again.
            foreach (var key in Keys) keys.Observe((int)key, Input.GetKey(key), false);
            joysticks.Clear();
            if (ReInput.isReady) foreach (var joystick in ReInput.controllers.Joysticks) joysticks[joystick] = Start(joystick);
        }

        private static JoystickCapture Start(Joystick joystick)
        {
            var state = new JoystickCapture { NeutralSince = Time.realtimeSinceStartup };
            SampleAxes(joystick, state.Baseline);
            foreach (var button in joystick.Buttons) state.Buttons.Observe(button.id, button.value, false);
            var template = joystick.GetTemplate<IGamepadTemplate>();
            if (template != null)
                foreach (GamepadControl control in Enum.GetValues(typeof(GamepadControl)))
                    state.Template.Observe((int)control, BindingInput.TemplateValue(template, control) >= .6f, false);
            return state;
        }

        // Keyboard keys and mouse buttons 3-7. A modifier alone binds when released untouched.
        private bool TickKeys()
        {
            foreach (var key in Keys)
            {
                bool down = Input.GetKey(key);
                if (!keys.Observe((int)key, down, true)) continue;
                if (BindingText.ModifierOf(key) != KeyModifiers.None)
                {
                    if (pendingModifier == KeyCode.None) { pendingModifier = key; otherKeySincePress = false; }
                    continue;
                }
                otherKeySincePress = true;
                Save(new BindingRow { Kind = RowKind.Key, Key = key, Modifiers = BindingInput.HeldModifiers(key) }, null);
                return true;
            }
            if (pendingModifier != KeyCode.None && !Input.GetKey(pendingModifier))
            {
                var key = pendingModifier; pendingModifier = KeyCode.None;
                if (!otherKeySincePress)
                {
                    Save(new BindingRow { Kind = RowKind.Key, Key = key, Modifiers = BindingInput.HeldModifiers(key) }, null);
                    return true;
                }
            }
            return false;
        }

        private void TickJoysticks()
        {
            if (!ReInput.isReady) return;
            var current = new List<Joystick>(ReInput.controllers.Joysticks);
            var removed = new List<Controller>();
            foreach (var known in joysticks.Keys) if (!current.Contains((Joystick)known)) removed.Add(known);
            foreach (var gone in removed) joysticks.Remove(gone);
            foreach (var joystick in current)
            {
                if (!joysticks.TryGetValue(joystick, out var state)) { joysticks[joystick] = Start(joystick); continue; }
                if (TickJoystick(joystick, state)) return;
            }
        }

        private bool TickJoystick(Joystick joystick, JoystickCapture state)
        {
            var axes = new Dictionary<int, float>(); SampleAxes(joystick, axes);
            var template = joystick.GetTemplate<IGamepadTemplate>();
            bool templateHeld = false;
            int templateEdge = -1;
            if (template != null)
                foreach (GamepadControl control in Enum.GetValues(typeof(GamepadControl)))
                {
                    bool down = BindingInput.TemplateValue(template, control) >= .6f;
                    templateHeld |= down;
                    if (state.Template.Observe((int)control, down, state.Armed) && templateEdge < 0) templateEdge = (int)control;
                }
            int pressed = -1;
            foreach (var button in joystick.Buttons)
                if (state.Buttons.Observe(button.id, button.value, state.Armed) && pressed < 0) pressed = button.id;
            if (!state.Armed)
            {
                // Each device settles on its own, so a drifting stick cannot block another device.
                bool settled = true;
                foreach (var value in axes)
                    if (!state.Baseline.TryGetValue(value.Key, out var before) || Math.Abs(before - value.Value) > .1f) settled = false;
                if (!settled) { state.NeutralSince = Time.realtimeSinceStartup; state.Baseline.Clear(); foreach (var axis in axes) state.Baseline.Add(axis.Key, axis.Value); }
                else if (Time.realtimeSinceStartup - state.NeutralSince >= .3f) state.Armed = true;
                return false;
            }
            // A recognized gamepad binds named controls, so the binding works on any gamepad.
            if (templateEdge >= 0) { Save(new BindingRow { Kind = RowKind.Gamepad, Pad = (GamepadControl)templateEdge }, null); return true; }
            if (templateHeld) return false;
            if (pressed >= 0) { Save(BindingInput.CreateDeviceRow(joystick, pressed, false, 1), joystick); return true; }
            foreach (var axis in axes)
            {
                if (Math.Abs(axis.Value) <= .25f) state.Baseline[axis.Key] = axis.Value;
                if (!state.Baseline.TryGetValue(axis.Key, out var start)) continue;
                int sign = BindingText.CaptureAxisSign(start, axis.Value);
                if (sign != 0) { Save(BindingInput.CreateDeviceRow(joystick, axis.Key, true, sign), joystick); return true; }
            }
            return false;
        }

        private static void SampleAxes(Joystick joystick, Dictionary<int, float> values)
        {
            foreach (var axis in joystick.Axes)
            {
                float value = joystick.GetAxisById(axis.id);
                if (!float.IsNaN(value) && !float.IsInfinity(value)) values[axis.id] = value;
            }
        }

        private void Save(BindingRow row, Controller? device)
        {
            // Do not create a silently inactive row for indistinguishable devices.
            if (device != null && !ReferenceEquals(BindingInput.Resolve(row, BindingInput.Devices()), device))
            { Cancel("Cannot tell this device apart from an identical one. Disconnect the other and try again."); return; }
            var rows = new List<BindingRow>(binding.Rows);
            foreach (var existing in rows)
                if (existing.Identity == row.Identity) { Cancel(row.Describe() + " is already bound here."); return; }
            var other = binding.OtherOwner(row);
            if (other != null) { Cancel(row.Describe() + " is already bound to " + other.Name + ". Remove it there first."); return; }
            rows.Add(row);
            pending = BindingText.Serialize(rows);
            Cancel("Added " + row.Describe() + ".");
        }

        internal void Cancel(string message = "")
        {
            capturing = false; requestedCapture = false; requestedCancel = false;
            keys.Clear(); joysticks.Clear(); pendingModifier = KeyCode.None;
            status = message;
        }
    }
}
