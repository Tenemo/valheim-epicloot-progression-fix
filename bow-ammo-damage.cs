using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // A bow or crossbow shot deals the weapon's damage plus the arrow's or bolt's, but EpicLoot applies the weapon's
    // enchantments only to the weapon's part; the ammunition gets just the coin and dodge bonuses and shards. Give the
    // ammunition's part the weapon's whole EpicLoot treatment instead, in place of what it got, so each bonus counts
    // once on the whole shot.
    [HarmonyPatch]
    internal static class BowAmmoDamage
    {
        private delegate void EnchantmentModifier(Player player, ItemDrop.ItemData item, ref HitData.DamageTypes damage, float coinHoarderBonus);
        private delegate void ShardModifier(ItemDrop.ItemData item, ref HitData.DamageTypes damage);

        private static readonly EnchantmentModifier ApplyEnchantments =
            AccessTools.MethodDelegate<EnchantmentModifier>(AccessTools.Method(typeof(ModifyDamage), "ApplyMagicDamageModifiers"));
        private static readonly ShardModifier ApplyShards =
            AccessTools.MethodDelegate<ShardModifier>(AccessTools.Method(typeof(ModifyDamage), "ApplyShardWeaponModifiers"));
        private static readonly AccessTools.FieldRef<Attack, ItemDrop.ItemData> AmmoOf = AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_ammoItem");
        private static ItemDrop.ItemData firingWeapon, firingAmmo;
        private static HitData.DamageTypes? ammoDamage;

        // Reuse the actual burst's modifier path for a read-only ammunition contribution.
        internal static HitData.DamageTypes Preview(ItemDrop.ItemData weapon, ItemDrop.ItemData ammo)
        {
            if (ammo == weapon || ammo.IsMagic() || ammo.m_shared.m_attack.m_attackProjectile?.name == ConjuredArrows.ProjectileName)
                return ammo.GetDamage();
            var priorWeapon = firingWeapon; var priorAmmo = firingAmmo; var priorDamage = ammoDamage;
            firingWeapon = weapon; firingAmmo = ammo; ammoDamage = null;
            try { return ammo.GetDamage(); }
            finally { firingWeapon = priorWeapon; firingAmmo = priorAmmo; ammoDamage = priorDamage; }
        }

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst"), HarmonyPrefix]
        private static void Fire(Attack __instance)
        {
            var player = Player.m_localPlayer;
            var weapon = __instance.GetWeapon();
            var ammo = AmmoOf(__instance);
            if (player == null || GameFields.AttackCharacter(__instance) != player || weapon == null || ammo == null || ammo == weapon || ammo.IsMagic()) return;
            // Conjured arrows already derive their frost damage from the enchanted bow; their base damage is zero.
            if (ammo.m_shared.m_attack.m_attackProjectile?.name == ConjuredArrows.ProjectileName) return;
            firingWeapon = weapon;
            firingAmmo = ammo;
        }

        [HarmonyPatch(typeof(Attack), "FireProjectileBurst"), HarmonyFinalizer]
        private static void Fired()
        {
            firingWeapon = firingAmmo = null;
            ammoDamage = null;
        }

        // Runs before EpicLoot's postfix, the pack's only other change to item damage.
        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), new[] { typeof(int), typeof(float) })]
        [HarmonyPostfix, HarmonyPriority(Priority.First)]
        private static void KeepAmmoDamage(ItemDrop.ItemData __instance, HitData.DamageTypes __result)
        {
            if (__instance == firingAmmo && firingAmmo != null) ammoDamage = __result;
        }

        [HarmonyPatch(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.GetDamage), new[] { typeof(int), typeof(float) })]
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void UseWeaponModifiers(ItemDrop.ItemData __instance, ref HitData.DamageTypes __result)
        {
            if (__instance != firingAmmo || firingAmmo == null || !ammoDamage.HasValue) return;
            __result = ammoDamage.Value;
            ammoDamage = null;
            ApplyEnchantments(Player.m_localPlayer, firingWeapon, ref __result, CoinHoarder.GetDamageBonus(Player.m_localPlayer));
            ApplyShards(firingWeapon, ref __result);
        }
    }
}
