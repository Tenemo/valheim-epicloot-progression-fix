using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Star and size modifiers enlarge a creature's body, but its melee attacks keep their reach, height and thickness from
    // the attack origin and its AI keeps its attack distance from the creature's root, so a giant moose, wolf, asksvin or
    // seeker can stand pressed against a target its swing never reaches, and an enlarged Unbjorn never gets close enough to
    // bite. Each melee swing of an enlarged creature reaches as far beyond its own collider as it would at the prefab's size
    // and as high and as wide as its body, using the game's character-only casts, which pass through terrain and buildings.
    // The swing's own cast no longer stops at terrain and still stops at walls, and characters behind a wall stay safe. The
    // AI's attack distance grows by what that attack's swing grew, so each attack keeps its native lead over its swing;
    // attacks from a bone, projectiles and area attacks grow by how far the body now reaches in front of its root.
    // The game tilts a swing towards the creature's eye line only for the few attacks given a vertical limit, and an
    // enlarged creature's eye sits so high above its swing that the eye line would point a swing at a level enemy into the
    // ground. A swing without such a limit is aimed instead from its own start at the middle half of the height of a target
    // creature in front of it and within its reach, by up to the 25 degrees the game gives the Dragon's frost breath.
    // Normal-sized creatures, players, damage and creature size are unchanged.
    internal static class CreatureMeleeReach
    {
        private const float CastLength = 60f;
        private const float MaxAimAngle = 25f;

        private sealed class Body
        {
            internal float Scale = -1f, Growth;
            internal Collider[] Colliders;
            internal readonly Dictionary<ItemDrop.ItemData.SharedData, float> Swings = new Dictionary<ItemDrop.ItemData.SharedData, float>();
        }

        private struct NativeSwing
        {
            internal bool Changed, HitTerrain;
            internal float Range, Width, WidthCharExtra, HeightChar1, HeightChar2;
        }

        private static readonly List<RaycastHit> SwingHits = (List<RaycastHit>)AccessTools.Field(typeof(Attack), "s_hitList").GetValue(null);
        private static readonly int CharacterLayers = LayerMask.GetMask("character", "character_net", "character_ghost", "hitbox", "character_noenv", "vehicle");
        private static readonly int SolidLayers = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        private static bool stretchedSwing;
        private static Vector3 swingStart;
        private static readonly ConditionalWeakTable<Character, Body> Bodies = new ConditionalWeakTable<Character, Body>();
        private static readonly Dictionary<string, float> PrefabScales = new Dictionary<string, float>();
        private static readonly AccessTools.FieldRef<Attack, Humanoid> Attacker = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
        private static readonly Func<Attack, Transform> AttackOrigin = AccessTools.MethodDelegate<Func<Attack, Transform>>(AccessTools.Method(typeof(Attack), "GetAttackOrigin"));
        private static readonly AccessTools.FieldRef<BaseAI, Character> AiCharacter = AccessTools.FieldRefAccess<BaseAI, Character>("m_character");
        private static readonly AccessTools.FieldRef<MonsterAI, Character> AiTarget = AccessTools.FieldRefAccess<MonsterAI, Character>("m_targetCreature");
        private static readonly AccessTools.FieldRef<Character, CapsuleCollider> Capsule = AccessTools.FieldRefAccess<Character, CapsuleCollider>("m_collider");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(CreatureMeleeReach);
            harmony.Patch(AccessTools.Method(typeof(Attack), "DoMeleeAttack"), prefix: new HarmonyMethod(self, nameof(ExtendReach)), finalizer: new HarmonyMethod(self, nameof(RestoreReach)), transpiler: new HarmonyMethod(self, nameof(DropHitsBehindWalls)));
            harmony.Patch(AccessTools.Method(typeof(MonsterAI), "UpdateAI"), transpiler: new HarmonyMethod(self, nameof(GrowAttackRange)));
            // DoMeleeAttack asks for its direction after ExtendReach, so the reach test below sees the grown reach; the other
            // caller, OnTrailStart, only turns the swing's trail effect.
            harmony.Patch(AccessTools.Method(typeof(Attack), "GetMeleeAttackDir"), postfix: new HarmonyMethod(self, nameof(AimSwing)));
        }

        // How much larger than its prefab the creature is drawn and collides.
        private static float Enlargement(Character character)
        {
            string name = Utils.GetPrefabName(character.gameObject);
            if (!PrefabScales.TryGetValue(name, out float prefab))
            {
                var original = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
                prefab = original ? original.transform.localScale.x : 0f;
                if (original) PrefabScales[name] = prefab;
            }
            return prefab > 0f ? character.transform.lossyScale.x / prefab : 1f;
        }

        private static Body BodyOf(Character character)
        {
            var body = Bodies.GetOrCreateValue(character);
            if (body.Colliders == null || body.Colliders.Any(collider => !collider))
            {
                var rigidbody = character.GetComponent<Rigidbody>();
                body.Colliders = character.GetComponentsInChildren<Collider>(true).Where(collider => !collider.isTrigger && collider.attachedRigidbody == rigidbody).ToArray();
                body.Scale = -1f;
            }
            return body;
        }

        // How far the creature's own colliders reach from origin along direction.
        private static float Extent(Body body, Vector3 origin, Vector3 direction)
        {
            var ray = new Ray(origin + direction * CastLength, -direction);
            float extent = 0f;
            foreach (var collider in body.Colliders)
                if (collider.enabled && collider.Raycast(ray, out var hit, CastLength)) extent = Mathf.Max(extent, CastLength - hit.distance);
            return extent;
        }

        // At s times its prefab's size, the body reaches s times as far from a point that scaled with it; the part beyond
        // what the prefab's body reached is what an attack measured from that point must also cover.
        private static float Growth(float extent, float enlargement) => extent * (1f - 1f / enlargement);

        // The game runs character casts only for a swing with a width.
        private static float RayWidth(Attack attack) => attack.m_attackRayWidth > 0f ? attack.m_attackRayWidth : 0.01f;

        // The heights above the attack origin that the enlarged swing's character casts cover, and their radius. Native
        // character casts count, so every hit that lands today still lands, and min and max keep attacks with negative
        // heights working. Two spheres leave no gap at a quarter of the span.
        private static (float Bottom, float Top, float Radius) SwingBand(Attack attack, float enlargement)
        {
            float height = attack.m_attackHeight, width = RayWidth(attack) + attack.m_attackRayWidthCharExtra;
            float low = height + Mathf.Min(0f, Mathf.Min(attack.m_attackHeightChar1, attack.m_attackHeightChar2)) - width;
            float high = height + Mathf.Max(0f, Mathf.Max(attack.m_attackHeightChar1, attack.m_attackHeightChar2)) + width;
            float bottom = Mathf.Min(low, enlargement * low), top = Mathf.Max(high, enlargement * high);
            return (bottom, top, Mathf.Max(enlargement * width, (top - bottom) / 4f));
        }

        // How much further an attack must reach from origin for its swing to keep clearing the enlarged body. The body is
        // measured at every height the swing covers, so a head or neck the animation lowers or raises still counts.
        private static float SwingGrowth(Character character, Body body, Attack attack, Vector3 origin, float enlargement)
        {
            var (bottom, top, _) = SwingBand(attack, enlargement);
            var start = origin + character.transform.right * attack.m_attackOffset;
            int steps = Mathf.Max(1, Mathf.CeilToInt((top - bottom) / 0.25f));
            float extent = 0f;
            for (int step = 0; step <= steps; step++)
                extent = Mathf.Max(extent, Extent(body, start + Vector3.up * Mathf.Lerp(bottom, top, (float)step / steps), character.transform.forward));
            return Growth(extent, enlargement);
        }

        private static void ExtendReach(Attack __instance, out NativeSwing __state)
        {
            __state = default;
            var character = Attacker(__instance);
            if (!character || character.IsPlayer()) return;
            float enlargement = Enlargement(character);
            if (enlargement <= 1.01f) return;
            __state = new NativeSwing
            {
                Changed = true, HitTerrain = __instance.m_hitTerrain, Range = __instance.m_attackRange, Width = __instance.m_attackRayWidth,
                WidthCharExtra = __instance.m_attackRayWidthCharExtra, HeightChar1 = __instance.m_attackHeightChar1, HeightChar2 = __instance.m_attackHeightChar2,
            };
            var origin = AttackOrigin(__instance).position;
            __instance.m_attackRange += SwingGrowth(character, BodyOf(character), __instance, origin, enlargement);
            // Stretch what the swing's casts cover.
            var (bottom, top, radius) = SwingBand(__instance, enlargement);
            float height = __instance.m_attackHeight;
            __instance.m_attackRayWidth = RayWidth(__instance);
            __instance.m_attackHeightChar1 = bottom + radius - height;
            __instance.m_attackHeightChar2 = top - radius - height;
            __instance.m_attackRayWidthCharExtra = radius - __instance.m_attackRayWidth;
            __instance.m_hitTerrain = false;
            stretchedSwing = true;
            swingStart = origin + Vector3.up * height + character.transform.right * __instance.m_attackOffset;
        }

        private static void RestoreReach(Attack __instance, NativeSwing __state)
        {
            if (!__state.Changed) return;
            stretchedSwing = false;
            __instance.m_attackRange = __state.Range;
            __instance.m_attackRayWidth = __state.Width;
            __instance.m_attackRayWidthCharExtra = __state.WidthCharExtra;
            __instance.m_attackHeightChar1 = __state.HeightChar1;
            __instance.m_attackHeightChar2 = __state.HeightChar2;
            __instance.m_hitTerrain = __state.HitTerrain;
        }

        // Aiming at the middle half of the target's height leaves a level swing that already crosses its body nearly level.
        // Only the pitch changes, so horizontal and vertical swings keep the shape of their fan.
        private static void AimSwing(Attack __instance, Transform originJoint, ref Vector3 attackDir)
        {
            var character = Attacker(__instance);
            if (!character || character.IsPlayer() || __instance.m_maxYAngle != 0f || !originJoint || Enlargement(character) <= 1.01f) return;
            var target = character.GetBaseAI()?.GetTargetCreature();
            var collider = target ? target.GetCollider() : null;
            if (!collider) return;
            var forward = character.transform.forward; forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) return;
            forward.Normalize();
            var start = originJoint.position + Vector3.up * __instance.m_attackHeight + character.transform.right * __instance.m_attackOffset;
            var bounds = collider.bounds;
            var flat = bounds.center - start; flat.y = 0f;
            if (Vector3.Dot(flat, forward) <= 0f || flat.magnitude > __instance.m_attackRange + bounds.extents.x) return;
            float wanted = Mathf.Clamp(start.y, bounds.min.y + bounds.size.y / 4f, bounds.max.y - bounds.size.y / 4f);
            float pitch = Mathf.Clamp(Mathf.Atan2(wanted - start.y, Mathf.Max(flat.magnitude, 0.1f)) * Mathf.Rad2Deg, -MaxAimAngle, MaxAimAngle) * Mathf.Deg2Rad;
            attackDir = forward * Mathf.Cos(pitch) + Vector3.up * Mathf.Sin(pitch);
        }

        // A swing stops at the first thing its casts touched, and a wide character cast touches a target behind a wall before
        // the thin cast touches the wall, so hits on characters also need a clear line from the swing's start.
        private static IEnumerable<CodeInstruction> DropHitsBehindWalls(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var sort = AccessTools.Method(typeof(List<RaycastHit>), nameof(List<RaycastHit>.Sort), new[] { typeof(Comparison<RaycastHit>) });
            int index = code.FindIndex(instruction => instruction.Calls(sort));
            if (index < 0) throw new InvalidOperationException("The melee attack's hit list changed.");
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureMeleeReach), nameof(RemoveWalledHits))));
            return code;
        }

        private static void RemoveWalledHits()
        {
            if (stretchedSwing) SwingHits.RemoveAll(WalledOff);
        }

        private static bool WalledOff(RaycastHit hit)
        {
            if ((CharacterLayers & 1 << hit.collider.gameObject.layer) == 0) return false;
            // A cast that starts inside a collider reports no point.
            var point = hit.point == Vector3.zero ? hit.collider.bounds.center : hit.point;
            return Physics.Linecast(swingStart, point, SolidLayers, QueryTriggerInteraction.Ignore);
        }

        // UpdateAI compares the root-to-target distance with each weapon's attack range: grow each range read, after any
        // late-biome range in either patch order.
        private static IEnumerable<CodeInstruction> GrowAttackRange(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var range = AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_aiAttackRange));
            var lateBiome = AccessTools.Method(typeof(LateBiomePressure), "AttackRange");
            var reads = code.Where(instruction => instruction.LoadsField(range) || instruction.Calls(lateBiome)).ToList();
            if (reads.Count != 3) throw new InvalidOperationException("MonsterAI's attack range checks changed.");
            foreach (var read in reads)
                code.InsertRange(code.IndexOf(read) + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CreatureMeleeReach), nameof(AttackRange), new[] { typeof(float), typeof(MonsterAI) })) });
            return code;
        }

        // UpdateAI reads the range of the weapon it just selected.
        internal static float AttackRange(float range, MonsterAI ai) => AttackRange(range, ai, (AiCharacter(ai) as Humanoid)?.GetCurrentWeapon());

        internal static float AttackRange(float range, MonsterAI ai, ItemDrop.ItemData item)
        {
            var character = AiCharacter(ai);
            if (!character) return range;
            float enlargement = Enlargement(character);
            if (enlargement <= 1.01f) return range;
            var body = BodyOf(character);
            if (!Mathf.Approximately(body.Scale, enlargement))
            {
                body.Scale = enlargement;
                body.Swings.Clear();
                var bounds = body.Colliders.Where(collider => collider.enabled).Select(collider => collider.bounds).DefaultIfEmpty().Aggregate((all, next) => { all.Encapsulate(next); return all; });
                var root = character.transform.position;
                body.Growth = Growth(Extent(body, new Vector3(root.x, bounds.center.y, root.z), character.transform.forward), enlargement);
            }
            var attack = item?.m_shared.m_attack;
            bool swing = attack != null && (attack.m_attackType == Attack.AttackType.Horizontal || attack.m_attackType == Attack.AttackType.Vertical) && string.IsNullOrEmpty(attack.m_attackOriginJoint);
            if (!swing) return range + body.Growth;
            if (!body.Swings.TryGetValue(item.m_shared, out float growth))
                body.Swings[item.m_shared] = growth = SwingGrowth(character, body, attack, character.transform.position, enlargement);
            // The distance UpdateAI compares subtracts a radius that overstates non-player targets, which would stop the
            // creature short of them. Minimum ranges and gap closers rely on the native range, so it never shrinks.
            var target = AiTarget(ai);
            float excess = target ? Mathf.Max(0f, target.GetRadius() - Capsule(target).radius * target.transform.lossyScale.x) : 0f;
            return Mathf.Max(range, range + growth - excess);
        }
    }
}
