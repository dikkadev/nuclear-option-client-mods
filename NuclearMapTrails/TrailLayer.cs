using HarmonyLib;
using NuclearOption.UIStyleSystem;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMapTrails
{
    internal static class TrailDiagnostics
    {
        // Static state spans scenes: each event is emitted at most once per process.
        private static readonly HashSet<string> Reported = new HashSet<string>();

        internal static void Once(string eventKey, string message)
        {
            if (Plugin.Instance != null && Reported.Add(eventKey))
            {
                Plugin.Instance.Log.LogInfo("Map trails: " + message);
            }
        }
    }

    // Native DynamicMap.Update moves icons and applies zoom/centering; trails follow in the same frame.
    [HarmonyPatch(typeof(DynamicMap), "Update")]
    internal static class DynamicMapUpdatePatch
    {
        private static void Postfix(DynamicMap __instance)
        {
            TrailDiagnostics.Once("hook", "DynamicMap.Update hook reached.");
            TrailLayer.Tick(__instance);
        }
    }

    internal sealed class TrailLayer : MonoBehaviour
    {
        private sealed class Trail
        {
            internal readonly TrailHistory History = new TrailHistory();
            internal Aircraft? Aircraft;
            internal Color Color = Color.white;
            internal bool HasHead;
            internal Vector3 Head;
            internal TrailRelation Relation;
            // Time of the newest sample in the history mesh; the head mesh draws everything after it.
            internal float DrawnTime = float.NegativeInfinity;
        }

        private const float ScanInterval = 0.1f;     // icon scan for new samples, 10 Hz
        private const float HistoryInterval = 0.2f;  // stored trail mesh rebuild, 5 Hz

        private static TrailLayer? current;

        private readonly Dictionary<Unit, Trail> trails = new Dictionary<Unit, Trail>();
        private readonly List<Unit> expired = new List<Unit>();
        private DynamicMap map = null!;
        private FactionHQ? hq;
        private TrailGraphic graphic = null!;
        // Short segments from each trail's newest drawn sample to the aircraft, rebuilt every frame.
        private TrailGraphic heads = null!;
        private float nextScan, nextHistory;
        private float lastFactor, lastPixel;
        private int lastRevision = -1;

        internal static void Tick(DynamicMap map)
        {
            var config = Plugin.Instance;
            if (config == null || map == null || map.iconLayer == null)
            {
                return;
            }

            if (current == null || current.map != map)
            {
                if (!config.ShowTrails.Value)
                {
                    return;
                }

                DestroyCurrent();
                current = Create(map);
            }

            current.Refresh(config);
        }

        internal static void ClearCurrent()
        {
            if (current != null)
            {
                current.trails.Clear();
            }
        }

        internal static void ClearCurrentIfDisabled()
        {
            if (Plugin.Instance != null && !Plugin.Instance.ShowTrails.Value)
            {
                ClearCurrent();
            }
        }

        internal static void DestroyCurrent()
        {
            if (current != null)
            {
                Destroy(current.gameObject);
            }

            current = null;
        }

        private static TrailLayer Create(DynamicMap map)
        {
            var layerObject = new GameObject("NuclearMapTrails_Layer", typeof(RectTransform));
            var rectTransform = (RectTransform)layerObject.transform;
            rectTransform.SetParent(map.iconLayer.transform, false);
            // First sibling: icons, markers and labels draw above the trails.
            rectTransform.SetAsFirstSibling();
            rectTransform.anchorMin = rectTransform.anchorMax = rectTransform.pivot = new Vector2(0.5f, 0.5f);
            rectTransform.localPosition = Vector3.zero;
            rectTransform.localRotation = Quaternion.identity;
            rectTransform.localScale = Vector3.one;
            rectTransform.sizeDelta = Vector2.zero;

            var layer = layerObject.AddComponent<TrailLayer>();
            layer.map = map;
            layer.hq = map.HQ;
            layer.graphic = layerObject.AddComponent<TrailGraphic>();
            layer.graphic.raycastTarget = false;

            var headObject = new GameObject("Heads", typeof(RectTransform));
            var headTransform = (RectTransform)headObject.transform;
            headTransform.SetParent(rectTransform, false);
            headTransform.anchorMin = headTransform.anchorMax = headTransform.pivot = new Vector2(0.5f, 0.5f);
            headTransform.localPosition = Vector3.zero;
            headTransform.localRotation = Quaternion.identity;
            headTransform.localScale = Vector3.one;
            headTransform.sizeDelta = Vector2.zero;
            layer.heads = headObject.AddComponent<TrailGraphic>();
            layer.heads.raycastTarget = false;
            TrailDiagnostics.Once("created", "Trail layer created under the native icon layer.");
            return layer;
        }

        private void Refresh(Plugin config)
        {
            if (!config.ShowTrails.Value)
            {
                graphic.enabled = false;
                heads.enabled = false;
                return;
            }

            // A team change must not keep trails recorded with another team's knowledge.
            if (hq != map.HQ)
            {
                trails.Clear();
                hq = map.HQ;
                nextHistory = 0f;
            }

            float now = Time.timeSinceLevelLoad;
            float clock = Time.unscaledTime;
            float duration = config.TrailDuration.Value;
            if (clock >= nextScan)
            {
                // Scanning every map icon is the costly part; samples are at least 0.1 s apart anyway.
                nextScan = clock + ScanInterval;
                Record(config, now);

                expired.Clear();
                foreach (var entry in trails)
                {
                    entry.Value.History.Prune(now, duration);
                    if (entry.Value.History.IsEmpty)
                    {
                        expired.Add(entry.Key);
                    }
                }

                foreach (var unit in expired)
                {
                    trails.Remove(unit);
                }
            }
            else
            {
                UpdateHeads();
            }

            bool visible = !config.ShowOnlyOnMaximizedMap.Value || DynamicMap.mapMaximized;
            graphic.enabled = visible;
            heads.enabled = visible;
            if (!visible)
            {
                // Rebuild as soon as the trails show again.
                nextHistory = 0f;
                return;
            }

            // The long stored trails rebuild at 5 Hz, or at once when zoom, scale or settings change;
            // only the short head segments to the aircraft rebuild every frame.
            float factor = map.mapDisplayFactor;
            float scale = transform.lossyScale.x;
            float pixel = graphic.canvas != null && scale > 0f ? graphic.canvas.scaleFactor / scale : 0f;
            bool viewChanged = factor != lastFactor || config.SettingsRevision != lastRevision ||
                Mathf.Abs(pixel - lastPixel) > 1e-4f * Mathf.Abs(pixel);
            lastFactor = factor;
            lastPixel = pixel;
            lastRevision = config.SettingsRevision;
            if (viewChanged || clock >= nextHistory)
            {
                nextHistory = clock + HistoryInterval;
                Draw(config, now, duration, false);
            }

            Draw(config, now, duration, true);
        }

        // Between icon scans: move each known trail's head to its aircraft's current visible position.
        private void UpdateHeads()
        {
            foreach (var trail in trails.Values)
            {
                if (!trail.HasHead)
                {
                    continue;
                }

                var aircraft = trail.Aircraft;
                if (aircraft == null || aircraft.disabled || !TryGetVisiblePosition(aircraft, trail.Relation, out var position))
                {
                    // The next scan records the loss and breaks the trail.
                    trail.HasHead = false;
                    continue;
                }

                trail.Head = position;
            }
        }

        private void Record(Plugin config, float now)
        {
            foreach (var trail in trails.Values)
            {
                trail.HasHead = false;
            }

            var local = SceneSingleton<CombatHUD>.i != null ? SceneSingleton<CombatHUD>.i.aircraft : null;
            float interval = config.SampleInterval.Value;
            foreach (var icon in map.mapIcons)
            {
                if (!(icon is UnitMapIcon unitIcon) || !(unitIcon.unit is Aircraft aircraft) || aircraft == null || aircraft.disabled)
                {
                    continue;
                }

                // Match the player-name labels: occupied, player-controlled aircraft only.
                if (aircraft.Player == null || aircraft.pilots == null || aircraft.pilots.Length == 0 || aircraft.pilots[0] == null)
                {
                    continue;
                }

                bool isLocal = aircraft == local;
                var unitHq = aircraft.NetworkHQ;
                var relation = Relation(DynamicMap.GetFactionMode(unitHq, checkNoFactionBeforeSpectator: true));
                if (!TrailPolicy.ShouldTrack(relation, isLocal, config.Teams.Value, config.ShowOwnTrail.Value))
                {
                    continue;
                }

                if (!trails.TryGetValue(aircraft, out var trail))
                {
                    trail = new Trail { Aircraft = aircraft };
                    trails.Add(aircraft, trail);
                    TrailDiagnostics.Once("tracked-" + relation, "First " + relation + " player trail started; local=" + isLocal + ".");
                }

                trail.Color = TrailColor(config, relation, isLocal, unitHq);
                trail.Relation = relation;
                if (!TryGetVisiblePosition(aircraft, relation, out var position))
                {
                    // Enemy track is not currently observed: never draw through the unknown section.
                    trail.History.Break();
                    continue;
                }

                trail.HasHead = true;
                trail.Head = position;
                if (trail.History.Due(now, interval))
                {
                    trail.History.Add(position.x, position.y, position.z, now);
                }
            }
        }

        private bool TryGetVisiblePosition(Aircraft aircraft, TrailRelation relation, out Vector3 position)
        {
            if (relation != TrailRelation.Enemy)
            {
                position = aircraft.GlobalPosition().AsVector3();
                return true;
            }

            // Use exactly what the team knows, as the native enemy icon does.
            var tracking = map.HQ != null ? map.HQ.GetTrackingData(aircraft.persistentID) : null;
            if (tracking == null || !tracking.Observed())
            {
                position = default;
                return false;
            }

            position = tracking.GetPosition().AsVector3();
            return true;
        }

        private static TrailRelation Relation(FactionMode mode)
        {
            switch (mode)
            {
                case FactionMode.Friendly:
                    return TrailRelation.Friendly;
                case FactionMode.Enemy:
                    return TrailRelation.Enemy;
                case FactionMode.Spectator:
                    return TrailRelation.Spectator;
                default:
                    return TrailRelation.NoFaction;
            }
        }

        private static Color TrailColor(Plugin config, TrailRelation relation, bool isLocal, FactionHQ? unitHq)
        {
            var theme = ThemeManager.Active.ColorTheme;
            var factionColor = unitHq != null && unitHq.faction != null ? unitHq.faction.color : theme.MapIconNeutral;
            switch (config.ColorMode.Value)
            {
                case TrailColorMode.FactionColor:
                    return factionColor;
                case TrailColorMode.Custom:
                    if (relation == TrailRelation.Spectator && !isLocal)
                    {
                        return factionColor;
                    }

                    return relation == TrailRelation.Enemy ? config.CustomEnemyColor.Value : config.CustomFriendlyColor.Value;
                default:
                    // Mirror the unselected native icon: own aircraft white, otherwise team-relative theme colors.
                    if (isLocal)
                    {
                        return Color.white;
                    }

                    switch (relation)
                    {
                        case TrailRelation.Friendly:
                            return theme.MapIconFriendly;
                        case TrailRelation.Enemy:
                            return theme.MapIconHostile;
                        case TrailRelation.Spectator:
                            return factionColor;
                        default:
                            return theme.MapIconNeutral;
                    }
            }
        }

        // headsOnly false: every stored sample. headsOnly true: from each trail's newest drawn sample to its aircraft.
        private void Draw(Plugin config, float now, float duration, bool headsOnly)
        {
            var target = headsOnly ? heads : graphic;
            float factor = map.mapDisplayFactor;
            bool fade = config.FadeOut.Value;
            float newest = config.NewestOpacity.Value;
            float oldest = config.OldestOpacity.Value;
            bool shade = config.AltitudeBrightness.Value;
            float lowAltitude = config.LowAltitude.Value;
            float highAltitude = config.HighAltitude.Value;
            float lowBrightness = config.LowAltitudeBrightness.Value;

            target.BeginStrokes();
            foreach (var trail in trails.Values)
            {
                var samples = trail.History.Samples;
                bool joinHead = trail.HasHead && samples.Count > 0 && trail.Aircraft != null && !trail.Aircraft.disabled;
                int first = 0;
                if (headsOnly)
                {
                    if (!joinHead)
                    {
                        continue;
                    }

                    // Start at the newest sample already in the history mesh, so the two meshes meet.
                    first = samples.Count - 1;
                    while (first > 0 && samples[first].Time > trail.DrawnTime)
                    {
                        first--;
                    }
                }

                for (int i = first; i < samples.Count; i++)
                {
                    var sample = samples[i];
                    if (sample.StartsRun || i == first)
                    {
                        target.EndRun();
                    }

                    target.AddPoint(
                        new Vector2(sample.X * factor, sample.Z * factor),
                        PointColor(trail.Color, now - sample.Time, sample.Altitude));
                }

                if (headsOnly)
                {
                    // Join the newest stored point to the aircraft's current map position.
                    target.AddPoint(
                        new Vector2(trail.Head.x * factor, trail.Head.z * factor),
                        PointColor(trail.Color, 0f, trail.Head.y));
                }
                else
                {
                    trail.DrawnTime = samples.Count > 0 ? samples[samples.Count - 1].Time : float.NegativeInfinity;
                }

                target.EndRun();
            }

            target.EndStrokes(config.LineWidth.Value);

            Color PointColor(Color baseColor, float age, float altitude)
            {
                float brightness = TrailPolicy.Brightness(altitude, lowAltitude, highAltitude, lowBrightness, shade);
                return new Color(
                    baseColor.r * brightness,
                    baseColor.g * brightness,
                    baseColor.b * brightness,
                    baseColor.a * TrailPolicy.Opacity(age, duration, newest, oldest, fade));
            }
        }

        private void OnDestroy()
        {
            if (current == this)
            {
                current = null;
            }
        }
    }

    // One mesh for all trails, in native icon-layer coordinates.
    internal sealed class TrailGraphic : MaskableGraphic
    {
        private const int MaxPoints = 30000;

        private readonly List<Vector2> points = new List<Vector2>();
        private readonly List<Color32> colors = new List<Color32>();
        private readonly List<int> runEnds = new List<int>();
        private float width = 2f;

        internal void BeginStrokes()
        {
            points.Clear();
            colors.Clear();
            runEnds.Clear();
        }

        internal void AddPoint(Vector2 point, Color color)
        {
            // Two vertices per point; stay well below Unity's 65k UI mesh vertex limit.
            if (points.Count >= MaxPoints)
            {
                return;
            }

            points.Add(point);
            colors.Add(color);
        }

        internal void EndRun()
        {
            if (points.Count > 0 && (runEnds.Count == 0 || runEnds[runEnds.Count - 1] != points.Count))
            {
                runEnds.Add(points.Count);
            }
        }

        internal void EndStrokes(float uiWidth)
        {
            EndRun();
            width = uiWidth;

            // RectMask2D culls by this rect, not the mesh, so it must enclose every point.
            float extent = 0f;
            foreach (var point in points)
            {
                extent = Mathf.Max(extent, Mathf.Abs(point.x), Mathf.Abs(point.y));
            }

            var size = Vector2.one * (2f * extent + 64f);
            if ((rectTransform.sizeDelta - size).sqrMagnitude > 1f)
            {
                rectTransform.sizeDelta = size;
            }

            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float scale = transform.lossyScale.x;
            if (scale <= 0f || canvas == null)
            {
                return;
            }

            // Keep a constant on-screen width at every map zoom level.
            float halfWidth = width * canvas.scaleFactor / scale * 0.5f;
            int start = 0;
            foreach (int end in runEnds)
            {
                AddRun(vh, start, end, halfWidth);
                start = end;
            }
        }

        private void AddRun(VertexHelper vh, int start, int end, float halfWidth)
        {
            if (end - start < 2)
            {
                return;
            }

            int first = vh.currentVertCount;
            for (int i = start; i < end; i++)
            {
                var previous = points[Mathf.Max(start, i - 1)];
                var next = points[Mathf.Min(end - 1, i + 1)];
                var direction = next - previous;
                if (direction.sqrMagnitude < 1e-12f)
                {
                    direction = Vector2.right;
                }

                var normal = new Vector2(-direction.y, direction.x).normalized * halfWidth;
                vh.AddVert(points[i] + normal, colors[i], Vector2.zero);
                vh.AddVert(points[i] - normal, colors[i], Vector2.zero);
            }

            for (int i = 0; i < end - start - 1; i++)
            {
                int vertex = first + i * 2;
                vh.AddTriangle(vertex, vertex + 1, vertex + 2);
                vh.AddTriangle(vertex + 1, vertex + 3, vertex + 2);
            }
        }
    }
}
