using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A new segment's record is created at its parent, before the game moves it into place, and nothing saved the move. It
    // came back on its parent after a reload, where the game deletes a bare exact duplicate and grows a new one in the gap.
    [HarmonyPatch(typeof(Vine), "Grow")]
    internal static class VineSegmentPosition
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Transform), nameof(Transform.Translate), new[] { typeof(Vector3), typeof(Space) })),
                AccessTools.Method(typeof(VineSegmentPosition), nameof(Translate)), 1,
                "Valheim's vine growth changed; expected one segment move.");

        private static void Translate(Transform segment, Vector3 translation, Space relativeTo)
        {
            segment.Translate(translation, relativeTo);
            var view = segment.GetComponent<ZNetView>();
            if (view != null && view.IsValid() && view.IsOwner()) view.GetZDO().SetPosition(segment.position);
        }
    }

    // Ashvine allows one ripe cluster in each segment's 4.9 m by 9.1 m berry area, so a harvested wall held barely any
    // berries; it allows two. Set before Awake, whose own berry check already uses the limit.
    [HarmonyPatch(typeof(Vine), "Awake")]
    internal static class AshvineClusters
    {
        private static void Prefix(Vine __instance)
        {
            if (Utils.GetPrefabName(__instance.gameObject) == "VineAsh") __instance.m_maxBerriesWithinBlocker = 2;
        }
    }

    // A failed fruit check records a new harvest. It fails on every load until the branches are rebuilt, at the end of a vine
    // and beside ripe berries, and in the first two cases that restarted the regrowth timer for nothing, so a picked segment
    // keeps its time. An overdue segment with two branches can only have failed because of ripe berries: it starts over as in
    // the unmodded game, instead of queueing to ripen the moment they are picked.
    [HarmonyPatch(typeof(Vine), "CheckBerryBlocker")]
    internal static class VineRegrowth
    {
        private static readonly AccessTools.FieldRef<Vine, int> Branches = AccessTools.FieldRefAccess<Vine, int>("m_branches");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(Pickable), nameof(Pickable.SetPicked))),
                AccessTools.Method(typeof(VineRegrowth), nameof(SetPicked)), 1,
                "Valheim's vine fruit check changed; expected one picked-state update.");

        private static void SetPicked(Pickable pickable, bool picked)
        {
            if (!picked || !pickable.GetPicked() || (PickableRegrowth.Overdue(pickable, pickable.GetComponent<ZNetView>()) && Branches(pickable.GetComponent<Vine>()) >= 2))
                pickable.SetPicked(picked);
        }
    }

    // A segment fruits only while it touches two others with fewer ripe berries near it than its limit, which only its owner's game can
    // tell, and Ivy's vine allows no berries at all. PickableRegrowth has already tried an overdue segment, and ripe berries
    // would have restarted its timer, so one still bare lacks the branches, not time. The game shows nothing when aiming at a
    // bare vine; PlantEverything's timer would name berries that aren't coming and call them Ready.
    [HarmonyPatch(typeof(Pickable), nameof(Pickable.GetHoverText))]
    internal static class BerrylessVineHover
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Pickable __instance, ZNetView ___m_nview, ref string __result)
        {
            if (!__instance.TryGetComponent<Vine>(out var vine)) return;
            if (vine.m_maxBerriesWithinBlocker <= 0 || __instance.GetPicked() && ___m_nview.IsValid() && PickableRegrowth.Overdue(__instance, ___m_nview))
                __result = "";
        }
    }
}
