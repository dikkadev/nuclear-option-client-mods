using System;
using System.Collections.Generic;
using System.Globalization;

namespace NuclearMinimap
{
    public enum InstrumentCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    // Engine-independent instrument math. World north is +z and east is +x, as on the native map.
    internal static class FlightInstrumentMath
    {
        private const double Degrees = 180d / Math.PI;

        internal static float Heading(float forwardX, float forwardZ)
        {
            if (!Finite(forwardX) || !Finite(forwardZ) || (forwardX == 0 && forwardZ == 0)) return 0;
            return Wrap360((float)(Math.Atan2(forwardX, forwardZ) * Degrees));
        }

        internal static float Pitch(float forwardY) =>
            Finite(forwardY) ? (float)(Math.Asin(Math.Max(-1f, Math.Min(1f, forwardY))) * Degrees) : 0;

        // Positive means right wing down. Inverted flight continues past +/-90 towards 180.
        internal static float Bank(float rightY, float upY)
        {
            if (!Finite(rightY) || !Finite(upY) || (rightY == 0 && upY == 0)) return 0;
            return (float)(Math.Atan2(-rightY, upY) * Degrees);
        }

        internal static float Wrap360(float degrees)
        {
            if (!Finite(degrees)) return 0;
            float value = degrees % 360f;
            if (value < 0) value += 360f;
            return value >= 360f ? 0 : value;
        }

        // Signed shortest offset from heading to value, in [-180, 180).
        internal static float Offset(float heading, float value)
        {
            float delta = Wrap360(value - heading);
            return delta >= 180f ? delta - 360f : delta;
        }

        internal static string HeadingText(float heading)
        {
            int value = (int)Math.Round(Wrap360(heading), MidpointRounding.AwayFromZero) % 360;
            return value.ToString("D3", CultureInfo.InvariantCulture);
        }

        // Labels every 30 degrees: cardinal letters, otherwise tens of degrees.
        internal static string TapeLabel(int degrees)
        {
            switch ((degrees % 360 + 360) % 360)
            {
                case 0: return "N";
                case 90: return "E";
                case 180: return "S";
                case 270: return "W";
                default: return (((degrees % 360 + 360) % 360) / 10).ToString("D2", CultureInfo.InvariantCulture);
            }
        }

        internal static string BankText(float bank)
        {
            int value = (int)Math.Round(Math.Abs(Finite(bank) ? bank : 0), MidpointRounding.AwayFromZero);
            if (value == 0) return "0";
            return (bank > 0 ? "R" : "L") + value.ToString(CultureInfo.InvariantCulture);
        }

        // Tape marks every step degrees within halfSpan of heading, ordered left to right.
        internal static void TapeTicks(float heading, float halfSpan, int step, List<(int degrees, float offset)> result)
        {
            result.Clear();
            if (step <= 0 || !Finite(halfSpan) || halfSpan <= 0) return;
            heading = Wrap360(heading);
            int first = (int)Math.Ceiling((heading - halfSpan) / step) * step;
            for (int value = first; value <= heading + halfSpan; value += step)
                result.Add(((value % 360 + 360) % 360, value - heading));
        }

        // Display offset of a pitch line from the ball centre, before bank rotation.
        internal static float LadderOffset(float pitch, float lineAngle, float unitsPerDegree) => (lineAngle - pitch) * unitsPerDegree;

        // Sutherland-Hodgman clip of a convex polygon to the half-plane nx*x + ny*y >= d.
        internal static void ClipHalfPlane(List<(float x, float y)> input, float nx, float ny, float d, List<(float x, float y)> output)
        {
            output.Clear();
            for (int i = 0; i < input.Count; i++)
            {
                var a = input[i]; var b = input[(i + 1) % input.Count];
                float da = nx * a.x + ny * a.y - d, db = nx * b.x + ny * b.y - d;
                if (da >= 0) output.Add(a);
                if ((da >= 0) != (db >= 0))
                {
                    float t = da / (da - db);
                    output.Add((a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t));
                }
            }
        }

        // Centre of a round instrument with the given outer radius, inset into a rect corner.
        internal static (float x, float y) CornerCenter(InstrumentCorner corner, float xMin, float yMin, float xMax, float yMax, float radius, float margin)
        {
            bool left = corner == InstrumentCorner.TopLeft || corner == InstrumentCorner.BottomLeft;
            bool top = corner == InstrumentCorner.TopLeft || corner == InstrumentCorner.TopRight;
            return (left ? xMin + margin + radius : xMax - margin - radius,
                top ? yMax - margin - radius : yMin + margin + radius);
        }

        internal static bool IsTop(InstrumentCorner corner) => corner == InstrumentCorner.TopLeft || corner == InstrumentCorner.TopRight;

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
