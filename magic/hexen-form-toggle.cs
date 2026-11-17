using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.Transformation;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Gullveig's 4-piece Hexen bonus put the wearer in the Jotun Witch form whenever the set was worn with its staff in hand,
    // so the staff's own spell could never be cast with the full set. The form is now a set ability like the others: its key
    // turns the form on and back off, in the air too, and putting the staff away still ends it. A new character, or one that
    // loses the bonus, starts in its own body. EpicLoot's owner decides the form and shares it through the player's ZDO, so
    // other players see the change as before. As with EpicLoot's set keys, the key does nothing else while the bonus is
    // active: T would otherwise also open the emote wheel.
    internal static class HexenFormToggle
    {
        private static readonly Func<Player, bool> TakeInput = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.Method(typeof(Player), "TakeInput"));
        private static Func<Player, KeyCode, bool> gamepadButtonDown, gamepadButtonHeld;
        private static Action<KeyCode, bool> consumeKey;
        // The character whose owner chose the witch form; any other local character starts in its own body.
        private static Player chosenFor;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(HexenFormToggle);
            var keys = AccessTools.TypeByName("EpicLoot.MagicItemEffects.SetAbilityKeys") ?? throw new InvalidOperationException("EpicLoot's set ability keys moved.");
            gamepadButtonDown = AccessTools.MethodDelegate<Func<Player, KeyCode, bool>>(Required(keys, "GamepadButtonDown"));
            gamepadButtonHeld = AccessTools.MethodDelegate<Func<Player, KeyCode, bool>>(Required(keys, "GamepadButtonHeld"));
            consumeKey = AccessTools.MethodDelegate<Action<KeyCode, bool>>(Required(AccessTools.TypeByName("EpicLoot.MagicItemEffects.OverwhelmingLaunch"), "ConsumeKey"));
            // Registering the key gives the set tooltip its Activation key line, as Earthshaker and Shield Throw have.
            var hotkeys = AccessTools.Field(keys, "Hotkeys")?.GetValue(null) as Dictionary<string, (Func<ConfigEntry<KeyCode>>, Func<ConfigEntry<KeyCode>>)>
                ?? throw new InvalidOperationException("EpicLoot's set ability key list moved.");
            hotkeys[MagicEffectType.Hexen] = (() => Plugin.HexenFormKey, () => Plugin.HexenFormGamepadButton);
            harmony.Patch(Required(typeof(HexenForm), "WantedScale"), postfix: new HarmonyMethod(self, nameof(KeepOwnBody)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Update"), prefix: new HarmonyMethod(self, nameof(ReadKey)));
            harmony.Patch(AccessTools.Method(typeof(PlayerController), "FixedUpdate"), prefix: new HarmonyMethod(self, nameof(HoldKey)));
        }

        private static System.Reflection.MethodInfo Required(Type type, string name) =>
            (type != null ? AccessTools.Method(type, name) : null) ?? throw new InvalidOperationException($"EpicLoot's {name} moved.");

        // EpicLoot calls this only on the character's owner. A debug scale set by the test client still wins.
        private static void KeepOwnBody(Player ____player, ref float __result)
        {
            if (HexenForm.DebugScale.HasValue) return;
            if (__result <= 0f) { if (chosenFor == ____player) chosenFor = null; return; }
            if (chosenFor != ____player) __result = 0f;
        }

        private static bool CanUseKey(Player player) =>
            player == Player.m_localPlayer && !player.IsDead() && !player.InPlaceMode() && TakeInput(player) && player.HasActiveMagicEffect(MagicEffectType.Hexen);

        private static void ReadKey(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            KeyCode key = Plugin.HexenFormKey.Value, button = Plugin.HexenFormGamepadButton.Value;
            bool keyDown = key != KeyCode.None && ZInput.GetKeyDown(key, false), buttonDown = gamepadButtonDown(__instance, button);
            if (!keyDown && !buttonDown || Hud.InRadial() || !CanUseKey(__instance) || !(__instance.GetComponent<HexenForm>() is HexenForm form)) return;
            if (keyDown) consumeKey(key, true);
            if (buttonDown) consumeKey(button, true);
            chosenFor = form.Active ? null : __instance;
            form.Refresh();
        }

        private static void HoldKey(PlayerController __instance)
        {
            var player = Player.m_localPlayer;
            if (!player || __instance.gameObject != player.gameObject) return;
            KeyCode key = Plugin.HexenFormKey.Value, button = Plugin.HexenFormGamepadButton.Value;
            bool keyHeld = key != KeyCode.None && ZInput.GetKey(key, false), buttonHeld = gamepadButtonHeld(player, button);
            if (!keyHeld && !buttonHeld || !CanUseKey(player)) return;
            if (keyHeld) consumeKey(key, false);
            if (buttonHeld) consumeKey(button, false);
        }
    }
}
