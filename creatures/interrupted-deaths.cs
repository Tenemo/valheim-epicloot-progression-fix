using System;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The game marks a creature dead before OnDeath runs and checks it only once, and OnDeath removes it from the world
    // last, after its loot and the other mods' death hooks. When a hook throws first, the game that runs the creature keeps
    // it at 0 health for as long as that player stays nearby: it ignores hits and every player sees it, until another game
    // takes it over. Removing it here ends that, and the line names the creature; the exception still reaches the log.
    [HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
    internal static class InterruptedDeaths
    {
        private static Exception Finalizer(Character __instance, Exception __exception)
        {
            var view = __instance ? GameFields.CharacterView(__instance) : null;
            if (!view || !view.IsValid() || !view.IsOwner() || !__instance.IsDead() || __instance.GetHealth() > 0f) return __exception;
            Debug.LogWarning($"[EpicLoot Progression Fix] Removed {Utils.GetPrefabName(__instance.gameObject)} at {__instance.transform.position}: " +
                "its death stopped before it left the world" + (__exception != null ? $" ({__exception.GetType().Name}: {__exception.Message})." : "."));
            ZNetScene.instance.Destroy(__instance.gameObject);
            return __exception;
        }
    }
}
