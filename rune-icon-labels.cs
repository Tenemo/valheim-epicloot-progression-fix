using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class RuneIconLabels
    {
        private static readonly Regex Words = new Regex("[A-Za-z]+");
        private static readonly Regex Tags = new Regex("<[^>]*>");
        private static readonly Regex ValueToken = new Regex(@"([+\-]?)\{(\d+)(?::([^}]+))?\}\s*(%|ms\b|s\b)?");
        private static readonly Func<string, float, object[]> DisplayArguments = AccessTools.MethodDelegate<Func<string, float, object[]>>(
            AccessTools.Method(typeof(MagicItem), "GetDisplayArgs"));
        private static readonly Dictionary<string, string> Codes = new Dictionary<string, string>();
        internal static int Generation { get; private set; }
        // Keep similar affixes legible: ordinary initials collide for skills, resistances and low-health variants.
        private static readonly Dictionary<string, string> DistinctCodes = new Dictionary<string, string>
        {
            { "Immovable", "IM" }, { "Indestructible", "IND" }, { "ModifyArmor", "AR" }, { "Apportation", "AP" },
            { "ModifyArmorLowHealth", "ARL" }, { "ModifyAttackSpeed", "AS" }, { "AddAxesSkill", "AXS" },
            { "ModifyAttackSpeedLowHealth", "ASL" }, { "ModifyBlockPower", "BA" }, { "ModifyBackstab", "BS" },
            { "Bloodlust", "BLT" }, { "ModifyBlockPowerLowHealth", "BAL" }, { "BulkUp", "BUL" },
            { "AddBluntDamage", "BLD" }, { "AddBlockingSkill", "BLS" }, { "AddBowsSkill", "BWS" },
            { "AddClubsSkill", "CLS" }, { "AddCrossbowsSkill", "CXS" }, { "AddCrafterSkills", "CFS" },
            { "ModifyDurability", "DU" }, { "ModifyDamageLowHealth", "ADL" }, { "Duelist", "DUL" },
            { "ModifyWispRange", "WR" }, { "IncreaseEitr", "EI" }, { "Executioner", "EX" },
            { "AddElementalResistancePercentage", "EDR" }, { "ModifyEitrRegenLowHealth", "ERL" },
            { "ModifyFireRate", "BFR" }, { "ModifyMagicFireRate", "MFR" }, { "AddFireResistancePercentage", "FDR" },
            { "AddFrostResistancePercentage", "FODR" }, { "IncreaseHealth", "HP" }, { "HeadHunter", "HH" },
            { "IncreaseHeatResistance", "HTR" }, { "AddHealthRegen", "FHR" }, { "ModifyHealthRegenLowHealth", "HRL" },
            { "AddFireDamage", "FD" }, { "AddFrostDamage", "FOD" }, { "LifeSteal", "LS" },
            { "LifeStealLowHealth", "LSL" }, { "Luck", "LCK" }, { "AddMovementSkills", "MVSK" },
            { "ModifyMovementSpeedLowHealth", "MSL" }, { "ModifyParry", "PAR" }, { "ModifyParryLowHealth", "PARL" },
            { "AddPoisonResistancePercentage", "PODR" }, { "AddPiercingResistancePercentage", "PIDR" },
            { "AddPhysicalResistancePercentage", "PHDR" }, { "ModifyProjectileSpeed", "PJS" },
            { "AddPolearmsSkill", "PLS" }, { "AddPickaxesSkill", "PKS" }, { "RecallWeapon", "RW" }, { "Riches", "RCH" },
            { "IncreaseStamina", "ST" }, { "Slow", "SL" }, { "SpellSword", "SB" }, { "AddSlashingDamage", "SLD" },
            { "ModifySummonDamage", "SMD" }, { "ModifyStaggerDuration", "STD" }, { "ModifyStaggerDamage", "SGD" },
            { "AddSlashingResistancePercentage", "SLDR" }, { "ModifyStaminaRegenLowHealth", "SRL" },
            { "AddSwordsSkill", "SWS" }, { "AddSpearsSkill", "SPS" }, { "Weightless", "WL" },
            { "Waterproof", "WP" }, { "Warmth", "WA" }, { "AutoMead", "AM" },
            { "ModifyAttackStaminaUse", "ASC" }, { "ModifySprintStaminaUse", "SSC" },
            { "ModifyDodgeStaminaUse", "DSC" }, { "ModifyJumpStaminaUse", "JSC" }, { "ModifyBlockStaminaUse", "BSC" },
            { "ModifyAttackEitrUse", "AEC" }, { "ModifyAttackHealthUse", "AHC" }, { "ModifyDrawStaminaUse", "BDSC" },
            { "AmmoConservation", "AC" }, { "DodgeBuff", "PFD" }, { "ModifyBuildDistance", "PLR" },
            { "ModifyDiscoveryRadius", "MER" }, { "IncreaseTreeDrop", "WD" }, { "QuickLearner", "SXP" },
            { "FreeBuild", "BWST" }, { "ReflectDamage", "THN" }, { "EitrWeave", "EPR" },
            { "ModifyPhysicalDamage", "PHD" }, { "ModifyElementalDamage", "ED" }, { "AddLightningDamage", "LD" },
            { "AddPoisonDamage", "POD" }, { "AddSpiritDamage", "SPD" }, { "AddLightningResistancePercentage", "LDR" },
            { "AddSpiritResistancePercentage", "SPDR" }, { "AddBluntResistancePercentage", "BDR" },
            { "AvoidDamageTakenLowHealth", "FEIL" }, { "ModifyLowHealth", "HCT" }, { "Paralyze", "PZ" }
        };
        private static readonly Dictionary<string, string> StrengthTemplates = new Dictionary<string, string>
        {
            { "AmmoConservation", "{0}%" }, { "SpellSword", "+{0}%" }, { "ModifyParryWindow", "+{0} ms" }
        };

        // Keep established abbreviations by default; explicit codes and strength templates follow the pack's wording.
        private static Dictionary<string, string> epicLootEnglish;
        private static readonly Regex Token = new Regex(@"\$(\w+)");

        private static string Template(MagicItemEffectDefinition definition)
        {
            if (epicLootEnglish == null)
            {
                var assembly = typeof(EpicLoot.EpicLoot).Assembly;
                using (var reader = new StreamReader(assembly.GetManifestResourceStream("EpicLoot.localizations.English.json")))
                    epicLootEnglish = JsonConvert.DeserializeObject<Dictionary<string, string>>(
                        Regex.Replace(reader.ReadToEnd(), @"^\s*\/\/.*$", "", RegexOptions.Multiline));
            }
            string own = Token.Replace(definition.DisplayText ?? "", match => epicLootEnglish.TryGetValue(match.Groups[1].Value, out string word) ? word : match.Value);
            string text = EnglishText.TryTranslate(own, out string english) ? english : Localization.instance.Localize(own);
            return Tags.Replace(text, "");
        }

        internal static string Code(string effectType)
        {
            if (Codes.TryGetValue(effectType, out string code)) return code;
            if (DistinctCodes.TryGetValue(effectType, out code)) return Codes[effectType] = code;
            if (!MagicItemEffectDefinitions.TryGet(effectType, out var definition)) return "?";
            string name = Template(definition).Split('{')[0].Trim(' ', '+', '-', ':', '(');
            if (name.Length == 0)
            {
                string token = "$mod_epicloot_me_" + effectType.ToLowerInvariant() + "_name";
                name = EnglishText.TryTranslate(token, out string english) ? english : Regex.Replace(effectType, "([a-z])([A-Z])", "$1 $2");
            }
            var words = Words.Matches(name).Cast<Match>().Select(match => match.Value).ToArray();
            code = words.Length == 1 ? words[0].Substring(0, Math.Min(3, words[0].Length)).ToUpperInvariant() :
                string.Concat(words.Select(word => char.ToUpperInvariant(word[0])));
            if (effectType.EndsWith("LowHealth", StringComparison.Ordinal)) code += "L";
            return Codes[effectType] = code;
        }

        internal static string Strength(MagicItemEffect effect)
        {
            if (!MagicItemEffectDefinitions.TryGet(effect.EffectType, out var definition) ||
                MagicItemEffectDefinitions.IsValuelessEffect(effect.EffectType)) return "";
            var token = ValueToken.Match(StrengthTemplates.TryGetValue(effect.EffectType, out string template) ? template : Template(definition));
            var arguments = DisplayArguments(effect.EffectType, effect.EffectValue);
            if (!token.Success) return effect.EffectValue.ToString("0.##", CultureInfo.InvariantCulture);
            int index = int.Parse(token.Groups[2].Value, CultureInfo.InvariantCulture);
            object value = index < arguments.Length ? arguments[index] : effect.EffectValue;
            string format = token.Groups[3].Success ? token.Groups[3].Value : "0.##";
            string number = AffixDisplayValues.Format(value, format, CultureInfo.InvariantCulture);
            string unit = token.Groups[4].Value;
            return token.Groups[1].Value + number + (unit == "ms" ? " ms" : unit);
        }

        internal static TMP_Text FontTemplate => InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<InventoryElement>().m_amount;
        internal static void Draw(Image icon, ItemDrop.ItemData item, TMP_Text template, TMP_Text quantity = null, int? displayedQuantity = null,
            bool restoreNativeQuantity = true)
        {
            if (icon == null) return;
            var badge = icon.GetComponent<RuneIconBadge>();
            if (!RuneStacks.IsRune(item) || item.GetMagicItem()?.Effects.Count != 1)
            {
                if (badge != null) badge.Hide();
                return;
            }
            if (badge == null) badge = icon.gameObject.AddComponent<RuneIconBadge>();
            badge.Draw(item.GetMagicItem().Effects[0], displayedQuantity ?? item.m_stack, template, quantity, restoreNativeQuantity);
        }
        internal static void ClearCodes() { Codes.Clear(); Generation++; }
    }

    internal sealed class RuneIconBadge : MonoBehaviour
    {
        private TMP_Text initials, strength, count, nativeQuantity;
        private bool nativeQuantityEnabled;
        private string effectType;
        private float value = float.NaN;
        private int generation = -1;
        private TMP_Text Label(TMP_Text template, string name, bool top)
        {
            var label = Instantiate(template, transform);
            label.name = name;
            label.raycastTarget = false;
            label.enabled = true;
            label.fontSize = top ? 16f : 14f;
            label.enableAutoSizing = false;
            label.fontStyle = FontStyles.Bold;
            label.color = Color.white;
            label.alignment = top ? TextAlignmentOptions.TopRight : TextAlignmentOptions.BottomLeft;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            var rect = label.rectTransform;
            rect.localScale = Vector3.one;
            rect.anchorMin = new Vector2(0f, top ? 1f : 0f);
            rect.anchorMax = new Vector2(1f, top ? 1f : 0f);
            rect.pivot = new Vector2(0f, top ? 1f : 0f);
            rect.offsetMin = new Vector2(1f, top ? -18f : 1f);
            rect.offsetMax = new Vector2(-1f, top ? -1f : 18f);
            label.gameObject.SetActive(true);
            return label;
        }
        internal void Draw(MagicItemEffect effect, int quantity, TMP_Text template, TMP_Text quantityLabel, bool restoreNativeQuantity)
        {
            if (initials == null) initials = Label(template, Plugin.Id + ".rune-code", true);
            if (strength == null) strength = Label(template, Plugin.Id + ".rune-strength", false);
            if (quantityLabel != null)
            {
                if (restoreNativeQuantity && nativeQuantity != quantityLabel)
                {
                    nativeQuantity = quantityLabel;
                    nativeQuantityEnabled = quantityLabel.enabled;
                }
                quantityLabel.enabled = false;
            }
            if (count == null)
            {
                count = Label(template, Plugin.Id + ".rune-quantity", false);
                count.alignment = TextAlignmentOptions.BottomRight;
                count.fontSize = 12f;
                count.enableAutoSizing = true;
                count.fontSizeMin = 9f;
                count.fontSizeMax = 12f;
                count.rectTransform.anchorMin = new Vector2(0.5f, 0f);
                strength.enableAutoSizing = true;
                strength.fontSizeMin = 9f;
                strength.fontSizeMax = 14f;
                strength.rectTransform.anchorMax = new Vector2(0.5f, 0f);
            }
            count.text = quantity.ToString(CultureInfo.InvariantCulture) + "/" + RuneStacks.Limit.ToString(CultureInfo.InvariantCulture);
            count.gameObject.SetActive(true);
            if (effectType != effect.EffectType || value != effect.EffectValue || generation != RuneIconLabels.Generation)
            {
                effectType = effect.EffectType;
                value = effect.EffectValue;
                generation = RuneIconLabels.Generation;
                initials.text = RuneIconLabels.Code(effectType);
                strength.text = RuneIconLabels.Strength(effect);
                bool milliseconds = strength.text.EndsWith(" ms", StringComparison.Ordinal);
                strength.rectTransform.anchorMax = new Vector2(milliseconds ? .6f : .5f, 0f);
                count.rectTransform.anchorMin = new Vector2(milliseconds ? .65f : .5f, 0f);
            }
            initials.gameObject.SetActive(true);
            strength.gameObject.SetActive(strength.text.Length > 0);
        }
        internal void Hide()
        {
            if (initials != null) initials.gameObject.SetActive(false);
            if (strength != null) strength.gameObject.SetActive(false);
            if (count != null) count.gameObject.SetActive(false);
            if (nativeQuantity != null)
            {
                nativeQuantity.enabled = nativeQuantityEnabled;
                nativeQuantity = null;
            }
        }
    }

    [HarmonyPatch(typeof(InventoryGrid), "UpdateGui", new[] { typeof(Player), typeof(ItemDrop.ItemData) })]
    internal static class RuneInventoryLabels
    {
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");
        [HarmonyAfter(PluginIds.BetterUi)]
        private static void Postfix(InventoryGrid __instance)
        {
            var inventory = __instance.GetInventory();
            foreach (var element in Elements(__instance))
            {
                var item = element.m_used ? inventory.GetItemAt(element.Position.x, element.Position.y) : null;
                // InventoryGrid sets quantity visibility for the current item on every update, including empty cells.
                RuneIconLabels.Draw(element.m_icon, item, element.m_amount, element.m_amount, restoreNativeQuantity: false);
            }
        }
    }

    [HarmonyPatch(typeof(HotkeyBar_UpdateIcons_Patch), nameof(HotkeyBar_UpdateIcons_Patch.UpdateIcons))]
    internal static class RuneHotbarLabels
    {
        private static readonly AccessTools.FieldRef<object, GameObject> Slot = AccessTools.FieldRefAccess<object, GameObject>(
            AccessTools.Field(AccessTools.Inner(typeof(HotkeyBar), "ElementData"), "m_go"));
        private static void Postfix(object element, ItemDrop.ItemData itemData)
        {
            var slot = Slot(element).transform;
            RuneIconLabels.Draw(slot.Find("icon").GetComponent<Image>(), itemData, RuneIconLabels.FontTemplate,
                slot.Find("amount").GetComponent<TMP_Text>());
        }
    }

    [HarmonyPatch(typeof(MultiSelectItemListElement), nameof(MultiSelectItemListElement.SetItem))]
    internal static class RuneEnchantingLabels
    {
        private static void Postfix(MultiSelectItemListElement __instance) =>
            RuneIconLabels.Draw(__instance.ItemIcon, __instance.GetListElement()?.GetItem(), RuneIconLabels.FontTemplate);
    }

    [HarmonyPatch(typeof(InventoryGui), "UpdateItemDrag")]
    internal static class RuneDraggedLabels
    {
        private static void Postfix(GameObject ___m_dragGo, ItemDrop.ItemData ___m_dragItem, int ___m_dragAmount)
        {
            if (___m_dragGo != null) RuneIconLabels.Draw(___m_dragGo.transform.Find("icon").GetComponent<Image>(), ___m_dragItem, RuneIconLabels.FontTemplate,
                ___m_dragGo.transform.Find("amount").GetComponent<TMP_Text>(), ___m_dragAmount);
        }
    }

    [HarmonyPatch(typeof(MagicItemEffectDefinitions), nameof(MagicItemEffectDefinitions.Initialize))]
    internal static class RuneDefinitionLabels
    {
        private static void Postfix() { RuneIconLabels.ClearCodes(); RuneStacks.DefinitionsChanged(); }
    }
}
