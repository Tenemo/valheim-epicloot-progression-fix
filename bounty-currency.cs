using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Pay once at death, outside both native and EpicLoot rolls, including when a ragdoll holds the other loot.
    [HarmonyPatch(typeof(CharacterDrop), "OnDeath")]
    internal static class BountyCurrency
    {
        private static readonly int PaidKey = (Plugin.Id + ".bounty-currency-paid").GetStableHashCode();

        internal static (int Coins, int Iron, int Gold) Amounts(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return (25, 1, 0);
                case Heightmap.Biome.BlackForest: return (50, 2, 0);
                case Heightmap.Biome.Swamp: return (100, 2, 1);
                case Heightmap.Biome.Mountain: return (175, 3, 1);
                case Heightmap.Biome.Plains: return (250, 3, 2);
                case Heightmap.Biome.Ocean: return (250, 3, 2);
                case Heightmap.Biome.Mistlands: return (400, 4, 2);
                case Heightmap.Biome.AshLands: return (550, 4, 3);
                case Heightmap.Biome.DeepNorth: return (700, 5, 4);
                default: return (0, 0, 0);
            }
        }

        private static void Prefix(CharacterDrop __instance)
        {
            var character = __instance.GetComponent<Character>();
            if (!character || character.IsPlayer() || character.IsTamed() || !character.IsDead()) return;
            var view = character.GetComponent<ZNetView>();
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            var data = view.GetZDO();
            if (data.GetBool(PaidKey)) return;
            Heightmap.Biome biome;
            if (BountyCreature.IsMainTarget(data))
            {
                if (!BountyCreature.TryRead(data, out var bounty)) return;
                biome = bounty.Biome;
            }
            // A Nemesis miniboss pays as a bounty target of the biome it appeared in, where the game saved its spawn point.
            else if (NemesisMinibosses.Is(data)) biome = WorldGenerator.instance.GetBiome(data.GetVec3(ZDOVars.s_spawnPoint, character.transform.position));
            else return;
            var reward = Amounts(biome);
            if (reward.Coins == 0) return;
            data.Set(PaidKey, true);
            var position = character.GetCenterPoint();
            Drop("Coins", reward.Coins, position, __instance.m_cheated);
            Drop("IronBountyToken", reward.Iron, position + Vector3.right * .4f, __instance.m_cheated);
            Drop("GoldBountyToken", reward.Gold, position + Vector3.left * .4f, __instance.m_cheated);
        }

        private static void Drop(string name, int amount, Vector3 position, bool cheated)
        {
            if (amount == 0) return;
            var prefab = ObjectDB.instance.GetItemPrefab(name);
            var item = prefab.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = prefab;
            item.m_cheated = cheated;
            item.m_pickedUp = false;
            item.m_worldLevel = Game.m_worldLevel;
            while (amount > 0)
            {
                int count = Mathf.Min(amount, item.m_shared.m_maxStackSize);
                ItemDrop.DropItem(item, count, position, Quaternion.identity);
                amount -= count;
            }
        }
    }
}
