using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using TMPro;
using System.Collections.Generic;

namespace NuclearMapPlayerNames
{
    internal static class MapLabelDiagnostics
    {
        // Static state spans scenes: each event is emitted at most once per process.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        internal static bool First(string eventKey)
        {
            return Plugin.Instance != null && Reported.Add(eventKey);
        }

        internal static void Once(string eventKey, string message)
        {
            if (First(eventKey))
            {
                Plugin.Instance.Log.LogInfo("Map labels: " + message);
            }
        }
    }

    [HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
    internal static class UnitMapIconUpdatePatch
    {
        private static void Postfix(UnitMapIcon __instance, float mapInverseScale, bool mapMaximized)
        {
            MapLabelDiagnostics.Once("hook", "UpdateIcon hook reached.");
            var label = __instance.GetComponent<PlayerNameMapLabel>();
            if (label == null)
            {
                label = __instance.gameObject.AddComponent<PlayerNameMapLabel>();
            }

            label.Refresh(__instance, mapInverseScale, mapMaximized);
        }
    }

    internal sealed class PlayerNameMapLabel : MonoBehaviour
    {
        private TextMeshProUGUI? _text;


        internal void Refresh(UnitMapIcon icon, float _mapInverseScale, bool mapMaximized)
        {
            if (!TryGetPlayerName(icon, out var playerName) || !ShouldShow(mapMaximized))
            {
                SetVisible(false);
                return;
            }

            EnsureLabel(icon);
            if (_text == null)
            {
                return;
            }

            var config = Plugin.Instance;
            var rectTransform = (RectTransform)_text.transform;
            rectTransform.localPosition = GetIconLocalPosition(icon) + new Vector3(config.OffsetX.Value, config.OffsetY.Value, 0f);
            rectTransform.localScale = Vector3.one * config.TextScale.Value;
            rectTransform.localEulerAngles = Vector3.zero;
            rectTransform.SetAsLastSibling();

            _text.text = playerName;
            _text.fontSize = config.FontSize.Value;
            _text.color = ReadableLabelColor(icon.iconImage.color);
            _text.gameObject.SetActive(true);
            if (MapLabelDiagnostics.First("shown"))
            {
                Plugin.Instance.Log.LogInfo("Map labels: First label activated; activeInHierarchy=" + _text.gameObject.activeInHierarchy + ", iconActive=" + icon.gameObject.activeInHierarchy + ".");
            }
        }

        private static bool ShouldShow(bool mapMaximized)
        {
            bool show = Plugin.Instance.ShowPlayerNames.Value
                && (!Plugin.Instance.ShowOnlyOnMaximizedMap.Value || mapMaximized);
            if (!show)
            {
                MapLabelDiagnostics.Once("config", "Eligible label hidden by Show Player Names / maximized-map configuration.");
            }
            return show;
        }

        private static bool TryGetPlayerName(UnitMapIcon icon, out string playerName)
        {
            playerName = string.Empty;

            if (!(icon.unit is Aircraft aircraft) || aircraft.Player == null)
            {
                MapLabelDiagnostics.Once("no-player", "Skipped icon without a player-controlled aircraft.");
                return false;
            }

            if (Plugin.Instance.HideLocalPlayerName.Value && aircraft.Player.IsLocalPlayer)
            {
                MapLabelDiagnostics.Once("local", "Local aircraft name hidden by Hide Local Player Name.");
                return false;
            }

            // Match the stock hover tooltip: only show a player name for occupied, player-controlled aircraft.
            if (aircraft.pilots == null || aircraft.pilots.Length == 0 || aircraft.pilots[0] == null)
            {
                MapLabelDiagnostics.Once("pilot", "Skipped player aircraft without an occupied main pilot slot.");
                return false;
            }

            playerName = aircraft.Player.GetDisplayName(PlayerNameContext.Other) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(playerName))
            {
                MapLabelDiagnostics.Once("name", "Skipped player aircraft with an empty display name.");
                return false;
            }
            if (MapLabelDiagnostics.First("eligible"))
            {
                Plugin.Instance.Log.LogInfo("Map labels: First eligible player aircraft; local=" + aircraft.Player.IsLocalPlayer + ".");
            }
            return true;
        }

        private void EnsureLabel(UnitMapIcon icon)
        {
            if (_text != null)
            {
                return;
            }

            // Use the same font asset the current game uses to validate player names.
            if (GameAssets.i == null || GameAssets.i.playerNameFont == null)
            {
                MapLabelDiagnostics.Once("font", "Label creation waiting for GameAssets.playerNameFont.");
                return;
            }

            var labelObject = new GameObject("NuclearMapPlayerNames_Label", typeof(RectTransform));
            labelObject.transform.SetParent(icon.transform.parent, false);
            labelObject.transform.SetAsLastSibling();

            var rectTransform = (RectTransform)labelObject.transform;
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0f, 0.5f);
            rectTransform.sizeDelta = new Vector2(220f, 28f);

            _text = labelObject.AddComponent<TextMeshProUGUI>();
            _text.font = GameAssets.i.playerNameFont;
            _text.alignment = TextAlignmentOptions.MidlineLeft;
            _text.enableWordWrapping = false;
            _text.overflowMode = TextOverflowModes.Overflow;
            _text.raycastTarget = false;
            // Usernames are literal text, never Unity rich-text markup.
            _text.richText = false;

            _text.outlineColor = new Color32(0, 0, 0, 217);
            _text.outlineWidth = 0.15f;
            if (MapLabelDiagnostics.First("created"))
            {
                Plugin.Instance.Log.LogInfo("Map labels: First TMP label created; parentPresent=" + (icon.transform.parent != null) + ", fontMaterialPresent=" + (_text.fontSharedMaterial != null) + ".");
            }
        }

        private void SetVisible(bool visible)
        {
            if (_text != null)
            {
                _text.gameObject.SetActive(visible);
            }
        }

        private void OnDestroy()
        {
            if (_text != null)
            {
                Destroy(_text.gameObject);
            }
        }

        private void OnDisable()
        {
            // The label is a sibling of the icon, so Unity does not hide it with the icon.
            SetVisible(false);
        }

        private static Vector3 GetIconLocalPosition(UnitMapIcon icon)
        {
            if (icon.iconImage.transform == icon.transform)
            {
                return icon.transform.localPosition;
            }

            return icon.transform.localPosition + icon.iconImage.transform.localPosition;
        }

        private static Color ReadableLabelColor(Color iconColor)
        {
            if (iconColor.grayscale < 0.35f)
            {
                return Color.white;
            }

            iconColor.a = 1f;
            return iconColor;
        }
    }
}
