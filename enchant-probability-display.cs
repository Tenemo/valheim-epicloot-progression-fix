using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EpicLoot;
using EpicLoot.CraftingV2;
using EpicLoot.LegendarySystem;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(EnchantingUIController), "GetEnchantInfo")]
    internal static class EnchantProbabilityDisplay
    {
        internal const string Feature = "Enchant chances";
        internal const string Heading = "Chance on finished item";
        private static readonly Dictionary<string, int[]> Results = new Dictionary<string, int[]>();
        private static CancellationTokenSource cancellation;
        private static Task<int[]> work;
        private static string workKey;

        internal static string UnsupportedSelection()
        {
            if (!AugmentProbabilityDisplay.SelectionRulesSupported()) return "the native effect sampler is not supported";
            if (LootRoller.CheatEffectCount >= 1 || !string.IsNullOrEmpty(LootRoller.CheatForceUniqueID) || LootRoller.CheatForceUniqueRarity.HasValue)
                return "an Enchant generation cheat is active";
            var methods = new[] { "RollMagicItem", "RollUniqueInfo", "RollEffectCountPerRarity", "GetEffectCountsPerRarity" }
                .SelectMany(name => AccessTools.GetDeclaredMethods(typeof(LootRoller)).Where(m => m.Name == name))
                .Concat(new[] { AccessTools.Method(typeof(MagicItemEffectDefinitions), "GetAvailableEffects"),
                    AccessTools.Method(typeof(MagicItemEffectDefinition), "CheckRequirements"),
                    AccessTools.Method(typeof(UniqueLegendaryHelper), "GetAvailableUniques"), AccessTools.Method(typeof(UniqueLegendaryHelper), "GetAvailableSetPieces") });
            foreach (var method in methods)
            {
                var owners = Harmony.GetPatchInfo(method)?.Owners.Where(owner => owner != Plugin.Id).ToArray();
                if (owners?.Length > 0) return string.Join(", ", owners) + " changes " + method.Name;
            }
            var definitions = MagicItemEffectDefinitions.AllDefinitions.Values;
            if (definitions.Any(d => d.Requirements == null || !d.Requirements.ExclusiveSelf || d.Requirements.ExternalRequirements?.Count > 0))
                return "the effect definitions have unsupported repeat or external requirements";
            return null;
        }

        internal static void CheckSupport() => DisabledFeatures.Set(Feature, UnsupportedSelection());

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class CheckOnSpawn
        {
            private static void Postfix(Player __instance) { if (__instance == Player.m_localPlayer) CheckSupport(); }
        }

        internal static void Clear()
        {
            CancelWork(); work = null; workKey = null; Results.Clear();
        }
        private static void CancelWork()
        {
            var previous = cancellation;
            if (previous == null) return;
            previous.Cancel();
            if (work == null || work.IsCompleted) previous.Dispose();
            else _ = work.ContinueWith(_ => previous.Dispose());
            cancellation = null;
        }
        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Shutdown))]
        private static class EndSession { private static void Prefix() => Clear(); }

        internal static int[] Request(EnchantProbabilityInput input)
        {
            if (Results.TryGetValue(input.Key, out var ready))
            {
                if (workKey != input.Key) { CancelWork(); work = null; workKey = null; }
                return ready;
            }
            if (workKey != input.Key)
            {
                CancelWork();
                var tokenSource = new CancellationTokenSource(); cancellation = tokenSource;
                workKey = input.Key;
                work = Task.Run(() => EnchantProbabilities.Calculate(input.Numbers, tokenSource.Token));
            }
            if (!work.IsCompleted) return null;
            if (work.IsFaulted) throw work.Exception.GetBaseException();
            if (work.IsCanceled) return null;
            if (Results.Count >= 32) Results.Remove(Results.Keys.First());
            return Results[input.Key] = work.Result;
        }

        private static void Postfix(ItemDrop.ItemData item, ItemRarity _rarity, ref string __result)
        {
            if (!AugmentProbabilityDisplay.ShowChances) return;
            string marker = Localization.instance.Localize("$mod_epicloot_augment_availableeffects");
            int start = __result.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("The Enchant probability section was not found.");
            string prefix = __result.Substring(0, start);
            var refresh = EnchantingTableUI.instance?.GetComponentInChildren<EnchantProbabilityRefresh>(true);
            try
            {
                string unsupported = UnsupportedSelection();
                if (unsupported != null) throw new NotSupportedException(unsupported);
                var input = EnchantProbabilityInput.Capture(item, _rarity);
                var result = Request(input);
                refresh?.Record(item, _rarity, input.Key, result != null);
                if (result == null) { __result = prefix + Heading + "\nCalculating chances…"; return; }
                var text = new StringBuilder(prefix).AppendLine(Heading).AppendLine("An item can have several effects.");
                text.AppendLine("<color=" + global::EpicLoot.EpicLoot.GetRarityColor(_rarity) + ">");
                for (int i = 0; i < input.Effects.Length; i++)
                {
                    var effect = input.Effects[i];
                    text.AppendLine("‣ " + AugmentProbabilities.Format(result[i]) + " " + MagicEffectRarity.Decorate(effect,
                        MagicItem.GetEffectTextRange(effect, input.Ranges[i]), allowAnimation: true));
                }
                __result = text.Append("</color>").ToString();
                DisabledFeatures.Set(Feature, null);
            }
            catch (Exception error)
            {
                DisabledFeatures.Set(Feature, error.Message);
                __result = prefix + Heading + "\nChances unavailable for this selection.";
                refresh?.Record(item, _rarity, null, false);
            }
        }
    }

    public sealed class EnchantProbabilityRefresh : MonoBehaviour
    {
        private EnchantUI panel;
        private bool ready;
        private float nextRefresh;
        private string key, pressedKey;
        private ItemDrop.ItemData item, pressedItem;
        private ItemRarity rarity;
        private static readonly AccessTools.FieldRef<EnchantUI, ItemRarity> Rarity = AccessTools.FieldRefAccess<EnchantUI, ItemRarity>("_rarity");
        private static readonly AccessTools.FieldRef<EnchantingTableUIPanelBase, bool> Locked = AccessTools.FieldRefAccess<EnchantingTableUIPanelBase, bool>("_locked");
        private static readonly Func<ItemDrop.ItemData, ItemRarity, string> Info = AccessTools.MethodDelegate<Func<ItemDrop.ItemData, ItemRarity, string>>(AccessTools.Method(typeof(EnchantingUIController), "GetEnchantInfo"));
        private void Awake() => panel = GetComponent<EnchantUI>();
        internal void Record(ItemDrop.ItemData selected, ItemRarity selectedRarity, string inputKey, bool completed)
        { item = selected; rarity = selectedRarity; key = inputKey; ready = completed; }
        internal void Capture() { pressedKey = key; pressedItem = item; }
        internal bool StillMatches()
        {
            var selected = panel.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            return ready && pressedItem == selected && pressedKey != null && pressedKey == EnchantProbabilityInput.Capture(selected, Rarity(panel)).Key;
        }
        private void Update()
        {
            if (!AugmentProbabilityDisplay.ShowChances || !panel.isActiveAndEnabled || Locked(panel)) return;
            if (!ready) panel.MainButton.interactable = false;
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + .5f;
            var selected = panel.AvailableItems.GetSingleSelectedItem<InventoryItemListElement>()?.Item1.GetItem();
            if (selected == null) return;
            float scroll = panel.EnchantInfoScrollbar.value;
            bool wasReady = ready;
            string text = Info(selected, Rarity(panel));
            if (panel.EnchantInfo.text != text)
            {
                panel.EnchantInfo.text = text;
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)panel.EnchantInfo.transform.parent);
            }
            if (ready && !wasReady) panel.OnRarityChanged();
            panel.EnchantInfoScrollbar.value = scroll;
            if (!ready) panel.MainButton.interactable = false;
        }
        [HarmonyPatch(typeof(EnchantUI), nameof(EnchantUI.Awake))]
        private static class Attach { private static void Postfix(EnchantUI __instance) => __instance.gameObject.AddComponent<EnchantProbabilityRefresh>(); }
        [HarmonyPatch(typeof(EnchantingTableUIPanelBase), "OnMainButtonClicked")]
        private static class CaptureInputs
        {
            private static void Prefix(EnchantingTableUIPanelBase __instance)
            { if (__instance is EnchantUI panel) panel.GetComponent<EnchantProbabilityRefresh>()?.Capture(); }
        }
        [HarmonyPatch(typeof(EnchantUI), "DoMainAction")]
        private static class CheckInputs
        {
            private static bool Prefix(EnchantUI __instance)
            {
                if (!AugmentProbabilityDisplay.ShowChances || __instance.GetComponent<EnchantProbabilityRefresh>()?.StillMatches() != false) return true;
                __instance.Cancel();
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Enchant chances changed. Review them and try again.");
                return false;
            }
        }
    }
}
