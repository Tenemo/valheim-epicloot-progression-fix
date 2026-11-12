using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The hammer builds the raft, karve, longship and drakkar, the cart, the battering ram and the catapult at a workbench,
    // but Valheim 1.0.15 and 1.0.16 mark none of them as removable, so the remove button, the middle mouse button by
    // default, does nothing on them. Let it take any of them apart while Ctrl is held, so a plain click still cannot. The
    // game's other removal checks stay: a workbench in range, no ward or no-build zone, and nobody aboard a raft. The game
    // then refunds every material, and the ship and cart holds drop their cargo in cargo crates, as when they break.
    [HarmonyPatch(typeof(Player), "RemovePiece")]
    internal static class VehicleRemoval
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.LoadsField(AccessTools.Field(typeof(Piece), nameof(Piece.m_canBeRemoved))),
                AccessTools.Method(typeof(VehicleRemoval), nameof(CanBeRemoved)), 1, "The game's piece removal check changed.");

        private static bool CanBeRemoved(Piece piece)
        {
            if (piece.m_canBeRemoved) return true;
            bool vehicle = piece.GetComponent<Ship>() != null || piece.GetComponent<Vagon>() != null;
            return vehicle && (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl));
        }
    }

    // The battering ram burns wood loaded into its engine, a smelter on one of its child objects. A smelter drops its queued
    // wood when its building is destroyed, but looks for the building only on its own object, so a ram taken apart or broken
    // lost the wood still loaded in it.
    [HarmonyPatch(typeof(Smelter), "Awake")]
    internal static class SiegeEngineWood
    {
        private static readonly Action<Smelter> DropQueued = AccessTools.MethodDelegate<Action<Smelter>>(AccessTools.Method(typeof(Smelter), "OnDestroyed"));

        private static void Postfix(Smelter __instance, ZNetView ___m_nview)
        {
            if (___m_nview == null || ___m_nview.GetZDO() == null || __instance.GetComponent<WearNTear>() != null) return;
            var building = __instance.GetComponentInParent<WearNTear>();
            if (building != null) building.m_onDestroyed += () => DropQueued(__instance);
        }
    }

    // Pulling a battering ram makes the player heavier, and letting go makes them light again, but a ram taken apart or
    // broken while pulled never let go, so its puller stayed heavier until the next harness.
    [HarmonyPatch(typeof(Vagon), "OnDestroy")]
    internal static class HarnessRelease
    {
        private static void Prefix(Vagon __instance, GameObject ___m_attachedObject)
        {
            if (___m_attachedObject != null && __instance.m_playerExtraPullMass != 0f) ___m_attachedObject.GetComponent<Character>()?.SetExtraMass(0f);
        }
    }
}
