using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot keeps Triple Shot's and Double Cast's pending volley in one field that every attack's trigger clears before
    // it checks who attacks. A summoned bat or another creature this game simulates attacking during a held Frost stream
    // cleared it, so the stream's later bolts kept both projectiles without their reduced damage or extra cost. Only the
    // local player's own attacks start or clear a volley now, and only the attack that rolled it fires it.
    [HarmonyPatch]
    internal static class MultiShotAttackState
    {
        private static Attack rolled;

        [HarmonyPatch(typeof(MultiShot), nameof(MultiShot.Attack_OnAttackTrigger_Prefix)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Trigger(Attack __0)
        {
            if (__0 == null || Player.m_localPlayer == null || GameFields.AttackCharacter(__0) != Player.m_localPlayer) return false;
            rolled = __0;
            return true;
        }

        [HarmonyPatch(typeof(MultiShot), nameof(MultiShot.Attack_FireProjectileBurst_Prefix)), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static bool Burst(Attack __0) => __0 != null && __0 == rolled;

        // Frost's bolts already spread widely, so Double Cast keeps the staff's own spread instead of widening it by a fifth.
        [HarmonyPatch(typeof(MultiShot), nameof(MultiShot.Attack_FireProjectileBurst_Prefix)), HarmonyPostfix]
        private static void FrostSpread(Attack __0)
        {
            var weapon = __0 == null ? null : GameFields.AttackWeapon(__0);
            if (weapon?.m_dropPrefab?.name == "StaffIceShards") __0.m_projectileAccuracy = weapon.m_shared.m_attack.m_projectileAccuracy;
        }
    }
}
