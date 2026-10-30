using System;
using System.Collections.Generic;
using System.Globalization;

namespace NuclearMapMarkings
{
    // Horizontal world position or direction: x east, z north, in metres.
    internal readonly struct MapPoint
    {
        internal readonly float X;
        internal readonly float Z;

        internal MapPoint(float x, float z)
        {
            X = x;
            Z = z;
        }

        internal float Length => (float)Math.Sqrt(X * X + Z * Z);

        internal MapPoint Normalized()
        {
            float length = Length;
            return length > 1e-6f ? new MapPoint(X / length, Z / length) : new MapPoint(0f, 0f);
        }

        public static MapPoint operator +(MapPoint a, MapPoint b) => new MapPoint(a.X + b.X, a.Z + b.Z);
        public static MapPoint operator -(MapPoint a, MapPoint b) => new MapPoint(a.X - b.X, a.Z - b.Z);
        public static MapPoint operator *(MapPoint a, float scale) => new MapPoint(a.X * scale, a.Z * scale);
    }

    internal enum UnitMotionKind
    {
        Aircraft,
        Surface,
    }

    // One landing end: aircraft fly Direction to touch down at Threshold.
    internal readonly struct ApproachEnd
    {
        internal readonly MapPoint Threshold;
        internal readonly MapPoint Direction;

        internal ApproachEnd(MapPoint threshold, MapPoint direction)
        {
            Threshold = threshold;
            Direction = direction;
        }

        // The approach line runs outward from the threshold, opposite to the landing direction.
        internal MapPoint PointAt(float distance) => Threshold - Direction * distance;
    }

    internal static class MarkingsGeometry
    {
        internal const float MaxAircraftTurnRate = 0.35f;  // rad/s, about 20 deg/s
        internal const float MaxSurfaceTurnRate = 0.1f;    // rad/s, about 6 deg/s
        internal const float TurnDeadband = 0.03f;         // rad/s, about 1.7 deg/s: jitter, not a turn

        // Native landing requests use reverse=false (touch down at Start) unless the runway is reversible.
        internal static int ApproachEnds(MapPoint start, MapPoint end, bool landing, bool reversible, ApproachEnd[] output)
        {
            var along = (end - start).Normalized();
            if (!landing || along.Length < 0.5f || output.Length < 2)
            {
                return 0;
            }

            output[0] = new ApproachEnd(start, along);
            if (!reversible)
            {
                return 1;
            }

            output[1] = new ApproachEnd(end, along * -1f);
            return 2;
        }

        // Approach centerline length in metres. Runways up to shortRunway get minimum, from longRunway on
        // maximum; in between it grows with the logarithm of runway length, so long runways do not dominate.
        internal static float ApproachLength(float runwayLength, float shortRunway, float longRunway, float minimum, float maximum)
        {
            if (!(runwayLength > 0f) || float.IsInfinity(runwayLength) || !(maximum > 0f))
            {
                return 0f;
            }

            minimum = Math.Max(0f, Math.Min(minimum, maximum));
            if (!(shortRunway > 0f) || !(longRunway > shortRunway) || runwayLength >= longRunway)
            {
                return runwayLength >= longRunway || !(longRunway > shortRunway) ? maximum : minimum;
            }

            if (runwayLength <= shortRunway)
            {
                return minimum;
            }

            double fraction = Math.Log(runwayLength / shortRunway) / Math.Log(longRunway / shortRunway);
            return (float)(minimum + (maximum - minimum) * fraction);
        }

        // Distances from the threshold for approach ticks, excluding the threshold itself.
        internal static void TickDistances(float length, float spacing, List<float> output)
        {
            output.Clear();
            if (!(length > 0f) || !(spacing > 0f) || float.IsInfinity(length))
            {
                return;
            }

            int count = (int)Math.Floor(length / spacing + 1e-4f);
            for (int i = 1; i <= count && i <= 1000; i++)
            {
                output.Add(i * spacing);
            }
        }

