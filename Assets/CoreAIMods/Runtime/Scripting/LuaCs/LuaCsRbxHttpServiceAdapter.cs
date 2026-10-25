using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;
using Lua.Runtime;
using static CoreAI.Ai.LuaCs.LuaCsRbxLua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Production-composed Rbx clock and HttpService adapter. The offline mirror defines
    /// <c>os.clock</c> as monotonic elapsed wall time; this intentionally replaces stock Lua's
    /// process-CPU-time meaning, and the stock meaning is not re-exposed under another name.
    /// Outbound methods always pass host policy, baseline safety, and per-actor rate checks before
    /// the transport. Production defaults to deny-all policy plus a loudly refusing transport.
    /// </summary>
    internal sealed class LuaCsRbxHttpServiceAdapter : IDisposable
    {
        internal const int DefaultRequestsPerWindow = 60;
        internal const double DefaultRateWindowSeconds = 60d;
        internal const int DefaultMaximumOutstandingRequests = 64;

        private const string BridgeSource = @"
            local prepare = task._prepareHttpRequest
            task._prepareHttpRequest = nil
            local function invoke(operation, service, ...)
                local prepared = prepare(operation, service, ...)
                if type(prepared) == 'string' then
                    error(prepared, 2)
                end
                local ok, value = prepared:Wait()
                if not ok then
                    error(value, 2)
                end
                return value
            end
            return {
                GetAsync = function(service, ...)
                    return invoke('GetAsync', service, ...)
                end,
                PostAsync = function(service, ...)
                    return invoke('PostAsync', service, ...)
                end,
                RequestAsync = function(service, ...)
                    return invoke('RequestAsync', service, ...)
                end,
            }";

        private sealed class PendingRequest : IDisposable
        {
            public PendingRequest(long id, RbxScriptSignal signal, CancellationToken lifetime)
            {
                Id = id;
                Signal = signal;
                Source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                Token = Source.Token;
            }

            public long Id { get; }
            public RbxScriptSignal Signal { get; }
            public CancellationTokenSource Source { get; }
            public CancellationToken Token { get; }
            public bool Completed;
            public bool Succeeded;
            public LuaValue Value;
            public Task Work = Task.CompletedTask;
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) { Source.Dispose(); }
            }
        }

        private readonly LuaCsRbxApiBindings _bindings;
        private readonly LuaCsRbxJson _json = new();
        private readonly IRbxHttpRequestPolicy _policy;
        private readonly IRbxHttpTransport _transport;
        private readonly IRbxHttpDestinationResolver _resolver;
        private readonly RbxHttpActorRateLimiter _rateLimiter;
        private readonly Func<double> _clock;
        private readonly object _gate = new();
        private readonly Dictionary<long, PendingRequest> _requests = new();
        private readonly ILuaCsRuntimeLifetimeHost _lifetimeHost;
        private readonly int _maximumOutstandingRequests;
        private volatile bool _disposed;
        private long _requestGeneration;

        public LuaCsRbxHttpServiceAdapter(LuaCsRbxApiBindings bindings,
            IRbxHttpRequestPolicy policy = null, IRbxHttpTransport transport = null,
            IRbxHttpDestinationResolver resolver = null,
            int requestsPerWindow = DefaultRequestsPerWindow,
            double rateWindowSeconds = DefaultRateWindowSeconds,
            Func<double> monotonicClock = null,
            int maximumOutstandingRequests = DefaultMaximumOutstandingRequests)
        {
            if (maximumOutstandingRequests <= 0) { throw new ArgumentOutOfRangeException(nameof(maximumOutstandingRequests)); }
            _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
            _lifetimeHost = (object)bindings as ILuaCsRuntimeLifetimeHost;
            _maximumOutstandingRequests = maximumOutstandingRequests;
            _policy = policy ?? RbxDenyAllHttpRequestPolicy.Instance;
            _transport = transport ?? RbxRefusingHttpTransport.Instance;
            _resolver = resolver ?? RbxRefusingHttpDestinationResolver.Instance;
            _clock = monotonicClock ?? ReadMonotonicSeconds;
            _rateLimiter = new RbxHttpActorRateLimiter(
                requestsPerWindow, rateWindowSeconds, _clock);
            _bindings.Scheduler.PhaseReached += DrainPendingCompletions;
            _lifetimeHost?.RegisterRuntimeLifetime(this);
        }

        public void Dispose()
        {
            List<PendingRequest> outgoing;
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
                outgoing = new List<PendingRequest>(_requests.Values);
                _requests.Clear();
            }

            _bindings.Scheduler.PhaseReached -= DrainPendingCompletions;
            foreach (PendingRequest request in outgoing)
            {
                try { request.Source.Cancel(); }
                catch (AggregateException) { }
                catch (ObjectDisposedException) { }
                finally { request.Dispose(); }
            }
        }

        public void Register(IScriptFunctionRegistry registry)
        {
            if (!(registry is LuaCsApiRegistry luaRegistry))
            {
                throw new ArgumentException(
                    "Rbx HttpService requires the Lua-CSharp registry adapter.",
                    nameof(registry));
            }

            luaRegistry.RegisterEnvironmentDecorator(InstallIntoState);
        }

        private void InstallIntoState(LuaState state)
        {
            // WHY: the sanctioned os surface (ONLY time + clock) has one definition on the
            // bindings; this decorator runs after the value factories and must reuse it rather
            // than hand-rolling a narrower table that would clobber os.time.
            state.Environment["os"] = _bindings.BuildOsTable();

            LuaValue gameValue = state.Environment["game"];
            if (gameValue.Type == LuaValueType.Nil)
            {
                return;
            }

            if (!TryGetInstance(gameValue, out LuaCsRbxInstanceProxy gameProxy))
            {
                throw new InvalidOperationException(
                    "Rbx HttpService requires the production game proxy before decoration.");
            }

            LuaCsRbxModContext context = gameProxy.Context;
            LuaTable methods = BuildMethods(state, context);
            LuaTable instanceMeta = gameProxy.Metatable;
            LuaValue originalIndex = instanceMeta[Metamethods.Index];
            if (originalIndex.Type != LuaValueType.Function)
            {
                throw new InvalidOperationException(
                    "Rbx HttpService requires the production Instance.__index function.");
            }

            instanceMeta[Metamethods.Index] = new LuaFunction(
                "Instance.__index.httpService", async (ctx, ct) =>
                {
                    LuaValue selfValue = Arg(ctx, 0);
                    LuaValue keyValue = Arg(ctx, 1);
                    if (TryGetInstance(selfValue, out LuaCsRbxInstanceProxy selfProxy)
                        && selfProxy.Instance.ClassName == "HttpService"
                        && keyValue.Type == LuaValueType.String
                        && methods.TryGetValue(keyValue.Read<string>(), out LuaValue method))
                    {
                        return ctx.Return(method);
                    }

                    LuaValue[] arguments = { selfValue, keyValue };
                    LuaValue[] results = await ctx.State.CallAsync(
                        originalIndex, arguments.AsSpan(), ct);
                    return ctx.Return(results);
                });

        }

        private LuaTable BuildMethods(LuaState state, LuaCsRbxModContext context)
        {
            LuaTable methods = new();
            methods["JSONEncode"] = Fn("HttpService.JSONEncode", ctx =>
            {
                RequireHttpService(ctx, context, 0);
                return new LuaValue(_json.Encode(Arg(ctx, 1)));
            }, context);
            methods["JSONDecode"] = Fn("HttpService.JSONDecode", ctx =>
            {
                RequireHttpService(ctx, context, 0);
                return _json.Decode(ReadString(ctx, 1, "HttpService:JSONDecode"));
            }, context);
            methods["GenerateGUID"] = Fn("HttpService.GenerateGUID", ctx =>
            {
                RequireHttpService(ctx, context, 0);
                bool braces = LuaCsRbxDatatypeBindings.ReadOptionalBoolean(ctx, 1, "HttpService:GenerateGUID", 1,
                    whenOmitted: true);
                string guid = Guid.NewGuid().ToString("D");
                return new LuaValue(braces ? "{" + guid + "}" : guid);
            }, context);
            methods["UrlEncode"] = Fn("HttpService.UrlEncode", ctx =>
            {
                RequireHttpService(ctx, context, 0);
                return new LuaValue(UrlEncode(
                    ReadString(ctx, 1, "HttpService:UrlEncode")));
            }, context);

            LuaValue taskValue = state.Environment["task"];
            if (taskValue.Type != LuaValueType.Table)
            {
                throw new InvalidOperationException(
                    "Rbx HttpService requires the production task scheduler table.");
            }

            LuaTable task = taskValue.Read<LuaTable>();
            task["_prepareHttpRequest"] = Fn("task._prepareHttpRequest",
                ctx => PrepareHttpRequest(ctx, context), context);
            try
            {
                ValueTask<LuaValue[]> bridgeExecution = state.DoStringAsync(
                    BridgeSource, "coreai_http_bridge");
                LuaValue[] bridgeResults = RequireSynchronousBridgeResult(bridgeExecution);
                if (bridgeResults.Length != 1
                    || bridgeResults[0].Type != LuaValueType.Table)
                {
                    throw new InvalidOperationException(
                        "Rbx HttpService scheduler bridge did not return its method table.");
                }

                LuaTable bridgeMethods = bridgeResults[0].Read<LuaTable>();
                methods["GetAsync"] = bridgeMethods["GetAsync"];
                methods["PostAsync"] = bridgeMethods["PostAsync"];
                methods["RequestAsync"] = bridgeMethods["RequestAsync"];
                return methods;
            }
            finally
            {
                task["_prepareHttpRequest"] = LuaValue.Nil;
            }
        }

        internal static LuaValue[] RequireSynchronousBridgeResult(
            ValueTask<LuaValue[]> execution)
        {
            if (!execution.IsCompletedSuccessfully)
            {
                throw new InvalidOperationException(
                    "Rbx HttpService bridge initialization yielded unexpectedly; "
                        + "blocking the runtime thread is forbidden.");
            }

            return execution.Result;
        }

        private LuaValue PrepareHttpRequest(LuaFunctionExecutionContext ctx,
            LuaCsRbxModContext context)
        {
            string operation = ReadString(ctx, 0, "HttpService outbound operation");
            RequireHttpService(ctx, context, 1);
            RbxHttpRequest request = BuildRequest(operation, ctx);
            string actorId = context.ActorContext.ActorId;

            // WHY: policy, safety and rate refusals are decided synchronously. Returning them as a
            // plain string lets the Lua bridge raise the host refusal in every execution context,
            // including one-off execute_lua chunks that own no scheduler signal-wait bridge.
            if (TryRefuse(actorId, request, out RbxHttpRequest approved, out RbxError refusal))
            {
                return new LuaValue(refusal.Message);
            }

            CancellationToken lifetime = _lifetimeHost?.CurrentScriptLifetimeToken ?? new CancellationToken(true);
            if (!lifetime.CanBeCanceled || lifetime.IsCancellationRequested)
            {
                return new LuaValue("HttpService requires a live scheduler thread and the host lifetime bridge.");
            }

            long generation = Interlocked.Increment(ref _requestGeneration);
            RbxScriptSignal responseSignal = new(
                "HttpService." + operation + ".Response[" + generation + "]");
            LuaValue wrappedSignal = LuaCsRbxDatatypeBindings.Wrap(responseSignal, context);

            // WHY: each request gets one fresh signal/generation. Its existing signal:Wait bridge
            // performs ScheduleSignalWait/ResumeSignalWait, and late completions cannot target a
            // later wait because no response signal is ever reused.
            PendingRequest pending = new(generation, responseSignal, lifetime);
            lock (_gate)
            {
                RemoveCanceledRequestsLocked();
                if (_disposed || _requests.Count >= _maximumOutstandingRequests || pending.Token.IsCancellationRequested)
                {
                    pending.Dispose();
                    return new LuaValue("HttpService outstanding-request capacity was reached or its lifetime ended.");
                }

                _requests.Add(generation, pending);
            }

            pending.Work = ResolveAndSendAsync(approved, operation, pending, actorId);
            return wrappedSignal;
        }

        internal Task CurrentWorkForTesting()
        {
            lock (_gate)
            {
                List<Task> work = new();
                foreach (PendingRequest request in _requests.Values) { work.Add(request.Work); }
                return Task.WhenAll(work);
            }
        }

        internal int OutstandingRequestsForTesting { get { lock (_gate) { return _requests.Count; } } }

        private RbxHttpRequest BuildRequest(string operation, LuaFunctionExecutionContext ctx)
        {
            switch (operation)
            {
                case "GetAsync":
                    return BuildGetRequest(ctx);
                case "PostAsync":
                    return BuildPostRequest(ctx);
                case "RequestAsync":
                    return BuildOptionsRequest(ctx);
                default:
                    throw RbxError.BadArgument(
                        "unknown HttpService outbound operation '" + operation + "'",
                        "call GetAsync, PostAsync, or RequestAsync");
            }
        }

        private static RbxHttpRequest BuildGetRequest(LuaFunctionExecutionContext ctx)
        {
            Uri uri = ReadAbsoluteUri(ctx, 2, "HttpService:GetAsync");
            IReadOnlyDictionary<string, string> headers = ReadHeaders(
                Arg(ctx, 4), "HttpService:GetAsync headers");
            return new RbxHttpRequest("GET", uri, headers);
        }

        private static RbxHttpRequest BuildPostRequest(LuaFunctionExecutionContext ctx)
        {
            Uri uri = ReadAbsoluteUri(ctx, 2, "HttpService:PostAsync");
            string body = ReadString(ctx, 3, "HttpService:PostAsync");
            Dictionary<string, string> headers = new(
                ReadHeaders(Arg(ctx, 6), "HttpService:PostAsync headers"),
                StringComparer.OrdinalIgnoreCase);
            if (!headers.ContainsKey("Content-Type"))
            {
                headers["Content-Type"] = ReadPostContentType(Arg(ctx, 4));
            }

            bool compress = LuaCsRbxDatatypeBindings.ReadOptionalBoolean(ctx, 5, "HttpService:PostAsync", 4);
            return new RbxHttpRequest("POST", uri, headers, body, compress);
        }

        private static RbxHttpRequest BuildOptionsRequest(LuaFunctionExecutionContext ctx)
        {
            LuaValue optionsValue = Arg(ctx, 2);
            if (optionsValue.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    "HttpService:RequestAsync expects an options table",
                    "pass { Url = 'https://...', Method = 'GET' }");
            }

            LuaTable options = optionsValue.Read<LuaTable>();
            string url = ReadRequiredString(options["Url"], "RequestAsync.Url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                throw RbxError.BadArgument(
                    "RequestAsync.Url must be an absolute URL",
                    "pass an absolute HTTPS URL allowed by the host policy");
            }

            string method = ReadOptionalString(options["Method"], "RequestAsync.Method")
                            ?? "GET";
            IReadOnlyDictionary<string, string> headers = ReadHeaders(
                options["Headers"], "RequestAsync.Headers");
            string body = ReadOptionalString(options["Body"], "RequestAsync.Body");
            bool compress = ReadCompress(options["Compress"]);
            int? timeoutSeconds = ReadTimeout(options["Timeout"]);
            return new RbxHttpRequest(
                method, uri, headers, body, compress, timeoutSeconds);
        }

        private bool TryRefuse(string actorId, RbxHttpRequest requested,
            out RbxHttpRequest approved, out RbxError refusal)
        {
            approved = null;
            string refusalReason = null;
            bool authorized = _policy.IsEnabled
                              && _policy.TryAuthorize(actorId, requested,
                                  out approved, out refusalReason);
            if (!authorized || approved == null)
            {
                string reason = string.IsNullOrWhiteSpace(refusalReason)
                    ? "the host policy did not explicitly authorize the request"
                    : refusalReason;
                refusal = new RbxError(
                    RbxErrorCode.NotAuthority,
                    "HttpService policy refused actor '" + actorId + "': " + reason,
                    "ask the host to allowlist the exact public HTTPS origin");
                return true;
            }

            if (!RbxHttpSafety.TryValidate(approved, out string safetyReason))
            {
                refusal = new RbxError(
                    RbxErrorCode.NotAuthority,
                    "HttpService safety check refused actor '" + actorId + "': " + safetyReason,
                    "use a public HTTPS endpoint without credentials or forbidden headers");
                return true;
            }

            if (!_rateLimiter.TryAcquire(actorId, out string rateReason))
            {
                refusal = new RbxError(
                    RbxErrorCode.BudgetExceeded,
                    "HttpService rate limit refused actor '" + actorId + "': " + rateReason,
                    "reduce this actor's request rate or wait for its window to expire");
                return true;
            }

            refusal = null;
            return false;
        }

        private async Task ResolveAndSendAsync(RbxHttpRequest approved, string operation,
            PendingRequest pending, string actorId)
        {
            try
            {
                CancellationToken token = pending.Token;
                token.ThrowIfCancellationRequested();
                IReadOnlyList<IPAddress> addresses;
                if (IPAddress.TryParse(approved.Uri.Host.Trim('[', ']'),
                        out IPAddress literalAddress))
                {
                    addresses = new[] { literalAddress };
                }
                else
                {
                    Task<IReadOnlyList<IPAddress>> resolveTask = _resolver.ResolveAsync(
                        approved.Uri.DnsSafeHost, token);
                    if (resolveTask == null)
                    {
                        throw new InvalidOperationException("the host resolver returned no task");
                    }

                    addresses = await AwaitRequestAsync(resolveTask, token);
                }

                token.ThrowIfCancellationRequested();
                if (addresses == null || addresses.Count == 0)
                {
                    throw new InvalidOperationException(
                        "the host resolver returned no destination addresses");
                }

                for (int index = 0; index < addresses.Count; index++)
                {
                    if (RbxHttpSafety.IsForbiddenAddress(addresses[index]))
                    {
                        throw new InvalidOperationException(
                            "the resolved destination is local, private, or special");
                    }
                }

                RbxValidatedHttpDestination destination = new(approved, addresses[0]);
                token.ThrowIfCancellationRequested();
                Task<RbxHttpResponse> sendTask = _transport.SendAsync(
                    destination, token);
                if (sendTask == null)
                {
                    throw new InvalidOperationException("the host transport returned no task");
                }

                RbxHttpResponse response = await AwaitRequestAsync(sendTask, token);
                token.ThrowIfCancellationRequested();
                if (response == null)
                {
                    throw new InvalidOperationException("the host transport returned no response");
                }

                if (response.StatusCode >= 300 && response.StatusCode <= 399)
                {
                    throw new InvalidOperationException(
                        "HTTP redirects are forbidden for mod requests");
                }

                if (operation != "RequestAsync" && !response.Success)
                {
                    throw new InvalidOperationException(
                        "HTTP " + response.StatusCode + " " + response.StatusMessage);
                }

                LuaValue value = operation == "RequestAsync"
                    ? new LuaValue(BuildResponseTable(response))
                    : new LuaValue(response.Body);
                Complete(pending, true, value);
            }
            catch (OperationCanceledException ex)
            {
                if (pending.Token.IsCancellationRequested) { Remove(pending); }
                else { Complete(pending, false, new LuaValue("HttpService host operation was canceled: " + ex.Message)); }
            }
            catch (Exception ex)
            {
                string reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                Complete(pending, false, new LuaValue("HttpService transport refused or failed actor '"
                    + actorId + "': " + reason));
            }
        }

        private static async Task<T> AwaitRequestAsync<T>(Task<T> operation, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                _ = ObserveLateCompletionAsync(operation);
                throw new OperationCanceledException(token);
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            TaskCompletionSource<bool> canceled = new();
#else
            TaskCompletionSource<bool> canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
#endif
            using (token.Register(() => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(operation, canceled.Task) != operation || token.IsCancellationRequested)
                {
                    _ = ObserveLateCompletionAsync(operation);
                    throw new OperationCanceledException(token);
                }

                return await operation;
            }
        }

        private static async Task ObserveLateCompletionAsync<T>(Task<T> operation)
        {
            try { await operation; }
            catch (Exception) { }
        }

        private void Complete(PendingRequest pending, bool succeeded, LuaValue value)
        {
            lock (_gate)
            {
                if (_disposed || pending.Token.IsCancellationRequested || !_requests.ContainsKey(pending.Id))
                {
                    _requests.Remove(pending.Id);
                    pending.Dispose();
                    return;
                }

                pending.Succeeded = succeeded;
                pending.Value = value;
                pending.Completed = true;
            }
        }

        private void Remove(PendingRequest pending)
        {
            lock (_gate) { _requests.Remove(pending.Id); }
            pending.Dispose();
        }

        private void RemoveCanceledRequestsLocked()
        {
            List<PendingRequest> canceled = new();
            foreach (PendingRequest request in _requests.Values)
            {
                if (request.Token.IsCancellationRequested) { canceled.Add(request); }
            }

            foreach (PendingRequest request in canceled)
            {
                _requests.Remove(request.Id);
                request.Dispose();
            }
        }

        private void DrainPendingCompletions(SchedulerPhase phase, double deltaSeconds)
        {
            List<PendingRequest> completed = new();
            lock (_gate)
            {
                RemoveCanceledRequestsLocked();
                if (_disposed) { return; }
                foreach (PendingRequest request in _requests.Values)
                {
                    if (request.Completed) { completed.Add(request); }
                }

                foreach (PendingRequest request in completed) { _requests.Remove(request.Id); }
            }

            foreach (PendingRequest completion in completed)
            {
                try
                {
                    if (!completion.Token.IsCancellationRequested && !_disposed)
                    {
                        completion.Signal.Fire(completion.Succeeded, completion.Value);
                    }
                }
                finally { completion.Dispose(); }
            }
        }

        private static LuaTable BuildResponseTable(RbxHttpResponse response)
        {
            LuaTable headers = new();
            foreach (KeyValuePair<string, string> pair in response.Headers)
            {
                headers[pair.Key] = pair.Value;
            }

            LuaTable result = new();
            result["Success"] = response.Success;
            result["StatusCode"] = response.StatusCode;
            result["StatusMessage"] = response.StatusMessage;
            result["Headers"] = headers;
            result["Body"] = response.Body;
            return result;
        }

        private static void RequireHttpService(LuaFunctionExecutionContext ctx,
            LuaCsRbxModContext context, int argumentIndex)
        {
            if (!TryGetInstance(Arg(ctx, argumentIndex), out LuaCsRbxInstanceProxy proxy)
                || !ReferenceEquals(proxy.Context, context)
                || proxy.Instance.ClassName != "HttpService"
                || !ReferenceEquals(proxy.Instance,
                    context.Bindings.Game.GetService("HttpService")))
            {
                throw RbxError.BadArgument(
                    "HttpService method expects HttpService as self",
                    "call methods with a colon on game:GetService('HttpService')");
            }
        }

        private static Uri ReadAbsoluteUri(LuaFunctionExecutionContext ctx,
            int index, string operation)
        {
            string value = ReadString(ctx, index, operation);
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri uri))
            {
                throw RbxError.BadArgument(
                    operation + " expects an absolute URL",
                    "pass an absolute HTTPS URL allowed by the host policy");
            }

            return uri;
        }

        private static IReadOnlyDictionary<string, string> ReadHeaders(
            LuaValue value, string label)
        {
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
            if (value.Type == LuaValueType.Nil)
            {
                return headers;
            }

            if (value.Type != LuaValueType.Table)
            {
                throw RbxError.BadArgument(
                    label + " must be a string-keyed table",
                    "pass a table whose header names and values are strings");
            }

            foreach (KeyValuePair<LuaValue, LuaValue> pair in value.Read<LuaTable>())
            {
                if (pair.Key.Type != LuaValueType.String
                    || pair.Value.Type != LuaValueType.String)
                {
                    throw RbxError.BadArgument(
                        label + " must contain only string names and values",
                        "convert every header name and value to a string");
                }

                headers.Add(pair.Key.Read<string>(), pair.Value.Read<string>());
            }

            return headers;
        }

        private static string ReadPostContentType(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return "application/json";
            }

            if (value.Type == LuaValueType.String)
            {
                return value.Read<string>();
            }

            if (TryUnbox(value, out RbxEnumItem item)
                && item.EnumType.Name == "HttpContentType")
            {
                switch (item.Name)
                {
                    case "ApplicationJson": return "application/json";
                    case "ApplicationXml": return "application/xml";
                    case "ApplicationUrlEncoded": return "application/x-www-form-urlencoded";
                    case "TextPlain": return "text/plain";
                    case "TextXml": return "text/xml";
                }
            }

            throw RbxError.BadArgument(
                "HttpService:PostAsync content_type is unsupported",
                "omit it for application/json or pass a supported HttpContentType value");
        }

        private static bool ReadCompress(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return false;
            }

            if (value.Type == LuaValueType.Boolean)
            {
                return value.Read<bool>();
            }

            if (TryUnbox(value, out RbxEnumItem item)
                && item.EnumType.Name == "HttpCompression")
            {
                return item.Name == "Gzip";
            }

            throw RbxError.BadArgument(
                "RequestAsync.Compress must be false, true, or an HttpCompression value",
                "omit it, pass false, or pass the Gzip compression value");
        }

        private static int? ReadTimeout(LuaValue value)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (value.Type != LuaValueType.Number)
            {
                throw RbxError.BadArgument(
                    "RequestAsync.Timeout must be a positive integer",
                    "pass a whole number of seconds greater than zero");
            }

            double number = value.Read<double>();
            if (number <= 0d || number > int.MaxValue
                || double.IsNaN(number) || double.IsInfinity(number)
                || number != Math.Truncate(number))
            {
                throw RbxError.BadArgument(
                    "RequestAsync.Timeout must be a positive integer",
                    "pass a whole number of seconds greater than zero");
            }

            return (int)number;
        }

        private static string ReadRequiredString(LuaValue value, string label)
        {
            string result = ReadOptionalString(value, label);
            if (result == null)
            {
                throw RbxError.BadArgument(
                    label + " is required",
                    "provide a non-empty string value");
            }

            return result;
        }

        private static string ReadOptionalString(LuaValue value, string label)
        {
            if (value.Type == LuaValueType.Nil)
            {
                return null;
            }

            if (value.Type != LuaValueType.String)
            {
                throw RbxError.BadArgument(
                    label + " must be a string",
                    "pass a string value");
            }

            return value.Read<string>();
        }

        private static string UrlEncode(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            StringBuilder output = new(bytes.Length);
            const string hex = "0123456789ABCDEF";
            for (int index = 0; index < bytes.Length; index++)
            {
                byte value = bytes[index];
                bool unescaped = value >= (byte)'a' && value <= (byte)'z'
                                 || value >= (byte)'A' && value <= (byte)'Z'
                                 || value >= (byte)'0' && value <= (byte)'9'
                                 || value == (byte)'-' || value == (byte)'_'
                                 || value == (byte)'~';
                if (unescaped)
                {
                    output.Append((char)value);
                    continue;
                }

                output.Append('%');
                output.Append(hex[value >> 4]);
                output.Append(hex[value & 0x0F]);
            }

            return output.ToString();
        }

        private static double ReadMonotonicSeconds()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
