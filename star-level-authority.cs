using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class StarLevelAuthority
    {
        private const string Request = Plugin.Id + ".creature-change";
        private const string Approved = Plugin.Id + ".creature-change-approved";
        private const string StatRpc = "SLS_SetStat";
        private const string ApiRpc = "SLS_ApiRelay";
        private static MethodInfo receiveStat, replayApi, validateStat;
        private static Type statGroup, apiOperation, baseAttribute, levelAttribute, damageType, modifierType;
        private static IDictionary modifierDefinitions;
        private static readonly Func<ZNet, string, bool> IsAdmin = AccessTools.MethodDelegate<Func<ZNet, string, bool>>(
            AccessTools.Method(typeof(ZNet), "IsAdmin", new[] { typeof(string) }));

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            var stats = assembly.GetType("StarLevelSystem.modules.CreatureSetup.CreatureStatOverrides", true);
            var relay = assembly.GetType("StarLevelSystem.APIOwnerRelay") ?? assembly.GetTypes().Single(type => type.Name == "APIOwnerRelay");
            statGroup = assembly.GetType("StarLevelSystem.modules.CreatureSetup.StatGroup", true);
            apiOperation = relay.GetNestedType("Op", BindingFlags.Public | BindingFlags.NonPublic);
            baseAttribute = assembly.GetType("StarLevelSystem.common.DataObjects+CreatureBaseAttribute", true);
            levelAttribute = assembly.GetType("StarLevelSystem.common.DataObjects+CreaturePerLevelAttribute", true);
            damageType = assembly.GetType("StarLevelSystem.common.DataObjects+DamageType", true);
            modifierType = assembly.GetType("StarLevelSystem.common.DataObjects+ModifierType", true);
            var modifiers = assembly.GetType("StarLevelSystem.Data.CreatureModifiersData", true);
            modifierDefinitions = AccessTools.Field(modifiers, "ModifierDefinitions")?.GetValue(null) as IDictionary;
            receiveStat = AccessTools.Method(stats, "Receive");
            replayApi = AccessTools.Method(relay, "Replay");
            validateStat = AccessTools.Method(stats, "Validate");
            if (receiveStat == null || replayApi == null || validateStat == null || apiOperation == null || modifierDefinitions == null)
                throw new InvalidOperationException("StarLevelSystem's creature command transport changed.");
            var self = typeof(StarLevelAuthority);
            harmony.Patch(AccessTools.Method(stats.GetNestedType("RegisterSetStatRPC", BindingFlags.Public | BindingFlags.NonPublic), "Postfix"),
                postfix: new HarmonyMethod(self, nameof(RegisterStat)));
            harmony.Patch(AccessTools.Method(relay.GetNestedType("RegisterApiRelayRPC", BindingFlags.Public | BindingFlags.NonPublic), "Postfix"),
                postfix: new HarmonyMethod(self, nameof(RegisterApi)));
            harmony.Patch(AccessTools.Method(stats, "Send"), prefix: new HarmonyMethod(self, nameof(SendStat)));
            harmony.Patch(AccessTools.Method(relay, "Send"), prefix: new HarmonyMethod(self, nameof(SendApi)));
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        private static class Registration
        {
            private static void Postfix(ZNet __instance, ZNetPeer peer)
            {
                if (!StarLevelSystem.Installed) return;
                if (__instance.IsServer()) peer.m_rpc.Register<ZPackage>(Request, (rpc, package) =>
                {
                    if (peer.m_rpc == rpc && peer.IsReady()) ReceiveRequest(peer, package);
                });
                else if (peer.m_server) peer.m_rpc.Register<ZPackage>(Approved, (rpc, package) =>
                {
                    if (ZNet.instance?.GetServerPeer()?.m_rpc == rpc) ApplyApproved(package);
                });
            }
        }

        private static void RegisterStat(Character __0) => Register(__0, StatRpc);
        private static void RegisterApi(Character __0) => Register(__0, ApiRpc);

        private static void Register(Character character, string name)
        {
            var view = character ? character.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid() || character.IsPlayer()) return;
            view.Unregister(name);
            // Routed RPCs carry a caller-supplied sender ID. Remote mutations now use the authenticated peer channel;
            // keep the old names registered so an obsolete or forged call cannot reach the upstream receiver.
            view.Register<ZPackage>(name, (sender, package) => { });
        }

        private static bool SendStat(Character __0, object __1, int __2, bool __3, float __4)
        {
            var view = __0 ? __0.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid()) return false;
            var package = new ZPackage();
            package.Write(Convert.ToInt32(__1)); package.Write(__2); package.Write(__3); package.Write(__4);
            Submit(view.GetZDO().m_uid, true, package);
            return false;
        }

        private static bool SendApi(ZNetView __0, int __1, object __2, ZPackage __3)
        {
            if (!__0 || !__0.IsValid()) return false;
            var package = new ZPackage();
            package.Write(__1); package.Write(Convert.ToInt32(__2)); package.Write(__3);
            Submit(__0.GetZDO().m_uid, false, package);
            return false;
        }

        private static void Submit(ZDOID id, bool command, ZPackage change)
        {
            if (!ValidPackage(command, change) || ZNet.instance == null || ZRoutedRpc.instance == null) return;
            var envelope = new ZPackage();
            envelope.Write(id); envelope.Write(command); envelope.Write(change);
            if (ZNet.instance.IsServer()) ReceiveRequest(null, new ZPackage(envelope.GetArray()));
            else
            {
                var server = ZNet.instance.GetServerPeer();
                if (server != null) server.m_rpc.Invoke(Request, envelope);
            }
        }

        private static void ReceiveRequest(ZNetPeer peer, ZPackage envelope)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ZDOID id;
            bool command;
            ZPackage change;
            try { id = envelope.ReadZDOID(); command = envelope.ReadBool(); change = envelope.ReadPackage(); }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException) { return; }
            var data = ZDOMan.instance.GetZDO(id);
            var prefab = data == null || ZNetScene.instance == null ? null : ZNetScene.instance.GetPrefab(data.GetPrefab());
            var character = prefab ? prefab.GetComponent<Character>() : null;
            if (!character || character.IsPlayer() || !ValidPackage(command, change)) return;
            bool localServer = peer == null;
            long sender = localServer ? ZDOMan.GetSessionID() : peer.m_uid;
            if (!localServer && (!peer.IsReady() || !peer.m_rpc.IsConnected() || peer.m_server || sender == ZDOMan.GetSessionID())) return;
            bool admin = localServer || IsAdmin(ZNet.instance, peer.m_socket.GetHostName());
            // API changes follow native simulation authority, including a nearby eligible simulator during handoff.
            // Console stat changes require the server's admin list even when the requesting player owns the creature.
            if (!admin && (command || data.GetOwner() != sender && !CreatureOwnership.CanOwn(data, sender))) return;
            if (!data.HasOwner()) data.SetOwner(ZDOMan.GetSessionID());
            var approved = new ZPackage();
            approved.Write(id); approved.Write(command); approved.Write(change);
            if (command || data.GetOwner() == ZDOMan.GetSessionID()) ApplyApproved(new ZPackage(approved.GetArray()));
            foreach (var recipient in ZNet.instance.GetPeers())
                if (recipient.IsReady() && recipient.m_rpc.IsConnected() && (command || recipient.m_uid == data.GetOwner()))
                    recipient.m_rpc.Invoke(Approved, approved);
        }

        private static void ApplyApproved(ZPackage envelope)
        {
            ZDOID id;
            bool command;
            ZPackage change;
            try { id = envelope.ReadZDOID(); command = envelope.ReadBool(); change = envelope.ReadPackage(); }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException) { return; }
            var instance = ZNetScene.instance == null ? null : ZNetScene.instance.FindInstance(id);
            var character = instance ? instance.GetComponent<Character>() : null;
            if (!character || character.IsPlayer() || !ValidPackage(command, change)) return;
            (command ? receiveStat : replayApi).Invoke(null, new object[] { character, change });
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool ValidStat(int group, int stat, float value, bool reset)
        {
            if (!Enum.IsDefined(statGroup, group) || !Finite(value)) return false;
            var type = group == 0 ? baseAttribute : group == 1 ? levelAttribute : damageType;
            return Enum.IsDefined(type, stat) && (reset || validateStat.Invoke(null, new object[] { Enum.ToObject(statGroup, group), stat, value }) == null);
        }

        private static bool ValidPackage(bool command, ZPackage package)
        {
            int position = package.GetPos();
            package.SetPos(0);
            try
            {
                if (command)
                {
                    int group = package.ReadInt(), stat = package.ReadInt();
                    bool reset = package.ReadBool();
                    return ValidStat(group, stat, package.ReadSingle(), reset);
                }
                int hops = package.ReadInt(), operation = package.ReadInt();
                if (hops < 0 || hops > 3 || !Enum.IsDefined(apiOperation, operation)) return false;
                var arguments = package.ReadPackage();
                switch (operation)
                {
                    case 0: return arguments.ReadInt() > 0;
                    case 1: arguments.ReadBool(); return true;
                    case 2: case 4: case 6: case 8:
                        return ValidStat((operation - 2) / 2, arguments.ReadInt(), arguments.ReadSingle(), false);
                    case 3: case 5: case 7: case 9:
                        int group = (operation - 3) / 2, count = arguments.ReadInt();
                        int maximum = Enum.GetValues(group == 0 ? baseAttribute : group == 1 ? levelAttribute : damageType).Length;
                        if (count < 0 || count > maximum) return false;
                        var seen = new HashSet<int>();
                        for (int index = 0; index < count; index++)
                        {
                            int stat = arguments.ReadInt();
                            if (!seen.Add(stat) || !ValidStat(group, stat, arguments.ReadSingle(), false)) return false;
                        }
                        return true;
                    case 10: return true;
                    case 11:
                        string name = arguments.ReadString();
                        int rarity = arguments.ReadInt();
                        arguments.ReadBool();
                        return modifierDefinitions.Contains(name) && Enum.IsDefined(modifierType, rarity);
                    case 12: return modifierDefinitions.Contains(arguments.ReadString());
                    case 13:
                        string modifier = arguments.ReadString();
                        int values = arguments.ReadInt();
                        if (!modifierDefinitions.Contains(modifier) || values <= 0 || values > (arguments.Size() - arguments.GetPos()) / 5) return false;
                        var keys = new HashSet<string>(StringComparer.Ordinal);
                        for (int index = 0; index < values; index++)
                        {
                            string key = arguments.ReadString();
                            if (string.IsNullOrEmpty(key) || !keys.Add(key) || !Finite(arguments.ReadSingle())) return false;
                        }
                        return true;
                    case 14:
                        string cleared = arguments.ReadString();
                        bool oneKey = arguments.ReadBool();
                        string clearedKey = arguments.ReadString();
                        return modifierDefinitions.Contains(cleared) && (!oneKey || !string.IsNullOrEmpty(clearedKey));
                    default: return false;
                }
            }
            catch (Exception error) when (error is EndOfStreamException || error is IOException || error is ArgumentException || error is OverflowException) { return false; }
            finally { package.SetPos(position); }
        }
    }
}
