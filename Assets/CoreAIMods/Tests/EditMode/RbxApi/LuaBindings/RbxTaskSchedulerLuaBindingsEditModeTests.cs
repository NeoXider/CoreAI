using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>Lua-level coverage for the MVP2 task scheduler bindings.</summary>
    [TestFixture]
    public sealed class RbxTaskSchedulerLuaBindingsEditModeTests
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
            public void LogDebug(GameLogFeature feature, string message,
                Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message,
                Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message,
                Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message,
                Object context = null)
            {
            }
        }

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings bindings,
            MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings
            });
        }

        [Test]
        public void Lua_ModMainChunk_TaskWait_SuspendsAndResumesOnLaterFrame()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                store_set('phase', 'waiting')
                local elapsed = task.wait(0.25)
                store_set('phase', 'resumed:' .. tostring(elapsed))");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("waiting", store.Get("m", "phase"));

            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));

            bindings.Scheduler.Advance(0.15d);
            Assert.AreEqual("resumed:0.25", store.Get("m", "phase"));
        }

        [Test]
        public void Lua_ModMainChunk_NonYieldingError_StillThrowsFromLoadMod()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            bindings.Scheduler.ThreadFaulted += (string modId, RbxError error) => { };

            System.Exception error = Assert.Catch<System.Exception>(() =>
                stack.Runtime.LoadMod("broken", @"
                    local total = 0
                    for index = 1, 20000 do
                        total = total + index
                    end
                    error('synchronous load failure:' .. tostring(total))"));

            Assert.IsNotNull(error);
            StringAssert.Contains("synchronous load failure", error.ToString());
            Assert.IsFalse(stack.Runtime.IsLoaded("broken"));
        }

        [Test]
        public void Lua_FailedLoad_RollsBackCandidateTasksAndConnections()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            System.Exception error = Assert.Catch<System.Exception>(() =>
                stack.Runtime.LoadMod("broken", @"
                    local run_service = game:GetService('RunService')
                    run_service.Heartbeat:Connect(function()
                        store_set('connection_leak', 'ran')
                    end)
                    task.delay(0, function()
                        store_set('task_leak', 'ran')
                    end)
                    error('candidate load failed')"));

            Assert.IsNotNull(error);
            StringAssert.Contains("candidate load failed", error.ToString());
            Assert.IsFalse(stack.Runtime.IsLoaded("broken"));

            bindings.Scheduler.Advance(0d);
            bindings.PumpHeartbeat(0.1f);
            Assert.AreEqual("", store.Get("broken", "task_leak"));
            Assert.AreEqual("", store.Get("broken", "connection_leak"));
            Assert.IsFalse(bindings.RunService.Heartbeat.HasConnections);
        }

        [Test]
        public void Lua_FailedReload_RollsBackCandidateAndPreservesOutgoingGeneration()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                store_set('outgoing', 'waiting')
                task.wait(0.25)
                store_set('outgoing', 'resumed')");

            System.Exception error = Assert.Catch<System.Exception>(() =>
                stack.Runtime.ReloadMod("m", @"
                    local run_service = game:GetService('RunService')
                    run_service.Heartbeat:Connect(function()
                        store_set('candidate_connection', 'ran')
                    end)
                    task.delay(0, function()
                        store_set('candidate_task', 'ran')
                    end)
                    error('candidate reload failed')"));

            Assert.IsNotNull(error);
            StringAssert.Contains("candidate reload failed", error.ToString());
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            bindings.Scheduler.Advance(0.25d);
            bindings.PumpHeartbeat(0.1f);
            Assert.AreEqual("resumed", store.Get("m", "outgoing"));
            Assert.AreEqual("", store.Get("m", "candidate_task"));
            Assert.AreEqual("", store.Get("m", "candidate_connection"));
            Assert.IsFalse(bindings.RunService.Heartbeat.HasConnections);
        }

        [Test]
        public void Lua_ModMainChunk_PostWaitError_ReachesSchedulerFaultChannel()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            RbxError fault = null;
            bindings.Scheduler.ThreadFaulted += (string modId, RbxError error) =>
            {
                if (modId == "m")
                {
                    fault = error;
                }
            };

            stack.Runtime.LoadMod("m", @"
                task.wait(0.1)
                error('post-wait entry failure')");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.IsNull(fault);

            bindings.Scheduler.Advance(0.1d);

            Assert.IsNotNull(fault);
            StringAssert.Contains("post-wait entry failure", fault.ToString());
        }

        [Test]
        public void Lua_ModMainChunk_UnloadKillsThreadSuspendedAtTaskWait()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            int killedThreads = -1;
            stack.Runtime.ModTearingDown += (string modId, LuaModTeardownReason reason) =>
            {
                killedThreads = bindings.KillAllScheduledOwnedBy(modId);
            };

            stack.Runtime.LoadMod("m", @"
                store_set('phase', 'waiting')
                task.wait(1)
                store_set('resumed', 'yes')");

            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.IsTrue(stack.Runtime.UnloadMod("m"));
            Assert.AreEqual(1, killedThreads);

            bindings.Scheduler.Advance(1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.AreEqual("", store.Get("m", "resumed"));
        }

        [Test]
        public void Lua_ModMainChunk_ReloadKillsOutgoingButKeepsReplacementThread()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            int killedThreads = -1;
            stack.Runtime.ModTearingDown += (string modId, LuaModTeardownReason reason) =>
            {
                killedThreads = reason == LuaModTeardownReason.Reload
                    ? bindings.KillOutgoingScheduledGenerations(modId)
                    : bindings.KillAllScheduledOwnedBy(modId);
            };

            stack.Runtime.LoadMod("m", @"
                store_set('outgoing', 'waiting')
                task.wait(1)
                store_set('outgoing', 'resumed')");

            stack.Runtime.ReloadMod("m", @"
                store_set('replacement', 'waiting')
                task.wait(0.5)
                store_set('replacement', 'resumed')");

            Assert.AreEqual(1, killedThreads);
            Assert.AreEqual("waiting", store.Get("m", "outgoing"));
            Assert.AreEqual("waiting", store.Get("m", "replacement"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("waiting", store.Get("m", "outgoing"));
            Assert.AreEqual("resumed", store.Get("m", "replacement"));
        }

        [Test]
        public void Lua_R4_8_TaskSpawnDeferAndDelay_UseSchedulerOrderingAndArguments()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local function append(value)
                    store_set('order', store_get('order') .. value)
                end
                task.spawn(append, 'S')
                append('A')
                task.defer(append, 'D')
                task.delay(0.25, append, 'L')");

            Assert.AreEqual("SA", store.Get("m", "order"));

            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("SAD", store.Get("m", "order"));

            bindings.Scheduler.Advance(0.15d);
            Assert.AreEqual("SADL", store.Get("m", "order"));
        }

        [Test]
        public void Lua_R4_8_TaskVarargs_PreserveArityNilPositionsAndZeroArguments()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local function encode(...)
                    local first, second, third, fourth = ...
                    return tostring(select('#', ...)) .. ':'
                        .. tostring(first) .. ':' .. tostring(second) .. ':'
                        .. tostring(third) .. ':' .. tostring(fourth)
                end
                task.spawn(function(...)
                    store_set('spawn_args', encode(...))
                end, 'A', nil, 'C', nil)
                task.defer(function(...)
                    store_set('defer_args', encode(...))
                end, 'A', nil, 'C', nil)
                task.delay(0, function(...)
                    store_set('delay_args', encode(...))
                end, 'A', nil, 'C', nil)
                task.spawn(function(...)
                    store_set('spawn_zero', tostring(select('#', ...)))
                end)
                task.defer(function(...)
                    store_set('defer_zero', tostring(select('#', ...)))
                end)
                task.delay(0, function(...)
                    store_set('delay_zero', tostring(select('#', ...)))
                end)");

            Assert.AreEqual("4:A:nil:C:nil", store.Get("m", "spawn_args"));
            Assert.AreEqual("0", store.Get("m", "spawn_zero"));

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("4:A:nil:C:nil", store.Get("m", "defer_args"));
            Assert.AreEqual("4:A:nil:C:nil", store.Get("m", "delay_args"));
            Assert.AreEqual("0", store.Get("m", "defer_zero"));
            Assert.AreEqual("0", store.Get("m", "delay_zero"));
        }

        [Test]
        public void Lua_R4_8_TaskWait_ReturnsActualElapsedFromControlledFrames()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                task.spawn(function()
                    local elapsed = task.wait(0.5)
                    store_set('elapsed', tostring(elapsed))
                end)");

            bindings.Scheduler.Advance(0.2d);
            Assert.AreEqual("", store.Get("m", "elapsed"));

            bindings.Scheduler.Advance(0.3d);
            Assert.AreEqual("0.5", store.Get("m", "elapsed"));
        }

        [Test]
        public void Lua_R4_9_LegacyGlobals_ApplyFloorAndDocumentedTimingValues()
        {
            double uptimeBefore = Time.realtimeSinceStartupAsDouble;
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                store_set('phase', 'loading')
                assert(spawn(function(elapsed, uptime, extra)
                    store_set('spawn_uptime', tostring(uptime))
                    store_set('spawn_args', tostring(
                        type(elapsed) == 'number' and type(uptime) == 'number'
                        and extra == nil and elapsed >= 0.029 and uptime >= elapsed))
                    store_set('spawn_phase', store_get('phase'))
                    local waited, total, third = wait(0)
                    store_set('wait_uptime', tostring(total))
                    store_set('wait_values', tostring(
                        type(waited) == 'number' and type(total) == 'number'
                        and third == nil and waited >= 0.029 and total > waited))
                end) == nil)
                assert(delay(0, function(value)
                    store_set('delay_ran', tostring(value == nil))
                end) == nil)
                store_set('phase', 'loaded')");

            Assert.AreEqual("", store.Get("m", "spawn_args"));
            Assert.AreEqual("", store.Get("m", "delay_ran"));

            bindings.Scheduler.Advance(0.028d);
            Assert.AreEqual("", store.Get("m", "spawn_args"));
            Assert.AreEqual("", store.Get("m", "delay_ran"));

            bindings.Scheduler.Advance(0.002d);
            Assert.AreEqual("true", store.Get("m", "spawn_args"));
            Assert.AreEqual("loaded", store.Get("m", "spawn_phase"));
            Assert.AreEqual("true", store.Get("m", "delay_ran"));
            Assert.AreEqual("", store.Get("m", "wait_values"));

            bindings.Scheduler.Advance(0.028d);
            Assert.AreEqual("", store.Get("m", "wait_values"));

            bindings.Scheduler.Advance(0.002d);
            Assert.AreEqual("true", store.Get("m", "wait_values"));

            double uptimeAfter = Time.realtimeSinceStartupAsDouble;
            double spawnUptime = double.Parse(
                store.Get("m", "spawn_uptime"), CultureInfo.InvariantCulture);
            double waitUptime = double.Parse(
                store.Get("m", "wait_uptime"), CultureInfo.InvariantCulture);
            Assert.That(spawnUptime,
                Is.InRange(uptimeBefore - 0.01d, uptimeAfter + 0.01d));
            Assert.That(waitUptime,
                Is.InRange(uptimeBefore - 0.01d, uptimeAfter + 0.01d));
        }

        [Test]
        public void Lua_R4_9_LegacySchedulerDeprecation_LogsExactlyOncePerMod()
        {
            List<string> log = new();
            LuaCsRbxApiBindings bindings = new(log: log.Add);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("alpha", @"
                spawn(function()
                    wait(0)
                end)
                delay(0, function() end)");
            stack.Runtime.LoadMod("beta", @"
                spawn(function() end)
                delay(0, function() end)");
            bindings.Scheduler.Advance(0.03d);

            int deprecationNotes = 0;
            foreach (string line in log)
            {
                if (line.Contains("wait/spawn/delay are deprecated"))
                {
                    deprecationNotes++;
                }
            }

            Assert.AreEqual(2, deprecationNotes,
                "the legacy scheduler deprecation note must fire exactly once per mod");
        }

        [Test]
        public void Lua_R4_8_TaskCancel_KillsPendingThreadAndDeadCancelErrors()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local pending = task.delay(0, function()
                    store_set('ran', 'yes')
                end)
                task.cancel(pending)
                local ok, err = pcall(task.cancel, pending)
                store_set('dead_cancel_errors', tostring(not ok and string.find(err, 'BAD_ARGUMENT') ~= nil))");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("", store.Get("m", "ran"));
            Assert.AreEqual("true", store.Get("m", "dead_cancel_errors"));
        }

        [Test]
        public void Lua_R4_8_TaskCancel_KillsThreadSuspendedMidWait()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local waiting = task.spawn(function()
                    store_set('phase', 'waiting')
                    task.wait(1)
                    store_set('phase', 'resumed')
                end)
                task.cancel(waiting)");

            Assert.AreEqual("waiting", store.Get("m", "phase"));

            bindings.Scheduler.Advance(1d);

            Assert.AreEqual("waiting", store.Get("m", "phase"));
        }

        [Test]
        public void Lua_ModUnload_KillsOwnedThreadSuspendedMidWait()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            int killedThreads = -1;
            LuaModTeardownReason teardownReason = LuaModTeardownReason.Reload;
            stack.Runtime.ModTearingDown += (string modId, LuaModTeardownReason reason) =>
            {
                teardownReason = reason;
                killedThreads = bindings.Scheduler.KillOwnedBy(modId);
            };

            stack.Runtime.LoadMod("m", @"
                task.spawn(function()
                    store_set('phase', 'waiting')
                    task.wait(1)
                    store_set('resumed', 'yes')
                end)");

            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.IsTrue(stack.Runtime.UnloadMod("m"));
            Assert.AreEqual(LuaModTeardownReason.Unload, teardownReason);
            Assert.AreEqual(1, killedThreads);

            bindings.Scheduler.Advance(1d);

            Assert.AreEqual("waiting", store.Get("m", "phase"));
            Assert.AreEqual("", store.Get("m", "resumed"));
        }

        [Test]
        public void Lua_ScheduledThreads_CarryOwningModIdForIsolatedKill()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("alpha", @"
                task.delay(0, function()
                    store_set('ran', 'yes')
                end)");
            stack.Runtime.LoadMod("beta", @"
                task.delay(0, function()
                    store_set('ran', 'yes')
                end)");

            Assert.AreEqual(1, bindings.Scheduler.KillOwnedBy("alpha"));
            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("", store.Get("alpha", "ran"));
            Assert.AreEqual("yes", store.Get("beta", "ran"));
        }
    }
}
