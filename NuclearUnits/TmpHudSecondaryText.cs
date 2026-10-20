using UnityEngine;
using TMPro;

namespace NuclearUnits
{
    internal static class TmpHudSecondaryText
    {
        private const string SecondaryName = "NuclearUnitsSecondary";
        private const float PrimaryLiftScale = 0.18f;
        private const float VerticalOffsetScale = 0.7f;

        public static void Set(TextMeshProUGUI primaryText, string? secondaryText)
        {
            if (string.IsNullOrWhiteSpace(secondaryText))
            {
                // Leave stock layout alone when the optional overlay has never been used.
                if (primaryText.TryGetComponent<HudSecondaryLayoutState>(out var existingState)
                    && existingState.HasOriginalAnchoredPosition)
                {
                    ResetPrimaryPosition(primaryText, existingState);
                    existingState.HasOriginalAnchoredPosition = false;
                }

                var existing = primaryText.transform.Find(SecondaryName);
                if (existing != null && existing.TryGetComponent<TextMeshProUGUI>(out var existingText))
                {
                    existingText.text = string.Empty;
                    existingText.enabled = false;
                }
                return;
            }

            var secondary = GetOrCreate(primaryText);
            var layoutState = GetOrCreateLayoutState(primaryText);
            LiftPrimary(primaryText, layoutState);
            SyncStyle(primaryText, secondary);
            secondary.text = secondaryText;
            secondary.enabled = primaryText.enabled;
        }

        private static TextMeshProUGUI GetOrCreate(TextMeshProUGUI primaryText)
        {
            var existing = primaryText.transform.Find(SecondaryName);
            if (existing != null && existing.TryGetComponent<TextMeshProUGUI>(out var existingText))
            {
                return existingText;
            }

            var child = new GameObject(SecondaryName, typeof(RectTransform));
            child.transform.SetParent(primaryText.transform, false);

            var secondary = child.AddComponent<TextMeshProUGUI>();
            secondary.raycastTarget = false;
            secondary.richText = false;

            return secondary;
        }

        private static HudSecondaryLayoutState GetOrCreateLayoutState(TextMeshProUGUI primaryText)
        {
            if (primaryText.TryGetComponent<HudSecondaryLayoutState>(out var existingState))
            {
                return existingState;
            }

            var state = primaryText.gameObject.AddComponent<HudSecondaryLayoutState>();
            state.OriginalAnchoredPosition = ((RectTransform)primaryText.transform).anchoredPosition;
            state.HasOriginalAnchoredPosition = true;
            return state;
        }

        private static void LiftPrimary(TextMeshProUGUI primaryText, HudSecondaryLayoutState layoutState)
        {
            var rect = (RectTransform)primaryText.transform;
            if (!layoutState.HasOriginalAnchoredPosition)
            {
                layoutState.OriginalAnchoredPosition = rect.anchoredPosition;
                layoutState.HasOriginalAnchoredPosition = true;
            }

            var lift = Mathf.RoundToInt(primaryText.fontSize * PrimaryLiftScale);
            rect.anchoredPosition = layoutState.OriginalAnchoredPosition + new Vector2(0f, lift);
        }

        private static void ResetPrimaryPosition(TextMeshProUGUI primaryText, HudSecondaryLayoutState layoutState)
        {
            if (!layoutState.HasOriginalAnchoredPosition)
            {
                return;
            }

            ((RectTransform)primaryText.transform).anchoredPosition = layoutState.OriginalAnchoredPosition;
        }

        private static void SyncStyle(TextMeshProUGUI primaryText, TextMeshProUGUI secondaryText)
        {
            secondaryText.font = primaryText.font;
            secondaryText.fontStyle = primaryText.fontStyle;
            secondaryText.alignment = primaryText.alignment;
            secondaryText.overflowMode = TextOverflowModes.Overflow;
            secondaryText.enableWordWrapping = false;
            secondaryText.enableAutoSizing = false;
            secondaryText.lineSpacing = primaryText.lineSpacing;
            secondaryText.fontSharedMaterial = primaryText.fontSharedMaterial;
            secondaryText.color = primaryText.color;

            var secondarySize = Mathf.Max(10, Mathf.RoundToInt(primaryText.fontSize * 0.6f));
            secondaryText.fontSize = secondarySize;

            var rect = (RectTransform)secondaryText.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);

            var yOffset = -Mathf.RoundToInt(primaryText.fontSize * VerticalOffsetScale);
            rect.anchoredPosition = new Vector2(0f, yOffset);
            rect.sizeDelta = new Vector2(0f, secondarySize + 6f);
        }
    }

}
