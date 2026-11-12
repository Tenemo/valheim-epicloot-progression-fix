using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class MooseAntlers
    {
        internal const string VariantKey = "valheim.epiclootprogressionfix.moose-antlers";

        internal static void Initialize(IntegrationPatches harmony)
        {
            if (!StarLevelSystem.Installed) return;
            var assembly = StarLevelSystem.Assembly;
            harmony.Patch(AccessTools.Method(assembly.GetType("StarLevelSystem.modules.Colorization", true), "ApplyLevelVisual"),
                prefix: new HarmonyMethod(typeof(MooseAntlers), nameof(UseSavedAntlers)));
            harmony.Patch(AccessTools.Method(assembly.GetType("StarLevelSystem.modules.CreatureSetup.CreatureSetupQueue", true), "AwaitsOwnerPass"),
                postfix: new HarmonyMethod(typeof(MooseAntlers), nameof(AwaitAppearance)));
            harmony.Patch(AccessTools.Method(assembly.GetType("StarLevelSystem.modules.CreatureSetup.CreatureSetupControl", true), "RunCharacterSetup"),
                postfix: new HarmonyMethod(typeof(MooseAntlers), nameof(KeepHornless)));
        }

        private static bool Adult(Character character) => character && Utils.GetPrefabName(character.gameObject) == "Moose";

        private static void AwaitAppearance(Character chara, ZDO zdo, ref bool __result)
        {
            if (Adult(chara) && zdo.GetInt(ZDOVars.s_level, chara.GetLevel()) > 1 && zdo.GetInt(VariantKey) == 0)
                __result = true;
        }

        private static bool UseSavedAntlers(Character charc)
        {
            if (!Adult(charc)) return true;
            var view = charc.GetComponent<ZNetView>();
            var data = view.GetZDO();
            int variant = data.GetInt(VariantKey);
            if (charc.GetLevel() > 1 && variant == 0 && view.IsOwner())
            {
                variant = Random.Range(1, 3);
                data.Set(VariantKey, variant);
            }
            SetModels(charc, charc.GetLevel() > 1 ? variant : 0);
            return false;
        }

        private static void KeepHornless(Character __0, bool __result)
        {
            if (__result && Adult(__0) && __0.GetLevel() <= 1) SetModels(__0, 0);
        }

        private static void SetModels(Character character, int variant)
        {
            var levels = character.GetComponentInChildren<LevelEffects>(true).m_levelSetups;
            for (int index = 0; index < levels.Count; index++)
                if (levels[index].m_enableObject) levels[index].m_enableObject.SetActive(index + 1 == variant);
        }
    }
}
