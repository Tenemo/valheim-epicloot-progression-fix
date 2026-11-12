using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Harvestables have their own illumination allowance. Placed fires keep their physical light volume and use only a
    // distant intensity fade; the native LightLod still owns their shadow distance, strength and priority.
    internal sealed class SurfaceLighting : MonoBehaviour
    {
        internal const int HarvestableLimit = 40;
        internal const float HarvestableDistance = 40f;
        internal const float FullFireDistance = 140f, FarFireDistance = 260f;
        private static readonly Dictionary<Light, SurfaceLighting> Lights = new Dictionary<Light, SurfaceLighting>();
        private static readonly Dictionary<LightLod, SurfaceLighting> LodOwners = new Dictionary<LightLod, SurfaceLighting>();
        private static readonly Dictionary<LightLod, int> IlluminationPriorities = new Dictionary<LightLod, int>();
        private static readonly List<SurfaceLighting> Harvestables = new List<SurfaceLighting>();
        // The lights each frame walks: every other light would return at once from Apply. A dark, unselected glowing plant
        // joins again when a selection picks it, a resting fire when the camera has travelled as far as its settledUntil,
        // and any light when it is enabled again.
        private static readonly List<SurfaceLighting> Changing = new List<SurfaceLighting>();
        private static readonly HashSet<SurfaceLighting> Settled = new HashSet<SurfaceLighting>();
        private static readonly List<SurfaceLighting> Waking = new List<SurfaceLighting>();
        // No settled fire's settledUntil is below this, so the set is looked at only once the camera has travelled this far.
        private static double settledSoonest = double.PositiveInfinity;
        private static readonly AccessTools.FieldRef<LightLod, float> BaseRange = AccessTools.FieldRefAccess<LightLod, float>("m_baseRange");
        private static readonly AccessTools.FieldRef<LightLod, int> NativePriority = AccessTools.FieldRefAccess<LightLod, int>("m_lightPrio");
        private static readonly AccessTools.FieldRef<List<LightLod>> SortedLights =
            AccessTools.StaticFieldRefAccess<List<LightLod>>(AccessTools.Field(typeof(LightLod), "m_sortedLights"));
        private static readonly AccessTools.FieldRef<float> PriorityTimer =
            AccessTools.StaticFieldRefAccess<float>(AccessTools.Field(typeof(LightLod), "m_updateTimer"));
        private static float nextSelection;
        private static Vector3 reference;
        // How far the camera has moved in all, which bounds how much nearer or farther any fire can have come.
        private static double travelled;
        private static bool Client => SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null;
        private Light source;
        private LightLod lod;
        private LightFlicker flicker;
        private Pickable pickable;
        private ZNetView view;
        private bool placedFire, originalEnabled, originalLightLod, selected, bound;
        private float range, rawIntensity, writtenIntensity, blend, distance;
        // Between OnEnable and OnDisable, and the light's state as last written, which is written again only when it changes
        // or after the light was enabled. A placed fire on nothing that moves keeps where it was bound.
        private bool active, stale = true, writtenEnabled, moves, changing;
        private Vector3 position;
        // A resting fire fully lit or dark cannot reach the fade before the camera has moved as far as the fire is from the
        // fade's edge, so it is looked at again only once the camera has travelled this far in all.
        private double settledUntil;

        internal static void Attach(Light light)
        {
            if (!Client) return;
            if (!light.GetComponent<SurfaceLighting>()) light.gameObject.AddComponent<SurfaceLighting>();
        }

        private void Awake() => Bind();
        private void Start() => Bind();
        private void OnEnable()
        {
            active = true;
            stale = true;
            Bind();
            Wake();
            if (bound && lod) lod.m_lightLod = false;
            if (bound && source) source.range = range;
            nextSelection = 0f;
        }

        private void Wake()
        {
            if (!bound || changing) return;
            Settled.Remove(this);
            changing = true;
            Changing.Add(this);
        }

        // Apply would return at once on every frame until a selection, an enable or, for a fire, the camera's travel changes that.
        private bool Resting => !active || !source || !stale && (travelled < settledUntil || !placedFire && !selected && blend == 0f);

        private void Bind()
        {
            if (bound || !gameObject.scene.IsValid()) return;
            source = GetComponent<Light>();
            if (!source || source.type != LightType.Point) return;
            pickable = GetComponentInParent<Pickable>();
            var fire = GetComponentInParent<Fireplace>();
            placedFire = !pickable && fire && fire.GetComponent<Piece>();
            if (!pickable && !placedFire) return;
            view = (pickable ? pickable.gameObject : fire.gameObject).GetComponent<ZNetView>();
            // Mod-created prefab templates can belong to a scene, but they have no network record. Never change their
            // serialized light state; a clone binds after its own ZNetView has initialized.
            if (!view || !view.IsValid()) return;
            flicker = GetComponent<LightFlicker>();
            lod = GetComponent<LightLod>();
            originalEnabled = source.enabled || lod && lod.m_lightLod;
            originalLightLod = lod && lod.m_lightLod;
            range = lod && BaseRange(lod) > 0f ? BaseRange(lod) : source.range;
            rawIntensity = writtenIntensity = source.intensity;
            moves = placedFire && GetComponentInParent<Rigidbody>();
            position = transform.position;
            bound = true;
            Lights.Add(source, this);
            Wake();
            if (lod) { LodOwners[lod] = this; lod.m_lightLod = false; }
            source.range = range;
            nextSelection = 0f;
        }

        private bool Eligible => bound && active && originalEnabled && view && view.IsValid()
            && (!pickable || pickable.enabled && !pickable.GetPicked());

        private void Restore()
        {
            if (!bound || !source) return;
            source.intensity = rawIntensity;
            writtenIntensity = rawIntensity;
            source.range = range;
            source.enabled = originalEnabled;
            if (lod) lod.m_lightLod = originalLightLod;
        }

        private void OnDisable()
        {
            active = false;
            Restore();
            selected = false;
            blend = 0f;
            nextSelection = 0f;
        }

        private void OnDestroy()
        {
            Restore();
            if ((object)source != null) Lights.Remove(source);
            if (changing) { Changing.Remove(this); changing = false; }
            Settled.Remove(this);
            if ((object)lod != null) { LodOwners.Remove(lod); IlluminationPriorities.Remove(lod); }
        }

        private static bool Exempt(LightLod light) => LodOwners.TryGetValue(light, out var owner) && owner && owner.enabled;
        private static int IlluminationPriority(LightLod light) =>
            IlluminationPriorities.TryGetValue(light, out int priority) ? priority : NativePriority(light);

        // Every frame for each light: the engine calls that write it run only when what they write changes, since a few
        // hundred fires at a base otherwise spend most of a millisecond rewriting the same values.
        private void Apply(float deltaTime)
        {
            if (travelled < settledUntil && !stale) return;
            // An unselected harvestable that has faded out stays dark until a selection picks it again. Most harvestables at
            // a base are, so this comes before the engine's check that the light still exists.
            if (!placedFire && !selected && blend == 0f && !stale) return;
            if (!active || !source) return;
            // LightLod's loop can start before this component takes the light over. With the shared allowance spent, it
            // is then already fading the light out and would finish switching it off; restart it to manage only shadows.
            if (stale && (object)lod != null && lod.isActiveAndEnabled)
            {
                lod.m_lightLod = false; lod.StopCoroutine("UpdateLoop"); lod.StartCoroutine("UpdateLoop");
                source.range = range;
            }
            if ((object)lod != null) lod.m_lightLod = false;
            bool eligible = Eligible;
            float previous = blend;
            if (placedFire)
            {
                float away = Vector3.Distance(reference, moves ? transform.position : position);
                float progress = Mathf.Clamp01((away - FullFireDistance) / (FarFireDistance - FullFireDistance));
                blend = eligible ? 1f - progress * progress * (3f - 2f * progress) : 0f;
                float gap = away < FullFireDistance ? FullFireDistance - away : away > FarFireDistance ? away - FarFireDistance : 0f;
                settledUntil = moves ? 0d : travelled + gap;
            }
            else blend = eligible ? Mathf.MoveTowards(blend, selected ? 1f : 0f, deltaTime) : 0f;
            // Flicker supplies an unscaled value on every native write. Steady lights also accept other direct writes.
            if (!flicker && source.intensity != writtenIntensity) rawIntensity = source.intensity;
            float intensity = rawIntensity * blend;
            if (stale || intensity != writtenIntensity || blend != previous)
            {
                writtenIntensity = intensity;
                source.intensity = intensity;
            }
            bool lit = eligible && blend > 0f;
            if (stale || lit != writtenEnabled)
            {
                writtenEnabled = lit;
                source.enabled = lit;
            }
            stale = false;
        }

        [HarmonyPatch(typeof(LightLod), nameof(LightLod.UpdateLights))]
        private static class UpdateLighting
        {
            private static bool Prepare() => Client;
            private static void Postfix(float dt)
            {
                var camera = Utils.GetMainCamera();
                if (!camera) return;
                var eye = camera.transform.position;
                travelled += Vector3.Distance(reference, eye);
                reference = eye;
                if (Time.unscaledTime >= nextSelection)
                {
                    nextSelection = Time.unscaledTime + .2f;
                    Harvestables.Clear();
                    foreach (var light in Lights.Values)
                    {
                        if (light.placedFire) continue;
                        light.selected = false;
                        // Distance first: the engine calls in Eligible matter only for the few within reach.
                        if (!light.active || !light.originalEnabled) continue;
                        light.distance = (light.transform.position - reference).sqrMagnitude;
                        if (light.distance < HarvestableDistance * HarvestableDistance && light.Eligible) Harvestables.Add(light);
                    }
                    Harvestables.Sort((left, right) => {
                        int order = left.distance.CompareTo(right.distance);
                        return order != 0 ? order : left.GetInstanceID().CompareTo(right.GetInstanceID());
                    });
                    for (int index = 0; index < Math.Min(HarvestableLimit, Harvestables.Count); index++)
                    {
                        Harvestables[index].selected = true;
                        Harvestables[index].Wake();
                    }
                }
                // Keep native shadow priorities intact. Only the separate illumination counter excludes our lights.
                if (PriorityTimer() == 0f && LightLod.m_lightLimit >= 0)
                {
                    IlluminationPriorities.Clear();
                    int priority = 0;
                    foreach (var light in SortedLights()) if (!Exempt(light)) IlluminationPriorities[light] = priority++;
                }
                if (travelled >= settledSoonest)
                {
                    settledSoonest = double.PositiveInfinity;
                    foreach (var light in Settled)
                        if (travelled >= light.settledUntil) Waking.Add(light);
                        else settledSoonest = Math.Min(settledSoonest, light.settledUntil);
                    foreach (var light in Waking) light.Wake();
                    Waking.Clear();
                }
                for (int index = 0; index < Changing.Count;)
                {
                    var light = Changing[index];
                    light.Apply(dt);
                    if (!light.Resting) { index++; continue; }
                    Changing[index] = Changing[Changing.Count - 1];
                    Changing.RemoveAt(Changing.Count - 1);
                    light.changing = false;
                    if (light.active && light.source && travelled < light.settledUntil)
                    {
                        Settled.Add(light);
                        settledSoonest = Math.Min(settledSoonest, light.settledUntil);
                    }
                }
            }
        }

        [HarmonyPatch]
        private static class SeparateIlluminationPriority
        {
            private static bool Prepare() => Client;
            private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LightLod), "UpdateLoop"));
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = instructions.ToList();
                var reads = code.Where(instruction => instruction.LoadsField(AccessTools.Field(typeof(LightLod), "m_lightPrio"))).ToArray();
                if (reads.Length != 2) throw new InvalidOperationException("The game's illumination and shadow priority checks changed.");
                reads[0].opcode = OpCodes.Call;
                reads[0].operand = AccessTools.Method(typeof(SurfaceLighting), nameof(IlluminationPriority));
                return code;
            }
        }

        [HarmonyPatch(typeof(LightFlicker), nameof(LightFlicker.CustomUpdate))]
        private static class FlickerIntensity
        {
            private static bool Prepare() => Client;
            private static void Postfix(Light ___m_light)
            {
                if (___m_light && ___m_light.enabled && Lights.TryGetValue(___m_light, out var owner) && owner.active)
                {
                    owner.rawIntensity = ___m_light.intensity;
                    owner.writtenIntensity = owner.rawIntensity * owner.blend;
                    ___m_light.intensity = owner.writtenIntensity;
                }
            }
        }

        [HarmonyPatch(typeof(LightFlicker), "ApplySettings")]
        private static class FlickerSettings
        {
            private static bool Prepare() => Client;
            private static void Postfix(Light ___m_light)
            {
                if (___m_light && Lights.TryGetValue(___m_light, out var owner) && owner.isActiveAndEnabled)
                {
                    owner.rawIntensity = ___m_light.intensity;
                    owner.writtenIntensity = owner.rawIntensity * owner.blend;
                    ___m_light.intensity = owner.writtenIntensity;
                }
            }
        }
    }
}
