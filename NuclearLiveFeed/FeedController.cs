using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearLiveFeed.Feed;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearLiveFeed
{
    /// <summary>Game-thread side: samples units, applies the player's side knowledge and hands encoded messages to the server.</summary>
    internal sealed class FeedController
    {
        private readonly Plugin plugin;
        private readonly EntityTracker tracker = new EntityTracker();
        private readonly TypeTable types = new TypeTable();
        private readonly Dictionary<uint, string> buildingStates = new Dictionary<uint, string>();
        private readonly Dictionary<uint, DateTime> observedBuildingAt = new Dictionary<uint, DateTime>();
        private readonly Dictionary<uint, string> observedBuildingState = new Dictionary<uint, string>();
        private readonly Dictionary<uint, double> buildingSentAt = new Dictionary<uint, double>();
        private readonly HashSet<uint> seenBuildings = new HashSet<uint>();
        private readonly Dictionary<uint, double> trackAges = new Dictionary<uint, double>();
        private readonly List<uint> scratch = new List<uint>();
        private readonly Dictionary<int, ushort> missileEvents = new Dictionary<int, ushort>();
        private readonly Dictionary<UnitDefinition, (EntityType Type, string Code)> definitionTypes = new Dictionary<UnitDefinition, (EntityType, string)>();
        private readonly HashSet<UnitDefinition> airRangeKnown = new HashSet<UnitDefinition>();
        /// <summary>Orientation and damage at the last moment the player's side observed a unit; stale tracks must not reveal newer state.</summary>
        private readonly Dictionary<uint, (Vec3 Forward, Vec3 Right, Vec3 Down, uint Damage)> lastObserved = new Dictionary<uint, (Vec3, Vec3, Vec3, uint)>();
        private readonly Dictionary<string, (string Fingerprint, double SentAt)> airbaseStates = new Dictionary<string, (string, double)>();
        private string? sentOwnship, sentTargets, sentMissiles;
        private readonly List<(EntityId Missile, EntityId Target)> missilePairs = new List<(EntityId, EntityId)>();
        private readonly Dictionary<uint, EntityId> lastMissileTarget = new Dictionary<uint, EntityId>();
        /// <summary>Anti-air range of each building type with anti-air weapons (0 when it has none); learned once its weapon stations exist.</summary>
        private readonly Dictionary<UnitDefinition, float> buildingAirRange = new Dictionary<UnitDefinition, float>();
        private readonly Dictionary<UnitDefinition, string> buildingAirEnvelope = new Dictionary<UnitDefinition, string>();
        private double nextAirbases, nextTargets;
        private readonly List<string> targetIds = new List<string>();
        private readonly List<Unit> resolved = new List<Unit>();
        private static readonly AccessTools.FieldRef<CombatHUD, Dictionary<Unit, HUDUnitMarker>> HudMarkers = AccessTools.FieldRefAccess<CombatHUD, Dictionary<Unit, HUDUnitMarker>>("markerLookup");
        private static readonly AccessTools.FieldRef<CombatHUD, AudioClip> SelectSound = AccessTools.FieldRefAccess<CombatHUD, AudioClip>("selectSound");
        private static readonly AccessTools.FieldRef<CombatHUD, AudioClip> DeselectSound = AccessTools.FieldRefAccess<CombatHUD, AudioClip>("deselectSound");
        private IFeedSink? sink;
        private ICommandSource? commands;
        private Func<bool>? watched;
        private MapSettings? map;
        private GeoAnchor? anchor;
        private SessionInfo? session;
        private FactionHQ? sessionHq;
        private int sentTypeVersion = -1;
        private double nextSample, nextBuildings, nextSessionCheck, nextMapSearch;
        private ushort eventNumber;

        public FeedController(Plugin plugin) { this.plugin = plugin; }

        /// <param name="watched">Whether anyone receives the feed. While false the session ends and no unit is sampled;
        /// the next check after a viewer connects starts a fresh session, which resets that viewer's picture.</param>
        public void Attach(IFeedSink value, ICommandSource? source, Func<bool>? watched = null)
        {
            sink = value;
            commands = source;
            this.watched = watched;
            EndSession();
        }

        public void Detach()
        {
            EndSession();
            sink = null;
            commands = null;
            watched = null;
        }

        public void Tick()
        {
            if (sink == null) return;
            double now = Time.realtimeSinceStartup;
            var state = GameManager.gameState;
            bool inMission = (watched == null || watched()) && (state == GameState.SinglePlayer || state == GameState.Multiplayer) && FindMap(now);
            if (!inMission) { if (session != null) EndSession(); while (commands != null && commands.TryTakeCommand(out _)) { } return; }
            if (session == null || map == null) return;

            // Optional: a failure in target sync must not stop the feed.
            try
            {
                RunCommands();
                if (now >= nextTargets) { nextTargets = now + 0.1; PublishTargets(); }
            }
            catch (Exception e) { plugin.Report("targets", e); nextTargets = now + 5; }

            if (now >= nextSessionCheck)
            {
                nextSessionCheck = now + 1;
                GameManager.GetLocalHQ(out var hq);
                if (hq != sessionHq) { sessionHq = hq; UpdateForces(); sentTypeVersion = -1; }
                if (types.Version != sentTypeVersion) PublishSession(false);
                try { PublishOwnship(); } catch (Exception e) { plugin.Report("ownship", e); }
            }
            if (now >= nextAirbases) { nextAirbases = now + 5; ObserveAirbases(now, DateTime.UtcNow); }
            if (now < nextSample) return;
            nextSample = now + 1 / Math.Max(1f, plugin.SampleHz.Value);
            ApplySettings();

            var utc = DateTime.UtcNow;
            bool buildingPass = now >= nextBuildings;
            if (buildingPass) { nextBuildings = now + 1; seenBuildings.Clear(); }
            bool revealAll = plugin.RevealAll.Value && state == GameState.SinglePlayer;

            trackAges.Clear();
            tracker.BeginFrame();
            var units = UnitRegistry.allUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.disabled || unit is Scenery) continue;
                if (unit is Building building)
                {
                    if (buildingPass && BuildingKnown(building, revealAll)) ObserveBuilding(building, now, utc, revealAll);
                    continue;
                }
                if (!TrySample(unit, revealAll, out var sample)) continue;
                tracker.Observe(sample, now, utc, anchor!, sink);
                if (unit is Missile missile) NoteMissileTarget(missile, sample.Key, sample.Frozen);
            }
            tracker.EndFrame(utc, sink);
            if (buildingPass) { RemoveMissingBuildings(utc); PublishTrackAges(utc); }
            PublishMissileTargets();
            try { PublishOwnship(); } catch (Exception e) { plugin.Report("ownship", e); }
        }

        /// <summary>
        /// Each seen missile's current target (the game's synced targetID), so a viewer can tell which missiles are guiding on
        /// whom right now, not only at launch: a missile that loses its target or switches to another one is followed.
        /// </summary>
        private void NoteMissileTarget(Missile missile, uint key, bool lost)
        {
            if (!tracker.TryGetId(key, out var id)) return;
            // A missile your side lost keeps the target it had when last seen: its later switches stay unknown, like its position.
            if (lost) { if (lastMissileTarget.TryGetValue(key, out var seen)) missilePairs.Add((id, seen)); return; }
            if (missile.targetID.TryGetUnit(out var target) && target != null && tracker.TryGetId(Key(target), out var targetId))
            {
                missilePairs.Add((id, targetId));
                lastMissileTarget[key] = targetId;
            }
            else lastMissileTarget.Remove(key);
        }

        // Mission-clock ages come from the side's tracking database, including observations before a viewer connected.
        private void PublishTrackAges(DateTime utc)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var pair in trackAges)
                if (tracker.TryGetId(pair.Key, out var id))
                    sb.Append(sb.Length > 0 ? " " : "").Append(id).Append('@').Append(pair.Value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            sink!.Building("nlf-track-ages", CotWriter.TrackAges(sb.ToString(), anchor!, utc), false);
        }

        private void PublishMissileTargets()
        {
            missilePairs.Sort((a, b) => a.Missile.Entity.CompareTo(b.Missile.Entity));
            var sb = new System.Text.StringBuilder();
            foreach (var (missile, target) in missilePairs) sb.Append(sb.Length > 0 ? " " : "").Append(missile).Append('>').Append(target);
            missilePairs.Clear();
            string value = sb.ToString();
            if (value == sentMissiles) return;
            sentMissiles = value;
            sink!.Building("nlf-missiles", CotWriter.MissileTargets(value, anchor!, DateTime.UtcNow), false);
        }

        public void MissileLaunched(Missile missile)
        {
            if (sink == null || session == null || anchor == null || missile == null) return;
            bool revealAll = plugin.RevealAll.Value && GameManager.gameState == GameState.SinglePlayer;
            if (!TrySample(missile, revealAll, out var sample)) return;
            var utc = DateTime.UtcNow;
            tracker.Observe(sample, Time.realtimeSinceStartup, utc, anchor, sink);
            if (!tracker.TryGetState(sample.Key, out var state)) return;
            var eventId = NextEvent();
            missileEvents[missile.GetInstanceID()] = eventId.Entity;
            var firing = missile.owner != null && tracker.TryGetId(Key(missile.owner), out var f) ? f : EntityId.None;
            var target = missile.targetID.TryGetUnit(out var targetUnit) && tracker.TryGetId(Key(targetUnit), out var t) ? t : EntityId.None;
            sink.Event(DisWriter.Fire(tracker.Exercise, DisWriter.Timestamp(utc), firing, target, state.Id, eventId, state.Location, state.Type, state.Velocity, 0));
        }

        public void MissileDetonated(Missile missile, Unit? relativeUnit, bool useUnit, Vector3 pos, bool armed, bool hitArmor, bool hitTerrain)
        {
            if (sink == null || session == null || anchor == null || missile == null) return;
            uint key = Key(missile);
            if (!tracker.TryGetState(key, out var state)) return;
            bool revealAll = plugin.RevealAll.Value && GameManager.gameState == GameState.SinglePlayer;
            // Having seen a missile earlier does not reveal its impact after observation is lost.
            if (!TrySample(missile, revealAll, out var sample) || sample.Frozen) return;
            GlobalPosition at = useUnit && relativeUnit != null ? relativeUnit.transform.TransformPoint(pos).ToGlobalPosition() : new GlobalPosition(pos);
            var result = !armed ? DetonationResult.Dud
                : useUnit && relativeUnit != null ? (hitArmor ? DetonationResult.EntityImpact : DetonationResult.EntityProximate)
                : hitTerrain ? DetonationResult.GroundImpact : DetonationResult.Detonation;
            var target = useUnit && relativeUnit != null && tracker.TryGetId(Key(relativeUnit), out var t) ? t : EntityId.None;
            var inEntity = useUnit ? new Vec3(pos.z, pos.x, -pos.y) : Vec3.Zero;
            var firing = missile.owner != null && tracker.TryGetId(Key(missile.owner), out var f) ? f : EntityId.None;
            var eventId = missileEvents.TryGetValue(missile.GetInstanceID(), out var number)
                ? new EntityId(tracker.Site, tracker.Application, number) : NextEvent();
            missileEvents.Remove(missile.GetInstanceID());
            lastMissileTarget.Remove(key);
            var utc = DateTime.UtcNow;
            sink.Event(DisWriter.Detonation(tracker.Exercise, DisWriter.Timestamp(utc), firing, target, state.Id, eventId,
                state.Velocity, anchor.EnuToEcef(Enu(at)), state.Type, inEntity, result));
            tracker.Remove(key, utc, sink);
        }

        private bool TrySample(Unit unit, bool revealAll, out UnitSample sample)
        {
            sample = default;
            var hq = unit.NetworkHQ;
            if (hq == null && unit is Missile missile && missile.owner != null) hq = missile.owner.NetworkHQ;
            ForceId force;
            bool frozen = false;
            GlobalPosition position = unit.GlobalPosition();
            if (hq == null) force = ForceId.Neutral;
            else if (sessionHq != null && hq == sessionHq) force = ForceId.Friendly;
            else if (sessionHq == null && !revealAll) return false;
            else
            {
                force = ForceId.Opposing;
                if (!revealAll)
                {
                    if (!sessionHq!.trackingDatabase.TryGetValue(unit.persistentID, out var track)) return false;
                    if (!track.Observed()) { frozen = true; position = track.lastKnownPosition; trackAges[Key(unit)] = Math.Max(0, Time.timeSinceLevelLoad - track.lastSpottedTime); }
                }
            }

            var unitClass = unit is Aircraft ? UnitClass.Aircraft : unit is Missile ? UnitClass.Munition
                : unit is GroundVehicle ? UnitClass.Ground : unit is Ship ? UnitClass.Ship : UnitClass.Other;
            var (type, code) = ResolveType(unit, unitClass);
            // Paused or scaled game time: the map should move at game speed.
            float timeScale = Time.timeScale;

            var t = unit.transform;
            var rb = unit.rb;
            bool hasVelocity = !frozen && rb != null && !rb.isKinematic;
            uint damage = unit.unitState switch
            {
                Unit.UnitState.Destroyed => Appearance.DamageDestroyed,
                Unit.UnitState.Damaged => Appearance.DamageModerate,
                Unit.UnitState.Abandoned => Appearance.DamageSlight,
                _ => 0u,
            };
            Vec3 forward = Enu(t.forward), right = Enu(t.right), down = -Enu(t.up);
            uint key = Key(unit);
            if (!frozen) { if (force == ForceId.Opposing) lastObserved[key] = (forward, right, down, damage); }
            else if (lastObserved.TryGetValue(key, out var seen)) (forward, right, down, damage) = seen;
            else { forward = new Vec3(0, 1, 0); right = new Vec3(1, 0, 0); down = new Vec3(0, 0, -1); damage = 0; }
            sample = new UnitSample
            {
                Key = key,
                Class = unitClass,
                Type = type,
                Force = force,
                Position = Enu(position),
                HasVelocity = hasVelocity,
                Velocity = hasVelocity ? Enu(rb!.velocity) * timeScale : Vec3.Zero,
                Forward = forward,
                Right = right,
                Down = down,
                // Unity's angular velocity is a left-handed pseudovector; the axis swap to ENU flips its sign.
                AngularVelocity = rb != null && !frozen ? -Enu(rb.angularVelocity) * timeScale : Vec3.Zero,
                Damage = damage,
                Frozen = frozen,
                Marking = Marking(unit, code),
            };
            return true;
        }

        private (EntityType Type, string Code) ResolveType(Unit unit, UnitClass unitClass)
        {
            var definition = unit.definition;
            if (definition != null && definitionTypes.TryGetValue(definition, out var cached))
            {
                if (!airRangeKnown.Contains(definition)) LearnAirRange(unit, definition);
                return cached;
            }
            string code = definition != null && !string.IsNullOrEmpty(definition.code) ? definition.code : unit.unitName ?? "";
            string name = definition != null && !string.IsNullOrEmpty(definition.unitName) ? definition.unitName : unit.unitName ?? "";
            // Unity's Object.name allocates, so the definition lookup is cached per session.
            string key = definition != null ? definition.name : unit.GetType().Name + ":" + name;
            var result = (types.Resolve(key, unitClass, code, name), code);
            if (definition != null) { definitionTypes[definition] = result; LearnAirRange(unit, definition); }
            return result;
        }

        /// <summary>
        /// Longest range of the type's anti-air weapons: the turret AI's own fire gate (targetRequirements.maxRange where
        /// effectiveness.antiAir &gt; 0). Ground units and ships only; learned once weapon stations exist.
        /// </summary>
        private void LearnAirRange(Unit unit, UnitDefinition definition)
        {
            if (!(unit is GroundVehicle) && !(unit is Ship)) { airRangeKnown.Add(definition); return; }
            if (unit.weaponStations.Count == 0) return;
            var (range, envelope) = AirEnvelope(unit);
            airRangeKnown.Add(definition);
            if (range <= 0) return;
            types.SetAirRange(definition.name, range, envelope);
            plugin.Log.LogInfo("Air defense range " + (range / 1000).ToString("0.0") + " km: " + (definition.unitName ?? definition.name));
        }

        private void PublishOwnship()
        {
            bool present = GameManager.GetLocalAircraft(out var own) && own != null && !own.disabled;
            var id = EntityId.None;
            if (present && !tracker.TryGetId(Key(own!), out id)) return; // not sampled yet; next check
            // Height above the ground (the game's radar altitude, which anti-air ceilings use), in 25 m steps, and the
            // selected weapon with its longest range: both only your own aircraft's data.
            float agl = present ? Mathf.Round(own!.radarAlt / 25f) * 25f : 0;
            var weapon = present && own!.weaponManager != null ? own.weaponManager.currentWeaponStation?.WeaponInfo : null;
            string weaponName = weapon != null ? (!string.IsNullOrEmpty(weapon.shortName) ? weapon.shortName : weapon.weaponName ?? "") : "";
            float weaponRange = weapon != null && !weapon.gun && !weapon.bomb ? weapon.targetRequirements.maxRange : 0;
            string value = present ? id + "/" + agl + "/" + weaponName + "/" + weaponRange : "";
            if (value == sentOwnship) return;
            sentOwnship = value;
            sink!.Building("nlf-ownship", CotWriter.Ownship(id, present, anchor!, DateTime.UtcNow, agl, weaponName, weaponRange), false);
        }

        /// <summary>The local aircraft's HUD and its target list (the same list object the weapon manager uses), when the player flies.</summary>
        private static bool TryGetTargets(out CombatHUD hud, out List<Unit> list)
        {
            hud = SceneSingleton<CombatHUD>.i;
            list = null!;
            if (hud == null || hud.aircraft == null || hud.aircraft.disabled || hud.aircraft.weaponManager == null) return false;
            if (!GameManager.GetLocalAircraft(out var own) || own != hud.aircraft) return false;
            list = hud.aircraft.weaponManager.GetTargetList();
            return list != null;
        }

        private void PublishTargets()
        {
            bool editable = TryGetTargets(out _, out var list);
            targetIds.Clear();
            if (editable)
                foreach (var unit in list)
                {
                    if (unit == null) continue;
                    // Buildings travel as their feed uid; everything else as its entity ID. Order is kept: primary first.
                    if (unit is Building building && building.persistentID.IsValid) targetIds.Add("nlf-b-" + building.persistentID.Id);
                    else if (tracker.TryGetId(Key(unit), out var id)) targetIds.Add(id.ToString());
                }
            string value = (editable ? "1 " : "0 ") + string.Join(" ", targetIds);
            if (value == sentTargets) return;
            sentTargets = value;
            sink!.Building("nlf-targets", CotWriter.Targets(targetIds, editable, anchor!, DateTime.UtcNow), false);
        }

        private void RunCommands()
        {
            if (commands == null) return;
            for (int n = 0; n < 16 && commands.TryTakeCommand(out var text); n++)
            {
                if (!TargetCommand.TryParse(text, out var command)) continue;
                try { ApplyTargets(command); }
                catch (Exception e) { plugin.Report("targets", e); }
                nextTargets = 0;
            }
        }

        /// <summary>
        /// Changes the target list the way the in-game map does (CombatHUD.SelectUnit and DeSelectUnit): only units with a HUD
        /// marker (own side and units the side tracks), never the own aircraft, and not units hidden by the target filter.
        /// Bulk changes send the list to the server once and play one sound.
        /// </summary>
        private void ApplyTargets(TargetCommand command)
        {
            if (!TryGetTargets(out var hud, out var list)) return;
            if (command.Op == TargetOp.Clear) { hud.DeselectAll(true); return; }
            resolved.Clear();
            foreach (var id in command.Ids)
                if (tracker.TryGetKey(id, out var key) && key < 0x80000000u && UnitRegistry.TryGetUnit(new PersistentID { Id = key }, out var unit) && unit != null && !unit.disabled)
                    resolved.Add(unit);
            // Buildings come by persistent ID, and only those the feed shows (known to your side).
            foreach (var key in command.Buildings)
                if (UnitRegistry.TryGetUnit(new PersistentID { Id = key }, out var unit) && unit is Building building && !unit.disabled && BuildingKnown(building, false))
                    resolved.Add(unit);
            var markers = HudMarkers(hud);
            bool added = false, removed = false;
            if (command.Op == TargetOp.Remove || command.Op == TargetOp.Set)
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var unit = list[i];
                    if ((command.Op == TargetOp.Remove) != resolved.Contains(unit)) continue;
                    list.RemoveAt(i);
                    removed = true;
                    if (unit == null) continue;
                    if (markers.TryGetValue(unit, out var marker) && marker != null) marker.DeselectMarker();
                    else SceneSingleton<DynamicMap>.i?.DeselectIcon(unit);
                }
            }
            if (command.Op == TargetOp.Add || command.Op == TargetOp.Set)
            {
                var selector = SceneSingleton<TargetListSelector>.i;
                // Reverse order: the first ID becomes the primary target (index 0), like the newest in-game selection.
                for (int i = resolved.Count - 1; i >= 0 && list.Count < TargetCommand.MaxIds; i--)
                {
                    var unit = resolved[i];
                    // Never your own aircraft or your own side's units, whatever client sent the change.
                    if (unit == hud.aircraft || (unit.NetworkHQ != null && unit.NetworkHQ == hud.aircraft.NetworkHQ)) continue;
                    if (list.Contains(unit) || !markers.TryGetValue(unit, out var marker) || marker == null) continue;
                    if (selector != null && selector.CheckExclusions(unit)) continue;
                    list.Insert(0, unit);
                    marker.SelectMarker();
                    added = true;
                }
            }
            if (!added && !removed) return;
            hud.aircraft.weaponManager.TargetListChanged();
            var clip = added ? SelectSound(hud) : DeselectSound(hud);
            if (clip != null) SoundManager.PlayInterfaceOneShot(clip);
        }

        /// <summary>Airbases and their owning side, as the in-game map shows them. Ship decks are skipped.</summary>
        private void ObserveAirbases(double now, DateTime utc)
        {
            foreach (var airbase in UnityEngine.Object.FindObjectsOfType<Airbase>())
            {
                if (airbase == null || airbase.AttachedAirbase || airbase.center == null) continue;
                var saved = airbase.SavedAirbase;
                string id = saved != null && !string.IsNullOrEmpty(saved.UniqueName) ? saved.UniqueName : airbase.GetInstanceID().ToString();
                string uid = "nlf-ab-" + id;
                var hq = airbase.CurrentHQ;
                var force = hq == null ? ForceId.Neutral : sessionHq != null && hq == sessionHq ? ForceId.Friendly : ForceId.Opposing;
                string fingerprint = ((int)force).ToString();
                if (airbaseStates.TryGetValue(uid, out var previous) && previous.Fingerprint == fingerprint && now - previous.SentAt < 600) continue;
                airbaseStates[uid] = (fingerprint, now);
                var (lat, lon, hae) = GeoAnchor.EcefToGeodetic(anchor!.EnuToEcef(Enu(airbase.center.GlobalPosition())));
                string name = saved != null && !string.IsNullOrEmpty(saved.DisplayName) ? saved.DisplayName : id;
                sink!.Building(uid, CotWriter.Building(uid, force, lat, lon, hae, utc, TimeSpan.FromHours(1), name, "", "active", "airbase", Runways(airbase)), false);
            }
        }

        private static readonly AccessTools.FieldRef<Airbase.Runway, float> RunwayWidth = AccessTools.FieldRefAccess<Airbase.Runway, float>("width");

        /// <summary>Runway ends as geodetic points. Landing aircraft touch down at the start and roll toward the end; reversible runways also the other way.</summary>
        private List<RunwayInfo>? Runways(Airbase airbase)
        {
            if (airbase.runways == null || airbase.runways.Length == 0) return null;
            var list = new List<RunwayInfo>();
            foreach (var r in airbase.runways)
            {
                if (r == null || r.Start == null || r.End == null) continue;
                var (lat1, lon1, h1) = GeoAnchor.EcefToGeodetic(anchor!.EnuToEcef(Enu(r.Start.GlobalPosition())));
                var (lat2, lon2, h2) = GeoAnchor.EcefToGeodetic(anchor.EnuToEcef(Enu(r.End.GlobalPosition())));
                list.Add(new RunwayInfo(r.GetName(false), r.GetName(true), lat1, lon1, h1, lat2, lon2, h2, RunwayWidth(r), r.Landing, r.Reversable));
            }
            return list;
        }

        private static string Marking(Unit unit, string code)
        {
            if (unit is Aircraft aircraft && aircraft.Player != null)
            {
                string player = aircraft.Player.GetDisplayName(PlayerNameContext.Other) ?? "";
                if (player.Length > 0) return player;
            }
            return code;
        }

        /// <summary>
        /// Like the in-game map (DynamicMap.GenerateMap): your side's and neutral buildings always, an enemy building only once
        /// it is in your side's tracking database. Without a side, only neutral buildings: no side knows anything yet.
        /// A building that stops being known is removed by the next building pass.
        /// </summary>
        private bool BuildingKnown(Building building, bool revealAll)
        {
            var hq = building.NetworkHQ;
            if (hq == null || revealAll || (sessionHq != null && hq == sessionHq)) return true;
            if (sessionHq == null) return false;
            return sessionHq.trackingDatabase.ContainsKey(building.persistentID);
        }

        private void ObserveBuilding(Building building, double now, DateTime utc, bool revealAll)
        {
            uint key = Key(building);
            seenBuildings.Add(key);
            var hq = building.NetworkHQ;
            var force = hq == null ? ForceId.Neutral : sessionHq != null && hq == sessionHq ? ForceId.Friendly : ForceId.Opposing;
            string state = building.unitState switch
            {
                Unit.UnitState.Destroyed => "destroyed",
                Unit.UnitState.Damaged => "damaged",
                Unit.UnitState.Abandoned => "abandoned",
                _ => "active",
            };
            bool observed = force != ForceId.Opposing || revealAll
                || (sessionHq != null && sessionHq.trackingDatabase.TryGetValue(building.persistentID, out var track) && track.Observed());
            if (observed) { observedBuildingState[key] = state; observedBuildingAt[key] = utc; }
            else state = observedBuildingState.TryGetValue(key, out var lastState) ? lastState : "unknown";
            float airRange = BuildingAirRange(building);
            string airEnvelope = building.definition != null && buildingAirEnvelope.TryGetValue(building.definition, out var c) ? c : "";
            string fingerprint = state + (int)force + "/" + observed + "/" + airRange + "/" + airEnvelope;
            if (buildingStates.TryGetValue(key, out var previous) && previous == fingerprint
                && buildingSentAt.TryGetValue(key, out var sentAt) && now - sentAt < 600) return;
            buildingStates[key] = fingerprint;
            buildingSentAt[key] = now;
            var (lat, lon, hae) = GeoAnchor.EcefToGeodetic(anchor!.EnuToEcef(Enu(building.GlobalPosition())));
            var definition = building.definition;
            string name = definition != null && !string.IsNullOrEmpty(definition.unitName) ? definition.unitName : building.unitName ?? "";
            string code = definition != null ? definition.code ?? "" : "";
            sink!.Building("nlf-b-" + key, CotWriter.Building("nlf-b-" + key, force, lat, lon, hae, utc, TimeSpan.FromHours(1), name, code, state, airRange: airRange, airEnvelope: airEnvelope, observed: observed, lastSeenUtc: observedBuildingAt.TryGetValue(key, out var lastAt) ? lastAt : (DateTime?)null), false);
        }

        /// <summary>
        /// Longest range and highest ceiling of a unit's anti-air weapons. The turret AI (CombatAI.AnalyzeTarget) engages a target
        /// only below targetRequirements.maxAltitude above ground, within maxRange; 0 ceiling means the weapons give none.
        /// </summary>
        private static (float Range, string Envelope) AirEnvelope(Unit unit)
        {
            float range = 0;
            var pairs = new List<string>();
            foreach (var station in unit.weaponStations)
            {
                var info = station?.WeaponInfo;
                if (info == null || info.effectiveness.antiAir <= 0 || info.targetRequirements.maxRange <= 0) continue;
                range = Math.Max(range, info.targetRequirements.maxRange);
                // Each weapon's own range and ceiling: a long-range weapon with a low ceiling must not borrow another's height.
                string pair = Math.Round(info.targetRequirements.maxRange) + ":" + Math.Round(Math.Max(0, info.targetRequirements.maxAltitude));
                if (!pairs.Contains(pair)) pairs.Add(pair);
            }
            return (range, string.Join(" ", pairs));
        }

        /// <summary>
        /// Anti-air emplacements are buildings: the same turret fire gate as for vehicles (targetRequirements.maxRange of
        /// weapons with effectiveness.antiAir above 0), cached per building type once its weapon stations exist.
        /// </summary>
        private float BuildingAirRange(Building building)
        {
            var definition = building.definition;
            if (definition == null) return 0;
            if (buildingAirRange.TryGetValue(definition, out var known)) return known;
            if (building.weaponStations == null || building.weaponStations.Count == 0) return 0;
            var (range, envelope) = AirEnvelope(building);
            buildingAirRange[definition] = range;
            buildingAirEnvelope[definition] = envelope;
            if (range > 0) plugin.Log.LogInfo("Air defense range " + (range / 1000).ToString("0.0") + " km: " + (definition.unitName ?? definition.name));
            return range;
        }

        private void RemoveMissingBuildings(DateTime utc)
        {
            scratch.Clear();
            foreach (var key in buildingStates.Keys) if (!seenBuildings.Contains(key)) scratch.Add(key);
            foreach (var key in scratch)
            {
                buildingStates.Remove(key);
                observedBuildingState.Remove(key);
                observedBuildingAt.Remove(key);
                buildingSentAt.Remove(key);
                string uid = "nlf-b-" + key;
                sink!.Building(uid, CotWriter.Building(uid, ForceId.Neutral, anchor!.Latitude, anchor.Longitude, 0, utc, TimeSpan.Zero, "", "", "removed"), true);
            }
        }

        private bool FindMap(double now)
        {
            if (map != null) return true;
            if (session != null) EndSession();
            if (now < nextMapSearch) return false;
            nextMapSearch = now + 1;
            var found = UnityEngine.Object.FindObjectOfType<MapSettings>();
            if (found == null || UnitRegistry.allUnits.Count == 0) return false;
            map = found;
            StartSession();
            return true;
        }

        private void StartSession()
        {
            types.Clear();
            definitionTypes.Clear();
            airRangeKnown.Clear();
            lastObserved.Clear();
            airbaseStates.Clear();
            sentOwnship = null;
            sentTargets = null;
            sentMissiles = null;
            missilePairs.Clear();
            lastMissileTarget.Clear();
            buildingAirRange.Clear();
            buildingAirEnvelope.Clear();
            nextAirbases = nextTargets = 0;
            trackAges.Clear();
            buildingStates.Clear();
            observedBuildingState.Clear();
            observedBuildingAt.Clear();
            buildingSentAt.Clear();
            missileEvents.Clear();
            eventNumber = 0;
            tracker.Exercise = (byte)plugin.Exercise.Value;
            tracker.Site = (ushort)plugin.Site.Value;
            tracker.Application = (ushort)plugin.Application.Value;
            anchor = new GeoAnchor(Mathf.Clamp(map!.Latitude, -80f, 80f), plugin.AnchorLongitude.Value, 0);
            GameManager.GetLocalHQ(out sessionHq);
            session = new SessionInfo
            {
                MapName = map.name.Replace("(Clone)", "").Trim(),
                MapWidth = map.MapSize.x,
                MapHeight = map.MapSize.y,
                Exercise = tracker.Exercise,
                Site = tracker.Site,
                Application = tracker.Application,
                Producer = "NuclearLiveFeed " + MyPluginInfo.PLUGIN_VERSION,
            };
            UpdateForces();
            nextSample = nextBuildings = nextSessionCheck = 0;
            PublishSession(true);
            plugin.Log.LogInfo("Feed session started on " + session.MapName + " (" + session.MapWidth + " x " + session.MapHeight + " m, anchor " + anchor.Latitude.ToString("0.###") + ", " + anchor.Longitude.ToString("0.###") + ").");
        }

        private void EndSession()
        {
            if (session != null && sink != null)
            {
                tracker.Clear(DateTime.UtcNow, sink);
                plugin.Log.LogInfo("Feed session ended.");
            }
            session = null;
            map = null;
            sessionHq = null;
        }

        private void UpdateForces()
        {
            if (session == null) return;
            session.Forces.Clear();
            var all = UnityEngine.Object.FindObjectsOfType<FactionHQ>();
            foreach (var hq in all)
            {
                if (hq == null || hq.faction == null) continue;
                var force = sessionHq != null && hq == sessionHq ? ForceId.Friendly : ForceId.Opposing;
                var c = hq.faction.color;
                session.Forces.Add((force, hq.faction.factionName ?? "", hq.faction.factionTag ?? "", "#" + ColorUtility.ToHtmlStringRGB(c)));
            }
        }

        private void PublishSession(bool reset)
        {
            sentTypeVersion = types.Version;
            sink!.Session(CotWriter.Session(session!, anchor!, types, DateTime.UtcNow), reset);
        }

        private void ApplySettings()
        {
            var s = tracker.Settings;
            s.MaxRateHz = plugin.MaxRateHz.Value;
            s.HeartbeatSeconds = plugin.Heartbeat.Value;
            s.AirPositionThreshold = plugin.AirThreshold.Value;
            s.GroundPositionThreshold = plugin.GroundThreshold.Value;
            s.OrientationThresholdDegrees = plugin.AngleThreshold.Value;
        }

        private EntityId NextEvent()
        {
            eventNumber = (ushort)(eventNumber >= 65534 ? 1 : eventNumber + 1);
            return new EntityId(tracker.Site, tracker.Application, eventNumber);
        }

        private static uint Key(Unit unit) => unit.persistentID.IsValid ? unit.persistentID.Id : (uint)unit.GetInstanceID() | 0x80000000u;
        private static Vec3 Enu(GlobalPosition p) => new Vec3(p.x, p.z, p.y);
        private static Vec3 Enu(Vector3 v) => new Vec3(v.x, v.z, v.y);
    }
}
