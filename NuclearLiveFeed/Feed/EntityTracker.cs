using System;
using System.Collections.Generic;

namespace NuclearLiveFeed.Feed
{
    public enum UnitClass : byte { Aircraft, Ground, Ship, Munition, Other }

    /// <summary>One sampled unit in game-independent terms. Vectors are ENU meters (game X, Z, Y).</summary>
    public struct UnitSample
    {
        public uint Key;
        public UnitClass Class;
        public EntityType Type;
        public ForceId Force;
        public Vec3 Position, Velocity, Forward, Right, Down, AngularVelocity;
        public bool HasVelocity;
        public uint Damage;
        public bool Frozen;
        public string Marking;
    }

    public sealed class TrackerSettings
    {
        public double HeartbeatSeconds = 5;
        public double MaxRateHz = 10;
        public double AirPositionThreshold = 10, GroundPositionThreshold = 5;
        public double OrientationThresholdDegrees = 5;
        public double AccelerationThreshold = 3;
        public double AngularRateThreshold = 0.05;
    }

    /// <summary>
    /// Decides per entity whether to send a full Entity State, an Entity State Update or nothing, using the same
    /// dead-reckoning model that viewers run. Not thread-safe; call from the game thread only.
    /// </summary>
    public sealed class EntityTracker
    {
        private sealed class Tracked
        {
            public EntityId Id;
            public bool Seen;
            public double SentAt = double.NegativeInfinity, FullAt = double.NegativeInfinity, SampleAt = double.NaN;
            public Vec3 SentPosition, SentVelocity, SentAcceleration, SentAngular, SentForward, SentUp;
            public DeadReckoning SentAlgorithm;
            public uint SentAppearance;
            public ForceId SentForce;
            public EntityType SentType;
            public string? SentMarking;
            public Vec3 LastPosition, LastVelocity, Acceleration;
            public EntityState Current;
        }

        private readonly Dictionary<uint, Tracked> entities = new Dictionary<uint, Tracked>();
        private readonly HashSet<ushort> usedNumbers = new HashSet<ushort>();
        private readonly Dictionary<ushort, uint> byNumber = new Dictionary<ushort, uint>();
        private readonly List<uint> scratch = new List<uint>();
        private ushort nextNumber = 1;
        public TrackerSettings Settings { get; } = new TrackerSettings();
        public ushort Site { get; set; } = 1;
        public ushort Application { get; set; } = 1;
        public byte Exercise { get; set; } = 1;
        public int Count => entities.Count;

        public void BeginFrame()
        {
            foreach (var t in entities.Values) t.Seen = false;
        }

        public bool TryGetId(uint key, out EntityId id)
        {
            if (entities.TryGetValue(key, out var t)) { id = t.Id; return true; }
            id = EntityId.None; return false;
        }

        /// <summary>Reverse lookup for viewer commands: entity ID to the game key.</summary>
        public bool TryGetKey(EntityId id, out uint key)
        {
            key = 0;
            return id.Site == Site && id.Application == Application && byNumber.TryGetValue(id.Entity, out key);
        }

        public bool TryGetState(uint key, out EntityState state)
        {
            if (entities.TryGetValue(key, out var t)) { state = t.Current; return true; }
            state = default; return false;
        }

