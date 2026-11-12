using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class TameMatingRange
    {
        private sealed class Applied { }
        private static readonly ConditionalWeakTable<Procreation, Applied> Instances = new ConditionalWeakTable<Procreation, Applied>();

        [HarmonyPatch(typeof(Procreation), "Awake"), HarmonyPostfix]
        private static void Postfix(Procreation __instance)
        {
            if (!__instance.GetComponent<Character>() || !__instance.GetComponent<Tameable>() || Instances.TryGetValue(__instance, out _)) return;
            __instance.m_partnerCheckRange *= Utils.GetPrefabName(__instance.gameObject) == "Moose" ? 3f : 2f;
            Instances.Add(__instance, new Applied());
        }

        private static float CurrentRange(Procreation breeding) => Scaled(breeding, breeding.m_partnerCheckRange);

        // The crowding limit counts the same body-scaled area, or large animals never fill it and breed without limit.
        private static float PopulationRadius(Procreation breeding) => Scaled(breeding, breeding.m_totalCheckRange);

        private static float Scaled(Procreation breeding, float baseline)
        {
            if (baseline <= 0f || !Instances.TryGetValue(breeding, out _) || !ZNetScene.instance) return baseline;
            var prefab = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(breeding.gameObject));
            if (!prefab || prefab.transform.lossyScale.x == 0f) return baseline;
            // StarLevelSystem scales the whole prefab uniformly; lossyScale also includes an applied parent scale.
            float size = Mathf.Abs(breeding.transform.lossyScale.x / prefab.transform.lossyScale.x);
            // The native population query treats a zero range as unlimited.
            return Mathf.Max(float.Epsilon, baseline * size);
        }

        [HarmonyPatch(typeof(Procreation), "Procreate"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Distances(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(
                Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(AccessTools.Field(typeof(Procreation), nameof(Procreation.m_partnerCheckRange))),
                    AccessTools.Method(typeof(TameMatingRange), nameof(CurrentRange)), 2, "The two native partner-distance checks changed."),
                instruction => instruction.LoadsField(AccessTools.Field(typeof(Procreation), nameof(Procreation.m_totalCheckRange))),
                AccessTools.Method(typeof(TameMatingRange), nameof(PopulationRadius)), 2, "The two native population-distance checks changed.");
    }
}
