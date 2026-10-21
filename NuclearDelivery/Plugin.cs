using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearMfd;
using UnityEngine;

namespace NuclearDelivery
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin? Instance;
        internal ConfigEntry<bool> Enabled = null!, Cockpit = null!, TargetCamera = null!, LandingCamera = null!, TargetMarkers = null!, AlliedMarkers = null!;
        internal ConfigEntry<float> Recent = null!, Timeout = null!, CockpitScale = null!, CameraScale = null!, OffsetX = null!, OffsetY = null!, MarkerScale = null!;
        internal bool IsTrackingEnabled => isActiveAndEnabled && Enabled.Value;
        private static readonly FieldInfo? TargetUi = AccessTools.Field(typeof(TargetCam), "targetScreenUI");
        private static readonly FieldInfo? LandingUi = AccessTools.Field(typeof(TargetCam), "landingScreenUI");
        private static readonly FieldInfo? LandingCanvas = AccessTools.Field(typeof(LandingScreenUI), "displayCanvas");
        private static readonly FieldInfo? TargetFeed = AccessTools.Field(typeof(TacScreen), "targetCamDisplay");
        private static readonly FieldInfo? LandingFeed = AccessTools.Field(typeof(TacScreen), "landingCamDisplay");
        private Harmony? harmony;
        private Aircraft? owner, screenOwner, cameraOwner;
        private TacScreen? screen;
        private TargetScreenUI? cameraScreen;
        private LandingScreenUI? landingScreen;
        private RectTransform? radarRect, landingRect;
        private MfdSession? session;
        private MfdPage? page;
        private DeliveryWidget? normal, camera, landing;
        private EngagementMarkers? markers;
        private TargetScreenUI? markerScreen;
        private float retryAfter, markerRetryAfter;
        private int warnings, readyMessages;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("General", "Enabled", true, "Track the controlled aircraft's launched missiles, rockets and bombs.");
            Cockpit = Config.Bind("Display", "Show Cockpit Counters", true, "Show horizontal counters at the bottom-left of the cockpit radar panel. Hide them while its target or landing camera feed is active.");
            TargetCamera = Config.Bind("Display", "Show Target Camera Counters", true, "Show the NOTT-style vertical missile/bomb counters in the target camera feed.");
            LandingCamera = Config.Bind("Display", "Show Landing Camera Counters", true, "Show the same vertical missile/bomb counters in the landing camera feed.");
            TargetMarkers = Config.Bind("Display", "Show Target Engagement Markers", true, "Show a small green missile icon below each target-camera box while a missile or bomb is guiding to that target. A number appears when more than one is on it.");
            AlliedMarkers = Config.Bind("Tracking", "Mark Allied Munitions", true, "Engagement markers also count munitions launched by your allies. Turn off to mark only your own launches.");
            Recent = Config.Bind("Tracking", "Recent Outcome Seconds", 3f, new ConfigDescription("How long green/red results remain. Green is the game's armor-contact report, not verified target damage; red includes other detonations, removal and timeout.", new AcceptableValueRange<float>(0.1f, 30f)));
            Timeout = Config.Bind("Tracking", "In Flight Timeout Seconds", 120f, new ConfigDescription("Failsafe after this many seconds: an unresolved munition moves to red, then expires like other recent outcomes.", new AcceptableValueRange<float>(10f, 600f)));
            CockpitScale = Config.Bind("Layout", "Cockpit Counter Scale", 1f, new ConfigDescription("Size multiplier relative to the camera counters. At 1, radar counters match the target-camera size; large rows are limited to the panel width.", new AcceptableValueRange<float>(0.25f, 3f)));
            CameraScale = Config.Bind("Layout", "Target Camera Counter Scale", 1f, new ConfigDescription("Base size for both camera views and cockpit size matching; bottom readout clearance is preserved.", new AcceptableValueRange<float>(0.25f, 3f)));
            OffsetX = Config.Bind("Layout", "Cockpit Left Margin", 6f, new ConfigDescription("Native radar-panel units from the left edge.", new AcceptableValueRange<float>(0f, 500f)));
            OffsetY = Config.Bind("Layout", "Cockpit Bottom Margin", 6f, new ConfigDescription("Native radar-panel units from the bottom edge.", new AcceptableValueRange<float>(0f, 500f)));
            MarkerScale = Config.Bind("Layout", "Target Engagement Marker Scale", 1f, new ConfigDescription("Size multiplier for the missile icon below target-camera boxes.", new AcceptableValueRange<float>(0.25f, 3f)));
            DeliveryTracking.Update(LocalAircraft(), true, Recent.Value, Timeout.Value);
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Logger.LogInfo("Loaded NuclearDelivery " + MyPluginInfo.PLUGIN_VERSION + " build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId + " owned patches=" + harmony.GetPatchedMethods().Count());
        }

        private static Aircraft? LocalAircraft()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            var value = hud != null ? hud.aircraft : null;
            return value != null && !value.disabled && !value.HasEjected() ? value : null;
        }

        internal void ScreenInitialized(TacScreen value, Aircraft aircraft)
        {
            if (aircraft != LocalAircraft()) return;
            DisposeNormal(); screen = value; screenOwner = aircraft; retryAfter = 0;
        }
        internal void ScreenDestroyed(TacScreen value)
        {
            if (screen != value) return;
            DisposeNormal(); screen = null; screenOwner = null;
        }
        internal void CameraInitialized(TargetScreenUI value, Aircraft aircraft)
        {
            if (aircraft != LocalAircraft()) return;
            DisposeCamera(); cameraScreen = value; cameraOwner = aircraft; retryAfter = 0;
        }

        private void LateUpdate()
        {
            // Tracking never depends on camera selection or successful UI creation.
            var current = LocalAircraft();
            try { DeliveryTracking.Update(current, IsTrackingEnabled, Recent.Value, Timeout.Value); }
            catch (Exception error) { Report("tracking", error); }
            try
            {
                if (!ReferenceEquals(owner, current))
                {
                    DisposeNormal(); DisposeCamera(); DisposeLanding(); DisposeMarkers(); owner = current;
                    if (screenOwner != current) { screen = null; screenOwner = null; }
                    if (cameraOwner != current) { cameraScreen = null; cameraOwner = null; }
                    retryAfter = 0;
                }
                if (!IsTrackingEnabled || owner == null) { DisposeNormal(); DisposeCamera(); DisposeLanding(); DisposeMarkers(); return; }
                if (screen == null && normal != null) DisposeNormal();
                if (cameraScreen == null && camera != null) DisposeCamera();
                if ((landingScreen == null || landingRect == null) && landing != null) DisposeLanding();
                if (!Cockpit.Value) DisposeNormal();
                if (!TargetCamera.Value) DisposeCamera();
                if (!LandingCamera.Value) DisposeLanding();
                if (Time.unscaledTime >= retryAfter)
                {
                    retryAfter = Time.unscaledTime + 1f;
                    TryCreateNormal(); TryCreateCamera(); TryCreateLanding();
                }
                var counts = DeliveryTracking.Counts;
                bool cameraFeed = FeedVisible(TargetFeed) || FeedVisible(LandingFeed);
                normal?.Render(counts, !cameraFeed, MatchedCockpitScale(), OffsetX.Value, OffsetY.Value);
                camera?.Render(counts, cameraScreen != null && cameraScreen.gameObject.activeInHierarchy, CameraScale.Value, 0, 0);
                landing?.Render(counts, landingRect != null && landingRect.gameObject.activeInHierarchy, CameraScale.Value, 0, 0);
            }
            catch (Exception error) { DisposeNormal(); DisposeCamera(); DisposeLanding(); retryAfter = Time.unscaledTime + 2f; Report("display", error); }
            // Separate failure scope: a marker problem must not remove the accepted counters.
            try { UpdateMarkers(); }
            catch (Exception error) { DisposeMarkers(); markerRetryAfter = Time.unscaledTime + 2f; Report("engagement markers", error); }
        }

        private void UpdateMarkers()
        {
            if (!IsTrackingEnabled || owner == null || !TargetMarkers.Value || owner.targetCam == null) { DisposeMarkers(); return; }
            var native = TargetUi?.GetValue(owner.targetCam) as TargetScreenUI;
            if (markerScreen != native) DisposeMarkers();
            if (native == null) return;
            if (markers == null)
            {
                if (Time.unscaledTime < markerRetryAfter) return;
                markerRetryAfter = Time.unscaledTime + 1f;
                if (!EngagementMarkers.Available) { Unavailable("TargetScreenUI.targetBoxes is unavailable; engagement markers are off."); markerRetryAfter = float.MaxValue; return; }
                markers = new EngagementMarkers(native); markerScreen = native;
                Ready("target engagement markers");
            }
            markers.Render(owner, AlliedMarkers.Value, MarkerScale.Value);
        }
        private void DisposeMarkers() { markers?.Dispose(); markers = null; markerScreen = null; }

        private bool FeedVisible(FieldInfo? field) => screen != null && field?.GetValue(screen) is GameObject feed && feed.activeInHierarchy;
        private void TryCreateNormal()
        {
            if (normal != null || !Cockpit.Value || screen == null || screenOwner != owner || owner == null) return;
            if (!MfdSession.TryCreate(screen, owner, out var created, out var reason) || created == null) { Unavailable(reason); return; }
            session = created;
            try
            {
                if (!session.TryGetSurface("radar", out var surface) || surface == null) { Unavailable("Native cockpit radar rectangle unavailable."); DisposeNormal(); return; }
                if (surface.Root.rect.width <= 0 || surface.Root.rect.height <= 0) { DisposeNormal(); return; }
                page = surface.Attach("delivery-counters", false);
                // Unity GameObject creation does not inherit the native UI camera's render layer.
                page.Root.gameObject.layer = surface.Root.gameObject.layer;
                radarRect = surface.Root;
                normal = new DeliveryWidget(page.Root, false);
                Ready("normal cockpit radar " + session.AircraftName + " panel='" + surface.Path + "' dimensions=" + surface.Root.rect.size + " layer=" + page.Root.gameObject.layer);
            }
            catch { DisposeNormal(); throw; }
        }
        private void TryCreateCamera()
        {
            if (camera != null || !TargetCamera.Value || owner == null || owner.targetCam == null) return;
            var native = TargetUi?.GetValue(owner.targetCam) as TargetScreenUI;
            if (native == null) return;
            if (cameraScreen != native) { DisposeCamera(); cameraScreen = native; cameraOwner = owner; }
            var root = cameraScreen.transform as RectTransform;
            if (root == null) return;
            camera = new DeliveryWidget(root, true);
            Ready("target camera canvas " + root.rect.size);
        }
        private void TryCreateLanding()
        {
            if (landing != null || !LandingCamera.Value || owner == null || owner.targetCam == null) return;
            var native = LandingUi?.GetValue(owner.targetCam) as LandingScreenUI;
            if (native == null) return;
            var canvas = LandingCanvas?.GetValue(native) as Canvas;
            var root = canvas != null ? canvas.transform as RectTransform : null;
            if (root == null) return;
            landingScreen = native; landingRect = root;
            landing = new DeliveryWidget(root, true);
            Ready("landing camera canvas " + root.rect.size);
        }
        private float MatchedCockpitScale()
        {
            var feed = screen != null ? TargetFeed?.GetValue(screen) as GameObject : null;
            var rect = feed != null ? feed.transform as RectTransform : null;
            if (radarRect == null || rect == null) return CockpitScale.Value;
            float height = 240; // Native camera UI reference height, before lazy UI creation.
            var native = owner != null && owner.targetCam != null ? TargetUi?.GetValue(owner.targetCam) as TargetScreenUI : null;
            var cameraRoot = native != null ? native.transform as RectTransform : null;
            if (cameraRoot != null && cameraRoot.rect.height > 0) height = cameraRoot.rect.height;
            // Match readable font height even when the native camera feed stretches its aspect ratio.
            float feedHeight = radarRect.InverseTransformVector(rect.TransformVector(Vector3.up * rect.rect.height)).magnitude;
            return DeliveryLayout.CockpitScale(feedHeight, height, CameraScale.Value, CockpitScale.Value);
        }
        private void Ready(string value) { if (readyMessages++ < 10) Logger.LogInfo("Delivery counters ready: " + value); }
        private void Unavailable(string reason) { if (warnings++ < 5) Logger.LogWarning("Delivery cockpit unavailable: " + reason); }
        internal static void Report(string stage, Exception error)
        {
            var instance = Instance;
            if (instance != null && instance.warnings++ < 5) instance.Logger.LogWarning("Delivery " + stage + ": " + error);
        }
        private void DisposeNormal()
        {
            normal?.Dispose(); normal = null;
            radarRect = null;
            page?.Dispose(); page = null;
            session?.Dispose(); session = null;
        }
        private void DisposeCamera() { camera?.Dispose(); camera = null; }
        private void DisposeLanding() { landing?.Dispose(); landing = null; landingScreen = null; landingRect = null; }
        private void OnDisable()
        {
            DeliveryTracking.Update(null, false, 3, 120);
            DisposeNormal(); DisposeCamera(); DisposeLanding(); DisposeMarkers();
        }
        private void OnEnable() { retryAfter = 0; markerRetryAfter = 0; }
        private void OnDestroy()
        {
            OnDisable(); EngagementMarkers.ReleaseShared(); harmony?.UnpatchSelf(); Instance = null;
        }
    }
    [HarmonyPatch(typeof(TacScreen), "Initialize")]
    internal static class ScreenInitializePatch
    {
        private static void Postfix(TacScreen __instance, Aircraft aircraft)
        { try { Plugin.Instance?.ScreenInitialized(__instance, aircraft); } catch (Exception error) { Plugin.Report("screen setup", error); } }
    }
    [HarmonyPatch(typeof(TacScreen), "OnDestroy")]
    internal static class ScreenDestroyPatch
    {
        private static void Prefix(TacScreen __instance)
        { try { Plugin.Instance?.ScreenDestroyed(__instance); } catch (Exception error) { Plugin.Report("screen cleanup", error); } }
    }
    [HarmonyPatch(typeof(TargetScreenUI), "SetupCamera")]
    internal static class CameraInitializePatch
    {
        private static void Postfix(TargetScreenUI __instance, Aircraft aircraft)
        { try { Plugin.Instance?.CameraInitialized(__instance, aircraft); } catch (Exception error) { Plugin.Report("camera setup", error); } }
    }
}
