using System;
using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class SealColors
    {
        private static readonly MethodInfo Copy = AccessTools.Method(typeof(object), "MemberwiseClone");
        private static PropertyInfo hue, saturation;

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var select = AccessTools.Method(StarLevelSystem.Assembly.GetType("StarLevelSystem.modules.Colorization"),
                "DetermineCharacterColorization", new[] { typeof(Character), typeof(int) });
            hue = AccessTools.Property(select?.ReturnType, "Hue");
            saturation = AccessTools.Property(select?.ReturnType, "Saturation");
            if (hue?.PropertyType != typeof(float) || saturation?.PropertyType != typeof(float))
                throw new InvalidOperationException("StarLevelSystem's creature color selection changed.");
            harmony.Patch(select, postfix: new HarmonyMethod(typeof(SealColors), nameof(KeepNaturalSkin)));
        }

        private static void KeepNaturalSkin(Character __0, ref object __result)
        {
            if (__0 == null || __result == null) return;
            string prefab = Utils.GetPrefabName(__0.gameObject);
            if (prefab != "Seal" && prefab != "Seal_Pup") return;

            // The grey skin turns into pastel blocks when the shader receives negative saturation.
            // Copy the shared palette before keeping native hue and saturation; star brightness stays.
            var palette = Copy.Invoke(__result, null);
            hue.SetValue(palette, 0f);
            saturation.SetValue(palette, 0f);
            __result = palette;
        }
    }
}
