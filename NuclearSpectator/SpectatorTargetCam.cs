using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NuclearSpectator
{
    // The game creates a TargetCam only for the aircraft's owner, so another player's
    // cockpit screen has no target feed. Their target list is synced to every client
    // (RpcSetStationTargets), so build the native target camera rig on the spectated
    // aircraft and point it at the pilot's first target, like TargetCam.SetTargetCam.
    internal sealed class SpectatorTargetCam
    {
        private static readonly AccessTools.FieldRef<TacScreen, GameObject> TargetFeed = AccessTools.FieldRefAccess<TacScreen, GameObject>("targetCamDisplay");
        private static readonly AccessTools.FieldRef<TargetCam, Transform> MountForward = AccessTools.FieldRefAccess<TargetCam, Transform>("camMountForward");
        private static readonly AccessTools.FieldRef<TargetCam, Transform> MountRear = AccessTools.FieldRefAccess<TargetCam, Transform>("camMountRear");

        // The native TargetCam component removes itself on other players' aircraft, but its
        // nose and tail camera mounts stay. Remember them from Awake.
        private sealed class NativeMounts
        {
            internal Transform? Forward;
            internal Transform? Rear;
        }
        private static readonly ConditionalWeakTable<Aircraft, NativeMounts> Mounts = new ConditionalWeakTable<Aircraft, NativeMounts>();

        internal static void Remember(TargetCam native)
        {
            if (!(native.GetComponentInParent<Aircraft>() is Aircraft aircraft) || aircraft == null) return;
            var mounts = Mounts.GetOrCreateValue(aircraft);
            mounts.Forward = MountForward(native);
            mounts.Rear = MountRear(native);
        }

        private readonly Aircraft aircraft;
        private readonly GameObject display;
        private readonly Transform mount;
        private readonly Transform? nativeForward;
        private readonly Transform? nativeRear;
        private readonly Vector3 fallbackPosition;
        private readonly Camera cam;
        private readonly ColorAdjustments? colors;
        private bool shown;
        private bool thermal;

        private SpectatorTargetCam(Aircraft aircraft, GameObject display, Transform mount, NativeMounts? native, Camera cam, ColorAdjustments? colors)
        {
            this.aircraft = aircraft;
            this.display = display;
            this.mount = mount;
            nativeForward = native?.Forward;
            nativeRear = native?.Rear;
            fallbackPosition = mount.localPosition;
            this.cam = cam;
            this.colors = colors;
            RenderPipelineManager.beginCameraRendering += BeginRendering;
            RenderPipelineManager.endCameraRendering += EndRendering;
        }

        // Like TargetCam: no fog in the thermal image, restored for every other camera.
        private void BeginRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == cam) RenderSettings.fog = !thermal;
        }

        private void EndRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == cam) RenderSettings.fog = true;
        }

        internal void Dispose()
        {
            Hide();
            RenderPipelineManager.beginCameraRendering -= BeginRendering;
            RenderPipelineManager.endCameraRendering -= EndRendering;
        }

        // Adds the camera rig to `roots`; returns null when the screen or rig is missing.
        internal static SpectatorTargetCam? Create(Aircraft aircraft, TacScreen screen, List<GameObject> roots)
        {
            var display = TargetFeed(screen);
            if (display == null || GameAssets.i == null || GameAssets.i.targetCam == null || aircraft.weaponManager == null) return null;
            Mounts.TryGetValue(aircraft, out var native);
            var mountObject = new GameObject("NuclearSpectator TargetCam");
            roots.Add(mountObject);
            var mount = mountObject.transform;
            mount.SetParent(aircraft.transform, false);
            // Without the native nose mount, the cockpit's lower front is close enough.
            var eye = aircraft.cockpit != null ? aircraft.cockpit.transform.position : aircraft.transform.position;
            mount.localPosition = aircraft.transform.InverseTransformPoint(eye) + new Vector3(0f, -1f, 2f);
            var rig = Object.Instantiate(GameAssets.i.targetCam, mount);
            // Same cameras as TargetCam.Initialize: the first active one renders the scene,
            // the second the target info overlay, which needs the owner's TargetCam.
            var cameras = rig.GetComponentsInChildren<Camera>();
            if (cameras.Length == 0) return null;
            for (int i = 1; i < cameras.Length; i++) cameras[i].enabled = false;
            var cam = cameras[0];
            cam.enabled = false;
            // SetTargetCam puts the scene camera itself on the mount, with no local offset.
            cam.transform.SetParent(mount, false);
            cam.transform.localPosition = Vector3.zero;
            cam.transform.localRotation = Quaternion.identity;
            cam.nearClipPlane = 2f;
            cam.farClipPlane = 60000f;
            cam.fieldOfView = 10f;
            // The screen volume carries the feed's exposure and contrast, which the native
            // TargetCam sets every frame; set the same values here.
            var volume = cam.GetComponentInChildren<Volume>();
            ColorAdjustments? colors = null;
            if (volume != null && volume.profile != null && volume.profile.TryGet(out ColorAdjustments found)) colors = found;
            if (volume != null) volume.enabled = colors != null;
            Plugin.Once("targetcam", "Spectator target camera: " + Describe(cameras, volume, colors, native));
            return new SpectatorTargetCam(aircraft, display, mount, native, cam, colors);
        }

        private static string Describe(Camera[] cameras, Volume? volume, ColorAdjustments? colors, NativeMounts? native)
        {
            var parts = cameras.Select((item, index) =>
            {
                var data = item.GetUniversalAdditionalCameraData();
                var texture = item.targetTexture;
                return $"camera {index} '{item.name}' renderType={(data != null ? data.renderType.ToString() : "?")}, stack={(data != null && data.cameraStack != null ? data.cameraStack.Count : 0)}, " +
                       $"target={(texture != null ? $"{texture.name} {texture.width}x{texture.height}" : "screen")}, clear={item.clearFlags}, mask={item.cullingMask}, postprocessing={(data != null && data.renderPostProcessing)}";
            });
            return string.Join("; ", parts) + $"; volume={(volume != null ? (colors != null ? "colour adjustments" : "no colour adjustments, disabled") : "none")}; native mounts={(native?.Forward != null ? "nose" : "none")}{(native?.Rear != null ? "+tail" : "")}";
        }

        internal void Update()
        {
            if (aircraft == null || mount == null || cam == null) return;
            var targets = aircraft.weaponManager.GetTargetList();
            var target = targets.Count > 0 ? targets[0] : null;
            if (target == null || target.disabled)
            {
                Hide();
                return;
            }
            var tracked = aircraft.NetworkHQ != null ? aircraft.NetworkHQ.GetTrackingData(target.persistentID) : null;
            var position = tracked != null ? tracked.GetPosition().ToLocalPosition() : target.transform.position;
            // Like the native camera: the nose mount, or the tail mount for targets behind.
            bool behind = Vector3.Angle(aircraft.transform.forward, position - aircraft.transform.position) > 135f;
            var native = behind && nativeRear != null ? nativeRear : nativeForward;
            if (native != null) mount.position = native.position;
            else mount.localPosition = fallbackPosition;
            var direction = position - mount.position;
            float distance = direction.magnitude;
            if (distance < 1f) return;
            mount.rotation = Quaternion.LookRotation(direction, Vector3.up);
            float fov = Mathf.Clamp(target.definition.length * 75f / distance, 0.25f, 20f);
            cam.fieldOfView = shown ? Mathf.Lerp(cam.fieldOfView, fov, Time.deltaTime) : fov;
            target.displayDetail = 1f;
            UpdateExposure(distance);
            if (!shown)
            {
                shown = true;
                cam.enabled = true;
                if (display != null) display.SetActive(true);
            }
        }

        // TargetCam.SetTargetCam and UpdateExposure: thermal at night, beyond 10 km or by
        // the player's setting, with exposure from the ambient light.
        private void UpdateExposure(float distance)
        {
            var level = NetworkSceneSingleton<LevelInfo>.i;
            if (level == null) return;
            thermal = level.timeOfDay < 6f || level.timeOfDay > 18f || distance > 10000f || PlayerSettings.tacScreenIR;
            if (colors == null) return;
            float light = Mathf.InverseLerp(0.02f, 0.4f, level.GetAmbientLight());
            colors.saturation.overrideState = thermal;
            colors.postExposure.value = thermal ? Mathf.Lerp(3f, -0.5f, light) : Mathf.Lerp(0.5f, -1f, light);
            colors.contrast.value = thermal ? 1f : 5f;
        }

        internal void Hide()
        {
            if (!shown) return;
            shown = false;
            if (cam != null) cam.enabled = false;
            if (display != null) display.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(TargetCam), "Awake")]
    internal static class TargetCamAwakePatch
    {
        private static void Postfix(TargetCam __instance)
        {
            try { SpectatorTargetCam.Remember(__instance); }
            catch (System.Exception error) { Plugin.Report("target camera mounts", error); }
        }
    }
}
