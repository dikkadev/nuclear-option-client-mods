using BepInEx.Configuration;
using BepInEx.Logging;
using NuclearModMigration;
using NuclearTargeting;

var directory = Path.Combine(Path.GetTempPath(), "nuclear-mod-migration-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed++; Console.WriteLine("PASS " + name); }
using var log = new ManualLogSource("Migration checks");
try
{
    var legacyPath = Path.Combine(directory, "legacy.cfg");
    File.WriteAllText(legacyPath, "[Targeting]\nEnable Alternative Targeting = false\nSelection Radius = 321.5\nPer Aircraft Tap Target Caps = Brawler=3, VT-7 Vagrant=2\n[Warnings]\nEnable Missile Voice Warnings = false\nMissile Voice Repeat Seconds = 4.25\n[Audio Test]\nPlay Audio Test = true\n");
    var original = File.ReadAllBytes(legacyPath);
    var newPath = Path.Combine(directory, "targeting.cfg");
    // Matches BepInEx's saveOnInit=true header-only configuration creation.
    var config = new ConfigFile(newPath, true);
    Check(File.Exists(newPath) && !LegacyConfig.HasSavedSettings(newPath), "header-only config remains eligible for migration");
    var migration = new LegacyConfig(config, log, legacyPath);
    Check(!migration.Bind("Targeting", "Enable Alternative Targeting", true, "").Value, "legacy disabled flag imports");
    var radius = migration.Bind("Targeting", "Selection Radius", 200f, "");
    Check(radius.Value == 321.5f, "legacy float imports without rounding");
    Check((float)radius.DefaultValue == 200f, "migration preserves factory reset default");
    Check(migration.Bind("Targeting", "Per Aircraft Tap Target Caps", "default", "").Value == "Brawler=3, VT-7 Vagrant=2", "legacy string containing equals imports exactly");
    Check(migration.Bind("Targeting", "New Setting", 9, "").Value == 9, "absent legacy key keeps factory default");
    Check(File.ReadAllBytes(legacyPath).SequenceEqual(original), "legacy config never rewritten");
    var reload = new LegacyConfig(new ConfigFile(newPath, false), log, legacyPath);
    Check(!reload.Bind("Targeting", "Enable Alternative Targeting", true, "").Value, "imported disabled value survives restart");
    File.WriteAllText(newPath, "[Targeting]\nSelection Radius = 44\n");
    var existing = new LegacyConfig(new ConfigFile(newPath, false), log, legacyPath);
    Check(existing.Bind("Targeting", "Selection Radius", 200f, "").Value == 44f, "existing destination value wins");
    Check(existing.Bind("Targeting", "Enable Alternative Targeting", true, "").Value, "existing destination missing key uses factory default rather than reimport");
    var soundPath = Path.Combine(directory, "sounds.cfg");
    var sounds = new ConfigFile(soundPath, true);
    var soundMigration = new LegacyConfig(sounds, log, legacyPath);
    Check(!soundMigration.Bind("Warnings", "Enable Missile Voice Warnings", true, "").Value, "independent sound config imports its subset");
    Check(soundMigration.Bind("Warnings", "Missile Voice Repeat Seconds", 2.5f, "").Value == 4.25f, "sound interval imports");
    Check(!sounds.Bind("Audio Test", "Play Audio Test", false).Value, "transient audio request is not imported");
    Check(File.ReadAllBytes(legacyPath).SequenceEqual(original), "multiple focused imports leave legacy intact");
    var noLegacy = new LegacyConfig(new ConfigFile(Path.Combine(directory, "fresh.cfg"), true), log, Path.Combine(directory, "missing.cfg"));
    Check(noLegacy.Bind("Targeting", "Selection Radius", 200f, "").Value == 200f, "missing legacy uses defaults");
    var commentsPath = Path.Combine(directory, "comments.cfg");
    File.WriteAllText(commentsPath, "# comment = value\n; comment = value\n[Targeting]\n");
    Check(!LegacyConfig.HasSavedSettings(commentsPath), "comments and sections alone remain eligible");
    var comments = new LegacyConfig(new ConfigFile(commentsPath, false), log, legacyPath);
    Check(comments.Bind("Targeting", "Selection Radius", 200f, "").Value == 321.5f, "section-only destination imports preferences");
    File.WriteAllText(commentsPath, "[Other]\nUnknown Key =\n");
    var savedEmpty = new LegacyConfig(new ConfigFile(commentsPath, false), log, legacyPath);
    Check(savedEmpty.Bind("Targeting", "Selection Radius", 200f, "").Value == 200f, "saved empty unknown key protects existing config");
    var capCurrent = Path.Combine(directory, "caps.cfg");
    var capLegacy = Path.Combine(directory, "legacy-caps.cfg");
    void CapCase(string name, string? current, string? old, bool expectedFixed, int expectedCap)
    {
        if (current == null) File.Delete(capCurrent); else File.WriteAllText(capCurrent, current);
        if (old == null) File.Delete(capLegacy); else File.WriteAllText(capLegacy, old);
        var beforeCurrent = File.Exists(capCurrent) ? File.ReadAllBytes(capCurrent) : null;
        var beforeLegacy = File.Exists(capLegacy) ? File.ReadAllBytes(capLegacy) : null;
        var captured = TapCapConfigMigration.Read(capCurrent, capLegacy);
        Check((captured.UseFixed ?? false) == expectedFixed && (captured.FixedCap ?? 1) == expectedCap, name);
        Check(beforeCurrent == null ? !File.Exists(capCurrent) : File.ReadAllBytes(capCurrent).SequenceEqual(beforeCurrent), name + " leaves destination untouched");
        Check(beforeLegacy == null ? !File.Exists(capLegacy) : File.ReadAllBytes(capLegacy).SequenceEqual(beforeLegacy), name + " leaves legacy untouched");
        // Exercise the same factory-default Bind/application order as the plugin.
        var destination = new ConfigFile(capCurrent, false) { SaveOnConfigSet = false };
        var enabled = destination.Bind("Targeting", "Use Fixed Tap Target Cap", false);
        var fixedCap = destination.Bind("Targeting", "Fixed Tap Target Cap", 1);
        captured.Apply(enabled, fixedCap);
        Check(enabled.Value == expectedFixed && fixedCap.Value == expectedCap, name + " applies through BepInEx");
        Check((bool)enabled.DefaultValue == false && (int)fixedCap.DefaultValue == 1, name + " keeps reset defaults");
    }
    string OldCap(string mode, int cap) => $"[Targeting]\nTap Target Cap Mode = {mode}\nFixed Tap Target Cap = {cap}\n";
    CapCase("fresh cap defaults", null, null, false, 1);
    CapCase("header-only cap defaults", "# header\n[Targeting]\n", null, false, 1);
    CapCase("old fixed factory preserved", OldCap("fixed", 2), null, true, 2);
    CapCase("old fixed custom preserved", OldCap("fixed", 7), null, true, 7);
    CapCase("old fixed clamps low", OldCap("fixed", 0), null, true, 1);
    CapCase("old fixed clamps high", OldCap("fixed", 999), null, true, 128);
    foreach (var mode in new[] { "disabled", "per_aircraft", "table" })
    {
        CapCase(mode + " inactive factory retunes", OldCap(mode, 2), null, false, 1);
        CapCase(mode + " inactive custom retained", OldCap(mode, 9), null, false, 9);
    }
    CapCase("explicit new false wins old fixed", OldCap("fixed", 2) + "Use Fixed Tap Target Cap = false\n", null, false, 2);
    CapCase("explicit new custom wins old automatic", OldCap("per_aircraft", 8) + "Use Fixed Tap Target Cap = true\n", null, true, 8);
    CapCase("legacy fixed imports", null, OldCap("fixed", 5), true, 5);
    CapCase("header-only legacy import", "# header\n", OldCap("fixed", 5), true, 5);
    CapCase("legacy inactive retunes", null, OldCap("disabled", 2), false, 1);
    CapCase("current previous mode takes precedence", OldCap("disabled", 3), OldCap("fixed", 5), false, 3);
    CapCase("any current saved entry prevents legacy import", "[Other]\nUnknown =\n", OldCap("fixed", 5), false, 1);
    CapCase("wrong section does not claim new flag", OldCap("fixed", 4) + "[Other]\nUse Fixed Tap Target Cap = false\n", null, true, 4);
    Console.WriteLine($"{passed} configuration migration checks passed.");
}
finally { Directory.Delete(directory, true); }
