using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(DamageText), "Awake")]
    internal static class DamageTextRange
    {
        private static long damagePlayer;

        private static void Postfix(DamageText __instance)
        {
            __instance.m_maxTextDistance = Plugin.DamageTextDistance.Value;
            Plugin.DamageTextDistance.SettingChanged -= Apply;
            Plugin.DamageTextDistance.SettingChanged += Apply;
        }

        private static void Apply(object sender, EventArgs arguments)
        {
            if (DamageText.instance != null) DamageText.instance.m_maxTextDistance = Plugin.DamageTextDistance.Value;
        }

        // Network Performance System relays a number only to players within the server's own distance plus 20 m of the hit.
        // Awake sets that to the distance for other sources, so a player whose hit another player's game simulated lost their
        // own numbers beyond it. The relay has to reach as far as either distance shows a number.
        internal static void Initialize(IntegrationPatches harmony)
        {
            // Network Performance System has no tested-version entry, so it is found as the ownership integration finds it; a
            // changed method is reported below.
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(PluginIds.NetworkPerformance)) return;
            var numbers = AccessTools.TypeByName("NetworkPerformanceSystem.Runtime.DamageNumbers");
            var relay = numbers == null ? null : AccessTools.Method(numbers, "RelayDistanceSq");
            if (relay == null) { DisabledFeatures.Set("Damage number relay distance", "Network Performance System has no DamageNumbers.RelayDistanceSq"); return; }
            harmony.Patch(relay, postfix: new HarmonyMethod(typeof(DamageTextRange), nameof(WidenRelay)));
            // With its damage numbers set to Attacker Only or Off, it sends or shows its own packet instead of the game's,
            // which needs the source as well for the own-damage distance.
            var route = AccessTools.Method(numbers, "OnShowText",
                new[] { typeof(DamageText), typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) });
            if (route == null) { DisabledFeatures.Set("Own damage text distance", "Network Performance System has no DamageNumbers.OnShowText"); return; }
            harmony.Patch(route, transpiler: new HarmonyMethod(typeof(DamageTextRange), nameof(AppendSource)));
        }

        private static void WidenRelay(ref float __result)
        {
            float distance = Mathf.Max(Plugin.DamageTextDistance.Value, Plugin.OwnDamageTextDistance.Value) + 20f;
            __result = Mathf.Max(__result, distance * distance);
        }

        private static bool IsDamage(DamageText.TextType type) => type <= DamageText.TextType.Immune || type == DamageText.TextType.Bonus;

        // Keep the native text packet intact and append its actual source. The RPC sender can be the creature's owner.
        private static void WriteSource(ZPackage package, bool player, DamageText.TextType type)
        {
            package.Write(player);
            package.Write(IsDamage(type) ? damagePlayer : 0L);
        }

        // The packet's player flag is its last field, written once; the text type is the method's second argument in both
        // the game's instance method and Network Performance System's static one.
        private static IEnumerable<CodeInstruction> AppendSource(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var write = AccessTools.Method(typeof(ZPackage), nameof(ZPackage.Write), new[] { typeof(bool) });
            int index = code.FindIndex(instruction => instruction.Calls(write));
            if (index < 0 || code.FindLastIndex(instruction => instruction.Calls(write)) != index)
                throw new InvalidOperationException("The damage text packet changed; expected one player flag.");
            code[index].opcode = OpCodes.Call;
            code[index].operand = AccessTools.Method(typeof(DamageTextRange), nameof(WriteSource));
            code.Insert(index, new CodeInstruction(OpCodes.Ldarg_1).MoveLabelsFrom(code[index]));
            return code;
        }

        [HarmonyPatch]
        private static class DamageSource
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string method in new[] { nameof(Character.Damage), "RPC_Damage", nameof(Character.ApplyDamage) })
                    yield return AccessTools.Method(typeof(Character), method);
                foreach (var type in new[] { typeof(Destructible), typeof(WearNTear), typeof(TreeBase), typeof(TreeLog) })
                    yield return AccessTools.Method(type, "RPC_Damage");
                yield return AccessTools.Method(typeof(MineRock), "RPC_Hit");
                yield return AccessTools.Method(typeof(MineRock5), "DamageArea");
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(object __instance, HitData hit, out long __state)
            {
                __state = damagePlayer;
                var attacker = hit == null || hit.m_attacker.IsNone() ? null : ZDOMan.instance?.GetZDO(hit.m_attacker);
                damagePlayer = attacker?.GetLong(ZDOVars.s_playerID) ?? 0L;
                if (damagePlayer == 0 && __instance is Character character &&
                    (hit?.m_hitType == HitData.HitType.Poisoned || hit?.m_hitType == HitData.HitType.Burning))
                    damagePlayer = CorpseRotKillCredit.PeriodicDamagePlayer(character);
            }

            private static void Finalizer(long __state) => damagePlayer = __state;
        }

        [HarmonyPatch(typeof(DamageText), nameof(DamageText.ShowText),
            new[] { typeof(DamageText.TextType), typeof(Vector3), typeof(string), typeof(bool) })]
        private static class SendSource
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => AppendSource(instructions);
        }

        [HarmonyPatch(typeof(DamageText), "RPC_DamageText")]
        private static class ReceiveSource
        {
            private static void Prefix(DamageText __instance, ZPackage pkg, out float __state)
            {
                __state = __instance.m_maxTextDistance;
                if (!Utils.GetMainCamera() || Hud.IsUserHidden()) return;
                int position = pkg.GetPos();
                long source;
                DamageText.TextType type;
                try
                {
                    type = (DamageText.TextType)pkg.ReadInt();
                    pkg.ReadVector3();
                    pkg.ReadString();
                    pkg.ReadBool();
                    source = pkg.Size() - pkg.GetPos() == sizeof(long) ? pkg.ReadLong() : 0L;
                }
                finally { pkg.SetPos(position); }
                var player = Player.m_localPlayer;
                __instance.m_maxTextDistance = IsDamage(type) && source != 0 && player && source == player.GetPlayerID()
                    ? Plugin.OwnDamageTextDistance.Value : Plugin.DamageTextDistance.Value;
            }

            private static void Finalizer(DamageText __instance, float __state) => __instance.m_maxTextDistance = __state;
        }
    }
}
