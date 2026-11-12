using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EpicLoot;
using EpicLoot.MagicItemEffects.Shards;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal sealed class CorpseRotDeathCredit : MonoBehaviour
    {
        internal long Player;
        internal bool Reported;
    }

    internal static class CorpseRotKillCredit
    {
        private const string DeathRequest = Plugin.Id + ".corpse-rot-death";
        private const string DeathDelivery = Plugin.Id + ".corpse-rot-delivery";
        private const string PoisonPlayer = Plugin.Id + ".corpse-rot-poison-player";
        private const string PoisonExpiry = Plugin.Id + ".corpse-rot-poison-expiry";
        private const string BurningPlayer = Plugin.Id + ".corpse-rot-burning-player";
        private const string BurningExpiry = Plugin.Id + ".corpse-rot-burning-expiry";
        private const string SpiritPlayer = Plugin.Id + ".corpse-rot-spirit-player";
        private const string SpiritExpiry = Plugin.Id + ".corpse-rot-spirit-expiry";
        private const string LethalPlayer = Plugin.Id + ".corpse-rot-lethal-player";
        private const float DestructionLifetime = 15f;
        private static readonly int Cooldown = "EL_BonemassPoisonCooldown".GetStableHashCode();
        private static readonly HashSet<ZDOID> Consumed = new HashSet<ZDOID>();
        private static readonly HashSet<ZDOID> Forwarded = new HashSet<ZDOID>();
        private static readonly Dictionary<ZDOID, (long Owner, float Removed)> Destroyed = new Dictionary<ZDOID, (long, float)>();
        private static readonly AccessTools.FieldRef<SE_Poison, float> PoisonRemaining = AccessTools.FieldRefAccess<SE_Poison, float>("m_damageLeft");
        private static readonly AccessTools.FieldRef<StatusEffect, Character> EffectCharacter = AccessTools.FieldRefAccess<StatusEffect, Character>("m_character");
        private static readonly Action<Vector3> Explosion = AccessTools.MethodDelegate<Action<Vector3>>(AccessTools.Method(typeof(BonemassCorpseRot), "SpawnExplosionFx"));
        private static readonly Action<Player> ShowCooldown = AccessTools.MethodDelegate<Action<Player>>(AccessTools.Method(typeof(BonemassCorpseRot), "ShowCooldown"));
        [ThreadStatic] private static Character ReceivingCharacter, TickCharacter;
        [ThreadStatic] private static HitData ReceivingHit;
        [ThreadStatic] private static long TickingPlayer;

        internal static long PeriodicDamagePlayer(Character character) => character == TickCharacter ? TickingPlayer : 0L;

        // The hit RPC_Damage is applying to this character, while it applies it; the death report reads it too.
        internal static HitData HitReceivedBy(Character character) => character != null && character == ReceivingCharacter ? ReceivingHit : null;
        internal static long LethalPlayerOf(Character character)
        {
            var credit = character.GetComponent<CorpseRotDeathCredit>();
            return credit ? credit.Player : GameFields.CharacterView(character).GetZDO().GetLong(LethalPlayer);
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            var old = AccessTools.Method(typeof(BonemassCorpseRot).GetNestedType("OnDeath_Patch", System.Reflection.BindingFlags.NonPublic), "Prefix");
            if (old == null) throw new InvalidOperationException("EpicLoot's Corpse Rot death handler moved.");
            harmony.Unpatch(AccessTools.Method(typeof(Character), "OnDeath"), old);
        }

        private static long Credit(Character attacker)
        {
            if (!attacker) return 0;
            if (attacker is Player player) return player.GetPlayerID();
            long owner = SummonedBats.SummonerId(attacker);
            if (owner != 0) return owner;
            var view = GameFields.CharacterView(attacker);
            if (!attacker.IsTamed() || !view || !view.IsValid()) return 0;
            var data = view.GetZDO();
            owner = data.GetLong("el-sumby", 0L);
            return owner != 0 ? owner : data.GetLong("el-spirit", 0L);
        }

        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class ReceivingDamage
        {
            private static void Prefix(Character __instance, HitData hit, out (Character Character, HitData Hit) __state)
            {
                __state = (ReceivingCharacter, ReceivingHit);
                ReceivingCharacter = __instance;
                ReceivingHit = hit;
            }
            private static void Finalizer((Character Character, HitData Hit) __state)
            { ReceivingCharacter = __state.Character; ReceivingHit = __state.Hit; }
        }

        [HarmonyPatch(typeof(SE_Poison), nameof(SE_Poison.AddDamage))]
        private static class AcceptedPoison
        {
            private static void Prefix(SE_Poison __instance, float damage, out bool __state) => __state = damage >= PoisonRemaining(__instance);
            private static void Postfix(SE_Poison __instance, bool __state)
            {
                var character = EffectCharacter(__instance);
                var view = character ? GameFields.CharacterView(character) : null;
                if (!__state || !view || !view.IsValid() || !view.IsOwner() || character.IsPlayer()) return;
                long player = character == ReceivingCharacter ? Credit(ReceivingHit?.GetAttacker()) : 0;
                var data = view.GetZDO();
                data.Set(PoisonPlayer, player);
                data.Set(PoisonExpiry, player == 0 ? 0L : ZNet.instance.GetTime().AddSeconds(__instance.m_ttl).Ticks);
            }
        }

        // Fire and spirit damage over time, separate burns, add up every accepted dose and run their full time past the
        // latest. The last player whose hit added to a burn is credited for it; a dose without a player keeps the burn and
        // that credit going.
        [HarmonyPatch]
        private static class AcceptedBurn
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddFireDamage));
                yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddSpiritDamage));
            }
            private static void Postfix(SE_Burning __instance, bool __result)
            {
                var character = EffectCharacter(__instance);
                var view = character ? GameFields.CharacterView(character) : null;
                if (!__result || !view || !view.IsValid() || !view.IsOwner() || character.IsPlayer()) return;
                var (playerKey, expiryKey) = Keys(__instance);
                long player = character == ReceivingCharacter ? Credit(ReceivingHit?.GetAttacker()) : 0;
                var data = view.GetZDO();
                var now = ZNet.instance.GetTime();
                if (player != 0) data.Set(playerKey, player);
                else if (data.GetLong(expiryKey) < now.Ticks) return;
                data.Set(expiryKey, now.AddSeconds(__instance.m_ttl).Ticks);
            }
        }

        // The credit keys of a damage-over-time effect: poison, or the fire or spirit burn.
        private static (string Player, string Expiry) Keys(StatusEffect effect) =>
            effect is SE_Poison ? (PoisonPlayer, PoisonExpiry) : effect.NameHash() == SEMan.s_statusEffectSpirit ? (SpiritPlayer, SpiritExpiry) : (BurningPlayer, BurningExpiry);

        // While a poison or burn ticks, its still valid credit stands in for the tick's missing attacker.
        [HarmonyPatch]
        private static class DamageOverTimeTick
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect));
                yield return AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect));
            }
            private static void Prefix(StatusEffect __instance, out (Character Character, long Player) __state)
            {
                __state = (TickCharacter, TickingPlayer);
                TickCharacter = EffectCharacter(__instance);
                var data = TickCharacter ? GameFields.CharacterView(TickCharacter)?.GetZDO() : null;
                var (playerKey, expiryKey) = Keys(__instance);
                TickingPlayer = data != null && data.GetLong(expiryKey) >= ZNet.instance.GetTime().Ticks ? data.GetLong(playerKey) : 0;
            }
            private static void Finalizer((Character Character, long Player) __state)
            { TickCharacter = __state.Character; TickingPlayer = __state.Player; }
        }

        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Stop))]
        private static class EndDamageOverTime
        {
            private static void Prefix(StatusEffect __instance)
            {
                if (!(__instance is SE_Poison) && !(__instance is SE_Burning)) return;
                var character = EffectCharacter(__instance);
                var view = character ? GameFields.CharacterView(character) : null;
                if (!view || !view.IsValid() || !view.IsOwner() || character.IsPlayer()) return;
                var (playerKey, expiryKey) = Keys(__instance);
                view.GetZDO().Set(playerKey, 0L);
                view.GetZDO().Set(expiryKey, 0L);
            }
        }

        // EpicLoot's other strikes name their wearer as the attacker; Stormcaller's lightning named no one, so a creature
        // it killed credited no one. It strikes only from the local player's own hits.
        [HarmonyPatch(typeof(StrikeCausesLightning), nameof(StrikeCausesLightning.OnDamageDealt))]
        private static class StormcallerStrike
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(DamageInRadius), nameof(DamageInRadius.DamageEnemiesInRadius))),
                    AccessTools.Method(typeof(CorpseRotKillCredit), nameof(Stormcaller)), 1, "EpicLoot's Stormcaller strike changed; expected one radius damage.");
        }

        // Takes the native DamageInRadius.DamageEnemiesInRadius arguments.
        private static void Stormcaller(Player attacker, Vector3 center, float radius, HitData.DamageTypes damage) =>
            DamageInRadius.DamageEnemiesInRadius(attacker ? attacker : Player.m_localPlayer, center, radius, damage);

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        private static class LethalDamage
        {
            private static void Prefix(Character __instance, out float __state) => __state = __instance.GetHealth();
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Character __instance, HitData hit, float __state)
            {
                var view = GameFields.CharacterView(__instance);
                if (__state <= 0f || __instance.GetHealth() > 0f || __instance.IsPlayer() || !view || !view.IsValid() || !view.IsOwner()) return;
                long player = Credit(hit?.GetAttacker());
                if (player == 0 && (hit?.m_hitType == HitData.HitType.Poisoned || hit?.m_hitType == HitData.HitType.Burning) && TickCharacter == __instance)
                    player = TickingPlayer;
                var credit = __instance.GetComponent<CorpseRotDeathCredit>() ?? __instance.gameObject.AddComponent<CorpseRotDeathCredit>();
                credit.Player = player;
                view.GetZDO().Set(LethalPlayer, player);
            }
        }

        [HarmonyPatch(typeof(Character), "OnDeath")]
        private static class CreatureDeath
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Character __instance)
            {
                var view = GameFields.CharacterView(__instance);
                var credit = __instance.GetComponent<CorpseRotDeathCredit>();
                if (__instance.IsPlayer() || !view || !view.IsValid() || !view.IsOwner() || (credit && credit.Reported)) return;
                long player = LethalPlayerOf(__instance);
                if (player == 0) return;
                if (!credit) credit = __instance.gameObject.AddComponent<CorpseRotDeathCredit>();
                credit.Reported = true;
                ReportDeath(view.GetZDO().m_uid, player, __instance.GetCenterPoint(), __instance.transform.position);
            }
        }

        private static ZPackage Event(ZDOID creature, long player, Vector3 center, Vector3 position)
        {
            var package = new ZPackage(); package.Write(creature); package.Write(player); package.Write(center); package.Write(position);
            return package;
        }

        private static void ReportDeath(ZDOID creature, long player, Vector3 center, Vector3 position)
        {
            var local = Player.m_localPlayer;
            if (local && local.GetPlayerID() == player) { Burst(creature, player, center, position); return; }
            var package = Event(creature, player, center, position);
            if (ZNet.instance.IsServer()) ReceiveDeath(null, new ZPackage(package.GetArray()));
            else ZNet.instance.GetServerPeer()?.m_rpc.Invoke(DeathRequest, package);
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class RegisterTransport
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (__instance.IsServer()) peer.m_rpc.Register<ZPackage>(DeathRequest, (rpc, package) =>
                { if (rpc == peer.m_rpc && peer.IsReady()) ReceiveDeath(peer, package); });
                else if (peer.m_server) peer.m_rpc.Register<ZPackage>(DeathDelivery, (rpc, package) =>
                { if (rpc == ZNet.instance.GetServerPeer()?.m_rpc) ReceiveDelivery(package); });
            }
        }

        private static bool ReadEvent(ZPackage package, out ZDOID creature, out long player, out Vector3 center, out Vector3 position)
        {
            creature = ZDOID.None; player = 0; center = position = Vector3.zero;
            try
            {
                creature = package.ReadZDOID(); player = package.ReadLong(); center = package.ReadVector3(); position = package.ReadVector3();
                return creature != ZDOID.None && player != 0 && Finite(center) && Finite(position);
            }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException) { return false; }
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.sqrMagnitude) && !float.IsInfinity(value.sqrMagnitude);

        private static void ReceiveDeath(ZNetPeer sender, ZPackage package)
        {
            if (!ZNet.instance.IsServer() || !ReadEvent(package, out var creature, out long player, out var center, out var position)) return;
            if (sender != null && (!sender.IsReady() || !sender.m_rpc.IsConnected() || sender.m_server || sender.m_uid == ZDOMan.GetSessionID())) return;
            long peerId = sender == null ? ZDOMan.GetSessionID() : sender.m_uid;
            var data = ZDOMan.instance.GetZDO(creature);
            if (data != null)
            {
                if (data.GetOwner() != peerId || !IsCreature(data)) return;
            }
            else if (!Destroyed.TryGetValue(creature, out var removed) || removed.Owner != peerId || Time.realtimeSinceStartup - removed.Removed > DestructionLifetime) return;
            if (!Forwarded.Add(creature)) return;
            var local = Player.m_localPlayer;
            if (local && local.GetPlayerID() == player) { Burst(creature, player, center, position); return; }
            foreach (var peer in ZNet.instance.GetPeers())
                if (peer.IsReady() && peer.m_rpc.IsConnected() && !peer.m_server && ZDOMan.instance.GetZDO(peer.m_characterID)?.GetLong(ZDOVars.s_playerID) == player)
                { peer.m_rpc.Invoke(DeathDelivery, Event(creature, player, center, position)); break; }
        }

        private static bool IsCreature(ZDO data)
        {
            var prefab = ZNetScene.instance.GetPrefab(data.GetPrefab());
            var character = prefab ? prefab.GetComponent<Character>() : null;
            return character && !character.IsPlayer();
        }

        private static void ReceiveDelivery(ZPackage package)
        {
            if (ReadEvent(package, out var creature, out long player, out var center, out var position)) Burst(creature, player, center, position);
        }

        private static void Burst(ZDOID creature, long playerId, Vector3 center, Vector3 position)
        {
            var player = Player.m_localPlayer;
            if (!player || player.GetPlayerID() != playerId || !Consumed.Add(creature) || player.IsDead() ||
                !player.HasActiveMagicEffect(MagicEffectType.CorpseRot, out float strength) || strength <= 0f || player.GetSEMan().HaveStatusEffect(Cooldown)) return;
            // Consume the death and start the native cooldown before damage can cause another credited death.
            ShowCooldown(player);
            Explosion(position);
            DamageInRadius.DamageEnemiesInRadius(player, center, EffectConfig.Get(MagicEffectType.CorpseRot, "Radius", 2f),
                new HitData.DamageTypes { m_poison = strength * EffectConfig.Get(MagicEffectType.CorpseRot, "PoisonPerTier", 20f) });
        }

        [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
        private static class RememberDestruction
        {
            private static void Prefix(ZDOID uid)
            {
                if (!ZNet.instance.IsServer()) return;
                foreach (var expired in Destroyed.Where(pair => Time.realtimeSinceStartup - pair.Value.Removed > DestructionLifetime).Select(pair => pair.Key).ToArray()) Destroyed.Remove(expired);
                var data = ZDOMan.instance.GetZDO(uid);
                if (data != null && IsCreature(data)) Destroyed[uid] = (data.GetOwner(), Time.realtimeSinceStartup);
            }
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class EndSession
        {
            private static void Postfix() { Consumed.Clear(); Forwarded.Clear(); Destroyed.Clear(); ReceivingCharacter = TickCharacter = null; ReceivingHit = null; TickingPlayer = 0; }
        }
    }
}
