using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearMfd;
using TMPro;
using UnityEngine;

namespace NuclearMfdWeapons
{
    // A local correction for the Vagrant's native cockpit gauge, never the separate FlightHud gauge.
    internal sealed class CockpitNozzleLayout : IDisposable
    {
        private static readonly FieldInfo? LabelField = AccessTools.Field(typeof(NozzleGauge), "label");
        private static readonly FieldInfo? IconField = AccessTools.Field(typeof(NozzleGauge), "nozzleIcon");
        private static readonly FieldInfo? PivotField = AccessTools.Field(typeof(NozzleGauge), "inputPivot");
        private readonly List<LabelState> labels = new List<LabelState>();
        private readonly RectTransform canvas;
        private readonly Vector3[] corners = new Vector3[4];
        private bool disposed;
        public string Diagnostic { get; private set; }

        private sealed class LabelState
        {
            internal TextMeshProUGUI Label = null!;
            internal Transform Icon = null!, Input = null!;
            internal Vector3 AnchoredPosition;
            internal Vector2 SizeDelta;
            internal readonly List<RectTransform> Glyphs = new List<RectTransform>();
        }

        public CockpitNozzleLayout(MfdSession session)
        {
            canvas = session.CanvasRoot;
            Diagnostic = "No cockpit nozzle correction required for this aircraft.";
            if (session.AircraftName != "VT-7 Vagrant") return;
            if (LabelField == null || IconField == null || PivotField == null)
            {
                Diagnostic = "Vagrant cockpit nozzle label fields unavailable; native placement retained.";
                return;
            }
            foreach (var gauge in canvas.GetComponentsInChildren<NozzleGauge>(true))
            {
                var label = LabelField.GetValue(gauge) as TextMeshProUGUI;
                var icon = IconField.GetValue(gauge) as Transform;
                var input = PivotField.GetValue(gauge) as Transform;
                if (label == null || icon == null || input == null || !label.transform.IsChildOf(canvas) ||
                    !icon.IsChildOf(canvas) || !input.IsChildOf(canvas)) continue;
                var state = new LabelState
                {
                    Label = label, Icon = icon, Input = input,
                    AnchoredPosition = label.rectTransform.anchoredPosition3D,
                    SizeDelta = label.rectTransform.sizeDelta
                };
                foreach (var source in new[] { icon, input })
                    foreach (var graphic in source.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
                        if (!(graphic is TMP_Text) && !state.Glyphs.Contains(graphic.rectTransform)) state.Glyphs.Add(graphic.rectTransform);
                labels.Add(state);
            }
            Diagnostic = labels.Count == 0 ? "Vagrant owned cockpit nozzle gauge unavailable; native placement retained." :
                "Vagrant cockpit nozzle label discovered; measured beside-glyph placement pending.";
        }

        public void Apply(bool custom)
        {
            if (disposed) return;
            int moved = 0;
            foreach (var state in labels)
            {
                if (state.Label == null) continue;
                var rect = state.Label.rectTransform;
                // Always derive from the original anchors/offsets, never accumulate frame offsets.
                rect.anchoredPosition3D = state.AnchoredPosition;
                rect.sizeDelta = state.SizeDelta;
                if (!custom || rect.parent == null) continue;
                float width = state.Label.preferredWidth;
                float height = Mathf.Max(state.Label.fontSize, state.Label.preferredHeight);
                if (width <= 0 || height <= 0) continue;
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
                bool placed = false;
                // The gauge root may be a tiny positioning rect. Inspect larger native panels,
                // but never use the complete canvas to justify crossing unrelated MFD widgets.
                for (var candidate = rect.parent as RectTransform; candidate != null && candidate != canvas;
                    candidate = candidate.parent as RectTransform)
                {
                    if (!TryGlyphBounds(state, candidate, out var glyph)) continue;
                    var labelBounds = BoundsIn(rect, candidate);
                    float gap = Mathf.Max(2f, height * 0.2f);
                    float desiredLeft = glyph.xMax + gap;
                    float desiredRight = desiredLeft + labelBounds.width;
                    var panel = candidate.rect;
                    // A panel must contain both the native symbol and the proposed text row.
                    if (glyph.xMin < panel.xMin - 1 || glyph.xMax > panel.xMax + 1 ||
                        labelBounds.yMin < panel.yMin || labelBounds.yMax > panel.yMax ||
                        desiredRight > panel.xMax - gap) continue;
                    var shift = candidate.TransformVector(new Vector3(desiredLeft - labelBounds.xMin, 0, 0));
                    rect.position += shift;
                    placed = true;
                    moved++;
                    break;
                }
                if (!placed)
                {
                    rect.anchoredPosition3D = state.AnchoredPosition;
                    rect.sizeDelta = state.SizeDelta;
                }
            }
            if (labels.Count > 0 && custom)
                Diagnostic = moved > 0 ? "Vagrant cockpit nozzle label placed beside native glyph at existing row; original font/style retained." :
                    "Vagrant cockpit nozzle label has no verified free native panel space; original placement retained.";
        }

        private bool TryGlyphBounds(LabelState state, RectTransform relativeTo, out Rect bounds)
        {
            bounds = default;
            bool found = false;
            foreach (var glyph in state.Glyphs)
            {
                if (glyph == null) continue;
                var part = BoundsIn(glyph, relativeTo);
                if (!found) { bounds = part; found = true; }
                else bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, part.xMin), Mathf.Min(bounds.yMin, part.yMin),
                    Mathf.Max(bounds.xMax, part.xMax), Mathf.Max(bounds.yMax, part.yMax));
            }
            return found;
        }

        private Rect BoundsIn(RectTransform rect, RectTransform relativeTo)
        {
            rect.GetWorldCorners(corners);
            var first = relativeTo.InverseTransformPoint(corners[0]);
            float minX = first.x, maxX = first.x, minY = first.y, maxY = first.y;
            for (int i = 1; i < corners.Length; i++)
            {
                var point = relativeTo.InverseTransformPoint(corners[i]);
                minX = Mathf.Min(minX, point.x); maxX = Mathf.Max(maxX, point.x);
                minY = Mathf.Min(minY, point.y); maxY = Mathf.Max(maxY, point.y);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        public void Dispose()
        {
            if (disposed) return;
            Apply(false);
            disposed = true;
            labels.Clear();
        }
    }
}
