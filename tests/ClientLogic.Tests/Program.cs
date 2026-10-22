using System.Globalization;
using NuclearUnits;
using NuclearTrackAge;
using NuclearSounds;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
int passed = 0;
int failed = 0;
void Case(string name, Action assertion)
{
    try { assertion(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL {name}: {error.Message}"); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'");
}
void Near(float expected, float actual)
{
    if (!float.IsFinite(actual) || Math.Abs(expected - actual) > .001f)
        throw new Exception($"Expected {expected} ± .001, got {actual}");
}

Case("100 m/s explicit speed conversions", () => {
    Equal("360km/h", HudUnitFormatter.FormatSpeed(100, SpeedUnit.KilometersPerHour));
    Equal("194kt", HudUnitFormatter.FormatSpeed(100, SpeedUnit.Knots));
    Equal("224mph", HudUnitFormatter.FormatSpeed(100, SpeedUnit.MilesPerHour));
});
Case("explicit altitude conversion and small-value precision", () => {
    Equal("3281ft", HudUnitFormatter.FormatAltitude(1000, AltitudeUnit.Feet));
    Equal("9.5m", HudUnitFormatter.FormatAltitude(9.5f, AltitudeUnit.Meters));
    Equal("10m", HudUnitFormatter.FormatAltitude(10, AltitudeUnit.Meters));
});
Case("signed climb and descent conversion", () => {
    Equal("+1969fpm", HudUnitFormatter.FormatClimbRate(10, ClimbRateUnit.FeetPerMinute));
    Equal("-1969fpm", HudUnitFormatter.FormatClimbRate(-10, ClimbRateUnit.FeetPerMinute));
    Equal("+1.0m/s", HudUnitFormatter.FormatClimbRate(1, ClimbRateUnit.MetersPerSecond));
});
Case("metric range precision and imperial yards/nautical miles", () => {
    Equal("500m", HudUnitFormatter.FormatDistance(500, DistanceUnit.Metric));
    Equal("1.5km", HudUnitFormatter.FormatDistance(1500, DistanceUnit.Metric));
    Equal("15km", HudUnitFormatter.FormatDistance(15000, DistanceUnit.Metric));
    Equal("109yd", HudUnitFormatter.FormatDistance(100, DistanceUnit.Imperial));
    Equal("1.0nm", HudUnitFormatter.FormatDistance(1852, DistanceUnit.Imperial));
});
Case("GameDefault delegates to boundary converter", () => {
    Equal("stock speed", HudUnitFormatter.FormatSpeed(42, SpeedUnit.GameDefault));
    Equal("stock altitude", HudUnitFormatter.FormatAltitude(42, AltitudeUnit.GameDefault));
    Equal("stock climb", HudUnitFormatter.FormatClimbRate(42, ClimbRateUnit.GameDefault));
    Equal("stock distance", HudUnitFormatter.FormatDistance(42, DistanceUnit.GameDefault));
});
Case("secondary disabled and duplicate policy", () => {
    Equal<string?>(null, HudUnitFormatter.FormatSecondarySpeed(100, SecondarySpeedUnit.Disabled, "194kt"));
    Equal<string?>(null, HudUnitFormatter.FormatSecondarySpeed(100, SecondarySpeedUnit.Knots, "194kt"));
    Equal<string?>("224mph", HudUnitFormatter.FormatSecondarySpeed(100, SecondarySpeedUnit.MilesPerHour, "194kt"));
});
Case("secondary prefixed readout policy", () => {
    Equal<string?>(null, HudUnitFormatter.FormatSecondarySpeedLabel("SPD ", 100, SecondarySpeedUnit.Knots, "SPD 194kt"));
    Equal<string?>("SPD 224mph", HudUnitFormatter.FormatSecondarySpeedLabel("SPD ", 100, SecondarySpeedUnit.MilesPerHour, "SPD 194kt"));
});

(Unit unit, FactionHQ hq) Track(float age)
{
    Plugin.Instance = new Plugin();
    var hq = new FactionHQ();
    hq.Tracks[7] = new TrackingInfo { lastSpottedTime = 0 };
    SceneSingleton<DynamicMap>.i = new DynamicMap { HQ = hq };
    UnityEngine.Time.timeSinceLevelLoad = age;
    return (new Unit { NetworkHQ = new FactionHQ(), persistentID = 7 }, hq);
}
Case("friendly tracks excluded", () => {
    var (unit, hq) = Track(40); unit.NetworkHQ = hq;
    Equal("RNG 5km", TrackAgeFormatter.ApplyInlineAge("RNG 5km", unit));
});
Case("missing HQ and absent tracking data excluded", () => {
    var (unit, hq) = Track(40); hq.Tracks.Clear();
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    SceneSingleton<DynamicMap>.i = null;
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});
Case("accurate position excluded unless configured otherwise", () => {
    var (unit, hq) = Track(12); hq.Accurate = true;
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    Plugin.Instance.ShowOnlyOutdatedTracks.Value = false;
    Equal("RNG | AGE 12s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});
Case("minimum age boundary", () => {
    var (unit, _) = Track(3.999f);
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    UnityEngine.Time.timeSinceLevelLoad = 4;
    Equal("RNG | AGE 4s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});
Case("STALE threshold boundary", () => {
    var (unit, _) = Track(34.999f);
    Equal("RNG | AGE 34s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    UnityEngine.Time.timeSinceLevelLoad = 35;
    Equal("RNG | STALE 35s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});
Case("disabled surface strips existing inline and line labels", () => {
    var (unit, _) = Track(40);
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG | AGE 12s", unit, showLabel: false));
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG | STALE 35s", unit, showLabel: false));
    Equal("TARGET", TrackAgeFormatter.ApplyLineAge("TARGET\nAGE 12s", unit, showLabel: false));
    Equal("TARGET", TrackAgeFormatter.ApplyLineAge("TARGET\nSTALE 35s", unit, showLabel: false));
});
Case("global disable removes old annotation", () => {
    var (unit, _) = Track(40); Plugin.Instance.Enabled.Value = false;
    Equal("RNG", TrackAgeFormatter.ApplyInlineAge("RNG | STALE 35s", unit));
});
Case("repeated inline and line refresh does not duplicate", () => {
    var (unit, _) = Track(40);
    var inline = "RNG"; var line = "TARGET";
    for (int i = 0; i < 10; i++) {
        inline = TrackAgeFormatter.ApplyInlineAge(inline, unit);
        line = TrackAgeFormatter.ApplyLineAge(line, unit);
    }
    Equal("RNG | STALE 40s", inline);
    Equal("TARGET\nSTALE 40s", line);
});
Case("duration minute/hour formatting", () => {
    var (unit, _) = Track(60);
    Equal("RNG | STALE 1m00s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    UnityEngine.Time.timeSinceLevelLoad = 195;
    Equal("RNG | STALE 3m15s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    UnityEngine.Time.timeSinceLevelLoad = 3600;
    Equal("RNG | STALE 1h00m", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
    UnityEngine.Time.timeSinceLevelLoad = 7260;
    Equal("RNG | STALE 2h01m", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});
Case("future tracking timestamp clamps age to zero", () => {
    var (unit, hq) = Track(10); hq.Tracks[7].lastSpottedTime = 20;
    Plugin.Instance.MinimumAgeSeconds.Value = 0;
    Equal("RNG | AGE 0s", TrackAgeFormatter.ApplyInlineAge("RNG", unit));
});

Case("fuel uses actual delayed sample interval", () => Near(140, FuelTimeEstimate.Calculate(.8f, .7f, 20)));
Case("fuel refuel or no burn yields unavailable estimate", () => {
    Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(.7f, .8f, 20));
    Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(.7f, .7f, 20));
});
Case("fuel invalid elapsed values yield unavailable estimate", () => {
    foreach (float elapsed in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(.8f, .7f, elapsed));
});
Case("fuel invalid sample values yield unavailable estimate", () => {
    foreach (float fuel in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity }) {
        Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(fuel, .7f, 20));
        Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(.8f, fuel, 20));
    }
    Equal(float.PositiveInfinity, FuelTimeEstimate.Calculate(.8f, -.1f, 20));
});
Case("empty remaining fuel with measurable burn yields zero", () => Near(0, FuelTimeEstimate.Calculate(.1f, 0, 20)));

DeliveryChecks.Run(Case);
MfdMapChecks.Run(Case);
MapZoomChecks.Run(Case);
TapHoldChecks.Run(Case);
ControllerGestureChecks.Run(Case);
TargetingChecks.Run(Case);
DeviceZoomChecks.Run(Case);
MapTrailChecks.Run(Case);
SatelliteMapChecks.Run(Case);
Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
