using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Craftable shields get stronger base stats: block armor and block force, with their gain per quality, are multiplied,
    // and the parry multiplier rises by a set amount, 0.5 on round shields (1.5 to 2) and 1 on bucklers (2.5 to 3.5).
    // Tower shields still cannot parry. The base stats change, so tooltips show them and Blocking skill, enchantments,
    // runes and shardstones apply on top as before. Players also stagger only at a larger share of their maximum health.
    [HarmonyPatch]
    internal static class ShieldBalance
    {
        // EpicLoot tells bucklers from round shields the same way.
        private const float BucklerParry = 2.5f;

        private struct NativeStats
        {
            internal float Block, BlockPerQuality, Force, ForcePerQuality, Parry;
        }

        // Keyed by the prefab's shared data, which every copy of the item uses, so each shield is changed once from its
        // native values however often the game reloads its item database.
        private static readonly Dictionary<ItemDrop.ItemData.SharedData, NativeStats> Shields = new Dictionary<ItemDrop.ItemData.SharedData, NativeStats>();

        internal static void Initialize()
        {
            foreach (var setting in new[] { Plugin.ShieldBlockArmorMultiplier, Plugin.ShieldBlockForceMultiplier, Plugin.RoundShieldParryIncrease, Plugin.BucklerParryIncrease })
                setting.SettingChanged += (sender, arguments) => Apply();
            Plugin.StaggerHealthShare.SettingChanged += (sender, arguments) =>
            {
                foreach (var player in Player.GetAllPlayers()) player.m_staggerDamageFactor = Plugin.StaggerHealthShare.Value;
            };
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ObjectDB), "Awake");
            yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        }

        private static void Postfix(ObjectDB __instance)
        {
            // Only shields players can craft; creatures' shield props and unused prefabs keep their stats.
            foreach (var recipe in __instance.m_recipes)
            {
                var shared = recipe.m_item?.m_itemData?.m_shared;
                if (!recipe.m_enabled || shared == null || shared.m_itemType != ItemDrop.ItemData.ItemType.Shield || Shields.ContainsKey(shared)) continue;
                Shields[shared] = new NativeStats
                {
                    Block = shared.m_blockPower, BlockPerQuality = shared.m_blockPowerPerLevel,
                    Force = shared.m_deflectionForce, ForcePerQuality = shared.m_deflectionForcePerLevel, Parry = shared.m_timedBlockBonus,
                };
            }
            Apply();
        }

        private static void Apply()
        {
            foreach (var shield in Shields)
            {
                var shared = shield.Key;
                var native = shield.Value;
                shared.m_blockPower = native.Block * Plugin.ShieldBlockArmorMultiplier.Value;
                shared.m_blockPowerPerLevel = native.BlockPerQuality * Plugin.ShieldBlockArmorMultiplier.Value;
                shared.m_deflectionForce = native.Force * Plugin.ShieldBlockForceMultiplier.Value;
                shared.m_deflectionForcePerLevel = native.ForcePerQuality * Plugin.ShieldBlockForceMultiplier.Value;
                // A shield parries only above 1, so tower shields keep theirs.
                shared.m_timedBlockBonus = native.Parry <= 1f ? native.Parry
                    : native.Parry + (native.Parry >= BucklerParry ? Plugin.BucklerParryIncrease.Value : Plugin.RoundShieldParryIncrease.Value);
            }
        }

        // Harmony patches a class either through TargetMethods or through annotated methods, so this has its own class.
        [HarmonyPatch(typeof(Player), "Awake")]
        private static class StaggerShare
        {
            private static void Postfix(Player __instance) => __instance.m_staggerDamageFactor = Plugin.StaggerHealthShare.Value;
        }
    }
}
