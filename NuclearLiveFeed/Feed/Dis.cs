using System;

namespace NuclearLiveFeed.Feed
{
    public readonly struct EntityId : IEquatable<EntityId>
    {
        public readonly ushort Site, Application, Entity;
        public EntityId(ushort site, ushort application, ushort entity) { Site = site; Application = application; Entity = entity; }
        public static readonly EntityId None = default;
        public bool Equals(EntityId other) => Site == other.Site && Application == other.Application && Entity == other.Entity;
        public override bool Equals(object? obj) => obj is EntityId other && Equals(other);
        public override int GetHashCode() => (Site << 16 ^ Application << 8 ^ Entity).GetHashCode();
        public override string ToString() => Site + ":" + Application + ":" + Entity;

        /// <summary>Parses "site:application:entity", the form CoT messages use.</summary>
        public static bool TryParse(string text, out EntityId id)
        {
            id = None;
            var parts = text.Split(':');
            if (parts.Length != 3 || !ushort.TryParse(parts[0], out var site) || !ushort.TryParse(parts[1], out var app) || !ushort.TryParse(parts[2], out var entity)) return false;
            id = new EntityId(site, app, entity);
            return true;
        }
    }

    public readonly struct EntityType
    {
        public readonly byte Kind, Domain, Category, Subcategory, Specific, Extra;
        public readonly ushort Country;
        public EntityType(byte kind, byte domain, ushort country, byte category, byte subcategory, byte specific, byte extra)
        { Kind = kind; Domain = domain; Country = country; Category = category; Subcategory = subcategory; Specific = specific; Extra = extra; }
    }

    public enum ForceId : byte { Other = 0, Friendly = 1, Opposing = 2, Neutral = 3 }

    public enum DeadReckoning : byte { Static = 1, Fpw = 2, Rvw = 4 }

    public enum DetonationResult : byte { Other = 0, EntityImpact = 1, EntityProximate = 2, GroundImpact = 3, GroundProximate = 4, Detonation = 5, Dud = 6 }

    /// <summary>One entity's state in DIS terms: ECEF location, ECEF velocity/acceleration, Euler angles and body angular velocity.</summary>
    public struct EntityState
    {
        public EntityId Id;
        public ForceId Force;
        public EntityType Type;
        public Vec3 Location, Velocity, Acceleration, AngularVelocity;
        public float Psi, Theta, Phi;
        public uint Appearance;
        public DeadReckoning Algorithm;
        public string Marking;
    }

    /// <summary>Appearance bits used by this profile (IEEE 1278.1-2012 general appearance).</summary>
    public static class Appearance
    {
        public const uint DamageSlight = 1u << 3, DamageModerate = 2u << 3, DamageDestroyed = 3u << 3;
        // Project convention: a frozen opposing entity is a last-known track that the player's side does not currently observe.
        public const uint Frozen = 1u << 21;
        public const uint Deactivated = 1u << 23;
    }

    /// <summary>Big-endian DIS 7 encoder for the four PDUs of the NuclearLiveFeed profile.</summary>
    public static class DisWriter
    {
        public const byte ProtocolVersion = 7;
        public const byte EntityStateType = 1, FireType = 2, DetonationType = 3, EntityStateUpdateType = 67;
        public const int EntityStateLength = 144, EntityStateUpdateLength = 72, FireLength = 96, DetonationLength = 104;

        public static byte[] EntityState(in EntityState s, byte exercise, uint timestamp)
        {
            var b = new byte[EntityStateLength];
            int o = Header(b, exercise, EntityStateType, 1, timestamp, EntityStateLength);
            o = Id(b, o, s.Id);
            b[o++] = (byte)s.Force;
            b[o++] = 0; // variable parameters
            o = Type(b, o, s.Type);
            o = Type(b, o, s.Type); // alternative type
            o = F3(b, o, s.Velocity);
            o = D3(b, o, s.Location);
            o = F(b, o, s.Psi); o = F(b, o, s.Theta); o = F(b, o, s.Phi);
            o = U32(b, o, s.Appearance);
            b[o++] = (byte)s.Algorithm;
            o += 15; // other dead-reckoning parameters
            o = F3(b, o, s.Acceleration);
            o = F3(b, o, s.AngularVelocity);
            b[o++] = 1; // ASCII marking
            Marking(b, o, s.Marking); o += 11;
            o = U32(b, o, 0); // capabilities
            return Check(b, o);
        }

