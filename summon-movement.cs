using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    [HarmonyPatch]
    internal static class SummonMovement
    {
        internal const string MovementKey = Plugin.Id + ".summon-movement";
        internal const string RunningKey = Plugin.Id + ".summon-running";
        private static readonly Func<Character, float> JogFactor = AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.Method(typeof(Character), "GetJogSpeedFactor"));
        private static readonly Func<Character, float> RunFactor = AccessTools.MethodDelegate<Func<Character, float>>(AccessTools.Method(typeof(Character), "GetRunSpeedFactor"));
        private static readonly AccessTools.FieldRef<Character, bool> Running = AccessTools.FieldRefAccess<Character, bool>("m_running");
        private static Player sampledPlayer;
        private static float refreshAt;

        private static float ValidMultiplier(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 1f : Mathf.Max(1f, value);

        internal static float Calculate(Player player, bool running)
        {
            bool previous = Running(player);
            try
            {
                // Run skill is part of the run factor even while standing. Treadwell's extra sprint factor is
                // conditional on running on the caster's road, which is not a bonus for the summon to inherit.
                Running(player) = false;
                float baseline = running ? player.m_runSpeed : player.m_speed;
                float speed = baseline * (running ? RunFactor(player) : JogFactor(player));
                Running(player) = running;
                player.GetSEMan().ApplyStatusEffectSpeedMods(ref speed, player.GetMoveDir());
                return baseline > 0f ? ValidMultiplier(speed / baseline) : 1f;
            }
            finally { Running(player) = previous; }
        }

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Refresh(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            var view = GameFields.CharacterView(__instance);
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            if (sampledPlayer == __instance && Time.unscaledTime < refreshAt) return;
            sampledPlayer = __instance; refreshAt = Time.unscaledTime + .25f;
            var data = view.GetZDO();
            float movement = __instance.IsDead() ? 1f : Calculate(__instance, false);
            float running = __instance.IsDead() ? 1f : Calculate(__instance, true);
            if (data.GetFloat(MovementKey) != movement) data.Set(MovementKey, movement);
            if (data.GetFloat(RunningKey) != running) data.Set(RunningKey, running);
        }

        internal static float Multiplier(Character character, bool running)
        {
            if (!SummonOwnership.IsFollower(character) || character.HaveRider()) return 1f;
            var player = SummonOwnership.Summoner(character);
            if (!player || player.IsDead()) return 1f;
            var view = GameFields.CharacterView(player);
            return view && view.IsValid() ? ValidMultiplier(view.GetZDO().GetFloat(running ? RunningKey : MovementKey, 1f)) : 1f;
        }

        // A ridden summon keeps at least its walking pace: a wolf's jog is a slow trot well below it.
        private static float RiddenPace(Character character) => character.HaveRider() && !character.IsRunning() && SummonOwnership.IsFollower(character)
            ? Mathf.Max(1f, character.m_walkSpeed / character.m_speed) : 1f;

        private static void Boost(Character character, ref float speed) => speed *= Multiplier(character, character.IsRunning()) * RiddenPace(character);
        private static void Turning(Character character, ref float speed) => speed *= Multiplier(character, character.IsRunning());

        [HarmonyPatch(typeof(Character), "UpdateWalking"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> GroundMovement(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var statusSpeed = AccessTools.Method(typeof(SEMan), nameof(SEMan.ApplyStatusEffectSpeedMods));
            var calls = Enumerable.Range(0, code.Count).Where(index => code[index].Calls(statusSpeed)).ToArray();
            // The speed each call changes is the local passed by reference just before its direction argument.
            var speeds = calls.Select(index => Enumerable.Range(Math.Max(0, index - 3), Math.Min(index, 3)).Reverse()
                .Select(position => code[position]).FirstOrDefault(instruction => instruction.opcode == OpCodes.Ldloca || instruction.opcode == OpCodes.Ldloca_S)).ToArray();
            if (calls.Length != 2 || speeds.Any(speed => speed == null))
                throw new InvalidOperationException("The ground movement speed calculation changed.");
            // The first status-effect call computes ground speed and the second turning speed. The game's own speed effects
            // change both, which keeps a creature's turning circle; a summon turning at its native rate while running at its
            // caster's pace circles a path corner it can no longer turn into.
            for (int call = 1; call >= 0; call--)
                code.InsertRange(calls[call] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(speeds[call].opcode, speeds[call].operand),
                    new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SummonMovement), call == 0 ? nameof(Boost) : nameof(Turning))) });
            return code;
        }
    }
}
