using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.DataStores;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// The Lua surface of <c>DataStoreService</c> for one world: <c>GetDataStore</c>, <c>GetGlobalDataStore</c> and
    /// <c>GetRequestBudgetForRequestType</c>, the yielding <c>GetAsync</c>, <c>SetAsync</c>, <c>UpdateAsync</c>,
    /// <c>IncrementAsync</c> and <c>RemoveAsync</c> of a store, and <c>DataStoreKeyInfo</c>. Installed as an environment
    /// decorator like HttpService and AIService: it patches the instance metatable for the three classes only. A
    /// yielding call returns one fresh signal its Lua bridge waits on; the scheduler's phase drain settles the request
    /// and fires the signal, so the thread resumes on a later step and nothing blocks a frame or runs on a thread of its
    /// own (WebGL-safe). Values cross through the shared table/JSON contract (<see cref="LuaCsRbxJson"/>).
    /// </summary>
    internal sealed class LuaCsRbxDataStoreAdapter : IDisposable
    {
        private const string BridgeSource = @"
            local prepare = task._prepareDataStore
            local readForUpdate = task._readDataStoreUpdate
            local commitUpdate = task._commitDataStoreUpdate
            task._prepareDataStore = nil
            task._readDataStoreUpdate = nil
            task._commitDataStoreUpdate = nil
            local create, resume, status = coroutine.create, coroutine.resume, coroutine.status
            local function await(prepared)
                local ok, first, second = prepared:Wait()
                if not ok then
                    error(first, 0)
                end
                return first, second
            end
            local function settle(ticket, co, ok, ...)
                if not ok then
                    commitUpdate(ticket, 'abandon')
                    error((...), 0)
                end
                if status(co) ~= 'dead' then
                    return commitUpdate(ticket, 'yielded')
                end
                return commitUpdate(ticket, 'commit', ...)
            end
            return {
                GetAsync = function(store, key, options)
                    return await(prepare('GetAsync', store, key, options))
                end,
                SetAsync = function(store, key, value, userIds, options)
                    local version = await(prepare('SetAsync', store, key, value, userIds, options))
                    return version
                end,
                IncrementAsync = function(store, key, delta, userIds, options)
                    return await(prepare('IncrementAsync', store, key, delta, userIds, options))
                end,
                RemoveAsync = function(store, key)
                    return await(prepare('RemoveAsync', store, key))
                end,
                UpdateAsync = function(store, key, transform)
                    local ticket = await(prepare('UpdateAsync', store, key, transform))
                    local current, info = readForUpdate(ticket)
                    local co = create(transform)
                    return settle(ticket, co, resume(co, current, info))
                end,
            }";

        private const int MaxValueDepth = LuaCsRbxJson.MaxNestingDepth;
        private const string ModScript = "main.lua";

        private static readonly (string Name, int Value)[] RequestTypes =
        {
            ("GetAsync", 0), ("SetIncrementAsync", 1), ("UpdateAsync", 2), ("GetSortedAsync", 3),
            ("SetIncrementSortedAsync", 4), ("OnUpdate", 5), ("ListAsync", 6), ("GetVersionAsync", 7),
            ("RemoveVersionAsync", 8), ("StandardRead", 9), ("StandardWrite", 10), ("StandardList", 11),
            ("StandardRemove", 12), ("OrderedRead", 13), ("OrderedWrite", 14), ("OrderedList", 15),
            ("OrderedRemove", 16)
        };

        private sealed class PendingCall
        {
            public PendingCall(RbxDataStoreRequest request, RbxScriptSignal signal, LuaCsRbxModContext context, int line)
            {
                Request = request;
                Signal = signal;
                Context = context;
                Line = line;
            }

            public RbxDataStoreRequest Request { get; }
            public RbxScriptSignal Signal { get; }
            public LuaCsRbxModContext Context { get; }
            public int Line { get; }
        }

        private sealed class UpdateTicket
        {
            public UpdateTicket(RbxDataStoreRequest request, LuaCsRbxModContext context)
            {
                Request = request;
                Context = context;
            }

            public RbxDataStoreRequest Request { get; }
            public LuaCsRbxModContext Context { get; }
            public bool Read;
        }

        private readonly LuaCsRbxApiBindings _bindings;
        private readonly RbxDataStoreBackend _backend;
        private readonly RbxDataStoreService _service;
        private readonly ILuaCsRuntimeLifetimeHost _lifetimeHost;
        private readonly LuaCsRbxJson _json = new();
        private readonly List<PendingCall> _inFlight = new();
        private readonly Action<SchedulerPhase, double> _onPhase;
        private long _generation;
        private bool _disposed;

        public LuaCsRbxDataStoreAdapter(LuaCsRbxApiBindings bindings, RbxDataStoreBackend backend)
        {
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _lifetimeHost = (object)bindings as ILuaCsRuntimeLifetimeHost;
            RegisterRequestTypeEnum(bindings.Enums);
            _service = AttachService();
            _onPhase = Drain;
            _bindings.Scheduler.PhaseReached += _onPhase;
            _lifetimeHost?.RegisterRuntimeLifetime(this);
        }

        /// <summary>The service <c>game:GetService("DataStoreService")</c> returns in this world.</summary>
        public RbxDataStoreService Service => _service;

        /// <summary>Requests whose thread has not been resumed yet.</summary>
        public int InFlightCount => _inFlight.Count;

        /// <summary>Registers the surface on one script registry.</summary>
        public void Register(IScriptFunctionRegistry registry)
        {
            if (!(registry is LuaCsApiRegistry luaRegistry))
            {
                throw new ArgumentException("The Rbx DataStore surface requires the Lua-CSharp registry adapter.",
                    nameof(registry));
            }

            luaRegistry.RegisterEnvironmentDecorator(InstallIntoState);
        }

        /// <summary>Runs the drain now (the scheduler does it every phase); for hosts and tests.</summary>
        internal void DrainForTesting(SchedulerPhase phase = SchedulerPhase.Heartbeat)
        {
            Drain(phase, 0d);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _bindings.Scheduler.PhaseReached -= _onPhase;
            _service.Close();
            _inFlight.Clear();
        }

        private RbxDataStoreService AttachService()
        {
            RbxDataModel game = _bindings.Game;
            RbxInstance existing;
            try
            {
                existing = game.Services.FindService(RbxDataStoreClasses.Service);
            }
            catch (RbxError)
            {
                existing = null;
            }

            if (existing is RbxDataStoreService attached)
            {
                return attached;
            }

            RbxDataStoreService service = new(_bindings.Registry.Catalog, _backend, _bindings.Registry.WorldId,
                CountPlayers, _bindings.LogSink);
            game.Services.Register(RbxDataStoreClasses.Service, service);
            return service;
        }

        private int CountPlayers()
        {
            return _bindings.Players != null ? Math.Max(1, _bindings.Players.GetPlayers().Count) : 1;
        }

        private static void RegisterRequestTypeEnum(RbxEnumRegistry enums)
        {
            if (enums == null || enums.TryGet("DataStoreRequestType", out _))
            {
                return;
            }

            List<(string, int)> items = new(RequestTypes.Length);
            for (int index = 0; index < RequestTypes.Length; index++)
            {
                items.Add((RequestTypes[index].Name, RequestTypes[index].Value));
            }

            enums.Register(new RbxEnum("DataStoreRequestType", items));
        }

        private void Drain(SchedulerPhase phase, double deltaSeconds)
        {
            if (_disposed)
            {
                return;
            }

            if (phase == SchedulerPhase.Heartbeat)
            {
                _service.FlushDue();
            }

            if (_inFlight.Count == 0)
            {
                return;
            }

            _service.Pump();
            int kept = 0;
            for (int index = 0; index < _inFlight.Count; index++)
            {
                PendingCall call = _inFlight[index];
                if (!TryComplete(call))
                {
                    _inFlight[kept++] = call;
                }
            }

            if (kept < _inFlight.Count)
            {
                _inFlight.RemoveRange(kept, _inFlight.Count - kept);
            }
        }

        private bool TryComplete(PendingCall call)
        {
            RbxDataStoreRequest request = call.Request;
            if (request.State == RbxDataStoreRequestState.Granted)
            {
                Fire(call, true, LuaCsRbxLua.Box(new UpdateTicket(request, call.Context), null), LuaValue.Nil);
                return true;
            }

            if (!request.IsSettled)
            {
                return false;
            }

            if (request.State == RbxDataStoreRequestState.Failed)
            {
                Fire(call, false, new LuaValue(FailureText(call, request.Error)), LuaValue.Nil);
                return true;
            }

            try
            {
                RbxDataStoreEntry entry = request.Result;
                switch (request.Operation)
                {
                    case RbxDataStoreOperation.Set:
                        Fire(call, true, new LuaValue(RbxDataStoreKeyInfo.VersionText(entry?.Version ?? 0L)), LuaValue.Nil);
                        break;
                    default:
                        if (entry == null)
                        {
                            Fire(call, true, LuaValue.Nil, LuaValue.Nil);
                        }
                        else
                        {
                            Fire(call, true, DecodeValue(entry.ValueJson, request.Store.StoreName),
                                WrapKeyInfo(call.Context, entry));
                        }

                        break;
                }
            }
            catch (RbxError error)
            {
                Fire(call, false, new LuaValue(FailureText(call, error)), LuaValue.Nil);
            }

            return true;
        }

        private static void Fire(PendingCall call, bool ok, LuaValue first, LuaValue second)
        {
            call.Signal.Fire(ok, first, second);
        }

        private static string FailureText(PendingCall call, RbxError error)
        {
            RbxError failure = error ?? RbxDataStoreErrors.Rejected("the request failed");
            string modId = call.Context?.OwnerModId;
            return string.IsNullOrWhiteSpace(modId) ? failure.Message : failure.WithContext(modId, ModScript, call.Line).Message;
        }

        private LuaValue DecodeValue(string json, string storeName)
        {
            try
            {
                return _json.DecodeWithin(json, RbxDataStoreOptions.MirrorValueLimit + 1);
            }
            catch (RbxError error)
            {
                throw RbxDataStoreErrors.Corrupted(storeName, error.RawMessage);
            }
        }

        private LuaValue WrapKeyInfo(LuaCsRbxModContext context, RbxDataStoreEntry entry)
        {
            return context.WrapInstance(new RbxDataStoreKeyInfo(_service.Catalog, entry));
        }

        private void InstallIntoState(LuaState state)
        {
            LuaValue gameValue = state.Environment["game"];
            if (gameValue.Type == LuaValueType.Nil)
            {
                return;
            }

            if (!TryGetInstance(gameValue, out LuaCsRbxInstanceProxy gameProxy))
            {
                throw new InvalidOperationException("The Rbx DataStore surface requires the production game proxy.");
            }

            LuaCsRbxModContext context = gameProxy.Context;
            LuaTable bridge = BuildBridge(state, context);
            LuaTable serviceMethods = BuildServiceMethods(context);
            LuaTable storeMethods = BuildStoreMethods(context, bridge);
            LuaTable keyInfoMethods = BuildKeyInfoMethods(context);
            LuaTable instanceMeta = gameProxy.Metatable;
            LuaValue originalIndex = instanceMeta[Metamethods.Index];
            LuaValue originalNewIndex = instanceMeta[Metamethods.NewIndex];
            if (originalIndex.Type != LuaValueType.Function || originalNewIndex.Type != LuaValueType.Function)
            {
                throw new InvalidOperationException(
                    "The Rbx DataStore surface requires the production Instance.__index and __newindex functions.");
            }

            // WHY every write is refused: the service, its stores and key information are detached instances the
            // registry does not hold, so a Parent or Name write would put an unregistered instance into the tree.
            instanceMeta[Metamethods.NewIndex] = new LuaFunction("Instance.__newindex.dataStore", async (ctx, ct) =>
            {
                LuaValue selfValue = Arg(ctx, 0);
                LuaValue keyValue = Arg(ctx, 1);
                if (TryGetInstance(selfValue, out LuaCsRbxInstanceProxy proxy) && ReferenceEquals(proxy.Context, context)
                    && IsOwnInstance(proxy.Instance))
                {
                    string member = keyValue.Type == LuaValueType.String ? keyValue.Read<string>() : Describe(keyValue);
                    throw ToLuaError(ctx.State, RbxError.BadArgument(
                        proxy.Instance.ClassName + "." + member + " can't be set",
                        "data store objects are read-only; keep your own values in a table"));
                }

                LuaValue[] arguments = { selfValue, keyValue, Arg(ctx, 2) };
                LuaValue[] results = await ctx.State.CallAsync(originalNewIndex, arguments.AsSpan(), ct);
                return ctx.Return(results);
            });

            instanceMeta[Metamethods.Index] = new LuaFunction("Instance.__index.dataStore", async (ctx, ct) =>
            {
                LuaValue selfValue = Arg(ctx, 0);
                LuaValue keyValue = Arg(ctx, 1);
                if (keyValue.Type == LuaValueType.String && TryGetInstance(selfValue, out LuaCsRbxInstanceProxy proxy)
                                                         && ReferenceEquals(proxy.Context, context))
                {
                    string key = keyValue.Read<string>();
                    if (ReferenceEquals(proxy.Instance, _service))
                    {
                        if (serviceMethods.TryGetValue(key, out LuaValue method))
                        {
                            return ctx.Return(method);
                        }
                    }
                    else if (proxy.Instance is RbxGlobalDataStore store && ReferenceEquals(store.Service, _service))
                    {
                        if (storeMethods.TryGetValue(key, out LuaValue method))
                        {
                            return ctx.Return(method);
                        }
                    }
                    else if (proxy.Instance is RbxDataStoreKeyInfo info)
                    {
                        if (TryReadKeyInfoProperty(info, key, out LuaValue property))
                        {
                            return ctx.Return(property);
                        }

                        if (keyInfoMethods.TryGetValue(key, out LuaValue method))
                        {
                            return ctx.Return(method);
                        }
                    }
                }

                LuaValue[] arguments = { selfValue, keyValue };
                LuaValue[] results = await ctx.State.CallAsync(originalIndex, arguments.AsSpan(), ct);
                return ctx.Return(results);
            });
        }

        private LuaTable BuildBridge(LuaState state, LuaCsRbxModContext context)
        {
            LuaValue taskValue = state.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw new InvalidOperationException("The Rbx DataStore surface requires the production task scheduler table.");
            }

            LuaTable task = taskValue.Read<LuaTable>();
            task["_prepareDataStore"] = Fn("task._prepareDataStore", ctx => Prepare(ctx, context), context);
            task["_readDataStoreUpdate"] = FnMulti("task._readDataStoreUpdate", ctx => ReadForUpdate(ctx, context), context);
            task["_commitDataStoreUpdate"] = FnMulti("task._commitDataStoreUpdate", ctx => CommitUpdate(ctx, context), context);
            try
            {
                LuaValue[] results = LuaCsRbxHttpServiceAdapter.RequireSynchronousBridgeResult(
                    state.DoStringAsync(BridgeSource, "coreai_datastore_bridge"));
                if (results.Length != 1 || results[0].Type != LuaValueType.Table)
                {
                    throw new InvalidOperationException("The Rbx DataStore scheduler bridge did not return its method table.");
                }

                return results[0].Read<LuaTable>();
            }
            finally
            {
                task["_prepareDataStore"] = LuaValue.Nil;
                task["_readDataStoreUpdate"] = LuaValue.Nil;
                task["_commitDataStoreUpdate"] = LuaValue.Nil;
            }
        }

        private LuaTable BuildServiceMethods(LuaCsRbxModContext context)
        {
            LuaTable methods = new();
            methods["GetDataStore"] = Fn("DataStoreService.GetDataStore", ctx =>
            {
                RequireServiceSelf(ctx, "DataStoreService:GetDataStore");
                RequireCapability(context, "DataStoreService:GetDataStore");
                string name = ReadString(ctx, 1, "DataStoreService:GetDataStore", 1);
                LuaValue scopeValue = Arg(ctx, 2);
                string scope = null;
                if (scopeValue.Type != LuaValueType.Nil)
                {
                    scope = ReadString(ctx, 2, "DataStoreService:GetDataStore", 2);
                }

                if (Arg(ctx, 3).Type != LuaValueType.Nil)
                {
                    throw RbxDataStoreErrors.Planned("DataStoreService:GetDataStore with DataStoreOptions",
                        "leave the options argument out; AllScopes listing is not built yet");
                }

                return context.WrapInstance(_service.GetDataStore(name, scope));
            }, context);
            methods["GetGlobalDataStore"] = Fn("DataStoreService.GetGlobalDataStore", ctx =>
            {
                RequireServiceSelf(ctx, "DataStoreService:GetGlobalDataStore");
                RequireCapability(context, "DataStoreService:GetGlobalDataStore");
                return context.WrapInstance(_service.GetGlobalDataStore());
            }, context);
            methods["GetRequestBudgetForRequestType"] = Fn("DataStoreService.GetRequestBudgetForRequestType", ctx =>
            {
                RequireServiceSelf(ctx, "DataStoreService:GetRequestBudgetForRequestType");
                RequireCapability(context, "DataStoreService:GetRequestBudgetForRequestType");
                RbxEnumItem item = ReadEnumArgument(ctx, 1, _bindings.Enums, "DataStoreRequestType",
                    "DataStoreService:GetRequestBudgetForRequestType", 1);
                return new LuaValue(_service.GetRequestBudget(item.Name));
            }, context);
            methods["GetOrderedDataStore"] = Stub("DataStoreService:GetOrderedDataStore",
                "keep a leaderboard in one DataStore key holding a table of { name, score } entries until ordered "
                + "stores land");
            methods["ListDataStoresAsync"] = Stub("DataStoreService:ListDataStoresAsync",
                "remember the store names you use in a table; listing is not built yet");
            methods["SetRateLimitForRequestType"] = Unsupported("DataStoreService:SetRateLimitForRequestType",
                "the host game sets the request budgets (RbxDataStoreOptions); read them with "
                + "GetRequestBudgetForRequestType");
            methods["IsA"] = IsAFunction("DataStoreService.IsA");
            return methods;
        }

        private LuaTable BuildStoreMethods(LuaCsRbxModContext context, LuaTable bridge)
        {
            LuaTable methods = new();
            methods["GetAsync"] = bridge["GetAsync"];
            methods["SetAsync"] = bridge["SetAsync"];
            methods["IncrementAsync"] = bridge["IncrementAsync"];
            methods["RemoveAsync"] = bridge["RemoveAsync"];
            methods["UpdateAsync"] = bridge["UpdateAsync"];
            const string versions = "the store keeps the latest value of each key only; keep the history you need "
                                    + "inside the value";
            methods["GetVersionAsync"] = Stub("DataStore:GetVersionAsync", versions);
            methods["GetVersionAtTimeAsync"] = Stub("DataStore:GetVersionAtTimeAsync", versions);
            methods["ListVersionsAsync"] = Stub("DataStore:ListVersionsAsync", versions);
            methods["RemoveVersionAsync"] = Stub("DataStore:RemoveVersionAsync", versions);
            methods["ListKeysAsync"] = Stub("DataStore:ListKeysAsync",
                "keep the keys you need in one index key (a table of names) until listing lands");
            methods["OnUpdate"] = Unsupported("GlobalDataStore:OnUpdate",
                "OnUpdate is deprecated in Roblox; read the key again with GetAsync when you need it");
            methods["IsA"] = IsAFunction("GlobalDataStore.IsA");
            return methods;
        }

        private LuaTable BuildKeyInfoMethods(LuaCsRbxModContext context)
        {
            LuaTable methods = new();
            methods["GetUserIds"] = Fn("DataStoreKeyInfo.GetUserIds", ctx =>
            {
                RbxDataStoreKeyInfo info = RequireKeyInfoSelf(ctx, "DataStoreKeyInfo:GetUserIds");
                LuaTable ids = new();
                for (int index = 0; index < info.UserIds.Length; index++)
                {
                    ids[index + 1] = new LuaValue((double)info.UserIds[index]);
                }

                return new LuaValue(ids);
            }, context);
            methods["GetMetadata"] = Fn("DataStoreKeyInfo.GetMetadata", ctx =>
            {
                RbxDataStoreKeyInfo info = RequireKeyInfoSelf(ctx, "DataStoreKeyInfo:GetMetadata");
                LuaValue metadata = _json.DecodeWithin(info.MetadataJson, RbxDataStoreOptions.MirrorValueLimit);
                return metadata.Type == LuaValueType.Table ? metadata : new LuaValue(new LuaTable());
            }, context);
            methods["IsA"] = IsAFunction("DataStoreKeyInfo.IsA");
            return methods;
        }

        private bool IsOwnInstance(RbxInstance instance)
        {
            return ReferenceEquals(instance, _service)
                   || (instance is RbxGlobalDataStore store && ReferenceEquals(store.Service, _service))
                   || instance is RbxDataStoreKeyInfo;
        }

        private static bool TryReadKeyInfoProperty(RbxDataStoreKeyInfo info, string key, out LuaValue value)
        {
            switch (key)
            {
                case "CreatedTime":
                    value = new LuaValue((double)info.CreatedTime);
                    return true;
                case "UpdatedTime":
                    value = new LuaValue((double)info.UpdatedTime);
                    return true;
                case "Version":
                    value = new LuaValue(info.Version);
                    return true;
                default:
                    value = LuaValue.Nil;
                    return false;
            }
        }

        private LuaValue Prepare(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            string method = ReadString(ctx, 0, "DataStore operation");
            string member = "DataStore:" + method;
            RequireCapability(context, member);
            RbxGlobalDataStore store = RequireStoreArgument(ctx, 1, member);
            string key = ReadKey(ctx, 2, member);
            RbxDataStoreRequest request;
            string modId = context.OwnerModId ?? string.Empty;
            switch (method)
            {
                case "GetAsync":
                    RequireNoOptions(ctx, 3, member, "DataStoreGetOptions");
                    request = new RbxDataStoreRequest(RbxDataStoreOperation.Get, store, key, modId);
                    break;
                case "SetAsync":
                    LuaValue value = Arg(ctx, 3);
                    if (value.Type == LuaValueType.Nil)
                    {
                        throw RbxError.BadArgument("DataStore:SetAsync Argument 2 missing or nil",
                            "pass the value to store; use RemoveAsync to delete a key");
                    }

                    RequireNoOptions(ctx, 5, member, "DataStoreSetOptions");
                    request = new RbxDataStoreRequest(RbxDataStoreOperation.Set, store, key, modId)
                    {
                        ValueJson = EncodeValue(value, false),
                        UserIds = ReadUserIds(Arg(ctx, 4))
                    };
                    break;
                case "IncrementAsync":
                    RequireNoOptions(ctx, 5, member, "DataStoreIncrementOptions");
                    request = new RbxDataStoreRequest(RbxDataStoreOperation.Increment, store, key, modId)
                    {
                        Delta = ReadDelta(Arg(ctx, 3)),
                        UserIds = ReadUserIds(Arg(ctx, 4))
                    };
                    break;
                case "UpdateAsync":
                    if (Arg(ctx, 3).Type != LuaValueType.Function)
                    {
                        throw RbxError.BadArgument("DataStore:UpdateAsync expects a function at argument 2",
                            "pass function(oldValue, keyInfo) return newValue end");
                    }

                    request = new RbxDataStoreRequest(RbxDataStoreOperation.Update, store, key, modId);
                    break;
                case "RemoveAsync":
                    request = new RbxDataStoreRequest(RbxDataStoreOperation.Remove, store, key, modId);
                    break;
                default:
                    throw RbxError.BadArgument("unknown DataStore operation '" + method + "'",
                        "call GetAsync, SetAsync, UpdateAsync, IncrementAsync or RemoveAsync");
            }

            RequireThread(member);
            _service.Submit(request);
            long generation = ++_generation;
            RbxScriptSignal signal = new("DataStore." + method + "[" + generation + "]");
            LuaValue wrapped = LuaCsRbxDatatypeBindings.Wrap(signal, context);
            _inFlight.Add(new PendingCall(request, signal, context, ctx.State.GetTraceback().LastLine));
            return wrapped;
        }

        private LuaValue[] ReadForUpdate(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            UpdateTicket ticket = RequireTicket(ctx, context);
            if (ticket.Read)
            {
                throw RbxError.BadArgument("UpdateAsync read its value twice", "let UpdateAsync run its function once");
            }

            ticket.Read = true;
            RbxDataStoreEntry current = _service.ReadForUpdate(ticket.Request);
            if (current == null)
            {
                return new[] { LuaValue.Nil, LuaValue.Nil };
            }

            return new[] { DecodeValue(current.ValueJson, ticket.Request.Store.StoreName), WrapKeyInfo(context, current) };
        }

        private LuaValue[] CommitUpdate(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            UpdateTicket ticket = RequireTicket(ctx, context);
            string mode = ReadString(ctx, 1, "UpdateAsync commit");
            RbxDataStoreRequest request = ticket.Request;
            if (mode == "abandon")
            {
                _service.CommitUpdate(request, false, null, null, null);
                return Array.Empty<LuaValue>();
            }

            if (mode == "yielded")
            {
                _service.CommitUpdate(request, false, null, null, null);
                throw RbxDataStoreErrors.TransformYielded();
            }

            LuaValue newValue = Arg(ctx, 2);
            if (newValue.Type == LuaValueType.Nil)
            {
                _service.CommitUpdate(request, false, null, null, null);
                return new[] { LuaValue.Nil, LuaValue.Nil };
            }

            string json;
            long[] userIds;
            string metadata;
            try
            {
                json = EncodeValue(newValue, true);
                userIds = ReadUserIds(Arg(ctx, 3));
                metadata = EncodeMetadata(Arg(ctx, 4));
            }
            catch (RbxError)
            {
                _service.CommitUpdate(request, false, null, null, null);
                throw;
            }

            RbxDataStoreEntry written = _service.CommitUpdate(request, true, json, userIds, metadata);
            return new[] { DecodeValue(written.ValueJson, request.Store.StoreName), WrapKeyInfo(context, written) };
        }

        private UpdateTicket RequireTicket(LuaFunctionExecutionContext ctx, LuaCsRbxModContext context)
        {
            if (!TryUnbox(Arg(ctx, 0), out UpdateTicket ticket) || !ReferenceEquals(ticket.Context, context))
            {
                throw RbxError.BadArgument("UpdateAsync bridge received no ticket of this mod",
                    "call store:UpdateAsync(key, function(old) ... end)");
            }

            return ticket;
        }

        private string EncodeValue(LuaValue value, bool fromUpdate)
        {
            int limit = Math.Min(_backend.Options.MaxValueCharacters, RbxDataStoreOptions.MirrorValueLimit);
            HashSet<LuaTable> active = new(LuaTableIdentity.Instance);
            long lowerBound = 0L;
            CheckStorable(value, "$", 0, active, fromUpdate, ref lowerBound, limit);
            string json;
            try
            {
                json = _json.EncodeWithin(value, limit);
            }
            catch (RbxError error)
            {
                throw RbxDataStoreErrors.CantStore("table", "$ (" + error.RawMessage + ")");
            }

            if (json == null)
            {
                throw RbxDataStoreErrors.ValueTooLarge(limit);
            }

            return json;
        }

        private static void CheckStorable(LuaValue value, string path, int depth, HashSet<LuaTable> active,
            bool fromUpdate, ref long lowerBound, int limit)
        {
            switch (value.Type)
            {
                case LuaValueType.Nil:
                case LuaValueType.Boolean:
                    lowerBound += 4;
                    break;
                case LuaValueType.Number:
                    lowerBound += 1;
                    break;
                case LuaValueType.String:
                    lowerBound += value.Read<string>().Length + 2L;
                    break;
                case LuaValueType.Table:
                    CheckTable(value.Read<LuaTable>(), path, depth, active, fromUpdate, ref lowerBound, limit);
                    break;
                default:
                    string typeName = Describe(value);
                    if (fromUpdate && depth == 0)
                    {
                        throw RbxDataStoreErrors.ValueNotAllowed(typeName);
                    }

                    throw RbxDataStoreErrors.CantStore(typeName, path);
            }

            if (lowerBound > limit)
            {
                throw RbxDataStoreErrors.ValueTooLarge(limit);
            }
        }

        private static void CheckTable(LuaTable table, string path, int depth, HashSet<LuaTable> active,
            bool fromUpdate, ref long lowerBound, int limit)
        {
            if (depth >= MaxValueDepth)
            {
                throw RbxDataStoreErrors.CantStore("a table nested deeper than " + MaxValueDepth + " levels", path);
            }

            if (!active.Add(table))
            {
                throw RbxDataStoreErrors.CantStore("a cyclic table", path);
            }

            bool hasNumber = false;
            bool hasString = false;
            lowerBound += 2;
            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                string childPath;
                if (pair.Key.Type == LuaValueType.Number)
                {
                    double index = pair.Key.Read<double>();
                    if (double.IsNaN(index) || index < 1d || index != Math.Floor(index) || index > LuaCsRbxJson.MaxArrayIndex)
                    {
                        throw RbxDataStoreErrors.CantStore("a table with a key that is not a positive integer", path);
                    }

                    hasNumber = true;
                    childPath = path + "[" + ((long)index).ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                }
                else if (pair.Key.Type == LuaValueType.String)
                {
                    hasString = true;
                    string name = pair.Key.Read<string>();
                    lowerBound += name.Length + 3L;
                    childPath = path + "." + name;
                }
                else
                {
                    throw RbxDataStoreErrors.CantStore("a table with " + Describe(pair.Key) + " keys", path);
                }

                if (hasNumber && hasString)
                {
                    throw RbxDataStoreErrors.CantStore("a mixed table (array and dictionary keys)", path);
                }

                CheckStorable(pair.Value, childPath, depth + 1, active, fromUpdate, ref lowerBound, limit);
            }

            active.Remove(table);
        }

        private static long[] ReadUserIds(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return Array.Empty<long>();
            }

            if (value.Type != LuaValueType.Table)
            {
                throw RbxDataStoreErrors.UserIdFormat();
            }

            LuaTable table = value.Read<LuaTable>();
            List<long> ids = new();
            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                if (pair.Key.Type != LuaValueType.Number || pair.Value.Type != LuaValueType.Number)
                {
                    throw RbxDataStoreErrors.UserIdFormat();
                }

                double id = pair.Value.Read<double>();
                if (double.IsNaN(id) || double.IsInfinity(id) || id != Math.Floor(id)
                    || id < -9.2233720368547758E18 || id >= 9.2233720368547758E18)
                {
                    throw RbxDataStoreErrors.UserIdFormat();
                }

                ids.Add((long)id);
            }

            if (ids.Count > RbxDataStoreOptions.MirrorUserIdLimit)
            {
                throw RbxDataStoreErrors.UserIdsTooMany(RbxDataStoreOptions.MirrorUserIdLimit);
            }

            return ids.ToArray();
        }

        private string EncodeMetadata(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return "{}";
            }

            if (value.Type != LuaValueType.Table)
            {
                throw RbxDataStoreErrors.MetadataFormat();
            }

            LuaTable table = value.Read<LuaTable>();
            foreach (KeyValuePair<LuaValue, LuaValue> pair in table)
            {
                if (pair.Key.Type != LuaValueType.String)
                {
                    throw RbxDataStoreErrors.MetadataFormat();
                }

                if (pair.Key.Read<string>().Length > RbxDataStoreOptions.MirrorMetadataKeyLimit)
                {
                    throw RbxDataStoreErrors.MetadataTooLarge(RbxDataStoreOptions.MirrorMetadataLimit);
                }

                string encodedValue = EncodeValue(pair.Value, false);
                if (encodedValue.Length > RbxDataStoreOptions.MirrorMetadataValueLimit)
                {
                    throw RbxDataStoreErrors.MetadataTooLarge(RbxDataStoreOptions.MirrorMetadataLimit);
                }
            }

            string json = EncodeValue(value, false);
            if (json == "[]")
            {
                return "{}";
            }

            if (json.Length > RbxDataStoreOptions.MirrorMetadataLimit)
            {
                throw RbxDataStoreErrors.MetadataTooLarge(RbxDataStoreOptions.MirrorMetadataLimit);
            }

            return json;
        }

        private static long ReadDelta(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return 1L;
            }

            if (!TryCoerceNumber(value, out double delta) || double.IsNaN(delta) || double.IsInfinity(delta)
                || delta != Math.Floor(delta) || delta < -9.2233720368547758E18 || delta >= 9.2233720368547758E18)
            {
                throw RbxDataStoreErrors.DeltaNotAnInteger();
            }

            return (long)delta;
        }

        private static string ReadKey(LuaFunctionExecutionContext ctx, int index, string member)
        {
            LuaValue value = Arg(ctx, index);
            if (value.Type == LuaValueType.Nil)
            {
                throw RbxDataStoreErrors.KeyEmpty();
            }

            if (!TryCoerceString(value, out string key))
            {
                throw ExpectedArgument(member, "a string key", value, 1);
            }

            return key;
        }

        private static void RequireNoOptions(LuaFunctionExecutionContext ctx, int index, string member, string className)
        {
            if (Arg(ctx, index).Type != LuaValueType.Nil)
            {
                throw RbxDataStoreErrors.Planned(member + " with " + className,
                    "leave the options argument out; return metadata as the third value of an UpdateAsync function");
            }
        }

        private void RequireThread(string member)
        {
            System.Threading.CancellationToken lifetime = _lifetimeHost?.CurrentScriptLifetimeToken
                                                          ?? new System.Threading.CancellationToken(true);
            if (!lifetime.CanBeCanceled || lifetime.IsCancellationRequested)
            {
                throw RbxDataStoreErrors.NeedsThread(member);
            }
        }

        private static void RequireCapability(LuaCsRbxModContext context, string member)
        {
            if (context == null || (context.Capabilities & LuaCapabilities.Data) == 0)
            {
                throw RbxDataStoreErrors.CapabilityRequired(member, context?.OwnerModId);
            }
        }

        private void RequireServiceSelf(LuaFunctionExecutionContext ctx, string member)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !ReferenceEquals(proxy.Instance, _service))
            {
                throw RbxError.BadArgument(member + " expects DataStoreService as self",
                    "call methods with a colon on game:GetService('DataStoreService')");
            }
        }

        private RbxGlobalDataStore RequireStoreArgument(LuaFunctionExecutionContext ctx, int index, string member)
        {
            if (!TryGetInstance(Arg(ctx, index), out LuaCsRbxInstanceProxy proxy)
                || !(proxy.Instance is RbxGlobalDataStore store) || !ReferenceEquals(store.Service, _service))
            {
                throw RbxError.BadArgument(member + " expects a DataStore as self",
                    "call methods with a colon on DataStoreService:GetDataStore(name)");
            }

            return store;
        }

        private static RbxDataStoreKeyInfo RequireKeyInfoSelf(LuaFunctionExecutionContext ctx, string member)
        {
            if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy) || !(proxy.Instance is RbxDataStoreKeyInfo info))
            {
                throw RbxError.BadArgument(member + " expects a DataStoreKeyInfo as self",
                    "call methods with a colon on the second value GetAsync returns");
            }

            return info;
        }

        private static LuaFunction IsAFunction(string name)
        {
            return Fn(name, ctx =>
            {
                if (!TryGetInstance(Arg(ctx, 0), out LuaCsRbxInstanceProxy proxy))
                {
                    throw RbxError.BadArgument(name + " expects an Instance as self", "call it with a colon");
                }

                string ancestor = ReadString(ctx, 1, name, 1);
                return new LuaValue(RbxDataStoreClasses.IsA(proxy.Instance.ClassName, ancestor));
            });
        }

        private static LuaFunction Stub(string feature, string workaround)
        {
            return Fn(feature, _ => throw RbxDataStoreErrors.Planned(feature, workaround));
        }

        private static LuaFunction Unsupported(string feature, string workaround)
        {
            return Fn(feature, _ => throw new RbxError(RbxErrorCode.NotImplemented,
                feature + " is a known Rbx member deliberately unsupported by CoreAI.", workaround));
        }

        private sealed class LuaTableIdentity : IEqualityComparer<LuaTable>
        {
            public static readonly LuaTableIdentity Instance = new();

            public bool Equals(LuaTable left, LuaTable right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(LuaTable value)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }
    }
}
