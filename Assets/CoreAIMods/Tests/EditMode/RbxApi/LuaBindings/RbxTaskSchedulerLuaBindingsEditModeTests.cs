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
using CoreAI.Sandbox.LuaCs;
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

        private static List<RbxError> RecordFaults(LuaCsRbxApiBindings bindings, string modId)
        {
            List<RbxError> faults = new();
            bindings.Scheduler.ThreadFaulted += (string ownerModId, RbxError error) =>
            {
                if (ownerModId == modId)
                {
                    faults.Add(error);
                }
            };
            return faults;
        }

        [Test]
        public void Lua_TaskSpawnHostError_FaultKeepsTheHostCodeAndLine_UnderOnePrefix()
        {
            // WHY: the thread fault wrapped the host's own §5.2.7 line as BAD_ARGUMENT, so ModHandlerErrored and
            // auto-repair read "[mod:m script:? line:0] BAD_ARGUMENT: [mod:m script:main.lua line:2]
            // UNKNOWN_SERVICE: ... | fix: ... | fix: fix the Lua error before scheduling the thread again" - two
            // prefixes, two fixes, and a wrong service name re-coded as a Lua bug.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            List<RbxError> faults = RecordFaults(bindings, "m");

            stack.Runtime.LoadMod("m", "task.spawn(function()\n    game:GetService('NoSuchService')\nend)");

            const string expected = "[mod:m script:main.lua line:2] UNKNOWN_SERVICE: NoSuchService is not a valid "
                                    + "Service name | fix: call game:GetService with an exact service class name, "
                                    + "e.g. \"Workspace\"";
            Assert.AreEqual(1, faults.Count, "the failing task thread must be reported once");
            Assert.AreEqual(RbxErrorCode.UnknownService, faults[0].Code, faults[0].Message);
            Assert.AreEqual(expected, faults[0].Message);
            Assert.AreEqual("m", faults[0].ModId);
            Assert.AreEqual("main.lua", faults[0].Script);
            Assert.AreEqual(2, faults[0].Line);

            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.AreEqual(1, errors.Count, "the fault must reach the handler-error buffer once");
            Assert.AreEqual(expected, errors[0].Error,
                "ModHandlerErrored and the handler-error buffer must carry the host's line itself, once");
        }

        [TestCase("error('boom')", "boom")]
        [TestCase("error('NOT_A_CODE: boom')", "NOT_A_CODE: boom")]
        [TestCase("error('BAD_ARGUMENT:boom')", "BAD_ARGUMENT:boom")]
        public void Lua_TaskSpawnPlainLuaError_FaultStaysWrappedAsBadArgument(string statement, string text)
        {
            // WHY the negative twin: only an exact §5.2.7 line keeps its own code; any other error text is a
            // Lua bug of the thread and keeps the BAD_ARGUMENT wrapping and its fix hint.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            List<RbxError> faults = RecordFaults(bindings, "m");

            stack.Runtime.LoadMod("m", "task.spawn(function()\n    " + statement + "\nend)");

            Assert.AreEqual(1, faults.Count, "the failing task thread must be reported once");
            Assert.AreEqual(RbxErrorCode.BadArgument, faults[0].Code, faults[0].Message);
            Assert.AreEqual("[mod:m script:? line:0] BAD_ARGUMENT: " + text
                            + " | fix: fix the Lua error before scheduling the thread again",
                faults[0].Message);
        }

        [Test]
        public void RbxErrorLine_TryParse_ReadsBackExactlyTheLinesFormatWrites()
        {
            RbxError[] originals =
            {
                new(RbxErrorCode.UnknownService, "X is not a valid Service name", "call GetService with a class name",
                    "m", "main.lua", 3),
                new(RbxErrorCode.ThreadCap, "too many live threads"),
                new(RbxErrorCode.BadArgument, "refused", "pass a number", "m", null, 0),
                new(RbxErrorCode.ContextViolation,
                    "wrapped: [mod:n script:lib.lua line:9] BAD_ARGUMENT: inner | fix: inner fix", "outer fix",
                    "m", "src/a b.lua", 12),
            };
            foreach (RbxError original in originals)
            {
                Assert.IsTrue(RbxError.TryParse(original.Message, out RbxError parsed), original.Message);
                Assert.AreEqual(original.Code, parsed.Code, original.Message);
                Assert.AreEqual(original.RawMessage, parsed.RawMessage, original.Message);
                Assert.AreEqual(original.Fix, parsed.Fix, original.Message);
                Assert.AreEqual(original.ModId, parsed.ModId, original.Message);
                Assert.AreEqual(original.Script, parsed.Script, original.Message);
                Assert.AreEqual(original.Line, parsed.Line, original.Message);
                Assert.AreEqual(original.Message, parsed.Message, original.Message);
            }

            string[] notLines =
            {
                null, "", "boom", "NOT_A_CODE: boom", "BAD_ARGUMENT:boom", "bad_argument: boom",
                "Lua-CSharp: BAD_ARGUMENT: boom", "[string \"main.lua\"]:5: BAD_ARGUMENT: boom",
                "[mod:m script:main.lua line:x] BAD_ARGUMENT: boom", "[mod:m script:main.lua line:05] BAD_ARGUMENT: boom",
                "[mod:m line:5] BAD_ARGUMENT: boom", "[mod:m script:main.lua line:5]BAD_ARGUMENT: boom",
            };
            foreach (string text in notLines)
            {
                Assert.IsFalse(RbxError.TryParse(text, out RbxError none), "must not parse: " + text);
                Assert.IsNull(none, "no error may come back for: " + text);
            }
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
                    store_set('phase', 'resumed:' .. string.format('%.2f', elapsed))
                end)
                Instance.new('Folder', workspace)");

            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            bindings.Scheduler.Advance(0.1d);
            Assert.AreEqual("waiting", store.Get("m", "phase"));
            bindings.Scheduler.Advance(0.15d);
            Assert.AreEqual("resumed:0.25", store.Get("m", "phase"));
        }

        [Test]
        public void Lua_M2_06_TaskWaitInsideCoroutineCreate_RaisesAndTheEnclosingThreadKeepsWaiting()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY: the inner coroutine's task.wait used to schedule the ENCLOSING main-chunk thread and
            // then suspend only the inner coroutine, so the main chunk's own task.wait below failed with
            // "already in state Waiting" and the inner coroutine was never resumed.
            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function()
                    local ok, err = pcall(task.wait, 1)
                    store_set('inner_ok', tostring(ok))
                    store_set('inner_err', tostring(err))
                end)
                store_set('resumed', tostring(coroutine.resume(co)))
                store_set('co_status', coroutine.status(co))
                local elapsed = task.wait(0.1)
                store_set('outer', 'resumed:' .. tostring(elapsed))");

            Assert.AreEqual("false", store.Get("m", "inner_ok"),
                "task.wait inside coroutine.create must raise instead of suspending the wrong thread");
            StringAssert.Contains("CONTEXT_VIOLATION", store.Get("m", "inner_err"));
            StringAssert.Contains("coroutine.create", store.Get("m", "inner_err"));
            Assert.AreEqual("true", store.Get("m", "resumed"));
            Assert.AreEqual("dead", store.Get("m", "co_status"));

            bindings.Scheduler.Advance(0.1d);

            Assert.AreEqual("resumed:0.1", store.Get("m", "outer"),
                "the enclosing thread's own wait was never touched");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_06_SignalWaitAndWaitForChildInsideCoroutineCreate_Raise()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function()
                    local ok, err = pcall(function() return workspace.ChildAdded:Wait() end)
                    store_set('signal_ok', tostring(ok))
                    store_set('signal_err', tostring(err))
                    local ok2, err2 = pcall(function() return workspace:WaitForChild('Never', 5) end)
                    store_set('child_ok', tostring(ok2))
                    store_set('child_err', tostring(err2))
                end)
                coroutine.resume(co)
                store_set('co_status', coroutine.status(co))
                task.delay(0.5, function()
                    local later = Instance.new('Folder')
                    later.Name = 'Later'
                    later.Parent = workspace
                end)
                local child = workspace:WaitForChild('Later', 1)
                store_set('outer', tostring(child))");

            Assert.AreEqual("false", store.Get("m", "signal_ok"));
            StringAssert.Contains("CONTEXT_VIOLATION", store.Get("m", "signal_err"));
            Assert.AreEqual("false", store.Get("m", "child_ok"));
            StringAssert.Contains("CONTEXT_VIOLATION", store.Get("m", "child_err"));
            Assert.AreEqual("dead", store.Get("m", "co_status"));

            bindings.Scheduler.Advance(0.5d);

            Assert.AreEqual("Later", store.Get("m", "outer"),
                "the enclosing thread's WaitForChild still returns the child");
        }

        [Test]
        public void Lua_M2_19_YieldRefusedInsideFormat_LeavesTheNextWaitsWorking()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY: the refused yield used to leave the thread's scheduler record waiting, so every
            // later task.wait or signal:Wait of that thread failed with "already in state Waiting".
            stack.Runtime.LoadMod("m", @"
                local quiet = Instance.new('Folder')
                quiet.Name = 'Quiet'
                quiet.Parent = workspace
                task.spawn(function()
                    local waits = setmetatable({}, {__tostring = function()
                        task.wait(0)
                        return 'X'
                    end})
                    local ok, err = pcall(string.format, '%s', waits)
                    store_set('wait_ok', tostring(ok))
                    store_set('wait_err', tostring(err))
                    store_set('first', tostring(task.wait(0.25)))
                    local signals = setmetatable({}, {__tostring = function()
                        quiet.ChildRemoved:Wait()
                        return 'Y'
                    end})
                    local ok2 = pcall(string.format, '%s', signals)
                    store_set('signal_ok', tostring(ok2))
                    store_set('second', tostring(task.wait(0.25)))
                end)");

            Assert.AreEqual("false", store.Get("m", "wait_ok"));
            StringAssert.Contains(LuaCsSecureEnvironment.YieldAcrossCallBoundaryMessage,
                store.Get("m", "wait_err"));

            bindings.Scheduler.Advance(0.25d);

            Assert.AreEqual("0.25", store.Get("m", "first"), "the next task.wait schedules normally");
            Assert.AreEqual("false", store.Get("m", "signal_ok"));

            bindings.Scheduler.Advance(0.25d);

            Assert.AreEqual("0.25", store.Get("m", "second"));
            RbxInstance quietFolder = bindings.Registry.WorldRoot.FindFirstChild("Quiet");
            Assert.IsFalse(quietFolder.ChildRemoved.HasConnections,
                "the refused signal wait left no connection behind");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_14_TaskHandles_AreRescheduledBySpawnDeferAndDelay()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local parked = task.spawn(function()
                    store_set('parked', 'first')
                    coroutine.yield()
                    store_set('parked', 'resumed')
                end)
                store_set('same_handle', tostring(task.spawn(parked) == parked))
                local delayed = task.delay(5, function(value)
                    store_set('delayed', tostring(value))
                end, 'original')
                task.defer(delayed, 'moved')
                local deferred = task.defer(function(value)
                    store_set('deferred', tostring(value))
                end, 'original')
                task.delay(0.5, deferred, 'moved-late')");

            Assert.AreEqual("resumed", store.Get("m", "parked"),
                "task.spawn(handle) resumes a thread parked by coroutine.yield");
            Assert.AreEqual("true", store.Get("m", "same_handle"),
                "task.spawn(thread) returns the thread it was given");

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("moved", store.Get("m", "delayed"), "task.defer moved the delayed thread");
            Assert.AreEqual("", store.Get("m", "deferred"), "task.delay took the thread off the defer queue");

            bindings.Scheduler.Advance(0.5d);

            Assert.AreEqual("moved-late", store.Get("m", "deferred"));

            bindings.Scheduler.Advance(5d);

            Assert.AreEqual("moved", store.Get("m", "delayed"), "the abandoned delay never runs it again");
            Assert.AreEqual(0, bindings.Scheduler.LiveThreadCount);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_14_ParkedThread_CoroutineYieldReturnsTheValuesPassedToTaskSpawn()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local parked = task.spawn(function()
                    local value, label = coroutine.yield('parking')
                    store_set('first', tostring(value) .. ',' .. tostring(label))
                    local again = select('#', coroutine.yield())
                    store_set('second', tostring(again))
                end)
                task.spawn(parked, 42, 'answer')
                task.defer(parked)");

            // WHY: the handle used to hand coroutine.yield the previous resume's own results (true and
            // what the thread had yielded), never the values the resumer passed.
            Assert.AreEqual("42,answer", store.Get("m", "first"),
                "coroutine.yield returns exactly what task.spawn(thread, ...) passed");

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("0", store.Get("m", "second"),
                "a resume that passes nothing makes coroutine.yield return nothing");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_14_NegativeTwin_WaitingDeadRunningAndRawThreadsAreRefused()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local function capture(name, callback)
                    local ok, err = pcall(callback)
                    store_set(name .. '_ok', tostring(ok))
                    store_set(name .. '_err', tostring(err))
                end
                local waiting = task.spawn(function() task.wait(10) end)
                capture('waiting', function() task.spawn(waiting) end)
                local finished = task.spawn(function() end)
                capture('dead', function() task.defer(finished) end)
                capture('raw', function() task.spawn(coroutine.create(function() end)) end)
                local selfHandle
                selfHandle = task.defer(function()
                    capture('running', function() task.spawn(selfHandle) end)
                end)");

            bindings.Scheduler.Advance(0d);

            string[] refused = { "waiting", "dead", "raw", "running" };
            string[] reasons =
            {
                "scheduler wait", "dead thread", "coroutine.create", "running thread"
            };
            for (int index = 0; index < refused.Length; index++)
            {
                Assert.AreEqual("false", store.Get("m", refused[index] + "_ok"),
                    refused[index] + " must be refused");
                StringAssert.Contains("BAD_ARGUMENT", store.Get("m", refused[index] + "_err"));
                StringAssert.Contains(reasons[index], store.Get("m", refused[index] + "_err"));
            }
        }

        [Test]
        public void Lua_M2_20_NativeYieldInASignalHandler_IsAContextViolationAndLeavesNoThread()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY: nothing can resume a handler thread that suspended itself outside the scheduler. It
            // used to stay as an idle record holding one of its actor's 256 thread slots per fire.
            stack.Runtime.LoadMod("h", @"
                game:GetService('RunService').Heartbeat:Connect(function()
                    store_set('fired', tostring((tonumber(store_get('fired')) or 0) + 1))
                    coroutine.yield()
                    store_set('after_yield', 'ran')
                end)");

            bindings.Scheduler.Advance(1d / 60d);
            bindings.Scheduler.Advance(1d / 60d);

            Assert.AreEqual("2", store.Get("h", "fired"));
            Assert.AreEqual("", store.Get("h", "after_yield"));
            Assert.AreEqual(0, bindings.Scheduler.LiveThreadCount,
                "each natively suspended handler thread is stopped, not kept");
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("h");
            Assert.GreaterOrEqual(errors.Count, 1, "the stopped handler is reported, not dropped");
            Assert.IsTrue(errors.All(error => error.Error.Contains("CONTEXT_VIOLATION")
                                              && error.Error.Contains("coroutine.yield")),
                "every report names the cause: " + string.Join(" || ", errors.Select(error => error.Error)));
        }

        [Test]
        public void Lua_M2_20_NativelyParkedTaskThreads_TheThreadCapNamesThem()
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
                MaxSchedulerThreadsPerActor = 8
            });

            stack.Runtime.LoadMod("p", @"
                for index = 1, 20 do
                    local ok, err = pcall(task.spawn, function() coroutine.yield() end)
                    if not ok then
                        store_set('refused_at', tostring(index))
                        store_set('err', tostring(err))
                        break
                    end
                end");

            Assert.AreNotEqual("", store.Get("p", "refused_at"), "the actor's thread quota still applies");
            string refusal = store.Get("p", "err");
            StringAssert.Contains("THREAD_CAP", refusal);
            StringAssert.Contains("parked outside the scheduler", refusal,
                "the refusal must say why an actor with no visible work is out of threads");
            StringAssert.Contains("task.cancel", refusal);
        }

        [Test]
        public void Lua_M2_20_MainChunkNativeYield_FailsTheLoadLoudly()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            System.Exception error = Assert.Catch<System.Exception>(() =>
                stack.Runtime.LoadMod("m", @"
                    store_set('before', 'ran')
                    coroutine.yield()
                    store_set('after', 'ran')"));

            StringAssert.Contains("CONTEXT_VIOLATION", error.ToString());
            StringAssert.Contains("main chunk", error.ToString());
            Assert.IsFalse(stack.Runtime.IsLoaded("m"));
            Assert.AreEqual("", store.Get("m", "after"));
            Assert.AreEqual(0, bindings.Scheduler.LiveThreadCount);
        }

        [Test]
        public void Lua_M2_07_TrackedThreadLedger_StaysBoundedUnderDeferAndDelayChurn()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY: every task.defer/task.delay thread used to stay in the per-mod ledger until the mod
            // unloaded, about 2 KB each, so an ordinary per-frame defer leaked hundreds of MB an hour.
            stack.Runtime.LoadMod("m", @"
                local function noop() end
                game:GetService('RunService').Heartbeat:Connect(function()
                    for index = 1, 50 do
                        task.defer(noop)
                    end
                    task.delay(0, noop)
                end)");

            for (int frame = 0; frame < 40; frame++)
            {
                bindings.Scheduler.Advance(1d / 60d);
            }

            Assert.LessOrEqual(bindings.TrackedScheduledThreadCount,
                bindings.Scheduler.LiveThreadCount + 2,
                "finished threads leave the ledger when the scheduler retires them");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }

        [Test]
        public void Lua_M2_18_CancelledSignalWaiters_LeaveNoConnectionBehind()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local quiet = Instance.new('Folder')
                quiet.Name = 'Quiet'
                quiet.Parent = workspace
                for index = 1, 1000 do
                    task.cancel(task.spawn(function() quiet.ChildRemoved:Wait() end))
                end
                task.cancel(task.spawn(function() quiet.ChildRemoved:Wait(30) end))
                store_set('done', 'yes')");

            Assert.AreEqual("yes", store.Get("m", "done"));
            RbxInstance quietFolder = bindings.Registry.WorldRoot.FindFirstChild("Quiet");
            Assert.IsFalse(quietFolder.ChildRemoved.HasConnections,
                "a cancelled waiter's connection is disconnected at once, not when the signal fires");
            Assert.LessOrEqual(bindings.Connections.TrackedEntryCount("m"), 32,
                "the mod's connection ledger does not keep a thousand dead waits");
        }

        [Test]
        public void Lua_WaitForChild_BadArguments_NameWaitForChild()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local ok1, err1 = pcall(function() return workspace:WaitForChild() end)
                store_set('nil_ok', tostring(ok1))
                store_set('nil_err', tostring(err1))
                local ok2, err2 = pcall(function() return workspace:WaitForChild('Child', '5') end)
                store_set('text_ok', tostring(ok2))
                store_set('text_err', tostring(err2))
                local ok3, err3 = pcall(function() return workspace:WaitForChild('Child', 0/0) end)
                store_set('nan_ok', tostring(ok3))
                store_set('nan_err', tostring(err3))");

            Assert.AreEqual("false", store.Get("m", "nil_ok"));
            StringAssert.Contains("WaitForChild expects a string at argument 1",
                store.Get("m", "nil_err"));
            Assert.AreEqual("false", store.Get("m", "text_ok"));
            StringAssert.Contains("WaitForChild expects a number at argument 2",
                store.Get("m", "text_err"), "a string timeout used to fail as 'attempt to compare'");
            Assert.AreEqual("false", store.Get("m", "nan_ok"));
            StringAssert.Contains("WaitForChild timeout must be a number, not NaN",
                store.Get("m", "nan_err"));
        }

        [Test]
        public void Lua_WaitForChild_ChildDestroyedBeforeDrain_KeepsWaiting()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            // WHY: ChildAdded is deferred, so the waiter used to resume with a child destroyed in the
            // meantime and raise INSTANCE_DESTROYED reading its Name.
            stack.Runtime.LoadMod("m", @"
                task.defer(function()
                    local doomed = Instance.new('Folder')
                    doomed.Name = 'Late'
                    doomed.Parent = workspace
                    doomed:Destroy()
                end)
                task.delay(0.5, function()
                    local live = Instance.new('Folder')
                    live.Name = 'Late'
                    live.Parent = workspace
                end)
                local child = workspace:WaitForChild('Late', 2)
                store_set('result', tostring(child))");

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual("", store.Get("m", "result"), "a destroyed child does not end the wait");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));

            bindings.Scheduler.Advance(0.5d);

            Assert.AreEqual("Late", store.Get("m", "result"), "the live child that appears later is returned");
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"));
        }
    }
}
