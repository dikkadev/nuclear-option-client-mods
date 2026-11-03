using System;
using HarmonyLib;
using UnityEngine;

namespace NuclearLiveFeed
{
    internal static class Patches
    {
        [HarmonyPatch(typeof(Missile), "StartMissile")]
        private static class MissileStartPatch
        {
            private static void Postfix(Missile __instance)
            {
                try { Plugin.Instance?.Controller?.MissileLaunched(__instance); }
                catch (Exception e) { Plugin.Instance?.Report("launch", e); }
            }
        }

        // Prefix: the original moves effect objects and stops the missile; the detonation facts are the RPC arguments.
        [HarmonyPatch(typeof(Missile), "UserCode_RpcDetonate_897349600")]
        private static class MissileDetonatePatch
        {
            private static void Prefix(Missile __instance, Unit relativeUnit, bool useUnit, Vector3 pos, bool armed, bool hitArmor, bool hitTerrain)
            {
                try { Plugin.Instance?.Controller?.MissileDetonated(__instance, relativeUnit, useUnit, pos, armed, hitArmor, hitTerrain); }
                catch (Exception e) { Plugin.Instance?.Report("detonation", e); }
            }
        }
    }
}
