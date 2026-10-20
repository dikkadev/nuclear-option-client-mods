using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;
using NuclearModMigration;

namespace NuclearSounds
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInIncompatibility("dev.dikka.nucleartacticaltools")]
    [BepInDependency("dev.dikka.nucleartargeting", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private Harmony? _harmony;
        private System.Reflection.EventInfo? _targetSelectionEvent;
        private System.Action<int, int>? _targetSelectionHandler;
        private bool _designationReady;
        private int _designationBridgeWarnings;

        private void OnTargetSelectionAdded(int previousCount, int currentCount)
        {
            if (!_designationReady) return;
            try { DesignateToneState.OnTargetListChanged(previousCount, currentCount); }
            catch (System.Exception error)
            {
                if (_designationBridgeWarnings++ < 3)
                    Logger.LogWarning($"Designation audio bridge skipped: {error.GetType().Name}: {error.Message}");
            }
        }

        private void SubscribeTargeting()
        {
            const string targetingId = "dev.dikka.nucleartargeting";
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(targetingId, out var targeting)) return;
            try
            {
                var selectionEvent = targeting.Instance.GetType().GetEvent("TargetSelectionAdded",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (selectionEvent == null || selectionEvent.EventHandlerType != typeof(System.Action<int, int>))
                {
                    Logger.LogWarning("Targeting designation bridge unavailable; deselection tones remain enabled.");
                    return;
                }
                _targetSelectionHandler = OnTargetSelectionAdded;
                selectionEvent.AddEventHandler(null, _targetSelectionHandler);
                _targetSelectionEvent = selectionEvent;
            }
            catch (System.Exception error)
            {
                _targetSelectionHandler = null;
                Logger.LogWarning($"Targeting designation bridge unavailable: {error.GetType().Name}: {error.Message}");
            }
        }

        private void UnsubscribeTargeting()
        {
            try { _targetSelectionEvent?.RemoveEventHandler(null, _targetSelectionHandler); }
            catch (System.Exception error) { Logger.LogWarning($"Targeting designation bridge cleanup: {error.GetType().Name}: {error.Message}"); }
            finally { _targetSelectionEvent = null; _targetSelectionHandler = null; }
        }
        private ConfigEntry<bool> _playAudioTest = null!;
        private bool _audioTestRunning;

        internal ConfigEntry<bool> EnableMissileVoiceWarnings { get; private set; } = null!;
        internal ConfigEntry<float> MissileVoiceRepeatSeconds { get; private set; } = null!;
        internal ConfigEntry<bool> EnableDesignateTones { get; private set; } = null!;
        internal ConfigEntry<float> DesignateToneVolume { get; private set; } = null!;
        internal ConfigEntry<bool> EnableFlavorCallouts { get; private set; } = null!;
        internal ConfigEntry<float> RadarLockFlavorChance { get; private set; } = null!;
        internal ConfigEntry<float> DeathFlavorChance { get; private set; } = null!;
        internal ConfigEntry<bool> EnableLowerGearVoice { get; private set; } = null!;
        internal ConfigEntry<bool> EnableOverspeedVoice { get; private set; } = null!;
        internal ConfigEntry<bool> EnableStallVoice { get; private set; } = null!;
        internal ConfigEntry<bool> EnableTerrainWarnings { get; private set; } = null!;
        internal ConfigEntry<float> TerrainWarningVolume { get; private set; } = null!;
        internal ConfigEntry<float> MinimumRadarAltitudeMeters { get; private set; } = null!;
        internal ConfigEntry<float> MinimumAirspeedMetersPerSecond { get; private set; } = null!;
        internal ConfigEntry<float> TerrainLookaheadSeconds { get; private set; } = null!;
        internal ConfigEntry<float> TerrainClearanceMeters { get; private set; } = null!;
        internal ConfigEntry<float> TerrainRepeatSeconds { get; private set; } = null!;
        internal ConfigEntry<float> PullUpLookaheadSeconds { get; private set; } = null!;
        internal ConfigEntry<float> PullUpClearanceMeters { get; private set; } = null!;
        internal ConfigEntry<float> PullUpRepeatSeconds { get; private set; } = null!;

        internal ConfigEntry<bool> EnableFuelWarnings { get; private set; } = null!;
        internal ConfigEntry<float> FuelWarningMinutes { get; private set; } = null!;
        internal ConfigEntry<float> BingoFuelMinutes { get; private set; } = null!;
        internal ConfigEntry<float> FuelWarningMinutesAfterburner { get; private set; } = null!;
        internal ConfigEntry<float> BingoFuelMinutesAfterburner { get; private set; } = null!;
        internal ConfigEntry<float> FuelWarningUpdateRateSeconds { get; private set; } = null!;

        private void Awake()
        {
            Instance = this;
            BindConfig();
            EnableFuelWarnings.SettingChanged += OnFuelEnabledChanged;
            // This is an action, never a persisted request to play at startup.
            _playAudioTest.Value = false;
            _playAudioTest.SettingChanged += OnAudioTestRequested;

            DesignateToneState.Initialize();
            _designationReady = true;
            FuelWarningState.Initialize();
            SubscribeTargeting();
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo($"Harmony installation owns {_harmony.GetPatchedMethods().Count()} patched methods; effective settings: {string.Join("; ", Config.Select(entry => $"[{entry.Key.Section}] {entry.Key.Key}={System.Convert.ToString(entry.Value.BoxedValue, System.Globalization.CultureInfo.InvariantCulture)?.Replace("\r", " ").Replace("\n", " ")}"))}");
            var build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Plugin).Assembly)?.InformationalVersion;
            Logger.LogInfo($"Loaded {MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} (build {build}); preloading audio.");
            StartCoroutine(InitializeFeatures());
        }

        private System.Collections.IEnumerator InitializeFeatures()
        {
            yield return AudioLoader.PreloadAssets();
            MissileVoiceWarnings.Initialize();
            FlavorCallouts.Initialize();
            BuiltInVoiceWarnings.Initialize();
            FuelWarningState.LoadAudio();
            TerrainWarningMonitor.Initialize(this);

            Logger.LogInfo("Audio feature initialization finished.");
        }

        private void Start()
            => Logger.LogInfo($"Plugin Start reached at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");

        private void OnDestroy()
        {
            Logger.LogInfo($"Plugin OnDestroy cleanup at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");
            _designationReady = false;
            UnsubscribeTargeting();
            EnableFuelWarnings.SettingChanged -= OnFuelEnabledChanged;
            _playAudioTest.SettingChanged -= OnAudioTestRequested;
            StopAllCoroutines();
            AudioDiagnostics.Clear();
            _harmony?.UnpatchSelf();
            FuelWarningState.DestroyLabel();
            DesignateToneState.Clear();
            AudioLoader.Clear();
        }

        private void OnDisable() => FuelWarningState.SyncExistingLabel();

        private void OnFuelEnabledChanged(object sender, System.EventArgs args)
            => FuelWarningState.SyncExistingLabel();

        private void OnAudioTestRequested(object sender, System.EventArgs args)
        {
            if (!_playAudioTest.Value) return;
            _playAudioTest.Value = false;
            if (_audioTestRunning) return;
            _audioTestRunning = true;
            StartCoroutine(RunAudioTest());
        }

        private System.Collections.IEnumerator RunAudioTest()
        {
            try { yield return AudioDiagnostics.PlayTest(); }
            finally { _audioTestRunning = false; }
        }

        private void BindConfig()
        {
            var legacy = new LegacyConfig(Config, Logger);
            _playAudioTest = Config.Bind("Audio Test", "Play Audio Test", false,
                "Tick once, then close the F1 settings menu. After two seconds, plays a test tone followed by the custom lower-gear voice through the normal game Interface audio channel. Resets automatically; records mixer and clip diagnostics in Player.log.");

            EnableMissileVoiceWarnings = legacy.Bind("Warnings", "Enable Missile Voice Warnings", true, "Play voice callouts for incoming missile seeker types.");
            MissileVoiceRepeatSeconds = legacy.Bind("Warnings", "Missile Voice Repeat Seconds", 2.5f, "Minimum time between missile voice callouts. Increase this if flare/notch warnings feel too rapid.");
            EnableDesignateTones = legacy.Bind("Warnings", "Enable Designate Tones", true, "Play short UI tones when target designation is gained or lost.");
            DesignateToneVolume = legacy.Bind("Warnings", "Designate Tone Volume", 0.55f, "Volume for target designation lock/lost tones.");
            EnableFlavorCallouts = legacy.Bind("Warnings", "Enable Flavor Callouts", true, "Allow rare alternate joke callouts for specific situations.");
            RadarLockFlavorChance = legacy.Bind("Warnings", "Radar Lock Flavor Chance", 0.03f, "Chance for the long lock-on line to replace the normal radar-missile warning.");
            DeathFlavorChance = legacy.Bind("Warnings", "Death Flavor Chance", 0.12f, "Chance for the short death callout on player aircraft loss when it does not look like a high-confidence crash.");
            EnableLowerGearVoice = legacy.Bind("Warnings", "Enable Lower Gear Voice", true, "Replace the stock lower-gear voice with the custom clip.");
            EnableOverspeedVoice = legacy.Bind("Warnings", "Enable Overspeed Voice", true, "Replace the stock overspeed voice with the custom clip.");
            EnableStallVoice = legacy.Bind("Warnings", "Enable Stall Voice", true, "Replace the stock stall voice with the custom clip.");

            EnableFuelWarnings = legacy.Bind("Warnings", "Enable Fuel Time Warning", true, "Show estimated fuel time and play low-fuel callouts.");
            FuelWarningMinutes = legacy.Bind("Warnings", "Low Fuel Minutes", 7f, "Play the low-fuel callout when estimated time remaining falls below this threshold.");
            BingoFuelMinutes = legacy.Bind("Warnings", "Bingo Fuel Minutes", 3f, "Play the bingo-fuel callout when estimated time remaining falls below this threshold.");
            FuelWarningMinutesAfterburner = legacy.Bind("Warnings", "Low Fuel Minutes Afterburner", 1.5f, "Low-fuel voice threshold to use while throttle is pinned in afterburner-like operation.");
            BingoFuelMinutesAfterburner = legacy.Bind("Warnings", "Bingo Fuel Minutes Afterburner", 0.75f, "Bingo-fuel voice threshold to use while throttle is pinned in afterburner-like operation.");
            FuelWarningUpdateRateSeconds = legacy.Bind("Warnings", "Fuel Update Seconds", 10f, "How often the mod recalculates fuel time and warning thresholds.");

            EnableTerrainWarnings = legacy.Bind("Terrain Warning", "Enable Terrain Warnings", true, "Play custom terrain and pull-up warnings based on projected impact.");
            TerrainWarningVolume = legacy.Bind("Terrain Warning", "Warning Volume", 1f, "Volume for custom terrain and pull-up warnings.");
            MinimumRadarAltitudeMeters = legacy.Bind("Terrain Warning", "Minimum Radar Altitude (m)", 20f, "Suppress terrain warnings below this radar altitude.");
            MinimumAirspeedMetersPerSecond = legacy.Bind("Terrain Warning", "Minimum Airspeed (m/s)", 35f, "Suppress terrain warnings below this airspeed.");
            TerrainLookaheadSeconds = legacy.Bind("Terrain Warning", "Terrain Lookahead Seconds", 4.5f, "How far ahead to sweep along the current movement vector for the terrain caution warning.");
            TerrainClearanceMeters = legacy.Bind("Terrain Warning", "Terrain Clearance Radius (m)", 10f, "Warn when the predicted path comes within this radius of terrain during the terrain lookahead window.");
            TerrainRepeatSeconds = legacy.Bind("Terrain Warning", "Terrain Repeat Seconds", 2.25f, "Repeat interval while terrain warning remains active.");
            PullUpLookaheadSeconds = legacy.Bind("Terrain Warning", "Pull Up Lookahead Seconds", 2.5f, "How far ahead to sweep along the current movement vector for the urgent pull-up warning.");
            PullUpClearanceMeters = legacy.Bind("Terrain Warning", "Pull Up Clearance Radius (m)", 5f, "Warn with pull-up when the predicted path comes within this radius of terrain during the pull-up lookahead window.");
            PullUpRepeatSeconds = legacy.Bind("Terrain Warning", "Pull Up Repeat Seconds", 1.1f, "Repeat interval while the pull-up warning remains active.");
        }
    }
}
