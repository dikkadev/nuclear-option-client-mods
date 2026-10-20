using System;
using UnityEngine;

namespace NuclearTrackAge
{
    internal static class TrackAgeFormatter
    {
        internal static Action<string>? DiagnosticObserved { get; set; }

        private const string InlineAgeMarker = " | AGE ";
        private const string InlineStaleMarker = " | STALE ";
        private const string LineAgeMarker = "\nAGE ";
        private const string LineStaleMarker = "\nSTALE ";

        internal static string ApplyInlineAge(string text, Unit unit, float accuracyThresholdMeters = 20f, bool showLabel = true)
        {
            if (!showLabel) DiagnosticObserved?.Invoke("surface disabled");
            string baseText = StripInlineAge(text);
            return showLabel && TryFormatLabel(unit, accuracyThresholdMeters, out string label) ? baseText + " | " + label : baseText;
        }

        internal static string ApplyLineAge(string text, Unit unit, float accuracyThresholdMeters = 20f, bool showLabel = true)
        {
            if (!showLabel) DiagnosticObserved?.Invoke("surface disabled");
            string baseText = StripLineAge(text);
            return showLabel && TryFormatLabel(unit, accuracyThresholdMeters, out string label) ? baseText + "\n" + label : baseText;
        }

        private static bool TryFormatLabel(Unit unit, float accuracyThresholdMeters, out string label)
        {
            label = string.Empty;
            Plugin plugin = Plugin.Instance;
            if (!plugin.Enabled.Value || unit == null)
            {
                DiagnosticObserved?.Invoke("plugin disabled or target unavailable");
                return false;
            }

            FactionHQ? hq = SceneSingleton<DynamicMap>.i != null ? SceneSingleton<DynamicMap>.i.HQ : null;
            if (hq == null)
            {
                DiagnosticObserved?.Invoke("map faction HQ unavailable");
                return false;
            }
            if (unit.NetworkHQ == null || unit.NetworkHQ == hq)
            {
                DiagnosticObserved?.Invoke("target friendly or without faction");
                return false;
            }

            TrackingInfo trackingInfo = hq.GetTrackingData(unit.persistentID);
            if (trackingInfo == null)
            {
                DiagnosticObserved?.Invoke("enemy tracking data unavailable");
                return false;
            }

            float ageSeconds = Mathf.Max(0f, Time.timeSinceLevelLoad - trackingInfo.lastSpottedTime);
            if (ageSeconds < plugin.MinimumAgeSeconds.Value)
            {
                DiagnosticObserved?.Invoke("track below minimum age");
                return false;
            }

            if (plugin.ShowOnlyOutdatedTracks.Value && hq.IsTargetPositionAccurate(unit, accuracyThresholdMeters))
            {
                DiagnosticObserved?.Invoke("track position still accurate");
                return false;
            }

            string prefix = ageSeconds >= plugin.StaleThresholdSeconds.Value ? "STALE" : "AGE";
            label = prefix + " " + FormatDuration(ageSeconds);
            DiagnosticObserved?.Invoke("first age label appended");
            return true;
        }

        private static string FormatDuration(float seconds)
        {
            int totalSeconds = Mathf.FloorToInt(seconds);
            if (totalSeconds < 60)
            {
                return totalSeconds + "s";
            }

            int totalMinutes = totalSeconds / 60;
            int remainingSeconds = totalSeconds % 60;
            if (totalMinutes < 60)
            {
                return totalMinutes + "m" + remainingSeconds.ToString("00") + "s";
            }

            int hours = totalMinutes / 60;
            int remainingMinutes = totalMinutes % 60;
            return hours + "h" + remainingMinutes.ToString("00") + "m";
        }

        private static string StripInlineAge(string text)
        {
            return StripFromFirstMarker(text, InlineAgeMarker, InlineStaleMarker);
        }

        private static string StripLineAge(string text)
        {
            return StripFromFirstMarker(text, LineAgeMarker, LineStaleMarker);
        }

        private static string StripFromFirstMarker(string text, string firstMarker, string secondMarker)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            int firstIndex = text.IndexOf(firstMarker, StringComparison.Ordinal);
            int secondIndex = text.IndexOf(secondMarker, StringComparison.Ordinal);
            int index;
            if (firstIndex >= 0 && secondIndex >= 0)
            {
                index = Math.Min(firstIndex, secondIndex);
            }
            else
            {
                index = Math.Max(firstIndex, secondIndex);
            }

            return index >= 0 ? text.Substring(0, index) : text;
        }
    }
}
