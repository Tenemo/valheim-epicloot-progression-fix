using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class RecallPriority
    {
        internal const float ReservationSeconds = 5f;
        private const string PlayerKey = "elpf_recall_player";
        private const string PeerKey = "elpf_recall_peer";
        private const string DeadlineKey = "elpf_recall_until";
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenRightItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenRightItem");
        private static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> HiddenLeftItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_hiddenLeftItem");
        private static readonly HashSet<ZDOID> Returning = new HashSet<ZDOID>();
        // Returned weapons waiting to be equipped, until their deadline; pickup has already released their reservations.
        private static readonly Dictionary<ItemDrop.ItemData, long> Equipping = new Dictionary<ItemDrop.ItemData, long>();

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(RecallWeapon_Projectile_SpawnOnHit_Patch), "IssueItemRecall"),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(Reserve)));
            harmony.Patch(AccessTools.Method(typeof(RPC_WeaponRecall_Player_Awake_Patch), "Postfix"),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(RegisterRecall)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.CanPickup)),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(CanPickup)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.Pickup)),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(AllowItemPickup)));
            harmony.Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.Pickup)),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(AllowHumanoidPickup)),
                postfix: new HarmonyMethod(typeof(RecallPriority), nameof(EquipAfterPickup)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.RequestOwn)),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(AllowOwnershipRequest)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), "RPC_RequestOwn"),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(AllowOwnershipTransfer)));
            harmony.Patch(AccessTools.Method(typeof(ItemDrop), nameof(ItemDrop.Load)),
                postfix: new HarmonyMethod(typeof(RecallPriority), nameof(ResumeLoadedDrop)));
            harmony.Patch(AccessTools.Constructor(typeof(ZDOMan), new[] { typeof(int) }),
                postfix: new HarmonyMethod(typeof(RecallPriority), nameof(ForgetReturns)));
            harmony.Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.StartAttack)),
                prefix: new HarmonyMethod(typeof(RecallPriority), nameof(SkipUnarmedKick)) { priority = Priority.First });
        }

        // Logging out can end a return before its finally block runs, and a ZDOID from an earlier connection
        // hashes through the user table each new ZDOMan resets.
        private static void ForgetReturns() { Returning.Clear(); Equipping.Clear(); RecallRecovery.Forget(); }

        // While a recalling weapon is thrown, returning or being equipped, the empty hands' secondary attack would only kick
        // and delay the equip. Refuse just that fallback kick; the game's own click buffer and held button then throw the
        // weapon once it is back in hand. Real fist weapons, primary attacks and stowed or other weapons keep their attacks.
        private static bool SkipUnarmedKick(Humanoid __instance, bool secondaryAttack, ref bool __result)
        {
            if (!secondaryAttack || !(__instance is Player player) || player != Player.m_localPlayer || player.IsDead() ||
                player.m_unarmedWeapon == null || player.GetCurrentWeapon() != player.m_unarmedWeapon.m_itemData || !HandsFree(player) ||
                !RecallRecovery.Outstanding(player) && !EquipPending(player)) return true;
            __result = false;
            return false;
        }

        private static bool EquipPending(Player player)
        {
            if (Equipping.Count == 0 || ZNet.instance == null) return false;
            long now = ZNet.instance.GetTime().Ticks;
            foreach (var entry in Equipping)
                if (entry.Value > now && player.GetInventory().ContainsItem(entry.Key) && !player.IsItemEquiped(entry.Key)) return true;
            return false;
        }

        // The native hand slots a returned weapon can take without replacing anything the player equipped or stowed.
        private static bool HandsFree(Player player) =>
            GameFields.RightItem(player) == null && HiddenRightItem(player) == null &&
            GameFields.LeftItem(player)?.IsTwoHanded() != true && HiddenLeftItem(player)?.IsTwoHanded() != true;

        // Both impact and the 80 m fallback call this before notifying the thrower. Network ownership is a
        // transferable session ID; keep the character identity separately for local pickup checks.
        private static void Reserve(ItemDrop item, Projectile projectile)
        {
            if (!item || !item.m_itemData.HasMagicEffect(MagicEffectType.RecallWeapon) ||
                !(GameFields.ProjectileOwner(projectile) is Player player)) return;
            var data = item.GetComponent<ZNetView>().GetZDO();
            data.Set(PlayerKey, player.GetPlayerID());
            data.Set(PeerKey, player.GetComponent<ZNetView>().GetZDO().GetOwner());
            data.Set(DeadlineKey, long.MaxValue);
            RecallRecovery.Landed(item, projectile, player);
        }

        private static ZDO Reservation(ItemDrop item)
        {
            // Humanoids also pick up non-networked item prefabs when receiving their default equipment.
            var view = item ? item.GetComponent<ZNetView>() : null;
            var data = view ? view.GetZDO() : null;
            return data != null && data.GetLong(PlayerKey, 0) != 0 &&
                data.GetLong(DeadlineKey, 0) > ZNet.instance.GetTime().Ticks ? data : null;
        }

        private static bool IsThrower(ZDO data, Humanoid character) =>
            character is Player player && player.GetPlayerID() == data.GetLong(PlayerKey, 0);

        private static bool CanPickup(ItemDrop __instance, ref bool __result)
        {
            var data = Reservation(__instance);
            if (data == null) return true;
            // The thrower can retrieve a reserved drop immediately, without the normal half-second delay.
            __result = IsThrower(data, Player.m_localPlayer) && __instance.GetComponent<ZNetView>().IsOwner();
            return false;
        }

        private static bool AllowItemPickup(ItemDrop __instance, Humanoid character)
        {
            var data = Reservation(__instance);
            return data == null || IsThrower(data, character);
        }

        private static bool AllowHumanoidPickup(Humanoid __instance, GameObject go, ref bool __result,
            out ItemDrop.ItemData __state)
        {
            __state = null;
            var drop = go.GetComponent<ItemDrop>();
            var data = Reservation(drop);
            if (data == null) return true;
            if (IsThrower(data, __instance))
            {
                __state = drop.m_itemData;
                return true;
            }
            __result = false;
            return false;
        }

        private static void EquipAfterPickup(Humanoid __instance, bool __result, ItemDrop.ItemData __state)
        {
            if (__result && __state != null && __instance is Player player && player == Player.m_localPlayer)
                player.StartCoroutine(EquipReturnedWeapon(player, __state));
        }

        // An immediate recall can arrive during the throw animation. Native pickup succeeds, but equip
        // refuses attacks and dodges. Retry only that returned inventory item while the native hand slots allow it.
        internal static IEnumerator EquipReturnedWeapon(Player player, ItemDrop.ItemData item)
        {
            long deadline = ZNet.instance.GetTime().AddSeconds(ReservationSeconds).Ticks;
            var pause = new WaitForSeconds(0.1f);
            Equipping[item] = deadline;
            try
            {
                while (player && player == Player.m_localPlayer && !player.IsDead() &&
                    player.GetInventory().ContainsItem(item) && !player.IsItemEquiped(item) && HandsFree(player) &&
                    ZNet.instance != null && ZNet.instance.GetTime().Ticks < deadline)
                {
                    if (player.EquipItem(item)) yield break;
                    yield return pause;
                }
            }
            finally { if (Equipping.TryGetValue(item, out long current) && current == deadline) Equipping.Remove(item); }
        }

        private static bool AllowOwnershipRequest(ItemDrop __instance)
        {
            var data = Reservation(__instance);
            return data == null || IsThrower(data, Player.m_localPlayer);
        }

        private static bool AllowOwnershipTransfer(ItemDrop __instance, long uid)
        {
            var data = Reservation(__instance);
            return data == null || uid == data.GetLong(PeerKey, 0);
        }

        private static bool RegisterRecall(Player __0)
        {
            __0.GetComponent<ZNetView>().Register<ZDOID>("el-wr", (sender, item) => ReturnToThrower(__0, item));
            return false;
        }

        private static void ResumeLoadedDrop(ItemDrop __instance)
        {
            var player = Player.m_localPlayer;
            var data = Reservation(__instance);
            if (!player || data == null || !IsThrower(data, player) || Game.instance == null) return;
            RecallRecovery.TrackDrop(__instance, player);
            ReturnToThrower(player, data.m_uid);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReturnToThrower(Player player, ZDOID item)
        {
            if (player && player == Player.m_localPlayer && Returning.Add(item)) Game.instance.StartCoroutine(Return(player, item));
        }

        // Keep the original drop reserved until ownership arrives. Death and a full inventory transfer it
        // to the character's saved recovery queue, rather than a coroutine on the dying player object.
        private static IEnumerator Return(Player player, ZDOID item)
        {
            long playerId = player.GetPlayerID();
            long arrivalDeadline = ZNet.instance.GetTime().AddSeconds(ReservationSeconds).Ticks;
            var pause = new WaitForSeconds(0.1f);
            try
            {
                while (Game.instance != null && ZNet.instance != null)
                {
                    var current = Player.m_localPlayer;
                    // A respawn can briefly have no local avatar after the dead one is destroyed. An absent
                    // local avatar while the original is still alive instead means this return has departed.
                    if (!current && player && !player.IsDead()) yield break;
                    if (current && current.GetPlayerID() != playerId) yield break;
                    var instance = ZNetScene.instance.FindInstance(item);
                    var drop = instance ? instance.GetComponent<ItemDrop>() : null;
                    if (drop)
                    {
                        var data = Reservation(drop);
                        if (data != null && data.GetLong(PlayerKey, 0) == playerId)
                        {
                            if (!drop.GetComponent<ZNetView>().IsOwner()) drop.RequestOwn();
                            else if (current)
                            {
                                drop.Load();
                                if (current.IsDead() || RecallRecovery.AlreadyCarried(current, drop.m_itemData) ||
                                    !current.GetInventory().CanAddItem(drop.m_itemData))
                                {
                                    if (RecallRecovery.Take(drop.gameObject, drop.m_itemData, current)) yield break;
                                }
                                else
                                {
                                    if (current.Pickup(drop.gameObject, autoPickupDelay: false)) yield break;
                                }
                            }
                        }
                        else if (drop.GetComponent<ZNetView>().GetZDO()?.GetLong(PlayerKey, 0) != 0) yield break;
                    }
                    else if (ZDOMan.instance.GetZDO(item) == null && ZNet.instance.GetTime().Ticks >= arrivalDeadline) yield break;
                    yield return pause;
                }
            }
            finally { Returning.Remove(item); }
        }
    }
}
