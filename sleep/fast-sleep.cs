using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace EpicLootProgressionFix
{
    // The night passes in about 2.5 seconds instead of 20. The game moves the clock once a server frame by a fixed step
    // meant for 12 seconds, which the dedicated server's 30 frames a second stretch to 20, and looks for its end every 2
    // seconds. The server now holds the clock while the screens fade to black, moves it to morning over 1.5 seconds of real
    // time, sends it to every player each frame and wakes everyone as it arrives. The sleep and wake-up fades take half a
    // second. Stations, crops, eggs, growth, regrowth and the enemy pause read the time that has passed, so the night does
    // what it did; what read the night in steps keeps its steps:
    // - A smelter credits at most an hour a step, so a station that was running when the night began steps through it in
    //   five-minute slices.
    // - The 20-second night reached players in ten clock updates, and each zone's spawner rolled for the time since its last
    //   check; while asleep, a spawner is also checked whenever a tenth of the night has passed since then.
    // - Day, night and the new day's message follow the sun, which trails the clock by a hundredth a frame; while the night
    //   passes it stays on the clock, behind the black screen.
    // The sleep screen starts a dream cinematic and the unused-asset unload once it is black, where the game waited 4 and 5
    // seconds, and its ZZZ fades out before waking. A dream text would have under a second to show, so it stays hidden.
    // The model gets up only from the looping sleep pose, and lying down takes about 2.7 seconds, so a night that begins as
    // the player lies down, as LetMeSleep's begins for its last sleeper, would end before it: the player left the bed while
    // the model stayed lying. The player then wakes, behind the black screen, once the model lies in the looping pose.
    internal static class FastSleep
    {
        internal const float FadeSeconds = .5f;
        private const double HoldSeconds = .5, SkipSeconds = 1.5;
        private const float ScreenBlackSeconds = .6f, ZzzHideSeconds = 1f, ZzzFadeSeconds = .4f;
        private static readonly long SliceTicks = TimeSpan.FromMinutes(5).Ticks;
        // A running station steps every 5 seconds of game time; one whose last step is older than this when the night
        // begins was not running, and keeps the game's own catch-up limit.
        private static readonly long RunningTicks = TimeSpan.FromSeconds(30).Ticks;

        private static readonly AccessTools.FieldRef<EnvMan, bool> Skipping = AccessTools.FieldRefAccess<EnvMan, bool>("m_skipTime");
        private static readonly AccessTools.FieldRef<EnvMan, double> SkipTarget = AccessTools.FieldRefAccess<EnvMan, double>("m_skipToTime");
        private static readonly AccessTools.FieldRef<EnvMan, float> SmoothDayFraction = AccessTools.FieldRefAccess<EnvMan, float>("m_smoothDayFraction");
        private static readonly Func<EnvMan, float, float> RescaleDayFraction = AccessTools.MethodDelegate<Func<EnvMan, float, float>>(AccessTools.Method(typeof(EnvMan), "RescaleDayFraction"));
        private static readonly Action<Game> UpdateSleeping = AccessTools.MethodDelegate<Action<Game>>(AccessTools.Method(typeof(Game), "UpdateSleeping"));
        private static readonly AccessTools.FieldRef<Game, bool> Sleeping = AccessTools.FieldRefAccess<Game, bool>("m_sleeping");
        private static readonly Action<ZNet> SendNetTime = AccessTools.MethodDelegate<Action<ZNet>>(AccessTools.Method(typeof(ZNet), "SendNetTime"));
        private static readonly AccessTools.FieldRef<Smelter, ZNetView> SmelterView = AccessTools.FieldRefAccess<Smelter, ZNetView>("m_nview");
        private static readonly Action<Smelter> StepSmelter = AccessTools.MethodDelegate<Action<Smelter>>(AccessTools.Method(typeof(Smelter), "UpdateSmelter"));
        private static readonly AccessTools.FieldRef<List<SpawnSystem>> SpawnSystems = AccessTools.StaticFieldRefAccess<List<SpawnSystem>>(AccessTools.Field(typeof(SpawnSystem), "m_instances"));
        private static readonly Action<SpawnSystem> CheckSpawns = AccessTools.MethodDelegate<Action<SpawnSystem>>(AccessTools.Method(typeof(SpawnSystem), "UpdateSpawning"));
        // Each loaded spawner's last check tonight, in game ticks.
        private static readonly Dictionary<SpawnSystem, long> SpawnsChecked = new();
        private static readonly List<SpawnSystem> SpawnsDue = new();
        private static long spawnSlice;

        // The server's skip: the real time it began, and the game time it runs from and to.
        private static long skipStarted;
        private static double skipFrom, skipTo = double.NaN;
        // This game's last night in game ticks, from its sleep start to its wake-up.
        private static long nightStarted = long.MaxValue, nightEnded = long.MaxValue;
        private static bool asleep, waking, slicing;

        internal static void Initialize(IntegrationPatches patches)
        {
            patches.Patch(AccessTools.Method(typeof(EnvMan), nameof(EnvMan.SkipToMorning)), postfix: new HarmonyMethod(typeof(FastSleep), nameof(SkipStarted)));
            patches.Patch(AccessTools.Method(typeof(EnvMan), "UpdateTimeSkip"), prefix: new HarmonyMethod(typeof(FastSleep), nameof(Skip)));
            patches.Patch(AccessTools.Method(typeof(EnvMan), "FixedUpdate"), prefix: new HarmonyMethod(typeof(FastSleep), nameof(FollowNight)));
            patches.Patch(AccessTools.Method(typeof(Game), "SleepStart"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(Slept)));
            patches.Patch(AccessTools.Method(typeof(Game), "SleepStop"), prefix: new HarmonyMethod(typeof(FastSleep), nameof(Waking)), postfix: new HarmonyMethod(typeof(FastSleep), nameof(Woke)),
                transpiler: new HarmonyMethod(typeof(FastSleep), nameof(WakeLyingDown)));
            patches.Patch(AccessTools.Method(typeof(Hud), "GetFadeDuration"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(Fade)));
            patches.Patch(AccessTools.Method(typeof(Hud), "UpdateBlackScreen"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(ScreenUpdated)));
            patches.Patch(AccessTools.Method(typeof(SleepText), "OnEnable"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(SleepScreen)));
            patches.Patch(AccessTools.Method(typeof(SleepText), "HideZZZ"), prefix: new HarmonyMethod(typeof(FastSleep), nameof(HideZzz)));
            patches.Patch(AccessTools.Method(typeof(SleepText), "ShowDreamText"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(NoDreamText)));
            patches.Patch(AccessTools.Method(typeof(Smelter), "UpdateSmelter"), prefix: new HarmonyMethod(typeof(FastSleep), nameof(SliceNight)) { priority = Priority.First });
            patches.Patch(AccessTools.Method(typeof(SpawnSystem), "UpdateSpawning"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(SpawnsWereChecked)));
            patches.Patch(AccessTools.Method(typeof(ZNet), "Update"), postfix: new HarmonyMethod(typeof(FastSleep), nameof(CheckSpawnsThroughNight)));
            patches.Patch(AccessTools.Method(typeof(ZNet), nameof(ZNet.Shutdown)), prefix: new HarmonyMethod(typeof(FastSleep), nameof(LeaveWorld)));
        }

        private static void LeaveWorld()
        {
            nightStarted = nightEnded = long.MaxValue;
            skipTo = double.NaN;
            asleep = waking = false;
            lyingDown = null;
            SpawnsChecked.Clear();
        }

        private static void SkipStarted(EnvMan __instance)
        {
            if (!ZNet.instance.IsServer() || !Skipping(__instance)) return;
            skipStarted = Stopwatch.GetTimestamp();
            skipFrom = ZNet.instance.GetTimeSeconds();
            skipTo = SkipTarget(__instance);
        }

        private static bool Skip(EnvMan __instance)
        {
            if (!ZNet.instance.IsServer() || !Skipping(__instance)) return true;
            // A skip this hook did not see begin starts its schedule now.
            if (SkipTarget(__instance) != skipTo) SkipStarted(__instance);
            double elapsed = (Stopwatch.GetTimestamp() - skipStarted) / (double)Stopwatch.Frequency;
            double progress = (elapsed - HoldSeconds) / SkipSeconds;
            if (progress <= 0d) return false;
            double now = ZNet.instance.GetTimeSeconds();
            double time = progress >= 1d ? skipTo : Math.Max(now, skipFrom + (skipTo - skipFrom) * progress);
            if (time >= skipTo)
            {
                time = skipTo;
                Skipping(__instance) = false;
            }
            ZNet.instance.SetNetTime(time);
            SendNetTime(ZNet.instance);
            // The game looks for the end of the skip every 2 seconds; wake everyone now, after the final time has gone out.
            // Only a night the players slept through ends here; any other skip leaves its next step to the game's own look.
            if (!Skipping(__instance) && Game.instance && Sleeping(Game.instance)) UpdateSleeping(Game.instance);
            return false;
        }

        private static void Slept(bool __runOriginal)
        {
            if (!__runOriginal) return;
            nightStarted = ZNet.instance.GetTime().Ticks;
            asleep = true;
            waking = false;
            SpawnsChecked.Clear();
            spawnSlice = 0L;
            var environment = EnvMan.instance;
            if (!environment) return;
            // The morning the server skips to, as EnvMan.SkipToMorning finds it.
            double now = ZNet.instance.GetTimeSeconds();
            double morning = environment.GetMorningStartSec(environment.GetDay(now - environment.m_dayLengthSec * .15f) + 1);
            spawnSlice = TimeSpan.FromSeconds(Math.Max(0d, morning - now) / 10d).Ticks;
            // Each spawner checks once as the night begins, as it would within the second, so the tenths start from now.
            if (!Player.m_localPlayer) return;
            SpawnsDue.Clear();
            SpawnsDue.AddRange(SpawnSystems());
            foreach (var system in SpawnsDue) if (system) CheckSpawns(system);
            SpawnsDue.Clear();
        }

        private static readonly AccessTools.FieldRef<Player, float> CoverPercentage = AccessTools.FieldRefAccess<Player, float>("m_coverPercentage");
        private static readonly AccessTools.FieldRef<Player, bool> UnderRoof = AccessTools.FieldRefAccess<Player, bool>("m_underRoof");
        private static readonly AccessTools.FieldRef<Player, int> ComfortLevel = AccessTools.FieldRefAccess<Player, int>("m_comfortLevel");

        // Rested takes its length from the comfort the game measures every 2 seconds, with shelter every second. A 20-second
        // night left both measured in the bed; a player who lay down just after coming in could now wake with comfort from
        // outside, so both are measured again as the night ends.
        private static void Waking()
        {
            var player = Player.m_localPlayer;
            if (!player || !player.IsSleeping()) return;
            Cover.GetCoverForPoint(player.GetCenterPoint(), out CoverPercentage(player), out UnderRoof(player));
            ComfortLevel(player) = SE_Rested.CalculateComfortLevel(player);
        }

        private static void Woke(bool __runOriginal)
        {
            if (!__runOriginal) return;
            nightEnded = ZNet.instance.GetTime().Ticks;
            asleep = false;
            SpawnsChecked.Clear();
            var player = Player.m_localPlayer;
            waking = player && !player.IsDead() && !player.IsTeleporting();
            // The ZZZ fades out on game time, which a long frame such as the asset unload holds back; the sleep screen stays
            // up while the world fades in, so what is left of it goes as the night ends.
            var sleepText = Hud.instance ? Hud.instance.m_sleepingProgress.GetComponentInChildren<SleepText>(true) : null;
            if (sleepText) sleepText.m_textField.CrossFadeAlpha(0f, 0f, ignoreTimeScale: true);
            var environment = EnvMan.instance;
            if (!environment) return;
            FollowClock(environment);
            environment.ForceInstantEnvironmentSwitch();
        }

        // The fixed update moves the sun toward the time of day by a hundredth a frame, and day, night and afternoon follow
        // the sun. A fast night would leave it hours behind the clock, so night-only rules would see day during the night and
        // the sun would sweep across the sky after waking; while the night passes it stays on the clock, behind the black screen.
        private static void FollowNight(EnvMan __instance)
        {
            if (asleep || ZNet.instance && ZNet.instance.IsServer() && Skipping(__instance)) FollowClock(__instance);
        }

        // The game's evening and morning, with the new day's message, music and survived-day count, fire when the sun passes
        // them in a small step. The sun passing one during the night gets the game's own check for such a step; the night
        // ends on morning, so the sun stops just short of it and the game's own step passes it after waking.
        private static readonly float[] DayTriggers = { .25f, .75f };
        private const float TriggerMargin = .0001f;
        // A night moves the sun at most three quarters of a turn, so a clock further ahead is in fact just behind the sun.
        private const float LargestNight = .9f;
        private static readonly Func<EnvMan, BiomeSector> CurrentBiome = AccessTools.MethodDelegate<Func<EnvMan, BiomeSector>>(AccessTools.Method(typeof(EnvMan), "GetBiome"));
        private static readonly Action<EnvMan, float, float, Heightmap.Biome, float> DayTriggered =
            AccessTools.MethodDelegate<Action<EnvMan, float, float, Heightmap.Biome, float>>(AccessTools.Method(typeof(EnvMan), "UpdateTriggers"));

        private static void FollowClock(EnvMan environment)
        {
            if (!ZNet.instance) return;
            long length = environment.m_dayLengthSec;
            float fraction = Mathf.Clamp01((float)(ZNet.instance.GetTimeSeconds() * 1000d % (length * 1000L) / 1000d) / length);
            float sun = SmoothDayFraction(environment), target = RescaleDayFraction(environment, fraction);
            float ahead = Mathf.Repeat(target - sun, 1f);
            if (ahead <= 0f || ahead >= LargestNight) return;
            // A long frame can carry the clock past both, so they pass in the order the sun meets them.
            float first = DayTriggers[0], second = DayTriggers[1];
            if (Mathf.Repeat(second - sun, 1f) < Mathf.Repeat(first - sun, 1f)) (first, second) = (second, first);
            foreach (float trigger in new[] { first, second })
            {
                float toTrigger = Mathf.Repeat(trigger - sun, 1f);
                if (toTrigger <= 0f || toTrigger > ahead) continue;
                if (ahead - toTrigger > TriggerMargin)
                {
                    DayTriggered(environment, trigger - TriggerMargin, trigger + TriggerMargin, CurrentBiome(environment).Biome, 0f);
                    continue;
                }
                target = Mathf.Repeat(trigger - TriggerMargin, 1f);
                break;
            }
            SmoothDayFraction(environment) = target;
        }

        private static void SpawnsWereChecked(SpawnSystem __instance)
        {
            if (asleep) SpawnsChecked[__instance] = ZNet.instance.GetTime().Ticks;
        }

        private static void CheckSpawnsThroughNight()
        {
            // A spawner runs only in a game with a player, so the dedicated server has none to check.
            if (!asleep || spawnSlice <= 0L || SpawnsChecked.Count == 0 || !Player.m_localPlayer) return;
            long now = ZNet.instance.GetTime().Ticks;
            SpawnsDue.Clear();
            foreach (var system in SpawnSystems())
                if (system && SpawnsChecked.TryGetValue(system, out long last) && now - last >= spawnSlice) SpawnsDue.Add(system);
            foreach (var system in SpawnsDue) if (system) CheckSpawns(system);
            SpawnsDue.Clear();
        }

        private static void Fade(Player player, ref float __result)
        {
            if (!player || player.IsDead() || player.IsTeleporting()) return;
            if (player.IsSleeping() || waking) __result = FadeSeconds;
        }

        private static readonly AccessTools.FieldRef<Character, Animator> Animator = AccessTools.FieldRefAccess<Character, Animator>("m_animator");
        // Lying down takes about 2.7 seconds; a model that never reaches the looping pose does not hold the wake longer.
        private const float LyingDownLimit = 3f;
        private static Player lyingDown;
        private static float lyingDownSince;

        // The game's wake sets the player awake and takes them out of bed; both go through here.
        private static IEnumerable<CodeInstruction> WakeLyingDown(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var setSleeping = AccessTools.Method(typeof(Player), nameof(Player.SetSleeping));
            // Player overrides AttachStop, so the game calls the character's virtual method.
            var attachStop = AccessTools.Method(typeof(Character), nameof(Character.AttachStop));
            var wakes = code.Where(instruction => instruction.Calls(setSleeping)).ToArray();
            var leaves = code.Where(instruction => instruction.Calls(attachStop)).ToArray();
            if (wakes.Length != 1 || leaves.Length != 1) throw new InvalidOperationException("The game's wake-up changed.");
            wakes[0].opcode = leaves[0].opcode = OpCodes.Call;
            wakes[0].operand = AccessTools.Method(typeof(FastSleep), nameof(SetAwake));
            leaves[0].operand = AccessTools.Method(typeof(FastSleep), nameof(LeaveBed));
            return code;
        }

        private static bool StillLyingDown(Player player)
        {
            if (!player.InBed() || !player.IsAttached()) return false;
            var animator = Animator(player);
            return animator && animator.isActiveAndEnabled && (animator.IsInTransition(0) || !animator.GetCurrentAnimatorStateInfo(0).loop);
        }

        private static void SetAwake(Player player, bool sleep)
        {
            if (!sleep && player == Player.m_localPlayer && StillLyingDown(player))
            {
                lyingDown = player;
                lyingDownSince = Time.unscaledTime;
                return;
            }
            player.SetSleeping(sleep);
        }

        private static void LeaveBed(Character character)
        {
            if (character != lyingDown) character.AttachStop();
        }

        private static void ScreenUpdated(Player player, CanvasGroup ___m_loadingScreen)
        {
            if (lyingDown && (lyingDown != player || !StillLyingDown(lyingDown) || Time.unscaledTime - lyingDownSince >= LyingDownLimit))
            {
                var sleeper = lyingDown;
                lyingDown = null;
                if (sleeper && sleeper.IsSleeping())
                {
                    sleeper.SetSleeping(false);
                    sleeper.AttachStop();
                    waking = !sleeper.IsDead() && !sleeper.IsTeleporting();
                }
            }
            else if (lyingDown) return;
            if (waking && (!player || player.IsDead() || player.IsTeleporting() || player.IsSleeping() || ___m_loadingScreen.alpha <= 0f)) waking = false;
        }

        private static void SleepScreen(SleepText __instance)
        {
            foreach (string step in new[] { "CollectResources", "HideZZZ", "ShowDreamText" }) __instance.CancelInvoke(step);
            __instance.Invoke("CollectResources", ScreenBlackSeconds);
            __instance.Invoke("ShowDreamText", ScreenBlackSeconds);
            __instance.Invoke("HideZZZ", ZzzHideSeconds);
        }

        private static bool HideZzz(SleepText __instance)
        {
            __instance.m_textField.CrossFadeAlpha(0f, ZzzFadeSeconds, ignoreTimeScale: true);
            return false;
        }

        private static void NoDreamText(SleepText __instance)
        {
            __instance.CancelInvoke("DelayedCrossFadeStart");
            __instance.CancelInvoke("HideDreamText");
            __instance.m_dreamField.enabled = false;
        }

        // The station was running when the night began and has stepped since, during the night or just after waking.
        private static bool SteppedThroughNight(long last, long now) =>
            nightStarted != long.MaxValue && last >= nightStarted - RunningTicks && (asleep ? last <= now : last <= nightEnded && now <= nightEnded + RunningTicks);

        private static bool SliceNight(Smelter __instance)
        {
            if (slicing) return true;
            var view = SmelterView(__instance);
            if (!view || !view.IsValid() || !view.IsOwner()) return true;
            var data = view.GetZDO();
            long last = data.GetLong(ZDOVars.s_startTime, 0L), now = ZNet.instance.GetTime().Ticks, span = now - last;
            if (last == 0L || span <= SliceTicks || !SteppedThroughNight(last, now)) return true;
            int slices = (int)Math.Ceiling(span / (double)SliceTicks);
            long credited = 0L;
            slicing = true;
            try
            {
                for (int slice = 1; slice <= slices; slice++)
                {
                    long through = (long)(span * (slice / (double)slices));
                    if (slice == slices) through = span;
                    data.Set(ZDOVars.s_startTime, now - (through - credited));
                    StepSmelter(__instance);
                    if (!view.IsValid()) break;
                    if (data.GetLong(ZDOVars.s_startTime, 0L) != now)
                    {
                        // The station did not take the step: leave it the time still owed, as before the slices.
                        data.Set(ZDOVars.s_startTime, now - (span - credited));
                        break;
                    }
                    credited = through;
                }
            }
            finally { slicing = false; }
            return false;
        }
    }
}
