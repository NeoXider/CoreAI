# Server-Managed LLM Protocol Specification

**Version:** 1.0 (Draft)
**Date:** 2026-05-03
**Goal:** Define the contract between a CoreAI WebGL client and a custom backend proxy.

## 1. Endpoint

`POST /chat/completions` (relative to `ApiBaseUrl`).

## 2. Request Headers

| Header | Required | Description |
|---|---|---|
| `Authorization` | Yes | Bearer token or dynamic header from `ServerManagedAuthorization`. |
| `Content-Type` | Yes | `application/json`. |
| `X-Tenant-Id` | No | Tenant identifier (from `ILlmAuthContextProvider.TenantId`). |
| `X-User-Id` | No | User identifier (from `ILlmAuthContextProvider.UserId`). |
| `X-Session-Id` | No | Session identifier (from `ILlmAuthContextProvider.SessionId`). |
| `X-Request-Id` | Yes | Unique request identifier (UUID, matches `traceId`). Used for logging. |
| `Idempotency-Key` | Yes | Stable key for the logical request. Reused across HTTP retries. Populated from **`LlmCompletionRequest.IdempotencyKey`** (auto-assigned once per request instance if empty). |
| `X-Coreai-Role` | No | Agent role ID (e.g., `SmartChat`, `Teacher`). |
| `X-Coreai-Client` | No | Client version (e.g., `1.6.0`). |

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
| 401 | `AuthExpired` | JWT invalid/expired. Client triggers `RefreshOnUnauthorizedDecorator`. |
| 409 | `QuotaExceeded` | `quota_exceeded` in body. User quota reached. |
| 429 | `RateLimited` | Rate limit hit. Check `Retry-After` header. |
| 500+ | `BackendUnavailable` | Server error. Client may retry if idempotency is guaranteed. |

Error Body Example:
```json
{"error": {"message": "quota exceeded", "type": "quota_exceeded"}}
```

## 5. Idempotency

When the client sends `Idempotency-Key: <key>`:
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
- [ ] Header parsing (`X-Tenant-Id`, `X-User-Id`, `Idempotency-Key`).
- [ ] Idempotency store (Redis/InMemory with TTL).
- [ ] SSE pass-through with `Transfer-Encoding: chunked`.
- [ ] Error mapping (401, 409, 429).
- [ ] CORS headers if cross-origin:
  `Access-Control-Allow-Origin: <origin>`
  `Access-Control-Allow-Headers: Authorization, Content-Type, X-Request-Id, Idempotency-Key, X-Tenant-Id, X-Session-Id`
