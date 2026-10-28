# CoreAI Gateway Client

Gateway Client composes server-managed OpenAI-compatible requests in a plain .NET host or a Unity game. It is a client bridge. You provide the gateway server, authentication, authorization, billing and account policy. No server implementation or game-specific account system is supplied.

The portable implementation is in CoreAI.Core under CoreAI.Infrastructure.Llm.Gateway. It uses MEAI IChatClient, Task, CancellationToken and existing CoreAI HTTP/SSE code. Unity adds scoped composition and a bounded UnityWebRequest adapter for optional side calls. No eighth UPM package, new LLM execution mode or protocol parser is needed.

## Plain .NET composition

Reference [tools/portable/CoreAI.Core.csproj](../../tools/portable/CoreAI.Core.csproj) or its
netstandard2.1 DLL with its dependencies. The portable project enables the required `COREAI_LLM`
compilation path. A .NET 8 executable can use the same client as Unity:

~~~csharp
OpenAiHttpOptions endpoint = new OpenAiHttpOptions
{
    ApiBaseUrl = "https://your-gateway.example/v1",
    ExecutionMode = LlmExecutionMode.ServerManagedApi,
    ApiKey = "",
    Model = "",
    LogLlmInput = false,
    LogLlmOutput = false
};
using GatewayChatClient client = GatewayClientFactory.Create(
    endpoint,
    new HttpClientOpenAiTransport(),
    authContextProvider: hostAuthContext,
    authRefresher: hostAuthRefresher);
ChatResponse response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
~~~

The host supplies ILlmAuthContextProvider: a dynamic full authorization header plus optional tenant/user/session metadata. It may return an empty header for a server that does not require one. An omitted/null scoped auth provider explicitly suppresses legacy global identity lookup. A nonempty explicit endpoint AuthorizationHeader retains priority. Provider ApiKey must be empty on this gateway path. Tokens are neither persisted into assets nor printed by this feature.

Model may remain empty; existing ServerManagedApi wire behavior omits it so the server chooses. HTTPS is the default. GatewayClientOptions.AllowInsecureHttp = true explicitly permits a host-selected HTTP endpoint, including LAN/private gateways. The host owns transport confidentiality and endpoint trust; URI validation does not establish either. This opt-in does not change legacy direct-provider HTTP clients.

IGatewayAuthRefresher.RefreshAsync is host-owned. It updates the same scoped token source and returns true to authorize one safe retry of the raw request. It must coordinate concurrent refresh calls and cancellation according to the host's auth policy. The feature does not mint tokens, create identities or implement refresh-token exchanges.

The factory accepts caller-owned completion/service transports, policy, sink, logger, marshaler, request-context factory and optional hostLifetime CancellationToken. Injected providers/transports are not disposed unless an explicit owned resource is created internally. Dispose the returned middleware; cancellation of hostLifetime stops its active requests and optional reports. Dispose your injected transports at your owning scope.

## Call context and replaceable policy

GatewayRequestContext bundles the existing LlmCompletionRequest and LlmRouteProfile. The contextFactory receives the actual once-materialized MEAI messages and options. Its default derives role/actor/trace/idempotency from LlmRequestContext, generates missing correlation ids, and retains actual ChatHistory. It never extracts identity or authorization from a prompt.

~~~csharp
GatewayChatClient client = GatewayClientFactory.Create(endpoint, transport, options,
    authContextProvider: hostAuth,
    contextFactory: (actualMessages, chatOptions) =>
        new GatewayRequestContext(
            new LlmCompletionRequest
            {
                AgentRoleId = "assistant",
                TraceId = Guid.NewGuid().ToString("N")
            },
            new LlmRouteProfile
            {
                ProfileId = "host-relay",
                Mode = LlmExecutionMode.ServerManagedApi
            }));
~~~

Custom metadata/header/idempotency providers are captured once per logical raw invocation; retries reuse that snapshot. Explicit call or ambient LlmRequestContext IDs win, followed by captured endpoint/host provider IDs, then fresh generated IDs. A shared endpoint/host provider instance is sampled once. A later invocation gets a fresh snapshot, even if a host reuses a request/profile object; invocation-owned copies never retain selected/generated IDs or rewrite that caller object. Authorization remains dynamic across an approved refresh. Authorization, Content-Type, Idempotency-Key and X-Request-Id are reserved from custom headers. Existing CORS header behavior remains in the HTTP client. Client tenant/user/role/correlation headers are metadata, not proof of server authority.

