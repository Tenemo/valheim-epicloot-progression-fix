using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class TamedDetectionRanges
    {
        private static readonly int FriendlySkeleton = "Skeleton_Friendly".GetStableHashCode();
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(BaseAI), nameof(BaseAI.CanSenseTarget), new[] { typeof(Character), typeof(bool) });
            yield return AccessTools.Method(typeof(BaseAI), nameof(BaseAI.CanSeeTarget), new[] { typeof(Character) });
            yield return AccessTools.Method(typeof(BaseAI), "CanSeeTarget", new[] { typeof(StaticTarget) });
            yield return AccessTools.Method(typeof(BaseAI), "FindClosestStaticPriorityTarget");
            yield return AccessTools.Method(typeof(BaseAI), nameof(BaseAI.InStealthRange));
            yield return AccessTools.Method(typeof(MonsterAI), "UpdateTarget");
            yield return AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI));
            yield return AccessTools.Method(typeof(MonsterAI), "SelectBestAttack");
        }

        private static bool TamedAnimal(BaseAI animal)
        {
            var character = animal.GetComponent<Character>();
            return character && character.IsTamed() && animal.GetComponent<Tameable>() && !SummonedBats.IsBat(character) &&
                GameFields.CharacterView(character)?.GetZDO()?.GetPrefab() != FriendlySkeleton;
        }

        private static float Sight(BaseAI animal) => TamedAnimal(animal) ? 50f : animal.m_viewRange;
        private static float Alert(MonsterAI animal) => SummonOwnership.IsFollower(animal.GetComponent<Character>())
            ? animal.m_alertRange : TamedAnimal(animal) ? 30f : animal.m_alertRange;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var view = AccessTools.Field(typeof(BaseAI), nameof(BaseAI.m_viewRange));
            var alert = AccessTools.Field(typeof(MonsterAI), nameof(MonsterAI.m_alertRange));
            int sightReads, alertReads = 0;
            switch (__originalMethod.Name)
            {
                case "CanSenseTarget": case "InStealthRange": case "SelectBestAttack": sightReads = 1; break;
                case "CanSeeTarget": sightReads = __originalMethod.GetParameters()[0].ParameterType == typeof(Character) ? 1 : 2; break;
                case "FindClosestStaticPriorityTarget": sightReads = 2; break;
                case "UpdateTarget": sightReads = 0; alertReads = 2; break;
                case "UpdateAI": sightReads = 2; alertReads = 2; break;
                default: throw new InvalidOperationException("Unknown animal range reader: " + __originalMethod.Name);
            }
            var code = Transpilers.CallInstead(instructions, instruction => instruction.LoadsField(view),
                AccessTools.Method(typeof(TamedDetectionRanges), nameof(Sight)), sightReads, "Animal sight range reads changed: " + __originalMethod.Name);
            return Transpilers.CallInstead(code, instruction => instruction.LoadsField(alert),
                AccessTools.Method(typeof(TamedDetectionRanges), nameof(Alert)), alertReads, "Animal alert range reads changed: " + __originalMethod.Name);
        }
    }
}
