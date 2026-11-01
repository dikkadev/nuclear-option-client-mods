using UnityEngine;

namespace NuclearSounds
{
    // Spectating: in a mission without a live aircraft of your own (a destroyed one stays
    // the local aircraft for about 30 s, until the server removes it). The warnings would
    // otherwise come from another aircraft (a spectator cockpit HUD) or from threats
    // left over after your own aircraft was lost.
    internal static class Spectating
    {
        private static AudioClip? _silence;

        internal static bool Muted
            => !Plugin.Instance.PlayWhileSpectating.Value
                && (GameManager.gameState == GameState.SinglePlayer || GameManager.gameState == GameState.Multiplayer)
                && !(GameManager.GetLocalAircraft(out var own) && !own.disabled);

        // Stands in for the stock gauge voices while muted.
        internal static AudioClip Silence
        {
            get
            {
                if (_silence == null)
                {
                    _silence = AudioClip.Create("silence", 1, 1, 44100, false);
                    _silence.SetData(new float[1], 0);
                }
                return _silence;
            }
        }

        internal static void Clear()
        {
            if (_silence != null) Object.Destroy(_silence);
            _silence = null;
        }
    }
}
