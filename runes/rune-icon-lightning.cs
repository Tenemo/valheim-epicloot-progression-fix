using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetIcon))]
    internal static class RuneIconLightning
    {
        private static readonly Dictionary<Sprite, Sprite> Icons = new Dictionary<Sprite, Sprite>();

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData __instance, ref Sprite __result)
        {
            if (!RuneStacks.IsRune(__instance) || __result == null || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            if (Icons.TryGetValue(__result, out var replacement))
            {
                __result = replacement;
                return;
            }

            // A missing or unreadable file keeps EpicLoot's icon, remembered so the warning appears once, not on every redraw.
            replacement = Load(__result) ?? __result;
            Icons.Add(__result, replacement);
            __result = replacement;
        }

        private static Sprite Load(Sprite original)
        {
            string name = original.name.ToLowerInvariant().Replace('_', '-');
            string path = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location), "rune-icons", name + ".png");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
            {
                name = name + "-40-percent-lightning",
                filterMode = original.texture.filterMode,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!File.Exists(path) || !ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true))
            {
                Object.Destroy(texture);
                Debug.LogWarning("Progression Fix: no readable rune icon at " + path + "; keeping EpicLoot's icon.");
                return null;
            }
            var pivot = new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height);
            var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot,
                original.pixelsPerUnit, 0, SpriteMeshType.FullRect, original.border);
            sprite.name = texture.name;
            return sprite;
        }
    }
}
