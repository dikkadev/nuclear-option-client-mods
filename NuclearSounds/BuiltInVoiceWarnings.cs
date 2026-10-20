using HarmonyLib;
using UnityEngine;

namespace NuclearSounds
{
    internal static class BuiltInVoiceWarnings
    {
        private static AudioClip? _lowerGearClip;
        private static AudioClip? _overspeedClip;
        private static AudioClip? _stallClip;

        internal static void Initialize()
        {
            _lowerGearClip = AudioLoader.LoadFromPluginAssets("lower gear.mp3");
            _overspeedClip = AudioLoader.LoadFromPluginAssets("overspeed.mp3");
            _stallClip = AudioLoader.LoadFromPluginAssets("stall.mp3");
        }

        // Swap only for this Refresh. The game's own warning conditions and timers
        // decide when to speak; failed loads and disabled options retain stock audio.
        private static AudioClip? Replace(object gauge, string field, bool enabled, AudioClip? replacement)
        {
            var voice = Traverse.Create(gauge).Field(field);
            var original = voice.GetValue<AudioClip>();
            if (enabled && replacement != null) voice.SetValue(replacement);
            return original;
        }

        [HarmonyPatch(typeof(GearIndicator), nameof(GearIndicator.Refresh))]
        private static class GearIndicatorRefreshPatch
        {
            private static void Prefix(GearIndicator __instance, out AudioClip? __state)
                => __state = Replace(__instance, "lowerGearVoice", Plugin.Instance.EnableLowerGearVoice.Value, _lowerGearClip);

            private static void Finalizer(GearIndicator __instance, AudioClip? __state)
                => Traverse.Create(__instance).Field("lowerGearVoice").SetValue(__state);
        }

        [HarmonyPatch(typeof(SpeedGauge), nameof(SpeedGauge.Refresh))]
        private static class SpeedGaugeRefreshPatch
        {
            private static void Prefix(SpeedGauge __instance, out AudioClip? __state)
                => __state = Replace(__instance, "overspeedVoice", Plugin.Instance.EnableOverspeedVoice.Value, _overspeedClip);

            private static void Finalizer(SpeedGauge __instance, AudioClip? __state)
                => Traverse.Create(__instance).Field("overspeedVoice").SetValue(__state);
        }

        [HarmonyPatch(typeof(AoADisplay), nameof(AoADisplay.Refresh))]
        private static class AoADisplayRefreshPatch
        {
            private static void Prefix(AoADisplay __instance, out AudioClip? __state)
                => __state = Replace(__instance, "stallVoice", Plugin.Instance.EnableStallVoice.Value, _stallClip);

            private static void Finalizer(AoADisplay __instance, AudioClip? __state)
                => Traverse.Create(__instance).Field("stallVoice").SetValue(__state);
        }
    }
}
