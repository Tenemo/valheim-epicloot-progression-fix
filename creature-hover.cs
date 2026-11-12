using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(Player), "FindHoverObject")]
    internal static class CreatureHover
    {
        private static readonly AccessTools.FieldRef<Player, float> InteractionDistance = AccessTools.FieldRefAccess<Player, float>("m_maxInteractDistance");
        private static readonly int CreatureMask = LayerMask.GetMask("character", "character_net", "character_ghost");
        private static Collider[] overlapping = new Collider[16];
        private static RaycastHit[] rayHits = new RaycastHit[32];
        internal static Character BodyHover;

        internal static bool StartedTaming(Tameable animal)
        {
            var view = animal.GetComponent<ZNetView>();
            return view && view.IsValid() && view.GetZDO().GetFloat(ZDOVars.s_tameTimeLeft, animal.m_tamingTime) < animal.m_tamingTime;
        }

        internal static bool KnownAnimal(Character character)
        {
            var animal = character.GetComponent<Tameable>();
            return animal && (character.IsTamed() || StartedTaming(animal));
        }

        internal static bool Available(Character character) => character && !character.IsPlayer() && !character.IsDead() &&
            character.GetHealth() > 0f && !FreshCreatureSpawns.Waiting(character) &&
            (!character.GetBaseAI() || !character.GetBaseAI().IsSleeping());

        internal struct CreatureRay
        {
            internal Character Creature;
            internal Vector3 Contact;
            internal bool Inside;
        }

        internal static CreatureRay Trace(Player player, Vector3 origin, Vector3 direction, float distanceLimit, bool tamedOnlyInside, bool castRay = true)
        {
            var eye = player.GetEyePoint();
            int mask = GameFields.InteractMask(player);
            int count = Physics.OverlapSphereNonAlloc(origin, 0.001f, overlapping, mask & CreatureMask, QueryTriggerInteraction.Ignore);
            while (count == overlapping.Length)
            {
                Array.Resize(ref overlapping, overlapping.Length * 2);
                count = Physics.OverlapSphereNonAlloc(origin, 0.001f, overlapping, mask & CreatureMask, QueryTriggerInteraction.Ignore);
            }
            CreatureRay result = default;
            float nearest = float.PositiveInfinity;
            for (int index = 0; index < count; index++)
            {
                var collider = overlapping[index];
                var character = collider.GetComponentInParent<Character>();
                if (!character || character == player || tamedOnlyInside && (!Available(character) || !character.GetComponent<Tameable>()) ||
                    !Measurable(collider) || (collider.ClosestPoint(origin) - origin).sqrMagnitude > 0.00000001f) continue;
                var point = collider.ClosestPoint(eye);
                float distance = (point - eye).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance;
                result = new CreatureRay { Creature = character, Contact = point, Inside = true };
            }
            if (result.Inside || !castRay) return result;
            count = Physics.RaycastNonAlloc(new Ray(origin, direction), rayHits, distanceLimit, mask);
            while (count == rayHits.Length)
            {
                Array.Resize(ref rayHits, rayHits.Length * 2);
                count = Physics.RaycastNonAlloc(new Ray(origin, direction), rayHits, distanceLimit, mask);
            }
            RaycastHit first = default;
            // Nearest first, so only the hits that would win are looked up for the player's own colliders.
            while (true)
            {
                int best = -1;
                float bestDistance = nearest;
                for (int index = 0; index < count; index++)
                    if (rayHits[index].distance < bestDistance) { best = index; bestDistance = rayHits[index].distance; }
                if (best < 0) break;
                if (rayHits[best].collider.GetComponentInParent<Player>() != player) { first = rayHits[best]; break; }
                rayHits[best] = rayHits[--count];
            }
            if (!first.collider) return default;
            return new CreatureRay { Creature = first.collider.GetComponentInParent<Character>(),
                Contact = Measurable(first.collider) ? first.collider.ClosestPoint(eye) : first.point };
        }

        internal static bool ClearBodyPath(Player player, Vector3 contact) =>
            !Physics.Linecast(player.GetEyePoint(), contact, GameFields.InteractMask(player) & ~CreatureMask, QueryTriggerInteraction.Ignore);

        private static void Postfix(Player __instance, ref GameObject hover, ref Character hoverCreature)
        {
            BodyHover = null;
            var camera = Utils.GetMainCamera();
            if (!camera || __instance.IsDead()) return;
            var origin = camera.transform.position;
            bool cast = !hover && EnemyHud.instance && (!hoverCreature || KnownAnimal(hoverCreature));
            float range = EnemyHud.instance ? EnemyHud.instance.m_maxShowDistance : 0f;
            var result = Trace(__instance, origin, camera.transform.forward, range + Vector3.Distance(origin, __instance.transform.position), true, cast);
            if (result.Inside)
            {
                BodyHover = result.Creature;
                hoverCreature = result.Creature;
                hover = Vector3.Distance(__instance.GetEyePoint(), result.Contact) < InteractionDistance(__instance) + result.Creature.GetHoverOffset() &&
                    ClearBodyPath(__instance, result.Contact) ? result.Creature.gameObject : null;
                return;
            }
            if (!cast || !Available(result.Creature) || !KnownAnimal(result.Creature) ||
                Vector3.Distance(__instance.transform.position, result.Creature.transform.position) >= range) return;
            hoverCreature = result.Creature;
            if (Vector3.Distance(__instance.GetEyePoint(), result.Contact) < InteractionDistance(__instance) + result.Creature.GetHoverOffset() && ClearBodyPath(__instance, result.Contact))
            {
                hover = result.Creature.gameObject;
                BodyHover = result.Creature;
            }
        }

        private static bool Measurable(Collider collider) => collider is BoxCollider || collider is SphereCollider ||
            collider is CapsuleCollider || collider is MeshCollider mesh && mesh.convex;
    }

    // The native hover names only the species, so a renamed Alert Lox read as a plain Lox, its own name shown above it
    // instead of StarLevelSystem's. The hover now names the species with its modifiers, as SLS names an unnamed animal.
    [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText))]
    internal static class TamingHoverText
    {
        private static readonly Func<Tameable, int> Tameness = AccessTools.MethodDelegate<Func<Tameable, int>>(AccessTools.Method(typeof(Tameable), "GetTameness"));

        private static string SpeciesName(Tameable animal)
        {
            string name = StarLevelSystem.Installed && animal.TryGetComponent<Character>(out var character) ? StarLevelSystem.CreatureNameOf(character) : null;
            return name != null ? Localization.instance.Localize(name) : animal.GetName();
        }

        internal static string Progress(Tameable animal) => SpeciesName(animal) + Localization.instance.Localize(
            " ( $hud_tameness " + Tameness(animal) + "%, " + animal.GetStatusString() + " )");

        private static void Postfix(Tameable __instance, ref string __result)
        {
            if (!__instance.IsTamed() && CreatureHover.StartedTaming(__instance) && Tameness(__instance) == 0)
                __result = Progress(__instance);
            else if (__result.StartsWith(__instance.GetName(), StringComparison.Ordinal))
                __result = SpeciesName(__instance) + __result.Substring(__instance.GetName().Length);
        }
    }

    [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
    internal static class DistantTamingHover
    {
        private static void Postfix(Hud __instance, Player player)
        {
            if (player.IsDead() || player.GetDoodadController() != null || player.GetHoverObject() || TextViewer.instance.IsVisible()) return;
            var creature = player.GetHoverCreature();
            var animal = creature ? creature.GetComponent<Tameable>() : null;
            if (animal && !animal.IsTamed() && CreatureHover.StartedTaming(animal) && EnemyHud.instance &&
                Vector3.Distance(player.transform.position, creature.transform.position) < EnemyHud.instance.m_maxShowDistance)
                __instance.m_hoverName.text = TamingHoverText.Progress(animal);
        }
    }
}
