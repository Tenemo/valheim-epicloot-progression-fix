using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A cart that another game simulates, or that nobody simulates while DistantBuildings keeps it loaded, follows its
    // saved pose by the chassis alone: native sync stops the chassis body without gravity while both jointed wheels keep
    // falling, so the joints pull the parts apart and a new owner saves the displaced pose. Such a cart now carries its
    // wheels where their joints hold them, turning them as it rolls, and gives them the chassis's motion back once this
    // game simulates it again.
    internal static class CartNetworkPhysics
    {
        private sealed class Wheel
        {
            internal Rigidbody Body;
            // Where the joint holds the wheel and the prefab's turn of it, both relative to the chassis.
            internal Vector3 Position;
            internal Quaternion Rotation;
            // The joint's free axis in the wheel's space.
            internal Vector3 Axle;
            internal float Radius;
            internal float Angle;
            // Radians per second about the axle, which the rolling sound follows.
            internal float Spin;
            internal Vector3 Last;
        }

        private sealed class Cart
        {
            internal ZSyncTransform Sync;
            internal ZNetView View;
            internal Rigidbody Chassis;
            internal Wheel[] Wheels;
            internal bool Parked;
            internal float Synced;
            internal int Frame;
        }

        private static readonly Dictionary<ZSyncTransform, Cart> Carts = new Dictionary<ZSyncTransform, Cart>();
        private static readonly Dictionary<Vagon, Cart> ByVagon = new Dictionary<Vagon, Cart>();
        // Every object this game simulates synchronizes each frame; only a parked cart has anything to release.
        private static readonly List<Cart> ParkedCarts = new List<Cart>();

        [HarmonyPatch(typeof(ZSyncTransform), "OnEnable")]
        private static class Register
        {
            private static void Postfix(ZSyncTransform __instance)
            {
                var vagon = __instance.GetComponent<Vagon>();
                if (!vagon) return;
                var chassis = __instance.GetComponent<Rigidbody>();
                var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(vagon.gameObject))?.GetComponent<Vagon>() : null;
                if (!prefab || !chassis || prefab.m_wheels.Length != vagon.m_wheels.Length) { Refuse(vagon, "its prefab or wheels"); return; }
                var wheels = new Wheel[vagon.m_wheels.Length];
                for (int index = 0; index < wheels.Length; index++)
                {
                    var body = vagon.m_wheels[index];
                    var joint = body ? body.GetComponent<ConfigurableJoint>() : null;
                    var mesh = body ? body.GetComponent<MeshCollider>()?.sharedMesh : null;
                    if (!joint || joint.connectedBody != chassis || body.transform.parent != chassis.transform || !mesh) { Refuse(vagon, "its wheel joints"); return; }
                    // The joint frame's x is its axis, z the cross of its axis and secondary axis; the wheel turns about the
                    // one angular axis left free.
                    var x = joint.axis.normalized;
                    var z = Vector3.Cross(joint.axis, joint.secondaryAxis).normalized;
                    var y = Vector3.Cross(z, x);
                    var free = new[] { (joint.angularXMotion, x), (joint.angularYMotion, y), (joint.angularZMotion, z) }
                        .Where(motion => motion.Item1 == ConfigurableJointMotion.Free).Select(motion => motion.Item2).ToArray();
                    if (free.Length != 1 || joint.xMotion != ConfigurableJointMotion.Locked || joint.yMotion != ConfigurableJointMotion.Locked ||
                        joint.zMotion != ConfigurableJointMotion.Locked) { Refuse(vagon, "its axles"); return; }
                    var extents = mesh.bounds.extents;
                    // The joint holds the wheel where the prefab places it, having configured its connected anchor from there.
                    var model = prefab.m_wheels[index].transform;
                    wheels[index] = new Wheel
                    {
                        Body = body,
                        Position = model.localPosition,
                        Rotation = model.localRotation,
                        Axle = free[0],
                        Radius = new[] { Vector3.right * extents.x, Vector3.up * extents.y, Vector3.forward * extents.z }
                            .Max(extent => Vector3.ProjectOnPlane(extent, free[0]).magnitude) * body.transform.lossyScale.x,
                    };
                }
                var cart = new Cart { Sync = __instance, View = __instance.GetComponent<ZNetView>(), Chassis = chassis, Wheels = wheels };
                Carts[__instance] = cart;
                ByVagon[vagon] = cart;
            }

            private static void Refuse(Vagon vagon, string changed) => DisabledFeatures.Set("Cart network physics", Utils.GetPrefabName(vagon.gameObject) + " changed " + changed);
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OnDisable")]
        private static class Unregister
        {
            private static void Postfix(ZSyncTransform __instance)
            {
                if (!Carts.TryGetValue(__instance, out var cart)) return;
                Carts.Remove(__instance);
                ByVagon.Remove(__instance.GetComponent<Vagon>());
                if (cart.Parked) Release(cart);
            }
        }

        [HarmonyPatch(typeof(ZSyncTransform), "ClientSync")]
        private static class Replica
        {
            private static void Postfix(ZSyncTransform __instance)
            {
                if (!Carts.TryGetValue(__instance, out var cart) || !cart.View.IsValid() || cart.View.IsOwner()) return;
                if (!cart.Parked) Park(cart);
                Turn(cart);
            }
        }

        [HarmonyPatch(typeof(ZSyncTransform), "OwnerSync")]
        private static class Owner
        {
            // Native sync has moved the chassis to its saved pose on the first frame this game owns the cart.
            private static void Postfix(ZSyncTransform __instance)
            {
                for (int index = 0; index < ParkedCarts.Count; index++)
                {
                    var cart = ParkedCarts[index];
                    if (!ReferenceEquals(cart.Sync, __instance)) continue;
                    if (cart.View.IsValid() && cart.View.IsOwner()) Release(cart);
                    return;
                }
            }
        }

        // Native UpdateAudio follows the wheel bodies' angular speed, which a carried wheel does not have.
        [HarmonyPatch(typeof(Vagon), "UpdateAudio")]
        private static class RollingSound
        {
            private static bool Prefix(Vagon __instance, float dt)
            {
                if (!ByVagon.TryGetValue(__instance, out var cart) || !cart.Parked) return true;
                float speed = 0f;
                foreach (var wheel in cart.Wheels) speed += Mathf.Abs(wheel.Spin);
                speed /= cart.Wheels.Length;
                float pitch = Mathf.Lerp(__instance.m_minPitch, __instance.m_maxPitch, Mathf.Clamp01(speed / __instance.m_maxPitchVel));
                float volume = __instance.m_maxVol * Mathf.Clamp01(speed / __instance.m_maxVolVel);
                foreach (var loop in __instance.m_wheelLoops)
                {
                    loop.SetVolumeModifier(Mathf.MoveTowards(loop.GetVolumeModifier(), volume, __instance.m_audioChangeSpeed * dt));
                    loop.SetPitchModifier(Mathf.MoveTowards(loop.GetPitchModifier(), pitch, __instance.m_audioChangeSpeed * dt));
                }
                return false;
            }
        }

        // Kinematic wheels follow the chassis's transform without simulating, keeping the angle each has turned about its
        // axle and dropping any other tilt.
        private static void Park(Cart cart)
        {
            foreach (var wheel in cart.Wheels)
            {
                var turn = Quaternion.Inverse(wheel.Rotation) * wheel.Body.transform.localRotation;
                float along = Vector3.Dot(new Vector3(turn.x, turn.y, turn.z), wheel.Axle);
                wheel.Angle = 2f * Mathf.Atan2(along, turn.w) * Mathf.Rad2Deg;
                wheel.Spin = 0f;
                wheel.Body.isKinematic = true;
                Place(wheel);
                wheel.Last = wheel.Body.transform.position;
            }
            cart.Parked = true;
            ParkedCarts.Add(cart);
            cart.Synced = Time.fixedTime;
            cart.Frame = Time.frameCount;
        }

        // Each wheel turns by how far it rolled across the ground since the last synchronization, which native sync applies
        // once per rendered frame.
        private static void Turn(Cart cart)
        {
            float elapsed = Time.fixedTime - cart.Synced;
            if (cart.Frame == Time.frameCount || elapsed <= 0f) return;
            cart.Synced = Time.fixedTime;
            cart.Frame = Time.frameCount;
            var up = cart.Chassis.transform.up;
            foreach (var wheel in cart.Wheels)
            {
                var moved = wheel.Body.transform.position - wheel.Last;
                // Native sync snaps a chassis more than 5 m off its saved position instead of moving it there.
                float turned = moved.sqrMagnitude < 25f ? Vector3.Dot(Vector3.Cross(up, moved), wheel.Body.transform.TransformDirection(wheel.Axle)) / wheel.Radius : 0f;
                wheel.Angle = Mathf.Repeat(wheel.Angle + turned * Mathf.Rad2Deg, 360f);
                wheel.Spin = turned / elapsed;
                Place(wheel);
                wheel.Last = wheel.Body.transform.position;
            }
        }

        private static void Place(Wheel wheel)
        {
            wheel.Body.transform.localPosition = wheel.Position;
            wheel.Body.transform.localRotation = wheel.Rotation * Quaternion.AngleAxis(wheel.Angle, wheel.Axle);
        }

        // The wheels start again from where their joints hold them, moving with the chassis and rolling as they turned.
        private static void Release(Cart cart)
        {
            cart.Parked = false;
            ParkedCarts.Remove(cart);
            foreach (var wheel in cart.Wheels)
            {
                if (!wheel.Body) continue;
                Place(wheel);
            }
            Physics.SyncTransforms();
            foreach (var wheel in cart.Wheels)
            {
                if (!wheel.Body) continue;
                wheel.Body.isKinematic = false;
                wheel.Body.linearVelocity = cart.Chassis.GetPointVelocity(wheel.Body.worldCenterOfMass);
                wheel.Body.angularVelocity = cart.Chassis.angularVelocity + wheel.Body.transform.TransformDirection(wheel.Axle) * wheel.Spin;
                wheel.Body.WakeUp();
            }
        }
    }
}
