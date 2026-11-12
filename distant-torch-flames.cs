using System.Collections.Generic;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal sealed class DistantTorchFlames : MonoBehaviour
    {
        private static readonly HashSet<DistantTorchFlames> Active = new HashSet<DistantTorchFlames>();
        private readonly List<Flame> flames = new List<Flame>();
        private Transform location;
        private float lastBlend, lastScreenMinimum;
        private bool prepared;
        // A torch at rest keeps where it stood, read again every 32nd frame; one on a ship or cart is read every frame.
        private Vector3 place;
        private bool moves;
        private int turn;

        private void Awake() => location = transform;

        // A flame's own minimum size and the one it was last given; writing an unchanged size still costs the engine a call.
        private sealed class Flame
        {
            internal ParticleSystemRenderer Renderer;
            internal float Original, Written;
        }

        internal static void Apply(Fireplace fire)
        {
            string prefab = Utils.GetPrefabName(fire.gameObject);
            if (prefab != "piece_groundtorch" && prefab != "piece_groundtorch_blue" && prefab != "piece_groundtorch_green" &&
                prefab != "piece_groundtorch_wood" && prefab != "piece_walltorch") return;
            DistantTorchFlames controller = null;
            foreach (var renderer in fire.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (!renderer.name.StartsWith("fx_Torch")) continue;
                if (!controller) controller = fire.GetComponent<DistantTorchFlames>() ?? fire.gameObject.AddComponent<DistantTorchFlames>();
                if (!controller.flames.Exists(flame => flame.Renderer == renderer))
                {
                    controller.flames.Add(new Flame { Renderer = renderer, Original = renderer.minParticleSize, Written = renderer.minParticleSize });
                    controller.prepared = false;
                }
            }
        }

        private void OnEnable()
        {
            prepared = false;
            place = location.position;
            moves = GetComponentInParent<Rigidbody>();
            turn = GetInstanceID() & 31;
            if (Active.Count == 0) Camera.onPreCull += PrepareFlames;
            Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
            if (Active.Count == 0) Camera.onPreCull -= PrepareFlames;
            foreach (var flame in flames)
                if (flame.Renderer) { flame.Renderer.minParticleSize = flame.Original; flame.Written = flame.Original; }
        }

        private static void PrepareFlames(Camera camera)
        {
            if (camera != Utils.GetMainCamera()) return;
            // A subpixel billboard can vanish between raster samples even while its particles and light stay active.
            // Use the rendering camera's actual viewport, including offscreen captures and resolution changes.
            float screenMinimum = 3f / Mathf.Max(1, camera.pixelHeight);
            var position = camera.transform.position;
            int frame = Time.frameCount & 31;
            foreach (var controller in Active)
            {
                if (controller.moves || controller.turn == frame) controller.place = controller.location.position;
                float distance = Vector3.Distance(position, controller.place);
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(80f, 160f, distance));
                if (controller.prepared && controller.lastBlend == blend && controller.lastScreenMinimum == screenMinimum) continue;
                controller.prepared = true;
                controller.lastBlend = blend;
                controller.lastScreenMinimum = screenMinimum;
                // Flames whose objects are inactive get their size too, so a size that stays the same costs nothing.
                foreach (var flame in controller.flames)
                {
                    float minimum = Mathf.Max(flame.Original, screenMinimum * blend);
                    if (minimum == flame.Written || !flame.Renderer) continue;
                    flame.Renderer.minParticleSize = minimum;
                    flame.Written = minimum;
                }
            }
        }
    }
}
