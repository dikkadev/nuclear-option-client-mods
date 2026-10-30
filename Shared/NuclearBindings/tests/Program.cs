using BepInEx.Configuration;
using BepInEx.Logging;
using NuclearBindings;
using NuclearControllerGestures;
using NuclearMinimap;
using UnityEngine;

int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
string Canon(string? text) => BindingText.Canonical(text);
BindingRow One(string text) { var rows = BindingText.Parse(text); if (rows.Count != 1) throw new InvalidOperationException("expected one row: " + text); return rows[0]; }

// Saved BepInEx KeyboardShortcut text, as the user's configs hold it.
Check(Canon("Slash") == "Slash", "plain key stays");
Check(Canon("F10") == "F10", "function key stays");
Check(Canon("F7 + LeftControl") == "Ctrl+F7", "BepInEx combination becomes Ctrl+F7");
Check(Canon("F10 + LeftControl") == "Ctrl+F10", "Replay default converts");
Check(Canon("Z + RightShift + LeftAlt") == "Shift+Alt+Z", "both sides of modifiers convert");
Check(Canon("") == "" && Canon("None") == "" && Canon(null) == "", "empty and None are unbound");
Check(Canon("LeftControl") == "LeftControl", "a modifier alone is a main key");
Check(Canon("LeftShift + LeftControl") == "Ctrl+LeftShift", "BepInEx main modifier keeps its own key");
Check(Canon("F + G") == "F", "a non-modifier extra key is dropped");
Check(Canon("Mouse3") == "Mouse3" && One("Mouse3").Describe() == "Mouse button 4", "mouse buttons stay keys");
Check(Canon("JoystickButton0") == "Gamepad:ButtonSouth", "Unity joystick button 0 becomes gamepad A");
Check(Canon("Joystick2Button7") == "Gamepad:Start", "numbered joystick button converts by button index");
Check(Canon("JoystickButton9 + LeftControl") == "Gamepad:RightStickButton", "joystick modifiers are dropped");
var unmapped = BindingText.Parse("JoystickButton15");
Check(unmapped.Count == 1 && unmapped[0].Kind == RowKind.Invalid && Canon("JoystickButton15") == "JoystickButton15", "unmappable joystick button stays visible as unreadable");

// Current row format.
Check(Canon("ctrl+shift+f7") == "Ctrl+Shift+F7", "words are case-insensitive and canonical");
Check(Canon("Alt+Ctrl+F1") == "Ctrl+Alt+F1", "modifier order is canonical");
Check(Canon("Gamepad:buttonsouth") == "Gamepad:ButtonSouth", "gamepad control names are case-insensitive");
Check(Canon("F10; Gamepad:DPadUp ;F10") == "F10; Gamepad:DPadUp", "rows trim and deduplicate");
Check(BindingText.Parse("Gamepad:Paddle1")[0].Kind == RowKind.Invalid, "unknown gamepad control is unreadable");
Check(BindingText.Parse("5")[0].Kind == RowKind.Invalid && BindingText.Parse("Ctrl+")[0].Kind == RowKind.Invalid, "numbers and dangling plus are unreadable");
var mixed = BindingText.Parse("F8; bogus; Gamepad:Start");
Check(mixed.Count == 3 && mixed[0].Kind == RowKind.Key && mixed[1].Kind == RowKind.Invalid && mixed[2].Kind == RowKind.Gamepad, "one unreadable row keeps the others active");
Check(One("Ctrl+F7").Describe() == "Ctrl + F7" && One("Gamepad:ButtonSouth").Describe() == "Gamepad A / Cross" && One("Gamepad:RightTrigger").Describe() == "Gamepad right trigger", "rows describe controls by name");
var keyRow = One("Ctrl+Alt+F7");
Check(keyRow.Key == KeyCode.F7 && keyRow.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Alt), "key row holds main key and modifiers");
Check(One("Ctrl+F7").Identity != One("F7").Identity, "a combination differs from its bare key");

