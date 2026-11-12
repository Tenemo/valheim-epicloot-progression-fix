using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class SummonDismissal
    {
        // The pack's creature hover distance, also verified against EnemyHud's installed prefab.
        internal const float Range = 60f;
        private const string RequestRpc = Plugin.Id + ".dismiss-summon-request";
        private const string OwnerRpc = Plugin.Id + ".dismiss-summon-owner";
        private sealed class Gesture { internal bool Consumed; }
        private static readonly ConditionalWeakTable<Player, Gesture> Gestures = new ConditionalWeakTable<Player, Gesture>();
        private static readonly ConditionalWeakTable<Character, object> Removed = new ConditionalWeakTable<Character, object>();
        private static readonly object Removal = new object();
        private static readonly Func<Player, bool> TakeInput = AccessTools.MethodDelegate<Func<Player, bool>>(AccessTools.Method(typeof(Player), "TakeInput"));
        private static readonly Func<Character, bool> CanMove = AccessTools.MethodDelegate<Func<Character, bool>>(AccessTools.Method(typeof(Character), "CanMove"));
        private static readonly AccessTools.FieldRef<Player, bool> Pressed = AccessTools.FieldRefAccess<Player, bool>("m_secondaryAttack");
        private static readonly AccessTools.FieldRef<Player, bool> Held = AccessTools.FieldRefAccess<Player, bool>("m_secondaryAttackHold");
        private static readonly AccessTools.FieldRef<Player, float> Queued = AccessTools.FieldRefAccess<Player, float>("m_queuedSecondAttackTimer");
        private static readonly Action<Tameable> Unsummon = AccessTools.MethodDelegate<Action<Tameable>>(AccessTools.Method(typeof(Tameable), "UnSummon"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SetControls)), postfix: new HarmonyMethod(typeof(SummonDismissal), nameof(Controls)));
            harmony.Patch(AccessTools.Method(typeof(Player), "PlayerAttackInput"), prefix: new HarmonyMethod(typeof(SummonDismissal), nameof(Press)));
            harmony.Patch(AccessTools.Method(typeof(ZNet), "OnNewConnection"), postfix: new HarmonyMethod(typeof(SummonDismissal), nameof(Register)));
            harmony.Patch(AccessTools.Method(typeof(Hud), "UpdateCrosshair"), postfix: new HarmonyMethod(typeof(SummonDismissal), nameof(HoverHint)));

        }

        private static string EquippedStaff(Player player) => player.GetCurrentWeapon()?.m_dropPrefab?.name;
        private static int EquippedWeapon(ZDO actor)
        {
            int right = actor.GetInt(ZDOVars.s_rightItem);
            var item = right == 0 ? null : ObjectDB.instance.GetItemPrefab(right)?.GetComponent<ItemDrop>()?.m_itemData;
            if (right != 0 && (item == null || item.IsWeapon())) return right;
            int left = actor.GetInt(ZDOVars.s_leftItem);
            item = left == 0 ? null : ObjectDB.instance.GetItemPrefab(left)?.GetComponent<ItemDrop>()?.m_itemData;
            return item != null && item.IsWeapon() && item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Torch ? left : 0;
        }
        private static bool Eligible(Player player, Character creature) => CreatureHover.Available(creature) &&
            SummonOwnership.TryGet(creature, out long summoner, out string staff) && summoner == player.GetPlayerID() &&
            staff == SummonOwnership.StaffFor(GameFields.CharacterView(creature).GetZDO()) && EquippedStaff(player) == staff;
        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private static CreatureHover.CreatureRay Aim(Player player, Vector3 origin, Vector3 direction)
        {
            if (!Finite(origin) || !Finite(direction) || direction.sqrMagnitude < .9f || direction.sqrMagnitude > 1.1f ||
                Vector3.Distance(origin, player.GetEyePoint()) > Range) return default;
            var hit = CreatureHover.Trace(player, origin, direction.normalized, Range + Vector3.Distance(origin, player.transform.position), false);
            if (!hit.Creature || Vector3.Distance(player.GetEyePoint(), hit.Contact) > Range || !CreatureHover.ClearBodyPath(player, hit.Contact) ||
                ParticleMist.IsMistBlocked(player.GetCenterPoint(), hit.Creature.GetCenterPoint())) return default;
            return hit;
        }

        private static void Controls(Player __instance, bool secondaryAttack, bool secondaryAttackHold)
        {
            if (__instance == Player.m_localPlayer && !secondaryAttack && !secondaryAttackHold && Gestures.TryGetValue(__instance, out var gesture))
                gesture.Consumed = false;
        }

        private static void Press(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var gesture = Gestures.GetValue(__instance, _ => new Gesture());
            if (!gesture.Consumed && Pressed(__instance) && TakeInput(__instance) && !__instance.InPlaceMode() &&
                !__instance.InAttack() && !__instance.InDodge() && CanMove(__instance) && !__instance.IsKnockedBack() &&
                !__instance.IsStaggering() && !__instance.InMinorAction() && SummonOwnership.IsStaff(EquippedStaff(__instance)))
            {
                var camera = Utils.GetMainCamera();
                if (camera)
                {
                    var origin = camera.transform.position; var direction = camera.transform.forward;
                    var target = Aim(__instance, origin, direction).Creature;
                    if (Eligible(__instance, target))
                    {
                        var package = new ZPackage(); package.Write(target.GetZDOID()); package.Write(origin); package.Write(direction);
                        if (ZNet.instance.IsServer())
                        {
                            package.SetPos(0);
                            Dispatch(GameFields.CharacterView(__instance).GetZDO(), package);
                            gesture.Consumed = true;
                        }
                        else if (ZNet.instance.GetServerPeer()?.m_rpc is ZRpc server && server.IsConnected())
                        {
                            server.Invoke(RequestRpc, package);
                            gesture.Consumed = true;
                        }
                    }
                }
            }
            if (gesture.Consumed) { Pressed(__instance) = false; Held(__instance) = false; Queued(__instance) = 0f; }
        }

        private static void Register(ZNet __instance, ZNetPeer peer)
        {
            if (__instance.IsServer())
                peer.m_rpc.Register<ZPackage>(RequestRpc, (rpc, package) =>
                {
                    if (rpc != peer.m_rpc || !peer.IsReady() || !rpc.IsConnected()) return;
                    var actor = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (actor != null && actor.GetOwner() == peer.m_uid && !actor.GetBool(ZDOVars.s_dead)) Dispatch(actor, package);
                });
            else if (peer.m_server)
                peer.m_rpc.Register<ZPackage>(OwnerRpc, (rpc, package) =>
                {
                    if (!ZNet.instance || ZNet.instance.GetServerPeer()?.m_rpc != rpc || package.Size() > 128) return;
                    try { Remove(package.ReadZDOID(), package.ReadZDOID(), package.ReadVector3(), package.ReadVector3()); }
                    catch (EndOfStreamException) { /* Reject an incomplete delivery. */ }
                });
        }

        private static void Dispatch(ZDO actor, ZPackage package)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || actor == null || actor.GetBool(ZDOVars.s_dead) || package.Size() > 128) return;
            ZDOID target; Vector3 origin, direction;
            try { target = package.ReadZDOID(); origin = package.ReadVector3(); direction = package.ReadVector3(); }
            catch (EndOfStreamException) { return; }
            if (!Finite(origin) || !Finite(direction)) return;
            var data = ZDOMan.instance.GetZDO(target); string staff = SummonOwnership.StaffFor(data);
            long player = actor.GetLong(ZDOVars.s_playerID);
            if (staff == null || player == 0 || EquippedWeapon(actor) != staff.GetStableHashCode()) return;
            if (!SummonOwnership.Recorded(data, player, staff))
            {
                if (!SummonOwnership.Unrecorded(data)) return;
                string following = data.GetString(ZDOVars.s_follow);
                var names = ZNet.instance.GetPeers().Where(peer => peer.IsReady() && peer.m_playerName == following).Select(peer => peer.m_characterID).ToList();
                if (Player.m_localPlayer && Player.m_localPlayer.GetPlayerName() == following) names.Add(Player.m_localPlayer.GetZDOID());
                if (string.IsNullOrEmpty(following) || names.Count != 1 || names[0] != actor.m_uid) return;
            }
            if (data.GetOwner() == ZDOMan.GetSessionID()) Remove(actor.m_uid, target, origin, direction);
            else
            {
                var owner = ZNet.instance.GetPeer(data.GetOwner());
                if (owner == null || !owner.IsReady() || !owner.m_rpc.IsConnected()) return;
                var delivery = new ZPackage(); delivery.Write(actor.m_uid); delivery.Write(target); delivery.Write(origin); delivery.Write(direction);
                owner.m_rpc.Invoke(OwnerRpc, delivery);
            }
        }

        private static void Remove(ZDOID actorId, ZDOID targetId, Vector3 origin, Vector3 direction)
        {
            if (!ZNetScene.instance || ZDOMan.instance == null) return;
            var actor = ZNetScene.instance.FindInstance(actorId)?.GetComponent<Player>();
            var target = ZNetScene.instance.FindInstance(targetId)?.GetComponent<Character>();
            var view = target ? GameFields.CharacterView(target) : null;
            var actorView = actor ? GameFields.CharacterView(actor) : null;
            if (!actor || actor.IsDead() || !actorView || !actorView.IsValid() || !view || !view.IsValid() || !view.IsOwner() ||
                !CreatureHover.Available(target) || Removed.TryGetValue(target, out _)) return;
            if (!SummonOwnership.TryGet(target, out long summoner, out string staff) || summoner != actor.GetPlayerID() ||
                staff != SummonOwnership.StaffFor(view.GetZDO()) || EquippedWeapon(actorView.GetZDO()) != staff.GetStableHashCode()) return;
            if (Aim(actor, origin, direction).Creature != target) return;
            Removed.Add(target, Removal);
            var tame = target.GetComponent<Tameable>();
            if (tame) Unsummon(tame);
            else ZNetScene.instance.Destroy(target.gameObject);
        }

        private static string Binding()
        {
            string key = ZInput.instance?.GetBoundKeyString(ZInput.IsGamepadActive() ? "JoySecondaryAttack" : "SecondaryAttack", true);
            return string.IsNullOrEmpty(key) ? "Secondary attack" : Localization.instance.Localize(key);
        }

        private static void HoverHint(Hud __instance, Player player)
        {
            if (player != Player.m_localPlayer || !TakeInput(player) || !SummonOwnership.IsStaff(EquippedStaff(player))) return;
            var camera = Utils.GetMainCamera(); if (!camera) return;
            var target = Aim(player, camera.transform.position, camera.transform.forward).Creature;
            if (!Eligible(player, target)) return;
            string original = __instance.m_hoverName.text;
            if (string.IsNullOrWhiteSpace(original) || player.GetHoverCreature() != target) original = target.GetHoverName();
            __instance.m_hoverName.text = original + "\n[<color=yellow><b>" + Binding() + "</b></color>] Dismiss summon";
        }

    }
}
