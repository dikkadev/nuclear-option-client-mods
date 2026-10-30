using System;
using System.IO;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace NuclearBindings
{
    internal static class BindingMigration
    {
        private const string Missing = "\u0001missing";

        // Rewrites a saved value in canonical row text: BepInEx KeyboardShortcut text such as
        // "F7 + LeftControl" becomes "Ctrl+F7", and "JoystickButton0" becomes "Gamepad:ButtonSouth".
        internal static void Normalize(ConfigEntry<string> entry, ManualLogSource log)
        {
            var old = entry.Value ?? "";
            var canonical = BindingText.Canonical(old);
            if (canonical != old)
            {
                entry.Value = canonical;
                log.LogInfo("Converted saved binding [" + entry.Definition.Section + "] " + entry.Definition.Key + " from '" + old + "' to '" + canonical + "'.");
            }
            foreach (var row in BindingText.Parse(canonical))
                if (row.Kind == RowKind.Invalid)
                    log.LogWarning("Binding [" + entry.Definition.Section + "] " + entry.Definition.Key + " has an unreadable control '" + row.Text + "'; it is ignored. Remove it in F1.");
        }

        // Whether the configuration file holds a value for this key (BepInEx cannot tell a
        // saved default from an absent key once the setting is bound).
        internal static bool IsSaved(ConfigFile config, string section, string key)
        {
            if (!File.Exists(config.ConfigFilePath)) return false;
            string current = "";
            foreach (var raw in File.ReadAllLines(config.ConfigFilePath))
            {
                var line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)) { current = line.Substring(1, line.Length - 2).Trim(); continue; }
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int split = line.IndexOf('=');
                if (split > 0 && current == section && line.Substring(0, split).Trim() == key) return true;
            }
            return false;
        }

        // Reads a saved value of a setting this version no longer has, then drops it from the
        // configuration. Returns null when it was never saved. The caller saves the file.
        internal static string? TakeLegacy(ConfigFile config, string section, string key)
        {
            var definition = new ConfigDefinition(section, key);
            bool save = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            try
            {
                var value = config.Bind(definition, Missing).Value;
                config.Remove(definition);
                return value == Missing ? null : value;
            }
            finally { config.SaveOnConfigSet = save; }
        }
    }
}
