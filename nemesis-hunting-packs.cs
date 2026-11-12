using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // StarLevelSystem's Nemesis ambushes spawn the same group every time. Once a biome's ambush has passed its own gates
    // and chance roll, the add-on picks one whole pack for it: the rule's own SpawnConfig or one of the disabled
    // HuntingPack/<biome>/<name> entries in NemesisSettings.yaml, which only hold compositions and never roll themselves.
    // A rule takes part when it is a Spawn rule allowed in exactly one biome that also needs the player there. Each biome
    // deals its packs from a shuffled deck, every pack once before the next shuffle and never the same pack twice in a
    // row, and only an ambush that spawns draws a card. SLS still spawns, levels, charges the score and starts the
    // cooldown, so one ambush is still one pack, one score charge and one cooldown. Decks start over in each game.
    //
    // A pack's creatures, and the creatures they leave behind or summon, such as an oozer's blobs, a splitter's copies or a
    // warlock's twitchers, carry a mark in their saved data. Their own Nemesis checks then bring nothing, as a spawn's
    // escorts never do, so a pack cannot set off another one.
    //
    // SLS spawns every ambush, a pack or a miniboss, where the creature that set it off appeared, and the game lets some
    // Ashlands creatures, such as Morgen and Fallen Valkyries, appear over lava. An ambush set off over lava appears on the
    // nearest ground within 30 m of the same biome that the game's own spawns count as outside lava for 1.5 m around and that
    // lies above the sea; without such ground it doesn't happen, before any roll, card, score charge or cooldown.
    internal static class NemesisHuntingPacks
    {
        internal const string Prefix = "HuntingPack/";
        private const float GroundSearch = 30f, GroundStep = 3f;
        private const int GroundDirections = 12;
        // SLS moves each of an ambush's creatures up to 0.8 m from its spot, so the ground must be free of lava this far around.
        private const float GroundClearance = 1.5f;
        // The ground each spawn check in progress moves its ambush to, or null where the ambush keeps its own spot.
        private static readonly Stack<Vector3?> AmbushGround = new Stack<Vector3?>();
        private static readonly System.Random Shuffle = new System.Random();
        private static readonly Dictionary<string, List<string>> Decks = new Dictionary<string, List<string>>();
        private static readonly Dictionary<string, string> Last = new Dictionary<string, string>();
        private static FieldInfo settings;
        private static PropertyInfo chanceChanges, creatureOps, enabled, action, allowedBiomes, spawnConfig, playerReqs, playerBiome;
        private static ManualLogSource log;
        private static readonly int Mark = "EpicLootProgressionFix.HuntingPack".GetStableHashCode();
        private static FieldInfo summoner, summonOwner;
        // Spawn checks in progress, the one whose pack is being spawned, and marked creatures dying or summoning.
        private static int depth, packAt, offspring;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource logger)
        {
            if (!StarLevelSystem.Installed) return;
            log = logger;
            Type Named(string name) => StarLevelSystem.Assembly.GetTypes().Single(type => type.FullName == name);
            var data = Named("StarLevelSystem.common.DataObjects");
            var entry = data.GetNestedType("NemesisChanceEntry");
            settings = AccessTools.Field(Named("StarLevelSystem.Data.NemesisSystemData"), "SLE_Nemesis_Settings");
            chanceChanges = AccessTools.Property(settings?.FieldType, "ChanceChanges");
            creatureOps = AccessTools.Property(chanceChanges?.PropertyType, "CreatureOps");
            enabled = AccessTools.Property(entry, "Enabled");
            action = AccessTools.Property(entry, "Action");
            allowedBiomes = AccessTools.Property(entry, "AllowedBiomes");
            spawnConfig = AccessTools.Property(entry, "SpawnConfig");
            playerReqs = AccessTools.Property(entry, "PlayerReqs");
            playerBiome = AccessTools.Property(playerReqs?.PropertyType, "PlayerCurrentBiome");
            var spawner = AccessTools.Method(Named("StarLevelSystem.modules.NemesisSystem.NemesisActions"), "NemesisRandomSpawner", new[] { typeof(Character) });
            if (creatureOps == null || enabled == null || action == null || allowedBiomes == null || spawnConfig == null || playerBiome == null || spawner == null)
                throw new InvalidOperationException("StarLevelSystem's Nemesis ambush rules changed.");
            var summon = AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(SpawnAbility), "Spawn"));
            summoner = summon == null ? null : AccessTools.Field(summon.DeclaringType, "<>4__this");
            summonOwner = AccessTools.Field(typeof(SpawnAbility), "m_owner");
            if (summoner == null || summonOwner == null) throw new InvalidOperationException("The game's summoning changed.");
            var self = typeof(NemesisHuntingPacks);
            var spawn = AccessTools.Method(Named("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnControl"), "SpawnNemesisSpawn");
            if (spawn == null || spawn.GetParameters().Length < 2 || spawn.GetParameters()[1].ParameterType != typeof(Vector3))
                throw new InvalidOperationException("StarLevelSystem's Nemesis spawn placement changed.");
            harmony.Patch(spawner, prefix: new HarmonyMethod(self, nameof(Enter)), finalizer: new HarmonyMethod(self, nameof(Leave)),
                transpiler: new HarmonyMethod(self, nameof(ChooseAtSpawn)));
            harmony.Patch(spawn, prefix: new HarmonyMethod(self, nameof(SpawnOnGround)));
            harmony.Patch(AccessTools.Method(typeof(Character), "Awake"), postfix: new HarmonyMethod(self, nameof(MarkOffspring)));
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.OnDeath)),
                prefix: new HarmonyMethod(self, nameof(DeathStarts)), finalizer: new HarmonyMethod(self, nameof(Ends)));
            harmony.Patch(summon, prefix: new HarmonyMethod(self, nameof(SummonStarts)), finalizer: new HarmonyMethod(self, nameof(Ends)));
            // SLS spawns some of a death's creatures from coroutines after it: the Splitter modifier's copies one a frame, and
            // loot such as an oozer's blobs 20 a tick, so a Lucky Loot-multiplied drop continues past the death.
            var splitter = Named("StarLevelSystem.Modifiers.Splitter+CharacterOnDeath");
            var splitStarts = AccessTools.Method(splitter, "Prefix");
            var splits = AccessTools.Method(splitter, "SpawnSplitsAsync");
            var loot = AccessTools.Method(Named("StarLevelSystem.modules.Loot.LootPerformanceChanges"), "DropItemsAsync");
            var multiplication = AccessTools.Method(Named("StarLevelSystem.modules.Spawnrate"), "CheckSetApplySpawnrate");
            if (splitStarts == null || splits == null || loot == null || multiplication == null)
                throw new InvalidOperationException("StarLevelSystem's offspring creation changed.");
            harmony.Patch(splitStarts, prefix: new HarmonyMethod(self, nameof(DescendantStarts)), finalizer: new HarmonyMethod(self, nameof(Ends)));
            // Spawn multiplication runs later in the creature setup queue, outside the parent's death or summon.
            harmony.Patch(multiplication, prefix: new HarmonyMethod(self, nameof(DescendantStarts)), finalizer: new HarmonyMethod(self, nameof(Ends)),
                transpiler: new HarmonyMethod(self, nameof(InheritHunting)));
            harmony.Patch(splits, postfix: new HarmonyMethod(self, nameof(CarryOffspring)));
            harmony.Patch(loot, postfix: new HarmonyMethod(self, nameof(CarryOffspring)));
        }

        // Whether a creature belongs to a hunting pack or descends from one.
        internal static bool Descends(Character character) =>
            character && character.GetComponent<ZNetView>()?.GetZDO() is ZDO zdo && zdo.GetBool(Mark);

        // A check that another prefix already stopped, a nested one and a pack creature's own bring nothing, so they search nothing.
        private static bool Enter(Character __0, bool __runOriginal)
        {
            depth++;
            Vector3? ground = null;
            bool allowed = true;
            if (__runOriginal && depth == 1 && __0 && !Descends(__0) && OverLava(__0.transform.position, out var biome))
            {
                ground = LavaFreeGround(__0.transform.position, biome);
                allowed = ground.HasValue;
            }
            AmbushGround.Push(ground);
            return allowed;
        }

        private static Exception Leave(Exception __exception)
        {
            if (packAt == depth) packAt = 0;
            if (AmbushGround.Count > 0) AmbushGround.Pop();
            depth--;
            return __exception;
        }

        // Remote miniboss spawners place their own bosses outside any spawn check and keep their spot.
        private static void SpawnOnGround(ref Vector3 __1)
        {
            if (depth > 0 && AmbushGround.Count > 0 && AmbushGround.Peek() is Vector3 ground) __1 = ground;
        }

        private static bool OverLava(Vector3 position, out Heightmap.Biome biome)
        {
            biome = Heightmap.Biome.None;
            if (!ZoneSystem.instance || WorldGenerator.instance == null || WorldGenerator.instance.GetBiome(position.x, position.z) != Heightmap.Biome.AshLands) return false;
            ZoneSystem.instance.GetGroundData(ref position, out _, out biome, out _, out var map);
            return map && map.IsLava(position);
        }

        // The game's own rule for creatures that may not spawn in lava, on ground of the same biome above the sea.
        private static Vector3? LavaFreeGround(Vector3 origin, Heightmap.Biome biome)
        {
            for (float distance = GroundStep; distance <= GroundSearch; distance += GroundStep)
                for (int direction = 0; direction < GroundDirections; direction++)
                {
                    var point = origin + Quaternion.Euler(0f, direction * 360f / GroundDirections, 0f) * Vector3.forward * distance;
                    ZoneSystem.instance.GetGroundData(ref point, out _, out var pointBiome, out _, out var map);
                    if (map && pointBiome == biome && point.y > ZoneSystem.instance.m_waterLevel && Clear(map, point)) return point;
                }
            return null;
        }

        private static bool Clear(Heightmap map, Vector3 point)
        {
            if (map.IsLava(point)) return false;
            for (int direction = 0; direction < 8; direction++)
            {
                var edge = point + Quaternion.Euler(0f, direction * 45f, 0f) * Vector3.forward * GroundClearance;
                var edgeMap = Heightmap.FindHeightmap(edge);
                if (!edgeMap || edgeMap.IsLava(edge)) return false;
            }
            return true;
        }

        private static void MarkOffspring(Character __instance)
        {
            if (packAt == 0 && offspring == 0) return;
            var view = __instance.GetComponent<ZNetView>();
            if (view && view.GetZDO() != null && view.IsOwner()) view.GetZDO().Set(Mark, true);
        }

        private static void DeathStarts(Character __instance, out bool __state)
        {
            __state = Descends(__instance);
            if (__state) offspring++;
        }

        private static void SummonStarts(object __instance, out bool __state)
        {
            __state = summoner.GetValue(__instance) is SpawnAbility ability && Descends(summonOwner.GetValue(ability) as Character);
            if (__state) offspring++;
        }

        private static void DescendantStarts(Character __0, out bool __state)
        {
            __state = Descends(__0);
            if (__state) offspring++;
        }

        private static IEnumerable<CodeInstruction> InheritHunting(IEnumerable<CodeInstruction> instructions)
        {
            int matches = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (!(instruction.operand is MethodInfo method) || method.DeclaringType != typeof(UnityEngine.Object) || method.Name != "Instantiate" ||
                    method.ReturnType != typeof(UnityEngine.GameObject) || !method.GetParameters().Select(parameter => parameter.ParameterType)
                        .SequenceEqual(new[] { typeof(UnityEngine.GameObject), typeof(UnityEngine.Vector3), typeof(UnityEngine.Quaternion) })) continue;
                yield return new CodeInstruction(OpCodes.Ldarg_0);
                yield return CodeInstruction.Call(typeof(NemesisHuntingPacks), nameof(CopyHunting));
                matches++;
            }
            if (matches != 1) throw new InvalidOperationException("StarLevelSystem's multiplied creature creation changed.");
        }

        private static UnityEngine.GameObject CopyHunting(UnityEngine.GameObject spawned, Character parent)
        {
            if (!parent || parent.IsTamed()) return spawned;
            var original = parent.GetComponent<ZNetView>()?.GetZDO();
            var ai = spawned.GetComponent<BaseAI>();
            if (original == null || !ai) return spawned;
            ai.SetHuntPlayer(original.GetBool(ZDOVars.s_huntPlayer));
            if (ai is MonsterAI child && parent.GetBaseAI() is MonsterAI source)
            {
                if (source.DespawnInDay()) child.SetDespawnInDay(true);
                if (source.IsEventCreature()) child.SetEventCreature(true);
            }
            return spawned;
        }

        private static void CarryOffspring(ref IEnumerator __result)
        {
            if (offspring > 0) __result = MarkEachStep(__result);
        }

        private static IEnumerator MarkEachStep(IEnumerator spawning)
        {
            while (true)
            {
                offspring++;
                bool more;
                try { more = spawning.MoveNext(); }
                finally { offspring--; }
                if (!more) yield break;
                yield return spawning.Current;
            }
        }

        private static Exception Ends(bool __state, Exception __exception)
        {
            if (__state) offspring--;
            return __exception;
        }

        // The rule's spawn list is read once, after its gates and chance roll have passed.
        private static IEnumerable<CodeInstruction> ChooseAtSpawn(IEnumerable<CodeInstruction> instructions)
        {
            var read = spawnConfig.GetGetMethod();
            var code = instructions.ToList();
            var reads = code.Where(instruction => instruction.Calls(read)).ToList();
            if (reads.Count != 1) throw new InvalidOperationException("StarLevelSystem's Nemesis ambush spawn list read changed.");
            int index = code.IndexOf(reads[0]);
            code[index] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NemesisHuntingPacks), nameof(Choose))).MoveLabelsFrom(code[index]);
            code.Insert(index + 1, new CodeInstruction(OpCodes.Castclass, spawnConfig.PropertyType));
            return code;
        }

        private static object Choose(object rule)
        {
            var own = spawnConfig.GetValue(rule);
            string biome = PoolBiome(rule);
            if (biome == null) return own;
            var ops = (IDictionary)creatureOps.GetValue(chanceChanges.GetValue(settings.GetValue(null)));
            var packs = new Dictionary<string, object>();
            string name = null;
            foreach (DictionaryEntry op in ops)
            {
                string key = (string)op.Key;
                if (op.Value == rule) name = key;
                if (key.StartsWith(Prefix + biome + "/", StringComparison.Ordinal) && !(bool)enabled.GetValue(op.Value) && spawnConfig.GetValue(op.Value) != null)
                    packs[key] = spawnConfig.GetValue(op.Value);
            }
            if (packs.Count == 0) return own;
            // The rule's own composition is the empty key, so both Ashlands ambushes share one deck with their own pack.
            string pick = Deal(biome, new[] { "" }.Concat(packs.Keys).ToList());
            packAt = depth;
            log.LogInfo($"Nemesis ambush {name}: {(pick.Length == 0 ? "its own pack" : pick)}.");
            return pick.Length == 0 ? own : packs[pick];
        }

        // The single biome of a Spawn rule that also needs the player in it, or null.
        private static string PoolBiome(object rule)
        {
            if (action.GetValue(rule).ToString() != "Spawn" || !(allowedBiomes.GetValue(rule) is IList biomes) || biomes.Count != 1) return null;
            var requirements = playerReqs.GetValue(rule);
            string biome = biomes[0].ToString();
            return requirements != null && playerBiome.GetValue(requirements).ToString() == biome ? biome : null;
        }

        private static string Deal(string biome, List<string> packs)
        {
            if (!Decks.TryGetValue(biome, out var deck)) Decks[biome] = deck = new List<string>();
            deck.RemoveAll(pack => !packs.Contains(pack));
            if (deck.Count == 0)
            {
                deck.AddRange(packs);
                for (int index = deck.Count - 1; index > 0; index--)
                {
                    int other = Shuffle.Next(index + 1);
                    (deck[index], deck[other]) = (deck[other], deck[index]);
                }
                if (deck.Count > 1 && Last.TryGetValue(biome, out string last) && deck[0] == last)
                    (deck[0], deck[deck.Count - 1]) = (deck[deck.Count - 1], deck[0]);
            }
            string pick = deck[0];
            deck.RemoveAt(0);
            Last[biome] = pick;
            return pick;
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        private static class EndSession
        {
            private static void Postfix()
            {
                Decks.Clear();
                Last.Clear();
                depth = packAt = offspring = 0;
            }
        }
    }
}