// Device rows (Minimap 0.3 identity), including the user's VKB row text.
var vkb = One("Device:Joystick|4fd75e3b-5840-5789-a3a9-f0a35e728902|00000000-0000-0000-0000-000000000000|WindowsRawInputVKBsimGladiatorEVOR0200231D|%20VKBsim%20Gladiator%20EVO%20R%20%20|163|button|1|Hat%200%20Left");
Check(vkb.Kind == RowKind.Device && vkb.ElementId == 163 && !vkb.IsAxis && vkb.Describe() == "VKBsim Gladiator EVO R: Hat 0 Left", "device row parses and names device and control");
Check(BindingText.MatchesDevice(vkb, DeviceKind.Joystick, "4FD75E3B-5840-5789-A3A9-F0A35E728902", Guid.Empty.ToString("D"), "other"), "device GUID matches case-insensitively");
Check(!BindingText.MatchesDevice(vkb, DeviceKind.Joystick, Guid.NewGuid().ToString("D"), Guid.Empty.ToString("D"), "WindowsRawInputVKBsimGladiatorEVOR0200231D"), "another device never matches a GUID row");
var axis = new BindingRow { Kind = RowKind.Device, DeviceKind = DeviceKind.Joystick, DeviceGuid = Guid.NewGuid().ToString("D"), HardwareGuid = Guid.NewGuid().ToString("D"), HardwareIdentifier = "Stick; |= %", DeviceName = "Flight stick", ElementId = 14, IsAxis = true, AxisSign = -1, ControlName = "Axis;Left" };
var axisText = BindingText.Serialize(new[] { axis, axis });
var axisBack = BindingText.Parse(axisText);
Check(axisBack.Count == 1 && axisBack[0].Identity == axis.Identity && axisBack[0].ControlName == "Axis;Left" && axisBack[0].HardwareIdentifier == "Stick; |= %", "device row escapes separators and roundtrips");
Check(axisBack[0].Describe() == "Flight stick: Axis;Left -", "axis rows show their direction");
Check(axis.AxisDown(-.5f) && !axis.AxisDown(-.499f) && !axis.AxisDown(.9f) && !axis.AxisDown(float.NaN), "axis direction threshold and nonfinite safety");
foreach (var bad in new[] { "Device:Joystick|||||12|axis|2|A", "Device:Joystick|wrong||||12|button|1|B", "Device:Joystick|||||-1|button|1|B", "Device:Pad|||||1|button|1|B", "Device:Joystick|||||1|button|-1|B", "Device:Joystick||||1|button|1" })
    Check(BindingText.Parse(bad)[0].Kind == RowKind.Invalid, "invalid device row stays unreadable: " + bad);
var hardware = Guid.NewGuid().ToString("D");
var noGuid = new BindingRow { Kind = RowKind.Device, DeviceKind = DeviceKind.Joystick, HardwareGuid = hardware, HardwareIdentifier = "Same model" };
Check(BindingText.MatchesDevice(noGuid, DeviceKind.Joystick, Guid.NewGuid().ToString("D"), hardware, "Same model") && !BindingText.MatchesDevice(noGuid, DeviceKind.Joystick, "", hardware, "Other"), "hardware identity applies only without a device GUID");
Check(BindingText.UniqueMatch(new[] { "a", "a", "b" }, x => x == "a") == -1 && BindingText.UniqueMatch(new[] { "a", "a", "b" }, x => x == "b") == 2, "identical devices stay ambiguous");

// Capture rules shared with Minimap 0.3.1.
Check(BindingText.CaptureAxisSign(.8f, .9f) == 0 && BindingText.CaptureAxisSign(0f, .59f) == 0 && BindingText.CaptureAxisSign(0f, .6f) == 1 && BindingText.CaptureAxisSign(-1f, .6f) == 1 && BindingText.CaptureAxisSign(0f, -.6f) == -1 && BindingText.CaptureAxisSign(float.NaN, 1f) == 0, "axis capture needs a fresh direction");
var edges = new CaptureButtonEdges();
Check(!edges.Observe(1, true, false) && !edges.Observe(1, true, true) && edges.Observe(2, true, true) && !edges.Observe(2, true, true), "capture takes a fresh button beside a held one");
Check(!edges.Observe(1, false, true) && edges.Observe(1, true, true), "a held button captures after release and press");
Check(BindingText.CanReserveAxis(true, false, -1, -1) && !BindingText.CanReserveAxis(true, true, -1, -1) && !BindingText.CanReserveAxis(false, false, -1, -1), "axis reservation keeps full and analog mappings");

