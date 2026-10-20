using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearSounds
{
    internal static class DesignateToneState
    {
        private const int SampleRate = 44100;

        private static AudioClip? _lockClip;
        private static AudioClip? _lostClip;

        internal static void Initialize()
        {
            _lockClip = CreateToneSequence(
                "designate-lock",
                (880f, 0.04f, 0.6f),
                (1240f, 0.06f, 1f));

            _lostClip = CreateToneSequence(
                "designate-lost",
                (880f, 0.04f, 0.9f),
                (640f, 0.08f, 1f));
        }

        internal static void Clear()
        {
            if (_lockClip != null) UnityEngine.Object.Destroy(_lockClip);
            if (_lostClip != null) UnityEngine.Object.Destroy(_lostClip);
            _lockClip = null;
            _lostClip = null;
        }

        internal static void OnTargetListChanged(int previousCount, int currentCount)
        {
            if (!Plugin.Instance.EnableDesignateTones.Value)
            {
                return;
            }

            if (previousCount <= 0 && currentCount > 0)
            {
                InterfaceAudio.PlayOneShot(_lockClip, Plugin.Instance.DesignateToneVolume.Value);
            }
            else if (previousCount > 0 && currentCount <= 0)
            {
                InterfaceAudio.PlayOneShot(_lostClip, Plugin.Instance.DesignateToneVolume.Value);
            }
        }

        private static AudioClip CreateToneSequence(string name, params (float frequency, float duration, float amplitude)[] tones)
        {
            var samples = new List<float>();
            foreach (var tone in tones)
            {
                var toneSamples = Mathf.CeilToInt(tone.duration * SampleRate);
                for (var i = 0; i < toneSamples; i++)
                {
                    var t = i / (float)SampleRate;
                    var envelope = Mathf.Min(1f, i / 256f) * Mathf.Min(1f, (toneSamples - i) / 256f);
                    var sample = Mathf.Sin(2f * Mathf.PI * tone.frequency * t) * tone.amplitude * envelope;
                    samples.Add(sample);
                }

                var gapSamples = Mathf.CeilToInt(0.015f * SampleRate);
                for (var i = 0; i < gapSamples; i++)
                {
                    samples.Add(0f);
                }
            }

            var clip = AudioClip.Create(name, samples.Count, 1, SampleRate, false);
            clip.SetData(samples.ToArray(), 0);
            return clip;
        }
    }

    [HarmonyPatch(typeof(CombatHUD), "DeselectAll")]
    internal static class CombatHudDeselectAllTonePatch
    {
        private static void Prefix(CombatHUD __instance, ref int __state)
        {
            __state = Traverse.Create(__instance).Field("targetList").GetValue<List<Unit>>().Count;
        }

        private static void Postfix(CombatHUD __instance, int __state)
        {
            var currentCount = Traverse.Create(__instance).Field("targetList").GetValue<List<Unit>>().Count;
            DesignateToneState.OnTargetListChanged(__state, currentCount);
        }
    }

    [HarmonyPatch(typeof(CombatHUD), "DeSelectUnit")]
    internal static class CombatHudDeselectUnitTonePatch
    {
        private static void Prefix(CombatHUD __instance, ref int __state)
        {
            __state = Traverse.Create(__instance).Field("targetList").GetValue<List<Unit>>().Count;
        }

        private static void Postfix(CombatHUD __instance, int __state)
        {
            var currentCount = Traverse.Create(__instance).Field("targetList").GetValue<List<Unit>>().Count;
            DesignateToneState.OnTargetListChanged(__state, currentCount);
        }
    }
}
