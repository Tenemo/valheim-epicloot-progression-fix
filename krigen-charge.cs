using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A Krigen's charge runs a fixed distance whatever the distance to its target, and slams at the end of it: from most
    // of the 5-10 m it charges from, it runs past its target, turns away and slams the ground about 2 m behind it, so only
    // 1 or 2 charges in 10 landed on a player standing still. Once its target is inside the slam's reach, the charge stops
    // carrying it forward, so it slams where the target is. The run-up, the slam's timing, reach and damage are the game's,
    // and a player can still sidestep or dodge it.
    internal static class KrigenCharge
    {
        private const string Krigen = "JotunWarrior", Charge = "attack_charge";
        private static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
        private static readonly AccessTools.FieldRef<Character, Vector3> RootMotion = AccessTools.FieldRefAccess<Character, Vector3>("m_rootMotion");
        private static readonly AccessTools.FieldRef<MonsterAI, Character> Target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var method = AccessTools.Method(typeof(Character), "ApplyRootMotion") ?? throw new InvalidOperationException("Character's root motion moved.");
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(KrigenCharge), nameof(StopAtTarget)));
        }

        private static void StopAtTarget(Character __instance)
        {
            if (!(__instance is Humanoid humanoid) || humanoid.IsPlayer() || !humanoid.InAttack()) return;
            var attack = CurrentAttack(humanoid);
            // Krigen carry a two-handed sword or axe; both charge the same way.
            if (attack == null || attack.m_attackAnimation != Charge || Utils.GetPrefabName(humanoid.gameObject) != Krigen) return;
            var target = humanoid.GetBaseAI() is MonsterAI ai ? Target(ai) : null;
            if (!target) return;
            // The slam is a sphere of the attack's width around a point the attack's range ahead of the creature.
            var slam = humanoid.transform.position + humanoid.transform.forward * attack.m_attackRange;
            var toTarget = target.transform.position - slam;
            toTarget.y = 0f;
            if (toTarget.magnitude > attack.m_attackRayWidth * .5f + target.GetRadius()) return;
            var motion = RootMotion(humanoid);
            RootMotion(humanoid) = new Vector3(0f, motion.y, 0f);
        }
    }
}
