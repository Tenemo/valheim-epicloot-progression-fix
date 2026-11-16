using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Player-built pieces take no damage from tames, which include most player summons, from the magic of a player's staff,
    // Dundr or Jotun Witch form, or from the Trollstav's landing blast. The untamed troll's own attacks still hit them, and
    // ruins and other world-generated buildings take every hit as before.
    internal static class FriendlyBuildingDamage
    {
        private sealed class Origin
        {
            internal readonly ZDOID Creature;
            internal Origin(ZDOID creature) { Creature = creature; }
        }

        private const string OriginKey = Plugin.Id + ".friendly-origin";
        private static readonly ConditionalWeakTable<Component, Origin> Origins = new ConditionalWeakTable<Component, Origin>();
        private static Origin current;

        private static ZNetView EffectView(Component effect)
        {
            var view = effect is Aoe area ? GameFields.AoeView(area) : effect.GetComponent<ZNetView>();
            // A built-in trampling effect shares its creature's view; its tame flag already supplies the origin there.
            return view && !view.GetComponent<Character>() ? view : null;
        }

        private static Origin Capture(Character owner, ItemDrop.ItemData weapon = null)
        {
            if (!owner) return current;
            return (owner.IsPlayer() ? IsMagic(weapon) : owner.IsTamed()) ? Source(owner) : null;
        }

        private static Origin Source(Character owner)
        {
            var view = GameFields.CharacterView(owner);
            return view && view.IsValid() ? new Origin(view.GetZDO().m_uid) : null;
        }

        private static bool IsMagic(ItemDrop.ItemData weapon) => weapon != null && (HexenCollateralDamage.IsWitchWeapon(weapon) ||
            weapon.m_shared.m_skillType == Skills.SkillType.ElementalMagic || weapon.m_shared.m_skillType == Skills.SkillType.BloodMagic);

        private static void Remember(Component effect, Origin origin)
        {
            Origins.Remove(effect);
            if (origin != null) Origins.Add(effect, origin);
            var view = EffectView(effect);
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(OriginKey, origin?.Creature ?? ZDOID.None);
        }

        private static Origin Read(Component effect)
        {
            if (Origins.TryGetValue(effect, out var origin)) return origin;
            var view = EffectView(effect);
            var creature = view && view.IsValid() ? view.GetZDO().GetZDOID(OriginKey) : ZDOID.None;
            return creature == ZDOID.None ? null : new Origin(creature);
        }

        // Spawn abilities instantiate their areas directly, outside the attack/effect-list call stack.
        internal static void Remember(GameObject effect, Character owner)
        {
            if (!effect) return;
            var origin = Capture(owner);
            foreach (var area in effect.GetComponentsInChildren<Aoe>(true)) Remember(area, origin);
            foreach (var projectile in effect.GetComponentsInChildren<Projectile>(true)) Remember(projectile, origin);
        }

        private static bool FromFriend(HitData hit)
        {
            if (hit.m_attacker == ZDOID.None) return current != null;
            if (current != null && hit.m_attacker == current.Creature) return true;
            var attacker = hit.GetAttacker();
            if (attacker) return !attacker.IsPlayer() && attacker.IsTamed();
            return ZDOMan.instance?.GetZDO(hit.m_attacker)?.GetBool(ZDOVars.s_tamed) == true;
        }

        [HarmonyPatch]
        private static class BuildingHits
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var type in new[] { typeof(WearNTear), typeof(Destructible) })
                {
                    yield return AccessTools.Method(type, "Damage", new[] { typeof(HitData) });
                    yield return AccessTools.Method(type, "RPC_Damage", new[] { typeof(long), typeof(HitData) });
                }
            }

            private static bool Prefix(Component __instance, HitData hit)
            {
                var piece = __instance.GetComponent<Piece>();
                if (!piece || piece.GetCreator() == 0L) return true;
                // Planted vegetation retains its existing crop/trampling policy and normal tree damage.
                if (__instance is Destructible destructible && (__instance.GetComponent<Plant>() || __instance.GetComponent<Pickable>() ||
                    (destructible.GetDestructibleType() & DestructibleType.Tree) != 0)) return true;
                return !FromFriend(hit);
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class ProjectileSetup
        {
            private static void Postfix(Projectile __instance, Character owner, ItemDrop.ItemData item) => Remember(__instance, Capture(owner, item));
        }

        [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup))]
        private static class AreaSetup
        {
            // The Trollstav's landing blast belongs to the troll it brings, which isn't tamed.
            private static void Postfix(Aoe __instance, Character owner, ItemDrop.ItemData item) =>
                Remember(__instance, owner && Utils.GetPrefabName(__instance.transform.root.gameObject) == StaffQuality.TrollLanding ? Source(owner) : Capture(owner, item));
        }

        [HarmonyPatch(typeof(Aoe), "Awake")]
        private static class AreaAwake
        {
            private static void Postfix(Aoe __instance)
            {
                if (current != null) Remember(__instance, current);
            }
        }

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
        private static class ProjectileImpact
        {
            private static void Prefix(Projectile __instance, out Origin __state)
            {
                __state = current;
                current = Read(__instance) ?? Capture(GameFields.ProjectileOwner(__instance));
            }
            private static void Finalizer(Origin __state) => current = __state;
        }

        [HarmonyPatch(typeof(Aoe), "OnHit")]
        private static class AreaImpact
        {
            private static void Prefix(Aoe __instance, out Origin __state)
            {
                __state = current;
                var owner = GameFields.AoeOwner(__instance);
                current = Read(__instance) ?? Capture(owner ? owner : __instance.GetComponentInParent<Character>());
            }
            private static void Finalizer(Origin __state) => current = __state;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
        private static class DeathEffects
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(Character __instance, out Origin __state)
            {
                __state = current;
                current = Capture(__instance);
            }
            private static void Finalizer(Origin __state) => current = __state;
        }

        // The Jotun Witch form's magic blast is a melee attack.
        [HarmonyPatch]
        private static class DirectAttack
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(Attack), "DoAreaAttack");
                yield return AccessTools.Method(typeof(Attack), "DoMeleeAttack");
            }

            [HarmonyPriority(Priority.First)]
            private static void Prefix(Attack __instance, out Origin __state)
            {
                __state = current;
                current = Capture(GameFields.AttackCharacter(__instance), GameFields.AttackWeapon(__instance));
            }
            private static void Finalizer(Origin __state) => current = __state;
        }
    }
}
