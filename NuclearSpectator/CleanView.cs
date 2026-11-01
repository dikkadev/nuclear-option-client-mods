using NuclearOption.MissionEditorScripts;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearSpectator
{
    // One key steps through hiding the on-screen interface for clean video and
    // screenshots: the game's interface (HUD, chat, kill feed, map, panels) with the
    // mod's info panel kept, then everything, then nothing. Canvases that render into
    // cockpit screens and other render textures, and world-space canvases, stay visible.
    // The full map brings the interface back while it is open. The info panel hides
    // itself in the last step (HidesOverlay), so it is never in the scanned set.
    internal sealed class CleanView
    {
        private const float ScanSeconds = 0.25f;
        private readonly List<Canvas> hidden = new List<Canvas>();
        private readonly InfoPanel info;
        private HideLevel level;
        private float nextScan;
        private int toggles;

        internal CleanView(InfoPanel info) => this.info = info;

        internal bool HidesOverlay => level == HideLevel.All && !DynamicMap.mapMaximized;

        internal void Update()
        {
            var plugin = Plugin.Instance;
            // Spectating only: while you fly the key does nothing, and getting an aircraft
            // shows everything again.
            if (plugin == null || !plugin.Enabled.Value || !FollowView.Spectating())
            {
                level = HideLevel.None;
                Restore();
                return;
            }
            // Menus switch GameplayUI canvases through Canvas.enabled themselves: while a
            // menu is open, neither toggle nor restore, so its choice is not overwritten.
            var ui = SceneSingleton<GameplayUI>.i;
            if (ui != null && ui.menuCanvas != null && ui.menuCanvas.enabled) return;
            if (!InputFieldChecker.InsideInputField && plugin.HideAllUi.IsDown())
            {
                level = SpectatorCycle.NextHideLevel(level);
                nextScan = 0f;
                if (toggles++ < 30)
                    Plugin.Log?.LogInfo(level == HideLevel.None ? "Hide All UI off." : level == HideLevel.GameUi ? "Hide All UI: game interface hidden, info panel kept." : "Hide All UI: everything hidden.");
            }
            if (level == HideLevel.None || DynamicMap.mapMaximized)
            {
                Restore();
                return;
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + ScanSeconds;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.enabled || !canvas.isRootCanvas || !OnScreen(canvas)) continue;
                if ((ui != null && canvas == ui.menuCanvas) || info.Owns(canvas)) continue;
                canvas.enabled = false;
                if (!hidden.Contains(canvas)) hidden.Add(canvas);
            }
            Plugin.Once("cleanview:first", $"Hide All UI hid {hidden.Count} screen canvases: {string.Join(", ", hidden.ConvertAll(item => item != null ? item.name : "?"))}.");
        }

        internal void Dispose()
        {
            level = HideLevel.None;
            Restore();
        }

        private static bool OnScreen(Canvas canvas)
        {
            switch (canvas.renderMode)
            {
                case RenderMode.ScreenSpaceOverlay:
                    return true;
                case RenderMode.ScreenSpaceCamera:
                    return canvas.worldCamera == null || canvas.worldCamera.targetTexture == null;
                default:
                    return false;
            }
        }

        private void Restore()
        {
            foreach (var canvas in hidden)
                if (canvas != null) canvas.enabled = true;
            hidden.Clear();
        }
    }
}
