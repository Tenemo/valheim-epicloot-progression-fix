using System;
using System.Linq;
using EpicLoot;
using EpicLoot.MagicItemEffects;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The Trollstav's troll stays untamed: it fights everything around it, follows no one and stays where it lands. The game
    // only refuses a cast while 2 trolls of anyone's are loaded, which SummonOwnership removes, and leaves the troll out of
    // Blood Magic training and the summon bonuses, which all need a tamed summon. The caster recorded on each troll gives it
    // those instead: each player keeps as many trolls as the staff's quality, newest first, and the troll trains its caster
    // and carries their summon bonuses as a Dead Raiser skeleton does. Staff of the Wild's roots aren't tamed either, so the game
    // holds them to no follower limit; each caster keeps as many roots as StaffQuality.RootFollowers allows, newest first.
    [HarmonyPatch]
    internal static class TrollstavSummons
    {
        internal const string Troll = "Troll_Summoned", Staff = "StaffRedTroll";
        private const string Spawn = "staff_redtroll_spawn", RootStaff = "StaffGreenRoots";
        private const string UnsummonRpc = "EpicLootProgressionFix_TrollUnsummon", SkillRpc = "EpicLootProgressionFix_TrollSkill";
        private static readonly int TrollHash = Troll.GetStableHashCode(), RootHash = "staff_greenroots_tentaroot".GetStableHashCode();
        private static EffectList unsummonEffect;
        private static Skills.SkillType casterSkill;
        private static float skillFactor;

        internal static bool Summons(SpawnAbility spawn) => spawn && Utils.GetPrefabName(spawn.gameObject) == Spawn;
        internal static bool IsTroll(ZDO data) => data != null && data.GetPrefab() == TrollHash;
        // The summonings whose limit is kept here, per caster, instead of by the game.
        internal static bool LimitsPerCaster(SpawnAbility spawn) => Summons(spawn) || StaffQuality.IsRoots(spawn);
        // The staff a limited summon counts against, and how its notice names the staff and the summon.
        private static (string Staff, string Name, string Kind)? Limited(ZDO data) =>
            IsTroll(data) ? (Staff, "Trollstav", "troll") : data != null && data.GetPrefab() == RootHash ? (RootStaff, "Staff of the Wild", "root") : null;

        // The limit, the training rate and the unsummoning all copy the game's own values, so a changed troll or skeleton stops
        // the game loading instead of leaving the troll without them.
        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void ReadDeadRaiser(ZNetScene __instance)
        {
            var spawn = __instance.GetPrefab(Staff)?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_attack.m_attackProjectile?.GetComponent<SpawnAbility>();
            var troll = spawn && spawn.m_spawnPrefab?.Length == 1 ? spawn.m_spawnPrefab[0] : null;
            if (!Summons(spawn) || !troll || troll.name != Troll || troll.GetComponent<Tameable>() || !spawn.m_setMaxInstancesFromWeaponLevel ||
                spawn.m_levelUpSettings == null || !spawn.m_levelUpSettings.Any(setting => setting.m_skillLevel <= 0))
                throw new InvalidOperationException("The Trollstav's untamed troll summoning changed.");
            var skeleton = __instance.GetPrefab("Skeleton_Friendly")?.GetComponent<Tameable>();
            if (!skeleton || skeleton.m_levelUpOwnerSkill != Skills.SkillType.BloodMagic || skeleton.m_levelUpFactor <= 0f || skeleton.m_unSummonEffect == null)
                throw new InvalidOperationException("Dead Raiser's skeleton training or unsummoning changed.");
            unsummonEffect = skeleton.m_unSummonEffect;
            casterSkill = skeleton.m_levelUpOwnerSkill;
            skillFactor = skeleton.m_levelUpFactor;
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, typeof(bool)), HarmonyPostfix]
        private static void Register(ZRoutedRpc __instance)
        {
            __instance.Register<ZDOID>(UnsummonRpc, Unsummon);
            __instance.Register<long, float>(SkillRpc, Train);
        }

        // Runs after SummonOwnership records the caster. The caster's own bonuses are stored with the troll, so a player who
        // loads it later doesn't change them.
        [HarmonyPatch(typeof(SpawnAbility), "SetupAoe"), HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Created(Character __0, Character ___m_owner)
        {
            if (!(___m_owner is Player player) || player != Player.m_localPlayer || !__0) return;
            var view = GameFields.CharacterView(__0);
            if (!view || !view.IsValid() || !view.IsOwner() || Limited(view.GetZDO()) == null) return;
            if (IsTroll(view.GetZDO()))
            {
                if (player.HasActiveMagicEffect(MagicEffectType.ModifySummonDamage, out float damage, .01f)) view.GetZDO().Set(ModifySummonDamage.ZdoKey, damage);
                SummonHealth.Record(__0, player);
            }
            if (!CreatureSpawnPlacement.Marked(__0)) Landed(__0);
        }

        // A troll or root counts once it has landed, so summons cast in quick succession are counted as each one appears.
        internal static void Landed(Character character)
        {
            var view = GameFields.CharacterView(character);
            var limited = view && view.IsValid() ? Limited(view.GetZDO()) : null;
            if (limited == null || !view.IsOwner() || CreatureSpawnPlacement.Marked(character)) return;
            var (staffName, name, kind) = limited.Value;
            if (!SummonOwnership.TryGet(character, out long caster, out string staff) || staff != staffName) return;
            int limit = view.GetZDO().GetInt(ZDOVars.s_maxInstances), prefab = view.GetZDO().GetPrefab();
            if (limit < 1) return;
            var oldest = Character.GetAllCharacters().Where(other => Counts(other, prefab, caster, staffName))
                .OrderBy(other => other.GetBaseAI().GetTimeSinceSpawned()).Skip(limit).ToList();
            if (oldest.Count == 0) return;
            foreach (var other in oldest) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, UnsummonRpc, GameFields.CharacterView(other).GetZDO().m_uid);
            var local = Player.m_localPlayer;
            if (local && local.GetPlayerID() == caster)
                local.Message(MessageHud.MessageType.Center, $"Your {name} holds {limit} {kind}{(limit == 1 ? "" : "s")}, so your oldest one left.");
        }

        private static bool Counts(Character character, int prefab, long caster, string staff)
        {
            var view = character ? GameFields.CharacterView(character) : null;
            return view && view.IsValid() && view.GetZDO().GetPrefab() == prefab && !character.IsDead() && character.GetBaseAI() &&
                !CreatureSpawnPlacement.Marked(character) && SummonOwnership.Recorded(view.GetZDO(), caster, staff);
        }

        // Every game shows the troll or root leaving, as a skeleton's does; the game that runs it removes it.
        private static void Unsummon(long sender, ZDOID id)
        {
            var instance = ZNetScene.instance ? ZNetScene.instance.FindInstance(id) : null;
            var view = instance ? instance.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid() || Limited(view.GetZDO()) == null) return;
            unsummonEffect.Create(instance.transform.position, instance.transform.rotation);
            if (view.IsOwner()) ZNetScene.instance.Destroy(instance);
        }

        // The game trains only a tamed summon's master. A troll's hits train its caster's Blood Magic at a skeleton's rate,
        // in the caster's own game.
        [HarmonyPatch(typeof(Character), nameof(Character.RaiseSkill)), HarmonyPostfix]
        private static void TrainCaster(Character __instance, float value)
        {
            if (__instance.IsPlayer() || __instance.IsTamed() || !SummonOwnership.TryGet(__instance, out long caster, out string staff) || staff != Staff) return;
            float amount = value * skillFactor;
            var local = Player.m_localPlayer;
            if (local && local.GetPlayerID() == caster) local.GetSkills().RaiseSkill(casterSkill, amount);
            else if (Player.GetPlayer(caster) is Player remote && GameFields.CharacterView(remote) is ZNetView view && view.IsValid())
                ZRoutedRpc.instance.InvokeRoutedRPC(view.GetZDO().GetOwner(), SkillRpc, caster, amount);
        }

        private static void Train(long sender, long caster, float amount)
        {
            var player = Player.m_localPlayer;
            if (player && player.GetPlayerID() == caster) player.GetSkills().RaiseSkill(casterSkill, amount);
        }
    }
}
