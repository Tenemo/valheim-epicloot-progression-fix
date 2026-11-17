using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A creature's stars, StarLevelSystem modifiers and the world's difficulty multiply its damage, while a shield's block
    // armor stays the same, so against strong creatures every block breaks the guard and then stops nothing. A player's
    // block or parry against a creature grows by that damage multiplier raised to the synced power: the level factor its
    // attacks carry (StarLevelSystem's, which includes its damage modifiers, boss rule and the SoulEater cap; the game's
    // 1 + 0.5 per level without it), the world's enemy damage rate and the difficulty scale for nearby players. It is
    // never smaller than the shield's own. The growth enters only the block armor the hit is checked against, after the
    // parry bonus; stamina and durability keep the shield's own block armor, so a grown block against a big hit costs the
    // game's full block stamina. A block that breaks the guard while the player has stamina still stops its block armor's
    // worth of damage; the stagger, the hit's status effect and the missing parry rewards stay as the game has them.
    internal static class CreatureStrengthBlocks
    {
        private static readonly Func<Character, float, Vector3, HitData, bool> AddStaggerDamage =
            AccessTools.MethodDelegate<Func<Character, float, Vector3, HitData, bool>>(AccessTools.Method(typeof(Character), "AddStaggerDamage"));
        private static Func<Character, float> starLevelFactor;
        // The current block: whether a player is blocking, the damage its block armor stops, and whether the guard broke
        // while the player still had stamina.
        private static bool recorded, brokeWithStamina;
        private static float blocked;

        internal static void Initialize(IntegrationPatches harmony)
        {
            // Calls through the SoulEater cap's postfix on the same method.
            if (StarLevelSystem.Installed) starLevelFactor = AccessTools.MethodDelegate<Func<Character, float>>(StarLevelSystem.DetermineDamageFactor);
            var self = typeof(CreatureStrengthBlocks);
            harmony.Patch(AccessTools.Method(typeof(Humanoid), "BlockAttack"),
                transpiler: new HarmonyMethod(self, nameof(GrowBlocks)),
                postfix: new HarmonyMethod(self, nameof(BlockBrokenGuard)) { priority = Priority.Last },
                finalizer: new HarmonyMethod(self, nameof(Clear)));
        }

        // How much harder than its base damage the attacker hits this player.
        internal static float Multiplier(Character attacker, Player player)
        {
            if (!attacker || attacker.IsPlayer()) return 1f;
            float level = starLevelFactor != null ? starLevelFactor(attacker) : 1f + Mathf.Max(0, attacker.GetLevel() - 1) * 0.5f;
            return level * Game.m_enemyDamageRate * Game.instance.GetDifficultyDamageScalePlayer(player.transform.position);
        }

        internal static float Growth(Character attacker, Player player) => Mathf.Pow(Mathf.Max(1f, Multiplier(attacker, player)), Plugin.CreatureBlockGrowth.Value);

        private static float Grow(float power, HitData hit, Character attacker, Humanoid target)
        {
            if (!(target is Player player)) return power;
            power *= Growth(attacker, player);
            // As the game measures the block next: the blockable damage the block armor takes off.
            var left = hit.m_damage.Clone();
            left.ApplyArmor(power);
            blocked = hit.GetTotalBlockableDamage() - left.GetTotalBlockableDamage();
            recorded = true;
            return power;
        }

        private static bool BreakGuard(Character character, float damage, Vector3 direction, HitData hit)
        {
            bool broke = AddStaggerDamage(character, damage, direction, hit);
            // The game checks the stamina right after this call to decide whether the block holds.
            if (recorded) brokeWithStamina = broke && character.HaveStamina();
            return broke;
        }

        private static IEnumerable<CodeInstruction> GrowBlocks(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var armor = AccessTools.Method(typeof(HitData.DamageTypes), nameof(HitData.DamageTypes.ApplyArmor), new[] { typeof(float) });
            var stagger = AccessTools.Method(typeof(Character), "AddStaggerDamage");
            var armorAt = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(armor)).ToList();
            var staggerAt = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(stagger)).ToList();
            if (armorAt.Count != 1 || staggerAt.Count != 1 || staggerAt[0] < armorAt[0])
                throw new InvalidOperationException("Humanoid.BlockAttack changed; expected one block armor step before one stagger step.");
            code[staggerAt[0]].opcode = OpCodes.Call;
            code[staggerAt[0]].operand = AccessTools.Method(typeof(CreatureStrengthBlocks), nameof(BreakGuard));
            // The block armor on the stack becomes the grown one for this step only; the game's local keeps the shield's own.
            code.InsertRange(armorAt[0], new[]
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldarg_2),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureStrengthBlocks), nameof(Grow))),
            });
            return code;
        }

        // Last, so the other block hooks see the broken guard as the game left it.
        private static void BlockBrokenGuard(HitData hit)
        {
            if (!recorded || !brokeWithStamina || blocked <= 0f) return;
            hit.BlockDamage(blocked);
            DamageText.instance.ShowText(DamageText.TextType.Blocked, hit.m_point + Vector3.up * 0.5f, blocked);
        }

        // Also after a thrown hit, so nothing carries over to the next block.
        private static void Clear()
        {
            recorded = brokeWithStamina = false;
            blocked = 0f;
        }
    }
}
