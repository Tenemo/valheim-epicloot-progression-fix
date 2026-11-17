using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(OffSetAttack), nameof(OffSetAttack.ReduceIncomingHit))]
    internal static class OffsetAttackSound
    {
        private static AudioSource activeSound;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions,
                x => x.Calls(AccessTools.Method(typeof(AudioSource), nameof(AudioSource.PlayClipAtPoint), new[] { typeof(AudioClip), typeof(Vector3) })),
                AccessTools.Method(typeof(OffsetAttackSound), nameof(Play)), 1, "EpicLoot's OffSet Attack sound call changed.");

        private static void Play(AudioClip clip, Vector3 position)
        {
            if (clip == null || AudioMan.instance == null || (activeSound != null && activeSound.isPlaying)) return;
            // PlayClipAtPoint creates an unrouted source, bypassing Valheim's SFX slider.
            var mixer = AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX").First(group => group.name == "SFX");
            var sound = new GameObject("EpicLoot OffSet attack sound");
            sound.transform.position = position;
            activeSound = sound.AddComponent<AudioSource>();
            activeSound.playOnAwake = false;
            activeSound.outputAudioMixerGroup = mixer;
            activeSound.clip = clip;
            activeSound.spatialBlend = 1f;
            activeSound.volume = Plugin.OffsetAttackVolume.Value;
            activeSound.Play();
            Chainloader.PluginInfos[Plugin.Id].Instance.StartCoroutine(FinishCue(sound, clip.length + 0.1f));
        }

        private static IEnumerator FinishCue(GameObject sound, float duration)
        {
            // Audio keeps running when simulation speed changes; give its buffer time to finish.
            yield return new WaitForSecondsRealtime(duration);
            if (sound != null) UnityEngine.Object.Destroy(sound);
        }
    }
}
