using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class PlantingGridSafety
    {
        private readonly struct Placement
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal Placement(GameObject ghost) { Position = ghost.transform.position; Rotation = ghost.transform.rotation; }
        }
        private static Type cacheType;
        private static FieldInfo cacheVersion, extras, statuses, valid, currentPiece, placementGhost, configuration;
        private static MethodInfo updateGridVersion, updateGhost;
        private static PropertyInfo active, columns, planting, activeGhosts;
        private static object invalid;
        private static List<GameObject> accepted;
        private static Dictionary<GameObject, Placement> placements;
        private static readonly Action<Player, bool> RefreshGhost = AccessTools.MethodDelegate<Action<Player, bool>>(AccessTools.Method(typeof(Player), "UpdatePlacementGhost"));
        private static readonly Action<Player> RecreateGhost = AccessTools.MethodDelegate<Action<Player>>(AccessTools.Method(typeof(Player), "SetupPlacementGhost"));

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!PluginIds.TryGet(PluginIds.PlantEasily, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            Type Type(string name) => assembly.GetType("Advize_PlantEasily." + name, true);
            var grid = Type("GhostGrid"); var controller = Type("PlacementController");
            cacheType = Type("GhostCache"); cacheVersion = AccessTools.Field(cacheType, "lastUpdatedVersion");
            extras = AccessTools.Field(grid, "ExtraGhosts"); statuses = AccessTools.Field(grid, "GhostPlacementStatus"); valid = AccessTools.Field(grid, "ValidExtraGhosts");
            currentPiece = AccessTools.Field(Type("PlacementState"), "Piece");
            placementGhost = AccessTools.Field(Type("PlacementState"), "PlacementGhost");
            configuration = AccessTools.Field(Type("ModContext"), "config");
            active = AccessTools.Property(configuration.FieldType, "ModActive"); columns = AccessTools.Property(configuration.FieldType, "Columns");
            planting = AccessTools.Property(controller, "IsPlanting");
            invalid = Enum.Parse(Type("Status"), "Invalid");
            updateGridVersion = AccessTools.Method(grid, "UpdateGridVersion"); updateGhost = AccessTools.Method(grid, "UpdateGhost");
            activeGhosts = AccessTools.Property(grid, "MaxActiveGhosts");
            var self = typeof(PlantingGridSafety);
            harmony.Patch(AccessTools.Method(grid, "InitializeGhosts"), postfix: new HarmonyMethod(self, nameof(Invalidate)));
            harmony.Patch(AccessTools.Method(grid, "ResizeGrid"), postfix: new HarmonyMethod(self, nameof(RebuildResizedGrid)));
            harmony.Patch(AccessTools.Method(grid, "Update"), prefix: new HarmonyMethod(self, nameof(RebuildResizedGrid)));
            harmony.Patch(AccessTools.Method(Type("PlacementPatches+PlayerTryPlacePiece"), "Prefix"), prefix: new HarmonyMethod(self, nameof(ValidateClick)));
            harmony.Patch(AccessTools.Method(controller, "StartBulkPlanting"), prefix: new HarmonyMethod(self, nameof(Capture)));
            harmony.Patch(AccessTools.Method(controller, "BulkPlanting"), postfix: new HarmonyMethod(self, nameof(CleanupBatch)));
            harmony.Patch(AccessTools.EnumeratorMoveNext(AccessTools.Method(controller, "BulkPlanting")), transpiler: new HarmonyMethod(self, nameof(UseAcceptedGhosts)));
            harmony.Patch(AccessTools.Method(controller, "PlacePiece"), prefix: new HarmonyMethod(self, nameof(UseAcceptedPosition)), finalizer: new HarmonyMethod(self, nameof(RemoveCarrier)));
        }

        // A settled preview has already lost its ZNetView. Growing the pool from that preview leaves Plant.Awake
        // incomplete and crashes the shared slow-update loop. Recreate it from the selected prefab before rebuilding.
        private static void RebuildResizedGrid()
        {
            var ghost = (GameObject)placementGhost.GetValue(null);
            if (ghost && Player.m_localPlayer && (bool)active.GetValue(configuration.GetValue(null)) &&
                ((IList)statuses.GetValue(null)).Count != 1 + (int)activeGhosts.GetValue(null))
                RecreateGhost(Player.m_localPlayer);
        }

        private static void Invalidate()
        {
            foreach (var ghost in (List<GameObject>)extras.GetValue(null))
                if (ghost && ghost.GetComponent(cacheType) is Component cache) cacheVersion.SetValue(cache, -1);
            var checks = (IList)statuses.GetValue(null);
            for (int index = 0; index < checks.Count; index++) checks[index] = invalid;
        }

        private static void ValidateClick(Player __0, Piece __1)
        {
            if (__0 != Player.m_localPlayer || !(bool)active.GetValue(configuration.GetValue(null)) || (bool)planting.GetValue(null) ||
                !__1 || (!__1.GetComponent<Plant>() && !__1.GetComponent<Pickable>()) || GameFields.RightItem(__0)?.m_shared.m_name != "$item_cultivator") return;
            RefreshGhost(__0, false);
            var piece = (Piece)currentPiece.GetValue(null);
            if (!piece || piece.m_resources.Length == 0) return;
            int cost = piece.m_resources[0].m_amount;
            int width = (int)columns.GetValue(configuration.GetValue(null));
            Invalidate(); updateGridVersion.Invoke(null, null);
            try
            {
                int count = ((IList)statuses.GetValue(null)).Count;
                for (int index = 0; index < count; index++)
                {
                    piece.m_resources[0].m_amount = checked(cost * (index + 1));
                    updateGhost.Invoke(null, new object[] { __0, piece, index / width, index % width, index });
                }
            }
            finally { piece.m_resources[0].m_amount = cost; }
        }

        private static void Capture()
        {
            accepted = ((List<GameObject>)valid.GetValue(null)).ToList();
            placements = accepted.ToDictionary(ghost => ghost, ghost => new Placement(ghost));
        }

        private static List<GameObject> AcceptedGhosts() => accepted;

        private static IEnumerable<CodeInstruction> UseAcceptedGhosts(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList(); int replaced = 0;
            for (int index = 0; index + 1 < code.Count; index++)
            {
                if (!code[index].LoadsField(valid) || !(code[index + 1].operand is MethodInfo method) || method.Name != "GetEnumerator") continue;
                code[index].opcode = OpCodes.Call; code[index].operand = AccessTools.Method(typeof(PlantingGridSafety), nameof(AcceptedGhosts)); replaced++;
            }
            if (replaced != 2) throw new InvalidOperationException("PlantEasily's accepted placement enumeration changed.");
            return code;
        }

        private static void UseAcceptedPosition(ref GameObject go, out GameObject __state)
        {
            __state = null;
            if (placements == null || !placements.TryGetValue(go, out var placement)) return;
            if (!go) go = __state = new GameObject("planting-position");
            go.transform.SetPositionAndRotation(placement.Position, placement.Rotation);
        }

        private static void RemoveCarrier(GameObject __state) { if (__state) UnityEngine.Object.Destroy(__state); }
        private static void CleanupBatch(ref IEnumerator __result) => __result = CompleteBatch(__result);
        private static IEnumerator CompleteBatch(IEnumerator batch)
        {
            try { while (batch.MoveNext()) yield return batch.Current; }
            finally { (batch as IDisposable)?.Dispose(); accepted = null; placements = null; }
        }
    }
}
