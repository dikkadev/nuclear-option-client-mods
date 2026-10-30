using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    // Copies only native UI geometry. No game controller, map singleton, camera or texture is cloned.
    internal sealed class MapGraphic : MaskableGraphic
    {
        private Mesh? mesh;
        private readonly List<Vector3> source = new List<Vector3>();
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector4> tangents = new List<Vector4>();
        private readonly List<Color32> colors = new List<Color32>();
        private readonly List<int> triangles = new List<int>();
        private readonly List<Vector4>[] uvs = { new List<Vector4>(), new List<Vector4>(), new List<Vector4>(), new List<Vector4>() };
        private Texture? texture, alphaTexture;
        private Color tint = new Color(-1, -1, -1, -1);
        public override Texture mainTexture => texture != null ? texture : Texture2D.whiteTexture;

        /// <summary>Whether the last copy held any vertices.</summary>
        internal bool HasGeometry { get; private set; }
        /// <summary>The transform that produced the current vertices, from the source's local space.</summary>
        internal Affine2 Baked { get; private set; }
        internal float MinX, MinY, MaxX, MaxY;

        // Culling state: bounds come from the mirror, in root-canvas space, so RectMask2D can reject
        // off-screen copies without walking their RectTransforms. Unchanged inputs skip the test.
        private bool shown;
        private Rect bounds;
        private int revision, culledRevision = -1;
        private Rect lastClip;
        private bool lastValid;
        private Vector2 softness = new Vector2(float.NaN, float.NaN);

        protected override void OnPopulateMesh(VertexHelper helper) => helper.Clear();
        protected override void UpdateGeometry()
        {
            // Unity may rebuild this Graphic after the capture callback. Keep the captured mesh.
            if (mesh != null) canvasRenderer.SetMesh(mesh);
            else base.UpdateGeometry();
        }

        /// <summary>Rereads every channel of the source's current mesh and its material.</summary>
        internal bool Copy(Graphic from, in Affine2 transform)
        {
            if (mesh == null) { mesh = new Mesh { name = "NuclearMinimap.NativeGeometry" }; mesh.MarkDynamic(); }
            // This client's GetMesh returns the native mesh, rather than accepting a destination.
            // Treat it as borrowed: never mutate or destroy the source geometry.
            var native = from.canvasRenderer.GetMesh();
            CopyMaterial(from);
            if (native == null || native.vertexCount == 0)
            {
                mesh.Clear(); source.Clear(); HasGeometry = false; Baked = transform;
                canvasRenderer.SetMesh(mesh);
                return false;
            }
            native.GetVertices(source); native.GetColors(colors); native.GetNormals(normals); native.GetTangents(tangents);
            Project(transform);
            mesh.Clear(); mesh.indexFormat = native.indexFormat; mesh.SetVertices(vertices);
            if (colors.Count > 0) mesh.SetColors(colors);
            if (normals.Count > 0) mesh.SetNormals(normals);
            if (tangents.Count > 0) mesh.SetTangents(tangents);
            // Retain all original UV channels (including TMP's signed-distance-field data).
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                native.GetUVs(channel, uvs[channel]);
                if (uvs[channel].Count > 0) mesh.SetUVs(channel, uvs[channel]);
            }
            native.GetTriangles(triangles, 0); mesh.SetTriangles(triangles, 0);
            canvasRenderer.SetMesh(mesh);
            HasGeometry = true;
            return true;
        }

        /// <summary>Moves the cached source vertices to a new transform; other channels stay uploaded.</summary>
        internal void Move(in Affine2 transform)
        {
            if (mesh == null || !HasGeometry) { Baked = transform; return; }
            Project(transform);
            mesh.SetVertices(vertices);
            canvasRenderer.SetMesh(mesh);
        }

        internal void CopyMaterial(Graphic from)
        {
            texture = from.mainTexture;
            if (material != from.material) material = from.material;
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(materialForRendering, 0);
            canvasRenderer.SetTexture(texture);
            var image = from as Image;
            alphaTexture = image != null && image.sprite != null ? image.sprite.associatedAlphaSplitTexture : null;
            canvasRenderer.SetAlphaTexture(alphaTexture);
        }

        // Disabling clears the CanvasRenderer; Unity's own rebuild restores material and texture, not these.
        protected override void UpdateMaterial()
        {
            base.UpdateMaterial();
            if (IsActive()) canvasRenderer.SetAlphaTexture(alphaTexture);
        }

        internal void SetTint(Color value)
        {
            if (value == tint) return;
            tint = value;
            canvasRenderer.SetColor(value);
        }

        private void Project(in Affine2 transform)
        {
            Baked = transform;
            vertices.Clear();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < source.Count; i++)
            {
                var v = source[i];
                float x = transform.X(v.x, v.y, v.z), y = transform.Y(v.x, v.y, v.z);
                vertices.Add(new Vector3(x, y, 0));
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        internal void SetCanvasBounds(bool show, Rect value)
        {
            if (show == shown && (!show || value == bounds)) return;
            shown = show; bounds = value; revision++;
        }

        public override void Cull(Rect clipRect, bool validRect)
        {
            if (revision == culledRevision && validRect == lastValid && clipRect == lastClip) return;
            culledRevision = revision; lastValid = validRect; lastClip = clipRect;
            bool cull = !shown || !validRect || !clipRect.Overlaps(bounds, true);
            if (canvasRenderer.cull == cull) return;
            canvasRenderer.cull = cull;
            onCullStateChanged.Invoke(cull);
            OnCullingChanged();
        }

        public override void SetClipSoftness(Vector2 clipSoftness)
        {
            if (clipSoftness == softness) return;
            softness = clipSoftness;
            base.SetClipSoftness(clipSoftness);
        }

        // Unity changes cull and clipping state itself on these transitions; test again afterwards.
        private void Invalidate() { culledRevision = -1; softness = new Vector2(float.NaN, float.NaN); }
        protected override void OnEnable()
        {
            Invalidate(); base.OnEnable();
            if (tint.r >= 0) canvasRenderer.SetColor(tint);
        }
        protected override void OnDisable() { Invalidate(); base.OnDisable(); }
        protected override void OnTransformParentChanged() { Invalidate(); base.OnTransformParentChanged(); }
        protected override void OnCanvasHierarchyChanged() { Invalidate(); base.OnCanvasHierarchyChanged(); }
        public override void RecalculateClipping() { Invalidate(); base.RecalculateClipping(); }

        protected override void OnDestroy()
        {
            if (mesh != null) Object.Destroy(mesh);
            base.OnDestroy();
        }
    }
}
