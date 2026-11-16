using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Each HUD entry owns its temporary layout, so a rebuilt or reordered list restores ordinary timers too.
    internal sealed class ProtectionStatusLayout : MonoBehaviour
    {
        internal TMP_Text Timer;
        internal Vector2 Size, Position;
        internal float RowOffset, Extra;
        internal string Text;
        internal bool Expanded;
        internal TextOverflowModes Overflow;
        internal TextWrappingModes Wrapping;

        // Gives the timer back its own size, position and text modes.
        internal void Collapse()
        {
            if (!Expanded) return;
            Expanded = false;
            if (!Timer) return;
            Timer.rectTransform.sizeDelta = Size;
            Timer.rectTransform.anchoredPosition = Position;
            Timer.overflowMode = Overflow;
            Timer.textWrappingMode = Wrapping;
        }

        // The timer grown to fit its text; an entry already grown for the same text keeps its layout.
        internal float Expand(TMP_Text timer)
        {
            string text = timer.text;
            if (Expanded && Timer == timer && Text == text) return Extra;
            Collapse();
            Timer = timer;
            var rect = timer.rectTransform;
            Size = rect.sizeDelta; Position = rect.anchoredPosition;
            Overflow = timer.overflowMode; Wrapping = timer.textWrappingMode;
            timer.textWrappingMode = TextWrappingModes.NoWrap;
            timer.overflowMode = TextOverflowModes.Overflow;
            float height = Mathf.Max(rect.rect.height, timer.GetPreferredValues(text).y);
            float extra = height - rect.rect.height;
            rect.sizeDelta += Vector2.up * extra;
            rect.anchoredPosition -= Vector2.up * (extra * (1f - rect.pivot.y));
            Expanded = true; Text = text; Extra = extra;
            return extra;
        }

        // Moves the entry down by the grown row above it, or back up with 0.
        internal void Shift(float extra)
        {
            if (RowOffset == extra) return;
            var entry = (RectTransform)transform;
            entry.anchoredPosition += Vector2.up * (RowOffset - extra);
            RowOffset = extra;
        }
    }

    internal static class ProtectionHud
    {
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<SE_Shield, float> Capacity = AccessTools.FieldRefAccess<SE_Shield, float>("m_totalAbsorbDamage");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Damage = AccessTools.FieldRefAccess<SE_Shield, float>("m_damage");
        private static readonly AccessTools.FieldRef<Hud, List<RectTransform>> Entries = AccessTools.FieldRefAccess<Hud, List<RectTransform>>("m_statusEffects");
        // The local player's shield while the HUD writes its status icons, so the game writes the remaining health with the
        // countdown once instead of this patch overwriting the countdown every frame.
        private static SE_Shield drawing;

        [HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.GetIconText))]
        private static class ShieldText
        {
            private static void Postfix(StatusEffect __instance, ref string __result)
            {
                if ((object)drawing == null || (object)__instance != drawing) return;
                string remaining = Math.Ceiling(Math.Max(0f, Capacity(drawing) - Damage(drawing))).ToString("0") + " HP";
                __result = string.IsNullOrEmpty(__result) ? remaining : __result + "\n" + remaining;
            }
        }

        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        private static class UpdateEffects
        {
            private static void Prefix()
            {
                var player = Player.m_localPlayer;
                drawing = player ? player.GetSEMan().GetStatusEffect(ShieldHash) as SE_Shield : null;
            }

            // Entries keep their layout from the last frame and change only where the shield's row or text changed.
            private static void Postfix(Hud __instance, List<StatusEffect> statusEffects)
            {
                var shield = drawing;
                drawing = null;
                var entries = Entries(__instance);
                int index = shield ? statusEffects.IndexOf(shield) : -1;
                if (index >= entries.Count || shield && shield.m_hidden) index = -1;
                float extra = 0f;
                int following = int.MaxValue;
                if (index >= 0)
                {
                    var entry = entries[index];
                    var timer = entry.Find("TimeText").GetComponent<TMP_Text>();
                    timer.gameObject.SetActive(true);
                    var layout = entry.GetComponent<ProtectionStatusLayout>() ?? entry.gameObject.AddComponent<ProtectionStatusLayout>();
                    layout.Shift(0f);
                    extra = layout.Expand(timer);
                    following = (index / __instance.m_effectsPerRow + 1) * __instance.m_effectsPerRow;
                }
                for (int next = 0; next < entries.Count; next++)
                {
                    if (next == index || !entries[next]) continue;
                    var layout = entries[next].GetComponent<ProtectionStatusLayout>();
                    if (next >= following && !layout) layout = entries[next].gameObject.AddComponent<ProtectionStatusLayout>();
                    if (!layout) continue;
                    layout.Collapse();
                    layout.Shift(next >= following ? extra : 0f);
                }
            }

            private static void Finalizer() => drawing = null;
        }
    }
}
