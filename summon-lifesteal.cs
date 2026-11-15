using EpicLoot;
using EpicLoot.MagicItemEffects;
using EpicLoot.src.Magic.MagicItemEffects.Helpers;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot's Lifesteal heals only from the player's own hits, so a summoner holding a Blood Magic staff never healed from
    // Hel's Covenant's set Lifesteal: the staff deals no hits, and a summon's hit names the summon as its attacker. While the
    // summoner holds a Blood Magic staff, each hit one of their summons deals now heals them by their Lifesteal, worked out
    // from their own gear in their own game, since other games don't know what they wear.
    internal static class SummonLifesteal
    {
        private const string HealRpc = "EpicLootProgressionFix_SummonLifesteal";

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(SummonLifesteal);
            harmony.Patch(AccessTools.Method(typeof(Character), nameof(Character.Damage)), postfix: new HarmonyMethod(self, nameof(AfterHit)));
            harmony.Patch(AccessTools.Constructor(typeof(ZRoutedRpc), new[] { typeof(bool) }), postfix: new HarmonyMethod(self, nameof(Register)));
        }

        private static void Register(ZRoutedRpc __instance) => __instance.Register<long, float>(HealRpc, Receive);

        // The game that runs the summon calls Damage, like the player's own game does for the player's hits.
        private static void AfterHit(Character __instance, HitData hit)
        {
            var summon = hit?.GetAttacker();
            if (!summon || summon == __instance || summon.IsPlayer() || HitSource.IsBonusHit) return;
            long summoner = SummonOwnership.SummonerId(summon);
            if (summoner == 0) return;
            // EpicLoot's Lifesteal leaves out tree and rock damage the same way.
            float damage = hit.m_damage.GetTotalDamage() - hit.m_damage.m_chop - hit.m_damage.m_pickaxe;
            if (damage <= 0f) return;
            var local = Player.m_localPlayer;
            if (local && local.GetPlayerID() == summoner) Heal(local, damage);
            else if (Player.GetPlayer(summoner) is Player remote && GameFields.CharacterView(remote) is ZNetView view && view.IsValid())
                ZRoutedRpc.instance.InvokeRoutedRPC(view.GetZDO().GetOwner(), HealRpc, summoner, damage);
        }

        private static void Receive(long sender, long summoner, float damage)
        {
            var player = Player.m_localPlayer;
            if (player && player.GetPlayerID() == summoner) Heal(player, damage);
        }

        private static void Heal(Player player, float damage)
        {
            var weapon = MagicEffectsHelper.GetActiveWeapon(player);
            if (weapon?.m_shared.m_skillType != Skills.SkillType.BloodMagic) return;
            float lifesteal = 0f;
            ModifyWithLowHealth.Apply(player, MagicEffectType.LifeSteal,
                effect => lifesteal += MagicEffectsHelper.GetTotalActiveMagicEffectValueForWeapon(player, weapon, effect, .01f));
            if (lifesteal > 0f) player.Heal(damage * lifesteal);
        }
    }
}
