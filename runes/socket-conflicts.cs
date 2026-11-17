using System.Collections.Generic;
using System.Linq;
using EpicLoot;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Weapon damage and other effects apply from rolled affixes and sockets alike, so two different effects never share an
    // item when either definition excludes the other, whichever holds them and whichever came first, two shardstones
    // included. A socketed Indestructible rune is the one exception: it overrides a rolled durability bonus. Matching
    // effects, duplicate sockets, shard stacking and exclusive shard categories keep EpicLoot's rules.
    [HarmonyPatch(typeof(ShardSocketManager))]
    internal static class SocketConflicts
    {
        private static bool Excludes(string effect, string other) =>
            effect != other && MagicItemEffectDefinitions.TryGet(effect, out var first) && MagicItemEffectDefinitions.TryGet(other, out var second) &&
            (first.Requirements?.ExclusiveEffectTypes?.Contains(other) == true || second.Requirements?.ExclusiveEffectTypes?.Contains(effect) == true);

        internal static MagicItemEffect Conflict(string effectType, IEnumerable<MagicItemEffect> retained) =>
            retained.FirstOrDefault(effect => effect != null && Excludes(effectType, effect.EffectType));

        // The rolled affixes a socketed effect must coexist with. Nothing drains an indestructible item, so a rolled durability
        // bonus stays beside an Indestructible rune; rolled together, or as two sockets, they still exclude each other.
        internal static IEnumerable<MagicItemEffect> RolledBeside(MagicItem magic, string socketedType) =>
            magic.Effects.Where(effect => socketedType != MagicEffectType.Indestructible || effect.EffectType != MagicEffectType.ModifyDurability);

        // EpicLoot's own conflict message, naming the retained effect.
        internal static string Describe(MagicItemEffect conflict, ItemRarity rarity) =>
            "$mod_epicloot_socket_conflict: " + MagicItem.GetEffectText(conflict, rarity, showRange: false);

        // What an augment or rune etch keeps besides its replacement: the other rolled affixes and every socketed rune.
        // Incompatible shardstones return when the replacement is chosen.
        internal static MagicItemEffect ReplacementConflict(string candidate, MagicItem magic, int replacedIndex) =>
            Conflict(candidate, magic.Effects.Where((effect, index) => index != replacedIndex).Concat(magic.Sockets
                .Where(socket => socket?.Effect != null && socket.ShardType == ShardType.None &&
                    !(candidate == MagicEffectType.ModifyDurability && socket.Effect.EffectType == MagicEffectType.Indestructible))
                .Select(socket => socket.Effect)));

        // The sockets an item must give back. Rolled affixes win, then runes and then shardstones in their saved order.
        // Missing prerequisites keep their former rule: dormant runes stay and unsupported shardstones return.
        internal static List<SocketedEffect> Returns(ItemDrop.ItemData item, MagicItem magic)
        {
            var kept = new List<MagicItemEffect>();
            var returns = new List<SocketedEffect>();
            foreach (var socket in magic.Sockets.Where(socket => socket != null).OrderBy(socket => socket.ShardType != ShardType.None))
            {
                if (!RerollShardstones.Compatible(item, magic, socket) || socket.Effect != null &&
                    Conflict(socket.Effect.EffectType, RolledBeside(magic, socket.Effect.EffectType).Concat(kept)) != null)
                    returns.Add(socket);
                else if (socket.Effect != null) kept.Add(socket.Effect);
            }
            return returns;
        }

        // Filling an empty socket, by drag, Ctrl-click or shortcut, keeps every occupant.
        [HarmonyPostfix, HarmonyPatch(nameof(ShardSocketManager.CanSocket))]
        private static void Insert(ItemDrop.ItemData equipment, ItemDrop.ItemData input, ref string reason, ref bool __result)
        {
            if (__result) __result = Fits(equipment, input, equipment.GetMagicItem().Sockets
                .Where(socket => socket?.Effect != null).Select(socket => socket.Effect), ref reason);
        }

        // Drag and shortcut swaps keep every occupant except the one being replaced.
        [HarmonyPostfix, HarmonyPatch(nameof(ShardSocketManager.CanCoexist))]
        private static void Swap(ItemDrop.ItemData equipment, ItemDrop.ItemData input, IEnumerable<ItemDrop.ItemData> coResident,
            ref string reason, ref bool __result)
        {
            if (!__result) return;
            var residents = new List<MagicItemEffect>();
            foreach (var resident in coResident)
                if (ShardSocketManager.ResolveSocketedEffect(equipment, resident, out var effect, out _, out _)) residents.Add(effect);
            __result = Fits(equipment, input, residents, ref reason);
        }

        private static bool Fits(ItemDrop.ItemData equipment, ItemDrop.ItemData input, IEnumerable<MagicItemEffect> sockets, ref string reason)
        {
            var magic = equipment.GetMagicItem();
            if (!ShardSocketManager.ResolveSocketedEffect(equipment, input, out var effect, out _, out _) || effect == null) return true;
            // A new occupant must do something here, whatever the rune or the stone's color and rarity resolve to.
            if (!EnchantmentApplicability.CheckingOccupants && EnchantmentApplicability.DeadReason(equipment, magic, effect.EffectType) is string dead)
            {
                reason = EnchantmentApplicability.Refusal(equipment, dead);
                return false;
            }
            var conflict = Conflict(effect.EffectType, RolledBeside(magic, effect.EffectType).Concat(sockets));
            if (conflict == null) return true;
            reason = Describe(conflict, magic.Rarity);
            return false;
        }
    }
}
