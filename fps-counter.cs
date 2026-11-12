using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace EpicLootProgressionFix
{
    // An optional in-game frame counter, independent of Steam, driver overlays and Gale's launch mode. A personal
    // Graphics checkbox, off by default, keeps the choice in PlatformPrefs; graphics presets never change it. The label
    // sits on the HUD, so UI scaling and hiding the HUD apply, and it samples nothing while disabled.
    internal sealed class FpsCounter : MonoBehaviour
    {
        private const string Preference = "ProgressionFixShowFps";
        private const string ToggleName = "ProgressionFixShowFps";
        private static bool? shown;
        private TextMeshProUGUI label;
        private int frames;
        private float elapsed;

        internal static bool Shown
        {
            get => shown ??= PlatformPrefs.GetInt(Preference, 0) == 1;
            private set { shown = value; PlatformPrefs.SetInt(Preference, value ? 1 : 0); PlatformPrefs.Save(); }
        }

        private void Update()
        {
            if (!Shown)
            {
                if (label.enabled) { label.enabled = false; frames = 0; elapsed = 0f; }
                return;
            }
            frames++;
            elapsed += Time.unscaledDeltaTime;
            if (elapsed < .5f) return;
            label.SetText("{0} FPS", Mathf.RoundToInt(frames / elapsed));
            label.enabled = true;
            frames = 0; elapsed = 0f;
        }

        [HarmonyPatch(typeof(Hud), "Awake")]
        private static class Create
        {
            private static void Postfix(Hud __instance)
            {
                // A dedicated server has no screen; its HUD can wake before ZNet exists, while it already runs without graphics.
                if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null || ZNet.instance != null && ZNet.instance.IsDedicated()) return;
                var root = new GameObject("ProgressionFixFps", typeof(RectTransform));
                var rect = (RectTransform)root.transform;
                rect.SetParent(__instance.m_rootObject.transform, false);
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = new Vector2(6f, -2f);
                rect.sizeDelta = new Vector2(160f, 24f);
                var label = root.AddComponent<TextMeshProUGUI>();
                label.font = __instance.m_healthText.font;
                label.fontSize = 18f;
                label.color = Color.white;
                label.alignment = TextAlignmentOptions.TopLeft;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                label.raycastTarget = false;
                label.outlineWidth = .25f;
                label.outlineColor = Color.white;
                label.enabled = false;
                root.AddComponent<FpsCounter>().label = label;
            }
        }

        [HarmonyPatch(typeof(GraphicsSettings), nameof(GraphicsSettings.Initialize))]
        private static class Checkbox
        {
            private static void Postfix(GraphicsSettings __instance, GameObject ___m_qualityTogglePrefab, List<Toggle> ___m_dynamicQualityToggles,
                RectTransform ___m_listRoot, VerticalLayoutGroup ___m_verticalLayoutGroup)
            {
                if (__instance.transform.Find(ToggleName) || ___m_dynamicQualityToggles.Exists(toggle => toggle && toggle.name == ToggleName)) return;
                var row = Object.Instantiate(___m_qualityTogglePrefab, ___m_qualityTogglePrefab.transform.parent);
                // Native rows are inserted before the hidden template, so this one follows the last of them.
                row.transform.SetSiblingIndex(___m_qualityTogglePrefab.transform.GetSiblingIndex());
                var toggle = row.GetComponentInChildren<Toggle>(true);
                toggle.name = ToggleName;
                toggle.transform.Find("Label").GetComponent<TMP_Text>().text = "Show FPS";
                toggle.onValueChanged.RemoveAllListeners();
                toggle.SetIsOnWithoutNotify(Shown);
                row.SetActive(true);
                // The native tab orders controller navigation from this list.
                ___m_dynamicQualityToggles.Add(toggle);
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)row.transform.parent);
                float height = 0f;
                for (int index = 0; index < ___m_listRoot.childCount; index++)
                {
                    var child = (RectTransform)___m_listRoot.GetChild(index);
                    if (child.gameObject.activeSelf) height += child.rect.height + ___m_verticalLayoutGroup.spacing;
                }
                ___m_listRoot.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            }
        }

        // Back and Escape leave the saved choice alone; only OK keeps the checkbox state.
        [HarmonyPatch(typeof(GraphicsSettings), nameof(GraphicsSettings.OnOkAsync))]
        private static class Save
        {
            private static void Prefix(List<Toggle> ___m_dynamicQualityToggles)
            {
                var toggle = ___m_dynamicQualityToggles.Find(value => value && value.name == ToggleName);
                if (toggle && toggle.isOn != Shown) Shown = toggle.isOn;
            }
        }
    }
}
