using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // EpicLoot 0.14.13 rolls a location chest's loot once, for whichever player's game first needs it: an inventory read,
    // hovering over the chest, or pressing Use before the game checks whether the chest opens. The loot follows that
    // player's crafting history and stays in the chest for everyone, so a beginner glancing at a fuling village chest
    // left club-tier gear in it for every later player. The loot is now rolled for the player whose open is granted,
    // once their crafting history has loaded, and the chest's owner adds it to the chest exactly once, so the chest's
    // other viewers, later openers and ownership changes never roll it again. The chest's own loot tables still cap its
    // equipment tier. Taking everything from a chest and destroying it collect it too, for the player who did so; on
    // the owner's game, another player's hit, a creature, fire or a collapse leaves the loot unrolled. Chests that rolled
    // before keep their contents.
    internal static class DeferredChestLoot
    {
        private const string Rpc = Plugin.Id + ".chest-loot";
        // An open grants ownership with it, and the new ownership can reach the opener after the grant; a roll is passed on
        // to the owner each game knows of until it arrives.
        private const int MaxHops = 4;
        private static readonly Func<Container, bool> Load = AccessTools.MethodDelegate<Func<Container, bool>>(AccessTools.Method(typeof(Container), "Load"));
        private static ManualLogSource logger;
        private static HitData destroyingHit;

        internal static void Initialize(IntegrationPatches harmony, ManualLogSource log)
        {
            logger = log;
            // EpicLoot's rolls on an inventory read, hover text, pressing Use, destruction and take all.
            foreach (var (method, patch) in new[] {
                (nameof(Container.GetInventory), typeof(EpicLoot.Container_GetInventory_Patch)),
                (nameof(Container.GetHoverText), typeof(EpicLoot.Container_GetHoverText_Patch)),
                (nameof(Container.Interact), typeof(EpicLoot.Container_Interact_Patch)),
                ("OnDestroyed", typeof(EpicLoot.Container_OnDestroyed_Patch)),
                ("RPC_TakeAllResponse", typeof(EpicLoot.Container_RPC_TakeAllRespons_Patch)) })
            {
                var original = AccessTools.Method(typeof(Container), method);
                var roll = AccessTools.Method(patch, "Prefix");
                if (original == null || roll == null) throw new MissingMethodException("EpicLoot's deferred chest roll changed.");
                harmony.Unpatch(original, roll);
            }
            harmony.Patch(AccessTools.Method(typeof(Container), "Awake"), postfix: Patch(nameof(Register)));
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_OpenResponse"), prefix: Patch(nameof(Opened)));
            harmony.Patch(AccessTools.Method(typeof(Container), "RPC_TakeAllResponse"), prefix: Patch(nameof(TakingAll)));
            harmony.Patch(AccessTools.Method(typeof(Container), "OnDestroyed"), prefix: Patch(nameof(Destroying)));
            harmony.Patch(AccessTools.Method(typeof(WearNTear), "Destroy"), prefix: Patch(nameof(BeforeDestroy)),
                finalizer: Patch(nameof(AfterDestroy)));
        }

        private static HarmonyMethod Patch(string name) => new HarmonyMethod(typeof(DeferredChestLoot), name);

        private static void Register(Container __instance)
        {
            var view = GameFields.ContainerView(__instance);
            if (view != null && view.GetZDO() != null && PendingChestLoot.TryGetLootTables(__instance, out _, out _))
                view.Register<ZPackage>(Rpc, (sender, package) => Receive(__instance, package));
        }

        // Runs after TreasureProgression, which can still refuse the open.
        private static void Opened(Container __instance, bool granted)
        {
            if (granted && Player.m_localPlayer != null) Collect(__instance, "opened");
        }

        private static void TakingAll(Container __instance, bool granted)
        {
            if (!granted || Player.m_localPlayer == null || !Pending(__instance, out _)) return;
            // The game claims the chest before moving everything; claiming it first adds the loot here, before the move.
            GameFields.ContainerView(__instance).ClaimOwnership();
            Collect(__instance, "took everything from");
        }

        private static void BeforeDestroy(HitData hitData, out HitData __state)
        {
            __state = destroyingHit;
            destroyingHit = hitData;
        }

        private static void AfterDestroy(HitData __state) => destroyingHit = __state;

        // A chest is destroyed on its owner's game, which knows only its own character's crafting history.
        private static void Destroying(Container __instance)
        {
            var player = Player.m_localPlayer;
            if (player != null && destroyingHit != null && destroyingHit.GetAttacker() == player) Collect(__instance, "destroyed");
        }

        private static bool Pending(Container chest, out ZDO data)
        {
            var view = GameFields.ContainerView(chest);
            data = view != null && view.IsValid() ? view.GetZDO() : null;
            return data != null && data.GetBool(PendingChestLoot.PendingKey);
        }

        // Rolls the chest's loot with this player's crafting history and sends it to the chest's owner.
        private static void Collect(Container chest, string action)
        {
            if (!Pending(chest, out var data) || !PendingChestLoot.TryGetLootTables(chest, out string name, out var tables)) return;
            string player = Player.m_localPlayer.GetPlayerName();
            if (Eligibility.CraftingMode && !CraftingHistory.Ready)
            {
                logger.LogWarning($"{name} at {chest.transform.position:F0} stays unrolled: {player}'s crafting history has not loaded.");
                return;
            }
            List<ItemDrop.ItemData> items;
            try { items = LootRoller.RollLootTable(tables, 1, chest.GetComponent<Piece>().name, chest.transform.position); }
            catch (Exception error)
            {
                logger.LogError($"{name} at {chest.transform.position:F0} stays unrolled; its roll for {player} failed: {error}");
                return;
            }
            var loot = new Inventory(name, null, 8, Math.Max(1, items.Count));
            foreach (var item in items) loot.AddItem(item);
            var rolled = new ZPackage();
            loot.Save(rolled);
            Send(GameFields.ContainerView(chest), data, 0, $"{player} {action}", rolled);
        }

        private static void Send(ZNetView view, ZDO data, int hops, string reason, ZPackage loot)
        {
            var package = new ZPackage();
            package.Write(hops);
            package.Write(reason);
            package.Write(loot);
            // A chest with no owner would broadcast to every game; its roll stays pending instead.
            if (data.HasOwner()) view.InvokeRPC(data.GetOwner(), Rpc, package);
        }

        // On the chest owner's game: the first roll to arrive fills the chest.
        private static void Receive(Container chest, ZPackage package)
        {
            int hops = package.ReadInt();
            string reason = package.ReadString();
            var rolled = package.ReadPackage();
            var view = GameFields.ContainerView(chest);
            if (!view.IsValid() || !PendingChestLoot.TryGetLootTables(chest, out string name, out _)) return;
            var data = view.GetZDO();
            string where = $"{name} at {chest.transform.position:F0}";
            if (!view.IsOwner())
            {
                if (hops < MaxHops) Send(view, data, hops + 1, reason, rolled);
                else logger.LogWarning($"{where} stays unrolled: the roll after {reason} it found no owner.");
                return;
            }
            if (!data.GetBool(PendingChestLoot.PendingKey))
            {
                logger.LogInfo($"{where} already has its loot; the roll after {reason} it was discarded.");
                return;
            }
            var loot = new Inventory(name, null, 8, 1);
            loot.Load(rolled);
            var items = loot.GetAllItems().ToList();
            // The chest as last saved, then its loot and the completed roll, saved together. EpicLoot's helper adds rows
            // for loot the chest can't fit, as its own rolls do.
            Load(chest);
            data.Set(PendingChestLoot.PendingKey, false);
            logger.LogInfo($"Rolled {where} after {reason} it at {DateTime.Now:yyyy-MM-dd HH:mm:ss}: " +
                (items.Count == 0 ? "no EpicLoot items" : string.Join(", ", items.Select(Describe))) + ".");
            ContainerCapacity.AddItems(chest, items);
        }

        private static string Describe(ItemDrop.ItemData item) =>
            item.m_dropPrefab.name + (item.m_stack > 1 ? " x" + item.m_stack : "") + (item.IsMagic() ? " " + item.GetRarity() : "");
    }
}
