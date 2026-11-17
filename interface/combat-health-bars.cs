using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Mist can prevent the native crosshair from acquiring a creature. Keep the normal HUD alive for
    // alerted enemies and animals whose taming has started, including completed tames.
    // World geometry still blocks the camera's view; vegetation and placeable structures do not. An enemy
    // attacking this player from within the configured attacker range keeps its bar through every obstruction, and so does
    // the player's own summon.
    [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
    internal static class CombatHealthBars
    {
        private static readonly AccessTools.FieldRef<object, float> HoverTimer =
            AccessTools.FieldRefAccess<float>(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_hoverTimer");
        private static readonly AccessTools.FieldRef<object, GameObject> Display =
            AccessTools.FieldRefAccess<GameObject>(AccessTools.Inner(typeof(EnemyHud), "HudData"), "m_gui");
        private static readonly int ObstructionMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "viewblock", "vehicle");
        private static RaycastHit[] obstructionHits = new RaycastHit[32];
        private static readonly Func<EnemyHud, Character, bool, bool> TestShow =
            AccessTools.MethodDelegate<Func<EnemyHud, Character, bool, bool>>(AccessTools.Method(typeof(EnemyHud), "TestShow"));
        // Bound to the game's private bar type, so the bars are visited through the dictionary's own enumerator, which boxes nothing.
        private static readonly Action<EnemyHud, Player, Sadle, Vector3, IDictionary> VisitBars = AccessTools.MethodDelegate<Action<EnemyHud, Player, Sadle, Vector3, IDictionary>>(
            AccessTools.Method(typeof(CombatHealthBars), nameof(Visit)).MakeGenericMethod(AccessTools.Inner(typeof(EnemyHud), "HudData")));

        private static bool Obstructed(Vector3 origin, Vector3 destination)
        {
            var direction = destination - origin;
            int count = Physics.RaycastNonAlloc(origin, direction, obstructionHits, direction.magnitude, ObstructionMask, QueryTriggerInteraction.Ignore);
            // A full buffer may have omitted a wall behind the ignored objects. Grow it and repeat that ray.
            while (count == obstructionHits.Length)
            {
                System.Array.Resize(ref obstructionHits, obstructionHits.Length * 2);
                count = Physics.RaycastNonAlloc(origin, direction, obstructionHits, direction.magnitude, ObstructionMask, QueryTriggerInteraction.Ignore);
            }
            // A hit names its collider by instance ID; finding the collider itself is an engine lookup, needed only the first time.
            for (int index = 0; index < count; index++)
                if (BlockingColliders.TryGetValue(obstructionHits[index].colliderInstanceID, out bool blocks) ? blocks : Blocks(obstructionHits[index].collider)) return true;
            return false;
        }

        // Whether a collider hides a creature depends only on what it belongs to, which stays the same while it exists, so
        // each collider is looked up once instead of on every ray of every frame; at a base a ray crosses many pieces.
        private static readonly Dictionary<int, bool> BlockingColliders = new Dictionary<int, bool>();

        private static bool Blocks(Collider collider)
        {
            int id = collider.GetInstanceID();
            bool blocks;
            if (BlockingColliders.Count > 20000) BlockingColliders.Clear();
            if (collider.GetComponentInParent<Heightmap>()) blocks = true;
            else if (collider.GetComponentInParent<Piece>() || collider.GetComponentInParent<Plant>() || collider.GetComponentInParent<Pickable>()) blocks = false;
            else
            {
                var destructible = collider.GetComponentInParent<IDestructible>();
                blocks = destructible == null || (destructible.GetDestructibleType() & DestructibleType.Tree) == 0;
            }
            BlockingColliders[id] = blocks;
            return blocks;
        }

        private static void Prefix(EnemyHud __instance, Player player, Sadle sadle, IDictionary ___m_huds)
        {
            var camera = Utils.GetMainCamera();
            if (!player || player.IsDead() || !camera) return;
            VisitBars(__instance, player, sadle, camera.transform.position, ___m_huds);
        }

        private static void Visit<THud>(EnemyHud __instance, Player player, Sadle sadle, Vector3 viewpoint, IDictionary bars) where THud : class
        {
            var playerId = player.GetZDOID();
            var hovered = player.GetHoverCreature();
            var mount = sadle ? sadle.GetCharacter() : null;
            foreach (var bar in (Dictionary<Character, THud>)bars)
            {
                var character = bar.Key;
                var hud = bar.Value;
                if (RecentHitHealthBars.Revealed(character) || RecentHitHealthBars.OwnSummon(character))
                {
                    HoverTimer(hud) = 0f;
                    continue;
                }
                if (!character || character.IsPlayer() || character.IsBoss() || character.m_hideHud || character.IsDead() ||
                    FreshCreatureSpawns.Waiting(character)) continue;
                var ai = character.GetBaseAI();
                if (ai && !ai.IsSleeping() && ((ai.IsAlerted() && BaseAI.IsEnemy(player, character)) || CreatureHover.KnownAnimal(character)))
                {
                    // The game zeroes the timer of the creature under the crosshair whenever it keeps that bar, so its rays
                    // would decide nothing.
                    if (character == hovered && character != mount && TestShow(__instance, character, true)) continue;
                    var data = GameFields.CharacterView(character).GetZDO();
                    bool attacking = !playerId.IsNone() && data != null && data.GetZDOID(CombatTargets.TargetKey) == playerId &&
                        Vector3.Distance(player.transform.position, character.transform.position) <= Plugin.AttackerHealthBarRange.Value;
                    // Hidden only if both rays are blocked. The eye, above the body, is seen more often, so its ray goes first.
                    bool obstructed = !attacking && Obstructed(viewpoint, character.GetEyePoint()) && Obstructed(viewpoint, character.GetCenterPoint());
                    HoverTimer(hud) = obstructed ? __instance.m_hoverShowDuration : 0f;
                }
            }
        }

        private static void Postfix(Player player, IDictionary ___m_huds)
        {
            // Most frames hover no body.
            var character = CreatureHover.BodyHover;
            if (!character) return;
            var camera = Utils.GetMainCamera();
            if (!player || player.IsDead() || player.GetDoodadController() != null || !camera || character.IsDead() ||
                character.IsBoss() || character.m_hideHud || FreshCreatureSpawns.Waiting(character) ||
                player.GetHoverCreature() != character || Vector3.Distance(player.transform.position, character.transform.position) >= EnemyHud.instance.m_maxShowDistance ||
                !___m_huds.Contains(character)) return;
            // Close body targeting can put the normal top point outside view or on the interaction text.
            // Keep that animal's bar above the crosshair and its pet/rename prompt.
            var display = Display(___m_huds[character]);
            display.transform.position = camera.WorldToScreenPointScaled(camera.transform.position + camera.transform.forward * 2f + camera.transform.up * 0.15f);
            display.SetActive(true);
        }
    }

    // Valheim synchronizes only whether a creature has a target. The AI's owner also records which one, beside that
    // flag on every target update, so each client can tell an enemy attacking it from one attacking another player
    // or a tame. A lost target is recorded as ZDOID.None, clearing the value as the native flag clears.
    [HarmonyPatch(typeof(BaseAI), "SetTargetInfo")]
    internal static class CombatTargets
    {
        internal static readonly KeyValuePair<int, int> TargetKey = ZDO.GetHashZDOID(Plugin.Id + ".combat-target");

        private static void Postfix(ZNetView ___m_nview, ZDOID targetID)
        {
            var data = ___m_nview.GetZDO();
            if (data.GetZDOID(TargetKey) != targetID) data.Set(TargetKey, targetID);
        }
    }
}