        public void Observe(in UnitSample s, double now, DateTime utc, GeoAnchor anchor, IFeedSink sink)
        {
            if (!entities.TryGetValue(s.Key, out var t))
            {
                t = new Tracked { Id = new EntityId(Site, Application, Allocate()), LastPosition = s.Position };
                entities.Add(s.Key, t);
                byNumber[t.Id.Entity] = s.Key;
            }
            t.Seen = true;

            var velocity = s.Velocity;
            if (!double.IsNaN(t.SampleAt) && now > t.SampleAt)
            {
                double dt = now - t.SampleAt;
                if (!s.HasVelocity) velocity = (s.Position - t.LastPosition) * (1 / dt);
                var measured = (velocity - t.LastVelocity) * (1 / dt);
                t.Acceleration = t.Acceleration * 0.7 + measured * 0.3;
            }
            else if (!s.HasVelocity) velocity = Vec3.Zero;
            t.SampleAt = now; t.LastPosition = s.Position; t.LastVelocity = velocity;

            double speed = velocity.Length;
            var algorithm = s.Frozen ? DeadReckoning.Static
                : s.Class == UnitClass.Aircraft || s.Class == UnitClass.Munition
                    ? (speed < 0.5 ? DeadReckoning.Static : DeadReckoning.Rvw)
                    : (speed < 0.2 ? DeadReckoning.Static : DeadReckoning.Fpw);
            var acceleration = algorithm == DeadReckoning.Rvw ? t.Acceleration : Vec3.Zero;
            var angular = algorithm == DeadReckoning.Rvw ? s.AngularVelocity : Vec3.Zero;
            if (algorithm == DeadReckoning.Static) velocity = Vec3.Zero;
            uint appearance = s.Damage | (s.Frozen ? Appearance.Frozen : 0);

            t.Current = Build(t.Id, s, anchor, velocity, acceleration, angular, appearance, algorithm);

            bool isNew = double.IsNegativeInfinity(t.SentAt);
            double sinceSent = now - t.SentAt;
            bool identityChanged = isNew || s.Force != t.SentForce || !SameType(s.Type, t.SentType) || s.Marking != t.SentMarking;
            bool full = identityChanged || algorithm != t.SentAlgorithm || now - t.FullAt >= Settings.HeartbeatSeconds
                || (algorithm == DeadReckoning.Rvw && ((acceleration - t.SentAcceleration).Length > Settings.AccelerationThreshold
                    || (angular - t.SentAngular).Length > Settings.AngularRateThreshold));
            bool appearanceChanged = appearance != t.SentAppearance;
            bool drift = false;
            if (!full && !appearanceChanged)
            {
                if (sinceSent < 1 / Settings.MaxRateHz) return;
                Predict(t, sinceSent, out var predicted, out var forward, out var up);
                double threshold = s.Class == UnitClass.Aircraft || s.Class == UnitClass.Munition ? Settings.AirPositionThreshold : Settings.GroundPositionThreshold;
                drift = (s.Position - predicted).Length > threshold
                    || Angle(forward, s.Forward) > Settings.OrientationThresholdDegrees
                    || Angle(up, -s.Down) > Settings.OrientationThresholdDegrees;
                if (!drift) return;
            }
            else if (!identityChanged && !appearanceChanged && sinceSent < 1 / Settings.MaxRateHz) return;

            uint stamp = DisWriter.Timestamp(utc);
            var snapshot = DisWriter.EntityState(t.Current, Exercise, stamp);
            var wire = full || isNew ? snapshot : DisWriter.EntityStateUpdate(t.Current, Exercise, stamp);
            sink.Entity(s.Key, wire, snapshot);

            t.SentAt = now;
            if (wire == snapshot) t.FullAt = now;
            t.SentPosition = s.Position; t.SentVelocity = velocity; t.SentAcceleration = acceleration; t.SentAngular = angular;
            t.SentForward = s.Forward; t.SentUp = -s.Down; t.SentAlgorithm = algorithm; t.SentAppearance = appearance;
            t.SentForce = s.Force; t.SentType = s.Type; t.SentMarking = s.Marking;
        }

        /// <summary>Deactivates every entity not observed since <see cref="BeginFrame"/>.</summary>
        public void EndFrame(DateTime utc, IFeedSink sink)
        {
            scratch.Clear();
            foreach (var pair in entities) if (!pair.Value.Seen) scratch.Add(pair.Key);
            foreach (var key in scratch) Remove(key, utc, sink);
        }

