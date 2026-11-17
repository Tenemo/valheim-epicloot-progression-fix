using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using EpicLoot;
using EpicLoot.MagicItemEffects;

namespace EpicLootProgressionFix
{
    // Food and flat equipment values form each pool first. Conversions read independently modified source pools,
    // never a pool that already contains another conversion, and then receive the destination's modifiers once.
    internal static class ResourcePools
    {
        private delegate void PoolModifier(Player player, ref float value);
        private delegate void CarryModifier(Player player, float subtotal, ref float value);
        private delegate void ComfortModifier(Player player, SEMan manager, ref float value);
        private static Func<float, float> bulkHealth;
        private static Func<Player, float> movementPenalty;
        private static PoolModifier bloodDrinker, travelLight;
        private static CarryModifier nightCarry, heavyCarry;
        private static ComfortModifier comfortCarry;
        private static AccessTools.FieldRef<int> penaltyFrame;

        internal static Type Effect(string name) => typeof(MagicItem).Assembly.GetTypes().Single(type => type.Name == name);
        private static MethodInfo Patch(string effect, string nested, string method = "Postfix") =>
            AccessTools.Method(AccessTools.Inner(Effect(effect), nested), method) ??
            throw new InvalidOperationException("EpicLoot's resource patch changed: " + effect + "." + nested + "." + method);

