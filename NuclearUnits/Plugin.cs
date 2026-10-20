using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using System.Linq;

namespace NuclearUnits
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;

        private Harmony? _harmony;

        internal ConfigEntry<SecondarySpeedUnit> HudSecondarySpeedUnit { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> AltitudeHudRadarAlt { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> AltitudeHudAbsoluteAlt { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> SpeedGaugeAirspeed { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> BasicInstrumentsAirspeed { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> BasicInstrumentsAltitude { get; private set; } = null!;
        internal ConfigEntry<ClimbRateUnit> BasicInstrumentsClimbRate { get; private set; } = null!;
        internal ConfigEntry<ClimbRateUnit> ClimbrateHudValue { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> VirtualMfdSpeed { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> VirtualMfdAltitude { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> LandingScreenAltitude { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> LandingScreenSpeed { get; private set; } = null!;
        internal ConfigEntry<ClimbRateUnit> LandingScreenVerticalSpeed { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> LandingScreenHorizontalSpeed { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> TargetMarkerSpeed { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> TargetMarkerAltitude { get; private set; } = null!;
        internal ConfigEntry<DistanceUnit> TargetMarkerRange { get; private set; } = null!;
        internal ConfigEntry<DistanceUnit> TargetScreenDistance { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> TargetScreenAltitude { get; private set; } = null!;
        internal ConfigEntry<AltitudeUnit> TargetScreenRelativeAltitude { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> TargetScreenSpeed { get; private set; } = null!;
        internal ConfigEntry<SpeedUnit> TargetScreenRelativeSpeed { get; private set; } = null!;

        private void Awake()
        {
            Instance = this;
            BindConfig();

            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Harmony installation owns {_harmony.GetPatchedMethods().Count()} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={System.Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");

            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build})");
        }

        private void Start()
            => Logger.LogInfo($"Plugin Start reached at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");

        private void OnDestroy()
        {
            Logger.LogInfo($"Plugin OnDestroy cleanup at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");
            _harmony?.UnpatchSelf();
        }

        private void BindConfig()
        {
            HudSecondarySpeedUnit = Bind("Dual Speed HUD", "Secondary Speed Unit", SecondarySpeedUnit.Disabled, "Optional second speed unit shown on a smaller line below supported HUD speed readouts.");

            AltitudeHudRadarAlt = Bind("Altitude HUD", "Radar Altitude", AltitudeUnit.GameDefault, "Unit override for the radar altitude readout.");
            AltitudeHudAbsoluteAlt = Bind("Altitude HUD", "Absolute Altitude", AltitudeUnit.GameDefault, "Unit override for the absolute altitude readout.");

            SpeedGaugeAirspeed = Bind("Speed Gauge", "Airspeed", SpeedUnit.GameDefault, "Unit override for the main speed gauge airspeed readout.");

            BasicInstrumentsAirspeed = Bind("Basic Flight Instruments", "Airspeed", SpeedUnit.GameDefault, "Unit override for the basic instruments airspeed readout.");
            BasicInstrumentsAltitude = Bind("Basic Flight Instruments", "Altitude", AltitudeUnit.GameDefault, "Unit override for the basic instruments altitude readout.");
            BasicInstrumentsClimbRate = Bind("Basic Flight Instruments", "Climb Rate", ClimbRateUnit.GameDefault, "Unit override for the basic instruments climb rate readout.");

            ClimbrateHudValue = Bind("Climb Rate HUD", "Climb Rate", ClimbRateUnit.GameDefault, "Unit override for the standalone climb rate readout.");

            VirtualMfdSpeed = Bind("Virtual MFD", "Speed", SpeedUnit.GameDefault, "Unit override for the virtual MFD speed readout.");
            VirtualMfdAltitude = Bind("Virtual MFD", "Altitude", AltitudeUnit.GameDefault, "Unit override for the virtual MFD altitude readout.");

            LandingScreenAltitude = Bind("Landing Screen", "Altitude", AltitudeUnit.GameDefault, "Unit override for the landing screen altitude readout.");
            LandingScreenSpeed = Bind("Landing Screen", "Speed", SpeedUnit.GameDefault, "Unit override for the landing screen speed readout.");
            LandingScreenVerticalSpeed = Bind("Landing Screen", "Vertical Speed", ClimbRateUnit.GameDefault, "Unit override for the landing screen vertical speed readout.");
            LandingScreenHorizontalSpeed = Bind("Landing Screen", "Horizontal Speed", SpeedUnit.GameDefault, "Unit override for the landing screen horizontal speed readout.");

            TargetMarkerSpeed = Bind("Target Marker", "Speed", SpeedUnit.GameDefault, "Unit override for target marker speed labels.");
            TargetMarkerAltitude = Bind("Target Marker", "Altitude", AltitudeUnit.GameDefault, "Unit override for target marker altitude labels.");
            TargetMarkerRange = Bind("Target Marker", "Range", DistanceUnit.GameDefault, "Unit override for target marker range labels.");

            TargetScreenDistance = Bind("Target Screen", "Distance", DistanceUnit.GameDefault, "Unit override for target screen distance labels.");
            TargetScreenAltitude = Bind("Target Screen", "Altitude", AltitudeUnit.GameDefault, "Unit override for target screen altitude labels.");
            TargetScreenRelativeAltitude = Bind("Target Screen", "Relative Altitude", AltitudeUnit.GameDefault, "Unit override for target screen relative altitude labels.");
            TargetScreenSpeed = Bind("Target Screen", "Speed", SpeedUnit.GameDefault, "Unit override for target screen speed labels.");
            TargetScreenRelativeSpeed = Bind("Target Screen", "Relative Speed", SpeedUnit.GameDefault, "Unit override for target screen relative speed labels.");
        }

        private ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string description)
        {
            return Config.Bind(section, key, defaultValue, description);
        }
    }
}
