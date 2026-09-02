# G11 test proxy

A stdlib-only OpenAI-compatible forwarding proxy for the **G11 WebGL browser acceptance**
(`dev-docs/MVP2_MULTIPLAYER_PLAN.md` §6.5 — *Retry, terminal failure, and recovery*).

It sits between the served WebGL player and LM Studio and provides the three things the
browser run needs and LM Studio does not give you:

- **Browser CORS** on every response, including the injected errors.
- **Real-time SSE passthrough** — `text/event-stream` frames are relayed chunk by chunk, never
  buffered, so token streaming and the "canvas stays responsive while the request is pending"
  assertion behave exactly as they do against the real endpoint.
- **Deterministic fault injection** — a retryable `503`, a persistent block, or a hang that
  outlives the client's outer timeout, all driven from `curl` while the browser stays open.

No pip packages. Python 3 standard library only (developed and tested on Python 3.14.5).

---

## Start it

```
python D:\Git\CoreAI\tools\G11Proxy\g11_proxy.py --port 8811 --upstream http://127.0.0.1:1234/v1 --log-file D:\Git\CoreAI\artifacts\g11-proxy.log
```

Defaults are `--host 127.0.0.1 --port 8811 --upstream http://127.0.0.1:1234/v1`, so the short
form is enough when LM Studio is on its default port:

```
python D:\Git\CoreAI\tools\G11Proxy\g11_proxy.py
```

