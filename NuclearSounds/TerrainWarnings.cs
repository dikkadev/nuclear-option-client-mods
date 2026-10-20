using HarmonyLib;
using UnityEngine;

namespace NuclearSounds
{
    internal sealed class TerrainWarningMonitor : MonoBehaviour
    {
        private enum WarningType
        {
            None,
            Terrain,
            PullUp,
        }

        private static TerrainWarningMonitor? _instance;

        private AudioSource _audioSource = null!;
        private AudioClip? _pullUpClip;
        private AudioClip? _terrainClip;
        private WarningType _activeWarning;
        private float _nextRepeatTime;

        internal static void Initialize(Plugin plugin)
        {
            if (_instance != null)
            {
                return;
            }

            _instance = plugin.gameObject.AddComponent<TerrainWarningMonitor>();
        }

        private void Awake()
        {
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = false;
            _audioSource.spatialBlend = 0f;
            _audioSource.ignoreListenerPause = true;

            _pullUpClip = AudioLoader.LoadFromPluginAssets("pull up.mp3");
            _terrainClip = AudioLoader.LoadFromPluginAssets("terrain.mp3");
        }

        private void Update()
        {
            if (!Plugin.Instance.EnableTerrainWarnings.Value)
            {
                _activeWarning = WarningType.None;
                return;
            }

            _audioSource.volume = Mathf.Clamp01(Plugin.Instance.TerrainWarningVolume.Value);
            var aircraft = CombatHUD.i != null ? CombatHUD.i.aircraft : null;
            if (aircraft == null || aircraft.rb == null)
            {
                _activeWarning = WarningType.None;
                return;
            }

            var radarAltitude = aircraft.radarAlt;
            var verticalSpeed = Vector3.Dot(aircraft.rb.velocity, Vector3.up);
            var airspeed = aircraft.speed;
            var warning = EvaluateWarning(aircraft, aircraft.rb.velocity, radarAltitude, verticalSpeed, airspeed);
            if (warning == WarningType.None)
            {
                _activeWarning = WarningType.None;
                return;
            }

            var now = Time.unscaledTime;
            if (warning != _activeWarning || now >= _nextRepeatTime)
            {
                if (TryPlay(warning))
                {
                    _activeWarning = warning;
                    _nextRepeatTime = now + GetRepeatSeconds(warning);
                }
            }
        }

        private static WarningType EvaluateWarning(Aircraft aircraft, Vector3 velocity, float radarAltitude, float verticalSpeed, float airspeed)
        {
            if (!float.IsFinite(radarAltitude) || !float.IsFinite(verticalSpeed) || !float.IsFinite(airspeed))
            {
                return WarningType.None;
            }

            if (radarAltitude <= Plugin.Instance.MinimumRadarAltitudeMeters.Value)
            {
                return WarningType.None;
            }

            if (airspeed < Plugin.Instance.MinimumAirspeedMetersPerSecond.Value)
            {
                return WarningType.None;
            }

            if (TerrainReflection.TryReadBool(aircraft, "onGround") || TerrainReflection.TryReadBool(aircraft, "landed"))
            {
                return WarningType.None;
            }

            if (HasTerrainConflict(aircraft, velocity, Plugin.Instance.PullUpLookaheadSeconds.Value, Plugin.Instance.PullUpClearanceMeters.Value))
            {
                return WarningType.PullUp;
            }

            if (HasTerrainConflict(aircraft, velocity, Plugin.Instance.TerrainLookaheadSeconds.Value, Plugin.Instance.TerrainClearanceMeters.Value))
            {
                return WarningType.Terrain;
            }

            return WarningType.None;
        }

        private static bool HasTerrainConflict(Aircraft aircraft, Vector3 velocity, float lookaheadSeconds, float clearanceMeters)
        {
            var speed = velocity.magnitude;
            if (speed < Plugin.Instance.MinimumAirspeedMetersPerSecond.Value)
            {
                return false;
            }

            var radius = Mathf.Max(0.5f, clearanceMeters);
            var maxDistance = speed * Mathf.Max(0.25f, lookaheadSeconds);
            var hits = Physics.SphereCastAll(aircraft.transform.position, radius, velocity / speed, maxDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0)
            {
                return false;
            }

            var aircraftRoot = aircraft.transform.root;
            foreach (var hit in hits)
            {
                if (hit.collider == null || hit.collider.transform.root == aircraftRoot)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private bool TryPlay(WarningType warning)
        {
            var clip = warning == WarningType.PullUp ? _pullUpClip : _terrainClip;
            if (clip == null)
            {
                return false;
            }

            _audioSource.Stop();
            _audioSource.clip = clip;
            _audioSource.Play();
            return true;
        }

        private static float GetRepeatSeconds(WarningType warning)
        {
            return warning == WarningType.PullUp
                ? Mathf.Max(0.25f, Plugin.Instance.PullUpRepeatSeconds.Value)
                : Mathf.Max(0.25f, Plugin.Instance.TerrainRepeatSeconds.Value);
        }
    }

    internal static class TerrainReflection
    {
        internal static bool TryReadBool(object target, string memberName)
        {
            const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var targetType = target.GetType();

            var property = targetType.GetProperty(memberName, Flags);
            if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
            {
                var value = property.GetValue(target);
                return value is bool && (bool)value;
            }

            var field = targetType.GetField(memberName, Flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                var value = field.GetValue(target);
                return value is bool && (bool)value;
            }

            return false;
        }
    }
}
