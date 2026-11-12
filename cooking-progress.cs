using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace EpicLootProgressionFix
{
    // Cooking stations and ovens are evaluated once a second, at least a full second apart, but the station's synchronized
    // record changes only when something meaningful happens or at a five-second checkpoint, so nearby players receive no
    // more updates than before. Every second the owner checks, from the last recorded step and the time since, whether
    // food has turned done or burnt, whether an oven has burnt a visible unit of fuel or run out, and whether heating has
    // started or stopped; any of those runs the game's own step at once, and otherwise one runs every five seconds while
    // something is cooking. A station with nothing cooking writes nothing. Time while unlit never counts, time lit until a
    // fire goes out does, and adding food first records the progress so far. Every game shows progress that advances each
    // second from the last recorded step, and a new owner, a reload or time away continues from that record.
    internal static class CookingProgress
    {
        private const float CheckpointSeconds = 5f, RunSeconds = 1f;
        internal const string PausedKey = "valheim.epiclootprogressionfix.cooking-paused";

        private sealed class State
        {
            internal bool Seen, WasLit;
            internal long LitAt;
            internal float Elapsed;
            internal double LastRun;
        }

        private static readonly ConditionalWeakTable<CookingStation, State> States = new ConditionalWeakTable<CookingStation, State>();
        private static readonly AccessTools.FieldRef<CookingStation, ZNetView> View = AccessTools.FieldRefAccess<CookingStation, ZNetView>("m_nview");
        private static readonly Func<CookingStation, bool> IsFireLit = AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "IsFireLit"));
        private static readonly Func<CookingStation, bool> HaveUncookedItem =
            AccessTools.MethodDelegate<Func<CookingStation, bool>>(AccessTools.Method(typeof(CookingStation), "HaveUncookedItem"));
        private static readonly Func<CookingStation, string, CookingStation.ItemConversion> Conversion =
            AccessTools.MethodDelegate<Func<CookingStation, string, CookingStation.ItemConversion>>(AccessTools.Method(typeof(CookingStation), "GetItemConversion"));
        private static readonly int PausedHash = PausedKey.GetStableHashCode();
        private static readonly List<(int Name, int Time, int Status)> SlotKeys = new List<(int, int, int)>();

        // The owner's native step runs only when this asks for it; while it runs, a fire that has just gone out still
        // counts as lit and the step credits time only until the fire was last seen lit.
        private static bool stepping, crediting;
        private static long creditUntil;

        internal static void Initialize(IntegrationPatches harmony)
        {
            var self = typeof(CookingProgress);
            var update = AccessTools.Method(typeof(CookingStation), "UpdateCooking");
            harmony.Patch(update, prefix: new HarmonyMethod(self, nameof(Evaluate)), postfix: new HarmonyMethod(self, nameof(ScheduleNext)),
                transpiler: new HarmonyMethod(self, nameof(StepOnRequest)));
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "Awake"), postfix: new HarmonyMethod(self, nameof(ReplaceTimer)));
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "IsFireLit"), prefix: new HarmonyMethod(self, nameof(StillLit)));
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "GetDeltaTime"), prefix: new HarmonyMethod(self, nameof(CreditLitTime)));
            harmony.Patch(AccessTools.Method(typeof(CookingStation), "RPC_AddItem"), prefix: new HarmonyMethod(self, nameof(RecordBeforeAdding)));
        }

        private static readonly Action<CookingStation> UpdateCooking =
            AccessTools.MethodDelegate<Action<CookingStation>>(AccessTools.Method(typeof(CookingStation), "UpdateCooking"));

        private static (int Name, int Time, int Status) Keys(int slot)
        {
            while (SlotKeys.Count <= slot)
            {
                int index = SlotKeys.Count;
                SlotKeys.Add((("slot" + index).GetStableHashCode(), ("slot" + index).GetStableHashCode(), ("slotstatus" + index).GetStableHashCode()));
            }
            return SlotKeys[slot];
        }

        private static IEnumerable<CodeInstruction> StepOnRequest(IEnumerable<CodeInstruction> instructions) =>
            Transpilers.ReplaceOwnerCheck(instructions, typeof(CookingStation), AccessTools.Method(typeof(CookingProgress), nameof(StepRequested)), false);

        private static bool StepRequested(ZNetView view) => stepping && view.IsOwner();

        // The game repeats UpdateCooking every second on its own timer, which can fire slightly under a second apart. Each
        // run instead schedules the next one a full second later.
        private static void ReplaceTimer(CookingStation __instance)
        {
            if (!__instance.IsInvoking("UpdateCooking")) return;
            __instance.CancelInvoke("UpdateCooking");
            __instance.Invoke("UpdateCooking", 0f);
        }

        // Only this timer and the owner's own steps run UpdateCooking, so a run that went ahead outside a step was the
        // timer's; asking Unity whether one is pending would search every pending invoke in the scene.
        private static void ScheduleNext(CookingStation __instance, bool __runOriginal)
        {
            if (__runOriginal && !stepping && __instance && View(__instance) && View(__instance).IsValid())
                __instance.Invoke("UpdateCooking", RunSeconds);
        }

        // The fire check found here is reused by the game's own check later in the same run.
        private static CookingStation fireStation;
        private static int fireFrame = -1;
        private static bool fireLit;

        private static bool Lit(CookingStation station)
        {
            bool fire = false;
            if (station.m_requireFire)
            {
                fireStation = null;
                fire = IsFireLit(station);
                fireStation = station; fireFrame = UnityEngine.Time.frameCount; fireLit = fire;
            }
            return fire || station.m_useFuel && Fuel(station) > 0f && (station.m_useFueldWhileEmpty || HaveUncookedItem(station));
        }

        private static float Fuel(CookingStation station) => View(station).GetZDO().GetFloat(ZDOVars.s_fuel);

        private static bool Evaluate(CookingStation __instance)
        {
            var view = View(__instance);
            if (stepping || !view || !view.IsValid()) return true;
            var state = States.GetOrCreateValue(__instance);
            // Unity can run an invoke a few milliseconds early; such a run waits until a full second has passed.
            double sinceRun = UnityEngine.Time.timeAsDouble - state.LastRun;
            if (state.LastRun > 0d && sinceRun < RunSeconds)
            {
                __instance.Invoke("UpdateCooking", (float)(RunSeconds - sinceRun) + 0.001f);
                return false;
            }
            state.LastRun = UnityEngine.Time.timeAsDouble;
            Advance(__instance, view, state);
            return true;
        }

        private static void Advance(CookingStation __instance, ZNetView view, State state)
        {
            var zdo = view.GetZDO();
            long now = ZNet.instance.GetTime().Ticks, start = zdo.GetLong(ZDOVars.s_startTime, 0L);
            bool lit = Lit(__instance), paused = zdo.GetBool(PausedHash);
            float elapsed = start == 0L ? 0f : Math.Max(0f, (float)((now - start) / (double)TimeSpan.TicksPerSecond));
            state.Elapsed = lit && !paused ? elapsed : 0f;
            if (!view.IsOwner()) { state.Seen = false; return; }
            bool first = !state.Seen;
            state.Seen = true;
            if (!lit)
            {
                // Heating stopped: count the time it was still lit, then record nothing until it starts again. A station
                // first seen unlit counts nothing, as the game's own step would.
                if (!paused)
                {
                    if (!first && state.WasLit) Step(__instance, state.LitAt);
                    else zdo.Set(ZDOVars.s_startTime, now);
                    zdo.Set(PausedHash, true);
                }
                state.WasLit = false;
                state.Elapsed = 0f;
                return;
            }
            state.WasLit = true;
            state.LitAt = now;
            if (paused || start == 0L)
            {
                // Heating started: progress counts from now.
                zdo.Set(ZDOVars.s_startTime, now);
                if (paused) zdo.Set(PausedHash, false);
                state.Elapsed = 0f;
                return;
            }
            if (first || Due(__instance, zdo, elapsed)) { Step(__instance, 0L); state.Elapsed = 0f; }
        }

        // A step is due when food turns done or burnt, when an oven burns a visible unit of fuel or runs out, or at a
        // checkpoint while anything is cooking.
        private static bool Due(CookingStation station, ZDO zdo, float elapsed)
        {
            bool cooking = false;
            if (station.m_useFuel && station.m_secPerFuel > 0)
            {
                float fuel = zdo.GetFloat(ZDOVars.s_fuel), left = fuel - elapsed / station.m_secPerFuel;
                if (left <= 0f || Math.Ceiling(left) < Math.Ceiling(fuel)) return true;
                cooking = true;
            }
            for (int slot = 0; slot < station.m_slots.Length; slot++)
            {
                var keys = Keys(slot);
                string name = zdo.GetString(keys.Name);
                int status = zdo.GetInt(keys.Status);
                if (name == "" || status == 2) continue;
                var conversion = Conversion(station, name);
                if (conversion == null) return true;
                float cooked = zdo.GetFloat(keys.Time) + elapsed;
                if (station.m_canOvercookItems && cooked > conversion.m_cookTime * 2f || status == 0 && cooked > conversion.m_cookTime) return true;
                if (status == 0 || station.m_canOvercookItems) cooking = true;
            }
            return cooking && elapsed >= CheckpointSeconds;
        }

        private static void Step(CookingStation station, long litUntil)
        {
            if (stepping) return;
            stepping = true; crediting = litUntil != 0L; creditUntil = litUntil;
            try { UpdateCooking(station); }
            finally { stepping = false; crediting = false; creditUntil = 0L; }
        }

        private static bool StillLit(CookingStation __instance, ref bool __result)
        {
            if (crediting) __result = true;
            else if (ReferenceEquals(fireStation, __instance) && fireFrame == UnityEngine.Time.frameCount) __result = fireLit;
            else return true;
            return false;
        }

        private static bool CreditLitTime(CookingStation __instance, ref float __result)
        {
            if (!crediting) return true;
            var zdo = View(__instance).GetZDO();
            long start = zdo.GetLong(ZDOVars.s_startTime, creditUntil);
            zdo.Set(ZDOVars.s_startTime, ZNet.instance.GetTime().Ticks);
            __result = Math.Max(0f, (float)((creditUntil - start) / (double)TimeSpan.TicksPerSecond));
            return false;
        }

        // Food added to a heating station starts from zero: earlier progress is recorded first.
        private static void RecordBeforeAdding(CookingStation __instance)
        {
            var view = View(__instance);
            if (!view || !view.IsValid() || !view.IsOwner() || view.GetZDO().GetBool(PausedHash) || !Lit(__instance)) return;
            Step(__instance, 0L);
            if (States.TryGetValue(__instance, out var state)) state.Elapsed = 0f;
        }

        // The cooked time a display should show for a slot this second: the recorded time plus the time since the last
        // step while the station heats, for food that is still cooking or can still burn.
        internal static float Displayed(CookingStation station, int slot, float recorded)
        {
            if (!station || !States.TryGetValue(station, out var state) || state.Elapsed <= 0f) return recorded;
            var view = View(station);
            if (!view || !view.IsValid()) return recorded;
            int status = view.GetZDO().GetInt(Keys(slot).Status);
            return status == 0 || status == 1 && station.m_canOvercookItems ? recorded + state.Elapsed : recorded;
        }
    }
}
