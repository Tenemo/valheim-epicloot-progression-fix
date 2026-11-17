using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // StarLevelSystem sets a Nemesis spawn's faction only on the game that spawns it, and its Splitter copies take neither
    // that faction nor the saved Minion name, so a hostile Dvergr minion splits into friendly Dvergr. Nemesis factions are
    // saved on the creature and restored wherever it loads. Each copy takes its parent's faction and saved name when it is
    // created, before StarLevelSystem sets it up; split counts, levels, tameness and the removed Splitter stay as they are.
    internal static class NemesisFactions
    {
        private static readonly int Saved = "EpicLootProgressionFix.Faction".GetStableHashCode();
        private static string nameKey;
        // The identity of the creature whose death is being split, and the one a copy being created takes.
        private static (Character.Faction Faction, string Name)? dying, inherited;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            Type Named(string name) => StarLevelSystem.Assembly.GetTypes().Single(type => type.FullName == name);
            nameKey = AccessTools.Field(Named("StarLevelSystem.common.DataObjects"), "SLS_NAME")?.GetValue(null) as string;
            var spawn = AccessTools.Method(Named("StarLevelSystem.modules.NemesisSystem.NemesisRemoteSpawnControl"), "SpawnNemesisSpawn");
            var splitter = Named("StarLevelSystem.Modifiers.Splitter+CharacterOnDeath");
            var splitStarts = AccessTools.Method(splitter, "Prefix");
            var splits = AccessTools.Method(splitter, "SpawnSplitsAsync");
            if (nameKey == null || spawn == null || splitStarts == null || splits == null)
                throw new InvalidOperationException("StarLevelSystem's Nemesis spawning or splitting changed.");
            var self = typeof(NemesisFactions);
            harmony.Patch(spawn, transpiler: new HarmonyMethod(self, nameof(SaveSpawnFaction)));
            harmony.Patch(AccessTools.Method(typeof(Character), "Awake"), postfix: new HarmonyMethod(self, nameof(Restore)));
            harmony.Patch(splitStarts, prefix: new HarmonyMethod(self, nameof(DeathStarts)), finalizer: new HarmonyMethod(self, nameof(DeathEnds)));
            harmony.Patch(splits, postfix: new HarmonyMethod(self, nameof(CarryIdentity)));
        }

        private static IEnumerable<CodeInstruction> SaveSpawnFaction(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(Character), nameof(Character.m_faction));
            int matches = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.StoresField(field))
                {
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(NemesisFactions), nameof(SetFaction))).MoveLabelsFrom(instruction);
                    matches++;
                }
                else yield return instruction;
            }
            if (matches != 1) throw new InvalidOperationException("StarLevelSystem's Nemesis faction assignment changed.");
        }

        private static void SetFaction(Character character, Character.Faction faction)
        {
            character.m_faction = faction;
            var zdo = character.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null && zdo.IsOwner()) zdo.Set(Saved, (int)faction);
        }

        private static void Restore(Character __instance)
        {
            var zdo = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (zdo == null) return;
            if (inherited is (Character.Faction Faction, string Name) identity && zdo.IsOwner())
            {
                if (__instance.m_faction != identity.Faction) SetFaction(__instance, identity.Faction);
                if (!string.IsNullOrEmpty(identity.Name)) zdo.Set(nameKey, identity.Name);
                return;
            }
            int saved = zdo.GetInt(Saved, -1);
            if (saved >= 0) __instance.m_faction = (Character.Faction)saved;
        }

        private static void DeathStarts(Character __0)
        {
            var zdo = __0 ? __0.GetComponent<ZNetView>()?.GetZDO() : null;
            dying = zdo == null ? ((Character.Faction, string)?)null : (__0.m_faction, zdo.GetString(nameKey));
        }

        private static Exception DeathEnds(Exception __exception)
        {
            dying = null;
            return __exception;
        }

        // The copies appear one a frame after the death, so each death's identity travels with its own spawning.
        private static void CarryIdentity(ref IEnumerator __result)
        {
            if (dying is (Character.Faction, string) identity) __result = Inherit(__result, identity);
        }

        private static IEnumerator Inherit(IEnumerator spawning, (Character.Faction, string) identity)
        {
            while (true)
            {
                var outer = inherited;
                inherited = identity;
                bool more;
                try { more = spawning.MoveNext(); }
                finally { inherited = outer; }
                if (!more) yield break;
                yield return spawning.Current;
            }
        }
    }
}
