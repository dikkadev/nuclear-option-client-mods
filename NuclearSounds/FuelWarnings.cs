using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace NuclearSounds
{
    internal static class FuelWarningState
    {
        internal enum WarningBand
        {
            None,
            Low,
            Bingo,
        }

        internal static AudioClip? LowFuelClip;
        internal static AudioClip? BingoFuelClip;
        internal static float LastReading;
        internal static float LowFuelTime;
        internal static float BingoFuelTime;
        internal static float LowFuelTimeAfterburner;
        internal static float BingoFuelTimeAfterburner;
        internal static float LastFuelAmount = 1f;
        internal static float UpdateRate;
        internal static GameObject? FuelTimeLabel;
        internal static TMP_Text? SourceFuelLabel;
        internal static bool Initialized;
        internal static float InitTimer = float.PositiveInfinity;
        internal static WarningBand LastAnnouncedBand;

        internal const float RearmMarginSeconds = 20f;

        internal static void Initialize()
        {
            LowFuelTime = Plugin.Instance.FuelWarningMinutes.Value * 60f;
            BingoFuelTime = Plugin.Instance.BingoFuelMinutes.Value * 60f;
            LowFuelTimeAfterburner = Plugin.Instance.FuelWarningMinutesAfterburner.Value * 60f;
            BingoFuelTimeAfterburner = Plugin.Instance.BingoFuelMinutesAfterburner.Value * 60f;
            UpdateRate = Plugin.Instance.FuelWarningUpdateRateSeconds.Value;
            LastAnnouncedBand = WarningBand.None;
        }

        internal static void LoadAudio()
        {
            LowFuelClip = AudioLoader.LoadFromPluginAssets("fuel low.mp3");
            BingoFuelClip = AudioLoader.LoadFromPluginAssets("bingo fuel.mp3");
        }

        internal static void SyncLabel(FuelGauge gauge)
        {
            SourceFuelLabel = Traverse.Create(gauge).Field("fuelLabel").GetValue<TMP_Text>();
            SyncExistingLabel();
        }

        internal static void SyncExistingLabel()
        {
            if (FuelTimeLabel == null) return;
            var label = FuelTimeLabel.GetComponent<TMP_Text>();
            if (label == null) return;
            var source = SourceFuelLabel;
            label.enabled = source != null && source.isActiveAndEnabled
                && Plugin.Instance.isActiveAndEnabled && Plugin.Instance.EnableFuelWarnings.Value;
            if (source == null) return;
            label.font = source.font;
            label.fontSharedMaterial = source.fontSharedMaterial;
            label.fontSize = source.fontSize;
            label.fontStyle = source.fontStyle;
            label.alignment = source.alignment;
            label.color = source.color;
        }

        internal static void DestroyLabel()
        {
            if (FuelTimeLabel != null) Object.Destroy(FuelTimeLabel);
            FuelTimeLabel = null;
            SourceFuelLabel = null;
            Initialized = false;
            InitTimer = float.PositiveInfinity;
            LastAnnouncedBand = WarningBand.None;
        }

        internal static bool IsAfterburnerLikely(Aircraft aircraft)
        {
            var inputs = aircraft.GetInputs();
            return inputs != null && inputs.throttle >= 0.99f;
        }

        internal static WarningBand GetWarningBand(float fuelTimeSeconds, bool afterburner)
        {
            var bingoTime = afterburner ? BingoFuelTimeAfterburner : BingoFuelTime;
            var lowTime = afterburner ? LowFuelTimeAfterburner : LowFuelTime;

            if (fuelTimeSeconds < bingoTime)
            {
                return WarningBand.Bingo;
            }

            if (fuelTimeSeconds < lowTime)
            {
                return WarningBand.Low;
            }

            return WarningBand.None;
        }
    }

    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.Refresh))]
    internal static class FuelGaugeRefreshPatch
    {
        private static void Postfix(FuelGauge __instance)
        {
            FuelWarningState.SyncLabel(__instance);
            if (!Plugin.Instance.isActiveAndEnabled || !Plugin.Instance.EnableFuelWarnings.Value)
            {
                return;
            }

            if (!FuelWarningState.Initialized)
            {
                if (Time.timeSinceLevelLoad - FuelWarningState.InitTimer < 3f)
                {
                    return;
                }

                var fuelTimeObject = FindChild(__instance.transform, "fuelTime");
                if (fuelTimeObject == null)
                {
                    var fuelLabel = Traverse.Create(__instance).Field("fuelLabel").GetValue<TMP_Text>();
                    if (fuelLabel == null)
                    {
                        Plugin.Instance.Log.LogWarning("Could not find fuelLabel while creating fuel time label.");
                        return;
                    }

                    fuelTimeObject = Object.Instantiate(fuelLabel.gameObject, __instance.transform);
                    fuelTimeObject.name = "fuelTime";

                    var rectTransform = fuelTimeObject.GetComponent<RectTransform>();
                    var currentPosition = rectTransform.anchoredPosition;
                    rectTransform.anchoredPosition = new Vector2(currentPosition.x, currentPosition.y - 20f);
                }

                FuelWarningState.FuelTimeLabel = fuelTimeObject;
                FuelWarningState.FuelTimeLabel.GetComponent<TMP_Text>().text = "(...)";
                FuelWarningState.Initialized = true;
                FuelWarningState.SyncLabel(__instance);
            }

            if (Time.timeSinceLevelLoad - FuelWarningState.LastReading < FuelWarningState.UpdateRate)
            {
                return;
            }

            var aircraft = Traverse.Create(__instance).Field("aircraft").GetValue<Aircraft>();
            if (aircraft == null) return;
            var afterburnerLikely = FuelWarningState.IsAfterburnerLikely(aircraft);
            var fuelAmount = aircraft.GetFuelLevel();
            var fuelTime = FuelTimeEstimate.Calculate(FuelWarningState.LastFuelAmount,
                fuelAmount, Time.timeSinceLevelLoad - FuelWarningState.LastReading);

            FuelWarningState.LastReading = Time.timeSinceLevelLoad;
            FuelWarningState.LastFuelAmount = fuelAmount;

            try
            {
                var labelText = float.IsFinite(fuelTime)
                    ? $"({Mathf.FloorToInt(fuelTime / 60f)}m)"
                    : "(...)";
                if (FuelWarningState.FuelTimeLabel != null)
                {
                    FuelWarningState.FuelTimeLabel.GetComponent<TMP_Text>().text = labelText;
                }
            }
            catch
            {
                FuelWarningState.DestroyLabel();
                FuelWarningState.InitTimer = Time.timeSinceLevelLoad;
            }

            var currentBand = FuelWarningState.GetWarningBand(fuelTime, afterburnerLikely);
            if (currentBand == FuelWarningState.WarningBand.None)
            {
                var resetThreshold = (afterburnerLikely ? FuelWarningState.LowFuelTimeAfterburner : FuelWarningState.LowFuelTime)
                    + FuelWarningState.RearmMarginSeconds;
                if (fuelTime > resetThreshold)
                {
                    FuelWarningState.LastAnnouncedBand = FuelWarningState.WarningBand.None;
                }
            }
            else if (currentBand > FuelWarningState.LastAnnouncedBand)
            {
                var clip = currentBand == FuelWarningState.WarningBand.Bingo
                    ? FuelWarningState.BingoFuelClip
                    : FuelWarningState.LowFuelClip;
                if (clip != null)
                {
                    InterfaceAudio.PlayOneShot(clip, 3f);
                    FuelWarningState.LastAnnouncedBand = currentBand;
                }
            }
        }

        private static GameObject? FindChild(Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name)
                {
                    return child.gameObject;
                }
            }

            return null;
        }
    }

    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.Show))]
    internal static class FuelGaugeShowPatch
    {
        private static void Postfix(FuelGauge __instance) => FuelWarningState.SyncLabel(__instance);
    }

    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.RefreshSettings))]
    internal static class FuelGaugeRefreshSettingsPatch
    {
        private static void Postfix(FuelGauge __instance) => FuelWarningState.SyncLabel(__instance);
    }

    [HarmonyPatch(typeof(FlightHud), "OnDestroy")]
    internal static class FuelFlightHudDestroyPatch
    {
        private static void Prefix()
        {
            FuelWarningState.DestroyLabel();
        }
    }

    [HarmonyPatch(typeof(FuelGauge), nameof(FuelGauge.Initialize))]
    internal static class FuelGaugeInitializePatch
    {
        private static void Postfix(FuelGauge __instance, ref Aircraft aircraft)
        {
            FuelWarningState.DestroyLabel();
            FuelWarningState.SyncLabel(__instance);
            if (aircraft == null) return;
            FuelWarningState.LastFuelAmount = aircraft.GetFuelLevel();
            FuelWarningState.LastReading = Time.timeSinceLevelLoad;
            FuelWarningState.Initialized = false;
            FuelWarningState.InitTimer = Time.timeSinceLevelLoad;
            FuelWarningState.LastAnnouncedBand = FuelWarningState.WarningBand.None;
        }
    }
}
