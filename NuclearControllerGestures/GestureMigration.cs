using System;
using System.Collections.Generic;
using NuclearBindings;

namespace NuclearControllerGestures
{
    // 0.1.x stored one native action per named gamepad control under [Double Press Actions].
    // Each assigned control becomes a numbered double press with a gamepad binding row.
    internal static class GestureMigration
    {
        internal static List<(string control, string action)> Read(Func<string, string?> take)
        {
            var result = new List<(string, string)>();
            foreach (GamepadControl control in Enum.GetValues(typeof(GamepadControl)))
            {
                // Take every old key, so all of them leave the configuration.
                var action = take(control.ToString());
                if (action != null && action.Trim().Length > 0 && action != "Unbound") result.Add(("Gamepad:" + control, action.Trim()));
            }
            return result;
        }
    }
}
