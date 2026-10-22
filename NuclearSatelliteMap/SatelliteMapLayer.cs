using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearSatelliteMap
{
    // Shows a satellite texture as the first child of the native DynamicMap.mapImage, below grid
    // lines, icons and other mods' map layers. The native Image stays in place for layout and input;
    // only its color alpha is scaled while the satellite layer is visible, and restored otherwise.
    internal sealed class SatelliteMapLayer
    {
        private const float NativeMapWidthUnits = 900f, NativeMetersPerWidth = 81920f;
        private const float SettingDebounceSeconds = .35f;

        private readonly Plugin plugin;
        private ManualLogSource Log => plugin.Log;

        private List<SatelliteMapFile>? index;
        private readonly HashSet<string> reportedIdentities = new HashSet<string>();
        private readonly HashSet<string> reportedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Image? nativeImage;
        private Sprite? boundSprite;
        private Vector2 boundSize;
        private SatelliteMapFile? selected;
        private Color nativeColor;
        private Color? writtenNativeColor;

        private RawImage? overlay;
        private Texture2D? texture;
        private SatelliteMapFile? textureFile;
        private ColorAdjustment textureAdjustment;
        private SatelliteLoadJob? job;
        private SatelliteMapFile? failedFile;
        private ColorAdjustment failedAdjustment;
        private float settingsChangedAt = float.NegativeInfinity;

        internal SatelliteMapLayer(Plugin plugin) => this.plugin = plugin;

        internal void SettingsChanged() => settingsChangedAt = Time.unscaledTime;

        internal void DataFolderChanged()
        {
            index = null;
            reportedIdentities.Clear();
            reportedFiles.Clear();
            boundSprite = null;
            failedFile = null;
        }

        internal void Update()
        {
            PollJobs();
            if (!plugin.Enabled.Value) { Shutdown(); return; }

            var map = SceneSingleton<DynamicMap>.i;
            var image = map != null && map.mapImage != null ? map.mapImage.GetComponent<Image>() : null;
            if (image == null || image.sprite == null) { Unbind(); return; }

            var size = ((RectTransform)image.transform).sizeDelta;
            if (image != nativeImage || image.sprite != boundSprite || size != boundSize) Bind(image, size);
            if (selected == null) { RestoreNative(); return; }

            RequestTexture(selected);
            bool show = texture != null && textureFile == selected &&
                (DynamicMap.mapMaximized ? plugin.UseOnExpandedMap.Value : plugin.UseOnMinimizedMap.Value);
            if (!show) { HideOverlay(); RestoreNative(); return; }

            ShowOverlay(image);
            ApplyNativeOpacity(image, plugin.NativeMapOpacity.Value);
        }

        internal void Shutdown()
        {
            Unbind();
            ReleaseTexture();
            job?.Abandon();
            job = null;
        }

        private void Bind(Image image, Vector2 size)
        {
            if (nativeImage != image) RestoreNative();
            nativeImage = image;
            boundSprite = image.sprite;
            boundSize = size;
            float widthMeters = size.x / NativeMapWidthUnits * NativeMetersPerWidth;
            float heightMeters = size.y / NativeMapWidthUnits * NativeMetersPerWidth;
            var rect = image.sprite.rect;
            int nativeWidth = Mathf.RoundToInt(rect.width), nativeHeight = Mathf.RoundToInt(rect.height);

            SatelliteMapFile? Find() => index!.Where(file => file.Matches(widthMeters, heightMeters, nativeWidth, nativeHeight))
                .OrderByDescending(file => file.Width).FirstOrDefault();
            index ??= BuildIndex();
            selected = Find();
            if (selected == null)
            {
                // A newly copied file should not require a restart.
                index = BuildIndex();
                selected = Find();
            }

            string identity = $"{image.sprite.name}/{image.sprite.texture?.name} {widthMeters:F0}x{heightMeters:F0} m, native {nativeWidth}x{nativeHeight}";
            if (reportedIdentities.Add(identity))
            {
                if (selected != null)
                    Log.LogInfo($"Map {identity}: using {Path.GetFileName(selected.Path)} ({selected.DisplayName}, {selected.Width}x{selected.Height}, {selected.MetersPerPixel:F1} m/px).");
                else
                    Log.LogInfo($"Map {identity}: no matching satellite data in {string.Join(", ", DataFolders())}; showing the native map.");
            }
            if (overlay != null && selected != textureFile) HideOverlay();
        }

        private void Unbind()
        {
            RestoreNative();
            if (overlay != null) UnityEngine.Object.Destroy(overlay.gameObject);
            overlay = null;
            nativeImage = null;
            boundSprite = null;
            selected = null;
        }

        private ColorAdjustment CurrentAdjustment()
        {
            var tint = plugin.Tint.Value;
            return new ColorAdjustment(plugin.Brightness.Value, plugin.Contrast.Value, plugin.Saturation.Value, plugin.Gamma.Value,
                tint.r, tint.g, tint.b, plugin.WaterOpacity.Value, plugin.LandOpacity.Value);
        }

        private void RequestTexture(SatelliteMapFile file)
        {
            var adjustment = CurrentAdjustment();
            // Free another map's texture before loading this one.
            if (texture != null && textureFile != file) ReleaseTexture();
            if (texture != null && textureFile == file && textureAdjustment.Equals(adjustment)) return;
            // One job per map at a time: a newer adjustment waits until the running one is uploaded.
            if (job != null && job.File == file) return;
            if (failedFile == file && failedAdjustment.Equals(adjustment)) return;
            // Debounce slider drags, but load a different map immediately.
            if (texture != null && textureFile == file && Time.unscaledTime - settingsChangedAt < SettingDebounceSeconds) return;
            job?.Abandon();
            job = SatelliteLoadJob.Start(file, adjustment);
        }

        private void PollJobs()
        {
            if (job == null || !job.Done) return;
            var finished = job;
            job = null;
            try
            {
                if (finished.Error != null) throw finished.Error;
                var created = Upload(finished);
                ReleaseTexture();
                texture = created;
                textureFile = finished.File;
                textureAdjustment = finished.Adjustment;
                if (overlay != null) overlay.texture = texture;
                Log.LogInfo($"Loaded {Path.GetFileName(finished.File.Path)} ({finished.File.DataLength / 1048576.0:F0} MiB GPU, {(finished.Adjusted ? "adjusted" : "unadjusted")}) in {finished.Milliseconds:F0} ms.");
            }
            catch (Exception error)
            {
                failedFile = finished.File;
                failedAdjustment = finished.Adjustment;
                Log.LogError($"Could not load {finished.File.Path}; showing the native map. {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                finished.Free();
            }
        }

        private static Texture2D Upload(SatelliteLoadJob finished)
        {
            var file = finished.File;
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.DXT5)) throw new NotSupportedException("This GPU does not support DXT5 textures.");
            if (Math.Max(file.Width, file.Height) > SystemInfo.maxTextureSize)
                throw new NotSupportedException($"{file.Width}x{file.Height} exceeds the GPU texture limit {SystemInfo.maxTextureSize}.");
            var created = new Texture2D(file.Width, file.Height, TextureFormat.DXT5, file.MipCount, false)
            {
                name = "NuclearSatelliteMap " + file.MapId,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 4,
                // Runtime textures are otherwise eligible for Resources.UnloadUnusedAssets between scenes.
                hideFlags = HideFlags.DontUnloadUnusedAsset,
            };
            try
            {
                created.LoadRawTextureData(finished.Buffer, (int)file.DataLength);
                created.Apply(false, true);
                return created;
            }
            catch
            {
                UnityEngine.Object.Destroy(created);
                throw;
            }
        }

        private void ReleaseTexture()
        {
            if (overlay != null) overlay.texture = null;
            if (texture != null) UnityEngine.Object.Destroy(texture);
            texture = null;
            textureFile = null;
        }

        private void ShowOverlay(Image image)
        {
            if (overlay == null)
            {
                var gameObject = new GameObject("NuclearSatelliteMap", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                gameObject.layer = image.gameObject.layer;
                var rect = (RectTransform)gameObject.transform;
                rect.SetParent(image.transform, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.pivot = new Vector2(.5f, .5f);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                overlay = gameObject.GetComponent<RawImage>();
                overlay.raycastTarget = false;
            }
            if (overlay.transform.GetSiblingIndex() != 0) overlay.transform.SetAsFirstSibling();
            if (!overlay.gameObject.activeSelf) overlay.gameObject.SetActive(true);
            if (overlay.texture != texture) overlay.texture = texture;
            var color = new Color(1f, 1f, 1f, DynamicMap.mapMaximized ? plugin.ExpandedMapOpacity.Value : plugin.MinimizedMapOpacity.Value);
            if (overlay.color != color) overlay.color = color;
        }

        private void HideOverlay()
        {
            if (overlay != null && overlay.gameObject.activeSelf) overlay.gameObject.SetActive(false);
        }

        // The game writes the theme color on map load and theme changes; adopt any color we did not write.
        private void ApplyNativeOpacity(Image image, float opacity)
        {
            var current = image.color;
            if (writtenNativeColor == null || current != writtenNativeColor.Value) nativeColor = current;
            var wanted = new Color(nativeColor.r, nativeColor.g, nativeColor.b, nativeColor.a * opacity);
            if (current != wanted) image.color = wanted;
            writtenNativeColor = wanted;
        }

        private void RestoreNative()
        {
            if (writtenNativeColor == null) return;
            if (nativeImage != null && nativeImage.color == writtenNativeColor.Value) nativeImage.color = nativeColor;
            writtenNativeColor = null;
        }

        internal IEnumerable<string> DataFolders()
        {
            var configured = plugin.DataFolder.Value.Trim();
            if (configured.Length > 0)
            {
                yield return Path.IsPathRooted(configured) ? configured : Path.Combine(BepInEx.Paths.BepInExRootPath, configured);
                yield break;
            }
            yield return Path.Combine(BepInEx.Paths.BepInExRootPath, "NuclearSatelliteMap");
            var pluginFolder = Path.GetDirectoryName(plugin.Info.Location);
            if (!string.IsNullOrEmpty(pluginFolder)) yield return Path.Combine(pluginFolder, "maps");
        }

        private List<SatelliteMapFile> BuildIndex()
        {
            var files = new List<SatelliteMapFile>();
            foreach (var folder in DataFolders())
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in Directory.GetFiles(folder, "*.nosat").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var file = SatelliteMapFile.Read(path);
                        files.Add(file);
                        if (reportedFiles.Add(path)) Log.LogInfo($"Found {path}: {file.DisplayName}, {file.Width}x{file.Height}, map {file.MapWidthMeters:F0}x{file.MapHeightMeters:F0} m, native {file.NativeWidth}x{file.NativeHeight}.");
                    }
                    catch (Exception error)
                    {
                        if (reportedFiles.Add(path)) Log.LogWarning($"Ignoring {path}: {error.Message}");
                    }
                }
            }
            return files;
        }
    }
}
