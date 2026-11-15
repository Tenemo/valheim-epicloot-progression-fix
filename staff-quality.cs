using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Magic staves grow with each quality level of the staff that cast them. Each value is set on the cast's own copy of an
    // effect before it acts, or on the staff's own damage and burst, so the shared prefabs keep the game's values and every
    // other user of them is unchanged.
    [HarmonyPatch]
    internal static class StaffQuality
    {
        private const string TrollQualityKey = Plugin.Id + ".trollstav-quality";
        private const string DundrBolt = "staff_lightning_projectile";
        internal const string FrostStaff = "StaffIceShards";
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

        // Echo Spike and Staff of Frost deal a sixth more damage per upgrade, 1.5 times at quality 4, as Dundr gains a sixth more
        // shots.
        internal static float UpgradeDamage(int quality) => 1f + Upgrades(quality) / 6f;

        // Staff of Frost's stream narrows by a quarter of its spread per upgrade, closing in on perfect aim without reaching it:
        // 5 degrees at quality 1, 2.9 at 4 and 1.3 at 12.
        internal static bool IsFrost(ItemDrop.ItemData item) => item?.m_dropPrefab?.name == FrostStaff;
        internal static float FrostSpread(float spread, int quality) => spread / (1f + .25f * Upgrades(quality));

        // Staff of Embers' fireball explodes 0.5 m wider per upgrade: 3 m at quality 1, 4.5 m at 4.
        private static float EmbersBlast(float radius, int quality) => radius + .5f * Upgrades(quality);

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
                    foreach (var area in root.GetComponentsInChildren<Aoe>(true)) area.m_damage.Modify(UpgradeDamage(item.m_quality));
                    break;
                // The Trollstav's landing blast reaches 5 m further per upgrade. Its owner is the summoned troll, which carries the
                // staff's quality because the blast may be released later, by whichever game finishes placing the troll.
                case "staff_redtroll_aoe":
                    var data = owner ? GameFields.CharacterView(owner)?.GetZDO() : null;
                    if (data != null) __instance.m_radius = 10f + 5f * Upgrades(data.GetInt(TrollQualityKey, 1));
                    break;
            }
        }

        // Tooltip rows for what each upgrade grows, at the quality the tooltip shows.
        internal static IEnumerable<string> TooltipRows(ItemDrop.ItemData item, int quality)
        {
            var projectile = item.m_shared.m_attack?.m_attackProjectile;
            if (!projectile) yield break;
            int upgrades = Upgrades(quality);
            if (IsFrost(item)) yield return "Aim spread: " + Number(FrostSpread(item.m_shared.m_attack.m_projectileAccuracy, quality)) + "°";
            switch (projectile.name)
            {
                case "staff_shield_aoe":
                case "staff_FrostOrbs_aoe":
                    yield return "Reaches allies within: " + 5 * Mathf.Max(1, quality) + " m";
                    break;
                case "staff_fireball_projectile":
                    yield return "Blast radius: " + Number(EmbersBlast(projectile.GetComponent<Projectile>().m_aoe, quality)) + " m";
                    break;
                case "staff_thunderblood_projectile":
                    var burst = Chaining(projectile.GetComponent<Projectile>().m_spawnOnHit);
                    if (!burst) break;
                    yield return "Blast radius: " + Number(burst.m_radius + .5f * upgrades) + " m";
                    yield return "Chains to: up to " + (burst.m_chainMaxTargets + upgrades) + " enemies";
                    break;
                case "staff_redtroll_spawn":
                    yield return "Landing blast radius: " + (10 + 5 * upgrades) + " m";
                    break;
                case "staff_OrbofAhri_projectile":
                    float factor = UpgradeDamage(quality);
                    var back = projectile.GetComponent<Projectile>().m_spawnOnHit?.GetComponent<Projectile>();
                    if (back) yield return "Returning spike: " + Damage(back.m_damage, factor);
                    // The burst's blast, and its lightning rod: a column 0.4 m wide where the spike lands, so it strikes only
                    // the target the spike hits.
                    var areas = ZNetScene.instance ? ZNetScene.instance.GetPrefab("staff_OrbofAhri_aoe")?.GetComponentsInChildren<Aoe>(true) : null;
                    var blast = areas?.FirstOrDefault(area => !area.m_useCollider);
                    var rod = areas?.FirstOrDefault(area => area.m_useCollider);
                    if (blast) yield return "Final burst: " + Damage(blast.m_damage, factor) + (rod ? ", plus " + Damage(rod.m_damage, factor) + " to the target the spike hits" : "");
                    break;
            }
        }

        private static string Number(float value) => value.ToString("0.#", CultureInfo.CurrentCulture);

        private static string Damage(HitData.DamageTypes damage, float factor)
        {
            var parts = new List<string>();
            void Add(float value, string name) { if (value > 0) parts.Add(Mathf.RoundToInt(value * factor) + " " + name); }
            Add(damage.m_damage, "damage"); Add(damage.m_blunt, "blunt"); Add(damage.m_slash, "slash"); Add(damage.m_pierce, "pierce");
            Add(damage.m_fire, "fire"); Add(damage.m_frost, "frost"); Add(damage.m_lightning, "lightning"); Add(damage.m_poison, "poison"); Add(damage.m_spirit, "spirit");
            return string.Join(", ", parts);
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
                case "staff_fireball_projectile":
                    if (Fires(item, "staff_fireball_projectile")) __instance.m_aoe = EmbersBlast(__instance.m_aoe, item.m_quality);
                    break;
                // Echo Spike's spike, out and back.
                case "staff_OrbofAhri_projectile":
                case "staff_OrbofAhri_projectile_return":
                    if (Fires(item, "staff_OrbofAhri_projectile")) __instance.m_damage.Modify(UpgradeDamage(item.m_quality));
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
            // The two summonings aren't networked: Staff of the Wild's projectile spawns its roots, and the Trollstav's attack is its spawn.
            var roots = __instance.GetPrefab("staff_greenroots_projectile")?.GetComponent<Projectile>()?.m_spawnOnHit;
            if (!roots || !IsRoots(roots.GetComponent<SpawnAbility>())) throw new InvalidOperationException("Staff of the Wild's roots are missing.");
            var troll = __instance.GetPrefab("StaffRedTroll")?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_attack.m_attackProjectile;
            if (!troll || !troll.GetComponent<SpawnAbility>() || Utils.GetPrefabName(troll) != "staff_redtroll_spawn") throw new InvalidOperationException("The Trollstav's summoning is missing.");
            foreach (string name in new[] { "staff_fireball_projectile", "staff_OrbofAhri_projectile", "staff_OrbofAhri_projectile_return", DundrBolt })
                Require<Projectile>(name);
            if (!__instance.GetPrefab(FrostStaff)?.GetComponent<ItemDrop>()) throw new InvalidOperationException("Staff of Frost is missing.");
        }

        // Staff of Frost's own damage, before EpicLoot adds its bonuses, so tooltips and every shard show it.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), typeof(int), typeof(float)), HarmonyPostfix, HarmonyPriority(Priority.First)]
        private static void ScaleFrostDamage(ItemDrop.ItemData __instance, int __0, ref HitData.DamageTypes __result)
        {
            if (IsFrost(__instance)) __result.Modify(UpgradeDamage(__0));
        }

        // Set after EpicLoot's Double Cast sets the burst's spread, so doubled shards keep the staff's own spread too.
        [HarmonyPatch(typeof(Attack), "FireProjectileBurst"), HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void NarrowFrost(Attack __instance, ItemDrop.ItemData ___m_weapon)
        {
            if (IsFrost(___m_weapon)) __instance.m_projectileAccuracy = FrostSpread(___m_weapon.m_shared.m_attack.m_projectileAccuracy, ___m_weapon.m_quality);
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
