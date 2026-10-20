using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace NuclearTrackAge
{
    internal static class TrackAgeDiagnostics
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();

        private static bool Initialized;

        internal static void Hook(string eventName)
        {
            if (!Initialized)
            {
                TrackAgeFormatter.DiagnosticObserved = Once;
                Initialized = true;
            }
            Once(eventName);
        }

        private static void Once(string decision)
        {
            if (Seen.Add(decision))
            {
                Plugin.Instance.Log.LogInfo("TrackAge diagnostic: " + decision);
            }
        }
    }

    [HarmonyPatch(typeof(TargetMarker), "Update")]
    [HarmonyAfter("dev.dikka.nuclearunits")]
    internal static class TargetMarkerUpdatePatch
    {
        private static readonly AccessTools.FieldRef<TargetMarker, Text> InfoRangeRef = AccessTools.FieldRefAccess<TargetMarker, Text>("infoRange");

        private static void Postfix(TargetMarker __instance)
        {
            TrackAgeDiagnostics.Hook("surface hook: dynamic map");
            Unit unit = __instance.GetUnit();
            Text infoRange = InfoRangeRef(__instance);
            if (unit == null || infoRange == null)
            {
                return;
            }

            infoRange.text = TrackAgeFormatter.ApplyInlineAge(infoRange.text, unit, showLabel: Plugin.Instance.ShowInDynamicMapMarkers.Value);
        }
    }

    [HarmonyPatch(typeof(TargetListSelector_UnitItem), "Update")]
    internal static class TargetListUnitItemUpdatePatch
    {
        private static readonly AccessTools.FieldRef<TargetListSelector_UnitItem, TextMeshProUGUI> UnitDistRef = AccessTools.FieldRefAccess<TargetListSelector_UnitItem, TextMeshProUGUI>("unitDist");

        private static void Postfix(TargetListSelector_UnitItem __instance)
        {
            TrackAgeDiagnostics.Hook("surface hook: target list");
            Unit unit = __instance.unit;
            TextMeshProUGUI unitDist = UnitDistRef(__instance);
            if (unit == null || unitDist == null)
            {
                return;
            }

            unitDist.text = TrackAgeFormatter.ApplyInlineAge(unitDist.text, unit, 100f, Plugin.Instance.ShowInTargetList.Value);
        }
    }

    [HarmonyPatch(typeof(TargetScreenUI), "UpdateTargetInfo")]
    [HarmonyAfter("dev.dikka.nuclearunits")]
    internal static class TargetScreenUpdateTargetInfoPatch
    {
        private static readonly AccessTools.FieldRef<TargetScreenUI, TextMeshProUGUI> GridTextRef = AccessTools.FieldRefAccess<TargetScreenUI, TextMeshProUGUI>("gridText");
        private static readonly AccessTools.FieldRef<TargetScreenUI, List<Unit>> TargetListRef = AccessTools.FieldRefAccess<TargetScreenUI, List<Unit>>("targetList");

        private static void Postfix(TargetScreenUI __instance)
        {
            TrackAgeDiagnostics.Hook("surface hook: target camera");
            List<Unit> targetList = TargetListRef(__instance);
            TextMeshProUGUI gridText = GridTextRef(__instance);
            if (targetList == null || targetList.Count != 1 || gridText == null)
            {
                return;
            }

            gridText.text = TrackAgeFormatter.ApplyInlineAge(gridText.text, targetList[0], showLabel: Plugin.Instance.ShowInTargetCamera.Value);
        }
    }

    [HarmonyPatch(typeof(CombatHUD), "ShowTargetInfo")]
    internal static class CombatHudShowTargetInfoPatch
    {
        private static readonly AccessTools.FieldRef<CombatHUD, TextMeshProUGUI> TargetInfoRef = AccessTools.FieldRefAccess<CombatHUD, TextMeshProUGUI>("targetInfo");
        private static readonly AccessTools.FieldRef<CombatHUD, List<Unit>> TargetListRef = AccessTools.FieldRefAccess<CombatHUD, List<Unit>>("targetList");

        private static void Postfix(CombatHUD __instance, bool __result)
        {
            TrackAgeDiagnostics.Hook("surface hook: HUD target info");
            if (!__result)
            {
                return;
            }

            List<Unit> targetList = TargetListRef(__instance);
            TextMeshProUGUI targetInfo = TargetInfoRef(__instance);
            if (targetList == null || targetList.Count == 0 || targetInfo == null)
            {
                return;
            }

            targetInfo.text = TrackAgeFormatter.ApplyLineAge(targetInfo.text, targetList[0], showLabel: Plugin.Instance.ShowInHudTargetInfo.Value);
        }
    }
}
