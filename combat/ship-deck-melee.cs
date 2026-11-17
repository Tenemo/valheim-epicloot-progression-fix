using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Most one-handed swings also sweep for short creatures from just above the attacker's feet, through the vehicle layer.
    // On a ship that sweep starts inside the deck, so Unity reports the deck at distance 0. It sorted before every real
    // target and ended each trace, so a log or creature on deck could not be hit and every swing damaged the ship instead.
    // Drop only those start overlaps with the ship the attacker stands on; its mast, sides and posts, met along a swing,
    // still stop the swing and take the hit.
    [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
    internal static class ShipDeckMelee
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var sort = AccessTools.Method(typeof(List<RaycastHit>), nameof(List<RaycastHit>.Sort), new[] { typeof(Comparison<RaycastHit>) });
            int index = code.FindIndex(instruction => instruction.Calls(sort));
            // Valheim 1.0.17: s_hitList.Sort((x, y) => x.distance.CompareTo(y.distance)); foreach (RaycastHit s_hit in s_hitList) ...
            if (index < 0 || code.FindLastIndex(instruction => instruction.Calls(sort)) != index)
                throw new InvalidOperationException("Valheim's melee hit sorting changed.");
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Attack), "s_hitList")),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ShipDeckMelee), nameof(SkipDeckUnderfoot))),
            });
            return code;
        }

        private static void SkipDeckUnderfoot(List<RaycastHit> hits, Attack attack)
        {
            var ship = GameFields.AttackCharacter(attack).GetStandingOnShip();
            if (!ship) return;
            for (int index = hits.Count - 1; index >= 0; index--)
            {
                var hit = hits[index];
                if (hit.distance == 0f && hit.point == Vector3.zero && Projectile.FindHitObject(hit.collider) == ship.gameObject)
                    hits.RemoveAt(index);
            }
        }
    }
}
