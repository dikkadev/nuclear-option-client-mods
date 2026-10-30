using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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

    /// <summary>Unity objects compare equal once destroyed; key caches by the exact instance.</summary>
    internal sealed class InstanceComparer<T> : IEqualityComparer<T> where T : class
    {
        internal static readonly InstanceComparer<T> Instance = new InstanceComparer<T>();
        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }

    [HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
    internal static class UnitMapIconUpdatePatch
    {
        // One label per eligible icon. Ground units, ships, buildings and missiles never get a component.
        internal static readonly Dictionary<UnitMapIcon, PlayerNameMapLabel> Labels =
            new Dictionary<UnitMapIcon, PlayerNameMapLabel>(InstanceComparer<UnitMapIcon>.Instance);
        private static bool hookReported;

        private static void Postfix(UnitMapIcon __instance, float mapInverseScale, bool mapMaximized)
        {
            if (!hookReported) { hookReported = true; MapLabelDiagnostics.Once("hook", "UpdateIcon hook reached."); }
            if (Labels.TryGetValue(__instance, out var label) && label != null)
            {
                label.Refresh(__instance, mapInverseScale, mapMaximized);
                return;
            }

            // Cheap checks first; a label is created only once its aircraft would actually show a name.
            if (!(__instance.unit is Aircraft) || !PlayerNameMapLabel.ShouldShow(mapMaximized) ||
                !PlayerNameMapLabel.TryGetPlayerName(__instance, out _))
            {
                return;
            }

            label = __instance.gameObject.AddComponent<PlayerNameMapLabel>();
            Labels[__instance] = label;
            label.Refresh(__instance, mapInverseScale, mapMaximized);
        }
    }

    internal sealed class PlayerNameMapLabel : MonoBehaviour
    {
        // Labels share one container per icon layer, kept above the icons, so no label reorders siblings.
        private static readonly Dictionary<Transform, RectTransform> Containers =
            new Dictionary<Transform, RectTransform>(InstanceComparer<Transform>.Instance);
        private static int orderedFrame = -1;

        private UnitMapIcon? _icon;
        private TextMeshProUGUI? _text;
        private RectTransform? _rect;
        private bool _visible;
        private Vector3 _position = new Vector3(float.NaN, 0f, 0f);
        private float _scale = float.NaN;
        private int _fontSize = -1;
        private string? _name;
        private Color _color = new Color(-1f, -1f, -1f, -1f);

        internal void Refresh(UnitMapIcon icon, float _mapInverseScale, bool mapMaximized)
        {
            _icon = icon;
            if (!ShouldShow(mapMaximized) || !TryGetPlayerName(icon, out var playerName))
            {
                SetVisible(false);
                return;
            }

            EnsureLabel(icon);
            if (_text == null || _rect == null)
            {
                return;
            }

            // Icons move; settings and names rarely change. Write only what changed, since every write
            // makes the map canvas rebuild or rebatch.
            var config = Plugin.Instance;
            var position = GetIconLocalPosition(icon) + new Vector3(config.OffsetX.Value, config.OffsetY.Value, 0f);
            if (position != _position) { _position = position; _rect.localPosition = position; }
            float scale = config.TextScale.Value;
            if (scale != _scale) { _scale = scale; _rect.localScale = Vector3.one * scale; }
            if (!string.Equals(playerName, _name)) { _name = playerName; _text.text = playerName; }
            int fontSize = config.FontSize.Value;
            if (fontSize != _fontSize) { _fontSize = fontSize; _text.fontSize = fontSize; }
            var color = ReadableLabelColor(icon.iconImage.color);
            if (color != _color) { _color = color; _text.color = color; }
            SetVisible(true);
            KeepAboveIcons();
            if (MapLabelDiagnostics.First("shown"))
            {
                Plugin.Instance.Log.LogInfo("Map labels: First label activated; activeInHierarchy=" + _text.gameObject.activeInHierarchy + ", iconActive=" + icon.gameObject.activeInHierarchy + ".");
            }
        }

        internal static bool ShouldShow(bool mapMaximized)
        {
            bool show = Plugin.Instance.ShowPlayerNames.Value
                && (!Plugin.Instance.ShowOnlyOnMaximizedMap.Value || mapMaximized);
            if (!show)
            {
                MapLabelDiagnostics.Once("config", "Eligible label hidden by Show Player Names / maximized-map configuration.");
            }
            return show;
        }

        internal static bool TryGetPlayerName(UnitMapIcon icon, out string playerName)
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
            labelObject.transform.SetParent(ContainerFor(icon.transform.parent), false);

            var rectTransform = (RectTransform)labelObject.transform;
            rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            rectTransform.pivot = new Vector2(0f, 0.5f);
            rectTransform.sizeDelta = new Vector2(220f, 28f);
            rectTransform.localEulerAngles = Vector3.zero;
            _rect = rectTransform;

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
            _visible = labelObject.activeSelf;
            if (MapLabelDiagnostics.First("created"))
            {
                Plugin.Instance.Log.LogInfo("Map labels: First TMP label created; parentPresent=" + (icon.transform.parent != null) + ", fontMaterialPresent=" + (_text.fontSharedMaterial != null) + ".");
            }
        }

        // A stretched child with the layer's pivot shares the icon layer's local space, so label positions
        // computed from icon positions stay valid.
        private static RectTransform ContainerFor(Transform layer)
        {
            if (Containers.TryGetValue(layer, out var container) && container != null)
            {
                return container;
            }

            var stale = new List<Transform>();
            foreach (var pair in Containers)
            {
                if (pair.Key == null || pair.Value == null) stale.Add(pair.Key!);
            }
            foreach (var key in stale) Containers.Remove(key);

            var containerObject = new GameObject("NuclearMapPlayerNames_Labels", typeof(RectTransform));
            containerObject.layer = layer.gameObject.layer;
            container = (RectTransform)containerObject.transform;
            container.SetParent(layer, false);
            container.anchorMin = Vector2.zero;
            container.anchorMax = Vector2.one;
            container.offsetMin = container.offsetMax = Vector2.zero;
            container.pivot = layer is RectTransform rect ? rect.pivot : new Vector2(0.5f, 0.5f);
            container.SetAsLastSibling();
            Containers[layer] = container;
            return container;
        }

        // New icons are appended to the layer; lift the shared container back above them at most once a frame.
        private static void KeepAboveIcons()
        {
            if (orderedFrame == Time.frameCount) return;
            orderedFrame = Time.frameCount;
            foreach (var container in Containers.Values)
            {
                if (container == null) continue;
                var parent = container.parent;
                if (parent != null && container.GetSiblingIndex() != parent.childCount - 1) container.SetAsLastSibling();
            }
        }

        private void SetVisible(bool visible)
        {
            if (_text != null && _visible != visible)
            {
                _visible = visible;
                _text.gameObject.SetActive(visible);
            }
        }

        private void OnDestroy()
        {
            // The icon is usually being destroyed too; the cache is keyed by instance, not Unity liveness.
            if (!ReferenceEquals(_icon, null) && UnitMapIconUpdatePatch.Labels.TryGetValue(_icon, out var current) && ReferenceEquals(current, this))
            {
                UnitMapIconUpdatePatch.Labels.Remove(_icon);
            }
            if (_text != null)
            {
                Destroy(_text.gameObject);
            }
        }

        private void OnDisable()
        {
            // The label sits in a shared container, so Unity does not hide it with the icon.
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
