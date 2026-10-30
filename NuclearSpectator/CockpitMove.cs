using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using Rewired;
using System;
using UnityEngine;

namespace NuclearSpectator
{
    // While spectating from another aircraft's cockpit, the free-camera move inputs
    // (left stick, WASD) move the eye relative to the view, attached to the aircraft.
    // The native view reset ("Center") returns to the pilot's eye point.
    //
    // The native state writes the look (mouse, head tracker) as the camera's local pose
    // under cameraPivot, which turns with the aircraft. With the horizon locked, that
    // same local pose is applied to a level frame (aircraft heading, no pitch or roll)
    // instead, so head tracking turns, tilts and rolls relative to the horizon.
    internal static class CockpitMove
    {
        // In the pivot frame, or in the level frame while the horizon is locked.
        private static Vector3 offset;
        private static Aircraft? owner;
        private static bool exterior;
        private static bool horizonLocked;
        private static bool overridden;
        private static Vector3 nativePosition;
        private static Quaternion nativeRotation;
        private static Quaternion level = Quaternion.identity;
        private static bool levelValid;

        // Restore last frame's native local pose, so the native state sees the pilot's
        // eye point and its own look angles.
        internal static void BeforeUpdate(CameraStateManager cam)
        {
            if (!overridden) return;
            cam.transform.localPosition = nativePosition;
            cam.transform.localRotation = nativeRotation;
            overridden = false;
        }

        internal static void AfterUpdate(CameraStateManager cam)
        {
            var plugin = Plugin.Instance;
            var aircraft = cam.followingUnit as Aircraft;
            if (plugin == null || !plugin.Enabled.Value || !plugin.CockpitMove.Value || aircraft == null
                || cam.currentState != cam.cockpitState || GameManager.GetLocalAircraft(out _))
            {
                // Leaving the cockpit view, native LeaveState already restored the exterior.
                Reset(restoreInterior: cam.currentState == cam.cockpitState);
                return;
            }
            if (!ReferenceEquals(owner, aircraft)) Reset(restoreInterior: true);
            owner = aircraft;
            var pivot = cam.cameraPivot;
            UpdateLevel(pivot);
            nativePosition = cam.transform.localPosition;
            nativeRotation = cam.transform.localRotation;

            if (GameManager.flightControlsEnabled && !InputFieldChecker.InsideInputField && !DynamicMap.mapMaximized)
            {
                var input = GameManager.playerInput;
                if (plugin.LockHorizon.IsDown())
                {
                    SetHorizonLock(!horizonLocked, pivot);
                    Plugin.Log?.LogInfo("Spectator horizon lock " + (horizonLocked ? "on" : "off") + ".");
                }
                if (input.GetButtonDown("Center"))
                {
                    offset = Vector3.zero;
                }
                else
                {
                    float forward = input.GetAxis("Move Longitudinal");
                    float right = input.GetAxis("Move Lateral");
                    float up = input.GetAxis("Move Vertical");
                    if (plugin.TriggersMoveVertically.Value) up = Mathf.Clamp(up + TriggerAxis(input), -1f, 1f);
                    if (forward != 0f || right != 0f || up != 0f)
                    {
                        float speed = plugin.MoveSpeed.Value * (Input.GetKey(KeyCode.LeftShift) ? 5f : 1f);
                        var frame = Frame(pivot);
                        var view = frame * nativeRotation;
                        var world = view * Vector3.forward * forward + view * Vector3.right * right + frame * Vector3.up * up;
                        offset += Quaternion.Inverse(frame) * world * (speed * Time.unscaledDeltaTime);
                        offset = Vector3.ClampMagnitude(offset, plugin.MaxMoveDistance.Value);
                    }
                }
            }

            overridden = true;
            if (horizonLocked)
                cam.transform.SetPositionAndRotation(pivot.position + level * (nativePosition + offset), level * nativeRotation);
            else
                cam.transform.localPosition = nativePosition + offset;
            SetExterior(aircraft, offset.magnitude > plugin.ExteriorDistance.Value);
        }

        private static Quaternion Frame(Transform pivot) => horizonLocked ? level : pivot.rotation;

        // The aircraft's heading on the horizon. Pointing straight up or down, the heading
        // comes from the aircraft's up axis; keep the last one if both are vertical. Over the
        // top of a loop the heading reverses: turn there quickly instead of in one frame.
        private static void UpdateLevel(Transform pivot)
        {
            var heading = pivot.forward;
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.0001f)
            {
                heading = pivot.forward.y > 0f ? -pivot.up : pivot.up;
                heading.y = 0f;
            }
            if (heading.sqrMagnitude <= 0.0001f) return;
            var target = Quaternion.LookRotation(heading.normalized, Vector3.up);
            level = levelValid ? Quaternion.RotateTowards(level, target, 360f * Time.unscaledDeltaTime) : target;
            levelValid = true;
        }

        // Keeps the eye where it is when the lock changes the frame the offset is kept in.
        private static void SetHorizonLock(bool value, Transform pivot)
        {
            var world = Frame(pivot) * offset;
            horizonLocked = value;
            offset = Quaternion.Inverse(Frame(pivot)) * world;
        }

        // Right trigger up, left trigger down, read from the named gamepad template.
        private static float TriggerAxis(Player input)
        {
            float value = 0f;
            foreach (var joystick in input.controllers.Joysticks)
            {
                var pad = joystick.GetTemplate<IGamepadTemplate>();
                if (pad == null) continue;
                if (pad.rightTrigger != null) value += pad.rightTrigger.value;
                if (pad.leftTrigger != null) value -= pad.leftTrigger.value;
            }
            return value;
        }

        // Entering the cockpit view starts at the pilot's eye point; native EnterState
        // already reset the camera position, cockpit renderers and pilot visibility.
        internal static void Entered()
        {
            offset = Vector3.zero;
            owner = null;
            exterior = false;
            overridden = false;
            levelValid = false;
        }

        private static void Reset(bool restoreInterior)
        {
            offset = Vector3.zero;
            if (restoreInterior && exterior && owner != null) SetExterior(owner, false);
            exterior = false;
            owner = null;
            levelValid = false;
        }

        // Away from the seat, show the outside of the aircraft and its pilot instead of
        // the cockpit-only interior meshes.
        private static void SetExterior(Aircraft aircraft, bool value)
        {
            if (value == exterior) return;
            exterior = value;
            aircraft.SetCockpitRenderers(!value);
            foreach (var pilot in aircraft.pilots)
                if (pilot != null) pilot.TogglePilotVisibility(value);
        }
    }

    [HarmonyPatch(typeof(CameraCockpitState), nameof(CameraCockpitState.UpdateState))]
    internal static class CockpitMoveUpdatePatch
    {
        private static void Prefix(CameraStateManager cam)
        {
            try { CockpitMove.BeforeUpdate(cam); }
            catch (Exception error) { Plugin.Report("cockpit movement", error); }
        }

        private static void Postfix(CameraStateManager cam)
        {
            try { CockpitMove.AfterUpdate(cam); }
            catch (Exception error) { Plugin.Report("cockpit movement", error); }
        }
    }

    [HarmonyPatch(typeof(CameraCockpitState), nameof(CameraCockpitState.EnterState))]
    internal static class CockpitMoveEnterPatch
    {
        private static void Postfix() => CockpitMove.Entered();
    }
}
