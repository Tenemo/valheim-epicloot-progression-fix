using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class LastLod
    {
        // An object's last LOD normally culls its mesh once it looks small enough on screen. Keep that last mesh until the
        // object unloads, retaining any earlier switches in model detail.
        internal static void Keep(Component root)
        {
            foreach (var group in root.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                if (levels.Length == 0) continue;
                levels[levels.Length - 1].screenRelativeTransitionHeight = 0f;
                group.SetLODs(levels);
            }
        }
    }
}
