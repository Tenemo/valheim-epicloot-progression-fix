using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A wild Hexen's magic blast reaches through walls and its lightning bolts hit everything within 3 m every half second
    // of their flight, so one Hexen near a base tore down buildings and killed tames. Its blast and every bolt pulse now
    // deal 10% to buildings and 25% to ordinary tames; summons, wild creatures and players take full damage. No creature
    // takes its fourfold surprise damage from them. The game that makes a hit changes it once, before it is sent, so the
    // owner of the target never changes it again, and a bolt keeps its source after the Hexen that cast it dies. A player in
    // the Jotun Witch form casts the same attacks: they deal the same 10% to buildings, so her own base survives them, and
    // only reach her enemies.
    internal static class HexenCollateralDamage
    {
        internal const float BuildingFactor = .1f, TameFactor = .25f;
        private const string WitchWeapon = "JotunWitch_attack_";
        private const string BoltKey = Plugin.Id + ".hexen-bolt";
        private static readonly int Hexen = "JotunWitch".GetStableHashCode();
        private static readonly ConditionalWeakTable<Projectile, object> Bolts = new ConditionalWeakTable<Projectile, object>();
        private static readonly object Marker = new object();
        private static readonly AccessTools.FieldRef<Projectile, ZNetView> ProjectileView = AccessTools.FieldRefAccess<Projectile, ZNetView>("m_nview");
        // Set while a Hexen's attack or bolt deals its hits; only the outermost damage call in it is one of those hits.
        private static int attacks, depth;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var type = typeof(HexenCollateralDamage);
            harmony.Patch(AccessTools.Method(typeof(Projectile), nameof(Projectile.Setup)), postfix: new HarmonyMethod(type, nameof(MarkBolt)));
            foreach (var method in new[] { AccessTools.Method(typeof(Projectile), "DoAOE"), AccessTools.Method(typeof(Projectile), nameof(Projectile.OnHit)) })
                harmony.Patch(method, prefix: new HarmonyMethod(type, nameof(BoltHits)), finalizer: new HarmonyMethod(type, nameof(AttackEnded)));
            harmony.Patch(AccessTools.Method(typeof(Attack), "DoMeleeAttack"), prefix: new HarmonyMethod(type, nameof(BlastHits)), finalizer: new HarmonyMethod(type, nameof(AttackEnded)));
            foreach (var method in new[] { AccessTools.Method(typeof(Character), nameof(Character.Damage)), AccessTools.Method(typeof(WearNTear), nameof(WearNTear.Damage)) })
                harmony.Patch(method, prefix: new HarmonyMethod(type, nameof(Reduce)) { priority = Priority.Last }, finalizer: new HarmonyMethod(type, nameof(DamageEnded)));
        }

        internal static bool IsWildHexen(Character character) =>
            character && !character.IsPlayer() && !character.IsTamed() && GameFields.CharacterView(character)?.GetZDO()?.GetPrefab() == Hexen;

        private static bool IsHexenAttack(Character attacker, ItemDrop.ItemData weapon) => IsWildHexen(attacker) ||
            (attacker is Player && weapon?.m_dropPrefab && weapon.m_dropPrefab.name.StartsWith(WitchWeapon, StringComparison.Ordinal));

        private static void MarkBolt(Projectile __instance, Character owner, ItemDrop.ItemData item)
        {
            if (!IsHexenAttack(owner, item)) return;
            Bolts.Remove(__instance); Bolts.Add(__instance, Marker);
            var view = ProjectileView(__instance);
            if (view && view.IsValid() && view.IsOwner()) view.GetZDO().Set(BoltKey, true);
        }

        private static bool IsBolt(Projectile projectile)
        {
            if (Bolts.TryGetValue(projectile, out _)) return true;
            var view = ProjectileView(projectile);
            return view && view.IsValid() && view.GetZDO().GetBool(BoltKey);
        }

        private static void BoltHits(Projectile __instance, out bool __state)
        {
            __state = IsBolt(__instance);
            if (__state) attacks++;
        }

        private static void BlastHits(Attack __instance, out bool __state)
        {
            __state = IsHexenAttack(GameFields.AttackCharacter(__instance), GameFields.AttackWeapon(__instance));
            if (__state) attacks++;
        }

        private static Exception AttackEnded(Exception __exception, bool __state)
        {
            if (__state) attacks--;
            return __exception;
        }

        private static bool Reduce(object __instance, HitData hit, bool __runOriginal, out bool __state)
        {
            __state = true;
            bool outermost = depth++ == 0;
            if (!__runOriginal || !outermost || attacks <= 0 || hit == null) return __runOriginal;
            var witch = hit.GetAttacker() as Player;
            if (__instance is WearNTear) hit.m_damage.Modify(BuildingFactor);
            else if (witch) return !SetEffectFriendlyFire.IsSpared(witch, __instance as Character);
            else if (__instance is Character target && !target.IsPlayer())
            {
                // Summons fight for a player and take full damage, though the game counts most of them as tamed.
                if (!SummonOwnership.IsPlayerSummon(target) && target.IsTamed()) hit.m_damage.Modify(TameFactor);
                hit.m_backstabBonus = 1f;
            }
            return true;
        }

        private static Exception DamageEnded(Exception __exception, bool __state)
        {
            if (__state) depth--;
            return __exception;
        }
    }
}
