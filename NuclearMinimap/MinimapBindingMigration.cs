using System.Collections.Generic;
using NuclearBindings;

namespace NuclearMinimap
{
    // 0.1.1-0.4.x zoom controls (keyboard shortcut, generic controller control and device rows)
    // become one Zoom In and one Zoom Out binding. Null means the old key was never saved.
    internal static class MinimapBindingMigration
    {
        internal static bool Convert(string? keyboardIn, string? keyboardOut, string? controllerIn, string? controllerOut, string? deviceRows,
            out string zoomIn, out string zoomOut, out string problem)
        {
            zoomIn = zoomOut = problem = "";
            if (keyboardIn == null && keyboardOut == null && controllerIn == null && controllerOut == null && deviceRows == null) return false;
            var inward = new List<BindingRow>();
            var outward = new List<BindingRow>();
            AddKey(inward, keyboardIn);
            AddKey(outward, keyboardOut);
            // Missing old values had these defaults.
            AddPad(inward, controllerIn ?? "DPadUp");
            AddPad(outward, controllerOut ?? "DPadDown");
            if (!string.IsNullOrWhiteSpace(deviceRows))
            {
                var lines = deviceRows!.Split(';');
                var converted = new List<(bool inward, BindingRow row)>();
                for (int i = 1; i < lines.Length && lines[0] == "v1"; i++)
                {
                    int split = lines[i].IndexOf('|');
                    var direction = split > 0 ? lines[i].Substring(0, split) : "";
                    var row = split > 0 && (direction == "in" || direction == "out") ? BindingText.ParseRow("Device:" + lines[i].Substring(split + 1)) : null;
                    // The old reader ignored every row when any row was unreadable; keep that outcome.
                    if (row == null) { converted.Clear(); problem = "unreadable device zoom bindings were not imported: " + deviceRows; break; }
                    converted.Add((direction == "in", row));
                }
                if (lines[0] != "v1") problem = "unsupported device zoom bindings were not imported: " + deviceRows;
                foreach (var item in converted) (item.inward ? inward : outward).Add(item.row);
            }
            zoomIn = BindingText.Serialize(inward);
            zoomOut = BindingText.Serialize(outward);
            return true;
        }

        private static void AddKey(List<BindingRow> rows, string? shortcut)
        {
            foreach (var row in BindingText.Parse(shortcut)) if (row.Kind == RowKind.Key) rows.Add(row);
        }

        private static void AddPad(List<BindingRow> rows, string control)
        {
            var row = control == "Unbound" ? null : BindingText.ParseRow("Gamepad:" + control);
            if (row != null) rows.Add(row);
        }
    }
}
