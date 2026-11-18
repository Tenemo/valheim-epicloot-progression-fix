using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using EpicLoot;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // A search field beside an open chest. Items whose name, enchantments, set name or set bonuses contain every typed word
    // are outlined; the chest's other items are dimmed. Unidentified items match by name only. Letters match with or without
    // accents, so "jorm" finds Jörmundling. The search stays while the player moves between chests and is cleared by
    // emptying the field or with the X button at its right end.
    internal static class ChestSearch
    {
        private const string FieldName = "ProgressionFixChestSearch", OutlineName = "ProgressionFixChestSearchMatch",
            ClearName = "ProgressionFixChestSearchClear";
        private const float DimmedAlpha = 0.25f, OutlineWidth = 3f;
        // White, since Quick Stack outlines favorites in yellow and EpicLoot colors items by rarity.
        private static readonly Color OutlineColor = Color.white;
        private static readonly Regex Tags = new Regex("<[^>]*>"), Placeholders = new Regex(@"\{[^}]*\}");
        private static readonly AccessTools.FieldRef<InventoryGrid, List<InventoryElement>> Elements = AccessTools.FieldRefAccess<InventoryGrid, List<InventoryElement>>("m_elements");

        private static TMP_InputField field;
        private static string[] words = Array.Empty<string>();
        private static string query = "";
        // The frame in which Enter or Escape took the keyboard from the field; that key stays the field's in this frame
        // and the next, whichever order the field and the game read it in.
        private static int keyLeftFrame = -10;
        // What the cells were last drawn for; the matches are worked out again only when one of these changes.
        private static Inventory shownInventory;
        private static InventoryElement shownFirst;
        private static int shownRevision = -1, shownElements;
        private static string shownQuery = "";
        private static readonly Dictionary<InventoryElement, (CanvasGroup Group, GameObject Outline)> Cells = new Dictionary<InventoryElement, (CanvasGroup, GameObject)>();

        internal static TMP_InputField Field => field;
        internal static bool Typing => field != null && (field.isFocused || Time.frameCount - keyLeftFrame <= 1);

        [HarmonyPatch(typeof(InventoryGui), "Awake")]
        private static class CreateField
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(InventoryGui __instance) => Create(__instance);
        }

        private static void Create(InventoryGui gui)
        {
            // A new login builds a new window, so a search from before logging out does not stay without its text.
            words = Array.Empty<string>();
            query = shownQuery = "";
            shownInventory = null;
            shownFirst = null;
            shownRevision = -1;
            keyLeftFrame = -10;
            Cells.Clear();
            var panel = gui.m_container;
            // The buttons' body font; the window title's font has capitals only.
            var template = gui.m_takeAllButton.GetComponentInChildren<TMP_Text>(true);
            var root = new GameObject(FieldName, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)root.transform;
            rect.SetParent(panel, false);
            // Beside the window's title row, outside its right edge: the HUD's bars sit under the window and Quick Stack's
            // buttons start lower down this edge.
            rect.anchorMin = rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(10f, -10f);
            rect.sizeDelta = new Vector2(240f, 36f);
            var background = root.GetComponent<Image>();
            var buttonImage = gui.m_takeAllButton.GetComponent<Image>();
            if (buttonImage)
            {
                background.sprite = buttonImage.sprite;
                background.type = Image.Type.Sliced;
            }
            background.color = new Color(0.6f, 0.6f, 0.6f, 1f);

            var area = new GameObject("Text Area", typeof(RectTransform), typeof(RectMask2D));
            var areaRect = (RectTransform)area.transform;
            areaRect.SetParent(rect, false);
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.offsetMin = new Vector2(10f, 2f);
            // The right end stays free for the X button.
            areaRect.offsetMax = new Vector2(-34f, -2f);

            TextMeshProUGUI Label(string name, Color color)
            {
                var label = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
                var labelRect = label.rectTransform;
                labelRect.SetParent(areaRect, false);
                labelRect.anchorMin = Vector2.zero;
                labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
                label.font = template.font;
                label.fontSharedMaterial = template.fontSharedMaterial;
                label.fontSize = 20f;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.color = color;
                label.raycastTarget = false;
                label.richText = false;
                return label;
            }
            var placeholder = Label("Placeholder", new Color(1f, 1f, 1f, 0.4f));
            placeholder.text = "Search";
            placeholder.fontStyle = FontStyles.Italic;
            var text = Label("Text", Color.white);

            field = root.AddComponent<TMP_InputField>();
            field.textViewport = areaRect;
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = TMP_InputField.LineType.SingleLine;
            // Typed angle brackets stay text instead of formatting it.
            field.richText = false;
            field.characterLimit = 40;
            field.targetGraphic = background;
            field.caretColor = Color.white;
            field.customCaretColor = true;
            field.selectionColor = new Color(1f, 1f, 1f, 0.3f);
            // Escape leaves the field with the search as typed; arrow keys move the caret, not to the window's buttons.
            field.restoreOriginalTextOnEscape = false;
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            field.onValueChanged.AddListener(Search);
            field.onSubmit.AddListener(_ => { keyLeftFrame = Time.frameCount; Unselect(); });
            field.onEndEdit.AddListener(_ => { if (field.wasCanceled) { keyLeftFrame = Time.frameCount; Unselect(); } });

            // A small X button at the field's right end, styled as the window's own buttons, shown while the field has text,
            // empties the search. It lies over the field rather than in it, so a click on it does not give the field the keyboard.
            var clear = new GameObject(ClearName, typeof(RectTransform), typeof(Image), typeof(Button));
            var clearRect = (RectTransform)clear.transform;
            clearRect.SetParent(panel, false);
            clearRect.anchorMin = clearRect.anchorMax = new Vector2(1f, 1f);
            clearRect.pivot = new Vector2(1f, 0.5f);
            clearRect.anchoredPosition = new Vector2(rect.anchoredPosition.x + rect.sizeDelta.x - 4f, rect.anchoredPosition.y - rect.sizeDelta.y / 2f);
            clearRect.sizeDelta = new Vector2(26f, 26f);
            var clearImage = clear.GetComponent<Image>();
            if (buttonImage)
            {
                clearImage.sprite = buttonImage.sprite;
                clearImage.type = Image.Type.Sliced;
                clearImage.color = buttonImage.color;
            }
            var cross = new GameObject("Text", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            var crossRect = cross.rectTransform;
            crossRect.SetParent(clearRect, false);
            crossRect.anchorMin = Vector2.zero;
            crossRect.anchorMax = Vector2.one;
            crossRect.offsetMin = crossRect.offsetMax = Vector2.zero;
            cross.font = template.font;
            cross.fontSharedMaterial = template.fontSharedMaterial;
            cross.fontSize = 18f;
            cross.alignment = TextAlignmentOptions.Center;
            cross.color = template.color;
            cross.raycastTarget = false;
            cross.text = "X";
            var button = clear.GetComponent<Button>();
            // Hover and press look as on Take All.
            button.targetGraphic = clearImage;
            button.transition = gui.m_takeAllButton.transition;
            button.colors = gui.m_takeAllButton.colors;
            button.spriteState = gui.m_takeAllButton.spriteState;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            button.onClick.AddListener(() =>
            {
                field.text = "";
                // A selected button would take the next Enter through the interface.
                var events = EventSystem.current;
                if (events && events.currentSelectedGameObject == clear) events.SetSelectedGameObject(null);
            });
            field.onValueChanged.AddListener(typed => clear.SetActive(typed.Length > 0));
            clear.SetActive(false);
        }

        // The field stays the interface's selected object after it gives up the keyboard, and the UI input module sends
        // the next Enter or Escape to it, which would make it take the keyboard again, as when Escape then closes the chest.
        private static void Unselect()
        {
            var events = EventSystem.current;
            if (field && events && events.currentSelectedGameObject == field.gameObject) events.SetSelectedGameObject(null);
        }

        // Dying or anything else that closes the inventory while the player types gives the keyboard back to the game.
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class LeaveOnClose
        {
            private static void Postfix()
            {
                if (!field) return;
                if (field.isFocused) field.DeactivateInputField();
                Unselect();
            }
        }

        private static void Search(string typed)
        {
            words = Fold(typed).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            query = string.Join(" ", words);
        }

        // Lowercase without accents; letters that are not an accented form of another, such as ł, become their nearest one.
        internal static string Fold(string text)
        {
            var folded = new StringBuilder(text.Length);
            foreach (char letter in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(letter) == UnicodeCategory.NonSpacingMark) continue;
                switch (letter)
                {
                    case 'ł': folded.Append('l'); break;
                    case 'ø': folded.Append('o'); break;
                    case 'đ': folded.Append('d'); break;
                    case 'ß': folded.Append("ss"); break;
                    case 'æ': folded.Append("ae"); break;
                    case 'œ': folded.Append("oe"); break;
                    default: folded.Append(letter); break;
                }
            }
            return folded.ToString();
        }

        // While the field has the keyboard, the game and every mod read keyboard keys and buttons as not pressed, so
        // typed letters, digits, Space, Enter and Escape reach only the field: E, Tab and Escape leave the chest open,
        // number keys use nothing and no hotkey runs. Mouse buttons stay live for clicks and Shift-clicks in the window.
        // These run after every other patch on the reads, including Jotunn's, which replaces button results with the
        // game's own.
        [HarmonyPatch]
        private static class TypedKeysStayInField
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { nameof(Input.GetKey), nameof(Input.GetKeyDown), nameof(Input.GetKeyUp) })
                {
                    yield return AccessTools.Method(typeof(Input), name, new[] { typeof(KeyCode) });
                    yield return AccessTools.Method(typeof(ZInput), name, new[] { typeof(KeyCode), typeof(bool) });
                }
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(KeyCode __0, ref bool __result)
            {
                if (__result && Typing && (__0 < KeyCode.Mouse0 || __0 > KeyCode.Mouse6)) __result = false;
            }
        }

        [HarmonyPatch]
        private static class TypedKeyNamesStayInField
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { nameof(Input.GetKey), nameof(Input.GetKeyDown), nameof(Input.GetKeyUp) })
                    yield return AccessTools.Method(typeof(Input), name, new[] { typeof(string) });
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(string __0, ref bool __result)
            {
                if (__result && Typing && !(__0 ?? "").StartsWith("mouse", StringComparison.OrdinalIgnoreCase)) __result = false;
            }
        }

        [HarmonyPatch]
        private static class TypedButtonsStayInField
        {
            private static readonly AccessTools.FieldRef<ZInput, Dictionary<string, ZInput.ButtonDef>> Buttons =
                AccessTools.FieldRefAccess<ZInput, Dictionary<string, ZInput.ButtonDef>>("m_buttons");

            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { nameof(ZInput.GetButton), nameof(ZInput.GetButtonDown), nameof(ZInput.GetButtonUp) })
                    yield return AccessTools.Method(typeof(ZInput), name, new[] { typeof(string) });
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(string __0, ref bool __result)
            {
                if (__result && Typing && !MouseBound(__0)) __result = false;
            }

            private static bool MouseBound(string name)
            {
                var input = ZInput.instance;
                if (input == null || name == null || !Buttons(input).TryGetValue(name, out var button)) return false;
                foreach (var binding in button.ButtonAction.bindings)
                    if (binding.effectivePath != null && binding.effectivePath.StartsWith("<Mouse>", StringComparison.Ordinal)) return true;
                return false;
            }
        }

        [HarmonyPatch]
        private static class TypingIsNoKeyPress
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.PropertyGetter(typeof(Input), nameof(Input.anyKey));
                yield return AccessTools.PropertyGetter(typeof(Input), nameof(Input.anyKeyDown));
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref bool __result)
            {
                if (__result && Typing) __result = false;
            }
        }

        [HarmonyPatch(typeof(Input), nameof(Input.inputString), MethodType.Getter)]
        private static class TypedTextStaysInField
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ref string __result)
            {
                if (!string.IsNullOrEmpty(__result) && Typing) __result = "";
            }
        }

        [HarmonyPatch(typeof(InventoryGrid), "UpdateGui")]
        private static class Highlight
        {
            [HarmonyAfter(PluginIds.BetterUi)]
            private static void Postfix(InventoryGrid __instance)
            {
                var gui = InventoryGui.instance;
                if (!gui || __instance != GameFields.ContainerGrid(gui)) return;
                var container = GameFields.CurrentContainer(gui);
                var inventory = container ? container.GetInventory() : null;
                if (inventory == null || __instance.GetInventory() != inventory) inventory = null;
                var elements = Elements(__instance);
                var first = elements.Count > 0 ? elements[0] : null;
                if (inventory == shownInventory && query == shownQuery && HoverPreviews.Inventories == shownRevision &&
                    first == shownFirst && elements.Count == shownElements) return;
                shownInventory = inventory;
                shownQuery = query;
                shownRevision = HoverPreviews.Inventories;
                shownFirst = first;
                shownElements = elements.Count;
                Draw(elements, inventory);
            }
        }

        private static void Draw(List<InventoryElement> elements, Inventory inventory)
        {
            bool searching = inventory != null && words.Length > 0;
            int width = inventory?.GetWidth() ?? 0;
            var matched = new Dictionary<int, bool>();
            if (searching)
                foreach (var item in inventory.GetAllItems())
                {
                    int slot = item.m_gridPos.y * width + item.m_gridPos.x;
                    if (!matched.ContainsKey(slot)) matched[slot] = Matches(item);
                }
            if (Cells.Count > elements.Count * 2) Prune(elements);
            foreach (var element in elements)
            {
                var at = element.Position;
                bool hit = false;
                bool used = searching && matched.TryGetValue(at.y * width + at.x, out hit);
                if (!Cells.TryGetValue(element, out var cell))
                {
                    if (!searching) continue;
                    Cells[element] = cell = (element.gameObject.AddComponent<CanvasGroup>(), Outline(element));
                }
                float alpha = used && !hit ? DimmedAlpha : 1f;
                if (!Mathf.Approximately(cell.Group.alpha, alpha)) cell.Group.alpha = alpha;
                if (cell.Outline.activeSelf != hit) cell.Outline.SetActive(hit);
            }
        }

        // A frame of four edges around the cell, above the item's icon and labels.
        private static GameObject Outline(InventoryElement element)
        {
            var frame = new GameObject(OutlineName, typeof(RectTransform));
            var rect = (RectTransform)frame.transform;
            rect.SetParent(element.transform, false);
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            foreach (var (min, max) in new[] { (new Vector2(0f, 0f), new Vector2(1f, 0f)), (new Vector2(0f, 1f), new Vector2(1f, 1f)),
                (new Vector2(0f, 0f), new Vector2(0f, 1f)), (new Vector2(1f, 0f), new Vector2(1f, 1f)) })
            {
                var edge = new GameObject("Edge", typeof(RectTransform)).AddComponent<Image>();
                var edgeRect = edge.rectTransform;
                edgeRect.SetParent(rect, false);
                edgeRect.anchorMin = min;
                edgeRect.anchorMax = max;
                edgeRect.sizeDelta = new Vector2(min.x == max.x ? OutlineWidth : 0f, min.y == max.y ? OutlineWidth : 0f);
                edgeRect.anchoredPosition = new Vector2(min.x == max.x ? (min.x == 0f ? 1f : -1f) * OutlineWidth / 2f : 0f,
                    min.y == max.y ? (min.y == 0f ? 1f : -1f) * OutlineWidth / 2f : 0f);
                edge.color = OutlineColor;
                edge.raycastTarget = false;
            }
            frame.transform.SetAsLastSibling();
            frame.SetActive(false);
            return frame;
        }

        private static void Prune(List<InventoryElement> elements)
        {
            var live = new HashSet<InventoryElement>(elements);
            var gone = new List<InventoryElement>();
            foreach (var element in Cells.Keys) if (!element || !live.Contains(element)) gone.Add(element);
            foreach (var element in gone) Cells.Remove(element);
        }

        private static bool Matches(ItemDrop.ItemData item)
        {
            string text = Searchable(item);
            foreach (string word in words) if (text.IndexOf(word, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        // The item's name and, for identified items, every enchantment line as its tooltip shows it, socketed runes'
        // included, the name of its set and the names of the set's bonuses, such as "Fire rate", without their values.
        internal static string Searchable(ItemDrop.ItemData item)
        {
            var text = new StringBuilder();
            var localization = Localization.instance;
            var magic = item.GetMagicItem();
            string baseName = localization.Localize(item.m_shared.m_name);
            // EpicLoot names some items after a rarity, such as "Legendary Hoe", which an upgrade keeps, and unidentified items
            // such as "Legendary Bow"; rarity alone matches nothing.
            text.Append(magic != null ? WithoutRarity(baseName) : baseName).Append('\n');
            if (magic != null) text.Append(WithoutRarity(localization.Localize(item.GetDisplayName()))).Append('\n');
            if (magic?.IsUnidentified != true)
            {
                if (magic != null)
                    foreach (var effect in magic.GetEffects(null, true))
                        if (effect != null && MagicItemEffectDefinitions.TryGet(effect.EffectType, out _))
                            text.Append(MagicItem.GetEffectText(effect, magic.Rarity, false, magic.LegendaryID)).Append('\n');
                var set = item.GetLegendarySetInfo();
                if (set != null)
                {
                    text.Append(localization.Localize(set.Name)).Append('\n');
                    foreach (var bonus in set.SetBonuses)
                        if (bonus?.Effect != null && MagicItemEffectDefinitions.TryGet(bonus.Effect.Type, out var definition))
                            text.Append(Placeholders.Replace(localization.Localize(definition.DisplayText), "")).Append('\n');
                }
                var mundane = item.m_shared.m_setStatusEffect;
                if (item.IsMundaneSetItem() && mundane != null && !string.IsNullOrEmpty(mundane.m_name))
                    text.Append(localization.Localize(mundane.m_name)).Append('\n');
            }
            return Fold(Tags.Replace(text.ToString(), ""));
        }

        private static string WithoutRarity(string name)
        {
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                string word = Localization.instance.Localize(EpicLoot.EpicLoot.GetRarityDisplayName(rarity));
                if (word.Length > 0 && name.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase)) return name.Substring(word.Length + 1);
            }
            return name;
        }
    }
}
