using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Quick Stack and Restock share this nearby-container check. Deliberate actions on the open or targeted
    // chest bypass it, so the personal pulling mode and a chest's Shift + L exclusion from crafting supply restrict
    // area transfers without blocking manual storage.
    //
    // Existing character choices always win. Only a missing choice receives the configured server default;
    // standalone installations inherit AzuCraftyBoxes' own default. Off and All show their existing status indicators.
    internal static class QuickStackPullingMode
    {
        // AzuCraftyBoxes' PullMode values.
        private const int Off = 0, All = 1, MineOnly = 2;
        private static Func<Player, bool> pullingAllowed;
        private static Func<ZNetView, bool> containerAllowed;
        private static Func<Container, bool> craftingSupply;
        private static ConfigEntryBase statusDisplay;
        private static System.Reflection.MethodInfo modeOf;
        private static StatusEffect offStatus, mineOnlyStatus, allStatus;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            if (!PluginIds.TryGet(PluginIds.AzuCraftyBoxes, out var crafty) ||
                !PluginIds.TryGet(PluginIds.QuickStack, out var quickStack)) return;
            var assembly = crafty.Instance.GetType().Assembly;
            var pulling = assembly.GetType("AzuCraftyBoxes.Util.PlayerPullingExtensions", true);
            var allowed = AccessTools.Method(pulling, "IsPullingAllowed", new[] { typeof(Player) });
            var getMode = AccessTools.Method(pulling, "GetPullMode", new[] { typeof(Player) });
            modeOf = getMode;
            var applyStatus = AccessTools.Method(pulling, "ApplyPullingStatusEffect", new[] { typeof(Player) });
            var keyCheck = AccessTools.Method(crafty.Instance.GetType(), "Update");
            var container = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Patches.CraftyToggle", true),
                "PullModeAllows", new[] { typeof(ZNetView) });
            var supply = AccessTools.Method(assembly.GetType("AzuCraftyBoxes.Patches.ContainerExtensions", true),
                "IsCraftyContainer", new[] { typeof(Container) });
            var nearby = AccessTools.Method(quickStack.Instance.GetType().Assembly.GetType("QuickStackStore.AreaStackRestockHelper", true),
                "ShouldAffectNonOwnerContainer", new[] { typeof(Container), typeof(long), typeof(bool) });
            var display = new ConfigDefinition("1 - General", "Prevent Pulling Status");
            if (allowed == null || getMode == null || applyStatus == null || keyCheck == null || container == null || supply == null ||
                nearby == null || !crafty.Instance.Config.ContainsKey(display))
                throw new InvalidOperationException("AzuCraftyBoxes' pulling mode or Quick Stack's nearby-container check moved.");
            pullingAllowed = AccessTools.MethodDelegate<Func<Player, bool>>(allowed);
            containerAllowed = AccessTools.MethodDelegate<Func<ZNetView, bool>>(container);
            craftingSupply = AccessTools.MethodDelegate<Func<Container, bool>>(supply);
            statusDisplay = crafty.Instance.Config[display];
            harmony.Patch(nearby, postfix: new HarmonyMethod(typeof(QuickStackPullingMode), nameof(ApplyPullingMode)));
            harmony.Patch(applyStatus, prefix: new HarmonyMethod(typeof(QuickStackPullingMode), nameof(ShowChangedMode)));
            harmony.Patch(keyCheck, transpiler: new HarmonyMethod(typeof(QuickStackPullingMode), nameof(ConfirmationText)));

            var effects = assembly.GetType("AzuCraftyBoxes.Patches.SE_ContainerPull", true);
            mineOnlyStatus = (StatusEffect)AccessTools.Field(effects, "SE_MineOnly").GetValue(null);
            offStatus = (StatusEffect)AccessTools.Field(effects, "SE_ContainerPulling").GetValue(null);
            if (!mineOnlyStatus || !offStatus) throw new InvalidOperationException("AzuCraftyBoxes' pulling status effects are not initialized.");
            var originalOff = offStatus; string originalName = offStatus.m_name, originalTooltip = offStatus.m_tooltip;
            IntegrationChanges.Defer(() =>
            {
                originalOff.m_name = "Nearby containers: Off";
                originalOff.m_tooltip = "";
                allStatus = ScriptableObject.CreateInstance<StatusEffect>();
                allStatus.name = "PullAll";
                allStatus.m_name = "Nearby containers: All";
                allStatus.m_tooltip = "";
                allStatus.m_startMessage = allStatus.m_stopMessage = "";
            }, () =>
            {
                originalOff.m_name = originalName; originalOff.m_tooltip = originalTooltip;
                if (allStatus) UnityEngine.Object.Destroy(allStatus);
            });
            log.LogInfo("Nearby Quick Stack and Restock follow the character's saved pulling mode; only missing choices receive the configured default.");
        }

        private static void ApplyPullingMode(Container __0, ref bool __result)
        {
            if (!__result) return;
            var player = Player.m_localPlayer;
            __result = player != null && pullingAllowed(player) && containerAllowed(GameFields.ContainerView(__0)) && craftingSupply(__0);
        }

        private static bool ShowChangedMode(Player __0)
        {
            int mode = Convert.ToInt32(modeOf.Invoke(null, new object[] { __0 }));
            bool shown = statusDisplay.BoxedValue.ToString() == "On";
            if (allStatus.m_icon == null && ZNetScene.instance != null)
                allStatus.m_icon = ZNetScene.instance.GetPrefab("piece_chest_wood")?.GetComponent<Piece>()?.m_icon;
            Show(__0, offStatus, shown && mode == Off);
            Show(__0, allStatus, shown && mode == All);
            Show(__0, mineOnlyStatus, false);
            return false;
        }

        private static void Show(Player player, StatusEffect effect, bool shown)
        {
            if (shown) player.GetSEMan().AddStatusEffect(effect);
            else player.GetSEMan().RemoveStatusEffect(effect);
        }

        // The Alt + O message names the mode it switched to.
        private static IEnumerable<CodeInstruction> ConfirmationText(IEnumerable<CodeInstruction> instructions)
        {
            var replacements = new Dictionary<string, string>
            {
                ["Pull from containers?"] = "Nearby containers",
                ["<color=green>Yes</color>"] = "<color=green>All</color>",
                ["<color=red>No</color>"] = "<color=red>Off</color>"
            };
            var code = instructions.ToList();
            var texts = code.Where(instruction => instruction.opcode == OpCodes.Ldstr && replacements.ContainsKey((string)instruction.operand)).ToList();
            if (texts.Count != replacements.Count) throw new InvalidOperationException("AzuCraftyBoxes' Alt + O message changed.");
            foreach (var instruction in texts) instruction.operand = replacements[(string)instruction.operand];
            return code;
        }
    }
}
