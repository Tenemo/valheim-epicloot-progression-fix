using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // SLS keeps its received-damage multipliers outside Character.GetDamageModifiers. Include them when choosing
    // the cape shard's conversion type, but leave the amount and the application of those multipliers to the mods.
    [HarmonyPatch(typeof(PoisonToTrueDamage), nameof(PoisonToTrueDamage.ModifyOutgoingHit))]
    internal static class PoisonConversion
    {
        private static readonly HitData.DamageType[] DamageTypes =
            { HitData.DamageType.Blunt, HitData.DamageType.Slash, HitData.DamageType.Pierce, HitData.DamageType.Fire,
              HitData.DamageType.Frost, HitData.DamageType.Lightning, HitData.DamageType.Spirit };
        private static readonly MethodInfo OriginalSelection = AccessTools.Method(typeof(PoisonToTrueDamage), "AddToLeastResisted");
        private static readonly Func<HitData.DamageModifier, float> Effectiveness =
            AccessTools.MethodDelegate<Func<HitData.DamageModifier, float>>(AccessTools.Method(typeof(PoisonToTrueDamage), "Effectiveness"));
        private static readonly PropertyInfo ReceivedDamageModifiers;
        private static readonly object[] StarDamageTypes;

        static PoisonConversion()
        {
            if (!StarLevelSystem.Installed) return;
            ReceivedDamageModifiers = AccessTools.Property(StarLevelSystem.CharacterCacheEntry, "DamageRecievedModifiers");
            if (ReceivedDamageModifiers == null) throw new InvalidOperationException("StarLevelSystem's damage resistances changed.");
            var damageType = ReceivedDamageModifiers.PropertyType.GetGenericArguments()[0];
            StarDamageTypes = new object[DamageTypes.Length];
            for (int index = 0; index < DamageTypes.Length; index++)
                StarDamageTypes[index] = Enum.Parse(damageType, DamageTypes[index].ToString());
        }

        private static bool Prepare() => StarLevelSystem.Installed;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (!instruction.Calls(OriginalSelection))
                {
                    yield return instruction;
                    continue;
                }
                // The original handler keeps all its guards and computes the same poison share. Supply the target
                // only to the selection helper, before SLS applies its reductions to the completed outgoing hit.
                yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PoisonConversion), nameof(AddConvertedDamage)));
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException("EpicLoot's poison conversion changed.");
        }

        private static void AddConvertedDamage(ref HitData.DamageTypes damage, HitData.DamageModifiers resistances, float amount, Character target)
        {
            var cacheEntry = StarLevelSystem.CacheEntryOf(target);
            var receivedDamage = cacheEntry == null ? null : (IDictionary)ReceivedDamageModifiers.GetValue(cacheEntry);
            var bestType = HitData.DamageType.Blunt;
            float bestScore = float.MinValue;
            for (int index = 0; index < DamageTypes.Length; index++)
            {
                var damageType = DamageTypes[index];
                float score = Effectiveness(resistances.GetModifier(damageType));
                if (receivedDamage != null && receivedDamage.Contains(StarDamageTypes[index]))
                    score *= (float)receivedDamage[StarDamageTypes[index]];
                if (score <= bestScore) continue;
                bestType = damageType;
                bestScore = score;
            }
            switch (bestType)
            {
                case HitData.DamageType.Blunt: damage.m_blunt += amount; break;
                case HitData.DamageType.Slash: damage.m_slash += amount; break;
                case HitData.DamageType.Pierce: damage.m_pierce += amount; break;
                case HitData.DamageType.Fire: damage.m_fire += amount; break;
                case HitData.DamageType.Frost: damage.m_frost += amount; break;
                case HitData.DamageType.Lightning: damage.m_lightning += amount; break;
                case HitData.DamageType.Spirit: damage.m_spirit += amount; break;
            }
        }
    }
}
