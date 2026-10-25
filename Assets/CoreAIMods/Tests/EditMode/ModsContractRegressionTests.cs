using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Logging;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.Luau;
using CoreAI.Messaging;
using CoreAI.Mods.WorldPackages;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Cysharp.Threading.Tasks;
using Lua;
using Lua.Runtime;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    [TestFixture]
    public sealed class ModsContractRegressionTests
    {
        private SynchronizationContext _context;

        [SetUp]
        public void SetUp()
        {
            _context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown() => SynchronizationContext.SetSynchronizationContext(_context);

        private sealed class Store : ILuaModStore
        {
            private readonly Dictionary<(string, string), string> _values = new();
            public string Get(string modId, string key) => _values.TryGetValue((modId, key), out string value) ? value : "";
            public void Set(string modId, string key, string value)
            {
                if (value == null) { _values.Remove((modId, key)); }
                else { _values[(modId, key)] = value; }
            }
            public void Clear(string modId)
            {
                foreach ((string, string) key in _values.Keys.Where(key => key.Item1 == modId).ToArray())
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class Sink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();
            public void Publish(ApplyAiGameCommand command) => Commands.Add(command);
        }

        private sealed class WritePolicy : ILuaVersioningWritePolicy
        {
            public bool CanWrite(string ownerModId, ActorContext? actorContext, string key, bool luaHistory)
                => ownerModId == "writer" && key == "owned" && !luaHistory;
        }

        [Test]
        public void MOD001_ReadCannotMutateSharedVersions_ExplicitWriterIsKeyScoped()
        {
            MemoryDataOverlayVersionStore data = new();
            data.RecordSuccessfulApply("other", "{}");
            data.RecordSuccessfulApply("other", "{\"changed\":true}");
            MemoryLuaScriptVersionStore lua = new();
            lua.RecordSuccessfulExecution("other", "return 1");
            lua.RecordSuccessfulExecution("other", "return 2");
            Sink sink = new();
            Store store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                DataOverlayVersions = data, LuaScriptVersions = lua, CommandSink = sink,
                ModStore = store, VersioningWritePolicy = new WritePolicy()
            });
            stack.Runtime.LoadMod("reader", @"
                local names = {'coreai_data_apply','coreai_data_seed','coreai_data_seed_overwrite',
                    'coreai_data_reset','coreai_data_reset_revision','coreai_data_reset_all',
                    'coreai_lua_reset','coreai_lua_reset_revision','coreai_lua_reset_all'}
                for _, name in ipairs(names) do assert(_G[name] == nil, name) end
                store_set('read', coreai_data_get('other'))", LuaCapabilities.Read, persistToStore: false);
            Assert.AreEqual("{\"changed\":true}", store.Get("reader", "read"));
            stack.Runtime.LoadMod("writer", @"
                assert(coreai_data_reset_all == nil and coreai_lua_reset_all == nil)
                assert(coreai_data_seed_overwrite == nil)
                assert(not pcall(coreai_data_apply, 'other', '{}'))
                assert(not pcall(coreai_lua_reset, 'other'))
                coreai_data_apply('owned', '{}')", LuaCapabilities.Read | LuaCapabilities.WorldEdit,
                persistToStore: false);
            Assert.IsTrue(data.TryGetSnapshot("other", out DataOverlayVersionRecord snapshot));
            Assert.AreEqual("{\"changed\":true}", snapshot.CurrentPayload);
            Assert.AreEqual(2, snapshot.History.Count);
            Assert.IsTrue(lua.TryGetSnapshot("other", out LuaScriptVersionRecord luaSnapshot));
            Assert.AreEqual("return 2", luaSnapshot.CurrentLua);
            Assert.AreEqual(2, luaSnapshot.History.Count);
            Assert.IsTrue(data.TryGetCurrentPayload("owned", out string owned));
            Assert.AreEqual("{}", owned);
            Assert.AreEqual(1, sink.Commands.Count);
        }

        [Test]
        public void MOD001_WorldEditWithoutAKeyPolicyCannotMutateAndTrustedHostRegistrationRemainsAvailable()
        {
            MemoryDataOverlayVersionStore data = new();
            data.RecordSuccessfulApply("key", "original");
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                DataOverlayVersions = data
            });
            try
            {
                stack.Runtime.LoadMod("ungranted", "assert(coreai_data_apply==nil and coreai_data_reset==nil)",
                    LuaCapabilities.Read | LuaCapabilities.WorldEdit, persistToStore: false);
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }

            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            IScriptFunctionRegistry registry = engine.CreateFunctionRegistry();
            LuaCsVersioningRuntimeBindings trusted = new(null, data, null);
            trusted.RegisterGameplayApis(registry);
            registry.ApplyTo(state);
            engine.RunChunk(state, @"
                coreai_data_seed_overwrite('key','{""phase"":""baseline""}')
                coreai_data_apply('key','{""phase"":""changed""}')
                coreai_data_reset_all()
                coreai_lua_reset_all()");
            Assert.IsTrue(data.TryGetCurrentPayload("key", out string restored));
            Assert.AreEqual("{\"phase\":\"baseline\"}", restored);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MOD004_ReentrantRetirementStopsOldTimerAndHandlerBatches(bool shutdown)
        {
            Store store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions { ModStore = store });
            stack.Runtime.LoadMod("old", @"
                hooks_every(0, function() error('retire now') end)
                hooks_every(0, function() store_set('stale_timer', 'bad') end)
                hooks_on('queued', function() store_set('stale_event', 'bad') end)", persistToStore: false);
            bool retired = false;
            stack.Runtime.ModHandlerErrored += (id, message, count) =>
            {
                if (retired) { return; }
                retired = true;
                if (shutdown) { stack.Runtime.ShutdownWithoutPersistence(); }
                else { stack.Runtime.ReloadMod("old", "hooks_every(0, function() store_set('fresh','yes') end)"); }
            };
            stack.Runtime.EmitEvent("queued", "");
            stack.Runtime.Tick(0);
            Assert.IsTrue(retired);
            Assert.AreEqual("", store.Get("old", "stale_timer"));
            Assert.AreEqual("", store.Get("old", "stale_event"));
            stack.Runtime.Tick(0);
            Assert.AreEqual(shutdown ? "" : "yes", store.Get("old", "fresh"));
        }

        [Test]
        public void MOD004_ReentrantReloadStopsTheRestOfAnAlreadyCapturedHandlerBatch()
        {
            Store store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions { ModStore = store });
            stack.Runtime.LoadMod("old", @"
                hooks_on('work', function() error('retire') end)
                hooks_on('work', function() store_set('stale', 'bad') end)", persistToStore: false);
            stack.Runtime.ModHandlerErrored += (id, message, count) => stack.Runtime.ReloadMod("old", "return 0");
            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.EmitEvent("work", "");
            stack.Runtime.Tick(0);
            Assert.AreEqual("", store.Get("old", "stale"));
        }

        [Test]
        public void MOD007_LogKeysTextAndCallerMutationsRemainBounded()
        {
            LuaLogService log = new(2, 3, maximumRetainedModIds: 2,
                maximumMessageCharacters: 32, maximumRetainedCharacters: 200);
            for (int i = 0; i < 50; i++)
            {
                LuaLogEntry entry = new() { ModId = "mod-" + i, Message = new string('x', 1000) };
                log.Append(entry);
                entry.Message = new string('y', 10000);
                Assert.LessOrEqual(log.RetainedModCount, 2);
                Assert.LessOrEqual(log.RetainedCharacters, 200);
            }

            Assert.AreEqual(0, log.Query(new LuaLogQuery { ModId = "mod-0" }).Count);
            IReadOnlyList<LuaLogEntry> tail = log.Query(new LuaLogQuery());
            Assert.IsNotEmpty(tail);
            Assert.IsTrue(tail.All(entry => entry.Message.Length <= 32));
            Assert.IsTrue(tail.All(entry => entry.Message.EndsWith("...[truncated]", StringComparison.Ordinal)));
            long sequence = tail[tail.Count - 1].Sequence;
            tail[tail.Count - 1].Message = "caller mutation";
            Assert.AreNotEqual("caller mutation", log.Query(new LuaLogQuery()).Last().Message);
            log.Clear();
            Assert.AreEqual(0, log.RetainedModCount);
            Assert.AreEqual(0, log.RetainedCharacters);
            LuaLogEntry next = new() { ModId = "next", Message = "ok" };
            log.Append(next);
            Assert.Greater(next.Sequence, sequence);
        }

        [Test]
        public void MOD007_MessageClampingDoesNotMergeLongModIdentities()
        {
            LuaLogService log = new(2, 4, maximumRetainedModIds: 2,
                maximumMessageCharacters: 16, maximumRetainedCharacters: 1024);
            string first = new string('a', 80) + ":first";
            string second = new string('a', 80) + ":second";
            log.Append(new LuaLogEntry { ModId = first, Message = "first" });
            log.Append(new LuaLogEntry { ModId = second, Message = "second" });
            Assert.AreEqual(2, log.RetainedModCount);
            Assert.AreEqual("first", log.Query(new LuaLogQuery { ModId = first }).Single().Message);
            Assert.AreEqual("second", log.Query(new LuaLogQuery { ModId = second }).Single().Message);
            log.Clear(first);
            Assert.AreEqual(0, log.Query(new LuaLogQuery { ModId = first }).Count);
            Assert.AreEqual("second", log.Query(new LuaLogQuery { ModId = second }).Single().Message);
            log.Append(new LuaLogEntry { ModId = new string('b', 2048), Message = "oversized identity" });
            Assert.LessOrEqual(log.RetainedCharacters, 1024);
        }

        [Test]
        public void MOD007_LogKeyEvictionRetainsRecentlyAppendedHistoryAndGlobalTail()
        {
            LuaLogService log = new(2, 5, maximumRetainedModIds: 2);
            log.Append(new LuaLogEntry { ModId = "a", Message = "a1" });
            log.Append(new LuaLogEntry { ModId = "b", Message = "b1" });
            log.Append(new LuaLogEntry { ModId = "a", Message = "a2" });
            log.Append(new LuaLogEntry { ModId = "c", Message = "c1" });
            Assert.AreEqual(0, log.Query(new LuaLogQuery { ModId = "b" }).Count);
            CollectionAssert.AreEqual(new[] { "a1", "a2" }, log.Query(new LuaLogQuery { ModId = "a" }).Select(entry => entry.Message));
            Assert.AreEqual(4, log.Query(new LuaLogQuery()).Count);
            log.Clear("a");
            Assert.AreEqual(1, log.RetainedModCount);
            Assert.AreEqual(4, log.Query(new LuaLogQuery()).Count);
        }

        [Test]
        public async Task MOD007_ConcurrentAppendQueryAndEvictionPreserveBoundsAndSequenceOrder()
        {
            LuaLogService log = new(2, 20, maximumRetainedModIds: 4,
                maximumMessageCharacters: 32, maximumRetainedCharacters: 512);
            List<Task> producers = new();
            for (int producer = 0; producer < 4; producer++)
            {
                int owner = producer;
                producers.Add(Task.Run(() =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        log.Append(new LuaLogEntry { ModId = owner + ":" + i, Message = "message" });
                        IReadOnlyList<LuaLogEntry> tail = log.Query(new LuaLogQuery());
                        for (int j = 1; j < tail.Count; j++) { Assert.Greater(tail[j].Sequence, tail[j - 1].Sequence); }
                    }
                }));
            }

            await Bounded(Task.WhenAll(producers));
            Assert.LessOrEqual(log.RetainedModCount, 4);
            Assert.LessOrEqual(log.RetainedCharacters, 512);
        }

        [TestCase(64)]
        [TestCase(240)]
        public void MOD011_ContinuousTimersCannotStarveLaterTimersOrQueuedEvents(int reservation)
        {
            List<string> expected = null;
            for (int run = 0; run < 2; run++)
            {
                List<string> invoked = new();
                LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
                {
                    ReservedEventInvocationsPerTick = reservation,
                    AdditionalGameplayBindings = (registry, caps) => registry.Register("mark", new Action<string>(invoked.Add))
                });
                for (int i = 0; i < 20; i++)
                {
                    string source = string.Concat(Enumerable.Repeat("hooks_every(0, function() mark('timer-" + i + "') end)\n", 16));
                    stack.Runtime.LoadMod("timer-" + i, source, persistToStore: false);
                }

                stack.Runtime.LoadMod("last", "hooks_every(0, function() mark('last-timer') end); hooks_on('work', function() mark('event') end)", persistToStore: false);
                stack.Runtime.EmitEvent("work", "");
                for (int tick = 0; tick < 22; tick++)
                {
                    int before = invoked.Count;
                    stack.Runtime.Tick(0);
                    Assert.LessOrEqual(invoked.Count - before, LuaCsModRuntime.DefaultMaxEventsDispatchedPerTickGlobal);
                }

                CollectionAssert.Contains(invoked, "event");
                CollectionAssert.Contains(invoked, "last-timer");
                for (int i = 0; i < 20; i++) { CollectionAssert.Contains(invoked, "timer-" + i); }
                if (expected == null) { expected = invoked; }
                else { CollectionAssert.AreEqual(expected, invoked); }
                stack.Runtime.ShutdownWithoutPersistence();
            }
        }

        [Test]
        public void MOD011_ContinuousFullEventBatchesAndTimersBothReceiveBoundedService()
        {
            List<string> invoked = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                ReservedEventInvocationsPerTick = 64,
                AdditionalGameplayBindings = (registry, caps) => registry.Register("mark", new Action<string>(invoked.Add))
            });
            try
            {
                for (int mod = 0; mod < 16; mod++)
                {
                    string timers = string.Concat(Enumerable.Repeat(
                        "hooks_every(0, function() mark('timer-" + mod + "') end)\n", 16));
                    string handlers = string.Concat(Enumerable.Repeat(
                        "hooks_on('work', function() mark('event-" + mod + "') end)\n", 64));
                    stack.Runtime.LoadMod("busy-" + mod, timers + handlers, persistToStore: false);
                }

                for (int tick = 0; tick < 16; tick++)
                {
                    stack.Runtime.EmitEvent("work", "");
                    int before = invoked.Count;
                    stack.Runtime.Tick(0);
                    Assert.LessOrEqual(invoked.Count - before, LuaCsModRuntime.DefaultMaxEventsDispatchedPerTickGlobal);
                }

                for (int mod = 0; mod < 16; mod++)
                {
                    CollectionAssert.Contains(invoked, "timer-" + mod);
                    CollectionAssert.Contains(invoked, "event-" + mod);
                }
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        private static object[] RunLuau(LuaCsScriptEngine engine, IScriptState state, string source,
            IScriptExecutionGuard guard = null, CancellationToken token = default)
        {
            DownlevelResult result = LuauDownleveler.Process(source, "contract",
                new LuauDownlevelOptions(trustedIteratorResolverName: engine.TrustedIteratorResolverName));
            Assert.IsFalse(result.HasErrors, string.Join(";", result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            return engine.RunChunk(state, result.LuaSource, guard, token).Select(engine.Marshaller.ToHostValue).ToArray();
        }

        private const string ProtectedIteratorProgram = @"
            local calls=0
            local values=setmetatable({}, {__metatable='locked', __iter=function(self)
                calls=calls+1; return next,{answer=23},nil end})
            __coreai_luau_iter=function() error('forged resolver') end
            local sum=0
            for key,value in values do sum+=value end
            assert(sum==23 and calls==1 and getmetatable(values)=='locked')";

        [TestCase(false)]
        [TestCase(true)]
        public void LUAU007_PersistentLoadAndReloadUseTheTrustedResolverThroughBothStartupPaths(bool withRbx)
        {
            using LuaCsRbxApiBindings rbx = withRbx ? new LuaCsRbxApiBindings() : null;
            Store store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbx, ModStore = store,
                AdditionalGameplayBindings = (registry, caps) => registry.Register(
                    LuaCsSecureEnvironment.TrustedIteratorResolverName,
                    new Action(() => throw new InvalidOperationException("A registry override must not replace the reserved resolver.")))
            });
            try
            {
                stack.Runtime.LoadMod("iterator", ProtectedIteratorProgram + "; store_set('load', tostring(sum))", persistToStore: false);
                Assert.AreEqual("23", store.Get("iterator", "load"));
                stack.Runtime.ReloadMod("iterator", ProtectedIteratorProgram + "; store_set('reload', tostring(sum))");
                Assert.AreEqual("23", store.Get("iterator", "reload"));
            }
            finally { stack.Runtime.ShutdownWithoutPersistence(); }
        }

        [Test]
        public async Task LUAU007_OneOffToolUsesTheTrustedResolverBeforeItsSourceGate()
        {
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions());
            Task<LuaTool.LuaResult> work = stack.ToolExecutor.ExecuteAsync(
                ProtectedIteratorProgram + "; return sum", CancellationToken.None);
            await Bounded(work);
            Assert.IsTrue(work.Result.Success, work.Result.Error);
            StringAssert.Contains("23", work.Result.Output);
        }

        [Test]
        public void LUAU007_AiEnvelopeUsesTheTrustedResolverAndRecordsOriginalSource()
        {
            Sink sink = new();
            MemoryLuaScriptVersionStore versions = new();
            LuaCsAiEnvelopeProcessor processor = new(new LuaCsSecureEnvironment(),
                new CoreDefaultLuaCsRuntimeBindings(), sink, () => null,
                new NullLuaExecutionObserver(), versions);
            string source = ProtectedIteratorProgram + "; return sum";
            processor.Process(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.Envelope,
                SourceRoleId = "iterator-contract", LuaScriptVersionKey = "original",
                JsonPayload = "```lua\n" + source + "\n```"
            });
            Assert.AreEqual(1, sink.Commands.Count);
            Assert.AreEqual(AiGameCommandTypeIds.LuaExecutionSucceeded, sink.Commands[0].CommandTypeId);
            StringAssert.Contains("23", sink.Commands[0].JsonPayload);
            Assert.IsTrue(versions.TryGetSnapshot("original", out LuaScriptVersionRecord snapshot));
            Assert.AreEqual(source.Trim(), snapshot.CurrentLua);
        }

        [Test]
        public void LUAU007_ProtectedIteratorIsInvokedOnceAndCapturedBeforeScriptReassignment()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] result = RunLuau(engine, state, @"
                local calls = 0
                local values = setmetatable({}, {__metatable='locked', __iter=function(self)
                    calls = calls + 1
                    return function(_, key) if key == nil then return 1, 17 end end, self, nil
                end})
                __coreai_luau_iter = function() error('forged resolver') end
                local sum = 0
                for key, value in values do sum = sum + value end
                return sum, calls, getmetatable(values), debug");
            CollectionAssert.AreEqual(new object[] { 17d, 1d, "locked", null }, result);
            engine.RunChunk(state, "next=function() error('forged next') end; __coreai_luau_iter=nil");
            object[] raw = RunLuau(engine, state, "local sum=0; for _, value in {3,4} do sum=sum+value end; return sum");
            Assert.AreEqual(7d, raw[0]);
        }

        [Test]
        public void LUAU007_IteratorTriplesAndMultiArgumentsAreNotReinterpreted()
        {
            LuaCsScriptEngine engine = new();
            object[] result = RunLuau(engine, engine.CreateState(), @"
                local calls = 0
                local values = setmetatable({a=5}, {__iter=function() error('must not run') end})
                local sum = 0
                for key, value in next, values, nil do sum=sum+value end
                for key, value in pairs({6,7}) do sum=sum+value end
                for key, value in function(_, key) calls=calls+1; if key==nil then return 1,8 end end do sum=sum+value end
                return sum,calls");
            CollectionAssert.AreEqual(new object[] { 26d, 2d }, result);
        }

        private sealed class IteratorData : ILuaUserData
        {
            public LuaTable Metatable { get; set; }
            public Span<LuaValue> UserValues => Span<LuaValue>.Empty;
        }

        [Test]
        public void LUAU007_ProtectedUserdataUsesItsActualIteratorWithoutExposingMetatable()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] table = engine.RunChunk(state, @"return {__metatable='private', __iter=function(self)
                return function(_, key) if key == nil then return 1, 9 end end, self, nil end}");
            IteratorData data = new() { Metatable = LuaCsValueMarshaller.Unbox(table[0]).Read<LuaTable>() };
            LuaCsScriptState.Unwrap(state).Environment["probe"] = new LuaValue(data);
            object[] result = RunLuau(engine, state,
                "local sum=0; for key,value in probe do sum=sum+value end; return sum,getmetatable(probe)");
            CollectionAssert.AreEqual(new object[] { 9d, "private" }, result);
        }

        [Test]
        [Timeout(5000)]
        public void LUAU007_IteratorBodyCannotEscapeTheEnclosingFiniteStepBudget()
        {
            LuaCsScriptEngine engine = new();
            Exception failure = Assert.Catch(() => RunLuau(engine, engine.CreateState(), @"
                for key,value in setmetatable({}, {__metatable='private', __iter=function()
                    local sum=0; for i=1,1000000 do sum=sum+i end; return next,{},nil end}) do end",
                engine.CreateGuard(new ExecutionBudget(1000, 1000))));
            StringAssert.Contains("EXCEEDED_HARD_LIMIT_STEPS", failure.Message);
        }

        [Test]
        [Timeout(5000)]
        public void LUAU007_IteratorCancellationPropagatesAndRestoresTheGuard()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            using CancellationTokenSource source = new();
            int calls = 0;
            IScriptFunctionRegistry registry = engine.CreateFunctionRegistry();
            registry.Register("stop_iteration", new Action(() => { calls++; source.Cancel(); }));
            registry.ApplyTo(state);
            Assert.Catch<OperationCanceledException>(() => RunLuau(engine, state, @"
                for key,value in setmetatable({}, {__metatable='private', __iter=function()
                    stop_iteration(); local sum=0; for i=1,100000 do sum=sum+i end
                    return next,{},nil end}) do end", engine.CreateGuard(new ExecutionBudget(1000, 1000000)), source.Token));
            Assert.AreEqual(1, calls);
            Assert.AreEqual(2d, engine.Marshaller.ToHostValue(engine.RunChunk(state, "return 2")[0]));
        }

        [Test]
        public void LUAU007_IteratorErrorsPropagateOnceAndDoNotExposeProtectedMetatables()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] result = RunLuau(engine, state, @"
                local calls=0
                local value=setmetatable({}, {__metatable='locked', __iter=function() calls=calls+1; error('iterator failure') end})
                local ok,message=pcall(function() for key,item in value do end end)
                return ok,string.find(message,'iterator failure',1,true) ~= nil,calls,getmetatable(value)");
            CollectionAssert.AreEqual(new object[] { false, true, 1d, "locked" }, result);
        }

        [Test]
        public void LUAU007_IteratorMetamethodCannotYieldAcrossTheCountedHostBoundary()
        {
            LuaCsScriptEngine engine = new();
            IScriptState state = engine.CreateState();
            object[] result = RunLuau(engine, state, @"
                local co=coroutine.create(function()
                    for key,value in setmetatable({}, {__iter=function() coroutine.yield('escaped') end}) do end
                end)
                local ok,message=coroutine.resume(co)
                return ok,string.find(message,'yield',1,true) ~= nil");
            CollectionAssert.AreEqual(new object[] { false, true }, result);
            Assert.AreEqual(5d, engine.Marshaller.ToHostValue(engine.RunChunk(state, "return 5")[0]));
        }

        private sealed class HttpPolicy : IRbxHttpRequestPolicy
        {
            public bool IsEnabled => true;
            public bool TryAuthorize(string actorId, RbxHttpRequest requested, out RbxHttpRequest approved, out string refusalReason)
            {
                approved = requested; refusalReason = null; return true;
            }
        }

        private sealed class Resolver : IRbxHttpDestinationResolver
        {
            public readonly TaskCompletionSource<IReadOnlyList<IPAddress>> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly List<CancellationToken> Tokens = new();
            public bool CompleteImmediately;
            public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken token)
            {
                Tokens.Add(token);
                return CompleteImmediately ? Task.FromResult<IReadOnlyList<IPAddress>>(new[] { IPAddress.Parse("93.184.216.34") }) : Pending.Task;
            }
        }

        private sealed class Transport : IRbxHttpTransport
        {
            public int Calls;
            public CancellationToken LastToken;
            public TaskCompletionSource<RbxHttpResponse> Pending;
            public Task<RbxHttpResponse> SendAsync(RbxValidatedHttpDestination destination, CancellationToken token)
            {
                Calls++;
                LastToken = token;
                return Pending?.Task ?? Task.FromResult(new RbxHttpResponse(200, "OK", "body"));
            }
        }

        private static async Task Bounded(Task work)
        {
            Assert.AreSame(work, await Task.WhenAny(work, Task.Delay(2000)), "Controlled work failed to settle within its finite timeout.");
            await work;
        }

        [TestCase("unload")]
        [TestCase("reload")]
        [TestCase("dispose")]
        public async Task MOD006_RetirementCancelsPendingDnsAndDoesNotWaitForTheResolver(string retirement)
        {
            using LuaCsRbxApiBindings rbx = new();
            Assert.IsInstanceOf<ILuaCsRuntimeLifetimeHost>(rbx, "The Rbx owner must install the lifetime bridge.");
            Resolver resolver = new();
            Transport transport = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbx, RbxHttpPolicy = new HttpPolicy(), RbxHttpResolver = resolver, RbxHttpTransport = transport
            });
            stack.Runtime.LoadMod("http", "task.spawn(function() game:GetService('HttpService'):GetAsync('https://api.example.test/') end)", persistToStore: false);
            Assert.AreEqual(1, resolver.Tokens.Count);
            Task work = stack.GameplayBindings.HttpForTesting.CurrentWorkForTesting();
            if (retirement == "unload") { stack.Runtime.UnloadMod("http"); }
            else if (retirement == "reload") { stack.Runtime.ReloadMod("http", "return 0"); }
            else { rbx.Dispose(); }
            Assert.IsTrue(resolver.Tokens[0].IsCancellationRequested);
            await Bounded(work);
            Assert.IsFalse(resolver.Pending.Task.IsCompleted, "Cancellation must not await the host's parked resolver.");
            Assert.AreEqual(0, stack.GameplayBindings.HttpForTesting.OutstandingRequestsForTesting);
            resolver.Pending.TrySetResult(new[] { IPAddress.Parse("93.184.216.34") });
            Assert.AreEqual(0, transport.Calls);
        }

        [Test]
        public void MOD006_OutstandingRequestsRemainBoundedAcrossRateWindows()
        {
            using LuaCsRbxApiBindings rbx = new();
            Resolver resolver = new();
            double clock = 0;
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbx, RbxHttpPolicy = new HttpPolicy(), RbxHttpResolver = resolver,
                RbxHttpTransport = new Transport(), RbxHttpMaximumOutstandingRequests = 2,
                RbxMonotonicClock = () => clock
            });
            stack.Runtime.LoadMod("http", @"hooks_on('request',function()
                task.spawn(function() pcall(function() game:GetService('HttpService'):GetAsync('https://api.example.test/') end) end)
            end)", persistToStore: false);
            for (int i = 0; i < 10; i++)
            {
                clock += 61;
                stack.Runtime.EmitEvent("request", "");
                stack.Runtime.Tick(0);
            }

            Assert.AreEqual(2, resolver.Tokens.Count);
            Assert.AreEqual(2, stack.GameplayBindings.HttpForTesting.OutstandingRequestsForTesting);
            stack.Runtime.ShutdownWithoutPersistence();
            Assert.IsTrue(resolver.Tokens.All(token => token.IsCancellationRequested));
        }

        [Test]
        public async Task MOD006_RetirementCancelsPendingTransportAndDiscardsItsLateResult()
        {
            using LuaCsRbxApiBindings rbx = new();
            Resolver resolver = new() { CompleteImmediately = true };
            Transport transport = new() { Pending = new TaskCompletionSource<RbxHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously) };
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                RbxApi = rbx, RbxHttpPolicy = new HttpPolicy(), RbxHttpResolver = resolver, RbxHttpTransport = transport
            });
            stack.Runtime.LoadMod("http", "task.spawn(function() game:GetService('HttpService'):GetAsync('https://api.example.test/') end)", persistToStore: false);
            Assert.AreEqual(1, transport.Calls);
            Task work = stack.GameplayBindings.HttpForTesting.CurrentWorkForTesting();
            stack.Runtime.UnloadMod("http");
            Assert.IsTrue(transport.LastToken.IsCancellationRequested);
            await Bounded(work);
            Assert.IsFalse(transport.Pending.Task.IsCompleted);
            transport.Pending.TrySetResult(new RbxHttpResponse(200, "OK", "late"));
            Assert.AreEqual(0, stack.GameplayBindings.HttpForTesting.OutstandingRequestsForTesting);
        }

        private sealed class FileSystem : IRbxWorldPackageFileSystem
        {
            private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
            private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
            public readonly TaskCompletionSource<bool> WriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> ReleaseWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool ParkFirstWrite;
            private bool _parked;
            public bool DirectoryExists(string path) => _directories.Contains(path);
            public void CreateDirectory(string path) => _directories.Add(path);
            public bool FileExists(string path) => _files.ContainsKey(path);
            public long GetFileLength(string path) => _files[path].Length;
            public async UniTask WriteAllBytesCreateNewAsync(string path, byte[] bytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                _files.Add(path, (byte[])bytes.Clone());
                if (ParkFirstWrite && !_parked)
                {
                    _parked = true;
                    WriteStarted.TrySetResult(true);
                    await ReleaseWrite.Task;
                    token.ThrowIfCancellationRequested();
                }
            }
            public UniTask<byte[]> ReadAllBytesAsync(string path, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return UniTask.FromResult((byte[])_files[path].Clone());
            }
            public void MoveCreateNew(string sourcePath, string destinationPath)
            {
                _files.Add(destinationPath, _files[sourcePath]);
                _files.Remove(sourcePath);
            }
            public void DeleteFile(string path) => _files.Remove(path);
            public IReadOnlyList<string> GetFiles(string directory, string extension) => _files.Keys
                .Where(path => Path.GetDirectoryName(path) == directory && path.EndsWith(extension, StringComparison.Ordinal))
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        private static RbxWorldPackagePayload Payload(string worldId)
        {
            using LuaCsRbxApiBindings rbx = new(registry: new InstanceRegistry(worldId: worldId));
            return RbxWorldPackageSerializer.Capture(new RbxWorldPackageCaptureContext(
                rbx.Registry, rbx.Game, rbx.PartSink, new RbxWorldSettings { WorldId = worldId }));
        }

        [Test]
        public async Task MOD008_StartupNamespacesIsolateSanitizerAndCaseCollisionsAfterReopen()
        {
            FileSystem fileSystem = new();
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-in-memory-startup-namespaces");
            string[] namespaces = { "a/b", "a_b_3a8e75c1", "Case", "case" };
            for (int index = 0; index < namespaces.Length; index++)
            {
                FileRbxWorldPackageStore store = new(root, fileSystem: fileSystem,
                    startupNamespace: namespaces[index], persistenceSyncAsync: token => UniTask.FromResult(true));
                RbxWorldPackageWriteResult written = await store.SelectStartupAsync(
                    Payload("world-" + index), "manual", namespaces[index]);
                Assert.IsTrue(written.Success, written.Error);
            }

            for (int index = 0; index < namespaces.Length; index++)
            {
                FileRbxWorldPackageStore reopened = new(root, fileSystem: fileSystem,
                    startupNamespace: namespaces[index], persistenceSyncAsync: token => UniTask.FromResult(true));
                RbxWorldStartupSelection selected = await reopened.ReadStartupAsync();
                Assert.AreEqual(RbxWorldStartupSelectionKind.Package, selected.Kind, selected.Error);
                Assert.AreEqual("world-" + index, selected.Payload.Settings.WorldId);
            }
        }

        [Test]
        public async Task MOD005_SharedRootSerializesQuotaAndConstructorCleanupIncludingCanceledWaiters()
        {
            int rootsBefore = WorldPackageTransactions.RetainedRootCountForTesting;
            FileSystem fileSystem = new() { ParkFirstWrite = true };
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-in-memory-transactions");
            FileRbxWorldPackageStore first = new(root, fileSystem: fileSystem, maximumManualSlots: 1,
                startupNamespace: "a", persistenceSyncAsync: token => UniTask.FromResult(true));
            Task<RbxWorldPackageWriteResult> firstWrite = first.CreateManualAsync("first", Payload("first")).AsTask();
            await Bounded(fileSystem.WriteStarted.Task);
            FileRbxWorldPackageStore second = new(root + Path.DirectorySeparatorChar, fileSystem: fileSystem,
                maximumManualSlots: 1, startupNamespace: "b", persistenceSyncAsync: token => UniTask.FromResult(true));
            Task<RbxWorldPackageWriteResult> secondWrite = second.CreateManualAsync("second", Payload("second")).AsTask();
            try
            {
                Assert.IsFalse(secondWrite.IsCompleted, "A second instance must wait before shared quota admission.");
                using CancellationTokenSource canceled = new();
                Task<RbxWorldPackageWriteResult> waiter = second.CreateManualAsync("canceled", Payload("canceled"), canceled.Token).AsTask();
                canceled.Cancel();
                Assert.CatchAsync<OperationCanceledException>(async () => await waiter);
                Assert.IsFalse(secondWrite.IsCompleted, "Canceling another waiter cannot release the active transaction.");
            }
            finally { fileSystem.ReleaseWrite.TrySetResult(true); }
            await Bounded(Task.WhenAll(firstWrite, secondWrite));
            Assert.IsTrue(firstWrite.Result.Success, firstWrite.Result.Error);
            Assert.IsFalse(secondWrite.Result.Success);
            Assert.AreEqual(rootsBefore, WorldPackageTransactions.RetainedRootCountForTesting);
            CollectionAssert.AreEqual(new[] { "first" }, first.ListManualSlots());
            Assert.AreEqual(0, fileSystem.GetFiles(Path.Combine(root, "Manual"), ".tmp").Count);
            FileRbxWorldPackageStore reopened = new(root, fileSystem: fileSystem,
                maximumManualSlots: 1, persistenceSyncAsync: token => UniTask.FromResult(true));
            Assert.IsFalse((await reopened.CreateManualAsync("third", Payload("third"))).Success);
        }

        [Test]
        public async Task MOD005_FailedAutosaveConfirmationRollsBackBeforeAnotherStoreRotatesTheRing()
        {
            FileSystem fileSystem = new();
            string root = Path.Combine(Path.GetTempPath(), "CoreAI-in-memory-autosaves");
            FileRbxWorldPackageStore initial = new(root, autoBackupCapacity: 2, fileSystem: fileSystem,
                persistenceSyncAsync: token => UniTask.FromResult(true));
            Assert.IsTrue((await initial.CreateAutoAsync("initial", Payload("initial"))).Success);
            TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FileRbxWorldPackageStore failing = new(root, autoBackupCapacity: 2, fileSystem: fileSystem,
                persistenceSyncAsync: async token => { entered.TrySetResult(true); return await release.Task; });
            FileRbxWorldPackageStore succeeding = new(root, autoBackupCapacity: 2, fileSystem: fileSystem,
                persistenceSyncAsync: token => UniTask.FromResult(true));
            Task<RbxWorldPackageWriteResult> failed = failing.CreateAutoAsync("failed", Payload("failed")).AsTask();
            await Bounded(entered.Task);
            Task<RbxWorldPackageWriteResult> success = succeeding.CreateAutoAsync("success", Payload("success")).AsTask();
            try { Assert.IsFalse(success.IsCompleted); }
            finally { release.TrySetResult(false); }
            await Bounded(Task.WhenAll(failed, success));
            Assert.IsFalse(failed.Result.Success);
            Assert.IsTrue(success.Result.Success, success.Result.Error);
            FileRbxWorldPackageStore reopened = new(root, autoBackupCapacity: 2, fileSystem: fileSystem,
                persistenceSyncAsync: token => UniTask.FromResult(true));
            List<string> worlds = new();
            foreach (string name in reopened.ListAutoFiles()) { worlds.Add((await reopened.LoadAutoAsync(name)).Settings.WorldId); }
            CollectionAssert.AreEquivalent(new[] { "initial", "success" }, worlds);
        }
    }
}