Preflight sees the model actually configured on the wire settings, including with an explicit host context factory. ChatOptions.ModelId does not override that existing transport choice. The model is frozen with the invocation header frame before policy awaits, so a configuration change during preflight applies to the next invocation and cannot change the already approved request. Empty configured model remains unknown/backend-selected. Host-owned request/profile objects are copied before normalization.

Optional client preflight and usage reporting are disabled by default. A plain OpenAI-compatible relay needs no extra endpoints. To opt in:

~~~csharp
GatewayClientOptions options = new GatewayClientOptions
{
    GatewayBaseUrl = "https://your-gateway.example/v1",
    EnableClientPreflight = true,
    ReportUsageToGateway = true,
    PreflightUnavailablePolicy = GatewayPreflightUnavailablePolicy.Allow,
    SideCallTimeoutMilliseconds = 15000,
    UsageTimeoutMilliseconds = 2000,
    MaxConcurrentUsageReports = 4,
    MaxResponseBytes = 65536
};
~~~

Use the optional v1 service adapter, or inject ILlmEntitlementPolicy, ILlmUsageSink or IGatewayHttpTransport. Existing portable policy/sink interfaces are consumed by the middleware around actual sends, independent of Unity or MessagePipe. Disabled services make zero side calls. Policy receives the actual history and resolved host context.

A valid explicit deny prevents completion dispatch and surfaces a non-retryable typed failure: an actual quota reason keeps QuotaExceeded, known permanent auth/model errors keep their existing category, and unspecified/unknown/transient-looking denial reasons use PermanentProviderError. Bare allowed=false never fabricates quota exhaustion. This prevents an outer fallback provider from bypassing the denial. Original reason_code/message/retry_after_seconds remain separately in ProviderErrorBody; the exception message is generic. Unavailable or malformed preflight responses follow PreflightUnavailablePolicy, default Allow for a UX-only check while the server remains authoritative. Deny availability mode surfaces BackendUnavailable, distinct from a verified business denial. Direct GatewayEntitlementPolicy callers receive that exception for unavailable Deny mode. Cancellation before/during preflight propagates and always prevents dispatch; it is never converted to an allowance.

## Optional service protocol v1

GatewayServiceClient uses `GET <GatewayBaseUrl>/entitlement?role_id=<escaped role>`. A valid JSON response must contain a boolean allowed. Optional fields consumed are reason_code, message and retry_after_seconds. Invalid/missing allowed, malformed JSON, transport failure or non-success HTTP status is unavailable, not a verified allow. Status/Retry-After remain available to the policy path. Service endpoint paths are configurable same-base paths, without an extra query/fragment.

Usage uses `POST <GatewayBaseUrl>/usage` with only the source-supported snake_case fields: trace_id, role_id, routing_profile_id, model, prompt_tokens, completion_tokens, total_tokens, streaming, success and estimated. No speculative backend field is added. Only provider-measured usage creates reports, so zero measured tokens remain estimated=false; missing measurement is not fabricated as zero usage. A custom sink receives existing reasoning/cache counters. The v1 wire adapter intentionally does not invent fields for them.

Service responses have a UTF-8 byte cap, finite timeout and explicit redirect policy. Default HttpClient service transport disables redirects and reuses its client. Injected HttpClient instances must disable automatic redirects; the host owns that configuration. Unity's service adapter disables redirects, caps its download storage and uses a player-loop deadline. The complete `HttpClientGatewayTransport` implementation is excluded in a WebGL player; the Unity binding supplies `UnityWebRequestGatewayTransport` there, and a custom WebGL host supplies its own `IGatewayHttpTransport`.

Telemetry is optional and non-authoritative. It is directly awaited under a configurable finite deadline with nonblocking bounded admission. Failure, timeout or saturation never replaces a successful primary response; diagnostic messages contain no token/body/exception text. An enabled report can add up to the configured wait latency. There is no queue, scheduler or automatic usage POST retry.

If an injected sink ignores cancellation, a bounded cleanup observer owns and observes its task; its admission slot is retained until completion, preventing repeated timed-out calls from growing unbounded work. A host must still make custom services cancellation-cooperative. The accounting unit is one raw model invocation/roundtrip; a buffered tool turn can contain several reports. Unity aggregate usage events remain available for diagnostics but are not forwarded a second time by this feature.

## Streaming, tools, errors and cancellation

Buffered and streamed requests reuse MeaiOpenAiChatClient and existing IOpenAiHttpTransport. Vision image parts, native tool schemas/calls/results, served-model metadata, SSE decoding, typed HTTP errors, correlation, retries and response disposal retain the existing protocol implementation.

