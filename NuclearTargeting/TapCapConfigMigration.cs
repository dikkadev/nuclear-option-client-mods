using System;
using System.IO;
using BepInEx.Configuration;
using NuclearModMigration;

namespace NuclearTargeting
{
    // Capture before any destination Bind: Bind can save and erase evidence of
    // which keys the user actually stored. Read-only snapshots never save files.
    internal sealed class TapCapConfigMigration
    {
        internal bool? UseFixed { get; private set; }
        internal int? FixedCap { get; private set; }

        internal void Apply(ConfigEntry<bool> useFixed, ConfigEntry<int> fixedCap)
        {
            if (UseFixed.HasValue) useFixed.Value = UseFixed.Value;
            if (FixedCap.HasValue) fixedCap.Value = FixedCap.Value;
        }

        internal static TapCapConfigMigration Read(string currentPath, string legacyPath)
        {
            var result = new TapCapConfigMigration();
            var path = LegacyConfig.HasSavedSettings(currentPath) ? currentPath : legacyPath;
            if (!File.Exists(path)) return result;
            var snapshot = new ConfigFile(path, false) { SaveOnConfigSet = false };
            bool hasUseFixed = HasKey(path, "Use Fixed Tap Target Cap");
            bool hasFixed = HasKey(path, "Fixed Tap Target Cap");
            bool hasMode = HasKey(path, "Tap Target Cap Mode");
            if (hasUseFixed)
            {
                result.UseFixed = snapshot.Bind("Targeting", "Use Fixed Tap Target Cap", false).Value;
                if (hasFixed) result.FixedCap = snapshot.Bind("Targeting", "Fixed Tap Target Cap", 1).Value;
                return result;
            }
            bool oldFixed = hasMode && string.Equals(snapshot.Bind("Targeting", "Tap Target Cap Mode", "disabled").Value.Trim(), "fixed", StringComparison.OrdinalIgnoreCase);
            if (hasMode) result.UseFixed = oldFixed;
            if (hasFixed)
            {
                int value = snapshot.Bind("Targeting", "Fixed Tap Target Cap", 1).Value;
                // Two was the inactive factory placeholder, not a chosen limit.
                result.FixedCap = hasMode && !oldFixed && value == 2 ? 1 : Math.Max(1, Math.Min(128, value));
            }
            return result;
        }

        private static bool HasKey(string path, string key)
        {
            string section = "";
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal)) continue;
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    section = line.Substring(1, line.Length - 2);
                else
                {
                    int equals = line.IndexOf('=');
                    if (section == "Targeting" && equals > 0 && line.Substring(0, equals).Trim() == key) return true;
                }
            }
            return false;
        }
    }
}
