using System;
using System.Collections.Generic;

namespace NuclearMapTrails
{
    internal enum TrailTeams
    {
        Friendly,
        AllVisible
    }

    internal enum TrailRelation
    {
        Friendly,
        Enemy,
        Spectator,
        NoFaction
    }

    internal enum TrailColorMode
    {
        IconColor,
        FactionColor,
        Custom
    }

    internal readonly struct TrailSample
    {
        internal TrailSample(float x, float altitude, float z, float time, bool startsRun)
        {
            X = x;
            Altitude = altitude;
            Z = z;
            Time = time;
            StartsRun = startsRun;
        }

        internal float X { get; }
        internal float Altitude { get; }
        internal float Z { get; }
        internal float Time { get; }

        // True when this sample must not be joined to the previous one.
        internal bool StartsRun { get; }
    }

    // Engine-independent sample buffer for one aircraft, in global map metres and level time.
    internal sealed class TrailHistory
    {
        internal const int MaxSamples = 4096;

        // Apparent motion faster than this is a respawn, teleport or track jump, not flight.
        internal const float MaxJoinSpeed = 1500f;

        private readonly List<TrailSample> samples = new List<TrailSample>();
        private bool gap = true;

        internal IReadOnlyList<TrailSample> Samples => samples;
        internal bool IsEmpty => samples.Count == 0;

        internal bool Due(float now, float interval)
            => samples.Count == 0 || gap || now - samples[samples.Count - 1].Time >= interval;

        internal void Add(float x, float altitude, float z, float time)
        {
            bool startsRun = gap || samples.Count == 0;
            if (!startsRun)
            {
                var last = samples[samples.Count - 1];
                float elapsed = time - last.Time;
                if (elapsed <= 0f)
                {
                    return;
                }

                float dx = x - last.X;
                float dz = z - last.Z;
                startsRun = Math.Sqrt(dx * dx + dz * dz) / elapsed > MaxJoinSpeed;
            }

            samples.Add(new TrailSample(x, altitude, z, time, startsRun));
            gap = false;
            if (samples.Count > MaxSamples)
            {
                samples.RemoveRange(0, samples.Count - MaxSamples);
            }
        }

        // The next sample starts a separate run, for example after a lost enemy track.
        internal void Break() => gap = true;

        internal void Prune(float now, float duration)
        {
            int expired = 0;
            while (expired < samples.Count && now - samples[expired].Time > duration)
            {
                expired++;
            }

            if (expired > 0)
            {
                samples.RemoveRange(0, expired);
            }
        }

        internal void Clear()
        {
            samples.Clear();
            gap = true;
        }
    }

    internal static class TrailPolicy
    {
        internal static bool ShouldTrack(TrailRelation relation, bool isLocalAircraft, TrailTeams teams, bool showOwnTrail)
        {
            if (isLocalAircraft)
            {
                return showOwnTrail;
            }

            switch (relation)
            {
                case TrailRelation.Friendly:
                case TrailRelation.Spectator:
                    // Spectators already see every unit's true position on the native map.
                    return true;
                case TrailRelation.Enemy:
                    return teams == TrailTeams.AllVisible;
                default:
                    return false;
            }
        }

        internal static float Opacity(float age, float duration, float newestOpacity, float oldestOpacity, bool fade)
        {
            if (!fade || duration <= 0f)
            {
                return newestOpacity;
            }

            return Lerp(newestOpacity, oldestOpacity, age / duration);
        }

        // Color multiplier: lowBrightness at or below lowAltitude, full brightness at or above highAltitude.
        internal static float Brightness(float altitude, float lowAltitude, float highAltitude, float lowBrightness, bool enabled)
        {
            if (!enabled)
            {
                return 1f;
            }

            if (highAltitude <= lowAltitude)
            {
                return altitude >= highAltitude ? 1f : lowBrightness;
            }

            return Lerp(lowBrightness, 1f, (altitude - lowAltitude) / (highAltitude - lowAltitude));
        }

        private static float Lerp(float from, float to, float t)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            return from + (to - from) * t;
        }
    }
}