// Minimap 0.4 settings, as saved on the user's PC.
const string vkbRows = "v1;in|Joystick|4fd75e3b-5840-5789-a3a9-f0a35e728902|00000000-0000-0000-0000-000000000000|WindowsRawInputVKBsimGladiatorEVOR0200231D|%20VKBsim%20Gladiator%20EVO%20R%20%20|163|button|1|Hat%200%20Left;out|Joystick|4fd75e3b-5840-5789-a3a9-f0a35e728902|00000000-0000-0000-0000-000000000000|WindowsRawInputVKBsimGladiatorEVOR0200231D|%20VKBsim%20Gladiator%20EVO%20R%20%20|161|button|1|Hat%200%20Right;in|Joystick|4fd75e3b-5840-5789-a3a9-f0a35e728902|00000000-0000-0000-0000-000000000000|WindowsRawInputVKBsimGladiatorEVOR0200231D|%20VKBsim%20Gladiator%20EVO%20R%20%20|56|button|1|Button%2024;out|Joystick|4fd75e3b-5840-5789-a3a9-f0a35e728902|00000000-0000-0000-0000-000000000000|WindowsRawInputVKBsimGladiatorEVOR0200231D|%20VKBsim%20Gladiator%20EVO%20R%20%20|57|button|1|Button%2025";
Check(MinimapBindingMigration.Convert("", "", "DPadUp", "DPadDown", vkbRows, out var zoomIn, out var zoomOut, out var problem) && problem == "", "Minimap settings convert");
var inRows = BindingText.Parse(zoomIn); var outRows = BindingText.Parse(zoomOut);
Check(inRows.Count == 3 && inRows[0].Kind == RowKind.Gamepad && inRows[0].Pad == GamepadControl.DPadUp && inRows[1].ElementId == 163 && inRows[2].ElementId == 56, "zoom in keeps D-pad up and both VKB controls");
Check(outRows.Count == 3 && outRows[0].Pad == GamepadControl.DPadDown && outRows[1].ElementId == 161 && outRows[2].ElementId == 57, "zoom out keeps D-pad down and both VKB controls");
Check(MinimapBindingMigration.Convert("Z + LeftControl", null, "Unbound", "RightTrigger", null, out zoomIn, out zoomOut, out _) && zoomIn == "Ctrl+Z" && zoomOut == "Gamepad:RightTrigger", "Minimap keyboard shortcut and controller choice convert");
Check(MinimapBindingMigration.Convert(null, null, null, null, "v1", out zoomIn, out zoomOut, out _) && zoomIn == "Gamepad:DPadUp" && zoomOut == "Gamepad:DPadDown", "missing old values use old defaults");
Check(!MinimapBindingMigration.Convert(null, null, null, null, null, out _, out _, out _), "no old settings, no conversion");
Check(MinimapBindingMigration.Convert(null, null, null, null, "v1;in|Joystick|bad|||x|1|button|1|B", out zoomIn, out _, out problem) && zoomIn == "Gamepad:DPadUp" && problem.Length > 0, "unreadable old device rows are reported, not imported");

// ControllerGestures 0.1 settings.
var old = new Dictionary<string, string> { ["DPadLeft"] = "Eject", ["ButtonNorth"] = "Unbound", ["Start"] = " Radar " };
var taken = new List<string>();
var slots = GestureMigration.Read(name => { taken.Add(name); return old.TryGetValue(name, out var v) ? v : null; });
Check(taken.Count == 16 && slots.Count == 2 && slots[0] == ("Gamepad:DPadLeft", "Eject") && slots[1] == ("Gamepad:Start", "Radar"), "every old gesture key is read; assigned ones become slots");

// Real BepInEx configuration files.
var directory = Path.Combine(Path.GetTempPath(), "nuclear-bindings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
using var log = new ManualLogSource("Binding checks");
try
{
    var path = Path.Combine(directory, "dev.dikka.test.cfg");
    File.WriteAllText(path, "[Cockpit Movement]\r\nLock Horizon = Slash\r\n[Controls]\r\nToggle Recording = F9 + LeftControl\r\nNext = \r\nHold Zoom In = \r\nDevice Zoom Bindings = " + vkbRows + "\r\n");
    var config = new ConfigFile(path, false);
    var lockHorizon = config.Bind("Cockpit Movement", "Lock Horizon", "", "test");
    BindingMigration.Normalize(lockHorizon, log);
    Check(lockHorizon.Value == "Slash", "saved Lock Horizon key survives as a string binding");
    var recording = config.Bind("Controls", "Toggle Recording", "Ctrl+F9", "test");
    BindingMigration.Normalize(recording, log);
    Check(recording.Value == "Ctrl+F9", "saved combination converts in place");
    var next = config.Bind("Controls", "Next", "", "test");
    BindingMigration.Normalize(next, log);
    Check(next.Value == "", "saved unbound stays unbound");
    var fresh = config.Bind("Controls", "Fresh", "F8", "test");
    Check(fresh.Value == "F8", "new setting uses the default");
    Check(BindingMigration.TakeLegacy(config, "Controls", "Hold Zoom In") == "" && BindingMigration.TakeLegacy(config, "Controls", "Device Zoom Bindings") == vkbRows, "old settings read exactly");
    Check(BindingMigration.TakeLegacy(config, "Controls", "Never Saved") == null, "never-saved old setting reads as missing");
    config.Save();
    var text = File.ReadAllText(path);
    Check(!text.Contains("Hold Zoom In") && !text.Contains("Device Zoom Bindings") && !text.Contains("Never Saved"), "taken old settings leave the file");
    var reload = new ConfigFile(path, false);
    Check(reload.Bind("Cockpit Movement", "Lock Horizon", "", "test").Value == "Slash" && reload.Bind("Controls", "Toggle Recording", "Ctrl+F9", "test").Value == "Ctrl+F9", "converted values survive restart");
    var rowsPath = Path.Combine(directory, "rows.cfg");
    var rowsConfig = new ConfigFile(rowsPath, true);
    rowsConfig.Bind("Controls", "Zoom In", "").Value = BindingText.Serialize(new[] { axis, vkb, One("Ctrl+F7") });
    var rowsBack = new ConfigFile(rowsPath, false).Bind("Controls", "Zoom In", "").Value;
    Check(BindingText.Canonical(rowsBack) == BindingText.Serialize(new[] { axis, vkb, One("Ctrl+F7") }), "rows survive the BepInEx file format");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine($"{passed} binding checks passed.");
