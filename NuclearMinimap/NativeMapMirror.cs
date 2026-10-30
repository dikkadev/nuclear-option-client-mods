using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace NuclearMinimap
{
    /// <summary>
    /// Copies the native minimized map's graphics onto the cockpit page.
    /// Each copy holds its source's geometry relative to the nearest map layer (map image, icon layer,
    /// info layer, or the viewport itself). A container per layer carries that layer's pan, rotation
    /// and zoom, so a moving map changes a few container transforms rather than every mesh.
    /// Source meshes are reread only after the source reports a vertex or material change, plus a slow
    /// round-robin refresh for anything that changes its mesh without reporting it.
    /// </summary>
    internal sealed class NativeMapMirror : IDisposable
    {
        private const int CapturesPerScan = 4;         // membership and order at 5 Hz
        private const int CapturesPerFullRefresh = 20;  // every copy is reread from its source once per second

        private sealed class Entry
        {
            internal Graphic Source = null!;
            internal RectTransform SourceTransform = null!;
            internal MapGraphic Copy = null!;
            internal Transform? Parent;
            internal int Anchor;
            internal int Segment = -1;
            internal int Seen;
            internal int Dirty = 2;            // captures that must reread geometry (one extra for late rebuilds)
            internal bool MaterialDirty = true;
            internal bool StateDirty = true;
            internal bool Active;
            internal bool AlwaysReread;        // TMP submeshes are rebuilt by their parent without reporting it
            internal bool Polled;              // non-maskable graphics report neither disabling nor destruction
            internal bool IsTerrain;
            internal UnityAction OnVertices = null!, OnMaterial = null!;
            internal UnityAction<bool>? OnCull;
        }

        private sealed class Segment
        {
            internal RectTransform Root = null!;
            internal int Anchor;
            internal Affine2 ToCanvas;
            internal Vector3 Position = new Vector3(float.NaN, 0, 0);
            internal float Degrees = float.NaN;
            internal Vector2 Scale;
        }

        private readonly DynamicMap source;
        private readonly RectTransform viewport, root, layers;
        private readonly Transform?[] anchors;
        private readonly Matrix4x4[] anchorWorldToLocal;
        private readonly Affine2[] anchorPose;
        private readonly bool[] anchorFlat;
        private readonly Dictionary<Graphic, Entry> entries = new Dictionary<Graphic, Entry>(ReferenceComparer.Instance);
        private readonly List<Graphic> scanned = new List<Graphic>();
        private readonly List<Entry> order = new List<Entry>(), previousOrder = new List<Entry>(), removed = new List<Entry>();
        private readonly List<int> previousAnchors = new List<int>();
        private readonly List<Segment> segments = new List<Segment>();
        private Canvas? canvas;
        private int captures, stamp, refreshCursor;
        private bool rescanRequested = true;
        private Vector3 layersPosition = new Vector3(float.NaN, 0, 0);
        private Vector3 layersScale;
        internal int GraphicCount => order.Count;

        internal NativeMapMirror(DynamicMap source, RectTransform root)
        {
            this.source = source; this.root = root;
            viewport = (RectTransform)source.transform;
            var clip = root.gameObject.AddComponent<RectMask2D>();
            var nativeClip = source.GetComponent<RectMask2D>();
            if (nativeClip != null) clip.softness = nativeClip.softness;
            layers = NewRect("NuclearMinimap.Layers", root);
            anchors = new[] { viewport, source.mapImage != null ? source.mapImage.transform : null,
                source.iconLayer != null ? source.iconLayer.transform : null, source.infoLayer != null ? source.infoLayer.transform : null };
            anchorWorldToLocal = new Matrix4x4[anchors.Length];
            anchorPose = new Affine2[anchors.Length];
            anchorFlat = new bool[anchors.Length];
        }

        internal bool SourceReady => source != null && source.isActiveAndEnabled && viewport != null &&
            source.mapImage != null && source.mapImage.activeInHierarchy && viewport.rect.width > 0 && viewport.rect.height > 0;

        internal bool Capture()
        {
            if (!SourceReady || DynamicMap.mapMaximized) return false;
            captures++;
            bool slow = rescanRequested || captures % CapturesPerScan == 0;
            if (slow) Rescan();
            PlaceLayers();

            int refreshCount = (order.Count + CapturesPerFullRefresh - 1) / CapturesPerFullRefresh;
            int refreshFrom = refreshCursor, refreshTo = refreshCursor + refreshCount;
            refreshCursor = order.Count == 0 ? 0 : refreshTo % order.Count;
            bool terrain = false;
            for (int i = 0; i < order.Count; i++)
            {
                var entry = order[i];
                bool scheduled = (i >= refreshFrom && i < refreshTo) || (refreshTo > order.Count && i < refreshTo - order.Count);
                if (entry.Polled) entry.StateDirty = true;
                if (scheduled) { entry.StateDirty = true; entry.MaterialDirty = true; if (entry.Dirty < 1) entry.Dirty = 1; }
                if (entry.StateDirty)
                {
                    entry.StateDirty = false;
                    if (!entry.Source) { entry.Active = false; rescanRequested = true; }
                    else entry.Active = entry.Source.isActiveAndEnabled && !entry.Source.canvasRenderer.cull;
                }
                var copy = entry.Copy;
                if (!entry.Active || entry.Segment < 0) { copy.SetCanvasBounds(false, default); continue; }

                var relative = Affine2Of(anchorWorldToLocal[entry.Anchor] * entry.SourceTransform.localToWorldMatrix);
                if (anchorFlat[entry.Anchor]) relative = anchorPose[entry.Anchor].After(relative);
                if (entry.Dirty > 0 || entry.AlwaysReread)
                {
                    entry.Dirty--; entry.MaterialDirty = false;
                    copy.Copy(entry.Source, relative);
                }
                else
                {
                    if (entry.MaterialDirty) { entry.MaterialDirty = false; copy.CopyMaterial(entry.Source); }
                    if (!copy.Baked.Near(relative)) copy.Move(relative);
                }
                if (slow || scheduled)
                {
                    var tint = entry.Source.canvasRenderer.GetColor();
                    tint.a *= entry.Source.canvasRenderer.GetInheritedAlpha();
                    copy.SetTint(tint);
                }
                if (!copy.HasGeometry) { copy.SetCanvasBounds(false, default); continue; }
                segments[entry.Segment].ToCanvas.Bounds(copy.MinX, copy.MinY, copy.MaxX, copy.MaxY, out float x0, out float y0, out float x1, out float y1);
                copy.SetCanvasBounds(true, Rect.MinMaxRect(x0, y0, x1, y1));
                if (entry.IsTerrain) terrain = true;
            }
            return terrain;
        }

        private void Rescan()
        {
            rescanRequested = false;
            stamp++;
            scanned.Clear();
            source.GetComponentsInChildren(false, scanned);
            order.Clear();
            foreach (var graphic in scanned)
            {
                if (graphic == null) continue;
                if (!entries.TryGetValue(graphic, out var entry)) { entry = Create(graphic); entries.Add(graphic, entry); }
                entry.Seen = stamp;
                var parent = entry.SourceTransform.parent;
                if (!ReferenceEquals(parent, entry.Parent)) { entry.Parent = parent; entry.Anchor = FindAnchor(entry.SourceTransform); }
                order.Add(entry);
            }
            removed.Clear();
            foreach (var entry in entries.Values)
                if (entry.Seen != stamp) removed.Add(entry);
            foreach (var entry in removed) { entries.Remove(entry.Source); Release(entry); }
            if (!SameLayout()) ApplyLayout();
        }

        private Entry Create(Graphic graphic)
        {
            var copy = NewRect("Native " + graphic.name, layers).gameObject.AddComponent<MapGraphic>();
            copy.raycastTarget = false;
            var entry = new Entry
            {
                Source = graphic, SourceTransform = graphic.rectTransform, Copy = copy, Parent = graphic.rectTransform.parent,
                AlwaysReread = graphic.GetType().Name == "TMP_SubMeshUI",
                Polled = !(graphic is MaskableGraphic),
                IsTerrain = ReferenceEquals(graphic.gameObject, source.mapImage),
            };
            entry.Anchor = FindAnchor(entry.SourceTransform);
            entry.OnVertices = () => { entry.Dirty = 2; entry.StateDirty = true; };
            entry.OnMaterial = () => { entry.MaterialDirty = true; entry.StateDirty = true; };
            graphic.RegisterDirtyVerticesCallback(entry.OnVertices);
            graphic.RegisterDirtyMaterialCallback(entry.OnMaterial);
            if (graphic is MaskableGraphic maskable)
            {
                entry.OnCull = _ => entry.StateDirty = true;
                maskable.onCullStateChanged.AddListener(entry.OnCull);
            }
            return entry;
        }

        private void Release(Entry entry)
        {
            if (entry.Source)
            {
                entry.Source.UnregisterDirtyVerticesCallback(entry.OnVertices);
                entry.Source.UnregisterDirtyMaterialCallback(entry.OnMaterial);
                if (entry.OnCull != null && entry.Source is MaskableGraphic maskable) maskable.onCullStateChanged.RemoveListener(entry.OnCull);
            }
            if (entry.Copy != null) { entry.Copy.gameObject.SetActive(false); UnityEngine.Object.Destroy(entry.Copy.gameObject); }
        }

        private int FindAnchor(Transform transform)
        {
            for (var current = transform; current != null && !ReferenceEquals(current, viewport); current = current.parent)
                for (int i = 1; i < anchors.Length; i++)
                    if (anchors[i] != null && ReferenceEquals(current, anchors[i])) return i;
            return 0;
        }

        private bool SameLayout()
        {
            if (order.Count != previousOrder.Count) return false;
            for (int i = 0; i < order.Count; i++)
                if (!ReferenceEquals(order[i], previousOrder[i]) || order[i].Anchor != previousAnchors[i]) return false;
            return true;
        }

        // Native nested canvases draw in hierarchy order; keep it, including new TMP submeshes.
        // Consecutive graphics on the same layer share one container, so draw order is preserved.
        private void ApplyLayout()
        {
            int count = 0;
            for (int i = 0; i < order.Count; i++)
            {
                var entry = order[i];
                if (count == 0 || segments[count - 1].Anchor != entry.Anchor)
                {
                    if (count == segments.Count) segments.Add(new Segment { Root = NewRect("NuclearMinimap.Layer", layers) });
                    var segment = segments[count++];
                    segment.Anchor = entry.Anchor;
                    segment.Degrees = float.NaN; // force its pose to be written for the new layer
                }
                entry.Segment = count - 1;
                var target = segments[count - 1].Root;
                var copyTransform = entry.Copy.rectTransform;
                if (!ReferenceEquals(copyTransform.parent, target)) { copyTransform.SetParent(target, false); ResetPose(copyTransform); }
            }
            for (int i = segments.Count - 1; i >= count; i--)
            {
                UnityEngine.Object.Destroy(segments[i].Root.gameObject);
                segments.RemoveAt(i);
            }
            for (int i = 0; i < segments.Count; i++)
                if (segments[i].Root.GetSiblingIndex() != i) segments[i].Root.SetSiblingIndex(i);
            int index = 0, segmentIndex = -1;
            foreach (var entry in order)
            {
                if (entry.Segment != segmentIndex) { segmentIndex = entry.Segment; index = 0; }
                var copyTransform = entry.Copy.rectTransform;
                if (copyTransform.GetSiblingIndex() != index) copyTransform.SetSiblingIndex(index);
                index++;
            }
            previousOrder.Clear(); previousOrder.AddRange(order);
            previousAnchors.Clear();
            foreach (var entry in order) previousAnchors.Add(entry.Anchor);
        }

        // The layers container maps viewport-local space onto the cockpit rectangle; each segment then
        // carries its map layer's pose inside the viewport. A layer whose pose has skew is baked into the
        // copies instead ("flat"), which is correct but rewrites those meshes whenever the map moves.
        private void PlaceLayers()
        {
            var from = viewport.rect; var to = root.rect;
            var position = new Vector3(
                MapPresentation.MapCoordinate(0, from.xMin, from.width, to.xMin, to.width),
                MapPresentation.MapCoordinate(0, from.yMin, from.height, to.yMin, to.height), 0);
            var scale = new Vector3(to.width / from.width, to.height / from.height, 1);
            if (position != layersPosition) { layersPosition = position; layers.localPosition = position; }
            if (scale != layersScale) { layersScale = scale; layers.localScale = scale; }

            var viewWorldToLocal = viewport.worldToLocalMatrix;
            anchorWorldToLocal[0] = viewWorldToLocal; anchorPose[0] = Affine2.Identity; anchorFlat[0] = false;
            for (int i = 1; i < anchors.Length; i++)
            {
                var anchor = anchors[i];
                if (anchor == null) continue;
                anchorWorldToLocal[i] = anchor.worldToLocalMatrix;
                anchorPose[i] = Affine2Of(viewWorldToLocal * anchor.localToWorldMatrix);
                anchorFlat[i] = !anchorPose[i].TryDecompose(out _, out _, out _);
            }

            if (canvas == null) canvas = root.GetComponentInParent<Canvas>()?.rootCanvas;
            var toCanvas = canvas != null ? canvas.transform.worldToLocalMatrix : Matrix4x4.identity;
            foreach (var segment in segments)
            {
                var pose = anchorPose[segment.Anchor];
                Vector3 segmentPosition; float degrees; Vector2 segmentScale;
                if (anchorFlat[segment.Anchor] || !pose.TryDecompose(out degrees, out float sx, out float sy))
                { segmentPosition = Vector3.zero; degrees = 0; segmentScale = Vector2.one; }
                else { segmentPosition = new Vector3(pose.Tx, pose.Ty, 0); segmentScale = new Vector2(sx, sy); }
                if (segmentPosition != segment.Position) { segment.Position = segmentPosition; segment.Root.localPosition = segmentPosition; }
                if (degrees != segment.Degrees) { segment.Degrees = degrees; segment.Root.localRotation = Quaternion.Euler(0, 0, degrees); }
                if (segmentScale != segment.Scale) { segment.Scale = segmentScale; segment.Root.localScale = new Vector3(segmentScale.x, segmentScale.y, 1); }
                segment.ToCanvas = Affine2Of(toCanvas * segment.Root.localToWorldMatrix);
            }
        }

        private static Affine2 Affine2Of(in Matrix4x4 m) => new Affine2(m.m00, m.m10, m.m01, m.m11, m.m02, m.m12, m.m03, m.m13);

        private static RectTransform NewRect(string name, Transform parent)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.gameObject.layer = parent.gameObject.layer;
            rect.SetParent(parent, false);
            ResetPose(rect);
            return rect;
        }

        private static void ResetPose(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = Vector2.zero;
            rect.localPosition = Vector3.zero; rect.localRotation = Quaternion.identity; rect.localScale = Vector3.one;
        }

        // Destroyed Unity objects compare equal to each other; key copies by the exact source instance.
        private sealed class ReferenceComparer : IEqualityComparer<Graphic>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public bool Equals(Graphic? x, Graphic? y) => ReferenceEquals(x, y);
            public int GetHashCode(Graphic obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        public void Dispose()
        {
            foreach (var entry in entries.Values) Release(entry);
            entries.Clear(); order.Clear(); previousOrder.Clear(); segments.Clear();
            if (layers != null) UnityEngine.Object.Destroy(layers.gameObject);
        }
    }
}
