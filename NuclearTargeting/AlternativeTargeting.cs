using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NuclearTargeting
{
    [HarmonyPatch(typeof(CombatHUD), "TargetSelect")]
    internal static class AlternativeTargetingPatch
    {
        private static bool Prefix(CombatHUD __instance, bool paint)
        {
            if (!Plugin.Instance.EnableAlternativeTargeting.Value)
            {
                return true;
            }

            var markers = Traverse.Create(__instance).Field("markers").GetValue<List<HUDUnitMarker>>();
            var eligibleTargets = markers
                .Where(marker => marker != null && marker.unit != null && marker.image != null && marker.image.enabled
                    && !TargetListSelector.i.CheckExclusions(marker.unit)
                    && FastMath.Distance(__instance.targetDesignator.gameObject.transform.position, marker.image.transform.position) < Plugin.Instance.TargetSelectionThreshold.Value)
                .ToList();

            if (eligibleTargets.Count == 0)
            {
                return false;
            }

            var weaponStation = Traverse.Create(__instance).Field("currentWeaponStation").GetValue<WeaponStation>();
            eligibleTargets.Sort((a, b) => b.AssessPriority(__instance.aircraft, __instance.targetDesignator.gameObject, weaponStation)
                .CompareTo(a.AssessPriority(__instance.aircraft, __instance.targetDesignator.gameObject, weaponStation)));

            var targetList = Traverse.Create(__instance).Field("targetList").GetValue<List<Unit>>();
            CleanupTargetList(targetList);
            var currentTargetIndex = -1;
            Unit? originalTarget = null;

            if (targetList.Count > 0)
            {
                // WeaponManager.AddTargetList inserts the most recent selection at zero.
                originalTarget = targetList[0];
                currentTargetIndex = eligibleTargets.FindIndex(marker => marker.unit == originalTarget);
            }

            var nextTargetIndex = GetNextTargetIndex(eligibleTargets, targetList, currentTargetIndex, paint);
            if (nextTargetIndex == -1)
            {
                return false;
            }

            if (paint) AddTarget(__instance, eligibleTargets[nextTargetIndex].unit);
            else ApplyTap(__instance, eligibleTargets[nextTargetIndex].unit);
            return false;
        }

        private static void AddTarget(CombatHUD hud, Unit target)
        {
            var targetList = Traverse.Create(hud).Field("targetList").GetValue<List<Unit>>();
            CleanupTargetList(targetList);
            var previousCount = targetList.Count;
            if (targetList.Contains(target))
            {
                return;
            }

            hud.SelectUnit(target);

            var currentCount = Traverse.Create(hud).Field("targetList").GetValue<List<Unit>>().Count;
            if (currentCount != previousCount)
            {
                Plugin.NotifyTargetSelectionAdded(previousCount, currentCount);
            }
        }

        private static void ApplyTap(CombatHUD hud, Unit target)
        {
            var targetList = Traverse.Create(hud).Field("targetList").GetValue<List<Unit>>();
            var plan = TapTargetBuffer.Plan(targetList, target, Plugin.Instance.GetTapTargetCap(hud.aircraft));
            // Use native deselection so markers, map icons and weapon targets stay in sync.
            foreach (var removed in targetList.Where(unit => !plan.Contains(unit)).Reverse().ToArray())
                hud.DeSelectUnit(removed);
            if (targetList.Contains(target))
            {
                // SelectUnit unconditionally adds an entry. Refresh order without duplicating
                // it or destroying/recreating its selected marker and map icon.
                if (targetList[0] != target)
                {
                    targetList.Remove(target);
                    targetList.Insert(0, target);
                    hud.aircraft.weaponManager.TargetListChanged();
                }
            }
            else AddTarget(hud, target);
        }
        private static void CleanupTargetList(List<Unit> targetList)
        {
            for (var i = targetList.Count - 1; i >= 0; i--)
            {
                if (targetList[i] == null)
                {
                    targetList.RemoveAt(i);
                }
            }

            for (var i = targetList.Count - 1; i > 0; i--)
            {
                var candidate = targetList[i];
                for (var j = 0; j < i; j++)
                {
                    if (targetList[j] == candidate)
                    {
                        targetList.RemoveAt(i);
                        break;
                    }
                }
            }
        }

        private static int GetNextTargetIndex(List<HUDUnitMarker> eligibleTargets, List<Unit> targetList, int currentTargetIndex, bool paint)
        {
            if (Plugin.Instance.NextTargetOrder.Value)
            {
                if (!paint || targetList.Count == 0)
                {
                    return (currentTargetIndex + 1) % eligibleTargets.Count;
                }

                for (var i = 1; i <= eligibleTargets.Count; i++)
                {
                    var candidateIndex = (currentTargetIndex + i) % eligibleTargets.Count;
                    if (!targetList.Contains(eligibleTargets[candidateIndex].unit))
                    {
                        return candidateIndex;
                    }
                }

                return -1;
            }

            if (!paint)
            {
                return 0;
            }

            for (var i = 0; i < eligibleTargets.Count; i++)
            {
                if (!targetList.Contains(eligibleTargets[i].unit))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
