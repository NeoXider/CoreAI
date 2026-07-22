using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Messaging;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// End-to-end EditMode proof of the additive Lua-CSharp mod stack, wired exactly as a future DI scope
    /// would wire it — through <see cref="LuaCsModRuntimeFactory"/>, exercising the managed
    /// (Lua-CSharp) runtime end to end.
    /// The test assembly does not reference the Lua-CSharp package (Lua.dll), so Lua-side failures are
    /// caught via the non-generic <see cref="Assert.Catch(TestDelegate)"/> rather than by exception type.
    /// </summary>
    public sealed class LuaCsModRuntimeEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>
        /// The Lua-CSharp runtime bridges its async VM to a synchronous call site via
        /// <c>state.ExecuteAsync(...).GetAwaiter().GetResult()</c> inside the execution guard. On Unity's
        /// main thread a <see cref="SynchronizationContext"/> is installed, so any continuation the VM
        /// posts back to it would deadlock the blocked main thread (a sync-over-async hazard — this is
        /// why the interactive Unity Test Runner freezes on these paths, and why batchmode is the
        /// reliable way to run them). Detaching the context for the duration of each test lets those
        /// continuations complete on the thread pool, exercising the runtime's logic deterministically.
        /// </summary>
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

        /// <summary>In-memory <see cref="ILuaModStore"/> used by the runtime fixtures.</summary>
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

                foreach ((string storedModId, string key) in keys)
                {
                    _values.Remove((storedModId, key));
                }
            }
        }

        /// <summary>Collects every command a WorldEdit-tier binding routes through the sink.</summary>
        private sealed class FakeCommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        /// <summary>No-op Unity logger so the ported gameplay bindings have a non-null sink.</summary>
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

        /// <summary>Builds the fully-wired stack the way the DI scope will, with test fakes.</summary>
        private static LuaCsModStack BuildStack(
            ILuaModStore store = null,
            IAiGameCommandSink sink = null,
            LuaCapabilities caps = LuaCapabilities.All)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                CommandSink = sink,
                ModStore = store,
                Capabilities = caps,
                OneOffCapabilities = caps
            });
        }

        [Test]
        public void LuaCs_Factory_BuildsFullyWiredStack()
        {
            LuaCsModStack stack = BuildStack(new MemoryStore(), new FakeCommandSink());

            Assert.IsNotNull(stack.Runtime);
            Assert.IsNotNull(stack.ToolExecutor);
            Assert.IsNotNull(stack.GameplayBindings);
            Assert.IsTrue(LuaCsModRuntime.IsSupported, "Lua-CSharp runtime must report supported.");
            Assert.IsTrue(LuaCsGameToolExecutor.IsSupported, "Lua-CSharp one-off executor must report supported.");
            Assert.AreEqual(LuaCapabilities.All, stack.GameplayBindings.Capabilities);
        }

        [Test]
        public void LuaCs_HooksOnAndHooksEvery_TickFiresTimer_EmitEventFiresHandler()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("m", @"
                hooks_on('hit', function(name, payload) store_set('last', payload) end)
                hooks_every(0.1, function()
                    local n = tonumber(store_get('ticks')) or 0
                    store_set('ticks', tostring(n + 1))
                end)");

            Assert.IsTrue(stack.Runtime.IsLoaded("m"));

            stack.Runtime.Tick(0.05);
            Assert.AreEqual("", store.Get("m", "ticks"), "Timer must not fire before its interval elapses.");

            stack.Runtime.Tick(0.06);
            Assert.AreEqual("1", store.Get("m", "ticks"), "Timer fires once the 0.1s interval elapses.");

            stack.Runtime.EmitEvent("hit", "42");
            stack.Runtime.Tick(0);
            Assert.AreEqual("42", store.Get("m", "last"), "EmitEvent + Tick dispatches the hooks_on handler.");
        }

        [Test]
        public void LuaCs_Store_PersistsAcrossTicksAndReload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            const string src = @"
                hooks_on('save', function(_, payload) store_set('v', payload) end)
                hooks_on('echo', function() store_set('after', store_get('v')) end)";
            stack.Runtime.LoadMod("m", src);

            stack.Runtime.EmitEvent("save", "persisted");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "v"));

            // Value survives to a later tick without being re-written.
            stack.Runtime.EmitEvent("echo", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "after"));

            // Value survives a reload: the store is host-owned and keyed by mod id, so the fresh state
            // still reads what was written before the reload.
            stack.Runtime.ReloadMod("m", src);
            store.Set("m", "after", null); // clear the marker to prove the reloaded mod reads live store
            stack.Runtime.EmitEvent("echo", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("persisted", store.Get("m", "after"),
                "store_set/get survives a reload (host-owned by mod id).");
        }

        [Test]
        public void LuaCs_EventsEmit_RaisesModEventEmitted()
        {
            LuaCsModStack stack = BuildStack();
            string gotMod = null, gotName = null, gotPayload = null;
            stack.Runtime.ModEventEmitted += (modId, name, payload) =>
            {
                gotMod = modId;
                gotName = name;
                gotPayload = payload;
            };

            // events_emit runs at load and raises ModEventEmitted synchronously.
            stack.Runtime.LoadMod("a", "events_emit('quest_event', 'payload')");

            Assert.AreEqual("a", gotMod);
            Assert.AreEqual("quest_event", gotName);
            Assert.AreEqual("payload", gotPayload);
        }

        [Test]
        public void LuaCs_ModSourceLoaded_ThrowingSubscriber_DoesNotFailLoadOrOtherSubscribers()
        {
            LuaCsModStack stack = BuildStack();
            bool healthySubscriberRan = false;
            stack.Runtime.ModSourceLoaded += (_, _, _) => throw new System.InvalidOperationException("boom");
            stack.Runtime.ModSourceLoaded += (_, _, _) => healthySubscriberRan = true;

            Assert.DoesNotThrow(() => stack.Runtime.LoadMod("a", "hooks_on('noop', function() end)"),
                "A throwing ModSourceLoaded subscriber must not make a healthy load fail.");

            Assert.IsTrue(stack.Runtime.IsLoaded("a"), "The mod must be loaded despite the throwing subscriber.");
            Assert.IsTrue(healthySubscriberRan, "Other subscribers must still run after one throws.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_Coroutine_AdvancesOneStepPerResume_CompletesWithoutHanging()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("m", @"
                local co = coroutine.create(function()
                    for i = 3, 1, -1 do
                        store_set('step', tostring(i))
                        coroutine.yield()
                    end
                    store_set('step', 'done')
                end)
                hooks_every(0.05, function()
                    if coroutine.status(co) ~= 'dead' then
                        coroutine.resume(co)
                    end
                end)");

            List<string> seq = new();
            for (int i = 0; i < 5; i++)
            {
                stack.Runtime.Tick(0.05); // one resume per tick
                seq.Add(store.Get("m", "step"));
            }

            // One step advances per resume; then the coroutine completes and stays done — no re-run,
            // no hang. This is the WebGL-critical path: coroutine.yield across ticks under Lua-CSharp.
            CollectionAssert.AreEqual(new[] { "3", "2", "1", "done", "done" }, seq);
            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("m"),
                "The coroutine pump must not raise handler errors.");
        }

        [Test]
        public void LuaCs_InterMod_ValueAndFunctionExport_ConsumerReadsAndCalls_FunctionNotReadable()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);

            stack.Runtime.LoadMod("provider", @"
                local base = 2.0
                mods_export('multiplier', base)
                mods_export('scale', function(x) return (tonumber(x) or 0) * base end)");

            stack.Runtime.LoadMod("consumer", @"
                hooks_on('read', function()
                    local m = mods_get('provider', 'multiplier')
                    local s = mods_call('provider', 'scale', 10)
                    store_set('ok', (m == 2.0 and s == 20.0) and 'yes' or 'no')
                    store_set('m_type', type(m))
                end)
                hooks_on('read_fn', function()
                    -- a function export is NOT readable via mods_get; this call must raise.
                    local fn = mods_get('provider', 'scale')
                    store_set('leaked', 'yes')
                end)");

            stack.Runtime.EmitEvent("read", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("yes", store.Get("consumer", "ok"),
                "Consumer reads the exported value and calls the exported function via mods_call.");
            Assert.AreEqual("number", store.Get("consumer", "m_type"),
                "Cross-mod value crosses as a plain-data copy.");

            stack.Runtime.EmitEvent("read_fn", "");
            stack.Runtime.Tick(0); // must not throw out of Tick
            Assert.AreEqual("", store.Get("consumer", "leaked"),
                "A function export must NOT be readable via mods_get.");
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("consumer");
            Assert.IsNotEmpty(errors, "mods_get on a function export must fail the handler.");
            StringAssert.Contains("function", errors[0].Error,
                "The error should steer the author toward mods_call.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_ModsCall_SelfCall_CannotDisarmHandlerGuard()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("m", @"
                mods_export('noop', function() return 1 end)
                hooks_on('go', function()
                    mods_call(mod_id(), 'noop')
                    local x = 0
                    for i = 1, 1000000 do x = x + 1 end
                    store_set('escaped', 'yes')
                end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual("", store.Get("m", "escaped"),
                "A self mods_call must not disarm the outer guard: the over-budget loop after it must be cut.");
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("m"),
                "The over-budget handler must fail with a recorded error, not run to completion unlimited.");
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_ModsCall_IndirectCycle_CannotDisarmHandlerGuard()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            stack.Runtime.LoadMod("a", @"
                mods_export('noop', function() return 1 end)
                hooks_on('go', function()
                    mods_call('b', 'pong')
                    local x = 0
                    for i = 1, 1000000 do x = x + 1 end
                    store_set('escaped', 'yes')
                end)");
            stack.Runtime.LoadMod("b", "mods_export('pong', function() return mods_call('a', 'noop') end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual("", store.Get("a", "escaped"),
                "An A->B->A mods_call cycle must not disarm A's outer guard (self-call bans cannot catch this).");
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("a"),
                "The over-budget handler must fail with a recorded error.");
        }

        [Test]
        public void LuaCs_HandlerDiesInsideWorldTransaction_NextHandlerCommandStillReachesSink()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("t", @"
                hooks_on('leak', function()
                    coreai_world_begin()
                    error('dies before commit')
                end)
                hooks_on('later', function() coreai_world_destroy('victim') end)",
                LuaCapabilities.WorldEdit);

            stack.Runtime.EmitEvent("leak", "");
            stack.Runtime.Tick(0);
            Assert.IsNotEmpty(stack.Runtime.GetRecentHandlerErrors("t"), "The leaking handler must fail.");
            Assert.AreEqual(0, sink.Commands.Count,
                "The aborted transaction's buffered commands must never reach the sink.");

            stack.Runtime.EmitEvent("later", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual(1, sink.Commands.Count,
                "A transaction leaked by a dead handler must not silently swallow the next handler's world command.");
        }

        [Test]
        public void LuaCs_LoadChunkDiesInsideWorldTransaction_NextLoadCommandStillReachesSink()
        {
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(new MemoryStore(), sink);

            Assert.Catch(() => stack.Runtime.LoadMod("dying",
                "coreai_world_begin()\nerror('dies before commit')",
                LuaCapabilities.WorldEdit));
            Assert.IsFalse(stack.Runtime.IsLoaded("dying"));

            stack.Runtime.LoadMod("healthy", "coreai_world_destroy('victim')", LuaCapabilities.WorldEdit);
            Assert.AreEqual(1, sink.Commands.Count,
                "A transaction leaked by a failing load chunk must not swallow the next load's world command.");
        }

        [Test]
        public void LuaCs_OneOff_ExecuteReturnsOutput()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("return 2 + 3", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Success, result.Error);
            Assert.AreEqual("5", result.Output);
        }

        [Test]
        [Timeout(15000)]
        public void LuaCs_OneOff_RunawayLoop_CutByInstructionBudget_DoesNotHang()
        {
            LuaCsModStack stack = BuildStack();

            LuaTool.LuaResult result = stack.ToolExecutor
                .ExecuteAsync("while true do local x = 1 end", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsFalse(result.Success, "A runaway loop must be cut by the budget, not run forever.");
            Assert.IsFalse(string.IsNullOrEmpty(result.Error), "A cut runaway must report an error.");
        }

        [Test]
        public void LuaCs_WorldEditTier_RoutesCommandToSink()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("w", @"
                store_set('has_world_destroy', (coreai_world_destroy ~= nil) and 'yes' or 'no')
                hooks_on('do_it', function() coreai_world_destroy('target') end)",
                LuaCapabilities.WorldEdit);

            Assert.AreEqual("yes", store.Get("w", "has_world_destroy"),
                "A WorldEdit-tier mod must see the coreai_world_* APIs.");

            stack.Runtime.EmitEvent("do_it", "");
            stack.Runtime.Tick(0);

            Assert.AreEqual(1, sink.Commands.Count, "The world API must route exactly one command to the sink.");
            Assert.AreEqual(AiGameCommandTypeIds.WorldCommand, sink.Commands[0].CommandTypeId);
        }

        [Test]
        public void LuaCs_ReadTier_DoesNotExposeWriteApis()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            stack.Runtime.LoadMod("r", @"
                store_set('has_world_destroy', (coreai_world_destroy ~= nil) and 'yes' or 'no')
                store_set('has_world_spawn', (coreai_world_spawn ~= nil) and 'yes' or 'no')",
                LuaCapabilities.Read);

            Assert.AreEqual("no", store.Get("r", "has_world_destroy"),
                "A Read-tier mod must not see world-edit APIs (fail-closed).");
            Assert.AreEqual("no", store.Get("r", "has_world_spawn"));

            // Calling an absent write API from a read-tier mod fails the load (attempt to call nil).
            Assert.Catch(() => stack.Runtime.LoadMod("r2", "coreai_world_destroy('x')", LuaCapabilities.Read));
            Assert.IsFalse(stack.Runtime.IsLoaded("r2"));
            Assert.AreEqual(0, sink.Commands.Count, "No command may reach the sink from a read-tier mod.");
        }

        [Test]
        public void LuaCs_NestedModsCall_WorldTransaction_DoesNotCorruptCallerBuffer()
        {
            MemoryStore store = new();
            FakeCommandSink sink = new();
            LuaCsModStack stack = BuildStack(store, sink);

            // B opens and commits its OWN world transaction while A's is still open. Before the per-run
            // transaction frames, B ran against the SAME shared buffer/flag as A: B's begin cleared A's
            // buffered 'a1' and its commit reset A's active flag, so A's commit below threw
            // "no active transaction" (or silently lost 'a1'). Each run must now own an isolated frame.
            stack.Runtime.LoadMod("b", @"
                mods_export('work', function()
                    coreai_world_begin()
                    coreai_world_destroy('b1')
                    coreai_world_destroy('b2')
                    return coreai_world_commit()
                end)", LuaCapabilities.WorldEdit);

            stack.Runtime.LoadMod("a", @"
                hooks_on('go', function()
                    coreai_world_begin()
                    coreai_world_destroy('a1')
                    local nb = mods_call('b', 'work')
                    local na = coreai_world_commit()
                    store_set('nb', tostring(nb))
                    store_set('na', tostring(na))
                end)", LuaCapabilities.WorldEdit);

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            Assert.IsEmpty(stack.Runtime.GetRecentHandlerErrors("a"),
                "A's commit must not throw: its transaction survives B's nested begin/commit.");
            Assert.AreEqual("2", store.Get("a", "nb"), "B's nested commit flushes its own 2 buffered commands.");
            Assert.AreEqual("1", store.Get("a", "na"),
                "A's commit flushes ONLY its own single buffered command, proving isolation.");
            Assert.AreEqual(3, sink.Commands.Count,
                "b1 + b2 (nested commit) then a1 (outer commit) all reach the sink exactly once.");
            StringAssert.Contains("a1", sink.Commands[2].JsonPayload,
                "The outer transaction's buffered command commits last and is not lost or merged into B's.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_RunawayHandler_IsCutAndSurvivesOneTrip()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // Tight per-call budgets so the runaway loop is cut quickly (keeps the test fast).
                HandlerTimeoutMs = 100,
                HandlerMaxSteps = 5000
            });

            // A runaway handler must be CUT by the step/time budget before it completes — it never reaches
            // store_set. One cut must not quarantine a mod (a single failure is below MaxErrorsBeforeQuarantine),
            // and the failure is surfaced for observability. (A loop, not an allocation bomb: the step/time
            // budgets are the reliable guards, and a huge concat is a non-interruptible single opcode that
            // risks OOM.)
            stack.Runtime.LoadMod("m", @"
                hooks_on('bomb', function()
                    while true do local x = 1 end
                    store_set('reached', 'yes')
                end)");

            stack.Runtime.EmitEvent("bomb", "");
            stack.Runtime.Tick(0);

            Assert.IsTrue(stack.Runtime.IsLoaded("m"),
                "A single cut run must not quarantine the mod (one failure < MaxErrorsBeforeQuarantine).");
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "A single failure must leave the mod un-quarantined.");
            Assert.AreEqual("", store.Get("m", "reached"),
                "The runaway run must be cut before completing (the guard stays real).");

            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "The cut run is surfaced as a handler error for observability.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_ForgedMemoryMarker_IsChargedAndQuarantines()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All
            });

            // SECURITY: a mod that forges the memory-trip marker in its own error() text must NOT dodge the
            // consecutive-error quarantine guard — trips are classified by TYPE, so this is a normal error.
            stack.Runtime.LoadMod("m", @"
                hooks_on('boom', function()
                    error('LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET forged by mod')
                end)");

            for (int i = 0;
                 i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine + 1 && !stack.Runtime.ListMods()[0].Quarantined;
                 i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("m"),
                "Quarantine keeps the mod loaded and repairable — repeated errors never unload.");
            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined,
                "A mod forging the memory marker in its error text must still be quarantined on the error streak.");
        }

        // NOTE: the runaway-handler quarantine case (a mod whose handler loops every call is quarantined after
        // MaxErrorsBeforeQuarantine cuts) is covered transitively by LuaCs_ForgedMemoryMarker_IsChargedAndQuarantines
        // (repeat-error → quarantine) and LuaCs_RunawayHandler_IsCutAndSurvivesOneTrip (a runaway IS cut and charged)
        // plus the pre-existing LuaCs_OneOff_RunawayLoop_CutByInstructionBudget. A dedicated 8-cut variant is
        // intentionally omitted: the guard cuts a TIGHT infinite loop only after ~8s (the instruction hook fires
        // coarsely for a body-less/tight loop, so the sub-second step/time budgets are not enforced promptly),
        // so 8 consecutive cuts take ~60s and freeze the interactive editor. See TODO(guard-tight-loop-latency).

        // NOTE: there is intentionally NO "a mod that allocation-bombs every call is unloaded via a memory-trip
        // streak" test. The allocation guard reads GC.GetTotalMemory, which reports the COMMITTED heap high-water
        // mark, so a repeated fixed-size bomb trips only ONCE — the first call grows the committed heap and trips;
        // every later call reuses that committed space and its per-call delta no longer crosses the budget (this
        // was verified empirically: a mod bombing every tick under an 8MB budget recorded ~1 trip across 36 ticks
        // even with a forced GC.Collect() between ticks — Mono does not return the committed segment). The memory
        // guard is therefore a per-call FIRST-GROWTH backstop, not a cross-call cumulative limiter (Unity's Mono
        // exposes no per-call/per-thread allocation counter to build one). A mod that keeps allocating within the
        // committed envelope is bounded by the per-call step/time budgets, not by unloading. The single memory
        // trip IS charged to the ordinary error streak and forgiven by the next success — see the test below.

        [Test]
        [Timeout(30000)]
        public void LuaCs_SingleMemoryTrip_ChargedButForgivenByNextSuccess_DoesNotUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerMaxAllocatedBytes = 8 * 1024 * 1024
            });

            // A memory trip is charged to the ordinary consecutive-error streak (a success resets it), so a mod
            // that trips once — on its own first oversized allocation or on unrelated shared-heap growth — and
            // then runs cleanly is forgiven and never quarantined. This mod bombs on its FIRST invocation only and
            // succeeds on every later call, so it must stay dispatching well past MaxErrorsBeforeQuarantine ticks.
            stack.Runtime.LoadMod("occasional", @"
                local n = 0
                hooks_on('poke', function()
                    n = n + 1
                    if n == 1 then
                        local s = string.rep('x', 1000000)
                        for i = 1, 6 do s = s .. s end
                    end
                end)");

            for (int i = 0; i < (LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine * 2) + 2; i++)
            {
                stack.Runtime.EmitEvent("poke", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("occasional"),
                "A single memory trip followed by successful calls must be forgiven (streak reset) and keep the mod loaded.");
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "A forgiven streak must never quarantine the mod.");
        }

        [Test]
        public void LuaCs_AdditionalGameplayBindings_ReachLoadedMods()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // A host/per-scene binding injected alongside the built-in surface (the seam a demo uses to add
                // e.g. forge_define). It must reach a persistently-loaded mod's handler.
                AdditionalGameplayBindings = (registry, caps) =>
                    registry.Register("extra_double", new Func<double, double>(x => x * 2))
            });

            stack.Runtime.LoadMod("m", @"
                hooks_on('go', function()
                    store_set('r', tostring(extra_double(21)))
                end)");

            stack.Runtime.EmitEvent("go", "");
            stack.Runtime.Tick(0);

            StringAssert.StartsWith("42", store.Get("m", "r"),
                "An injected AdditionalGameplayBindings API must be callable from a loaded mod's handler.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_PcallSwallowedMemoryTrip_DoesNotLaunderLaterRealError()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerMaxAllocatedBytes = 64 * 1024 * 1024
            });

            // SECURITY: the mod swallows a failure INSIDE pcall, then throws a REAL, unrelated error. That real
            // error must be charged to the normal error streak and quarantine the mod — a swallowed inner failure
            // must never launder a subsequent real error out of the quarantine guard.
            stack.Runtime.LoadMod("m", @"
                hooks_on('evade', function()
                    pcall(function() error('swallowed inner failure') end)
                    error('a real unrelated error')
                end)");

            for (int i = 0;
                 i < LuaCsModRuntime.DefaultMaxErrorsBeforeQuarantine + 1 && !stack.Runtime.ListMods()[0].Quarantined;
                 i++)
            {
                stack.Runtime.EmitEvent("evade", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined,
                "A real error after a pcall-swallowed memory trip must charge the error streak and quarantine the mod.");
        }

        /// <summary>Stack with a low quarantine threshold so streak tests stay fast.</summary>
        private static LuaCsModStack BuildQuarantineStack(MemoryStore store, int maxErrorsBeforeQuarantine = 2)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                MaxErrorsBeforeQuarantine = maxErrorsBeforeQuarantine
            });
        }

        private const string FailingModSource = @"
            hooks_on('boom', function() error('boom') end)
            hooks_on('work', function()
                store_set('n', tostring((tonumber(store_get('n')) or 0) + 1))
            end)
            hooks_every(0.05, function()
                store_set('t', tostring((tonumber(store_get('t')) or 0) + 1))
            end)";

        private const string HealthyModSource = @"
            hooks_on('work', function()
                store_set('n', tostring((tonumber(store_get('n')) or 0) + 1))
            end)";

        [Test]
        public void LuaCs_Quarantine_ModStaysListedAndStopsDispatching()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            string quarantinedId = null;
            int quarantinedStreak = 0;
            stack.Runtime.ModQuarantined += (id, count) =>
            {
                quarantinedId = id;
                quarantinedStreak = count;
            };

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("m"), "A quarantined mod must STAY loaded and addressable.");
            LuaModInfo info = stack.Runtime.ListMods()[0];
            Assert.IsTrue(info.Quarantined, "ListMods must surface the quarantine so the repairing agent SEES it.");
            Assert.AreEqual("m", quarantinedId, "ModQuarantined must fire with the mod id.");
            Assert.AreEqual(2, quarantinedStreak, "ModQuarantined must carry the error streak.");
            Assert.IsTrue(stack.Runtime.TryGetModSource("m", out string source) && source.Length > 0,
                "get_source must keep working for a quarantined mod.");

            // Suspended: named-event handlers and timers must both stop running.
            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0.06);
            stack.Runtime.Tick(0.06);
            Assert.AreEqual("", store.Get("m", "n"), "A quarantined mod's hooks_on handlers must not dispatch.");
            Assert.AreEqual("", store.Get("m", "t"), "A quarantined mod's hooks_every timers must not fire.");
        }

        [Test]
        public void LuaCs_Quarantine_ReloadClearsQuarantineAndResumesDispatch()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined, "Precondition: the mod is quarantined.");

            // The flagship repair path: an async LLM repair lands as a plain ReloadMod — it must work on a
            // quarantined mod, clear the quarantine + streak, and dispatch must resume.
            Assert.DoesNotThrow(() => stack.Runtime.ReloadMod("m", HealthyModSource),
                "ReloadMod on a quarantined mod must succeed normally.");

            LuaModInfo info = stack.Runtime.ListMods()[0];
            Assert.IsFalse(info.Quarantined, "A successful reload must clear the quarantine.");
            Assert.AreEqual(0, info.ErrorCount, "A successful reload must clear the error streak.");

            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "n"), "Dispatch must resume after the repairing reload.");
        }

        [Test]
        public void LuaCs_Quarantine_ReloadLandsMidTick_FreshInstanceIsNotQuarantined()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            stack.Runtime.LoadMod("m", FailingModSource);

            // Reproduces the stale-snapshot race: Tick iterates a snapshot of mod objects; this subscriber
            // reloads the mod MID-TICK the moment the streak hits the threshold, swapping the registry
            // entry. The quarantine check at the end of the tick then sees the OLD object's streak — it
            // must re-resolve the live entry and skip, never suspending the freshly repaired instance.
            bool repaired = false;
            stack.Runtime.ModHandlerErrored += (id, error, count) =>
            {
                if (!repaired && count >= 2)
                {
                    repaired = true;
                    stack.Runtime.ReloadMod("m", HealthyModSource);
                }
            };

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(repaired, "Precondition: the mid-tick repair ran.");
            Assert.IsTrue(stack.Runtime.IsLoaded("m"));
            Assert.IsFalse(stack.Runtime.ListMods()[0].Quarantined,
                "The stale snapshot's error streak must not quarantine the freshly reloaded instance.");

            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "n"), "The repaired instance must dispatch normally.");
        }

        [Test]
        public void LuaCs_LogicSlots_OverrideClearedOnUnload()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            List<(string ModId, LuaModTeardownReason Reason)> teardowns = new();
            stack.Runtime.ModTearingDown += (id, reason) => teardowns.Add((id, reason));

            stack.Runtime.LoadMod("m", "logic_define('dmg', function(x) return x * 2 end)");
            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double value, 21), "The mod's override is installed.");
            Assert.AreEqual(42d, value);

            stack.Runtime.UnloadMod("m");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "Unload must clear the mod's logic-slot override — the dead mod's formula is never invoked again.");
            Assert.IsFalse(slots.TryInvokeNumber("dmg", out _, 21), "The call falls back to the C# default.");
            CollectionAssert.Contains(teardowns, ("m", LuaModTeardownReason.Unload),
                "ModTearingDown must fire for the unload so future subsystems can hook the same point.");
        }

        [Test]
        public void LuaCs_LogicSlots_ReloadDropsOldFormula_AndKeepsTheReplacementsOwn()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");
            slots.DeclareSlot("loot");

            stack.Runtime.LoadMod("m", @"
                logic_define('dmg', function(x) return x * 2 end)
                logic_define('loot', function() return 10 end)");

            // v2 re-defines dmg with a NEW formula and drops loot entirely. After the reload the old
            // instance's formulas must be gone: dmg answers with the new math, loot reverts to vanilla.
            stack.Runtime.ReloadMod("m", "logic_define('dmg', function(x) return x * 3 end)");

            Assert.IsTrue(slots.TryInvokeNumber("dmg", out double value, 10),
                "The replacement's own logic_define (made during its load chunk) must survive the teardown.");
            Assert.AreEqual(30d, value, "The NEW formula answers — the old mod version's formula is dead.");
            Assert.IsFalse(slots.IsOverridden("loot"),
                "A slot the new version no longer defines must revert to vanilla, not keep the stale formula.");
        }

        [Test]
        public void LuaCs_LogicSlots_QuarantineRevertsOverridesToVanilla()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildQuarantineStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            stack.Runtime.LoadMod("m",
                "logic_define('dmg', function(x) return x * 2 end)\n" + FailingModSource);
            Assert.IsTrue(slots.IsOverridden("dmg"), "Precondition: the override is installed.");

            for (int i = 0; i < 2; i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.ListMods()[0].Quarantined, "Precondition: the mod is quarantined.");
            Assert.IsFalse(slots.IsOverridden("dmg"),
                "Quarantine must clear the broken mod's overrides — its formula must stop being invoked.");
        }

        [Test]
        public void LuaCs_LogicSlots_OverrideFailure_AttributedToOwningModInDiagnostics()
        {
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(store);
            LuaCsLogicSlots slots = stack.GameplayBindings.LogicSlots;
            slots.DeclareSlot("dmg");

            stack.Runtime.LoadMod("m", "logic_define('dmg', function() error('formula broke') end)");

            Assert.IsFalse(slots.TryInvokeNumber("dmg", out _, 1),
                "The failing override fails open: the call reports 'not overridden'.");
            Assert.IsFalse(slots.IsOverridden("dmg"), "The failing override is reset to vanilla.");

            // The old behavior was a SILENT revert; the failure must now land in the mod's own error
            // channel with the slot named, so diagnostics/get_mod_logs show which mod's formula broke.
            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "The override failure must be recorded against the owning mod.");
            StringAssert.Contains("dmg", errors[0].Error, "The recorded error must name the slot.");
            Assert.AreEqual(1, stack.Runtime.ListMods()[0].ErrorCount,
                "The override failure charges the owning mod's error streak.");
        }
    }
}
