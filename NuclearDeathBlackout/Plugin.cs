using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Linq;

namespace NuclearDeathBlackout
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;

        private Harmony? _harmony;

        internal ConfigEntry<bool> Enabled { get; private set; } = null!;
        internal ConfigEntry<float> BlackoutSeconds { get; private set; } = null!;
        internal ConfigEntry<float> FadeInSeconds { get; private set; } = null!;
        internal ConfigEntry<float> FadeOutSeconds { get; private set; } = null!;
        internal ConfigEntry<bool> MuteAudio { get; private set; } = null!;
        internal ConfigEntry<float> AudioRestoreSeconds { get; private set; } = null!;
        internal ManualLogSource Log => Logger;

        private void Awake()
        {
            Instance = this;
            BindConfig();
            Enabled.SettingChanged += OnEnabledChanged;
            DeathBlackoutController.EnsureCreated();

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
            Enabled.SettingChanged -= OnEnabledChanged;
            _harmony?.UnpatchSelf();
            DeathBlackoutController.DestroyInstance();
            UnitDisabledPatch.Reset();
        }

        private void Update() => UnitDisabledPatch.ObserveLocalAircraft();

        private void OnDisable()
        {
            DeathBlackoutController.CancelBlackout();
            UnitDisabledPatch.Reset();
        }

        private void OnEnabledChanged(object sender, System.EventArgs args)
        {
            if (!Enabled.Value) DeathBlackoutController.CancelBlackout();
        }

        private void BindConfig()
        {
            Enabled = Config.Bind(
                "Death Blackout",
                "Enabled",
                true,
                "Enable the realistic death blackout effect."
            );

            BlackoutSeconds = Config.Bind(
                "Death Blackout",
                "Blackout Seconds",
                3.5f,
                new ConfigDescription(
                    "Seconds to keep the screen fully black after the local pilot dies.",
                    new AcceptableValueRange<float>(0.1f, 10f)
                )
            );

            FadeInSeconds = Config.Bind(
                "Death Blackout",
                "Fade In Seconds",
                0f,
                new ConfigDescription(
                    "Seconds for the screen to go black. Use 0 for an instant cut.",
                    new AcceptableValueRange<float>(0f, 5f)
                )
            );

            FadeOutSeconds = Config.Bind(
                "Death Blackout",
                "Fade Out Seconds",
                0.35f,
                new ConfigDescription(
                    "Seconds for the black screen to fade away after the hold time.",
                    new AcceptableValueRange<float>(0f, 5f)
                )
            );

            MuteAudio = Config.Bind(
                "Death Blackout",
                "Mute Audio",
                true,
                "Mute all game audio while the death blackout is active."
            );

            AudioRestoreSeconds = Config.Bind(
                "Death Blackout",
                "Audio Restore Seconds",
                0.35f,
                new ConfigDescription(
                    "Seconds for audio volume to fade back after the blackout hold time. Use 0 for an instant restore.",
                    new AcceptableValueRange<float>(0f, 5f)
                )
            );
        }
    }
}
