using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Reflection.Emit;
using System.Text;
using EpicLoot;
using EpicLoot.LegendarySystem;
using EpicLoot.MagicItemEffects;
using EpicLoot.MagicItemEffects.Shards;
using EpicLoot.ShardStones;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The enchantment tooltips are EpicLoot's English override in the pack's client configs,
    // BepInEx/config/EpicLoot/localizations/English.json. This supplies the values that text shows beyond the stored roll,
    // read from EpicLoot's effect config and the add-on's settings, and the Shift-details lines that depend on the player.
    internal static class EffectTooltips
    {
        private static readonly AccessTools.FieldRef<Dictionary<string, Func<float, object[]>>> Providers =
            AccessTools.StaticFieldRefAccess<Dictionary<string, Func<float, object[]>>>(AccessTools.Field(typeof(MagicItem), "DisplayValueProviders"));
        private static readonly Func<Player, ItemRarity> IcyRetributionRarity = AccessTools.MethodDelegate<Func<Player, ItemRarity>>(
            AccessTools.Method(typeof(ModerIcyRetribution), "GetEffectRarity"));

        internal static void Initialize()
        {
            // EpicLoot's multi-shot defaults apply when the config leaves a value out.
            Extend(MagicEffectType.TripleBowShot, value => new object[] { Percent(MagicEffectType.TripleBowShot, MultiShot.DAMAGE_KEY, 0.4f),
                Mathf.RoundToInt(EffectConfig.Get(MagicEffectType.TripleBowShot, MultiShot.PROJECTILES_KEY, 3f)),
                Percent(MagicEffectType.TripleBowShot, MultiShot.ACCURACY_KEY, 1.25f) - 100f,
                Mathf.Max(0f, EffectConfig.Get(MagicEffectType.TripleBowShot, MultiShot.COSTSCALE_KEY, 2f) - 1f) });
            Extend(MagicEffectType.DoubleMagicShot, value => new object[] {
                Percent(MagicEffectType.DoubleMagicShot, MultiShot.CHANCE_KEY, 1f), Percent(MagicEffectType.DoubleMagicShot, MultiShot.DAMAGE_KEY, 0.66f),
                Percent(MagicEffectType.DoubleMagicShot, MultiShot.ACCURACY_KEY, 1.2f) - 100f,
                Mathf.RoundToInt(EffectConfig.Get(MagicEffectType.DoubleMagicShot, MultiShot.PROJECTILES_KEY, 2f)),
                Mathf.Max(0f, EffectConfig.Get(MagicEffectType.DoubleMagicShot, MultiShot.COSTSCALE_KEY, 2f) - 1f) });
            Extend(MagicEffectType.EitrImbueAttack, value => new object[] { Mathf.Max(0f, EffectConfig.Get(MagicEffectType.EitrImbueAttack, "EitrCostPerDamage", 1f)) * Game.m_eitrRate });
            Extend(MagicEffectType.Andvaranaut, value => new object[] { Plugin.AndvaranautMaxRange.Value, Plugin.AndvaranautSearchRadius.Value });
            Extend(MagicEffectType.Riches, value => new object[] { Plugin.LootBonusRadius.Value });
            Extend(MagicEffectType.Luck, value => new object[] { Plugin.LootBonusRadius.Value });
            Extend(MagicEffectType.LuckyLoot, value => new object[] { Plugin.LootBonusRadius.Value });
            Extend(MagicEffectType.DaySailingSpeed, value => new object[] { SailingSpeedText(value) });
            Extend(MagicEffectType.SailingSpeed, value => new object[] { SailingSpeedText(value) });
            Extend(MagicEffectType.StrikeCausesLightning, value => new object[] {
                value * EffectConfig.Get(MagicEffectType.StrikeCausesLightning, "DamagePerValue", 6f), EffectConfig.Get(MagicEffectType.StrikeCausesLightning, "Radius", 1f) });
            Extend(MagicEffectType.IcyRetribution, value => new object[] { EffectConfig.Get(MagicEffectType.IcyRetribution, "Radius", 8f), IcyRetributionCooldown(value) });
            Extend(MagicEffectType.SummonBatWhenActivatingAdrenaline, value =>
            {
                // As SummonBatWhenActivatingAdrenaline.SummonBats counts them.
                int bats = Math.Max(1, Mathf.RoundToInt(value + EffectConfig.Get(MagicEffectType.SummonBatWhenActivatingAdrenaline, "SummonCountOffset", -1f)));
                int most = Math.Max(bats, Mathf.RoundToInt(bats * EffectConfig.Get(MagicEffectType.SummonBatWhenActivatingAdrenaline, "ConcurrentPerSummon", 2f)));
                return new object[] { bats == 1 ? "1 bat" : bats.ToString(CultureInfo.CurrentCulture) + " bats", (float)most };
            });
        }

        // Appends to the effect's existing display values, so the text's {0} and EpicLoot's own values stay where they were.
        private static void Extend(string effectType, Func<float, object[]> extra)
        {
            Providers().TryGetValue(effectType, out var existing);
            MagicItem.RegisterDisplayValues(effectType, value => (existing?.Invoke(value) ?? new object[] { value }).Concat(extra(value)).ToArray());
        }

        private static float Percent(string effectType, string key, float fallback) => EffectConfig.Get(effectType, key, fallback) * 100f;

        internal static string SailingSpeedText(float sailForcePercent) =>
            (100d * (Math.Sqrt(1d + sailForcePercent / 100d) - 1d)).ToString("0.0", CultureInfo.InvariantCulture);

        private static string HarvestText(string type, float value) =>
            (type == MagicEffectType.IncreaseMiningDrop ? "Mining" : "Woodcutting") + " drops: " +
            value.ToString("+0.######;-0.######;+0", CultureInfo.InvariantCulture) + " extra rolls";

        private static float PreviewValue(ItemDrop.ItemData preview, string type, float value)
        {
            if (!AffixDisplayValues.IsDropCount(type) && type != MagicEffectType.DaySailingSpeed && type != MagicEffectType.SailingSpeed) return value;
            var inventory = OpenSockets.Row;
            var magic = OpenSockets.Item?.GetMagicItem();
            if (inventory != null && magic != null && inventory.ContainsItem(preview))
            {
                int index = SocketPositions.Index(OpenSockets.Item, preview.m_gridPos.x);
                if (index >= 0 && index < magic.Sockets.Count && magic.Sockets[index].Effect?.EffectType == type)
                    return magic.Sockets[index].Effect.EffectValue;
            }
            return value;
        }

        private static string ShardDetails(MagicItemEffect effect, ItemRarity rarity, string legendary,
            MagicItemEffectDefinition.ValueDef values, string indent)
        {
            string text = MagicItem.GetEffectDetailBlock(effect, rarity, legendary, values, indent);
            // A stone's fractional drop count adds into the worn total before rounding, so the per-roll description would mislead.
            if (AffixDisplayValues.IsDropCount(effect.EffectType))
            {
                var definition = MagicItemEffectDefinitions.Get(effect.EffectType);
                text = text.Replace(indent + MagicItem.GetEffectDescription(definition, effect.EffectValue) + "\n", "");
            }
            return text;
        }

        [HarmonyPatch(typeof(MagicItem), nameof(MagicItem.GetSocketEffect))]
        private static class SocketSource
        {
            private static void Postfix(MagicItem __instance, int __0, MagicItemEffect __result) =>
                SocketDetails.RememberSocket(__instance, __0, __result);
        }

        // EpicLoot describes each socket of an item's tooltip here; its first argument is the item's magic.
        [HarmonyPatch]
        private static class SocketDetails
        {
            private sealed class Source { internal SocketedEffect Socket; }
            private static readonly ConditionalWeakTable<MagicItemEffect, Source> sources = new ConditionalWeakTable<MagicItemEffect, Source>();

            internal static void RememberSocket(MagicItem __instance, int __0, MagicItemEffect __result)
            {
                if (__result != null && __0 >= 0 && __0 < __instance.Sockets.Count)
                    sources.GetValue(__result, _ => new Source()).Socket = __instance.Sockets[__0];
            }

            private static MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GatherSockets");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var detail = AccessTools.Method(typeof(MagicItem), nameof(MagicItem.GetEffectDetailBlock));
                var text = AccessTools.Method(typeof(MagicItem), nameof(MagicItem.GetEffectText),
                    new[] { typeof(MagicItemEffect), typeof(ItemRarity), typeof(bool), typeof(string) });
                int details = 0, texts = 0;
                foreach (var instruction in instructions)
                {
                    string replacement = null;
                    if (instruction.Calls(detail)) { details++; replacement = nameof(Detail); }
                    else if (instruction.Calls(text)) { texts++; replacement = nameof(Text); }
                    if (replacement != null)
                    {
                        yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                        instruction.operand = AccessTools.Method(typeof(SocketDetails), replacement);
                    }
                    yield return instruction;
                }
                if (details != 1 || texts != 1) throw new InvalidOperationException("EpicLoot's socket tooltip calls changed.");
            }

            private static SocketedEffect Socket(MagicItem owner, MagicItemEffect effect) =>
                sources.TryGetValue(effect, out var source) && owner.Sockets.Contains(source.Socket) ? source.Socket :
                owner.Sockets?.FirstOrDefault(socket => socket != null && ReferenceEquals(socket.Effect, effect));

            private static string Text(MagicItemEffect effect, ItemRarity rarity, bool showRange, string legendary, MagicItem owner)
            {
                var socket = Socket(owner, effect);
                return socket != null && socket.ShardType != ShardType.None && AffixDisplayValues.IsDropCount(effect.EffectType)
                    ? HarvestText(effect.EffectType, effect.EffectValue) : MagicItem.GetEffectText(effect, rarity, showRange, legendary);
            }

            private static string Detail(MagicItemEffect effect, ItemRarity rarity, string legendary,
                MagicItemEffectDefinition.ValueDef values, string indent, MagicItem owner)
            {
                var socket = Socket(owner, effect);
                if (socket == null || socket.ShardType == ShardType.None)
                    return MagicItem.GetEffectDetailBlock(effect, rarity, legendary, values, indent);
                return ShardDetails(effect, rarity, legendary, new MagicItemEffectDefinition.ValueDef {
                    MinValue = effect.EffectValue, MaxValue = effect.EffectValue, Increment = 0f }, indent);
            }
        }

        // EpicLoot fills each slot line of a shardstone's tooltip here, from the stone's value for its rarity.
        [HarmonyPatch]
        private static class ShardPreview
        {
            private static MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GetShardSlot");

            private static void Prefix(object __instance, MagicItemEffectDefinition effectDef, ref float value) =>
                value = PreviewValue((ItemDrop.ItemData)MagicTooltipContext.Item.GetValue(__instance), effectDef.Type, value);

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var text = AccessTools.Method(typeof(MagicItem), nameof(MagicItem.GetEffectText), new[] { typeof(MagicItemEffectDefinition), typeof(float) });
                var detail = AccessTools.Method(typeof(MagicItem), nameof(MagicItem.GetEffectDetailBlock));
                int texts = 0, details = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(text)) { instruction.operand = AccessTools.Method(typeof(ShardPreview), nameof(EffectText)); texts++; }
                    else if (instruction.Calls(detail)) { instruction.operand = AccessTools.Method(typeof(EffectTooltips), nameof(ShardDetails)); details++; }
                    yield return instruction;
                }
                if (texts != 1 || details != 1) throw new InvalidOperationException("EpicLoot's shard preview changed; expected one effect text and one detail call.");
            }

            private static string EffectText(MagicItemEffectDefinition definition, float value) =>
                AffixDisplayValues.IsDropCount(definition.Type) ? HarvestText(definition.Type, value) : MagicItem.GetEffectText(definition, value);
        }

        // EpicLoot sets the cooldown from the rarity of the best Moder stone worn. A tooltip shows the stone's own rarity,
        // found from its value; a total of several stones uses the worn rarity, as the game does.
        private static float IcyRetributionCooldown(float value)
        {
            ItemRarity rarity = ItemRarity.Epic;
            var values = ShardEffectValues(MagicEffectType.IcyRetribution);
            if (values != null && values.Any(pair => Mathf.Approximately(pair.Value, value))) rarity = values.First(pair => Mathf.Approximately(pair.Value, value)).Key;
            else if (Player.m_localPlayer != null) rarity = IcyRetributionRarity(Player.m_localPlayer);
            int steps = Math.Max(0, (int)rarity - 2);
            return Mathf.Max(0.1f, Mathf.Max(EffectConfig.Get(MagicEffectType.IcyRetribution, "MinCooldown", 30f),
                EffectConfig.Get(MagicEffectType.IcyRetribution, "BaseCooldown", 200f) - steps * EffectConfig.Get(MagicEffectType.IcyRetribution, "CooldownPerRarity", 20f)));
        }

        private static Dictionary<ItemRarity, float> ShardEffectValues(string effectType)
        {
            if (!MagicItemEffectDefinitions.TryGet(effectType, out var definition) || definition.ValuesPerRarity == null) return null;
            var result = new Dictionary<ItemRarity, float>();
            foreach (ItemRarity rarity in Enum.GetValues(typeof(ItemRarity)))
            {
                var range = definition.GetValuesForRarity(rarity);
                if (range != null) result[rarity] = range.MinValue;
            }
            return result;
        }

        private static readonly HashSet<string> HealthCriticalEffects = new HashSet<string> {
            MagicEffectType.InstantMead, MagicEffectType.AutoMead, MagicEffectType.ModifyLowHealth };
        private static readonly HashSet<string> StaminaRegenEffects = new HashSet<string> {
            MagicEffectType.ModifyStaminaRegen, MagicEffectType.ModifyStaminaRegenLowHealth };
        private static readonly HashSet<string> FixedRestorationEffects = new HashSet<string> {
            MagicEffectType.LifeGainOnHit, MagicEffectType.LifeAfterKill, MagicEffectType.HealthGainPerXDamageDone, MagicEffectType.HealthOnEitrUse,
            MagicEffectType.Conduit, MagicEffectType.LifeGainOnBlock, MagicEffectType.StaminaGainOnBlock,
            MagicEffectType.EitrGainOnBlock, MagicEffectType.EitrWeave, MagicEffectType.Kindling };
        private const string RestorationRule = "Restoration scales with regeneration.";
        private const string MaxResistance = "MaxResistance";
        private static readonly string[] MultiShotSettings = { MultiShot.CHANCE_KEY, MultiShot.DAMAGE_KEY, MultiShot.ACCURACY_KEY, MultiShot.PROJECTILES_KEY, MultiShot.COSTSCALE_KEY },
            StackSettings = { "BuffDuration", "MaxStacks" }, DamageTrigger = { "DamagePerTrigger" };
        // Settings whose values, or the values computed from them, the effect's line or description gives.
        private static readonly Dictionary<string, string[]> DescribedSettings = new Dictionary<string, string[]> {
            [MagicEffectType.TripleBowShot] = MultiShotSettings, [MagicEffectType.DoubleMagicShot] = MultiShotSettings,
            [MagicEffectType.ModifyLowHealth] = new[] { "Max" }, [MagicEffectType.CoinHoarder] = new[] { "LinearGrowth", "LinearCutoff", "Scalar" },
            [MagicEffectType.EitrImbueAttack] = new[] { "EitrCostPerDamage" }, [MagicEffectType.ChanceDoubleDamage] = new[] { "DamageMultiplier" },
            [MagicEffectType.BurdenedBlock] = new[] { "WeightPerBonus", "WeightThreshold" }, [MagicEffectType.BloodDrinker] = new[] { "MaxHealthPercentPerValue", "MinHealthReduction" },
            [MagicEffectType.Bloodrage] = StackSettings, [MagicEffectType.Everflow] = StackSettings, [MagicEffectType.PerfectDodge] = StackSettings,
            [MagicEffectType.PerfectDodgeGivesSpeed] = new[] { "BuffDuration" }, [MagicEffectType.KillsReduceNextBloodCost] = new[] { "BuffDuration", "MaxReduction" },
            [MagicEffectType.AdrenalineFrostWave] = new[] { "Radius" }, [MagicEffectType.AdrenalineIncreasesHealthRegen] = new[] { "SecondsPerPercent" },
            [MagicEffectType.GainAdrenalineWhenApplyingPoison] = new[] { "Radius", "TickInterval" }, [MagicEffectType.StormFury] = new[] { "TickInterval" },
            [MagicEffectType.Conduit] = DamageTrigger, [MagicEffectType.Kindling] = DamageTrigger, [MagicEffectType.HealthGainPerXDamageDone] = DamageTrigger,
            [MagicEffectType.HealthOnEitrUse] = new[] { "EitrPerTrigger" }, [MagicEffectType.CorpseRot] = new[] { "Cooldown", "PoisonPerTier" },
            [MagicEffectType.ForestsAid] = new[] { "BaseRadius", "RadiusPerTier", "Cooldown", "CooldownPerValue", "MinCooldown" },
            [MagicEffectType.IcyRetribution] = new[] { "FrostPerTier", "Radius", "BaseCooldown", "CooldownPerRarity", "MinCooldown" },
            [MagicEffectType.Inspiration] = new[] { "ProcChance" }, [MagicEffectType.LuckWhileFishing] = new[] { "TripleChance" },
            [MagicEffectType.LuckyLoot] = new[] { "MaxMultiplier" }, [MagicEffectType.MeteorSummoner] = new[] { "MaxCharges" },
            [MagicEffectType.RunningOnEmpty] = new[] { "Cooldown" }, [MagicEffectType.ShockingCharge] = new[] { "MaxCharges", "MinCharges", "ChargesPerValue", "DamageFraction" },
            [MagicEffectType.StrikeCausesLightning] = new[] { "DamagePerValue", "Radius" }, [MagicEffectType.Trailblazer] = new[] { "PatchTickInterval" },
            [MagicEffectType.SummonBatWhenActivatingAdrenaline] = new[] { "BatLifetime", "Cooldown", "SummonCountOffset", "ConcurrentPerSummon" },
            [MagicEffectType.TravelLight] = new[] { "CarryWeightPerValue", "MinResultingCarryWeight" } };

        [HarmonyPatch]
        private static class SetBonusDetails
        {
            private static IEnumerable<MethodBase> TargetMethods() => AccessTools.GetDeclaredMethods(typeof(SetDescriptions))
                .Where(method => method.Name == nameof(SetDescriptions.DescribeSetBonusDetail));

            // An effect that also rolls on items shares its text with the roll; a set-only key says what only the set needs.
            private static void Prefix(ref MagicItemEffectDefinition definition)
            {
                string key = "mod_epicloot_set_me_" + definition.Type.ToLowerInvariant() + "_desc";
                if (definition.Description != "$" + key && Localization.instance.Localize("$" + key) != "[" + key + "]")
                    definition = new MagicItemEffectDefinition { Type = definition.Type, Description = "$" + key };
            }

            private static void Postfix(MagicItemEffectDefinition definition, ref string __result)
            {
                if (!MagicItem.ShowEffectDetails) return;
                var extra = DetailLines(definition).ToList();
                if (extra.Count > 0) __result += "\n    " + string.Join("\n    ", extra);
            }
        }

        // EpicLoot's generic description writes X for every value that follows the effect's value, though X stands for that
        // value alone: an Assassin rune read "Cooldown: X seconds". Set runes and the Compendium show the others at each rarity.
        [HarmonyPatch(typeof(MagicItem), nameof(MagicItem.GetEffectDescriptionGeneric))]
        private static class GenericDescription
        {
            private static readonly ItemRarity[] Rarities = { ItemRarity.Legendary, ItemRarity.Mythic, ItemRarity.Ancient };
            private static readonly Func<string, float, object[]> DisplayArgs =
                AccessTools.MethodDelegate<Func<string, float, object[]>>(AccessTools.Method(typeof(MagicItem), "GetDisplayArgs"));
            private static readonly Func<string, string, object[]> GenericArgs =
                AccessTools.MethodDelegate<Func<string, string, object[]>>(AccessTools.Method(typeof(MagicItem), "GetGenericArgs"));

            // One value per rarity, in the rarity colors and format the set's own "X =" list and the text's placeholder use.
            private sealed class RarityValues : IFormattable
            {
                internal IFormattable[] Values;
                public string ToString(string format, IFormatProvider provider) => string.Join(" / ", Rarities.Select((rarity, index) =>
                    "<color=" + global::EpicLoot.EpicLoot.GetRarityColor(rarity) + ">" + Values[index].ToString(format, provider) + "</color>"));
            }

            private static void Postfix(MagicItemEffectDefinition effectDef, string valueToken, ref string __result)
            {
                var values = Rarities.Select(effectDef.GetValuesForRarity).ToArray();
                if (values.Any(value => value == null || value.MinValue != value.MaxValue) || values.All(value => value.MinValue == values[0].MinValue)) return;
                var arguments = GenericArgs(effectDef.Type, valueToken);
                var perRarity = values.Select(value => DisplayArgs(effectDef.Type, value.MinValue)).ToArray();
                bool changed = false;
                for (int index = 1; index < arguments.Length; index++)
                {
                    int position = index;
                    if (!Equals(arguments[index], valueToken) || perRarity.Any(row => !(row.ElementAtOrDefault(position) is IFormattable))) continue;
                    arguments[index] = new RarityValues { Values = perRarity.Select(row => (IFormattable)row[position]).ToArray() };
                    changed = true;
                }
                if (changed) __result = string.Format(Localization.instance.Localize(effectDef.Description), arguments);
            }
        }

        internal static string Number(float value) => ((double)value).ToString("0.##", CultureInfo.CurrentCulture);

        // The Shift-details lines an effect adds after its description.
        internal static IEnumerable<string> DetailLines(MagicItemEffectDefinition definition)
        {
            string type = definition.Type;
            var player = Player.m_localPlayer;
            if (type.EndsWith("LowHealth", StringComparison.Ordinal) || HealthCriticalEffects.Contains(type))
            {
                float threshold = player != null ? ModifyWithLowHealth.GetLowHealthPercentage(player) : ModifyWithLowHealth.LowHealthDefaultThreshold;
                yield return $"Health Critical: at or below {Number(Mathf.Min(threshold, 1f) * 100f)}% health.";
            }
            if (StaminaRegenEffects.Contains(type))
            {
                float bonus = player != null ? ModifyPlayerRegen.GetModifyRegenValue(player, MagicEffectType.ModifyStaminaRegen) : 0f;
                float most = Plugin.StaminaRegenDelayReduction.Value * 100f;
                yield return $"Stamina regeneration starts {Number(most * Mathf.Clamp01(bonus))}% sooner (at most {Number(most)}%).";
            }
            if (FixedRestorationEffects.Contains(type)) yield return RestorationRule;
        }

        // Shared thresholds and benefits appear once when several effects on an item use them.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        private static class SharedDetailsOnce
        {
            private static void Postfix(ref string __result)
            {
                if (string.IsNullOrEmpty(__result)) return;
                var seen = new HashSet<string>();
                __result = string.Join("\n", __result.Split('\n').Where(line =>
                {
                    string shared = line.Contains("Health Critical:") ? "health" :
                        line.Contains("Stamina regeneration starts") ? "stamina" : line.Contains(RestorationRule) ? "restoration" : null;
                    return shared == null || seen.Add(shared);
                }));
            }
        }

        [HarmonyPatch(typeof(MagicItem), nameof(MagicItem.GetEffectDetailBlock))]
        private static class Details
        {
            private static void Postfix(MagicItemEffect effect, string indent, ref string __result)
            {
                if (effect == null || !MagicItemEffectDefinitions.AllDefinitions.TryGetValue(effect.EffectType, out var definition)) return;
                // The pack leaves a description empty where the effect's name says it all; EpicLoot would show a blank line.
                if (!string.IsNullOrEmpty(definition.Description) && __result.StartsWith(indent + "\n"))
                    __result = __result.Substring(indent.Length + 1);
                // EpicLoot's special items and set abilities define no description; the pack's text has one for each.
                if (string.IsNullOrEmpty(definition.Description))
                {
                    string key = "mod_epicloot_me_" + definition.Type.ToLowerInvariant() + "_desc";
                    string description = Localization.instance.Localize("$" + key);
                    if (!string.IsNullOrWhiteSpace(description) && description != "[" + key + "]") __result = indent + MagicItem.GetEffectDescription(
                        new MagicItemEffectDefinition { Type = definition.Type, Description = description }, effect.EffectValue) + "\n" + __result;
                }
                // The pack's texts give the damage reduction cap and the described settings, so their raw setting lines go.
                if (definition.Config != null)
                    foreach (var setting in definition.Config.Where(pair => pair.Key == MaxResistance ||
                        DescribedSettings.TryGetValue(definition.Type, out var keys) && keys.Contains(pair.Key)))
                        __result = __result.Replace($"{indent}{definition.GetConfigLabel(setting.Key)}: {setting.Value:0.###}\n", "");
                var lines = DetailLines(definition).ToList();
                if (lines.Count > 0) __result += string.Concat(lines.Select(line => indent + line + "\n"));
            }
        }

        // The normal hit excludes Eitr Imbue, whose own effect line and description give its spirit damage and impact-time price.
        [HarmonyPatch]
        private static class EitrImbueDamage
        {
            private static MethodBase TargetMethod() => AccessTools.Method(MagicTooltipContext.Gatherer, "GatherDamages");

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var bonus = AccessTools.Method(typeof(EitrImbueAttack), nameof(EitrImbueAttack.GetSpiritBonus));
                int changes = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(bonus))
                    {
                        instruction.operand = AccessTools.Method(typeof(EitrImbueDamage), nameof(Withhold));
                        changes++;
                    }
                    yield return instruction;
                }
                if (changes != 1) throw new InvalidOperationException("EpicLoot's tooltip damage changed; expected one Eitr Imbue bonus.");
            }

            private static float Withhold(MagicItem magicItem, HitData.DamageTypes damage) => 0f;
        }

        // Only an extraction bonus needs an additional row.
        [HarmonyPatch(typeof(RuneUI), "SelectMode")]
        private static class ExtractionPower
        {
            private static void Postfix(RuneUI __instance, object __0)
            {
                if (__0.ToString() != "Extract") return;
                var table = EnchantingTableUI.instance == null ? null : EnchantingTableUI.instance.SourceTable;
                if (table == null || __instance.Warning == null) return;
                float power = table.GetFeatureCurrentValue(EnchantingFeature.Rune).Item2;
                if (power > 100f) __instance.Warning.text += $"\n\nExtraction power: {Number(power)}%";
            }
        }

        // EpicLoot never shows its augment explanation; the effects pane holds it until an effect is chosen.
        [HarmonyPatch(typeof(AugmentUI), nameof(AugmentUI.OnAugmentIndexChanged))]
        private static class AugmentExplanation
        {
            private static void Postfix(AugmentUI __instance)
            {
                if (__instance.AvailableEffectsText != null && string.IsNullOrEmpty(__instance.AvailableEffectsText.text))
                    __instance.AvailableEffectsText.text = Localization.instance.Localize("$mod_epicloot_augment_explain");
            }
        }

        // A lasting mead's tooltip names the refresh window.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetTooltip), typeof(ItemDrop.ItemData), typeof(int), typeof(bool), typeof(float), typeof(int), typeof(bool))]
        private static class LastingMeads
        {
            private static void Postfix(ItemDrop.ItemData item, ref string __result)
            {
                var effect = item?.m_shared.m_consumeStatusEffect;
                float window = Plugin.BuffRefreshWindowPercent.Value;
                if (effect == null || window <= 0f || item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable || item.m_shared.m_food > 0f
                    || !PotionBuffRefresh.Lasting(effect)) return;
                __result += $"\n\nRefresh window: last {Number(window)}% of duration.";
            }
        }
    }
}
