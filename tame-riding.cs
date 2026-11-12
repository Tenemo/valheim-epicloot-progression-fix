using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // Repairs TameRidingOverhaul 0.1.20 for this pack:
    // - Its gameplay settings follow the server through this add-on's synchronized copies; the Sit key stays personal.
    // - Saddle stamina is the game's own again: running and swimming drain once, with the Riding skill's reduction,
    //   and regeneration keeps its hunger, swimming and attack rules. Mount attacks still cost their stamina once.
    // - Mounted controls come from the game's guarded input, so chat, menus and text entry never move or attack, and
    //   controller movement, sprint, jump and attacks work like the keyboard.
    // - Riding multiplies ground movement only, from the creature's current speeds; a speed another mod changes
    //   during the ride is kept and multiplied, and dismounting restores only what riding set.
    // - Vegetation clearing runs only on the mount's owner, never touches protected crops and plantings, and every
    //   collision it passes through comes back even after the ride ends.
    // - Sit goes through the animal's owner instead of taking ownership, and releasing it resumes following whoever
    //   the animal followed before.
    // - Sit replaces a tame's home with the spot it sits on, and releasing Sit, which mounting also does, cleared the
    //   home, so the tame wandered off from wherever it stood. Releasing Sit now gives back the home it had before, or the
    //   spot the harpoon last let go of it, unless it goes back to following someone.
    internal static class TameRiding
    {
        private const string Runtime = "Ainzworth.TameRidingOverhaul.Runtime.";
        private const string SitKey = "uk.ainzworth.tameridingoverhaul.sit", SitRpc = Plugin.Id + ".tame-sit";
        private static readonly int SitFollowHash = (Plugin.Id + ".sit-follow").GetStableHashCode();

        // Personal or native-replaced settings are not synchronized; Internal holds the mod's migration markers.
        private static readonly HashSet<string> Unsynchronized = new HashSet<string>
        {
            "General/DebugLogging", "Sit/Keybind", "Movement/StationaryTurnAssist", "Stamina/MoveDrainPerSecond",
            "Stamina/SprintDrainPerSecond", "Stamina/RegenerationPerSecond", "Stamina/RegenerationDelaySeconds"
        };
        private static readonly Dictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            { "Sit/Enabled", "Allow tamed animals to hold position while turning, reacting and eating food within reach." },
            { "Sit/Keybind", "Shortcut to toggle Stay while pointing at a saddled animal." },
            { "Sit/SitOnDismount", "Stay after dismounting. Mounting releases Stay." },
            { "Stamina/Enabled", "Use the mount attack costs below; stop sprinting at MinimumSprintStamina." },
            { "Movement/MoveSpeedMultiplier", "Mounted ground movement speed multiplier." },
            { "Movement/SprintSpeedMultiplier", "Mounted ground running speed multiplier." },
            { "Movement/AccelerationMultiplier", "Multiplier applied to mounted ground acceleration and braking." },
            { "Movement/TurnSpeedMultiplier", "Multiplier applied to the creature's normal and running ground turn speeds while ridden." },
        };

        private static ConfigEntry<bool> enabled, staminaEnabled, stayEnabled;
        private static ConfigEntry<float> moveMultiplier, sprintMultiplier, accelerationMultiplier, turnMultiplier;
        private static Action<Sadle, float> useStamina;
        private static Func<PlayerController, bool, bool> takeInput;
        private static Action restoreCollisions;
        private static MethodInfo setSitting;
        private static readonly FieldInfo ConfigurationDescription = AccessTools.Field(typeof(ConfigEntryBase), "<Description>k__BackingField");
        private static readonly AccessTools.FieldRef<Character, bool> Tamed = AccessTools.FieldRefAccess<Character, bool>("m_tamed");
        private static readonly Action<BaseAI, Vector3> SetHome = AccessTools.MethodDelegate<Action<BaseAI, Vector3>>(
            AccessTools.Method(typeof(BaseAI), "SetPatrolPoint", new[] { typeof(Vector3) }));

        internal static void Initialize(IntegrationPatches harmony, ConfigFile settings)
        {
            if (!PluginIds.TryGet(PluginIds.TameRiding, out var plugin)) return;
            var assembly = plugin.Instance.GetType().Assembly;
            var riding = plugin.Instance.Config;
            Type Find(string name) => assembly.GetType(name) ?? throw new InvalidOperationException("TameRidingOverhaul's " + name + " moved.");
            MethodInfo Method(Type type, string name) => AccessTools.Method(type, name) ?? throw new InvalidOperationException("TameRidingOverhaul's " + type.Name + "." + name + " moved.");
            ConfigEntry<T> Entry<T>(string section, string key) =>
                riding.TryGetEntry(section, key, out ConfigEntry<T> entry) ? entry : throw new InvalidOperationException("TameRidingOverhaul's " + section + "." + key + " setting changed.");
            enabled = Entry<bool>("General", "Enabled");
            staminaEnabled = Entry<bool>("Stamina", "Enabled");
            stayEnabled = Entry<bool>("Sit", "Enabled");
            moveMultiplier = Entry<float>("Movement", "MoveSpeedMultiplier");
            sprintMultiplier = Entry<float>("Movement", "SprintSpeedMultiplier");
            accelerationMultiplier = Entry<float>("Movement", "AccelerationMultiplier");
            turnMultiplier = Entry<float>("Movement", "TurnSpeedMultiplier");

            var stamina = Find(Runtime + "MountStamina");
            var input = Find(Runtime + "MountDirectInput");
            var movement = Find(Runtime + "MountMovement");
            var trample = Find(Runtime + "TrampleSystem");
            var sit = Find(Runtime + "TameSitSystem");
            var actions = Find("Ainzworth.TameRidingOverhaul.Patches.PlayerUpdateMountedActionsPatch");
            setSitting = Method(sit, "SetSitting");
            useStamina = AccessTools.MethodDelegate<Action<Sadle, float>>(AccessTools.Method(typeof(Sadle), "UseStamina"));
            takeInput = AccessTools.MethodDelegate<Func<PlayerController, bool, bool>>(AccessTools.Method(typeof(PlayerController), "TakeInput"));
            restoreCollisions = AccessTools.MethodDelegate<Action>(Method(trample, "RestoreExpiredCollisions"));

            Synchronize(riding, settings);
            var self = typeof(TameRiding);
            HarmonyMethod Hook(string name, int priority = Priority.Normal) => new HarmonyMethod(self, name) { priority = priority };
            harmony.Patch(Method(stamina, "CaptureBeforeVanilla"), prefix: Hook(nameof(Skip)));
            harmony.Patch(Method(stamina, "ApplyAfterVanilla"), prefix: Hook(nameof(Skip)));
            harmony.Patch(Method(stamina, "SpendAttack"), prefix: Hook(nameof(SpendAttack)));
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SetControls)), prefix: Hook(nameof(RecordControls), Priority.First));
            harmony.Patch(Method(input, "ApplyBeforeVanilla"), transpiler: Hook(nameof(GuardedInput)));
            harmony.Patch(Method(actions, "Postfix"), transpiler: Hook(nameof(GuardedInput)));
            harmony.Patch(Method(sit, "UpdateManualInput"), prefix: Hook(nameof(SitShortcutAllowed)));
            harmony.Patch(Method(movement, "Update"), prefix: Hook(nameof(UpdateMovement)));
            harmony.Patch(Method(movement, "Restore"), prefix: Hook(nameof(RestoreMovement)));
            harmony.Patch(Method(movement, "RestoreAll"), prefix: Hook(nameof(RestoreAllMovement)));
            harmony.Patch(Method(trample, "Update"), prefix: Hook(nameof(OwnerTramples)));
            harmony.Patch(Method(trample, "IsTrampleable"), postfix: Hook(nameof(ProtectPlantings)));
            harmony.Patch(AccessTools.Method(typeof(Sadle), "FixedUpdate"), postfix: Hook(nameof(RestoreCollisions)));
            harmony.Patch(setSitting, prefix: Hook(nameof(SitThroughOwner)), postfix: Hook(nameof(ResumeFollowing)), transpiler: Hook(nameof(StayWording)));
            harmony.Patch(Method(sit, "AppendHoverText"), transpiler: Hook(nameof(StayWording)));
            harmony.Patch(Method(sit, "ResolveRideableTame"), postfix: Hook(nameof(NoStayOnSummons)));
            harmony.Patch(Method(sit, "Update"), prefix: Hook(nameof(ReleaseUnsaddled)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), "Awake"), postfix: Hook(nameof(RegisterSit)));
            harmony.Patch(AccessTools.Method(typeof(Tameable), nameof(Tameable.DropSaddle)), postfix: Hook(nameof(SaddleRemoved)));
            MountedSteering.Initialize(harmony, assembly, riding);
        }

        // Each gameplay setting gets a server-synchronized copy in this add-on's configuration; the copy's value
        // replaces the mod's own whenever it loads or changes.
        private static void Synchronize(ConfigFile riding, ConfigFile settings)
        {
            var bind = typeof(ConfigFile).GetMethods().Single(method => method.Name == nameof(ConfigFile.Bind) && method.IsGenericMethodDefinition &&
                method.GetParameters().Select(parameter => parameter.ParameterType.Name).SequenceEqual(new[] { nameof(ConfigDefinition), "T", nameof(ConfigDescription) }));
            var copies = new Dictionary<ConfigEntryBase, ConfigEntryBase>();
            foreach (var definition in riding.Keys.ToList())
            {
                string name = definition.Section + "/" + definition.Key;
                var entry = riding[definition];
                string text = Descriptions.TryGetValue(name, out string replaced) ? replaced : entry.Description.Description;
                if (definition.Section == "Sit" && replaced != null)
                {
                    var original = entry.Description;
                    var description = new ConfigDescription(text, original.AcceptableValues, original.Tags);
                    IntegrationChanges.Defer(() => ConfigurationDescription.SetValue(entry, description), () => ConfigurationDescription.SetValue(entry, original));
                }
                if (definition.Section == "Internal" || Unsynchronized.Contains(name)) continue;
                IntegrationChanges.Defer(() =>
                {
                    var copy = (ConfigEntryBase)bind.MakeGenericMethod(entry.SettingType).Invoke(settings, new object[]
                        { new ConfigDefinition("Tame riding " + definition.Section.ToLowerInvariant(), definition.Key), entry.DefaultValue, Plugin.Synced(text, entry.Description.AcceptableValues) });
                    copies[copy] = entry;
                }, () => { settings.Remove(new ConfigDefinition("Tame riding " + definition.Section.ToLowerInvariant(), definition.Key)); });
            }
            void Copy(object sender, SettingChangedEventArgs change)
            {
                if (copies.TryGetValue(change.ChangedSetting, out var entry)) entry.BoxedValue = change.ChangedSetting.BoxedValue;
            }
            IntegrationChanges.Defer(() =>
            {
                foreach (var pair in copies) pair.Value.BoxedValue = pair.Key.BoxedValue;
                settings.SettingChanged += Copy;
            }, () => settings.SettingChanged -= Copy);
        }

        private static bool Skip() => false;

        internal static bool HoldingPosition(Character character)
        {
            if (!character || character.HaveRider()) return false;
            var view = character.GetComponent<ZNetView>();
            return view && view.IsValid() && HoldingPosition(view.GetZDO());
        }

        internal static bool HoldingPosition(ZDO data) => enabled?.Value == true && stayEnabled?.Value == true &&
            OptionalIntegrations.Enabled(typeof(TameRiding)) && data != null && data.GetBool(SitKey);

        // The native drain also starts the native regeneration delay.
        private static bool SpendAttack(Sadle sadle, float cost)
        {
            if (staminaEnabled.Value && cost > 0f && sadle) useStamina(sadle, cost);
            return false;
        }

        // The game's controls this physics step, already guarded against chat, menus and text entry and combining
        // keyboard and controller. Presses wait for the next frame's mounted actions.
        private static Vector3 ridingMove;
        private static bool ridingRun, attackPressed, blockPressed, secondaryPressed, jumpPressed;

        private static void RecordControls(Player __instance, Vector3 movedir, bool attack, bool secondaryAttack, bool block, bool jump, bool run)
        {
            if (__instance != Player.m_localPlayer) return;
            if (!__instance.IsRiding())
            {
                ridingMove = Vector3.zero; ridingRun = attackPressed = blockPressed = secondaryPressed = jumpPressed = false;
                return;
            }
            ridingMove = movedir; ridingRun = run;
            attackPressed |= attack; blockPressed |= block; secondaryPressed |= secondaryAttack; jumpPressed |= jump;
        }

        private static IEnumerable<CodeInstruction> GuardedInput(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var held = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButton), new[] { typeof(string) });
            var pressed = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetButtonDown), new[] { typeof(string) });
            int replaced = 0;
            for (int index = 1; index < code.Count; index++)
            {
                bool hold = code[index].Calls(held);
                if (!hold && !code[index].Calls(pressed)) continue;
                string button = code[index - 1].opcode == OpCodes.Ldstr ? (string)code[index - 1].operand : null;
                if (!(hold ? new[] { "Forward", "Backward", "Run" } : new[] { "Jump", "Attack", "Block", "SecondaryAttack" }).Contains(button))
                    throw new InvalidOperationException("TameRidingOverhaul reads an unexpected control: " + button);
                code[index].operand = AccessTools.Method(typeof(TameRiding), hold ? nameof(Held) : nameof(Pressed));
                replaced++;
            }
            if (replaced == 0) throw new InvalidOperationException("TameRidingOverhaul's mounted controls changed.");
            return code;
        }

        private static bool Held(string button) =>
            button == "Forward" ? ridingMove.z > 0.5f : button == "Backward" ? ridingMove.z < -0.5f : ridingRun;

        private static bool Pressed(string button)
        {
            bool result;
            switch (button)
            {
                case "Jump": result = jumpPressed; jumpPressed = false; break;
                case "Attack": result = attackPressed; attackPressed = false; break;
                case "Block": result = blockPressed; blockPressed = false; break;
                default: result = secondaryPressed; secondaryPressed = false; break;
            }
            return result;
        }

        private static bool SitShortcutAllowed(Player player)
        {
            var controller = player ? player.GetComponent<PlayerController>() : null;
            return controller && takeInput(controller, false);
        }

        // Riding multiplies speed, walk, run, acceleration and both ground turn speeds.
        private sealed class Ride
        {
            internal readonly float[] Original = new float[6], Applied = { float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN };
        }
        private static readonly Dictionary<Character, Ride> Rides = new Dictionary<Character, Ride>();

        private static float Speed(Character character, int field)
        {
            switch (field)
            {
                case 0: return character.m_speed;
                case 1: return character.m_walkSpeed;
                case 2: return character.m_runSpeed;
                case 3: return character.m_acceleration;
                case 4: return character.m_turnSpeed;
                default: return character.m_runTurnSpeed;
            }
        }

        private static void SetSpeed(Character character, int field, float value)
        {
            switch (field)
            {
                case 0: character.m_speed = value; break;
                case 1: character.m_walkSpeed = value; break;
                case 2: character.m_runSpeed = value; break;
                case 3: character.m_acceleration = value; break;
                case 4: character.m_turnSpeed = value; break;
                default: character.m_runTurnSpeed = value; break;
            }
        }

        private static float Multiplier(int field) => Mathf.Max(0.05f, (field <= 1 ? moveMultiplier : field == 2 ? sprintMultiplier : field == 3 ? accelerationMultiplier : turnMultiplier).Value);

        private static bool UpdateMovement(Character character)
        {
            if (!character) return false;
            if (!enabled.Value || !character.HaveRider()) { RestoreMovement(character); return false; }
            if (!Rides.TryGetValue(character, out var ride)) Rides.Add(character, ride = new Ride());
            for (int field = 0; field < 6; field++)
            {
                float current = Speed(character, field);
                if (current == ride.Applied[field]) continue;
                // On mounting, or after another mod set this speed during the ride, the current value is the unridden one.
                ride.Original[field] = current;
                ride.Applied[field] = current * Multiplier(field);
                SetSpeed(character, field, ride.Applied[field]);
            }
            return false;
        }

        private static bool RestoreMovement(Character character)
        {
            if (Rides.Count == 0) return false;
            if (character && Rides.TryGetValue(character, out var ride))
                for (int field = 0; field < 6; field++)
                    if (Speed(character, field) == ride.Applied[field]) SetSpeed(character, field, ride.Original[field]);
            Rides.Remove(character);
            foreach (var unloaded in Rides.Keys.Where(key => !key).ToList()) Rides.Remove(unloaded);
            return false;
        }

        private static bool RestoreAllMovement()
        {
            foreach (var character in Rides.Keys.ToList()) RestoreMovement(character);
            return false;
        }

        private static bool OwnerTramples(Character mount)
        {
            var view = mount ? mount.GetComponent<ZNetView>() : null;
            return view && view.IsValid() && view.IsOwner();
        }

        private static void ProtectPlantings(GameObject target, Collider hitCollider, ref bool __result)
        {
            if (!__result) return;
            var hit = hitCollider ? Projectile.FindHitObject(hitCollider) : null;
            if (TamedCropTrampling.Protected(target) || hit && TamedCropTrampling.Protected(hit)) __result = false;
        }

        // Clearing passes through vegetation for a moment; the collisions return on time whether or not anyone rides.
        private static void RestoreCollisions() => restoreCollisions();

        private static void RegisterSit(Tameable __instance)
        {
            var view = __instance.GetComponent<ZNetView>();
            var character = __instance.GetComponent<Character>();
            if (!__instance.m_saddle || !view || !view.IsValid() || !character) return;
            view.Register<bool>(SitRpc, (sender, sitting) =>
            {
                if (!view.IsValid() || !view.IsOwner()) return;
                // Valheim reads the tamed state only while it does not own the animal, so an owner that loaded the
                // animal before a player tamed it would still treat it as wild.
                Tamed(character) = view.GetZDO().GetBool(ZDOVars.s_tamed, Tamed(character));
                setSitting.Invoke(null, new object[] { character, sitting, null, false });
            });
            ReleaseUnsaddled(character);
        }

        private static void NoStayOnSummons(Tameable __0, ref Character __result)
        {
            if (SpiritMounts.Bare(__0)) __result = null;
        }

        private static void SaddleRemoved(Tameable __instance, bool __result)
        {
            if (__result) ReleaseUnsaddled(__instance.GetComponent<Character>());
        }

        // The saved saddle flag remains available after its object is disabled, including on a newly controlling peer.
        private static void ReleaseUnsaddled(Character character)
        {
            var view = character ? GameFields.OwnView(character) : null;
            if (!view || !view.IsValid() || !view.IsOwner()) return;
            var data = view.GetZDO();
            if (!data.GetBool(SitKey) || data.GetBool(ZDOVars.s_haveSaddleHash) || !data.GetBool(ZDOVars.s_tamed)) return;
            Tamed(character) = true;
            setSitting.Invoke(null, new object[] { character, false, null, false });
        }

        private static IEnumerable<CodeInstruction> StayWording(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldstr)
                {
                    string text = (string)instruction.operand;
                    if (text == "Sit" || text == "Release Sit" || text == "Sit released")
                    {
                        instruction.operand = text == "Sit" ? "Stay" : text == "Release Sit" ? "Release stay" : "Stay released";
                        replaced++;
                    }
                }
                yield return instruction;
            }
            if (replaced != 2) throw new InvalidOperationException("TameRidingOverhaul's command wording changed.");
        }

        private static bool SitThroughOwner(Character character, bool sitting, Player player, bool showMessage)
        {
            var view = character ? character.GetComponent<ZNetView>() : null;
            if (!view || !view.IsValid()) return true;
            if (sitting && !view.GetZDO().GetBool(ZDOVars.s_haveSaddleHash)) return false;
            if (!view.IsOwner())
            {
                view.InvokeRPC(SitRpc, sitting);
                if (showMessage && player) player.Message(MessageHud.MessageType.Center, character.GetHoverName() + ": " + (sitting ? "Stay" : "Stay released"));
                return false;
            }
            var zdo = view.GetZDO();
            // Sit makes the spot it sits on the tame's home; the home it had waits for the end of Sit.
            if (!zdo.GetBool(SitKey) && zdo.GetBool(ZDOVars.s_patrol)) zdo.Set(TamedHarpoonMovement.Home, zdo.GetVec3(ZDOVars.s_patrolPoint, Vector3.zero));
            if (sitting && !zdo.GetBool(SitKey)) zdo.Set(SitFollowHash, zdo.GetString(ZDOVars.s_follow));
            return true;
        }

        // The tame's own follow update finds the saved player by name, as after a reload.
        private static void ResumeFollowing(Character character, bool sitting)
        {
            var view = character ? character.GetComponent<ZNetView>() : null;
            if (sitting || !view || !view.IsValid() || !view.IsOwner()) return;
            var zdo = view.GetZDO();
            string followed = zdo.GetString(SitFollowHash);
            if (followed.Length > 0)
            {
                zdo.Set(ZDOVars.s_follow, followed);
                zdo.Set(SitFollowHash, "");
                return;
            }
            // A following tame has no home, as the game's follow command leaves it.
            var home = zdo.GetVec3(TamedHarpoonMovement.Home, Vector3.zero);
            if (home != Vector3.zero && zdo.GetString(ZDOVars.s_follow).Length == 0 && character.GetBaseAI()) SetHome(character.GetBaseAI(), home);
        }
    }
}
