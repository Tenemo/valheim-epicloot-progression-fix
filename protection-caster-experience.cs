using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class ProtectionCasterExperience
    {
        private const string Request = Plugin.Id + ".protection-experience";
        private const string Delivery = Plugin.Id + ".protection-experience-delivery";
        private const float TransitGrace = 10f, OwnershipWait = 5f;
        private const int Register = 0, Break = 1, Forget = 2;
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<StatusEffect, float> Age = AccessTools.FieldRefAccess<StatusEffect, float>("m_time");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Damage = AccessTools.FieldRefAccess<SE_Shield, float>("m_damage");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Capacity = AccessTools.FieldRefAccess<SE_Shield, float>("m_totalAbsorbDamage");
        private static ConditionalWeakTable<SE_Shield, Shield> Shields = new ConditionalWeakTable<SE_Shield, Shield>();
        private static ConditionalWeakTable<ZRpc, Connection> Connections = new ConditionalWeakTable<ZRpc, Connection>();
        private static readonly Dictionary<ZDOID, Dictionary<ZDOID, Application>> Applications = new Dictionary<ZDOID, Dictionary<ZDOID, Application>>();
        private static Connection serverConnection = new Connection();
        private static uint nextShield;
        private static long nextRegistration, lastDelivery;
        private static ZRpc receivingConnection;
        private static float nextCleanup;

        private sealed class Shield
        {
            // A session id and per-shield sequence identify an award, not a world object.
            internal ZDOID Award, Target, Caster;
            internal ZNet Network;
            internal ZRpc Server;
            internal long CasterPeer;
            internal bool Consumed, Registered;
        }
        private sealed class Connection { internal long Registration, Delivery; }
        private sealed class Application
        {
            internal ZDOID Caster;
            internal long Owner, CasterPeer;
            internal ZRpc Source, Receiver;
            internal float Expires, VerifyUntil, OwnerLeft = float.NegativeInfinity, Destroyed = float.NegativeInfinity;
            internal long DestroyedOwner;
            internal bool Verified, Broken, Ended, Consumed;
        }

        internal static void Initialize(IntegrationPatches harmony)
        {
            var type = typeof(ProtectionCasterExperience);
            harmony.Patch(AccessTools.Method(typeof(StatusEffect), nameof(StatusEffect.SetAttacker)), postfix: new HarmonyMethod(type, nameof(Applied)));
            harmony.Patch(AccessTools.Method(typeof(SE_Shield), nameof(SE_Shield.IsDone)), transpiler: new HarmonyMethod(type, nameof(RedirectBreak)));
            foreach (string method in new[] { nameof(StatusEffect.Stop), nameof(StatusEffect.OnDestroy) })
                harmony.Patch(AccessTools.Method(typeof(StatusEffect), method), prefix: new HarmonyMethod(type, nameof(Ended)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(type, nameof(Connected)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Disconnect", new[] { typeof(ZNetPeer) }), prefix: new HarmonyMethod(type, nameof(Disconnected)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Shutdown"), prefix: new HarmonyMethod(type, nameof(ClearSession)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "Update"), postfix: new HarmonyMethod(type, nameof(Cleanup)));
            harmony.Patch(AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), prefix: new HarmonyMethod(type, nameof(OwnerChanging)));
            harmony.Patch(AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO"), prefix: new HarmonyMethod(type, nameof(Destroying)));
        }

        private static bool ConnectedNow(Shield state) => ZNet.instance && ReferenceEquals(state.Network, ZNet.instance) &&
            (ZNet.instance.IsServer() || state.Server != null && ReferenceEquals(state.Server, ZNet.instance.GetServerPeer()?.m_rpc) && state.Server.IsConnected());

        internal static void Reapplying(SE_Shield shield)
        {
            // A hit can exhaust the old cast before the status update removes it. Settle that cast before its capacity
            // and caster change; the following native SetAttacker starts a distinct award for the refreshed bubble.
            if (Damage(shield) > Capacity(shield)) BreakReward(shield);
            Ended(shield);
        }

        private static void Applied(StatusEffect __instance, Character attacker)
        {
            if (!(__instance is SE_Shield shield) || shield.NameHash() != ShieldHash || !ZNet.instance) return;
            var view = shield.m_character ? GameFields.CharacterView(shield.m_character) : null;
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            if (!(attacker is Player caster))
            {
                if (Shields.TryGetValue(shield, out var previous)) { Unregister(previous); previous.Caster = ZDOID.None; }
                return;
            }
            var casterView = GameFields.CharacterView(caster);
            if (!casterView || !casterView.IsValid()) return;
            var state = Shields.GetValue(shield, _ => new Shield { Award = new ZDOID(ZDOMan.GetSessionID(), ++nextShield) });
            if (state.Registered && (!ReferenceEquals(state.Network, ZNet.instance) || caster == Player.m_localPlayer)) Unregister(state);
            state.Network = ZNet.instance; state.Server = ZNet.instance.GetServerPeer()?.m_rpc;
            state.Target = view.GetZDO().m_uid; state.Caster = caster.GetZDOID(); state.CasterPeer = casterView.GetZDO().GetOwner();
            if (state.Consumed || !ConnectedNow(state) || caster == Player.m_localPlayer) return;
            var package = Message(Register, state); package.Write(++nextRegistration);
            package.Write(state.Caster); package.Write(state.CasterPeer); package.Write(Mathf.Max(0f, shield.m_ttl - Age(shield)));
            Submit(state, package); state.Registered = true;
        }

        private static IEnumerable<CodeInstruction> RedirectBreak(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var damage = AccessTools.Field(typeof(SE_Shield), "m_damage");
            var capacity = AccessTools.Field(typeof(SE_Shield), "m_totalAbsorbDamage");
            var skill = AccessTools.Field(typeof(SE_Shield), nameof(SE_Shield.m_levelUpSkillOnBreak));
            var gates = Enumerable.Range(0, code.Count - 1).Where(index => code[index].LoadsField(skill) &&
                (code[index + 1].opcode == OpCodes.Brfalse || code[index + 1].opcode == OpCodes.Brfalse_S)).ToArray();
            if (code.Count < 5 || !code[1].LoadsField(damage) || !code[3].LoadsField(capacity) ||
                (code[4].opcode != OpCodes.Ble_Un && code[4].opcode != OpCodes.Ble_Un_S) || gates.Length != 1)
                throw new InvalidOperationException("Protection's strict damage-break award branch changed.");
            code[gates[0]].opcode = OpCodes.Call;
            code[gates[0]].operand = AccessTools.Method(typeof(ProtectionCasterExperience), nameof(BreakReward));
            return code;
        }

        // Called inside the native damage > capacity branch, before a creature's null GetSkills lookup.
        private static Skills.SkillType BreakReward(SE_Shield shield)
        {
            if (shield.NameHash() != ShieldHash) return shield.m_levelUpSkillOnBreak;
            var view = shield.m_character ? GameFields.CharacterView(shield.m_character) : null;
            if (!view || !view.IsValid() || !view.IsOwner() || !Shields.TryGetValue(shield, out var state) ||
                state.Consumed || state.Caster.IsNone() || !ConnectedNow(state)) return Skills.SkillType.None;
            state.Consumed = true;
            var local = Player.m_localPlayer;
            if (local && local.GetZDOID() == state.Caster)
                local.GetSkills().RaiseSkill(shield.m_levelUpSkillOnBreak, shield.m_levelUpSkillFactor);
            else if (state.Registered) Submit(state, Message(Break, state));
            return Skills.SkillType.None;
        }

        private static ZPackage Message(int action, Shield state)
        {
            var package = new ZPackage(); package.Write(action); package.Write(state.Target); package.Write(state.Award); return package;
        }
        private static void Submit(Shield state, ZPackage package)
        {
            if (!ConnectedNow(state)) return;
            if (ZNet.instance.IsServer()) Receive(null, serverConnection, new ZPackage(package.GetArray()));
            else state.Server.Invoke(Request, package);
        }
        private static void Unregister(Shield state)
        {
            if (state.Registered) Submit(state, Message(Forget, state));
            state.Registered = false;
        }
        private static void Ended(StatusEffect __instance)
        {
            if (__instance is SE_Shield shield && Shields.TryGetValue(shield, out var state))
            { Unregister(state); Shields.Remove(shield); }
        }

        private static void Connected(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
            {
                var connection = Connections.GetValue(peer.m_rpc, _ => new Connection());
                peer.m_rpc.Register<ZPackage>(Request, (rpc, package) =>
                { if (rpc == peer.m_rpc && peer.IsReady() && rpc.IsConnected()) Receive(peer, connection, package); });
            }
            else if (peer.m_server)
            {
                if (!ReferenceEquals(receivingConnection, peer.m_rpc)) { receivingConnection = peer.m_rpc; lastDelivery = 0; }
                peer.m_rpc.Register<ZPackage>(Delivery, (rpc, package) =>
                {
                    if (rpc != receivingConnection || rpc != ZNet.instance.GetServerPeer()?.m_rpc || package.Size() > 64) return;
                    try
                    {
                        long sequence = package.ReadLong(); var caster = package.ReadZDOID();
                        if (sequence <= lastDelivery) return;
                        lastDelivery = sequence;
                        RewardLocal(caster);
                    }
                    catch (EndOfStreamException) { }
                });
            }
        }

        private static bool Receiver(ZDOID caster, long owner, out ZRpc rpc)
        {
            rpc = null;
            if (Player.m_localPlayer && Player.m_localPlayer.GetZDOID() == caster && owner == ZDOMan.GetSessionID()) return true;
            var peer = ZNet.instance.GetPeer(owner);
            if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected() || peer.m_characterID != caster ||
                ZDOMan.instance.GetZDO(caster)?.GetOwner() != owner) return false;
            rpc = peer.m_rpc; return true;
        }
        private static bool OwnsTarget(ZDOID target, long owner)
        {
            var data = ZDOMan.instance.GetZDO(target);
            var prefab = data == null ? null : ZNetScene.instance.GetPrefab(data.GetPrefab());
            return data != null && data.GetOwner() == owner && prefab && prefab.GetComponent<Character>();
        }

        private static void Receive(ZNetPeer sender, Connection connection, ZPackage package)
        {
            if (!ZNet.instance.IsServer() || package.Size() > 128) return;
            long owner = sender == null ? ZDOMan.GetSessionID() : sender.m_uid;
            ZRpc source = sender?.m_rpc;
            try
            {
                int action = package.ReadInt(); var target = package.ReadZDOID(); var award = package.ReadZDOID();
                if (target.IsNone() || award.IsNone() || award.UserID != owner) return;
                Applications.TryGetValue(target, out var entries);
                if (action == Register)
                {
                    long sequence = package.ReadLong(); var caster = package.ReadZDOID(); long casterOwner = package.ReadLong(); float remaining = package.ReadSingle();
                    if (sequence <= connection.Registration) return;
                    connection.Registration = sequence;
                    if (float.IsNaN(remaining) || float.IsInfinity(remaining) || remaining <= 0f || !Receiver(caster, casterOwner, out var receiver)) return;
                    var data = ZDOMan.instance.GetZDO(target);
                    var prefab = data == null ? null : ZNetScene.instance.GetPrefab(data.GetPrefab());
                    if (!prefab || !prefab.GetComponent<Character>()) return;
                    if (entries == null) Applications[target] = entries = new Dictionary<ZDOID, Application>();
                    // Retire the previous cast, but retain a break still waiting for its ownership verification.
                    foreach (var old in entries.Where(pair => pair.Key != award && ReferenceEquals(pair.Value.Source, source) &&
                        (!pair.Value.Broken || pair.Value.Consumed)).Select(pair => pair.Key).ToArray()) entries.Remove(old);
                    if (!entries.TryGetValue(award, out var application)) entries[award] = application = new Application();
                    application.Caster = caster; application.CasterPeer = casterOwner; application.Receiver = receiver;
                    application.Owner = owner; application.Source = source; application.Expires = Time.realtimeSinceStartup + remaining + TransitGrace;
                    application.VerifyUntil = Time.realtimeSinceStartup + OwnershipWait;
                    application.Verified = OwnsTarget(target, owner);
                    Process(target, application);
                }
                else if (entries != null && entries.TryGetValue(award, out var application) && ReferenceEquals(application.Source, source))
                {
                    if (action == Break) application.Broken = true;
                    else if (action == Forget) application.Ended = true;
                    else return;
                    Process(target, application);
                    if (application.Ended && (!application.Broken || application.Consumed)) entries.Remove(award);
                    if (entries.Count == 0) Applications.Remove(target);
                }
            }
            catch (EndOfStreamException) { }
        }

        private static void Process(ZDOID target, Application application)
        {
            if (Time.realtimeSinceStartup > application.Expires) { application.Consumed = true; return; }
            if (!application.Verified && Time.realtimeSinceStartup <= application.VerifyUntil)
                application.Verified = OwnsTarget(target, application.Owner);
            if (!application.Verified || !application.Broken || application.Consumed) return;
            var data = ZDOMan.instance.GetZDO(target);
            bool authority = data?.GetOwner() == application.Owner || Time.realtimeSinceStartup - application.OwnerLeft <= TransitGrace ||
                data == null && application.DestroyedOwner == application.Owner && Time.realtimeSinceStartup - application.Destroyed <= TransitGrace;
            if (!authority) { application.Consumed = true; return; }
            application.Consumed = true;
            if (!Receiver(application.Caster, application.CasterPeer, out var receiver) || !ReferenceEquals(receiver, application.Receiver)) return;
            if (receiver == null) { RewardLocal(application.Caster); return; }
            var connection = Connections.GetValue(receiver, _ => new Connection());
            var package = new ZPackage(); package.Write(++connection.Delivery); package.Write(application.Caster);
            receiver.Invoke(Delivery, package);
        }

        private static void RewardLocal(ZDOID caster)
        {
            var player = Player.m_localPlayer;
            if (!player || player.GetZDOID() != caster || !player.IsOwner()) return;
            // SetLevel changes capacity and duration, not these definition-owned reward fields. No XP amount or
            // skill identifier is accepted from a peer; the caster runs the normal Skills path with its own modifiers.
            var definition = ObjectDB.instance.GetStatusEffect(ShieldHash) as SE_Shield;
            if (definition) player.GetSkills().RaiseSkill(definition.m_levelUpSkillOnBreak, definition.m_levelUpSkillFactor);
        }
        private static void OwnerChanging(ZDO __instance, long uid)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || __instance.GetOwner() == uid || !Applications.TryGetValue(__instance.m_uid, out var entries)) return;
            long previous = __instance.GetOwner();
            foreach (var application in entries.Values)
                if (application.Verified && application.Owner == previous) application.OwnerLeft = Time.realtimeSinceStartup;
        }
        private static void Destroying(ZDOID uid)
        {
            if (!ZNet.instance.IsServer() || !Applications.TryGetValue(uid, out var entries)) return;
            long owner = ZDOMan.instance.GetZDO(uid)?.GetOwner() ?? 0;
            foreach (var application in entries.Values) { application.DestroyedOwner = owner; application.Destroyed = Time.realtimeSinceStartup; }
        }
        private static void Cleanup(ZNet __instance)
        {
            if (!__instance.IsServer() || Time.realtimeSinceStartup < nextCleanup) return;
            nextCleanup = Time.realtimeSinceStartup + .25f;
            foreach (var target in Applications.Keys.ToArray())
            {
                if (!Applications.TryGetValue(target, out var entries)) continue;
                foreach (var pair in entries.ToArray())
                {
                    var application = pair.Value; Process(target, application);
                    if (Time.realtimeSinceStartup > application.Expires || !application.Verified && Time.realtimeSinceStartup > application.VerifyUntil ||
                        Time.realtimeSinceStartup - application.Destroyed > TransitGrace && !float.IsNegativeInfinity(application.Destroyed) ||
                        application.Source != null && !application.Source.IsConnected() || application.Ended && (!application.Broken || application.Consumed)) entries.Remove(pair.Key);
                }
                if (entries.Count == 0) Applications.Remove(target);
            }
        }
        private static void Disconnected(ZNetPeer peer)
        {
            foreach (var target in Applications.Keys.ToArray())
            {
                var entries = Applications[target];
                foreach (var award in entries.Where(pair => ReferenceEquals(pair.Value.Source, peer.m_rpc) || ReferenceEquals(pair.Value.Receiver, peer.m_rpc)).Select(pair => pair.Key).ToArray()) entries.Remove(award);
                if (entries.Count == 0) Applications.Remove(target);
            }
            Connections.Remove(peer.m_rpc);
        }
        private static void ClearSession()
        {
            Applications.Clear(); Shields = new ConditionalWeakTable<SE_Shield, Shield>(); Connections = new ConditionalWeakTable<ZRpc, Connection>();
            serverConnection = new Connection(); receivingConnection = null; lastDelivery = nextRegistration = 0; nextShield = 0; nextCleanup = 0f;
        }
    }
}
