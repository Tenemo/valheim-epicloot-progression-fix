using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class StationCapacity
    {
        private static readonly Func<CookingStation, int> FreeSlot = AccessTools.MethodDelegate<Func<CookingStation, int>>(
            AccessTools.Method(typeof(CookingStation), "GetFreeSlot"));
        private static readonly Func<CookingStation, bool> HasDoneItem = AccessTools.MethodDelegate<Func<CookingStation, bool>>(
            AccessTools.Method(typeof(CookingStation), "HaveDoneItem"));
        private static readonly Func<CookingStation, bool> FireLit = AccessTools.MethodDelegate<Func<CookingStation, bool>>(
            AccessTools.Method(typeof(CookingStation), "IsFireLit"));
        private static readonly Func<Smelter, int> QueueSize = AccessTools.MethodDelegate<Func<Smelter, int>>(
            AccessTools.Method(typeof(Smelter), "GetQueueSize"));
        private static ConfigEntry<KeyboardShortcut> fillAllKey;
        private static Func<bool> pullingPrevented;
        private static Func<string, string, bool> canPull;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var prefix = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Patches.FireplaceInteractPatch", true), "Prefix");
            var prevent = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions", true), "ShouldPrevent", Type.EmptyTypes);
            var pull = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.API", true), "CanItemBePulled", new[] { typeof(string), typeof(string) });
            fillAllKey = (ConfigEntry<KeyboardShortcut>)AccessTools.Field(plugin.Instance.GetType(), "fillAllModKey")?.GetValue(null);
            if (prefix == null || prevent == null || pull == null || fillAllKey == null)
                throw new InvalidOperationException("AzuCraftyBoxes' fire filling controls changed.");
            pullingPrevented = AccessTools.MethodDelegate<Func<bool>>(prevent);
            canPull = AccessTools.MethodDelegate<Func<string, string, bool>>(pull);
            harmony.Patch(prefix, prefix: new HarmonyMethod(typeof(StationCapacity), nameof(SkipFullFireSupply)));
        }

        private static bool FireFull(Fireplace fire)
        {
            var view = fire.GetComponent<ZNetView>();
            return view != null && view.IsValid() && Mathf.CeilToInt(view.GetZDO().GetFloat(ZDOVars.s_fuel)) >= fire.m_maxFuel;
        }

        // Let the native interaction retain its hold, toggle and keep-lit priorities. A bulk refill of zero adds nothing.
        private static bool SkipFullFireSupply(Fireplace __0, Humanoid __1, bool __2, ref bool __3, ref bool __result)
        {
            if (!FireFull(__0)) return true;
            if (!KeepLitFires.IsKeptLit(__0) && __0.m_canRefill && !__0.m_infiniteFuel && !__2 &&
                Input.GetKey(fillAllKey.Value.MainKey) && !pullingPrevented() && canPull(Utils.GetPrefabName(__0.gameObject), __0.m_fuelItem.name))
            {
                __1.Message(MessageHud.MessageType.Center, Localization.instance.Localize("$msg_cantaddmore", __0.m_fuelItem.m_itemData.m_shared.m_name));
                __3 = false;
                __result = false;
            }
            else __result = true;
            return false;
        }

        [HarmonyPatch]
        private static class FireSupplyOrder
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.Interact));
                yield return AccessTools.Method(typeof(Fireplace), nameof(Fireplace.CanUseItems));
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var haveItem = AccessTools.Method(typeof(Inventory), nameof(Inventory.HaveItem), new[] { typeof(string), typeof(bool) });
                int changes = 0;
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(haveItem))
                    {
                        var fire = new CodeInstruction(OpCodes.Ldarg_0);
                        fire.labels.AddRange(instruction.labels);
                        instruction.labels.Clear();
                        yield return fire;
                        instruction.opcode = OpCodes.Call;
                        instruction.operand = AccessTools.Method(typeof(StationCapacity), nameof(HaveFuelOrFull));
                        changes++;
                    }
                    yield return instruction;
                }
                if (changes != 1) throw new InvalidOperationException("Valheim's fire supply check changed; expected one inventory check.");
            }
        }

        // Full fires reach the native capacity message, including the item menu's silent-check branch.
        private static bool HaveFuelOrFull(Inventory inventory, string name, bool matchWorldLevel, Fireplace fire) =>
            FireFull(fire) || inventory.HaveItem(name, matchWorldLevel);

        [HarmonyPatch(typeof(CookingStation), "OnInteract"), HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
        private static bool FullCooking(CookingStation __instance, Humanoid user, ref bool __result)
        {
            if (BulkCooking.AreaAction(__instance, user) || HasDoneItem(__instance) || FreeSlot(__instance) >= 0) return true;
            user.Message(MessageHud.MessageType.Center, __instance.m_requireFire && !FireLit(__instance) ? "$msg_needfire" : "$msg_nocookroom");
            user.UseIemBlockkMessage();
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(Smelter), "OnAddOre"), HarmonyPrefix, HarmonyPriority(Priority.First + 1)]
        private static bool FullOre(Smelter __instance, Humanoid user, ItemDrop.ItemData item, ref bool __result)
        {
            // Explicit items keep their native wrong-item and boss-lock warnings.
            if (BulkProcessing.AreaAction(__instance, user) || item != null || QueueSize(__instance) < __instance.m_maxOre) return true;
            user.Message(MessageHud.MessageType.Center, "$msg_itsfull");
            __result = false;
            return false;
        }
    }
}
