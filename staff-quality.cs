using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Magic staves grow with each quality level of the staff that cast them. Each value is set on the cast's own copy of an
    // effect before it acts, so the shared prefabs keep the game's values and every other user of them is unchanged.
    [HarmonyPatch]
    internal static class StaffQuality
    {
        private const string TrollQualityKey = Plugin.Id + ".trollstav-quality";
        private const string DundrBolt = "staff_lightning_projectile";
        private static readonly ConditionalWeakTable<Attack, StrongBox<int>> SetBolts = new ConditionalWeakTable<Attack, StrongBox<int>>();
        private static readonly ConditionalWeakTable<GameObject, object> ScaledBursts = new ConditionalWeakTable<GameObject, object>();

        // Quality levels above the first.
        private static int Upgrades(int quality) => Mathf.Max(1, quality) - 1;

        // Dundr fires 2 more bolts per upgrade.
        internal static bool IsDundr(Attack attack) => attack.m_attackProjectile && attack.m_attackProjectile.name == DundrBolt;
        internal static int DundrBolts(int bolts, int quality) => bolts + 2 * Upgrades(quality);

        // Staff of the Wild keeps 2 more roots per upgrade.
        internal static bool IsRoots(SpawnAbility spawn) => spawn && Utils.GetPrefabName(spawn.gameObject) == "staff_greenroots_spawn";
        internal static int RootLimit(int limit, int quality) => limit + 2 * Upgrades(quality);

        // Echo Spike deals a sixth more damage per upgrade, 1.5 times at quality 4, as Dundr and Fracturing gain a sixth more shots.
        internal static float EchoSpikeDamage(int quality) => 1f + Upgrades(quality) / 6f;

        [HarmonyPatch(typeof(Aoe), nameof(Aoe.Setup)), HarmonyPostfix]
        private static void ScaleArea(Aoe __instance, Character owner, ItemDrop.ItemData item)
        {
            var root = __instance.transform.root.gameObject;
            switch (Utils.GetPrefabName(root))
            {
                // Staff of Protection and Northern Vengeance reach 5 m per quality level.
                case "staff_shield_aoe":
                case "staff_FrostOrbs_aoe":
                    if (item != null) __instance.m_radius = 5f * Mathf.Max(1, item.m_quality);
                    break;
                // Lightning Strike's burst reaches 0.5 m further and can chain to 1 more target per upgrade. The game sets up only
                // the first of the burst's two area effects, so the chaining one is found beside it.
                case "staff_thunderblood_aoe":
                    var burst = Chaining(root);
                    var original = Chaining(ZNetScene.instance.GetPrefab("staff_thunderblood_aoe"));
                    if (item == null || !burst || !original) break;
                    burst.m_radius = original.m_radius + .5f * Upgrades(item.m_quality);
                    burst.m_chainMaxTargets = original.m_chainMaxTargets + Upgrades(item.m_quality);
                    break;
                // Echo Spike's final burst. The game sets up only the first of its two area effects, so the burst is scaled whole, once.
                case "staff_OrbofAhri_aoe":
                    if (item == null || ScaledBursts.TryGetValue(root, out _)) break;
                    ScaledBursts.Add(root, root);
                    foreach (var area in root.GetComponentsInChildren<Aoe>(true)) area.m_damage.Modify(EchoSpikeDamage(item.m_quality));
                    break;
                // The Trollstav's landing blast reaches 5 m further per upgrade. Its owner is the summoned troll, which carries the
                // staff's quality because the blast may be released later, by whichever game finishes placing the troll.
                case "staff_redtroll_aoe":
                    var data = owner ? GameFields.CharacterView(owner)?.GetZDO() : null;
                    if (data != null) __instance.m_radius = 10f + 5f * Upgrades(data.GetInt(TrollQualityKey, 1));
                    break;
            }
        }

        private static Aoe Chaining(GameObject effect) => effect ? effect.GetComponentsInChildren<Aoe>(true).FirstOrDefault(area => area.m_chainObj) : null;

        [HarmonyPatch(typeof(SpawnAbility), "SetupAoe"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void RecordTrollQuality(SpawnAbility __instance, Character owner, ItemDrop.ItemData ___m_weapon)
        {
            if (___m_weapon == null || !owner || Utils.GetPrefabName(__instance.gameObject) != "staff_redtroll_spawn") return;
            var view = GameFields.CharacterView(owner);
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(TrollQualityKey, Mathf.Max(1, ___m_weapon.m_quality));
        }

        // Set before Setup starts the summoning, which checks the limit at once.
        [HarmonyPatch(typeof(SpawnAbility), nameof(SpawnAbility.Setup)), HarmonyPrefix]
        private static void ScaleRootLimit(SpawnAbility __instance, ItemDrop.ItemData item)
        {
            if (item != null && IsRoots(__instance)) __instance.m_maxSpawned = RootLimit(__instance.m_maxSpawned, item.m_quality);
        }

        // Only the staff that fires a projectile scales it: EpicLoot's Artillery set makes crossbows fire Staff of Embers'
        // fireball, which keeps the radius Artillery gives it.
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup)), HarmonyPostfix]
        private static void ScaleProjectile(Projectile __instance, ItemDrop.ItemData item)
        {
            if (item == null) return;
            switch (Utils.GetPrefabName(__instance.gameObject))
            {
                // Staff of Embers' impact area grows 1 m per upgrade.
                case "staff_fireball_projectile":
                    if (Fires(item, "staff_fireball_projectile")) __instance.m_aoe += Upgrades(item.m_quality);
                    break;
                // Staff of Fracturing bursts into 2 more splinters per upgrade.
                case "staff_clusterbombstaff_projectile":
                    if (Fires(item, "staff_clusterbombstaff_projectile")) __instance.m_spawnCount += 2 * Upgrades(item.m_quality);
                    break;
                // Echo Spike's spike, out and back.
                case "staff_OrbofAhri_projectile":
                case "staff_OrbofAhri_projectile_return":
                    if (Fires(item, "staff_OrbofAhri_projectile")) __instance.m_damage.Modify(EchoSpikeDamage(item.m_quality));
                    break;
            }
        }

        private static bool Fires(ItemDrop.ItemData item, string projectile) =>
            item.m_shared.m_attack?.m_attackProjectile?.name == projectile || item.m_shared.m_secondaryAttack?.m_attackProjectile?.name == projectile;

        // Every effect above is found by its prefab's name, so a renamed or missing one stops the game loading instead of
        // leaving its staff unscaled.
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void RequireEffects(ZNetScene __instance)
        {
            void Require<T>(string name) where T : Component
            {
                var prefab = __instance.GetPrefab(name);
                if (!prefab || !prefab.GetComponentInChildren<T>(true)) throw new InvalidOperationException("Staff effect " + name + " with its " + typeof(T).Name + " is missing.");
            }
            foreach (string name in new[] { "staff_shield_aoe", "staff_FrostOrbs_aoe", "staff_OrbofAhri_aoe", "staff_redtroll_aoe" }) Require<Aoe>(name);
            if (!Chaining(__instance.GetPrefab("staff_thunderblood_aoe"))) throw new InvalidOperationException("Lightning Strike's chaining burst is missing.");
            foreach (string name in new[] { "staff_greenroots_spawn", "staff_redtroll_spawn" }) Require<SpawnAbility>(name);
            foreach (string name in new[] { "staff_fireball_projectile", "staff_clusterbombstaff_projectile", "staff_OrbofAhri_projectile", "staff_OrbofAhri_projectile_return", DundrBolt })
                Require<Projectile>(name);
        }

        // EpicLoot's multishot sets the bolt count again as each burst fires, so the count is scaled last, once per value it sets.
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void ScaleBolts(Attack __instance, ItemDrop.ItemData ___m_weapon)
        {
            if (___m_weapon == null || !IsDundr(__instance)) return;
            var set = SetBolts.GetValue(__instance, _ => new StrongBox<int>(-1));
            if (set.Value == __instance.m_projectiles) return;
            int bolts = ___m_weapon.m_shared.m_attack.m_projectiles, count = __instance.m_projectiles;
            // A multishot fires whole volleys of the staff's bolts; each volley gains the extra bolts.
            __instance.m_projectiles = bolts > 0 && count % bolts == 0
                ? DundrBolts(bolts, ___m_weapon.m_quality) * (count / bolts)
                : DundrBolts(count, ___m_weapon.m_quality);
            set.Value = __instance.m_projectiles;
        }
    }
}