        public static byte[] EntityStateUpdate(in EntityState s, byte exercise, uint timestamp)
        {
            var b = new byte[EntityStateUpdateLength];
            int o = Header(b, exercise, EntityStateUpdateType, 1, timestamp, EntityStateUpdateLength);
            o = Id(b, o, s.Id);
            b[o++] = 0; // padding
            b[o++] = 0; // variable parameters
            o = F3(b, o, s.Velocity);
            o = D3(b, o, s.Location);
            o = F(b, o, s.Psi); o = F(b, o, s.Theta); o = F(b, o, s.Phi);
            o = U32(b, o, s.Appearance);
            return Check(b, o);
        }

        public static byte[] Fire(byte exercise, uint timestamp, EntityId firing, EntityId target, EntityId munition, EntityId eventId,
            Vec3 location, EntityType munitionType, Vec3 velocity, float range)
        {
            var b = new byte[FireLength];
            int o = Header(b, exercise, FireType, 2, timestamp, FireLength);
            o = Id(b, o, firing); o = Id(b, o, target); o = Id(b, o, munition); o = Id(b, o, eventId);
            o = U32(b, o, 0); // fire mission index
            o = D3(b, o, location);
            o = Descriptor(b, o, munitionType);
            o = F3(b, o, velocity);
            o = F(b, o, range);
            return Check(b, o);
        }

        public static byte[] Detonation(byte exercise, uint timestamp, EntityId firing, EntityId target, EntityId exploding, EntityId eventId,
            Vec3 velocity, Vec3 location, EntityType munitionType, Vec3 locationInEntity, DetonationResult result)
        {
            var b = new byte[DetonationLength];
            int o = Header(b, exercise, DetonationType, 2, timestamp, DetonationLength);
            o = Id(b, o, firing); o = Id(b, o, target); o = Id(b, o, exploding); o = Id(b, o, eventId);
            o = F3(b, o, velocity);
            o = D3(b, o, location);
            o = Descriptor(b, o, munitionType);
            o = F3(b, o, locationInEntity);
            b[o++] = (byte)result;
            b[o++] = 0; // variable parameters
            o += 2; // padding
            return Check(b, o);
        }

        /// <summary>Absolute DIS timestamp: units of 3600 s / 2^31 past the hour, low bit set.</summary>
        public static uint Timestamp(DateTime utc)
        {
            double seconds = utc.Minute * 60.0 + utc.Second + utc.Millisecond / 1000.0;
            uint units = (uint)Math.Min(seconds / 3600.0 * 2147483648.0, 2147483647.0);
            return units << 1 | 1u;
        }

        private static int Header(byte[] b, byte exercise, byte type, byte family, uint timestamp, int length)
        {
            b[0] = ProtocolVersion; b[1] = exercise; b[2] = type; b[3] = family;
            U32(b, 4, timestamp);
            U16(b, 8, (ushort)length);
            b[10] = 0; b[11] = 0; // PDU status, padding
            return 12;
        }

        private static int Descriptor(byte[] b, int o, EntityType t)
        {
            o = Type(b, o, t);
            o = U16(b, o, 0); // warhead
            o = U16(b, o, 0); // fuse
            o = U16(b, o, 1); // quantity
            return U16(b, o, 0); // rate
        }

        private static int Id(byte[] b, int o, EntityId id) { o = U16(b, o, id.Site); o = U16(b, o, id.Application); return U16(b, o, id.Entity); }

        private static int Type(byte[] b, int o, EntityType t)
        {
            b[o++] = t.Kind; b[o++] = t.Domain; o = U16(b, o, t.Country);
            b[o++] = t.Category; b[o++] = t.Subcategory; b[o++] = t.Specific; b[o++] = t.Extra;
            return o;
        }

        private static void Marking(byte[] b, int o, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int i = 0; i < 11 && i < text!.Length; i++)
            {
                char c = text[i];
                b[o + i] = c >= 32 && c < 127 ? (byte)c : (byte)'_';
            }
        }

        private static int F3(byte[] b, int o, Vec3 v) { o = F(b, o, (float)v.X); o = F(b, o, (float)v.Y); return F(b, o, (float)v.Z); }
        private static int D3(byte[] b, int o, Vec3 v) { o = D(b, o, v.X); o = D(b, o, v.Y); return D(b, o, v.Z); }
        private static int F(byte[] b, int o, float v) => U32(b, o, (uint)BitConverter.SingleToInt32Bits(v));
        private static int D(byte[] b, int o, double v)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(v);
            for (int i = 7; i >= 0; i--) { b[o + i] = (byte)bits; bits >>= 8; }
            return o + 8;
        }
        private static int U32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; return o + 4; }
        private static int U16(byte[] b, int o, ushort v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; return o + 2; }

        private static byte[] Check(byte[] b, int written)
        {
            if (written != b.Length) throw new InvalidOperationException("PDU length mismatch: wrote " + written + " of " + b.Length);
            return b;
        }
    }
}
