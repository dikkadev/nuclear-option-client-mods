using NuclearMinimap;

internal static class MirrorMathChecks
{
    internal static void Run(Action<string, Action> test)
    {
        void Near(float expected, float actual)
        {
            if (!float.IsFinite(actual) || Math.Abs(expected - actual) > .001f)
                throw new Exception($"Expected {expected}, got {actual}");
        }
        void True(bool value, string what) { if (!value) throw new Exception(what); }

        Affine2 Pose(float degrees, float sx, float sy, float tx, float ty)
        {
            double r = degrees * Math.PI / 180;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return new Affine2(c * sx, s * sx, -s * sy, c * sy, 0, 0, tx, ty);
        }

        test("mirror layer pose decomposes into rotation and per-axis scale", () => {
            var pose = Pose(37, 2.5f, 0.75f, 10, -4);
            True(pose.TryDecompose(out float degrees, out float sx, out float sy), "decompose");
            Near(37, degrees); Near(2.5f, sx); Near(0.75f, sy);
        });
        test("mirror layer pose keeps a mirrored axis", () => {
            var pose = Pose(-120, 1, -2, 0, 0);
            True(pose.TryDecompose(out float degrees, out float sx, out float sy), "decompose");
            Near(-120, degrees); Near(1, sx); Near(-2, sy);
        });
        test("mirror skewed layer falls back to baking", () => {
            // Non-uniform scale applied after a rotation cannot be held by one Transform.
            var skew = new Affine2(2, 0, 0, 1, 0, 0, 0, 0).After(Pose(30, 1, 1, 0, 0));
            True(!skew.TryDecompose(out _, out _, out _), "skew must not decompose");
            True(!new Affine2(0, 0, 0, 1, 0, 0, 0, 0).TryDecompose(out _, out _, out _), "degenerate must not decompose");
        });
        test("mirror container pose times baked geometry equals the direct transform", () => {
            var layer = Pose(25, 3, 3, 40, -12);
            var relative = new Affine2(0.5f, 0.1f, -0.1f, 0.5f, 0, 0, 7, 9);
            var combined = layer.After(relative);
            float x = 3, y = -2;
            float bx = relative.X(x, y, 0), by = relative.Y(x, y, 0);
            Near(layer.X(bx, by, 0), combined.X(x, y, 0));
            Near(layer.Y(bx, by, 0), combined.Y(x, y, 0));
        });
        test("mirror change detection ignores float noise but not real moves", () => {
            var a = Pose(10, 1, 1, 300, -200);
            var noise = new Affine2(a.A + 1e-7f, a.B, a.C, a.D - 1e-7f, 0, 0, a.Tx + 1e-5f, a.Ty - 1e-5f);
            True(a.Near(noise), "noise is unchanged");
            True(!a.Near(new Affine2(a.A, a.B, a.C, a.D, 0, 0, a.Tx + 0.01f, a.Ty)), "translation moved");
            True(!a.Near(Pose(10.1f, 1, 1, 300, -200)), "rotation moved");
            True(!a.Near(Pose(10, 1.001f, 1.001f, 300, -200)), "scale moved");
        });
        test("mirror cull bounds cover a rotated rectangle", () => {
            Pose(90, 1, 1, 5, 5).Bounds(0, 0, 4, 2, out float x0, out float y0, out float x1, out float y1);
            Near(3, x0); Near(5, y0); Near(5, x1); Near(9, y1);
        });
    }
}
