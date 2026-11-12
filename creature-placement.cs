using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class CreaturePlacement
    {
        private const float ContactTolerance = .01f;
        private static readonly int GroundMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "piece_nonsolid", "vehicle");
        private static readonly AccessTools.FieldRef<Character, float> MaximumAltitude = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude");
        private static readonly AccessTools.FieldRef<Character, float> LastGroundTouch = AccessTools.FieldRefAccess<Character, float>("m_lastGroundTouch");
        private static readonly AccessTools.FieldRef<Character, Vector3> Velocity = AccessTools.FieldRefAccess<Character, Vector3>("m_currentVel");
        private static readonly AccessTools.FieldRef<Character, Vector3> LastPosition = AccessTools.FieldRefAccess<Character, Vector3>("m_lastPos");
        private static readonly AccessTools.FieldRef<Character, Vector3> LazyPosition = AccessTools.FieldRefAccess<Character, Vector3>("m_lazyPos");
        private static readonly Action<Character> ResetGround = AccessTools.MethodDelegate<Action<Character>>(AccessTools.Method(typeof(Character), "ResetGroundContact"));

        internal static bool Clear(Character character, Vector3 position, out Collider obstacle)
        {
            obstacle = null;
            var body = character.GetComponent<Rigidbody>();
            var size = CreatureClearance.Measure(character);
            if (!size.Valid || !body) return true;
            var shift = position - character.transform.position;
            var nearby = Physics.OverlapBox(size.Bounds.center + shift, size.Bounds.extents + Vector3.one * ContactTolerance,
                Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            foreach (var shape in CreatureClearance.Colliders(character))
            {
                if (!CreatureClearance.Obstructs(shape, body)) continue;
                foreach (var other in nearby)
                {
                    if (!other || !other.enabled || other.transform.IsChildOf(character.transform) || other.attachedRigidbody == body ||
                        (other.attachedRigidbody && !other.attachedRigidbody.detectCollisions) || !CreatureClearance.CanContact(shape, other)) continue;
                    if (Physics.ComputePenetration(shape, shape.transform.position + shift, shape.transform.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth) && depth > ContactTolerance)
                    { obstacle = other; return false; }
                }
            }
            return true;
        }

        // A body placed where it overlaps the room because it fits nowhere would be shoved out at once: up through a thin
        // ceiling onto the roof, down through the floor or over a wall. It is only kept from moving deeper until it stands
        // clear, then separates as usual again.
        internal static void Settle(Character character)
        {
            var body = character.GetComponent<Rigidbody>();
            if (!body) return;
            float separation = body.maxDepenetrationVelocity;
            body.maxDepenetrationVelocity = 0f;
            character.StartCoroutine(Release(character, body, separation));
        }

        private static IEnumerator Release(Character character, Rigidbody body, float separation)
        {
            var wait = new WaitForSeconds(.5f);
            while (character && !Clear(character, character.transform.position, out _)) yield return wait;
            if (body) body.maxDepenetrationVelocity = separation;
        }

        internal static bool Separate(Character first, Vector3 firstPosition, Character second, Vector3 secondPosition)
        {
            var firstBody = first.GetComponent<Rigidbody>(); var secondBody = second.GetComponent<Rigidbody>();
            var firstShift = firstPosition - first.transform.position; var secondShift = secondPosition - second.transform.position;
            foreach (var shape in CreatureClearance.Colliders(first))
            {
                if (!CreatureClearance.Obstructs(shape, firstBody)) continue;
                foreach (var other in CreatureClearance.Colliders(second))
                {
                    if (!CreatureClearance.Obstructs(other, secondBody) || !CreatureClearance.CanContact(shape, other)) continue;
                    if (Physics.ComputePenetration(shape, shape.transform.position + firstShift, shape.transform.rotation,
                        other, other.transform.position + secondShift, other.transform.rotation, out _, out float depth) && depth > ContactTolerance) return false;
                }
            }
            return true;
        }

        // Only look around the requested floor level; a ceiling or a different dungeon storey is not a landing site.
        internal static bool CanSupport(Character character, Collider surface)
        {
            if (!surface || surface.GetComponentInParent<Character>() || (surface.attachedRigidbody && !surface.attachedRigidbody.detectCollisions)) return false;
            var body = character.GetComponent<Rigidbody>();
            foreach (var shape in CreatureClearance.Colliders(character))
                if (CreatureClearance.Obstructs(shape, body) && CreatureClearance.CanContact(shape, surface)) return true;
            return false;
        }

        // Rise is how far above the requested level a supporting surface may be; the probe starts 1 m above it.
        internal static bool Floor(Character character, Vector3 point, float floorLevel, float offset, out Vector3 position, float rise = .25f)
        {
            position = point;
            var size = CreatureClearance.Measure(character);
            if (!size.Valid) return false;
            var ray = new Vector3(point.x, floorLevel + 1f, point.z);
            bool found = false; float closest = float.PositiveInfinity, height = 0f; Collider support = null;
            foreach (var hit in Physics.RaycastAll(ray, Vector3.down, 3f, GroundMask, QueryTriggerInteraction.Ignore))
            {
                if (!CanSupport(character, hit.collider) || hit.normal.y < .25f ||
                    hit.point.y > floorLevel + rise) continue;
                float distance = Mathf.Abs(hit.point.y - floorLevel);
                if (distance >= closest) continue;
                closest = distance; height = hit.point.y; support = hit.collider; found = true;
            }
            if (!found) return false;
            position.y = height - size.Bottom + Mathf.Max(ContactTolerance * 2f, offset);
            var body = character.GetComponent<Rigidbody>();
            float startingHeight = position.y;
            // A capsule resting on a slope needs more clearance than the ground height at its centre.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                float raise = 0f; var shift = position - character.transform.position;
                foreach (var shape in CreatureClearance.Colliders(character))
                    if (CreatureClearance.Obstructs(shape, body) && Physics.ComputePenetration(shape, shape.transform.position + shift, shape.transform.rotation,
                        support, support.transform.position, support.transform.rotation, out var direction, out float depth) && depth > ContactTolerance)
                    {
                        if (direction.y < .25f) return false;
                        raise = Mathf.Max(raise, depth / direction.y + ContactTolerance);
                    }
                if (raise == 0f) return true;
                position.y += raise;
                if (position.y - startingHeight > Mathf.Max(.5f, size.Radius * 2f)) return false;
            }
            return true;
        }

        internal static IEnumerable<Vector3> Candidates(Vector3 center, float radius)
        {
            yield return center;
            if (radius <= 0f) yield break;
            for (int ring = 1; ring <= 3; ring++)
                for (int direction = 0; direction < 8; direction++)
                    yield return center + Quaternion.Euler(0f, direction * 45f + (ring % 2 == 0 ? 22.5f : 0f), 0f) * Vector3.forward * (radius * ring / 3f);
        }

        internal static void Move(Character character, Vector3 position)
        {
            var view = GameFields.CharacterView(character);
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            var body = character.GetComponent<Rigidbody>();
            character.transform.position = body.position = position; body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            view.GetZDO().SetPosition(position);
            MaximumAltitude(character) = position.y; Velocity(character) = Vector3.zero;
            LastPosition(character) = LazyPosition(character) = position;
            ResetGround(character); LastGroundTouch(character) = 1f;
        }
    }
}
