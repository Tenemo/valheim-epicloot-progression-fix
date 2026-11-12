using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A remote obstacle may still be alive locally when every arrow in a burst reaches it. Keep later arrows at
    // their actual contact until the owner confirms the preceding hit, then process the next real impact in order.
    internal static class DestructibleProjectiles
    {
        private sealed class Contact
        {
            internal Projectile Arrow;
            internal Collider Collider;
            internal Vector3 Point, Normal;
            internal bool Water, Enabled;
        }
        private sealed class Barrier
        {
            internal ZDOID Id;
            internal bool Waiting, Requested;
            internal readonly Queue<Contact> Contacts = new Queue<Contact>();
        }
        private static readonly Dictionary<ZDOID, Barrier> Barriers = new Dictionary<ZDOID, Barrier>();
        private static readonly AccessTools.FieldRef<Projectile, bool> Hit = AccessTools.FieldRefAccess<Projectile, bool>("m_didHit");
        private static Barrier current;

        [HarmonyPatch(typeof(Projectile), nameof(Projectile.OnHit))]
        private static class Impact
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Projectile __instance, Collider collider, Vector3 hitPoint, bool water, Vector3 normal, out Barrier __state)
            {
                __state = current; current = null;
                var owner = GameFields.ProjectileOwner(__instance);
                var weapon = GameFields.ProjectileWeapon(__instance);
                if (!owner || owner != Player.m_localPlayer || weapon == null || __instance.m_onlyStopOnTerrain ||
                    weapon.m_shared.m_skillType != Skills.SkillType.Bows && weapon.m_shared.m_skillType != Skills.SkillType.Crossbows || !collider) return true;
                var obstacle = Projectile.FindHitObject(collider).GetComponent<Destructible>();
                var view = obstacle ? obstacle.GetComponent<ZNetView>() : null;
                if (!view || !view.IsValid()) return true;
                var id = view.GetZDO().m_uid;
                if (!Barriers.TryGetValue(id, out var barrier))
                {
                    if (view.IsOwner()) return true;
                    Barriers.Add(id, barrier = new Barrier { Id = id });
                }
                if (!barrier.Waiting) { barrier.Waiting = true; barrier.Requested = false; current = barrier; return true; }
                barrier.Contacts.Enqueue(new Contact { Arrow = __instance, Collider = collider, Point = hitPoint, Normal = normal,
                    Water = water, Enabled = __instance.enabled });
                // FixedUpdate is already inside its raycast loop. Its native hit flag also stops the rest of that loop.
                Hit(__instance) = true; __instance.enabled = false; __instance.transform.position = hitPoint;
                return false;
            }
            private static void Finalizer(Barrier __state)
            {
                var barrier = current; current = __state;
                // Native bounces and IHitProjectile refusals can finish an impact without sending any damage.
                if (barrier != null && !barrier.Requested) { barrier.Waiting = false; Barriers.Remove(barrier.Id); }
            }
        }

        [HarmonyPatch(typeof(Destructible), nameof(Destructible.Damage))]
        private static class Sent
        {
            private static void Postfix(Destructible __instance, HitData hit)
            {
                var barrier = current;
                var view = __instance.GetComponent<ZNetView>();
                if (barrier != null && !barrier.Requested && view && view.IsValid() && view.GetZDO().m_uid == barrier.Id)
                {
                    barrier.Requested = true;
                    RangedHitConfirmation.ObstacleResult(__instance, hit, destroyed => Resolve(barrier, destroyed));
                }
            }
        }

        private static void Resume(Contact contact, bool destroyed)
        {
            if (!contact.Arrow) return;
            Hit(contact.Arrow) = false; contact.Arrow.enabled = contact.Enabled;
            if (!destroyed && contact.Collider)
                contact.Arrow.OnHit(contact.Collider, contact.Point, contact.Water, contact.Normal);
        }

        private static void Resolve(Barrier barrier, bool destroyed)
        {
            if (!Barriers.TryGetValue(barrier.Id, out var active) || active != barrier) return;
            barrier.Waiting = false;
            while (barrier.Contacts.Count > 0)
            {
                var contact = barrier.Contacts.Dequeue();
                Resume(contact, destroyed);
                if (!destroyed && barrier.Waiting) return;
            }
            Barriers.Remove(barrier.Id);
        }

        internal static void Destroyed(ZDOID id)
        {
            if (Barriers.TryGetValue(id, out var barrier)) Resolve(barrier, true);
        }

        internal static void Clear()
        {
            var contacts = Barriers.Values.SelectMany(barrier => barrier.Contacts).ToArray();
            Barriers.Clear(); current = null;
            foreach (var contact in contacts)
                if (contact.Arrow) { Hit(contact.Arrow) = false; contact.Arrow.enabled = contact.Enabled; }
        }
    }
}
