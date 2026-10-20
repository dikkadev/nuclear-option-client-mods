using System;
using NuclearMfd;

// Coverage contract: upstream recognition and special destinations are separate.
string[] standard = { "T/A-30 Compass", "FS-3 Ternion", "FS-12 Revoker", "FS-20 Vortex", "KR-67 Ifrit", "UH-80 Ibis", "A-19 Brawler", "FQ-106 Kestrel" };
foreach (var name in standard) Check(name, "SystemStatus", true);
Check("EW-1 Medusa", "engPanel1", true);
Check("CI-22 Cricket", "EngPanel", true);
Check("SAH-46 Chicane", "BasicFlightInstrument", true);
Check("VL-49 Tarantula", "RightScreenBorder/WeaponPanel", true);
Check("SFB-81", "weaponPanel", true);
foreach (var name in new[] { "FastBomber1", "AB-4 Alkyon", "Alkyon AB-4" }) Check(name, "weaponPanel/frontProfile", true);
Check("MiG-15", "StatusGauges/FrontView", true);
Check("F-16M King Viper", "SystemsPanel", true);
Check("VT-7 Vagrant", "SystemStatus", true);
// Current official stock roster, kept separate from upstream/third-party coverage.
var currentStock = new (string Name, string Canonical, string Path)[]
{
    ("CI-22 Cricket", "CI-22 Cricket", "EngPanel"),
    ("T/A-30 Compass", "T/A-30 Compass", "SystemStatus"),
    ("VT-7 Vagrant", "VT-7 Vagrant", "SystemStatus"),
    ("UH-90 Ibis", "UH-80 Ibis", "SystemStatus"),
    ("SAH-46 Chicane", "SAH-46 Chicane", "BasicFlightInstrument"),
    ("A-19 Brawler", "A-19 Brawler", "SystemStatus"),
    ("FS-12 Revoker", "FS-12 Revoker", "SystemStatus"),
    ("FS-20 Vortex", "FS-20 Vortex", "SystemStatus"),
    ("VL-49 Tarantula", "VL-49 Tarantula", "RightScreenBorder/WeaponPanel"),
    ("KR-67 Ifrit", "KR-67 Ifrit", "SystemStatus"),
    ("EW-25 Medusa", "EW-1 Medusa", "engPanel1"),
    ("SFB-81 Darkreach", "SFB-81", "weaponPanel"),
    ("Alkyon AB-4", "Alkyon AB-4", "weaponPanel/frontProfile")
};
foreach (var aircraft in currentStock)
{
    Check(aircraft.Name, aircraft.Path, true);
    if (MfdLayouts.GetCanonicalName(aircraft.Name) != aircraft.Canonical)
        throw new Exception("Wrong canonical layout identity for " + aircraft.Name);
}
if (MfdLayouts.GetCanonicalName("VTOLTrainer1") != "VTOLTrainer1" ||
    MfdLayouts.GetCanonicalName("Unknown Modded Ibis") != "Unknown Modded Ibis")
    throw new Exception("Canonical names must not infer prefab or fuzzy aliases.");
Check("New Modded Airframe", "SystemStatus", false);
Check("ci-22 cricket", "SystemStatus", false); // Case-sensitive game names; no fuzzy matches.
Check("", "SystemStatus", false);
Check(null!, "SystemStatus", false);
// Prefab/network identity, clone and livery labels cannot establish a known layout.
Check("VTOLTrainer1", "SystemStatus", false);
Check("T/A-30 Compass(Clone)", "SystemStatus", false);
Check("T/A-30 Compass - custom livery", "SystemStatus", false);
Check("An actual Bear [T/A-30 Compass]", "SystemStatus", false);
foreach (var path in new[] { "SystemsPanel", "RightScreenBorder/WeaponPanel", "Panels/Custom Region", "Panel.with.dots/Child" })
    CheckPath("my-plugin.engine", path, true);
foreach (var path in new[] { "", " ", "/Panel", "Panel/", "Panel//Child", "../Panel", "Panel/../Child", "Panel/./Child", "C:/Panel", "\\Panel", "Panel\\Child", " Panel", "Panel /Child", "Panel/\nChild" })
    CheckPath("my-plugin.engine", path, false);
foreach (var role in new[] { "", " ", "main", "weapons", "radar", "../role", "role/name", "role name", ".", "..", "role:child" })
    CheckPath(role, "Panel", false);
CheckPath(null!, "Panel", false);
CheckPath("engine", null!, false);
Console.WriteLine("27 MFD registry, 13 current stock roster and 31 custom surface path checks passed.");

static void CheckPath(string role, string path, bool valid)
{
    if (MfdPaths.IsValidCustomSurface(role, path) != valid)
        throw new Exception($"Unexpected custom surface validation for role '{role}', path '{path}'");
}

static void Check(string name, string path, bool recognized)
{
    var actual = MfdLayouts.GetWeaponPath(name, out var actualRecognition);
    if (actual != path || actualRecognition != recognized)
        throw new Exception($"Unexpected mapping for {name}: {actual}, recognized={actualRecognition}");
}
