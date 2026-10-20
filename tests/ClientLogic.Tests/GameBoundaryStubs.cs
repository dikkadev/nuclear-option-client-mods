// Test-only boundaries. These deliberately make no claim about real game API compatibility.
namespace UnityEngine
{
    internal static class Mathf
    {
        internal static float Abs(float value) => Math.Abs(value);
        internal static float Max(float a, float b) => Math.Max(a, b);
        internal static int FloorToInt(float value) => (int)Math.Floor(value);
    }
    internal static class Time { internal static float timeSinceLevelLoad; }
}
internal static class UnitConverter
{
    internal static string SpeedReading(float value) => "stock speed";
    internal static string ClimbRateReading(float value) => "stock climb";
    internal static string DistanceReading(float value) => "stock distance";
    internal static string AltitudeReading(float value) => "stock altitude";
}
internal sealed class Unit { internal FactionHQ? NetworkHQ; internal int persistentID; }
internal sealed class TrackingInfo { internal float lastSpottedTime; }
internal sealed class FactionHQ
{
    internal readonly Dictionary<int, TrackingInfo> Tracks = new();
    internal bool Accurate;
    internal TrackingInfo GetTrackingData(int id) => Tracks.TryGetValue(id, out var track) ? track : null!;
    internal bool IsTargetPositionAccurate(Unit unit, float threshold) => Accurate;
}
internal sealed class DynamicMap { internal FactionHQ? HQ; }
internal static class SceneSingleton<T> { internal static T? i; }
namespace NuclearTrackAge
{
    internal sealed class Setting<T>(T value) { internal T Value = value; }
    internal sealed class Plugin
    {
        internal static Plugin Instance = new();
        internal Setting<bool> Enabled = new(true);
        internal Setting<bool> ShowOnlyOutdatedTracks = new(true);
        internal Setting<float> MinimumAgeSeconds = new(4f);
        internal Setting<float> StaleThresholdSeconds = new(35f);
    }
}
