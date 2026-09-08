using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>
    /// MVP2 acceptance criterion 15 ("U1-U7 stances recorded; testable stances have conformance
    /// tests" — roadmap §5.2.9) through the REAL mod runtime. Stances are recorded in the
    /// appendix of Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md ("Appendix:
    /// UNCERTAIN items"). This fixture is the deliverable that maps every one of U1-U7 to either
    /// the test that pins it or the recorded reason it cannot be pinned, so that reversing a
    /// stance goes red somewhere:
    /// <list type="bullet">
    /// <item>U1 (Default SignalBehavior is Deferred-only) — already conformance-tested by
    /// <c>Lua_WorkspaceSignalBehavior_ReadsDeferred</c> and
    /// <c>Lua_WorkspaceSignalBehavior_WriteIsUnsupported</c> in
    /// RbxApiLuaBindingsEditModeTests.cs (read returns <c>Enum.SignalBehavior.Deferred</c>;
    /// writing any other value raises NOT_IMPLEMENTED naming "signal mode is Deferred-only").
    /// Not duplicated here.</item>
    /// <item>U2 (Heartbeat and PostSimulation are two separate, both-current phases — neither
    /// supersedes the other) — already conformance-tested by
    /// <c>Lua_FrameOrder_FollowsTheMirrorsPhaseOrder</c> in
    /// RbxRunServiceModernEventsEditModeTests.cs (frame order "APsOHRr" fires PostSimulation and
    /// Heartbeat as two distinct signals, PostSimulation before Heartbeat) and
    /// <c>Lua_ModernEvents_EachFireOncePerFrameWithANumericDelta</c> (PostSimulation fires every
    /// frame, so it is not an alias folded into Heartbeat). Not duplicated here.</item>
    /// <item>U3 (raw <c>coroutine.resume</c> error visibility differs from <c>task.spawn</c>) —
    /// <see cref="U3_RawCoroutineResumeErrors_NeverReachTheSchedulerFaultChannel_UnlikeTaskDelay"/>.</item>
    /// <item>U4 (handler invocation order is deterministic internally but NOT an API guarantee) —
    /// already pinned by <c>Internal_DispatchOrderIsDeterministic_NotAnApiGuarantee</c> in
    /// RbxTaskSchedulerLuaBindingsEditModeTests.cs. Not duplicated here.</item>
    /// <item>U5 (yielding inside <c>pcall</c> is allowed, safe to rely on) —
    /// <see cref="U5_YieldingInsidePcall_ResumesCleanlyAndReturnsTheYieldedValue"/>.</item>
    /// <item>U6 (the "Stack Begin"/"Script '&lt;path&gt;', Line &lt;n&gt;"/"Stack End" trace
    /// format is stable empirical behavior but explicitly NOT documented and must NOT be spec'd
    /// as parseable) —
    /// <see cref="U6_StackTraceVisualFormat_IsUnspecified_OnlyTheErrorTextGuaranteeIsPinned"/>.</item>
    /// <item>U7 (ReplicatedFirst inter-script start order) —
    /// <see cref="U7_ReplicatedFirstScriptOrder_IsUntestable_TheServiceIsNotRegisteredAtAll"/>.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public sealed class Mvp2StanceConformanceEditModeTests
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

        [Test]
        public void U3_RawCoroutineResumeErrors_NeverReachTheSchedulerFaultChannel_UnlikeTaskDelay()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);
            List<RbxError> faults = new();
            bindings.Scheduler.ThreadFaulted += (string modId, RbxError error) => faults.Add(error);

            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function() error('raw-coroutine-boom') end)
                local ok, err = coroutine.resume(co)
                store_set('raw_ok', tostring(ok))
                store_set('raw_err', tostring(err))
                task.delay(0, function() error('task-delay-boom') end)");

            // U3: an error inside a thread resumed via raw coroutine.resume is a normal Lua
            // (ok, err) pair local to the caller — it never touches CoreAI's scheduler fault
            // channel, unlike task.spawn/task.delay whose errors are the ones that reach the
            // console-visible reporting path (ThreadFaulted / GetRecentHandlerErrors).
            Assert.AreEqual("false", store.Get("m", "raw_ok"));
            StringAssert.Contains("raw-coroutine-boom", store.Get("m", "raw_err"));
            Assert.IsEmpty(faults, "a raw coroutine.resume error must never reach ThreadFaulted");

            bindings.Scheduler.Advance(0d);

            Assert.AreEqual(1, faults.Count,
                "task.delay's error must reach ThreadFaulted, proving the fault channel works at all");
            StringAssert.Contains("task-delay-boom", faults[0].ToString());
        }

        [Test]
        public void U5_YieldingInsidePcall_ResumesCleanlyAndReturnsTheYieldedValue()
        {
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                store_set('phase', 'before')
                local ok, elapsed = pcall(function()
                    return task.wait(0.5)
                end)
                store_set('phase', 'after')
                store_set('ok', tostring(ok))
                store_set('elapsed', tostring(elapsed))");

            Assert.AreEqual("before", store.Get("m", "phase"));

            bindings.Scheduler.Advance(0.5d);

            // U5: yielding (task.wait) across a pcall boundary is allowed and resumes the pcall
            // with its normal (true, results...) — not an error — matching the DataStore
            // retry idiom every Roblox script relies on despite the globals doc never saying so.
            Assert.AreEqual("after", store.Get("m", "phase"));
            Assert.AreEqual("true", store.Get("m", "ok"));
            Assert.AreEqual("0.5", store.Get("m", "elapsed"));
        }

        [Test]
        public void U6_StackTraceVisualFormat_IsUnspecified_OnlyTheErrorTextGuaranteeIsPinned()
        {
            // WHY untestable as a format: the roadmap flags "Stack Begin" / "Script '<path>',
            // Line <n>" / "Stack End" as stable empirical Roblox behavior that is explicitly NOT
            // documented and must NOT be spec'd as parseable (U6). CoreAI does not implement or
            // promise this framing at all today — there is nothing in Runtime/ that emits it — so
            // asserting a byte-for-byte format here would invent a promise the roadmap says must
            // not be made. What CAN be pinned, and already is elsewhere (see
            // RbxTaskSchedulerLuaBindingsEditModeTests.Lua_ModMainChunk_PostWaitError_
            // ReachesSchedulerFaultChannel), is that the underlying error TEXT survives to the
            // fault channel untouched. This test only reconfirms that guarantee here and pins
            // that no incidental "Stack Begin" framing has been bolted onto it since.
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

            stack.Runtime.LoadMod("m", "task.delay(0, function() error('u6-format-probe') end)");
            bindings.Scheduler.Advance(0d);

            Assert.IsNotNull(fault);
            StringAssert.Contains("u6-format-probe", fault.ToString());
            StringAssert.DoesNotContain("Stack Begin", fault.ToString());
        }

        [Test]
        public void U7_ReplicatedFirstScriptOrder_IsUntestable_TheServiceIsNotRegisteredAtAll()
        {
            // WHY untestable: U7 is about inter-script START ORDER for ReplicatedFirst
            // LocalScripts, but ReplicatedFirst is not registered in ServiceCatalog at all —
            // unlike RunService/DataStoreService/UserInputService/etc it has no
            // RegisterStub("ReplicatedFirst", ...) entry and no MVP assigned, so there is no
            // runtime surface to script an ordering test against. What IS pinned: resolving it
            // fails as an entirely UNKNOWN service (RbxErrorCode.UnknownService), not as a
            // "planned, errors at the member" stub the way criterion 11's frozen example
            // (a still-planned service) does. The day ReplicatedFirst is scaffolded as a planned
            // stub, this assertion goes red and flags that U7 then needs a real ordering test.
            LuaCsRbxApiBindings bindings = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(bindings, store);

            stack.Runtime.LoadMod("m", @"
                local ok, err = pcall(function() return game:GetService('ReplicatedFirst') end)
                store_set('ok', tostring(ok))
                store_set('err', tostring(err))");

            Assert.AreEqual("false", store.Get("m", "ok"));
            StringAssert.Contains("UNKNOWN_SERVICE", store.Get("m", "err"));
        }
    }
}
