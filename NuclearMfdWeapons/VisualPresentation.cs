using System;
using HarmonyLib;
using NuclearOption.UIStyleSystem;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMfdWeapons
{
    internal sealed partial class DisplayController
    {
        // Source presentation follows NOTT's UILabel and LoadoutPreview coordinate formulas.
        private void ResolveFont()
        {
            var hud = SceneSingleton<FlightHud>.i;
            if (hud != null)
            {
                var label = hud.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null) fontAsset = label.font;
            }
            if (fontAsset == null)
                foreach (var stock in session.CanvasRoot.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (stock.font != null) { fontAsset = stock.font; break; }
            if (fontAsset == null) fontAsset = TMP_Settings.defaultFontAsset;
            if (fontAsset == null) throw new InvalidOperationException("No HUD text font is available.");
        }

        private Image CreateWeaponIcon(RectTransform root)
        {
            var hud = SceneSingleton<CombatHUD>.i;
            var status = AccessTools.Field(typeof(CombatHUD), "weaponStatus").GetValue(hud) as WeaponStatus;
            var original = status == null ? null : AccessTools.Field(typeof(WeaponStatus), "weaponImage").GetValue(status) as Image;
            // Copy the serialized Image presentation only, without stock child components/subscriptions.
            if (original == null && aircraft.weaponStations.Count > 0)
                throw new InvalidOperationException("The stock weapon image is unavailable.");
            var image = Image(root, "Icon", layout.weaponImagePos, original == null ? Vector2.one : original.rectTransform.sizeDelta * layout.imageScaleFactor);
            if (original != null)
            {
                image.material = original.material;
                image.sprite = original.sprite;
                image.type = original.type;
                image.preserveAspect = original.preserveAspect;
                image.fillCenter = original.fillCenter;
                image.fillMethod = original.fillMethod;
                image.fillAmount = original.fillAmount;
                image.fillClockwise = original.fillClockwise;
                image.fillOrigin = original.fillOrigin;
                image.pixelsPerUnitMultiplier = original.pixelsPerUnitMultiplier;
                image.rectTransform.pivot = original.rectTransform.pivot;
                image.rectTransform.localScale = original.rectTransform.localScale;
                image.rectTransform.localRotation = original.rectTransform.localRotation;
            }
            return image;
        }

        private Material? nameBaseMaterial;
        private void ApplyTheme()
        {
            if (ThemeManager.Active != null)
            {
                clear = ThemeManager.Active.ColorTheme.AllClear;
                alert = ThemeManager.Active.ColorTheme.Alert;
                valueGradient = ThemeManager.Active.ColorTheme.Gradient();
                textColor = Color.white;
                if (name != null) { if (nameBaseMaterial == null) nameBaseMaterial = name.fontSharedMaterial; name.font = fontAsset; name.fontSharedMaterial = nameBaseMaterial; }
                foreach (var item in ThemeManager.Active.TacScreenTheme.TextStyles)
                {
                    if (item.Label != null && item.Label.name == "TacScreen_TextMainColor")
                    {
                        if (name != null) { name.font = item.Style.Font != null ? item.Style.Font : fontAsset; name.fontSharedMaterial = item.Style.Material != null ? item.Style.Material : nameBaseMaterial; }
                        var color = item.Style.Color;
                        if (color.r != 0 || color.g != 0 || color.b != 0) { color.a = color.a == 0 ? 1 : color.a; textColor = color; }
                        break;
                    }
                }
            }
            var accent = clear;
            if (ThemeManager.Active != null)
                foreach (var item in ThemeManager.Active.TacScreenTheme.ImageStyles)
                    if (item.Label != null && item.Label.name == "TacScreen_ImageMainColor")
                    {

                        var color = item.Style.Color;
                        if (color.r != 0 || color.g != 0 || color.b != 0) { color.a = color.a == 0 ? 1 : color.a; accent = color; }
                        break;
                    }
            if (name != null) name.color = textColor;
            if (divider != null) divider.color = accent;
            foreach (var border in previewBorders) if (border != null) border.color = accent;
        }

        private void BuildPreview()
        {
            if (preview == null) return;
            previewBackground = Image(preview.Root, "Background", Vector2.zero, Vector2.one);
            previewBackground.color = Color.black;
            foreach (var edge in new[] { "Top", "Bottom", "Left", "Right" })
                previewBorders.Add(Image(preview.Root, "Border" + edge, Vector2.zero, Vector2.one));
            for (int i = 0; i < aircraft.weaponStations.Count; i++)
                rows.Add(Label(preview.Root, "Station" + i, Vector2.zero, previewFont + 6, Vector2.one));
            RefreshPreview(aircraft.weaponManager.currentWeaponStation);
        }

        private void RefreshPreview(WeaponStation? selected)
        {
            if (preview == null) return;
            // All rows measured at their largest selected size, exactly as the original initial popup.
            float maxWidth = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var station = i < aircraft.weaponStations.Count ? aircraft.weaponStations[i] : null;
                var row = rows[i];
                SetText(row, station == null ? "" : "[" + i + "]" + Name(station) + ": " + station.Ammo + "/" + station.FullAmmo);
                row.fontStyle = FontStyles.Bold;
                SetFont(row, previewFont + 6);
                maxWidth = Mathf.Max(maxWidth, row.preferredWidth);
            }
            float padding = (previewFont + 6) / 4;
            float halfWidth = maxWidth / 2;
            float halfHeight = rows.Count / 2f * (previewFont + 6);
            previewBackground.rectTransform.sizeDelta = new Vector2(maxWidth + padding * 2, halfHeight * 2 + padding * 2);
            for (int i = 0; i < rows.Count; i++)
            {
                var station = i < aircraft.weaponStations.Count ? aircraft.weaponStations[i] : null;
                var row = rows[i];
                bool active = station == selected;
                row.fontStyle = active ? FontStyles.Bold : FontStyles.Normal;
                SetFont(row, active ? previewFont + 6 : previewFont);
                row.color = station != null && station.Ammo == 0 ? alert : textColor;
                row.rectTransform.anchoredPosition = new Vector2(-(maxWidth - row.preferredWidth) / 2 - padding / 2, (rows.Count - 1) * padding * 2 - i * (previewFont + 6));
            }
            float x = halfWidth + padding, y = halfHeight + padding;
            // UIAdvancedRectangle's outward 2px border geometry.
            PlaceBorder(0, new Vector2(0, y + 1), new Vector2(x * 2 + 4, 2));
            PlaceBorder(1, new Vector2(0, -y - 1), new Vector2(x * 2 + 4, 2));
            PlaceBorder(2, new Vector2(-x - 1, 0), new Vector2(2, y * 2));
            PlaceBorder(3, new Vector2(x + 1, 0), new Vector2(2, y * 2));
            preview.Root.SetAsLastSibling();
        }

        private void PlaceBorder(int index, Vector2 pos, Vector2 size)
        {
            previewBorders[index].rectTransform.anchoredPosition = pos;
            previewBorders[index].rectTransform.sizeDelta = size;
        }
        private static void SetFont(TextMeshProUGUI label, float size)
        {
            if (label.fontSize != size) label.fontSize = size;
            MeasureLabel(label);
        }
        private static void MeasureLabel(TextMeshProUGUI label)
        {
            label.rectTransform.sizeDelta = new Vector2(label.preferredWidth, label.preferredHeight);
        }
    }
}
