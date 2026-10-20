using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    // Copies only native UI geometry. No game controller, map singleton, camera or texture is cloned.
    internal sealed class MapGraphic : MaskableGraphic
    {
        private Mesh? mesh;
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector4> tangents = new List<Vector4>();
        private readonly List<Color32> colors = new List<Color32>();
        private readonly List<int> triangles = new List<int>();
        private readonly List<Vector4>[] uvs = { new List<Vector4>(), new List<Vector4>(), new List<Vector4>(), new List<Vector4>() };
        private Texture? texture;
        public override Texture mainTexture => texture != null ? texture : Texture2D.whiteTexture;

        protected override void OnPopulateMesh(VertexHelper helper) => helper.Clear();
        protected override void UpdateGeometry()
        {
            // Unity may rebuild this Graphic after the capture callback. Keep the captured mesh.
            if (mesh != null) canvasRenderer.SetMesh(mesh);
            else base.UpdateGeometry();
        }

        internal bool Copy(Graphic source, Matrix4x4 transform, Color tint)
        {
            if (mesh == null) { mesh = new Mesh { name = "NuclearMinimap.NativeGeometry" }; mesh.MarkDynamic(); }
            // This client's GetMesh returns the native mesh, rather than accepting a destination.
            // Treat it as borrowed: never mutate or destroy the source geometry.
            var native = source.canvasRenderer.GetMesh();
            if (native == null || native.vertexCount == 0) { Clear(); return false; }
            native.GetVertices(vertices); native.GetColors(colors); native.GetNormals(normals); native.GetTangents(tangents);
            for (int i = 0; i < vertices.Count; i++)
            {
                var value = transform.MultiplyPoint3x4(vertices[i]);
                value.z = 0;
                vertices[i] = value;
            }
            mesh.Clear(); mesh.indexFormat = native.indexFormat; mesh.SetVertices(vertices);
            if (colors.Count > 0) mesh.SetColors(colors);
            if (normals.Count > 0) mesh.SetNormals(normals);
            if (tangents.Count > 0) mesh.SetTangents(tangents);
            for (int channel = 0; channel < uvs.Length; channel++)
            {
                native.GetUVs(channel, uvs[channel]);
                if (uvs[channel].Count > 0) mesh.SetUVs(channel, uvs[channel]);
            }
            native.GetTriangles(triangles, 0); mesh.SetTriangles(triangles, 0);
            // Retain all original UV channels (including TMP's signed-distance-field data).
            texture = source.mainTexture;
            if (material != source.material) material = source.material;
            canvasRenderer.materialCount = 1;
            canvasRenderer.SetMaterial(materialForRendering, 0);
            canvasRenderer.SetTexture(texture);
            var image = source as Image;
            canvasRenderer.SetAlphaTexture(image != null && image.sprite != null ? image.sprite.associatedAlphaSplitTexture : null);
            canvasRenderer.SetColor(tint);
            canvasRenderer.SetMesh(mesh);
            return true;
        }

        internal void Clear() { if (mesh != null) mesh.Clear(); canvasRenderer.Clear(); }
        protected override void OnDestroy()
        {
            if (mesh != null) Object.Destroy(mesh);
            base.OnDestroy();
        }
    }
}
