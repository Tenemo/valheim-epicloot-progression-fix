using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SummonedFollowers
    {
        // New enemies are picked up near the player a companion fights for, or near the spot a staying tame keeps; one it
        // already fights is kept until it is twice as far.
        private const float CombatRange = 40f, HoldRange = 80f;
        // Hashed once: the object data's string keys hash on every read.
        private static readonly KeyValuePair<int, int> CommandKey = ZDO.GetHashZDOID(Plugin.Id + ".summon-target");
        private static readonly int ContactKey = (Plugin.Id + ".summon-contact").GetStableHashCode();
        private static readonly int ContactTimeKey = (Plugin.Id + ".summon-contact-time").GetStableHashCode();
        private static readonly int SessionKey = (Plugin.Id + ".summon-target-session").GetStableHashCode();
        private static readonly AccessTools.FieldRef<MonsterAI, float> Unseen = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceSensedTargetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> TargetSearchTimer = AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");
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
            if (!Companions.IsCompanion(creature) || !creature.IsOwner() || creature.HaveRider()) return true;
            var data = GameFields.CharacterView(creature).GetZDO();
            var command = data.GetZDOID(CommandKey);
            if (command.IsNone() || data.GetLong(SessionKey) != Session || Resolve(command)) return true;
            var target = ZDOMan.instance.GetZDO(command);
            var summoner = Companions.Leader(creature);
            // A new owner can load the follower before its distant target. Keep approaching the saved position
            // while that known enemy is in the caster's simulation area, using the same failed-pursuit deadline.
            if (target != null && summoner && !summoner.IsDead() && target.GetFloat(ZDOVars.s_health) > 0f &&
                !target.GetBool(ZDOVars.s_tamed) && CreatureOwnership.CanOwn(target, GameFields.CharacterView(summoner).GetZDO().GetOwner()))
            {
                GameFields.TimeSinceAttacking(__instance) += dt;
                if (GameFields.TimeSinceAttacking(__instance) <= 60f)
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
            GameFields.TargetCreature(ai) = null; SetTarget(ai, null); Alert(ai, false);
            Unseen(ai) = GameFields.TimeSinceAttacking(ai) = 0f;
            ai.SetFollowTarget(summoner.gameObject);
        }

        // A follower fights only while its summoner lives. Tames go on hunting deer and other wildlife, which summons leave alone.
        private static bool Valid(Character creature, Player leader, Character target, bool commanded)
        {
            if (!target || !target.gameObject.activeInHierarchy || target.IsDead() || target.IsPlayer() ||
                GameFields.CharacterView(target)?.IsValid() != true || target.IsTamed() || target.m_aiSkipTarget) return false;
            bool follower = SummonOwnership.CompanionOf(creature) == SummonOwnership.Companion.Follower;
            if (follower && (!leader || leader.IsDead()) || !BaseAI.IsEnemy(creature, target)) return false;
            if (commanded) return leader && !leader.IsDead() && SummonedBats.WithinSimulation(leader, target) && WithinSpiritLeash(creature, leader, target);
            return (target.transform.position - Anchor(creature, leader, target)).sqrMagnitude <= HoldRange * HoldRange &&
                (!follower || SummonedBats.WildlifeCanFight(target)) && WithinSpiritLeash(creature, leader, target);
        }

        // Where a companion's ranges count from: its leader, else the spot a tame stays at, else where it stood when it took
        // on this enemy.
        private sealed class Engagement { internal Character Target; internal Vector3 Point; }
        private static readonly ConditionalWeakTable<Character, Engagement> Engagements = new ConditionalWeakTable<Character, Engagement>();

        private static Vector3 Anchor(Character creature, Player leader, Character target)
        {
            if (leader) return leader.transform.position;
            if (GameFields.PatrolPoint(creature.GetBaseAI(), out var patrol)) return patrol;
            return target && Engagements.TryGetValue(creature, out var engagement) && engagement.Target == target ? engagement.Point : creature.transform.position;
        }

        private static void Engage(Character creature, Character target)
        {
            if (!target) return;
            var engagement = Engagements.GetValue(creature, _ => new Engagement());
            if (engagement.Target != target) { engagement.Target = target; engagement.Point = creature.transform.position; }
        }

        // EpicLoot fades a Fylgja spirit once it is farther than its leash from its player, 60 m unless configured, so the spirit
        // takes on only enemies at least 10 m inside the leash, even ones its player hit; 80 m with the pack's 90 m leash.
        private const float SpiritLeashMargin = 10f;
        private static readonly Func<float> SpiritLeash =
            AccessTools.MethodDelegate<Func<float>>(AccessTools.PropertyGetter(typeof(EpicLoot.MagicItemEffects.SpiritAnimal), "LeashDistance"));

        private static bool WithinSpiritLeash(Character creature, Player summoner, Character target)
        {
            if (!SummonOwnership.IsFylgjaSpirit(creature) || !summoner) return true;
            float reach = SpiritLeash() - SpiritLeashMargin;
            return (target.transform.position - summoner.transform.position).sqrMagnitude <= reach * reach;
        }

        // This runs inside the native candidate loop, before it compares distances or chooses the nearest enemy.
        internal static bool CanHunt(Character creature, Character target)
        {
            var leader = Companions.Leader(creature);
            return Valid(creature, leader, target, false) && (target.transform.position - Anchor(creature, leader, null)).sqrMagnitude <= CombatRange * CombatRange &&
                !IsSetAside(creature, target);
        }

        private const float SetAsideSeconds = 15f;
        private static readonly ConditionalWeakTable<Character, Dictionary<Character, float>> SetAsideTargets = new ConditionalWeakTable<Character, Dictionary<Character, float>>();

        private static void SetAside(Character creature, Character target)
        {
            var targets = SetAsideTargets.GetValue(creature, _ => new Dictionary<Character, float>());
            foreach (var expired in targets.Where(item => !item.Key || item.Value <= Time.time).Select(item => item.Key).ToArray()) targets.Remove(expired);
            targets[target] = Time.time + SetAsideSeconds;
        }

        private static bool IsSetAside(Character creature, Character target) =>
            SetAsideTargets.TryGetValue(creature, out var targets) && targets.TryGetValue(target, out float until) && Time.time < until;

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
            var kind = SummonOwnership.CompanionOf(creature);
            if (kind == SummonOwnership.Companion.None || !creature.IsOwner()) return;
            var data = GameFields.CharacterView(creature).GetZDO();
            if (data.GetLong(SessionKey) != Session)
            {
                // Creature identities are reassigned on world load; retain commands across owners, not sessions.
                data.Set(SessionKey, Session);
                data.Set(CommandKey, ZDOID.None);
                data.Set(ContactKey, 0L);
                data.Set(ContactTimeKey, 0L);
            }
            // The moose, bear and wolf otherwise break off a fight to run circles around their enemy; wild ones keep the prefab's taunt.
            if (kind == SummonOwnership.Companion.Tame || SummonOwnership.StaffFor(data) == SummonOwnership.SpiritStaff || SummonOwnership.IsFylgjaSpirit(data))
                __instance.m_circleTargetInterval = 0f;
            var summoner = Companions.Leader(creature, kind);
            var commandedId = data.GetZDOID(CommandKey);
            var commanded = Resolve(commandedId);
            __instance.m_alertRange = CombatRange;
            if (!Valid(creature, summoner, commanded, true))
            {
                if (!commandedId.IsNone())
                {
                    data.Set(CommandKey, ZDOID.None);
                    if (GameFields.TargetCreature(__instance) && GameFields.TargetCreature(__instance).GetZDOID() == commandedId) GameFields.TargetCreature(__instance) = null;
                }
            }
            else if (GameFields.TargetCreature(__instance) != commanded)
            {
                // Restore the recorded command when this peer takes ownership of an already-existing summon.
                GameFields.TargetCreature(__instance) = null;
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
                    var current = GameFields.TargetCreature(__instance);
                    bool currentCommand = current && data.GetZDOID(CommandKey) == current.GetZDOID();
                    if (fresh && Valid(creature, summoner, contact, true) && !IsSetAside(creature, contact) &&
                        (current == contact || !Valid(creature, summoner, current, currentCommand)))
                    {
                        data.Set(CommandKey, contact.GetZDOID());
                        if (current != contact)
                        {
                            GameFields.TargetCreature(__instance) = null;
                            SetTarget(__instance, contact);
                            GameFields.TimeSinceAttacking(__instance) = 0f;
                        }
                        Alert(__instance, true);
                    }
                }
            }

            var target = GameFields.TargetCreature(__instance);
            // Most companions at a base hold nothing; leave their data alone then.
            if (!target && data.GetZDOID(CommandKey).IsNone()) return;
            Engage(creature, target);
            bool designated = target && data.GetZDOID(CommandKey) == target.GetZDOID();
            if (!Valid(creature, summoner, target, designated))
            {
                GameFields.TargetCreature(__instance) = null;
                data.Set(CommandKey, ZDOID.None);
                return;
            }
            // A Spirit Wolf held a skeleton standing high in a swamp tree for nearly a minute, standing beside the Bonemass its
            // player was fighting. An enemy it can neither attack from where it stands nor walk to a place to attack from is
            // set aside for a while, and the next search picks another.
            if (CreatureReachability.Unreachable(__instance, target))
            {
                SetAside(creature, target);
                GameFields.TargetCreature(__instance) = null;
                data.Set(CommandKey, ZDOID.None);
                TargetSearchTimer(__instance) = 0f;
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
            // The native tamed check drops a target farther than this from the player followed or the spot stayed at.
            else __instance.m_alertRange = HoldRange;
        }

        private static void Pursue(MonsterAI __instance, Character ___m_character, ref bool canHearTarget)
        {
            var creature = GameFields.HeldCreature ? ___m_character : __instance.GetComponent<Character>();
            if (!Companions.IsCompanion(creature) || !creature.IsOwner()) return;
            // A held target the native update just dropped leaves the pick-up range for the next search.
            if (!GameFields.TargetCreature(__instance)) __instance.m_alertRange = CombatRange;
            var data = GameFields.CharacterView(creature).GetZDO();
            var command = data.GetZDOID(CommandKey);
            if (command.IsNone()) return;
            var target = GameFields.TargetCreature(__instance);
            if (!target || target.GetZDOID() != command)
            {
                // The native failed-pursuit timeout must end the order, not re-arm it on the next update.
                data.Set(CommandKey, ZDOID.None);
                __instance.m_alertRange = CombatRange;
                return;
            }
            if (!Valid(creature, Companions.Leader(creature), target, true)) return;
            canHearTarget = true;
            Unseen(__instance) = 0f;
        }

        private static bool KeepTarget(BaseAI __instance, Character ___m_character, ref Character __result)
        {
            var ai = __instance as MonsterAI;
            var creature = ai ? (GameFields.HeldCreature ? ___m_character : ai.GetComponent<Character>()) : null;
            if (!Companions.IsCompanion(creature) || !creature.IsOwner() || Unseen(ai) >= 30f) return true;
            var target = GameFields.TargetCreature(ai);
            bool commanded = target && GameFields.CharacterView(creature).GetZDO().GetZDOID(CommandKey) == target.GetZDOID();
            if (!Valid(creature, Companions.Leader(creature), target, commanded)) return true;
            __result = target;
            return false;
        }
    }
}
