using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NuclearDelivery
{
    internal static class DeliveryTracking
    {
        private sealed class TrackedLaunch
        {
            public Missile Missile = null!;
            public DeliveryKind Kind;
            public float Started;
            public bool Accepted;
            public bool? ArmorContact;
            public float Resolved;
        }
        private static readonly DeliveryLedger Ledger = new DeliveryLedger();
        private static readonly Dictionary<int, TrackedLaunch> Launches = new Dictionary<int, TrackedLaunch>();
        private static readonly List<int> Remove = new List<int>();
        // Every launch, regardless of owner, so allied munitions can mark targets after an aircraft change.
        private static readonly HashSet<Missile> Flying = new HashSet<Missile>();
        private static readonly Predicate<Missile> Ended = missile => missile == null || missile.disabled;
        private static Aircraft? owner;
        private static bool active;
        public static DeliveryCounts Counts => Ledger.Snapshot();

        public static void Update(Aircraft? aircraft, bool enabled, float recentSeconds, float timeoutSeconds)
        {
            SetOwner(aircraft, enabled);
            if (!enabled) Flying.Clear(); else Flying.RemoveWhere(Ended);
            if (!active || owner == null) return;
            float now = Time.time;
            Remove.Clear();
            foreach (var pair in Launches)
            {
                var launch = pair.Value;
                if (!launch.Accepted)
                {
                    TryAccept(pair.Key, launch);
                    if (!launch.Accepted && (launch.Missile == null || now - launch.Started >= 2f))
                    { Remove.Add(pair.Key); continue; }
                }
                if (launch.Accepted)
                {
                    if (launch.Missile == null) Ledger.Resolve(pair.Key, false, now);
                    if (!Ledger.Contains(pair.Key)) Remove.Add(pair.Key);
                }
            }
            foreach (int id in Remove) Launches.Remove(id);
            Ledger.Advance(now, recentSeconds, timeoutSeconds);
        }

        private static void SetOwner(Aircraft? aircraft, bool enabled)
        {
            // Reference identity detects a destroyed previous owner as well as a new cockpit.
            if (!ReferenceEquals(owner, aircraft) || active != enabled)
            {
                Clear();
                owner = aircraft;
                active = enabled;
            }
            if (owner == null && Launches.Count != 0) Clear();
        }
        public static void Clear()
        {
            Ledger.Clear(); Launches.Clear(); Remove.Clear();
        }
        private static bool IsOwned(Missile missile)
        {
            Unit? current = missile;
            for (int depth = 0; depth < 8 && current is Missile child; depth++)
            {
                Unit? next = child.owner;
                if (next == null && child.ownerID.TryGetUnit(out var found)) next = found;
                if (next == null || ReferenceEquals(next, current)) return false;
                current = next;
            }
            return owner != null && current == owner;
        }
        private static void TryAccept(int id, TrackedLaunch launch)
        {
            if (launch.Accepted || launch.Missile == null || !IsOwned(launch.Missile)) return;
            launch.Accepted = true;
            Ledger.Launch(id, launch.Kind, launch.Started);
            if (launch.ArmorContact.HasValue) Ledger.Resolve(id, launch.ArmorContact.Value, launch.Resolved);
        }
        // Live targetID follows retargeting and loss of lock; the HQ filter excludes enemy munitions.
        public static int Engaging(Unit target, bool includeAllies)
        {
            if (!active || owner == null || target == null) return 0;
            var id = target.persistentID;
            if (!id.IsValid) return 0;
            var hq = owner.NetworkHQ;
            int count = 0;
            foreach (var missile in Flying)
            {
                if (missile == null || missile.disabled || missile.targetID != id) continue;
                if (includeAllies ? missile.NetworkHQ == hq : IsOwned(missile)) count++;
            }
            return count;
        }
        private static void Started(Missile missile)
        {
            if (Plugin.Instance?.IsTrackingEnabled != true) return;
            Flying.Add(missile);
            SetOwner(CombatHUD.i != null ? CombatHUD.i.aircraft : null, active);
            if (!active || owner == null) return;
            int id = missile.GetInstanceID();
            // StartMissile denotes a new launch, including a reused instance: drop old dedup/outcome.
            Ledger.Forget(id);
            var launch = new TrackedLaunch
            {
                Missile = missile, Started = Time.time,
                Kind = missile.GetWeaponInfo().bomb ? DeliveryKind.Bomb : DeliveryKind.Missile
            };
            Launches[id] = launch;
            TryAccept(id, launch);
        }
        private static void Detonated(Missile missile, bool hitArmor)
        {
            Flying.Remove(missile);
            if (Plugin.Instance?.IsTrackingEnabled != true) return;
            SetOwner(CombatHUD.i != null ? CombatHUD.i.aircraft : null, active);
            if (!active || owner == null || !Launches.TryGetValue(missile.GetInstanceID(), out var launch)) return;
            if (!launch.ArmorContact.HasValue)
            {
                launch.ArmorContact = hitArmor;
                launch.Resolved = Time.time;
            }
            int id = missile.GetInstanceID();
            TryAccept(id, launch);
            if (launch.Accepted) Ledger.Resolve(id, launch.ArmorContact.Value, launch.Resolved);
        }

        [HarmonyPatch(typeof(Missile), "StartMissile")]
        private static class MissileStartPatch
        {
            private static void Postfix(Missile __instance)
            { try { Started(__instance); } catch (System.Exception error) { Plugin.Report("launch", error); } }
        }
        // Generated user-code body runs for both host-local execution and remote client reception.
        // Prefix captures the collision flag even if the original cosmetic effect code fails.
        [HarmonyPatch(typeof(Missile), "UserCode_RpcDetonate_897349600")]
        private static class MissileDetonatePatch
        {
            private static void Prefix(Missile __instance, bool hitArmor)
            { try { Detonated(__instance, hitArmor); } catch (System.Exception error) { Plugin.Report("outcome", error); } }
        }
    }
}
