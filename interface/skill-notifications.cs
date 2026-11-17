using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using EpicLoot.MagicItemEffects.Shards;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    // The game shows pickups and skill notices in one top-left row, one message a second, so a burst of BetterUI's XP
    // percentages held pickups back and pickups held skill notices back. Skill notices get a row of their own under the
    // pickup row, with its own queue and timing, in the pickup row's font and layout: BetterUI's XP progress, skill and
    // FortifySkillsReborn level-ups and EpicLoot's Inspiration notices. Level-ups and Inspiration notices go ahead of XP
    // progress, and each skill keeps one pending percentage, updated to its latest progress. A skill's first level stays
    // the game's centered message, as does BetterUI's character level-up.
    internal static class SkillNotifications
    {
        private static Action<MessageHud, string> addLog;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            var logMessage = AccessTools.Method(typeof(MessageHud), "AddLog", new[] { typeof(string) }) ??
                throw new InvalidOperationException("The game's message log changed.");
            addLog = AccessTools.MethodDelegate<Action<MessageHud, string>>(logMessage);
            var notices = new HarmonyMethod(typeof(SkillNotifications), nameof(RouteNotices));
            harmony.Patch(AccessTools.Method(typeof(Skills), nameof(Skills.RaiseSkill)), transpiler: notices);
            harmony.Patch(AccessTools.Method(AccessTools.Inner(typeof(Inspiration), "RaiseSkill_Patch"), "Postfix"), transpiler: notices);
            if (PluginIds.TryGet(PluginIds.FortifySkills, out var fortify))
                harmony.Patch(AccessTools.Method(fortify.Instance.GetType().Assembly.GetType("FortifySkillsReborn.Patches.SkillPatch"),
                    "LevelUpFortifySkill"), transpiler: notices);
            if (PluginIds.TryGet(PluginIds.BetterUi, out var betterUi))
            {
                var notification = AccessTools.Method(betterUi.Instance.GetType().Assembly.GetType("BetterUI.Patches.XPNotification"),
                    "Show", new[] { typeof(Skills.Skill), typeof(float) }) ??
                    throw new InvalidOperationException("BetterUI's skill notifications changed.");
                harmony.Patch(notification, transpiler: new HarmonyMethod(typeof(SkillNotifications), nameof(RouteProgress)));
            }
            log.LogInfo("Skill notifications have their own row under pickups.");
        }

        // Each patched method shows exactly one notice through Character.Message.
        private static IEnumerable<CodeInstruction> RouteNotices(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var message = AccessTools.Method(typeof(Character), nameof(Character.Message));
            var calls = code.Where(instruction => instruction.Calls(message)).ToList();
            if (calls.Count != 1)
                throw new InvalidOperationException($"{original.DeclaringType.FullName}.{original.Name} changed; expected one message.");
            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(typeof(SkillNotifications), nameof(Notice));
            return code;
        }

        // BetterUI 2.5.13 shows each XP gain with one HUD message; pass its skill along.
        private static IEnumerable<CodeInstruction> RouteProgress(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var show = AccessTools.Method(typeof(MessageHud), nameof(MessageHud.ShowMessage),
                new[] { typeof(MessageHud.MessageType), typeof(string), typeof(int), typeof(Sprite), typeof(bool), typeof(bool) });
            var calls = code.Where(instruction => instruction.Calls(show)).ToList();
            if (calls.Count != 1)
                throw new InvalidOperationException("BetterUI's XP notification changed; expected one HUD message.");
            int index = code.IndexOf(calls[0]);
            var skill = new CodeInstruction(OpCodes.Ldarg_0);
            skill.labels.AddRange(code[index].labels);
            code[index].labels.Clear();
            code.Insert(index, skill);
            code[index + 1].opcode = OpCodes.Call;
            code[index + 1].operand = AccessTools.Method(typeof(SkillNotifications), nameof(Progress));
            return code;
        }

        private static void Notice(Character character, MessageHud.MessageType type, string text, int amount, Sprite icon, bool log)
        {
            var hud = MessageHud.instance;
            if (type != MessageHud.MessageType.TopLeft || hud == null || character == null || character != Player.m_localPlayer)
                character.Message(type, text, amount, icon, log);
            else
                Add(hud, text, icon, log, null);
        }

        private static void Progress(MessageHud hud, MessageHud.MessageType type, string text, int amount, Sprite icon,
            bool showDespiteHiddenHUD, bool log, Skills.Skill skill)
        {
            if (type != MessageHud.MessageType.TopLeft || showDespiteHiddenHUD)
                hud.ShowMessage(type, text, amount, icon, showDespiteHiddenHUD, log);
            else
                Add(hud, text, icon, log, skill.m_info.m_skill);
        }

        // As the game's ShowMessage: nothing while the player hides the HUD, and logged when asked.
        private static void Add(MessageHud hud, string text, Sprite icon, bool log, Skills.SkillType? skill)
        {
            if (Hud.IsUserHidden()) return;
            string localized = Localization.instance.Localize(text);
            (hud.GetComponent<SkillMessageRow>() ?? hud.gameObject.AddComponent<SkillMessageRow>()).Add(localized, icon, skill);
            if (log) addLog(hud, localized);
        }
    }

    internal sealed class SkillMessageRow : MonoBehaviour
    {
        // Space between the pickup row's lowest edge and this row, in the HUD's own units.
        private const float Gap = 10f;

        private sealed class Entry
        {
            internal string Text;
            internal Sprite Icon;
            // Set for XP progress; level-ups and Inspiration notices have none and are never replaced.
            internal Skills.SkillType? Skill;
        }

        private readonly List<Entry> queue = new List<Entry>();
        private MessageHud hud;
        private TMP_Text text;
        private Image icon;
        private Player player;
        private float timer = 1f;

        private void Awake()
        {
            hud = GetComponent<MessageHud>();
            text = Instantiate(hud.m_messageText, hud.m_messageText.transform.parent);
            text.name = Plugin.Id + ".skill-message";
            text.text = "";
            icon = Instantiate(hud.m_messageIcon, hud.m_messageIcon.transform.parent);
            icon.name = Plugin.Id + ".skill-icon";
            Hide();
            player = Player.m_localPlayer;
        }

        private void OnDestroy()
        {
            if (text != null) Destroy(text.gameObject);
            if (icon != null) Destroy(icon.gameObject);
        }

        internal void Add(string message, Sprite sprite, Skills.SkillType? skill)
        {
            Forget();
            if (skill != null && queue.FirstOrDefault(entry => entry.Skill == skill) is Entry pending)
            {
                pending.Text = message;
                pending.Icon = sprite;
                return;
            }
            var entry = new Entry { Text = message, Icon = sprite, Skill = skill };
            if (skill != null) queue.Add(entry);
            else queue.Insert(queue.FindIndex(other => other.Skill != null) is int first && first >= 0 ? first : queue.Count, entry);
        }

        // A new character starts with an empty row.
        private void Forget()
        {
            if (player == Player.m_localPlayer) return;
            player = Player.m_localPlayer;
            queue.Clear();
            Hide();
        }

        private void Hide()
        {
            text.CrossFadeAlpha(0f, 0f, true);
            icon.canvasRenderer.SetAlpha(0f);
            icon.CrossFadeAlpha(0f, 0f, true);
        }

        // As MessageHud: hidden and paused while the player hides the HUD, and a new message at most once a second.
        private void Update()
        {
            Forget();
            if (Hud.IsUserHidden())
            {
                Hide();
                return;
            }
            if (Time.deltaTime > 0.5f) return;
            timer += Time.deltaTime;
            Place();
            if (queue.Count == 0 || timer < 1f) return;
            var entry = queue[0];
            queue.RemoveAt(0);
            text.text = entry.Text;
            text.canvasRenderer.SetAlpha(1f);
            text.CrossFadeAlpha(0f, 4f, true);
            if (entry.Icon != null)
            {
                icon.sprite = entry.Icon;
                icon.canvasRenderer.SetAlpha(1f);
                icon.CrossFadeAlpha(0f, 4f, true);
            }
            else icon.canvasRenderer.SetAlpha(0f);
            timer = 0f;
            Place();
        }

        // The pickup row's layout, moved below its lowest edge: its text, which can run to more lines, and its icon.
        // Extra lines of this row's text push its icon down.
        private void Place()
        {
            var pickupText = hud.m_messageText.rectTransform;
            var pickupIcon = hud.m_messageIcon.rectTransform;
            float bottom = Mathf.Min(Bottom(pickupText), Bottom(pickupIcon));
            if (hud.m_messageText.text.Length > 0)
                bottom = Mathf.Min(bottom, pickupText.anchoredPosition.y + hud.m_messageText.textBounds.min.y);
            float textHeight = Mathf.Max(pickupText.rect.height, text.text.Length > 0 ? text.preferredHeight : 0f);
            var textRect = text.rectTransform;
            textRect.sizeDelta = new Vector2(pickupText.sizeDelta.x, textHeight);
            float textTop = bottom - Gap;
            textRect.anchoredPosition = new Vector2(pickupText.anchoredPosition.x, textTop - textHeight * (1f - textRect.pivot.y));
            float iconTop = textTop - textHeight - (Bottom(pickupText) - Top(pickupIcon));
            var iconRect = icon.rectTransform;
            iconRect.anchoredPosition = new Vector2(pickupIcon.anchoredPosition.x, iconTop - pickupIcon.rect.height * (1f - iconRect.pivot.y));
        }

        private static float Bottom(RectTransform rect) => rect.anchoredPosition.y - rect.rect.height * rect.pivot.y;
        private static float Top(RectTransform rect) => rect.anchoredPosition.y + rect.rect.height * (1f - rect.pivot.y);
    }
}
