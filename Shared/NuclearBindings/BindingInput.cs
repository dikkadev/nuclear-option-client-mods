using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using NuclearOption.MissionEditorScripts;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NuclearBindings
{
    // Reads bound controls. Keys and mouse buttons use Unity's keyboard input (as BepInEx
    // shortcuts do); every controller is read through Rewired, which is what the game uses.
    internal static class BindingInput
    {
        private static readonly List<Controller> devices = new List<Controller>();
        private static int devicesFrame = -1;
        private static string topology = "";

        // Every connected keyboard, mouse and joystick, including joysticks not assigned to player 0.
        internal static List<Controller> Devices()
        {
            if (devicesFrame == Time.frameCount) return devices;
            devicesFrame = Time.frameCount;
            devices.Clear();
            if (!ReInput.isReady) { topology = ""; return devices; }
            if (ReInput.controllers.Keyboard != null) devices.Add(ReInput.controllers.Keyboard);
            if (ReInput.controllers.Mouse != null) devices.Add(ReInput.controllers.Mouse);
            foreach (var joystick in ReInput.controllers.Joysticks) devices.Add(joystick);
            var text = "";
            foreach (var device in devices) text += device.type + ":" + device.id + ":" + RuntimeHelpers.GetHashCode(device) + ";";
            topology = text;
            return devices;
        }

        // Changes whenever a device connects, disconnects or is replaced.
        internal static string Topology { get { Devices(); return topology; } }

        // Joysticks assigned to player 0: the scope of "any gamepad" rows, as in the game.
        internal static IList<Joystick> PlayerJoysticks()
        {
            if (!ReInput.isReady) return Array.Empty<Joystick>();
            var player = ReInput.players.GetPlayer(0);
            return player != null ? player.controllers.Joysticks : (IList<Joystick>)Array.Empty<Joystick>();
        }

        internal static DeviceKind Kind(Controller controller) => controller is Joystick ? DeviceKind.Joystick : controller is Mouse ? DeviceKind.Mouse : DeviceKind.Keyboard;

        internal static Controller? Resolve(BindingRow row, IList<Controller> candidates)
        {
            if (row.Kind != RowKind.Device) return null;
            int index = BindingText.UniqueMatch(candidates, device => BindingText.MatchesDevice(row, Kind(device), device.deviceInstanceGuid.ToString("D"), device.hardwareTypeGuid.ToString("D"), device.hardwareIdentifier));
            return index < 0 ? null : candidates[index];
        }

        internal static BindingRow CreateDeviceRow(Controller controller, int elementId, bool isAxis, int sign)
        {
            var guid = controller.deviceInstanceGuid;
            var element = controller.GetElementIdentifierById(elementId);
            return new BindingRow
            {
                Kind = RowKind.Device, DeviceKind = Kind(controller), DeviceGuid = guid == Guid.Empty ? "" : guid.ToString("D"),
                HardwareGuid = controller.hardwareTypeGuid.ToString("D"), HardwareIdentifier = controller.hardwareIdentifier,
                DeviceName = controller.name, ElementId = elementId, IsAxis = isAxis, AxisSign = isAxis ? sign : 1,
                ControlName = element != null ? element.name : "Control " + elementId
            };
        }

        internal static bool Valid(Controller controller, BindingRow row)
        {
            try
            {
                if (row.Kind != RowKind.Device || Kind(controller) != row.DeviceKind) return false;
                var element = controller.GetElementById(row.ElementId);
                return row.IsAxis ? controller is Joystick && element is Controller.Axis : element is Controller.Button;
            }
            catch (Exception) { return false; }
        }

        internal static bool DeviceDown(Controller controller, BindingRow row)
        {
            try
            {
                if (!Valid(controller, row)) return false;
                if (row.IsAxis) return controller is ControllerWithAxes axes && row.AxisDown(axes.GetAxisById(row.ElementId));
                return controller.GetButtonById(row.ElementId);
            }
            catch (Exception) { return false; }
        }

        internal static bool KeyMainHeld(BindingRow row) => row.Kind == RowKind.Key && Input.GetKey(row.Key);

        internal static KeyModifiers HeldModifiers(KeyCode ignore = KeyCode.None)
        {
            var held = KeyModifiers.None;
            var own = BindingText.ModifierOf(ignore);
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) held |= KeyModifiers.Ctrl;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) held |= KeyModifiers.Shift;
            if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt) || Input.GetKey(KeyCode.AltGr)) held |= KeyModifiers.Alt;
            return held & ~own;
        }

        // The main key plus exactly the listed Ctrl/Shift/Alt modifiers; other keys may be held.
        // The test ignores the main key's own group, so Right Ctrl alone is a valid key.
        internal static bool KeyRowHeld(BindingRow row) => row.Kind == RowKind.Key && Input.GetKey(row.Key) && HeldModifiers(row.Key) == row.Modifiers;

        // Named gamepad control on one joystick. A recognized gamepad's template is authoritative;
        // other controllers map the D-pad to their directional pads or POV hats (diagonals count).
        internal static bool PadHeld(Joystick joystick, GamepadControl control)
        {
            var template = joystick.GetTemplate<IGamepadTemplate>();
            if (template != null) return TemplateHeld(template, control);
            foreach (var pad in joystick.DirectionalPads)
            {
                if ((control == GamepadControl.DPadUp && Held(pad.buttonUp)) || (control == GamepadControl.DPadDown && Held(pad.buttonDown)) ||
                    (control == GamepadControl.DPadLeft && Held(pad.buttonLeft)) || (control == GamepadControl.DPadRight && Held(pad.buttonRight))) return true;
            }
            foreach (var hat in joystick.Hats)
            {
                if ((control == GamepadControl.DPadUp && (Held(hat.buttonUp) || Held(hat.buttonUpLeft) || Held(hat.buttonUpRight))) ||
                    (control == GamepadControl.DPadDown && (Held(hat.buttonDown) || Held(hat.buttonDownLeft) || Held(hat.buttonDownRight))) ||
                    (control == GamepadControl.DPadLeft && (Held(hat.buttonLeft) || Held(hat.buttonUpLeft) || Held(hat.buttonDownLeft))) ||
                    (control == GamepadControl.DPadRight && (Held(hat.buttonRight) || Held(hat.buttonUpRight) || Held(hat.buttonDownRight)))) return true;
            }
            return false;
        }

        private static bool Held(Controller.Button? button) => button != null && button.value;
        private static bool Held(IControllerTemplateButton? button) => button != null && button.value;

        internal static bool TemplateHeld(IGamepadTemplate pad, GamepadControl control)
        {
            var element = TemplateElement(pad, control);
            if (element is IControllerTemplateButton button) return button.value;
            return element is IControllerTemplateAxis axis && axis.value >= .5f;
        }

        internal static IControllerTemplateElement? TemplateElement(IGamepadTemplate pad, GamepadControl control)
        {
            switch (control)
            {
                case GamepadControl.DPadUp: return pad.dPad?.up;
                case GamepadControl.DPadDown: return pad.dPad?.down;
                case GamepadControl.DPadLeft: return pad.dPad?.left;
                case GamepadControl.DPadRight: return pad.dPad?.right;
                case GamepadControl.ButtonSouth: return pad.a;
                case GamepadControl.ButtonEast: return pad.b;
                case GamepadControl.ButtonWest: return pad.x;
                case GamepadControl.ButtonNorth: return pad.y;
                case GamepadControl.LeftShoulder: return pad.leftBumper;
                case GamepadControl.RightShoulder: return pad.rightBumper;
                case GamepadControl.LeftTrigger: return pad.leftTrigger;
                case GamepadControl.RightTrigger: return pad.rightTrigger;
                case GamepadControl.LeftStickButton: return pad.leftStick?.press;
                case GamepadControl.RightStickButton: return pad.rightStick?.press;
                case GamepadControl.Select: return pad.back;
                case GamepadControl.Start: return pad.start;
                default: return null;
            }
        }

        internal static float TemplateValue(IGamepadTemplate pad, GamepadControl control)
        {
            var element = TemplateElement(pad, control);
            if (element is IControllerTemplateButton button) return button.value ? 1f : 0f;
            return element is IControllerTemplateAxis axis ? axis.value : 0f;
        }

        // Is any row held? Names the first held control for the fired log.
        // A device row on a joystick replaces "any gamepad" rows on that joystick.
        internal static bool AnyHeld(IList<BindingRow> rows, out string source)
        {
            source = "";
            bool hasController = false;
            foreach (var row in rows)
            {
                if (row.Kind == RowKind.Key && KeyRowHeld(row)) { source = "Keyboard/mouse: " + row.Describe(); return true; }
                if (row.Kind == RowKind.Device || row.Kind == RowKind.Gamepad) hasController = true;
            }
            if (!hasController || !ReInput.isReady) return false;
            var connected = Devices();
            HashSet<Controller>? custom = null;
            foreach (var row in rows)
            {
                if (row.Kind != RowKind.Device) continue;
                var device = Resolve(row, connected);
                if (device == null || !Valid(device, row)) continue;
                (custom ??= new HashSet<Controller>()).Add(device);
                if (DeviceDown(device, row)) { source = row.Describe() + " (device " + device.name.Trim() + ")"; return true; }
            }
            foreach (var row in rows)
            {
                if (row.Kind != RowKind.Gamepad) continue;
                foreach (var joystick in PlayerJoysticks())
                {
                    if (custom != null && custom.Contains(joystick)) continue;
                    if (PadHeld(joystick, row.Pad)) { source = row.Describe() + " (device " + joystick.name.Trim() + ")"; return true; }
                }
            }
            return false;
        }
    }

    internal static class InputContext
    {
        private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";
        private static BaseUnityPlugin? configurationManager;
        private static PropertyInfo? displayingWindow;

        // Bindings never fire without focus, while typing (text fields, chat), with F1 open
        // or in the pause menu, and, unless allowed, while the full map is open.
        internal static bool Blocked(bool allowFullMap)
        {
            if (!Application.isFocused || InputFieldChecker.InsideInputField || ConfigurationWindowOpen()) return true;
            if ((CursorManager.GetFlags() & (CursorFlags.GameMenu | CursorFlags.Chat | CursorFlags.Loading)) != CursorFlags.None) return true;
            if (!allowFullMap && DynamicMap.mapMaximized) return true;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var input = selected.GetComponentInParent<InputField>();
            var tmpInput = selected.GetComponentInParent<TMP_InputField>();
            return (input != null && input.isFocused) || (tmpInput != null && tmpInput.isFocused);
        }

        internal static bool ConfigurationWindowOpen()
        {
            if (configurationManager == null)
            {
                if (!Chainloader.PluginInfos.TryGetValue(ConfigurationManagerGuid, out var info) || info.Instance == null)
                    return false;
                configurationManager = info.Instance;
                displayingWindow = configurationManager.GetType().GetProperty("DisplayingWindow", BindingFlags.Public | BindingFlags.Instance);
            }
            if (displayingWindow == null || displayingWindow.PropertyType != typeof(bool)) return false;
            try { return displayingWindow.GetValue(configurationManager, null) is bool open && open; }
            catch (Exception) { return true; }
        }
    }
}
