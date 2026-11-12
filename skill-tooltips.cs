using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class SkillTooltips
    {
        private sealed class Row
        {
            internal Player Player;
            internal Skills.Skill Skill;
            internal float RefreshAt;
            internal GameObject OriginalPrefab;
        }
        private static readonly ConditionalWeakTable<UITooltip, Row> Rows = new ConditionalWeakTable<UITooltip, Row>();
        private static readonly AccessTools.FieldRef<SkillsDialog, List<GameObject>> Elements = AccessTools.FieldRefAccess<SkillsDialog, List<GameObject>>("m_elements");
        private static readonly AccessTools.FieldRef<Skills, Dictionary<Skills.SkillType, Skills.Skill>> SkillData = AccessTools.FieldRefAccess<Skills, Dictionary<Skills.SkillType, Skills.Skill>>("m_skillData");
        private static readonly Func<Player, float> DodgeCost = AccessTools.MethodDelegate<Func<Player, float>>(AccessTools.Method(typeof(Player), "GetDodgeStaminaUse"));
        private static readonly Func<Character, float> RunFactor = AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.Method(typeof(Character), "GetRunSpeedFactor"));
        private static readonly FieldInfo Current = AccessTools.Field(typeof(UITooltip), "m_current");
        private static readonly Action<UITooltip> UpdateText = AccessTools.MethodDelegate<Action<UITooltip>>(AccessTools.Method(typeof(UITooltip), "UpdateTextElements"));
        private static readonly AccessTools.FieldRef<Humanoid, float> BlockTimer = AccessTools.FieldRefAccess<Humanoid, float>("m_blockTimer");
        private static readonly AccessTools.FieldRef<Humanoid, float> DrawTime = AccessTools.FieldRefAccess<Humanoid, float>("m_attackDrawTime");
        private static readonly AccessTools.FieldRef<Character, float> SwimTimer = AccessTools.FieldRefAccess<Character, float>("m_swimTimer");
        private static readonly AccessTools.FieldRef<Character, bool> Running = AccessTools.FieldRefAccess<Character, bool>("m_running");

        // Running, jumping and swimming pay both world stamina rates; the other costs only the general one.
        private static float MoveStamina => Game.m_moveStaminaRate * Game.m_staminaRate;

        // Bind after the other postfixes without replacing text during setup: Fortify identifies rows by their
        // original descriptions. Mouse hover and controller selection both enter OnHoverStart afterwards.
        [HarmonyPatch(typeof(SkillsDialog), nameof(SkillsDialog.Setup)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Bind(SkillsDialog __instance, Player player)
        {
            var skills = player.GetSkills().GetSkillList(); var elements = Elements(__instance);
            for (int index = 0; index < elements.Count; index++)
            {
                var tooltip = elements[index].GetComponentInChildren<UITooltip>(true);
                if (!tooltip) continue;
                if (Rows.TryGetValue(tooltip, out var previous)) tooltip.m_tooltipPrefab = previous.OriginalPrefab;
                Rows.Remove(tooltip);
                bool supported = index < skills.Count && Supported(skills[index].m_info.m_skill);
                if (supported)
                {
                    Rows.Add(tooltip, new Row { Player = player, Skill = skills[index], OriginalPrefab = tooltip.m_tooltipPrefab });
                    tooltip.m_tooltipPrefab = InventoryGui.instance.m_playerGrid.m_elementPrefab.GetComponent<UITooltip>().m_tooltipPrefab;
                }
                // A row whose tooltip opened during setup, in the window of the template it had then, opens again in its own.
                if (ReferenceEquals(Current.GetValue(null), tooltip)) TooltipWindows.Replace(tooltip);
                if (supported) tooltip.Set(Localization.instance.Localize("$skill_" + skills[index].m_info.m_skill.ToString().ToLowerInvariant()), tooltip.m_text);
            }
        }

        [HarmonyPatch(typeof(UITooltip), nameof(UITooltip.OnHoverStart)), HarmonyPrefix]
        private static void Hover(UITooltip __instance)
        {
            if (Rows.TryGetValue(__instance, out var row) && row.Player)
            {
                if (ReferenceEquals(Current.GetValue(null), __instance) && Time.unscaledTime < row.RefreshAt) return;
                __instance.m_text = Text(row.Player, row.Skill); row.RefreshAt = Time.unscaledTime + .5f;
            }
        }

        [HarmonyPatch(typeof(UITooltip), "LateUpdate"), HarmonyPostfix]
        private static void Refresh(UITooltip __instance)
        {
            if (!ReferenceEquals(Current.GetValue(null), __instance) || !Rows.TryGetValue(__instance, out var row) || !row.Player || Time.unscaledTime < row.RefreshAt) return;
            row.RefreshAt = Time.unscaledTime + .5f;
            string text = Text(row.Player, row.Skill);
            if (__instance.m_text == text) return;
            __instance.m_text = text; UpdateText(__instance);
        }

        private static string Number(float value) => value.ToString("0.##", CultureInfo.CurrentCulture);
        private static string Percent(float value) => Number(value * 100f) + "%";
        private static string Multiplier(float value) => Number(value) + "x";
        private static string Value(string label, string value) => label + ": <color=orange>" + value + "</color>";
        private static bool Supported(Skills.SkillType skill) => SkillDamageRange.DamageSkills.Contains(skill) ||
            new[] { Skills.SkillType.Blocking, Skills.SkillType.Run, Skills.SkillType.Jump, Skills.SkillType.Swim, Skills.SkillType.Sneak,
                Skills.SkillType.Dodge, Skills.SkillType.Fishing, Skills.SkillType.Farming, Skills.SkillType.Cooking, Skills.SkillType.Crafting, Skills.SkillType.Ride }.Contains(skill) || IsSailing(skill);

        internal static string Text(Player player, Skills.Skill skill)
        {
            var skills = player.GetSkills(); var data = SkillData(skills); var known = new HashSet<Skills.SkillType>(data.Keys);
            try
            {
                var type = skill.m_info.m_skill; float factor = skills.GetSkillFactor(type);
                float statusLevel = skill.m_level; player.GetSEMan().ModifySkillLevel(type, ref statusLevel);
                float enchantment = AddSkillLevel_Skills_GetSkillFactor_Patch.SkillIncrease(player, type);
                var rows = new List<string> { Localization.instance.Localize(skill.m_info.m_description), "", Value("Trained level", Number(skill.m_level)) };
                // Only bonuses that change the level and a fortified level above 0 are listed, and the effective level only when it differs.
                void Level(string label, float value, float omittedAt = 0f) { if (Mathf.Abs(value - omittedAt) >= .005f) rows.Add(Value(label, Number(value))); }
                Level("Equipment/status level bonus", statusLevel - skill.m_level);
                Level("Enchantment level bonus", enchantment);
                var fortify = PluginIds.TryGet(PluginIds.FortifySkills, out var plugin) ? plugin.Instance.GetType().Assembly.GetType("FortifySkillsReborn.FortifySkillData") : null;
                var fortified = fortify == null ? null : AccessTools.Field(fortify, "s_FortifySkills")?.GetValue(null) as IDictionary;
                if (fortified != null && fortified.Contains(type)) Level("Fortified level", (float)AccessTools.Field(fortify, "FortifyLevel").GetValue(fortified[type]));
                Level("Effective level", player.GetSkillLevel(type), skill.m_level);
                rows.Add("");
                if (SkillDamageRange.DamageSkills.Contains(type))
                {
                    skills.GetRandomSkillRange(out float minimum, out float maximum, type);
                    rows.Add(Value(type == Skills.SkillType.WoodCutting ? "Chop damage roll" : type == Skills.SkillType.Pickaxes ? "Mining damage roll" : "Skill damage roll", Percent(minimum) + "–" + Percent(maximum) + " of base"));
                    if (type != Skills.SkillType.WoodCutting) rows.Add(Value("Attack cost reduction from skill", Percent(.33f * factor)));
                    WeaponSkill(rows, player, type, factor);
                    Weapon(rows, player, type);
                }
                Effects(rows, player, type, factor);
                if (EpicLootSkillLevels.CarryContribution(skills, type, out float carry)) rows.Add(Value("Carry capacity from this skill", Number(carry) + " kg"));
                return string.Join("\n", rows);
            }
            finally
            {
                // Native getters create entries for untrained skills, including conversion source skills.
                foreach (var added in data.Keys.Where(key => !known.Contains(key)).ToArray()) data.Remove(added);
            }
        }

        // What the skill does for the craftable weapons that use it, whichever is held: bow draws, reloads, and the summons
        // and shields of staves, which the caster's level sets when cast.
        private static void WeaponSkill(List<string> rows, Player player, Skills.SkillType skill, float factor)
        {
            var items = ObjectDB.instance.m_items.Select(prefab => prefab.GetComponent<ItemDrop>()?.m_itemData)
                .Where(item => item?.m_shared.m_attack != null && ObjectDB.instance.GetRecipe(item) != null).ToList();
            var own = items.Where(item => item.m_shared.m_skillType == skill).ToList();
            // Humanoid.GetAttackDrawPercentage and ItemData.GetWeaponLoadingTime; an untrained skill leaves both at their base.
            if (factor > 0f && own.Any(item => item.m_shared.m_attack.m_bowDraw)) rows.Add(Value("Draw time from skill", Percent(Mathf.Lerp(1f, .2f, factor)) + " of base"));
            if (factor > 0f && own.Any(item => item.m_shared.m_attack.m_requiresReload)) rows.Add(Value("Reload time from skill", Percent(Mathf.Lerp(1f, .5f, factor)) + " of base"));
            float level = player.GetSkillLevel(skill);
            void Add(string row) { if (!rows.Contains(row)) rows.Add(row); }
            foreach (var item in items)
            {
                string name = Localization.instance.Localize(item.m_shared.m_name);
                var spawn = MagicItemTooltips.Summoning(item);
                // SpawnAbility.Spawn stores 1 + level × factor on the summon, which multiplies the damage it deals.
                if (spawn && spawn.m_copySkill == skill && spawn.m_copySkillToRandomFactor > 0f && level > 0f)
                    Add(Value("Summon damage from skill", Multiplier(1f + level * spawn.m_copySkillToRandomFactor)));
                string followers = spawn ? Followers(player, spawn, skill, level) : null;
                if (followers != null) Add(Value(name + " follower limit", followers));
                // SE_Shield.SetLevel adds the caster's level times the shield's per-level absorption.
                var shield = Shield(item, skill);
                if (shield && shield.m_absorbDamagePerSkillLevel != 0f)
                {
                    float absorb = shield.m_absorbDamage + shield.m_absorbDamagePerSkillLevel * level;
                    if (Game.m_worldLevel > 0) absorb += shield.m_absorbDamageWorldLevel * Game.m_worldLevel;
                    Add(Value(name + " shield absorbs", Number(absorb) + " damage"));
                }
            }
        }

        // SpawnAbility.Spawn takes the follower limit from the last level-up setting the caster's level reaches, unless the
        // staff's quality sets it. The settings' summon levels are all 1, so the skill adds no stars.
        private static string Followers(Player player, SpawnAbility spawn, Skills.SkillType skill, float level)
        {
            var settings = spawn.m_levelUpSettings;
            if (!spawn.m_commandOnSpawn || spawn.m_setMaxInstancesFromWeaponLevel || settings == null || !settings.Any(setting => setting.m_skill == skill)) return null;
            var reached = settings.AsEnumerable().Reverse().FirstOrDefault(setting => player.GetSkillLevel(setting.m_skill) >= setting.m_skillLevel);
            if (reached == null || reached.m_maxSpawns <= 0) return null;
            var next = settings.Where(setting => setting.m_skill == skill && setting.m_skillLevel > level && setting.m_maxSpawns > reached.m_maxSpawns)
                .OrderBy(setting => setting.m_skillLevel).FirstOrDefault();
            return reached.m_maxSpawns + (next == null ? "" : "; " + next.m_maxSpawns + " at level " + next.m_skillLevel);
        }

        // The shield a staff's cast applies through an area effect trained by this skill. Aoe.Setup gives an area effect
        // that uses its attack's settings the attack's skill.
        private static SE_Shield Shield(ItemDrop.ItemData item, Skills.SkillType skill)
        {
            var attack = item.m_shared.m_attack; var projectile = attack.m_attackProjectile;
            var landing = projectile && projectile.TryGetComponent<Projectile>(out var shot) ? shot.m_spawnOnHit : null;
            foreach (var launched in new[] { projectile, attack.m_spawnOnTrigger, landing })
            {
                var area = launched ? launched.GetComponentInChildren<Aoe>(true) : null;
                if (!area || (area.m_useAttackSettings ? item.m_shared.m_skillType : area.m_skill) != skill) continue;
                foreach (string effect in new[] { area.m_statusEffect, area.m_statusEffectIfPlayer })
                    if (!string.IsNullOrEmpty(effect) && ObjectDB.instance.GetStatusEffect(effect.GetStableHashCode()) is SE_Shield shield) return shield;
            }
            return null;
        }

        private static void Weapon(List<string> rows, Player player, Skills.SkillType skill)
        {
            var item = player.GetCurrentWeapon();
            if (item == null) return;
            var damageSkill = WeaponDamagePreview.Skill(player, item);
            bool chopping = skill == Skills.SkillType.WoodCutting && item.m_shared.m_attack.m_specialHitSkill == skill;
            if (item.m_shared.m_skillType != skill && damageSkill != skill && !chopping) return;
            var section = new List<string>();
            var previousItem = WeaponTooltipRange.Item; var previousHands = HandOverride.Active;
            try
            {
                // Read the held weapon as Active effects does, with the ammunition a shot would load.
                WeaponTooltipRange.Item = item; WeaponTooltip.AsIfEquipped(item);
                ItemDrop.ItemData ammunition = null; bool conjured = false;
                var range = chopping ? WeaponDamagePreview.ToolRange(player, item, skill) : WeaponDamagePreview.Range(player, item, out ammunition, out conjured);
                string damage = chopping ? Number(range.Low.m_chop) + "–" + Number(range.High.m_chop) : skill == Skills.SkillType.Pickaxes ?
                    Number(range.Low.m_pickaxe) + "–" + Number(range.High.m_pickaxe) : WeaponDamagePreview.FormatRange(range.Low, range.High);
                // A staff that only casts, such as the protection staff, has no hit damage to show.
                if (damage != "0" && damage != "0–0")
                    section.Add(Value(chopping ? "Current chop damage" : skill == Skills.SkillType.Pickaxes ? "Current mining damage" : "Current hit damage" + WeaponDamagePreview.PerProjectile(player, item), damage));
                if (damageSkill != item.m_shared.m_skillType) section.Add(Value("Damage skill", Localization.instance.Localize("$skill_" + damageSkill.ToString().ToLowerInvariant())));
                if (!string.IsNullOrEmpty(item.m_shared.m_ammoType)) section.Add(Value("Ammunition", conjured ? "Conjured arrows" : ammunition != null ?
                    Localization.instance.Localize(ammunition.m_shared.m_name) : "None"));
                // Costs, reloading, barriers and summons stay on the item's own tooltip.
                if (item.m_shared.m_attack.m_bowDraw)
                {
                    float draw = FullDrawTime(player);
                    if (draw > 0f) section.Add(Value("Full draw time", Number(draw) + " s"));
                }
            }
            finally { WeaponTooltipRange.Item = previousItem; HandOverride.Active = previousHands; }
            if (section.Count == 0) return;
            rows.Add("With " + Localization.instance.Localize(item.m_shared.m_name) + ":");
            rows.AddRange(section);
        }

        // The game's draw percentage carries EpicLoot's Quick Draw and a converted bow's skill. A short probe stays
        // below the full draw, so the percentage gives the draw duration; 0 when the bow draws at once.
        private static float FullDrawTime(Player player)
        {
            const float probe = .001f;
            float drawn = DrawTime(player);
            try
            {
                DrawTime(player) = probe;
                float share = player.GetAttackDrawPercentage();
                return share > 0f && share < 1f ? probe / share : 0f;
            }
            finally { DrawTime(player) = drawn; }
        }

        private static void Effects(List<string> rows, Player player, Skills.SkillType skill, float factor)
        {
            var effects = player.GetSEMan(); float movement = player.GetEquipmentMovementModifier();
            // Wind-dependent effects read the direction of travel; the tooltip takes the way the player faces.
            var facing = player.transform.forward;
            void Add(string label, float value, string unit = "") => rows.Add(Value(label, Number(value) + unit));
            switch (skill)
            {
                case Skills.SkillType.Run:
                    rows.Add(Value("Run speed from skill", Multiplier(1f + .25f * factor)));
                    // Treadwell scales a running player's speed, and always the run drain, by the ground underfoot.
                    bool running = Running(player); float speed;
                    try { Running(player) = true; speed = player.m_runSpeed * RunFactor(player); }
                    finally { Running(player) = running; }
                    effects.ApplyStatusEffectSpeedMods(ref speed, facing);
                    Add("Current running speed", speed, " m/s");
                    rows.Add(Value("Run drain reduction from skill", Percent(.5f * Mathf.Clamp01(factor))));
                    // Player.CheckRun applies the movement and run modifiers one after the other, then status effects.
                    float run = player.m_runStaminaDrain * Mathf.Lerp(1f, .5f, factor);
                    run -= run * movement; run += run * player.GetEquipmentRunStaminaModifier();
                    effects.ModifyRunStaminaDrain(run, ref run, facing);
                    Add("Current running stamina", run * MoveStamina, "/s"); break;
                case Skills.SkillType.Jump:
                    rows.Add(Value("Jump strength from skill", Multiplier(1f + .4f * factor)));
                    var jump = Vector3.up * player.m_jumpForce * (1f + .4f * factor); effects.ApplyStatusEffectJumpMods(ref jump);
                    if (MagicItem.ShowEffectDetails) Add("Current upward jump speed", jump.y, " m/s");
                    float jumpCost = player.m_jumpStaminaUsage - player.m_jumpStaminaUsage * movement + player.m_jumpStaminaUsage * player.GetEquipmentJumpStaminaModifier();
                    effects.ModifyJumpStaminaUsage(jumpCost, ref jumpCost); Add("Current jump stamina", jumpCost * MoveStamina, " per jump"); break;
                case Skills.SkillType.Swim:
                    rows.Add(Value("Swim speed from skill", Multiplier(1f + .25f * factor)));
                    float swimSpeed = SwimSpeed.WithSkill(player), swimTimer = SwimTimer(player);
                    // Status effects apply their swimming rules, such as half of a general speed bonus, only in water.
                    try { SwimTimer(player) = 0f; effects.ApplyStatusEffectSpeedMods(ref swimSpeed, facing); }
                    finally { SwimTimer(player) = swimTimer; }
                    Add("Current swimming speed", swimSpeed, " m/s");
                    float swim = Mathf.Lerp(player.m_swimStaminaDrainMinSkill, player.m_swimStaminaDrainMaxSkill, factor);
                    swim += swim * player.GetEquipmentSwimStaminaModifier(); effects.ModifySwimStaminaUsage(swim, ref swim);
                    Add("Current moving swim stamina", swim * MoveStamina, "/s"); break;
                case Skills.SkillType.Sneak:
                    float sneakFactor = Mathf.Lerp(1f, .25f, Mathf.Sqrt(factor));
                    rows.Add(Value("Sneak drain reduction from skill", Percent(1f - sneakFactor)));
                    float sneak = player.m_sneakStaminaDrain * sneakFactor * (1f + player.GetEquipmentSneakStaminaModifier());
                    effects.ModifySneakStaminaUsage(sneak, ref sneak); Add("Current moving sneak stamina", sneak * Game.m_staminaRate, "/s");
                    float dark = Mathf.Lerp(.5f, .2f, factor), bright = Mathf.Lerp(1f, .6f, factor);
                    effects.ModifyStealth(dark, ref dark); effects.ModifyStealth(bright, ref bright);
                    rows.Add(Value("Visual detection in darkness / bright light", Multiplier(Mathf.Clamp01(dark)) + " / " + Multiplier(Mathf.Clamp01(bright)))); break;
                case Skills.SkillType.Dodge:
                    rows.Add(Value("Roll drain reduction from skill", Percent(.5f * Mathf.Clamp01(factor))));
                    Add("Current roll stamina", DodgeCost(player) * Game.m_staminaRate, " per roll"); break;
                case Skills.SkillType.Blocking:
                    rows.Add(Value("Block armor from skill", Multiplier(1f + .5f * factor)));
                    var blocker = BlockedHits.CurrentBlocker(player);
                    if (blocker != null)
                    {
                        rows.Add("With " + Localization.instance.Localize(blocker.m_shared.m_name) + ":");
                        Add("Block armor", blocker.GetBlockPower(factor));
                        if (blocker.m_shared.m_timedBlockBonus > 1f)
                        {
                            float timer = BlockTimer(player);
                            try
                            {
                                BlockTimer(player) = 0f;
                                float parry = blocker.GetBlockPower(factor) * blocker.m_shared.m_timedBlockBonus;
                                effects.ModifyTimedBlockBonus(ref parry); Add("Parry armor", parry);
                            }
                            finally { BlockTimer(player) = timer; }
                        }
                    }
                    break;
                case Skills.SkillType.Fishing:
                    var bobber = Bobber();
                    if (bobber)
                    {
                        // FishingFloat.FixedUpdate pays both drains, which EpicLoot's fishing stamina reduction lowers.
                        float paid = Game.m_staminaRate * Mathf.Max(0f, 1f - player.GetTotalActiveMagicEffectValue(MagicEffectType.ReduceFishingStaminaCost, .01f));
                        rows.Add(Value("Reeling drain reduction from skill", Percent(1f - Mathf.Lerp(1f, bobber.m_pullStaminaUseMaxSkillMultiplier, factor))));
                        float empty = Mathf.Lerp(bobber.m_pullStaminaUse, bobber.m_pullStaminaUse * bobber.m_pullStaminaUseMaxSkillMultiplier, factor) * paid;
                        if (empty > 0f) Add("Base reeling stamina", empty, "/s");
                        Add("Stamina while a fish is hooked", Mathf.Lerp(bobber.m_hookedStaminaPerSec, bobber.m_hookedStaminaPerSecMaxSkill, factor) * paid, "/s");
                        Add("Reeling speed", Mathf.Lerp(bobber.m_pullLineSpeed, bobber.m_pullLineSpeedMaxSkill, factor), " m/s");
                    }
                    break;
                case Skills.SkillType.Farming:
                case Skills.SkillType.Crafting:
                case Skills.SkillType.Cooking:
                    if (skill == Skills.SkillType.Farming)
                    {
                        var chances = ZNetScene.instance.m_prefabs.Select(prefab => prefab.GetComponent<Pickable>())
                            .Where(pickable => pickable && pickable.m_pickRaiseSkill == skill && pickable.m_bonusYieldAmount > 0)
                            .Select(pickable => Mathf.Clamp01(pickable.m_maxLevelBonusChance * factor)).Distinct().OrderBy(chance => chance).ToArray();
                        if (chances.Length > 0) rows.Add(Value("Bonus harvest chance", Percent(chances[0]) + (chances.Length > 1 ? "–" + Percent(chances[chances.Length - 1]) + ", depending on crop" : "")));
                    }
                    else
                    {
                        rows.Add(Value("Bonus output chance", Percent(Mathf.Clamp01(InventoryGui.instance.m_craftBonusChance * factor))));
                        rows.Add(Value("Table work time remaining from skill", Percent(1f - InventoryGui.instance.m_craftDurationSkillMaxDecrease * factor)));
                    }
                    var tool = skill == Skills.SkillType.Cooking ? ObjectDB.instance.m_items.Select(prefab => prefab.GetComponent<ItemDrop>()?.m_itemData).FirstOrDefault(ServingTrayStacks.IsTray) :
                        ObjectDB.instance.GetItemPrefab(skill == Skills.SkillType.Farming ? "Cultivator" : "Hammer").GetComponent<ItemDrop>().m_itemData;
                    if (tool != null)
                    {
                        if (tool.m_shared.m_buildPieces && tool.m_shared.m_buildPieces.m_skill == skill)
                            rows.Add(Value("Work stamina remaining from skill", Percent(1f - .5f * factor)));
                        if (tool.m_shared.m_placementDurabilitySkill == skill)
                            rows.Add(Value(skill == Skills.SkillType.Cooking ? "Serving tray wear remaining from skill" : "Tool wear remaining from skill", Percent(1f - tool.m_shared.m_placementDurabilityMax * factor)));
                        var held = player.GetCurrentWeapon();
                        if (held?.m_shared.m_buildPieces && held.m_shared.m_buildPieces.m_skill == skill)
                        {
                            float work = held.m_shared.m_attack.m_attackStamina * (1f + player.GetEquipmentHomeItemModifier());
                            effects.ModifyHomeItemStaminaUsage(work, ref work); work *= 1f - .5f * factor;
                            Add("Current work stamina", work * Game.m_staminaRate);
                            Add("Current tool wear per placement", held.m_shared.m_useDurabilityDrain * (1f - held.m_shared.m_placementDurabilityMax * player.GetSkillFactor(held.m_shared.m_placementDurabilitySkill)) * Game.m_durabilityRate);
                        }
                        if (skill == Skills.SkillType.Farming && tool.m_shared.m_buildPieces)
                            foreach (var prefab in tool.m_shared.m_buildPieces.m_pieces)
                            {
                                var piece = prefab.GetComponent<Piece>();
                                if (piece && piece.m_harvest) Add("Harvest radius of " + Localization.instance.Localize(piece.m_name), Mathf.Lerp(piece.m_harvestRadius, piece.m_harvestRadiusMaxLevel, factor), " m");
                            }
                    }
                    // Attack.DoMeleeAttack harvests around a harvesting attack, such as the scythe's, by Farming.
                    if (skill == Skills.SkillType.Farming)
                        foreach (var item in ObjectDB.instance.m_items.Select(prefab => prefab.GetComponent<ItemDrop>()?.m_itemData).Where(item => item != null))
                            foreach (var attack in new[] { item.m_shared.m_attack, item.m_shared.m_secondaryAttack })
                                if (attack != null && attack.m_harvest)
                                {
                                    Add("Harvest radius with " + Localization.instance.Localize(item.m_shared.m_name), Mathf.Lerp(attack.m_harvestRadius, attack.m_harvestRadiusMaxLevel, factor), " m");
                                    break;
                                }
                    if (skill == Skills.SkillType.Crafting)
                        rows.Add(Value("New cart health when built", Multiplier(CraftingCartHealth.HealthFactor(CraftingCartHealth.BuilderFactor(player)))));
                    break;
                case Skills.SkillType.Ride:
                    rows.Add(Value("Mount run speed from skill", Multiplier(1f + .25f * factor)));
                    rows.Add(Value("Mount stamina drain reduction", Percent(.5f * Mathf.Clamp01(factor)))); break;
            }
            if (IsSailing(skill)) Sailing(rows, player, skill);
        }

        // The float the fishing rod casts; any other float only if the rod's moved.
        private static FishingFloat Bobber()
        {
            var rod = ObjectDB.instance.GetItemPrefab("FishingRod")?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_attack.m_attackProjectile;
            var bobber = rod ? rod.GetComponent<FishingFloat>() : null;
            return bobber ? bobber : ZNetScene.instance.m_prefabs.Select(prefab => prefab.GetComponent<FishingFloat>()).FirstOrDefault(value => value);
        }

        private static bool IsSailing(Skills.SkillType skill)
        {
            if (!PluginIds.TryGet(PluginIds.Sailing, out var plugin)) return false;
            var sailing = AccessTools.Field(plugin.Instance.GetType(), "sailing")?.GetValue(null);
            var definition = sailing == null ? null : AccessTools.Field(sailing.GetType(), "skillDef")?.GetValue(sailing) as Skills.SkillDef;
            return definition != null && definition.m_skill == skill;
        }

        private static void Sailing(List<string> rows, Player player, Skills.SkillType skill)
        {
            if (!PluginIds.TryGet(PluginIds.Sailing, out var plugin)) return;
            var type = plugin.Instance.GetType(); var sailing = AccessTools.Field(type, "sailing").GetValue(null);
            float factor = player.GetSkillFactor(skill) * (float)AccessTools.Property(sailing.GetType(), "SkillEffectFactor").GetValue(sailing);
            var speeds = (IDictionary)AccessTools.Field(type, "shipSpeedIncrease").GetValue(null);
            foreach (DictionaryEntry speed in speeds)
            {
                string row = Value(Localization.instance.Localize((string)speed.Key) + " sail force", Multiplier(1f + factor * ((ConfigEntry<float>)speed.Value).Value));
                if (!rows.Contains(row)) rows.Add(row);
            }
            float Setting(string name) => ((ConfigEntry<float>)AccessTools.Field(type, name).GetValue(null)).Value;
            rows.Add(Value("New ship health when built", Multiplier(1f + factor * Setting("shipHealthIncrease"))));
            rows.Add(Value("Exploration radius while attached to a ship", Multiplier(1f + factor * Setting("explorationRadiusIncrease"))));
        }
    }
}
