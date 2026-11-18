using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game sizes Staff of Protection's bubble from the creature's collision body. StarLevelSystem gives a tamed lox or
    // asksvin a slimmer body so riders can reach the saddle, which left their bubble a third of its size, inside the lox.
    // Size it from the creature's own body instead. The bubble's scale syncs to other players.
    [HarmonyPatch(typeof(StatusEffect), "TriggerStartEffects")]
    internal static class ProtectionBubbleSize
    {
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<StatusEffect, GameObject[]> StartEffects =
            AccessTools.FieldRefAccess<StatusEffect, GameObject[]>("m_startEffectInstances");

        private static void Postfix(StatusEffect __instance)
        {
            var character = __instance.m_character;
            var instances = StartEffects(__instance);
            if (__instance.NameHash() != ShieldHash || !character || instances == null || !ZNetScene.instance) return;
            var body = character.GetComponent<CapsuleCollider>();
            var prefab = ZNetScene.instance.GetPrefab(Utils.GetPrefabName(character.gameObject));
            var original = prefab ? prefab.GetComponent<CapsuleCollider>() : null;
            if (!body || !original || body.radius <= 0f || Mathf.Approximately(body.radius, original.radius)) return;
            float factor = original.radius / body.radius;
            foreach (var instance in instances)
                if (instance) instance.transform.localScale *= factor;
        }
    }
}
