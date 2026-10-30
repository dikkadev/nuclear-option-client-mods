using NuclearOption.MissionEditorScripts;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearSpectator
{
    // One key hides every on-screen interface (HUD, chat, kill feed, map, panels) for
    // clean video and screenshots. Canvases that render into cockpit screens and other
    // render textures, and world-space canvases, stay visible. The full map brings the
    // interface back while it is open.
    internal sealed class CleanView
    {
        private const float ScanSeconds = 0.25f;
        private readonly List<Canvas> hidden = new List<Canvas>();
        private bool toggled;
        private float nextScan;
        private int toggles;

        internal void Update()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
            {
                toggled = false;
                Restore();
                return;
            }
            // Menus switch GameplayUI canvases through Canvas.enabled themselves: while a
            // menu is open, neither toggle nor restore, so its choice is not overwritten.
            var ui = SceneSingleton<GameplayUI>.i;
            if (ui != null && ui.menuCanvas != null && ui.menuCanvas.enabled) return;
            if (!InputFieldChecker.InsideInputField && plugin.HideAllUi.IsDown())
            {
                toggled = !toggled;
                nextScan = 0f;
                if (toggles++ < 20) Plugin.Log?.LogInfo("Hide All UI " + (toggled ? "on" : "off") + ".");
            }
            if (!toggled || DynamicMap.mapMaximized)
            {
                Restore();
                return;
            }
            if (Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + ScanSeconds;
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.enabled || !canvas.isRootCanvas || !OnScreen(canvas)) continue;
                if (ui != null && canvas == ui.menuCanvas) continue;
                canvas.enabled = false;
                if (!hidden.Contains(canvas)) hidden.Add(canvas);
            }
            Plugin.Once("cleanview:first", $"Hide All UI hid {hidden.Count} screen canvases: {string.Join(", ", hidden.ConvertAll(item => item != null ? item.name : "?"))}.");
        }

        internal void Dispose()
        {
            toggled = false;
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
