using NuclearOption.Networking;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearSpectator
{
    // Pilot, type, speed, altitude and heading of the followed aircraft, bottom centre.
    internal sealed class InfoPanel
    {
        private const float RefreshSeconds = 0.1f;
        private static readonly Vector2 Padding = new Vector2(28f, 12f);

        private GameObject? root;
        private RectTransform? panel;
        private TextMeshProUGUI? text;
        private float nextRefresh;
        private bool broken;

        internal void Update()
        {
            if (broken) return;
            try
            {
                var aircraft = Target();
                if (aircraft == null)
                {
                    if (root != null && root.activeSelf) root.SetActive(false);
                    return;
                }
                if (root == null) Create();
                if (!root!.activeSelf)
                {
                    root.SetActive(true);
                    nextRefresh = 0f;
                }
                if (Time.unscaledTime < nextRefresh) return;
                nextRefresh = Time.unscaledTime + RefreshSeconds;
                Refresh(aircraft);
            }
            catch (Exception error)
            {
                Plugin.Report("info panel", error);
                broken = true;
                Dispose();
            }
        }

        internal void Dispose()
        {
            if (root != null) UnityEngine.Object.Destroy(root);
            root = null;
            panel = null;
            text = null;
        }

        private static Aircraft? Target()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value || !plugin.ShowInfo.Value) return null;
            if (DynamicMap.mapMaximized || !GameManager.flightControlsEnabled || GameManager.GetLocalAircraft(out _)) return null;
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.currentState == null) return null;
            if (cam.currentState == cam.cockpitState && !plugin.InfoInCockpit.Value) return null;
            if (!(cam.followingUnit is Aircraft aircraft) || aircraft == null || aircraft.disabled) return null;
            return aircraft;
        }

        private void Refresh(Aircraft aircraft)
        {
            var plugin = Plugin.Instance!;
            var player = aircraft.Player;
            string pilot = player != null ? player.GetDisplayName(PlayerNameContext.Other) ?? "" : "AI";
            float heading = Mathf.Repeat(aircraft.transform.eulerAngles.y, 360f);
            string value = $"<b>{Escape(pilot)}</b>   {Escape(aircraft.definition.unitName)}\n"
                + $"{UnitConverter.SpeedReading(aircraft.speed)}   ALT {UnitConverter.AltitudeReading(aircraft.transform.position.GlobalY())}   "
                + $"AGL {UnitConverter.AltitudeReading(aircraft.radarAlt)}   HDG {Mathf.RoundToInt(heading) % 360:000}";
            if (text!.fontSize != plugin.InfoFontSize.Value) text.fontSize = plugin.InfoFontSize.Value;
            if (text.text != value) text.text = value;
            panel!.sizeDelta = text.GetPreferredValues(value) + Padding;
        }

        // Player names are user text; keep them from opening rich-text tags.
        private static string Escape(string value) => value.Replace("<", "<​");

        private void Create()
        {
            root = new GameObject("NuclearSpectator Info");
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            var background = new GameObject("Panel", typeof(RectTransform));
            background.transform.SetParent(root.transform, false);
            panel = (RectTransform)background.transform;
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0f);
            panel.pivot = new Vector2(0.5f, 0f);
            panel.anchoredPosition = new Vector2(0f, 64f);
            var image = background.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.45f);
            image.raycastTarget = false;

            var label = new GameObject("Text", typeof(RectTransform));
            label.transform.SetParent(background.transform, false);
            var rect = (RectTransform)label.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            text = label.AddComponent<TextMeshProUGUI>();
            if (GameAssets.i != null && GameAssets.i.playerNameFont != null) text.font = GameAssets.i.playerNameFont;
            text.alignment = TextAlignmentOptions.Center;
            text.richText = true;
            text.enableWordWrapping = false;
            text.raycastTarget = false;
            text.color = Color.white;
            text.fontSize = Plugin.Instance != null ? Plugin.Instance.InfoFontSize.Value : 22;
        }
    }
}
