using System;
using EpicLoot.CraftingV2;
using EpicLoot_UnityLib;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Private game fields and methods that several features read.
    internal static class GameFields
    {
        internal static readonly AccessTools.FieldRef<Character, ZNetView> CharacterView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");
        // Off searches for the creature of an AI hook as before; the isolated client switches it for paired measurements.
        internal static bool HeldCreature = true;

        // The character's own ZNetView, as GetComponent finds it, without the lookup for hooks every creature runs every
        // frame: Awake keeps it unless the prefab names another view.
        internal static ZNetView OwnView(Character character)
        {
            var view = (object)character.m_nViewOverride == null ? CharacterView(character) : null;
            return (object)view != null ? view : character.GetComponent<ZNetView>();
        }
        internal static readonly AccessTools.FieldRef<Character, bool> Blocking = AccessTools.FieldRefAccess<Character, bool>("m_blocking");
        internal static readonly AccessTools.FieldRef<Character, bool> AttackHold = AccessTools.FieldRefAccess<Character, bool>("m_attackHold");
        internal static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> RightItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_rightItem");
        internal static readonly AccessTools.FieldRef<Humanoid, ItemDrop.ItemData> LeftItem =
            AccessTools.FieldRefAccess<Humanoid, ItemDrop.ItemData>("m_leftItem");
        internal static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
        internal static readonly AccessTools.FieldRef<Player, int> InteractMask = AccessTools.FieldRefAccess<Player, int>("m_interactMask");
        internal static readonly AccessTools.FieldRef<SEMan, Character> StatusCharacter = AccessTools.FieldRefAccess<SEMan, Character>("m_character");
        internal static readonly AccessTools.FieldRef<Attack, Humanoid> AttackCharacter = AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");
        internal static readonly AccessTools.FieldRef<Attack, ItemDrop.ItemData> AttackWeapon =
            AccessTools.FieldRefAccess<Attack, ItemDrop.ItemData>("m_weapon");
        internal static readonly AccessTools.FieldRef<Projectile, Character> ProjectileOwner = AccessTools.FieldRefAccess<Projectile, Character>("m_owner");
        internal static readonly AccessTools.FieldRef<Projectile, ItemDrop.ItemData> ProjectileWeapon =
            AccessTools.FieldRefAccess<Projectile, ItemDrop.ItemData>("m_weapon");
        internal static readonly AccessTools.FieldRef<Aoe, Character> AoeOwner = AccessTools.FieldRefAccess<Aoe, Character>("m_owner");
        internal static readonly AccessTools.FieldRef<Aoe, ZNetView> AoeView = AccessTools.FieldRefAccess<Aoe, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<Container, ZNetView> ContainerView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<Container, Inventory> ContainerInventory = AccessTools.FieldRefAccess<Container, Inventory>("m_inventory");
        internal static readonly AccessTools.FieldRef<InventoryGui, InventoryGrid> ContainerGrid = AccessTools.FieldRefAccess<InventoryGui, InventoryGrid>("m_containerGrid");
        internal static readonly AccessTools.FieldRef<Inventory, Action> InventoryChanged = AccessTools.FieldRefAccess<Inventory, Action>("m_onChanged");
        internal static readonly AccessTools.FieldRef<WearNTear, ZNetView> WearView = AccessTools.FieldRefAccess<WearNTear, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<PlayerProfile, byte[]> ProfileData = AccessTools.FieldRefAccess<PlayerProfile, byte[]>("m_playerData");
        internal static readonly AccessTools.FieldRef<ConvertUI, MaterialConversionType> ConversionMode =
            AccessTools.FieldRefAccess<ConvertUI, MaterialConversionType>("_mode");
        // A creature's saved home: where a tame was told to stay, or a patrol spot.
        internal delegate bool ReadPatrolPoint(BaseAI ai, out Vector3 point);
        internal static readonly ReadPatrolPoint PatrolPoint = AccessTools.MethodDelegate<ReadPatrolPoint>(AccessTools.Method(typeof(BaseAI), "GetPatrolPoint"));
    }
}
