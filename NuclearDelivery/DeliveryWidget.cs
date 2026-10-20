using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearDelivery
{
    // Presentation geometry follows NOTT DeliveryChecker; see THIRD-PARTY-NOTICES.
    internal sealed class DeliveryWidget : IDisposable
    {
        private readonly RectTransform parent, root;
        private readonly bool targetCamera;
        private readonly TextMeshProUGUI[] labels = new TextMeshProUGUI[8];
        private readonly int[] values = new int[6];
        private bool disposed;
        internal DeliveryWidget(RectTransform parent, bool targetCamera)
        {
            this.parent = parent; this.targetCamera = targetCamera;
            var font = ResolveFont(parent);
            var go = new GameObject("NuclearDelivery." + (targetCamera ? "TargetCamera" : "Cockpit"), typeof(RectTransform), typeof(LayoutElement));
            go.layer = parent.gameObject.layer;
            root = (RectTransform)go.transform;
            root.SetParent(parent, false);
            root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
            root.offsetMin = root.offsetMax = Vector2.zero;
            root.pivot = Vector2.zero;
            go.GetComponent<LayoutElement>().ignoreLayout = true;
            try
            {
                labels[0] = Box("Missiles", "M", Color.black, Color.white, font);
                labels[4] = Box("Bombs", "B", Color.black, Color.white, font);
                for (int kind = 0; kind < 2; kind++)
                {
                    labels[kind * 4 + 1] = Box("InFlight" + kind, "", Color.yellow, Color.black, font);
                    labels[kind * 4 + 2] = Box("Contact" + kind, "", Color.green, Color.black, font);
                    labels[kind * 4 + 3] = Box("Lost" + kind, "", Color.red, Color.black, font);
                }
                root.gameObject.SetActive(false);
            }
            catch { Dispose(); throw; }
        }
        private static TMP_FontAsset ResolveFont(RectTransform parent)
        {
            var hud = SceneSingleton<FlightHud>.i;
            var native = hud != null ? hud.GetComponentInChildren<TextMeshProUGUI>(true) : null;
            if (native != null && native.font != null) return native.font;
            foreach (var label in parent.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (label.font != null) return label.font;
            if (TMP_Settings.defaultFontAsset != null) return TMP_Settings.defaultFontAsset;
            throw new InvalidOperationException("No native HUD font is available yet.");
        }
        private TextMeshProUGUI Box(string name, string content, Color background, Color foreground, TMP_FontAsset font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.layer = root.gameObject.layer;
            go.transform.SetParent(root, false);
            var image = go.GetComponent<Image>(); image.raycastTarget = false;
            background.a = 0.8f; image.color = background;
            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            var child = new GameObject("Value", typeof(RectTransform), typeof(TextMeshProUGUI));
            child.layer = go.layer; child.transform.SetParent(rect, false);
            var text = child.GetComponent<TextMeshProUGUI>();
            text.font = font; text.fontSize = 20; text.fontStyle = FontStyles.Bold;
            text.enableAutoSizing = false; text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow; text.alignment = TextAlignmentOptions.Center;
            text.raycastTarget = false; text.color = foreground; text.text = content;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            return text;
        }
        internal void Render(DeliveryCounts counts, bool visible, float scale, float left, float bottom)
        {
            if (disposed || !root || !parent) return;
            values[0] = counts.MissileInFlight; values[1] = counts.MissileHit; values[2] = counts.MissileMissed;
            values[3] = counts.BombInFlight; values[4] = counts.BombHit; values[5] = counts.BombMissed;
            bool any = false;
            for (int i = 0; i < values.Length; i++) any |= values[i] > 0;
            root.gameObject.SetActive(visible && any);
            if (!visible || !any) return;
            scale = Mathf.Clamp(float.IsNaN(scale) || float.IsInfinity(scale) ? 1 : scale, 0.25f, targetCamera ? 3f : 9f);
            if (!targetCamera) scale = Mathf.Min(scale, Mathf.Max(.25f, parent.rect.width / (36 * 4)));
            float width = 36 * scale, height = 20 * scale;
            if (float.IsNaN(left) || float.IsInfinity(left)) left = 6;
            if (float.IsNaN(bottom) || float.IsInfinity(bottom)) bottom = 6;
            float marginX = Mathf.Clamp(left, 0, Mathf.Max(0, parent.rect.width - width * 4));
            float marginY = Mathf.Clamp(bottom, 0, Mathf.Max(0, parent.rect.height - height * 2 - 2 * scale));
            int row = 0;
            for (int kind = 0; kind < 2; kind++)
            {
                int start = kind * 3, labelStart = kind * 4;
                bool has = values[start] + values[start + 1] + values[start + 2] > 0;
                var category = labels[labelStart];
                category.transform.parent.gameObject.SetActive(has);
                float x = targetCamera ? (kind == 0 ? 0 : parent.rect.width - width) : marginX;
                float y = targetCamera ? 64 : marginY + row * (height + 2 * scale);
                Place(category, x, y, width, height, scale);
                int compactIndex = 1;
                for (int status = 0; status < 3; status++)
                {
                    int count = values[start + status];
                    var label = labels[labelStart + status + 1];
                    label.transform.parent.gameObject.SetActive(count > 0 && has);
                    if (count <= 0) continue;
                    string content = count.ToString(CultureInfo.InvariantCulture);
                    if (label.text != content) label.text = content;
                    Place(label, x + (targetCamera ? 0 : width * compactIndex), y + (targetCamera ? height * compactIndex : 0), width, height, scale);
                    compactIndex++;
                }
                if (has) row++;
            }
        }
        private static void Place(TextMeshProUGUI label, float x, float y, float width, float height, float scale)
        {
            var box = (RectTransform)label.transform.parent;
            box.anchoredPosition = new Vector2(x, y); box.sizeDelta = new Vector2(width, height);
            label.fontSize = 20 * scale;
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (root) { root.gameObject.SetActive(false); UnityEngine.Object.Destroy(root.gameObject); }
        }
    }
}
