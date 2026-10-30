using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace NuclearBindings
{
    // ConfigurationManager requires this type name and reads public tag fields by name.
    internal sealed class ConfigurationManagerAttributes
    {
        public Action<ConfigEntryBase>? CustomDrawer;
    }

    // One action's bindings: a string setting holding rows (see BindingRow), drawn in F1 as a
    // list of controls with Add binding and Remove. Additive: never consumes the game's bindings.
    internal sealed class InputBinding
    {
        internal const string Help = " In F1, select Add binding, then press a key (hold Ctrl, Shift or Alt for a combination), a mouse button, a gamepad button, trigger or D-pad direction, or a joystick button, hat direction or axis direction. Remove deletes one control; Reset restores the default.";

        // Per plugin assembly: this source is compiled into each mod separately.
        private static readonly List<InputBinding> all = new List<InputBinding>();

        internal readonly ConfigEntry<string> Entry;
        internal readonly string Name;
        private readonly ManualLogSource log;
        private readonly bool allowFullMap;
        private readonly BindingDrawer drawer;
        private readonly HashSet<string> logged = new HashSet<string>();
        private List<BindingRow> rows = new List<BindingRow>();
        private string parsed = "\0", topology = "";
        private int frame = -2;
        private bool held, pressed, needRelease = true;

        private InputBinding(ConfigEntry<string> entry, string name, ManualLogSource log, bool allowFullMap)
        {
            Entry = entry; Name = name; this.log = log; this.allowFullMap = allowFullMap;
            drawer = new BindingDrawer(this);
        }

        internal static IReadOnlyList<InputBinding> All => all;

        // defaultText uses the row format, for example "F10", "Ctrl+F9" or "Gamepad:DPadUp".
        // Saved BepInEx KeyboardShortcut text under the same key converts in place once.
        internal static InputBinding Bind(ConfigFile config, ManualLogSource log, string section, string key, string defaultText, string description, bool allowFullMap = false)
        {
            var attributes = new ConfigurationManagerAttributes();
            var entry = config.Bind(section, key, BindingText.Canonical(defaultText), new ConfigDescription(description + Help, null, attributes));
            BindingMigration.Normalize(entry, log);
            var binding = new InputBinding(entry, key, log, allowFullMap);
            attributes.CustomDrawer = binding.drawer.Draw;
            all.Add(binding);
            return binding;
        }

        internal IReadOnlyList<BindingRow> Rows { get { Refresh(); return rows; } }

        // Optional mod-specific explanation shown under a row in F1 (empty for none).
        internal Func<BindingRow, string>? RowNote { get; set; }

        internal bool Capturing => drawer.Capturing;

        // True on the frame the binding goes from released to held. After a gap in calls, a
        // device change, a settings change or a blocked context, the control must be released first.
        internal bool IsDown() { Sample(); return pressed; }

        internal bool IsHeld() { Sample(); return held; }

        private void Refresh()
        {
            var text = Entry.Value ?? "";
            if (text == parsed) return;
            parsed = text; rows = BindingText.Parse(text); needRelease = true;
        }

        private void Sample()
        {
            int now = Time.frameCount;
            if (now == frame) return;
            bool gap = now != frame + 1;
            frame = now;
            Refresh();
            pressed = false;
            var currentTopology = BindingInput.Topology;
            if (gap || currentTopology != topology) { needRelease = true; topology = currentTopology; }
            if (rows.Count > 0) LogDevices(currentTopology);
            if (rows.Count == 0 || drawer.Capturing || InputContext.Blocked(allowFullMap)) { held = false; needRelease = true; return; }
            bool down = BindingInput.AnyHeld(rows, out var source);
            if (needRelease)
            {
                held = false;
                if (!down) needRelease = false;
                return;
            }
            pressed = down && !held;
            held = down;
            if (pressed) LogFired(source);
        }

        // One info line per control, so a log shows that a press reached the mod.
        internal void LogFired(string source)
        {
            if (logged.Add(source)) log.LogInfo("Binding '" + Name + "' fired from " + source + ".");
        }

        // Which controllers this mod can see, once per device change (bounded), for support logs.
        private static string loggedTopology = "";
        private static int deviceLogs;
        private void LogDevices(string currentTopology)
        {
            if (currentTopology.Length == 0 || currentTopology == loggedTopology || deviceLogs >= 8) return;
            loggedTopology = currentTopology; deviceLogs++;
            var names = new List<string>();
            var player = BindingInput.PlayerJoysticks();
            foreach (var device in BindingInput.Devices())
            {
                if (!(device is Rewired.Joystick joystick)) continue;
                names.Add("'" + joystick.name.Trim() + "' (" + (joystick.GetTemplate<Rewired.IGamepadTemplate>() != null ? "gamepad" : "joystick") +
                    (player.Contains(joystick) ? "" : ", not assigned to player 1") + ")");
            }
            log.LogInfo("Binding input devices: keyboard, mouse" + (names.Count > 0 ? ", " + string.Join(", ", names) : "; no controllers connected") + ".");
        }

        internal InputBinding? OtherOwner(BindingRow row)
        {
            foreach (var other in all)
            {
                if (ReferenceEquals(other, this)) continue;
                foreach (var existing in other.Rows)
                    if (existing.Identity == row.Identity) return other;
            }
            return null;
        }

        internal void CancelCapture() => drawer.Cancel();

        // The bound controls by name, for messages: "F10", "Gamepad A / Cross, Ctrl + F7" or "Not bound".
        public override string ToString()
        {
            var names = new List<string>();
            foreach (var row in Rows) if (row.Kind != RowKind.Invalid) names.Add(row.Describe());
            return names.Count == 0 ? "Not bound" : string.Join(", ", names);
        }
    }
}
