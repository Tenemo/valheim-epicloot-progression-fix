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
        internal float RowOffset;
        internal bool Expanded;
        internal TextOverflowModes Overflow;
        internal TextWrappingModes Wrapping;

        internal void Restore()
        {
            var entry = (RectTransform)transform;
            entry.anchoredPosition += Vector2.up * RowOffset;
            RowOffset = 0f;
            if (!Expanded || !Timer) return;
            Timer.rectTransform.sizeDelta = Size;
            Timer.rectTransform.anchoredPosition = Position;
            Timer.overflowMode = Overflow;
            Timer.textWrappingMode = Wrapping;
            Expanded = false;
        }

        internal float Expand(TMP_Text timer)
        {
            Timer = timer;
            var rect = timer.rectTransform;
            Size = rect.sizeDelta; Position = rect.anchoredPosition;
            Overflow = timer.overflowMode; Wrapping = timer.textWrappingMode;
            timer.textWrappingMode = TextWrappingModes.NoWrap;
            timer.overflowMode = TextOverflowModes.Overflow;
            float height = Mathf.Max(rect.rect.height, timer.GetPreferredValues(timer.text).y);
            float extra = height - rect.rect.height;
            rect.sizeDelta += Vector2.up * extra;
            rect.anchoredPosition -= Vector2.up * (extra * (1f - rect.pivot.y));
            Expanded = true;
            return extra;
        }
    }

    internal static class ProtectionHud
    {
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<SE_Shield, float> Capacity = AccessTools.FieldRefAccess<SE_Shield, float>("m_totalAbsorbDamage");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Damage = AccessTools.FieldRefAccess<SE_Shield, float>("m_damage");
        private static readonly AccessTools.FieldRef<Hud, List<RectTransform>> Entries = AccessTools.FieldRefAccess<Hud, List<RectTransform>>("m_statusEffects");

        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        private static class UpdateEffects
        {
            private static void Prefix(Hud __instance)
            {
                foreach (var entry in Entries(__instance))
                    if (entry) entry.GetComponent<ProtectionStatusLayout>()?.Restore();
            }

            private static void Postfix(Hud __instance, List<StatusEffect> statusEffects)
            {
                var player = Player.m_localPlayer;
                var shield = player ? player.GetSEMan().GetStatusEffect(ShieldHash) as SE_Shield : null;
                if (!shield) return;
                var entries = Entries(__instance);
                int index = statusEffects.IndexOf(shield);
                if (index < 0 || index >= entries.Count || shield.m_hidden) return;
                var entry = entries[index];
                var timer = entry.Find("TimeText").GetComponent<TMP_Text>();
                string countdown = shield.GetIconText();
                string remaining = Math.Ceiling(Math.Max(0f, Capacity(shield) - Damage(shield))).ToString("0") + " HP";
                timer.text = string.IsNullOrEmpty(countdown) ? remaining : countdown + "\n" + remaining;
                timer.gameObject.SetActive(true);
                var layout = entry.GetComponent<ProtectionStatusLayout>() ?? entry.gameObject.AddComponent<ProtectionStatusLayout>();
                float extra = layout.Expand(timer);
                int row = index / __instance.m_effectsPerRow;
                for (int next = (row + 1) * __instance.m_effectsPerRow; next < entries.Count; next++)
                {
                    var following = entries[next];
                    var offset = following.GetComponent<ProtectionStatusLayout>() ?? following.gameObject.AddComponent<ProtectionStatusLayout>();
                    offset.RowOffset = extra;
                    following.anchoredPosition -= Vector2.up * extra;
                }
            }
        }
    }
}
