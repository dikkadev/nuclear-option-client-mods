using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace NuclearBindings
{
    internal enum RowKind { Key, Gamepad, Device, Invalid }
    internal enum DeviceKind { Keyboard, Mouse, Joystick }

    [Flags]
    internal enum KeyModifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4 }

    // Named Rewired gamepad template controls. Values match the older Minimap and
    // ControllerGestures enums, so saved names stay readable.
    internal enum GamepadControl
    {
        DPadUp, DPadDown, DPadLeft, DPadRight,
        ButtonSouth, ButtonEast, ButtonWest, ButtonNorth,
        LeftShoulder, RightShoulder, LeftTrigger, RightTrigger,
        LeftStickButton, RightStickButton, Select, Start
    }

    // One bound control. Text forms, separated by ';' in a setting:
    //   Ctrl+F7, Slash, Mouse3              keyboard key or mouse button (Unity KeyCode)
    //   Gamepad:ButtonSouth                 named control on any gamepad
    //   Device:Joystick|guid|hwguid|hwid|name|element|button|1|control
    //                                       one control on one device (Minimap 0.3 rows)
    internal sealed class BindingRow
    {
        internal RowKind Kind;
        internal KeyCode Key;
        internal KeyModifiers Modifiers;
        internal GamepadControl Pad;
        internal DeviceKind DeviceKind;
        internal string DeviceGuid = "", HardwareGuid = "", HardwareIdentifier = "", DeviceName = "", ControlName = "";
        internal int ElementId, AxisSign = 1;
        internal bool IsAxis;
        internal string Text = "";

        // Physical identity: two rows with the same identity are the same control.
        internal string Identity
        {
            get
            {
                switch (Kind)
                {
                    case RowKind.Key: return "key:" + (int)Key + ":" + (int)Modifiers;
                    case RowKind.Gamepad: return "pad:" + Pad;
                    case RowKind.Device: return "dev:" + DeviceKind + ":" + DeviceGuid.ToLowerInvariant() + ":" + HardwareGuid.ToLowerInvariant() + ":" + HardwareIdentifier + ":" + ElementId.ToString(CultureInfo.InvariantCulture) + ":" + IsAxis + ":" + AxisSign;
                    default: return "invalid:" + Text;
                }
            }
        }

        internal bool AxisDown(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value * AxisSign >= .5f;

        internal string Describe()
        {
            switch (Kind)
            {
                case RowKind.Key: return BindingText.KeyLabel(Key, Modifiers);
                case RowKind.Gamepad: return "Gamepad " + BindingText.PadLabel(Pad);
                case RowKind.Device:
                    string device = DeviceName.Trim().Length > 0 ? DeviceName.Trim() : DeviceKind.ToString();
                    string control = ControlName.Trim().Length > 0 ? ControlName.Trim() : "Control " + ElementId.ToString(CultureInfo.InvariantCulture);
                    return device + ": " + control + (IsAxis ? (AxisSign > 0 ? " +" : " -") : "");
                default: return "Unreadable: " + Text;
            }
        }
    }

    internal static class BindingText
    {
        private const string PadPrefix = "Gamepad:", DevicePrefix = "Device:";

        // Unity's legacy JoystickButtonN order for XInput gamepads on Windows.
        private static readonly GamepadControl[] UnityJoystickButtons =
        {
            GamepadControl.ButtonSouth, GamepadControl.ButtonEast, GamepadControl.ButtonWest, GamepadControl.ButtonNorth,
            GamepadControl.LeftShoulder, GamepadControl.RightShoulder, GamepadControl.Select, GamepadControl.Start,
            GamepadControl.LeftStickButton, GamepadControl.RightStickButton
        };

        // Never throws. Unreadable rows are kept as Invalid rows, so the user can see and remove them.
        internal static List<BindingRow> Parse(string? text)
        {
            var rows = new List<BindingRow>();
            var seen = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(text)) return rows;
            foreach (var raw in text!.Split(';'))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                var row = ParseRow(part) ?? new BindingRow { Kind = RowKind.Invalid, Text = part };
                if (part == "None" && row.Kind == RowKind.Invalid) continue;
                if (seen.Add(row.Identity)) rows.Add(row);
            }
            return rows;
        }

        internal static string Serialize(IEnumerable<BindingRow> rows)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>();
            foreach (var row in rows)
                if (seen.Add(row.Identity)) parts.Add(Format(row));
            return string.Join("; ", parts);
        }

        // The stored text in canonical form. Older BepInEx KeyboardShortcut values ("F7 + LeftControl",
        // "None", "JoystickButton0") and every other readable row are rewritten; unreadable rows stay.
        internal static string Canonical(string? text) => Serialize(Parse(text));

        internal static string Format(BindingRow row)
        {
            switch (row.Kind)
            {
                case RowKind.Key:
                    var mods = "";
                    if ((row.Modifiers & KeyModifiers.Ctrl) != 0) mods += "Ctrl+";
                    if ((row.Modifiers & KeyModifiers.Shift) != 0) mods += "Shift+";
                    if ((row.Modifiers & KeyModifiers.Alt) != 0) mods += "Alt+";
                    return mods + row.Key;
                case RowKind.Gamepad: return PadPrefix + row.Pad;
                case RowKind.Device:
                    return DevicePrefix + string.Join("|", row.DeviceKind.ToString(), Escape(row.DeviceGuid), Escape(row.HardwareGuid), Escape(row.HardwareIdentifier),
                        Escape(row.DeviceName), row.ElementId.ToString(CultureInfo.InvariantCulture), row.IsAxis ? "axis" : "button",
                        row.AxisSign.ToString(CultureInfo.InvariantCulture), Escape(row.ControlName));
                default: return row.Text;
            }
        }

        internal static BindingRow? ParseRow(string part)
        {
            if (part.StartsWith(PadPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = part.Substring(PadPrefix.Length).Trim();
                return IsName(name) && Enum.TryParse(name, true, out GamepadControl pad) && Enum.IsDefined(typeof(GamepadControl), pad)
                    ? new BindingRow { Kind = RowKind.Gamepad, Pad = pad } : null;
            }
            if (part.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
                return ParseDevice(part.Substring(DevicePrefix.Length).Split('|'));
            return ParseKey(part);
        }

        // "Ctrl+Shift+F7" (main key last) or BepInEx "F7 + LeftControl" (main key first).
        internal static BindingRow? ParseKey(string part)
        {
            bool legacy = part.Contains(" + ");
            var tokens = part.Split('+');
            for (int i = 0; i < tokens.Length; i++) tokens[i] = tokens[i].Trim();
            if (tokens.Length == 0 || Array.Exists(tokens, t => t.Length == 0)) return null;
            int mainIndex = legacy ? 0 : tokens.Length - 1;
            if (!TryKey(tokens[mainIndex], out var main)) return null;
            if (IsJoystickKey(main, out int button))
            {
                // Unity's legacy joystick buttons never reach the game through Rewired.
                if (button < 0 || button >= UnityJoystickButtons.Length) return null;
                return new BindingRow { Kind = RowKind.Gamepad, Pad = UnityJoystickButtons[button] };
            }
            var modifiers = KeyModifiers.None;
            for (int i = 0; i < tokens.Length; i++)
            {
                if (i == mainIndex) continue;
                var modifier = ModifierWord(tokens[i]);
                if (modifier == KeyModifiers.None && TryKey(tokens[i], out var key)) modifier = ModifierOf(key);
                // A non-modifier extra key cannot be expressed; keep the main key alone.
                modifiers |= modifier;
            }
            modifiers &= ~ModifierOf(main);
            return new BindingRow { Kind = RowKind.Key, Key = main, Modifiers = modifiers };
        }

        private static BindingRow? ParseDevice(string[] fields)
        {
            if (fields.Length != 9 || (fields[6] != "axis" && fields[6] != "button")) return null;
            if (!IsName(fields[0]) || !Enum.TryParse(fields[0], false, out DeviceKind kind) || !Enum.IsDefined(typeof(DeviceKind), kind)) return null;
            if (!int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0) return null;
            if (!int.TryParse(fields[7], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sign) || (sign != 1 && sign != -1)) return null;
            string deviceGuid, hardwareGuid;
            try { deviceGuid = Uri.UnescapeDataString(fields[1]); hardwareGuid = Uri.UnescapeDataString(fields[2]); }
            catch (UriFormatException) { return null; }
            if ((deviceGuid.Length > 0 && !Guid.TryParse(deviceGuid, out _)) || (hardwareGuid.Length > 0 && !Guid.TryParse(hardwareGuid, out _))) return null;
            bool axis = fields[6] == "axis";
            if (!axis && sign != 1) return null;
            try
            {
                return new BindingRow
                {
                    Kind = RowKind.Device, DeviceKind = kind, DeviceGuid = deviceGuid, HardwareGuid = hardwareGuid,
                    HardwareIdentifier = Uri.UnescapeDataString(fields[3]), DeviceName = Uri.UnescapeDataString(fields[4]),
                    ElementId = id, IsAxis = axis, AxisSign = sign, ControlName = Uri.UnescapeDataString(fields[8])
                };
            }
            catch (UriFormatException) { return null; }
        }

        private static bool IsName(string value) => value.Length > 0 && char.IsLetter(value[0]);

        internal static bool TryKey(string token, out KeyCode key)
        {
            key = KeyCode.None;
            if (!IsName(token) || !Enum.TryParse(token, true, out key) || !Enum.IsDefined(typeof(KeyCode), key) || key == KeyCode.None) return false;
            return true;
        }

        internal static bool IsJoystickKey(KeyCode key, out int button)
        {
            button = -1;
            if (key < KeyCode.JoystickButton0) return false;
            var name = key.ToString();
            int index = name.LastIndexOf("Button", StringComparison.Ordinal);
            if (index < 0 || !int.TryParse(name.Substring(index + 6), NumberStyles.None, CultureInfo.InvariantCulture, out button)) button = -1;
            return true;
        }

        private static KeyModifiers ModifierWord(string token)
        {
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase)) return KeyModifiers.Ctrl;
            if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase)) return KeyModifiers.Shift;
            if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase)) return KeyModifiers.Alt;
            return KeyModifiers.None;
        }

        internal static KeyModifiers ModifierOf(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftControl: case KeyCode.RightControl: return KeyModifiers.Ctrl;
                case KeyCode.LeftShift: case KeyCode.RightShift: return KeyModifiers.Shift;
                case KeyCode.LeftAlt: case KeyCode.RightAlt: case KeyCode.AltGr: return KeyModifiers.Alt;
                default: return KeyModifiers.None;
            }
        }

        internal static string KeyLabel(KeyCode key, KeyModifiers modifiers)
        {
            var text = "";
            if ((modifiers & KeyModifiers.Ctrl) != 0) text += "Ctrl + ";
            if ((modifiers & KeyModifiers.Shift) != 0) text += "Shift + ";
            if ((modifiers & KeyModifiers.Alt) != 0) text += "Alt + ";
            string name;
            switch (key)
            {
                case KeyCode.Mouse0: name = "Left mouse button"; break;
                case KeyCode.Mouse1: name = "Right mouse button"; break;
                case KeyCode.Mouse2: name = "Middle mouse button"; break;
                case KeyCode.Mouse3: case KeyCode.Mouse4: case KeyCode.Mouse5: case KeyCode.Mouse6:
                    name = "Mouse button " + (key - KeyCode.Mouse0 + 1).ToString(CultureInfo.InvariantCulture); break;
                default: name = key.ToString(); break;
            }
            return text + name;
        }

        internal static string PadLabel(GamepadControl pad)
        {
            switch (pad)
            {
                case GamepadControl.DPadUp: return "D-pad up";
                case GamepadControl.DPadDown: return "D-pad down";
                case GamepadControl.DPadLeft: return "D-pad left";
                case GamepadControl.DPadRight: return "D-pad right";
                case GamepadControl.ButtonSouth: return "A / Cross";
                case GamepadControl.ButtonEast: return "B / Circle";
                case GamepadControl.ButtonWest: return "X / Square";
                case GamepadControl.ButtonNorth: return "Y / Triangle";
                case GamepadControl.LeftShoulder: return "left bumper";
                case GamepadControl.RightShoulder: return "right bumper";
                case GamepadControl.LeftTrigger: return "left trigger";
                case GamepadControl.RightTrigger: return "right trigger";
                case GamepadControl.LeftStickButton: return "left stick click";
                case GamepadControl.RightStickButton: return "right stick click";
                case GamepadControl.Select: return "Back / Select";
                case GamepadControl.Start: return "Start";
                default: return pad.ToString();
            }
        }

        private static string Escape(string value) => Uri.EscapeDataString(value ?? "");

        // Joystick identity: the persistent device GUID when present, otherwise a unique
        // hardware type plus identifier. Keyboard and mouse are single devices.
        internal static bool MatchesDevice(BindingRow row, DeviceKind kind, string deviceGuid, string hardwareGuid, string hardwareIdentifier)
        {
            if (row.Kind != RowKind.Device || row.DeviceKind != kind) return false;
            if (kind != DeviceKind.Joystick) return true;
            if (!string.IsNullOrEmpty(row.DeviceGuid))
                return Guid.TryParse(row.DeviceGuid, out var selected) && selected != Guid.Empty && Guid.TryParse(deviceGuid, out var actual) && selected == actual;
            return Guid.TryParse(row.HardwareGuid, out var selectedHardware) && Guid.TryParse(hardwareGuid, out var actualHardware) && selectedHardware == actualHardware && row.HardwareIdentifier == hardwareIdentifier;
        }

        internal static int UniqueMatch<T>(IList<T> candidates, Func<T, bool> matches)
        {
            int result = -1;
            for (int i = 0; i < candidates.Count; i++) if (matches(candidates[i])) { if (result >= 0) return -1; result = i; }
            return result;
        }

        // A fresh axis direction: moved past 0.6 from a start that was not already that way.
        internal static int CaptureAxisSign(float start, float value)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) || float.IsNaN(value) || float.IsInfinity(value)) return 0;
            if (value >= .6f && start <= .25f) return 1;
            if (value <= -.6f && start >= -.25f) return -1;
            return 0;
        }

        internal static bool CanReserveAxis(bool nativeButtonAction, bool fullAxis, int mappingSign, int bindingSign) => nativeButtonAction && !fullAxis && mappingSign == bindingSign;
    }

    // Observe during settling too, so an early or maintained press cannot become a capture.
    internal sealed class CaptureButtonEdges
    {
        private readonly HashSet<int> held = new HashSet<int>();
        internal bool Observe(int id, bool down, bool ready)
        {
            if (!down) { held.Remove(id); return false; }
            return held.Add(id) && ready;
        }
        internal void Clear() => held.Clear();
    }
}
