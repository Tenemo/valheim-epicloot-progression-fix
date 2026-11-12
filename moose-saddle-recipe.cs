using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class MooseSaddleRecipe
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ObjectDB), "Awake");
            yield return AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB));
        }

        private static void Postfix(ObjectDB __instance)
        {
            foreach (var recipe in __instance.m_recipes)
                if (recipe.m_item && recipe.m_item.gameObject.name == "SaddleMoose")
                    recipe.m_resources = recipe.m_resources.Where(resource =>
                        !resource.m_resItem || resource.m_resItem.gameObject.name != "Gold").ToArray();
        }
    }
}
