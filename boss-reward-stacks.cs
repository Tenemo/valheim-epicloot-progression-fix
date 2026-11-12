using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Boss rewards and serving trays opt into the existing stack settings. Explicit limits also enable mead bases and feast
    // platters, whose shared names cache the individual servings' limit. Modified Wishbones and enchanted trays remain
    // individual, trays merge only at full durability, and enchanting panels take only a single Wishbone or tray.
    internal static class BossRewardStacks
    {
        private static Func<ItemDrop.ItemData.SharedData, bool> patchShared;
        private static Func<ItemDrop.ItemData.SharedData, int> defaultStack;
        private static Dictionary<string, int> stackSizes;
        private static readonly Func<object, object> CloneShared = AccessTools.MethodDelegate<Func<object, object>>(
            AccessTools.Method(typeof(object), "MemberwiseClone"));

        internal static bool IsWishbone(ItemDrop.ItemData item) => item?.m_shared?.m_name == "$item_wishbone";
        private static bool SingleUse(ItemDrop.ItemData item) => IsWishbone(item) || ServingTrayStacks.IsTray(item);
        private static bool ModifiedSingle(ItemDrop.ItemData item, MagicItem magic) => IsWishbone(item) ? Modified(magic) : magic != null;
        private static bool IsReward(ItemDrop.ItemData.SharedData shared) =>
            shared?.m_name == "$item_cryptkey" || shared?.m_name == "$item_wishbone" || shared?.m_name == "$item_bloodgoldkey";

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.ItemStacks, out var plugin)) return;
            var processing = plugin.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemProcessing", true);
            var defaults = plugin.Instance.GetType().Assembly.GetType("ItemStacksItemWeights.ItemDefaults", true);
            defaultStack = AccessTools.MethodDelegate<Func<ItemDrop.ItemData.SharedData, int>>(
                AccessTools.Method(defaults, "GetDefaultItemStack") ?? throw new MissingMethodException(defaults.FullName, "GetDefaultItemStack"));
            var shared = AccessTools.Method(processing, "PatchShared") ?? throw new MissingMethodException(processing.FullName, "PatchShared");
            var item = AccessTools.Method(processing, "PatchItem") ?? throw new MissingMethodException(processing.FullName, "PatchItem");
            var config = (AccessTools.Field(processing, "itemConfig") ?? throw new MissingFieldException(processing.FullName, "itemConfig")).GetValue(null);
            var sizes = AccessTools.Field(config.GetType(), "stackSize") ?? throw new MissingFieldException(config.GetType().FullName, "stackSize");
            stackSizes = (Dictionary<string, int>)sizes.GetValue(config);
            patchShared = AccessTools.MethodDelegate<Func<ItemDrop.ItemData.SharedData, bool>>(shared);
            var self = typeof(BossRewardStacks);
            harmony.Patch(shared, transpiler: new HarmonyMethod(self, nameof(IncludeBossRewards)));
            harmony.Patch(item, postfix: new HarmonyMethod(self, nameof(RefreshItem)));
            harmony.Patch(AccessTools.Method(typeof(global::EpicLoot.EpicLoot), "Nonstackable"),
                postfix: new HarmonyMethod(self, nameof(SingleTarget)));
            harmony.Patch(AccessTools.Method(typeof(MagicItemData), "StacksWith"),
                postfix: new HarmonyMethod(self, nameof(KeepModifiedSeparate)));
            harmony.Patch(AccessTools.Method(typeof(MagicItemComponent), nameof(MagicItemComponent.SetMagicItem)),
                postfix: new HarmonyMethod(self, nameof(MagicChanged)));
            foreach (string method in new[] { "GetEnchantableItems", "GetAugmentableItems", "GetRuneModifyableItems", "GetDisenchantItems" })
                harmony.Patch(AccessTools.Method(typeof(EnchantingUIController), method),
                    postfix: new HarmonyMethod(self, nameof(SingleTargets)));
            foreach (var panel in new[] { typeof(EnchantUI), typeof(AugmentUI), typeof(RuneUI), typeof(DisenchantUI) })
                harmony.Patch(AccessTools.Method(panel, "DoMainAction"),
                    prefix: new HarmonyMethod(self, nameof(RecheckSingleTarget)) { priority = Priority.First });
        }

        private static IEnumerable<CodeInstruction> IncludeBossRewards(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int replaced = 0;
            for (int index = 1; index < code.Count - 1; index++)
            {
                // PatchShared tests its cached original stack limit before consulting any stack configuration.
                if (code[index - 1].opcode != OpCodes.Ldloc_2 || code[index].opcode != OpCodes.Ldc_I4_1 ||
                    code[index + 1].opcode != OpCodes.Cgt) continue;
                code[index].opcode = OpCodes.Ldarg_0;
                code[index].operand = null;
                code[index + 1].opcode = OpCodes.Call;
                code[index + 1].operand = AccessTools.Method(typeof(BossRewardStacks), nameof(CanScale));
                replaced++;
            }
            if (replaced != 1) throw new InvalidOperationException("ItemStacksItemWeights' original stack-limit check changed.");
            return code;
        }

        private static bool CanScale(int originalLimit, ItemDrop.ItemData.SharedData shared) =>
            originalLimit > 1 || IsReward(shared) || shared.m_name == "$item_feaster" ||
            (shared.m_itemType == ItemDrop.ItemData.ItemType.Material &&
             stackSizes.ContainsKey(shared.m_name) &&
             (shared.m_name.StartsWith("$item_feast", StringComparison.Ordinal) ||
              shared.m_name.StartsWith("$item_meadbase", StringComparison.Ordinal) ||
              shared.m_name == "$item_barleywinebase"));

        // The game's own limit, which ItemStacksItemWeights records by item name before its settings raise it.
        internal static int GameStackLimit(ItemDrop.ItemData.SharedData shared) => defaultStack?.Invoke(shared) ?? shared.m_maxStackSize;

        internal static bool Modified(MagicItem magic) => magic != null &&
            (magic.Rarity != ItemRarity.Epic || magic.Effects.Count != 1 ||
             magic.Effects[0].EffectType != MagicEffectType.Wishbone || magic.Effects[0].EffectValue != 1f ||
             magic.SocketCount != 0 || magic.Sockets.Count != 0 ||
             !string.IsNullOrEmpty(magic.LegendaryID) || !string.IsNullOrEmpty(magic.SetID));

        private static void KeepModifiedSeparate(ItemDrop.ItemData existing, ItemDrop.ItemData item, ref bool __result)
        {
            if (!__result || existing == item) return;
            bool worn = ServingTrayStacks.IsTray(existing) && !ServingTrayStacks.Full(existing) || ServingTrayStacks.IsTray(item) && !ServingTrayStacks.Full(item);
            if (worn || SingleUse(existing) && ModifiedSingle(existing, existing.GetMagicItem()) || SingleUse(item) && ModifiedSingle(item, item.GetMagicItem()))
                __result = false;
        }

        private static void SingleTarget(ItemDrop.ItemData item, ref bool __result)
        {
            if (SingleUse(item)) __result = item.m_stack == 1;
        }

        private static void SingleTargets(List<InventoryItemListElement> __result) =>
            __result.RemoveAll(entry => SingleUse(entry.GetItem()) && entry.GetItem().m_stack != 1);

        private static bool RecheckSingleTarget(EnchantingTableUIPanelBase __instance)
        {
            var list = __instance switch
            {
                EnchantUI panel => panel.AvailableItems,
                AugmentUI panel => panel.AvailableItems,
                RuneUI panel => panel.AvailableItems,
                DisenchantUI panel => panel.AvailableItems,
                _ => throw new InvalidOperationException("Unexpected item modification panel.")
            };
            var item = list.GetSelectedItems<InventoryItemListElement>().FirstOrDefault()?.Item1.GetItem();
            if (!SingleUse(item) || item.m_stack == 1) return true;
            __instance.Cancel();
            list.DeselectAll();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                $"Separate one {Localization.instance.Localize(item.m_shared.m_name)} before modifying it.");
            return false;
        }

        private static bool LimitModified(ItemDrop.ItemData item, MagicItem magic)
        {
            if (!SingleUse(item) || !ModifiedSingle(item, magic) || item.m_shared.m_maxStackSize == 1) return false;
            // SharedData normally belongs to the prefab. A modified copy must not shrink ordinary copies' stacks.
            item.m_shared = (ItemDrop.ItemData.SharedData)CloneShared(item.m_shared);
            item.m_shared.m_maxStackSize = 1;
            return true;
        }

        private static void RefreshItem(ItemDrop.ItemData __0, ref int __result)
        {
            if (SingleUse(__0) && LimitModified(__0, __0.GetMagicItem())) __result = 1;
        }

        private static void MagicChanged(MagicItemComponent __instance)
        {
            var item = __instance.Item;
            if (!SingleUse(item)) return;
            patchShared(item.m_shared);
            LimitModified(item, __instance.MagicItem);
        }
    }
}
