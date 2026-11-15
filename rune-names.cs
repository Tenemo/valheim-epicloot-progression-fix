using System;
using System.Linq;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.ShardStones;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // An etched rune keeps EpicLoot's blank-runestone name and description, which hide the one enchantment it holds.
    internal static class RuneNames
    {
        private static readonly Regex Tags = new Regex("<[^>]*>");
        private static readonly Func<string, float, object[]> DisplayArguments =
            AccessTools.MethodDelegate<Func<string, float, object[]>>(AccessTools.Method(typeof(MagicItem), "GetDisplayArgs"));
        private static readonly Func<string, object[], string> FormatText =
            AccessTools.MethodDelegate<Func<string, object[], string>>(AccessTools.Method(typeof(MagicItem), "FormatDisplayText"));

        // Set runes are named and described by EpicLoot itself.
        internal static MagicItemEffect Effect(ItemDrop.ItemData item)
        {
            if (!RuneStacks.IsRune(item) || item.IsSetRune()) return null;
            var effects = item.GetMagicItem()?.Effects;
            return effects?.Count == 1 ? effects[0] : null;
        }

        internal static string Name(MagicItemEffect effect)
        {
            if (!MagicItemEffectDefinitions.TryGet(effect.EffectType, out var definition)) return null;
            string text = Tags.Replace(FormatText(definition.DisplayText, DisplayArguments(effect.EffectType, effect.EffectValue)), "").Trim();
            return text.Length == 0 ? null : "Rune: " + text;
        }

        internal static string Description(MagicItemEffect effect)
        {
            var fits = RuneFits.For(effect.EffectType);
            bool etchable = fits?.Etchable ?? true;
            const string etch = "etch it over one of an item's enchantments in the enchanting table's Runes tab; that uses up the rune.";
            string text;
            if (!ShardSocketManager.RuneSocketingEnabled) text = "Holds one enchantment. To use it, " + etch;
            else
            {
                string removal;
                switch (ShardSocketManager.GetRemovalPolicy(ShardType.None, effect))
                {
                    case SocketRemoval.BreakOnly: removal = "taking it out destroys it"; break;
                    case SocketRemoval.Locked: removal = "it can't be taken out"; break;
                    default: removal = "you can take it out again"; break;
                }
                text = "Holds one enchantment. Drag it into a socket on gear it fits; " + removal + "." +
                    (etchable ? " Or " + etch : " It can't be etched.");
            }
            if (fits == null) return text;
            var lines = fits.Rules.Concat(MagicItem.ShowEffectDetails ? fits.Details : Enumerable.Empty<string>());
            return text + "\n\nFits " + fits.Gear + "." + string.Concat(lines.Select(line => "\n" + line));
        }
    }

    [HarmonyPatch(typeof(ItemDataExtensions), nameof(ItemDataExtensions.GetDisplayName))]
    internal static class RuneDisplayName
    {
        private static void Postfix(ItemDrop.ItemData itemData, ref string __result)
        {
            var effect = RuneNames.Effect(itemData);
            if (effect != null) __result = RuneNames.Name(effect) ?? __result;
        }
    }

    [HarmonyPatch(typeof(ItemDataExtensions), nameof(ItemDataExtensions.GetDescription))]
    internal static class RuneDescription
    {
        private static void Postfix(ItemDrop.ItemData itemData, ref string __result)
        {
            var effect = RuneNames.Effect(itemData);
            if (effect != null) __result = RuneNames.Description(effect);
        }
    }

    // A rune is not a crafting material, and its rarity is already on its Rarity row.
    [HarmonyPatch]
    internal static class RuneMaterialLine
    {
        private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GatherHeader");
        private static void Postfix(object __instance)
        {
            if (RuneNames.Effect((ItemDrop.ItemData)MagicTooltipContext.Item.GetValue(__instance)) != null)
                ((ItemTooltipData)MagicTooltipContext.Data.GetValue(__instance)).MaterialRarity = -1;
        }
    }
}
