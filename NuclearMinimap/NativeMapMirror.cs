using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    internal sealed class NativeMapMirror : IDisposable
    {
        private readonly DynamicMap source;
        private readonly RectTransform viewport, root;
        private readonly Dictionary<Graphic, MapGraphic> copies = new Dictionary<Graphic, MapGraphic>();
        private readonly List<Graphic> graphics = new List<Graphic>(), removed = new List<Graphic>();
        private readonly HashSet<Graphic> current = new HashSet<Graphic>();
        internal int GraphicCount => graphics.Count;
        internal NativeMapMirror(DynamicMap source, RectTransform root)
        {
            this.source = source; this.root = root;
            viewport = (RectTransform)source.transform;
            var clip = root.gameObject.AddComponent<RectMask2D>();
            var nativeClip = source.GetComponent<RectMask2D>();
            if (nativeClip != null) clip.softness = nativeClip.softness;
        }

        internal bool SourceReady => source != null && source.isActiveAndEnabled && viewport != null &&
            source.mapImage != null && source.mapImage.activeInHierarchy && viewport.rect.width > 0 && viewport.rect.height > 0;

        internal bool Capture()
        {
            if (!SourceReady || DynamicMap.mapMaximized) return false;
            graphics.Clear();
            source.GetComponentsInChildren(false, graphics);
            current.Clear(); current.UnionWith(graphics);
            removed.Clear();
            foreach (var item in copies)
                if (item.Key == null || !current.Contains(item.Key)) removed.Add(item.Key!);
            foreach (var value in removed)
            {
                var copy = copies[value];
                if (copy != null) { copy.gameObject.SetActive(false); UnityEngine.Object.Destroy(copy.gameObject); }
                copies.Remove(value);
            }
            var from = viewport.rect; var to = root.rect;
            float sx = to.width / from.width, sy = to.height / from.height;
            var projection = Matrix4x4.Translate(new Vector3(
                MapPresentation.MapCoordinate(0, from.xMin, from.width, to.xMin, to.width),
                MapPresentation.MapCoordinate(0, from.yMin, from.height, to.yMin, to.height), 0)) *
                Matrix4x4.Scale(new Vector3(sx, sy, 1)) * viewport.worldToLocalMatrix;
            bool terrain = false;
            foreach (var graphic in graphics)
            {
                if (graphic == null) continue;
                if (!copies.TryGetValue(graphic, out var copy))
                {
                    var obj = new GameObject("Native " + graphic.name, typeof(RectTransform), typeof(MapGraphic));
                    obj.layer = root.gameObject.layer;
                    var rect = (RectTransform)obj.transform; rect.SetParent(root, false);
                    rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                    rect.offsetMin = rect.offsetMax = Vector2.zero; rect.pivot = root.pivot;
                    copy = obj.GetComponent<MapGraphic>(); copy.raycastTarget = false;
                    copies.Add(graphic, copy);
                }
                // Native nested canvases use hierarchy order; preserve it including new TMP submeshes.
                copy.transform.SetAsLastSibling();
                if (!graphic.isActiveAndEnabled || graphic.canvasRenderer.cull) { copy.Clear(); continue; }
                var tint = graphic.canvasRenderer.GetColor();
                tint.a *= graphic.canvasRenderer.GetInheritedAlpha();
                bool drawn = copy.Copy(graphic, projection * graphic.transform.localToWorldMatrix, tint);
                if (graphic.gameObject == source.mapImage && drawn) terrain = true;
            }
            return terrain;
        }

        public void Dispose()
        {
            foreach (var copy in copies.Values)
                if (copy != null) { copy.gameObject.SetActive(false); UnityEngine.Object.Destroy(copy.gameObject); }
            copies.Clear();
        }
    }
}
