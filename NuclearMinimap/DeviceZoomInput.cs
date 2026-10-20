using System;
using System.Collections.Generic;
using Rewired;

namespace NuclearMinimap
{
    internal static class DeviceZoomInput
    {
        internal static List<Controller> Devices()
        {
            var result = new List<Controller>();
            if (!ReInput.isReady) return result;
            if (ReInput.controllers.Keyboard != null) result.Add(ReInput.controllers.Keyboard);
            if (ReInput.controllers.Mouse != null) result.Add(ReInput.controllers.Mouse);
            foreach (var joystick in ReInput.controllers.Joysticks) result.Add(joystick);
            return result;
        }
        internal static DeviceKind Kind(Controller controller) => controller is Joystick ? DeviceKind.Joystick : controller is Mouse ? DeviceKind.Mouse : DeviceKind.Keyboard;
        internal static Controller? Resolve(DeviceZoomBinding row, IList<Controller> devices)
        {
            int index = DeviceZoomBindings.UniqueMatch(devices, device => DeviceZoomBindings.MatchesDevice(row, Kind(device), device.deviceInstanceGuid.ToString("D"), device.hardwareTypeGuid.ToString("D"), device.hardwareIdentifier));
            return index < 0 ? null : devices[index];
        }
        internal static DeviceZoomBinding Create(Controller controller, int elementId, bool isAxis, int sign, bool inward)
        {
            var guid = controller.deviceInstanceGuid;
            var element = controller.GetElementIdentifierById(elementId);
            return new DeviceZoomBinding { Inward = inward, DeviceKind = Kind(controller), DeviceGuid = guid == Guid.Empty ? "" : guid.ToString("D"), HardwareGuid = controller.hardwareTypeGuid.ToString("D"), HardwareIdentifier = controller.hardwareIdentifier, DeviceName = controller.name, ElementId = elementId, IsAxis = isAxis, AxisSign = isAxis ? sign : 1, ControlName = element != null ? element.name : "Control " + elementId };
        }
        internal static bool Valid(Controller controller, DeviceZoomBinding row)
        {
            try
            {
                if (Kind(controller) != row.DeviceKind) return false;
                var element = controller.GetElementById(row.ElementId);
                return row.IsAxis ? controller is Joystick && element is Controller.Axis && (row.AxisSign == 1 || row.AxisSign == -1) : element is Controller.Button;
            }
            catch (Exception) { return false; }
        }
        internal static bool Down(Controller controller, DeviceZoomBinding row)
        {
            try
            {
                if (!Valid(controller, row)) return false;
                if (row.IsAxis) return controller is ControllerWithAxes axes && controller.GetElementById(row.ElementId) is Controller.Axis && row.AxisDown(axes.GetAxisById(row.ElementId));
                return controller.GetElementById(row.ElementId) is Controller.Button && controller.GetButtonById(row.ElementId);
            }
            catch (Exception) { return false; }
        }
    }
}
