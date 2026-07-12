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
                .ExecuteAsync("while true do end", CancellationToken.None)
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
        public void LuaCs_MemoryBudgetTrip_CutsRunButDoesNotUnloadHealthyMod()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                // A tiny per-call allocation budget so a modest live string trips the process-heap backstop.
                HandlerMaxAllocatedBytes = 4 * 1024 * 1024
            });

            // The handler retains ~8MB (concat doubling), which survives the confirming forced GC and so
            // trips the 4MB budget on every call. A memory trip must cut the run but NOT count toward the
            // consecutive-error auto-unload streak: firing it well past MaxErrorsBeforeUnload (8) must leave
            // the mod loaded, since the budget measures the whole process heap and cannot blame the mod.
            stack.Runtime.LoadMod("m", @"
                hooks_on('bomb', function()
                    local s = string.rep('x', 1000000)
                    s = s .. s
                    s = s .. s
                    s = s .. s
                    store_set('reached', 'yes')
                end)");

            for (int i = 0; i < LuaCsModRuntime.MaxErrorsBeforeUnload + 4; i++)
            {
                stack.Runtime.EmitEvent("bomb", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsTrue(stack.Runtime.IsLoaded("m"),
                "Repeated process-heap memory-budget trips must not auto-unload a blameless mod.");
            Assert.AreEqual("", store.Get("m", "reached"),
                "Each over-budget run must still be cut before completing (the guard stays real).");

            IReadOnlyList<LuaModInfo> mods = stack.Runtime.ListMods();
            Assert.AreEqual(1, mods.Count);
            Assert.AreEqual(0, mods[0].ErrorCount,
                "A memory-budget trip must not be charged to the consecutive-error streak.");

            IReadOnlyList<LuaModHandlerError> errors = stack.Runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "The cut run is still surfaced as a handler error for observability.");
            StringAssert.Contains("EXCEEDED_MEMORY_BUDGET", errors[errors.Count - 1].Error,
                "The recorded error identifies the memory-budget trip.");
        }

        [Test]
        [Timeout(30000)]
        public void LuaCs_ForgedMemoryMarker_IsChargedAndUnloads()
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
            // consecutive-error auto-unload guard — trips are classified by TYPE, so this is a normal error.
            stack.Runtime.LoadMod("m", @"
                hooks_on('boom', function()
                    error('LuaCsSecureEnvironment: EXCEEDED_MEMORY_BUDGET forged by mod')
                end)");

            for (int i = 0; i < LuaCsModRuntime.MaxErrorsBeforeUnload + 1 && stack.Runtime.IsLoaded("m"); i++)
            {
                stack.Runtime.EmitEvent("boom", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsFalse(stack.Runtime.IsLoaded("m"),
                "A mod forging the memory marker in its error text must still be auto-unloaded on the error streak.");
        }

        [Test]
        [Timeout(60000)]
        public void LuaCs_RepeatedMemoryTrips_EventuallyUnloadRepeatOffender()
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                HandlerMaxAllocatedBytes = 4 * 1024 * 1024
            });

            // A mod that trips the allocation budget on EVERY call and never completes is a genuine repeat
            // offender; the separate capped memory-trip streak must eventually unload it, even though a single
            // trip is not charged to the general error streak (a blameless process-heap false positive).
            stack.Runtime.LoadMod("m", @"
                hooks_on('bomb', function()
                    local s = string.rep('x', 1000000)
                    s = s .. s
                    s = s .. s
                    s = s .. s
                end)");

            for (int i = 0; i < LuaCsModRuntime.MaxMemoryTripsBeforeUnload + 2 && stack.Runtime.IsLoaded("m"); i++)
            {
                stack.Runtime.EmitEvent("bomb", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsFalse(stack.Runtime.IsLoaded("m"),
                "A mod that trips the memory budget on every call must eventually be unloaded by the capped memory-trip streak.");
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
                HandlerMaxAllocatedBytes = 4 * 1024 * 1024
            });

            // SECURITY: the mod arms a budget trip INSIDE pcall (which swallows it), then throws a REAL,
            // unrelated error. That real error must be charged to the normal error streak and unload the mod —
            // it must NOT be laundered into a blameless "memory trip" by a stale trip signal.
            stack.Runtime.LoadMod("m", @"
                hooks_on('evade', function()
                    pcall(function()
                        local s = string.rep('x', 1000000)
                        s = s .. s
                        s = s .. s
                        s = s .. s
                    end)
                    error('a real unrelated error')
                end)");

            for (int i = 0; i < LuaCsModRuntime.MaxErrorsBeforeUnload + 1 && stack.Runtime.IsLoaded("m"); i++)
            {
                stack.Runtime.EmitEvent("evade", "");
                stack.Runtime.Tick(0);
            }

            Assert.IsFalse(stack.Runtime.IsLoaded("m"),
                "A real error after a pcall-swallowed memory trip must charge the error streak and unload the mod.");
        }
    }
}
