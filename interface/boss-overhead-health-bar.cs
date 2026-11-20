using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Valheim gives each creature either a boss bar at the top of the screen or a bar above it. Nemesis minibosses and
    // bounty targets keep their boss bar and also show their name and health above them, exactly while that boss bar
    // shows: its distance, alert, recent-hit and hidden-HUD rules decide both, and world geometry hides neither.
    [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
    internal static class BossOverheadHealthBars
    {
        private sealed class Bar
        {
            internal GameObject Display;
            internal GuiBar HealthFast, HealthSlow;
            internal TextMeshProUGUI Label;
        }

        private static readonly Dictionary<Character, Bar> Bars = new Dictionary<Character, Bar>();
        private static readonly List<Character> Gone = new List<Character>();
        private static readonly AccessTools.FieldRef<object, GameObject> NativeDisplay =
            AccessTools.FieldRefAccess<GameObject>(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_gui");
        private static readonly AccessTools.FieldRef<object, TextMeshProUGUI> NativeName =
            AccessTools.FieldRefAccess<TextMeshProUGUI>(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_name");

        internal static void Add(Character character)
        {
            if (character && !Bars.ContainsKey(character)) Bars.Add(character, new Bar());
        }

        // After the game, BetterUI and StarLevelSystem have shown, hidden or removed this frame's boss bars.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(EnemyHud __instance, IDictionary ___m_huds)
        {
            if (Bars.Count == 0) return;
            var camera = Utils.GetMainCamera();
            foreach (var pair in Bars)
            {
                var character = pair.Key;
                var bar = pair.Value;
                if (!character)
                {
                    if (bar.Display) Object.Destroy(bar.Display);
                    Gone.Add(character);
                    continue;
                }
                var native = ___m_huds[character];
                var shown = native != null ? NativeDisplay(native) : null;
                var screenPosition = camera && shown && shown.activeSelf ? Utils.WorldToScreenPointScaled(camera, character.GetTopPoint()) : Vector3.zero;
                // A creature behind the camera has no point on screen to draw a bar above.
                if (screenPosition.z <= 0f)
                {
                    if (bar.Display) bar.Display.SetActive(false);
                    continue;
                }
                if (!bar.Display) Create(__instance, bar);
                bar.Display.SetActive(true);
                bar.Display.transform.position = screenPosition;
                bar.Label.text = NativeName(native).text;
                float health = character.GetHealthPercentage();
                bar.HealthFast.SetValue(health);
                bar.HealthSlow.SetValue(health);
            }
            if (Gone.Count == 0) return;
            foreach (var character in Gone) Bars.Remove(character);
            Gone.Clear();
        }

        private static void Create(EnemyHud hud, Bar bar)
        {
            bar.Display = Object.Instantiate(hud.m_baseHud, hud.m_hudRoot.transform);
            bar.Display.name = "Boss overhead health";
            // Level and modifier details remain on the full boss HUD. Disable the ordinary prefab's stars and
            // awareness markers, including mod-added prototype labels which are not updated for this extra HUD.
            foreach (Transform child in bar.Display.transform)
                child.gameObject.SetActive(child.name == "Health" || child.name == "Name");
            bar.HealthFast = bar.Display.transform.Find("Health/health_fast").GetComponent<GuiBar>();
            bar.HealthSlow = bar.Display.transform.Find("Health/health_slow").GetComponent<GuiBar>();
            var friendly = bar.Display.transform.Find("Health/health_fast_friendly");
            if (friendly) friendly.gameObject.SetActive(false);
            bar.Label = bar.Display.transform.Find("Name").GetComponent<TextMeshProUGUI>();
        }
    }
}
