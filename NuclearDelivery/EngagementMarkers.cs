using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearDelivery
{
    // Per-target engagement marker idea follows NOTT's ammo conservation indicator; see THIRD-PARTY-NOTICES.
    // Markers are children of the native target boxes, so they follow each box without a draw-order dependency.
    internal sealed class EngagementMarkers : IDisposable
    {
        private sealed class Marker
        {
            public GameObject Root = null!;
            public Image Icon = null!;
            public TextMeshProUGUI Count = null!;
        }
        private static readonly FieldInfo? TargetBoxes = AccessTools.Field(typeof(TargetScreenUI), "targetBoxes");
        private static readonly Color Green = new Color(0.2f, 1f, 0.3f, 0.95f);
        // Client feedback: the first build's 1.8 setting is the intended default size.
        private const float BaseScale = 1.8f;
        private static readonly string[] Shape =
        {
            "....#....",
            "...###...",
            "...###...",
            "...###...",
            "...###...",
            "...###...",
            "...###...",
            "...###...",
            "...###...",
            "..#####..",
            ".#######.",
            "##.###.##",
            "#..###..#",
            "...###...",
        };
        private static Texture2D? texture;
        private static Sprite? sprite;
        private readonly TargetScreenUI screen;
        private readonly Dictionary<Image, Marker> markers = new Dictionary<Image, Marker>();
        private readonly List<Image> stale = new List<Image>();
        private readonly TMP_FontAsset font;
        private bool disposed;

        internal static bool Available => TargetBoxes != null;
        internal EngagementMarkers(TargetScreenUI screen)
        {
            this.screen = screen;
            font = DeliveryWidget.ResolveFont((RectTransform)screen.transform);
        }

        internal void Render(Aircraft owner, bool includeAllies, float scale)
        {
            if (disposed || !screen) return;
            if (!(TargetBoxes?.GetValue(screen) is List<Image> boxes)) return;
            var targets = owner.weaponManager != null ? owner.weaponManager.GetTargetList() : null;
            scale = Mathf.Clamp(float.IsNaN(scale) || float.IsInfinity(scale) ? 1 : scale, 0.25f, 3f);
            int shown = targets == null ? 0 : Math.Min(boxes.Count, targets.Count);
            for (int i = 0; i < boxes.Count; i++)
            {
                var box = boxes[i];
                if (!box) continue;
                int count = i < shown ? DeliveryTracking.Engaging(targets![i], includeAllies) : 0;
                markers.TryGetValue(box, out var marker);
                if (count <= 0) { if (marker != null) marker.Root.SetActive(false); continue; }
                marker ??= markers[box] = Create(box);
                Layout(marker, scale);
                string content = count > 1 ? count.ToString(CultureInfo.InvariantCulture) : "";
                if (marker.Count.text != content) marker.Count.text = content;
                marker.Root.SetActive(true);
            }
            // Native code destroys surplus boxes together with their child markers.
            stale.Clear();
            foreach (var pair in markers) if (!pair.Key || !pair.Value.Root) stale.Add(pair.Key);
            foreach (var box in stale) markers.Remove(box);
        }

        private Marker Create(Image box)
        {
            var go = new GameObject("NuclearDelivery.EngagementMarker", typeof(RectTransform));
            go.layer = box.gameObject.layer;
            var rect = (RectTransform)go.transform;
            rect.SetParent(box.rectTransform, false);
            // Bottom-centre of the native box, independent of its pivot.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            var marker = new Marker { Root = go };
            try
            {
                var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconObject.layer = go.layer;
                iconObject.transform.SetParent(rect, false);
                marker.Icon = iconObject.GetComponent<Image>();
                marker.Icon.sprite = Sprite(); marker.Icon.color = Green; marker.Icon.raycastTarget = false;
                var iconRect = marker.Icon.rectTransform;
                iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 1f);

                var textObject = new GameObject("Count", typeof(RectTransform), typeof(TextMeshProUGUI));
                textObject.layer = go.layer;
                textObject.transform.SetParent(rect, false);
                marker.Count = textObject.GetComponent<TextMeshProUGUI>();
                marker.Count.font = font; marker.Count.fontStyle = FontStyles.Bold; marker.Count.color = Green;
                marker.Count.enableAutoSizing = false; marker.Count.enableWordWrapping = false;
                marker.Count.overflowMode = TextOverflowModes.Overflow; marker.Count.alignment = TextAlignmentOptions.Left;
                marker.Count.raycastTarget = false; marker.Count.text = "";
                var textRect = marker.Count.rectTransform;
                textRect.anchorMin = textRect.anchorMax = new Vector2(0.5f, 1f);
                textRect.pivot = new Vector2(0f, 1f);
            }
            catch { UnityEngine.Object.Destroy(go); throw; }
            return marker;
        }
        private static void Layout(Marker marker, float scale)
        {
            scale *= BaseScale;
            var rect = (RectTransform)marker.Root.transform;
            float pixel = 1.25f * scale;
            float width = (Shape[0].Length + 2) * pixel, height = (Shape.Length + 2) * pixel;
            rect.anchoredPosition = new Vector2(0, -3 * scale);
            rect.sizeDelta = new Vector2(width, height);
            marker.Icon.rectTransform.anchoredPosition = Vector2.zero;
            marker.Icon.rectTransform.sizeDelta = new Vector2(width, height);
            marker.Count.rectTransform.anchoredPosition = new Vector2(width * 0.5f + scale, 0);
            marker.Count.rectTransform.sizeDelta = new Vector2(16 * scale, height);
            marker.Count.fontSize = 12 * scale;
        }

        // White fill with a dark outline; the Image colour tints only the fill.
        private static Sprite Sprite()
        {
            if (sprite != null) return sprite;
            int w = Shape[0].Length + 2, h = Shape.Length + 2;
            texture = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var value = new Color32(0, 0, 0, 0);
                    if (Filled(x - 1, y - 1)) value = new Color32(255, 255, 255, 255);
                    else
                        for (int dy = -1; dy <= 1 && value.a == 0; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                if (Filled(x - 1 + dx, y - 1 + dy)) { value = new Color32(0, 0, 0, 80); break; }
                    // Texture rows start at the bottom; Shape rows start at the nose.
                    pixels[(h - 1 - y) * w + x] = value;
                }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            sprite = UnityEngine.Sprite.Create(texture, new Rect(0, 0, w, h), new Vector2(0.5f, 1f), 1f);
            return sprite;
        }
        private static bool Filled(int x, int y) =>
            y >= 0 && y < Shape.Length && x >= 0 && x < Shape[y].Length && Shape[y][x] == '#';

        public void Dispose()
        {
            if (disposed) return; disposed = true;
            foreach (var marker in markers.Values) if (marker.Root) UnityEngine.Object.Destroy(marker.Root);
            markers.Clear();
        }
        internal static void ReleaseShared()
        {
            if (sprite != null) UnityEngine.Object.Destroy(sprite);
            if (texture != null) UnityEngine.Object.Destroy(texture);
            sprite = null; texture = null;
        }
    }
}
