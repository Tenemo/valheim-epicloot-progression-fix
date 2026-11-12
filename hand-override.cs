using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EpicLoot;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Lets EpicLoot read a player's weapon effects as if other items were in their hands and another ammunition were
    // selected, for a weapon or ammunition in a tooltip as if it were equipped. Effects of other equipment count as
    // usual. Tooltip reads also evaluate the proposed set membership in a private cache; gameplay's deferred skill
    // awards retain their existing hand adjustment and actual equipped sets.
    [HarmonyPatch]
    internal static class HandOverride
    {
        internal sealed class Hands
        {
            internal Player Player;
            internal ItemDrop.ItemData Right, Left, Ammo;
            // Reported as equipped while set.
            internal ItemDrop.ItemData Equipped;
            internal bool? Parry;
            // Reads the player's effects with empty hands, which also leaves hand items out of set counts.
            internal bool WornOnly;
            internal readonly Dictionary<string, float?> PreviewValues = new Dictionary<string, float?>();
        }

        private static readonly Func<ItemDrop.ItemData, bool> IsWeapon =
            AccessTools.MethodDelegate<Func<ItemDrop.ItemData, bool>>(AccessTools.Method(typeof(MagicEffectsHelper), "IsWeapon"));
        internal static Hands Active;
        private static bool Preview(Player player) => Active != null && player && Active.Player == player && (Active.WornOnly || WeaponTooltipRange.Item != null);

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.IsItemEquiped)), HarmonyPrefix]
        private static bool Equipped(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
        {
            var hands = Active;
            if (hands != null && Preview(__instance as Player) && item != null)
            {
                if (item == hands.Right || item == hands.Left || item == hands.Ammo) { __result = true; return false; }
                if (item == GameFields.RightItem(__instance) || item == GameFields.LeftItem(__instance) || item == hands.Player.GetAmmoItem())
                { __result = false; return false; }
            }
            if (hands?.Equipped == null || item != hands.Equipped || __instance != hands.Player) return true;
            __result = true;
            return false;
        }

        // A weapon's effects leave out a weapon or torch in the other hand; find it in the overriding hands.
        [HarmonyPatch(typeof(MagicEffectsHelper), "GetIgnoreWeapon"), HarmonyPrefix]
        private static bool OtherHand(Player player, ItemDrop.ItemData equippedWeapon, ref ItemDrop.ItemData __result)
        {
            var hands = Active;
            if (hands == null || player != hands.Player) return true;
            __result = hands.Right == equippedWeapon && IsWeapon(hands.Left) ? hands.Left
                : hands.Left == equippedWeapon && IsWeapon(hands.Right) ? hands.Right
                : null;
            return false;
        }

        // Swap the effects of the items really in hand and the ammunition really selected for those of the override.
        // EpicLoot has already subtracted the item it was asked to ignore if it counts that item as equipped, so it is
        // skipped here; the one item it wrongly counts as equipped, a tooltip's, is added back.
        [HarmonyPatch(typeof(global::EpicLoot.PlayerExtensions), nameof(global::EpicLoot.PlayerExtensions.GetTotalActiveMagicEffectValue)), HarmonyPostfix]
        private static void SwapHands(Player player, string effectType, float scale, ItemDrop.ItemData ignoreThisItem, ref float __result)
        {
            var hands = Active;
            if (hands == null || player != hands.Player || Preview(player)) return;
            var right = GameFields.RightItem(player);
            var left = GameFields.LeftItem(player);
            var ammo = player.GetAmmoItem();
            __result += Value(hands.Right, right, left, ignoreThisItem, effectType, scale)
                + Value(hands.Left, right, left, ignoreThisItem, effectType, scale)
                + Value(hands.Ammo, ammo, null, ignoreThisItem, effectType, scale)
                - Value(right, hands.Right, hands.Left, ignoreThisItem, effectType, scale)
                - Value(left, hands.Right, hands.Left, ignoreThisItem, effectType, scale)
                - Value(ammo, hands.Ammo, null, ignoreThisItem, effectType, scale);
            if (ignoreThisItem != null && ignoreThisItem == hands.Equipped) __result += Value(ignoreThisItem, right, left, null, effectType, scale);
        }

        [HarmonyPatch(typeof(global::EpicLoot.PlayerExtensions), nameof(global::EpicLoot.PlayerExtensions.GetMagicEquipment)), HarmonyPostfix]
        private static void PreviewEquipment(Player player, List<ItemDrop.ItemData> __result)
        {
            if (!Preview(player)) return;
            __result.Remove(GameFields.RightItem(player));
            __result.Remove(GameFields.LeftItem(player));
            __result.Remove(player.GetAmmoItem());
            foreach (var item in new[] { Active.Right, Active.Left, Active.Ammo })
                if (item != null && item.IsMagic() && !__result.Contains(item)) __result.Add(item);
        }

        [HarmonyPatch(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.TryGetValue)), HarmonyPrefix]
        private static bool PreviewCache(Player player, string effect, ref float? value, ref bool __result)
        {
            if (!Preview(player) || effect == null) return true;
            __result = Active.PreviewValues.TryGetValue(effect, out value);
            return false;
        }

        [HarmonyPatch(typeof(EquipmentEffectCache), nameof(EquipmentEffectCache.Store)), HarmonyPrefix]
        private static bool StorePreview(Player player, string effect, float? value)
        {
            if (!Preview(player) || effect == null) return true;
            Active.PreviewValues[effect] = value;
            return false;
        }

        [HarmonyPatch(typeof(MagicEffectsHelper), nameof(MagicEffectsHelper.GetFiringWeaponAdjustment)), HarmonyPrefix]
        private static bool IgnorePreviousShot(Player player, ref float __result)
        {
            if (!Preview(player)) return true;
            __result = 0;
            return false;
        }

        [HarmonyPatch(typeof(MagicEffectsHelper), nameof(MagicEffectsHelper.GetActiveWeapon)), HarmonyPrefix]
        private static bool PreviewWeapon(Player player, ref ItemDrop.ItemData __result)
        {
            if (!Preview(player)) return true;
            __result = IsWeapon(Active.Right) ? Active.Right : IsWeapon(Active.Left) ? Active.Left : null;
            return false;
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.GetCurrentWeapon)), HarmonyPrefix]
        private static bool PreviewCurrentWeapon(Humanoid __instance, ref ItemDrop.ItemData __result)
        {
            if (!Preview(__instance as Player)) return true;
            var weapon = IsWeapon(Active.Right) ? Active.Right : IsWeapon(Active.Left) ? Active.Left : null;
            if (weapon == null) return true;
            __result = weapon;
            return false;
        }

        [HarmonyPatch(typeof(Humanoid), "GetCurrentBlocker"), HarmonyPrefix]
        private static bool PreviewBlocker(Humanoid __instance, ref ItemDrop.ItemData __result)
        {
            if (!Preview(__instance as Player)) return true;
            __result = Active.Left ?? __instance.GetCurrentWeapon();
            return false;
        }

        private static ItemDrop.ItemData PreviewLeftItem(Humanoid humanoid) =>
            Preview(humanoid as Player) ? Active.Left : GameFields.LeftItem(humanoid);

        // Duelist checks the native left-hand field directly, rather than GetCurrentBlocker.
        [HarmonyPatch(typeof(EpicLoot.MagicItemEffects.ModifyBlock), nameof(EpicLoot.MagicItemEffects.ModifyBlock.GetDuelistBlockBonus))]
        [HarmonyPatch(typeof(EpicLoot.MagicItemEffects.ModifyBlock), nameof(EpicLoot.MagicItemEffects.ModifyBlock.GetDuelistDeflectionBonus))]
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> PreviewDuelist(IEnumerable<CodeInstruction> instructions)
        {
            var left = AccessTools.Field(typeof(Humanoid), "m_leftItem");
            int changes = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.LoadsField(left))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(HandOverride), nameof(PreviewLeftItem));
                    changes++;
                }
                yield return instruction;
            }
            if (changes != 1) throw new InvalidOperationException("EpicLoot's Duelist hand check changed.");
        }

        [HarmonyPatch(typeof(EpicLoot.MagicItemEffects.ModifyParryWindow), "IsParry"), HarmonyPrefix]
        private static bool PreviewParry(ref bool __result)
        {
            if (!Preview(Player.m_localPlayer) || !Active.Parry.HasValue) return true;
            __result = Active.Parry.Value;
            return false;
        }

        private static readonly AccessTools.FieldRef<FieldInfo[]> ModifierSources =
            AccessTools.StaticFieldRefAccess<FieldInfo[]>(AccessTools.Field(typeof(Player), "s_equipmentModifierSourceFields"));

        [HarmonyPatch(typeof(Player), "GetEquipmentModifier"), HarmonyPostfix]
        private static void NativeHandModifiers(Player __instance, int index, ref float __result)
        {
            if (!Preview(__instance)) return;
            var field = ModifierSources()[index];
            float ValueOf(ItemDrop.ItemData item) => item == null ? 0f : (float)field.GetValue(item.m_shared);
            __result += ValueOf(Active.Right) + ValueOf(Active.Left)
                - ValueOf(GameFields.RightItem(__instance)) - ValueOf(GameFields.LeftItem(__instance));
        }

        // The effect value of an item that is not one of the two others and not ignored.
        private static float Value(ItemDrop.ItemData item, ItemDrop.ItemData other, ItemDrop.ItemData secondOther,
            ItemDrop.ItemData ignored, string effectType, float scale)
        {
            if (item == null || item == other || item == secondOther || item == ignored || !item.IsMagic(out var magicItem)) return 0f;
            return magicItem.GetTotalEffectValue(effectType, scale);
        }
    }
}
