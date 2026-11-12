using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.Crafting;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(EnchantingUIController), "GetAvailableAugmentEffects")]
    internal static class AugmentProbabilityDisplay
    {
        private const string VerifiedUnity = "4d161e15d8ccdb32eb73262e7a3e0a66f8c175b50a38e22ae5b0e8fb9aea98f3";
        internal const string Feature = "Augment chances";
        private static readonly ConfigEntry<bool> Enabled = (ConfigEntry<bool>)AccessTools.Field(
            AccessTools.TypeByName("EpicLoot.Config.ELConfig"), "ShowEnchantSelectionChance").GetValue(null);
        private static readonly Lazy<bool> VerifiedSampler = new Lazy<bool>(() =>
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.Is64BitProcess &&
                Matches(Path.Combine(Paths.GameRootPath, "UnityPlayer.dll"), VerifiedUnity);
        });
        private static readonly Task Verification = Task.Run(() => _ = VerifiedSampler.Value);
        private static bool Matches(string path, string expected)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var hash = SHA256.Create())
                    return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant() == expected;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        // EpicLoot's own build needs no check here: PluginIds.TestedVersions stops the whole add-on on any other version.
        internal static bool SelectionRulesSupported() => UnsupportedSelection() == null;

        private static string UnsupportedSelection()
        {
            Verification.GetAwaiter().GetResult();
            if (!VerifiedSampler.Value) return "this game build's random numbers are not the verified Unity Windows x64 ones";
            if (LootRoller.CheatEffectRarityTier > 0) return "EpicLoot's effect rarity cheat is on";
            var methods = new[] { nameof(LootRoller.RollAugmentEffects), nameof(LootRoller.RollEffects), nameof(LootRoller.RollEffect) }
                .Select(name => AccessTools.Method(typeof(LootRoller), name))
                .Concat(AccessTools.GetDeclaredMethods(typeof(WeightedRandomCollection<MagicItemEffectDefinition>))
                    .Where(method => method.Name == "Roll" || method.Name == "Setup"));
            foreach (var method in methods)
            {
                var others = Harmony.GetPatchInfo(method)?.Owners.Where(owner => owner != Plugin.Id).ToList();
                if (others?.Count > 0) return string.Join(", ", others) + " patches " + method.DeclaringType.Name + "." + method.Name;
            }
            return null;
        }

        // Checked when the local player spawns too, so a build or mod that turns the chances off fails mod-compatibility
        // before anyone opens the Augment tab.
        internal static void CheckSupport() => DisabledFeatures.Set(Feature, !Enabled.Value || SelectionRulesSupported() ? null :
            UnsupportedSelection() ?? "the augment selection rules are not supported");

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class CheckOnSpawn
        {
            private static void Postfix(Player __instance) { if (__instance == Player.m_localPlayer) CheckSupport(); }
        }
        private static string workKey, reportedError;
        private static Task<int[]> work;
        internal static bool ShowChances => Enabled.Value;
        internal static string Heading { get; private set; } = "Appearance chances";

        internal static int ChoiceCount(EnchantingTable table)
        {
            float value = table.GetFeatureCurrentValue(EnchantingFeature.Augment).Item1;
            return float.IsNaN(value) ? 2 : (int)value;
        }

        internal static List<MagicItemEffectDefinition> Pool(ItemDrop.ItemData item, int selected) =>
            AffixReplacements.AugmentEffects(item, item.GetMagicItem(), selected, true, false, false, selected);

        internal static string InputKey(List<MagicItemEffectDefinition> pool, int count) => VerifiedUnity + ":" + count + ":" + string.Join("|",
            pool.Select(definition => definition.Type.Length + ":" + definition.Type + ":" + BitConverter.ToInt32(BitConverter.GetBytes(definition.SelectionWeight), 0) + ":" +
                MagicItemEffectDefinitions.IsValuelessEffect(definition.Type)));

        [HarmonyPatch(typeof(EnchantingUIController), "GetAugmentableItems")]
        private static class Prewarm
        {
            private static void Postfix(List<InventoryItemListElement> __result)
            {
                var table = EnchantingTableUI.instance?.SourceTable;
                if (!Enabled.Value || !table || !SelectionRulesSupported()) return;
                int count = ChoiceCount(table);
                foreach (var row in __result)
                {
                    var item = row.GetItem(); var effects = item.GetMagicItem().Effects;
                    for (int selected = 0; selected < effects.Count; selected++)
                    {
                        if (!MagicItemEffectDefinitions.TryGet(effects[selected].EffectType, out var effect) || !effect.CanBeAugmented) continue;
                        var pool = Pool(item, selected);
                        AugmentProbabilityCache.Get(InputKey(pool, count), pool.Select(value => value.SelectionWeight).ToArray(),
                            pool.Select(value => MagicItemEffectDefinitions.IsValuelessEffect(value.Type)).ToArray(), count, false);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class EndSession
        {
            private static void Prefix() => AugmentProbabilityCache.Clear();
        }

        private static bool Prefix(ItemDrop.ItemData item, int augmentindex, ref string __result)
        {
            // Even with percentages disabled, use the pack's authoritative pool and native effect text.
            __result = Render(item, augmentindex);
            EnchantingTableUI.instance?.GetComponentInChildren<AugmentProbabilityRefresh>(true)?.SetReady(false);
            return false;
        }

        internal static string Render(ItemDrop.ItemData item, int selected)
        {
            var magic = item?.GetMagicItem();
            var table = EnchantingTableUI.instance?.SourceTable;
            if (magic == null || selected < 0 || selected >= magic.Effects.Count || table == null) return "";
            var pool = Pool(item, selected);
            string color = global::EpicLoot.EpicLoot.GetRarityColor(magic.Rarity);
            if (!Enabled.Value)
                return "<color=" + color + ">" + string.Join("\n", pool.Select(definition => "‣ " +
                    MagicItem.GetEffectTextRange(definition, definition.GetValuesForRarity(magic.Rarity)))) + "</color>";
            int count = ChoiceCount(table);
            var original = magic.Effects[selected];
            var text = new StringBuilder();
            string keep = EnchantCostsHelper.EffectIsDeprecated(original.EffectType) ? " (original shown, unavailable)" : " (keep original)";
            try
            {
                if (!SelectionRulesSupported()) throw new NotSupportedException(UnsupportedSelection() ?? "the augment selection rules are not supported");
                if (pool.Select(definition => definition.Type).Distinct().Count() != pool.Count)
                    throw new NotSupportedException("the augment pool contains duplicate effect definitions");
                DisabledFeatures.Set(Feature, null);
                var weights = pool.Select(definition => definition.SelectionWeight).ToArray();
                var removed = pool.Select(definition => MagicItemEffectDefinitions.IsValuelessEffect(definition.Type)).ToArray();
                string key = InputKey(pool, count);
                workKey = key;
                work = AugmentProbabilityCache.Get(key, weights, removed, count, true);
                int[] calculation = null;
                if (workKey == key && work.IsCompleted)
                {
                    if (work.IsFaulted) throw work.Exception.GetBaseException();
                    calculation = work.Result;
                }
                if (calculation == null) return null;
                Heading = "Chance to appear in choices";
                text.AppendLine("Up to " + count + " new choices.");
                text.AppendLine("<color=" + color + ">");
                text.AppendLine("‣ " + MagicItem.GetEffectText(original, magic.Rarity, showRange: false, legendaryID: magic.LegendaryID) + keep);
                // Keep the fresh-roll explanation directly under its guaranteed original, while preserving the native
                // order for the other effects. The sampler still uses the original pool order above.
                foreach (int index in Enumerable.Range(0, pool.Count).OrderBy(index => pool[index].Type != original.EffectType))
                {
                    var definition = pool[index];
                    string chance = AugmentProbabilities.Format(calculation[index]);
                    string description = MagicItem.GetEffectTextRange(definition, definition.GetValuesForRarity(magic.Rarity));
                    if (definition.Type == original.EffectType) text.AppendLine("  Fresh roll: " + chance + " " + description);
                    else text.AppendLine("‣ " + chance + " " + description);
                }
            }
            catch (Exception error)
            {
                Heading = "Available effects";
                string message = error.GetBaseException().Message;
                if (error is NotSupportedException) DisabledFeatures.Set(Feature, message);
                else if (reportedError != message)
                {
                    reportedError = message;
                    if (error is ArgumentException) Debug.LogWarning("[EpicLoot Progression Fix] Augment probabilities unavailable: " + message);
                    else Debug.LogError("[EpicLoot Progression Fix] Augment probabilities unavailable: " + error);
                }
                text.AppendLine(error is NotSupportedException ? "Chances unavailable in this game setup." : "Chances unavailable for this selection.");
                text.AppendLine("<color=" + color + ">");
                text.AppendLine("‣ " + MagicItem.GetEffectText(original, magic.Rarity, showRange: false, legendaryID: magic.LegendaryID) + " (current effect)");
                foreach (var definition in pool)
                    text.AppendLine("‣ " + MagicItem.GetEffectTextRange(definition, definition.GetValuesForRarity(magic.Rarity)));
            }
            text.Append("</color>");
            return text.ToString();
        }
    }

    // Native selection changes refresh immediately. Polling only the visible panel also catches table upgrades,
    // socket changes and completed background calculations without saving items, drawing randomness or moving the scroll.
    [HarmonyPatch(typeof(AugmentUI), nameof(AugmentUI.Awake))]
    public sealed class AugmentProbabilityRefresh : MonoBehaviour
    {
        private static readonly AccessTools.FieldRef<AugmentUI, int> Selected = AccessTools.FieldRefAccess<AugmentUI, int>("_augmentIndex");
        private static readonly AccessTools.FieldRef<EnchantingTableUIPanelBase, bool> Locked =
            AccessTools.FieldRefAccess<EnchantingTableUIPanelBase, bool>("_locked");
        private AugmentUI panel;
        private float nextRefresh;
        private ItemDrop.ItemData pressedItem;
        private int pressedSlot;
        private string pressedInputs;
        private bool pending;
        private CanvasGroup[] chanceContent;

        private static void Postfix(AugmentUI __instance) => __instance.gameObject.AddComponent<AugmentProbabilityRefresh>();

        private void Awake() => panel = GetComponent<AugmentUI>();

        [HarmonyPatch(typeof(AugmentUI), nameof(AugmentUI.OnAugmentIndexChanged))]
        private static class SelectionChanged
        {
            private static void Postfix(AugmentUI __instance)
            {
                var refresh = __instance.GetComponent<AugmentProbabilityRefresh>();
                if (!refresh) return;
                refresh.nextRefresh = 0f;
                refresh.Update();
            }
        }

        internal void SetReady(bool ready)
        {
            if (chanceContent == null)
                chanceContent = new[] { panel.AvailableEffectsHeader.gameObject, panel.AvailableEffectsText.gameObject, panel.AvailableEffectsScrollbar.gameObject }
                    .Select(element => element.GetComponent<CanvasGroup>() ?? element.AddComponent<CanvasGroup>()).ToArray();
            pending = !ready;
            foreach (var group in chanceContent)
            {
                group.alpha = ready ? 1f : 0f;
                group.interactable = ready;
                group.blocksRaycasts = ready;
            }
        }

        internal void Capture()
        {
            pressedItem = panel.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            pressedSlot = Selected(panel);
            pressedInputs = Inputs(pressedItem, pressedSlot);
        }

        private static string Inputs(ItemDrop.ItemData item, int selected)
        {
            var table = EnchantingTableUI.instance?.SourceTable;
            var magic = item?.GetMagicItem();
            if (table == null || magic == null || selected < 0 || selected >= magic.Effects.Count) return null;
            return magic.Effects[selected].EffectType.Length + ":" + magic.Effects[selected].EffectType + ":" + magic.Effects[selected].EffectValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ":" +
                AugmentProbabilityDisplay.InputKey(AugmentProbabilityDisplay.Pool(item, selected), AugmentProbabilityDisplay.ChoiceCount(table));
        }

        internal bool StillMatches()
        {
            if (pressedInputs == null) return true;
            var item = panel.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            return item == pressedItem && Selected(panel) == pressedSlot && Inputs(item, Selected(panel)) == pressedInputs;
        }

        private void Update()
        {
            if (panel == null || !panel.isActiveAndEnabled || Locked(panel) || !pending && Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + 0.2f;
            var item = panel.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            if (item == null || Selected(panel) < 0) return;
            string text = AugmentProbabilityDisplay.Render(item, Selected(panel));
            if (text == null) { SetReady(false); return; }
            if (panel.AvailableEffectsHeader != null && EnchantingTableUI.instance?.SourceTable != null)
                panel.AvailableEffectsHeader.text = AugmentProbabilityDisplay.ShowChances ? AugmentProbabilityDisplay.Heading :
                    Localization.instance.Localize("$mod_epicloot_augment_availableeffects ($mod_epicloot_augment_choices)",
                        AugmentProbabilityDisplay.ChoiceCount(EnchantingTableUI.instance.SourceTable).ToString());
            float position = panel.AvailableEffectsScrollbar.value;
            if (panel.AvailableEffectsText.text != text) panel.AvailableEffectsText.text = text;
            if (pending)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.AvailableEffectsText.transform.parent);
            }
            panel.AvailableEffectsScrollbar.value = position;
            SetReady(true);
        }
    }

    [HarmonyPatch(typeof(EnchantingTableUIPanelBase), "OnMainButtonClicked")]
    internal static class CaptureAugmentProbabilityInputs
    {
        private static void Prefix(EnchantingTableUIPanelBase __instance)
        {
            if (__instance is AugmentUI panel) panel.GetComponent<AugmentProbabilityRefresh>()?.Capture();
        }
    }

    [HarmonyPatch(typeof(AugmentUI), "DoMainAction")]
    internal static class CheckAugmentProbabilityInputs
    {
        private static bool Prefix(AugmentUI __instance)
        {
            if (__instance.GetComponent<AugmentProbabilityRefresh>()?.StillMatches() != false) return true;
            __instance.Cancel();
            __instance.OnAugmentIndexChanged();
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Augment choices changed. Review them and try again.");
            return false;
        }
    }
}
