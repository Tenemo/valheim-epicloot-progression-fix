using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    internal static class MountedSteering
    {
        private static ConfigEntry<bool> enabled, directSteering;
        private static ConfigEntry<float> steeringRate;
        private static Func<Sadle, bool> isLocalSaddle;
        private static Action<Sadle> originalStop;
        private static Sadle localSaddle;
        private static float desiredYaw;
        private static MethodInfo inputBefore;
        private static readonly Dictionary<MethodInfo, MethodInfo> Replacements = new Dictionary<MethodInfo, MethodInfo>();
        private static readonly FieldInfo SaddleSpeed = AccessTools.Field(typeof(Sadle), "m_speed");
        private static readonly object Stopped = Enum.Parse(SaddleSpeed.FieldType, "Stop"), Turning = Enum.Parse(SaddleSpeed.FieldType, "Turn");
        private static readonly int TurnRequest = Convert.ToInt32(Turning);
        private static readonly AccessTools.FieldRef<Sadle, Vector3> ControlDirection = AccessTools.FieldRefAccess<Sadle, Vector3>("m_controlDir");

        internal static void Initialize(IntegrationPatches harmony, Assembly assembly, ConfigFile settings)
        {
            MethodInfo Method(string type, string name) => AccessTools.Method(assembly.GetType("Ainzworth.TameRidingOverhaul." + type, true), name)
                ?? throw new MissingMethodException(type, name);
            ConfigEntry<T> Entry<T>(string section, string key) => settings.TryGetEntry(section, key, out ConfigEntry<T> entry)
                ? entry : throw new InvalidOperationException("TameRidingOverhaul's " + section + "." + key + " setting changed.");
            enabled = Entry<bool>("General", "Enabled");
            directSteering = Entry<bool>("Movement", "DirectSteering");
            steeringRate = Entry<float>("Movement", "SteeringDegreesPerSecond");
            isLocalSaddle = AccessTools.MethodDelegate<Func<Sadle, bool>>(Method("Runtime.MountRuntime", "IsLocalSadle"));
            var forceStop = Method("Runtime.MountDirectInput", "ForceStop");
            originalStop = AccessTools.MethodDelegate<Action<Sadle>>(forceStop);
            inputBefore = Method("Runtime.MountDirectInput", "ApplyBeforeVanilla");
            Replacements.Clear();
            foreach (string name in new[] { "Reset", "Clear", "Apply", "TurnAround" })
                Replacements.Add(Method("Runtime.MountSteering", name), AccessTools.Method(typeof(MountedSteering), name));
            Replacements.Add(Method("Runtime.MountSteering", "ApplyStationaryFacingLock"), null);
            Replacements.Add(forceStop, AccessTools.Method(typeof(MountedSteering), nameof(StopInput)));
            var rewrite = new HarmonyMethod(typeof(MountedSteering), nameof(RewriteSteering));
            // Replace callers so the unused old steering type never initializes its removed FaceLookDirection lookup.
            foreach (var caller in new[] {
                Method("TameRidingOverhaulPlugin", "OnDestroy"), Method("Runtime.MountRuntime", "Begin"),
                Method("Runtime.MountRuntime", "End"), Method("Runtime.MountRuntime", "Clear"), inputBefore,
                Method("Patches.SadleApplyControllsPatch", "Prefix"), Method("Patches.SadleApplyControllsPatch", "Postfix"),
                Method("Patches.SadleFixedUpdatePatch", "Postfix") })
                harmony.Patch(caller, transpiler: rewrite);
            harmony.Patch(Method("Runtime.MountDirectInput", "ApplyAfterVanilla"),
                prefix: new HarmonyMethod(typeof(MountedSteering), nameof(KeepControlledStop)));
            harmony.Patch(AccessTools.Method(typeof(Sadle), "RPC_Controls"),
                prefix: new HarmonyMethod(typeof(MountedSteering), nameof(AcceptTurn)),
                postfix: new HarmonyMethod(typeof(MountedSteering), nameof(ReceiveControls)));
            harmony.Patch(AccessTools.Method(typeof(Sadle), nameof(Sadle.UpdateRiding)),
                prefix: new HarmonyMethod(typeof(MountedSteering), nameof(HoldBeforeFirstInput)));
            harmony.Patch(AccessTools.Method(typeof(Character), "UpdateRotation"),
                prefix: new HarmonyMethod(typeof(MountedSteering), nameof(RotateControlledMount)));
            void ResetOnChange(object sender, EventArgs args) => Clear();
            IntegrationChanges.Defer(() =>
            {
                enabled.SettingChanged += ResetOnChange;
                directSteering.SettingChanged += ResetOnChange;
            }, () =>
            {
                enabled.SettingChanged -= ResetOnChange;
                directSteering.SettingChanged -= ResetOnChange;
            });
        }

        private static IEnumerable<CodeInstruction> RewriteSteering(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int changed = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && Replacements.TryGetValue(method, out var replacement))
                {
                    // The removed facing-lock call takes one saddle and returns nothing.
                    instruction.opcode = replacement == null ? OpCodes.Pop : OpCodes.Call;
                    instruction.operand = replacement;
                    changed++;
                }
                yield return instruction;
            }
            if (changed != (__originalMethod == inputBefore ? 2 : 1))
                throw new InvalidOperationException("TameRidingOverhaul's steering calls changed in " + __originalMethod.Name + ".");
        }

        private static bool Local(Sadle saddle) => enabled.Value && directSteering.Value && saddle && isLocalSaddle(saddle);

        private static void Reset(Sadle saddle)
        {
            localSaddle = saddle;
            desiredYaw = saddle && saddle.GetCharacter() ? saddle.GetCharacter().transform.eulerAngles.y : 0f;
        }

        private static void Clear() { localSaddle = null; desiredYaw = 0f; }

        private static void TurnAround(Sadle saddle)
        {
            if (!Local(saddle)) return;
            if (localSaddle != saddle) Reset(saddle);
            desiredYaw = Mathf.Repeat(desiredYaw + 180f, 360f);
        }

        private static void Apply(Sadle saddle, ref Vector3 moveDir, ref Vector3 lookDir, ref bool block)
        {
            if (!Local(saddle)) return;
            if (localSaddle != saddle) Reset(saddle);
            if (Mathf.Abs(moveDir.x) > .01f)
                desiredYaw = Mathf.Repeat(desiredYaw + moveDir.x * Mathf.Max(0f, steeringRate.Value) * Time.deltaTime, 360f);
            lookDir = Quaternion.Euler(0f, desiredYaw, 0f) * Vector3.forward;
            moveDir.x = 0f;
            if (moveDir.z <= .5f) block = true;
        }

        private static void StopInput(Sadle saddle)
        {
            if (!Local(saddle)) originalStop(saddle);
        }

        private static bool KeepControlledStop(Sadle sadle) => !Local(sadle);

        private static bool Controlled(Sadle saddle)
        {
            if (!enabled.Value || !directSteering.Value || !saddle || !saddle.isActiveAndEnabled || !saddle.HaveValidUser()) return false;
            var character = saddle.GetCharacter();
            if (!character || !character.IsTamed()) return false;
            var view = GameFields.CharacterView(character);
            return view && view.IsValid() && view.IsOwner() && view.GetZDO().GetLong(ZDOVars.s_user) != 0;
        }

        private static void AcceptTurn(Sadle __instance, int rideSpeed)
        {
            // Native Controls otherwise ignores Turn while Walk or Run is active, including on a remote owner.
            if (rideSpeed == TurnRequest && Controlled(__instance)) SaddleSpeed.SetValue(__instance, Stopped);
        }

        private static void ReceiveControls(Sadle __instance)
        {
            if (!Controlled(__instance) || !Turning.Equals(SaddleSpeed.GetValue(__instance))) return;
            var character = __instance.GetCharacter();
            character.SetMoveDir(Vector3.zero);
            character.SetRun(false);
            character.SetLookDir(ControlDirection(__instance));
        }

        private static void HoldBeforeFirstInput(Sadle __instance)
        {
            if (!Controlled(__instance)) return;
            bool stopped = Stopped.Equals(SaddleSpeed.GetValue(__instance));
            if (stopped || ControlDirection(__instance) == Vector3.zero) ControlDirection(__instance) = __instance.GetCharacter().transform.forward;
            if (stopped) SaddleSpeed.SetValue(__instance, Turning);
        }

        private static bool RotateControlledMount(Character __instance, ref float __result)
        {
            if (!__instance.HaveRider()) return true;
            var tameable = __instance.GetComponent<Tameable>();
            var saddle = tameable ? tameable.m_saddle : null;
            if (!Controlled(saddle) || !Turning.Equals(SaddleSpeed.GetValue(saddle))) return true;
            Vector3 direction = ControlDirection(saddle); direction.y = 0f;
            if (direction.sqrMagnitude == 0f) return true;
            __instance.transform.rotation = Quaternion.LookRotation(direction);
            // Preserve direct steering's stationary animation signal, including the deliberate 180-degree turn.
            __result = 0f;
            return false;
        }
    }
}
