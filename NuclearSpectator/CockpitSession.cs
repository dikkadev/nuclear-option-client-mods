using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NuclearSpectator
{
    // The game builds an aircraft's HUD gauges, damage display and cockpit screens only
    // for your own aircraft (Aircraft.SetupLocalPlayerAndUI and Cockpit's initializer).
    // While you have no aircraft and look from another aircraft's cockpit, this builds
    // the same native prefabs for that aircraft and removes them again when you leave.
    internal sealed class CockpitSession
    {
        private static readonly AccessTools.FieldRef<Cockpit, GameObject> TacScreenPrefab = AccessTools.FieldRefAccess<Cockpit, GameObject>("tacScreenUIPrefab");
        private static readonly AccessTools.FieldRef<Cockpit, TacScreen> NativeTacScreen = AccessTools.FieldRefAccess<Cockpit, TacScreen>("tacScreen");
        private static readonly AccessTools.FieldRef<StatusDisplay, AudioSource> DamageAlertSource = AccessTools.FieldRefAccess<StatusDisplay, AudioSource>("damageAlertSource");

        private const string MinimapGuid = "dev.dikka.nuclearminimap";

        private readonly List<GameObject> roots = new List<GameObject>();
        private Aircraft? aircraft;
        private Aircraft? failed;
        private int builtVersion = -1;
        private bool hudShown;
        private StatusDisplay? status;
        private AudioSource? alertSource;
        private Cockpit? cockpit;
        private bool cockpitTouched;
        private bool cockpitWasEnabled;
        private SpectatorTargetCam? targetCam;
        private TacScreen? screen;
        private int builds;
        private static readonly AccessTools.FieldRef<TacScreen, Camera> ScreenCamera = AccessTools.FieldRefAccess<TacScreen, Camera>("cam");
        private static readonly AccessTools.FieldRef<TacScreen, RenderTexture> ScreenTexture = AccessTools.FieldRefAccess<TacScreen, RenderTexture>("renderTexture");
        private static readonly AccessTools.FieldRef<Cockpit, Renderer> ScreenRenderer = AccessTools.FieldRefAccess<Cockpit, Renderer>("tacScreenRender");

        // True while this session shows the spectated aircraft's real HUD.
        internal bool HudActive => hudShown && aircraft != null;

        internal void Update()
        {
            try
            {
                var plugin = Plugin.Instance;
                var target = plugin != null && plugin.Enabled.Value && (plugin.ShowHud.Value || plugin.ShowScreens.Value || plugin.AnimateControls.Value)
                    ? FindTarget() : null;
                if (target == null)
                {
                    failed = null;
                    Teardown();
                    return;
                }
                if (ReferenceEquals(target, failed) && builtVersion == plugin!.CockpitVersion) return;
                if (!ReferenceEquals(target, aircraft) || builtVersion != plugin!.CockpitVersion)
                {
                    Teardown();
                    failed = null;
                    Build(target, plugin!);
                }
                if (targetCam != null)
                {
                    try { targetCam.Update(); }
                    catch (Exception error)
                    {
                        Plugin.Report("spectator target camera", error);
                        targetCam.Dispose();
                        targetCam = null;
                    }
                }
            }
            catch (Exception error)
            {
                Plugin.Report("cockpit view", error);
                failed = aircraft;
                Teardown();
            }
        }

        internal void Dispose()
        {
            try { Teardown(); }
            catch (Exception error) { Plugin.Report("cockpit view cleanup", error); }
        }

        private static Aircraft? FindTarget()
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.currentState == null || cam.currentState != cam.cockpitState) return null;
            if (!(cam.followingUnit is Aircraft target) || target == null) return Skip("the cockpit view follows no aircraft");
            if (target.disabled) return Skip("the followed aircraft is disabled");
            if (target.cockpit == null) return Skip("the followed aircraft has no cockpit part");
            if (GameManager.GetLocalAircraft(out _)) return Skip("you have an aircraft of your own");
            var hud = SceneSingleton<CombatHUD>.i;
            if (hud != null && hud.aircraft != null) return Skip("CombatHUD still holds an aircraft");
            if (target.pilots == null || target.pilots.Length == 0 || target.pilots[0] == null || target.pilots[0].dead) return Skip("the pilot is dead");
            return target;
        }

        // Explains once per reason why a cockpit view gets no spectator cockpit.
        private static Aircraft? Skip(string reason)
        {
            Plugin.Once("cockpit-skip:" + reason, "Spectator cockpit not built: " + reason + ".");
            return null;
        }

        private void Build(Aircraft target, Settings plugin)
        {
            aircraft = target;
            builtVersion = plugin.CockpitVersion;
            HudScope.Begin(target, roots, helmet: plugin.ShowHud.Value);
            if (plugin.ShowHud.Value) Guard("cockpit HUD", () => BuildHud(target));
            if (plugin.ShowScreens.Value || plugin.AnimateControls.Value)
                cockpit = target.GetComponentsInChildren<Cockpit>(true).FirstOrDefault();
            if (plugin.ShowScreens.Value && cockpit != null) Guard("cockpit screens", () => BuildScreens(target, cockpit));
            if (plugin.AnimateControls.Value && cockpit != null)
            {
                cockpitWasEnabled = cockpit.enabled;
                cockpitTouched = true;
                cockpit.enabled = true;
            }
            Plugin.Once("cockpit:" + target.definition.unitName, Describe(target));
            if (builds++ < 10)
                Plugin.Log?.LogInfo($"Spectator cockpit built for {target.definition.unitName}: hud={hudShown}, roots={roots.Count}, cockpit={(cockpit != null ? cockpit.name : "none")}, controls={cockpitTouched}, targetCamera={targetCam != null}; {DescribeScreen()}");
        }

        private void Guard(string scope, Action action)
        {
            int before = roots.Count;
            try { action(); }
            catch (Exception error)
            {
                Plugin.Report(scope, error);
                Discard(before);
            }
        }

        // Removes the roots from `first` on: their handlers, the damage voice source and the objects.
        private void Discard(int first)
        {
            var failedRoots = roots.GetRange(first, roots.Count - first);
            roots.RemoveRange(first, roots.Count - first);
            var owned = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var root in failedRoots)
                if (root != null)
                    foreach (var component in root.GetComponentsInChildren<Component>(true))
                        owned.Add(component);
            try { DelegateScrubber.Remove(aircraft, owned); }
            catch (Exception error) { Plugin.Report("cockpit view handler cleanup", error); }
            if (status != null && owned.Contains(status))
            {
                alertSource ??= DamageAlertSource(status);
                status = null;
            }
            if (status == null && alertSource != null)
            {
                UnityEngine.Object.Destroy(alertSource);
                alertSource = null;
            }
            foreach (var root in failedRoots)
                if (root != null) UnityEngine.Object.Destroy(root);
        }

        private void BuildHud(Aircraft target)
        {
            var flight = SceneSingleton<FlightHud>.i;
            if (flight == null) return;
            flight.SetAircraft(target);
            hudShown = true;
            var parameters = target.GetAircraftParameters();
            if (parameters != null && parameters.StatusDisplay != null)
            {
                var display = UnityEngine.Object.Instantiate(parameters.StatusDisplay, Vector3.zero, Quaternion.identity);
                roots.Add(display);
                status = display.GetComponent<StatusDisplay>();
                if (status != null)
                {
                    status.Initialize(target);
                    alertSource = DamageAlertSource(status);
                }
            }
            if (parameters != null && parameters.HUDExtras != null)
                roots.Add(UnityEngine.Object.Instantiate(parameters.HUDExtras, flight.GetHUDCenter()));
            if (!DynamicMap.mapMaximized) FlightHud.EnableCanvas(enable: true);
        }

        private void BuildScreens(Aircraft target, Cockpit owner)
        {
            if (NativeTacScreen(owner) != null) return;
            var prefab = TacScreenPrefab(owner);
            if (prefab == null) return;
            var instance = UnityEngine.Object.Instantiate(prefab, owner.transform);
            roots.Add(instance);
            screen = instance.GetComponent<TacScreen>();
            if (screen == null) throw new InvalidOperationException("Cockpit screen prefab has no TacScreen.");
            screen.Initialize(target, owner);
            // NuclearMinimap shows its cockpit map on this screen area the whole time you spectate.
            if (MinimapShowsScreen())
                Plugin.Once("targetcam-minimap", "Spectator target camera not built: NuclearMinimap shows its cockpit map on that screen while you spectate.");
            else if (Plugin.Instance != null && Plugin.Instance.ShowTargetCamera.Value)
            {
                try { targetCam = SpectatorTargetCam.Create(target, screen, roots); }
                catch (Exception error) { Plugin.Report("spectator target camera", error); }
            }
        }

        // NuclearMinimap 0.4.4+ with its cockpit map on. Read through its config, without a
        // dependency on the plugin.
        private static bool MinimapShowsScreen()
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(MinimapGuid, out var info)
                || info.Metadata.Version < new Version(0, 4, 4) || info.Instance == null) return false;
            var config = info.Instance.Config;
            return Flag("General", "Enabled") && Flag("Display", "Show Cockpit Minimap");
            bool Flag(string section, string key) => !config.TryGetEntry<bool>(section, key, out var entry) || entry.Value;
        }

        private void Teardown()
        {
            if (aircraft is null && roots.Count == 0 && !cockpitTouched && !hudShown) return;
            targetCam?.Dispose();
            targetCam = null;
            screen = null;
            Discard(0);
            status = null;
            alertSource = null;
            if (cockpitTouched && cockpit != null) cockpit.enabled = cockpitWasEnabled;
            cockpitTouched = false;
            cockpit = null;
            if (hudShown) HideHudIfStillSpectating();
            hudShown = false;
            HudScope.End();
            aircraft = null;
        }

        // Other camera views set the HUD canvas themselves; only the cockpit view of an
        // aircraft that is not yours relies on this session to have shown it.
        private static void HideHudIfStillSpectating()
        {
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.currentState != cam.cockpitState || GameManager.GetLocalAircraft(out _)) return;
            FlightHud.EnableCanvas(enable: false);
        }

        // Shows what the screen camera renders into and what the cockpit screen mesh samples.
        private string DescribeScreen()
        {
            if (screen == null) return "no cockpit screen";
            var cam = ScreenCamera(screen);
            var texture = ScreenTexture(screen);
            string camera = cam == null ? "camera missing" : $"camera enabled={cam.enabled && cam.gameObject.activeInHierarchy}, target={(cam.targetTexture != null ? cam.targetTexture.name : "none")}";
            string mesh = "screen mesh missing";
            var renderer = cockpit != null ? ScreenRenderer(cockpit) : null;
            if (renderer != null)
            {
                var material = renderer.sharedMaterial;
                var textures = material == null ? "" : string.Join("/", material.GetTexturePropertyNames()
                    .Select(name => material.GetTexture(name)).Where(item => item != null).Select(item => item.name).Distinct());
                mesh = $"screen mesh enabled={renderer.enabled && renderer.gameObject.activeInHierarchy}, material={(material != null ? material.name : "none")}, textures={textures}";
            }
            return $"screen texture={(texture != null ? texture.name : "none")}, {camera}, {mesh}";
        }

        private string Describe(Aircraft target)
        {
            var parts = roots.Where(root => root != null).Select(root =>
            {
                var types = root.GetComponentsInChildren<MonoBehaviour>(true).Where(item => item != null)
                    .Select(item => item.GetType().Name).Distinct().OrderBy(name => name, StringComparer.Ordinal);
                return $"{root.name} [{string.Join(", ", types)}]";
            });
            return $"Spectator cockpit for {target.definition.unitName}: hud={hudShown}, controls={cockpitTouched}, roots: {string.Join("; ", parts)}";
        }
    }
}
