using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Configuration;
using System.Reflection;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot adds its skill bonuses, such as +Blood Magic on a staff or +Movement Skills on legs, to the skill factor
    // and to the levels the game reads for hits, parries and summons, but not to the level SkilledCarryWeight reads for
    // carry weight. Count those of worn gear there too, leaving out what is in the hands so that changing weapons can't
    // overburden the player, and show the status effects in a tooltip with the bonuses of its weapon as if it were
    // equipped, like the weapon's damage range.
    //
    // SkilledCarryWeight 1.6.1 reads each learned skill's stored level, which leaves out status effect skill bonuses,
    // the game's rounding down and skills not learned yet. Its carry bonus is added here as 1.5.0 added it, from the
    // game's skill level of every skill, with the same settings.
    internal static class EpicLootSkillLevels
    {
        private static readonly AccessTools.FieldRef<Skills, Player> PlayerOf = AccessTools.FieldRefAccess<Skills, Player>("m_player");
        private static readonly MethodInfo CharacterLevel = AccessTools.Method(typeof(Character), nameof(Character.GetSkillLevel));
        // Each patched method and how many of its level reads it must have.
        private static readonly Dictionary<MethodBase, int> Reads = new Dictionary<MethodBase, int>();
        // SkilledCarryWeight reads every skill whenever the game checks the carry weight, several times per physics
        // step, and EpicLoot's lookup allocates on every call. Carry weight reuses each skill's bonus for up to a
        // second, or until EpicLoot recounts the player's effects after an equipment change.
        private static readonly Dictionary<Skills.SkillType, int> CarryBonuses = new Dictionary<Skills.SkillType, int>();
        private static readonly Dictionary<Skills.SkillType, (ConfigEntry<bool> Enabled, ConfigEntry<float> Coefficient, ConfigEntry<float> Power)> CarrySettings =
            new Dictionary<Skills.SkillType, (ConfigEntry<bool>, ConfigEntry<float>, ConfigEntry<float>)>();
        private static float carryBonusesUntil;

        internal static void Initialize(IntegrationPatches harmony)
        {
            Add(AccessTools.Method(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip),
                new[] { typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool) }), 1);
            Add(AccessTools.PropertyGetter(MagicTooltipContext.Renderer, "SkillLevel"), 1);
            if (PluginIds.TryGet(PluginIds.SkilledCarryWeight, out var plugin))
            {
                var assembly = plugin.Instance.GetType().Assembly;
                var carryWeight = AccessTools.Method(assembly.GetType("SkilledCarryWeight.Patches.PlayerPatch"), "GetMaxCarryWeight");
                var settings = AccessTools.Field(plugin.Instance.GetType(), "SkillConfigsMap")?.GetValue(null) as IDictionary;
                var setting = assembly.GetType("SkilledCarryWeight.SkilledCarryWeight+SkillConfig");
                var enabled = AccessTools.Field(setting, "enabledConfig");
                var coefficient = AccessTools.Field(setting, "coeffConfig");
                var power = AccessTools.Field(setting, "powConfig");
                if (carryWeight == null || settings == null || enabled == null || coefficient == null || power == null)
                    throw new InvalidOperationException("SkilledCarryWeight's carry weight bonus or skill settings moved.");
                foreach (DictionaryEntry entry in settings)
                    CarrySettings.Add((Skills.SkillType)entry.Key, ((ConfigEntry<bool>)enabled.GetValue(entry.Value),
                        (ConfigEntry<float>)coefficient.GetValue(entry.Value), (ConfigEntry<float>)power.GetValue(entry.Value)));
                harmony.Patch(carryWeight, prefix: new HarmonyMethod(typeof(EpicLootSkillLevels), nameof(AddCarryBonus)));
                harmony.Patch(AccessTools.Method(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.Reset)),
                    postfix: new HarmonyMethod(typeof(EpicLootSkillLevels), nameof(ForgetCarryBonuses)));
            }
            foreach (var method in Reads.Keys)
                harmony.Patch(method, transpiler: new HarmonyMethod(typeof(EpicLootSkillLevels), nameof(Transpiler)));
        }

        private static void Add(MethodBase method, int reads)
        {
            if (method == null) throw new InvalidOperationException("A skill level reader for EpicLoot's skill bonuses changed.");
            Reads.Add(method, reads);
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod) =>
            Transpilers.CallInstead(instructions, x => x.Calls(CharacterLevel), AccessTools.Method(typeof(EpicLootSkillLevels), nameof(InTooltip)),
                Reads[__originalMethod], "Skill level reads changed: " + __originalMethod.DeclaringType + "." + __originalMethod.Name);

        // Replaces SkilledCarryWeight's own postfix of the player's maximum carry weight, given by position because the
        // postfix is static.
        private static bool AddCarryBonus(Player __0, ref float __1)
        {
            var skills = __0.GetSkills();
            if (skills == null) return false;
            foreach (var definition in skills.m_skills)
            {
                if (!CarrySettings.TryGetValue(definition.m_skill, out var setting) || !setting.Enabled.Value) continue;
                __1 += setting.Coefficient.Value * Mathf.Pow(InCarryWeight(skills, definition.m_skill), setting.Power.Value);
                if (float.IsInfinity(__1))
                {
                    __1 = float.MaxValue;
                    break;
                }
            }
            return false;
        }

        internal static bool CarryContribution(Skills skills, Skills.SkillType skill, out float weight)
        {
            weight = 0f;
            if (!CarrySettings.TryGetValue(skill, out var setting) || !setting.Enabled.Value) return false;
            weight = setting.Coefficient.Value * Mathf.Pow(InCarryWeight(skills, skill), setting.Power.Value);
            return true;
        }

        // A tooltip shows the level as if its weapon were equipped, like the weapon's damage range. EpicLoot adds its
        // bonus inside the character's level from the effects of the hands in use.
        private static float InTooltip(Character character, Skills.SkillType skill)
        {
            var hands = WeaponTooltip.AsIfEquipped(WeaponTooltipRange.Item);
            try { return character.GetSkillLevel(skill); }
            finally { HandOverride.Active = hands; }
        }

        private static float InCarryWeight(Skills skills, Skills.SkillType skill)
        {
            var player = PlayerOf(skills);
            if (!player || player != Player.m_localPlayer) return skills.GetSkillLevel(skill);
            if (Time.time >= carryBonusesUntil)
            {
                CarryBonuses.Clear();
                carryBonusesUntil = Time.time + 1f;
            }
            if (!CarryBonuses.TryGetValue(skill, out int bonus)) CarryBonuses[skill] = bonus = WornBonus(player, skill);
            return skills.GetSkillLevel(skill) + bonus;
        }

        private static void ForgetCarryBonuses() => carryBonusesUntil = 0f;

        // Only a player's own game knows their equipment; everywhere else EpicLoot's bonus is 0. A set bonus counts only
        // if worn pieces alone reach its piece count.
        private static int WornBonus(Player player, Skills.SkillType skill)
        {
            var previous = HandOverride.Active;
            HandOverride.Active = new HandOverride.Hands { Player = player, Ammo = player.GetAmmoItem(), WornOnly = true };
            try { return AddSkillLevel_Skills_GetSkillFactor_Patch.SkillIncrease(player, skill); }
            finally { HandOverride.Active = previous; }
        }
    }
}
