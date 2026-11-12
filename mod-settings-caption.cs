using System;
using System.Reflection;
using HarmonyLib;
using Jotunn.Managers;
using UnityEngine;
using UnityEngine.UI;

namespace EpicLootProgressionFix
{
    internal static class ModSettingsCaption
    {
        private const string Token = "modquickconfig_button";
        private static Component broker;
        private static FieldInfo mainButton, pauseButton;
        private static readonly Func<Localization, string, string> Translate = AccessTools.MethodDelegate<Func<Localization, string, string>>(
            AccessTools.Method(typeof(Localization), "Translate"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.StarLevelSystem, out var plugin)) return;
            var type = plugin.Instance.GetType().Assembly.GetType("StarLevelSystem.common.QuickConfigBroker", true);
            mainButton = AccessTools.Field(type, "mainMenuButton");
            pauseButton = AccessTools.Field(type, "pauseButton");
            var create = AccessTools.Method(type, "EnsureCornerButton");
            var translated = AccessTools.Method(typeof(LocalizationManager), "AddTranslations");
            if (mainButton == null || pauseButton == null || create == null || translated == null)
                throw new InvalidOperationException("StarLevelSystem's settings button or Jotunn translation registration moved.");
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(ModSettingsCaption), nameof(Created)));
            harmony.Patch(translated, postfix: new HarmonyMethod(typeof(ModSettingsCaption), nameof(TranslationsReady)));
            broker = GameObject.Find("ModQuickConfigLauncher")?.GetComponent(type);
            TranslationsReady(Localization.instance);
        }

        private static void Created(Component __instance, GameObject __result)
        {
            broker = __instance;
            Refresh(__result, Localization.instance);
        }

        private static void TranslationsReady(Localization localization)
        {
            if (broker == null) return;
            Refresh((GameObject)mainButton.GetValue(broker), localization);
            Refresh((GameObject)pauseButton.GetValue(broker), localization);
        }

        private static void Refresh(GameObject button, Localization localization)
        {
            if (button == null) return;
            // Jotunn can register a foreign word first. Read the final dictionary without the game's stale text cache.
            string word = Translate(localization, Token);
            if (word == "[" + Token + "]") return;
            var caption = button.GetComponentInChildren<Text>(true);
            if (caption != null) caption.text = word;
        }
    }
}
