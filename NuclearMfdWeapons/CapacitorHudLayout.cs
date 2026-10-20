using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMfdWeapons
{
    // Keep the native charge controller and its power/theme subscriptions intact.
    // A separate parent controls visibility even when native callbacks reactivate it.
    internal sealed class CapacitorHudLayout : IDisposable
    {
        private readonly RectTransform? indicator;
        private readonly Transform? parent;
        private readonly RectTransform? canvas;
        private readonly int sibling;
        private readonly Vector2 anchorMin, anchorMax, pivot, size;
        private readonly Vector3 position;
        private readonly Graphic[] graphics = Array.Empty<Graphic>();
        private readonly Vector3 scale;
        private readonly Quaternion rotation;
        private RectTransform? holder;
        private bool disposed;
        private readonly Vector3[] corners = new Vector3[4];

        internal CapacitorHudLayout()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null) return;
            var panel = AccessTools.Field(typeof(CombatHUD), "topRightPanel").GetValue(hud) as GameObject;
            if (panel == null) return;
            var source = panel.GetComponentInChildren<ChargeIndicator>(true);
            var rootCanvas = panel.GetComponentInParent<Canvas>();
            if (source == null || rootCanvas == null || rootCanvas.rootCanvas.renderMode == RenderMode.WorldSpace) return;
            indicator = source.transform as RectTransform;
            canvas = rootCanvas.rootCanvas.transform as RectTransform;
            if (indicator == null || canvas == null || indicator == panel.transform) { indicator = null; return; }
            parent = indicator.parent;
            sibling = indicator.GetSiblingIndex();
            anchorMin = indicator.anchorMin; anchorMax = indicator.anchorMax;
            pivot = indicator.pivot; position = indicator.anchoredPosition3D; size = indicator.sizeDelta;
            scale = indicator.localScale; rotation = indicator.localRotation;
            graphics = indicator.GetComponentsInChildren<Graphic>(true);
        }

        internal void Apply(bool custom, bool show)
        {
            if (disposed || indicator == null || canvas == null) return;
            if (!custom) { Restore(); return; }
            if (holder == null)
            {
                holder = new GameObject("NuclearMfdWeapons.Capacitor", typeof(RectTransform)).GetComponent<RectTransform>();
                holder.SetParent(canvas, false);
                holder.anchorMin = holder.anchorMax = holder.pivot = Vector2.one;
                holder.anchoredPosition = Vector2.zero; holder.sizeDelta = Vector2.zero;
                var dimensions = indicator.rect.size;
                indicator.SetParent(holder, true);
                indicator.anchorMin = indicator.anchorMax = new Vector2(0.5f, 0.5f);
                indicator.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, dimensions.x);
                indicator.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, dimensions.y);
            }
            // The native component retains its activeSelf/enabled state and keeps
            // receiving charge changes. Only our parent is hidden by this option.
            holder.gameObject.SetActive(show);
            if (!show) return;
            bool measured = false;
            float right = float.NegativeInfinity, top = float.NegativeInfinity;
            foreach (var graphic in graphics)
            {
                if (graphic == null) continue;
                graphic.rectTransform.GetWorldCorners(corners);
                foreach (var corner in corners)
                {
                    var point = holder.InverseTransformPoint(corner);
                    right = Mathf.Max(right, point.x); top = Mathf.Max(top, point.y);
                    measured = true;
                }
            }
            if (measured) indicator.localPosition += new Vector3(-8f - right, -8f - top, 0);
        }

        private void Restore()
        {
            if (holder == null) return;
            if (indicator != null && parent != null)
            {
                indicator.SetParent(parent, false);
                indicator.SetSiblingIndex(sibling);
                indicator.anchorMin = anchorMin; indicator.anchorMax = anchorMax;
                indicator.pivot = pivot; indicator.anchoredPosition3D = position; indicator.sizeDelta = size;
                indicator.localScale = scale; indicator.localRotation = rotation;
            }
            // Never destroy a surviving native indicator with its temporary parent.
            else if (indicator != null) indicator.SetParent(canvas, true);
            holder.gameObject.SetActive(false);
            UnityEngine.Object.Destroy(holder.gameObject);
            holder = null;
        }

        public void Dispose() { if (disposed) return; Restore(); disposed = true; }
    }
}
