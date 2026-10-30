using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearBindings;
using NuclearMfd;
using Rewired;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMinimap
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInIncompatibility("dev.dikka.nuclearmfdmap")]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal static Plugin? Instance;
        private ConfigEntry<bool> enabledSetting = null!;
        private ConfigEntry<bool> cockpitMinimap = null!;
        private ConfigEntry<HudMinimapMode> hudMode = null!;
        private ConfigEntry<MapOrientation> orientation = null!;
        private InputBinding zoomIn = null!, zoomOut = null!;
        private ConfigEntry<float> zoomSpeed = null!, holdThreshold = null!;
        private ConfigEntry<bool> consumeBindings = null!;
        private ConfigEntry<bool> headingTape = null!, northIndicator = null!, attitude = null!, bankReadout = null!;
        private ConfigEntry<InstrumentCorner> attitudeCorner = null!;
        private ConfigEntry<float> attitudeSize = null!, instrumentOpacity = null!;
        private MfdPage? instrumentPage;
        private FlightInstruments? instruments;
        private readonly LiveMapControls controls = new LiveMapControls();
        private readonly MapInputConsumption input = new MapInputConsumption();
        private static readonly FieldInfo? TargetFeed = AccessTools.Field(typeof(TacScreen), "targetCamDisplay");
        private static readonly FieldInfo? LandingFeed = AccessTools.Field(typeof(TacScreen), "landingCamDisplay");
        private readonly Dictionary<TacScreen, Aircraft> screens = new Dictionary<TacScreen, Aircraft>();
        private Harmony? harmony;
        private Aircraft? owner;
        private TacScreen? screen;
        private DynamicMap? map;
        private NativeMapMirror? mirror;
        private NativeHudVisibility? hud;
        private NativeRadar? radar;
        private MfdSession? session;
        private MfdPage? page;
        private bool captured, rendering, wasVisible, spectating;
        private float retryAfter, nextCapture, instrumentRetryAfter;
        private int warnings, readyMessages, renderMessages;
        private float sourceWaitStarted;
        private bool FeatureEnabled => isActiveAndEnabled && enabledSetting.Value;
        private bool CockpitEnabled => FeatureEnabled && cockpitMinimap.Value;

        private void Awake()
        {
            Instance = this;
            MinimapConfigMigration.Import(Config, Logger);
            enabledSetting = Config.Bind("General", "Enabled", true, "Enable native minimap orientation, hold zoom and optional cockpit display.");
            cockpitMinimap = Config.Bind("Display", "Show Cockpit Minimap", true, "Show the native minimap on the cockpit radar panel and apply the HUD visibility policy. Disable to keep native HUD placement while retaining minimap orientation and hold zoom.");
            hudMode = Config.Bind("Display", "HUD Minimap", HudMinimapMode.HideWhenMfdVisible,
                "AlwaysShow keeps the HUD minimap; AlwaysHide hides it; HideWhenMfdVisible hides it only while the cockpit map is successfully shown. The expanded map remains available in all modes.");
            orientation = Config.Bind("Display", "Minimap Orientation", MapOrientation.NorthUp,
                "HeadingUp rotates the minimap with your aircraft; NorthUp keeps north at the top. Applies to both the HUD and cockpit minimap. The expanded map keeps its native orientation and controls.");
            BindZoomControls();
            zoomSpeed = Config.Bind("Controls", "Zoom Speed", 1f, new ConfigDescription("Zoom doublings per second of held input. Applies to the shared HUD/cockpit minimap; menus, text entry and unfocused windows suspend input.", new AcceptableValueRange<float>(.1f, 5f)));
            consumeBindings = Config.Bind("Controls", "Consume Zoom Bindings", true,
                "Short taps replay their normal game bindings on release; long holds zoom without firing those bindings. Other bindings for the same action still work. Turn off to also allow native actions during zoom holds. Menus and the expanded map keep native controls; saved assignments are preserved.");
            holdThreshold = Config.Bind("Controls", "Zoom Hold Delay", .3f,
                new ConfigDescription("Seconds before a press becomes map zoom. With Consume Zoom Bindings on, release earlier to trigger the normal game binding; reaching this delay consumes the press instead. Normal taps therefore wait until release.", new AcceptableValueRange<float>(.1f, 1f)));
            headingTape = Config.Bind("Flight Instruments", "Show Heading Tape", true, "Show a compass tape with the current heading along the top of the cockpit minimap.");
            northIndicator = Config.Bind("Flight Instruments", "Show North Indicator", true, "Show a small north pointer in the top-left corner of the cockpit minimap. It appears only with HeadingUp orientation, where north rotates.");
            attitude = Config.Bind("Flight Instruments", "Show Attitude Indicator", true, "Show a small attitude ball (artificial horizon with bank scale) in a corner of the cockpit minimap.");
            bankReadout = Config.Bind("Flight Instruments", "Show Bank Angle", true, "Show the bank angle in degrees next to the attitude ball, for example R30 or L15.");
            attitudeCorner = Config.Bind("Flight Instruments", "Attitude Indicator Corner", InstrumentCorner.BottomRight, "Cockpit minimap corner for the attitude ball. NuclearDelivery counters use the bottom-left corner.");
            attitudeSize = Config.Bind("Flight Instruments", "Attitude Indicator Size", .3f, new ConfigDescription("Attitude ball diameter, as a fraction of the cockpit minimap's shorter side.", new AcceptableValueRange<float>(.15f, .5f)));
            instrumentOpacity = Config.Bind("Flight Instruments", "Instrument Opacity", .9f, new ConfigDescription("Opacity of the cockpit minimap flight instruments.", new AcceptableValueRange<float>(.1f, 1f)));
            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);
            StartInput();
            Canvas.willRenderCanvases += Render;
            Logger.LogInfo("Loaded NuclearMinimap " + MyPluginInfo.PLUGIN_VERSION + " build=" + typeof(Plugin).Assembly.ManifestModule.ModuleVersionId + " owned patches=" + harmony.GetPatchedMethods().Count());
        }

        // 0.4.x and older kept each zoom direction in a keyboard shortcut, a generic controller
        // control and shared device rows. They move once into one binding per direction.
        private void BindZoomControls()
        {
            bool saved = BindingMigration.IsSaved(Config, "Controls", "Zoom In") || BindingMigration.IsSaved(Config, "Controls", "Zoom Out");
            bool converted = MinimapBindingMigration.Convert(
                BindingMigration.TakeLegacy(Config, "Controls", "Hold Zoom In"), BindingMigration.TakeLegacy(Config, "Controls", "Hold Zoom Out"),
                BindingMigration.TakeLegacy(Config, "Controls", "Controller Hold Zoom In"), BindingMigration.TakeLegacy(Config, "Controls", "Controller Hold Zoom Out"),
                BindingMigration.TakeLegacy(Config, "Controls", "Device Zoom Bindings"), out var inText, out var outText, out var problem);
            zoomIn = InputBinding.Bind(Config, Logger, "Controls", "Zoom In", "Gamepad:DPadUp",
                "Hold to zoom the HUD and cockpit minimap in. Defaults to gamepad D-pad up (or the hat of another assigned controller). A joystick binding replaces the gamepad defaults on that joystick.");
            zoomOut = InputBinding.Bind(Config, Logger, "Controls", "Zoom Out", "Gamepad:DPadDown",
                "Hold to zoom the HUD and cockpit minimap out. Defaults to gamepad D-pad down. Holding both directions cancels zoom.");
            zoomIn.RowNote = zoomOut.RowNote = AxisNote;
            if (converted && !saved)
            {
                zoomIn.Entry.Value = inText; zoomOut.Entry.Value = outText;
                Logger.LogInfo("Moved saved zoom controls into Zoom In = '" + inText + "' and Zoom Out = '" + outText + "'.");
            }
            if (problem.Length > 0) Logger.LogWarning("Minimap zoom bindings: " + problem);
            if (converted) Config.Save();
        }

        // Consumption cannot take full-axis or analog game controls on a zoom axis; say so in F1.
        private static string AxisNote(BindingRow row)
        {
            if (row.Kind != RowKind.Device || !row.IsAxis || !ReInput.isReady) return "";
            var controller = BindingInput.Resolve(row, BindingInput.Devices());
            var player = ReInput.players.GetPlayer(0);
            if (controller == null || player == null) return "";
            foreach (var map in player.controllers.maps.GetAllMaps())
            {
                if (!map.enabled || map.controllerType != controller.type || map.controllerId != controller.id) continue;
                foreach (var element in map.AllMaps)
                {
                    if (!element.enabled || element.elementIdentifierId != row.ElementId || element.elementType != ControllerElementType.Axis) continue;
                    var action = ReInput.mapping.GetAction(element.actionId);
                    if (element.axisRange == AxisRange.Full || action == null || action.type != InputActionType.Button)
                        return "This axis also has game controls. Those controls remain active while zooming.";
                }
            }
            return "";
        }

        internal void ScreenInitialized(TacScreen value, Aircraft aircraft) { screens[value] = aircraft; retryAfter = 0; }
        internal void ScreenDestroyed(TacScreen value)
        {
            screens.Remove(value);
            if (screen == value) DisposeDisplay();
        }
        internal void BeforeMapUpdate(DynamicMap value)
        {
            try
            {
                if (!FeatureEnabled) { RestoreControls(); return; }
                controls.Update(value, orientation.Value, input.ZoomInHeld, input.ZoomOutHeld, zoomSpeed.Value);
            }
            catch (Exception error) { RestoreControls(); Report(error); }
        }
        internal void AfterMapCenter(DynamicMap value)
        {
            if (!FeatureEnabled) return;
            try { controls.ApplyPose(value, orientation.Value); }
            catch (Exception error) { Report(error); }
        }
        private static Aircraft? LocalAircraft()
        {
            var value = SceneSingleton<CombatHUD>.i != null ? SceneSingleton<CombatHUD>.i.aircraft : null;
            return value != null && !value.disabled && !value.HasEjected() ? value : null;
        }
        // While you have no aircraft and watch another aircraft from its cockpit, use a
        // cockpit screen that another mod (NuclearSpectator) initialized for it.
        private Aircraft? SpectatedAircraft()
        {
            if (GameManager.GetLocalAircraft(out _)) return null;
            var camera = SceneSingleton<CameraStateManager>.i;
            if (camera == null || camera.currentState == null || camera.currentState != camera.cockpitState) return null;
            if (!(camera.followingUnit is Aircraft followed) || followed == null || followed.disabled) return null;
            foreach (var item in screens)
                if (item.Key != null && ReferenceEquals(item.Value, followed)) return followed;
            return null;
        }
        // Restore before native UI changes, so native fades and culling see their own state.
        private void Update() => hud?.Restore();
        private void LateUpdate()
        {
            try
            {
                if (!FeatureEnabled) { RestoreControls(); DisposeAll(); return; }
                var current = LocalAircraft(); var native = SceneSingleton<DynamicMap>.i;
                spectating = current == null && CockpitEnabled && (current = SpectatedAircraft()) != null;
                controls.Spectated = spectating ? current : null;
                if (!ReferenceEquals(map, native))
                {
                    DisposeAll(); map = native;
                    if (map != null) hud = new NativeHudVisibility(map);
                }
                if (!ReferenceEquals(owner, current) || (screen == null && page != null))
                { DisposeDisplay(); owner = current; retryAfter = 0; }
                if (!CockpitEnabled) { DisposeDisplay(); return; }
                if (owner == null || map == null) { SetVisible(false); return; }
                if (page == null && Time.unscaledTime >= retryAfter)
                {
                    retryAfter = Time.unscaledTime + 1f;
                    TryCreate();
                }
                if (!PotentiallyVisible()) { captured = false; SetVisible(false); }
                UpdateInstruments();
            }
            catch (Exception error) { DisposeAll(); retryAfter = Time.unscaledTime + 2f; Report(error); }
        }
        private InstrumentOptions Instruments() => new InstrumentOptions
        {
            HeadingTape = headingTape.Value, NorthIndicator = northIndicator.Value, Attitude = attitude.Value,
            BankReadout = bankReadout.Value, Corner = attitudeCorner.Value, Size = attitudeSize.Value,
            Opacity = instrumentOpacity.Value, Orientation = orientation.Value
        };
        // Runs before canvas rebuilds, so instrument meshes and text match this frame's attitude.
        private void UpdateInstruments()
        {
            if (instrumentPage == null || owner == null) return;
            var options = Instruments();
            if (!options.Any) return;
            try
            {
                if (instruments == null)
                {
                    if (Time.unscaledTime < instrumentRetryAfter) return;
                    instrumentRetryAfter = Time.unscaledTime + 1f;
                    instruments = new FlightInstruments(instrumentPage.Root, FlightInstruments.ResolveFont(instrumentPage.Root));
                }
                instruments.Update(owner.transform, options);
            }
            catch (Exception error)
            {
                // Instruments are optional; keep the minimap running without them.
                instruments?.Dispose(); instruments = null; instrumentRetryAfter = Time.unscaledTime + 5f;
                Report(error);
            }
        }
        private void TryCreate()
        {
            foreach (var item in screens)
                if (item.Key != null && ReferenceEquals(item.Value, owner)) { screen = item.Key; break; }
            if (screen == null || owner == null || map == null) return;
            if (!MfdSession.TryCreate(screen, owner, out var created, out var reason) || created == null)
            { if (warnings++ < 5) Logger.LogWarning("MFD minimap unavailable: " + reason); return; }
            session = created;
            try
            {
                if (!session.TryGetSurface("radar", out var surface) || surface == null)
                {
                    if (warnings++ < 5) Logger.LogWarning("MFD minimap native radar unavailable for " + session.AircraftName);
                    DisposeDisplay(); return;
                }
                if (surface.Root.rect.width <= 0 || surface.Root.rect.height <= 0) { DisposeDisplay(); return; }
                page = surface.Attach("minimap", false);
                page.Root.gameObject.layer = surface.Root.gameObject.layer;
                // Native visuals are suppressed explicitly. Independent source-only libraries cannot
                // arbitrate replacement pages from other plugins; preserve their children and ordering.
                page.Root.SetAsFirstSibling();
                radar = new NativeRadar(screen, surface.Root);
                mirror = new NativeMapMirror(map, page.Root);
                page.SetVisible(false);
                // Separate page: the mirror keeps reordering its own children to the end.
                instrumentPage = surface.Attach("minimap-instruments", false);
                instrumentPage.Root.gameObject.layer = page.Root.gameObject.layer;
                instrumentPage.Root.SetSiblingIndex(page.Root.GetSiblingIndex() + 1);
                instrumentPage.SetVisible(false);
                captured = false; nextCapture = 0; sourceWaitStarted = Time.unscaledTime;
                // Register after Graphic has initialized CanvasUpdateRegistry so capture sees built meshes.
                Canvas.willRenderCanvases -= Render; Canvas.willRenderCanvases += Render;
                if (readyMessages++ < 6) Logger.LogInfo("MFD minimap surface ready: " + session.AircraftName + " panel='" + surface.Path + "' native layer=" + page.Root.gameObject.layer);
            }
            catch { DisposeDisplay(); throw; }
        }
        private bool FeedVisible(FieldInfo? field) => screen != null && field?.GetValue(screen) is GameObject feed && feed.activeInHierarchy;
        private bool PotentiallyVisible()
        {
            bool active = session != null && session.CanvasRoot != null && session.CanvasRoot.gameObject.activeInHierarchy &&
                session.CanvasRoot.GetComponent<Canvas>() is Canvas canvas && canvas.enabled &&
                page != null && page.Root != null && page.Root.rect.width > 0 && page.Root.rect.height > 0 && page.Root.parent.gameObject.activeInHierarchy;
            // A spectated pilot cannot switch the screen, so the map stays on over camera feeds.
            return MapPresentation.MfdVisible(mirror != null && mirror.SourceReady, CameraStateManager.cameraMode == CameraMode.cockpit,
                active, !spectating && FeedVisible(TargetFeed), !spectating && FeedVisible(LandingFeed), DynamicMap.mapMaximized);
        }
        private void Render()
        {
            if (rendering) return;
            rendering = true;
            try
            {
                hud?.Restore();
                bool potential = CockpitEnabled && owner != null && PotentiallyVisible();
                bool capturedNow = false;
                if (potential && mirror != null && Time.unscaledTime >= nextCapture)
                {
                    capturedNow = true;
                    nextCapture = Time.unscaledTime + .05f; // Match live UI at 20 Hz without another camera/render target.
                    captured = mirror.Capture();
                    if (captured)
                    {
                        sourceWaitStarted = Time.unscaledTime;
                        if (renderMessages++ < 6) Logger.LogInfo("MFD minimap geometry ready: " + session?.AircraftName + " source graphics=" + mirror.GraphicCount);
                    }
                    else if (Time.unscaledTime - sourceWaitStarted > 3f)
                    {
                        sourceWaitStarted = Time.unscaledTime;
                        if (warnings++ < 5) Logger.LogWarning("MFD minimap waiting for native terrain geometry; retaining stock radar and default HUD. Source graphics=" + mirror.GraphicCount);
                    }
                }
                bool visible = potential && captured;
                SetVisible(visible);
                // Copies change only when captured, after this frame's native clipping pass; between captures
                // that pass keeps their unchanged cull state.
                if (visible && page != null && (capturedNow || !wasVisible)) page.Root.GetComponent<RectMask2D>().PerformClipping();
                wasVisible = visible;
                if (MapPresentation.ShouldHideHud(hudMode.Value, CockpitEnabled, DynamicMap.mapMaximized, visible)) hud?.Hide();
            }
            catch (Exception error) { hud?.Restore(); captured = false; wasVisible = false; SetVisible(false); Report(error); }
            finally { rendering = false; }
        }
        private void SetVisible(bool value)
        {
            radar?.SetReplacing(value); page?.SetVisible(value);
            instrumentPage?.SetVisible(value && instruments != null && Instruments().Any);
        }
        private void DisposeDisplay()
        {
            hud?.Restore(); captured = false;
            instruments?.Dispose(); instruments = null;
            instrumentPage?.Dispose(); instrumentPage = null;
            radar?.Dispose(); radar = null;
            mirror?.Dispose(); mirror = null;
            page?.Dispose(); page = null;
            session?.Dispose(); session = null;
            screen = null;
        }
        private void DisposeAll() { DisposeDisplay(); hud?.Restore(); hud = null; map = null; owner = null; }
        private void Report(Exception error) { if (warnings++ < 6) Logger.LogWarning("MFD minimap: " + error); }
        private void RestoreControls()
        {
            try { controls.Restore(); }
            catch (Exception error) { Report(error); }
        }
        private bool InputAvailable()
        {
            var native = SceneSingleton<DynamicMap>.i;
            return FeatureEnabled && !DynamicMap.mapMaximized &&
                CameraStateManager.cameraMode == CameraMode.cockpit && (LocalAircraft() != null || controls.Spectated != null) &&
                native != null && native.isActiveAndEnabled && !zoomIn.Capturing && !zoomOut.Capturing && !MapHoldInput.IsBlocked();
        }
        private void StartInput() => input.Start(InputAvailable, () => zoomIn.Rows, () => zoomOut.Rows, () => consumeBindings.Value, () => holdThreshold.Value,
            (inward, source) => (inward ? zoomIn : zoomOut).LogFired(source), Report);
        private void CancelCapture() { zoomIn?.CancelCapture(); zoomOut?.CancelCapture(); }
        private void OnEnable() { if (harmony != null) StartInput(); }
        private void OnDisable() { CancelCapture(); input.Dispose(); RestoreControls(); DisposeAll(); }
        private void OnDestroy()
        {
            Canvas.willRenderCanvases -= Render; CancelCapture(); input.Dispose(); RestoreControls(); DisposeAll(); screens.Clear(); harmony?.UnpatchSelf(); Instance = null;
        }
    }
    [HarmonyPatch(typeof(TacScreen), "Initialize")]
    internal static class ScreenInitializePatch
    {
        private static void Postfix(TacScreen __instance, Aircraft aircraft) => Plugin.Instance?.ScreenInitialized(__instance, aircraft);
    }
    [HarmonyPatch(typeof(TacScreen), "OnDestroy")]
    internal static class ScreenDestroyPatch
    {
        private static void Prefix(TacScreen __instance) => Plugin.Instance?.ScreenDestroyed(__instance);
    }
    [HarmonyPatch(typeof(DynamicMap), "Update")]
    internal static class MapUpdatePatch
    {
        private static void Prefix(DynamicMap __instance) => Plugin.Instance?.BeforeMapUpdate(__instance);
    }
    [HarmonyPatch(typeof(DynamicMap), "CenterMinimizedMap")]
    internal static class MapCenterPatch
    {
        private static void Postfix(DynamicMap __instance) => Plugin.Instance?.AfterMapCenter(__instance);
    }
}
