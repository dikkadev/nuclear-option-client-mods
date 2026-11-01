using NuclearOption.Networking;
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearSpectator
{
    // Pilot, type, speed, altitude and heading of the followed aircraft, bottom centre,
    // with the Follow Nearest When Lost status below. Either part can show alone.
    internal sealed class InfoPanel
    {
        private const float RefreshSeconds = 0.1f;
        private static readonly Vector2 Padding = new Vector2(28f, 12f);

        private GameObject? root;
        private RectTransform? panel;
        private TextMeshProUGUI? text;
        private float nextRefresh;
        private string? lastStatus;
        private bool broken;

        internal bool Owns(Canvas canvas) => root != null && canvas.gameObject == root;

        // `hidden`: Hide All UI is in its hide-everything step.
        internal void Update(bool hidden, AutoFollow follow)
        {
            if (broken) return;
            try
            {
                Aircraft? aircraft = null;
                string? status = null;
                var plugin = Plugin.Instance;
                var cam = SceneSingleton<CameraStateManager>.i;
                if (!hidden && plugin != null && plugin.Enabled.Value && cam != null && cam.currentState != null
                    && !DynamicMap.mapMaximized && GameManager.flightControlsEnabled && FollowView.Spectating())
                {
                    aircraft = Target(plugin, cam);
                    status = Status(plugin, cam, follow);
                }
                if (aircraft == null && status == null)
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
                if (Time.unscaledTime < nextRefresh && status == lastStatus) return;
                nextRefresh = Time.unscaledTime + RefreshSeconds;
                lastStatus = status;
                Refresh(plugin!, aircraft, status);
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

        private static Aircraft? Target(Settings plugin, CameraStateManager cam)
        {
            if (!plugin.ShowInfo.Value) return null;
            if (cam.currentState == cam.cockpitState && !plugin.InfoInCockpit.Value) return null;
            if (!(cam.followingUnit is Aircraft aircraft) || aircraft == null || aircraft.disabled) return null;
            return aircraft;
        }

        private static string? Status(Settings plugin, CameraStateManager cam, AutoFollow follow)
        {
            if (!plugin.ShowFollowNearestStatus.Value) return null;
            if (follow.Pending) return "<color=#9FE0A0>Following the nearest aircraft...</color>";
            if (!(cam.followingUnit is Aircraft aircraft) || !FollowView.Alive(aircraft)) return null;
            return plugin.FollowNearest.Value
                ? "<color=#9FE0A0>Follow nearest when lost: on</color>"
                : "<color=#A0A0A0>Follow nearest when lost: off</color>";
        }

        private void Refresh(Settings plugin, Aircraft? aircraft, string? status)
        {
            string value = string.Empty;
            if (aircraft != null)
            {
                var player = aircraft.Player;
                string pilot = player != null ? player.GetDisplayName(PlayerNameContext.Other) ?? "" : "AI";
                float heading = Mathf.Repeat(aircraft.transform.eulerAngles.y, 360f);
                value = $"<b>{Escape(pilot)}</b>   {Escape(aircraft.definition.unitName)}\n"
                    + $"{UnitConverter.SpeedReading(aircraft.speed)}   ALT {UnitConverter.AltitudeReading(aircraft.transform.position.GlobalY())}   "
                    + $"AGL {UnitConverter.AltitudeReading(aircraft.radarAlt)}   HDG {Mathf.RoundToInt(heading) % 360:000}";
            }
            if (status != null) value += (value.Length > 0 ? "\n" : "") + "<size=75%>" + status + "</size>";
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
