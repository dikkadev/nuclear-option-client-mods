using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NuclearLiveFeed.Feed
{
    public sealed class SessionInfo
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string MapName = "";
        public double MapWidth, MapHeight;
        public byte Exercise = 1;
        public ushort Site = 1, Application = 1;
        public string Producer = "";
        public readonly List<(ForceId Force, string Name, string Tag, string Color)> Forces = new List<(ForceId, string, string, string)>();
    }

    /// <summary>Private type table: DIS subcategory/specific carry a 16-bit index into this table (country 0).</summary>
    public sealed class TypeTable
    {
        public sealed class Entry
        {
            public readonly int Index; public readonly UnitClass Class; public readonly string Code, Name;
            /// <summary>Longest anti-air weapon range in meters (turret fire gate), 0 when the type has none.</summary>
            public float AirRange;
            /// <summary>Each anti-air weapon's "range:ceiling" in meters (targetRequirements.maxRange and maxAltitude, the
            /// highest target height above ground it engages; 0 when none), space-separated; empty when the type has none.</summary>
            public string AirEnvelope = "";
            public Entry(int index, UnitClass unitClass, string code, string name) { Index = index; Class = unitClass; Code = code; Name = name; }
        }

        private readonly Dictionary<string, Entry> byKey = new Dictionary<string, Entry>();
        public readonly List<Entry> Entries = new List<Entry>();
        public int Version { get; private set; }

        public EntityType Resolve(string key, UnitClass unitClass, string code, string name)
        {
            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = new Entry(Entries.Count + 1, unitClass, code, name);
                byKey.Add(key, entry);
                Entries.Add(entry);
                Version++;
            }
            return ToDis(entry);
        }

        public int IndexOf(string key) => byKey.TryGetValue(key, out var e) ? e.Index : 0;

        /// <summary>Records a type's anti-air range once known; a change republishes the session.</summary>
        public void SetAirRange(string key, float meters, string envelope = "")
        {
            if (!byKey.TryGetValue(key, out var e) || (Math.Abs(e.AirRange - meters) < 1 && e.AirEnvelope == envelope)) return;
            e.AirRange = meters;
            e.AirEnvelope = envelope;
            Version++;
        }

        public static EntityType ToDis(Entry e)
        {
            byte kind = e.Class == UnitClass.Munition ? (byte)2 : (byte)1;
            byte domain = e.Class switch { UnitClass.Aircraft => 2, UnitClass.Ground => 1, UnitClass.Ship => 3, UnitClass.Other => 1, _ => 0 };
            return new EntityType(kind, domain, 0, 0, (byte)(e.Index >> 8), (byte)e.Index, 0);
        }

        public void Clear() { byKey.Clear(); Entries.Clear(); Version++; }
    }

    /// <summary>One runway: start and end points, names for both directions, width in meters and use flags.</summary>
    public readonly struct RunwayInfo
    {
        public readonly string Name, ReverseName;
        public readonly double Lat1, Lon1, Hae1, Lat2, Lon2, Hae2;
        public readonly float Width;
        public readonly bool Landing, Reversible;
        public RunwayInfo(string name, string reverseName, double lat1, double lon1, double hae1, double lat2, double lon2, double hae2, float width, bool landing, bool reversible)
        {
            Name = name; ReverseName = reverseName; Lat1 = lat1; Lon1 = lon1; Hae1 = hae1; Lat2 = lat2; Lon2 = lon2; Hae2 = hae2;
            Width = width; Landing = landing; Reversible = reversible;
        }
    }

    public static class CotWriter
    {
        /// <summary>Feed format version, sent in the hello and session events. Raise it when a viewer could misread the stream.</summary>
        public const int ProtocolVersion = 1;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Building(string uid, ForceId force, double lat, double lon, double hae, DateTime now, TimeSpan staleAfter,
            string name, string code, string state, string kind = "structure", IReadOnlyList<RunwayInfo>? runways = null, double airRange = 0, string airEnvelope = "", bool observed = true, DateTime? lastSeenUtc = null)
        {
            string affiliation = force == ForceId.Friendly ? "f" : force == ForceId.Opposing ? "h" : "n";
            var sb = Event(uid, "a-" + affiliation + "-G-I", now, staleAfter, lat, lon, hae);
            sb.Append("<detail><contact callsign=\"").Append(Escape(name)).Append("\"/>");
            sb.Append("<nlf kind=\"").Append(kind).Append("\" state=\"").Append(state).Append("\" code=\"").Append(Escape(code)).Append("\" force=\"").Append((int)force).Append('"');
            sb.Append(" observed=\"").Append(observed ? "true" : "false").Append('"');
            if (lastSeenUtc.HasValue) sb.Append(" lastSeenUtc=\"").Append(T(lastSeenUtc.Value)).Append('"');
            if (airRange > 0) sb.Append(" airRange=\"").Append(N(airRange)).Append('"');
            if (airEnvelope.Length > 0) sb.Append(" airEnvelope=\"").Append(Escape(airEnvelope)).Append('"');
            if (runways == null || runways.Count == 0) sb.Append("/>");
            else
            {
                sb.Append('>');
                foreach (var r in runways)
                    sb.Append("<runway name=\"").Append(Escape(r.Name)).Append("\" reverseName=\"").Append(Escape(r.ReverseName))
                      .Append("\" lat1=\"").Append(r.Lat1.ToString("0.0000000", Inv)).Append("\" lon1=\"").Append(r.Lon1.ToString("0.0000000", Inv)).Append("\" hae1=\"").Append(r.Hae1.ToString("0.0", Inv))
                      .Append("\" lat2=\"").Append(r.Lat2.ToString("0.0000000", Inv)).Append("\" lon2=\"").Append(r.Lon2.ToString("0.0000000", Inv)).Append("\" hae2=\"").Append(r.Hae2.ToString("0.0", Inv))
                      .Append("\" width=\"").Append(N(r.Width)).Append("\" landing=\"").Append(r.Landing ? "true" : "false").Append("\" reversible=\"").Append(r.Reversible ? "true" : "false").Append("\"/>");
                sb.Append("</nlf>");
            }
            return sb.Append("</detail></event>").ToString();
        }

        public static string Session(SessionInfo s, GeoAnchor anchor, TypeTable types, DateTime now)
        {
            var sb = Event("nlf-session", "b-x-nlf-session", now, TimeSpan.FromDays(1), anchor.Latitude, anchor.Longitude, anchor.Height);
            sb.Append("<detail><nlf protocol=\"").Append(ProtocolVersion).Append("\" session=\"").Append(s.Id).Append("\" exercise=\"").Append(s.Exercise)
              .Append("\" site=\"").Append(s.Site).Append("\" application=\"").Append(s.Application)
              .Append("\" producer=\"").Append(Escape(s.Producer)).Append("\">");
            sb.Append("<map name=\"").Append(Escape(s.MapName)).Append("\" width=\"").Append(N(s.MapWidth)).Append("\" height=\"").Append(N(s.MapHeight))
              .Append("\" latitude=\"").Append(N(anchor.Latitude)).Append("\" longitude=\"").Append(N(anchor.Longitude)).Append("\" altitude=\"").Append(N(anchor.Height)).Append("\"/>");
            foreach (var f in s.Forces)
                sb.Append("<force id=\"").Append((int)f.Force).Append("\" name=\"").Append(Escape(f.Name)).Append("\" tag=\"").Append(Escape(f.Tag)).Append("\" color=\"").Append(f.Color).Append("\"/>");
            foreach (var t in types.Entries)
            {
                sb.Append("<type index=\"").Append(t.Index).Append("\" class=\"").Append(t.Class.ToString().ToLowerInvariant()).Append("\" code=\"").Append(Escape(t.Code)).Append("\" name=\"").Append(Escape(t.Name)).Append('"');
                if (t.AirRange > 0) sb.Append(" airRange=\"").Append(N(t.AirRange)).Append('"');
                if (t.AirEnvelope.Length > 0) sb.Append(" airEnvelope=\"").Append(t.AirEnvelope).Append('"');
                sb.Append("/>");
            }
            return sb.Append("</nlf></detail></event>").ToString();
        }

        /// <summary>Which entity is the local player's aircraft; an empty ID means none (menu, dead, spectating).</summary>
        public static string Ownship(EntityId id, bool present, GeoAnchor anchor, DateTime now, float agl = 0, string weapon = "", float weaponRange = 0)
        {
            var sb = Event("nlf-ownship", "b-x-nlf-ownship", now, TimeSpan.FromDays(1), anchor.Latitude, anchor.Longitude, anchor.Height);
            sb.Append("<detail><nlf entity=\"").Append(present ? id.ToString() : "").Append('"');
            if (present) sb.Append(" agl=\"").Append(N(agl)).Append("\" weapon=\"").Append(Escape(weapon)).Append("\" weaponRange=\"").Append(N(weaponRange)).Append('"');
            sb.Append("/></detail></event>");
            return sb.ToString();
        }

        /// <summary>Current target of each missile the player's side sees: space-separated "missile>target" entity ID pairs.</summary>
        public static string MissileTargets(string pairs, GeoAnchor anchor, DateTime now)
        {
            var sb = Event("nlf-missiles", "b-x-nlf-missiles", now, TimeSpan.FromDays(1), anchor.Latitude, anchor.Longitude, anchor.Height);
            sb.Append("<detail><nlf pairs=\"").Append(Escape(pairs)).Append("\"/></detail></event>");
            return sb.ToString();
        }

        /// <summary>Age in mission seconds of the side's last observation, for each lost mobile track.</summary>
        public static string TrackAges(string pairs, GeoAnchor anchor, DateTime now)
        {
            var sb = Event("nlf-track-ages", "b-x-nlf-track-ages", now, TimeSpan.FromDays(1), anchor.Latitude, anchor.Longitude, anchor.Height);
            return sb.Append("<detail><nlf pairs=\"").Append(Escape(pairs)).Append("\"/></detail></event>").ToString();
        }

        /// <summary>Sent to each viewer alone on connect: what this viewer may control.</summary>
        public static string Hello(bool control, DateTime now)
        {
            var sb = Event("nlf-hello", "b-x-nlf-hello", now, TimeSpan.FromDays(1), 0, 0, 0);
            sb.Append("<detail><nlf protocol=\"").Append(ProtocolVersion).Append("\" control=\"").Append(control ? "targets" : "").Append("\"/></detail></event>");
            return sb.ToString();
        }

        /// <summary>
        /// The local player's target list, newest first (index 0 is the game's primary target). Editable is false when the
        /// player has no aircraft or weapons, so viewers offer no selection.
        /// </summary>
        public static string Targets<T>(IEnumerable<T> ids, bool editable, GeoAnchor anchor, DateTime now)
        {
            var sb = Event("nlf-targets", "b-x-nlf-targets", now, TimeSpan.FromDays(1), anchor.Latitude, anchor.Longitude, anchor.Height);
            sb.Append("<detail><nlf editable=\"").Append(editable ? "true" : "false").Append("\" entities=\"").Append(string.Join(" ", ids)).Append("\"/></detail></event>");
            return sb.ToString();
        }

        private static StringBuilder Event(string uid, string type, DateTime now, TimeSpan staleAfter, double lat, double lon, double hae)
        {
            var sb = new StringBuilder(256);
            sb.Append("<event version=\"2.0\" uid=\"").Append(Escape(uid)).Append("\" type=\"").Append(type).Append("\" how=\"m-g\" time=\"").Append(T(now))
              .Append("\" start=\"").Append(T(now)).Append("\" stale=\"").Append(T(now + staleAfter)).Append("\">");
            sb.Append("<point lat=\"").Append(lat.ToString("0.0000000", Inv)).Append("\" lon=\"").Append(lon.ToString("0.0000000", Inv))
              .Append("\" hae=\"").Append(hae.ToString("0.0", Inv)).Append("\" ce=\"9999999.0\" le=\"9999999.0\"/>");
            return sb;
        }

        private static string T(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Inv);
        private static string N(double v) => v.ToString("0.#######", Inv);

        public static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: if (c >= 32 || c == '\t') sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
