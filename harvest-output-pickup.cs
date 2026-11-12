using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class HarvestOutputPickup
    {
        // Calls remember with the object the method's one Instantiate<T> creates.
        internal static IEnumerable<CodeInstruction> CaptureInstantiate<T>(IEnumerable<CodeInstruction> instructions, MethodInfo remember,
            string changed)
        {
            int changes = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!(instruction.operand is MethodInfo method) || method.DeclaringType != typeof(UnityEngine.Object) ||
                    method.Name != "Instantiate" || !method.IsGenericMethod || method.GetGenericArguments()[0] != typeof(T)) continue;
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Call, remember);
                changes++;
            }
            if (changes != 1) throw new InvalidOperationException(changed);
        }

        // On the station's owner: runs the native collection once per collector request, passing in the list its captured
        // outputs go to, hands those outputs to the collector and replies with their IDs. A repeated request gets the same
        // reply. Ownership can move after the collector sent its request; removing nothing then lets it stop at once.
        internal static void ReceiveCollection(ZNetView view, Dictionary<long, (int Request, ZDOID[] Outputs)> receipts, long sender,
            int request, string pickupRpc, Action<List<GameObject>> collect)
        {
            if (!view.IsValid()) return;
            if (receipts.TryGetValue(sender, out var previous) && request <= previous.Request)
            {
                if (request == previous.Request) Reply(previous.Outputs);
                return;
            }
            if (!view.IsOwner())
            {
                receipts[sender] = (request, Array.Empty<ZDOID>());
                Reply(Array.Empty<ZDOID>());
                return;
            }
            var outputs = new List<GameObject>();
            collect(outputs);
            var ids = PassToCollector(outputs, sender);
            receipts[sender] = (request, ids);
            Reply(ids);

            void Reply(ZDOID[] items)
            {
                var package = new ZPackage();
                package.Write(items.Length);
                foreach (var id in items) package.Write(id);
                view.InvokeRPC(sender, pickupRpc, request, package);
            }
        }

        // The output IDs of a collection reply.
        internal static List<ZDOID> ReadIds(ZPackage package)
        {
            var ids = new List<ZDOID>();
            int count = package.ReadInt();
            for (int index = 0; index < count; index++) ids.Add(package.ReadZDOID());
            return ids;
        }

        internal static ZDOID[] PassToCollector(List<GameObject> outputs, long collector)
        {
            var ids = new List<ZDOID>();
            foreach (var output in outputs)
            {
                var item = output.GetComponent<ItemDrop>();
                var data = output.GetComponent<ZNetView>().GetZDO();
                // Save native quantities and item data before handing the output to its collector.
                ItemDrop.SaveToZDO(item.m_itemData, data);
                data.SetOwner(collector);
                ZDOMan.instance.ForceSendZDO(data.m_uid);
                ids.Add(data.m_uid);
            }
            return ids.ToArray();
        }

        internal static IEnumerator Pickup(Player player, List<ZDOID> ids)
        {
            float deadline = Time.time + 5f;
            while (ids.Count > 0 && player != null && !player.IsDead() && ZNetScene.instance != null && Time.time < deadline)
            {
                foreach (var id in ids.ToArray())
                {
                    var output = ZNetScene.instance.FindInstance(id);
                    if (output == null) continue;
                    var item = output.GetComponent<ItemDrop>();
                    if (!item.CanPickup(false)) { item.RequestOwn(); continue; }
                    // Native pickup handles stacks and inventory limits; overflow stays on the ground.
                    player.Pickup(output, false, false);
                    ids.Remove(id);
                }
                if (ids.Count > 0) yield return null;
            }
        }
    }
}
