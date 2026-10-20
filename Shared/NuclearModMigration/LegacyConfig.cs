using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace NuclearModMigration
{
    // Source-only helper: each focused plugin owns its own config and migration.
    internal sealed class LegacyConfig
    {
        private readonly ConfigFile destination;
        private readonly ManualLogSource log;
        private readonly ConfigFile? legacy;
        private bool reported;

        internal LegacyConfig(ConfigFile destination, ManualLogSource log)
            : this(destination, log, Path.Combine(Paths.ConfigPath, "dev.dikka.nucleartacticaltools.cfg")) { }

        internal LegacyConfig(ConfigFile destination, ManualLogSource log, string legacyPath)
        {
            this.destination = destination;
            this.log = log;
            // BaseUnityPlugin may create a header-only file before Awake. Existence
            // alone therefore cannot distinguish new settings from saved preferences.
            try
            {
                if (!HasSavedSettings(destination.ConfigFilePath) && File.Exists(legacyPath))
                    legacy = new ConfigFile(legacyPath, false) { SaveOnConfigSet = false };
            }
            catch (Exception error)
            {
                log.LogWarning("Legacy settings import unavailable: " + error.Message);
            }
        }

        internal ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            var entry = destination.Bind(section, key, defaultValue, description);
            if (legacy != null)
            {
                entry.Value = legacy.Bind(section, key, defaultValue, description).Value;
                if (!reported)
                {
                    log.LogInfo("Imported matching Tactical Tools settings into new focused configuration; original file retained.");
                    reported = true;
                }
            }
            return entry;
        }

        internal static bool HasSavedSettings(string path)
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
