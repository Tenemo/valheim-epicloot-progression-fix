using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class TamedHarpoonMovement
    {
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDirection = AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, Vector3> CurrentVelocity = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        private static readonly AccessTools.FieldRef<Character, Vector3> RootMotion = AccessTools.FieldRefAccess<Character, Vector3>("m_rootMotion");
        // Where the harpoon last let go of a tame, or the home it had when TameRidingOverhaul's Sit took over, for
        // TameRiding to give back when Sit or a ride ends.
        internal static readonly int Home = (Plugin.Id + ".tame-home").GetStableHashCode();

        // Characters a harpoon caught since they were last seen free of it. Every status effect joins a character through
        // its Setup, so a character outside this set carries no harpoon, and the movement hooks every character runs every
        // frame answer for it at once.
        private static readonly HashSet<Character> Caught = new HashSet<Character>();

        internal static bool Yielding(Character character)
        {
            if (Caught.Count == 0 || !Caught.Contains(character)) return false;
            bool harpooned = false;
            foreach (var effect in character.GetSEMan().GetStatusEffects())
                if (effect is SE_Harpooned) { harpooned = true; break; }
            if (!harpooned)
            {
                Caught.Remove(character);
                return false;
            }
            return !character.IsPlayer() && character.IsTamed() && character.IsOwner() && !character.IsDead() &&
                !character.HaveRider() && !character.IsAttached() && !character.GetStandingOnShip();
        }

        [HarmonyPatch(typeof(SE_Harpooned), nameof(SE_Harpooned.Setup))]
        private static class Catch
        {
            private static void Prefix(Character character)
            {
                if (character) Caught.Add(character);
            }
        }

        [HarmonyPatch(typeof(Character), "OnDestroy")]
        private static class Gone
        {
            private static void Prefix(Character __instance) => Caught.Remove(__instance);
        }

        private static readonly AccessTools.FieldRef<Projectile, ItemDrop.ItemData> ProjectileWeapon = AccessTools.FieldRefAccess<Projectile, ItemDrop.ItemData>("m_weapon");

        // A player's projectile reaches a friendly creature only with PvP on, so with it off the harpoon never caught a tame.
        // The projectile records the weapon that threw it, so switching weapons in flight changes nothing. Summons keep
        // their own protection, and every other check of the target stays the game's.
        [HarmonyPatch(typeof(Projectile), "IsValidTarget")]
        private static class TameTarget
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var pvp = AccessTools.Method(typeof(Character), nameof(Character.IsPVPEnabled));
                int index = code.FindIndex(instruction => instruction.Calls(pvp));
                // Valheim 1.0.21: if (m_owner.IsPlayer() && !m_owner.IsPVPEnabled() && !flag) return false;
                if (index < 0 || code.FindLastIndex(instruction => instruction.Calls(pvp)) != index || code[index].labels.Count > 0)
                    throw new InvalidOperationException("Valheim's projectile target check changed; expected one PvP check.");
                code[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TameTarget), nameof(AllowsFriendlyHit)));
                code.InsertRange(index, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldarg_1) });
                return code;
            }

            private static bool AllowsFriendlyHit(Character owner, Projectile projectile, IDestructible target) =>
                owner.IsPVPEnabled() || ProjectileWeapon(projectile)?.m_dropPrefab?.name == "SpearChitin" &&
                target is Character tame && !tame.IsPlayer() && tame.IsTamed() && !SummonOwnership.IsPlayerSummon(tame);
        }

        [HarmonyPatch(typeof(Character), "UpdateMotion")]
        private static class Motion
        {
            private static void Prefix(Character __instance)
            {
                if (!Yielding(__instance)) return;
                MoveDirection(__instance) = Vector3.zero;
                CurrentVelocity(__instance) = Vector3.zero;
                RootMotion(__instance) = Vector3.zero;
            }
        }

        [HarmonyPatch(typeof(Character), "AddPushbackForce")]
        private static class MovementVelocity
        {
            private static void Prefix(Character __instance, ref Vector3 velocity)
            {
                if (Yielding(__instance)) velocity = __instance.GetComponent<Rigidbody>().linearVelocity;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.AddRootMotion))]
        private static class AttackMovement
        {
            private static bool Prefix(Character __instance) => !Yielding(__instance);
        }

        // An idle tame wanders around its saved home, if it has one, and otherwise around where it stands, and runs back from
        // farther than twice its wander range from that home, also after a fight. The home is the spot it was told to stay
        // at, where TameRidingOverhaul sat it, or the patrol point a creature spawner gave it before it was tamed; tamed Lox
        // usually have none. Its AI keeps choosing where to wander while the rope holds it, and the harpoon's hit alerts it,
        // so a tame dragged away sprinted back to its home or to the spot it had chosen before the drag as soon as the line
        // dropped, and one without a home wandered off from wherever a later fight left it. Every way the harpoon ends stops
        // its effect on the game that owns the creature: where the tame is released becomes its saved home, and, as after
        // a rider steers a mount, the chosen spot is dropped. Following tames keep following; a sitting tame stays sitting.
        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.Stop))]
        private static class Release
        {
            private static void Postfix(StatusEffect __instance, Character ___m_character)
            {
                var character = ___m_character;
                if (!(__instance is SE_Harpooned) || !character || character.IsPlayer() || !character.IsTamed() || !character.IsOwner() ||
                    character.IsDead() || character.HaveRider() || character.IsAttached()) return;
                if (!(character.GetBaseAI() is MonsterAI ai) || ai.GetFollowTarget()) return;
                ai.SetPatrolPoint();
                character.GetComponent<ZNetView>().GetZDO().Set(Home, character.transform.position);
                ai.ResetRandomMovement();
            }
        }
    }
}
