using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Frost Cone hit everything in its cone and Kinetic Quake everything the game lets you provoke, which took in your tames
    // and Dvergr. Both now hit only your enemies; other players stay subject to the game's own PvP rules.
    internal static class SetEffectFriendlyFire
    {
        private static readonly AccessTools.FieldRef<FrostAOEDamageController, Player> FrostConeOwner =
            AccessTools.FieldRefAccess<FrostAOEDamageController, Player>("_player");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(SetEffectFriendlyFire);
            harmony.Patch(AccessTools.Method(typeof(FrostAOEDamageController), "OnTriggerEnter"), postfix: new HarmonyMethod(self, nameof(DropFriendFromFrostCone)));
            harmony.Patch(AccessTools.Method(typeof(KineticQuake), "HitCharacter"), prefix: new HarmonyMethod(self, nameof(SkipFriendInQuake)));
        }

        internal static bool IsSpared(Player player, Character target) =>
            target && player && !target.IsPlayer() && !BaseAI.IsEnemy(player, target);

        private static void DropFriendFromFrostCone(FrostAOEDamageController __instance, Collider other)
        {
            var target = other.GetComponent<Character>();
            if (IsSpared(FrostConeOwner(__instance), target)) __instance.Colliders.Remove(target);
        }

        private static bool SkipFriendInQuake(Player player, Collider collider, ref bool __result)
        {
            var hit = Projectile.FindHitObject(collider);
            if (!hit || !IsSpared(player, hit.GetComponent<Character>())) return true;
            __result = false;
            return false;
        }
    }
}
