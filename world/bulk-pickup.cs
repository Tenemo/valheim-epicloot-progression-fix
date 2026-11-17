using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using EpicLoot;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class BulkPickup
    {
        private static object config;
        private static PropertyInfo harvestRadius, harvestStyle, modActive, enableBulkHarvest, harvestModifier;
        private static int collisionMask;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.PlantEasily, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var context = assembly.GetType("Advize_PlantEasily.ModContext", true);
            config = AccessTools.Field(context, "config")?.GetValue(null);
            var settings = assembly.GetType("Advize_PlantEasily.ModConfig", true);
            harvestRadius = AccessTools.Property(settings, "HarvestRadius");
            harvestStyle = AccessTools.Property(settings, "HarvestStyle");
            modActive = AccessTools.Property(settings, "ModActive");
            enableBulkHarvest = AccessTools.Property(settings, "EnableBulkHarvest");
            harvestModifier = AccessTools.Property(settings, "KeyboardHarvestModifierKey");
            var mask = AccessTools.Field(assembly.GetType("Advize_PlantEasily.CollisionScanner", true), "CollisionMask");
            var interaction = AccessTools.Method(assembly.GetType("Advize_PlantEasily.InteractPatches", true), "Prefix",
                new[] { typeof(Player), typeof(GameObject), typeof(bool), typeof(bool) });
            var nearby = AccessTools.Method(assembly.GetType("Advize_PlantEasily.ModUtils", true), "FindResourcesInRadius");
            if (config == null || harvestRadius?.PropertyType != typeof(float) || harvestStyle == null ||
                modActive?.PropertyType != typeof(bool) || enableBulkHarvest?.PropertyType != typeof(bool) ||
                harvestModifier?.PropertyType != typeof(KeyCode) ||
                mask?.FieldType != typeof(int) || interaction == null || nearby?.ReturnType != typeof(HashSet<Interactable>))
                throw new InvalidOperationException("PlantEasily's bulk-harvest settings or interaction methods changed.");
            var plantRadius = plugin.Instance.Config[new ConfigDefinition("Harvesting", "HarvestRadius")];
            void ApplyRadius(object sender, EventArgs arguments)
            {
                if (Plugin.HarvestRadius.Value > 0f) plantRadius.BoxedValue = Plugin.HarvestRadius.Value;
            }
            if (Plugin.HarvestRadius.Value > 0f) IntegrationChanges.ConfigValue(plantRadius, Plugin.HarvestRadius.Value);
            IntegrationChanges.Defer(() => Plugin.HarvestRadius.SettingChanged += ApplyRadius,
                () => Plugin.HarvestRadius.SettingChanged -= ApplyRadius);
            collisionMask = (int)mask.GetValue(null);
            harmony.Patch(harvestRadius.GetMethod, postfix: new HarmonyMethod(typeof(BulkPickup), nameof(PickupRangeBonus)));
            harmony.Patch(interaction, transpiler: new HarmonyMethod(typeof(BulkPickup), nameof(IncludeLootTargets)));
            harmony.Patch(nearby, postfix: new HarmonyMethod(typeof(BulkPickup), nameof(AddNearbyLoot)));
        }

        internal static KeyCode Modifier => config == null ? KeyCode.None : (KeyCode)harvestModifier.GetValue(config);

        internal static bool TryGetHarvestRadius(out float radius, bool alternate = false)
        {
            radius = 0f;
            if (config == null || !(bool)modActive.GetValue(config) || !(bool)enableBulkHarvest.GetValue(config)) return false;
            var key = Modifier;
            bool held = key == KeyCode.LeftShift || key == KeyCode.RightShift
                ? ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift) : ZInput.GetKey(key);
            if (!held && !alternate) return false;
            radius = (float)harvestRadius.GetValue(config);
            return true;
        }

        internal static string Hint(string action)
        {
            if (!TryGetHarvestRadius(out _, true)) return "";
            string key = Modifier.ToString();
            return Localization.instance.Localize("\n[<color=yellow><b>" + key + " + $KEY_Use</b></color>] " + action);
        }

        // Every bulk action reads this radius. EpicLoot's pickup range enchantments widen it by the same share as auto-pickup,
        // while PlantEasily's saved setting keeps the base radius.
        private static void PickupRangeBonus(ref float __result)
        {
            var player = Player.m_localPlayer;
            float bonus = player == null ? 0f : player.GetTotalActiveMagicEffectValue(MagicEffectType.ModifyPickupRange, 0.01f);
            if (bonus > 0f) __result *= 1f + bonus;
        }

        // Preserve PlantEasily's enable, key, attack, dodge, repeat and replant checks.
        private static IEnumerable<CodeInstruction> IncludeLootTargets(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.CallInstead(instructions, x => x.opcode == OpCodes.Isinst && Equals(x.operand, typeof(Beehive)),
                AccessTools.Method(typeof(BulkPickup), nameof(IsAdditionalTarget)), 1, "PlantEasily's bulk-harvest target check changed.");

        private static bool IsAdditionalTarget(Interactable target) => target is Beehive || target is PickableItem ||
            (target is ItemDrop drop && !drop.IsPiece());

        private static void AddNearbyLoot(GameObject __0, HashSet<Interactable> __result)
        {
            var root = __0.transform.root;
            float radius = (float)harvestRadius.GetValue(config);
            bool sameTypeOnly = Convert.ToInt32(harvestStyle.GetValue(config)) == 0;
            foreach (var collider in Physics.OverlapSphere(root.position, radius, collisionMask))
            {
                Component loot = collider.GetComponentInParent<PickableItem>();
                if (loot == null)
                {
                    var drop = collider.GetComponentInParent<ItemDrop>();
                    if (drop == null || drop.IsPiece()) continue;
                    loot = drop;
                }
                var target = loot.transform.root;
                if (target == root || (sameTypeOnly && target.name != root.name)) continue;
                // The caller still invokes the native interaction, including ownership, tar and inventory checks.
                __result.Add((Interactable)loot);
            }
        }
    }
}
