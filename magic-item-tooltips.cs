using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
    internal static class MagicItemTooltips
    {
        private static readonly Func<Attack, float> Stamina = AccessTools.MethodDelegate<Func<Attack, float>>(AccessTools.Method(typeof(Attack), "GetAttackStamina"));
        private static readonly Func<Attack, float> Health = AccessTools.MethodDelegate<Func<Attack, float>>(AccessTools.Method(typeof(Attack), "GetAttackHealth"));
        private static readonly Func<Player, ItemDrop.ItemData, Attack, bool> BloodlustActive =
            AccessTools.MethodDelegate<Func<Player, ItemDrop.ItemData, Attack, bool>>(AccessTools.Method(typeof(Bloodlust), "HasBloodlust"));
        private static readonly Func<ItemDrop.ItemData, bool> SpellswordWeapon =
            AccessTools.MethodDelegate<Func<ItemDrop.ItemData, bool>>(AccessTools.Method(typeof(Spellsword), "IsSpellswordWeapon"));
        private static readonly Regex OldCosts = new Regex(@"(?:^|\r?\n)\$item_(?:staminause|eitruse|healthuse|staminahold):[^\r\n]*", RegexOptions.Compiled);

        private static string Number(float value) => value.ToString("0.#", CultureInfo.CurrentCulture);

        [HarmonyPriority(Priority.Last)]
        private static void Postfix(ItemDrop.ItemData item, int qualityLevel, ref string __result)
        {
            var player = Player.m_localPlayer;
            if (!player || item?.m_shared == null || item.IsUnidentified() || string.IsNullOrEmpty(__result)) return;
            var primary = item.m_shared.m_attack;
            if (primary == null || string.IsNullOrEmpty(primary.m_attackAnimation)) return;
            var priorHands = WeaponTooltip.AsIfEquipped(item);
            try
            {
                var rows = new List<string>();
                bool spell = item.m_shared.m_skillType == Skills.SkillType.BloodMagic || item.m_shared.m_skillType == Skills.SkillType.ElementalMagic;
                AddAttack(rows, player, item, primary, spell ? "Cast cost" : "Attack cost");
                var secondary = item.m_shared.m_secondaryAttack;
                if (secondary != null && !string.IsNullOrEmpty(secondary.m_attackAnimation)) AddAttack(rows, player, item, secondary, "Secondary attack cost");
                if (primary.m_bowDraw) AddDraw(rows, player, item);
                if (primary.m_requiresReload) rows.Add(ReloadRow(item));
                AddSummons(rows, player, item, qualityLevel);
                if (primary.m_attackType == Attack.AttackType.Projectile && primary.m_projectiles > 1)
                    rows.Add("Projectiles per burst: " + primary.m_projectiles);
                if (rows.Count == 0) return;
                string color = item.GetMagicItem()?.GetColorString() ?? "orange";
                string replacement = "\n" + string.Join("\n", rows.Select(row => "<color=" + color + ">" + row + "</color>"));
                var match = OldCosts.Match(__result);
                if (match.Success)
                {
                    int position = match.Index;
                    __result = OldCosts.Replace(__result, "").Insert(position, replacement);
                }
                else
                {
                    int position = __result.IndexOf("\n$item_blockarmor:", StringComparison.Ordinal);
                    if (position < 0) position = __result.IndexOf("\n$item_knockback:", StringComparison.Ordinal);
                    __result = __result.Insert(position < 0 ? __result.Length : position, replacement);
                }
            }
            finally { HandOverride.Active = priorHands; }
        }

        private static string ReloadRow(ItemDrop.ItemData item)
        {
            var primary = item.m_shared.m_attack; var drains = new List<string>();
            AddResource(drains, primary.m_reloadStaminaDrain * Game.m_staminaRate, "stamina/s", primary.m_reloadStaminaDrain != 0);
            AddResource(drains, primary.m_reloadEitrDrain * Game.m_eitrRate, "eitr/s", primary.m_reloadEitrDrain != 0);
            return "Reload: " + Number(item.GetWeaponLoadingTime()) + " s" + (drains.Count == 0 ? "" : "; " + string.Join("; ", drains));
        }

        // A single summon per cast goes without saying, and the cap shared by everyone's summons shows only with Shift.
        private static void AddSummons(List<string> rows, Player player, ItemDrop.ItemData item, int quality)
        {
            var spawn = Summoning(item);
            if (!spawn) return;
            int maximum = spawn.m_maxToSpawn > spawn.m_minToSpawn ? spawn.m_maxToSpawn - 1 : spawn.m_minToSpawn;
            if (spawn.m_minToSpawn != 1 || maximum != 1) rows.Add("Summons per cast: " + spawn.m_minToSpawn + (maximum == spawn.m_minToSpawn ? "" : "-" + maximum));
            var level = spawn.m_levelUpSettings?.AsEnumerable().Reverse().FirstOrDefault(setting => player.GetSkillLevel(setting.m_skill) >= setting.m_skillLevel);
            int following = level == null ? 0 : spawn.m_setMaxInstancesFromWeaponLevel ? quality : level.m_maxSpawns;
            if (spawn.m_commandOnSpawn && following > 0) rows.Add("Follower limit: " + following);
            if (spawn.m_maxSpawned > 0 && MagicItem.ShowEffectDetails) rows.Add("Summon limit nearby: " + spawn.m_maxSpawned + " per type");
            if (spawn.m_copySkill != Skills.SkillType.None && spawn.m_copySkillToRandomFactor > 0)
                rows.Add("Summon damage multiplier: " + (1f + player.GetSkillLevel(spawn.m_copySkill) * spawn.m_copySkillToRandomFactor).ToString("0.###", CultureInfo.CurrentCulture) + "x");
        }

        // The ability a primary attack summons creatures with, cast directly or where its projectile lands.
        internal static SpawnAbility Summoning(ItemDrop.ItemData item)
        {
            var projectile = item.m_shared.m_attack.m_attackProjectile;
            var spawn = projectile ? projectile.GetComponent<SpawnAbility>() : null;
            if (!spawn && projectile && projectile.TryGetComponent<Projectile>(out var shot) && shot.m_spawnOnHit)
                spawn = shot.m_spawnOnHit.GetComponent<SpawnAbility>();
            return spawn && spawn.m_spawnPrefab != null && spawn.m_spawnPrefab.Any(prefab => prefab && prefab.GetComponent<Character>()) ? spawn : null;
        }

        private static Attack Prepared(Player player, ItemDrop.ItemData item, Attack source)
        {
            var attack = source.Clone();
            GameFields.AttackCharacter(attack) = player;
            GameFields.AttackWeapon(attack) = item;
            return attack;
        }

        private static void AddResource(List<string> parts, float value, string resource, bool present)
        {
            if (present || value != 0) parts.Add(Number(Mathf.Abs(value)) + " " + resource + (value < 0 ? " restored" : ""));
        }

        private static string AttackCost(Player player, ItemDrop.ItemData item, Attack source)
        {
            var attack = Prepared(player, item, source);
            float stamina = Stamina(attack), eitr = attack.GetAttackEitr(player, item), health = Health(attack);
            bool burst = source.m_attackType == Attack.AttackType.Projectile && source.m_perBurstResourceUsage;
            var parts = new List<string>();
            AddResource(parts, (burst ? Mathf.Max(0, stamina) : stamina) * Game.m_staminaRate, "stamina", source.m_attackStamina != 0);
            bool spellsword = SpellswordWeapon(item) && !EitrInfusion.IsInfused &&
                MagicEffectsHelper.HasActiveMagicEffectOnWeapon(player, item, MagicEffectType.SpellSword, out _);
            AddResource(parts, (burst ? Mathf.Max(0, eitr) : eitr) * Game.m_eitrRate, "eitr", item.m_shared.m_attack.m_attackEitr != 0 || spellsword || ConjuredArrows.TryGetShotCost(item, out _));
            float healthPayment = Mathf.Max(0, Mathf.Min(player.GetHealth() - 1f, health));
            bool healthUsed = source.m_attackHealth != 0 || source.m_attackHealthPercentage != 0 || BloodlustActive(player, item, attack);
            if (healthUsed || healthPayment != 0)
                parts.Add(Number(healthPayment) + " health" + (source.m_attackHealthPercentage > 0 && player.GetHealth() > 0 ? " (" + Number(100f * healthPayment / player.GetHealth()) + "% of current health)" : ""));
            return string.Join("; ", parts);
        }

        private static void AddAttack(List<string> rows, Player player, ItemDrop.ItemData item, Attack source, string title)
        {
            string cost = AttackCost(player, item, source);
            bool burst = source.m_attackType == Attack.AttackType.Projectile && source.m_perBurstResourceUsage;
            if (cost.Length > 0) rows.Add(title + (burst ? " per burst" : "") + ": " + cost);
            if (burst && !source.m_loopingAttack && source.m_projectileBursts > 1) rows.Add("Bursts per attack: " + source.m_projectileBursts);
        }

        private static string DrawCost(Player player, ItemDrop.ItemData item)
        {
            var source = item.m_shared.m_attack;
            float stamina = item.GetDrawStaminaDrain();
            stamina *= 1f + player.GetEquipmentAttackStaminaModifier();
            player.GetSEMan().ModifyAttackStaminaUsage(stamina, ref stamina);
            var values = new List<string>();
            AddResource(values, stamina * Game.m_staminaRate, "stamina/s", source.m_drawStaminaDrain != 0);
            // The game halves only the stamina drain once the draw is full, not the eitr drain.
            if (stamina != 0) values[values.Count - 1] += ", half at full draw";
            AddResource(values, item.GetDrawEitrDrain() * Game.m_eitrRate, "eitr/s", source.m_drawEitrDrain != 0);
            return string.Join("; ", values);
        }

        private static void AddDraw(List<string> rows, Player player, ItemDrop.ItemData item)
        {
            var source = item.m_shared.m_attack;
            if (source.m_drawStaminaDrain == 0 && source.m_drawEitrDrain == 0) return;
            rows.Add("Draw cost: " + DrawCost(player, item));
        }
    }
}
