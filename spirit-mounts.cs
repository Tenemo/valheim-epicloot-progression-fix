using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // A Spirit Caller wolf, moose or bear carries its caster with no saddle item. The moose's prefab already holds the saddle of
    // the wild moose, switched off; the wolf and bear get a copy of it with a seat on their spine. Each stays hidden and is on from
    // the start without the saved saddle flag, so it never drops, and the pack's Stay, which needs that flag, leaves the animal
    // following its caster after a ride.
    [HarmonyPatch]
    internal static class SpiritMounts
    {
        private const string Feature = "Riding Spirit Caller animals";
        private static readonly (string Prefab, string Trophy, string Bone, Vector3 Seat)[] Seats =
        {
            ("Wolf_spiritcaller", "TrophyWolf", "Spine1", new Vector3(0f, 1f, 0f)),
            ("Bjorn_spiritcaller", "TrophyBjorn", "Spine_02", new Vector3(0f, 2.15f, -.3f)),
        };

        // No other animal has a saddle without its item.
        internal static bool Bare(Tameable animal) => animal && animal.m_saddle && !animal.m_saddleItem;

        private static bool CanRide(Tameable animal, Player player) => Bare(animal) && player &&
            SummonOwnership.TryGet(animal.GetComponent<Character>(), out long summoner, out _, player) && summoner == player.GetPlayerID();

        private static Sprite Trophy(ZNetScene scene, string name) => scene.GetPrefab(name)?.GetComponent<ItemDrop>()?.m_itemData.GetIcon();

        private static void MakeBare(Tameable animal, Sprite icon)
        {
            animal.m_saddleItem = null;
            animal.m_dropSaddleOnDeath = false;
            animal.m_saddle.m_mountIcon = icon;
        }

        private static Sadle CopySaddle(Sadle source, Transform seat)
        {
            var holder = new GameObject(source.name);
            holder.SetActive(false);
            var saddle = holder.AddComponent<Sadle>();
            foreach (var field in typeof(Sadle).GetFields(BindingFlags.Public | BindingFlags.Instance)) field.SetValue(saddle, field.GetValue(source));
            saddle.m_attachPoint = seat;
            return saddle;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake"), HarmonyPostfix]
        private static void PrepareAnimals(ZNetScene __instance)
        {
            var moose = __instance.GetPrefab("Moose_spiritcaller")?.GetComponent<Tameable>();
            if (!moose || !moose.m_saddle) { DisabledFeatures.Set(Feature, "the Spirit Moose lost its saddle"); return; }
            var saddle = moose.m_saddle;
            foreach (var renderer in saddle.GetComponents<Renderer>()) renderer.enabled = false;
            foreach (var collider in saddle.GetComponents<Collider>()) collider.enabled = false;
            MakeBare(moose, Trophy(__instance, "TrophyMoose"));
            foreach (var (prefab, trophy, boneName, position) in Seats)
            {
                var root = __instance.GetPrefab(prefab);
                var animal = root?.GetComponent<Tameable>();
                if (Bare(animal)) continue;
                var bone = root?.GetComponentsInChildren<Transform>(true).FirstOrDefault(transform => transform.name == boneName);
                if (!animal || !bone) { DisabledFeatures.Set(Feature, prefab + "'s " + boneName + " moved"); return; }
                var seat = new GameObject("Seat").transform;
                seat.SetParent(bone, false);
                seat.SetPositionAndRotation(root.transform.TransformPoint(position), root.transform.rotation);
                animal.m_saddle = CopySaddle(saddle, seat);
                animal.m_saddle.transform.SetParent(root.transform, false);
                MakeBare(animal, Trophy(__instance, trophy));
            }
        }

        [HarmonyPatch(typeof(Tameable), "HaveSaddle"), HarmonyPostfix]
        private static void AlwaysSaddled(Tameable __instance, ref bool __result) => __result |= Bare(__instance);

        // Interacting with a summon only pets it, so its caster's Use can ride it instead.
        [HarmonyPatch(typeof(Tameable), nameof(Tameable.Interact)), HarmonyPrefix]
        private static bool Ride(Tameable __instance, Humanoid user, bool hold, bool alt, ref bool __result)
        {
            if (hold || alt || !CanRide(__instance, user as Player)) return true;
            __result = __instance.m_saddle.Interact(user, false, false);
            return false;
        }

        [HarmonyPatch(typeof(Tameable), nameof(Tameable.GetHoverText)), HarmonyPostfix]
        private static void RideRow(Tameable __instance, ref string __result)
        {
            const string keyEnd = "</b></color>] ";
            if (CanRide(__instance, Player.m_localPlayer))
                __result = __result.Replace(keyEnd + Localization.instance.Localize("$hud_pet"), keyEnd + Localization.instance.Localize("$" + SummonNames.Ride));
        }
    }
}
