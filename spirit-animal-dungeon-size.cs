using System;
using System.Reflection;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot copies the bound animal's saved StarLevelSystem size onto each new Fylgja spirit, and StarLevelSystem applies a
    // saved size even inside dungeons, where it otherwise leaves creatures at their prefab's size. A spirit summoned inside a
    // dungeon goes without the copied size, so it comes at its species' normal size and its spawn spot is measured for that
    // size. The binding itself keeps the size, so the next spirit summoned outdoors has it again.
    internal static class SpiritAnimalDungeonSize
    {
        internal static readonly int SizeKey = "SLS_SIZE".GetStableHashCode();
        internal static readonly Type Record = AccessTools.Inner(typeof(SpiritAnimal), "SpiritRecord");
        private static bool measuringIndoors;

        // The new spirit's data before it is created: StarLevelSystem then finds no saved size and leaves it at normal size.
        [HarmonyPatch]
        private static class NewSpirit
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Record, "WriteTo", new[] { typeof(ZDO) });
            private static void Postfix(ZDO zdo)
            {
                if (Character.InInterior(zdo.GetPosition())) zdo.RemoveVec3(SizeKey);
            }
        }

        [HarmonyPatch(typeof(SpiritAnimal), "TryFindSpawnPoint")]
        private static class SpawnSpace
        {
            private static void Prefix(Player player) => measuringIndoors = Character.InInterior(player.transform.position);
            private static void Finalizer() => measuringIndoors = false;
        }

        [HarmonyPatch]
        private static class SavedSize
        {
            private static MethodBase TargetMethod() => AccessTools.Method(Record, "TryGetVec3");
            private static void Postfix(int hash, ref Vector3 value, ref bool __result)
            {
                if (!measuringIndoors || hash != SizeKey) return;
                value = Vector3.zero;
                __result = false;
            }
        }
    }
}
