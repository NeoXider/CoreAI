# Server-Managed LLM Protocol Specification

**Version:** 1.1 (Draft)
**Date:** 2026-08-01
**Goal:** Define the contract between a CoreAI client (WebGL player, desktop or Editor) and a custom backend proxy.

## 1. Endpoint

`POST /chat/completions` (relative to `ApiBaseUrl`).

## 2. Request Headers

| Header | Sent by desktop / Editor | Sent by a WebGL player | Description |
|---|---|---|---|
| `Authorization` | Yes | Yes | Bearer token or dynamic header from `ServerManagedAuthorization`. |
| `Content-Type` | Yes | Yes | `application/json`. |
| `X-Tenant-Id` | When set | **No** | Tenant identifier (from `ILlmAuthContextProvider.TenantId`). |
| `X-User-Id` | When set | **No** | User identifier (from `ILlmAuthContextProvider.UserId`). |
| `X-Session-Id` | When set | **No** | Session identifier (from `ILlmAuthContextProvider.SessionId`). |
| `X-Request-Id` | When the request has a trace id | **No** | Unique request identifier (matches `traceId`). Used for logging. |
| `Idempotency-Key` | Yes (requests sent through `MeaiLlmClient`) | **No** | Stable key for the logical request. Reused across HTTP retries. Populated from **`LlmCompletionRequest.IdempotencyKey`** (auto-assigned once per request instance if empty). |
| `X-Coreai-Role` | When known | **No** | Agent role ID (e.g., `SmartChat`, `Teacher`). |
| Host-specific attribution header | No | Allowed | For example `X-MyGame-Lesson-Id`; supplied dynamically by the host and validated by the backend. |

**WebGL players omit the correlation headers.** In a WebGL player (`UNITY_WEBGL && !UNITY_EDITOR`)
`MeaiOpenAiChatClient` never sends `Idempotency-Key`, `X-Request-Id`, `X-Coreai-Role`, `X-Tenant-Id`,
`X-User-Id` or `X-Session-Id` — not even when an `IRequestHeaderProvider` supplies them — because
public gateways often leave them out of their CORS allow-list and the browser preflight would fail. A
backend that serves WebGL clients must therefore not require these headers: carry identity in the
`Authorization` token (or in a custom header of your own name from `IRequestHeaderProvider`), and do
not rely on `Idempotency-Key` for deduplication of browser traffic.

### 2.1. Dynamic product headers

The host may register an `IRequestHeaderProvider` via
`ServerManagedAuthorization.SetRequestHeaderProvider(...)`. Already created `ServerManagedLlmClient`
instances pick up the provider without rebuilding the client. Headers are captured once per invocation of
`CompleteAsync` / `CompleteStreamingAsync`: internal HTTP retries, the retry after JWT refresh, outer sync
retries after a retryable result/exception, and streaming pre-commit retries receive the same snapshot, while the next
invocation re-reads current values even if the host reuses
the same `LlmCompletionRequest` object. `ClearRequestHeaderProvider()` removes only this hook and does not reset
the Authorization provider/refresher; call it separately on logout and in integration-test `TearDown`.

If `IOpenAiHttpSettings.HeaderProvider` is also set, its values take precedence, while the global
ServerManaged provider fills in missing names. A custom provider cannot override transport-owned
`Authorization`, `Content-Type`, `Idempotency-Key` and `X-Request-Id`; its same-named entries and properties
are ignored. Trace and idempotency still come from `LlmCompletionRequest`/`LlmRequestContext`.

The backend must validate a host-specific value in the context of the authenticated user. For example,
`X-MyGame-Lesson-Id` may be used for cost attribution only after verifying that the lesson exists
and is available to the current user; a header from a WebGL client must not be treated as a trusted billing source.
For cross-origin WebGL, add the exact custom header name to `Access-Control-Allow-Headers`.

## 3. Request Body (JSON)

Standard OpenAI-compatible payload:

```json
{
  "model": "gpt-4o",
  "messages": [ ... ],
  "stream": true,
  "temperature": 0.7,
  "max_tokens": 1024,
  "tools": [ ... ]
}
```

> Since 5.9.0, `temperature` and `max_tokens` are only present when their respective overrides are enabled
> on `CoreAISettingsAsset` (both OFF by default). When an override is off the key is omitted entirely and the
> provider chooses its own value — the server must not assume either field is always sent.

## 4. Response

### 4.1. Success (200 OK)

If `stream: false`:
Standard JSON `{"choices": [{"message": {...}}], "usage": {...}}`.

If `stream: true`:
`Content-Type: text/event-stream`.
SSE format:
```
data: {"choices":[{"delta":{"content":"Hello"}}]}\n\n
data: {"choices":[{"delta":{"content":" world"}}]}\n\n
data: [DONE]\n\n
```

**Backend requirements for SSE:**
- Set `X-Accel-Buffering: no` (nginx) or equivalent to disable proxy buffering.
- Set `Cache-Control: no-cache`.
- **Disable gzip** for `text/event-stream` to ensure incremental delivery.

### 4.2. Errors

| HTTP Status | `LlmErrorCode` (Client) | Description |
|---|---|---|
| 401 / 403 | `AuthExpired` | JWT invalid/expired. Client triggers `RefreshOnUnauthorizedDecorator`. |
| 402 | `PaymentRequired` | Account out of credit. Never retried; degrades the endpoint's health. |
| 409 | `QuotaExceeded` | `quota_exceeded` in body. User quota reached. |
| 429 | `RateLimited` | Rate limit hit. Check `Retry-After` header. |
| 500+ | `BackendUnavailable` | Server error. Client may retry if idempotency is guaranteed. |

Error Body Example:
```json
{"error": {"message": "quota exceeded", "type": "quota_exceeded"}}
```

## 5. Idempotency

When the client sends `Idempotency-Key: <key>` (desktop and Editor clients; a WebGL player does not send it):
1. If the backend has a stored response for this key (TTL 24h), return it immediately (even on retry).
2. Otherwise, process the request and store the response mapped to the key.

**Important:** For streaming requests, the idempotency key typically applies to the *initiation*. If a stream fails mid-way, the client may retry with the same key, expecting the backend to either resume or return the cached full response.

## 6. Same-Origin Deployment

If `ApiBaseUrl` starts with `/` (e.g., `/api/llm/v1`):
- The client resolves it against `Application.absoluteURL` (e.g., `https://game.example.com/api/llm/v1`).
- CORS is not required if the backend is on the same host.
- Use `credentials: 'same-origin'` in fetch if using session cookies.

## 7. Minimal Backend Checklist (for Free users)

To implement a basic compliant backend, ensure:
- [ ] JWT Validation (JWKS or static secret).
- [ ] Header parsing (`X-Tenant-Id`, `X-User-Id`, `Idempotency-Key`, allowed host-specific headers) — all optional, since a WebGL player sends none of the correlation headers.
- [ ] Idempotency store (Redis/InMemory with TTL).
- [ ] SSE pass-through with `Transfer-Encoding: chunked`.
- [ ] Error mapping (401, 409, 429).
- [ ] CORS headers if cross-origin:
  `Access-Control-Allow-Origin: <origin>`
  `Access-Control-Allow-Headers: Authorization, Content-Type, <your-custom-header>` (CORS applies only to
  browser clients, and a WebGL player sends none of the correlation headers).
