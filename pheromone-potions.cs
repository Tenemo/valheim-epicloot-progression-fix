using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Love Potion and the Anti-Sting Concoction work through their status effect's pheromone data: a target creature
    // that shows hearts or flees, and for the Love Potion the target's spawn chance, most at once, minimum level and
    // level-up factor. A status effect exists only on its drinker's game, but a zone's spawns and a creature's behavior run
    // on whichever game controls them, so each game publishes its player's pheromone effects, and every pheromone check
    // reads another player's from there.
    internal static class PheromonePotions
    {
        private static readonly int LovePotionEffect = "Potion_TrollPheromones".GetStableHashCode();
        // Name hashes of the player's pheromone status effects, comma-separated.
        internal static readonly int EffectsKey = "epicloot_progression_pheromones".GetStableHashCode();
        private static readonly Dictionary<string, List<StatusEffect>> published = new Dictionary<string, List<StatusEffect>>();
        private static ObjectDB publishedFrom;
        private static readonly List<StatusEffect> none = new List<StatusEffect>();

        // A player's status effects on its own game, and the pheromone effects its game published everywhere else.
        internal static List<StatusEffect> EffectsOf(SEMan seman)
        {
            var view = GameFields.StatusCharacter(seman) is Player player ? GameFields.CharacterView(player) : null;
            return view == null || !view.IsValid() || view.IsOwner() ? seman.GetStatusEffects() : PublishedEffects(view.GetZDO());
        }

        private static List<StatusEffect> PublishedEffects(ZDO zdo)
        {
            string hashes = zdo.GetString(EffectsKey);
            if (hashes.Length == 0 || ObjectDB.instance == null) return none;
            // Each game session loads its own ObjectDB, and mods may create its status effects anew.
            if (!ReferenceEquals(publishedFrom, ObjectDB.instance))
            {
                published.Clear();
                publishedFrom = ObjectDB.instance;
            }
            if (!published.TryGetValue(hashes, out var effects))
                published[hashes] = effects = hashes.Split(',').Select(hash => ObjectDB.instance.GetStatusEffect(int.Parse(hash, CultureInfo.InvariantCulture))).Where(effect => effect != null).ToList();
            return effects;
        }

        // Compared by name, as the game compares a pheromone effect's target with a creature.
        internal static bool Targets(SE_Stats effect, GameObject creature)
        {
            var target = effect.m_pheromoneTarget != null ? effect.m_pheromoneTarget.GetComponent<Character>() : null;
            var character = creature != null ? creature.GetComponent<Character>() : null;
            return target != null && character != null && target.m_name == character.m_name;
        }

        internal static bool IsLovePotion(SE_Stats effect) => effect != null && effect.NameHash() == LovePotionEffect;
    }

    // Publish the local player's pheromone effects once a second, when they changed.
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class PheromoneEffectsPublished
    {
        private static float nextCheck;

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer || Time.time < nextCheck) return;
            nextCheck = Time.time + 1f;
            var view = GameFields.CharacterView(__instance);
            if (view == null || !view.IsValid()) return;
            string hashes = string.Join(",", __instance.GetSEMan().GetStatusEffects().OfType<SE_Stats>()
                .Where(effect => effect.m_pheromoneTarget != null).Select(effect => effect.NameHash().ToString(CultureInfo.InvariantCulture)));
            var zdo = view.GetZDO();
            if (zdo.GetString(PheromonePotions.EffectsKey) != hashes) zdo.Set(PheromonePotions.EffectsKey, hashes);
        }
    }

    // The game's pheromone checks, a Love Potion target's hearts and alert and an Anti-Sting target's flight, run on the
    // game that controls the creature and read each player's status effects there, where only its own player has any.
    [HarmonyPatch]
    internal static class PheromoneChecks
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var checks = new[] { AccessTools.Method(typeof(Character), "UpdatePheromones"), AccessTools.Method(typeof(MonsterAI), "PheromoneFleeCheck") };
            if (checks.Contains(null)) throw new InvalidOperationException("Valheim's pheromone checks changed.");
            return checks;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            var code = instructions.ToList();
            var effects = AccessTools.Method(typeof(SEMan), nameof(SEMan.GetStatusEffects));
            int index = code.FindIndex(x => x.Calls(effects));
            if (index < 0 || code.FindLastIndex(x => x.Calls(effects)) != index)
                throw new InvalidOperationException($"Valheim's {original.Name} changed; expected one read of a player's status effects.");
            code[index].opcode = OpCodes.Call;
            code[index].operand = AccessTools.Method(typeof(PheromonePotions), nameof(PheromonePotions.EffectsOf));
            return code;
        }
    }

    // Valheim keeps the Love Potion's spawn chance and most at once for trolls, but its spawner never reads them. A zone
    // rolls its world spawns on the game that controls it, for the players standing in it; while one of them has such a
    // potion, the zone rolls for the potion's target with the potion's chance and most at once where they are higher.
    // Raids keep their own spawns.
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList")]
    internal static class PheromoneSpawns
    {
        // The game fills this with the players in the zone before each pass.
        private static readonly List<Player> PlayersInZone = AccessTools.Field(typeof(SpawnSystem), "m_tempNearPlayers")?.GetValue(null) as List<Player>
            ?? throw new InvalidOperationException("Valheim's spawner changed; expected its list of the players in a zone.");
        // This pass's raised spawn entries, each with the potion that raised it.
        internal static readonly Dictionary<SpawnSystem.SpawnData, SE_Stats> Raised = new Dictionary<SpawnSystem.SpawnData, SE_Stats>();

        private static void Prefix(ref List<SpawnSystem.SpawnData> spawners, bool eventSpawners)
        {
            Raised.Clear();
            if (eventSpawners) return;
            var potions = PlayersInZone.SelectMany(player => PheromonePotions.EffectsOf(player.GetSEMan())).OfType<SE_Stats>()
                .Where(effect => effect.m_pheromoneTarget != null && !effect.m_pheromoneFlee).ToList();
            if (potions.Count == 0) return;
            var raised = new List<SpawnSystem.SpawnData>(spawners);
            for (int index = 0; index < raised.Count; index++)
            {
                var potion = potions.FirstOrDefault(effect => PheromonePotions.Targets(effect, raised[index].m_prefab));
                if (potion == null) continue;
                var entry = raised[index].Clone();
                bool lovePotion = PheromonePotions.IsLovePotion(potion);
                entry.m_spawnChance = Mathf.Max(entry.m_spawnChance, lovePotion
                    ? Plugin.LovePotionTrollSpawnChance.Value : potion.m_pheromoneSpawnChanceOverride);
                if (lovePotion) entry.m_spawnInterval = Mathf.Min(entry.m_spawnInterval, Plugin.LovePotionZoneRecheckSeconds.Value);
                // 0 sets no limit.
                if (entry.m_maxSpawned > 0) entry.m_maxSpawned = Mathf.Max(entry.m_maxSpawned, potion.m_pheromoneMaxInstanceOverride);
                Raised[entry] = potion;
                raised[index] = entry;
            }
            spawners = raised;
        }

        private static void Finalizer() => Raised.Clear();
    }

    // The Love Potion also improves stars near the world center. Other pheromone effects retain their entry's distance gate.
    [HarmonyPatch(typeof(SpawnSystem), "Spawn")]
    internal static class PheromoneSpawnLevel
    {
        internal static SE_Stats Potion;

        private static void Prefix(SpawnSystem.SpawnData __0, Vector3 __1)
        {
            Potion = PheromoneSpawns.Raised.TryGetValue(__0, out var potion) &&
                (PheromonePotions.IsLovePotion(potion) || __0.m_levelUpMinCenterDistance <= 0f ||
                 __1.magnitude > __0.m_levelUpMinCenterDistance) ? potion : null;
        }

        private static void Finalizer() => Potion = null;
    }

    // StarLevelSystem rolls every spawned creature's level itself, so the Love Potion's minimum level and level-up factor go
    // into its roll. A creature spawned for the potion keeps the potion's mark, and every roll of a marked creature, on
    // whichever game rolls it, multiplies the level-up chances by the potion's factor and gives at least the potion's
    // minimum level, within the creature's maximum.
    internal static class PheromoneLevels
    {
        private static readonly int PotionKey = "epicloot_progression_pheromone_potion".GetStableHashCode();
        private static MethodInfo maxLevel;
        private static SE_Stats rolling;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var selection = StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.LevelSystem.LevelSelection");
            var data = StarLevelSystem.Assembly.GetType("StarLevelSystem.common.DataObjects");
            var creature = data?.GetNestedType("CreatureSpecificSetting");
            var biome = data?.GetNestedType("BiomeSpecificSetting");
            var determine = creature == null || biome == null ? null : AccessTools.Method(selection, "DetermineLevel",
                new[] { typeof(Character), typeof(ZDO), creature, biome, typeof(Heightmap.Biome), typeof(int), typeof(bool) });
            maxLevel = determine == null ? null : AccessTools.Method(selection, "GetMaxCreatureLevel", new[] { typeof(Character), creature, biome, typeof(Heightmap.Biome), typeof(bool?) });
            if (determine?.ReturnType != typeof(int) || maxLevel?.ReturnType != typeof(int))
                throw new InvalidOperationException("StarLevelSystem's level selection changed.");
            var self = typeof(PheromoneLevels);
            harmony.Patch(determine, prefix: new HarmonyMethod(self, nameof(Mark)), postfix: new HarmonyMethod(self, nameof(AtLeastMinimum)),
                finalizer: new HarmonyMethod(self, nameof(Done)));
            // The upstream selector supplies the day/night factor; the potion multiplies it afterwards.
            harmony.Patch(StarLevelSystem.LevelRoll, prefix: new HarmonyMethod(self, nameof(RaiseChances)) { priority = Priority.Low });
        }

        // SLS rolls a level only on the game allowed to, which marks the creature there.
        private static void Mark(Character __0, ZDO __1, bool __6)
        {
            rolling = null;
            if (__0 == null || __1 == null || !__6) return;
            var spawned = PheromoneSpawnLevel.Potion;
            if (spawned != null && PheromonePotions.Targets(spawned, __0.gameObject) && __1.GetInt(PotionKey) != spawned.NameHash())
                __1.Set(PotionKey, spawned.NameHash());
            int potion = __1.GetInt(PotionKey);
            rolling = potion != 0 && ObjectDB.instance != null ? ObjectDB.instance.GetStatusEffect(potion) as SE_Stats : null;
        }

        private static void RaiseChances(ref float nightBonus)
        {
            if (rolling != null) nightBonus *= PheromonePotions.IsLovePotion(rolling)
                ? Plugin.LovePotionStarChanceMultiplier.Value : rolling.m_pheromoneLevelUpMultiplier;
        }

        private static void AtLeastMinimum(Character __0, object __2, object __3, Heightmap.Biome __4, ref int __result)
        {
            if (rolling == null) return;
            int minimumLevel = PheromonePotions.IsLovePotion(rolling)
                ? Plugin.LovePotionMinimumTrollStars.Value + 1 : rolling.m_pheromoneSpawnMinLevel;
            if (__result >= minimumLevel) return;
            int max = (int)maxLevel.Invoke(null, new object[] { __0, __2, __3, __4, null });
            __result = Math.Max(__result, Math.Min(minimumLevel, max));
        }

        private static void Done() => rolling = null;
    }
}
