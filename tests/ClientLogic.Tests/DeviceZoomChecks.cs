using NuclearMinimap;

internal static class DeviceZoomChecks
{
    internal static void Run(Action<string, Action> test)
    {
        void Require(bool value) { if (!value) throw new Exception("Device binding check failed."); }
        test("capture accepts fresh button beside a maintained HOTAS switch", () => {
            var edges = new CaptureButtonEdges();
            Require(!edges.Observe(1, true, false));
            Require(!edges.Observe(1, true, true));
            Require(edges.Observe(2, true, true));
            Require(!edges.Observe(2, true, true));
        });
        test("initial held capture button requires release and fresh press", () => {
            var edges = new CaptureButtonEdges();
            Require(!edges.Observe(1, true, false));
            Require(!edges.Observe(1, true, true));
            Require(!edges.Observe(1, false, true));
            Require(edges.Observe(1, true, true));
        });
        test("press during capture settling cannot capture later while held", () => {
            var edges = new CaptureButtonEdges();
            Require(!edges.Observe(1, false, false));
            Require(!edges.Observe(1, true, false));
            Require(!edges.Observe(1, true, true));
            Require(!edges.Observe(1, false, true));
            Require(edges.Observe(1, true, true));
        });
        test("device bindings roundtrip and deduplicate without losing opposite direction", () => {
            var row = new DeviceZoomBinding { Inward = true, DeviceKind = DeviceKind.Joystick, DeviceGuid = Guid.NewGuid().ToString("D"), HardwareGuid = Guid.NewGuid().ToString("D"), DeviceName = "Stick | ; %", HardwareIdentifier = "Model;X", ElementId = 12, IsAxis = true, AxisSign = -1, ControlName = "Axis | left" };
            var text = DeviceZoomBindings.Serialize(new[] { row, row });
            var rows = DeviceZoomBindings.Parse(text, out var error);
            Require(error == "" && rows.Count == 1 && rows[0].Key == row.Key && rows[0].DeviceName == row.DeviceName && rows[0].ControlName == row.ControlName);
            rows.Add(new DeviceZoomBinding { Inward = false, DeviceKind = row.DeviceKind, DeviceGuid = row.DeviceGuid, HardwareGuid = row.HardwareGuid, HardwareIdentifier = row.HardwareIdentifier, ElementId = 12, IsAxis = true, AxisSign = -1 });
            Require(DeviceZoomBindings.Parse(DeviceZoomBindings.Serialize(rows), out error).Count == 2);
        });
        test("invalid device rows disable entire configuration", () => {
            foreach (var text in new[] { "v2", "v1;in|Joystick|||||12|axis|-1|Axis;bad", "v1;in|99|||||12|button|1|Button", "v1;in|Joystick|wrong||||12|button|1|Button", "v1;in|Joystick|||||-1|button|1|Button" })
                Require(DeviceZoomBindings.Parse(text, out var error).Count == 0 && error.Length > 0);
            Require(DeviceZoomBindings.Parse("v1", out var emptyError).Count == 0 && emptyError == "");
        });
        test("axis polarity threshold and nonfinite safety", () => {
            var row = new DeviceZoomBinding { IsAxis = true, AxisSign = -1 };
            Require(row.AxisDown(-.5f) && !row.AxisDown(-.499f) && !row.AxisDown(.9f) && !row.AxisDown(float.NaN) && !row.AxisDown(float.NegativeInfinity));
        });
        test("ambiguous device fallback never selects first device", () => {
            var devices = new[] { "same", "same", "unique" };
            Require(DeviceZoomBindings.UniqueMatch(devices, x => x == "same") == -1);
            Require(DeviceZoomBindings.UniqueMatch(devices, x => x == "unique") == 2);
            Require(DeviceZoomBindings.UniqueMatch(devices, x => x == "missing") == -1);
        });
        test("axis capture requires release of initial held direction and ignores drift", () => {
            Require(DeviceZoomBindings.CaptureAxisSign(.8f, .9f) == 0);
            Require(DeviceZoomBindings.CaptureAxisSign(-.8f, -.9f) == 0);
            Require(DeviceZoomBindings.CaptureAxisSign(0f, .59f) == 0);
            Require(DeviceZoomBindings.CaptureAxisSign(0f, .6f) == 1);
            Require(DeviceZoomBindings.CaptureAxisSign(0f, -.6f) == -1);
            Require(DeviceZoomBindings.CaptureAxisSign(-1f, .6f) == 1);
            Require(DeviceZoomBindings.CaptureAxisSign(float.NaN, 1f) == 0);
            Require(DeviceZoomBindings.CaptureAxisSign(0f, float.PositiveInfinity) == 0);
        });
        test("preferred device identity separates same models and never falls back", () => {
            var hardware = Guid.NewGuid().ToString("D");
            var first = Guid.NewGuid().ToString("D");
            var second = Guid.NewGuid().ToString("D");
            var row = new DeviceZoomBinding { DeviceKind = DeviceKind.Joystick, DeviceGuid = first, HardwareGuid = hardware, HardwareIdentifier = "Same model" };
            Require(DeviceZoomBindings.MatchesDevice(row, DeviceKind.Joystick, first.ToUpperInvariant(), hardware, "Same model"));
            Require(!DeviceZoomBindings.MatchesDevice(row, DeviceKind.Joystick, second, hardware, "Same model"));
            Require(!DeviceZoomBindings.MatchesDevice(row, DeviceKind.Joystick, "", hardware, "Same model"));
            Require(!DeviceZoomBindings.MatchesDevice(row, DeviceKind.Mouse, first, hardware, "Same model"));
            row.DeviceGuid = "";
            Require(DeviceZoomBindings.MatchesDevice(row, DeviceKind.Joystick, second, hardware, "Same model"));
            Require(!DeviceZoomBindings.MatchesDevice(row, DeviceKind.Joystick, second, hardware, "Other model"));
        });        test("axis reservations preserve full and analog native mappings", () => {
            Require(DeviceZoomBindings.CanReserveAxis(true, false, -1, -1));
            Require(!DeviceZoomBindings.CanReserveAxis(true, true, -1, -1));
            Require(!DeviceZoomBindings.CanReserveAxis(false, false, -1, -1));
            Require(!DeviceZoomBindings.CanReserveAxis(true, false, 1, -1));
        });
    }
}
