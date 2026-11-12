using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Keep the cause on the native death broadcast: a second RPC can arrive after disconnect or the daily cutoff.
    // The native OnDeath receiver has no arguments and ignores these four integers; its animation stays unchanged.
    internal static class DeathAttribution
    {
        internal const int Marker = 0x44454131;
        private const int DamageMask = (1 << 12) - 1;
        private static readonly AccessTools.FieldRef<Character, HitData> LastHit =
            AccessTools.FieldRefAccess<Character, HitData>("m_lastHit");
        private static readonly AccessTools.FieldRef<StatusEffect, Character> EffectCharacter =
            AccessTools.FieldRefAccess<StatusEffect, Character>("m_character");
        private static readonly AccessTools.FieldRef<SE_Poison, float> PoisonLeft = AccessTools.FieldRefAccess<SE_Poison, float>("m_damageLeft");
        // The creature behind each of the local player's fire, spirit and poison effects. Their ticks name no attacker,
        // and naming one would add world-level damage to every tick, so the creature is only kept here for the report.
        private static readonly Dictionary<StatusEffect, Culprit> culprits = new Dictionary<StatusEffect, Culprit>();
        [ThreadStatic] private static StatusEffect tickingEffect;
        private static Player capturedPlayer;
        private static HitData capturedHit;
        private static Snapshot captured;
        private static Culprit capturedCulprit;

        internal sealed class Culprit
        {
            internal ZDOID Id;
            internal int Prefab;
            internal int Level;
        }

        internal sealed class Source
        {
            internal string Key;
            internal string Name;
        }

        private struct Snapshot
        {
            internal int Prefab;
            internal HitData.HitType HitType;
            internal int DamageKinds;
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.ApplyDamage)),
                prefix: new HarmonyMethod(typeof(DeathAttribution), nameof(Capture)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnDeath)),
                transpiler: new HarmonyMethod(typeof(DeathAttribution), nameof(IncludeCause)));
            var burned = new HarmonyMethod(typeof(DeathAttribution), nameof(BurnAdded));
            harmony.Patch(AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddFireDamage)), postfix: burned);
            harmony.Patch(AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.AddSpiritDamage)), postfix: burned);
            harmony.Patch(AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.AddDamage)),
                prefix: new HarmonyMethod(typeof(DeathAttribution), nameof(PoisonOffered)), postfix: new HarmonyMethod(typeof(DeathAttribution), nameof(PoisonAdded)));
            foreach (var tick in new[] { AccessTools.Method(typeof(SE_Burning), nameof(SE_Burning.UpdateStatusEffect)), AccessTools.Method(typeof(SE_Poison), nameof(SE_Poison.UpdateStatusEffect)) })
                harmony.Patch(tick, prefix: new HarmonyMethod(typeof(DeathAttribution), nameof(TickStarts)), finalizer: new HarmonyMethod(typeof(DeathAttribution), nameof(TickEnds)));
            harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.Stop)), prefix: new HarmonyMethod(typeof(DeathAttribution), nameof(EffectStopped)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Shutdown)), prefix: new HarmonyMethod(typeof(DeathAttribution), nameof(Forget)));
        }

        // Only a hit taken with health left becomes the native last hit; a tick landing after the fatal one leaves it.
        private static void Capture(Character __instance, HitData __0)
        {
            if (__instance != Player.m_localPlayer || __0 == null || __instance.GetHealth() <= 0f) return;
            capturedPlayer = Player.m_localPlayer;
            capturedHit = __0;
            captured = Describe(__0);
            capturedCulprit = null;
            // A burn or poison tick names no attacker; the creature that started its effect stands in for it.
            if (captured.Prefab == 0 && __0.m_attacker == ZDOID.None && (__0.m_hitType == HitData.HitType.Burning || __0.m_hitType == HitData.HitType.Poisoned) &&
                tickingEffect != null && EffectCharacter(tickingEffect) == __instance && culprits.TryGetValue(tickingEffect, out var culprit))
            {
                capturedCulprit = culprit;
                captured.Prefab = culprit.Prefab;
            }
        }

        // The creature whose burn or poison dealt the local player's fatal hit, while that hit is still the last one.
        internal static Culprit FatalCulprit(Player player) =>
            player != null && player == capturedPlayer && capturedCulprit != null && ReferenceEquals(LastHit(player), capturedHit) ? capturedCulprit : null;

        // A creature's hit names that creature for the effect it adds to, another player's or the player's own hit
        // clears it, and a dose without an attacker, such as a campfire's, keeps it. Doses the effect refuses change nothing.
        private static void BurnAdded(SE_Burning __instance, bool __result) { if (__result) Remember(__instance); }
        private static void PoisonOffered(SE_Poison __instance, float damage, out bool __state) => __state = damage >= PoisonLeft(__instance);
        private static void PoisonAdded(SE_Poison __instance, bool __state) { if (__state) Remember(__instance); }

        private static void Remember(StatusEffect effect)
        {
            var player = Player.m_localPlayer;
            if (player == null || EffectCharacter(effect) != player) return;
            var hit = CorpseRotKillCredit.HitReceivedBy(player);
            if (hit == null) return;
            // RPC_Damage drops a hit whose attacker is not loaded, so a named attacker is present here.
            var attacker = hit.GetAttacker();
            if (attacker == null && hit.m_hitType != HitData.HitType.Self) return;
            int prefab = attacker != null && !attacker.IsPlayer() ? GameFields.CharacterView(attacker)?.GetZDO()?.GetPrefab() ?? 0 : 0;
            if (prefab == 0) culprits.Remove(effect);
            else culprits[effect] = new Culprit { Id = attacker.GetZDOID(), Prefab = prefab, Level = attacker.GetLevel() };
        }

        private static void TickStarts(StatusEffect __instance, out StatusEffect __state) { __state = tickingEffect; tickingEffect = __instance; }
        private static void TickEnds(StatusEffect __state) => tickingEffect = __state;
        private static void EffectStopped(StatusEffect __instance) => culprits.Remove(__instance);
        private static void Forget() { culprits.Clear(); capturedCulprit = null; }

        private static Snapshot Describe(HitData hit)
        {
            if (hit == null) return default;
            var result = new Snapshot { HitType = hit.m_hitType, DamageKinds = Kinds(hit.m_damage) };
            var attacker = hit.GetAttacker();
            if (attacker != null && attacker.IsPlayer())
            {
                if (result.HitType != HitData.HitType.Self) result.HitType = HitData.HitType.PlayerHit;
            }
            else if (attacker != null)
                result.Prefab = GameFields.CharacterView(attacker)?.GetZDO()?.GetPrefab() ?? 0;
            else if (hit.m_attacker != ZDOID.None)
                result.Prefab = ZDOMan.instance?.GetZDO(hit.m_attacker)?.GetPrefab() ?? 0;
            return result;
        }

        private static int Kinds(HitData.DamageTypes damage)
        {
            int result = 0;
            float[] amounts = { damage.m_damage, damage.m_blunt, damage.m_slash, damage.m_pierce,
                damage.m_chop, damage.m_pickaxe, damage.m_fire, damage.m_frost, damage.m_lightning,
                damage.m_poison, damage.m_spirit, damage.m_nonPlayer };
            for (int index = 0; index < amounts.Length; index++)
                if (amounts[index] > 0) result |= 1 << index;
            return result;
        }

        private static void SendDeath(ZNetView view, long target, string method, object[] parameters)
        {
            var player = Player.m_localPlayer;
            bool localDeath = player != null && GameFields.CharacterView(player) == view && view.IsOwner() && target == ZNetView.Everybody && method == "OnDeath";
            if (localDeath)
            {
                var hit = LastHit(player);
                var cause = Describe(hit);
                if (player == capturedPlayer && ReferenceEquals(hit, capturedHit))
                {
                    if (cause.Prefab == 0) cause.Prefab = captured.Prefab;
                    if (captured.HitType == HitData.HitType.PlayerHit && cause.HitType == HitData.HitType.EnemyHit)
                        cause.HitType = HitData.HitType.PlayerHit;
                }
                parameters = new object[] { Marker, cause.Prefab, (int)cause.HitType, cause.DamageKinds };
            }
            try { view.InvokeRPC(target, method, parameters); }
            finally
            {
                if (localDeath) { capturedPlayer = null; capturedHit = null; captured = default; capturedCulprit = null; }
            }
        }

        private static IEnumerable<CodeInstruction> IncludeCause(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions,
                x => x.Calls(AccessTools.Method(typeof(ZNetView), nameof(ZNetView.InvokeRPC), new[] { typeof(long), typeof(string), typeof(object[]) })),
                AccessTools.Method(typeof(DeathAttribution), nameof(SendDeath)), 1, "Native player death broadcast changed.");

        // Called only after the existing recorder has validated the sending peer and current character.
        internal static Source Read(ZPackage package)
        {
            if (package == null || package.Size() == 0) return null;
            int position = package.GetPos();
            try
            {
                if (package.Size() != 16) throw new InvalidDataException("Death attribution must contain four integers.");
                package.SetPos(0);
                if (package.ReadInt() != Marker) throw new InvalidDataException("Unsupported death attribution version.");
                int prefabHash = package.ReadInt();
                var hitType = (HitData.HitType)package.ReadInt();
                int kinds = package.ReadInt();
                if (!Enum.IsDefined(typeof(HitData.HitType), hitType) || kinds < 0 || (kinds & ~DamageMask) != 0)
                    throw new InvalidDataException("Invalid death attribution values.");
                if (prefabHash != 0)
                {
                    var prefab = ZNetScene.instance.GetPrefab(prefabHash);
                    var creature = prefab != null ? prefab.GetComponent<Character>() : null;
                    if (creature == null || creature.IsPlayer()) throw new InvalidDataException("Death attribution has no creature prefab.");
                    string key = string.IsNullOrWhiteSpace(creature.m_name) ? prefab.name : creature.m_name;
                    EnglishText.TryTranslate(key, out string name);
                    return new Source { Key = "creature:" + key, Name = name };
                }
                return new Source { Key = "cause:" + hitType + (hitType is HitData.HitType.Undefined or HitData.HitType.EnemyHit ? ":" + kinds : ""),
                    Name = CauseName(hitType, kinds) };
            }
            finally { package.SetPos(position); }
        }

        private static string CauseName(HitData.HitType type, int kinds)
        {
            switch (type)
            {
                case HitData.HitType.PlayerHit: return "Other player";
                case HitData.HitType.Fall: return "Falling";
                case HitData.HitType.Drowning: return "Drowning";
                case HitData.HitType.Burning: return "Burning";
                case HitData.HitType.Freezing: return "Freezing";
                case HitData.HitType.Poisoned: return "Poison";
                case HitData.HitType.Water: return "Water";
                case HitData.HitType.Smoke: return "Smoke";
                case HitData.HitType.EdgeOfWorld: return "World edge";
                case HitData.HitType.Impact: return "Impact";
                case HitData.HitType.Cart: return "Cart";
                case HitData.HitType.Tree: return "Falling tree";
                case HitData.HitType.Self: return "Self-inflicted";
                case HitData.HitType.Structural: return "Collapsing structure";
                case HitData.HitType.Turret: return "Turret";
                case HitData.HitType.Boat: return "Boat";
                case HitData.HitType.Stalagtite: return "Falling stalactite";
                case HitData.HitType.Catapult: return "Catapult";
                case HitData.HitType.CinderFire: return "Cinder fire";
                case HitData.HitType.AshlandsOcean: return "Ashlands sea";
                case HitData.HitType.AshlandsLava: return "Ashlands lava";
                case HitData.HitType.Incinerator: return "Incinerator";
                case HitData.HitType.DrawBridge: return "Drawbridge";
            }
            if (kinds == 0) return "Health loss";
            if (kinds == 1) return type == HitData.HitType.EnemyHit ? "Unidentified creature attack" : "Other damage";
            var names = new List<string>();
            string[] damageNames = { "other", "blunt", "slash", "piercing", "chop", "pickaxe", "fire", "frost", "lightning", "poison", "spirit", "other" };
            for (int index = 0; index < damageNames.Length; index++)
                if ((kinds & (1 << index)) != 0) { if (!names.Contains(damageNames[index])) names.Add(damageNames[index]); }
            string damage = string.Join("/", names);
            return char.ToUpperInvariant(damage[0]) + damage.Substring(1) + " damage";
        }
    }
}
