using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace NuclearNightVision
{
    // The native NightVision.Update switches instantly between two post-processing
    // volumes: its own night vision volume and LevelInfo.PostProcessing (the normal view).
    // After it runs, this keeps both volumes enabled during a switch and crossfades their
    // weights. When the fade ends, the enabled flags and weights are the native ones again.
    [HarmonyPatch(typeof(NightVision), "Update")]
    internal static class NightVisionFade
    {
        private static readonly AccessTools.FieldRef<NightVision, Volume> NightVisionVolume =
            AccessTools.FieldRefAccess<NightVision, Volume>("postProcessing");
        private static readonly AccessTools.FieldRef<NightVision, bool> NightVisionActive =
            AccessTools.FieldRefAccess<NightVision, bool>("nightVisActive");

        private static NightVision? _owner;
        private static Volume? _nightVolume;
        private static Volume? _normalVolume;
        private static float _nightWeight = 1f;
        private static float _normalWeight = 1f;
        private static float _amount;
        private static bool _blending;
        private static bool _failed;

        private static void Postfix(NightVision __instance)
        {
            if (_failed) return;
            try
            {
                Step(__instance);
            }
            catch (Exception exception)
            {
                _failed = true;
                Plugin.Log?.LogError("Night vision fade failed and is off until restart; the native switch still works: " + exception);
                Restore();
            }
        }

        private static void Step(NightVision nightVision)
        {
            var settings = Plugin.Instance;
            var level = NetworkSceneSingleton<LevelInfo>.i;
            Volume? nightVolume = NightVisionVolume(nightVision);
            Volume? normalVolume = level != null ? level.PostProcessing : null;
            if (settings == null || !settings.Enabled.Value || nightVolume == null || normalVolume == null)
            {
                Restore();
                return;
            }

            bool active = NightVisionActive(nightVision);
            if (nightVision != _owner || nightVolume != _nightVolume || normalVolume != _normalVolume)
            {
                // New scene or new volumes: start at the current native state, without a fade.
                Restore();
                _owner = nightVision;
                _nightVolume = nightVolume;
                _normalVolume = normalVolume;
                _nightWeight = nightVolume.weight;
                _normalWeight = normalVolume.weight;
                _amount = active ? 1f : 0f;
                return;
            }

            _amount = FadeMath.Step(_amount, active, Time.unscaledDeltaTime, settings.FadeInSeconds.Value, settings.FadeOutSeconds.Value);
            if (_amount == (active ? 1f : 0f))
            {
                Finish(active);
                return;
            }

            float eased = FadeMath.Ease(_amount);
            _blending = true;
            nightVolume.enabled = true;
            normalVolume.enabled = true;
            nightVolume.weight = _nightWeight * eased;
            normalVolume.weight = _normalWeight * (1f - eased);
        }

        // Puts the native state back: the captured weights and the enabled flags native code set.
        private static void Finish(bool active)
        {
            if (!_blending) return;
            _blending = false;
            if (_nightVolume != null)
            {
                _nightVolume.weight = _nightWeight;
                _nightVolume.enabled = active;
            }
            if (_normalVolume != null)
            {
                _normalVolume.weight = _normalWeight;
                _normalVolume.enabled = !active;
            }
        }

        // Ends any fade now, as if it had finished, and forgets the tracked scene.
        internal static void Restore()
        {
            if (_blending)
            {
                bool active = _owner != null ? NightVisionActive(_owner) : _amount >= 0.5f;
                Finish(active);
            }
            _owner = null;
            _nightVolume = null;
            _normalVolume = null;
        }
    }
}
