using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Hel's Harvest raises a thrall for the attacker of a creature's last hit. A poison or burn tick has no attacker, so
    // those kills raised nothing; they now go to the player the kill is credited to.
    // Raising crumbled the oldest thrall before finding that nothing can rise in deep water, losing it for no new one.
    internal static class HelsHarvestRaises
    {
        private const string RaiseRpc = "el-helraise";
        private const float DeepestWater = 1f;
        private static readonly int HarvestFlag = "el-hel".GetStableHashCode();
        private static readonly int Thrall = "EL_HelThrall".GetStableHashCode();
        private static readonly AccessTools.FieldRef<Character, HitData> LastHit = AccessTools.FieldRefAccess<Character, HitData>("m_lastHit");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(HelsHarvestRaises);
            harmony.Patch(AccessTools.Method(typeof(Character), "OnDeath"), prefix: new HarmonyMethod(self, nameof(RaiseFromPeriodicKill)));
            harmony.Patch(AccessTools.Method(typeof(HelsHarvest), "Raise"), prefix: new HarmonyMethod(self, nameof(KeepThrallsOverDeepWater)));
        }

        private static void RaiseFromPeriodicKill(Character __instance)
        {
            var view = GameFields.CharacterView(__instance);
            if (__instance.IsPlayer() || __instance.IsTamed() || !view || !view.IsValid() || !view.IsOwner() || view.GetZDO().GetPrefab() == Thrall ||
                LastHit(__instance)?.GetAttacker() != null) return;
            var player = Player.GetPlayer(CorpseRotKillCredit.LethalPlayerOf(__instance));
            var playerView = player ? GameFields.CharacterView(player) : null;
            if (playerView && playerView.IsValid() && playerView.GetZDO().GetBool(HarvestFlag))
                playerView.InvokeRPC(RaiseRpc, __instance.transform.position, __instance.transform.rotation.eulerAngles.y, __instance.GetLevel());
        }

        // The same test Raise makes after crumbling.
        private static bool KeepThrallsOverDeepWater(Vector3 position)
        {
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetSolidHeight(position, out float height, 2)) position.y = height;
            return Floating.GetLiquidLevel(position) - position.y <= DeepestWater;
        }
    }
}
