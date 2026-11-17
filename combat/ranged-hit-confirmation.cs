using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using EpicLoot.MagicItemEffects;
using EpicLoot.MagicItemEffects.Shards;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Keep native projectile movement and damage RPCs. Remote creature rewards and later obstacle contacts use
    // their owner's result query, sent after the original damage on the same reliable connection.
    internal static class RangedHitConfirmation
    {
        private const string QueryRpc = "ELPF_RangedHitQuery";
        private const string ResultRpc = "ELPF_RangedHitResult";
        private const float Lifetime = 15f;
        private static readonly Dictionary<(long Peer, ZDOID Target, ulong Hit), Queue<Receipt>> Received = new Dictionary<(long, ZDOID, ulong), Queue<Receipt>>();
        private static readonly Dictionary<long, Shot> Pending = new Dictionary<long, Shot>();
        private static readonly Dictionary<(long Peer, long Request), Receipt> Replies = new Dictionary<(long, long), Receipt>();
        private static readonly Dictionary<(long Peer, long Request), (long Owner, long ReplyPeer, float Started)> Forwarded = new Dictionary<(long, long), (long, long, float)>();
        private static readonly AccessTools.FieldRef<HitData> WagerHit = AccessTools.StaticFieldRefAccess<HitData>(AccessTools.Field(typeof(Wager), "stakedHit"));
        private static readonly AccessTools.FieldRef<int> WagerCoins = AccessTools.StaticFieldRefAccess<int>(AccessTools.Field(typeof(Wager), "stakedCoins"));
        private static readonly AccessTools.FieldRef<Destructible, bool> Destroyed = AccessTools.FieldRefAccess<Destructible, bool>("m_destroyed");
        private static readonly Action<Player, int> Refund = AccessTools.MethodDelegate<Action<Player, int>>(
            AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse"), "Refund"));
        private static readonly Func<Player, int> CountCoins = AccessTools.MethodDelegate<Func<Player, int>>(
            AccessTools.Method(typeof(Wager).Assembly.GetType("EpicLoot.General.CoinPurse"), "GetTotalCoins", new[] { typeof(Player) }));
        private static readonly AccessTools.FieldRef<ItemDrop.ItemData> FiringWeapon = AccessTools.StaticFieldRefAccess<ItemDrop.ItemData>(
            AccessTools.Field(typeof(HitSource), "<FiringWeapon>k__BackingField"));
        private static Action<Character, HitData> Effects;
        private static Shot Current, Replaying;
        private static long nextRequest;
        private static float nextCleanup;
        private static ManualLogSource logger;

        private sealed class Shot
        {
            internal Action<bool> Obstacle;
            internal BowVolleySound.Volley Volley;
            internal Character Target;
            internal ItemDrop.ItemData Weapon;
            internal HitData Hit;
            internal Vector3 Position, Center;
            internal bool Tamed;
            internal long Owner, Request;
            internal ZDOID TargetId;
            internal int CoinsBefore, CoinsSpent, Stake;
            internal float Started, Adrenaline, SkillAmount, HealthReturn;
            internal Skills.SkillType Skill;
            internal Receipt Result;
            internal bool Resolved, ReplayEffects = true;
            internal readonly List<(Character Owner, Skills.SkillType Skill, float Amount, ItemDrop.ItemData Weapon)> SkillAwards =
                new List<(Character, Skills.SkillType, float, ItemDrop.ItemData)>();
        }

        private sealed class Receipt
        {
            internal bool Known, Alive, Killed, Dead;
            internal float Started;
        }

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            var self = typeof(RangedHitConfirmation);
            void Patch(Type type, string method, string prefix = null, string postfix = null, string finalizer = null, string transpiler = null)
            {
                var original = AccessTools.Method(type, method) ?? throw new InvalidOperationException("Ranged hit hook moved: " + type.Name + "." + method);
                harmony.Patch(original,
                    prefix: prefix == null ? null : new HarmonyMethod(self, prefix) { priority = Priority.First },
                    postfix: postfix == null ? null : new HarmonyMethod(self, postfix) { priority = Priority.Last },
                    finalizer: finalizer == null ? null : new HarmonyMethod(self, finalizer),
                    transpiler: transpiler == null ? null : new HarmonyMethod(self, transpiler), ilmanipulator: null);
            }
            Patch(typeof(Projectile), "IsValidTarget", postfix: nameof(RejectDeadTarget));
            Patch(typeof(ZNetScene), "OnZDODestroyed", prefix: nameof(DestroyedObject));
            Patch(typeof(Projectile), nameof(Projectile.OnHit), prefix: nameof(BeginImpact), finalizer: nameof(EndImpact), transpiler: nameof(RewardCalls));
            Patch(typeof(Character), "RPC_Damage", prefix: nameof(BeforeReceive), finalizer: nameof(AfterReceive));
            Patch(typeof(Destructible), "RPC_Damage", prefix: nameof(BeforeObstacle), finalizer: nameof(AfterObstacle));
            var shared = typeof(Wager).Assembly.GetType("EpicLoot.src.Magic.MagicItemEffects.Helpers.SharedCharacterDamagePatch");
            var post = AccessTools.Method(shared, "PostDamagePatch") ?? throw new InvalidOperationException("EpicLoot's outgoing effects moved.");
            Effects = AccessTools.MethodDelegate<Action<Character, HitData>>(post);
            Patch(shared, "PostDamagePatch", prefix: nameof(ConfirmEffects));
            Patch(typeof(Wager), nameof(Wager.OnDamageDealt), prefix: nameof(KeepImmediateWager));
            Patch(typeof(MagicEffectsHelper), nameof(MagicEffectsHelper.IsLethalHit), prefix: nameof(ConfirmedKill));
            // Accepted effects may arrive after the creature's network data and visual have been removed.
            // Keep the impact's position and tame/death state, rather than inventing a replacement creature.
            foreach (var type in new[] { typeof(ChainLightning), typeof(StrikeCausesLightning), typeof(StaminaOnKill),
                typeof(Conduit), typeof(QueenEverflow), typeof(EikthyrShockingCharge), typeof(MeteorSummoner) })
                Patch(type, nameof(Conduit.OnDamageDealt), transpiler: nameof(ReplayTarget));
            Patch(typeof(MeteorSummoner), "SummonMeteor", transpiler: nameof(ReplayTarget));
            foreach (var type in new[] { typeof(ApplySlow_Character_Damage_Patch), typeof(StaggerOnDamageTaken_Character_Damage_Patch), typeof(Paralyze) })
                Patch(type, type == typeof(Paralyze) ? nameof(Paralyze.OnDamaged) : nameof(Conduit.OnDamageDealt), prefix: nameof(KeepLivingDebuff));
            harmony.Patch(AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) }),
                postfix: new HarmonyMethod(self, nameof(Register)));
        }

        private static bool Ranged(Skills.SkillType skill) => skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows;

        private static void RejectDeadTarget(IDestructible __0, Skills.SkillType ___m_skill, ref bool __result)
        {
            if (!__result || !Ranged(___m_skill)) return;
            if (__0 is Character target && !target.IsPlayer() && (target.IsDead() || target.GetHealth() <= 0f) ||
                __0 is Destructible obstacle && Destroyed(obstacle)) __result = false;
        }

        private static void DestroyedObject(ZNetScene __instance, ZDO zdo)
        {
            var view = __instance.FindInstance(zdo);
            var obstacle = view ? view.GetComponent<Destructible>() : null;
            // Replicas also retain colliders until the end of the frame. Only the native destruction notification
            // marks them dead; unloading or an estimated damage amount never does.
            if (obstacle) Destroyed(obstacle) = true;
            DestructibleProjectiles.Destroyed(zdo.m_uid);
        }

        internal static void ObstacleResult(Destructible obstacle, HitData hit, Action<bool> result)
        {
            var view = obstacle.GetComponent<ZNetView>();
            if (!view || !view.IsValid()) { result(Destroyed(obstacle)); return; }
            Request(new Shot { TargetId = view.GetZDO().m_uid, Owner = view.GetZDO().GetOwner(),
                Started = Time.realtimeSinceStartup, ReplayEffects = false, Obstacle = result }, hit);
        }

        private static void BeginImpact(Projectile __instance, Collider collider, out Shot __state)
        {
            __state = Current;
            Current = null;
            var player = Player.m_localPlayer;
            var target = collider ? Projectile.FindHitObject(collider).GetComponent<Character>() : null;
            var view = target ? GameFields.CharacterView(target) : null;
            if (!player || GameFields.ProjectileOwner(__instance) != player || !target || target.IsPlayer() || target.IsDead() ||
                target.GetHealth() <= 0f || !view || !view.IsValid() || view.IsOwner() || HitSource.IsBonusHit) return;
            var weapon = GameFields.ProjectileWeapon(__instance);
            if (weapon == null || !Ranged(weapon.m_shared.m_skillType)) return;
            Current = new Shot { Target = target, TargetId = target.GetZDOID(), Owner = view.GetZDO().GetOwner(),
                Position = target.transform.position, Center = target.GetCenterPoint(), Tamed = target.IsTamed(),
                Weapon = weapon.Clone(), CoinsBefore = CountCoins(player), Started = Time.realtimeSinceStartup };
        }

        private static Exception EndImpact(Exception __exception, Shot __state)
        {
            Current = __state;
            return __exception;
        }

        private static bool ConfirmEffects(Character __0, HitData hit)
        {
            if (Replaying != null || Current == null || HitSource.IsBonusHit || hit == null || !ReferenceEquals(Current.Target, __0) || !Ranged(hit.m_skill)) return true;
            var shot = Current;
            Request(shot, hit);
            return false;
        }

        private static void Request(Shot shot, HitData hit)
        {
            if (shot.Request > 0) return;
            shot.Hit = hit;
            if (shot.ReplayEffects)
            {
                shot.CoinsSpent = Mathf.Max(0, shot.CoinsBefore - CountCoins(Player.m_localPlayer));
                if (WagerHit() == hit) { shot.Stake = WagerCoins(); WagerHit() = null; WagerCoins() = 0; }
            }
            shot.Request = ++nextRequest;
            Pending.Add(shot.Request, shot);
            var query = new ZPackage();
            query.Write(shot.Request);
            query.Write(ZDOMan.GetSessionID());
            query.Write(shot.TargetId);
            query.Write(unchecked((long)Fingerprint(hit)));
            query.Write(ZDOMan.GetSessionID());
            query.Write((byte)0);
            ZRoutedRpc.instance.InvokeRoutedRPC(shot.Owner, QueryRpc, query);
            Cleanup();
        }

        // Each eligible splash or area target has its own receipt. The native damage still sends immediately.
        internal static object DamageForSkill(IDestructible target, HitData hit, Character owner, ItemDrop.ItemData weapon, bool eligible)
        {
            var character = target as Character;
            var view = character ? GameFields.CharacterView(character) : null;
            var volley = BowVolleySound.CaptureImpact(character, hit, owner);
            if (volley != null && Current != null && ReferenceEquals(Current.Target, character)) Current.Volley = volley;
            if (!eligible || owner != Player.m_localPlayer || !owner || !character || character.IsPlayer() ||
                !hit.m_ranged || !view || !view.IsValid() || view.IsOwner() || HitSource.IsBonusHit)
            { target.Damage(hit); return null; }
            var previous = Current;
            var shot = previous != null && ReferenceEquals(previous.Target, character) &&
                (previous.Hit == null || ReferenceEquals(previous.Hit, hit)) ? previous : new Shot
                {
                    Target = character, TargetId = character.GetZDOID(), Owner = view.GetZDO().GetOwner(),
                    Position = character.transform.position, Center = character.GetCenterPoint(), Tamed = character.IsTamed(),
                    Weapon = weapon?.Clone(), CoinsBefore = CountCoins(Player.m_localPlayer), Started = Time.realtimeSinceStartup,
                    ReplayEffects = Ranged(hit.m_skill)
                };
            try
            {
                if (volley != null) shot.Volley = volley;
                Current = shot;
                target.Damage(hit);
                Request(shot, hit);
                return shot;
            }
            finally { Current = previous; }
        }

        internal static void AwardSkill(Character owner, Skills.SkillType skill, float amount, ItemDrop.ItemData weapon, object confirmation)
        {
            if (!owner) return;
            if (confirmation is Shot shot)
            {
                if (!shot.Resolved) shot.SkillAwards.Add((owner, skill, amount, weapon?.Clone()));
                else if (shot.Result?.Known == true && shot.Result.Alive) GrantSkill(owner, skill, amount, weapon);
                return;
            }
            GrantSkill(owner, skill, amount, weapon);
        }

        private static void GrantSkill(Character owner, Skills.SkillType skill, float amount, ItemDrop.ItemData weapon)
        {
            var firingWeapon = FiringWeapon();
            var hands = owner == Player.m_localPlayer ? WeaponTooltip.AsIfEquipped(weapon) : HandOverride.Active;
            // The hand override already substitutes the source weapon. EpicLoot's firing adjustment must not do it again.
            if (owner == Player.m_localPlayer) FiringWeapon() = null;
            try { owner.RaiseSkill(skill, amount); }
            finally { FiringWeapon() = firingWeapon; HandOverride.Active = hands; }
        }

        private static void RaiseSkill(Character owner, Skills.SkillType skill, float amount)
        {
            if (Current?.Request > 0 && owner == Player.m_localPlayer) { Current.Skill = skill; Current.SkillAmount += amount; }
            else owner.RaiseSkill(skill, amount);
        }

        private static void AddAdrenaline(Character owner, float amount)
        {
            if (Current?.Request > 0 && owner == Player.m_localPlayer) Current.Adrenaline += amount;
            else owner.AddAdrenaline(amount);
        }

        private static void Heal(Character owner, float amount, bool showText)
        {
            if (Current?.Request > 0 && owner == Player.m_localPlayer) Current.HealthReturn += amount;
            else owner.Heal(amount, showText);
        }

        private static IEnumerable<CodeInstruction> RewardCalls(IEnumerable<CodeInstruction> instructions)
        {
            int skill = 0, adrenaline = 0, healing = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.RaiseSkill))))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(RangedHitConfirmation), nameof(RaiseSkill)); skill++; }
                if (instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.AddAdrenaline))))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(RangedHitConfirmation), nameof(AddAdrenaline)); adrenaline++; }
                if (instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.Heal))))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(RangedHitConfirmation), nameof(Heal)); healing++; }
                yield return instruction;
            }
            if (skill != 1 || adrenaline != 1 || healing != 1) throw new InvalidOperationException("Native projectile rewards moved.");
        }

        private static ulong Fingerprint(HitData hit)
        {
            var package = new ZPackage();
            hit.Serialize(ref package);
            ulong hash = 14695981039346656037UL;
            foreach (byte value in package.GetArray()) hash = unchecked((hash ^ value) * 1099511628211UL);
            return hash;
        }

        private sealed class Receiving
        {
            internal (long Peer, ZDOID Target, ulong Hit) Key;
            internal Receipt Result;
            internal BowVolleySound.Volley Volley;
        }

        private static bool BeforeObstacle(Destructible __instance, long sender, HitData hit, out Receiving __state)
        {
            __state = null;
            var view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || hit == null || !hit.m_ranged || !Ranged(hit.m_skill)) return true;
            if (!view.IsOwner())
            {
                long owner = view.GetZDO().GetOwner();
                if (owner != 0 && owner != sender) view.InvokeRPC("RPC_Damage", hit.Clone());
                return false;
            }
            __state = new Receiving { Key = (HitPeer(hit, sender), view.GetZDO().m_uid, Fingerprint(hit)),
                Result = new Receipt { Known = true, Alive = !Destroyed(__instance), Started = Time.realtimeSinceStartup } };
            return true;
        }

        private static Exception AfterObstacle(Destructible __instance, Receiving __state, Exception __exception)
        {
            if (__state == null) return __exception;
            var result = __state.Result;
            result.Dead = __exception == null && Destroyed(__instance);
            result.Killed = result.Alive && result.Dead;
            if (__exception != null) result.Known = false;
            if (!Received.TryGetValue(__state.Key, out var queue)) Received.Add(__state.Key, queue = new Queue<Receipt>());
            queue.Enqueue(result); Cleanup();
            return __exception;
        }

        private static bool BeforeReceive(Character __instance, long sender, HitData hit, out Receiving __state)
        {
            __state = null;
            var view = GameFields.CharacterView(__instance);
            if (!view || !view.IsValid() || hit == null || !hit.m_ranged || __instance.IsPlayer()) return true;
            if (!view.IsOwner())
            {
                long owner = view.GetZDO().GetOwner();
                if (owner != 0 && owner != sender) view.InvokeRPC("RPC_Damage", hit.Clone());
                return false;
            }
            long source = HitPeer(hit, sender);
            // A former owner can forward the shot back to its shooter after ownership moves.
            // Local bow kills use this same health boundary, before outgoing secondary effects on Character.Damage.
            bool local = source == ZDOMan.GetSessionID() && sender == source;
            var volley = local ? BowVolleySound.CaptureImpact(__instance, hit, hit.GetAttacker()) : null;
            if (local && volley == null) return true;
            __state = new Receiving { Key = (source, __instance.GetZDOID(), Fingerprint(hit)),
                Volley = volley,
                Result = new Receipt { Known = true, Alive = !__instance.IsDead() && __instance.GetHealth() > 0f, Started = Time.realtimeSinceStartup } };
            return true;
        }

        private static long HitPeer(HitData hit, long sender)
        {
            var attacker = hit.GetAttacker();
            var view = attacker ? GameFields.CharacterView(attacker) : null;
            return view && view.IsValid() ? view.GetZDO().GetOwner() : sender;
        }

        private static Exception AfterReceive(Character __instance, Receiving __state, Exception __exception)
        {
            if (__state == null) return __exception;
            var result = __state.Result;
            result.Dead = !__instance || __instance.IsDead();
            result.Killed = result.Alive && (result.Dead || __instance.GetHealth() <= 0f);
            if (__exception != null) result.Alive = false;
            if (__state.Volley != null)
            {
                if (__exception == null && result.Killed) BowVolleySound.ConfirmKill(__state.Volley, __state.Key.Target);
                return __exception;
            }
            if (!Received.TryGetValue(__state.Key, out var queue)) Received.Add(__state.Key, queue = new Queue<Receipt>());
            queue.Enqueue(result);
            Cleanup();
            return __exception;
        }

        private static void Register(ZRoutedRpc __instance)
        {
            Received.Clear(); Pending.Clear(); Replies.Clear(); Forwarded.Clear(); Current = null; Replaying = null; nextRequest = 0;
            DestructibleProjectiles.Clear();
            __instance.Register<ZPackage>(QueryRpc, ReceiveQuery);
            __instance.Register<ZPackage>(ResultRpc, ReceiveResult);
        }

        private static void ReceiveQuery(long sender, ZPackage package)
        {
            if (package == null || package.Size() != 45) return;
            long request = package.ReadLong();
            long source = package.ReadLong();
            var target = package.ReadZDOID();
            ulong fingerprint = unchecked((ulong)package.ReadLong());
            long replyPeer = package.ReadLong();
            byte hops = package.ReadByte();
            if (request <= 0 || source == 0 || replyPeer != sender || (hops == 0 && source != sender) || hops > 4) return;
            var requestKey = (source, request);
            if (!Replies.TryGetValue(requestKey, out var receipt))
            {
                var key = (source, target, fingerprint);
                bool found = Received.TryGetValue(key, out var queue) && queue.Count > 0;
                if (!found && hops < 4)
                {
                    var data = ZDOMan.instance.GetZDO(target);
                    long owner = data == null ? 0 : data.GetOwner();
                    if (owner != 0 && owner != ZDOMan.GetSessionID() && owner != sender)
                    {
                        Forwarded[requestKey] = (owner, replyPeer, Time.realtimeSinceStartup);
                        var forward = new ZPackage();
                        forward.Write(request); forward.Write(source); forward.Write(target); forward.Write(unchecked((long)fingerprint));
                        forward.Write(ZDOMan.GetSessionID()); forward.Write((byte)(hops + 1));
                        ZRoutedRpc.instance.InvokeRoutedRPC(owner, QueryRpc, forward);
                        return;
                    }
                }
                receipt = found ? queue.Dequeue() : new Receipt { Started = Time.realtimeSinceStartup };
                if (queue != null && queue.Count == 0) Received.Remove(key);
                Replies.Add(requestKey, receipt);
            }
            var result = new ZPackage();
            result.Write(request); result.Write(source); result.Write(receipt.Known); result.Write(receipt.Alive); result.Write(receipt.Killed); result.Write(receipt.Dead);
            ZRoutedRpc.instance.InvokeRoutedRPC(replyPeer, ResultRpc, result);
            Cleanup();
        }

        private static void ReceiveResult(long sender, ZPackage package)
        {
            if (package == null || package.Size() != 20) return;
            long request = package.ReadLong();
            long source = package.ReadLong();
            bool known = package.ReadBool(), alive = package.ReadBool(), killed = package.ReadBool(), dead = package.ReadBool();
            if (source != ZDOMan.GetSessionID())
            {
                var key = (source, request);
                if (!Forwarded.TryGetValue(key, out var forwarded) || sender != forwarded.Owner) return;
                Forwarded.Remove(key);
                package.SetPos(0);
                ZRoutedRpc.instance.InvokeRoutedRPC(forwarded.ReplyPeer, ResultRpc, package);
                return;
            }
            if (!Pending.TryGetValue(request, out var shot) || sender != shot.Owner) return;
            Pending.Remove(request);
            shot.Resolved = true;
            shot.Result = new Receipt { Known = known, Alive = alive, Killed = killed, Dead = dead };
            if (shot.Obstacle != null) { shot.Obstacle(known && dead); return; }
            if (!known)
            {
                logger.LogWarning("A creature owner could not match a ranged hit to its damage receipt; damage was not resent and unconfirmed rewards were withheld.");
                return;
            }
            var player = Player.m_localPlayer;
            if (!player) return;
            if (!alive) { Refund(player, shot.CoinsSpent); return; }
            if (killed) Refund(player, shot.Stake);
            if (killed) BowVolleySound.ConfirmKill(shot.Volley, shot.TargetId);
            shot.Result = new Receipt { Known = true, Alive = true, Killed = killed, Dead = dead };
            var previous = Replaying;
            var previousWeapon = FiringWeapon();
            try
            {
                Replaying = shot;
                FiringWeapon() = shot.Weapon;
                if (shot.ReplayEffects) Effects(shot.Target, shot.Hit);
                if (shot.HealthReturn > 0f) player.Heal(shot.HealthReturn);
                if (shot.SkillAmount != 0f) GrantSkill(player, shot.Skill, shot.SkillAmount, shot.Weapon);
                foreach (var award in shot.SkillAwards) GrantSkill(award.Owner, award.Skill, award.Amount, award.Weapon);
                shot.SkillAwards.Clear();
                player.AddAdrenaline(shot.Adrenaline);
            }
            finally { FiringWeapon() = previousWeapon; Replaying = previous; }
        }

        internal static void Cleanup()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextCleanup) return;
            nextCleanup = now + 2f;
            foreach (var key in Received.Keys.ToArray())
            {
                var queue = Received[key];
                while (queue.Count > 0 && now - queue.Peek().Started > Lifetime) queue.Dequeue();
                if (queue.Count == 0) Received.Remove(key);
            }
            foreach (var key in Replies.Where(pair => now - pair.Value.Started > Lifetime).Select(pair => pair.Key).ToArray()) Replies.Remove(key);
            foreach (var key in Forwarded.Where(pair => now - pair.Value.Started > Lifetime).Select(pair => pair.Key).ToArray()) Forwarded.Remove(key);
            foreach (var key in Pending.Where(pair => now - pair.Value.Started > Lifetime).Select(pair => pair.Key).ToArray())
            {
                var shot = Pending[key];
                Pending.Remove(key);
                if (shot.Obstacle != null) { shot.Obstacle(false); continue; }
                logger.LogWarning("A creature owner did not confirm a ranged hit; damage was not resent and unconfirmed rewards were withheld.");
            }
        }

        private static bool Matches(Character target) => Replaying != null && ReferenceEquals(target, Replaying.Target);
        internal static Vector3 Position(Character target) => Matches(target) ? Replaying.Position : target.transform.position;
        private static Vector3 ComponentPosition(Component target) => target is Character character && Matches(character) ? Replaying.Position : target.transform.position;
        internal static Vector3 Center(Character target) => Matches(target) ? Replaying.Center : target.GetCenterPoint();
        private static bool Dead(Character target) => Matches(target) ? Replaying.Result.Dead : target.IsDead();
        private static bool Tamed(Character target) => Matches(target) ? Replaying.Tamed : target.IsTamed();
        private static bool Equal(UnityEngine.Object first, UnityEngine.Object second)
        {
            if (Replaying != null && ((ReferenceEquals(first, Replaying.Target) && ReferenceEquals(second, null)) ||
                (ReferenceEquals(second, Replaying.Target) && ReferenceEquals(first, null)))) return false;
            return first == second;
        }
        private static bool ConfirmedKill(Character target, ref bool __result)
        {
            if (!Matches(target)) return true;
            __result = Replaying.Result.Killed;
            return false;
        }
        private static bool KeepLivingDebuff(Character __0) => !Matches(__0) || (__0 && !Replaying.Result.Killed);
        private static bool KeepImmediateWager() => Replaying == null;

        private static IEnumerable<CodeInstruction> ReplayTarget(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var transform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
            var position = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
            for (int index = 0; index < code.Count; index++)
            {
                var instruction = code[index];
                string replacement = instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.GetCenterPoint))) ? nameof(Center) :
                    instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.IsDead))) ? nameof(Dead) :
                    instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.IsTamed))) ? nameof(Tamed) :
                    instruction.Calls(AccessTools.Method(typeof(UnityEngine.Object), "op_Equality")) ? nameof(Equal) : null;
                if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(RangedHitConfirmation), replacement); }
                if (instruction.Calls(transform) && index + 1 < code.Count && code[index + 1].Calls(position))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(RangedHitConfirmation), nameof(ComponentPosition));
                    code[index + 1].opcode = OpCodes.Nop;
                    code[index + 1].operand = null;
                }
                yield return instruction;
            }
        }
    }
}
