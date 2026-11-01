using NuclearOption.MissionEditorScripts;
using UnityEngine;

namespace NuclearSpectator
{
    // When the aircraft you spectate is lost (destroyed, pilot dead or ejected, or gone
    // from the game), follow the aircraft nearest to it, of any team, after a short delay.
    // The game itself drops to the free camera.
    internal sealed class AutoFollow
    {
        private const float RetrySeconds = 1f;
        private const float GiveUpSeconds = 10f;

        private CameraStateManager? camera;
        private Aircraft? watched;
        private PersistentID watchedId;
        private bool watching;
        private FollowedView watchedView;
        private GlobalPosition lastPosition;

        private bool pending;
        private FollowedView pendingView;
        private GlobalPosition lossPosition;
        private int pendingFollows;
        private float dueTime;
        private float giveUpTime;
        private int toggles, losses;

        // Waiting to follow the nearest aircraft after a loss.
        internal bool Pending => pending;

        internal void Update()
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Enabled.Value)
            {
                Reset();
                return;
            }
            // Spectating only; a binding skipped while you fly waits for release afterwards.
            if (FollowView.Spectating() && plugin.ToggleFollowNearest.IsDown() && !InputFieldChecker.InsideInputField)
            {
                plugin.FollowNearest.Value = !plugin.FollowNearest.Value;
                if (toggles++ < 20) Plugin.Log?.LogInfo("Follow Nearest When Lost " + (plugin.FollowNearest.Value ? "on" : "off") + ".");
            }
            var cam = SceneSingleton<CameraStateManager>.i;
            if (cam == null || cam.currentState == null || !FollowView.Spectating() || !ReferenceEquals(cam, camera))
            {
                // A new camera means a new mission: nothing from the old one carries over.
                Reset();
                camera = cam;
                return;
            }

            var followed = cam.followingUnit as Aircraft;
            if (followed != null && FollowView.Alive(followed))
            {
                pending = false;
                watched = followed;
                watchedId = followed.persistentID;
                watching = true;
                watchedView = cam.currentState == cam.cockpitState ? FollowedView.Cockpit : FollowedView.Outside;
                lastPosition = followed.transform.GlobalPosition();
                return;
            }
            if (watching)
            {
                // A live aircraft you stopped following (free camera, another unit, an
                // untracked enemy the camera dropped) is not a loss. After an ejection the
                // game follows the aircraft's pilot.
                bool lost = watched == null || !FollowView.Alive(watched);
                bool stillOnIt = cam.followingUnit == null || ReferenceEquals(cam.followingUnit, watched)
                    || (cam.followingUnit is PilotDismounted pilot && pilot.pilotNumber == 0 && pilot.parentUnit == watchedId);
                if (lost && stillOnIt && plugin.FollowNearest.Value)
                {
                    pending = true;
                    pendingView = watchedView;
                    lossPosition = lastPosition;
                    pendingFollows = FollowView.OtherFollows;
                    dueTime = Time.unscaledTime + plugin.FollowNearestDelay.Value;
                    giveUpTime = dueTime + GiveUpSeconds;
                    if (losses++ < 20) Plugin.Log?.LogInfo($"Followed aircraft lost; following the nearest aircraft in {plugin.FollowNearestDelay.Value:0.#} s.");
                }
                watched = null;
                watching = false;
            }
            if (!pending) return;
            // Any follow change you or the game made since the loss wins.
            if (!plugin.FollowNearest.Value || FollowView.OtherFollows != pendingFollows || Time.unscaledTime > giveUpTime)
            {
                pending = false;
                return;
            }
            var ui = SceneSingleton<GameplayUI>.i;
            if (!GameManager.flightControlsEnabled || (ui != null && ui.menuCanvas != null && ui.menuCanvas.enabled))
            {
                // A menu delays the switch; the give-up time counts only outside menus.
                giveUpTime += Time.unscaledDeltaTime;
                return;
            }
            if (Time.unscaledTime < dueTime) return;
            var target = Nearest(plugin.IncludeAi.Value);
            if (target == null)
            {
                dueTime = Time.unscaledTime + RetrySeconds;
                return;
            }
            pending = false;
            FollowView.PreviousOverride = pendingView;
            try { cam.SetFollowingUnit(target); }
            finally { FollowView.PreviousOverride = null; }
            if (losses <= 20) Plugin.Log?.LogInfo($"Following the nearest aircraft, {target.definition.unitName} ({(target.Player != null ? "player" : "AI")}).");
        }

        internal void Reset()
        {
            camera = null;
            watched = null;
            watching = false;
            pending = false;
        }

        // Nearest live aircraft to where the lost one was. Enemies the local team does not
        // track accurately are skipped: the game camera drops them within a second.
        private Aircraft? Nearest(bool includeAi)
        {
            bool hasHq = GameManager.GetLocalHQ(out var localHq);
            Aircraft? best = null;
            float bestDistance = float.MaxValue;
            foreach (var unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || !FollowView.Alive(aircraft)) continue;
                if (aircraft.Player == null && !includeAi) continue;
                if (hasHq && aircraft.NetworkHQ != null && aircraft.NetworkHQ != localHq && !localHq.IsTargetPositionAccurate(aircraft, 100f)) continue;
                float distance = FastMath.Distance(aircraft.transform.GlobalPosition(), lossPosition);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = aircraft;
                }
            }
            return best;
        }
    }
}
