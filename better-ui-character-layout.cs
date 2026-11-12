using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class BetterUiCharacterLayout
    {
        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.BetterUi, out var plugin)) return;
            if (AccessTools.Method(plugin.Instance.GetType().Assembly.GetType("BetterUI.Patches.CharacterStats"), "Show") == null)
                throw new InvalidOperationException("BetterUI's character statistics moved.");
            harmony.Patch(AccessTools.Method(typeof(FejdStartup), "UpdateCharacterList"), postfix:
                new HarmonyMethod(typeof(BetterUiCharacterLayout), nameof(Layout)) {
                    priority = Priority.Last, after = new[] { PluginIds.BetterUi, PluginIds.Dyrr } });
        }

        private static void Layout(FejdStartup __instance)
        {
            var text = __instance.m_csName;
            if (!text || !text.gameObject.activeSelf || !text.text.Contains("\n")) return;
            var rect = text.rectTransform;
            var parent = rect.parent as RectTransform;
            if (!parent) return;
            // The explanatory line otherwise stretches the statistics through the character-selection arrows.
            text.text = text.text.Replace("   <size=14>", "\n<size=14>");
            float width = Mathf.Min(1100f, parent.rect.width - 80f);
            if (width <= 0f) return;
            text.textWrappingMode = TextWrappingModes.Normal;
            text.alignment = TextAlignmentOptions.Center;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            float height = text.GetPreferredValues(text.text, width, float.PositiveInfinity).y;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            var arrows = __instance.m_characterSelectScreen.GetComponentsInChildren<Button>(true).Where(button =>
                Enumerable.Range(0, button.onClick.GetPersistentEventCount()).Any(index =>
                    button.onClick.GetPersistentMethodName(index) == "OnCharacterLeft" || button.onClick.GetPersistentMethodName(index) == "OnCharacterRight"));
            float bottom = arrows.Select(button => RectTransformUtility.CalculateRelativeRectTransformBounds(parent, button.transform).max.y)
                .DefaultIfEmpty(RectTransformUtility.CalculateRelativeRectTransformBounds(parent, __instance.m_csStartButton.transform).max.y).Max() + 12f;
            rect.localPosition = new Vector3(rect.localPosition.x, bottom + height * rect.pivot.y, rect.localPosition.z);
            var binding = __instance.m_characterSelectScreen.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault(label => label.name == "DyrrHome");
            if (!binding) return;
            var note = binding.rectTransform;
            binding.textWrappingMode = TextWrappingModes.Normal;
            note.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            float noteHeight = binding.GetPreferredValues(binding.text, width, float.PositiveInfinity).y;
            note.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, noteHeight);
            Vector3 position = parent.TransformPoint(new Vector3(rect.localPosition.x, bottom + height + 10f + noteHeight * note.pivot.y, 0f));
            note.position = position;
        }
    }
}