        // Dashes around a ring of the given circumference, rounded so the pattern closes without a seam.
        // 0 means a solid ring. Very large rings keep maximum dashes, which grow longer instead.
        internal static int RingDashCount(float circumference, float dash, float gap, int maximum)
        {
            if (!(dash > 0f) || !(gap > 0f) || !(circumference > 0f) || float.IsInfinity(circumference) || maximum < 4)
            {
                return 0;
            }

            double count = Math.Round(circumference / (dash + gap));
            return (int)Math.Max(4.0, Math.Min(maximum, count));
        }

        // Each ring outward from the first gets a wider gap, so rings read apart at a glance.
        internal static float RingGap(float firstGap, int index) => firstGap * (1 + Math.Max(0, index));

        // Heading change after t seconds when the turn rate fades exponentially with time constant fade.
        // fade <= 0 means straight; infinity keeps the turn constant. The total change never exceeds turnRate * fade.
        internal static double HeadingChange(float turnRate, float fade, double t)
        {
            if (!(fade > 0f) || Math.Abs(turnRate) < 1e-5f)
            {
                return 0.0;
            }

            return float.IsPositiveInfinity(fade) ? turnRate * t : turnRate * fade * (1.0 - Math.Exp(-t / fade));
        }

        // Prediction time for a path of at most maxLength metres. The length, maxLength * (1 - exp(-speed * seconds / maxLength)),
        // grows almost linearly at low speed and levels off towards maxLength, so fast aircraft do not draw very long paths.
        // maxLength <= 0 means no limit.
        internal static float PathHorizon(float speed, float seconds, float maxLength)
        {
            if (!(seconds > 0f) || float.IsInfinity(seconds))
            {
                return 0f;
            }

            if (!(speed > 0f) || !(maxLength > 0f) || float.IsInfinity(speed) || float.IsInfinity(maxLength))
            {
                return seconds;
            }

            double length = maxLength * (1.0 - Math.Exp(-speed * seconds / maxLength));
            return (float)(length / speed);
        }

        // Constant speed with a fading turn, sampled every step seconds, including the start point.
        internal static void PredictPath(MapPoint position, MapPoint velocity, float turnRate, float turnFade, float horizon, float step, List<MapPoint> output)
        {
            output.Clear();
            output.Add(position);
            if (!(horizon > 0f) || !(step > 0f))
            {
                return;
            }

            float speed = velocity.Length;
            double heading = Math.Atan2(velocity.Z, velocity.X);
            int count = Math.Min(1000, (int)Math.Ceiling(horizon / step - 1e-4f));
            const int substeps = 8;
            double x = position.X, z = position.Z, time = 0.0;
            for (int i = 1; i <= count; i++)
            {
                double end = Math.Min(horizon, i * step);
                double dt = (end - time) / substeps;
                for (int s = 0; s < substeps; s++)
                {
                    // Midpoint heading of each substep keeps the arc accurate at coarse steps.
                    double angle = heading + HeadingChange(turnRate, turnFade, time + 0.5 * dt);
                    x += speed * Math.Cos(angle) * dt;
                    z += speed * Math.Sin(angle) * dt;
                    time += dt;
                }

                output.Add(new MapPoint((float)x, (float)z));
            }
        }

        // Distances in kilometres from text such as "10, 20, 40"; invalid parts are skipped.
        internal static List<float> ParseDistances(string? text, float maximum)
        {
            var values = new List<float>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return values;
            }

            foreach (var part in text!.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
                    value > 0f && value <= maximum && !values.Contains(value))
                {
                    values.Add(value);
                }
            }

            values.Sort();
            if (values.Count > 8)
            {
                values.RemoveRange(8, values.Count - 8);
            }

