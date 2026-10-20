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

namespace NuclearMinimap
{
    public enum ControllerMapBinding
    {
        Unbound, DPadUp, DPadDown, DPadLeft, DPadRight,
        ButtonSouth, ButtonEast, ButtonWest, ButtonNorth,
        LeftShoulder, RightShoulder, LeftTrigger, RightTrigger,
        LeftStickButton, RightStickButton, Select, Start
    }

    internal static class MapHoldInput
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
            catch (System.Exception) { return true; }
        }

        internal static bool IsHeld(KeyboardShortcut keyboard, ControllerMapBinding controller)
        {
            if (IsBlocked()) return false;
            if (keyboard.MainKey != KeyCode.None && keyboard.IsPressed()) return true;
            if (controller == ControllerMapBinding.Unbound || !ReInput.isReady) return false;
            var player = ReInput.players.GetPlayer(0);
            if (player == null) return false;
            foreach (var joystick in player.controllers.Joysticks)
            {
                var template = joystick.GetTemplate<IGamepadTemplate>();
                if (template != null)
                {
                    if (TemplateHeld(template, controller)) return true;
                    // A recognized gamepad's D-pad mapping is authoritative.
                    continue;
                }
                foreach (var pad in joystick.DirectionalPads)
                {
                    if ((controller == ControllerMapBinding.DPadUp && Held(pad.buttonUp)) ||
                        (controller == ControllerMapBinding.DPadDown && Held(pad.buttonDown)) ||
                        (controller == ControllerMapBinding.DPadLeft && Held(pad.buttonLeft)) ||
                        (controller == ControllerMapBinding.DPadRight && Held(pad.buttonRight))) return true;
                }
                foreach (var hat in joystick.Hats)
                {
                    // Eight-way hats expose diagonals independently; include them without changing native hat policy.
                    if ((controller == ControllerMapBinding.DPadUp && (Held(hat.buttonUp) || Held(hat.buttonUpLeft) || Held(hat.buttonUpRight))) ||
                        (controller == ControllerMapBinding.DPadDown && (Held(hat.buttonDown) || Held(hat.buttonDownLeft) || Held(hat.buttonDownRight))) ||
                        (controller == ControllerMapBinding.DPadLeft && (Held(hat.buttonLeft) || Held(hat.buttonUpLeft) || Held(hat.buttonDownLeft))) ||
                        (controller == ControllerMapBinding.DPadRight && (Held(hat.buttonRight) || Held(hat.buttonUpRight) || Held(hat.buttonDownRight)))) return true;
                }
            }
            return false;
        }

        private static bool Held(Controller.Button? button) => button != null && button.value;
        private static bool Held(IControllerTemplateButton? button) => button != null && button.value;

        private static bool TemplateHeld(IGamepadTemplate pad, ControllerMapBinding binding)
        {
            switch (binding)
            {
                case ControllerMapBinding.DPadUp: return Held(pad.dPad?.up);
                case ControllerMapBinding.DPadDown: return Held(pad.dPad?.down);
                case ControllerMapBinding.DPadLeft: return Held(pad.dPad?.left);
                case ControllerMapBinding.DPadRight: return Held(pad.dPad?.right);
                case ControllerMapBinding.ButtonSouth: return Held(pad.a);
                case ControllerMapBinding.ButtonEast: return Held(pad.b);
                case ControllerMapBinding.ButtonWest: return Held(pad.x);
                case ControllerMapBinding.ButtonNorth: return Held(pad.y);
                case ControllerMapBinding.LeftShoulder: return Held(pad.leftBumper);
                case ControllerMapBinding.RightShoulder: return Held(pad.rightBumper);
                case ControllerMapBinding.LeftTrigger: return pad.leftTrigger != null && pad.leftTrigger.value >= 0.5f;
                case ControllerMapBinding.RightTrigger: return pad.rightTrigger != null && pad.rightTrigger.value >= 0.5f;
                case ControllerMapBinding.LeftStickButton: return Held(pad.leftStick?.press);
                case ControllerMapBinding.RightStickButton: return Held(pad.rightStick?.press);
                case ControllerMapBinding.Select: return Held(pad.back);
                case ControllerMapBinding.Start: return Held(pad.start);
                default: return false;
            }
        }
    }
}
