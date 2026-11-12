using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class ProtectionBubble
    {
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<SE_Shield, float> Damage = AccessTools.FieldRefAccess<SE_Shield, float>("m_damage");
        private static readonly Func<Character, short, WeakSpot> WeakSpotOf =
            AccessTools.MethodDelegate<Func<Character, short, WeakSpot>>(AccessTools.Method(typeof(Character), "GetWeakSpot"));
        private static readonly ConditionalWeakTable<HitData, object> ButcherHits = new ConditionalWeakTable<HitData, object>();
        private static readonly object Butcher = new object();
        // The packet already carries a cause byte. Encode this one exact weapon source there, and decode it back before
        // any damage handler sees it. The original hit, packet length, saved last hit and ordinary causes stay native.
        private const byte ButcherCause = 254;
        [ThreadStatic] private static HitData directTick;

        internal static void Initialize(IntegrationPatches patches)
        {
            if (Enum.IsDefined(typeof(HitData.HitType), ButcherCause))
                throw new InvalidOperationException("The game's damage causes now use the butcher source identifier.");
            var type = typeof(ProtectionBubble);
            patches.Patch(AccessTools.Method(typeof(SE_Shield), nameof(SE_Shield.OnDamaged)), prefix: new HarmonyMethod(type, nameof(MeasureHarmless)),
                postfix: new HarmonyMethod(type, nameof(AbsorbGeneric)));
            foreach (var effect in new[] { typeof(SE_Smoke), typeof(SE_Poison), typeof(SE_Burning) })
                patches.Patch(AccessTools.Method(effect, nameof(StatusEffect.UpdateStatusEffect)), transpiler: new HarmonyMethod(type, nameof(BridgeTick)));
            patches.Patch(AccessTools.Method(typeof(Character), nameof(Character.ApplyDamage)), transpiler: new HarmonyMethod(type, nameof(AfterDamageGuards)));
            patches.Patch(AccessTools.Method(typeof(Character), "RPC_Damage"), transpiler: new HarmonyMethod(type, nameof(RefreshApplication)));
            patches.Patch(AccessTools.Method(typeof(HitData), nameof(HitData.Serialize)), transpiler: new HarmonyMethod(type, nameof(SerializeSource)));
            patches.Patch(AccessTools.Method(typeof(HitData), nameof(HitData.Deserialize)), postfix: new HarmonyMethod(type, nameof(DeserializeSource)));
            patches.Patch(AccessTools.Method(typeof(HitData), nameof(HitData.Clone)), postfix: new HarmonyMethod(type, nameof(CopySource)));
        }

        private static bool Eligible(HitData hit)
        {
            if (ReferenceEquals(hit, directTick)) return true;
            switch (hit.m_hitType)
            {
                case HitData.HitType.Freezing:
                case HitData.HitType.AshlandsLava:
                case HitData.HitType.AshlandsOcean:
                case HitData.HitType.Fall:
                case HitData.HitType.EnemyHit:
                case HitData.HitType.PlayerHit:
                case HitData.HitType.Turret:
                case HitData.HitType.Catapult:
                    return !ButcherHits.TryGetValue(hit, out _);
                default:
                    return false;
            }
        }

        // The bubble counts a hit before the character's resistances. Damage types the character is immune to or ignores
        // would cost it no health, as a battering ram's pickaxe punch costs a player none, so they spend none of the bubble.
        // Status ticks already carry their final damage.
        private static void MeasureHarmless(SE_Shield __instance, HitData hit, out float __state)
        {
            __state = 0f;
            var character = __instance.m_character;
            if (__instance.NameHash() != ShieldHash || !character || ReferenceEquals(hit, directTick)) return;
            var modifiers = character.GetDamageModifiers(WeakSpotOf(character, hit.m_weakSpot));
            var damage = hit.m_damage;
            float Harmless(float amount, HitData.DamageModifier modifier) =>
                modifier == HitData.DamageModifier.Immune || modifier == HitData.DamageModifier.Ignore ? amount : 0f;
            __state = Harmless(damage.m_blunt, modifiers.m_blunt) + Harmless(damage.m_slash, modifiers.m_slash) +
                Harmless(damage.m_pierce, modifiers.m_pierce) + Harmless(damage.m_chop, modifiers.m_chop) +
                Harmless(damage.m_pickaxe, modifiers.m_pickaxe) + Harmless(damage.m_fire, modifiers.m_fire) +
                Harmless(damage.m_frost, modifiers.m_frost) + Harmless(damage.m_lightning, modifiers.m_lightning) +
                Harmless(damage.m_poison, modifiers.m_poison) + Harmless(damage.m_spirit, modifiers.m_spirit) +
                Harmless(damage.m_nonPlayer, modifiers.m_nonPlayer);
        }

        private static void AbsorbGeneric(SE_Shield __instance, HitData hit, float __state)
        {
            if (__instance.NameHash() != ShieldHash) return;
            Damage(__instance) -= __state;
            // Native accounting has already counted generic damage alongside every ordinary type.
            if (hit.m_damage.m_damage != 0f && Eligible(hit)) hit.m_damage.m_damage = 0f;
        }

        private static IEnumerable<CodeInstruction> BridgeTick(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.ApplyDamage))),
                AccessTools.Method(typeof(ProtectionBubble), nameof(ApplyTick)), 1, "A protection-eligible status tick's direct damage call changed.");

        private static void ApplyTick(Character target, HitData hit, bool showDamageText, bool triggerEffects, HitData.DamageModifier modifier)
        {
            var previous = directTick; directTick = hit;
            try { target.ApplyDamage(hit, showDamageText, triggerEffects, modifier); }
            finally { directTick = previous; }
        }

        private static IEnumerable<CodeInstruction> AfterDamageGuards(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            var anchors = code.Select((instruction, index) => (instruction, index)).Where(entry => entry.instruction.IsLdarg(2)).ToArray();
            if (anchors.Length != 1 || anchors[0].index == 0 || code[anchors[0].index - 1].opcode != OpCodes.Ret)
                throw new InvalidOperationException("Character's guarded damage-text branch changed.");
            int index = anchors[0].index;
            var resume = generator.DefineLabel();
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[index].labels); code[index].labels.Clear(); code[index].labels.Add(resume);
            code.InsertRange(index, new[] { first, new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProtectionBubble), nameof(AbsorbTick))),
                new CodeInstruction(OpCodes.Brfalse, resume), new CodeInstruction(OpCodes.Ret) });
            return code;
        }

        private static bool AbsorbTick(Character target, HitData hit)
        {
            if (!ReferenceEquals(hit, directTick)) return false;
            var shield = target.GetSEMan().GetStatusEffect(ShieldHash) as SE_Shield;
            if (!shield) return false;
            // Native damage eligibility, scaling and the zero-damage threshold ran before this point. Only the bubble
            // handles this spent tick; attack handlers and resistance calculations are not replayed.
            var direction = hit.m_dir;
            if (direction.sqrMagnitude == 0f) hit.m_dir = Vector3.down;
            try { shield.OnDamaged(hit, hit.GetAttacker()); }
            finally { hit.m_dir = direction; }
            return true;
        }

        private static IEnumerable<CodeInstruction> RefreshApplication(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.SetLevel))),
                AccessTools.Method(typeof(ProtectionBubble), nameof(ApplyLevel)), 1, "The actual status-effect reapplication path changed.");

        private static void ApplyLevel(StatusEffect effect, int itemLevel, float skillLevel)
        {
            var shield = effect as SE_Shield;
            bool protection = shield && shield.NameHash() == ShieldHash;
            if (protection) ProtectionCasterExperience.Reapplying(shield);
            effect.SetLevel(itemLevel, skillLevel);
            if (protection) Damage(shield) = 0f;
        }

        internal static void MarkWeaponSource(HitData hit, ItemDrop.ItemData weapon)
        {
            if (weapon?.m_shared.m_tamedOnly == true && hit.m_hitType == HitData.HitType.PlayerHit)
                ButcherHits.GetValue(hit, _ => Butcher);
        }

        private static IEnumerable<CodeInstruction> SerializeSource(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(HitData), nameof(HitData.m_hitType))),
                AccessTools.Method(typeof(ProtectionBubble), nameof(SerializedCause)), 1, "HitData's serialized damage cause changed.");

        private static HitData.HitType SerializedCause(HitData hit) => hit.m_hitType == HitData.HitType.PlayerHit && ButcherHits.TryGetValue(hit, out _)
            ? (HitData.HitType)ButcherCause : hit.m_hitType;

        private static void DeserializeSource(HitData __instance)
        {
            ButcherHits.Remove(__instance);
            if ((byte)__instance.m_hitType != ButcherCause) return;
            __instance.m_hitType = HitData.HitType.PlayerHit;
            ButcherHits.Add(__instance, Butcher);
        }

        private static void CopySource(HitData __instance, HitData __result)
        {
            if (ButcherHits.TryGetValue(__instance, out _)) ButcherHits.GetValue(__result, _ => Butcher);
        }
    }
}
