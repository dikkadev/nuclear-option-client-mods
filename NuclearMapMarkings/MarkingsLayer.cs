using HarmonyLib;
using NuclearOption.UIStyleSystem;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearMapMarkings
{
    internal static class MarkingsDiagnostics
    {
        // Static state spans scenes: each event is emitted at most once per process.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        internal static void Once(string eventKey, string message)
        {
            if (Plugin.Instance != null && Reported.Add(eventKey))
            {
                Plugin.Instance.Log.LogInfo("Map markings: " + message);
            }
        }
    }

    // Native DynamicMap.Update moves icons and applies zoom/centering; markings follow in the same frame.
    [HarmonyPatch(typeof(DynamicMap), "Update")]
    internal static class DynamicMapUpdatePatch
    {
        private static void Postfix(DynamicMap __instance)
        {
            MarkingsDiagnostics.Once("hook", "DynamicMap.Update hook reached.");
            MarkingsLayer.Tick(__instance);
        }
    }

    internal sealed class MarkingsLayer : MonoBehaviour
    {
        private const float DashPixels = 6f;
        private const float GapPixels = 4f;
        private const float TickPixels = 4f;
        private const float DotPixels = 2f;
        private const int RingSegments = 128;

        private sealed class Track
        {
            internal readonly MotionEstimate Motion = new MotionEstimate();
            internal MapPoint Position;
        }

        private static MarkingsLayer? current;

        private readonly Dictionary<Unit, Track> tracks = new Dictionary<Unit, Track>();
        private readonly List<Unit> expired = new List<Unit>();
        private readonly HashSet<Unit> emphasized = new HashSet<Unit>();
        private readonly ApproachEnd[] ends = new ApproachEnd[2];
        private readonly List<float> ticks = new List<float>();
        private readonly List<MapPoint> points = new List<MapPoint>();
        private DynamicMap map = null!;
        private FactionHQ? hq;
        private MarkingsGraphic runways = null!;
        private MarkingsGraphic rings = null!;
        private MarkingsGraphic paths = null!;

        internal static void Tick(DynamicMap map)
        {
            var config = Plugin.Instance;
            if (config == null || map == null || map.iconLayer == null)
            {
                return;
            }

            if (current == null || current.map != map)
            {
                if (!config.Enabled.Value)
                {
                    return;
                }

                DestroyCurrent();
                current = Create(map);
            }

            current.Refresh(config);
        }

        internal static void DestroyCurrent()
        {
            if (current != null)
            {
                Destroy(current.gameObject);
            }

            current = null;
        }

        private static MarkingsLayer Create(DynamicMap map)
        {
            var root = NewRect("NuclearMapMarkings_Layer", map.iconLayer.transform);
            // First sibling: icons, markers and labels draw above the markings.
            root.SetAsFirstSibling();

            var layer = root.gameObject.AddComponent<MarkingsLayer>();
            layer.map = map;
            layer.hq = map.HQ;
            layer.runways = NewGraphic("Runways", root);
            layer.rings = NewGraphic("RangeRings", root);
            layer.paths = NewGraphic("PredictedPaths", root);
            MarkingsDiagnostics.Once("created", "Marking layer created under the native icon layer.");
            return layer;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localPosition = Vector3.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        private static MarkingsGraphic NewGraphic(string name, Transform parent)
        {
            var graphic = NewRect(name, parent).gameObject.AddComponent<MarkingsGraphic>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private void Refresh(Plugin config)
        {
            // A team change must not keep motion recorded with another team's knowledge.
            if (hq != map.HQ)
            {
                tracks.Clear();
                hq = map.HQ;
            }

            bool full = DynamicMap.mapMaximized;
            bool enabled = config.Enabled.Value;
            bool showRunways = enabled && (full ? config.RunwaysOnFullMap.Value : config.RunwaysOnMinimap.Value);
            bool showRings = enabled && (full ? config.RingsOnFullMap.Value : config.RingsOnMinimap.Value);
            bool showPaths = enabled && (full ? config.PathsOnFullMap.Value : config.PathsOnMinimap.Value);
            float width = config.LineWidth.Value;
            float factor = map.mapDisplayFactor;

            Run(runways, showRunways, "runways", () => DrawRunways(config, factor, width));
            Run(rings, showRings, "rings", () => DrawRings(config, factor, width));

            // Keep sampling motion while paths are hidden, so they are ready when the view changes.
            bool recordPaths = enabled && (config.PathsOnFullMap.Value || config.PathsOnMinimap.Value);
            if (!recordPaths)
            {
                tracks.Clear();
            }

            else
            {
                try
                {
                    Record(Time.timeSinceLevelLoad);
                }
                catch (System.Exception error)
                {
                    tracks.Clear();
                    MarkingsDiagnostics.Once("error-record", "Recording motion failed: " + error);
                }
            }

            Run(paths, showPaths, "paths", () => DrawPaths(config, factor, width));
        }

        // Each marking has its own failure scope so one bad object cannot remove the others.
        private static void Run(MarkingsGraphic graphic, bool visible, string name, System.Action draw)
        {
            graphic.enabled = visible;
            if (!visible)
            {
                return;
            }

            try
            {
                graphic.Begin();
                draw();
                graphic.End();
            }
            catch (System.Exception error)
            {
                graphic.Begin();
                graphic.End();
                MarkingsDiagnostics.Once("error-" + name, "Drawing " + name + " failed: " + error);
            }
        }

        private void DrawRunways(Plugin config, float factor, float width)
        {
            var theme = ThemeManager.Active.ColorTheme;
            float minApproach = config.MinApproachLength.Value * 1000f;
            float maxApproach = config.MaxApproachLength.Value * 1000f;
            float tickSpacing = config.TickSpacing.Value * 1000f;
            foreach (var airbase in FactionRegistry.airbaseLookup.Values)
            {
                if (airbase == null || airbase.disabled || airbase.runways == null)
                {
                    continue;
                }

                var mode = DynamicMap.GetFactionMode(airbase.CurrentHQ, checkNoFactionBeforeSpectator: true);
                if (!ShowBase(config.RunwayBases.Value, mode))
                {
                    continue;
                }

                var color = BaseColor(theme, mode, airbase.CurrentHQ);
                color.a *= config.RunwayOpacity.Value;
                foreach (var runway in airbase.runways)
                {
                    if (runway == null || runway.Start == null || runway.End == null)
                    {
                        continue;
                    }

                    var start = Flat(runway.Start.GlobalPosition().AsVector3());
                    var end = Flat(runway.End.GlobalPosition().AsVector3());
                    runways.Band(Layer(start, factor), Layer(end, factor), Mathf.Max(runway.GetWidth(), 20f) * factor, 3f, color);
                    float runwayLength = (end - start).Length;
                    MarkingsDiagnostics.Once("runway-" + airbase.GetInstanceID() + "-" + runway.index,
                        $"runway at {BaseName(airbase)}: length {runwayLength:F0} m, width {runway.GetWidth():F0} m, landing={runway.Landing}, reversible={runway.Reversable}.");
                    if (!config.ShowApproaches.Value)
                    {
                        continue;
                    }

                    float approach = MarkingsGeometry.ApproachLength(runwayLength, config.ShortRunway.Value, config.LongRunway.Value, minApproach, maxApproach);
                    if (approach <= 0f)
                    {
                        continue;
                    }

                    MarkingsGeometry.TickDistances(approach, tickSpacing, ticks);
                    int count = MarkingsGeometry.ApproachEnds(start, end, runway.Landing, runway.Reversable, ends);
                    for (int i = 0; i < count; i++)
                    {
                        var landing = ends[i];
                        var threshold = Layer(landing.Threshold, factor);
                        var direction = new Vector2(landing.Direction.X, landing.Direction.Z);
                        runways.Line(threshold, Layer(landing.PointAt(approach), factor), width, color, DashPixels, GapPixels);
                        foreach (float distance in ticks)
                        {
                            runways.Tick(Layer(landing.PointAt(distance), factor), direction, TickPixels, width, color);
                        }
                    }
                }
            }
        }

        private void DrawRings(Plugin config, float factor, float width)
        {
            var aircraft = LocalAircraft();
            if (aircraft == null)
            {
                return;
            }

            var center = Flat(aircraft.GlobalPosition().AsVector3());
            var color = new Color(1f, 1f, 1f, config.RingOpacity.Value);
            foreach (float kilometres in config.RingDistancesKm)
            {
                MarkingsGeometry.Circle(center, kilometres * 1000f, RingSegments, points);
                for (int i = 1; i < points.Count; i++)
                {
                    rings.Line(Layer(points[i - 1], factor), Layer(points[i], factor), width, color);
                }
            }
        }

        private void Record(float now)
        {
            foreach (var icon in map.mapIcons)
            {
                if (!(icon is UnitMapIcon unitIcon) || unitIcon.unit == null || unitIcon.unit.disabled || Kind(unitIcon.unit) == null)
                {
                    continue;
                }

                var unit = unitIcon.unit;
                if (!TryGetVisiblePosition(unit, out var position))
                {
                    // Lost enemy track: forget its motion rather than predict from stale data.
                    tracks.Remove(unit);
                    continue;
                }

                if (!tracks.TryGetValue(unit, out var track))
                {
                    track = new Track();
                    tracks.Add(unit, track);
                }

                track.Position = position;
                track.Motion.Add(position, now, Kind(unit)!.Value);
            }

            expired.Clear();
            foreach (var entry in tracks)
            {
                if (entry.Key == null || entry.Key.disabled || now - entry.Value.Motion.LastSeen > 2f)
                {
                    expired.Add(entry.Key!);
                }
            }

            foreach (var unit in expired)
            {
                tracks.Remove(unit);
            }
        }

        private void DrawPaths(Plugin config, float factor, float width)
        {
            var local = LocalAircraft();
            CollectEmphasized(local);
            float opacity = config.PathOpacity.Value;
            foreach (var entry in tracks)
            {
                var unit = entry.Key;
                var kind = Kind(unit);
                if (kind == null || !entry.Value.Motion.TryGet(out var velocity, out float turnRate))
                {
                    continue;
                }

                float minimumSpeed = kind == UnitMotionKind.Aircraft ? 20f : 1.5f;
                if (velocity.Length < minimumSpeed)
                {
                    continue;
                }

                bool isLong = unit == local || emphasized.Contains(unit);
                if (!isLong && !config.ShortPathsForAll.Value)
                {
                    continue;
                }

                float surface = kind == UnitMotionKind.Aircraft ? 1f : 3f;
                float horizon = (isLong ? config.LongPathSeconds.Value : config.ShortPathSeconds.Value) * surface;
                float step = surface;
                MarkingsGeometry.PredictPath(entry.Value.Position, velocity, turnRate, config.TurnFadeSeconds.Value * surface, horizon, step, points);

                var color = UnitColor(unit, unit == local);
                color.a *= opacity * (isLong ? 1f : 0.7f);
                for (int i = 1; i < points.Count; i++)
                {
                    paths.Line(Layer(points[i - 1], factor), Layer(points[i], factor), isLong ? width : width * 0.75f, color);
                }

                if (!isLong)
                {
                    continue;
                }

                // A dot every 10 s for aircraft, every 30 s for vehicles and ships.
                float dotEvery = 10f * surface;
                for (int i = 1; i < points.Count; i++)
                {
                    float t = Mathf.Min(horizon, i * step);
                    if (Mathf.Abs(t / dotEvery - Mathf.Round(t / dotEvery)) < 1e-3f)
                    {
                        paths.Dot(Layer(points[i], factor), DotPixels, color);
                    }
                }
            }
        }

        private void CollectEmphasized(Aircraft? local)
        {
            emphasized.Clear();
            foreach (var icon in map.selectedIcons)
            {
                if (icon is UnitMapIcon unitIcon && unitIcon.unit != null)
                {
                    emphasized.Add(unitIcon.unit);
                }
            }

            var hud = SceneSingleton<CombatHUD>.i;
            var targets = local != null && hud != null ? hud.GetTargetList() : null;
            if (targets != null)
            {
                foreach (var target in targets)
                {
                    if (target != null)
                    {
                        emphasized.Add(target);
                    }
                }
            }
        }

        private bool TryGetVisiblePosition(Unit unit, out MapPoint position)
        {
            var mode = DynamicMap.GetFactionMode(unit.NetworkHQ, checkNoFactionBeforeSpectator: true);
            if (mode != FactionMode.Enemy)
            {
                position = Flat(unit.GlobalPosition().AsVector3());
                return true;
            }

            // Use exactly what the team knows, as the native enemy icon does.
            var tracking = map.HQ != null ? map.HQ.GetTrackingData(unit.persistentID) : null;
            if (tracking == null || !tracking.Observed())
            {
                position = default;
                return false;
            }

            position = Flat(tracking.GetPosition().AsVector3());
            return true;
        }

        private static UnitMotionKind? Kind(Unit unit)
        {
            if (unit is Aircraft)
            {
                return UnitMotionKind.Aircraft;
            }

            if (unit is GroundVehicle || unit is Ship)
            {
                return UnitMotionKind.Surface;
            }

            // Missiles, buildings and other units get no predicted path.
            return null;
        }

        private static bool ShowBase(RunwayBases bases, FactionMode mode)
        {
            switch (mode)
            {
                case FactionMode.Friendly:
                case FactionMode.Spectator:
                    return true;
                case FactionMode.Enemy:
                    return bases == RunwayBases.All;
                default:
                    return bases != RunwayBases.Friendly;
            }
        }

        private static Color BaseColor(ColorTheme theme, FactionMode mode, FactionHQ? baseHq)
        {
            switch (mode)
            {
                case FactionMode.Friendly:
                    return theme.MapIconFriendly;
                case FactionMode.Enemy:
                    return theme.MapIconHostile;
                case FactionMode.Spectator:
                    return baseHq != null && baseHq.faction != null ? baseHq.faction.color : theme.MapIconNeutral;
                default:
                    return theme.MapIconNeutral;
            }
        }

        // Mirror the unselected native icon: own aircraft white, otherwise team-relative theme colors.
        private static Color UnitColor(Unit unit, bool isLocal)
        {
            if (isLocal)
            {
                return Color.white;
            }

            var theme = ThemeManager.Active.ColorTheme;
            var unitHq = unit.NetworkHQ;
            switch (DynamicMap.GetFactionMode(unitHq, checkNoFactionBeforeSpectator: true))
            {
                case FactionMode.Friendly:
                    return theme.MapIconFriendly;
                case FactionMode.Enemy:
                    return theme.MapIconHostile;
                case FactionMode.Spectator:
                    return unitHq != null && unitHq.faction != null ? unitHq.faction.color : theme.MapIconNeutral;
                default:
                    return theme.MapIconNeutral;
            }
        }

        private static string BaseName(Airbase airbase)
        {
            var saved = airbase.SavedAirbase;
            return saved != null && !string.IsNullOrEmpty(saved.DisplayName) ? saved.DisplayName : airbase.name;
        }

        private static Aircraft? LocalAircraft()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            var aircraft = hud != null ? hud.aircraft : null;
            return aircraft != null && !aircraft.disabled ? aircraft : null;
        }

        private static MapPoint Flat(Vector3 global) => new MapPoint(global.x, global.z);

        private static Vector2 Layer(MapPoint point, float factor) => new Vector2(point.X * factor, point.Z * factor);

        private void OnDestroy()
        {
            if (current == this)
            {
                current = null;
            }
        }
    }
}
