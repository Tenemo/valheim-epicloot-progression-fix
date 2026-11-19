using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Skip only entries the player already knows. The game and other mods still evaluate every
    // unknown recipe and piece on each inventory notification, in the original order.
    [HarmonyPatch(typeof(Player), "UpdateKnownRecipesList")]
    internal static class HarvestDiscovery
    {
        internal static Player Collector;
        private static readonly AccessTools.FieldRef<Player, HashSet<string>> KnownRecipes =
            AccessTools.FieldRefAccess<Player, HashSet<string>>("m_knownRecipes");
        private static readonly AccessTools.FieldRef<HashSet<string>, int> SetVersion =
            AccessTools.FieldRefAccess<HashSet<string>, int>("_version");
        private static readonly AccessTools.StructFieldRef<List<Recipe>.Enumerator, int> RecipeIndex =
            AccessTools.StructFieldRefAccess<List<Recipe>.Enumerator, int>("_index");
        private static readonly AccessTools.StructFieldRef<List<GameObject>.Enumerator, int> PieceIndex =
            AccessTools.StructFieldRefAccess<List<GameObject>.Enumerator, int>("_index");
        private static readonly AccessTools.StructFieldRef<List<GameObject>.Enumerator, List<GameObject>> PieceList =
            AccessTools.StructFieldRefAccess<List<GameObject>.Enumerator, List<GameObject>>("_list");
        private static readonly AccessTools.FieldRef<UnityEngine.Object, IntPtr> NativePointer =
            AccessTools.FieldRefAccess<UnityEngine.Object, IntPtr>("m_CachedPtr");

        private struct Membership
        {
            private string name;
            private int version;
            private bool known;

            internal bool IsKnown(string currentName, HashSet<string> knowledge, int revision)
            {
                if (currentName == null) return knowledge.Contains(null);
                if (!ReferenceEquals(currentName, name) || revision != version)
                {
                    name = currentName;
                    version = revision;
                    known = knowledge.Contains(currentName);
                }
                return known;
            }
        }

        private struct PieceSlot
        {
            internal GameObject Prefab;
            internal Piece Component;
            internal Membership Membership;
        }

        private sealed class PieceCatalog
        {
            internal PieceSlot[] Slots = Array.Empty<PieceSlot>();
        }

        private sealed class Catalog
        {
            internal Membership[] Recipes = Array.Empty<Membership>();
            internal readonly ConditionalWeakTable<List<GameObject>, PieceCatalog> Tables = new ConditionalWeakTable<List<GameObject>, PieceCatalog>();
        }

        private static readonly ConditionalWeakTable<HashSet<string>, Catalog> Catalogs = new ConditionalWeakTable<HashSet<string>, Catalog>();
        private static HashSet<string> currentKnowledge;
        private static Catalog currentCatalog;

        private static Catalog ForKnowledge(HashSet<string> knowledge)
        {
            if (!ReferenceEquals(currentKnowledge, knowledge))
            {
                currentKnowledge = knowledge;
                currentCatalog = Catalogs.GetValue(knowledge, _ => new Catalog());
            }
            return currentCatalog;
        }

        internal static void Pickup(Player player, GameObject output)
        {
            var previous = Collector;
            Collector = player;
            try { player.Pickup(output, false, false); }
            finally
            {
                Collector = previous;
                if (ReferenceEquals(previous, null)) { currentCatalog = null; currentKnowledge = null; }
            }
        }

        private static bool NextRecipe(ref List<Recipe>.Enumerator items, Player player)
        {
            if (!ReferenceEquals(player, Collector)) return items.MoveNext();
            var knowledge = KnownRecipes(player);
            var catalog = ForKnowledge(knowledge);
            int version = SetVersion(knowledge), index = RecipeIndex(ref items);
            while (items.MoveNext())
            {
                string name = items.Current.m_item?.m_itemData?.m_shared?.m_name;
                if (index >= catalog.Recipes.Length) Array.Resize(ref catalog.Recipes, Math.Max(64, index * 2));
                if (name == null || !catalog.Recipes[index++].IsKnown(name, knowledge, version)) return true;
            }
            return false;
        }

        private static bool NextPiece(ref List<GameObject>.Enumerator items, Player player)
        {
            if (!ReferenceEquals(player, Collector)) return items.MoveNext();
            var knowledge = KnownRecipes(player);
            var pieces = PieceList(ref items);
            var table = ForKnowledge(knowledge).Tables.GetValue(pieces, _ => new PieceCatalog());
            if (table.Slots.Length < pieces.Count) Array.Resize(ref table.Slots, pieces.Count);
            int version = SetVersion(knowledge), index = PieceIndex(ref items);
            while (items.MoveNext())
            {
                ref var slot = ref table.Slots[index++];
                var prefab = items.Current;
                // A list entry or its Piece can be replaced without changing the player's knowledge.
                if (!ReferenceEquals(slot.Prefab, prefab) || ReferenceEquals(slot.Component, null) || NativePointer(slot.Component) == IntPtr.Zero)
                {
                    slot.Prefab = prefab;
                    slot.Component = prefab.GetComponent<Piece>();
                }
                if (ReferenceEquals(slot.Component, null) || !slot.Membership.IsKnown(slot.Component.m_name, knowledge, version)) return true;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var recipe = AccessTools.Method(typeof(List<Recipe>.Enumerator), nameof(List<Recipe>.Enumerator.MoveNext));
            var piece = AccessTools.Method(typeof(List<GameObject>.Enumerator), nameof(List<GameObject>.Enumerator.MoveNext));
            int recipeLoops = 0, pieceLoops = 0;
            foreach (var instruction in instructions)
            {
                bool isRecipe = instruction.Calls(recipe);
                if (isRecipe || instruction.Calls(piece))
                {
                    if (isRecipe) recipeLoops++; else pieceLoops++;
                    yield return new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(instruction);
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(HarvestDiscovery), isRecipe ? nameof(NextRecipe) : nameof(NextPiece));
                }
                yield return instruction;
            }
            if (recipeLoops != 1 || pieceLoops != 1) throw new InvalidOperationException("The recipe discovery iterators changed.");
        }
    }
}
