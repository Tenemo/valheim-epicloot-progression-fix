using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SummonHealth
    {
        private static readonly int BonusKey = (Plugin.Id + ".summon-health-bonus").GetStableHashCode();
        private static AccessTools.FieldRef<bool> forceUpdate;

        internal static void Initialize(IntegrationPatches patches)
        {
            if (!StarLevelSystem.Installed) return;
            var health = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Health.HealthModifications", true);
            var apply = AccessTools.Method(health, "ApplyHealthModifications", new[] { typeof(Character), StarLevelSystem.CharacterCacheEntry });
            var force = AccessTools.Field(health, "ForceUpdateHealth");
            if (apply?.ReturnType != typeof(void) || force?.FieldType != typeof(bool) || !force.IsStatic)
                throw new InvalidOperationException("StarLevelSystem's creature health update changed.");
            forceUpdate = AccessTools.StaticFieldRefAccess<bool>(force);
            patches.Patch(apply, prefix: new HarmonyMethod(typeof(SummonHealth), nameof(Before)),
                finalizer: new HarmonyMethod(typeof(SummonHealth), nameof(After)),
                transpiler: new HarmonyMethod(typeof(SummonHealth), nameof(PreserveMissingHealth)));
            patches.Patch(AccessTools.Method(typeof(EpicLoot.MagicItemEffects.ModifySummonHealth), "Prefix"),
                prefix: new HarmonyMethod(typeof(SummonHealth), nameof(ApplyBonus)));
        }

        private static void Before(Character __0, out bool __state)
        {
            __state = forceUpdate();
            if (MagicSummon(__0) && Bonus(__0) > 0f) forceUpdate() = true;
        }

        private static void After(bool __state) => forceUpdate() = __state;

        private static float Bonus(Character character)
        {
            var data = GameFields.CharacterView(character).GetZDO();
            if (Player.m_localPlayer)
            {
                float bonus = Player.m_localPlayer.HasActiveMagicEffect(MagicEffectType.ModifySummonHealth, out float value, .01f) ? value : 0f;
                data.Set(BonusKey, bonus);
                return bonus;
            }
            return data.GetFloat(BonusKey);
        }

        // The server has no local player. Carry the client's applied bonus with the summon across ownership and loads;
        // clients still use the current equipped effect, as EpicLoot does.
        private static bool ApplyBonus(Character __0, ref float __1)
        {
            if (!MagicSummon(__0)) return true;
            __1 *= 1f + Bonus(__0);
            return false;
        }

        private static bool MagicSummon(Character character)
        {
            if (!character || !character.IsTamed()) return false;
            var view = GameFields.CharacterView(character);
            var tame = character.GetComponent<Tameable>();
            return view && view.IsValid() && view.IsOwner() && tame &&
                (tame.m_levelUpOwnerSkill == Skills.SkillType.BloodMagic || tame.m_levelUpOwnerSkill == Skills.SkillType.ElementalMagic);
        }

        private static IEnumerable<CodeInstruction> PreserveMissingHealth(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var setMaximum = AccessTools.Method(typeof(Character), nameof(Character.SetMaxHealth));
            int maximum = code.FindIndex(instruction => instruction.Calls(setMaximum));
            if (maximum < 1 || code.Count(instruction => instruction.Calls(setMaximum)) != 1 ||
                !code[maximum - 1].opcode.Name.StartsWith("ldloc") || code[maximum - 1].opcode.Name.StartsWith("ldloca"))
                throw new InvalidOperationException("StarLevelSystem's saved missing-health calculation changed.");
            var target = code[maximum - 1];
            var store = new CodeInstruction(OpCodes.Stloc, target.operand);
            if (target.opcode == OpCodes.Ldloc_0) store.opcode = OpCodes.Stloc_0;
            else if (target.opcode == OpCodes.Ldloc_1) store.opcode = OpCodes.Stloc_1;
            else if (target.opcode == OpCodes.Ldloc_2) store.opcode = OpCodes.Stloc_2;
            else if (target.opcode == OpCodes.Ldloc_3) store.opcode = OpCodes.Stloc_3;
            else if (target.opcode == OpCodes.Ldloc_S) store.opcode = OpCodes.Stloc_S;
            code.InsertRange(maximum + 1, new[] { new CodeInstruction(target.opcode, target.operand),
                new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SummonHealth), nameof(AppliedMaximum))), store });
            return code;
        }

        // Both the missing-health restoration and fresh-summon healing use the maximum EpicLoot actually applied.
        private static float AppliedMaximum(float nativeMaximum, Character character) => MagicSummon(character) ? character.GetMaxHealth() : nativeMaximum;
    }
}