        internal static void Initialize(IntegrationPatches harmony)
        {
            var replace = new HarmonyMethod(typeof(ResourcePools), nameof(Replaced));
            foreach (var method in new[] {
                Patch("IncreasePlayerBaseStats", "Player_GetTotalFoodValue_Patch"),
                Patch("IncreasePlayerBaseStats", "Player_GetTotalFoodValue_Patch", "PostfixPercentOfPool"),
                Patch("IncreasePlayerBaseStats", "IncreaseHealth_Player_GetBaseFoodHP_Patch"),
                Patch("BulkupEffect", "Player_GetTotalFoodValue_Patch"), Patch("BulkupEffect", "Player_GetBaseFoodHP_Patch"),
                Patch("BloodDrinker", "Player_GetTotalFoodValue_Patch"), Patch("BloodDrinker", "Player_GetBaseFoodHP_Patch"),
                Patch("EveryXPointsOfEitrIncreasesStamina", "GetTotalFoodValue_Patch"), Patch("DartingThoughts", "ModifyMaxEitr") })
                harmony.Patch(method, prefix: replace);
            bulkHealth = AccessTools.MethodDelegate<Func<float, float>>(AccessTools.Method(typeof(BulkupEffect), "GetAdditionalHealthWithBulkUp"));
            movementPenalty = AccessTools.MethodDelegate<Func<Player, float>>(AccessTools.Method(Effect("PenaltyScaling"), "MovementPenaltyFactor"));
            penaltyFrame = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(Effect("PenaltyScaling"), "_penaltyFrame"));
            harmony.Patch(AccessTools.Method(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.Reset)),
                postfix: new HarmonyMethod(typeof(ResourcePools), nameof(EquipmentChanged)));
            bloodDrinker = AccessTools.MethodDelegate<PoolModifier>(AccessTools.Method(Effect("BloodDrinker"), "ApplyMaxHealthReduction"));
            travelLight = AccessTools.MethodDelegate<PoolModifier>(AccessTools.Method(Effect("TravelLight"), "ModifyMaxCarryWeight"));
            nightCarry = AccessTools.MethodDelegate<CarryModifier>(AccessTools.Method(Effect("NightCarryWeight"), "ModifyMaxCarryWeight"));
            heavyCarry = AccessTools.MethodDelegate<CarryModifier>(AccessTools.Method(Effect("CarryWeightForMovementPenalty"), "ModifyMaxCarryWeight"));
            comfortCarry = AccessTools.MethodDelegate<ComfortModifier>(AccessTools.Method(Effect("GainMaxCarryWeightFromRested"), "ModifyMaxCarryWeight"));
            harmony.Patch(AccessTools.Method(Effect("SharedSEManModifyMaxCarryWeightPatch"), "Postfix"),
                prefix: new HarmonyMethod(typeof(ResourcePools), nameof(CarryAdditions)));
        }

        private static bool Replaced() => false;
        private static void EquipmentChanged(Player player) { if (player == Player.m_localPlayer) penaltyFrame() = -1; }
        private static float Value(Player player, string effect, float scale = 1f) => player.GetTotalActiveMagicEffectValue(effect, scale);

        private static float Health(Player player, float native)
        {
            float total = native + Value(player, MagicEffectType.IncreaseHealth);
            if (player == Player.m_localPlayer) total += bulkHealth(total);
            // Keep Blood Drinker's minimum deduction and health floor, before the existing percentage-health stage.
            bloodDrinker(player, ref total);
            return total * (1f + Value(player, MagicEffectType.PercentHealth, .01f));
        }

        [HarmonyPatch(typeof(Player), "GetTotalFoodValue")]
        private static class FoodPools
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot)]
            private static void Postfix(Player __instance, ref float hp, ref float stamina, ref float eitr)
            {
                var player = __instance;
                hp = Health(player, hp);
                float staminaBase = stamina + Value(player, MagicEffectType.IncreaseStamina);
                float eitrBase = eitr + Value(player, MagicEffectType.IncreaseEitr);
                float staminaFactor = 1f + Value(player, MagicEffectType.PercentStamina, .01f);
                float heavyStamina = Value(player, MagicEffectType.StaminaIncreaseForMovementPenalty, .01f);
                if (player == Player.m_localPlayer && heavyStamina != 0f)
                    staminaFactor *= 1f + heavyStamina * movementPenalty(player);
                float eitrFactor = (1f + Value(player, MagicEffectType.PercentEitr, .01f)) *
                    UnityEngine.Mathf.Max(0f, 1f - Value(player, MagicEffectType.DartingThoughts, .01f) * .5f);
                eitrFactor *= 1f + UnityEngine.Mathf.Max(0f, Value(player, MagicEffectType.DeepWell, .01f));
                float sourceHealth = hp, healthTransfer = 0f;
                ConvertHealthToEitr.Apply(player, ref hp, ref healthTransfer);
                float sourceStamina = staminaBase * staminaFactor, sourceEitr = eitrBase * eitrFactor;
                float convertedStamina = sourceEitr * UnityEngine.Mathf.Max(0f, Value(player, MagicEffectType.EveryXPointsOfEitrIncreasesStamina, .01f));
                if (player == Player.m_localPlayer)
                    convertedStamina += sourceHealth * Value(player, MagicEffectType.GainMaxStaminaBasedOnPlayerMaxHealth, .01f);
                stamina = (staminaBase + convertedStamina) * staminaFactor;
                float convertedEitr = sourceHealth * Value(player, MagicEffectType.HeartyEitr, .01f);
                if (eitrBase > 0f || healthTransfer > 0f)
                    convertedEitr += sourceStamina * Value(player, MagicEffectType.EnergeticEitr, .01f);
                eitr = (eitrBase + convertedEitr + healthTransfer) * eitrFactor;
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetBaseFoodHP))]
        private static class BaseHealth
        {
            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot)]
            private static void Postfix(Player __instance, ref float __result) => __result = Health(__instance, __result);
        }

        // This replaces a static postfix, so its original manager and result are arguments, not Harmony's instance/result.
        private static bool CarryAdditions(SEMan __0, ref float __2)
        {
            var player = GameFields.StatusCharacter(__0) as Player;
            if (player == null) return false;
            AddCarryWeight.ModifyMaxCarryWeight(player, ref __2);
            if (player == Player.m_localPlayer) comfortCarry(player, __0, ref __2);
            return false;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.GetMaxCarryWeight))]
        private static class CarryCapacity
        {
            private static float BeforeWorldMultiplier() => 1f;
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
                Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Game), nameof(Game.m_carryWeightRate))),
                    AccessTools.Method(typeof(CarryCapacity), nameof(BeforeWorldMultiplier)), 1, "Valheim's world carry multiplier changed.");

            [HarmonyPriority(Priority.Last), HarmonyAfter(PluginIds.EpicLoot, PluginIds.SkilledCarryWeight)]
            private static void Postfix(Player __instance, ref float __result)
            {
                if (__instance == Player.m_localPlayer)
                {
                    float subtotal = __result;
                    nightCarry(__instance, subtotal, ref __result);
                    heavyCarry(__instance, subtotal, ref __result);
                    GiantsBlood.ModifyMaxCarryWeight(__instance, subtotal, ref __result);
                    travelLight(__instance, ref __result);
                }
                __result *= Game.m_carryWeightRate;
            }
        }
    }
}
