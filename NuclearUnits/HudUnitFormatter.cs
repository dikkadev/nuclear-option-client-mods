using UnityEngine;

namespace NuclearUnits
{
    public enum SpeedUnit
    {
        GameDefault,
        KilometersPerHour,
        Knots,
        MilesPerHour,
    }

    public enum SecondarySpeedUnit
    {
        Disabled,
        GameDefault,
        KilometersPerHour,
        Knots,
        MilesPerHour,
    }

    public enum AltitudeUnit
    {
        GameDefault,
        Meters,
        Feet,
    }

    public enum ClimbRateUnit
    {
        GameDefault,
        MetersPerSecond,
        FeetPerMinute,
    }

    public enum DistanceUnit
    {
        GameDefault,
        Metric,
        Imperial,
    }

    internal static class HudUnitFormatter
    {
        public static string FormatSpeed(float speed, SpeedUnit unit)
        {
            return unit switch
            {
                SpeedUnit.GameDefault => UnitConverter.SpeedReading(speed),
                SpeedUnit.KilometersPerHour => $"{speed * 3.6f:F0}km/h",
                SpeedUnit.Knots => $"{speed * 1.94384f:F0}kt",
                SpeedUnit.MilesPerHour => $"{speed * 2.23694f:F0}mph",
                _ => UnitConverter.SpeedReading(speed),
            };
        }

        public static string? FormatSecondarySpeed(float speed, SecondarySpeedUnit secondaryUnit, string primary)
        {
            var resolvedSecondaryUnit = ResolveSecondarySpeedUnit(secondaryUnit);
            if (!resolvedSecondaryUnit.HasValue)
            {
                return null;
            }

            var secondary = FormatSpeed(speed, resolvedSecondaryUnit.Value);
            if (secondary == primary)
            {
                return null;
            }

            return secondary;
        }

        public static string FormatClimbRate(float speed, ClimbRateUnit unit)
        {
            return unit switch
            {
                ClimbRateUnit.GameDefault => UnitConverter.ClimbRateReading(speed),
                ClimbRateUnit.MetersPerSecond => FormatSigned(speed, Mathf.Abs(speed) < 10f ? $"{speed:F1}m/s" : $"{speed:F0}m/s"),
                ClimbRateUnit.FeetPerMinute => FormatSigned(speed, $"{speed * 60f * 3.28084f:F0}fpm"),
                _ => UnitConverter.ClimbRateReading(speed),
            };
        }

        public static string FormatDistance(float distance, DistanceUnit unit)
        {
            return unit switch
            {
                DistanceUnit.GameDefault => UnitConverter.DistanceReading(distance),
                DistanceUnit.Metric => FormatMetricDistance(distance),
                DistanceUnit.Imperial => FormatImperialDistance(distance),
                _ => UnitConverter.DistanceReading(distance),
            };
        }

        public static string FormatSpeedLabel(string prefix, float speed, SpeedUnit unit)
        {
            return prefix + FormatSpeed(speed, unit);
        }

        public static string? FormatSecondarySpeedLabel(string prefix, float speed, SecondarySpeedUnit secondaryUnit, string primaryWithPrefix)
        {
            var secondary = FormatSecondarySpeed(speed, secondaryUnit, primaryWithPrefix[prefix.Length..]);
            return secondary == null ? null : prefix + secondary;
        }

        public static string FormatAltitude(float altitude, AltitudeUnit unit)
        {
            return unit switch
            {
                AltitudeUnit.GameDefault => UnitConverter.AltitudeReading(altitude),
                AltitudeUnit.Meters => Mathf.Abs(altitude) < 10f ? $"{altitude:F1}m" : $"{altitude:F0}m",
                AltitudeUnit.Feet => $"{altitude * 3.28084f:F0}ft",
                _ => UnitConverter.AltitudeReading(altitude),
            };
        }

        public static string FormatAltitudeLabel(string prefix, float altitude, AltitudeUnit unit)
        {
            return prefix + FormatAltitude(altitude, unit);
        }

        public static string FormatDistanceLabel(string prefix, float distance, DistanceUnit unit)
        {
            return prefix + FormatDistance(distance, unit);
        }

        public static string FormatClimbRateLabel(string prefix, float speed, ClimbRateUnit unit)
        {
            return prefix + FormatClimbRate(speed, unit);
        }

        private static string FormatMetricDistance(float distance)
        {
            if (distance > 10000f)
            {
                return $"{distance * 0.001f:F0}km";
            }

            if (distance > 1000f)
            {
                return $"{distance * 0.001f:F1}km";
            }

            return $"{distance:F0}m";
        }

        private static string FormatImperialDistance(float distance)
        {
            var yards = distance * 1.09361f;
            if (yards < 1000f)
            {
                return $"{yards:F0}yd";
            }

            return $"{distance * 0.000539957f:F1}nm";
        }

        private static string FormatSigned(float speed, string formatted)
        {
            if (speed > 0.5f)
            {
                return "+" + formatted;
            }

            return formatted;
        }

        private static SpeedUnit? ResolveSecondarySpeedUnit(SecondarySpeedUnit unit)
        {
            return unit switch
            {
                SecondarySpeedUnit.Disabled => null,
                SecondarySpeedUnit.GameDefault => SpeedUnit.GameDefault,
                SecondarySpeedUnit.KilometersPerHour => SpeedUnit.KilometersPerHour,
                SecondarySpeedUnit.Knots => SpeedUnit.Knots,
                SecondarySpeedUnit.MilesPerHour => SpeedUnit.MilesPerHour,
                _ => null,
            };
        }
    }
}
