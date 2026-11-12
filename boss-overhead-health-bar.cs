using TMPro;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal sealed class BossOverheadHealthBar : MonoBehaviour
    {
        private Character character;
        private GameObject display;
        private GuiBar healthFast;
        private GuiBar healthSlow;
        private TextMeshProUGUI label;
        internal TMP_Text NameSource;

        private void Awake() => character = GetComponent<Character>();

        private void LateUpdate()
        {
            var hud = EnemyHud.instance;
            var player = Player.m_localPlayer;
            var camera = Utils.GetMainCamera();
            if (!character || !character.m_boss || character.m_hideHud || FreshCreatureSpawns.Waiting(character) || character.IsDead() || !hud || !player || !camera ||
                (!RecentHitHealthBars.Revealed(character) &&
                    (character.transform.position - player.transform.position).sqrMagnitude >= hud.m_maxShowDistance * hud.m_maxShowDistance))
            {
                if (display) display.SetActive(false);
                return;
            }

            var screenPosition = Utils.WorldToScreenPointScaled(camera, character.GetTopPoint());
            // Ordinary creature HUDs let the canvas clip the bar's rectangle at screen edges. Its attachment point
            // can leave the viewport while part of the bar is still visible, especially during nearby camera movement.
            if (screenPosition.z <= 0f)
            {
                if (display) display.SetActive(false);
                return;
            }

            if (!display)
            {
                display = Instantiate(hud.m_baseHud, hud.m_hudRoot.transform);
                display.name = "Boss overhead health";
                // Level and modifier details remain on the full boss HUD. Disable the ordinary prefab's stars and
                // awareness markers, including mod-added prototype labels which are not updated for this extra HUD.
                foreach (Transform child in display.transform)
                    child.gameObject.SetActive(child.name == "Health" || child.name == "Name");
                healthFast = display.transform.Find("Health/health_fast").GetComponent<GuiBar>();
                healthSlow = display.transform.Find("Health/health_slow").GetComponent<GuiBar>();
                var friendly = display.transform.Find("Health/health_fast_friendly");
                if (friendly) friendly.gameObject.SetActive(false);
                label = display.transform.Find("Name").GetComponent<TextMeshProUGUI>();
            }

            display.SetActive(true);
            display.transform.position = screenPosition;
            // Tameable caches the original creature name; EpicLoot puts the bounty's assigned name on Character.
            label.text = NameSource ? NameSource.text : Localization.instance.Localize(character.m_name);
            float health = character.GetHealthPercentage();
            healthFast.SetValue(health);
            healthSlow.SetValue(health);
        }

        private void OnDestroy()
        {
            if (display) Destroy(display);
        }

        private void OnDisable()
        {
            if (display) display.SetActive(false);
        }
    }
}
