using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    internal sealed class NativeHudVisibility
    {
        private readonly DynamicMap source;
        private readonly List<Graphic> graphics = new List<Graphic>();
        private readonly Dictionary<CanvasRenderer, RendererState> hidden = new Dictionary<CanvasRenderer, RendererState>();
        private Image? panelBacking;
        private bool backingEnabled, backingHidden;
        private Image? forwardMarker;
        private bool markerEnabled, markerHidden;
        private readonly struct RendererState
        {
            internal readonly Color Color;
            internal readonly bool CullTransparent;
            internal RendererState(CanvasRenderer renderer) { Color = renderer.GetColor(); CullTransparent = renderer.cullTransparentMesh; }
        }
        internal NativeHudVisibility(DynamicMap source) { this.source = source; }
        internal void Restore()
        {
            if (markerHidden && forwardMarker != null) forwardMarker.enabled = markerEnabled;
            forwardMarker = null; markerHidden = false;
            if (backingHidden && panelBacking != null) panelBacking.enabled = backingEnabled;
            panelBacking = null; backingHidden = false;
            foreach (var item in hidden)
                if (item.Key != null) { item.Key.SetColor(item.Value.Color); item.Key.cullTransparentMesh = item.Value.CullTransparent; }
            hidden.Clear();
        }
        internal void Hide()
        {
            if (source == null || !source.isActiveAndEnabled || DynamicMap.mapMaximized) return;
            // The HUD frame is on the anchor's parent, outside DynamicMap's subtree.
            // Disable only this verified decoration; its children/controller must stay active.
            var panel = source.hudMapAnchor != null ? source.hudMapAnchor.parent : null;
            var backing = panel != null && panel.name == "LowerLeftPanel" ? panel.GetComponent<Image>() : null;
            if (!backingHidden && backing != null)
            {
                panelBacking = backing; backingEnabled = backing.enabled; backingHidden = true;
                backing.enabled = false;
            }
            // The small heading reference lives under HUDMapAnchor, outside the map too.
            // Preserve its native object/style updates and restore its exact enabled state.
            var markerTransform = source.hudMapAnchor != null ? source.hudMapAnchor.Find("forwardRef") : null;
            var marker = markerTransform != null ? markerTransform.GetComponent<Image>() : null;
            if (!markerHidden && marker != null)
            {
                forwardMarker = marker; markerEnabled = marker.enabled; markerHidden = true;
                marker.enabled = false;
            }
            graphics.Clear(); source.GetComponentsInChildren(false, graphics);
            foreach (var graphic in graphics)
            {
                if (graphic == null || !graphic.isActiveAndEnabled) continue;
                var renderer = graphic.canvasRenderer;
                if (!hidden.ContainsKey(renderer)) hidden.Add(renderer, new RendererState(renderer));
                // Preserve native map updates and their meshes. Alpha suppression must not cull them.
                renderer.cullTransparentMesh = false;
                var tint = renderer.GetColor(); tint.a = 0; renderer.SetColor(tint);
            }
        }
    }
}
