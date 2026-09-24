using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

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

        private static void RecordMissing(MemoryStore store, List<string> missedBoundaries,
            string key, string boundary)
        {
            if (store.Get("m", key) != "seen")
            {
                missedBoundaries.Add(boundary);
            }
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
        public void Lua_R6_9_WaitForChild_YieldsForNamedChildAndReturnsIt()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                task.delay(0, function()
                    local unrelated = Instance.new('Folder')
                    unrelated.Name = 'Unrelated'
                    unrelated.Parent = workspace
                    local expected = Instance.new('Folder')
                    expected.Name = 'Expected'
                    expected.Parent = workspace
                end)
                store_set('phase', 'waiting')
                local child = workspace:WaitForChild('Expected')
                store_set('phase', child.Name)");

            Assert.AreEqual("waiting", store.Get("m", "phase"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("Expected", store.Get("m", "phase"));
        }

        [Test]
        public void Lua_R6_9_WaitForChild_TimeoutReturnsNilOnOwningScheduler()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local child = workspace:WaitForChild('NeverThere', 0.25)
                store_set('timed_out', tostring(child == nil))");

            bindings.Scheduler.Advance(0.24d);
            Assert.AreEqual("", store.Get("m", "timed_out"));
            bindings.Scheduler.Advance(0.01d);
            Assert.AreEqual("true", store.Get("m", "timed_out"));
        }

        [Test]
        public void Lua_R6_9_WaitForChild_WarnsAtFiveSecondsThenStillReturnsChild()
        {
            List<string> log = new();
            LuaCsRbxApiBindings bindings = new(log: log.Add);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                task.delay(5.5, function()
                    local child = Instance.new('Folder')
                    child.Name = 'Late'
                    child.Parent = workspace
                end)
                local child = workspace:WaitForChild('Late')
                store_set('result', child.Name)");

            bindings.Scheduler.Advance(4.9d);
            int warningCount = CountInfiniteYieldWarnings(log, "Late");
            Assert.AreEqual(0, warningCount);

            bindings.Scheduler.Advance(0.1d);
            warningCount = CountInfiniteYieldWarnings(log, "Late");
            Assert.AreEqual(1, warningCount);
            Assert.AreEqual("", store.Get("m", "result"));

            bindings.Scheduler.Advance(0.5d);
            Assert.AreEqual("Late", store.Get("m", "result"));
            Assert.AreEqual(1, CountInfiniteYieldWarnings(log, "Late"));
        }

        private static int CountInfiniteYieldWarnings(List<string> log, string childName)
        {
            string expected = "Infinite yield possible on 'Workspace:WaitForChild(\""
                              + childName + "\")'";
            int count = 0;
            for (int index = 0; index < log.Count; index++)
            {
                if (log[index].Contains(expected))
                {
                    count++;
                }
            }

            return count;
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
        public void Lua_R4_8_M2_13_TaskCancel_KillsPendingThreadAndDeadCancelIsANoOp()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY a no-op: task.cancel closes the thread like coroutine.close, which accepts a dead
            // coroutine; the mirror's task.yaml names only the running thread (and one that resumed
            // another coroutine) as uncancellable. `task.cancel(self._t)` in a Destroy method must not
            // abort the teardown because the task already ran.
            stack.Runtime.LoadMod("m", @"
                local pending = task.delay(0, function()
                    store_set('ran', 'yes')
                end)
                task.cancel(pending)
                local ok = pcall(task.cancel, pending)
                store_set('cancelled_twice_ok', tostring(ok))
                local finished = task.spawn(function() end)
                local finishedOk = pcall(task.cancel, finished)
                store_set('finished_cancel_ok', tostring(finishedOk))
                local selfHandle
                selfHandle = task.defer(function()
                    local runningOk, runningErr = pcall(task.cancel, selfHandle)
                    store_set('running_cancel_errors', tostring(not runningOk
                        and string.find(tostring(runningErr), 'currently running', 1, true) ~= nil))
                end)");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("", store.Get("m", "ran"));
            Assert.AreEqual("true", store.Get("m", "cancelled_twice_ok"));
            Assert.AreEqual("true", store.Get("m", "finished_cancel_ok"));
            Assert.AreEqual("true", store.Get("m", "running_cancel_errors"),
                "cancelling the currently running thread must still raise");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_17_InfiniteWaitAndDelayParkUntilCancelled()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local parkedDelay = task.delay(math.huge, function()
                    store_set('delay_ran', 'yes')
                end)
                local sleeper = task.spawn(function()
                    task.wait(math.huge)
                    store_set('sleeper_woke', 'yes')
                end)
                task.delay(1, function()
                    task.cancel(parkedDelay)
                    task.cancel(sleeper)
                    store_set('cancelled', 'yes')
                end)
                store_set('scheduled', 'yes')");

            Assert.AreEqual("yes", store.Get("m", "scheduled"),
                "task.delay(math.huge) and task.wait(math.huge) are accepted, not refused as non-finite");
            bindings.Scheduler.Advance(0.5d);
            int liveBeforeCancel = bindings.Scheduler.LiveThreadCount;
            bindings.Scheduler.Advance(0.5d);

            Assert.AreEqual("yes", store.Get("m", "cancelled"));
            Assert.AreEqual(liveBeforeCancel - 3, bindings.Scheduler.LiveThreadCount,
                "both parked threads and the canceller are gone");
            bindings.Scheduler.Advance(1e6d);
            Assert.AreEqual("", store.Get("m", "delay_ran"));
            Assert.AreEqual("", store.Get("m", "sleeper_woke"));
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
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

        [Test]
        public void Lua_R5_4_DeferredMutationDoesNotReenterAndNewSignalUsesNextGeneration()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function(child)
                    if child.Name == 'First' then
                        store_set('order', store_get('order') .. ',first-start')
                        local second = Instance.new('Folder')
                        second.Name = 'Second'
                        second.Parent = workspace
                        store_set('order', store_get('order') .. ',first-end')
                    else
                        store_set('order', store_get('order') .. ',second')
                    end
                end)
                local first = Instance.new('Folder')
                first.Name = 'First'
                store_set('order', 'before')
                first.Parent = workspace
                store_set('order', store_get('order') .. ',after')");

            Assert.AreEqual("before,after", store.Get("m", "order"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("before,after,first-start,first-end,second",
                store.Get("m", "order"));
        }

        [Test]
        public void Lua_R5_5_ConnectDuringDispatchDoesNotReceiveTheQueuedFire()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function(child)
                    if child.Name == 'First' then
                        workspace.ChildAdded:Connect(function(lateChild)
                            store_set('late_names', store_get('late_names') .. lateChild.Name)
                        end)
                        local second = Instance.new('Folder')
                        second.Name = 'Second'
                        second.Parent = workspace
                    end
                end)
                local first = Instance.new('Folder')
                first.Name = 'First'
                first.Parent = workspace");

            Assert.AreEqual("", store.Get("m", "late_names"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("Second", store.Get("m", "late_names"));
        }

        [Test]
        public void Lua_SignalFire_DeliversBothArguments()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local child = Instance.new('Folder')
                child.Name = 'Subject'
                child.AncestryChanged:Connect(function(subject, parent)
                    store_set('subject', subject.Name)
                    store_set('parent', parent.Name)
                end)
                child.Parent = workspace");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("Subject", store.Get("m", "subject"));
            Assert.AreEqual("Workspace", store.Get("m", "parent"));
        }

        /// <summary>
        /// Pins CoreAI's internal dispatch determinism for replication reproducibility. R5.11 leaves
        /// handler order unguaranteed at the Roblox API level, so this is never a mod-facing promise.
        /// </summary>
        [Test]
        public void Internal_DispatchOrderIsDeterministic_NotAnApiGuarantee()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function()
                    store_set('order', store_get('order') .. '1')
                end)
                workspace.ChildAdded:Connect(function()
                    store_set('order', store_get('order') .. '2')
                end)
                workspace.ChildAdded:Connect(function()
                    store_set('order', store_get('order') .. '3')
                end)
                Instance.new('Folder', workspace)");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("123", store.Get("m", "order"));
        }

        [Test]
        public void Lua_ParamsSignalFire_DispatchesThroughTheDeferredQueue()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function(child)
                    store_set('child_name', child.Name)
                end)
                local child = Instance.new('Folder')
                child.Name = 'FromParams'
                child.Parent = workspace");

            Assert.AreEqual("", store.Get("m", "child_name"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("FromParams", store.Get("m", "child_name"));
        }

        [Test]
        public void Lua_SignalHandlerFault_ReportsOwningModAndRunsQueuedSiblings()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("faulting", @"
                workspace.ChildAdded:Connect(function()
                    store_set('started', 'yes')
                    error('signal-boom')
                end)
                workspace.ChildAdded:Connect(function()
                    store_set('same_mod_sibling', 'yes')
                end)");
            stack.Runtime.LoadMod("healthy", @"
                workspace.ChildAdded:Connect(function()
                    store_set('other_mod_sibling', 'yes')
                end)");
            stack.Runtime.LoadMod("trigger", "Instance.new('Folder', workspace)");

            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(0d));

            Assert.AreEqual("yes", store.Get("faulting", "started"));
            Assert.AreEqual("yes", store.Get("faulting", "same_mod_sibling"));
            Assert.AreEqual("yes", store.Get("healthy", "other_mod_sibling"));
            IReadOnlyList<LuaModHandlerError> errors =
                stack.Runtime.GetRecentHandlerErrors("faulting");
            Assert.AreEqual(1, errors.Count);
            Assert.AreEqual("faulting", errors[0].ModId);
            StringAssert.Contains("signal-boom", errors[0].Error);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("healthy"));
        }

        [Test]
        public void Lua_SignalAdmissionQuota_FirstActorSaturated_DoesNotDropLaterActor()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = bindings,
                MaxSchedulerThreadsPerActor = 1
            });
            // WHY characters are off here: this gate is about signal ADMISSION, and a joining actor
            // now gets a character Model parented into Workspace (Players.CharacterAutoLoads, the
            // mirror's default). That is one more ChildAdded than this world is about, and it would
            // make the error count below depend on world traffic the test never asked for.
            bindings.Players.CharacterAutoLoads = false;

            ActorContext saturatedActor = new LocalActorIdentityProvider("signal-saturated-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
            ActorContext healthyActor = new LocalActorIdentityProvider("signal-healthy-actor")
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            stack.Runtime.LoadMod(saturatedActor, "signal-saturated", @"
                workspace.ChildAdded:Connect(function()
                    store_set('unexpected', 'ran')
                end)
                task.wait(1000)", persistToStore: false);
            stack.Runtime.LoadMod(healthyActor, "signal-healthy", @"
                workspace.ChildAdded:Connect(function()
                    store_set('delivered', 'yes')
                end)", persistToStore: false);
            stack.Runtime.LoadMod("signal-trigger", "Instance.new('Folder', workspace)");

            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(0d));
            Assert.AreEqual("", store.Get("signal-saturated", "unexpected"));
            Assert.AreEqual("yes", store.Get("signal-healthy", "delivered"));
            IReadOnlyList<LuaModHandlerError> errors =
                stack.Runtime.GetRecentHandlerErrors("signal-saturated");
            // WHY the message carries the errors: a bare count tells you the number is wrong and
            // nothing about which extra error appeared, which is the only thing that names a cause.
            Assert.AreEqual(1, errors.Count,
                "saturated actor errors: "
                + string.Join(" || ", errors.Select(error => error.Error)));
            StringAssert.Contains("THREAD_CAP", errors[0].Error);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("signal-healthy"));
        }

        [Test]
        public void R5_4_SignalDrainRunsAfterDelayedResumptionAndBeforeHeartbeat()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            bindings.Scheduler.PhaseReached += (SchedulerPhase phase, double _) =>
            {
                if (phase == SchedulerPhase.Heartbeat)
                {
                    store.Set("m", "order", store.Get("m", "order") + "H");
                }
            };

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function()
                    store_set('order', store_get('order') .. 'S')
                end)
                task.delay(0, function()
                    store_set('order', store_get('order') .. 'D')
                    Instance.new('Folder', workspace)
                end)");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("DSH", store.Get("m", "order"));
        }

        [Test]
        public void Lua_R5_5_SignalsDrainAtEveryLiveFrameResumptionPoint()
        {
            InMemoryInputSource input = new();
            LuaCsRbxApiBindings bindings = new(inputSource: input);
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            RbxInstance workspace = bindings.Game.FindFirstChildOfClass("Workspace");
            List<string> missedBoundaries = new();

            stack.Runtime.LoadMod("m", @"
                local RunService = game:GetService('RunService')
                local UserInputService = game:GetService('UserInputService')
                workspace.AttributeChanged:Connect(function(name)
                    store_set('attribute_' .. name, 'seen')
                end)
                RunService.Stepped:Connect(function()
                    store_set('stepped', 'seen')
                end)
                RunService.Heartbeat:Connect(function()
                    store_set('heartbeat', 'seen')
                end)
                RunService.RenderStepped:Connect(function()
                    store_set('render', 'seen')
                end)
                UserInputService.InputBegan:Connect(function()
                    store_set('input', 'seen')
                end)
                task.delay(0, function()
                    workspace:SetAttribute('Task', true)
                end)
                delay(0, function()
                    workspace:SetAttribute('Legacy', true)
                end)
                workspace:SetAttribute('Initial', true)");

            bindings.Scheduler.PhaseReached += (SchedulerPhase phase, double deltaSeconds) =>
            {
                switch (phase)
                {
                    case SchedulerPhase.PreAnimation:
                        RecordMissing(store, missedBoundaries, "attribute_Initial", "frame-entry");
                        workspace.SetAttribute("PreAnimation", true);
                        break;
                    case SchedulerPhase.PreSimulation:
                        RecordMissing(store, missedBoundaries,
                            "attribute_PreAnimation", "PreAnimation");
                        workspace.SetAttribute("PreSimulation", true);
                        break;
                    case SchedulerPhase.PostSimulation:
                        RecordMissing(store, missedBoundaries, "stepped", "PreSimulation signal");
                        RecordMissing(store, missedBoundaries,
                            "attribute_PreSimulation", "PreSimulation mutation");
                        workspace.SetAttribute("PostSimulation", true);
                        break;
                    case SchedulerPhase.Heartbeat:
                        RecordMissing(store, missedBoundaries,
                            "attribute_PostSimulation", "PostSimulation");
                        RecordMissing(store, missedBoundaries,
                            "attribute_Legacy", "legacy script resumption");
                        RecordMissing(store, missedBoundaries, "attribute_Task", "task resumption");
                        workspace.SetAttribute("Heartbeat", true);
                        break;
                    case SchedulerPhase.InputProcessing:
                        RecordMissing(store, missedBoundaries, "heartbeat", "Heartbeat signal");
                        RecordMissing(store, missedBoundaries,
                            "attribute_Heartbeat", "Heartbeat mutation");
                        workspace.SetAttribute("InputProcessing", true);
                        break;
                    case SchedulerPhase.PreRender:
                        RecordMissing(store, missedBoundaries, "input", "input processing signal");
                        RecordMissing(store, missedBoundaries,
                            "attribute_InputProcessing", "input processing mutation");
                        workspace.SetAttribute("PreRender", true);
                        break;
                }
            };
            input.PressKey(32);

            bindings.Scheduler.Advance(0.029d);

            RecordMissing(store, missedBoundaries, "render", "PreRender signal");
            RecordMissing(store, missedBoundaries, "attribute_PreRender", "PreRender mutation");
            Assert.IsEmpty(missedBoundaries,
                "Signals missed their R5.5 resumption boundary: "
                + string.Join(", ", missedBoundaries));
        }

        [Test]
        public void Lua_R5_6_DeferredReentrancyCapIs10AndReportsChain()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local count = 0
                workspace.ChildAdded:Connect(function()
                    count = count + 1
                    store_set('count', tostring(count))
                    Instance.new('Folder', workspace)
                end)
                Instance.new('Folder', workspace)");

            // WHY no throw: the cascade is reported to its mod and only its own chain is dropped; a
            // throw here used to abort the rest of the frame for every loaded mod (M2-02).
            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(0d));
            Assert.AreEqual("10", store.Get("m", "count"));
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.AreEqual(1, errors.Count,
                "one cascade is one fault: " + string.Join(" || ", errors.Select(error => error.Error)));
            StringAssert.Contains("SIGNAL_CASCADE", errors[0].Error);
            StringAssert.Contains("Workspace.ChildAdded -> Workspace.ChildAdded", errors[0].Error);
        }

        [Test]
        public void Lua_M2_02_CascadingModEveryFrame_OtherModKeepsHeartbeatAndTaskWait()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("cascading", @"
                local RunService = game:GetService('RunService')
                workspace.ChildAdded:Connect(function(child)
                    if child.Name == 'Loop' then
                        local folder = Instance.new('Folder')
                        folder.Name = 'Loop'
                        folder.Parent = workspace
                    end
                end)
                RunService.Stepped:Connect(function()
                    local seed = Instance.new('Folder')
                    seed.Name = 'Loop'
                    seed.Parent = workspace
                end)");
            stack.Runtime.LoadMod("healthy", @"
                local RunService = game:GetService('RunService')
                local beats = 0
                RunService.Heartbeat:Connect(function()
                    beats = beats + 1
                    store_set('heartbeats', tostring(beats))
                end)
                task.spawn(function()
                    local waits = 0
                    while true do
                        task.wait()
                        waits = waits + 1
                        store_set('waits', tostring(waits))
                    end
                end)");

            const int frames = 5;
            for (int frame = 0; frame < frames; frame++)
            {
                Assert.DoesNotThrow(() => bindings.Scheduler.Advance(1d / 60d),
                    "a cascading mod must not abort the frame for every other mod");
            }

            Assert.AreEqual(frames.ToString(CultureInfo.InvariantCulture),
                store.Get("healthy", "heartbeats"), "the healthy mod's Heartbeat ran every frame");
            Assert.AreEqual(frames.ToString(CultureInfo.InvariantCulture),
                store.Get("healthy", "waits"), "the healthy mod's task.wait loop resumed every frame");
            IReadOnlyList<LuaModHandlerError> errors =
                stack.Runtime.GetRecentHandlerErrors("cascading");
            Assert.AreEqual(frames, errors.Count,
                "exactly one attributed fault per cascading frame: "
                + string.Join(" || ", errors.Select(error => error.Error)));
            Assert.IsTrue(errors.All(error => error.Error.Contains("SIGNAL_CASCADE")));
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("healthy"));
        }

        [Test]
        public void Lua_M2_02_NativeResumeOfATaskThread_FaultsOnlyItsModWhenTheWaitExpires()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            List<string> faults = new();
            bindings.Scheduler.ThreadFaulted += (string modId, RbxError error) =>
                faults.Add(modId + ":" + error.Code);

            stack.Runtime.LoadMod("healthy", @"
                local beats = 0
                game:GetService('RunService').Heartbeat:Connect(function()
                    beats = beats + 1
                    store_set('beats', tostring(beats))
                end)");
            // WHY this shape: a native coroutine.resume finishes the task thread outside the scheduler
            // while its task.wait is still queued; when the wait expires the scheduler finds it dead.
            stack.Runtime.LoadMod("resumer", @"
                local co
                task.spawn(function()
                    co = coroutine.running()
                    task.wait(0.5)
                end)
                coroutine.resume(co)
                store_set('status', coroutine.status(co))");

            Assert.AreEqual("dead", store.Get("resumer", "status"));
            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(0.5d),
                "resuming a thread that died elsewhere is that mod's fault, not a frame abort");

            Assert.AreEqual("1", store.Get("healthy", "beats"));
            CollectionAssert.AreEqual(new[] { "resumer:BadArgument" }, faults);
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("resumer");
            Assert.AreEqual(1, errors.Count);
            StringAssert.Contains("dead thread", errors[0].Error);

            Assert.DoesNotThrow(() => bindings.Scheduler.Advance(0.5d));
            Assert.AreEqual(1, faults.Count, "the dead thread is reported once and then forgotten");
        }

        [Test]
        public void TickDriver_ContainsAdvanceAndTickSeparately()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            stack.Runtime.LoadMod("m", @"
                hooks_every(0, function()
                    store_set('ticks', tostring((tonumber(store_get('ticks')) or 0) + 1))
                end)");
            // WHY a PhaseReached subscriber: with no HostFaulted subscriber its failure is rethrown by
            // Advance once the frame completes, which is exactly what reaches the driver.
            bindings.Scheduler.PhaseReached += (SchedulerPhase phase, double delta) =>
            {
                if (phase == SchedulerPhase.Heartbeat)
                {
                    throw new System.InvalidOperationException("driver-test phase failure");
                }
            };
            GameObject driverObject = new("LuaModRuntimeTickDriver");
            try
            {
                LuaModRuntimeTickDriver driver = driverObject.AddComponent<LuaModRuntimeTickDriver>();
                ActorContext hostActor = CoreServicesInstaller.DefaultLocalHostIdentityProvider
                    .GetActorContext(BuiltInAgentRoleIds.Programmer);
                driver.Initialize(stack.Runtime, hostActor, bindings.Scheduler);

                LogAssert.Expect(LogType.Error,
                    new Regex("ModScheduler\\.Advance failed.*driver-test phase failure"));
                Assert.DoesNotThrow(() => driver.PumpFrame(0.1f));

                Assert.AreEqual("1", store.Get("m", "ticks"),
                    "the runtime still ticks in a frame whose Advance threw");
            }
            finally
            {
                Object.DestroyImmediate(driverObject);
            }
        }

        [Test]
        public void Lua_R5_1_R5_9_OnceAndWaitDeliverOnlyTheFirstQueuedFire()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("once", @"
                workspace.ChildAdded:Once(function(child)
                    store_set('name', child.Name)
                    store_set('count', tostring((tonumber(store_get('count')) or 0) + 1))
                end)
                local first = Instance.new('Folder')
                first.Name = 'First'
                first.Parent = workspace
                local second = Instance.new('Folder')
                second.Name = 'Second'
                second.Parent = workspace");

            stack.Runtime.LoadMod("wait", @"
                local folder = Instance.new('Folder')
                task.defer(function()
                    folder.Parent = workspace
                end)
                local subject, parent = folder.AncestryChanged:Wait()
                store_set('subject', subject.Name)
                store_set('parent', parent.Name)");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("1", store.Get("once", "count"));
            Assert.AreEqual("First", store.Get("once", "name"));
            Assert.AreEqual("Folder", store.Get("wait", "subject"));
            Assert.AreEqual("Workspace", store.Get("wait", "parent"));
        }

        [Test]
        public void Lua_R5_7_ExplicitDisconnectDropsPendingButDestroyPreservesPending()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local dropped
                workspace.ChildAdded:Connect(function()
                    dropped:Disconnect()
                end)
                dropped = workspace.ChildAdded:Connect(function()
                    store_set('dropped', 'ran')
                end)
                Instance.new('Folder', workspace)

                local doomed = Instance.new('Folder', workspace)
                doomed.AncestryChanged:Connect(function()
                    store_set('destroy_pending', 'ran')
                end)
                doomed.Parent = nil
                doomed:Destroy()");

            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("", store.Get("m", "dropped"));
            Assert.AreEqual("ran", store.Get("m", "destroy_pending"));
        }

        [Test]
        public void Lua_InstanceLifecycleAndAttributeSignalsUseTheDeferredProductionPath()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local parent = Instance.new('Folder', workspace)
                local child = Instance.new('Folder')
                parent.ChildAdded:Connect(function(value)
                    store_set('child_added', tostring(value == child))
                end)
                parent.ChildRemoved:Connect(function(value)
                    store_set('child_removed', tostring(value == child))
                end)
                workspace.DescendantAdded:Connect(function(value)
                    if value == child then store_set('descendant_added', 'true') end
                end)
                child.AncestryChanged:Connect(function(subject)
                    if subject == child then
                        store_set('ancestry_count',
                            tostring((tonumber(store_get('ancestry_count')) or 0) + 1))
                    end
                end)
                parent.AttributeChanged:Connect(function(name)
                    store_set('attribute_name', name)
                end)
                parent:GetAttributeChangedSignal('Health'):Connect(function(...)
                    store_set('attribute_args', tostring(select('#', ...)))
                end)
                child.Parent = parent
                parent:SetAttribute('Health', 100)
                child.Parent = nil");

            Assert.AreEqual("", store.Get("m", "child_added"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("true", store.Get("m", "child_added"));
            Assert.AreEqual("true", store.Get("m", "child_removed"));
            Assert.AreEqual("true", store.Get("m", "descendant_added"));
            Assert.AreEqual("2", store.Get("m", "ancestry_count"));
            Assert.AreEqual("Health", store.Get("m", "attribute_name"));
            Assert.AreEqual("0", store.Get("m", "attribute_args"));
        }

        [Test]
        public void Lua_R5_8_DEV7_DestroyingRunsAfterDestroyWithTombstoneAndDeadConnection()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local folder = Instance.new('Folder', workspace)
                folder.Name = 'Doomed'
                local connection
                connection = folder.Destroying:Connect(function()
                    store_set('name', folder.Name)
                    store_set('class', folder.ClassName)
                    store_set('parent_nil', tostring(folder.Parent == nil))
                    store_set('connected', tostring(connection.Connected))
                end)
                folder:Destroy()
                store_set('after_destroy', 'yes')");

            Assert.AreEqual("yes", store.Get("m", "after_destroy"));
            Assert.AreEqual("", store.Get("m", "name"));
            bindings.Scheduler.Advance(0d);
            Assert.AreEqual("Doomed", store.Get("m", "name"));
            Assert.AreEqual("Folder", store.Get("m", "class"));
            Assert.AreEqual("true", store.Get("m", "parent_nil"));
            Assert.AreEqual("false", store.Get("m", "connected"));
        }

        [Test]
        public void Lua_SignalHandler_TaskWaitUsesOwningSchedulerThread()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                workspace.ChildAdded:Connect(function()
                    store_set('phase', 'waiting')
                    local elapsed = task.wait(0.25)
                    store_set('phase', 'resumed:' .. tostring(elapsed))
                end)
                Instance.new('Folder', workspace)");

            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            bindings.Scheduler.Advance(0.15d);
            Assert.AreEqual("resumed:0.25", store.Get("m", "phase"));
        }
    }
}
