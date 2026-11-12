using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(ItemDrop), "Awake")]
    internal static class ItemIceDrag
    {
        private static void Postfix(ItemDrop __instance, Rigidbody ___m_body, ZNetView ___m_nview)
        {
            if (!___m_body || !___m_nview || __instance.IsPiece()) return;
            var contact = __instance.gameObject.AddComponent<ItemIceContact>();
            contact.Item = __instance; contact.Body = ___m_body; contact.View = ___m_nview;
            contact.enabled = false;
        }
    }

    internal sealed class ItemIceContact : MonoBehaviour
    {
        internal ItemDrop Item;
        internal Rigidbody Body;
        internal ZNetView View;
        private double lastStep = double.MinValue;
        private double lastBrake = double.MinValue;
        private HashSet<Collider> supports;
        private int solverIterations, velocityIterations;
        private bool refinedContacts;

        private void OnCollisionEnter(Collision collision) => Brake(collision);
        private void OnCollisionStay(Collision collision) => Brake(collision);

        private void OnCollisionExit(Collision collision)
        {
            if (supports == null) return;
            supports.Remove(collision.collider);
            CheckSupports();
        }

        private void RestoreContacts()
        {
            if (refinedContacts && Body)
            {
                Body.solverIterations = solverIterations;
                Body.solverVelocityIterations = velocityIterations;
            }
            refinedContacts = false; supports?.Clear(); lastBrake = double.MinValue;
            if (enabled) enabled = false;
        }

        private void OnDisable() => RestoreContacts();

        // Unity delivers collisions to disabled behaviours. Only an awake item touching ice needs this small
        // lifetime check: destroying a supporting collider does not reliably deliver OnCollisionExit.
        private void FixedUpdate()
        {
            if (!Body || Body.isKinematic || Body.IsSleeping() || !View || !View.IsValid() || !View.IsOwner())
            { RestoreContacts(); return; }
            CheckSupports();
        }

        private static bool HasIceMaterial(Collider collider)
        {
            if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy) return false;
            var material = collider.sharedMaterial;
            return material && (material.name == "SlipperyIce" || material.name == "SlipperyIce (Instance)");
        }

        private void CheckSupports()
        {
            supports?.RemoveWhere(collider => !HasIceMaterial(collider));
            if (supports == null || supports.Count == 0) RestoreContacts();
        }

        private void Support(Collider collider)
        {
            (supports ??= new HashSet<Collider>()).Add(collider);
            if (refinedContacts || Body.IsSleeping()) return;
            solverIterations = Body.solverIterations; velocityIterations = Body.solverVelocityIterations;
            // The game's two position iterations leave convex trophies rocking even from rest on frictionless ice.
            // Resolve those supporting contacts accurately enough to settle; retain each body's settings off ice.
            Body.solverIterations = Mathf.Max(solverIterations, 6);
            Body.solverVelocityIterations = Mathf.Max(velocityIterations, 2);
            refinedContacts = true; enabled = true;
        }

        private void Brake(Collision collision)
        {
            if (!Body || Body.isKinematic || !View || !View.IsValid() || !View.IsOwner() || Item.IsPiece())
            { RestoreContacts(); return; }
            for (int index = 0; index < collision.contactCount; index++)
            {
                var contact = collision.GetContact(index);
                if (contact.normal.y < .5f || !HasIceMaterial(contact.otherCollider)) continue;
                Support(contact.otherCollider);
                if (Body.IsSleeping() || lastStep == Time.fixedTimeAsDouble) return;
                lastStep = Time.fixedTimeAsDouble;
                var velocity = Body.linearVelocity;
                var alongSurface = Vector3.ProjectOnPlane(velocity, contact.normal);
                // Turning about the support also slides the contact across the ice; an irregular trophy otherwise
                // keeps spinning in place for minutes. Rocking and bouncing about the other axes stay native.
                var spin = Vector3.Project(Body.angularVelocity, contact.normal);
                if (alongSurface.sqrMagnitude == 0f && spin.sqrMagnitude == 0f) return;
                // Velocity writes reset PhysX's sleep counter. Near its sleep speed, apply the accumulated
                // braking in half-second intervals so normal contact settling can finish between writes.
                double elapsed = Time.fixedTimeAsDouble - lastBrake;
                bool settling = alongSurface.sqrMagnitude <= Body.sleepThreshold * 8f && spin.sqrMagnitude <= Body.sleepThreshold * 8f;
                if (settling && elapsed < .5) return;
                float steps = settling && elapsed <= .6 ? (float)elapsed / Time.fixedDeltaTime : 1f;
                float retained = Mathf.Pow(Mathf.Max(0f, 1f - .5f * Time.fixedDeltaTime), steps);
                Body.linearVelocity = velocity - alongSurface * (1f - retained);
                Body.angularVelocity -= spin * (1f - retained);
                lastBrake = Time.fixedTimeAsDouble;
                return;
            }
            if (supports != null)
            {
                supports.Remove(collision.collider);
                CheckSupports();
            }
        }
    }
}
