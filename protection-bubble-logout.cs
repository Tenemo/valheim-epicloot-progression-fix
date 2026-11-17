using System.Globalization;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // The game saves no status effects and Logout Tweaks restores only vanilla ones, so a Staff of Protection bubble
    // ended at logout. Its remaining time, strength and absorbed damage travel in the character's custom data instead;
    // like Logout Tweaks' potions, it doesn't run down while you are logged out. A restored bubble has no caster in
    // this session, so breaking it awards no Blood Magic experience.
    [HarmonyPatch]
    internal static class ProtectionBubbleLogout
    {
        private const string BubbleKey = Plugin.Id + ".protection-bubble";
        private static readonly int ShieldHash = "Staff_shield".GetStableHashCode();
        private static readonly AccessTools.FieldRef<StatusEffect, float> Age = AccessTools.FieldRefAccess<StatusEffect, float>("m_time");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Damage = AccessTools.FieldRefAccess<SE_Shield, float>("m_damage");
        private static readonly AccessTools.FieldRef<SE_Shield, float> Capacity = AccessTools.FieldRefAccess<SE_Shield, float>("m_totalAbsorbDamage");

        [HarmonyPrefix, HarmonyPatch(typeof(Player), nameof(Player.Save))]
        private static void Remember(Player __instance)
        {
            var shield = __instance.IsDead() ? null : __instance.GetSEMan().GetStatusEffect(ShieldHash) as SE_Shield;
            // SE_Shield.IsDone plays the break effects, so test its two end conditions directly.
            if (shield && Damage(shield) <= Capacity(shield) && (shield.m_ttl <= 0f || Age(shield) < shield.m_ttl))
                __instance.m_customData[BubbleKey] = string.Join(":", Text(shield.m_ttl), Text(Age(shield)), Text(Capacity(shield)), Text(Damage(shield)));
            else __instance.m_customData.Remove(BubbleKey);
        }

        // The main menu's character preview loads the same data; only the spawned character takes the bubble.
        [HarmonyPostfix, HarmonyPatch(typeof(Player), nameof(Player.Load))]
        private static void Restore(Player __instance)
        {
            if (__instance != Player.m_localPlayer || !__instance.m_customData.TryGetValue(BubbleKey, out string saved)) return;
            __instance.m_customData.Remove(BubbleKey);
            var values = saved.Split(':');
            if (values.Length != 4 || !Number(values[0], out float duration) || !Number(values[1], out float age) ||
                !Number(values[2], out float capacity) || !Number(values[3], out float damage)) return;
            // Adding the bubble sets its strength and duration for level 0; the saved values replace both.
            if (!(__instance.GetSEMan().AddStatusEffect(ShieldHash) is SE_Shield shield)) return;
            shield.m_ttl = duration;
            Age(shield) = age;
            Capacity(shield) = capacity;
            Damage(shield) = damage;
        }

        private static string Text(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static bool Number(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
