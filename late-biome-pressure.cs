using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Ashlands and Deep North combatants notice players from farther away, pursue them for longer and answer arrows:
    // Charred infantry and casters, northern skeletons, frozen greydwarves and shamans, Fryslings and Jotun warriors,
    // when they first spawned in either biome. Following a player across a border keeps them in it, and the same
    // creatures met elsewhere, such as a Nemesis party in the Mistlands, keep the game's behavior. Bosses, boss aspects,
    // Nemesis minibosses and tames are left out, and a creature that becomes one returns to its own values.
    //
    // Sight and alert ranges rise to the synced minimums and go through the game's own checks, so view angle, stealth,
    // factions and obstructions still apply. A target is kept for the synced memory while unseen and unheard and for the
    // synced timeout without an attack, instead of 30 and 60 seconds; death, invalid targets, chase distance and the other
    // disengagements are unchanged. Shared creature reachability handles hurt retreats separately. Fast pursuers
    // and heavy infantry move faster by the synced multipliers, applied to StarLevelSystem's freshly calculated speeds so
    // they never compound. Charred archers' ordinary bow is chosen and fired from the synced range, for these creatures
    // only; the weapon's shared definition and every other attack are unchanged.
    internal static class LateBiomePressure
    {
        private static readonly HashSet<string> Fast = new HashSet<string> { "Charred_Twitcher", "Greydwarf_Frozen", "Frysling" };
        private static readonly HashSet<string> Heavy = new HashSet<string> { "Charred_Melee", "Skeleton_DeepNorth", "JotunWarrior", "JotunWarriorDualWield" };
        private static readonly HashSet<string> Other = new HashSet<string> { "Charred_Archer", "Charred_Mage", "Greydwarf_Shaman_Frozen" };
        private const string CharredBow = "charred_bow";
        // The game's own target memory and attack timeout.
        private const float NativeMemory = 30f, NativeTimeout = 60f;

        private sealed class State
        {
            internal MonsterAI Ai;
            internal bool Active;
            internal float View, Alert, Multiplier;
            // The speeds before the multiplier, and the ones it wrote.
            internal float Speed, Walk, Run;
            internal float WrittenSpeed = float.NaN, WrittenWalk = float.NaN, WrittenRun = float.NaN;
        }

        private static readonly ConditionalWeakTable<Character, State> States = new ConditionalWeakTable<Character, State>();
        private static readonly AccessTools.FieldRef<BaseAI, Character> Owner = AccessTools.FieldRefAccess<BaseAI, Character>("m_character");
        private static readonly AccessTools.FieldRef<BaseAI, Vector3> SpawnPoint = AccessTools.FieldRefAccess<BaseAI, Vector3>("m_spawnPoint");
        private static string nemesisBoss;
        private static ObjectDB items;
        private static GameObject bow;

        internal static void Initialize(IntegrationPatches harmony)
        {
            void Patch(System.Reflection.MethodBase method, string prefix = null, string postfix = null, string transpiler = null)
            {
                if (method == null) throw new InvalidOperationException("A late-biome pressure hook moved.");
                harmony.Patch(method, prefix: prefix == null ? null : new HarmonyMethod(typeof(LateBiomePressure), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(LateBiomePressure), postfix),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(LateBiomePressure), transpiler));
            }
            Patch(AccessTools.DeclaredMethod(typeof(MonsterAI), "Awake"), postfix: nameof(Setup));
            Patch(AccessTools.Method(typeof(MonsterAI), "UpdateTarget",
                new[] { typeof(Humanoid), typeof(float), typeof(bool).MakeByRefType(), typeof(bool).MakeByRefType() }), transpiler: nameof(Timeouts));
            Patch(AccessTools.DeclaredMethod(typeof(MonsterAI), nameof(MonsterAI.UpdateAI)), transpiler: nameof(Decisions));
            Patch(AccessTools.Method(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon)), transpiler: nameof(Selection));
            if (!StarLevelSystem.Installed) return;
            nemesisBoss = AccessTools.Field(StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects"), "SLS_NEMESIS_BOSS")?.GetValue(null) as string ??
                throw new InvalidOperationException("StarLevelSystem's Nemesis miniboss key changed.");
            Patch(AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.AnimationAndSpeed.SpeedModifications"), "ApplySpeedModifications"),
                prefix: nameof(Unmultiply), postfix: nameof(Speeds));
        }

        private static bool Eligible(Character character, BaseAI ai)
        {
            string name = Utils.GetPrefabName(character.gameObject);
            if (!Fast.Contains(name) && !Heavy.Contains(name) && !Other.Contains(name)) return false;
            var view = GameFields.CharacterView(character);
            var data = view != null && view.IsValid() ? view.GetZDO() : null;
            var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(SpawnPoint(ai)) : Heightmap.Biome.None;
            return (biome == Heightmap.Biome.AshLands || biome == Heightmap.Biome.DeepNorth) && !Excluded(character) &&
                (nemesisBoss == null || data == null || !data.GetBool(nemesisBoss));
        }

        private static bool Excluded(Character character) => character.IsTamed() || character.IsBoss();

        private static void Setup(MonsterAI __instance)
        {
            var character = Owner(__instance);
            if (character == null || !Eligible(character, __instance)) return;
            var state = States.GetValue(character, _ => new State());
            if (state.Active) return;
            string name = Utils.GetPrefabName(character.gameObject);
            state.Ai = __instance;
            state.View = __instance.m_viewRange;
            state.Alert = __instance.m_alertRange;
            state.Multiplier = Fast.Contains(name) ? Plugin.LateBiomeFastSpeed.Value : Heavy.Contains(name) ? Plugin.LateBiomeHeavySpeed.Value : 1f;
            __instance.m_viewRange = Mathf.Max(state.View, Plugin.LateBiomeSightRange.Value);
            __instance.m_alertRange = Mathf.Max(state.Alert, Plugin.LateBiomeAlertRange.Value);
            state.Active = true;
            Move(character, state);
        }

        // Whether the creature has the pressure; one that has become a tame or a boss gets its own values back.
        private static bool Active(Character character)
        {
            if ((object)character == null || !States.TryGetValue(character, out var state) || !state.Active) return false;
            if (!Excluded(character)) return true;
            state.Active = false;
            if (state.Ai)
            {
                state.Ai.m_viewRange = state.View;
                state.Ai.m_alertRange = state.Alert;
            }
            Restore(character, state);
            return false;
        }

        // The speeds the multiplier started from, unless something else has written the creature's speeds since.
        private static void Restore(Character character, State state)
        {
            if (character.m_speed != state.WrittenSpeed || character.m_walkSpeed != state.WrittenWalk || character.m_runSpeed != state.WrittenRun) return;
            character.m_speed = state.Speed;
            character.m_walkSpeed = state.Walk;
            character.m_runSpeed = state.Run;
        }

        // StarLevelSystem sets a creature's speeds from its prefab's whenever its stars or modifiers apply; the multiplier
        // starts from those, and from what was there at setup, never from its own result. StarLevelSystem gets the speeds
        // back without the multiplier first, so what follows is its own result even when that equals the multiplied speeds,
        // as Lootbags' extra 0.15 makes it for heavy infantry at 1.15, and the earlier speeds when it writes none.
        private static void Unmultiply(Character creature)
        {
            if (Active(creature) && States.TryGetValue(creature, out var state)) Restore(creature, state);
        }

        private static void Speeds(Character creature)
        {
            if (Active(creature) && States.TryGetValue(creature, out var state)) Move(creature, state);
        }

        private static void Move(Character character, State state)
        {
            state.Speed = character.m_speed;
            state.Walk = character.m_walkSpeed;
            state.Run = character.m_runSpeed;
            state.WrittenSpeed = character.m_speed = state.Speed * state.Multiplier;
            state.WrittenWalk = character.m_walkSpeed = state.Walk * state.Multiplier;
            state.WrittenRun = character.m_runSpeed = state.Run * state.Multiplier;
        }

        // MonsterAI.UpdateTarget drops a target unsensed for 30 seconds, or not attacked for 60 unless it hunts players.
        private static IEnumerable<CodeInstruction> Timeouts(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var sensed = AccessTools.Field(typeof(MonsterAI), "m_timeSinceSensedTargetCreature");
            var memory = Enumerable.Range(0, code.Count - 1).Where(index => code[index].LoadsField(sensed) && code[index + 1].Is(OpCodes.Ldc_R4, NativeMemory)).ToList();
            var timeout = Enumerable.Range(0, code.Count).Where(index => code[index].Is(OpCodes.Ldc_R4, NativeTimeout)).ToList();
            if (memory.Count != 1 || timeout.Count != 1) throw new InvalidOperationException("MonsterAI's target timeouts changed.");
            foreach (var (index, method) in new[] { (memory[0] + 1, nameof(Memory)), (timeout[0], nameof(Timeout)) }.OrderByDescending(entry => entry.Item1))
                Call(code, index, method);
            return code;
        }

        // MonsterAI.UpdateAI: the attack ranges it fires from.
        private static IEnumerable<CodeInstruction> Decisions(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var ranges = Ranges(code);
            if (ranges.Count != 3) throw new InvalidOperationException("MonsterAI's attack ranges changed.");
            foreach (int index in ranges)
            {
                code[index].opcode = OpCodes.Ldarg_0;
                code[index].operand = null;
                code[index + 1].opcode = OpCodes.Call;
                code[index + 1].operand = AccessTools.Method(typeof(LateBiomePressure), nameof(AttackRange));
            }
            return code;
        }

        // Humanoid.EquipBestWeapon: the range at which a creature chooses a weapon.
        private static IEnumerable<CodeInstruction> Selection(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var ranges = Ranges(code);
            if (ranges.Count != 1) throw new InvalidOperationException("Creature weapon selection changed.");
            code[ranges[0]].opcode = OpCodes.Ldarg_0;
            code[ranges[0]].operand = null;
            code[ranges[0] + 1].opcode = OpCodes.Call;
            code[ranges[0] + 1].operand = AccessTools.Method(typeof(LateBiomePressure), nameof(SelectionRange));
            return code;
        }

        // Each item.m_shared.m_aiAttackRange read.
        private static List<int> Ranges(List<CodeInstruction> code)
        {
            var shared = AccessTools.Field(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.m_shared));
            var range = AccessTools.Field(typeof(ItemDrop.ItemData.SharedData), nameof(ItemDrop.ItemData.SharedData.m_aiAttackRange));
            return Enumerable.Range(0, code.Count - 1).Where(index => code[index].LoadsField(shared) && code[index + 1].LoadsField(range)).ToList();
        }

        // Replaces a constant with this method's value for the creature.
        private static void Call(List<CodeInstruction> code, int index, string method)
        {
            code[index].opcode = OpCodes.Ldarg_0;
            code[index].operand = null;
            code.Insert(index + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(LateBiomePressure), method)));
        }

        private static float Memory(MonsterAI ai) => Active(Owner(ai)) ? Plugin.LateBiomeTargetMemory.Value : NativeMemory;

        private static float Timeout(MonsterAI ai) => Active(Owner(ai)) ? Plugin.LateBiomeAttackTimeout.Value : NativeTimeout;

        internal static float AttackRange(ItemDrop.ItemData item, MonsterAI ai) => Range(item, Owner(ai));

        private static float SelectionRange(ItemDrop.ItemData item, Humanoid owner) => Range(item, owner);

        private static float Range(ItemDrop.ItemData item, Character character)
        {
            float range = item.m_shared.m_aiAttackRange;
            if ((object)ObjectDB.instance != items)
            {
                items = ObjectDB.instance;
                bow = items != null ? items.GetItemPrefab(CharredBow) : null;
            }
            var prefab = (object)item.m_dropPrefab;
            float wanted = prefab == null ? 0f : prefab == (object)bow ? Plugin.CharredBowRange.Value : 0f;
            return wanted > range && Active(character) ? wanted : range;
        }
    }
}
