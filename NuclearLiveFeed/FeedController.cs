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
        private readonly Dictionary<uint, double> buildingSentAt = new Dictionary<uint, double>();
        private readonly HashSet<uint> seenBuildings = new HashSet<uint>();
        private readonly List<uint> scratch = new List<uint>();
        private readonly Dictionary<int, ushort> missileEvents = new Dictionary<int, ushort>();
        private readonly Dictionary<UnitDefinition, (EntityType Type, string Code)> definitionTypes = new Dictionary<UnitDefinition, (EntityType, string)>();
        private readonly HashSet<UnitDefinition> airRangeKnown = new HashSet<UnitDefinition>();
        /// <summary>Orientation and damage at the last moment the player's side observed a unit; stale tracks must not reveal newer state.</summary>
        private readonly Dictionary<uint, (Vec3 Forward, Vec3 Right, Vec3 Down, uint Damage)> lastObserved = new Dictionary<uint, (Vec3, Vec3, Vec3, uint)>();
        private readonly Dictionary<string, (string Fingerprint, double SentAt)> airbaseStates = new Dictionary<string, (string, double)>();
        private string? sentOwnship, sentTargets;
        private double nextAirbases, nextTargets;
        private readonly List<EntityId> targetIds = new List<EntityId>();
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

            tracker.BeginFrame();
            var units = UnitRegistry.allUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit.disabled || unit is Scenery) continue;
                if (unit is Building building)
                {
                    if (buildingPass) ObserveBuilding(building, now, utc);
                    continue;
                }
                if (TrySample(unit, revealAll, out var sample)) tracker.Observe(sample, now, utc, anchor!, sink);
            }
            tracker.EndFrame(utc, sink);
            if (buildingPass) RemoveMissingBuildings(utc);
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
            if (!tracker.TryGetState(key, out var state)) return; // only munitions the player's side could see
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
                    if (!track.Observed()) { frozen = true; position = track.lastKnownPosition; }
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
            float range = 0;
            foreach (var station in unit.weaponStations)
            {
                var info = station?.WeaponInfo;
                if (info != null && info.effectiveness.antiAir > 0) range = Math.Max(range, info.targetRequirements.maxRange);
            }
            airRangeKnown.Add(definition);
            if (range <= 0) return;
            types.SetAirRange(definition.name, range);
            plugin.Log.LogInfo("Air defense range " + (range / 1000).ToString("0.0") + " km: " + (definition.unitName ?? definition.name));
        }

        private void PublishOwnship()
        {
            bool present = GameManager.GetLocalAircraft(out var own) && own != null && !own.disabled;
            var id = EntityId.None;
            if (present && !tracker.TryGetId(Key(own!), out id)) return; // not sampled yet; next check
            string value = present ? id.ToString() : "";
            if (value == sentOwnship) return;
            sentOwnship = value;
            sink!.Building("nlf-ownship", CotWriter.Ownship(id, present, anchor!, DateTime.UtcNow), false);
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
                    if (unit != null && tracker.TryGetId(Key(unit), out var id)) targetIds.Add(id);
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
                    if (unit == hud.aircraft || list.Contains(unit) || !markers.TryGetValue(unit, out var marker) || marker == null) continue;
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

        private void ObserveBuilding(Building building, double now, DateTime utc)
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
            string fingerprint = state + (int)force;
            if (buildingStates.TryGetValue(key, out var previous) && previous == fingerprint
                && buildingSentAt.TryGetValue(key, out var sentAt) && now - sentAt < 600) return;
            buildingStates[key] = fingerprint;
            buildingSentAt[key] = now;
            var (lat, lon, hae) = GeoAnchor.EcefToGeodetic(anchor!.EnuToEcef(Enu(building.GlobalPosition())));
            var definition = building.definition;
            string name = definition != null && !string.IsNullOrEmpty(definition.unitName) ? definition.unitName : building.unitName ?? "";
            string code = definition != null ? definition.code ?? "" : "";
            sink!.Building("nlf-b-" + key, CotWriter.Building("nlf-b-" + key, force, lat, lon, hae, utc, TimeSpan.FromHours(1), name, code, state), false);
        }

        private void RemoveMissingBuildings(DateTime utc)
        {
            scratch.Clear();
            foreach (var key in buildingStates.Keys) if (!seenBuildings.Contains(key)) scratch.Add(key);
            foreach (var key in scratch)
            {
                buildingStates.Remove(key);
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
            nextAirbases = nextTargets = 0;
            buildingStates.Clear();
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
