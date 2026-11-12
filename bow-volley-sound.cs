using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;

namespace EpicLootProgressionFix
{
    internal static class BowVolleySound
    {
        private const string CueRpc = Plugin.Id + ".bow-volley-cue";
        internal sealed class Volley
        {
            internal Player Archer;
            internal long Number;
            internal readonly HashSet<ZDOID> Kills = new HashSet<ZDOID>();
            internal bool Played;
        }
        private sealed class Heard
        {
            internal readonly HashSet<long> Numbers = new HashSet<long>();
            internal readonly Queue<long> Order = new Queue<long>();
        }
        private static readonly ConditionalWeakTable<Attack, Volley> Attacks = new ConditionalWeakTable<Attack, Volley>();
        private static readonly ConditionalWeakTable<Projectile, Volley> Projectiles = new ConditionalWeakTable<Projectile, Volley>();
        private static readonly ConditionalWeakTable<Player, Heard> HeardCues = new ConditionalWeakTable<Player, Heard>();
        private static Volley firing, impact;
        private static long nextVolley;
        private static AudioClip clip;
        private static Plugin host;
        private static ManualLogSource logger;

        internal static void Initialize(Plugin instance, ManualLogSource log)
        {
            host = instance;
            logger = log;
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null) instance.StartCoroutine(LoadClip());
        }

        private static IEnumerator LoadClip()
        {
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "sounds", "triple-kill.mp3");
            using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.MPEG);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                logger.LogError("Could not load the bow volley cue: " + request.error);
                yield break;
            }
            clip = DownloadHandlerAudioClip.GetContent(request);
            clip.name = "bow-volley-triple-kill";
        }

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst")]
        private static class BowRelease
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Attack __instance, out Volley __state)
            {
                __state = firing;
                var owner = GameFields.AttackCharacter(__instance) as Player;
                var weapon = GameFields.AttackWeapon(__instance);
                firing = owner && owner == Player.m_localPlayer && weapon?.m_shared.m_skillType == Skills.SkillType.Bows && !HitSource.IsBonusHit
                    ? Attacks.GetValue(__instance, _ => new Volley { Archer = owner, Number = ++nextVolley }) : null;
            }
            private static void Finalizer(Volley __state) => firing = __state;
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class LaunchedArrow
        {
            private static void Postfix(Projectile __instance, Character owner)
            {
                if (firing == null || owner != firing.Archer || HitSource.IsBonusHit) return;
                Projectiles.Remove(__instance);
                Projectiles.Add(__instance, firing);
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
        private static class ArrowImpact
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Projectile __instance, out Volley __state)
            {
                __state = impact;
                impact = Projectiles.TryGetValue(__instance, out var volley) ? volley : null;
            }
            private static void Finalizer(Volley __state) => impact = __state;
        }

        internal static Volley CaptureImpact(Character target, HitData hit, Character owner)
        {
            if (impact == null || impact.Archer != owner || owner != Player.m_localPlayer || !target || target.IsPlayer() ||
                target.IsTamed() || !target.GetBaseAI() || target.IsDead() || target.GetHealth() <= 0f ||
                hit == null || !hit.m_ranged || hit.m_skill != Skills.SkillType.Bows || HitSource.IsBonusHit) return null;
            return impact;
        }

        internal static void ConfirmKill(Volley volley, ZDOID target)
        {
            if (volley == null || volley.Played || target == ZDOID.None || !volley.Kills.Add(target) || volley.Kills.Count != 3) return;
            volley.Played = true;
            var archer = volley.Archer;
            var view = archer ? GameFields.CharacterView(archer) : null;
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            var package = new ZPackage();
            package.Write(volley.Number);
            package.Write(archer.transform.position);
            view.InvokeRPC(ZNetView.Everybody, CueRpc, package);
        }

        [HarmonyPatch(typeof(Player), "Awake")]
        private static class Registration
        {
            private static void Postfix(Player __instance)
            {
                var view = GameFields.CharacterView(__instance);
                if (view && view.IsValid()) view.Register<ZPackage>(CueRpc, (sender, package) => Receive(__instance, sender, package));
            }
        }

        private static void Receive(Player archer, long sender, ZPackage package)
        {
            var view = archer ? GameFields.CharacterView(archer) : null;
            if (!view || !view.IsValid() || view.GetZDO().GetOwner() != sender || package == null || package.Size() != 20) return;
            long number = package.ReadLong();
            var position = package.ReadVector3();
            if (number <= 0 || !Finite(position.x) || !Finite(position.y) || !Finite(position.z)) return;
            var heard = HeardCues.GetValue(archer, _ => new Heard());
            if (!heard.Numbers.Add(number)) return;
            heard.Order.Enqueue(number);
            while (heard.Order.Count > 64) heard.Numbers.Remove(heard.Order.Dequeue());
            if (ZNet.instance.IsDedicated()) return;
            Play(position);
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void Play(Vector3 position)
        {
            if (!clip)
            {
                logger.LogError("The bow volley cue is unavailable; its bundled audio did not finish loading.");
                return;
            }
            var template = ZNetScene.instance.GetPrefab("sfx_bow_fire").GetComponentInChildren<ZSFX>(true);
            var nativeAudio = template.GetComponent<AudioSource>();
            var camera = Utils.GetMainCamera();
            if (!camera || Vector3.Distance(camera.transform.position, position) >= nativeAudio.maxDistance) return;
            var root = new GameObject("Bow volley cue");
            root.SetActive(false);
            root.transform.position = position;
            // The native bow sound prefab has a network view. Copy its audio settings into a local source, since the
            // player RPC has already distributed this event to listeners.
            var audio = root.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.loop = false;
            audio.spatialBlend = 1f;
            audio.minDistance = nativeAudio.minDistance;
            audio.maxDistance = nativeAudio.maxDistance;
            audio.rolloffMode = nativeAudio.rolloffMode;
            audio.SetCustomCurve(AudioSourceCurveType.CustomRolloff, nativeAudio.GetCustomCurve(AudioSourceCurveType.CustomRolloff));
            audio.spread = nativeAudio.spread;
            audio.dopplerLevel = nativeAudio.dopplerLevel;
            audio.outputAudioMixerGroup = AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX").First(group => group.name == "SFX");
            var sound = root.AddComponent<ZSFX>();
            sound.m_audioClips = new[] { clip };
            sound.m_closedCaptionToken = "Triple kill!";
            sound.m_secondaryCaptionToken = "";
            sound.m_minPitch = sound.m_maxPitch = 1f;
            sound.m_minVol = template.m_minVol;
            sound.m_maxVol = template.m_maxVol;
            sound.m_distanceReverb = template.m_distanceReverb;
            sound.m_useCustomReverbDistance = template.m_useCustomReverbDistance;
            sound.m_customReverbDistance = template.m_customReverbDistance;
            sound.m_playOnAwake = false;
            sound.m_useVibration = false;
            sound.m_playThroughController = false;
            sound.m_hash = CueRpc.GetStableHashCode();
            root.SetActive(true);
            sound.Play();
            host.StartCoroutine(Finish(root, clip.length + 0.25f));
        }

        private static IEnumerator Finish(GameObject sound, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            if (sound) UnityEngine.Object.Destroy(sound);
        }
    }
}
