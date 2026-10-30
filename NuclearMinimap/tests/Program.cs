using BepInEx.Configuration;
using BepInEx.Logging;
using NuclearMinimap;

var directory = Path.Combine(Path.GetTempPath(), "nuclear-minimap-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
using var log = new ManualLogSource("Minimap migration checks");
try
{
    string legacy = Path.Combine(directory, "old.cfg"), target = Path.Combine(directory, "new.cfg");
    File.WriteAllText(legacy, "[General]\nEnabled = false\n[Display]\nHUD Minimap = AlwaysShow\nMinimap Orientation = HeadingUp\n[Controls]\nHold Zoom In = Z + LeftControl\nZoom Speed = 2.25\nZoom Hold Delay = 0.75\nConsume Zoom Bindings = false\n[Future]\nOpaque Value = a=b=c\n");
    var original = File.ReadAllBytes(legacy);
    var destination = new ConfigFile(target, true);
    Check(MinimapConfigMigration.Import(destination, log, legacy), "BepInEx header-only destination imports");
    Check(!destination.Bind("General", "Enabled", true).Value, "disabled master imports");
    Check(destination.Bind("Display", "HUD Minimap", "HideWhenMfdVisible").Value == "AlwaysShow", "HUD choice imports");
    Check(destination.Bind("Display", "Minimap Orientation", "NorthUp").Value == "HeadingUp", "orientation imports");
    Check(destination.Bind("Controls", "Hold Zoom In", "None").Value == "Z + LeftControl", "shortcut representation imports exactly");
    var speed = destination.Bind("Controls", "Zoom Speed", 1f);
    Check(speed.Value == 2.25f && (float)speed.DefaultValue == 1f, "numeric preference imports and reset default stays new");
    Check(destination.Bind("Controls", "Zoom Hold Delay", .3f).Value == .75f, "hold delay imports");
    Check(!destination.Bind("Controls", "Consume Zoom Bindings", true).Value, "consumption preference imports");
    Check(destination.Bind("Display", "Show Cockpit Minimap", true).Value, "new cockpit option defaults enabled");
    destination.Save();
    var reload = new ConfigFile(target, false);
    Check(reload.Bind("Future", "Opaque Value", "").Value == "a=b=c", "unknown values survive save and restart");
    Check(!MinimapConfigMigration.Import(reload, log, legacy), "saved imported config never reimports");
    Check(File.ReadAllBytes(legacy).SequenceEqual(original), "legacy bytes never change");
    File.WriteAllText(target, "[Controls]\nZoom Speed = 4.5\n");
    var existing = new ConfigFile(target, false);
    Check(!MinimapConfigMigration.Import(existing, log, legacy), "existing destination wins as whole config");
    Check(existing.Bind("Controls", "Zoom Speed", 1f).Value == 4.5f, "existing value remains");
    Check(existing.Bind("General", "Enabled", true).Value, "existing missing key uses new default");
    File.WriteAllText(target, "# header = comment\n; second = comment\n[Display]\n");
    Check(MinimapConfigMigration.Import(new ConfigFile(target, false), log, legacy), "comments and sections alone import");
    File.WriteAllText(target, "[Other]\nUnknown =\n");
    Check(!MinimapConfigMigration.Import(new ConfigFile(target, false), log, legacy), "unknown saved empty key protects destination");
    File.Delete(target);
    Check(MinimapConfigMigration.Import(new ConfigFile(target, false), log, legacy), "absent destination imports");
    File.Delete(target);
    Check(!MinimapConfigMigration.Import(new ConfigFile(target, false), log, legacy + ".missing"), "missing old configuration uses defaults");
    Check(File.ReadAllBytes(legacy).SequenceEqual(original), "all paths preserve legacy bytes");
    Console.WriteLine($"{passed} minimap migration checks passed.");
}
finally { Directory.Delete(directory, true); }
