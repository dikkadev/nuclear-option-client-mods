using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMapMarkings
{
    // Segments, ticks, dots and rings in native icon-layer coordinates; widths are UI pixels at any zoom.
    internal sealed class MarkingsGraphic : MaskableGraphic
    {
        private const int MaxVertices = 60000;
        private const int MaxRingDashes = 2048;
        private const int MaxRingSteps = 2048;

        private enum Kind
        {
            Segment,
            Band,
            Tick,
            Dot,
            Ring,
        }

        private struct Primitive
        {
            internal Kind Kind;
            internal Vector2 A;
            internal Vector2 B;
            internal float Size;
            internal float Radius;
            internal float Dash;
            internal float Gap;
            internal Color32 Color;
        }

        private readonly List<Primitive> primitives = new List<Primitive>();
        private float extent;

        internal void Begin()
        {
            primitives.Clear();
            extent = 0f;
        }

        // Straight line; dash/gap > 0 draws it dashed. Lengths in UI pixels.
        internal void Line(Vector2 a, Vector2 b, float widthPixels, Color color, float dashPixels = 0f, float gapPixels = 0f)
            => Add(new Primitive { Kind = Kind.Segment, A = a, B = b, Size = widthPixels, Dash = dashPixels, Gap = gapPixels, Color = color });

        // Line with a width in layer units, never thinner than minimumPixels.
        internal void Band(Vector2 a, Vector2 b, float layerWidth, float minimumPixels, Color color)
            => Add(new Primitive { Kind = Kind.Band, A = a, B = b, Size = layerWidth, Dash = minimumPixels, Color = color });

        // Cross tick centred on a point, perpendicular to direction.
        internal void Tick(Vector2 center, Vector2 direction, float halfLengthPixels, float widthPixels, Color color)
            => Add(new Primitive { Kind = Kind.Tick, A = center, B = direction, Size = halfLengthPixels, Dash = widthPixels, Color = color });

        internal void Dot(Vector2 center, float radiusPixels, Color color)
            => Add(new Primitive { Kind = Kind.Dot, A = center, Size = radiusPixels, Color = color });

        // Circle with a radius in layer units; dash/gap > 0 draws it dashed, evenly spaced all the way round.
        internal void Ring(Vector2 center, float radius, float widthPixels, Color color, float dashPixels = 0f, float gapPixels = 0f)
            => Add(new Primitive { Kind = Kind.Ring, A = center, Radius = radius, Size = widthPixels, Dash = dashPixels, Gap = gapPixels, Color = color });

        internal void End()
        {
            // RectMask2D culls by this rect, not the mesh, so it must enclose every vertex.
            var size = Vector2.one * (2f * extent + 256f);
            if ((rectTransform.sizeDelta - size).sqrMagnitude > 1f)
            {
                rectTransform.sizeDelta = size;
            }

            SetVerticesDirty();
        }

        private void Add(Primitive primitive)
        {
            primitives.Add(primitive);
            extent = Mathf.Max(extent, Mathf.Abs(primitive.A.x) + primitive.Radius, Mathf.Abs(primitive.A.y) + primitive.Radius);
            if (primitive.Kind == Kind.Segment || primitive.Kind == Kind.Band)
            {
                extent = Mathf.Max(extent, Mathf.Abs(primitive.B.x), Mathf.Abs(primitive.B.y));
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float scale = transform.lossyScale.x;
            if (scale <= 0f || canvas == null)
            {
                return;
            }

            float pixel = canvas.scaleFactor / scale;
            foreach (var primitive in primitives)
            {
                if (vh.currentVertCount > MaxVertices)
                {
                    break;
                }

                switch (primitive.Kind)
                {
                    case Kind.Segment:
                        AddSegment(vh, primitive, pixel);
                        break;
                    case Kind.Band:
                        AddQuad(vh, primitive.A, primitive.B, 0.5f * Mathf.Max(primitive.Size, primitive.Dash * pixel), primitive.Color);
                        break;
                    case Kind.Tick:
                        var normal = new Vector2(-primitive.B.y, primitive.B.x).normalized * (primitive.Size * pixel);
                        AddQuad(vh, primitive.A - normal, primitive.A + normal, 0.5f * primitive.Dash * pixel, primitive.Color);
                        break;
                    case Kind.Dot:
                        float radius = primitive.Size * pixel;
                        AddQuad(vh, primitive.A - new Vector2(radius, 0f), primitive.A + new Vector2(radius, 0f), radius, primitive.Color);
                        break;
                    case Kind.Ring:
                        AddRing(vh, primitive, pixel);
                        break;
                }
            }
        }

        private static void AddRing(VertexHelper vh, Primitive primitive, float pixel)
        {
            float radiusPixels = primitive.Radius / pixel;
            if (!(radiusPixels > 0f) || float.IsInfinity(radiusPixels))
            {
                return;
            }

            float halfWidth = 0.5f * primitive.Size * pixel;
            // Steps short enough that a chord sits within a quarter pixel of the circle.
            float step = Mathf.Clamp(Mathf.Sqrt(2f / radiusPixels), 2f * Mathf.PI / MaxRingSteps, 2f * Mathf.PI / 32f);
            int dashes = MarkingsGeometry.RingDashCount(2f * Mathf.PI * radiusPixels, primitive.Dash, primitive.Gap, MaxRingDashes);
            if (dashes == 0)
            {
                AddArc(vh, primitive, halfWidth, 0f, 2f * Mathf.PI, Mathf.CeilToInt(2f * Mathf.PI / step));
                return;
            }

            float period = 2f * Mathf.PI / dashes;
            float dashAngle = period * primitive.Dash / (primitive.Dash + primitive.Gap);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dashAngle / step));
            for (int i = 0; i < dashes && vh.currentVertCount <= MaxVertices; i++)
            {
                AddArc(vh, primitive, halfWidth, i * period, i * period + dashAngle, steps);
            }
        }

        // One strip from the inner to the outer edge, so translucent rings have no brighter overlaps.
        private static void AddArc(VertexHelper vh, Primitive primitive, float halfWidth, float from, float to, int steps)
        {
            float inner = Mathf.Max(0f, primitive.Radius - halfWidth);
            float outer = primitive.Radius + halfWidth;
            int first = vh.currentVertCount;
            for (int k = 0; k <= steps; k++)
            {
                float angle = Mathf.Lerp(from, to, (float)k / steps);
                var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                vh.AddVert(primitive.A + direction * inner, primitive.Color, Vector2.zero);
                vh.AddVert(primitive.A + direction * outer, primitive.Color, Vector2.zero);
                if (k > 0)
                {
                    int v = first + 2 * k;
                    vh.AddTriangle(v - 2, v - 1, v + 1);
                    vh.AddTriangle(v - 2, v + 1, v);
                }
            }
        }

        private static void AddSegment(VertexHelper vh, Primitive primitive, float pixel)
        {
            float halfWidth = 0.5f * primitive.Size * pixel;
            if (primitive.Dash <= 0f || primitive.Gap <= 0f)
            {
                AddQuad(vh, primitive.A, primitive.B, halfWidth, primitive.Color);
                return;
            }

            var delta = primitive.B - primitive.A;
            float length = delta.magnitude;
            float dash = primitive.Dash * pixel;
            float period = dash + primitive.Gap * pixel;
            if (length < 1e-6f || period <= 0f)
            {
                return;
            }

            // Too many dashes at this zoom: a solid line reads the same and stays cheap.
            if (length / period > 400f)
            {
                AddQuad(vh, primitive.A, primitive.B, halfWidth, primitive.Color);
                return;
            }

            var direction = delta / length;
            for (float start = 0f; start < length; start += period)
            {
                float end = Mathf.Min(length, start + dash);
                AddQuad(vh, primitive.A + direction * start, primitive.A + direction * end, halfWidth, primitive.Color);
            }
        }

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, float halfWidth, Color32 color)
        {
            var direction = b - a;
            if (direction.sqrMagnitude < 1e-12f)
            {
                direction = Vector2.right;
            }

            var normal = new Vector2(-direction.y, direction.x).normalized * halfWidth;
            int first = vh.currentVertCount;
            vh.AddVert(a + normal, color, Vector2.zero);
            vh.AddVert(a - normal, color, Vector2.zero);
            vh.AddVert(b - normal, color, Vector2.zero);
            vh.AddVert(b + normal, color, Vector2.zero);
            vh.AddTriangle(first, first + 1, first + 2);
            vh.AddTriangle(first, first + 2, first + 3);
        }
    }
}
