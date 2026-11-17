using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A door's animator starts in its prefab's pose whenever the door loads, closed for nearly every door, so the game's
    // first state check finds a door saved open, or opened the other way, "opening" and plays its opening sound. Open doors,
    // such as the drawbridges inside Morkhalla, played it each time their area loaded as a player came near. The first check
    // now gives the animator the saved state before the game compares them, so the sound plays only when someone opens or
    // closes the door; the door still swings to its saved state as natively.
    [HarmonyPatch(typeof(Door), "UpdateState")]
    internal static class LoadedDoorSounds
    {
        private static readonly int State = Animator.StringToHash("state");

        private static void Prefix(ZNetView ___m_nview, Animator ___m_animator, uint ___m_lastDataRevision)
        {
            if (___m_lastDataRevision != uint.MaxValue || !___m_nview.IsValid() || !___m_animator) return;
            ___m_animator.SetInteger(State, ___m_nview.GetZDO().GetInt(ZDOVars.s_state));
        }
    }
}
