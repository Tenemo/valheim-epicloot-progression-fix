using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Keep SLS's delayed setup, so spawners can finish assigning bounty/summon data. Only a creature's first
    // appearance waits for it; saved creatures retain their existing loading path and never get a new roll here.
    internal static class FreshCreatureSpawns
    {
        private sealed class Setup { internal bool Complete, Sized; internal FreshCreaturePresentation Presentation; }
        private static readonly ConditionalWeakTable<Character, Setup> pending = new ConditionalWeakTable<Character, Setup>();
        // Creatures still in pending, so the checks every creature runs every frame skip the table while none is appearing.
        // A creature collected without being removed keeps it above zero, which only costs the lookup.
        private static int pendingCount;
        private static readonly Dictionary<ZDOID, long> recentInstances = new Dictionary<ZDOID, long>();
        private static readonly Queue<ZDOID> recentOrder = new Queue<ZDOID>();
        private const long ReplicaWindow = 5 * TimeSpan.TicksPerSecond;
        // SLS retries a failed setup five times a second apart, and a client waits on its owner's setup. A creature whose
        // setup never finishes appears without it after this many seconds rather than staying hidden and frozen.
        internal const float MaximumWait = 10f;
        private static readonly Action<Character, bool> setVisible = AccessTools.MethodDelegate<Action<Character, bool>>(
            AccessTools.Method(typeof(Character), "SetVisible"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(FreshCreatureSpawns);
            if (StarLevelSystem.Installed)
            {
                var setup = AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.CreatureSetup.CreatureSetupControl"),
                    "RunCharacterSetup", new[] { typeof(Character), StarLevelSystem.CharacterCacheEntry });
                if (setup?.ReturnType != typeof(bool)) throw new InvalidOperationException("StarLevelSystem's creature setup changed.");
                harmony.Patch(setup, postfix: new HarmonyMethod(self, nameof(SetupFinished)));
            }
            harmony.Patch(AccessTools.Constructor(typeof(ZDOMan), new[] { typeof(int) }), postfix: new HarmonyMethod(self, nameof(ForgetInstances)));
            harmony.Patch(AccessTools.Method(typeof(Character), "Awake"),
                prefix: new HarmonyMethod(self, nameof(BeforeAwake)) { priority = Priority.First },
                postfix: new HarmonyMethod(self, nameof(AfterAwake)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(Character), "SetVisible"), prefix: new HarmonyMethod(self, nameof(OnlyWhenReady)));
            harmony.Patch(AccessTools.Method(typeof(Character), "CustomFixedUpdate"), prefix: new HarmonyMethod(self, nameof(UpdateCharacter)));
            foreach (var type in new[] { typeof(BaseAI), typeof(MonsterAI), typeof(AnimalAI) })
                harmony.Patch(AccessTools.DeclaredMethod(type, "UpdateAI"), prefix: new HarmonyMethod(self, nameof(UpdateAi)));
            harmony.Patch(AccessTools.Method(typeof(EnemyHud), "TestShow"), prefix: new HarmonyMethod(self, nameof(ShowHud)));
        }

        private static void BeforeAwake(Character __instance)
        {
            if (__instance is Player || !__instance.GetComponent<BaseAI>()) return;
            var view = __instance.GetComponent<ZNetView>();
            var data = view != null ? view.GetZDO() : null;
            if (data == null) return;
            long now = ZNet.instance.GetTime().Ticks;
            // Vanilla's spawn timestamp identifies initial replicas without adding any persistent pack metadata.
            // Remember instances briefly so unloading/reloading even a just-born creature keeps the old load path.
            while (recentOrder.Count > 0 && recentInstances[recentOrder.Peek()] < now)
                recentInstances.Remove(recentOrder.Dequeue());
            bool placement = CreatureSpawnPlacement.PendingPlacement(data);
            if (recentInstances.ContainsKey(data.m_uid) && !placement) return;
            if (!recentInstances.ContainsKey(data.m_uid))
            {
                recentInstances[data.m_uid] = now + ReplicaWindow;
                recentOrder.Enqueue(data.m_uid);
            }
            long born = data.GetLong(ZDOVars.s_spawnTime, 0L);
            if (!placement && ZNetView.m_useInitZDO && (born <= 0 || now < born || now - born > ReplicaWindow)) return;
            pending.Add(__instance, new Setup { Sized = !StarLevelSystem.Installed });
            pendingCount++;
        }

        // A ZDOID hashes through a user table that every new ZDOMan resets, so IDs remembered from an earlier
        // connection no longer find their entries. Relogging without restarting the game starts with an empty window.
        private static void ForgetInstances() { recentInstances.Clear(); recentOrder.Clear(); CreatureSpawnPlacement.ClearWorld(); }

        private static void AfterAwake(Character __instance)
        {
            if (!pending.TryGetValue(__instance, out var setup) || setup.Complete) return;
            setup.Presentation = __instance.gameObject.AddComponent<FreshCreaturePresentation>();
            setup.Presentation.Begin(__instance);
        }

        private static void SetupFinished(Character __0, bool __result)
        {
            if (__result && __0 != null)
            {
                if (pending.TryGetValue(__0, out var setup)) setup.Sized = true;
                Finish(__0);
            }
        }

        internal static void Finish(Character character, bool timedOut = false)
        {
            if (!pending.TryGetValue(character, out var setup)) return;
            var view = character ? GameFields.CharacterView(character) : null;
            if (!view || !view.IsValid()) { Cancel(character); return; }
            if (!CreatureSpawnPlacement.Ready(character, timedOut)) return;
            if (!pending.TryGetValue(character, out setup)) return;
            setup.Complete = true;
            if (setup.Presentation) setup.Presentation.Release();
            Unpend(character);
            if (character) CreatureSpawnPlacement.AfterRelease(character);
        }

        internal static void Cancel(Character character)
        {
            if (!pending.TryGetValue(character, out var setup)) return;
            Unpend(character);
            if (setup.Presentation) setup.Presentation.Cancel();
        }

        internal static bool Waiting(Character character) => pendingCount != 0 && character != null && pending.TryGetValue(character, out var setup) && !setup.Complete;
        private static void OnlyWhenReady(Character __instance, ref bool visible) { if (Waiting(__instance)) visible = false; }
        private static bool UpdateCharacter(Character __instance) => !Waiting(__instance);
        // Every creature's AI runs this; it keeps the character its Awake found.
        private static bool UpdateAi(BaseAI __instance, Character ___m_character, ref bool __result)
        {
            if (!Waiting(___m_character)) return true;
            __result = false;
            return false;
        }
        // TestShow is the native eligibility check. Skipping ShowHud itself would still run SLS's postfix,
        // which expects the original to have created the boss HUD's children.
        private static bool ShowHud(Character __0, ref bool __result)
        {
            if (!Waiting(__0)) return true;
            __result = false;
            return false;
        }
        internal static void RestoreVisibility(Character character) => setVisible(character, character.GetComponent<ZNetView>().GetZDO().HasOwner());
        internal static bool Sized(Character character) => character && pending.TryGetValue(character, out var setup) && setup.Sized;
        internal static void Forget(Character character) { CreatureSpawnPlacement.Forget(character); Unpend(character); }
        private static void Unpend(Character character) { if (pending.Remove(character)) pendingCount--; }
    }

    internal sealed class FreshCreaturePresentation : MonoBehaviour
    {
        private Character character;
        private Rigidbody body;
        private RigidbodyConstraints constraints;
        private bool collisions, released;
        private float releaseAt;
        private readonly Dictionary<Renderer, bool> renderers = new Dictionary<Renderer, bool>();

        internal void Begin(Character creature)
        {
            character = creature;
            releaseAt = Time.time + FreshCreatureSpawns.MaximumWait;
            body = GetComponent<Rigidbody>();
            if (body)
            {
                constraints = body.constraints;
                collisions = body.detectCollisions;
                body.constraints = RigidbodyConstraints.FreezeAll;
                body.detectCollisions = false;
            }
            HideRenderers();
        }

        private void LateUpdate()
        {
            if (released) return;
            if (Time.time >= releaseAt) FreshCreatureSpawns.Finish(character, true);
            else if (FreshCreatureSpawns.Sized(character)) FreshCreatureSpawns.Finish(character);
            if (!released) HideRenderers();
        }
        private void HideRenderers()
        {
            // Leave renderers/components enabled: SLS must still find and recolor them, and VisEquipment may add
            // more before the first setup. forceRenderingOff only withholds drawing the unfinished model.
            foreach (var renderer in character.GetVisual().GetComponentsInChildren<Renderer>(true))
            {
                if (!renderers.ContainsKey(renderer)) renderers.Add(renderer, renderer.forceRenderingOff);
                renderer.forceRenderingOff = true;
            }
        }

        internal void Release()
        {
            Restore();
            FreshCreatureSpawns.RestoreVisibility(character);
            Destroy(this);
        }

        internal void Cancel() { Restore(); Destroy(this); }

        private void Restore()
        {
            if (released) return;
            released = true;
            if (body) { body.constraints = constraints; body.detectCollisions = collisions; }
            foreach (var renderer in renderers) if (renderer.Key) renderer.Key.forceRenderingOff = renderer.Value;
        }

        private void OnDestroy()
        {
            Restore();
            if (!ReferenceEquals(character, null)) FreshCreatureSpawns.Forget(character);
        }
    }
}
