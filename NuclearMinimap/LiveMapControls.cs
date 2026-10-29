using System.Collections.Generic;
using NuclearOption;
using UnityEngine;

namespace NuclearMinimap
{
    internal sealed class LiveMapControls
    {
        private DynamicMap? controlled;
        private Aircraft? aircraft;
        private float originalZoom = MapZoom.Default, desiredZoom = MapZoom.Default;
        private MapOrientation? lastOrientation;

        private static Aircraft? ControlledAircraft()
        {
            var hud = SceneSingleton<CombatHUD>.i;
            var value = hud != null ? hud.aircraft : null;
            return value != null && !value.disabled && !value.HasEjected() ? value : null;
        }
        internal void Update(DynamicMap map, MapOrientation orientation, bool inHeld, bool outHeld, float speed)
        {
            var current = ControlledAircraft();
            if (current == null) { Restore(); return; }
            if (!ReferenceEquals(controlled, map) || !ReferenceEquals(aircraft, current))
            {
                Restore(); controlled = map; aircraft = current;
                map.onMapMaximized += OnMapMaximized;
                // Expanded-map zoom belongs to the native map. A later Minimize resets it to 2.
                originalZoom = DynamicMap.mapMaximized ? MapZoom.Default : map.GetZoomLevel();
                desiredZoom = MapZoom.Step(originalZoom, false, false, 0, 1);
                lastOrientation = null;
            }
            if (DynamicMap.mapMaximized || !map.isActiveAndEnabled || CameraStateManager.cameraMode != CameraMode.cockpit) return;
            desiredZoom = MapZoom.Step(desiredZoom, inHeld, outHeld, Time.unscaledDeltaTime, speed);
            if (!Mathf.Approximately(map.GetZoomLevel(), desiredZoom)) map.SetZoomLevel(desiredZoom);
            // Native UpdateIcons runs before CenterMinimizedMap: establish orientation/scale first.
            ApplyPose(map, orientation);
            if (!lastOrientation.HasValue || lastOrientation.Value != orientation) RefreshIcons(map);
            lastOrientation = orientation;
        }

        internal void ApplyPose(DynamicMap map, MapOrientation orientation) => ApplyPose(map, orientation, true);

        private void ApplyPose(DynamicMap map, MapOrientation orientation, bool centerOnAircraft)
        {
            if (DynamicMap.mapMaximized || map == null || map.mapImage == null || map.mapBackground == null) return;
            if (centerOnAircraft && CameraStateManager.cameraMode != CameraMode.cockpit) return;
            var camera = SceneSingleton<CameraStateManager>.i;
            var current = ControlledAircraft();
            if (camera == null || current == null) return;
            var position = camera.transform.position.ToGlobalPosition().AsVector3() * map.mapDisplayFactor;
            var forward = current.transform.forward; forward.y = 0;
            // Anchor to the same global position as ownship, rather than the camera's 4km look-ahead.
            var center = centerOnAircraft ? current.GlobalPosition().AsVector3() * map.mapDisplayFactor
                : position + forward.normalized * map.mapDisplayFactor * 4000f;
            var image = map.mapImage.transform;
            image.eulerAngles = new Vector3(0, 0, MapZoom.Angle(current.transform.eulerAngles.y, orientation));
            var offset = MapCenter.Offset(center.x, center.z, image.eulerAngles.z, image.localScale.x * map.mapBackground.transform.localScale.x);
            image.localPosition = centerOnAircraft ? new Vector3(offset.x, offset.y, 0)
                : (image.right * -center.x + image.up * -center.z) * image.localScale.x * map.mapBackground.transform.localScale.x;
            // Native icon updates are spread across frames. Keep ownship current at high zoom.
            if (centerOnAircraft && DynamicMap.TryGetMapIcon(current, out var ownship) && ownship != null)
                ownship.UpdateIcon(map.mapDisplayFactor, 1f / image.localScale.x, image, false);
            if (map.viewIndicator != null)
            {
                map.viewIndicator.transform.localPosition = new Vector3(position.x, position.z, 0);
                map.viewIndicator.transform.eulerAngles = new Vector3(0, 0, image.eulerAngles.z - camera.transform.eulerAngles.y);
            }
        }
        private static void RefreshIcons(DynamicMap map)
        {
            if (map.mapImage == null) return;
            float inverseScale = 1f / map.mapImage.transform.localScale.x;
            foreach (var icon in new List<MapIcon>(map.mapIcons))
                if (icon != null) icon.UpdateIcon(map.mapDisplayFactor, inverseScale, map.mapImage.transform, false);
        }
        // Native Maximize keeps the current scale. Open the expanded map at its native zoom, not the minimap's.
        private void OnMapMaximized()
        {
            var map = controlled;
            if (map != null && map.mapScaleCenter != null && !Mathf.Approximately(map.GetZoomLevel(), originalZoom))
                map.SetZoomLevel(originalZoom);
        }
        internal void Restore()
        {
            var value = controlled; controlled = null; aircraft = null; lastOrientation = null;
            if (value != null) value.onMapMaximized -= OnMapMaximized;
            if (value == null || DynamicMap.mapMaximized || value.mapScaleCenter == null || value.mapScaleProxy == null || value.mapImage == null || value.mapBackground == null) return;
            // Do not change fullscreen navigation. Native Minimize already restores its normal scale.
            value.SetZoomLevel(originalZoom);
            ApplyPose(value, MapOrientation.HeadingUp, false);
            RefreshIcons(value);
        }
    }
}
