using System;
using System.Reflection;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // AAA_Crafting 2.1.12 shows the item tooltip over a recipe, such as an item in a table's upgrade tab, with its own copy of
    // the inventory tooltip window, which EpicLoot's scroll box never reaches. The game's screen clamp moves that window by
    // whatever it overhangs, so a tooltip taller than the screen, such as an enchanted staff's with its set, went below the
    // screen one frame and above it the next, jumping too fast to read. These tooltips now get EpicLoot's scroll box and are
    // placed beside the pointer the way EpicLoot places inventory tooltips.
    internal static class CraftingRecipeTooltips
    {
        private static FieldInfo current;
        private static AccessTools.FieldRef<GameObject> window;
        private static Action<GameObject> addScrollBox, addShimmer;
        private static Func<GameObject, Component> mainBox;
        private static MethodInfo fit;
        private static readonly object[] fitArguments = new object[2];

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AaaCrafting, out var plugin)) return;
            var tooltip = plugin.Instance.GetType().Assembly.GetType("AzuAntiArthriticCrafting.Handlers.RecipeHoverTooltip");
            var box = typeof(TooltipBoxPatches).Assembly.GetType("EpicLoot.TooltipBox");
            var hover = tooltip == null ? null : AccessTools.Method(tooltip, "OnHoverStart", new[] { typeof(GameObject) });
            var update = tooltip == null ? null : AccessTools.Method(tooltip, "LateUpdate", Type.EmptyTypes);
            current = tooltip == null ? null : AccessTools.Field(tooltip, "m_current");
            var open = tooltip == null ? null : AccessTools.Field(tooltip, "m_tooltip");
            var scroll = AccessTools.Method(typeof(TooltipBoxPatches), "AddScrollbar", new[] { typeof(GameObject) });
            var shimmer = AccessTools.Method(typeof(TooltipBoxPatches), "EnsureShimmer", new[] { typeof(GameObject) });
            var main = AccessTools.Method(typeof(TooltipBoxPatches), "GetMainBox", new[] { typeof(GameObject) });
            fit = box == null ? null : AccessTools.Method(box, "Fit", new[] { typeof(Vector2), typeof(Vector2).MakeByRefType() });
            if (hover == null || update == null || current == null || open == null || open.FieldType != typeof(GameObject))
                throw new InvalidOperationException("AAA_Crafting's recipe tooltip moved.");
            if (scroll == null || shimmer == null || main == null || fit == null)
                throw new InvalidOperationException("EpicLoot's tooltip scroll box moved.");
            window = AccessTools.StaticFieldRefAccess<GameObject>(open);
            addScrollBox = AccessTools.MethodDelegate<Action<GameObject>>(scroll);
            addShimmer = AccessTools.MethodDelegate<Action<GameObject>>(shimmer);
            mainBox = AccessTools.MethodDelegate<Func<GameObject, Component>>(main);
            harmony.Patch(hover, postfix: new HarmonyMethod(typeof(CraftingRecipeTooltips), nameof(AddScrollBox)));
            harmony.Patch(update, postfix: new HarmonyMethod(typeof(CraftingRecipeTooltips), nameof(Place)));
            log.LogInfo("AAA_Crafting's recipe tooltips scroll and stay on screen like inventory tooltips.");
        }

        private static void AddScrollBox(object __instance)
        {
            var tooltip = window();
            if (tooltip == null || !ReferenceEquals(current.GetValue(null), __instance)) return;
            addScrollBox(tooltip);
            addShimmer(tooltip);
        }

        // EpicLoot's placement of a single inventory tooltip; AAA_Crafting has just moved the window to the pointer.
        private static void Place(object __instance)
        {
            var tooltip = window();
            if (tooltip == null || !tooltip.activeInHierarchy || !ReferenceEquals(current.GetValue(null), __instance)) return;
            var box = mainBox(tooltip);
            if (box == null) return;
            float scale = box.transform.lossyScale.y;
            if (scale <= 0f) scale = 1f;
            float width = Screen.width / scale, height = Screen.height / scale;
            fitArguments[0] = new Vector2(Mathf.Max(Mathf.Min(TooltipBoxPatches.MaxWidth, width - 10f), 75f),
                Mathf.Max(Mathf.Min(TooltipBoxPatches.MaxHeight, height - 10f), 75f));
            if (!(bool)fit.Invoke(box, fitArguments)) return;
            var size = (Vector2)fitArguments[1];
            var pointer = tooltip.transform.position / scale;
            float left = pointer.x > width / 2f ? pointer.x - 10f - size.x : pointer.x + 20f;
            left = Mathf.Max(5f, Mathf.Min(left, width - 5f - size.x));
            float top = Mathf.Min(height - 5f, Mathf.Max(pointer.y, 5f + size.y));
            box.transform.position = new Vector3(left * scale, top * scale, box.transform.position.z);
        }
    }
}
