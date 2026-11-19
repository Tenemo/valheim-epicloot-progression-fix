using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Jump skill raises the height a player falls without damage, from the game's 4 m untrained to 8 m at level 100.
    // Damage then grows as in the unmodded game, reaching its 100 maximum 16 m above that height.
    [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyFallDamage))]
    internal static class JumpFallHeight
    {
        private const float UntrainedSafeHeight = 4f, SkillSafeHeight = 4f, DamageSpan = 16f, MaxDamage = 100f;
        private static readonly AccessTools.FieldRef<Character, float> HighestPoint = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");

        internal static float SafeHeight(Player player) =>
            UntrainedSafeHeight + SkillSafeHeight * player.GetSkills().GetSkillFactor(Skills.SkillType.Jump);

        // Character.UpdateGroundContact asks for the fall damage on landing, before it resets the highest point of the fall,
        // so the height it measured is still known here. Status effects and EpicLoot's Reduce fall damage then apply as before.
        private static void Prefix(Character ___m_character, ref float baseDamage, ref float damage)
        {
            if (!(___m_character is Player player) || player != Player.m_localPlayer) return;
            float height = Mathf.Max(0f, HighestPoint(player) - player.transform.position.y);
            baseDamage = damage = Mathf.Clamp01((height - SafeHeight(player)) / DamageSpan) * MaxDamage;
        }
    }
}
