using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearMfdWeapons
{
    // Keep the stock controllers running: StatusDisplay also handles damage audio.
    internal sealed class StockHudVisibility
    {
        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly Dictionary<GameObject, bool> hidden = new Dictionary<GameObject, bool>();
        private Image? outline;
        private Aircraft? aircraft;
        private bool outlineEnabled, outlineHidden;

        internal void Cache(Aircraft owner)
        {
            aircraft = owner;
            var hud = SceneSingleton<CombatHUD>.i;
            if (hud == null) return;
            // The readouts' parent also draws the stock gray backing. Hide the
            // complete native container, as Tactical Tools does, rather than its text alone.
            Add(AccessTools.Field(typeof(CombatHUD), "topRightPanel").GetValue(hud) as GameObject);
            var status = AccessTools.Field(typeof(CombatHUD), "weaponStatus").GetValue(hud) as WeaponStatus;
            if (status != null) Add(AccessTools.Field(typeof(WeaponStatus), "weaponPanel").GetValue(status) as GameObject);
            foreach (var field in new[] { "countermeasureBackground", "countermeasureImage", "countermeasureName", "countermeasureAmmo" })
            {
                var value = AccessTools.Field(typeof(CombatHUD), field).GetValue(hud);
                Add(value is GameObject go ? go : (value as Component)?.gameObject);
            }
        }

        private void Add(GameObject? go) { if (go != null && !objects.Contains(go)) objects.Add(go); }

        internal void Hide()
        {
            foreach (var go in objects)
            {
                if (go == null) continue;
                if (!hidden.ContainsKey(go)) hidden.Add(go, go.activeSelf);
                go.SetActive(false);
            }
            // The aircraft outline is a separate FlightHud graphic, not WeaponStatus.
            // Resolve lazily because Aircraft can create it after TacScreen.Initialize.
            if (outline == null)
            {
                outlineHidden = false;
                var status = aircraft != null ? AccessTools.Field(typeof(Aircraft), "statusDisplay").GetValue(aircraft) as StatusDisplay : null;
                if (status != null) outline = AccessTools.Field(typeof(StatusDisplay), "aircraftBackground").GetValue(status) as Image;
            }
            if (outline != null)
            {
                if (!outlineHidden) { outlineEnabled = outline.enabled; outlineHidden = true; }
                outline.enabled = false;
            }
        }

        internal void Restore()
        {
            foreach (var item in hidden) if (item.Key != null) item.Key.SetActive(item.Value);
            hidden.Clear();
            if (outlineHidden && outline != null) outline.enabled = outlineEnabled;
            outlineHidden = false;
        }
    }
}
