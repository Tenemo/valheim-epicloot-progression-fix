using System.Collections.Generic;
using System.Reflection;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Movement speed enchantments make a player faster without making sprinting, jumping, dodging or grappling cheaper.
    // Gear speed penalties, and Unhindered removing them, still change those costs as the game intends.
    [HarmonyPatch]
    internal static class MovementStamina
    {
        internal static float Modifier(Character character)
        {
            float modifier = character.GetEquipmentMovementModifier();
            return character is Player player ? modifier - EpicLootMovementCache.SpeedBonus(player) : modifier;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(Player), "CheckRun");
            yield return AccessTools.Method(typeof(Player), "OnJump");
            yield return AccessTools.Method(typeof(Player), "GetDodgeStaminaUse");
            // The grappling pull's cost; the ground jump's own checks go through JumpStaminaEligibility.
            yield return AccessTools.Method(typeof(Character), nameof(Character.Jump));
            yield return AccessTools.Method(typeof(DoubleJump), "HaveJumpStamina");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            Transpilers.CallInstead(instructions, x => x.Calls(AccessTools.Method(typeof(Character), nameof(Character.GetEquipmentMovementModifier))),
                AccessTools.Method(typeof(MovementStamina), nameof(Modifier)), 1,
                original.DeclaringType.Name + "." + original.Name + " changed; expected one movement modifier in its stamina cost.");
    }
}
