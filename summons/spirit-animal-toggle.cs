using System;
using System.Reflection;
using BepInEx.Configuration;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Fylgja's Bond's spirit can be kept away: the Spirit Animal key, pressed while no creature but the spirit itself is under
    // the crosshair, turns its summoning off or on, a choice saved on the character. A spirit that leaves before its time is up,
    // because it is sent away, the set comes off, it is left behind or another animal is bound, comes back only after the delay
    // a fallen spirit waits, so none of those brings a fresh spirit at full health sooner than letting it fall. A spirit that
    // lasts its whole time still comes again at once, as EpicLoot has it. A spirit left behind by a portal or a dungeon doorway,
    // which it can't pass, is followed at once by the next one, which keeps its health and remaining time.
    internal static class SpiritAnimalToggle
    {
        internal const string OffKey = Plugin.Id + ".spirit-animal-off";
        internal const string OffIndicatorName = "EpicLootProgressionFix_SpiritAnimalOff";
        private static readonly Type Spirits = typeof(SpiritAnimal);
        private static readonly FieldInfo State = AccessTools.Field(Spirits, "_state");
        private static readonly object Returning = Enum.Parse(State.FieldType, "Returning");
        private static readonly AccessTools.FieldRef<Character> Spirit = AccessTools.StaticFieldRefAccess<Character>(AccessTools.Field(Spirits, "_spirit"));
        private static readonly AccessTools.FieldRef<float> ExpireAt = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Spirits, "_expireAt"));
        private static readonly AccessTools.FieldRef<float> ReturnAt = AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(Spirits, "_returnAt"));
        private static readonly string RecordKey = (string)AccessTools.Field(Spirits, "RecordKey").GetValue(null);
        private static readonly Func<float> LeashDistance = AccessTools.MethodDelegate<Func<float>>(AccessTools.PropertyGetter(Spirits, "LeashDistance"));
        private static readonly Func<float> ReturnDelay = AccessTools.MethodDelegate<Func<float>>(AccessTools.PropertyGetter(Spirits, "ReturnDelay"));
        // Fades the spirit, starts the return delay and shows EpicLoot's Spirit Returning countdown, as when it falls.
        private static readonly Action<Character> Fall = AccessTools.MethodDelegate<Action<Character>>(AccessTools.Method(Spirits, "OnSpiritKilled"));
        private static readonly Func<Player, bool> CanUseKey = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.Method(Spirits, "CanUseBindKey"));
        private static readonly Func<Player, Character> Hovered = AccessTools.MethodDelegate<Func<Player, Character>>(AccessTools.Method(Spirits, "GetHoveredCreature"));
        private static readonly Func<string> KeyLabel = AccessTools.MethodDelegate<Func<string>>(AccessTools.Method(Spirits, "KeyLabel"));
        private static readonly Func<StatusEffect> ReturnIndicator = AccessTools.MethodDelegate<Func<StatusEffect>>(AccessTools.Method(Spirits, "GetReturnIndicator"));
        private static readonly Action<Player, StatusEffect, float> ShowIndicator =
            AccessTools.MethodDelegate<Action<Player, StatusEffect, float>>(AccessTools.Method(Spirits, "ShowIndicator"));
        private static readonly Type Keys = AccessTools.TypeByName("EpicLoot.MagicItemEffects.SetAbilityKeys");
        private static readonly Func<Player, KeyCode, bool> GamepadDown = AccessTools.MethodDelegate<Func<Player, KeyCode, bool>>(AccessTools.Method(Keys, "GamepadButtonDown"));
        private static readonly Func<Player, KeyCode, bool> GamepadHeld = AccessTools.MethodDelegate<Func<Player, KeyCode, bool>>(AccessTools.Method(Keys, "GamepadButtonHeld"));
        // Resets the game's buttons that share a key, so the press does only this.
        private static readonly Action<KeyCode, bool> Consume =
            AccessTools.MethodDelegate<Action<KeyCode, bool>>(AccessTools.Method(typeof(OverwhelmingLaunch), "ConsumeKey"));
        private static readonly Type Settings = AccessTools.TypeByName("EpicLoot.Config.ELConfig");
        private static readonly FieldInfo KeySetting = AccessTools.Field(Settings, "SpiritAnimalKey");
        private static readonly FieldInfo ButtonSetting = AccessTools.Field(Settings, "SpiritAnimalGamepadButton");
        private static readonly int ExpireKey = (int)AccessTools.Field(Spirits, "ExpireHash").GetValue(null);
        // Zero while the player teleports, then counting up; a doorway's teleport can end in the frame it moves the player.
        private static readonly AccessTools.FieldRef<Player, float> SinceTeleport = AccessTools.FieldRefAccess<Player, float>("m_teleportCooldown");
        private static StatusEffect offIndicator;
        private static float nextIndicatorCheck;
        // What the spirit left behind by travel had: its health share and the end of its time, for the next spirit.
        private static float carriedHealth = -1f, carriedUntil;
        // StarLevelSystem sets up the next spirit's health some seconds after it appears, at times only after a distant teleport has
        // settled. Its share stays the carried one through that: it doesn't rise in the first 5 s, and a new maximum keeps the share
        // it had for 20 s.
        private static Character capped;
        private static float cappedShare, cappedMaximum, capUntil, watchUntil;

        internal static bool IsOff(Player player) => player.m_customData.ContainsKey(OffKey);
        private static KeyCode Bound(FieldInfo setting) => (setting.GetValue(null) as ConfigEntry<KeyCode>)?.Value ?? KeyCode.None;

        // The key as EpicLoot's own hints name it, for the set bonus's description.
        internal static string KeyText()
        {
            string label = KeyLabel();
            return string.IsNullOrEmpty(label) ? "the Spirit Animal Hotkey" : label;
        }

        // Nothing under the crosshair, or only the player's own spirit, which can't be bound.
        private static bool Aimless(Player player)
        {
            var creature = Hovered(player);
            return !creature || creature == Spirit();
        }

        [HarmonyPatch(typeof(SpiritAnimal), "HandleBindKey")]
        private static class Press
        {
            private static bool Prefix(Player player)
            {
                KeyCode key = Bound(KeySetting), button = Bound(ButtonSetting);
                bool keyboard = key != KeyCode.None && ZInput.GetKeyDown(key, false), gamepad = GamepadDown(player, button);
                if (!keyboard && !gamepad || !CanUseKey(player) || !Aimless(player)) return true;
                if (keyboard) Consume(key, true);
                if (gamepad) Consume(button, true);
                bool off = !IsOff(player);
                if (off) player.m_customData[OffKey] = "1";
                else player.m_customData.Remove(OffKey);
                player.Message(MessageHud.MessageType.Center, off ? "Spirit animal off" : "Spirit animal on");
                nextIndicatorCheck = 0f;
                return false;
            }
        }

        // The game reads held buttons before the press above, so the key stays out of their way while it is held, as EpicLoot
        // keeps it while a creature that can be bound is under the crosshair.
        [HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
        private static class Held
        {
            private static void Prefix(PlayerController __instance)
            {
                var player = Player.m_localPlayer;
                if (!player || __instance.gameObject != player.gameObject || UnifiedPopup.IsVisible()) return;
                KeyCode key = Bound(KeySetting), button = Bound(ButtonSetting);
                bool keyboard = key != KeyCode.None && ZInput.GetKey(key, false), gamepad = GamepadHeld(player, button);
                if ((!keyboard && !gamepad) || !CanUseKey(player) || !Aimless(player)) return;
                if (keyboard) Consume(key, false);
                if (gamepad) Consume(button, false);
            }
        }

        [HarmonyPatch(typeof(SpiritAnimal), "CanSummon")]
        private static class Summoning
        {
            private static bool Prefix(Player player, ref bool __result)
            {
                if (!IsOff(player)) return true;
                __result = false;
                return false;
            }
        }

        // EpicLoot fades a spirit whose set comes off or that is left behind and lets the next one come at once.
        [HarmonyPatch(typeof(SpiritAnimal), "Tick")]
        private static class Leaving
        {
            private static void Prefix(Player player)
            {
                if (Time.time >= nextIndicatorCheck) ShowOff(player);
                var spirit = Spirit();
                var view = spirit ? GameFields.CharacterView(spirit) : null;
                if (!view || !view.IsValid() || player.IsDead() || Time.time >= ExpireAt()) return;
                if (spirit == capped && Time.time < watchUntil)
                {
                    float maximum = spirit.GetMaxHealth(), share = spirit.GetHealth() / Mathf.Max(1f, maximum);
                    if (maximum != cappedMaximum || Time.time < capUntil && share > cappedShare) spirit.SetHealth(maximum * cappedShare);
                    else cappedShare = share;
                    cappedMaximum = maximum;
                }
                if (IsOff(player) || !player.HasActiveMagicEffect(MagicEffectType.SpiritAnimal, out _)) Fall(spirit);
                else if (Vector3.Distance(player.transform.position, spirit.transform.position) > LeashDistance())
                {
                    if (!player.IsTeleporting() && SinceTeleport(player) >= 1f) { Fall(spirit); return; }
                    // EpicLoot fades it in this update and lets the next one come at once.
                    carriedHealth = spirit.GetHealth() / Mathf.Max(1f, spirit.GetMaxHealth());
                    carriedUntil = ExpireAt();
                }
            }
        }

        [HarmonyPatch(typeof(SpiritAnimal), "Summon")]
        private static class Arriving
        {
            private static void Postfix()
            {
                var spirit = Spirit();
                float share = carriedHealth, until = carriedUntil;
                carriedHealth = -1f;
                if (share < 0f || !spirit || Time.time >= until) return;
                capped = spirit; cappedShare = share; cappedMaximum = spirit.GetMaxHealth();
                capUntil = Time.time + 5f; watchUntil = Time.time + 20f;
                spirit.SetHealth(cappedMaximum * share);
                ExpireAt() = until;
                var view = GameFields.CharacterView(spirit);
                if (view && view.IsValid()) view.GetZDO().Set(ExpireKey, ZNet.instance.GetTime().Ticks + (long)((until - Time.time) * 10000000f));
            }
        }

        // Binding another animal clears EpicLoot's return delay and fades the spirit that is out.
        [HarmonyPatch(typeof(SpiritAnimal), "Bind")]
        private static class Rebinding
        {
            private static void Prefix(Player player, out (string record, float returnAt) __state)
            {
                player.m_customData.TryGetValue(RecordKey, out string record);
                var spirit = Spirit();
                float returnAt = spirit && Time.time < ExpireAt() ? Time.time + ReturnDelay()
                    : Returning.Equals(State.GetValue(null)) ? ReturnAt() : 0f;
                __state = (record, returnAt);
            }

            private static void Postfix(Player player, (string record, float returnAt) __state)
            {
                player.m_customData.TryGetValue(RecordKey, out string record);
                if (record == __state.record) return;
                carriedHealth = -1f;
                if (__state.returnAt <= Time.time) return;
                State.SetValue(null, Returning);
                ReturnAt() = __state.returnAt;
                ShowIndicator(player, ReturnIndicator(), __state.returnAt - Time.time);
            }
        }

        // A lasting status icon while the set is worn with its spirit turned off.
        private static void ShowOff(Player player)
        {
            nextIndicatorCheck = Time.time + .25f;
            var effects = player.GetSEMan();
            int hash = OffIndicatorName.GetStableHashCode();
            bool show = IsOff(player) && player.HasActiveMagicEffect(MagicEffectType.SpiritAnimal, out _);
            if (show == effects.HaveStatusEffect(hash)) return;
            if (!show) { effects.RemoveStatusEffect(hash, true); return; }
            if (!offIndicator)
            {
                var icon = ObjectDB.instance?.GetItemPrefab("StaffSpiritCaller")?.GetComponent<ItemDrop>()?.m_itemData.GetIcon();
                if (!icon) return;
                offIndicator = ScriptableObject.CreateInstance<StatusEffect>();
                offIndicator.name = OffIndicatorName;
                offIndicator.m_name = "Spirit off";
                offIndicator.m_icon = icon;
            }
            offIndicator.m_tooltip = "Your fylgja doesn't come to fight. Press " + KeyText() + " while not looking at a creature to call it again.";
            effects.AddStatusEffect(offIndicator, false, 0, 0f, -1);
        }
    }
}
