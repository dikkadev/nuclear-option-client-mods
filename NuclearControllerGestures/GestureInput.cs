using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using NuclearOption.MissionEditorScripts;
using Rewired;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace NuclearControllerGestures
{
    public enum ControllerBinding
    {
        Unbound, DPadUp, DPadDown, DPadLeft, DPadRight,
        ButtonSouth, ButtonEast, ButtonWest, ButtonNorth,
        LeftShoulder, RightShoulder, LeftTrigger, RightTrigger,
        LeftStickButton, RightStickButton, Select, Start
    }

    internal static class GestureInput
    {
        private const string ConfigurationManagerGuid = "com.bepis.bepinex.configurationmanager";
        private static BaseUnityPlugin? configurationManager;
        private static PropertyInfo? displayingWindow;

        internal static bool IsBlocked()
        {
            if (!Application.isFocused || (CursorManager.GetFlags() & ~CursorFlags.Map) != CursorFlags.None ||
                InputFieldChecker.InsideInputField || ConfigurationWindowOpen())
                return true;
            var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (selected == null) return false;
            var input = selected.GetComponentInParent<InputField>();
            var tmpInput = selected.GetComponentInParent<TMP_InputField>();
            return (input != null && input.isFocused) || (tmpInput != null && tmpInput.isFocused);
        }

        private static bool ConfigurationWindowOpen()
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
            catch (System.Exception) { return true; }
        }

        // Read exactly one authoritative source: named gamepad D-pad, otherwise the
        // first declared directional pad, otherwise the first POV hat.
        private static int DPadState(Joystick joystick)
        {
            var template = joystick.GetTemplate<IGamepadTemplate>();
            if (template != null)
                return Mask(Held(template.dPad?.up), Held(template.dPad?.down), Held(template.dPad?.left), Held(template.dPad?.right));
            foreach (var pad in joystick.DirectionalPads)
                return Mask(Held(pad.buttonUp), Held(pad.buttonDown), Held(pad.buttonLeft), Held(pad.buttonRight));
            foreach (var hat in joystick.Hats)
            {
                int state = Mask(Held(hat.buttonUp), Held(hat.buttonDown), Held(hat.buttonLeft), Held(hat.buttonRight));
                if (Held(hat.buttonUpLeft) || Held(hat.buttonUpRight) || Held(hat.buttonDownLeft) || Held(hat.buttonDownRight)) state |= 16;
                return state;
            }
            return 0;
        }
        private static int Mask(bool up, bool down, bool left, bool right) => (up ? 1 : 0) | (down ? 2 : 0) | (left ? 4 : 0) | (right ? 8 : 0);
        internal static bool InvalidDPad(Joystick joystick)
        {
            int state = DPadState(joystick);
            return (state & 16) != 0 || (state != 0 && (state & (state - 1)) != 0);
        }
        internal static bool DPadNonNeutral(Joystick joystick) => DPadState(joystick) != 0;
        internal static bool IsHeld(Joystick joystick, ControllerBinding binding)
        {
            switch (binding)
            {
                case ControllerBinding.DPadUp: return DPadState(joystick) == 1;
                case ControllerBinding.DPadDown: return DPadState(joystick) == 2;
                case ControllerBinding.DPadLeft: return DPadState(joystick) == 4;
                case ControllerBinding.DPadRight: return DPadState(joystick) == 8;
            }
            var template = joystick.GetTemplate<IGamepadTemplate>();
            return template != null && TemplateHeld(template, binding);
        }
        private static bool Held(Controller.Button? button) => button != null && button.value;
        private static bool Held(IControllerTemplateButton? button) => button != null && button.value;

        private static bool TemplateHeld(IGamepadTemplate pad, ControllerBinding binding)
        {
            switch (binding)
            {
                case ControllerBinding.DPadUp: return Held(pad.dPad?.up);
                case ControllerBinding.DPadDown: return Held(pad.dPad?.down);
                case ControllerBinding.DPadLeft: return Held(pad.dPad?.left);
                case ControllerBinding.DPadRight: return Held(pad.dPad?.right);
                case ControllerBinding.ButtonSouth: return Held(pad.a);
                case ControllerBinding.ButtonEast: return Held(pad.b);
                case ControllerBinding.ButtonWest: return Held(pad.x);
                case ControllerBinding.ButtonNorth: return Held(pad.y);
                case ControllerBinding.LeftShoulder: return Held(pad.leftBumper);
                case ControllerBinding.RightShoulder: return Held(pad.rightBumper);
                case ControllerBinding.LeftTrigger: return pad.leftTrigger != null && pad.leftTrigger.value >= 0.5f;
                case ControllerBinding.RightTrigger: return pad.rightTrigger != null && pad.rightTrigger.value >= 0.5f;
                case ControllerBinding.LeftStickButton: return Held(pad.leftStick?.press);
                case ControllerBinding.RightStickButton: return Held(pad.rightStick?.press);
                case ControllerBinding.Select: return Held(pad.back);
                case ControllerBinding.Start: return Held(pad.start);
                default: return false;
            }
        }
    }
}
