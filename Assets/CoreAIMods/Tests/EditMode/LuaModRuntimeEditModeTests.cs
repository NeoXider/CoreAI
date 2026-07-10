using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using MoonSharp.Interpreter;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaModRuntimeEditModeTests
    {
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

        [Test]
        public void LuaModRuntime_LoadMod_ValidChunkLoadsAndDuplicateIdThrows()
        {
            LuaModRuntime runtime = new();

            runtime.LoadMod("m", "local x = 1");

            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.Throws<InvalidOperationException>(() => runtime.LoadMod("m", "local x = 2"));
        }

        [Test]
        public void LuaModRuntime_LoadMod_BadLuaCodeThrowsAndIsLoadedFalse()
        {
            LuaModRuntime runtime = new();

            Assert.Throws<ScriptRuntimeException>(() => runtime.LoadMod("bad", "error('bad')"));
            Assert.IsFalse(runtime.IsLoaded("bad"));
        }

        [Test]
        public void LuaModRuntime_HooksOnEmitEventTick_DispatchesHandler()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('hit', function(name, payload)
                    store_set('last', payload)
                end)");

            runtime.EmitEvent("hit", "42");
            runtime.Tick(0);

            Assert.AreEqual("42", store.Get("m", "last"));
        }

        [Test]
        public void LuaModRuntime_HandlerRegistersHandlerForSameEvent_TickDoesNotThrow()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            // A handler for 'hit' registers another 'hit' handler while it runs. The dispatch enumerates a
            // snapshot, so this must not throw "Collection was modified" out of Tick().
            runtime.LoadMod("m", @"
                hooks_on('hit', function(name, payload)
                    store_set('ran', '1')
                    hooks_on('hit', function() store_set('second', '1') end)
                end)");

            runtime.EmitEvent("hit", "x");
            Assert.DoesNotThrow(() => runtime.Tick(0),
                "Registering a handler during dispatch must not crash the tick");
            Assert.AreEqual("1", store.Get("m", "ran"));

            // The newly registered handler fires on a later event, proving the runtime stayed healthy.
            runtime.EmitEvent("hit", "y");
            runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "second"));
        }

        [Test]
        public void LuaModRuntime_HooksEvery_IntervalElapsed_FiresOnce()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_every(0.1, function()
                    local n = tonumber(store_get('ticks')) or 0
                    store_set('ticks', tostring(n + 1))
                end)");

            runtime.Tick(0.05);
            Assert.AreEqual("", store.Get("m", "ticks"));

            runtime.Tick(0.06);
            Assert.AreEqual("1", store.Get("m", "ticks"));
        }

        [Test]
        public void LuaModRuntime_ReportLogging_MutedByDefaultAndCanBeEnabled()
        {
            LuaModRuntime runtime = new();
            List<string> reports = new();
            runtime.ModReportEmitted += (modId, message) => reports.Add($"{modId}:{message}");
            runtime.LoadMod("m", @"
                hooks_every(0.1, function()
                    report('tick')
                end)");

            runtime.Tick(0.11);
            Assert.AreEqual(0, reports.Count, "Mod report() output must be muted by default.");
            Assert.IsFalse(runtime.ListMods()[0].LogReports);

            Assert.IsTrue(runtime.SetModReportLoggingEnabled("m", true));
            runtime.Tick(0.11);
            CollectionAssert.AreEqual(new[] { "m:tick" }, reports);
            Assert.IsTrue(runtime.GetModReportLoggingEnabled("m"));
            Assert.IsTrue(runtime.ListMods()[0].LogReports);

            Assert.IsTrue(runtime.SetModReportLoggingEnabled("m", false));
            runtime.Tick(0.11);
            Assert.AreEqual(1, reports.Count);
        }

        [Test]
        public void LuaModRuntime_HooksEvery_IntervalBelowMinimum_LoadModThrows()
        {
            LuaModRuntime runtime = new();

            Assert.Throws<ScriptRuntimeException>(() =>
                runtime.LoadMod("m", "hooks_every(0.01, function() end)"));
            Assert.IsFalse(runtime.IsLoaded("m"));
        }

        [Test]
        public void LuaModRuntime_EventsEmit_FromOneMod_ReachesOtherModAndRaisesEvent()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            string emittedModId = "";
            string emittedName = "";
            string emittedPayload = "";
            runtime.ModEventEmitted += (modId, name, payload) =>
            {
                emittedModId = modId;
                emittedName = name;
                emittedPayload = payload;
            };

            runtime.LoadMod("b", @"
                hooks_on('ping', function(name, payload)
                    store_set('received', payload)
                end)");
            runtime.LoadMod("a", "events_emit('ping', 'hello')");

            Assert.AreEqual("a", emittedModId);
            Assert.AreEqual("ping", emittedName);
            Assert.AreEqual("hello", emittedPayload);

            runtime.Tick(0);
            Assert.AreEqual("hello", store.Get("b", "received"));
        }

        [Test]
        public void LuaModRuntime_EventBudget_CapsDispatchPerTick()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);

            // The counter mod records how many 'ping' handler invocations actually run, both in
            // total and within the most recent tick (reset by the host between ticks below).
            runtime.LoadMod("counter", @"
                hooks_on('ping', function()
                    local total = tonumber(store_get('count')) or 0
                    store_set('count', tostring(total + 1))
                    local this_tick = tonumber(store_get('tick_count')) or 0
                    store_set('tick_count', tostring(this_tick + 1))
                end)");

            // The emitter mod floods the counter with far more than the per-tick budget in a
            // single 'go' handler call. events_emit only delivers to *other* mods, so every ping
            // lands in the counter's pending queue (queue capacity is well above the flood size).
            int cap = LuaModRuntime.DefaultMaxEventsDispatchedPerTick;
            int flood = cap * 2;
            runtime.LoadMod("emitter", $@"
                hooks_on('go', function()
                    for i = 1, {flood} do
                        events_emit('ping', tostring(i))
                    end
                end)");

            runtime.EmitEvent("go", "");

            // Drive several ticks; mod iteration order within a tick is unspecified, so the flood
            // may land before or after the counter is serviced this tick. Either way, no single
            // tick may dispatch more handler invocations than the budget allows.
            for (int tick = 0; tick < 4; tick++)
            {
                store.Set("counter", "tick_count", "0");
                runtime.Tick(0);
                int dispatchedThisTick = int.Parse(store.Get("counter", "tick_count"));
                Assert.LessOrEqual(dispatchedThisTick, cap,
                    "A single tick must not dispatch more handler invocations than the per-tick budget.");
            }

            // The runtime must not drop the surplus: every flooded ping is eventually delivered
            // across subsequent ticks (flood < queue capacity, so nothing is dropped at enqueue).
            int total = int.Parse(store.Get("counter", "count"));
            Assert.AreEqual(flood, total,
                "Events over the per-tick budget must carry over to later ticks, not be lost.");
        }

        [Test]
        public void LuaModRuntime_EventBudget_CapsTotalDispatchAcrossModsPerTick()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);

            // Enough receiver mods that, at the per-mod cap each, their combined per-tick demand
            // far exceeds the global budget — so without a global cap a single tick would dispatch
            // perModCap * receiverCount handler calls. Each receiver records, per tick, how many
            // 'ping' invocations actually ran (the host resets these counters between ticks).
            int perModCap = LuaModRuntime.DefaultMaxEventsDispatchedPerTick;
            int globalCap = LuaModRuntime.DefaultMaxEventsDispatchedPerTickGlobal;
            int receiverCount = globalCap / perModCap + 4;
            string[] receivers = new string[receiverCount];
            for (int r = 0; r < receiverCount; r++)
            {
                string id = $"receiver{r}";
                receivers[r] = id;
                runtime.LoadMod(id, @"
                    hooks_on('ping', function()
                        local total = tonumber(store_get('count')) or 0
                        store_set('count', tostring(total + 1))
                        local this_tick = tonumber(store_get('tick_count')) or 0
                        store_set('tick_count', tostring(this_tick + 1))
                    end)");
            }

            // One flood of pings reaches every *other* mod, so each receiver gets its own queue
            // filled well past the per-mod cap.
            int floodPerReceiver = perModCap * 2;
            runtime.LoadMod("emitter", $@"
                hooks_on('go', function()
                    for i = 1, {floodPerReceiver} do
                        events_emit('ping', tostring(i))
                    end
                end)");

            runtime.EmitEvent("go", "");

            // Drive enough ticks to drain every receiver. No single tick may dispatch more handler
            // invocations across all mods than the global budget allows.
            int ticks = floodPerReceiver * receiverCount / globalCap + 4;
            for (int tick = 0; tick < ticks; tick++)
            {
                foreach (string id in receivers)
                {
                    store.Set(id, "tick_count", "0");
                }

                runtime.Tick(0);

                int dispatchedThisTick = 0;
                foreach (string id in receivers)
                {
                    dispatchedThisTick += int.Parse(store.Get(id, "tick_count"));
                }

                Assert.LessOrEqual(dispatchedThisTick, globalCap,
                    "A single tick must not dispatch more handler invocations across all mods than the global budget.");
            }

            // Every flooded ping is eventually delivered to every receiver: the global cap only
            // defers dispatch to later ticks, it never drops queued events.
            foreach (string id in receivers)
            {
                Assert.AreEqual(floodPerReceiver, int.Parse(store.Get(id, "count")),
                    $"Receiver '{id}' must eventually receive every queued ping across ticks.");
            }
        }

        [Test]
        public void LuaModRuntime_HandlerRepeatedErrors_AutoUnloadsMod()
        {
            LuaModRuntime runtime = new();
            runtime.LoadMod("m", "hooks_on('bad', function() error('boom') end)");

            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            Assert.IsFalse(runtime.IsLoaded("m"));
        }

        [Test]
        public void LuaModRuntime_HandlerError_RaisesModHandlerErroredWithRisingCount()
        {
            LuaModRuntime runtime = new();
            List<(string ModId, string Error, int Count)> errors = new();
            runtime.ModHandlerErrored += (modId, error, count) => errors.Add((modId, error, count));

            runtime.LoadMod("m", "hooks_on('bad', function() error('boom') end)");

            runtime.EmitEvent("bad", "");
            runtime.Tick(0);
            runtime.EmitEvent("bad", "");
            runtime.Tick(0);

            Assert.AreEqual(2, errors.Count);
            Assert.AreEqual("m", errors[0].ModId);
            Assert.AreEqual(1, errors[0].Count);
            Assert.AreEqual(2, errors[1].Count);
            StringAssert.Contains("boom", errors[0].Error);
            // The error text is flattened to a single line for prompt/log safety.
            Assert.IsFalse(errors[0].Error.Contains("\n"));
        }

        [Test]
        public void LuaModRuntime_HandlerErrored_CountResetsAfterSuccess()
        {
            LuaModRuntime runtime = new();
            List<int> counts = new();
            runtime.ModHandlerErrored += (_, _, count) => counts.Add(count);

            runtime.LoadMod("m", @"
                hooks_on('bad', function() error('boom') end)
                hooks_on('good', function() end)");

            runtime.EmitEvent("bad", "");
            runtime.Tick(0);
            runtime.EmitEvent("good", "");
            runtime.Tick(0);
            runtime.EmitEvent("bad", "");
            runtime.Tick(0);

            // Both failures report streak length 1 because the success in between reset the counter.
            CollectionAssert.AreEqual(new[] { 1, 1 }, counts);
        }

        [Test]
        public void LuaModRuntime_UnloadModAndReloadMod_BasicBehavior()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");

            Assert.IsTrue(runtime.UnloadMod("m"));
            Assert.IsFalse(runtime.IsLoaded("m"));
            Assert.IsFalse(runtime.UnloadMod("m"));

            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");
            runtime.ReloadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'new')
                end)");

            runtime.EmitEvent("set", "");
            runtime.Tick(0);
            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.AreEqual("new", store.Get("m", "value"));
        }

        [Test]
        public void LuaModRuntime_LoadReloadUnload_RaisesSourceLifecycleEvents()
        {
            LuaModRuntime runtime = new();
            List<string> events = new();
            runtime.ModSourceLoaded += (id, source, caps) =>
                events.Add($"load:{id}:{source}:{caps}");
            runtime.ModSourceUnloaded += (id, source, caps) => events.Add($"unload:{id}:{source}:{caps}");

            runtime.LoadMod("m", "local a = 1", LuaCapabilities.Read);
            runtime.ReloadMod("m", "local b = 2");
            runtime.UnloadMod("m");

            CollectionAssert.AreEqual(
                new[]
                {
                    "load:m:local a = 1:Read",
                    "load:m:local b = 2:Read",
                    "unload:m:local b = 2:Read"
                },
                events);
        }

        [Test]
        public void LuaModRuntime_ModSourceLoaded_ThrowingSubscriber_DoesNotFailLoadOrOtherSubscribers()
        {
            LuaModRuntime runtime = new();
            bool healthySubscriberRan = false;
            runtime.ModSourceLoaded += (_, _, _) => throw new InvalidOperationException("boom");
            runtime.ModSourceLoaded += (_, _, _) => healthySubscriberRan = true;

            Assert.DoesNotThrow(() => runtime.LoadMod("m", "local a = 1"),
                "A throwing ModSourceLoaded subscriber must not make a healthy load fail.");

            Assert.IsTrue(runtime.IsLoaded("m"), "The mod must be loaded despite the throwing subscriber.");
            Assert.IsTrue(healthySubscriberRan, "Other subscribers must still run after one throws.");
        }

        [Test]
        public void LuaModRuntime_ReloadMod_BadNewCode_KeepsOldModWorking()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('set', function()
                    store_set('value', 'old')
                end)");

            Assert.Throws<ScriptRuntimeException>(() => runtime.ReloadMod("m", "error('broken')"));

            Assert.IsTrue(runtime.IsLoaded("m"));
            runtime.EmitEvent("set", "");
            runtime.Tick(0);
            Assert.AreEqual("old", store.Get("m", "value"));
        }

        [Test]
        public void LuaModRuntime_SuccessfulHandlerCall_ResetsErrorCount()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", @"
                hooks_on('bad', function() error('boom') end)
                hooks_on('good', function() store_set('ok', '1') end)");

            // Just below the unload threshold, then one success: the counter must reset, so the
            // same amount of sporadic failures again must not unload the mod.
            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload - 1; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            runtime.EmitEvent("good", "");
            runtime.Tick(0);
            Assert.AreEqual("1", store.Get("m", "ok"));

            for (int i = 0; i < LuaModRuntime.MaxErrorsBeforeUnload - 1; i++)
            {
                runtime.EmitEvent("bad", "");
                runtime.Tick(0);
            }

            Assert.IsTrue(runtime.IsLoaded("m"));
        }

        private sealed class ScopedBindingsStub : IGameLuaRuntimeBindings, ICapabilityScopedLuaBindings
        {
            public void RegisterGameplayApis(Sandbox.LuaApiRegistry registry)
            {
                RegisterGameplayApis(registry, LuaCapabilities.All);
            }

            public void RegisterGameplayApis(
                Sandbox.LuaApiRegistry registry,
                LuaCapabilities capabilities)
            {
                if ((capabilities & LuaCapabilities.Read) != 0)
                {
                    registry.Register("stub_read", new Func<double>(() => 1d));
                }

                if ((capabilities & LuaCapabilities.WorldEdit) != 0)
                {
                    registry.Register("stub_edit", new Func<double>(() => 2d));
                }
            }
        }

        private sealed class UnscopedBindingsStub : IGameLuaRuntimeBindings
        {
            public void RegisterGameplayApis(Sandbox.LuaApiRegistry registry)
            {
                registry.Register("stub_edit", new Func<double>(() => 2d));
            }
        }

        /// <summary>
        /// Minimal stand-in for the shared world bindings singleton: <c>world_begin()</c> opens a
        /// transaction that buffers subsequent <c>world_cmd()</c> calls instead of publishing them,
        /// exactly like <c>CoreAiWorldLuaRuntimeBindings</c>. The runtime is expected to reset this
        /// shared transaction state around every guarded handler/timer so a transaction opened (and
        /// abandoned via an error) inside one handler cannot swallow another handler's commands.
        /// </summary>
        private sealed class TxLeakBindingsStub : IGameLuaRuntimeBindings, ILuaTransactionScope
        {
            public readonly List<string> Published = new();
            private readonly List<string> _buffer = new();
            private bool _txActive;

            public void RegisterGameplayApis(Sandbox.LuaApiRegistry registry)
            {
                registry.Register("world_begin", new Action(() =>
                {
                    _txActive = true;
                    _buffer.Clear();
                }));

                registry.Register("world_cmd", new Action<string>(payload =>
                {
                    if (_txActive)
                    {
                        _buffer.Add(payload ?? "");
                        return;
                    }

                    Published.Add(payload ?? "");
                }));
            }

            public void ResetTransactions()
            {
                _buffer.Clear();
                _txActive = false;
            }
        }

        [Test]
        public void LuaModRuntime_WorldTransactionLeftOpenByErroringHandler_DoesNotLeakIntoNextHandler()
        {
            TxLeakBindingsStub bindings = new();
            LuaModRuntime runtime = new(bindings);

            // First mod opens a world transaction inside its handler and then errors before any
            // commit/rollback. InvokeGuarded swallows the error, so without a per-call reset the
            // shared transaction stays open.
            runtime.LoadMod("leaker", @"
                hooks_on('go', function()
                    world_begin()
                    error('boom')
                end)");

            // Second mod emits a world command in the SAME tick. It must be published immediately,
            // not silently buffered into the leaked transaction.
            runtime.LoadMod("worker", @"
                hooks_on('go', function()
                    world_cmd('published')
                end)");

            runtime.EmitEvent("go", "");
            runtime.Tick(0);

            CollectionAssert.AreEqual(new[] { "published" }, bindings.Published,
                "A world transaction opened by an erroring handler must not leak into and swallow a " +
                "later handler's world command in the same tick.");
        }

        [Test]
        public void LuaModRuntime_DispatchBudget_RotatesStartSoEveryModMakesProgress()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);

            // More mods than a single tick's global budget can fully service. Each mod re-emits a
            // ping to itself-equivalent (via the looping source below) every tick, so every mod is
            // permanently saturated. If dispatch always started at index 0, the tail mods would
            // never be reached; the rotating start index must let each one make progress within a
            // bounded number of ticks.
            int perModCap = LuaModRuntime.DefaultMaxEventsDispatchedPerTick;
            int globalCap = LuaModRuntime.DefaultMaxEventsDispatchedPerTickGlobal;
            int modCount = globalCap / perModCap + 4;
            string[] ids = new string[modCount];
            for (int m = 0; m < modCount; m++)
            {
                string id = $"mod{m}";
                ids[m] = id;
                runtime.LoadMod(id, @"
                    hooks_on('ping', function()
                        local n = tonumber(store_get('seen')) or 0
                        store_set('seen', tostring(n + 1))
                    end)");
            }

            // Keep every mod's queue saturated each tick: emit enough pings to every mod to exceed
            // the per-mod cap, then drive enough ticks that a fair rotation must have reached the
            // tail at least once.
            int ticks = modCount * 2;
            for (int t = 0; t < ticks; t++)
            {
                for (int e = 0; e < perModCap + 1; e++)
                {
                    runtime.EmitEvent("ping", "x");
                }

                runtime.Tick(0);
            }

            foreach (string id in ids)
            {
                Assert.Greater(int.Parse(store.Get(id, "seen")), 0,
                    $"Under sustained saturation every mod must be serviced within a bounded number " +
                    $"of ticks; '{id}' never ran, so the dispatch start index is not rotating fairly.");
            }
        }

        [Test]
        public void LuaModRuntime_LoadMod_ReadCapability_DoesNotExposeWorldEditApi()
        {
            LuaModRuntime runtime = new(new ScopedBindingsStub());

            runtime.LoadMod("reader", "local v = stub_read()", LuaCapabilities.Read);
            Assert.IsTrue(runtime.IsLoaded("reader"));

            // World-edit binding must be physically absent for a read-only mod.
            Assert.Throws<ScriptRuntimeException>(() =>
                runtime.LoadMod("writer", "stub_edit()", LuaCapabilities.Read));
        }

        [Test]
        public void LuaModRuntime_LoadMod_RestrictedModWithUnscopedBindings_FailsClosed()
        {
            LuaModRuntime runtime = new(new UnscopedBindingsStub());

            // Unscoped bindings cannot be trimmed, so a restricted mod must get no game APIs.
            Assert.Throws<ScriptRuntimeException>(() =>
                runtime.LoadMod("m", "stub_edit()", LuaCapabilities.Read));

            // Full capabilities keep historical behavior.
            runtime.LoadMod("full", "stub_edit()", LuaCapabilities.All);
            Assert.IsTrue(runtime.IsLoaded("full"));
        }

        // ==================== Mod versioning (manage_mods versions/revert) ====================

        [Test]
        public void LuaModRuntime_Reload_WithChangedSource_AppendsRevision()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            Assert.AreEqual(1, runtime.ListModVersions("m").Count, "Initial load seeds one revision.");

            runtime.ReloadMod("m", "local x = 2");

            IReadOnlyList<LuaScriptRevision> history = runtime.ListModVersions("m");
            Assert.AreEqual(2, history.Count, "A changed reload appends a revision.");
            Assert.AreEqual("local x = 1", history[0].Source);
            Assert.AreEqual("local x = 2", history[history.Count - 1].Source);
        }

        [Test]
        public void LuaModRuntime_Reload_WithIdenticalSource_DoesNotGrowHistory()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            int before = runtime.ListModVersions("m").Count;

            runtime.ReloadMod("m", "local x = 1");

            Assert.AreEqual(before, runtime.ListModVersions("m").Count, "A no-op reload must not add a revision.");
        }

        [Test]
        public void LuaModRuntime_Revert_RestoresPriorSource()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 1");
            runtime.ReloadMod("m", "local x = 2");

            Assert.IsTrue(runtime.TryRevertMod("m", 0, out string restored));
            Assert.AreEqual("local x = 1", restored);
            Assert.IsTrue(runtime.TryGetModSource("m", out string live));
            Assert.AreEqual("local x = 1", live, "The live mod runs the reverted source.");
        }

        [Test]
        public void LuaModRuntime_Revert_UnknownRevision_ReturnsFalse()
        {
            MemoryLuaScriptVersionStore versions = new();
            LuaModRuntime runtime = new(versionStore: versions);
            runtime.LoadMod("m", "local x = 1");

            Assert.IsFalse(runtime.TryRevertMod("m", 99, out _));
            Assert.IsFalse(runtime.TryRevertMod("m", -1, out _));
        }

        [Test]
        public void LuaModRuntime_NoVersionStore_ListVersionsEmpty_LoadStillWorks()
        {
            LuaModRuntime runtime = new(); // NullLuaScriptVersionStore fallback

            runtime.LoadMod("m", "local x = 1");

            Assert.IsTrue(runtime.IsLoaded("m"));
            Assert.IsEmpty(runtime.ListModVersions("m"));
        }

        [Test]
        public void LuaModRuntime_Revert_AfterRetentionEviction_OriginalWorks_EvictedMiddleFails()
        {
            // F-11: history is bounded (original + last N intermediate + current); a revert must resolve
            // revisions by their stable index, not by array position, once eviction has removed entries.
            MemoryLuaScriptVersionStore versions = new(maxIntermediateRevisions: 2);
            LuaModRuntime runtime = new(versionStore: versions);

            runtime.LoadMod("m", "local x = 0");
            for (int i = 1; i <= 10; i++)
            {
                runtime.ReloadMod("m", $"local x = {i}");
            }

            IReadOnlyList<LuaScriptRevision> history = runtime.ListModVersions("m");
            Assert.LessOrEqual(history.Count, 4, "original + 2 intermediate + current at most.");

            Assert.IsTrue(runtime.TryRevertMod("m", 0, out string restored),
                "Revision 0 (original) must remain revertible after eviction.");
            Assert.AreEqual("local x = 0", restored);

            Assert.IsFalse(runtime.TryRevertMod("m", 1, out _),
                "Revision 1 was evicted by retention; revert must fail cleanly, not resolve the wrong revision.");
        }

        // ============== Runtime handler-error feedback (manage_mods diagnostics) ==============

        [Test]
        public void LuaModRuntime_TickHandlerThrows_RecordedInRecentHandlerErrors()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", "hooks_on('boom', function(n, p) error('kaboom') end)");

            runtime.EmitEvent("boom", "x");
            runtime.Tick(0); // must not throw

            IReadOnlyList<LuaModHandlerError> errors = runtime.GetRecentHandlerErrors("m");
            Assert.IsNotEmpty(errors, "A Tick-time handler throw must be visible to the agent.");
            Assert.AreEqual("m", errors[0].ModId);
            Assert.IsFalse(string.IsNullOrEmpty(errors[0].Error));
            Assert.GreaterOrEqual(errors[0].ConsecutiveCount, 1);
        }

        [Test]
        public void LuaModRuntime_ClearRecentHandlerErrors_RemovesModEntries()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m", "hooks_on('boom', function(n, p) error('kaboom') end)");
            runtime.EmitEvent("boom", "x");
            runtime.Tick(0);
            Assert.IsNotEmpty(runtime.GetRecentHandlerErrors("m"));

            int cleared = runtime.ClearRecentHandlerErrors("m");

            Assert.GreaterOrEqual(cleared, 1);
            Assert.IsEmpty(runtime.GetRecentHandlerErrors("m"));
        }

        // ==================== Per-tick event dispatch cap (P3) ====================

        [Test]
        public void LuaModRuntime_EmitMoreThanPerTickCap_TruncatesDispatchThenDrainsNextTick()
        {
            MemoryStore store = new();
            LuaModRuntime runtime = new(store: store);
            runtime.LoadMod("m",
                "hooks_on('e', function(n, p) store_set('c', tostring((tonumber(store_get('c')) or 0) + 1)) end)");

            int over = LuaModRuntime.DefaultMaxEventsDispatchedPerTick + 6;
            for (int i = 0; i < over; i++)
            {
                runtime.EmitEvent("e", i.ToString());
            }

            runtime.Tick(0);
            Assert.AreEqual(
                LuaModRuntime.DefaultMaxEventsDispatchedPerTick.ToString(),
                store.Get("m", "c"),
                "Per-tick dispatch must be capped at DefaultMaxEventsDispatchedPerTick.");

            runtime.Tick(0);
            Assert.AreEqual(over.ToString(), store.Get("m", "c"),
                "The remaining queued events drain on the following tick.");
        }
    }
}
