#if COREAI_LLM
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using Newtonsoft.Json;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Portable raw-model middleware: optional preflight, scoped auth retry and measured telemetry.</summary>
    public sealed class GatewayChatClient : MEAI.IChatClient
    {
        private readonly MEAI.IChatClient _inner;
        private readonly GatewayAuthorizationSettings _settings;
        private readonly GatewayClientOptions _options;
        private readonly ILlmEntitlementPolicy? _policy;
        private readonly ILlmUsageSink? _sink;
        private readonly IGatewayAuthRefresher? _refresher;
        private readonly Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions?, GatewayRequestContext> _contextFactory;
        private readonly ILog _log;
        private readonly ILlmAsyncMarshaler _marshaler;
        private readonly IDisposable? _ownedService;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationToken _stop;
        private readonly SemaphoreSlim _usageSlots;
        private readonly object _usageGate = new object();
        private readonly HashSet<Task> _pendingUsage = new HashSet<Task>();
        private int _disposed;

        /// <summary>The inner client must be a raw model adapter; tool execution stays outside this middleware.</summary>
        public GatewayChatClient(MEAI.IChatClient inner, GatewayAuthorizationSettings settings,
            GatewayClientOptions options, ILlmEntitlementPolicy? entitlementPolicy = null,
            ILlmUsageSink? usageSink = null, IGatewayAuthRefresher? authRefresher = null,
            Func<IReadOnlyList<MEAI.ChatMessage>, MEAI.ChatOptions?, GatewayRequestContext>? contextFactory = null,
            ILog? log = null, ILlmAsyncMarshaler? asyncMarshaler = null, IDisposable? ownedService = null,
            CancellationToken hostLifetime = default)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Snapshot();
            _policy = entitlementPolicy;
            _sink = usageSink;
            _refresher = authRefresher;
            if (_options.EnableClientPreflight && _policy == null)
                throw new ArgumentException("Enabled gateway preflight requires a policy.");
            if (_options.ReportUsageToGateway && _sink == null)
                throw new ArgumentException("Enabled gateway telemetry requires a sink.");
            _contextFactory = contextFactory ?? GatewayRequestContext.FromMessages;
            _log = log ?? NullLog.Instance;
            _marshaler = asyncMarshaler ?? PassThroughLlmAsyncMarshaler.Instance;
            _ownedService = ownedService;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(hostLifetime);
            _stop = _lifetime.Token;
            _usageSlots = new SemaphoreSlim(_options.MaxConcurrentUsageReports,
                _options.MaxConcurrentUsageReports);
        }

        public async Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
            MEAI.ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            _stop.ThrowIfCancellationRequested();
            MEAI.ChatMessage[] materialized = Materialize(messages);
            GatewayRequestContext context = Context(materialized, options);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _stop);
            using IDisposable headers = _settings.BeginRequest(context);
            BindInvocation(context);
            using LlmRequestContext.Scope correlation = Correlation(context);
            await PreflightAsync(context, linked.Token);
            MEAI.UsageDetails? measured = null;
            string model = "";
            bool success = false;
            try
            {
                MEAI.ChatResponse response;
                try
                {
                    response = await _inner.GetResponseAsync(materialized, options, linked.Token);
                }
                catch (LlmClientException failure) when (failure.ErrorCode == LlmErrorCode.AuthExpired)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (context.Request.ProviderResponseState?.RetrySuppressed == true ||
                        !await TryRefreshAsync(linked.Token))
                        throw;
                    response = await _inner.GetResponseAsync(materialized, options, linked.Token);
                }
                linked.Token.ThrowIfCancellationRequested();
                measured = response.Usage;
                model = response.ModelId ?? "";
                success = true;
                return response;
            }
            finally
            {
                await ReportUsageAsync(context, measured, model, false, success, linked.Token);
            }
        }

        public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            _stop.ThrowIfCancellationRequested();
            MEAI.ChatMessage[] materialized = Materialize(messages);
            GatewayRequestContext context = Context(materialized, options);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _stop);
            using IDisposable headers = _settings.BeginRequest(context);
            BindInvocation(context);
            using LlmRequestContext.Scope correlation = Correlation(context);
            await PreflightAsync(context, linked.Token);
            MEAI.UsageDetails? measured = null;
            string model = "";
            bool committed = false;
            bool success = false;
            int attempt = 0;
            try
            {
                while (true)
                {
                    bool retry = false;
                    bool completedAttempt = false;
                    IAsyncEnumerator<MEAI.ChatResponseUpdate> stream =
                        _inner.GetStreamingResponseAsync(materialized, options, linked.Token)
                            .GetAsyncEnumerator(linked.Token);
                    try
                    {
                        while (true)
                        {
                            bool hasNext;
                            try
                            {
                                hasNext = await stream.MoveNextAsync();
                            }
                            catch (LlmClientException failure) when (failure.ErrorCode == LlmErrorCode.AuthExpired)
                            {
                                linked.Token.ThrowIfCancellationRequested();
                                if (attempt == 0 && !committed &&
                                    context.Request.ProviderResponseState?.RetrySuppressed != true &&
                                    await TryRefreshAsync(linked.Token))
                                {
                                    retry = true;
                                    attempt++;
                                    break;
                                }
                                throw;
                            }
                            linked.Token.ThrowIfCancellationRequested();
                            if (!hasNext)
                            {
                                completedAttempt = true;
                                break;
                            }
                            MEAI.ChatResponseUpdate update = stream.Current;
                            if (!string.IsNullOrEmpty(update.ModelId)) model = update.ModelId;
                            foreach (MEAI.AIContent content in update.Contents)
                            {
                                if (content is MEAI.UsageContent usage && usage.Details != null)
                                {
                                    measured = LlmUsageAccumulator.Accumulate(measured, usage.Details);
                                }
                                else if (content is not MEAI.TextContent text || !string.IsNullOrEmpty(text.Text))
                                {
                                    committed = true;
                                }
                            }
                            yield return update;
                        }
                    }
                    finally
                    {
                        await stream.DisposeAsync();
                    }
                    if (!retry)
                    {
                        success = completedAttempt;
                        break;
                    }
                }
            }
            finally
            {
                await ReportUsageAsync(context, measured, model, true, success, linked.Token);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            if (serviceType == null) throw new ArgumentNullException(nameof(serviceType));
            if (serviceKey == null && serviceType.IsInstanceOfType(this)) return this;
            return _inner.GetService(serviceType, serviceKey);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancel(_lifetime);
            try { _inner.Dispose(); }
            finally
            {
                try { _ownedService?.Dispose(); }
                finally { _lifetime.Dispose(); }
            }
        }

        private GatewayRequestContext Context(IReadOnlyList<MEAI.ChatMessage> messages, MEAI.ChatOptions? options)
        {
            GatewayRequestContext context = _contextFactory(messages, options) ??
                throw new InvalidOperationException("Gateway context factory returned no context.");
            return context.Snapshot(messages);
        }

        private void BindInvocation(GatewayRequestContext context)
        {
            context.Request.TraceId = _settings.HeaderProvider.RequestId;
            context.Request.IdempotencyKey = _settings.HeaderProvider.IdempotencyKey;
            context.Profile.Model = _settings.Model ?? "";
        }

        private static MEAI.ChatMessage[] Materialize(IEnumerable<MEAI.ChatMessage> messages) =>
            (messages ?? throw new ArgumentNullException(nameof(messages))).ToArray();

        private static LlmRequestContext.Scope Correlation(GatewayRequestContext context) =>
            LlmRequestContext.Begin(context.Request.AgentRoleId, context.Request.TraceId,
                context.Request.IdempotencyKey, context.Request.ActorId);

        private async Task PreflightAsync(GatewayRequestContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_options.EnableClientPreflight) return;
            LlmEntitlementDecision? decision;
            LlmClientException? unavailableFailure = null;
            try
            {
                decision = await _policy!.CheckAsync(context.Request, context.Profile, token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (LlmClientException failure) when (failure.ErrorCode == LlmErrorCode.BackendUnavailable)
            {
                token.ThrowIfCancellationRequested();
                unavailableFailure = failure;
                decision = null;
            }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                Warn("Gateway preflight failed; the configured availability policy was applied.");
                decision = null;
            }
            token.ThrowIfCancellationRequested();
            if (decision?.IsAllowed == true) return;
            if (decision == null && _options.PreflightUnavailablePolicy == GatewayPreflightUnavailablePolicy.Allow)
                return;
            if (decision == null)
                throw new LlmClientException("Gateway preflight is unavailable.", LlmErrorCode.BackendUnavailable,
                    unavailableFailure?.HttpStatus, unavailableFailure?.RetryAfterSeconds);
            LlmErrorCode code = decision.ToProviderError().ErrorCode;
            if (code == LlmErrorCode.None || code == LlmErrorCode.ProviderError ||
                code == LlmErrorCode.BackendUnavailable || code == LlmErrorCode.RateLimited ||
                code == LlmErrorCode.Timeout || code == LlmErrorCode.ContextLengthExceeded)
                code = LlmErrorCode.PermanentProviderError;
            throw new LlmClientException("Gateway preflight denied this request.", code,
                retryAfterSeconds: decision.RetryAfterSeconds,
                providerErrorBody: JsonConvert.SerializeObject(new Dictionary<string, object?>
                {
                    ["reason_code"] = decision.ReasonCode,
                    ["message"] = decision.Message,
                    ["retry_after_seconds"] = decision.RetryAfterSeconds
                }));
        }

        private async Task<bool> TryRefreshAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_refresher == null) return false;
            try
            {
                bool refreshed = await _refresher.RefreshAsync(token);
                token.ThrowIfCancellationRequested();
                return refreshed;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();
                Warn("Gateway credential refresh failed.");
                return false;
            }
        }

        private async Task ReportUsageAsync(GatewayRequestContext context, MEAI.UsageDetails? usage,
            string model, bool streaming, bool success, CancellationToken token)
        {
            try { await ReportUsageCoreAsync(context, usage, model, streaming, success, token); }
            catch (Exception) { Warn("Gateway telemetry failed; the primary outcome was preserved."); }
        }

        private async Task ReportUsageCoreAsync(GatewayRequestContext context, MEAI.UsageDetails? usage,
            string model, bool streaming, bool success, CancellationToken token)
        {
            if (!_options.ReportUsageToGateway || usage == null || token.IsCancellationRequested ||
                _stop.IsCancellationRequested) return;
            if (!await _usageSlots.WaitAsync(0))
            {
                Warn("Gateway telemetry admission is full; the optional report was skipped.");
                return;
            }
            CancellationTokenSource operationStop = CancellationTokenSource.CreateLinkedTokenSource(token, _stop);
            Task operation;
            try
            {
                operationStop.Token.ThrowIfCancellationRequested();
                operation = _sink!.RecordAsync(new LlmUsageRecord
                {
                    TraceId = context.Request.TraceId, RoleId = context.Request.AgentRoleId,
                    RoutingProfileId = context.Request.RoutingProfileId,
                    ExecutionMode = LlmExecutionMode.ServerManagedApi, Model = model,
                    PromptTokens = Count(usage.InputTokenCount), CompletionTokens = Count(usage.OutputTokenCount),
                    TotalTokens = Count(usage.TotalTokenCount), Streaming = streaming, Success = success,
                    ReasoningTokens = Additional(usage, "completion_tokens_details.reasoning_tokens"),
                    CacheReadTokens = Additional(usage, "prompt_tokens_details.cached_tokens"),
                    CacheWriteTokens = Additional(usage, "prompt_tokens_details.cache_write_tokens")
                }, operationStop.Token);
            }
            catch (Exception)
            {
                operationStop.Dispose();
                _usageSlots.Release();
                Warn("Gateway telemetry failed; the primary outcome was preserved.");
                return;
            }
            lock (_usageGate) _pendingUsage.Add(operation);
            Task observed = ObserveUsageAsync(operation, operationStop);
            using CancellationTokenSource deadlineStop = CancellationTokenSource.CreateLinkedTokenSource(_stop);
            Task deadline = _marshaler.DelayAsync(_options.UsageTimeoutMilliseconds, deadlineStop.Token);
            Task finished = await Task.WhenAny(observed, deadline);
            if (finished != observed)
            {
                Cancel(operationStop);
                Warn("Gateway telemetry deadline expired; the primary outcome was preserved.");
            }
            else
            {
                await observed;
            }
            Cancel(deadlineStop);
        }

        private async Task ObserveUsageAsync(Task operation, CancellationTokenSource operationStop)
        {
            try { await operation; }
            catch (OperationCanceledException) { }
            catch (Exception) { Warn("Gateway telemetry failed; the primary outcome was preserved."); }
            finally
            {
                lock (_usageGate) _pendingUsage.Remove(operation);
                operationStop.Dispose();
                _usageSlots.Release();
            }
        }

        private static int Count(long? count) =>
            (int)Math.Max(0, Math.Min(int.MaxValue, count ?? 0));

        private static int Additional(MEAI.UsageDetails usage, string key) =>
            Count(AdditionalCount(usage, key));

        private static long AdditionalCount(MEAI.UsageDetails usage, string requested)
        {
            if (usage.AdditionalCounts == null) return 0;
            long total = 0;
            foreach (KeyValuePair<string, long> count in usage.AdditionalCounts)
            {
                string key = count.Key ?? "";
                bool matches = requested == "completion_tokens_details.reasoning_tokens"
                    ? key.Equals("reasoning_tokens", StringComparison.OrdinalIgnoreCase) ||
                      key.EndsWith(".reasoning_tokens", StringComparison.OrdinalIgnoreCase)
                    : key.IndexOf("cache", StringComparison.OrdinalIgnoreCase) >= 0 &&
                      (requested == "prompt_tokens_details.cached_tokens"
                          ? key.IndexOf("read", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("cached", StringComparison.OrdinalIgnoreCase) >= 0
                          : key.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("creation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            key.IndexOf("create", StringComparison.OrdinalIgnoreCase) >= 0);
                if (matches) total = Math.Min(int.MaxValue, total + Math.Min(int.MaxValue, Math.Max(0, count.Value)));
            }
            return total;
        }

        private void Warn(string message)
        {
            try { _log.Warn(message, LogTag.Llm); }
            catch { }
        }

        private static void Cancel(CancellationTokenSource source)
        {
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(GatewayChatClient));
        }
    }
}
#endif
