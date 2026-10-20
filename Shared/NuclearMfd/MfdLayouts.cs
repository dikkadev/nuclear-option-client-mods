using System.Collections.Generic;

namespace NuclearMfd
{
    /// <summary>Source-derived paths, checked against each actual canvas before use.</summary>
    public static class MfdLayouts
    {
        public const string Provenance = "NO_Tactitools b4db2288c668dd55ae596fa46b15fce029170efd, WeaponDisplay.cs; NuclearMfd current-stock adapters (2026-10-08)";
        private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["EW-1 Medusa"] = "engPanel1", ["CI-22 Cricket"] = "EngPanel",
            ["SAH-46 Chicane"] = "BasicFlightInstrument", ["VL-49 Tarantula"] = "RightScreenBorder/WeaponPanel",
            ["SFB-81"] = "weaponPanel", ["FastBomber1"] = "weaponPanel/frontProfile",
            ["AB-4 Alkyon"] = "weaponPanel/frontProfile", ["Alkyon AB-4"] = "weaponPanel/frontProfile",
            ["MiG-15"] = "StatusGauges/FrontView", ["F-16M King Viper"] = "SystemsPanel",
            ["T/A-30 Compass"] = "SystemStatus", ["FS-3 Ternion"] = "SystemStatus",
            ["FS-12 Revoker"] = "SystemStatus", ["FS-20 Vortex"] = "SystemStatus",
            ["KR-67 Ifrit"] = "SystemStatus", ["UH-80 Ibis"] = "SystemStatus",
            ["A-19 Brawler"] = "SystemStatus", ["FQ-106 Kestrel"] = "SystemStatus",
            ["VT-7 Vagrant"] = "SystemStatus"
        };

        public static string GetWeaponPath(string aircraftName, out bool recognized)
        {
            recognized = Paths.TryGetValue(GetCanonicalName(aircraftName), out var path);
            return path ?? "SystemStatus";
        }

        /// <summary>Maps current exact stock names onto existing source-adapter names.</summary>
        public static string GetCanonicalName(string? aircraftName)
        {
            // Current names: official https://store.steampowered.com/app/2168680/Nuclear_Option/?l=english
            // Existing adapter names are retained to preserve consumer layout selection.
            switch (aircraftName)
            {
                case "UH-90 Ibis": return "UH-80 Ibis";
                case "EW-25 Medusa": return "EW-1 Medusa";
                case "SFB-81 Darkreach": return "SFB-81";
                default: return aircraftName ?? "";
            }
        }
    }
}
