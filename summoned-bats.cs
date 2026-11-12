using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Keep ordinary bat combat and wildlife exclusions; a new direct hit commands the summoner's bats.
    internal static class SummonedBats
    {
        private static readonly int BatPrefab = "EL_TamedBat".GetStableHashCode();
        private const string SummonerKey = "elpf_bat_summoner";
        private const string TargetKey = "elpf_bat_target";
        private const string ContactTimeKey = "elpf_bat_contact";
        private const string ContactSequenceKey = "elpf_bat_contact_sequence";
        private static float PursuitRange => Plugin.BatPursuitRange.Value;
        private const double ContactLifetime = 2.5;
        private static readonly AccessTools.FieldRef<MonsterAI, Character> Target = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> UnseenTime = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceSensedTargetCreature");
        private static readonly AccessTools.FieldRef<MonsterAI, float> InactiveTime = AccessTools.FieldRefAccess<MonsterAI, float>("m_timeSinceAttacking");
        private static readonly AccessTools.FieldRef<MonsterAI, float> TargetUpdateTime = AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");
        private static readonly Action<MonsterAI, Character> SetTarget = AccessTools.MethodDelegate<Action<MonsterAI, Character>>(AccessTools.Method(typeof(MonsterAI), "SetTarget"));
        private static readonly Action<MonsterAI, bool> Alert = AccessTools.MethodDelegate<Action<MonsterAI, bool>>(AccessTools.Method(typeof(MonsterAI), "SetAlerted"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            void Patch(Type type, string name, string prefix = null, string postfix = null, string transpiler = null)
            {
                var method = AccessTools.Method(type, name);
                if (method == null) throw new InvalidOperationException("Summoned bat hook moved: " + type.Name + "." + name);
                harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(SummonedBats), prefix) { priority = Priority.First },
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(SummonedBats), postfix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(SummonedBats), transpiler));
            }
            Patch(typeof(MonsterAI), nameof(MonsterAI.SetFollowTarget), postfix: nameof(RememberSummoner));
            Patch(typeof(MonsterAI), "UpdateTarget", prefix: nameof(PrepareTarget), postfix: nameof(FollowCommand));
            Patch(typeof(MonsterAI), "SetTarget", prefix: nameof(RetaliateAgainstSummoner));
            Patch(typeof(BaseAI), "FindEnemy", prefix: nameof(KeepTarget), transpiler: nameof(FilterHunting));
            Patch(typeof(Character), "OnEnable", postfix: nameof(SkipBatTarget));
            Patch(typeof(Character), "OnEnable", postfix: nameof(IgnoreCharacterCollisions));
            Patch(typeof(Character), nameof(Character.Damage), prefix: nameof(BeforeDamage));
            Patch(typeof(Character), "RPC_Damage", prefix: nameof(ReceiveDamage));
            Patch(typeof(Projectile), "IsValidTarget", postfix: nameof(ProjectileTarget));
            Patch(typeof(Aoe), "ShouldHit", postfix: nameof(AreaTarget));

            // EpicLoot's contact postfix runs even when Character.Damage is skipped. Its prefixes also spend
            // resources before a hit lands, so all three must reject the same protected contact.
            var effects = typeof(HitSource).Assembly.GetType("EpicLoot.src.Magic.MagicItemEffects.Helpers.SharedCharacterDamagePatch");
            foreach (string name in new[] { "EarlyPreDamagePatch", "LastPreDamagePatch", "PostDamagePatch" })
                Patch(effects ?? throw new InvalidOperationException("EpicLoot's contact effects moved."), name, prefix: nameof(AllowEffects));

            var attacks = new[] { AccessTools.Method(typeof(Attack), "DoMeleeAttack"),
                typeof(Attack).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(method => method.Name.StartsWith("<DoAreaAttack>g__checkHits|")) };
            foreach (var attack in attacks)
                harmony.Patch(attack, transpiler: new HarmonyMethod(typeof(SummonedBats), nameof(ProtectMelee)));
        }

        internal static bool IsBat(Character character)
        {
            var view = character ? GameFields.OwnView(character) : null;
            return view && view.IsValid() && view.GetZDO().GetPrefab() == BatPrefab;
        }

        private static bool Protected(Character target, Character attacker) =>
            IsBat(target) && attacker && (attacker.IsPlayer() || attacker.IsTamed());

        private static Player Summoner(Character bat) =>
            Player.GetPlayer(bat.GetComponent<ZNetView>().GetZDO().GetLong(SummonerKey, 0));

        internal static long SummonerId(Character bat) => IsBat(bat) ? SummonerId(bat.GetComponent<ZNetView>().GetZDO()) : 0;
        internal static long SummonerId(ZDO data) => data != null && data.GetPrefab() == BatPrefab ? data.GetLong(SummonerKey, 0) : 0;

        internal static void ResetAfterDungeon(Character bat, Player summoner)
        {
            var ai = bat.GetComponent<MonsterAI>(); var data = GameFields.CharacterView(bat).GetZDO();
            data.Set(TargetKey, ZDOID.None);
            data.Set(ContactSequenceKey, GameFields.CharacterView(summoner).GetZDO().GetLong(ContactSequenceKey));
            Target(ai) = null; SetTarget(ai, null); Alert(ai, false);
            UnseenTime(ai) = InactiveTime(ai) = TargetUpdateTime(ai) = 0f;
            ai.SetFollowTarget(summoner.gameObject);
        }

        private static void SkipBatTarget(Character __instance)
        {
            if (IsBat(__instance)) __instance.m_aiSkipTarget = true;
        }

        private static void RetaliateAgainstSummoner(MonsterAI __instance, ref Character __0)
        {
            if (!IsBat(__0)) return;
            // Native damage still wakes and alerts the mob. Only its retaliation target changes.
            var summoner = Summoner(__0);
            var creature = __instance.GetComponent<Character>();
            __0 = summoner && !summoner.IsDead() && !summoner.InGhostMode() && !summoner.InDebugFlyMode() &&
                !summoner.m_aiSkipTarget && BaseAI.IsEnemy(creature, summoner) &&
                (creature.transform.position - summoner.transform.position).sqrMagnitude <= PursuitRange * PursuitRange
                ? summoner : null;
        }

        private static void IgnoreCharacterCollisions(Character __instance)
        {
            bool bat = IsBat(__instance);
            // Bosses stay solid: a bat inside a large boss body cannot bite it from within, so it bites at the surface.
            if (__instance.IsBoss()) return;
            // Some mobs have extra solid attack/weakspot colliders. Exclude their physical contacts too;
            // colliders stay enabled for attack queries, while trigger hitboxes and terrain are untouched.
            var bodies = __instance.GetComponentsInChildren<Collider>()
                .Where(collider => !collider.isTrigger && collider.GetComponentInParent<Character>() == __instance).ToArray();
            foreach (var other in Character.GetAllCharacters())
            {
                if (other == __instance || !other.gameObject.activeInHierarchy || (!bat && !IsBat(other)) || other.IsBoss()) continue;
                foreach (var otherBody in other.GetComponentsInChildren<Collider>())
                {
                    if (otherBody.isTrigger || otherBody.GetComponentInParent<Character>() != other) continue;
                    foreach (var body in bodies) Physics.IgnoreCollision(body, otherBody);
                }
            }
        }

        private static void RememberSummoner(MonsterAI __instance, GameObject __0)
        {
            var bat = __instance.GetComponent<Character>();
            var summoner = __0 ? __0.GetComponent<Player>() : null;
            if (IsBat(bat) && bat.IsOwner() && summoner)
                bat.GetComponent<ZNetView>().GetZDO().Set(SummonerKey, summoner.GetPlayerID());
        }

        private static bool ValidTarget(Character bat, Player summoner, Character target, bool commanded = false) =>
            target && !target.IsDead() && !target.IsPlayer() && !target.IsTamed() && !target.m_aiSkipTarget &&
            BaseAI.IsEnemy(bat, target) && (commanded ? WithinSimulation(summoner, target) :
                !summoner || (target.transform.position - summoner.transform.position).sqrMagnitude <= PursuitRange * PursuitRange);

        internal static bool WithinSimulation(Player summoner, Character target)
        {
            var playerView = summoner ? summoner.GetComponent<ZNetView>() : null;
            var targetView = target ? target.GetComponent<ZNetView>() : null;
            return playerView && playerView.IsValid() && !summoner.IsDead() && targetView && targetView.IsValid() &&
                CreatureOwnership.CanOwn(targetView.GetZDO(), playerView.GetZDO().GetOwner());
        }

        private static bool CanHunt(Character creature, Character target)
        {
            // One still waiting to appear can't be fought, and standing on its spot would keep it from appearing.
            if (!BaseAI.IsEnemy(creature, target) || FreshCreatureSpawns.Waiting(target)) return false;
            if (SummonOwnership.IsFollower(creature)) return SummonedFollowers.CanHunt(creature, target);
            return !IsBat(creature) || WildlifeCanFight(target);
        }

        internal static bool WildlifeCanFight(Character target)
        {
            var ai = target.GetBaseAI();
            if (target.GetFaction() != Character.Faction.AnimalsVeg && !(ai is AnimalAI) && !target.GetComponent<Tameable>()) return true;
            var monster = ai as MonsterAI;
            var opponent = monster ? Target(monster) : null;
            return monster && monster.IsAlerted() && opponent && !opponent.IsDead() && (opponent.IsPlayer() || opponent.IsTamed());
        }

        internal static bool RecentContact(Player summoner, out Character target, out long sequence, out long contact)
        {
            target = null;
            var data = summoner.GetComponent<ZNetView>().GetZDO();
            sequence = data.GetLong(ContactSequenceKey);
            contact = data.GetLong(ContactTimeKey);
            double age = (ZNet.instance.GetTime().Ticks - contact) / (double)TimeSpan.TicksPerSecond;
            if (age < 0 || age > ContactLifetime) return false;
            var instance = ZNetScene.instance.FindInstance(data.GetZDOID(TargetKey));
            target = instance ? instance.GetComponent<Character>() : null;
            return target;
        }

        private static IEnumerable<CodeInstruction> FilterHunting(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var enemy = AccessTools.Method(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) });
            if (code.Count(item => item.Calls(enemy)) != 1)
                throw new InvalidOperationException("The autonomous enemy search moved.");
            code.Single(item => item.Calls(enemy)).operand = AccessTools.Method(typeof(SummonedBats), nameof(CanHunt));
            return code;
        }

        // The target hooks run for every creature's AI and read the creature its Awake took from the same search.
        private static void PrepareTarget(MonsterAI __instance, Character ___m_character)
        {
            var creature = GameFields.HeldCreature ? ___m_character : __instance.GetComponent<Character>();
            if (!IsBat(creature))
            {
                var previous = Target(__instance);
                if (IsBat(previous))
                {
                    Target(__instance) = null;
                    RetaliateAgainstSummoner(__instance, ref previous);
                    SetTarget(__instance, previous);
                    TargetUpdateTime(__instance) = 0f;
                }
                return;
            }
            if (!creature.IsOwner()) return;
            var summoner = Summoner(creature);
            if (__instance.GetFollowTarget() != (summoner ? summoner.gameObject : null))
                __instance.SetFollowTarget(summoner ? summoner.gameObject : null);
            __instance.m_alertRange = PursuitRange;
            var batData = creature.GetComponent<ZNetView>().GetZDO();
            if (summoner)
            {
                if (RecentContact(summoner, out var target, out long sequence, out _) && sequence != batData.GetLong(ContactSequenceKey, 0))
                {
                    if (ValidTarget(creature, summoner, target, true))
                    {
                        batData.Set(ContactSequenceKey, sequence);
                        batData.Set(TargetKey, target.GetZDOID());
                        if (Target(__instance) != target) Target(__instance) = null;
                        SetTarget(__instance, target);
                        InactiveTime(__instance) = 0f;
                        Alert(__instance, true);
                    }
                }
            }
            var commandedObject = ZNetScene.instance.FindInstance(batData.GetZDOID(TargetKey));
            var commanded = commandedObject ? commandedObject.GetComponent<Character>() : null;
            if (ValidTarget(creature, summoner, commanded, true))
            {
                // The contact survives ownership transfer. Native attack reach and sight remain unchanged;
                // only the follow leash and knowledge of the designated target's position are extended.
                if (Target(__instance) != commanded)
                {
                    Target(__instance) = null;
                    SetTarget(__instance, commanded);
                }
                __instance.m_alertRange = Mathf.Max(PursuitRange, Vector3.Distance(summoner.transform.position, commanded.transform.position) + 1f);
                UnseenTime(__instance) = 0f;
                return;
            }
            if (!batData.GetZDOID(TargetKey).IsNone())
            {
                batData.Set(TargetKey, ZDOID.None);
                Target(__instance) = null;
            }
            if (!ValidTarget(creature, summoner, Target(__instance))) Target(__instance) = null;
        }

        private static void FollowCommand(MonsterAI __instance, Character ___m_character, ref bool canHearTarget)
        {
            var creature = GameFields.HeldCreature ? ___m_character : __instance.GetComponent<Character>();
            var target = Target(__instance);
            if (!IsBat(creature) || !creature.IsOwner() || !target) return;
            var data = creature.GetComponent<ZNetView>().GetZDO();
            if (data.GetZDOID(TargetKey) != target.GetZDOID() || !ValidTarget(creature, Summoner(creature), target, true)) return;
            // This selects the native pursuit branch without granting sight through cover or bypassing bite reach.
            canHearTarget = true;
            UnseenTime(__instance) = 0f;
        }

        private static bool KeepTarget(BaseAI __instance, Character ___m_character, ref Character __result)
        {
            var ai = __instance as MonsterAI;
            if (!ai || !IsBat(GameFields.HeldCreature ? ___m_character : ai.GetComponent<Character>()) || !Target(ai) || UnseenTime(ai) >= 30f) return true;
            // UpdateTarget has already discarded dead, friendly or out-of-range targets. A new scan must not
            // replace a living one merely because another creature became closer while the bat was attacking.
            __result = Target(ai);
            return false;
        }

        private static bool BeforeDamage(Character __instance, HitData hit)
        {
            var attacker = hit?.GetAttacker();
            if (Protected(__instance, attacker)) return false;
            var player = attacker as Player;
            if (player && player == Player.m_localPlayer && !HitSource.IsBonusHit && hit.GetTotalDamage() > 0f &&
                hit.m_hitType != HitData.HitType.Poisoned && hit.m_hitType != HitData.HitType.Burning &&
                !__instance.IsDead() && !__instance.IsPlayer() && !__instance.IsTamed())
            {
                // A first hit can aggravate a neutral dvergr after this contact hook. Target admission still checks hostility.
                var data = player.GetComponent<ZNetView>().GetZDO();
                data.Set(TargetKey, __instance.GetZDOID());
                data.Set(ContactTimeKey, ZNet.instance.GetTime().Ticks);
                // The network clock is shared by every impact in a frame; it is an age, not a unique contact.
                data.Set(ContactSequenceKey, data.GetLong(ContactSequenceKey, 0) + 1);
                RecentHitHealthBars.Record(__instance);
            }
            return true;
        }

        private static bool ReceiveDamage(Character __instance, HitData hit) => !Protected(__instance, hit?.GetAttacker());
        private static bool AllowEffects(Character __0, HitData hit) => !Protected(__0, hit?.GetAttacker());

        private static void ProjectileTarget(IDestructible __0, Character ___m_owner, ref bool __result)
        {
            if (__result && Protected(__0 as Character, ___m_owner)) __result = false;
        }

        private static void AreaTarget(Collider __0, Character ___m_owner, ref bool __result)
        {
            if (!__result) return;
            var hit = Projectile.FindHitObject(__0);
            if (hit && Protected(hit.GetComponent<Character>(), ___m_owner)) __result = false;
        }

        private static bool FriendlyFire(bool enabled, Character target) => enabled && !IsBat(target);

        private static IEnumerable<CodeInstruction> ProtectMelee(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var enemy = AccessTools.Method(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) });
            var pvp = AccessTools.Method(typeof(Character), nameof(Character.IsPVPEnabled));
            int enemyCall = code.FindIndex(item => item.Calls(enemy));
            int pvpCall = code.FindIndex(item => item.Calls(pvp));
            if (enemyCall < 1 || pvpCall <= enemyCall || !code[enemyCall - 1].IsLdloc() || code.Count(item => item.Calls(pvp)) != 1)
                throw new InvalidOperationException("The melee friendly-fire check moved.");
            code.InsertRange(pvpCall + 1, new[] { new CodeInstruction(code[enemyCall - 1].opcode, code[enemyCall - 1].operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SummonedBats), nameof(FriendlyFire))) });
            return code;
        }
    }
}
