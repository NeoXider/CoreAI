#if COREAI_LLM
#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm.Gateway
{
    /// <summary>Explicit scoped identity; null intentionally disables legacy global identity lookup.</summary>
    public interface IOpenAiAuthContextSettings
    {
        ILlmAuthContextProvider? AuthContextProvider { get; }
    }

    /// <summary>Scopes dynamic auth and logical-request headers without changing endpoint settings.</summary>
    public sealed class GatewayAuthorizationSettings : IOpenAiHttpSettings, IOpenAiAuthContextSettings
    {
        private readonly IOpenAiHttpSettings _inner;
        private readonly SnapshotHeaderProvider _headers;

        public GatewayAuthorizationSettings(IOpenAiHttpSettings inner,
            ILlmAuthContextProvider? authContextProvider, IRequestHeaderProvider? headerProvider = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (!string.IsNullOrWhiteSpace(inner.ApiKey))
            {
                throw new ArgumentException("Gateway settings must not contain a provider API key.", nameof(inner));
            }

            AuthContextProvider = authContextProvider;
            _headers = new SnapshotHeaderProvider(inner.HeaderProvider, headerProvider, () => inner.Model);
        }

        public ILlmAuthContextProvider? AuthContextProvider { get; }
        public string ApiBaseUrl => _inner.ApiBaseUrl;
        public string ApiKey => "";
        public string AuthorizationHeader => !string.IsNullOrWhiteSpace(_inner.AuthorizationHeader)
            ? _inner.AuthorizationHeader : AuthContextProvider?.GetAuthorizationHeader() ?? "";
        public LlmExecutionMode ExecutionMode => LlmExecutionMode.ServerManagedApi;
        public string Model => _headers.ActiveModel ?? _inner.Model;
        public float Temperature => _inner.Temperature;
        public float? TemperatureOverride => _inner.TemperatureOverride;
        public int RequestTimeoutSeconds => _inner.RequestTimeoutSeconds;
        public int? ResponseWaitTimeoutSeconds => _inner.ResponseWaitTimeoutSeconds;
        public int MaxTokens => _inner.MaxTokens;
        public int? MaxTokensOverride => _inner.MaxTokensOverride;
        public LlmReasoningEffort ReasoningEffort => _inner.ReasoningEffort;
        public LlmReasoningDialect ReasoningDialect => _inner.ReasoningDialect;
        public string ExtraBodyJson => _inner.ExtraBodyJson;
        public LlmReasoningMode ReasoningMode => _inner.ReasoningMode;
        public int ThinkingBudgetTokens => _inner.ThinkingBudgetTokens;
        public bool LogLlmInput => _inner.LogLlmInput;
        public bool LogLlmOutput => _inner.LogLlmOutput;
        public bool EnableHttpDebugLogging => _inner.EnableHttpDebugLogging;
        public IRequestHeaderProvider HeaderProvider => _headers;

        /// <summary>Captures custom headers once; auth remains dynamic across a credential refresh.</summary>
        public IDisposable BeginRequest(GatewayRequestContext context) => _headers.Begin(context);

        private sealed class SnapshotHeaderProvider : IRequestHeaderProvider
        {
            private readonly IRequestHeaderProvider? _endpoint;
            private readonly IRequestHeaderProvider? _host;
            private readonly Func<string> _model;
            private readonly AsyncLocal<HeaderSnapshot?> _current = new AsyncLocal<HeaderSnapshot?>();
            private static readonly HashSet<string> Reserved = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "Authorization", "Content-Type", "Idempotency-Key", "X-Request-Id"
            };

            public SnapshotHeaderProvider(IRequestHeaderProvider? endpoint, IRequestHeaderProvider? host,
                Func<string> model)
            {
                _endpoint = endpoint;
                _host = host;
                _model = model;
            }

            public string? ActiveModel => _current.Value?.Model;

            public IReadOnlyList<KeyValuePair<string, string>> GetHeaders() =>
                RequireSnapshot().Headers;
            public string IdempotencyKey => RequireSnapshot().IdempotencyKey;
            public string RequestId => RequireSnapshot().RequestId;

            public IDisposable Begin(GatewayRequestContext context)
            {
                if (context == null) throw new ArgumentNullException(nameof(context));
                HeaderSnapshot? previous = _current.Value;
                ProviderSnapshot endpoint = Capture(_endpoint);
                ProviderSnapshot host = ReferenceEquals(_endpoint, _host) ? endpoint : Capture(_host);
                List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
                Add(headers, endpoint.Headers);
                if (!ReferenceEquals(_endpoint, _host)) Add(headers, host.Headers);
                _current.Value = new HeaderSnapshot(headers.AsReadOnly(),
                    First(context.Request.IdempotencyKey, endpoint.IdempotencyKey, host.IdempotencyKey),
                    First(context.Request.TraceId, endpoint.RequestId, host.RequestId), (_model() ?? "").Trim());
                return new RestoreScope(this, previous);
            }

            private static ProviderSnapshot Capture(IRequestHeaderProvider? source) => source == null
                ? new ProviderSnapshot(null, "", "")
                : new ProviderSnapshot(source.GetHeaders(), source.IdempotencyKey, source.RequestId);

            private HeaderSnapshot RequireSnapshot() => _current.Value ??
                throw new InvalidOperationException("Gateway headers require an active logical request.");

            private static string First(string? first, string? second, string? third) =>
                !string.IsNullOrWhiteSpace(first) ? first :
                !string.IsNullOrWhiteSpace(second) ? second :
                !string.IsNullOrWhiteSpace(third) ? third : Guid.NewGuid().ToString("N");

            private static void Add(List<KeyValuePair<string, string>> destination,
                IReadOnlyList<KeyValuePair<string, string>>? source)
            {
                if (source == null) return;
                foreach (KeyValuePair<string, string> header in source)
                {
                    string name = header.Key?.Trim() ?? "";
                    if (name.Length == 0 || Reserved.Contains(name)) continue;
                    bool duplicate = destination.Exists(existing =>
                        string.Equals(existing.Key, name, StringComparison.OrdinalIgnoreCase));
                    if (!duplicate) destination.Add(new KeyValuePair<string, string>(name, header.Value ?? ""));
                }
            }

            private sealed class HeaderSnapshot
            {
                public HeaderSnapshot(IReadOnlyList<KeyValuePair<string, string>> headers,
                    string idempotencyKey, string requestId, string model)
                {
                    Headers = headers;
                    IdempotencyKey = idempotencyKey;
                    RequestId = requestId;
                    Model = model;
                }
                public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }
                public string IdempotencyKey { get; }
                public string RequestId { get; }
                public string Model { get; }
            }

            private sealed class ProviderSnapshot
            {
                public ProviderSnapshot(IReadOnlyList<KeyValuePair<string, string>>? headers,
                    string idempotencyKey, string requestId)
                {
                    Headers = headers;
                    IdempotencyKey = idempotencyKey;
                    RequestId = requestId;
                }
                public IReadOnlyList<KeyValuePair<string, string>>? Headers { get; }
                public string IdempotencyKey { get; }
                public string RequestId { get; }
            }

            private sealed class RestoreScope : IDisposable
            {
                private readonly SnapshotHeaderProvider _owner;
                private readonly HeaderSnapshot? _previous;
                private int _disposed;
                public RestoreScope(SnapshotHeaderProvider owner, HeaderSnapshot? previous)
                {
                    _owner = owner;
                    _previous = previous;
                }
                public void Dispose()
                {
                    if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner._current.Value = _previous;
                }
            }
        }
    }
}
#endif
