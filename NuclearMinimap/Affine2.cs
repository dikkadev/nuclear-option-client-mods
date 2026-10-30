using System;

namespace NuclearMinimap
{
    /// <summary>
    /// The planar part of a UI transform: x' = A x + C y + E z + Tx, y' = B x + D y + F z + Ty.
    /// The cockpit mirror bakes each source graphic relative to its map layer with this, and gives the
    /// layer's own pan/rotate/zoom to a container transform, so a moving map does not rewrite every mesh.
    /// </summary>
    internal readonly struct Affine2
    {
        internal readonly float A, B, C, D, E, F, Tx, Ty;

        internal Affine2(float a, float b, float c, float d, float e, float f, float tx, float ty)
        {
            A = a; B = b; C = c; D = d; E = e; F = f; Tx = tx; Ty = ty;
        }

        internal static readonly Affine2 Identity = new Affine2(1, 0, 0, 1, 0, 0, 0, 0);

        internal float X(float x, float y, float z) => A * x + C * y + E * z + Tx;
        internal float Y(float x, float y, float z) => B * x + D * y + F * z + Ty;

        /// <summary>This transform applied after <paramref name="inner"/>; inner's depth output is flattened.</summary>
        internal Affine2 After(in Affine2 inner) => new Affine2(
            A * inner.A + C * inner.B, B * inner.A + D * inner.B,
            A * inner.C + C * inner.D, B * inner.C + D * inner.D,
            A * inner.E + C * inner.F, B * inner.E + D * inner.F,
            A * inner.Tx + C * inner.Ty + Tx, B * inner.Tx + D * inner.Ty + Ty);

        /// <summary>
        /// Equal within float noise. Re-deriving an unchanged layer-relative transform through world space
        /// differs in the last bits each frame; real moves exceed these bounds.
        /// </summary>
        internal bool Near(in Affine2 other)
        {
            float linear = 1e-5f * (Math.Abs(A) + Math.Abs(B) + Math.Abs(C) + Math.Abs(D) + 1e-6f);
            return Math.Abs(A - other.A) <= linear && Math.Abs(B - other.B) <= linear &&
                Math.Abs(C - other.C) <= linear && Math.Abs(D - other.D) <= linear &&
                Math.Abs(E - other.E) <= linear && Math.Abs(F - other.F) <= linear &&
                Math.Abs(Tx - other.Tx) <= 1e-3f && Math.Abs(Ty - other.Ty) <= 1e-3f;
        }

        /// <summary>Rotation then per-axis scale, as a Transform can hold. Fails for skew.</summary>
        internal bool TryDecompose(out float degrees, out float scaleX, out float scaleY)
        {
            scaleX = (float)Math.Sqrt(A * A + B * B);
            degrees = 0; scaleY = 0;
            if (!(scaleX > 1e-12f) || float.IsInfinity(scaleX)) return false;
            float cos = A / scaleX, sin = B / scaleX;
            scaleY = -sin * C + cos * D;
            float shear = cos * C + sin * D;
            if (!(Math.Abs(scaleY) > 1e-12f) || Math.Abs(shear) > 1e-4f * Math.Max(scaleX, Math.Abs(scaleY))) return false;
            degrees = (float)(Math.Atan2(sin, cos) * (180.0 / Math.PI));
            return true;
        }

        /// <summary>Axis-aligned bounds of a transformed rectangle lying at depth zero.</summary>
        internal void Bounds(float minX, float minY, float maxX, float maxY,
            out float outMinX, out float outMinY, out float outMaxX, out float outMaxY)
        {
            float x0 = A * minX + C * minY + Tx, y0 = B * minX + D * minY + Ty;
            float x1 = A * maxX + C * minY + Tx, y1 = B * maxX + D * minY + Ty;
            float x2 = A * minX + C * maxY + Tx, y2 = B * minX + D * maxY + Ty;
            float x3 = A * maxX + C * maxY + Tx, y3 = B * maxX + D * maxY + Ty;
            outMinX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            outMaxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            outMinY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            outMaxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        }
    }
}
