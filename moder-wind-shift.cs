using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game turns the wind one frame-sized step at a time and ignores a new direction during a running shift, so Moder's
    // power could take 40 seconds to reach a ship. Only the speed changes: the wind still turns smoothly and re-aims when a shift ends.
    internal static class ModerWindShift
    {
        private const float Speed = 5f;

        internal static void Initialize(IntegrationPatches harmony) =>
            harmony.Patch(AccessTools.Method(typeof(EnvMan), "UpdateWindTransition"), prefix: new HarmonyMethod(typeof(ModerWindShift), nameof(Faster)));

        private static void Faster(ref float dt)
        {
            var ship = Ship.GetLocalShip();
            if (ship && ship.IsWindControllActive()) dt *= Speed;
        }
    }
}
