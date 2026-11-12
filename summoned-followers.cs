using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SummonedFollowers
    {
        // New enemies are picked up near the caster; one a summon already fights is kept until it is twice as far.
        private const float CombatRange = 40f, HoldRange = 80f;
        private const string CommandKey = Plugin.Id + ".summon-target";
        private const string ContactKey = Plugin.Id + ".summon-contact";
        private const string ContactTimeKey = Plugin.Id + ".summon-contact-time";
        private const string SessionKey = Plugin.Id + ".summon-target-session";
        private static readonly AccessTools.FieldRef<MonsterAI, Character> Target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> Unseen = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceSensedTargetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> Inactive = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceAttacking");
        private static readonly Action<MonsterAI, Character> SetTarget = AccessTools.MethodDelegate<Action<MonsterAI, Character>>(AccessTools.Method(typeof(MonsterAI), "SetTarget"));
        private static readonly Action<MonsterAI, bool> Alert = AccessTools.MethodDelegate<Action<MonsterAI, bool>>(AccessTools.Method(typeof(MonsterAI), "SetAlerted"));
        internal static readonly Func<BaseAI, float, Vector3, float, bool, bool> MoveTo =
            AccessTools.MethodDelegate<Func<BaseAI, float, Vector3, float, bool, bool>>(AccessTools.Method(typeof(BaseAI), "MoveTo"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), nameof(MonsterAI.UpdateAI)),
                prefix: new HarmonyMethod(typeof(SummonedFollowers), nameof(LoadCommandTarget)));
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), "UpdateTarget"),
                prefix: new HarmonyMethod(typeof(SummonedFollowers), nameof(Prepare)),
                postfix: new HarmonyMethod(typeof(SummonedFollowers), nameof(Pursue)));
            harmony.Patch(AccessTools.Method(typeof(BaseAI), "FindEnemy"),
                prefix: new HarmonyMethod(typeof(SummonedFollowers), nameof(KeepTarget)));
        }

        // Every creature's AI runs this; it keeps the character its Awake found.
        private static bool LoadCommandTarget(MonsterAI __instance, Character ___m_character, float dt, ref bool __result)
        {
            var creature = ___m_character;
            if (!SummonOwnership.IsFollower(creature) || !creature.IsOwner() || creature.HaveRider()) return true;
            var data = GameFields.CharacterView(creature).GetZDO();
            var command = data.GetZDOID(CommandKey);
            if (command.IsNone() || data.GetLong(SessionKey) != Session || Resolve(command)) return true;
            var target = ZDOMan.instance.GetZDO(command);
            var summoner = SummonOwnership.Summoner(creature);
            // A new owner can load the follower before its distant target. Keep approaching the saved position
            // while that known enemy is in the caster's simulation area, using the same failed-pursuit deadline.
            if (target != null && summoner && !summoner.IsDead() && target.GetFloat(ZDOVars.s_health) > 0f &&
                !target.GetBool(ZDOVars.s_tamed) && CreatureOwnership.CanOwn(target, GameFields.CharacterView(summoner).GetZDO().GetOwner()))
            {
                Inactive(__instance) += dt;
                if (Inactive(__instance) <= 60f)
                {
                    MoveTo(__instance, dt, target.GetPosition(), 0f, true);
                    __result = true;
                    return false;
                }
            }
            data.Set(CommandKey, ZDOID.None);
            return true;
        }

        internal static void ResetAfterDungeon(Character creature, Player summoner)
        {
            var ai = creature.GetComponent<MonsterAI>(); var data = GameFields.CharacterView(creature).GetZDO();
            data.Set(CommandKey, ZDOID.None); data.Set(SessionKey, Session);
            SummonedBats.RecentContact(summoner, out _, out long sequence, out long time);
            data.Set(ContactKey, sequence); data.Set(ContactTimeKey, time);
            Target(ai) = null; SetTarget(ai, null); Alert(ai, false);
            Unseen(ai) = Inactive(ai) = 0f;
            ai.SetFollowTarget(summoner.gameObject);
        }

        private static bool Valid(Character creature, Player summoner, Character target, bool commanded) =>
            summoner && !summoner.IsDead() && target && target.gameObject.activeInHierarchy && !target.IsDead() && !target.IsPlayer() &&
            GameFields.CharacterView(target)?.IsValid() == true && !target.IsTamed() && !target.m_aiSkipTarget && BaseAI.IsEnemy(creature, target) &&
            (commanded ? SummonedBats.WithinSimulation(summoner, target) :
                (target.transform.position - summoner.transform.position).sqrMagnitude <= HoldRange * HoldRange && SummonedBats.WildlifeCanFight(target));

        // This runs inside the native candidate loop, before it compares distances or chooses the nearest enemy.
        internal static bool CanHunt(Character creature, Character target)
        {
            var summoner = SummonOwnership.Summoner(creature);
            return Valid(creature, summoner, target, false) && (target.transform.position - summoner.transform.position).sqrMagnitude <= CombatRange * CombatRange;
        }

        private static long Session => ZNet.instance.IsServer() ? ZDOMan.GetSessionID() : ZNet.instance.GetServerPeer()?.m_uid ?? 0L;

        private static Character Resolve(ZDOID id)
        {
            var instance = id.IsNone() ? null : ZNetScene.instance.FindInstance(id);
            return instance ? instance.GetComponent<Character>() : null;
        }

        // The target hooks run for every creature's AI and read the creature its Awake took from the same search.
        private static void Prepare(MonsterAI __instance, Character ___m_character)
        {
            var creature = GameFields.HeldCreature ? ___m_character : __instance.GetComponent<Character>();
            if (!SummonOwnership.IsFollower(creature) || !creature.IsOwner()) return;
            var data = GameFields.CharacterView(creature).GetZDO();
            if (data.GetLong(SessionKey) != Session)
            {
                // Creature identities are reassigned on world load; retain commands across owners, not sessions.
                data.Set(SessionKey, Session);
                data.Set(CommandKey, ZDOID.None);
                data.Set(ContactKey, 0L);
                data.Set(ContactTimeKey, 0L);
            }
            // The moose and bear otherwise break off a fight to run circles around their enemy; wild ones keep the prefab's taunt.
            if (SummonOwnership.StaffFor(data) == SummonOwnership.SpiritStaff) __instance.m_circleTargetInterval = 0f;
            var summoner = SummonOwnership.Summoner(creature);
            var commandedId = data.GetZDOID(CommandKey);
            var commanded = Resolve(commandedId);
            __instance.m_alertRange = CombatRange;
            if (!Valid(creature, summoner, commanded, true))
            {
                if (!commandedId.IsNone())
                {
                    data.Set(CommandKey, ZDOID.None);
                    if (Target(__instance) && Target(__instance).GetZDOID() == commandedId) Target(__instance) = null;
                }
            }
            else if (Target(__instance) != commanded)
            {
                // Restore the recorded command when this peer takes ownership of an already-existing summon.
                Target(__instance) = null;
                SetTarget(__instance, commanded);
                Alert(__instance, true);
            }

            if (summoner)
            {
                bool fresh = SummonedBats.RecentContact(summoner, out var contact, out long sequence, out long contactTime);
                if (sequence != data.GetLong(ContactKey) || contactTime != data.GetLong(ContactTimeKey))
                {
                    // Busy followers consume and ignore other targets; a later idle state cannot replay this hit.
                    data.Set(ContactKey, sequence);
                    data.Set(ContactTimeKey, contactTime);
                    var current = Target(__instance);
                    bool currentCommand = current && data.GetZDOID(CommandKey) == current.GetZDOID();
                    if (fresh && Valid(creature, summoner, contact, true) &&
                        (current == contact || !Valid(creature, summoner, current, currentCommand)))
                    {
                        data.Set(CommandKey, contact.GetZDOID());
                        if (current != contact)
                        {
                            Target(__instance) = null;
                            SetTarget(__instance, contact);
                            Inactive(__instance) = 0f;
                        }
                        Alert(__instance, true);
                    }
                }
            }

            var target = Target(__instance);
            bool designated = target && data.GetZDOID(CommandKey) == target.GetZDOID();
            if (!Valid(creature, summoner, target, designated))
            {
                Target(__instance) = null;
                data.Set(CommandKey, ZDOID.None);
                return;
            }
            if (designated)
            {
                Vector3 leashOrigin = summoner.transform.position;
                if (GameFields.PatrolPoint(__instance, out var patrol)) leashOrigin = patrol;
                else if (__instance.GetFollowTarget()) leashOrigin = __instance.GetFollowTarget().transform.position;
                __instance.m_alertRange = Mathf.Max(CombatRange, Vector3.Distance(leashOrigin, target.transform.position) + 1f);
                Unseen(__instance) = 0f;
            }
            // The native tamed check drops a target farther than this from the caster.
            else __instance.m_alertRange = HoldRange;
        }

        private static void Pursue(MonsterAI __instance, Character ___m_character, ref bool canHearTarget)
        {
            var creature = GameFields.HeldCreature ? ___m_character : __instance.GetComponent<Character>();
            if (!SummonOwnership.IsFollower(creature) || !creature.IsOwner()) return;
            // A held target the native update just dropped leaves the pick-up range for the next search.
            if (!Target(__instance)) __instance.m_alertRange = CombatRange;
            var data = GameFields.CharacterView(creature).GetZDO();
            var command = data.GetZDOID(CommandKey);
            if (command.IsNone()) return;
            var target = Target(__instance);
            if (!target || target.GetZDOID() != command)
            {
                // The native failed-pursuit timeout must end the order, not re-arm it on the next update.
                data.Set(CommandKey, ZDOID.None);
                __instance.m_alertRange = CombatRange;
                return;
            }
            if (!Valid(creature, SummonOwnership.Summoner(creature), target, true)) return;
            canHearTarget = true;
            Unseen(__instance) = 0f;
        }

        private static bool KeepTarget(BaseAI __instance, Character ___m_character, ref Character __result)
        {
            var ai = __instance as MonsterAI;
            var creature = ai ? (GameFields.HeldCreature ? ___m_character : ai.GetComponent<Character>()) : null;
            if (!SummonOwnership.IsFollower(creature) || !creature.IsOwner() || Unseen(ai) >= 30f) return true;
            var target = Target(ai);
            bool commanded = target && GameFields.CharacterView(creature).GetZDO().GetZDOID(CommandKey) == target.GetZDOID();
            if (!Valid(creature, SummonOwnership.Summoner(creature), target, commanded)) return true;
            __result = target;
            return false;
        }
    }
}
