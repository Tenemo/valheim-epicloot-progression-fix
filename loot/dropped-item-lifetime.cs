using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot.LootBeams;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game controlling a dropped item removes it once it is an hour of world time old, unless it lies inside a base,
    // a player is within 25 m, or it is in tar or part of a build. Sleeping skips the night's world time at once, so loot
    // dropped shortly before bed vanished together. The age limit is the synced lifetime instead; the rest stays native.
    [HarmonyPatch(typeof(ItemDrop), "TimedDestruction")]
    internal static class DroppedItemLifetime
    {
        internal const double NativeSeconds = 3600.0;

        internal static double Seconds() => Plugin.DroppedItemLifetimeHours.Value * 3600.0;

        // Valheim 1.0.17: if (!(GetTimeSinceSpawned() < 3600.0) && !IsInsideBase() && ...) m_nview.Destroy();
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = Transpilers.CallInstead(instructions, instruction => instruction.opcode == OpCodes.Ldc_R8 && Equals(instruction.operand, NativeSeconds),
                AccessTools.Method(typeof(DroppedItemLifetime), nameof(Seconds)), 1, "Valheim's dropped item lifetime changed; expected one 3600 s limit.");
            // UnownedExpiry checks the same conditions before it takes control of an item.
            var conditions = new[] {
                AccessTools.Method(typeof(ItemDrop), "GetTimeSinceSpawned"), AccessTools.Method(typeof(ItemDrop), "IsInsideBase"),
                AccessTools.Method(typeof(Player), nameof(Player.IsPlayerInRange), new[] { typeof(Vector3), typeof(float) }),
                AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.InTar)), AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.IsPiece)),
                AccessTools.Method(typeof(ZNetView), nameof(ZNetView.Destroy), Type.EmptyTypes) };
            if (conditions.Any(method => method == null || !code.Any(instruction => instruction.Calls(method))))
                throw new InvalidOperationException("Valheim's dropped item removal changed.");
            return code;
        }
    }

    // Only the game controlling an item runs that removal, and the server gives control of items only to a player within
    // about 1.5 zones of them, while a player's game draws loose items out to 160 m. An expired item between the two
    // stayed in view, with its loot beam, until a player came near. A game that has such an item loaded with nobody
    // controlling it now takes control and lets the game's own removal decide, then gives control back if the item stays.
    // Everything before taking control is a cheap check, and the area scan runs once a second per zone at most.
    [HarmonyPatch(typeof(ItemDrop), "SlowUpdate")]
    internal static class UnownedExpiry
    {
        private static readonly Func<ItemDrop, double> Age = AccessTools.MethodDelegate<Func<ItemDrop, double>>(AccessTools.Method(typeof(ItemDrop), "GetTimeSinceSpawned"));
        private static readonly Func<ItemDrop, bool> InsideBase = AccessTools.MethodDelegate<Func<ItemDrop, bool>>(AccessTools.Method(typeof(ItemDrop), "IsInsideBase"));
        private static readonly Action<ItemDrop> TimedDestruction = AccessTools.MethodDelegate<Action<ItemDrop>>(AccessTools.Method(typeof(ItemDrop), "TimedDestruction"));
        private static readonly Dictionary<Vector2s, KeyValuePair<bool, float>> readyZones = new Dictionary<Vector2s, KeyValuePair<bool, float>>();

        internal static bool Expired(ItemDrop item, ZNetView view) =>
            view && view.IsValid() && item.m_autoDestroy && !view.GetZDO().HasOwner() && Age(item) >= DroppedItemLifetime.Seconds();

        private static void Postfix(ItemDrop __instance, ZNetView ___m_nview)
        {
            var view = ___m_nview;
            if (ZNet.instance.IsDedicated()) return;
            if (!Expired(__instance, view)) { HeldLootBeam.Show(__instance); return; }
            var position = __instance.transform.position;
            if (InsideBase(__instance) || Player.IsPlayerInRange(position, 25f) || __instance.InTar() || __instance.IsPiece()) { HeldLootBeam.Show(__instance); return; }
            if (!AreaReady(position)) return;
            view.ClaimOwnership();
            TimedDestruction(__instance);
            if (!view.IsValid()) return;
            view.GetZDO().SetOwner(0L);
            HeldLootBeam.Show(__instance);
        }

        // A base whose pieces are still being created, after a teleport for example, has no base area around the item yet.
        private static bool AreaReady(Vector3 position)
        {
            var zone = ZoneSystem.GetZone(position);
            if (readyZones.TryGetValue(zone, out var known) && Time.time < known.Value) return known.Key;
            if (readyZones.Count > 64) readyZones.Clear();
            bool ready = ZNetScene.instance.IsAreaReady(position);
            readyZones[zone] = new KeyValuePair<bool, float>(ready, Time.time + 1f);
            return ready;
        }
    }

    // EpicLoot draws a magic item's loot beam as soon as a game creates the item, while the item's first check comes 1-2 s
    // later, or 10 s after that when the far side of its surroundings is still loading at the edge of those 160 m. So an
    // expired item there flashed its beam before it went. An expired item nobody controls shows its beam once a check keeps it.
    [HarmonyPatch(typeof(LootBeam), "Start")]
    internal static class HeldLootBeam
    {
        private static readonly HashSet<LootBeam> held = new HashSet<LootBeam>();

        private static void Postfix(LootBeam __instance)
        {
            if (ZNet.instance == null || ZNet.instance.IsDedicated()) return;
            var item = __instance.GetComponent<ItemDrop>();
            if (!item || !UnownedExpiry.Expired(item, item.GetComponent<ZNetView>())) return;
            if (held.Count >= 64) held.RemoveWhere(beam => !beam);
            held.Add(__instance);
            __instance.enabled = false;
        }

        internal static void Show(ItemDrop item)
        {
            if (held.Count == 0) return;
            var beam = item.GetComponent<LootBeam>();
            if (beam && held.Remove(beam)) beam.enabled = true;
        }
    }
}
