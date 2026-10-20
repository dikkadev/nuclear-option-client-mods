using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMfd
{
    public sealed class MfdSurface : IDisposable
    {
        private readonly Dictionary<string, MfdPage> pages = new Dictionary<string, MfdPage>();
        private bool disposed;
        internal bool IsDisposed => disposed;
        public RectTransform Root { get; }
        public string Path { get; }
        public bool IsRecognizedAircraft { get; }
        public bool IsFallback { get; }
        public bool IsCustomMapping { get; }
        public bool IsNativeMapping { get; }
        public string MappingProvenance => IsCustomMapping ? "Consumer-supplied canvas-relative path (checked)" :
            IsNativeMapping ? "Native TacScreen.radarCenter parent (checked)" :
            Path.Length == 0 ? "Exact TacScreen canvas" : MfdLayouts.Provenance;
        internal MfdSurface(RectTransform root, string path, bool recognized, bool fallback, bool custom = false, bool native = false)
        { Root = root; Path = path; IsRecognizedAircraft = recognized; IsFallback = fallback; IsCustomMapping = custom; IsNativeMapping = native; }

        public MfdPage Attach(string id, bool replaceStock = false, bool hideStockDecoration = false)
        {
            if (disposed || !Root) throw new ObjectDisposedException(nameof(MfdSurface));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Page ID is required.", nameof(id));
            if (pages.ContainsKey(id)) throw new InvalidOperationException("Page ID is already attached: " + id);
            var page = new MfdPage(Root, id, replaceStock, hideStockDecoration, () => pages.Remove(id));
            pages.Add(id, page);
            return page;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var page in new List<MfdPage>(pages.Values)) page.Dispose();
            pages.Clear();
        }
    }

    public sealed class MfdPage : IDisposable
    {
        // Only coordinates copies compiled into this assembly, on Unity's main thread.
        private static readonly HashSet<MfdPage> Replacements = new HashSet<MfdPage>();
        private static readonly HashSet<Transform> OwnedRoots = new HashSet<Transform>();
        private readonly RectTransform parent;
        private readonly bool replaceStock, hideDecoration;
        private readonly Action onDisposed;
        private readonly List<KeyValuePair<GameObject, bool>> children = new List<KeyValuePair<GameObject, bool>>();
        private readonly List<KeyValuePair<Behaviour, bool>> behaviours = new List<KeyValuePair<Behaviour, bool>>();
        private bool disposed, replacing;
        public RectTransform Root { get; }

        internal MfdPage(RectTransform parent, string id, bool replaceStock, bool hideDecoration, Action onDisposed)
        {
            this.parent = parent; this.replaceStock = replaceStock; this.hideDecoration = hideDecoration; this.onDisposed = onDisposed;
            if (replaceStock) CheckReplacement();
            var obj = new GameObject("NuclearMfd.Page." + id, typeof(RectTransform));
            Root = (RectTransform)obj.transform;
            Root.SetParent(parent, false);
            // Centered on the parent's local origin, no artboard scale or offsets.
            Root.anchorMin = Root.anchorMax = parent.pivot;
            Root.pivot = parent.pivot;
            Root.sizeDelta = parent.rect.size;
            Root.localPosition = Vector3.zero;
            Root.localRotation = Quaternion.identity;
            Root.localScale = Vector3.one;
            var layout = obj.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            OwnedRoots.Add(Root);
            SetVisible(true);
        }

        private void CheckReplacement()
        {
            foreach (var page in Replacements)
                if (page.parent && (page.parent == parent || page.parent.IsChildOf(parent) || parent.IsChildOf(page.parent)))
                    throw new InvalidOperationException("An overlapping MFD replacement page is already visible.");
        }

        public void SetVisible(bool visible)
        {
            if (disposed) return;
            if (!Root || !parent) { Dispose(); return; }
            if (replaceStock && visible && !replacing)
            {
                CheckReplacement();
                foreach (Transform child in parent)
                {
                    if (OwnedRoots.Contains(child)) continue;
                    children.Add(new KeyValuePair<GameObject, bool>(child.gameObject, child.gameObject.activeSelf));
                    child.gameObject.SetActive(false);
                }
                foreach (var layout in parent.GetComponents<LayoutGroup>()) SaveDisable(layout);
                foreach (var fitter in parent.GetComponents<ContentSizeFitter>()) SaveDisable(fitter);
                if (hideDecoration) foreach (var image in parent.GetComponents<Image>()) SaveDisable(image);
                replacing = true;
                Replacements.Add(this);
            }
            else if (!visible) Restore();
            Root.gameObject.SetActive(visible);
        }

        private void SaveDisable(Behaviour component)
        { behaviours.Add(new KeyValuePair<Behaviour, bool>(component, component.enabled)); component.enabled = false; }

        private void Restore()
        {
            if (!replacing) return;
            foreach (var item in children) if (item.Key) item.Key.SetActive(item.Value);
            foreach (var item in behaviours) if (item.Key) item.Key.enabled = item.Value;
            children.Clear(); behaviours.Clear();
            replacing = false;
            Replacements.Remove(this);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Restore();
            OwnedRoots.Remove(Root);
            if (Root) { Root.gameObject.SetActive(false); UnityEngine.Object.Destroy(Root.gameObject); }
            onDisposed();
        }
    }
}