| Flag | Default | Meaning |
| --- | --- | --- |
| `--host` | `127.0.0.1` | bind address |
| `--port` | `8811` | bind port (`0` = ephemeral, used by the tests) |
| `--upstream` | `http://127.0.0.1:1234/v1` | upstream base URL; its path replaces the incoming `/v1` prefix |
| `--log-file` | *(none)* | append the stdout request log to this file as well |
| `--upstream-timeout` | `300` | upstream socket timeout in seconds (must exceed the player's own timeout) |

Confirm it is alive and pointed at the right upstream:

```
curl -s http://127.0.0.1:8811/health
{"ok": true, "upstream": "http://127.0.0.1:1234/v1"}
```

Stop it with Ctrl+C.

---

## Point the CoreAI player at it

The player reads `apiBaseUrl` from `Assets/Resources/CoreAISettings.asset`. **This tool does not
touch that asset** — the orchestrator sets it before running
`CoreAI.Editor.CoreAIG11WebGlBuild.Build`:

```
apiBaseUrl: http://127.0.0.1:8811/v1
```

Everything else in the asset stays as-is. The values that matter for §6.5, as currently committed:

| Field | Value | Why it matters |
| --- | --- | --- |
| `requestTimeoutSeconds` | `120` | the "outer timeout + 5 s" budget in §6.5 is **125 s** |
| `maxLlmRequestRetries` | `3` | one injected `503` is retried automatically |
| `modelName` | committed `qwen3.5-4b-mtp`; working tree currently `ling-3.0-tiny` | must be a model LM Studio can actually load (see *Limitations*) |

Two details the proxy exists to handle:

- `UnityWebRequestOpenAiReadinessProbe` probes **`GET /v1/models` first**, then falls back to
  `POST /v1/chat/completions`. Both are forwarded. Because the probe is a real `/v1` request, use
  the `path` filter on `fail-next` (below) when you want the injected failure to land on the chat
  turn and not on a probe.
- `MeaiOpenAiChatClient.IsRetryableHttpStatus` treats **408, 429 and every 5xx** as retryable, so
  `503` is the correct status for the "one retryable failure" step and *not* for the terminal-error
  step (that one uses a persistent block, which exhausts the retries).

The proxy binds `127.0.0.1`, so serve the player from the same machine. A player served from
another host needs `--host 0.0.0.0` and a matching `apiBaseUrl`.

---

## Control endpoints

All control endpoints take and return JSON. Every response — control, proxied, or injected —
carries the CORS headers.

### `GET /health`

```
curl -s http://127.0.0.1:8811/health
```

### `POST /control/fail-next` — the next N requests fail

```
curl -s -X POST http://127.0.0.1:8811/control/fail-next ^
  -H "Content-Type: application/json" ^
  -d "{\"count\": 1, \"status\": 503, \"path\": \"chat/completions\"}"
```

| Field | Default | Meaning |
| --- | --- | --- |
| `count` | `1` | how many matching `/v1` requests to fail (replaces any previous value) |
| `status` | `503` | status to return |
| `body` | injected-error JSON | response body; a string starting with `{`/`[` is sent as `application/json`, anything else as `text/plain` |
| `path` | `"/v1"` | substring filter — `"/v1"` means every proxied request; `"chat/completions"` spares the readiness probe |

Upstream is not contacted for an injected request.

### `POST /control/block` — fail everything until turned off

```
curl -s -X POST http://127.0.0.1:8811/control/block -H "Content-Type: application/json" -d "{\"enabled\": true}"
curl -s -X POST http://127.0.0.1:8811/control/block -H "Content-Type: application/json" -d "{\"enabled\": false}"
```

`enabled` defaults to `true` when the body is empty; `status` defaults to `503`.

### `POST /control/hang` — hold the next request, then fail

```
curl -s -X POST http://127.0.0.1:8811/control/hang -H "Content-Type: application/json" -d "{\"seconds\": 130}"
```

One-shot: the next `/v1` request is held that long and then answered `503`. Use a value above
`requestTimeoutSeconds` (120) to exercise the player's own outer timeout rather than the proxy's
error path. `seconds` defaults to `5`.

### `POST /control/reset` — clear every injection

```
curl -s -X POST http://127.0.0.1:8811/control/reset
curl -s -X POST http://127.0.0.1:8811/control/reset -H "Content-Type: application/json" -d "{\"counters\": true}"
```

Injections are always cleared. Counters are **kept** so the run record survives, unless you pass
`{"counters": true}`.

### `GET /control/state` — counters and current settings

```
curl -s http://127.0.0.1:8811/control/state
```

```json
{
  "ok": true,
  "counters": {
    "total_requests": 4,
    "total_proxied": 2,
    "injected_failures": 2,
    "upstream_errors": 0,
    "in_flight": 0
  },
  "last": {"method": "GET", "path": "/v1/models", "status": 200, "injected": null, "at": "..."},
  "settings": {
    "upstream": "http://127.0.0.1:1234/v1",
    "fail_next_remaining": 0,
    "fail_status": 503,
    "fail_path": "/v1",
    "blocked": false,
    "block_status": 503,
    "hang_seconds": 0.0
  }
}
```

`total_requests` counts every `/v1` request the proxy handled; `total_proxied` counts only the ones
actually forwarded upstream. The difference is `injected_failures`.

> PowerShell note: the `^` line continuations and `\"` escaping above are for `cmd.exe`. In
> PowerShell use backticks and `'{"count": 1}'` single-quoted JSON, or just call `curl.exe`.

---

## Request log

One line per request on stdout, and appended to `--log-file` when given:

```
2026-09-02T00:35:24.891Z POST /control/fail-next status=200 injected=control bytes=336 dur_ms=0.5 streamed=no
2026-09-02T00:35:25.032Z POST /v1/chat/completions status=503 injected=fail-next bytes=113 dur_ms=2.6 streamed=no
2026-09-02T00:35:47.564Z GET /v1/models status=200 injected=no bytes=1247 dur_ms=5.8 streamed=no
2026-09-02T00:37:14.850Z POST /v1/chat/completions status=200 injected=no bytes=6433 dur_ms=7892.2 streamed=yes
```

`injected` is `no` for a real forward, or `fail-next` / `block` / `hang` / `upstream-error` /
`preflight` / `control`. `streamed=yes` means the body was relayed frame by frame (SSE); attach this
log to the G11 run record next to the browser console and Network trace.

---

## The §6.5 sequence

Start the proxy, build and serve the player with `apiBaseUrl: http://127.0.0.1:8811/v1`, then keep
the browser open for all three steps — **no page reload is allowed between them**.

**1. One `503`, then success.** Arm a single retryable failure, then send a prompt whose expected
answer contains a fresh run nonce:

```
curl -s -X POST http://127.0.0.1:8811/control/fail-next -H "Content-Type: application/json" -d "{\"count\": 1, \"status\": 503, \"path\": \"chat/completions\"}"
```

Expected: the log shows `status=503 injected=fail-next` immediately followed by a real
`status=200 injected=no` for the same turn; the assistant bubble ends up containing the nonce; the
player stays responsive throughout. `/control/state` must show `injected_failures: 1` and
`total_proxied` incremented.

**2. Persistent block until a terminal error.** Send a new prompt with the block on:

```
curl -s -X POST http://127.0.0.1:8811/control/block -H "Content-Type: application/json" -d "{\"enabled\": true}"
```

Expected: every attempt is `status=503 injected=block`, upstream is never touched, and by
`requestTimeoutSeconds + 5` (**125 s** with the committed asset) the turn shows a visible terminal
error/timeout and the send controls are re-enabled. An indefinite busy state, controls that stay
disabled, or a retry that never resumes is a G11 failure.

Use `/control/hang` instead of `block` when you want to test the *outer timeout* path rather than
the retry-exhaustion path:

```
curl -s -X POST http://127.0.0.1:8811/control/hang -H "Content-Type: application/json" -d "{\"seconds\": 130}"
```

**3. Restore, then a new nonce without reloading.**

```
curl -s -X POST http://127.0.0.1:8811/control/reset
```

Expected: send a **new** nonce in the same page; it succeeds (`status=200 injected=no`,
`streamed=yes` for a streaming turn) and the bubble contains the new nonce. Needing a page reload to
recover is a G11 failure.

Capture `/control/state` at the end — non-zero `total_proxied` and `injected_failures` with
`upstream_errors: 0` and `in_flight: 0` is the proof that the run exercised the real path rather
than doing nothing.

---

## Tests

```
cd D:\Git\CoreAI
python tools\G11Proxy\test_g11_proxy.py
```

21 tests, no network and no LM Studio required: they spin up a fake upstream (delayed three-frame
SSE stream plus non-streaming JSON) and a proxy on ephemeral ports, and cover passthrough,
chunked request bodies, incremental streaming (asserting the client has frame 1 *before* upstream
sends the last frame), `fail-next`, `block`, `hang`, upstream-error `502`, CORS on preflight and on
injected errors, the counters, and the log-line format.

---

## Limitations

- **HTTP only, no TLS termination.** An `https://` *upstream* works; the proxy's own listener is
  plain HTTP, which is what a locally served WebGL player needs.
- **HTTP/1.1 keep-alive** is supported in both directions, but each proxied request opens a fresh
  upstream connection (`http.client`, closed in `finally`). Correct and simple; it costs one TCP
  handshake per request against localhost, which is irrelevant at G11's request rate.
- **No WebSocket / HTTP2 / `Upgrade`** — hop-by-hop headers are dropped, so an upgrade request will
  not be proxied. The OpenAI REST + SSE surface is all that is needed.
- **Response framing is re-decided.** A response with a `Content-Length` and a non-SSE content type
  is relayed buffered with that same length; everything else (SSE, chunked, or no length) is relayed
  as `Transfer-Encoding: chunked` frame by frame. Upstream `Access-Control-*` headers are stripped
  and replaced with the proxy's own, so the browser never sees a duplicated
  `Access-Control-Allow-Origin` (LM Studio does send one).
- **Trailers on a chunked request body are parsed and discarded.**
- **`OPTIONS` is answered locally** with `204` and never forwarded upstream.
- **Injection precedence is `hang` → `block` → `fail-next`**, and `hang`/`fail-next` are consumed
  per request. Concurrent in-flight requests therefore race for a `fail-next` credit; drive §6.5
  one turn at a time.
- **No auth.** Any `Authorization` header the player sends is forwarded verbatim; the proxy adds
  none. §6.5 requires no provider key in the player, so the header is normally absent.
- **Observed on this machine:** LM Studio's `GET /v1/models` and streaming completions proxy
  correctly end to end. The committed `qwen3.5-4b-mtp` fails to load
  (`LM Link connection entered error state peer_keepalive_timeout`) and returns HTTP 400 — a known
  local blocker unrelated to this proxy — while `ling-3.0-tiny` streamed a nonce back through the
  proxy in 26 separate relayed reads. Verify the configured model actually loads before starting the
  G11 run, or the nonce turn will fail for the wrong reason.
