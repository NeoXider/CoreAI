using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// Proof that signal handlers run on pooled runner threads through the REAL mod runtime: a
    /// handler that returns leaves no live scheduler thread and the next fire reuses the runner; a
    /// handler that yields keeps its thread and resumes; a handler that throws retires its runner; two
    /// mods never share a runner.
    /// </summary>
    [TestFixture]
    public sealed class RbxSignalHandlerThreadReuseEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling RunService fixture: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
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

        /// <summary>Drives one host frame: fire the phase signals, then drain and advance scaled time.</summary>
        private static void PumpFrame(LuaCsRbxApiBindings roblox, float dt)
        {
            roblox.PumpFrame(dt);
            roblox.Scheduler.Advance(dt);
        }

        [Test]
        public void Lua_HeartbeatHandlerThatReturns_LeavesNoLiveThreadAndReusesOneRunner()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local n = 0
                rs.Heartbeat:Connect(function(dt)
                    n = n + 1
                    store_set('n', tostring(n))
                end)");

            for (int frame = 0; frame < 10; frame++)
            {
                PumpFrame(roblox, 0.1f);
                Assert.AreEqual(0, roblox.Scheduler.LiveThreadCount,
                    "a handler that returned must leave no live scheduler thread (frame " + frame + ")");
            }

            Assert.AreEqual("10", store.Get("m", "n"));
            Assert.AreEqual(1, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "one parked runner serves every fire of a handler that never yields");
            Assert.AreEqual(9, roblox.SchedulerThreadFactory.SignalRunnersReused);
            Assert.AreEqual(1, roblox.Scheduler.PooledRecordCount,
                "the scheduler record is reused too, never accumulated");
        }

        [Test]
        public void Lua_TaskWaitInsideHeartbeatHandler_KeepsItsThreadAndResumesWithElapsed()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local fired = false
                rs.Heartbeat:Connect(function(dt)
                    if fired then
                        return
                    end
                    fired = true
                    store_set('phase', 'waiting')
                    local elapsed = task.wait(0.25)
                    store_set('phase', 'resumed')
                    store_set('elapsed', tostring(elapsed))
                end)");

            PumpFrame(roblox, 0.1f);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.AreEqual(1, roblox.Scheduler.LiveThreadCount,
                "a handler parked in task.wait is a live scheduler thread");

            PumpFrame(roblox, 0.1f);
            PumpFrame(roblox, 0.1f);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.AreEqual(1, roblox.Scheduler.LiveThreadCount);
            Assert.AreEqual(2, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "later fires cannot use the runner parked inside the yield, so a second one serves them");

            PumpFrame(roblox, 0.1f);
            Assert.AreEqual("resumed", store.Get("m", "phase"));
            double elapsed = double.Parse(store.Get("m", "elapsed"), CultureInfo.InvariantCulture);
            Assert.GreaterOrEqual(elapsed, 0.25d);
            Assert.AreEqual(0, roblox.Scheduler.LiveThreadCount,
                "the resumed handler returned, so its thread is gone");

            long createdBeforeReuse = roblox.SchedulerThreadFactory.SignalRunnersCreated;
            PumpFrame(roblox, 0.1f);
            PumpFrame(roblox, 0.1f);
            Assert.AreEqual(createdBeforeReuse, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "a runner whose handler yielded and then returned is parked again and reused");
        }

        [Test]
        public void Lua_HandlerThatThrows_RetiresItsRunnerAndTheNextHandlerRunsClean()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            List<string> errors = new();
            stack.Runtime.ModHandlerErrored += (modId, message, streak) =>
                errors.Add(modId + ": " + message);
            stack.Runtime.LoadMod("m", @"
                local rs = game:GetService('RunService')
                local n = 0
                rs.Heartbeat:Connect(function(dt)
                    n = n + 1
                    store_set('n', tostring(n))
                    if n == 1 then
                        error('boom')
                    end
                    store_set('clean', tostring(n))
                end)");

            PumpFrame(roblox, 0.1f);
            Assert.AreEqual(1, errors.Count, "the throwing handler is reported as a mod handler error");
            StringAssert.Contains("boom", errors[0]);
            Assert.AreEqual("", store.Get("m", "clean"));
            Assert.AreEqual(0, roblox.Scheduler.LiveThreadCount);

            PumpFrame(roblox, 0.1f);
            Assert.AreEqual("2", store.Get("m", "clean"), "the next fire runs to completion");
            Assert.AreEqual(1, errors.Count, "no error leaks from the dead runner into the next handler");
            Assert.AreEqual(2, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "the runner killed by the error is never reused; the next handler got a fresh one");

            PumpFrame(roblox, 0.1f);
            Assert.AreEqual("3", store.Get("m", "clean"));
            Assert.AreEqual(2, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "the fresh runner is reused from then on");
        }

        [Test]
        public void Lua_TwoMods_NeverShareARunnerOrObserveEachOthersState()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);
            stack.Runtime.LoadMod("a", @"
                local rs = game:GetService('RunService')
                rs.Heartbeat:Connect(function(dt)
                    _G.leak = 'set by a'
                    store_set('thread', tostring(coroutine.running()))
                end)");
            stack.Runtime.LoadMod("b", @"
                local rs = game:GetService('RunService')
                rs.Heartbeat:Connect(function(dt)
                    store_set('thread', tostring(coroutine.running()))
                    store_set('leak', tostring(rawget(_G, 'leak')))
                end)");

            PumpFrame(roblox, 0.1f);
            string aFirst = store.Get("a", "thread");
            string bFirst = store.Get("b", "thread");
            Assert.IsNotEmpty(aFirst);
            Assert.IsNotEmpty(bFirst);
            Assert.AreNotEqual(aFirst, bFirst, "each mod runs its handlers on its own runner");
            Assert.AreEqual("nil", store.Get("b", "leak"),
                "state written by mod a's handler is invisible to mod b's handler");

            PumpFrame(roblox, 0.1f);
            PumpFrame(roblox, 0.1f);
            Assert.AreEqual(aFirst, store.Get("a", "thread"), "mod a keeps reusing its own runner");
            Assert.AreEqual(bFirst, store.Get("b", "thread"), "mod b keeps reusing its own runner");
            Assert.AreEqual("nil", store.Get("b", "leak"));
            Assert.AreEqual(2, roblox.SchedulerThreadFactory.SignalRunnersCreated,
                "exactly one runner per mod, never one shared across mods");
            Assert.AreEqual(0, roblox.Scheduler.LiveThreadCount);
        }
    }
}
