using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
    internal static class FloatingIceShipDamage
    {
        // Ordinary drifting ocean ice, floating shore shards and the ten ice shelf floes. Static shore ice, frozen ponds and
        // ice rocks keep native collisions.
        private static readonly HashSet<string> Floes = new HashSet<string>(Enumerable.Range(1, 10).Select(index => "IceShelf_" + index.ToString("D2")))
            { "ice1", "IceShoreShard" };

        private static bool FloatingIce(ImpactEffect impact, Collision collision)
        {
            if (!impact.GetComponent<Ship>() || collision.contactCount == 0) return false;
            var root = Projectile.FindHitObject(collision.GetContact(0).otherCollider);
            if (!root) return false;
            return Floes.Contains(Utils.GetPrefabName(root));
        }

        private static float Strength(float strength, bool floatingIce) => strength * (floatingIce ? Plugin.FloatingIceImpactMultiplier.Value : 1f);

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = instructions.ToList();
            var modify = AccessTools.Method(typeof(HitData.DamageTypes), nameof(HitData.DamageTypes.Modify), new[] { typeof(float) });
            var calls = code.Select((instruction, index) => (instruction, index)).Where(entry => entry.instruction.Calls(modify)).ToList();
            if (calls.Count != 2) throw new InvalidOperationException("ImpactEffect no longer scales obstacle damage followed by self damage");
            var floatingIce = generator.DeclareLocal(typeof(bool));
            code.InsertRange(calls[1].index, new[] { new CodeInstruction(OpCodes.Ldloc, floatingIce),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FloatingIceShipDamage), nameof(Strength))) });
            // Resolve the contact before native damage can destroy its ice object.
            var first = new CodeInstruction(OpCodes.Ldarg_0);
            first.labels.AddRange(code[0].labels);
            code[0].labels.Clear();
            code.InsertRange(0, new[] { first, new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FloatingIceShipDamage), nameof(FloatingIce))), new CodeInstruction(OpCodes.Stloc, floatingIce) });
            return code;
        }
    }
}
