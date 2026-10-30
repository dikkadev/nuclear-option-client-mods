using NuclearBindings;
using Rewired;

namespace NuclearControllerGestures
{
    internal static class GestureInput
    {
        // The shared binding context plus any open game UI except the map itself.
        internal static bool IsBlocked() =>
            InputContext.Blocked(false) || (CursorManager.GetFlags() & ~CursorFlags.Map) != CursorFlags.None;

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
        internal static bool IsDPad(GamepadControl control) => control >= GamepadControl.DPadUp && control <= GamepadControl.DPadRight;
        internal static bool InvalidDPad(Joystick joystick)
        {
            int state = DPadState(joystick);
            return (state & 16) != 0 || (state != 0 && (state & (state - 1)) != 0);
        }
        internal static bool DPadNonNeutral(Joystick joystick) => DPadState(joystick) != 0;
        internal static bool IsHeld(Joystick joystick, GamepadControl control)
        {
            switch (control)
            {
                case GamepadControl.DPadUp: return DPadState(joystick) == 1;
                case GamepadControl.DPadDown: return DPadState(joystick) == 2;
                case GamepadControl.DPadLeft: return DPadState(joystick) == 4;
                case GamepadControl.DPadRight: return DPadState(joystick) == 8;
            }
            var template = joystick.GetTemplate<IGamepadTemplate>();
            return template != null && BindingInput.TemplateHeld(template, control);
        }
        private static bool Held(IControllerTemplateButton? button) => button != null && button.value;
        private static bool Held(Controller.Button? button) => button != null && button.value;
    }
}
