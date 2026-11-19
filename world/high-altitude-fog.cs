using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EpicLootProgressionFix
{
    // The game picks the weather from the camera's place on the map alone and draws its fog as one density everywhere, so
    // high above a swamp in rain the fog closes in as thickly as on the ground and nothing below can be seen. From ClearFrom
    // above whatever lies under the camera and the player, buildings and the sea included, the fog thins towards the game's own clear
    // weather at the same hour and reaches it at ClearAt. Fog lighter than clear weather's stays as it is, and everything
    // else the weather does, its rain, light, clouds, sound and effects on players, is unchanged. Only rendering reads the
    // fog density. Dungeons, which the game places above 3000 m, keep their own fog.
    internal static class HighAltitudeFog
    {
        internal const float ClearFrom = 40f, ClearAt = 200f;
        // Height is measured a few times a second, and the fog follows over about a second so passing a hill does not flicker.
        private const float Interval = 0.25f, FadeSeconds = 1f, Interior = 3000f;
        private static readonly Func<EnvMan, string, EnvSetup> weatherNamed =
            AccessTools.MethodDelegate<Func<EnvMan, string, EnvSetup>>(AccessTools.Method(typeof(EnvMan), "GetEnv", new[] { typeof(string) }));
        private static EnvMan clearOwner;
        private static EnvSetup clear;
        private static float nextCheck, lastTime, target;
        internal static float Height { get; private set; }
        internal static float Clearing { get; private set; }

        internal static void Initialize(IntegrationPatches patches)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            patches.Patch(AccessTools.Method(typeof(EnvMan), "SetEnv"), postfix: new HarmonyMethod(typeof(HighAltitudeFog), nameof(Thin)));
        }

        // The game sets the density afresh on every call, so this scales its own value and never compounds.
        private static void Thin(EnvMan __instance, float dayInt, float nightInt, float morningInt, float eveningInt)
        {
            var camera = Utils.GetMainCamera();
            if (!camera) return;
            float now = Time.time, elapsed = Mathf.Clamp(now - lastTime, 0f, FadeSeconds);
            lastTime = now;
            if (now >= nextCheck || now < nextCheck - Interval)
            {
                nextCheck = now + Interval;
                // The lower of the camera's and the player's heights, so a camera out over a cliff edge doesn't count.
                var player = Player.m_localPlayer;
                Height = HeightAboveGround(camera.transform.position);
                if (player) Height = Mathf.Min(Height, HeightAboveGround(player.transform.position));
                target = Mathf.InverseLerp(ClearFrom, ClearAt, Height);
            }
            Clearing = Mathf.MoveTowards(Clearing, target, elapsed / FadeSeconds);
            if (Clearing <= 0f) return;
            if (clearOwner != __instance)
            {
                clearOwner = __instance;
                clear = weatherNamed(__instance, "Clear");
            }
            if (clear == null) return;
            float clearDensity = clear.m_fogDensityDay * dayInt + clear.m_fogDensityNight * nightInt
                + clear.m_fogDensityMorning * morningInt + clear.m_fogDensityEvening * eveningInt;
            float density = RenderSettings.fogDensity;
            if (clearDensity < density) RenderSettings.fogDensity = Mathf.Lerp(density, clearDensity, Clearing);
        }

        // Height above the highest solid surface or the sea below; under a roof, in a dungeon or before the world has loaded it is 0.
        private static float HeightAboveGround(Vector3 position)
        {
            if (position.y > Interior || !ZoneSystem.instance) return 0f;
            float ground = ZoneSystem.instance.GetSolidHeight(position, out float solid)
                ? solid
                : WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(position.x, position.z) : position.y;
            return Mathf.Max(0f, position.y - Mathf.Max(ground, ZoneSystem.instance.m_waterLevel));
        }
    }
}