            return values;
        }

        internal static float WrapAngle(double radians)
        {
            while (radians > Math.PI) radians -= 2.0 * Math.PI;
            while (radians < -Math.PI) radians += 2.0 * Math.PI;
            return (float)radians;
        }
    }

    // Velocity and turn rate from recent map positions, so friendly and tracked units are treated alike.
    internal sealed class MotionEstimate
    {
        internal const float SampleInterval = 0.25f;
        internal const float Window = 1f;
        internal const float MaxSpeed = 1500f;

        private readonly struct Sample
        {
            internal readonly MapPoint Position;
            internal readonly float Time;

            internal Sample(MapPoint position, float time)
            {
                Position = position;
                Time = time;
            }
        }

        private readonly List<Sample> samples = new List<Sample>();
        private MapPoint velocity;
        private bool hasVelocity;
        private float smoothedTurnRate;
        private bool hasTurnRate;

        internal float LastSeen { get; private set; } = float.NegativeInfinity;

        internal void Reset()
        {
            samples.Clear();
            hasVelocity = false;
            hasTurnRate = false;
            smoothedTurnRate = 0f;
        }

        internal void Add(MapPoint position, float time, UnitMotionKind kind)
        {
            LastSeen = time;
            if (samples.Count > 0)
            {
                var last = samples[samples.Count - 1];
                float dt = time - last.Time;
                if (dt < SampleInterval)
                {
                    return;
                }

                // A respawn, teleport or stale track jump would produce an impossible prediction.
                if ((position - last.Position).Length / dt > MaxSpeed || dt > 4f * Window)
                {
                    Reset();
                }
            }

            samples.Add(new Sample(position, time));
            while (samples.Count > 0 && time - samples[0].Time > 2f * Window + SampleInterval)
            {
                samples.RemoveAt(0);
            }

            Update(kind);
        }

        internal bool TryGet(out MapPoint currentVelocity, out float turnRate)
        {
            currentVelocity = velocity;
            // Small measured turns are position jitter: report straight motion.
            turnRate = hasTurnRate && Math.Abs(smoothedTurnRate) >= MarkingsGeometry.TurnDeadband ? smoothedTurnRate : 0f;
            return hasVelocity;
        }

        // Velocity over the last second, turn rate from the heading change between the last two seconds.
        // Runs once per new sample, so smoothing does not depend on frame rate.
        private void Update(UnitMotionKind kind)
        {
            hasVelocity = false;
            var newest = samples[samples.Count - 1];
            int middle = IndexAtOrBefore(newest.Time - Window);
            if (middle < 0 || newest.Time - samples[middle].Time < 0.5f * Window)
            {
                return;
            }

            var mid = samples[middle];
            velocity = (newest.Position - mid.Position) * (1f / (newest.Time - mid.Time));
            hasVelocity = true;
            int oldest = IndexAtOrBefore(mid.Time - Window);
            if (oldest < 0 || mid.Time - samples[oldest].Time < 0.5f * Window)
            {
                return;
            }

            var old = samples[oldest];
            var previous = (mid.Position - old.Position) * (1f / (mid.Time - old.Time));
            if (previous.Length <= 1f || velocity.Length <= 1f)
            {
                return;
            }

            float maxTurn = kind == UnitMotionKind.Aircraft ? MarkingsGeometry.MaxAircraftTurnRate : MarkingsGeometry.MaxSurfaceTurnRate;
            float change = MarkingsGeometry.WrapAngle(Math.Atan2(velocity.Z, velocity.X) - Math.Atan2(previous.Z, previous.X));
            float elapsed = 0.5f * (newest.Time - old.Time);
            float measured = Math.Max(-maxTurn, Math.Min(maxTurn, change / elapsed));
            smoothedTurnRate = hasTurnRate ? smoothedTurnRate + 0.3f * (measured - smoothedTurnRate) : measured;
            hasTurnRate = true;
        }
        private int IndexAtOrBefore(float time)
        {
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                if (samples[i].Time <= time + 1e-4f)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
