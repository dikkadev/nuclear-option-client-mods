using System;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;


namespace NuclearSounds
{
    internal static class InterfaceAudio
    {
        private static bool _reportedUnavailableSource;
        private static readonly System.Collections.Generic.HashSet<int> ReportedClips = new System.Collections.Generic.HashSet<int>();

        internal static AudioSource? PlaybackSource => SoundManager.i != null
            ? Traverse.Create(SoundManager.i).Field("interfaceSource").GetValue<AudioSource>()
            : null;

        internal static void PlayOneShot(AudioClip? clip, float volume = 1f)
        {
            if (clip == null)
            {
                return;
            }

            var interfaceSource = PlaybackSource;
            if (interfaceSource == null || !interfaceSource.isActiveAndEnabled)
            {
                if (!_reportedUnavailableSource)
                {
                    _reportedUnavailableSource = true;
                    Plugin.Instance.Log.LogInfo("Custom interface audio skipped: source missing or inactive.");
                }
                return;
            }

            // Bound diagnostics while keeping voice requests observable even if
            // a designation tone was the first custom sound in the session.
            if (ReportedClips.Count < 32 && ReportedClips.Add(clip.GetInstanceID()))
            {
                Plugin.Instance.Log.LogInfo($"First custom interface playback request: clip={clip.name}, samples={clip.samples}, loadState={clip.loadState}, oneShotVolume={volume}; {AudioDiagnostics.DescribeSource(interfaceSource)}.");
            }
            interfaceSource.PlayOneShot(clip, volume);
        }
    }

    internal static class AudioLoader
    {
        private static readonly System.Collections.Generic.Dictionary<string, AudioClip> Clips
            = new System.Collections.Generic.Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        internal static System.Collections.IEnumerator PreloadAssets()
        {
            var pluginDirectory = Path.GetDirectoryName(Plugin.Instance.Info.Location);
            if (string.IsNullOrWhiteSpace(pluginDirectory)) yield break;
            var assetsDirectory = Path.Combine(pluginDirectory, "assets");
            if (!Directory.Exists(assetsDirectory))
            {
                Plugin.Instance.Log.LogWarning("Custom audio assets directory is missing.");
                yield break;
            }
            foreach (var fullPath in Directory.GetFiles(assetsDirectory, "*.mp3"))
            {
                using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(fullPath).AbsoluteUri, AudioType.MPEG);
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Instance.Log.LogWarning($"Failed to load audio asset '{Path.GetFileName(fullPath)}': {request.error}");
                    continue;
                }
                var clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null) continue;
                clip.name = Path.GetFileNameWithoutExtension(fullPath);
                Clips[Path.GetFileName(fullPath)] = clip;
            }
            Plugin.Instance.Log.LogInfo($"Custom audio preload completed: {Clips.Count} clips cached.");
        }

        internal static void Clear()
        {
            foreach (var clip in Clips.Values) UnityEngine.Object.Destroy(clip);
            Clips.Clear();
        }

        internal static AudioClip? LoadFromPluginAssets(string fileName)
        {
            if (Clips.TryGetValue(fileName, out var cachedClip)) return cachedClip;
            var pluginDirectory = Path.GetDirectoryName(Plugin.Instance.Info.Location);
            if (string.IsNullOrWhiteSpace(pluginDirectory))
            {
                Plugin.Instance.Log.LogWarning($"Could not resolve plugin directory for {fileName}.");
                return null;
            }

            var fullPath = Path.Combine(pluginDirectory, "assets", fileName);
            if (!File.Exists(fullPath))
            {
                Plugin.Instance.Log.LogWarning($"Missing audio asset: {fullPath}");
                return null;
            }

            Plugin.Instance.Log.LogWarning($"Audio asset '{fileName}' could not be preloaded.");
            return null;
        }
    }
}
