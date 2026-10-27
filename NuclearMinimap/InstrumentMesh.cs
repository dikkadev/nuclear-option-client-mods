using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    // Untextured vector geometry in this Graphic's local space. Callers rebuild it each frame.
    internal sealed class InstrumentMesh : MaskableGraphic
    {
        private readonly List<UIVertex> vertices = new List<UIVertex>();
        private readonly List<int> indices = new List<int>();

        internal void Begin() { vertices.Clear(); indices.Clear(); }
        internal void End() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();
            if (vertices.Count > 0) helper.AddUIVertexStream(vertices, indices);
        }

        private int Vertex(Vector2 position, Color32 color)
        {
            var value = UIVertex.simpleVert;
            value.position = position; value.color = color;
            vertices.Add(value);
            return vertices.Count - 1;
        }

        internal void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 color)
        {
            int i = Vertex(a, color); Vertex(b, color); Vertex(c, color);
            indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
        }

        internal void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 color)
        {
            int i = Vertex(a, color); Vertex(b, color); Vertex(c, color); Vertex(d, color);
            indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
            indices.Add(i); indices.Add(i + 2); indices.Add(i + 3);
        }

        internal void Box(Rect rect, Color32 color) =>
            Quad(new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMin, rect.yMax), new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMax, rect.yMin), color);

        internal void Outline(Rect rect, float width, Color32 color)
        {
            Box(new Rect(rect.xMin, rect.yMin, rect.width, width), color);
            Box(new Rect(rect.xMin, rect.yMax - width, rect.width, width), color);
            Box(new Rect(rect.xMin, rect.yMin + width, width, rect.height - 2 * width), color);
            Box(new Rect(rect.xMax - width, rect.yMin + width, width, rect.height - 2 * width), color);
        }

        internal void Line(Vector2 a, Vector2 b, float width, Color32 color)
        {
            var direction = b - a;
            if (direction.sqrMagnitude < 1e-8f) return;
            var side = new Vector2(-direction.y, direction.x).normalized * (width * .5f);
            Quad(a - side, a + side, b + side, b - side, color);
        }

        internal void Polygon(List<(float x, float y)> convex, Vector2 offset, Color32 color)
        {
            if (convex.Count < 3) return;
            int first = vertices.Count;
            foreach (var point in convex) Vertex(new Vector2(point.x, point.y) + offset, color);
            for (int i = 1; i < convex.Count - 1; i++) { indices.Add(first); indices.Add(first + i); indices.Add(first + i + 1); }
        }

        internal void Ring(Vector2 center, float inner, float outer, int segments, Color32 color)
        {
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2 / segments, b = (i + 1) * Mathf.PI * 2 / segments;
                var da = new Vector2(Mathf.Cos(a), Mathf.Sin(a)); var db = new Vector2(Mathf.Cos(b), Mathf.Sin(b));
                Quad(center + da * inner, center + da * outer, center + db * outer, center + db * inner, color);
            }
        }

        internal void Disc(Vector2 center, float radius, int segments, Color32 color) => Ring(center, 0, radius, segments, color);
    }
}
