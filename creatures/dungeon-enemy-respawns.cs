using HarmonyLib;

namespace EpicLootProgressionFix
{
    [HarmonyPatch(typeof(CreatureSpawner), "Awake")]
    internal static class DungeonEnemyRespawns
    {
        [HarmonyPostfix]
        private static void OnLoad(CreatureSpawner __instance, ZNetView ___m_nview)
        {
            if (!DungeonInteriors.Enabled || ___m_nview.GetZDO() == null ||
                __instance.transform.position.y <= DungeonInteriors.InteriorHeight ||
                __instance.m_creaturePrefab.GetComponent<BaseAI>() == null) return;

            // Native one-shot mode keeps its saved Spawned connection after death and across reloads. The weekly
            // rebuild creates fresh points without that connection. Change this instance, never its shared prefab.
            __instance.m_respawnTimeMinuts = 0f;
        }
    }
}
