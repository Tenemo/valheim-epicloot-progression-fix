using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Vanish clears a creature's target, but a hunting creature knows where every player is and picked the same player again
    // at its next target update, a second or so later. Holds that update back for as long as the player stays hidden, also for
    // a hunter that aims at nothing just then, as one attacking a structure does; a hunter fighting something else carries on.
    // EpicLoot's own message names no duration, so this one goes out just before it, while the creatures still hold their target.
    internal static class VanishHunters
    {
        private const string HoldRpc = Plugin.Id + ".vanish-hunters";
        private static readonly AccessTools.FieldRef<MonsterAI, float> TargetUpdateTime = AccessTools.FieldRefAccess<MonsterAI, float>("m_updateTargetTimer");

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(VanishHunters);
            harmony.Patch(AccessTools.Method(typeof(Vanish), "StartHiding"), prefix: new HarmonyMethod(self, nameof(AnnounceHiding)));
            harmony.Patch(AccessTools.Method(typeof(Player), "Awake"), postfix: new HarmonyMethod(self, nameof(RegisterHold)));
        }

        private static void RegisterHold(Player __instance) =>
            GameFields.CharacterView(__instance)?.Register<Vector3, float, float>(HoldRpc, (sender, position, radius, seconds) => HoldHunters(__instance, position, radius, seconds));

        private static void AnnounceHiding(Player player, float seconds, float radius)
        {
            var view = GameFields.CharacterView(player);
            if (view && view.IsValid()) view.InvokeRPC(ZNetView.Everybody, HoldRpc, player.transform.position, radius, seconds);
        }

        private static void HoldHunters(Player vanished, Vector3 position, float radius, float seconds)
        {
            foreach (var ai in BaseAI.BaseAIInstances)
            {
                if (!(ai is MonsterAI monster) || !monster.HuntPlayer() || (ai.transform.position - position).sqrMagnitude > radius * radius) continue;
                var target = monster.GetTargetCreature();
                if (target && target != vanished) continue;
                var view = ai.GetComponent<ZNetView>();
                if (view && view.IsValid() && view.IsOwner()) TargetUpdateTime(monster) = seconds;
            }
        }
    }
}
