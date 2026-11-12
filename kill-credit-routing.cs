using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class KillCreditRouting
    {
        private const string Request = Plugin.Id + ".kill-credit.1";
        private static readonly FieldInfo Integers = AccessTools.Field(typeof(ZDOExtraData), "s_ints");
        private static readonly List<ZNet.PlayerInfo> NoRecipients = new();

        private sealed class Credit
        {
            internal string Enemy;
            internal int Boss, Modifiers, Attackers;
            internal bool Cheated;
            internal HashSet<int> Flags;
        }

        [HarmonyPatch(typeof(ZNet), "OnNewConnection"), HarmonyPostfix]
        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (!__instance.IsServer()) return;
            peer.m_rpc.Register<ZPackage>(Request, (rpc, package) =>
            {
                if (!peer.IsReady() || !rpc.IsConnected() || package.Size() > 65536) return;
                try
                {
                    var credit = new Credit { Enemy = package.ReadString(), Boss = package.ReadInt(), Modifiers = package.ReadInt(),
                        Attackers = package.ReadInt(), Cheated = package.ReadBool(), Flags = new HashSet<int>() };
                    int count = package.ReadInt();
                    if (count < 0 || count > 4096) return;
                    for (int index = 0; index < count; index++) credit.Flags.Add(package.ReadInt());
                    Deliver(credit);
                }
                catch (EndOfStreamException) { }
            });
        }

        private static bool Participated(Credit credit, string name) => credit.Flags.Contains((ZDOVars.s_attackers + name).GetStableHashCode());

        private static void Deliver(Credit credit)
        {
            var network = ZNet.instance;
            if (!network || !network.IsServer()) return;
            // Account plus character name is the connected identity, including between character objects. Prefer
            // the newest connection if a reconnect briefly leaves an older peer in the list.
            var delivered = new HashSet<(string Account, string Character)>();
            foreach (var peer in network.GetPeers().AsEnumerable().Reverse())
            {
                if (!peer.IsReady() || !peer.m_rpc.IsConnected() || !Participated(credit, peer.m_playerName) ||
                    !delivered.Add((peer.m_socket.GetHostName(), peer.m_playerName))) continue;
                Game.instance.RegisterKill(peer.m_uid, credit.Enemy, credit.Boss, (KillModifiers)credit.Modifiers, credit.Attackers, credit.Cheated);
            }
            // The local host's profile exists during respawn too. This calls the receiver directly; zero is its
            // sender argument, never a routed destination that could broadcast the credit.
            if (!network.IsDedicated() && Participated(credit, Game.instance.GetPlayerProfile().GetName()))
                Game.instance.RPC_RegisterKill(0L, credit.Enemy, credit.Boss, credit.Modifiers, credit.Attackers, credit.Cheated);
        }

        private static List<ZNet.PlayerInfo> Route(ZNet network, Character creature, bool cheated)
        {
            var data = GameFields.CharacterView(creature).GetZDO();
            var integers = (IDictionary)Integers.GetValue(null);
            var fields = integers[data.m_uid] as IEnumerable<KeyValuePair<int, int>>;
            var credit = new Credit { Enemy = creature.m_name, Boss = creature.BossOrder(), Modifiers = data.GetInt(ZDOVars.s_modifiers),
                Attackers = data.GetInt(ZDOVars.s_attackers), Cheated = cheated,
                Flags = fields == null ? new HashSet<int>() : new HashSet<int>(fields.Where(pair => pair.Value != 0).Select(pair => pair.Key)) };
            if (network.IsServer()) Deliver(credit);
            else
            {
                var peer = network.GetServerPeer();
                if (peer == null || !peer.IsReady() || !peer.m_rpc.IsConnected()) return NoRecipients;
                // Native RegisterKill already trusts the simulator's death result. Send its existing flags so the
                // server can resolve every current participant even when this simulator's player list is stale.
                var package = new ZPackage(); package.Write(credit.Enemy); package.Write(credit.Boss); package.Write(credit.Modifiers);
                package.Write(credit.Attackers); package.Write(credit.Cheated); package.Write(credit.Flags.Count);
                foreach (int flag in credit.Flags) package.Write(flag);
                peer.m_rpc.Invoke(Request, package);
            }
            return NoRecipients;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Recipients(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var list = AccessTools.Method(typeof(ZNet), nameof(ZNet.GetPlayerList));
            var register = AccessTools.Method(typeof(Game), nameof(Game.RegisterKill));
            int index = code.FindIndex(value => value.Calls(list)), delivery = code.FindIndex(value => value.Calls(register));
            if (index < 0 || index != code.FindLastIndex(value => value.Calls(list)) || delivery < 1 ||
                delivery != code.FindLastIndex(value => value.Calls(register)) || !code[delivery - 1].IsLdloc())
                throw new InvalidOperationException("The native death-credit recipient loop changed.");
            var character = new CodeInstruction(OpCodes.Ldarg_0); character.labels.AddRange(code[index].labels); code[index].labels.Clear();
            var cheated = new CodeInstruction(code[delivery - 1].opcode, code[delivery - 1].operand);
            code[index].opcode = OpCodes.Call; code[index].operand = AccessTools.Method(typeof(KillCreditRouting), nameof(Route));
            code.InsertRange(index, new[] { character, cheated });
            return code;
        }
    }
}