Compose SmartToolCallingChatClient outside GatewayChatClient to execute buffered tools, as examples/dotnet does. Portable GetStreamingResponseAsync forwards native tool calls without executing them; the caller owns streaming tool execution. This feature does not claim parity with the Unity execute-as-you-stream loop.

Auth refresh happens at the raw model boundary, inside the outer tool loop. It cannot replay an entire tool turn. Stream replay stops once visible or native tool content has been emitted. Missing/failed refresh stays typed; refresh is attempted at most once. Early enumeration disposal releases the current stream. Caller cancellation, hostLifetime cancellation and client disposal stop owned operations; custom provider/sink cleanup remains bounded and observed.

Existing completion status mapping remains: 401/403 AuthExpired, 409/quota QuotaExceeded, 402 PaymentRequired unless quota, 429 RateLimited. Existing Retry-After/CORS behavior stays in the transport. Backend policy always controls the real authorization/billing decision.

## Unity composition

Install in the same declarative owning scope that builds the LLM pipeline:

~~~csharp
builder.RegisterCoreAiGatewayClient(
    new GatewayClientOptions(),
    auth: hostAuthContext,
    headers: hostHeaders,
    refresher: hostRefresher);
~~~

RegisterCoreAiGatewayClient installs an IUnityGatewayClientBinding only when the host has not already supplied a replacement. It does not shadow the host's auth/policy/sink services or install a production token stub. Both the primary server route and registry server profiles use the binding. CreateClient transfers ownership of each newly created gateway lifetime to its calling route owner. Primary replacement, manifest replacement/disable, runtime endpoint retirement and registry disposal close those explicit owned leaves promptly, including across a fallback wrapper; admitted requests are cancelled and stale owned handles are refused. Leaf disposal releases its linked host registration rather than waiting for whole-scope teardown. The original public Meai/ServerManaged constructors and arbitrary clients passed as borrowed legacy fallbacks do not transfer ownership.

Scope disposal marks the binding unavailable then cancels remaining leaves through a BCL lifetime token, without retaining an expanding client list. Caller-owned providers, completion/service transports and injected HttpClient remain borrowed. Explicitly owned HttpClient service transport cleanup is attempted even when a cancellation callback throws; repeated disposal is idempotent and borrowed clients remain usable.

Existing ServerManagedAuthorization/static hooks and original constructors remain the legacy fallback when the new binding is omitted. UnityGatewayAuthorizationAdapter can adapt existing IServerManagedAuthProvider/IServerManagedAuthRefresher implementations into the explicit scoped path. There is no automatic global auth replacement or network warmup. New middleware does not also stack the legacy Source auth-refresh wrapper or duplicate usage forwarding.

Unity keeps the existing completion transport choice: HttpClient outside WebGL, or fetch-SSE plus UnityWebRequest composite/native-streaming configuration in WebGL. Without native SSE, existing simulated updates follow a buffered completion. Main-thread service calls and deadlines use the installed Unity marshaler; no ThreadPool/blocking wait is required. Browser CORS, cookies/custom headers, built-player cancellation and repeated no-domain-reload sessions require actual platform verification.

Existing production/Resources provider-key build guards remain in force. No scene, prefab, settings asset, game networking topology or account/business module is required.

## Integration checks

Use the [portable console sample](../../examples/dotnet/README.md) to compose the client with
buffered tool execution. Test the host's actual gateway for authentication refresh, denied
entitlement, cancellation and streamed tool-call handling. A fake transport verifies protocol
behavior; it does not establish live gateway, player, IL2CPP or browser behavior. For WebGL,
check browser CORS, allowed headers, cancellation and scene/scope teardown in the built player.

## Source provenance

The optional v1 side-call behavior was adapted from the owner's Neoxider CoreAiPro 0.1.0 in RedoSchool, source HEAD f69a0e38624cb91be4b7a50bb0908163f6777622 and package tree 52ba740d6de5df1830594cb2d4bd0642333e9843. No RedoSchool login/session/student/lesson adapter or configuration was copied. Portable scoped middleware and host-neutral composition reuse CoreAI framework contracts. Neoxider, the copyright holder of CoreAiPro, relicensed the adapted material for publication in this repository. It is distributed under the repository license, PolyForm Small Business 1.0.0 (see [LICENSE](../../LICENSE)), like the rest of CoreAI. The proprietary CoreAiPro notice does not apply to the material published here.
