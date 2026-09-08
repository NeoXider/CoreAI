using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// MVP2 clock model (roadmap §5.2.9 criterion 6), the scaled-time half: at
    /// <c>timeScale = 0.5</c>, <c>task.wait(1)</c> takes ~2 real seconds and returns ~1;
    /// <c>os.time()</c> is unaffected by timeScale; <c>time()</c> advances with scaled time;
    /// <c>GetServerTimeNow()</c> is unscaled and epoch-comparable with <c>os.time()</c>.
    /// <see cref="RbxClockLuaBindingsEditModeTests"/> covers every clock in the UNSCALED case and
    /// <c>GetServerTimeNow()</c> monotonicity; this fixture is the ONLY one that ever drives
    /// <see cref="LuaCsRbxApiBindings.Scheduler"/> with a non-unity scale, so it is the sole gate
    /// on the scaled half of criterion 6. There is no <c>timeScale</c> parameter on the scheduler
    /// itself — <see cref="CoreAI.Mods.Rbx.Instances.Scheduling.ModScheduler.Advance"/> only ever
    /// takes a SCALED delta — so a scale is simulated the same way the production frame driver
    /// would apply it: by halving the real per-frame delta before calling Advance.
    /// </summary>
    [TestFixture]
    public sealed class RbxScaledTimeEditModeTests
    {
        private SynchronizationContext _savedContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        /// <summary>
        /// Drives <paramref name="modId"/>'s <c>task.wait(1)</c> to resumption by repeatedly
        /// advancing the scheduler with <paramref name="frameRealDelta"/> scaled by
        /// <paramref name="scale"/> — the same per-frame amount a real frame driver running at
        /// timeScale <paramref name="scale"/> would feed in. Returns the frame count needed to
        /// resume plus the elapsed/time() values the mod recorded at resumption.
        /// </summary>
        private static (int frames, string elapsed, string timeNow) DriveTaskWaitOneSecond(
            double scale, double frameRealDelta, MemoryStore store, string modId)
        {
            LuaCsRbxApiBindings bindings = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod(modId, @"
                store_set('phase', 'waiting')
                local elapsed = task.wait(1)
                store_set('elapsed', tostring(elapsed))
                store_set('time_now', tostring(time()))
                store_set('phase', 'resumed')");

            int frames = 0;
            while (store.Get(modId, "phase") != "resumed")
            {
                bindings.Scheduler.Advance(frameRealDelta * scale);
                frames++;
                Assert.Less(frames, 1000,
                    "task.wait(1) never resumed at scale " + scale.ToString(CultureInfo.InvariantCulture));
            }

            return (frames, store.Get(modId, "elapsed"), store.Get(modId, "time_now"));
        }

        [Test]
        public void Lua_TaskWait_AtHalfScale_TakesTwiceTheFramesOfFullScale_AndReturnsScaledElapsed()
        {
            // WHY 0.125s: an exact binary fraction (1/8), so repeated addition at full and half
            // scale (1/16) never accumulates floating-point drift — the frame counts below are
            // exact, not approximate, so the doubling assertion isn't hiding behind a tolerance.
            const double frameRealDelta = 0.125d;

            (int frames, string elapsed, string timeNow) fullScale =
                DriveTaskWaitOneSecond(scale: 1.0d, frameRealDelta, new MemoryStore(), "full-scale");
            (int frames, string elapsed, string timeNow) halfScale =
                DriveTaskWaitOneSecond(scale: 0.5d, frameRealDelta, new MemoryStore(), "half-scale");

            // (i) same real per-frame delta; half the scale must need EXACTLY twice the frames to
            // accumulate the same 1s of SCALED elapsed time. This is the "~2 real seconds" proof
            // at a fixed frame rate: the assertion is the doubling itself, not a magic frame count.
            Assert.AreEqual(fullScale.frames * 2, halfScale.frames,
                "half timeScale must take exactly twice the frames of full timeScale to satisfy the same wait");

            // (ii) task.wait(1) returns the SCALED elapsed (~1), not the real elapsed (which would
            // be ~1 at full scale but ~2 at half scale if it were wall-clock).
            Assert.AreEqual(1d, double.Parse(fullScale.elapsed, CultureInfo.InvariantCulture));
            Assert.AreEqual(1d, double.Parse(halfScale.elapsed, CultureInfo.InvariantCulture));

            // (iii) time() advances by the scaled amount, matching the returned elapsed exactly at
            // either scale — if timeScale leaked out of Advance()'s scaled-delta contract, this
            // would drift from the elapsed value above.
            Assert.AreEqual(1d, double.Parse(fullScale.timeNow, CultureInfo.InvariantCulture));
            Assert.AreEqual(1d, double.Parse(halfScale.timeNow, CultureInfo.InvariantCulture));
        }

        [Test]
        public void Lua_OsTime_UnaffectedByTimeScale_WhileGetServerTimeNowStaysEpochComparable()
        {
            // WHY a fixed injected epoch instead of the real system clock: os.time()/
            // GetServerTimeNow() must stay pinned to the clock SOURCE regardless of how the
            // scheduler is driven, and a fixed instant makes that a deterministic equality check
            // instead of a real-wall-clock race.
            DateTimeOffset fixedInstant = DateTimeOffset.FromUnixTimeMilliseconds(1700000000250L);
            LuaCsRbxApiBindings bindings = new(utcNowProvider: () => fixedInstant);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("before", @"
                store_set('os_time', tostring(os.time()))
                store_set('server_time', tostring(workspace:GetServerTimeNow()))");

            Assert.AreEqual("1700000000", store.Get("before", "os_time"));
            Assert.AreEqual("1700000000.25", store.Get("before", "server_time"));

            // WHY 20 frames of 1/16s (exact binary fraction): drives 1.25s of SCALED time — as if
            // timeScale were held at 0.5 against a 0.125s real frame — through the scheduler.
            // If timeScale ever leaked into os.time()/GetServerTimeNow(), these would drift with
            // it instead of staying pinned to the injected epoch source.
            for (int frame = 0; frame < 20; frame++)
            {
                bindings.Scheduler.Advance(0.0625d);
            }

            stack.Runtime.LoadMod("after", @"
                store_set('os_time', tostring(os.time()))
                store_set('server_time', tostring(workspace:GetServerTimeNow()))
                store_set('game_time', tostring(time()))");

            // (iv) os.time() is unaffected by timeScale: unchanged even though the scheduler moved.
            Assert.AreEqual("1700000000", store.Get("after", "os_time"));
            Assert.AreEqual("1700000000.25", store.Get("after", "server_time"));

            // proves the scheduler DID move under scaled time, so the equalities above are a real
            // decoupling and not just "nothing was ever driven".
            Assert.AreEqual(1.25d, double.Parse(store.Get("after", "game_time"), CultureInfo.InvariantCulture));

            // (v) GetServerTimeNow() is unscaled and epoch-comparable with os.time(): |diff| <= 1s.
            double osTime = double.Parse(store.Get("after", "os_time"), CultureInfo.InvariantCulture);
            double serverTime = double.Parse(store.Get("after", "server_time"), CultureInfo.InvariantCulture);
            Assert.LessOrEqual(Math.Abs(serverTime - osTime), 1d);
        }
    }
}
