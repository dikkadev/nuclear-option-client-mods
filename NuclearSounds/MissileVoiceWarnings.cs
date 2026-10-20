using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearSounds
{
    internal static class MissileVoiceWarnings
    {
        private const string IrSeeker = "IR";
        private const string OpticalSeeker = "Optical";
        private const string ArhSeeker = "ARH";
        private const string SarhSeeker = "SARH";
        private const string ArmSeeker = "ARAD";

        private static readonly Dictionary<string, string> SeekerToAsset = new Dictionary<string, string>
        {
            { IrSeeker, "flare.mp3" },
            { OpticalSeeker, "hide.mp3" },
            { ArhSeeker, "notch.mp3" },
            { SarhSeeker, "notch.mp3" },
            { ArmSeeker, "radar.mp3" },
        };

        private static readonly Dictionary<string, AudioClip?> SeekerToClip = new Dictionary<string, AudioClip?>();
        private static Dictionary<string, int> _missileCounts = CreateCountMap();
        private static readonly List<string> PlaybackOrder = new List<string> { IrSeeker, OpticalSeeker, ArhSeeker, SarhSeeker, ArmSeeker };

        private static int _lastPlaybackIndex;
        private static float _lastVoiceTime;
        private static string? _lastSeekerType;

        internal static void Initialize()
        {
            // Load clips regardless of the current toggle so enabling warnings
            // through the in-game configuration menu works without a restart.
            SeekerToClip.Clear();
            foreach (var entry in SeekerToAsset)
            {
                SeekerToClip[entry.Key] = AudioLoader.LoadFromPluginAssets(entry.Value);
            }
        }

        private static Dictionary<string, int> CreateCountMap()
        {
            return new Dictionary<string, int>
            {
                { IrSeeker, 0 },
                { OpticalSeeker, 0 },
                { ArhSeeker, 0 },
                { SarhSeeker, 0 },
                { ArmSeeker, 0 },
            };
        }

        private static void Increment(string seekerType)
        {
            if (_missileCounts.ContainsKey(seekerType))
            {
                _missileCounts[seekerType]++;
            }
        }

        private static void Decrement(string seekerType)
        {
            if (_missileCounts.TryGetValue(seekerType, out var count) && count > 0)
            {
                _missileCounts[seekerType] = count - 1;
            }
        }

        private static void ResetState()
        {
            _missileCounts = CreateCountMap();
            _lastPlaybackIndex = 0;
            _lastVoiceTime = 0f;
            _lastSeekerType = null;
        }

        [HarmonyPatch(typeof(ThreatList), "ThreatList_OnMissileWarning")]
        private static class ThreatListOnMissileWarningPatch
        {
            private static void Prefix(ThreatList __instance, ref MissileWarning.OnMissileWarning e)
            {
                // Always track threats; the Update playback gate enforces the toggle.
                var itemLookup = Traverse.Create(__instance).Field("itemLookup").GetValue<Dictionary<PersistentID, ThreatItem>>();
                if (itemLookup.ContainsKey(e.missile.persistentID))
                {
                    return;
                }

                Increment(e.missile.GetSeekerType());
            }
        }

        [HarmonyPatch(typeof(ThreatList), "ThreatList_OffMissileWarning")]
        private static class ThreatListOffMissileWarningPatch
        {
            private static void Prefix(ThreatList __instance, ref MissileWarning.OffMissileWarning e)
            {
                var itemLookup = Traverse.Create(__instance).Field("itemLookup").GetValue<Dictionary<PersistentID, ThreatItem>>();
                if (!itemLookup.ContainsKey(e.missile.persistentID))
                {
                    return;
                }

                Decrement(e.missile.GetSeekerType());
            }
        }

        [HarmonyPatch(typeof(ThreatList), "Update")]
        private static class ThreatListUpdatePatch
        {
            private static void Postfix()
            {
                if (!Plugin.Instance.EnableMissileVoiceWarnings.Value)
                {
                    return;
                }

                var minRepeatSeconds = Mathf.Max(0.25f, Plugin.Instance.MissileVoiceRepeatSeconds.Value);
                if (Time.timeSinceLevelLoad - _lastVoiceTime < minRepeatSeconds)
                {
                    return;
                }

                for (var i = 1; i <= PlaybackOrder.Count; i++)
                {
                    var candidateIndex = (_lastPlaybackIndex + i) % PlaybackOrder.Count;
                    var seekerType = PlaybackOrder[candidateIndex];
                    if (!_missileCounts.TryGetValue(seekerType, out var count) || count <= 0)
                    {
                        continue;
                    }

                    if (!SeekerToClip.TryGetValue(seekerType, out var clip) || clip == null)
                    {
                        continue;
                    }

                    if (seekerType == _lastSeekerType && Time.timeSinceLevelLoad - _lastVoiceTime < minRepeatSeconds * 1.5f)
                    {
                        continue;
                    }

                    if (seekerType == ArhSeeker || seekerType == SarhSeeker)
                    {
                        clip = FlavorCallouts.TryGetRadarLockFlavorClip() ?? clip;
                    }

                    InterfaceAudio.PlayOneShot(clip, 3f);
                    _lastVoiceTime = Time.timeSinceLevelLoad;
                    _lastPlaybackIndex = candidateIndex;
                    _lastSeekerType = seekerType;
                    break;
                }
            }
        }

        [HarmonyPatch(typeof(ThreatList), "SetAircraft")]
        private static class ThreatListSetAircraftPatch
        {
            private static void Prefix()
            {
                ResetState();
            }
        }
    }
}
