using System;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearBindings;
using NuclearModMigration;
using UnityEngine;

namespace NuclearTargeting
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInIncompatibility("dev.dikka.nucleartacticaltools")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        private Harmony? _harmony;

        public static event Action<int, int>? TargetSelectionAdded;

        internal ConfigEntry<bool> EnableAlternativeTargeting { get; private set; } = null!;
        internal ConfigEntry<bool> NextTargetOrder { get; private set; } = null!;
        internal ConfigEntry<float> TargetSelectionThreshold { get; private set; } = null!;
        internal ConfigEntry<bool> UseFixedTapTargetCap { get; private set; } = null!;
        internal ConfigEntry<int> FixedTapTargetCap { get; private set; } = null!;
        internal InputBinding ToggleTapTargetCap { get; private set; } = null!;

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

        private void Update()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            // Skipped frames make the binding wait for release, so a press held into flight cannot toggle.
            if (!EnableAlternativeTargeting.Value || Cursor.visible || CursorManager.GetFlags() != CursorFlags.None ||
                hud == null || hud.aircraft == null || hud.aircraft.disabled || hud.aircraft.HasEjected())
                return;
            if (ToggleTapTargetCap.IsDown())
            {
                UseFixedTapTargetCap.Value = !UseFixedTapTargetCap.Value;
                Logger.LogInfo("Tap target cap: " + (UseFixedTapTargetCap.Value ? "fixed " : "laser capacity ") + GetTapTargetCap(hud.aircraft));
            }
        }

        internal int GetTapTargetCap(Aircraft aircraft)
        {
            if (UseFixedTapTargetCap.Value) return Mathf.Clamp(FixedTapTargetCap.Value, 1, 128);
            var designator = aircraft.GetLaserDesignator();
            // Native designators disable their Behaviour while idle; that is not missing capacity.
            return designator != null ? Mathf.Clamp(designator.GetMaxTargets(), 1, 128) : 1;
        }

        private void OnDestroy()
        {
            Logger.LogInfo($"Plugin OnDestroy cleanup at frame {UnityEngine.Time.frameCount}, realtime {UnityEngine.Time.realtimeSinceStartup:F2}s.");
            _harmony?.UnpatchSelf();
            TargetSelectionAdded = null;
        }

        internal static void NotifyTargetSelectionAdded(int previousCount, int currentCount)
            => TargetSelectionAdded?.Invoke(previousCount, currentCount);

        private void BindConfig()
        {
            // Snapshot old settings before any Bind can save the new schema.
            var capMigration = new TapCapConfigMigration();
            try
            {
                capMigration = TapCapConfigMigration.Read(Config.ConfigFilePath,
                    System.IO.Path.Combine(Paths.ConfigPath, "dev.dikka.nucleartacticaltools.cfg"));
            }
            catch (Exception error) { Logger.LogWarning("Tap-cap settings import unavailable: " + error.Message); }
            var migration = new LegacyConfig(Config, Logger);
            EnableAlternativeTargeting = migration.Bind("Targeting", "Enable Alternative Targeting", true, "Enable this mod's target selector and capped taps. Disable to restore stock selection and disable the mode shortcut.");
            NextTargetOrder = migration.Bind("Targeting", "Next Target Order", false, "Cycle through eligible targets instead of always preferring the highest-priority one.");
            TargetSelectionThreshold = migration.Bind("Targeting", "Selection Radius", 200f, "Maximum HUD-space distance from the designator to consider a target selectable.");
            UseFixedTapTargetCap = Config.Bind("Targeting", "Use Fixed Tap Target Cap", false,
                "Override the aircraft's actual laser-designation capacity with Fixed Tap Target Cap. Off uses native capacity (minimum 1). A tap refreshes or adds the target, removing oldest selections as needed; holding still adds targets without this cap.");
            FixedTapTargetCap = Config.Bind("Targeting", "Fixed Tap Target Cap", 1,
                new ConfigDescription("Tap-selection limit for every aircraft while the fixed override is enabled. Native target synchronization supports up to 128 targets.", new AcceptableValueRange<int>(1, 128)));
            capMigration.Apply(UseFixedTapTargetCap, FixedTapTargetCap);
            ToggleTapTargetCap = InputBinding.Bind(Config, Logger, "Controls", "Toggle Fixed Tap Target Cap", "",
                "Optional binding to switch between native laser capacity and the fixed override. Unbound by default; ignored in menus, text entry, the full map and without focus. Does not change existing selections until the next tap.");
        }
    }
}
