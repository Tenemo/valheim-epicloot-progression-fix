using System;
using System.Reflection;
using EpicLoot;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    internal static class CreatureLootScope
    {
        // EpicLoot rolls a creature's loot in its death callback, or later in its corpse's when the corpse holds the drop.
        // Wraps both in the owner's BeginDeath and BeginCorpse prefixes and its EndDeath finalizer, and, when the owner has
        // one, runs its RememberCorpse postfix as the corpse is set up, to carry the creature's facts to that later roll.
        internal static void Patch(IntegrationPatches harmony, Type owner)
        {
            HarmonyMethod Hook(string name) => new HarmonyMethod(Method(owner, name) ?? throw new MissingMethodException(owner.Name, name));
            harmony.Patch(AccessTools.Method(typeof(CharacterDrop_OnDeath_Patch), nameof(CharacterDrop_OnDeath_Patch.Postfix)),
                prefix: Hook("BeginDeath"), finalizer: Hook("EndDeath"));
            if (Method(owner, "RememberCorpse") != null)
                harmony.Patch(AccessTools.Method(typeof(Ragdoll_Setup_Patch), nameof(Ragdoll_Setup_Patch.Postfix)), postfix: Hook("RememberCorpse"));
            harmony.Patch(AccessTools.Method(typeof(Ragdoll_SpawnLoot_Patch), nameof(Ragdoll_SpawnLoot_Patch.Postfix)),
                prefix: Hook("BeginCorpse"), finalizer: Hook("EndDeath"));
        }

        private static MethodInfo Method(Type owner, string name) => owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
    }
}
