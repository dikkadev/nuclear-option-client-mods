using System;
using System.Collections.Generic;

namespace NuclearDelivery
{
    internal enum DeliveryKind { Missile, Bomb }
    internal enum DeliveryOutcome { InFlight, Hit, Missed }

    internal readonly struct DeliveryCounts
    {
        public readonly int MissileInFlight, MissileHit, MissileMissed;
        public readonly int BombInFlight, BombHit, BombMissed;
        public DeliveryCounts(int missileInFlight, int missileHit, int missileMissed,
            int bombInFlight, int bombHit, int bombMissed)
        {
            MissileInFlight = missileInFlight; MissileHit = missileHit; MissileMissed = missileMissed;
            BombInFlight = bombInFlight; BombHit = bombHit; BombMissed = bombMissed;
        }
    }

    // Engine-independent transitions. "Hit" denotes the game's hitArmor collision proxy.
    internal sealed class DeliveryLedger
    {
        private sealed class Entry
        {
            public DeliveryKind Kind;
            public DeliveryOutcome Outcome;
            public double Started, Resolved;
        }
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly List<int> expired = new List<int>();
        public void Launch(int id, DeliveryKind kind, double now)
        {
            entries[id] = new Entry { Kind = kind, Outcome = DeliveryOutcome.InFlight, Started = now };
        }
        public bool Resolve(int id, bool hitArmor, double now)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.Outcome != DeliveryOutcome.InFlight) return false;
            entry.Outcome = hitArmor ? DeliveryOutcome.Hit : DeliveryOutcome.Missed;
            entry.Resolved = now;
            return true;
        }
        public bool IsInFlight(int id) => entries.TryGetValue(id, out var entry) && entry.Outcome == DeliveryOutcome.InFlight;
        public bool Contains(int id) => entries.ContainsKey(id);
        public void Forget(int id) => entries.Remove(id);
        public void Clear() { entries.Clear(); expired.Clear(); }
        public void Advance(double now, double recentSeconds, double flightTimeoutSeconds)
        {
            recentSeconds = ValidSeconds(recentSeconds, 3);
            flightTimeoutSeconds = ValidSeconds(flightTimeoutSeconds, 120);
            expired.Clear();
            foreach (var pair in entries)
            {
                var entry = pair.Value;
                if (entry.Outcome == DeliveryOutcome.InFlight)
                {
                    if (now - entry.Started >= flightTimeoutSeconds)
                    {
                        entry.Outcome = DeliveryOutcome.Missed;
                        entry.Resolved = now;
                    }
                }
                else if (now - entry.Resolved >= recentSeconds) expired.Add(pair.Key);
            }
            foreach (int id in expired) entries.Remove(id);
        }
        private static double ValidSeconds(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? fallback : value;
        public DeliveryCounts Snapshot()
        {
            int mf = 0, mh = 0, mm = 0, bf = 0, bh = 0, bm = 0;
            foreach (var entry in entries.Values)
            {
                if (entry.Kind == DeliveryKind.Missile)
                {
                    if (entry.Outcome == DeliveryOutcome.InFlight) mf++;
                    else if (entry.Outcome == DeliveryOutcome.Hit) mh++; else mm++;
                }
                else
                {
                    if (entry.Outcome == DeliveryOutcome.InFlight) bf++;
                    else if (entry.Outcome == DeliveryOutcome.Hit) bh++; else bm++;
                }
            }
            return new DeliveryCounts(mf, mh, mm, bf, bh, bm);
        }
    }
}
