using HarmonyLib;
using UnityEngine;

namespace NuclearSounds
{
    internal static class FlavorCallouts
    {
        private static AudioClip? _radarLockClip;
        private static AudioClip? _deathClip;

        internal static void Initialize()
        {
            _radarLockClip = AudioLoader.LoadFromPluginAssets("theyve got a fucking lock on us.mp3");
            _deathClip = AudioLoader.LoadFromPluginAssets("shit.mp3");
        }

        internal static AudioClip? TryGetRadarLockFlavorClip()
        {
            if (!Plugin.Instance.EnableFlavorCallouts.Value)
            {
                return null;
            }

            if (Random.value > Plugin.Instance.RadarLockFlavorChance.Value)
            {
                return null;
            }

            return _radarLockClip;
        }

        private static void TryPlayDeathCallout(Aircraft aircraft)
        {
            if (!Plugin.Instance.EnableFlavorCallouts.Value || _deathClip == null)
            {
                return;
            }

            if (CombatHUD.i == null || CombatHUD.i.aircraft != aircraft)
            {
                return;
            }

            var crashLikely = aircraft.radarAlt < 25f || aircraft.speed > 55f || aircraft.rb == null || aircraft.rb.velocity.sqrMagnitude > 900f;
            if (crashLikely || Random.value <= Plugin.Instance.DeathFlavorChance.Value)
            {
                InterfaceAudio.PlayOneShot(_deathClip, 2.5f);
            }
        }

        [HarmonyPatch(typeof(Unit), nameof(Unit.UnitDisabled))]
        private static class UnitDisabledPatch
        {
            private static void Postfix(Unit __instance, bool nowDisabled)
            {
                if (!nowDisabled)
                {
                    return;
                }

                var aircraft = __instance as Aircraft;
                if (aircraft == null)
                {
                    return;
                }

                TryPlayDeathCallout(aircraft);
            }
        }
    }
}
