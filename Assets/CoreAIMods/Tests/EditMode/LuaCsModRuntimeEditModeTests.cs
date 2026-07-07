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
    /// would wire it — through <see cref="LuaCsModRuntimeFactory"/>. Mirrors the MoonSharp
    /// <c>LuaModRuntimeEditModeTests</c> fixtures/fakes on the managed (Lua-CSharp) runtime instead.
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

        /// <summary>In-memory <see cref="ILuaModStore"/> (same shape as the MoonSharp fixture's fake).</summary>
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

            // events_emit runs at load and raises ModEventEmitted synchronously (matches MoonSharp).
            stack.Runtime.LoadMod("a", "events_emit('quest_event', 'payload')");

            Assert.AreEqual("a", gotMod);
            Assert.AreEqual("quest_event", gotName);
            Assert.AreEqual("payload", gotPayload);
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
    }
}
