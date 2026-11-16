using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Unity plays at most 32 sounds at once and silences the rest. A Staff of Fracturing barrage starts hundreds of short
    // impact, weakness hit and lightning sounds in a few seconds, most of them repeats of the same sound in the same place,
    // so footsteps, swings and other combat sounds cut out while it lasts. The game itself limits a sound's repeats only for
    // a few prefabs and only within 0.2 s. Here a one-shot sound doesn't start while three copies of it are still playing
    // within the distance the game already uses for that sound's limit, so a pile-up sounds like three of it instead of
    // twenty, and the voices stay free for everything else.
    internal static class SameSoundLimit
    {
        private const int Copies = 3;
        // Each one-shot sound's playing copies and where they started, pruned as they finish.
        private static readonly Dictionary<int, List<(AudioSource Source, Vector3 Position)>> playing = new Dictionary<int, List<(AudioSource, Vector3)>>();
        // Off plays every repeat, as natively; the isolated client switches it for paired measurements.
        internal static bool Limiting = true;

        internal static void Initialize(IntegrationPatches patches) =>
            patches.Patch(AccessTools.Method(typeof(AudioMan), nameof(AudioMan.RequestPlaySound)),
                new HarmonyMethod(typeof(SameSoundLimit), nameof(Prefix)), new HarmonyMethod(typeof(SameSoundLimit), nameof(Postfix)));

        private static bool Prefix(ZSFX sfx, ref bool __result)
        {
            if (!Limiting || sfx.IsLooping() || !playing.TryGetValue(sfx.m_hash, out var copies)) return true;
            var position = sfx.transform.position;
            float distance = sfx.GetConcurrencyDistance();
            int near = 0;
            for (int i = copies.Count - 1; i >= 0; i--)
            {
                var copy = copies[i];
                if (!copy.Source || !copy.Source.isPlaying) copies.RemoveAt(i);
                else if (Vector3.Distance(copy.Position, position) < distance) near++;
            }
            if (near < Copies) return true;
            __result = false;
            return false;
        }

        private static void Postfix(ZSFX sfx, bool __result)
        {
            if (!__result || !Limiting || sfx.IsLooping()) return;
            var source = sfx.GetComponent<AudioSource>();
            if (!source) return;
            if (!playing.TryGetValue(sfx.m_hash, out var copies)) playing.Add(sfx.m_hash, copies = new List<(AudioSource, Vector3)>());
            copies.Add((source, sfx.transform.position));
        }
    }
}
