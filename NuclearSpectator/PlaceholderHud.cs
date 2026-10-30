using HarmonyLib;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NuclearSpectator
{
    // Without an aircraft of your own, the native HUD shows its prefab placeholder text
    // (weapon name, ammo, countermeasures) whenever the game re-enables its canvas, for
    // example after closing the map in a cockpit view. While you have no aircraft:
    // hide those CombatHUD panels, and keep the HUD canvas off unless the cockpit view
    // shows the spectated aircraft's real HUD.
    internal static class PlaceholderHud
    {
        private static readonly AccessTools.FieldRef<CombatHUD, GameObject> TopRightPanel = AccessTools.FieldRefAccess<CombatHUD, GameObject>("topRightPanel");
        private static readonly AccessTools.FieldRef<CombatHUD, WeaponStatus> WeaponStatusField = AccessTools.FieldRefAccess<CombatHUD, WeaponStatus>("weaponStatus");
        private static readonly AccessTools.FieldRef<WeaponStatus, GameObject> WeaponPanel = AccessTools.FieldRefAccess<WeaponStatus, GameObject>("weaponPanel");
        private static readonly AccessTools.FieldRef<CombatHUD, GameObject> CountermeasureBackground = AccessTools.FieldRefAccess<CombatHUD, GameObject>("countermeasureBackground");
        private static readonly AccessTools.FieldRef<CombatHUD, Image> CountermeasureImage = AccessTools.FieldRefAccess<CombatHUD, Image>("countermeasureImage");
        private static readonly AccessTools.FieldRef<CombatHUD, TextMeshProUGUI> CountermeasureName = AccessTools.FieldRefAccess<CombatHUD, TextMeshProUGUI>("countermeasureName");
        private static readonly AccessTools.FieldRef<CombatHUD, TextMeshProUGUI> CountermeasureAmmo = AccessTools.FieldRefAccess<CombatHUD, TextMeshProUGUI>("countermeasureAmmo");
        private static readonly AccessTools.FieldRef<FlightHud, Canvas> FlightCanvas = AccessTools.FieldRefAccess<FlightHud, Canvas>("canvas");

        private static readonly Dictionary<GameObject, bool> Hidden = new Dictionary<GameObject, bool>();
        private static readonly List<GameObject> Targets = new List<GameObject>();

        internal static void Update(bool realHudShown)
        {
            var plugin = Plugin.Instance;
            var hud = SceneSingleton<CombatHUD>.i;
            if (plugin == null || !plugin.Enabled.Value || !plugin.HideEmptyHud.Value || hud == null
                || hud.aircraft != null || GameManager.GetLocalAircraft(out _))
            {
                Restore();
                return;
            }
            Collect(hud);
            foreach (var target in Targets)
            {
                if (!Hidden.ContainsKey(target)) Hidden.Add(target, target.activeSelf);
                if (target.activeSelf) target.SetActive(false);
            }
            Plugin.Once("emptyhud:panels", $"Empty HUD: hiding {Targets.Count} placeholder panels while no aircraft is on CombatHUD.");
            if (realHudShown) return;
            var flight = SceneSingleton<FlightHud>.i;
            var canvas = flight != null ? FlightCanvas(flight) : null;
            if (canvas != null && canvas.gameObject.activeSelf)
            {
                FlightHud.EnableCanvas(enable: false);
                Plugin.Once("emptyhud:canvas", "Empty HUD: turned off the HUD canvas the game showed without an aircraft.");
            }
        }

        // Called before CombatHUD.SetAircraft, so the new aircraft's HUD and other mods
        // capture the native panel states.
        internal static void Restore()
        {
            foreach (var item in Hidden)
                if (item.Key != null) item.Key.SetActive(item.Value);
            Hidden.Clear();
        }

        private static void Collect(CombatHUD hud)
        {
            Targets.Clear();
            Add(TopRightPanel(hud));
            var status = WeaponStatusField(hud);
            if (status != null) Add(WeaponPanel(status));
            Add(CountermeasureBackground(hud));
            Add(CountermeasureImage(hud));
            Add(CountermeasureName(hud));
            Add(CountermeasureAmmo(hud));
        }

        private static void Add(GameObject? target)
        {
            if (target != null && !Targets.Contains(target)) Targets.Add(target);
        }

        private static void Add(Component? component)
        {
            if (component != null) Add(component.gameObject);
        }
    }

    [HarmonyPatch(typeof(CombatHUD), nameof(CombatHUD.SetAircraft))]
    internal static class CombatHudSetAircraftPatch
    {
        private static void Prefix() => PlaceholderHud.Restore();
    }
}
