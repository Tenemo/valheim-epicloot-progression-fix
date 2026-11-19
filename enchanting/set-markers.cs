using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using EpicLoot;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // EpicLoot's set marker also lit up for the game's own armor sets, such as Fenris. Only its own sets keep the marker.
    // Tooltips and set bonuses make their own IsSetItem calls, so they still see both kinds of set.
    [HarmonyPatch]
    internal static class EpicLootSetMarkers
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(API), nameof(API.ApplyMagicItemBackground));
            yield return AccessTools.Method(typeof(ItemBackgroundHelper), nameof(ItemBackgroundHelper.UpdateIconSetMarker));
        }

        private static List<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, instruction => instruction.Calls(AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.IsSetItem))),
                AccessTools.Method(typeof(ItemDataExtensions), nameof(ItemDataExtensions.IsMagicSetItem)), 1, "EpicLoot's set marker check changed.");
    }

    // Every set marks its pieces with its own Elder Futhark rune in the bottom-right corner of the slot, in place of the cross
    // all sets shared, so the bag, chests and the enchanting table's lists show which set a piece belongs to. The rune takes
    // EpicLoot's set item colour with a dark grey edge. Every rune of the Elder Futhark now marks a set, so a set this table
    // doesn't know gets a plain cross, which is no rune.
    internal static class SetRunes
    {
        // Strokes on a grid 9 wide and 14 high with y down, one rune per set, picked by the set's initial or its meaning.
        private const string UnknownSet = "M4.5,1 L4.5,13 M1,7 L8,7";
        private static readonly Dictionary<string, string> Strokes = new Dictionary<string, string>
        {
            ["Sif"] = "M4.5,1.5 L1,5 L4.5,8.5 M4.5,5.5 L8,9 L4.5,12.5", // Jera, the year's harvest
            ["Heimdall"] = "M1,0 L1,14 M8,0 L8,14 M1,5 L8,9", // Hagalaz
            ["Heidr"] = "M3,0 L3,14 M3,0 L8,4 M3,4.5 L8,8.5", // Ansuz, the god's word
            ["Hel"] = "M4.5,0 L4.5,14 M4.5,0 L8,3.5 M4.5,14 L1,10.5", // Eihwaz, the yew of death and rebirth
            ["HelCourt"] = "M4.5,0 L8,4.5 L1,11.5 L1,14 M4.5,0 L1,4.5 L8,11.5 L8,14", // Othala, the hall
            ["Huldra"] = "M2,0 L2,14 M2,0 L6,4 L8.5,1 M2,14 L6,10 L8.5,13", // Perthro, the hidden
            ["Hrungnir"] = "M4.5,0 L4.5,14 M1,5 L8,9", // Naudiz, standing firm
            ["Egil"] = "M1,14 L1,0 L4.5,4.5 L8,0 L8,14", // Ehwaz
            ["Einherjar"] = "M4.5,14 L4.5,0 M0.5,4.5 L4.5,0 L8.5,4.5", // Tiwaz, the warrior
            ["Elivagar"] = "M4.5,0 L4.5,14", // Isa, ice
            ["Thor"] = "M2.5,0 L2.5,14 M2.5,3.5 L7.5,7 L2.5,10.5", // Thurisaz
            ["Loki"] = "M2.5,14 L2.5,0 L8,4.5", // Laguz
            ["Freyja"] = "M3,0 L3,14 M3,5 L8,1 M3,9.5 L8,5.5", // Fehu
            ["Fylgja"] = "M4.5,14 L4.5,0 M4.5,6 L0.5,1 M4.5,6 L8.5,1", // Algiz, the guardian spirit
            ["Surtr"] = "M7.5,2 L2,7 L7.5,12", // Kaunan, the torch
            ["Svalinn"] = "M7,0.5 L2,5 L7,9 L2,13.5", // Sowilo, the sun Svalinn shields
            ["GiantsBlood"] = "M2,14 L2,0 L7,3.5 L2,7", // Wunjo, the giant's joy in his strength
            ["Jormungandr"] = "M4.5,2.5 L8.5,7 L4.5,11.5 L0.5,7 L4.5,2.5", // Ingwaz, the closed ring
            ["Ragnar"] = "M2,14 L2,0 L7.5,3.5 L2,7 L7.5,14", // Raido
            ["Gullveig"] = "M1,1 L8,13 M8,1 L1,13", // Gebo
            ["Mimir"] = "M1,14 L1,0 L8,6.5 M8,14 L8,0 L1,6.5", // Mannaz
            ["Bolthorn"] = "M2,0 L2,14 M2,0 L7.5,3.5 L2,7 L7.5,10.5 L2,14", // Berkanan
            ["DvergerArsenal"] = "M1,1 L1,13 L8,1 L8,13 L1,1", // Dagaz
            ["Ullr"] = "M2,14 L2,0 L7.5,4.5 L7.5,14", // Uruz
        };

        // Grid units: the stroke's half width, the edge around it and the margin the edge needs; texture pixels per unit.
        private const float Stroke = .9f, Edge = .7f, Margin = Stroke + Edge + .1f, Pixels = 6f;
        private static readonly Color EdgeColor = new Color(.24f, .24f, .24f);
        private const float GridWidth = 9f, GridHeight = 14f;
        // The rune's height and its gap from the right and bottom edges, as shares of an inventory slot, and of the smaller
        // item icon in the enchanting table's lists, where it takes a larger share to stay readable.
        private const float SlotRuneHeight = 28f / 76f, SlotGap = 3f / 76f, IconRuneHeight = .55f, IconGap = 0f;
        private static readonly Dictionary<(string Set, string Colour), Sprite> Sprites = new Dictionary<(string Set, string Colour), Sprite>();

        // The colour is drawn into the rune, so the grey edge isn't tinted; a changed set item colour draws the runes again.
        private static Sprite For(ItemDrop.ItemData item)
        {
            string set = item?.GetMagicItem()?.SetID ?? "", colour = EpicLoot.EpicLoot.GetSetItemColor();
            var key = (set, colour);
            if (Sprites.TryGetValue(key, out var sprite)) return sprite;
            var tint = ColorUtility.TryParseHtmlString(colour, out var parsed) ? parsed : Color.white;
            return Sprites[key] = Draw("SetRune" + set, Strokes.TryGetValue(set, out var strokes) ? strokes : UnknownSet, tint);
        }

        // Fills the bottom-right corner of the marker's parent; the edge reaches past the rune's own box.
        private static void Show(Image marker, ItemDrop.ItemData item, float runeHeight, float gap)
        {
            var rune = For(item);
            marker.color = Color.white;
            if (marker.sprite == rune) return;
            marker.sprite = rune;
            marker.preserveAspect = true;
            float unit = runeHeight / GridHeight;
            var rect = marker.rectTransform;
            rect.anchorMin = new Vector2(1f - gap - (GridWidth + Margin) * unit, gap - Margin * unit);
            rect.anchorMax = new Vector2(1f - gap + Margin * unit, gap + (GridHeight + Margin) * unit);
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            rect.pivot = new Vector2(.5f, .5f);
        }

        [HarmonyPatch(typeof(API), nameof(API.ApplyMagicItemBackground))]
        private static class Slots
        {
            private static void Postfix(GameObject slotRoot, ItemDrop.ItemData item, bool inventoryGrid)
            {
                if (!inventoryGrid || item == null || !slotRoot) return;
                var marker = slotRoot.transform.Find("setItem");
                var image = marker ? marker.GetComponent<Image>() : null;
                if (image && image.enabled) Show(image, item, SlotRuneHeight, SlotGap);
            }
        }

        [HarmonyPatch(typeof(ItemBackgroundHelper), nameof(ItemBackgroundHelper.UpdateIconSetMarker))]
        private static class Icons
        {
            private static void Postfix(Image icon, ItemDrop.ItemData item)
            {
                if (!icon || item == null) return;
                var marker = icon.transform.Find("EL_SetItemMarker");
                if (marker && marker.gameObject.activeSelf) Show(marker.GetComponent<Image>(), item, IconRuneHeight, IconGap);
            }
        }

        private static Sprite Draw(string name, string strokes, Color tint)
        {
            var segments = new List<(Vector2 From, Vector2 To)>();
            Vector2 pen = Vector2.zero;
            var words = strokes.Split(' ');
            foreach (var word in words)
            {
                var numbers = word.Substring(1).Split(',');
                var point = new Vector2(float.Parse(numbers[0], CultureInfo.InvariantCulture), float.Parse(numbers[1], CultureInfo.InvariantCulture));
                if (word[0] == 'L') segments.Add((pen, point));
                pen = point;
            }
            int width = Mathf.CeilToInt((GridWidth + 2f * Margin) * Pixels), height = Mathf.CeilToInt((GridHeight + 2f * Margin) * Pixels);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            var colors = new Color[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    // Texture rows run up, grid rows down.
                    var point = new Vector2((x + .5f) / Pixels - Margin, GridHeight + Margin - (y + .5f) / Pixels);
                    float distance = float.MaxValue;
                    foreach (var (from, to) in segments) distance = Mathf.Min(distance, Distance(point, from, to));
                    float core = Mathf.Clamp01((Stroke - distance) * Pixels + .5f), edge = Mathf.Clamp01((Stroke + Edge - distance) * Pixels + .5f);
                    var colour = Color.Lerp(EdgeColor, tint, core);
                    colour.a = edge;
                    colors[y * width + x] = colour;
                }
            texture.SetPixels(colors);
            texture.Apply(true, true);
            var sprite = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(.5f, .5f));
            sprite.name = name;
            return sprite;
        }

        private static float Distance(Vector2 point, Vector2 from, Vector2 to)
        {
            var line = to - from;
            float along = Mathf.Clamp01(Vector2.Dot(point - from, line) / line.sqrMagnitude);
            return Vector2.Distance(point, from + line * along);
        }
    }
}
