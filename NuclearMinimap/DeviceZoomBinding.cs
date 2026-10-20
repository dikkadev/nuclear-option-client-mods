using System;
using System.Collections.Generic;
using System.Globalization;


namespace NuclearMinimap
{
    internal enum DeviceKind { Keyboard, Mouse, Joystick }
    internal sealed class DeviceZoomBinding
    {
        internal bool Inward;
        internal DeviceKind DeviceKind;
        internal string DeviceGuid = "", HardwareGuid = "", HardwareIdentifier = "", DeviceName = "", ControlName = "";
        internal int ElementId, AxisSign = 1;
        internal bool IsAxis;
        internal string Key => DeviceKind + ":" + DeviceGuid + ":" + HardwareGuid + ":" + HardwareIdentifier + ":" + ElementId.ToString(CultureInfo.InvariantCulture) + ":" + IsAxis + ":" + AxisSign;
        internal bool AxisDown(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value * AxisSign >= .5f;
    }
    internal static class DeviceZoomBindings
    {
        internal static int CaptureAxisSign(float start, float value)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) || float.IsNaN(value) || float.IsInfinity(value)) return 0;
            if (value >= .6f && start <= .25f) return 1;
            if (value <= -.6f && start >= -.25f) return -1;
            return 0;
        }
        internal static bool MatchesDevice(DeviceZoomBinding row, DeviceKind kind, string deviceGuid, string hardwareGuid, string hardwareIdentifier)
        {
            if (row.DeviceKind != kind) return false;
            if (kind != DeviceKind.Joystick) return true;
            if (!string.IsNullOrEmpty(row.DeviceGuid))
                return Guid.TryParse(row.DeviceGuid, out var selected) && selected != Guid.Empty && Guid.TryParse(deviceGuid, out var actual) && selected == actual;
            return Guid.TryParse(row.HardwareGuid, out var selectedHardware) && Guid.TryParse(hardwareGuid, out var actualHardware) && selectedHardware == actualHardware && row.HardwareIdentifier == hardwareIdentifier;
        }
        internal static bool CanReserveAxis(bool nativeButtonAction, bool fullAxis, int mappingSign, int bindingSign) => nativeButtonAction && !fullAxis && mappingSign == bindingSign;
        private static string Escape(string value) => Uri.EscapeDataString(value ?? "");
        internal static string Serialize(IEnumerable<DeviceZoomBinding> rows)
        {
            var lines = new List<string> { "v1" };
            var seen = new HashSet<string>();
            foreach (var row in rows)
            {
                if (!seen.Add(row.Key + ":" + row.Inward)) continue;
                lines.Add(string.Join("|", row.Inward ? "in" : "out", row.DeviceKind.ToString(), Escape(row.DeviceGuid), Escape(row.HardwareGuid), Escape(row.HardwareIdentifier), Escape(row.DeviceName), row.ElementId.ToString(CultureInfo.InvariantCulture), row.IsAxis ? "axis" : "button", row.AxisSign.ToString(CultureInfo.InvariantCulture), Escape(row.ControlName)));
            }
            return string.Join(";", lines);
        }
        internal static List<DeviceZoomBinding> Parse(string text, out string error)
        {
            error = "";
            var rows = new List<DeviceZoomBinding>();
            if (string.IsNullOrWhiteSpace(text)) return rows;
            try
            {
                var lines = text.Split(';');
                if (lines[0] != "v1") throw new FormatException("Unsupported device binding version.");
                var seen = new HashSet<string>();
                for (int i = 1; i < lines.Length; i++)
                {
                    var fields = lines[i].Split('|');
                    if (fields.Length != 10 || (fields[0] != "in" && fields[0] != "out") || (fields[7] != "axis" && fields[7] != "button")) throw new FormatException("Invalid device binding row.");
                    if (!Enum.TryParse(fields[1], false, out DeviceKind kind) || !Enum.IsDefined(typeof(DeviceKind), kind)) throw new FormatException("Invalid device type.");
                    if (!int.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id < 0 || !int.TryParse(fields[8], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int sign) || (sign != 1 && sign != -1)) throw new FormatException("Invalid control identifier or axis sign.");
                    string deviceGuid = Uri.UnescapeDataString(fields[2]), hardwareGuid = Uri.UnescapeDataString(fields[3]);
                    if ((deviceGuid.Length > 0 && !Guid.TryParse(deviceGuid, out _)) || (hardwareGuid.Length > 0 && !Guid.TryParse(hardwareGuid, out _))) throw new FormatException("Invalid device GUID.");
                    var row = new DeviceZoomBinding { Inward = fields[0] == "in", DeviceKind = kind, DeviceGuid = deviceGuid, HardwareGuid = hardwareGuid, HardwareIdentifier = Uri.UnescapeDataString(fields[4]), DeviceName = Uri.UnescapeDataString(fields[5]), ElementId = id, IsAxis = fields[7] == "axis", AxisSign = sign, ControlName = Uri.UnescapeDataString(fields[9]) };
                    if (!row.IsAxis && sign != 1) throw new FormatException("Invalid button axis sign.");
                    if (seen.Add(row.Key + ":" + row.Inward)) rows.Add(row);
                }
                return rows;
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            { error = exception.Message; return new List<DeviceZoomBinding>(); }
        }
        internal static int UniqueMatch<T>(IList<T> candidates, Func<T, bool> matches)
        {
            int result = -1;
            for (int i = 0; i < candidates.Count; i++) if (matches(candidates[i])) { if (result >= 0) return -1; result = i; }
            return result;
        }
    }
}
