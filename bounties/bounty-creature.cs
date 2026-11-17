using System;
using EpicLoot.Adventure;

namespace EpicLootProgressionFix
{
    // The bounty data EpicLoot saves on a bounty's creatures, under the names of BountyTargetComponent's internal constants.
    internal static class BountyCreature
    {
        internal static readonly int IdKey = "BountyID".GetStableHashCode();
        internal static readonly int DataKey = "BountyData".GetStableHashCode();
        internal static readonly int IsAddKey = "IsAdd".GetStableHashCode();

        // The bounty's target, not one of its adds.
        internal static bool IsMainTarget(ZDO zdo) => zdo.GetString(IdKey).Length > 0 && !zdo.GetBool(IsAddKey);

        // EpicLoot reports and removes a creature whose bounty it cannot read.
        internal static bool TryRead(ZDO zdo, out BountyInfo bounty)
        {
            bounty = null;
            string data = zdo.GetString(DataKey);
            if (data.Length == 0) return false;
            try { bounty = BountyInfo.FromPackage(new ZPackage(data)); }
            catch (Exception) { return false; }
            return true;
        }
    }
}
