using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearSatelliteMap
{
    internal enum TextureDetail { Full, Half }

    // Shows satellite textures as the first child of the native DynamicMap.mapImage, below grid
    // lines, icons and other mods' map layers. The native Image stays in place for layout and input;
    // only its color alpha is scaled while the satellite layer is visible, and restored otherwise.
    internal sealed class SatelliteMapLayer
    {
        private const float NativeMapWidthUnits = 900f, NativeMetersPerWidth = 81920f;

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

        private RectTransform? overlay;
        private readonly List<RawImage> tileImages = new List<RawImage>();
        private SatelliteMapFile? tilesFile;
        private Texture2D[]? textures;
        private SatelliteMapFile? textureFile;
        private TextureDetail textureDetail;
        private SatelliteLoadJob? job;
        private SatelliteMapFile? failedFile;
        private TextureDetail failedDetail;

        internal SatelliteMapLayer(Plugin plugin) => this.plugin = plugin;

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
            bool show = textures != null && textureFile == selected &&
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
            // Re-enabling retries a failed load.
            failedFile = null;
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
                    Log.LogInfo($"Map {identity}: using {Path.GetFileName(selected.Path)} ({selected.DisplayName}, {selected.Width}x{selected.Height} in {selected.TileColumns}x{selected.TileRows} tiles, {selected.MetersPerPixel:F1} m/px).");
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
            tileImages.Clear();
            tilesFile = null;
            nativeImage = null;
            boundSprite = null;
            selected = null;
        }

        private void RequestTexture(SatelliteMapFile file)
        {
            var detail = plugin.Detail.Value;
            // Free another map's textures before loading this one.
            if (textures != null && textureFile != file) ReleaseTexture();
            if (textures != null && textureDetail == detail) return;
            if (failedFile == file && failedDetail == detail) return;
            // One job per map at a time; a detail change waits for the running job, then reloads.
            if (job != null && job.File == file) return;
            job?.Abandon();
            job = SatelliteLoadJob.Start(file);
        }

        private void PollJobs()
        {
            if (job == null || !job.Done) return;
            var finished = job;
            // The buffer holds every level, so upload at the detail selected now.
            var detail = plugin.Detail.Value;
            job = null;
            try
            {
                if (finished.Error != null) throw finished.Error;
                // Free the previous textures first so a switch to Half never needs both sets at once.
                ReleaseTexture();
                var created = Upload(finished, detail, out long gpuBytes);
                textures = created;
                textureFile = finished.File;
                textureDetail = detail;
                Log.LogInfo($"Loaded {Path.GetFileName(finished.File.Path)} at {detail} detail ({created.Length} texture(s), {gpuBytes / 1048576.0:F0} MiB GPU); read and inflate took {finished.Milliseconds:F0} ms.");
            }
            catch (Exception error)
            {
                failedFile = finished.File;
                failedDetail = detail;
                Log.LogError($"Could not load {finished.File.Path}; showing the native map. {error.GetType().Name}: {error.Message}");
            }
            finally
            {
                finished.Free();
            }
        }

        // Each tile is its own texture. Half detail starts each tile's chain at mip 1.
        private static Texture2D[] Upload(SatelliteLoadJob finished, TextureDetail detail, out long gpuBytes)
        {
            var file = finished.File;
            // BC3 needs a top level in whole 4x4 blocks; otherwise Half keeps Full.
            int skip = detail == TextureDetail.Half && file.MipCount > 1 && file.TileWidth % 8 == 0 && file.TileHeight % 8 == 0 ? 1 : 0;
            int width = Math.Max(1, file.TileWidth >> skip), height = Math.Max(1, file.TileHeight >> skip), mips = file.MipCount - skip;
            long skipped = SatelliteBlocks.Bc3Size(file.TileWidth, file.TileHeight, skip);
            long length = file.TileDataLength - skipped;
            if (!SystemInfo.SupportsTextureFormat(TextureFormat.DXT5)) throw new NotSupportedException("This GPU does not support DXT5 textures.");
            if (Math.Max(width, height) > SystemInfo.maxTextureSize)
                throw new NotSupportedException($"{width}x{height} tiles exceed the GPU texture limit {SystemInfo.maxTextureSize}; try Texture Detail = Half.");
            var created = new List<Texture2D>();
            try
            {
                for (int tile = 0; tile < file.TileColumns * file.TileRows; tile++)
                {
                    var texture = new Texture2D(width, height, TextureFormat.DXT5, mips, false)
                    {
                        name = $"NuclearSatelliteMap {file.MapId} {tile}",
                        filterMode = FilterMode.Trilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        anisoLevel = 4,
                        // Runtime textures are otherwise eligible for Resources.UnloadUnusedAssets between scenes.
                        hideFlags = HideFlags.DontUnloadUnusedAsset,
                    };
                    created.Add(texture);
                    texture.LoadRawTextureData(finished.Buffer + (int)(tile * file.TileDataLength + skipped), (int)length);
                    texture.Apply(false, true);
                }
                gpuBytes = length * created.Count;
                return created.ToArray();
            }
            catch
            {
                foreach (var texture in created) UnityEngine.Object.Destroy(texture);
                throw;
            }
        }

        private void ReleaseTexture()
        {
            foreach (var tile in tileImages) if (tile != null) tile.texture = null;
            if (textures != null) foreach (var texture in textures) UnityEngine.Object.Destroy(texture);
            textures = null;
            textureFile = null;
        }

        private void ShowOverlay(Image image)
        {
            if (overlay == null)
            {
                var root = new GameObject("NuclearSatelliteMap", typeof(RectTransform));
                root.layer = image.gameObject.layer;
                overlay = (RectTransform)root.transform;
                overlay.SetParent(image.transform, false);
                Stretch(overlay, Vector2.zero, Vector2.one);
                tileImages.Clear();
                tilesFile = null;
            }
            if (overlay.GetSiblingIndex() != 0) overlay.SetAsFirstSibling();
            if (!overlay.gameObject.activeSelf) overlay.gameObject.SetActive(true);

            var file = textureFile!;
            if (tilesFile != file) BuildTiles(file);
            var color = new Color(1f, 1f, 1f, DynamicMap.mapMaximized ? plugin.ExpandedMapOpacity.Value : plugin.MinimizedMapOpacity.Value);
            for (int i = 0; i < tileImages.Count; i++)
            {
                var tile = tileImages[i];
                if (tile.texture != textures![i]) tile.texture = textures[i];
                if (tile.color != color) tile.color = color;
            }
        }

        // Tiles are row-major from the north-west corner.
        private void BuildTiles(SatelliteMapFile file)
        {
            foreach (var tile in tileImages) if (tile != null) UnityEngine.Object.Destroy(tile.gameObject);
            tileImages.Clear();
            for (int i = 0; i < file.TileColumns * file.TileRows; i++)
            {
                int column = i % file.TileColumns, row = i / file.TileColumns;
                var gameObject = new GameObject($"Tile {i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
                gameObject.layer = overlay!.gameObject.layer;
                var rect = (RectTransform)gameObject.transform;
                rect.SetParent(overlay, false);
                Stretch(rect,
                    new Vector2((float)column / file.TileColumns, 1f - (float)(row + 1) / file.TileRows),
                    new Vector2((float)(column + 1) / file.TileColumns, 1f - (float)row / file.TileRows));
                var tile = gameObject.GetComponent<RawImage>();
                tile.raycastTarget = false;
                tileImages.Add(tile);
            }
            tilesFile = file;
        }

        private static void Stretch(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(.5f, .5f);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
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
