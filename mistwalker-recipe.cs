using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Mistwalker is crafted and upgraded with Moder's dragon tears instead of wisps, in the native amounts; its other
    // ingredients, station levels and refinement idol stay as they are.
    [HarmonyPatch]
    internal static class MistwalkerRecipe
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ObjectDB), "Awake");
            yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        }

        private static void Postfix(ObjectDB __instance)
        {
            foreach (var recipe in __instance.m_recipes)
            {
                if (recipe.m_item == null || recipe.m_item.gameObject.name != "SwordMistwalker") continue;
                var tear = __instance.GetItemPrefab("DragonTear")?.GetComponent<ItemDrop>();
                if (tear == null) throw new InvalidOperationException("Mistwalker's recipe requires the native dragon tear.");
                foreach (var requirement in recipe.m_resources)
                    if (!requirement.m_upgraderResource && requirement.m_resItem != null && requirement.m_resItem.gameObject.name == "Wisp")
                        requirement.m_resItem = tear;
            }
        }
    }
}
