using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(LuckyLoot), "IsProtected")]
    internal static class MouldLuckyLoot
    {
        private static readonly HashSet<string> Moulds = new HashSet<string>(StringComparer.Ordinal)
        {
            "MoldArmorGoldChest", "MoldArmorGoldHelmet", "MoldArmorGoldLegs", "MoldArmorMageChest", "MoldArmorMageHelmet", "MoldArmorMageLegs",
            "MoldArmormediumChest", "MoldArmorMediumHelmet", "MoldArmorMediumLegs", "MoldAtgeir", "MoldAxe", "MoldAxe2H", "MoldBow", "MoldCrossbow",
            "MoldFistweapon", "MoldKeys", "MoldKnife", "MoldMace", "MoldMace2H", "MoldShieldBuckler", "MoldShieldRound", "MoldShieldTower",
            "MoldSmallParts", "MoldSpear", "MoldStafffrostorbs", "MoldStaffOrbofAhri", "MoldStaffspiritcaller", "MoldStaffthunderblood", "MoldSword", "MoldSword2H"
        };
        private static readonly HashSet<string> ProtectedDrops = new HashSet<string>(StringComparer.Ordinal)
        {
            "BloodGoldKey", "CryptKey", "DvergrKey", "HildirKey_forestcrypt", "HildirKey_mountaincave", "HildirKey_plainsfortress",
            "MemorialCoal", "OrbFrostFire", "OrbThunderBlood"
        };

        internal static bool IsMould(GameObject prefab) => prefab && Moulds.Contains(prefab.name);

        private static void Postfix(GameObject prefab, ref bool __result) =>
            __result |= IsMould(prefab) || prefab && ProtectedDrops.Contains(prefab.name);
    }

    [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
    internal static class MouldQuantities
    {
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        private static void Finalizer(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result, Exception __exception)
        {
            if (__exception == null && __result != null && __instance.GetComponent<Character>()) Limit(__result);
        }

        internal static void Limit(List<KeyValuePair<GameObject, int>> drops)
        {
            HashSet<string> seen = null;
            int kept = 0;
            for (int index = 0; index < drops.Count; index++)
            {
                var drop = drops[index];
                if (MouldLuckyLoot.IsMould(drop.Key))
                {
                    if (seen == null) seen = new HashSet<string>(StringComparer.Ordinal);
                    if (drop.Value <= 0 || !seen.Add(drop.Key.name)) continue;
                    drop = new KeyValuePair<GameObject, int>(drop.Key, 1);
                }
                drops[kept++] = drop;
            }
            if (kept < drops.Count) drops.RemoveRange(kept, drops.Count - kept);
        }
    }

    // Boss resources never multiply: a multiplier of 1 leaves them alone and keeps the proc's bonus magic item rolls.
    [HarmonyPatch(typeof(LuckyLoot.LuckyLoot_CharacterDrop_GenerateDropList_Patch), "Postfix")]
    internal static class BossLuckyLoot
    {
        private static readonly MethodInfo NativeMultiplier = AccessTools.Method(typeof(LuckyLoot), "Multiplier");
        private static readonly Func<float, int> Multiplier = AccessTools.MethodDelegate<Func<float, int>>(NativeMultiplier);

        internal static bool IsBoss(CharacterDrop source)
        {
            var character = source ? source.GetComponent<Character>() : null;
            var prefab = character ? ZNetScene.instance.GetPrefab(global::EpicLoot.EpicLoot.GetCharacterCleanName(character))?.GetComponent<Character>() : null;
            return prefab && prefab.IsBoss();
        }

        private static int EffectiveMultiplier(float value, CharacterDrop source) => IsBoss(source) ? 1 : Multiplier(value);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(NativeMultiplier))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    instruction.operand = AccessTools.Method(typeof(BossLuckyLoot), nameof(EffectiveMultiplier));
                    replaced++;
                }
                yield return instruction;
            }
            if (replaced != 1) throw new InvalidOperationException("Lucky Loot's proc changed; expected one multiplier calculation.");
        }
    }

    // Lucky Loot multiplies a dying creature's whole drop list, including the blobs an oozer releases, so a Summoner
    // Bonemass's oozers turned into hundreds of blobs. While a character listed in `Oozer Lucky Loot exclusions` is within
    // the equipment bonus radius of a dying oozer, no one's Lucky Loot applies to it, whoever wears the stones. Every
    // other creature, including the released blobs, keeps the native lookup.
    internal static class OozerLuckyLoot
    {
        private static bool dyingOozer;

        // EpicLoot reads the chance in its GenerateDropList postfix; finalizers run after every postfix.
        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static class Death
        {
            private static void Prefix(CharacterDrop __instance) => dyingOozer = Utils.GetPrefabName(__instance.gameObject) == "BlobElite";
            private static void Finalizer() => dyingOozer = false;
        }

        // EpicLoot takes the best published chance among the players in the same radius, measured the same way.
        [HarmonyPatch(typeof(LuckyLoot), "BestNearbyValue")]
        private static class NearbyChance
        {
            private static bool Prefix(Vector3 position, ref float __result)
            {
                if (!dyingOozer || !ListedNearby(position)) return true;
                __result = 0f;
                return false;
            }
        }

        private static bool ListedNearby(Vector3 position)
        {
            var names = Plugin.OozerLuckyLootExclusions.Value.Split(',').Select(entry => entry.Trim()).Where(entry => entry.Length > 0).ToList();
            if (names.Count == 0) return false;
            var players = new List<Player>();
            Player.GetPlayersInRange(position, Plugin.LootBonusRadius.Value, players);
            return players.Any(player => names.Contains(player.GetPlayerName(), StringComparer.OrdinalIgnoreCase));
        }
    }
}
