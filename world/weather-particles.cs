using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A weather change crossfades over ten seconds, and the game leaves the outgoing weather's particle systems emitting for
    // all of them: the blended setup is a copy of the outgoing one, so its particle systems only change once the fade is
    // done. Walking off a mountain therefore keeps snow falling at full rate while the fog, light and colours already belong
    // to the biome below, and the flakes in the air then need their own ten-second lifetime to fall out, about twenty seconds
    // of snow in all, restarted whenever the view passes over another mountain point. The blended setup already carries the
    // incoming weather's name, so it is given its particle systems as well: emission changes when the fade starts and the
    // snow thins out as the sky clears instead of after it. The fade itself, and everything else it blends, is unchanged.
    internal static class WeatherParticles
    {
        internal static void Initialize(IntegrationPatches patches)
        {
            patches.Patch(AccessTools.Method(typeof(EnvMan), "InterpolateEnvironment", new[] { typeof(EnvSetup), typeof(EnvSetup), typeof(float) }),
                postfix: new HarmonyMethod(typeof(WeatherParticles), nameof(CarryParticles)));
        }

        // The result is a fresh copy the game makes for this frame's blend, so the stored weather setups keep their own systems.
        private static void CarryParticles(EnvSetup __result, EnvSetup __1)
        {
            if (__result == null || __1 == null) return;
            __result.m_psystems = __1.m_psystems;
            __result.m_psystemsOutsideOnly = __1.m_psystemsOutsideOnly;
        }
    }
}
