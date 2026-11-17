using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Unity plays at most 32 sounds at once and silences the rest. A Staff of Fracturing barrage starts hundreds of short
    // impact, weakness hit and lightning sounds in a few seconds, most of them repeats of the same sound in the same place,
    // so footsteps, swings and other combat sounds cut out while it lasts. The game itself limits a sound's repeats only for
    // a few prefabs and only within 0.2 s. Here, when a one-shot sound starts while three copies of it are still playing
    // within the distance the game uses for that sound's limit, the oldest of them fades out in 0.05 s. A pile-up sounds
    // like three of it instead of twenty and the voices stay free for everything else, while every new copy still plays,
    // so a steady stream such as items going into a windmill keeps its rhythm.
    internal static class SameSoundLimit
    {
        private const int Copies = 3;
        private const float FadeSeconds = 0.05f;
        // Each one-shot sound's playing copies, oldest first, and where they started, pruned as they finish or fade.
        private static readonly Dictionary<int, List<(ZSFX Sound, Vector3 Position)>> playing = new Dictionary<int, List<(ZSFX, Vector3)>>();
        // A sound is known by its first clip. The game's sound hash can't tell sounds apart: dozens of different sounds share
        // one, such as lightning, blood and nature impacts and the player's catching-fire cue, and some have none.
        private static int Sound(ZSFX sfx)
        {
            var clips = sfx.m_audioClips;
            return clips.Length > 0 && clips[0] ? clips[0].GetInstanceID() : sfx.m_hash;
        }
        // Off plays every repeat, as natively; the isolated client switches it for paired measurements.
        internal static bool Limiting = true;

        internal static void Initialize(IntegrationPatches patches) =>
            patches.Patch(AccessTools.Method(typeof(AudioMan), nameof(AudioMan.RequestPlaySound)),
                postfix: new HarmonyMethod(typeof(SameSoundLimit), nameof(Postfix)));

        // Runs after the game's own repeat limit has let the new copy play, so a copy the game refuses never fades another.
        private static void Postfix(ZSFX sfx, bool __result)
        {
            if (!__result || !Limiting || sfx.IsLooping()) return;
            int sound = Sound(sfx);
            var position = sfx.transform.position;
            if (!playing.TryGetValue(sound, out var copies)) playing.Add(sound, copies = new List<(ZSFX, Vector3)>());
            else FadeOldest(sfx, copies, position);
            copies.Add((sfx, position));
        }

        private static void FadeOldest(ZSFX sfx, List<(ZSFX Sound, Vector3 Position)> copies, Vector3 position)
        {
            // Sounds the game limits world-wide, such as cinder rain hits and some boss attacks, are limited here only within
            // the radius every other sound uses, so three of them still play in each spot.
            float distance = sfx.m_ignoreConcurrencyDistance ? Mathf.Max(1f, sfx.GetComponent<AudioSource>().minDistance) : sfx.GetConcurrencyDistance();
            int near = 0;
            for (int i = copies.Count - 1; i >= 0; i--)
            {
                var copy = copies[i];
                if (!copy.Sound || !copy.Sound.IsPlaying()) copies.RemoveAt(i);
                else if (Vector3.Distance(copy.Position, position) < distance) near++;
            }
            for (int i = 0; i < copies.Count && near >= Copies; i++)
            {
                var copy = copies[i];
                if (Vector3.Distance(copy.Position, position) >= distance) continue;
                if (copy.Sound.m_fadeOutDuration <= 0f || copy.Sound.m_fadeOutDuration > FadeSeconds) copy.Sound.m_fadeOutDuration = FadeSeconds;
                copy.Sound.FadeOut();
                copies.RemoveAt(i--);
                near--;
            }
        }
    }
}
