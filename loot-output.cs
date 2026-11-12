using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // EpicLoot uses one private output type for physical drops and inventory-only rolls.
    internal static class LootOutput
    {
        private static readonly Type OutputType = AccessTools.Inner(typeof(LootRoller), "LootOutput");
        private static readonly FieldInfo ObjectItems = AccessTools.Field(OutputType, "Objects");
        private static readonly FieldInfo InventoryItems = AccessTools.Field(OutputType, "Items");
        private static readonly ConstructorInfo Constructor = AccessTools.Constructor(OutputType,
            new[] { typeof(List<GameObject>), typeof(List<ItemDrop.ItemData>) });
        private static readonly MethodInfo CreateItem = AccessTools.Method(OutputType, "Create");
        private static readonly MethodInfo SpawnItem = AccessTools.Method(typeof(LootRoller), "SpawnNormalItem");

        internal static List<GameObject> Objects(object output) => (List<GameObject>)ObjectItems.GetValue(output);
        internal static IEnumerable<ItemDrop.ItemData> Items(object output) => Objects(output) is List<GameObject> objects
            ? objects.Where(item => item).Select(item => item.GetComponent<ItemDrop>()?.m_itemData).Where(item => item != null)
            : (List<ItemDrop.ItemData>)InventoryItems.GetValue(output);
        internal static int Count(object output) => Objects(output)?.Count ?? ((List<ItemDrop.ItemData>)InventoryItems.GetValue(output)).Count;
        internal static object ForObjects(List<GameObject> objects) => Constructor.Invoke(new object[] { objects, null });
        internal static ItemDrop.ItemData Create(object output, GameObject prefab, Vector3 position, out ItemDrop drop)
        {
            object[] arguments = { prefab, position, null };
            var item = (ItemDrop.ItemData)CreateItem.Invoke(output, arguments);
            drop = (ItemDrop)arguments[2];
            return item;
        }
        internal static void Spawn(LootDrop loot, string name, Vector3 position, float luck, object output) =>
            SpawnItem.Invoke(null, new object[] { loot, name, position, luck, output });
    }
}
