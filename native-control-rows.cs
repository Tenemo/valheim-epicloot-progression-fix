using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Valheim.SettingsGui;

namespace EpicLootProgressionFix
{
    // Adds a rebindable row to Settings, Keyboard & Mouse, copied from the first native row, so the game's own menu saves,
    // resets and clears the binding in PlatformPrefs with the other personal controls.
    internal static class NativeControlRows
    {
        // ZInput.AddButton(name, path, altKey, showHints, rebindable, repeatDelay, repeatInterval)
        internal static readonly Action<ZInput, string, string, bool, bool, bool, float, float> AddButton =
            AccessTools.MethodDelegate<Action<ZInput, string, string, bool, bool, bool, float, float>>(
                AccessTools.Method(typeof(ZInput), "AddButton", new[] { typeof(string), typeof(string), typeof(bool),
                    typeof(bool), typeof(bool), typeof(float), typeof(float) }));
        private static readonly AccessTools.FieldRef<KeyboardMouseSettings, List<KeySetting>> Keys =
            AccessTools.FieldRefAccess<KeyboardMouseSettings, List<KeySetting>>("m_keys");
        private static readonly AccessTools.FieldRef<KeyboardMouseSettings, int> Rows =
            AccessTools.FieldRefAccess<KeyboardMouseSettings, int>("m_keyRows");
        private static readonly AccessTools.FieldRef<KeyboardMouseSettings, Button> BottomLeft =
            AccessTools.FieldRefAccess<KeyboardMouseSettings, Button>("m_bottomLeftKeyButton");

        internal static void Add(KeyboardMouseSettings settings, string buttonName, string objectName, string label)
        {
            var keys = Keys(settings);
            if (keys.Any(key => key.m_keyName == buttonName)) return;
            var source = keys[0].m_keyTransform;
            var control = UnityEngine.Object.Instantiate(source, source.parent);
            control.name = objectName;
            control.SetSiblingIndex(Rows(settings));
            control.Find("Label").GetComponent<TMP_Text>().text = label;
            var button = control.GetComponentInChildren<Button>();
            button.onClick.RemoveAllListeners();
            keys.Insert(Rows(settings), new KeySetting { m_keyName = buttonName, m_keyTransform = control, m_blockedButtons = Array.Empty<KeyCode>() });
            Rows(settings)++;
            var grid = source.parent.GetComponent<GridLayoutGroup>();
            grid.constraint = GridLayoutGroup.Constraint.FixedRowCount;
            grid.constraintCount = Rows(settings);
            ((RectTransform)grid.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,
                grid.padding.vertical + grid.constraintCount * grid.cellSize.y + (grid.constraintCount - 1) * grid.spacing.y);
            BottomLeft(settings) = button;
        }
    }
}
