using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Native hit collection, filtering and damage stay in place. Replace only the bundled skill awards.
    internal static class PerTargetSkillXp
    {
        private enum Source { Melee, Slam, Projectile, AreaEffect }
        private sealed class Award
        {
            internal Character Owner;
            internal ItemDrop.ItemData Weapon;
            internal Skills.SkillType Skill;
            internal float Amount;
            internal object Confirmation;
        }
        private sealed class Scope
        {
            internal Source Source;
            internal Character Owner;
            internal ItemDrop.ItemData Weapon;
            internal Attack Attack;
            internal Aoe Area;
            internal Skills.SkillType MeleeSkill;
            internal bool MeleeEligible, LastEligible, FrameQueued;
            internal (UnityEngine.Object Target, int Section) LastTarget;
            internal object LastConfirmation;
            internal readonly HashSet<(UnityEngine.Object Target, int Section)> Targets = new HashSet<(UnityEngine.Object, int)>();
            internal readonly List<Award> Awards = new List<Award>();
        }
        private static Scope current;
        private static readonly ConditionalWeakTable<Aoe, Scope> TriggerPulses = new ConditionalWeakTable<Aoe, Scope>();
        private static readonly List<Scope> FramePulses = new List<Scope>();
        private static readonly AccessTools.FieldRef<Aoe, ItemDrop.ItemData> AreaWeapon = AccessTools.FieldRefAccess<Aoe, ItemDrop.ItemData>("m_itemData");
        private static readonly AccessTools.FieldRef<Aoe, List<GameObject>> AreaHits = AccessTools.FieldRefAccess<Aoe, List<GameObject>>("m_hitList");
        private static readonly Func<MineRock, Collider, int> RockSection = AccessTools.MethodDelegate<Func<MineRock, Collider, int>>(AccessTools.Method(typeof(MineRock), "GetAreaIndex"));
        private static readonly Func<MineRock5, Collider, int> VeinSection = AccessTools.MethodDelegate<Func<MineRock5, Collider, int>>(AccessTools.Method(typeof(MineRock5), "GetAreaIndex"));
        private static readonly FieldInfo VeinAreas = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
        private static readonly AccessTools.FieldRef<MineRock, Collider[]> RockAreas = AccessTools.FieldRefAccess<MineRock, Collider[]>("m_hitAreas");
        private static readonly FieldInfo VeinHealth = AccessTools.Field(AccessTools.Inner(typeof(MineRock5), "HitArea"), "m_health");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(PerTargetSkillXp);
            void Patch(MethodBase method, string prefix = null, string finalizer = null, string transpiler = null, string postfix = null)
            {
                harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(self, prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(self, postfix),
                    finalizer: finalizer == null ? null : new HarmonyMethod(self, finalizer),
                    transpiler: transpiler == null ? null : new HarmonyMethod(self, transpiler) { priority = Priority.Last }, ilmanipulator: null);
            }
            Patch(AccessTools.Method(typeof(Attack), "DoMeleeAttack"), nameof(BeginMelee), nameof(EndScope), nameof(MeleeCalls));
            Patch(AccessTools.Method(typeof(Attack), "DoAreaAttack"), nameof(BeginSlam), nameof(EndScope), nameof(AggregatedCalls));
            var areaHits = AccessTools.GetDeclaredMethods(typeof(Attack)).Concat(typeof(Attack).GetNestedTypes(BindingFlags.NonPublic)
                .SelectMany(AccessTools.GetDeclaredMethods)).Single(method => method.Name.Contains("<DoAreaAttack>g__checkHits"));
            Patch(areaHits, transpiler: nameof(DamageCalls));
            Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.OnHit)), nameof(BeginProjectile), nameof(EndScope), nameof(ProjectileCalls));
            Patch(AccessTools.Method(typeof(Projectile), "DoAOE"), transpiler: nameof(DamageCalls));
            Patch(AccessTools.Method(typeof(Aoe), "Initiate"), nameof(BeginArea), nameof(EndScope));
            Patch(AccessTools.Method(typeof(Aoe), "OnHit"), nameof(BeginAreaHit), nameof(EndAreaHit), nameof(AreaCalls));
            Patch(AccessTools.Method(typeof(Aoe), "Update"), postfix: nameof(EndTriggerPulse));
            Patch(AccessTools.Method(typeof(Aoe), "OnDisable"), postfix: nameof(EndTriggerPulseOnDisable));
            Patch(AccessTools.Method(typeof(InventoryGui), "DoCrafting"), transpiler: nameof(CraftingCalls));
        }

        private static void BeginMelee(Attack __instance, out Scope __state) => BeginAttack(__instance, Source.Melee, out __state);
        private static void BeginSlam(Attack __instance, out Scope __state) => BeginAttack(__instance, Source.Slam, out __state);
        private static void BeginAttack(Attack attack, Source source, out Scope previous)
        {
            previous = current;
            current = new Scope { Source = source, Owner = GameFields.AttackCharacter(attack), Weapon = GameFields.AttackWeapon(attack), Attack = attack };
        }
        private static void BeginProjectile(Projectile __instance, out Scope __state)
        {
            __state = current;
            current = new Scope { Source = Source.Projectile, Owner = GameFields.ProjectileOwner(__instance), Weapon = GameFields.ProjectileWeapon(__instance) };
        }
        private static void BeginArea(Aoe __instance, out Scope __state)
        {
            __state = current;
            current = AreaScope(__instance);
        }
        private static Scope AreaScope(Aoe area) => new Scope { Source = Source.AreaEffect, Owner = GameFields.AoeOwner(area), Weapon = AreaWeapon(area), Area = area };
        private static void BeginAreaHit(Aoe __instance, out Scope __state)
        {
            __state = current;
            if (current?.Area == __instance) return;
            current = __instance.m_useTriggers ? TriggerPulses.GetValue(__instance, AreaScope) : AreaScope(__instance);
        }
        private static Exception EndAreaHit(Aoe __instance, Scope __state, Exception __exception)
        {
            var completed = current;
            current = __state;
            if (__exception == null && completed != __state && !__instance.m_useTriggers) Complete(completed, __state);
            return __exception;
        }
        private static Exception EndScope(Scope __state, Exception __exception)
        {
            var completed = current;
            current = __state;
            if (__exception == null) Complete(completed, __state);
            return __exception;
        }
        private static void Complete(Scope scope, Scope parent)
        {
            if (parent == null) { Flush(scope); return; }
            parent.Awards.AddRange(scope.Awards);
            scope.Awards.Clear();
        }
        private static void EndTriggerPulse(Aoe __instance)
        {
            if (AreaHits(__instance).Count != 0 || !TriggerPulses.TryGetValue(__instance, out var pulse)) return;
            TriggerPulses.Remove(__instance);
            Flush(pulse);
        }
        private static void EndTriggerPulseOnDisable(Aoe __instance)
        {
            if (!TriggerPulses.TryGetValue(__instance, out var pulse)) return;
            TriggerPulses.Remove(__instance);
            Flush(pulse);
        }
        internal static void FlushFrameAwards()
        {
            foreach (var pulse in FramePulses) { pulse.FrameQueued = false; Flush(pulse); }
            FramePulses.Clear();
        }

        private static bool MeleeSkill(HashSet<Skills.SkillType> skills, Skills.SkillType skill)
        {
            current.MeleeEligible = true;
            current.MeleeSkill = skill;
            return skills.Add(skill);
        }
        private static void IgnoreAggregated(Character owner, Skills.SkillType skill, float amount) { }

        private static void Damage(IDestructible target, HitData hit)
        {
            var scope = current;
            if (scope == null) { target.Damage(hit); return; }
            var component = target as Component;
            int section = target is MineRock rock && RockAreas(rock) != null ? RockSection(rock, hit.m_hitCollider) :
                target is MineRock5 vein && VeinAreas.GetValue(vein) != null ? VeinSection(vein, hit.m_hitCollider) : -1;
            bool valid = component && (!(target is Character creature) || (!creature.IsDead() && creature.GetHealth() > 0f));
            var resource = component && (target is TreeBase || target is TreeLog || target is MineRock || target is MineRock5)
                ? component.GetComponent<ZNetView>()?.GetZDO() : null;
            if (target is TreeBase standingTree)
                valid &= resource != null && hit.CheckToolTier(standingTree.m_minToolTier, true) && resource.GetFloat(ZDOVars.s_health, standingTree.m_health) > 0f;
            if (target is TreeLog fallenTree)
                valid &= resource != null && hit.CheckToolTier(fallenTree.m_minToolTier, true) && resource.GetFloat(ZDOVars.s_health, fallenTree.m_health) > 0f;
            if (target is MineRock originalRock)
                valid &= resource != null && section >= 0 && hit.CheckToolTier(originalRock.m_minToolTier) && resource.GetFloat("Health" + section, originalRock.m_health) > 0f;
            if (target is MineRock5 fracturedRock)
            {
                var areas = (IList)VeinAreas.GetValue(fracturedRock);
                valid &= resource != null && section >= 0 && areas != null && section < areas.Count && (float)VeinHealth.GetValue(areas[section]) > 0f && hit.CheckToolTier(fracturedRock.m_minToolTier);
            }
            bool eligible = scope.Source == Source.Melee ? scope.MeleeEligible : scope.Source == Source.Slam ?
                (target.GetDestructibleType() & scope.Attack.m_skillHitType) != 0 : target is Character &&
                (scope.Source != Source.AreaEffect || scope.Area.m_canRaiseSkill && scope.Area.m_skill != Skills.SkillType.None && scope.Area.m_skill != Skills.SkillType.All);
            var key = ((UnityEngine.Object)component, section);
            var skill = scope.MeleeSkill;
            scope.MeleeEligible = false;
            ProtectionBubble.MarkWeaponSource(hit, scope.Weapon);
            var confirmation = RangedHitConfirmation.DamageForSkill(target, hit, scope.Owner, scope.Weapon, valid && eligible);
            scope.LastTarget = key;
            scope.LastConfirmation = confirmation;
            scope.LastEligible = valid && eligible;
            if (!scope.LastEligible || scope.Source == Source.AreaEffect) return;
            if (scope.Source != Source.Melee) skill = hit.m_skill;
            // Elemental Archery trains Elemental Magic; native shots still name the bow's skill.
            if (skill == Skills.SkillType.Bows && scope.Owner == Player.m_localPlayer && scope.Weapon != null)
                skill = WeaponDamagePreview.Skill(Player.m_localPlayer, scope.Weapon);
            float amount = hit.m_skillRaiseAmount * (scope.Source == Source.Melee && target is Character ? 1.5f : 1f);
            Queue(scope, skill, amount);
        }
        private static void AreaSkill(Character owner, Skills.SkillType skill, float amount)
        {
            if (current?.LastEligible == true) Queue(current, skill, amount);
        }
        private static void Queue(Scope scope, Skills.SkillType skill, float amount)
        {
            if (!scope.Owner || amount <= 0f || skill == Skills.SkillType.None || skill == Skills.SkillType.All || !scope.Targets.Add(scope.LastTarget)) return;
            scope.Awards.Add(new Award { Owner = scope.Owner, Weapon = scope.Weapon, Skill = skill, Amount = amount, Confirmation = scope.LastConfirmation });
            if (scope.Area && scope.Area.m_useTriggers && scope.Area.m_hitInterval <= 0f && !scope.FrameQueued)
            { scope.FrameQueued = true; FramePulses.Add(scope); }
        }
        private static void Flush(Scope scope)
        {
            if (scope == null) return;
            foreach (var award in scope.Awards) RangedHitConfirmation.AwardSkill(award.Owner, award.Skill, award.Amount, award.Weapon, award.Confirmation);
            scope.Awards.Clear();
        }

        private static IEnumerable<CodeInstruction> DamageCalls(IEnumerable<CodeInstruction> instructions) => Transpilers.CallInstead(instructions,
            instruction => instruction.Calls(AccessTools.Method(typeof(IDestructible), nameof(IDestructible.Damage))),
            AccessTools.Method(typeof(PerTargetSkillXp), nameof(Damage)), 1, "Native per-target damage call moved.");
        private static IEnumerable<CodeInstruction> AggregatedCalls(IEnumerable<CodeInstruction> instructions) => Transpilers.CallInstead(instructions,
            instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.RaiseSkill))) ||
                instruction.Calls(AccessTools.Method(typeof(RangedHitConfirmation), "RaiseSkill")),
            AccessTools.Method(typeof(PerTargetSkillXp), nameof(IgnoreAggregated)), 1, "Native bundled skill award moved.");
        private static IEnumerable<CodeInstruction> MeleeCalls(IEnumerable<CodeInstruction> instructions)
        {
            var code = DamageCalls(AggregatedCalls(instructions));
            return Transpilers.CallInstead(code, instruction => instruction.Calls(AccessTools.Method(typeof(HashSet<Skills.SkillType>), nameof(HashSet<Skills.SkillType>.Add))),
                AccessTools.Method(typeof(PerTargetSkillXp), nameof(MeleeSkill)), 2, "Native melee skill eligibility moved.");
        }
        private static IEnumerable<CodeInstruction> ProjectileCalls(IEnumerable<CodeInstruction> instructions) => DamageCalls(AggregatedCalls(instructions));
        private static IEnumerable<CodeInstruction> AreaCalls(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int damage = 0, lifetime = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(AccessTools.Method(typeof(IDestructible), nameof(IDestructible.Damage))) && damage++ == 0)
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(PerTargetSkillXp), nameof(Damage)); }
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, AccessTools.Field(typeof(Aoe), "m_gaveSkill")))
                { instruction.opcode = OpCodes.Pop; instruction.operand = null; lifetime++; yield return instruction; yield return new CodeInstruction(OpCodes.Ldc_I4_0); continue; }
                if (instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.RaiseSkill))))
                { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(PerTargetSkillXp), nameof(AreaSkill)); }
                yield return instruction;
            }
            if (damage != 2 || lifetime != 1) throw new InvalidOperationException("Native area skill gates moved.");
        }
        private static void CraftingSkill(Character owner, Skills.SkillType skill, float completed)
        {
            for (int index = 0; index < completed; index++) owner.RaiseSkill(skill, 1f);
        }
        private static IEnumerable<CodeInstruction> CraftingCalls(IEnumerable<CodeInstruction> instructions) => Transpilers.CallInstead(instructions,
            instruction => instruction.Calls(AccessTools.Method(typeof(Character), nameof(Character.RaiseSkill))),
            AccessTools.Method(typeof(PerTargetSkillXp), nameof(CraftingSkill)), 1, "Native completed-craft skill award moved.");
    }
}
