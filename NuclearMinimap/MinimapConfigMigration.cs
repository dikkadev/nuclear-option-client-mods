using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace NuclearMinimap
{
    internal static class MinimapConfigMigration
    {
        // Called before any Bind: preserve every saved key, including orphaned keys.
        internal static void Import(ConfigFile destination, ManualLogSource log) =>
            Import(destination, log, Path.Combine(Paths.ConfigPath, "dev.dikka.nuclearmfdmap.cfg"));

        internal static bool Import(ConfigFile destination, ManualLogSource log, string legacyPath)
        {
            try
            {
                if (HasSavedSettings(destination.ConfigFilePath) || !File.Exists(legacyPath)) return false;
                var contents = File.ReadAllText(legacyPath);
                File.WriteAllText(destination.ConfigFilePath, contents);
                destination.Reload();
                log.LogInfo("Imported NuclearMfdMap configuration; original configuration retained.");
                return true;
            }
            catch (Exception error)
            {
                log.LogWarning("NuclearMfdMap configuration import unavailable: " + error.Message);
                return false;
            }
        }

        private static bool HasSavedSettings(string path)
        {
            if (!File.Exists(path)) return false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) ||
                    line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal)) continue;
                if (line.IndexOf('=') > 0) return true;
            }
            return false;
        }
    }
}
