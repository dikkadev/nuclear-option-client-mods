using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Audio;

namespace NuclearSounds
{
    internal static class AudioDiagnostics
    {
        private const int SampleRate = 44100;
        private const int MaximumPcmSamples = 2 * 1024 * 1024;
        private static AudioClip? _testTone;

        internal static void Clear()
        {
            if (_testTone != null) UnityEngine.Object.Destroy(_testTone);
            _testTone = null;
        }

        internal static IEnumerator PlayTest()
        {
            Plugin.Instance.Log.LogInfo("Manual audio test requested; close F1. Generated tone plays in two seconds, then lower gear voice.");
            AudioClip? tone = null;
            try
            {
                yield return new WaitForSecondsRealtime(2f);
                Safely("generated tone", () =>
                {
                    var samples = new float[(int)(SampleRate * 0.7f)];
                    for (var i = 0; i < samples.Length; i++)
                    {
                        var ramp = Mathf.Min(1f, i / 441f) * Mathf.Min(1f, (samples.Length - 1 - i) / 441f);
                        samples[i] = 0.35f * ramp * Mathf.Sin(2f * Mathf.PI * 880f * i / SampleRate);
                    }
                    tone = AudioClip.Create("manual-audio-test-880hz", samples.Length, 1, SampleRate, false);
                    _testTone = tone;
                    var written = tone.SetData(samples, 0);
                    Plugin.Instance.Log.LogInfo($"Manual audio test generated tone: SetData={written}.");
                    LogPcm(tone);
                    InterfaceAudio.PlayOneShot(tone, 1f);
                });
                yield return null;
                Snapshot("tone next frame");
                yield return new WaitForSecondsRealtime(0.2f);
                Snapshot("tone after 0.2 seconds");
                yield return new WaitForSecondsRealtime(2f);
                Safely("lower gear voice", () =>
                {
                    var voice = AudioLoader.LoadFromPluginAssets("lower gear.mp3");
                    if (voice == null)
                    {
                        Plugin.Instance.Log.LogWarning("Manual audio test: lower gear voice unavailable.");
                        return;
                    }
                    LogPcm(voice);
                    InterfaceAudio.PlayOneShot(voice, 1f);
                });
                yield return null;
                Snapshot("voice next frame");
                yield return new WaitForSecondsRealtime(0.2f);
                Snapshot("voice after 0.2 seconds");
                Plugin.Instance.Log.LogInfo("Manual audio test finished.");
            }
            finally
            {
                Safely("tone cleanup", () => { if (tone != null) UnityEngine.Object.Destroy(tone); });
                _testTone = null;
            }
        }

        private static void LogPcm(AudioClip clip)
        {
            Safely("PCM analysis", () => AnalyzePcm(clip));
        }

        private static void AnalyzePcm(AudioClip clip)
        {
            var count = (long)clip.samples * clip.channels;
            if (count <= 0 || count > MaximumPcmSamples)
            {
                Plugin.Instance.Log.LogWarning($"Manual audio test PCM skipped: clip={clip.name}, sampleValues={count}, limit={MaximumPcmSamples}.");
                return;
            }
            var samples = new float[(int)count];
            if (!clip.GetData(samples, 0))
            {
                Plugin.Instance.Log.LogWarning($"Manual audio test PCM GetData=False: clip={clip.name}, loadState={clip.loadState}.");
                return;
            }
            double sum = 0;
            float peak = 0;
            var nonzero = 0;
            foreach (var sample in samples)
            {
                sum += (double)sample * sample;
                peak = Mathf.Max(peak, Mathf.Abs(sample));
                if (sample != 0f) nonzero++;
            }
            Plugin.Instance.Log.LogInfo($"Manual audio test PCM: clip={clip.name}, GetData=True, samples={clip.samples}, channels={clip.channels}, frequency={clip.frequency}, loadState={clip.loadState}, nonzero={nonzero}, peak={peak}, RMS={Math.Sqrt(sum / samples.Length)}.");
        }

        private static void Snapshot(string phase)
        {
            Safely(phase, () =>
            {
                var source = InterfaceAudio.PlaybackSource;
                if (source == null)
                {
                    Plugin.Instance.Log.LogWarning($"Manual audio test {phase}: playback source missing.");
                    return;
                }
                Plugin.Instance.Log.LogInfo($"Manual audio test {phase}: {DescribeSource(source)}, isPlaying={source.isPlaying}, timeSamples={source.timeSamples}, isVirtual={source.isVirtual}.");
            });
        }

        internal static string DescribeSource(AudioSource source)
        {
            try
            {
                var group = source.outputAudioMixerGroup;
                var mixer = group != null ? group.audioMixer : null;
                return $"source={source.name}, active={source.isActiveAndEnabled}, pitch={source.pitch}, spatialBlend={source.spatialBlend}, mute={source.mute}, volume={source.volume}, listenerPause={AudioListener.pause}, listenerVolume={AudioListener.volume}, mixerGroup={(group != null ? group.name : "none")}, mixer={(mixer != null ? mixer.name : "none")}, MasterVolume={ReadMixer(mixer, "MasterVolume")}, InterfaceVolume={ReadMixer(mixer, "InterfaceVolume")}, MasterLowPassCutoff={ReadMixer(mixer, "MasterLowPassCutoff", "Hz")}";
            }
            catch (Exception error)
            {
                return $"source diagnostics unavailable: {error.GetType().Name}: {error.Message}";
            }
        }

        private static string ReadMixer(AudioMixer? mixer, string parameter, string units = "dB")
        {
            if (mixer == null) return "unrouted";
            return mixer.GetFloat(parameter, out var value) ? value + " " + units : "not exposed";
        }

        private static void Safely(string operation, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                Plugin.Instance.Log.LogWarning($"Manual audio test {operation} failed: {error.GetType().Name}: {error.Message}");
            }
        }
    }
}
