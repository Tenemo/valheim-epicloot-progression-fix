using System.Reflection;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A corpse removes itself only in its owner's game, from four seconds after the death. Creatures stay with a player whose
    // game loads them far beyond the active area, but the ownership pass releases everything else there within two seconds,
    // so the corpse of a creature that dies out there lost its owner first and lay on, a deer's 11 bodies running physics and a
    // water query every fixed step in each game that loaded it, until a player came near. A game holding an ownerless corpse
    // past its time now asks the server for it, and the next check, a second later, removes it the way its owner would. The
    // server decides because the ownership sync ignores an equal revision, so two games taking a corpse in the same moment
    // would each go on believing they own it and both drop its loot.
    [HarmonyPatch(typeof(Ragdoll), "DestroyNow")]
    internal static class OwnerlessCorpses
    {
        private const string Claim = "EpicLootProgressionFix_ClaimCorpse";

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Ragdoll __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || view.GetZDO().HasOwner()) return true;
            ZRoutedRpc.instance.InvokeRoutedRPC(Claim, view.GetZDO().m_uid);
            return false;
        }

        [HarmonyPatch]
        private static class Registration
        {
            private static MethodBase TargetMethod() => AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) });
            private static void Postfix(ZRoutedRpc __instance) => __instance.Register<ZDOID>(Claim, Receive);
        }

        private static void Receive(long sender, ZDOID corpse)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var data = ZDOMan.instance.GetZDO(corpse);
            if (data != null && !data.HasOwner()) data.SetOwner(sender);
        }
    }
}
