using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem's SoulEater raises a creature's saved damage multiplier by a share of its level for every creature
    // that dies within 5 m, without limit, so its damage keeps growing for as long as it lives. Stop the growth at the
    // configured cap above the creature's base damage multiplier, 0.5 or +50% damage for an ordinary creature, and hold
    // creatures that already saved more to that cap when their damage is worked out. The heal and the growth in size are
    // unchanged.
    internal static class SoulEaterCap
    {
        private static string damageMultiplierKey, soulsKey;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var data = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects");
            damageMultiplierKey = AccessTools.Field(data, "SLS_DAMAGE_MODIFIER")?.GetValue(null) as string;
            soulsKey = AccessTools.Field(data, "SLS_SOULEATER")?.GetValue(null) as string;
            var raise = AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Damage.DamageModifications"), "ForceUpdateDamageMod");
            if (damageMultiplierKey == null || soulsKey == null || raise == null)
                throw new InvalidOperationException("StarLevelSystem SoulEater changed.");
            harmony.Patch(raise, prefix: new HarmonyMethod(typeof(SoulEaterCap), nameof(StopAtCap)));
            harmony.Patch(StarLevelSystem.DetermineDamageFactor, postfix: new HarmonyMethod(typeof(SoulEaterCap), nameof(HoldToCap)));
        }

        // SoulEater is the only caller of ForceUpdateDamageMod.
        private static void StopAtCap(Character __0, ref float __1)
        {
            var zdo = ZdoOf(__0);
            if (zdo != null) __1 = Mathf.Max(0f, Mathf.Min(__1, CapOf(__0) - zdo.GetFloat(damageMultiplierKey, 1f)));
        }

        private static void HoldToCap(Character __0, ref float __result)
        {
            var zdo = ZdoOf(__0);
            if (zdo == null || zdo.GetInt(soulsKey) <= 0) return;
            float saved = zdo.GetFloat(damageMultiplierKey, 1f), cap = CapOf(__0);
            if (saved > cap) __result *= cap / saved;
        }

        // SLS saves a creature's configured base damage multiplier, 1 unless its level settings set one, in the same
        // value that SoulEater raises.
        private static float CapOf(Character character)
        {
            var values = StarLevelSystem.CacheEntryOf(character) is object entry ? StarLevelSystem.BaseValuesOf(entry) : null;
            return (values != null && values.Contains(StarLevelSystem.BaseDamage) ? (float)values[StarLevelSystem.BaseDamage] : 1f) + Plugin.SoulEaterDamageCap.Value;
        }

        private static ZDO ZdoOf(Character character)
        {
            var view = character == null ? null : GameFields.CharacterView(character);
            return view == null ? null : view.GetZDO();
        }
    }
}