        public void Remove(uint key, DateTime utc, IFeedSink sink)
        {
            if (!entities.TryGetValue(key, out var t)) return;
            entities.Remove(key);
            usedNumbers.Remove(t.Id.Entity);
            byNumber.Remove(t.Id.Entity);
            if (double.IsNegativeInfinity(t.SentAt)) return;
            var final = t.Current;
            final.Appearance |= Appearance.Deactivated;
            final.Velocity = Vec3.Zero; final.Acceleration = Vec3.Zero; final.AngularVelocity = Vec3.Zero;
            final.Algorithm = DeadReckoning.Static;
            sink.Removed(key, DisWriter.EntityState(final, Exercise, DisWriter.Timestamp(utc)));
        }

        public void Clear(DateTime utc, IFeedSink sink)
        {
            scratch.Clear();
            scratch.AddRange(entities.Keys);
            foreach (var key in scratch) Remove(key, utc, sink);
            nextNumber = 1;
        }

        private static EntityState Build(EntityId id, in UnitSample s, GeoAnchor anchor, Vec3 velocity, Vec3 acceleration, Vec3 angularEnu, uint appearance, DeadReckoning algorithm)
        {
            var (psi, theta, phi) = anchor.EulerFromEnuAxes(s.Forward, s.Right, s.Down);
            return new EntityState
            {
                Id = id,
                Force = s.Force,
                Type = s.Type,
                Location = anchor.EnuToEcef(s.Position),
                Velocity = anchor.EnuToEcefVector(velocity),
                Acceleration = anchor.EnuToEcefVector(acceleration),
                // DIS angular velocity is in body coordinates (x forward, y right, z down).
                AngularVelocity = new Vec3(s.Forward.Dot(angularEnu), s.Right.Dot(angularEnu), s.Down.Dot(angularEnu)),
                Psi = psi, Theta = theta, Phi = phi,
                Appearance = appearance,
                Algorithm = algorithm,
                Marking = s.Marking,
            };
        }

        private static void Predict(Tracked t, double dt, out Vec3 position, out Vec3 forward, out Vec3 up)
        {
            switch (t.SentAlgorithm)
            {
                case DeadReckoning.Rvw:
                    position = t.SentPosition + t.SentVelocity * dt + t.SentAcceleration * (0.5 * dt * dt);
                    forward = Rotate(t.SentForward, t.SentAngular, dt);
                    up = Rotate(t.SentUp, t.SentAngular, dt);
                    break;
                case DeadReckoning.Fpw:
                    position = t.SentPosition + t.SentVelocity * dt;
                    forward = t.SentForward; up = t.SentUp;
                    break;
                default:
                    position = t.SentPosition; forward = t.SentForward; up = t.SentUp;
                    break;
            }
        }

        /// <summary>Rodrigues rotation of v by angular velocity w (rad/s, right-handed) over dt.</summary>
        public static Vec3 Rotate(Vec3 v, Vec3 w, double dt)
        {
            double rate = w.Length;
            if (rate < 1e-9) return v;
            var k = w * (1 / rate);
            double a = rate * dt, c = Math.Cos(a), s = Math.Sin(a);
            return v * c + k.Cross(v) * s + k * (k.Dot(v) * (1 - c));
        }

        private static double Angle(Vec3 a, Vec3 b)
        {
            double d = a.Normalized().Dot(b.Normalized());
            return Math.Acos(Math.Max(-1, Math.Min(1, d))) * 180 / Math.PI;
        }

        private static bool SameType(EntityType a, EntityType b) =>
            a.Kind == b.Kind && a.Domain == b.Domain && a.Country == b.Country && a.Category == b.Category
            && a.Subcategory == b.Subcategory && a.Specific == b.Specific && a.Extra == b.Extra;

        private ushort Allocate()
        {
            for (int i = 0; i < 65534; i++)
            {
                ushort n = nextNumber;
                nextNumber = (ushort)(nextNumber >= 65534 ? 1 : nextNumber + 1);
                if (usedNumbers.Add(n)) return n;
            }
            throw new InvalidOperationException("No free DIS entity numbers.");
        }
    }
}
