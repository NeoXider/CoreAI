# Changelog

## [Unreleased]

## 5.4.0 - MoonSharp removed; Lua-CSharp is the only VM (2026-07-10)

- **MoonSharp fully removed — Lua-CSharp is now the single Lua runtime.** The legacy MoonSharp VM and its
  entire binding/sandbox/runtime layer are deleted; the managed, AOT-safe Lua-CSharp stack (already the
  DI-registered runtime for mods, world, hierarchy/components, input, time, logic slots, coroutines) is the
  only VM. The `org.moonsharp.moonsharp` package dependency is gone and the Lua VM (`Lua.dll` +
  `Lua.Annotations.dll`) now ships bundled inside the CoreAI Mods package — no external Lua package to
  install. The `COREAI_HAS_MOONSHARP` scripting define no longer exists; Lua is compiled in by default and
  `COREAI_NO_LUA` still compiles it out.
- Dead `#if COREAI_HAS_MOONSHARP` blocks removed from CoreAI.Source (`CorePortableInstaller`,
  `AiGameCommandRouter`, `CoreAILifetimeScope`, `CoreAiChatExternalDriver.RunLuaDiag`). The Programmer agent
  system prompt now names the Lua-CSharp sandbox instead of MoonSharp.
- **Benchmark G6 image-feedback mode.** The free-build visual can now run with a `off` / `image` / `both`
  vision mode (benchmark-window "Vision feedback" dropdown or `COREAI_BENCHMARK_VISION_MODE`). In `image`,
  the model additionally gets the `camera` tool so it can `camera_capture` a screenshot of its own
  work-in-progress, judge it, and refine — the "look at what you made and fix it" loop; `both` runs the
  text-only and image-feedback builds side by side. Grading is inherited unchanged so the two are directly
  comparable; the camera tool is null-safe and degrades to a text-only build for non-vision models.
- **Speed probe fixed for a fair comparison.** `DirectVsAgent_Speed` now warms each configuration
  immediately before measuring it, so the reported TTFT reflects pipeline prefill cost rather than call
  order (previously the last-run, biggest-prompt role looked fastest because the server kept warming).

## 5.3.0 - benchmark v2 and resilience primitives (2026-07-10)

- **Benchmark suite v2 — new G8 described-state selection group.** `BenchmarkInfo.Version` bumped `v1 → v2`.
  Adds a group that gives the model a DESCRIBED, already-populated scene and grades acting on the named
  existing objects (clear only junk, raise only undersized towers via conditional selection, encode an
  observed rule as Lua) — the "director-AI / beyond the chat box" axis. Prompts state the goal, not the
  tool syntax, so weaker local models visibly fail the conditional-selection step. Scores are only
  comparable within a suite version; v2 starts a new leaderboard section.
- **Benchmark v2 — less hand-holding, more intelligence.** Reworded prompts that previously dictated the
  exact solution so the test measures understanding, not transcription: G6 castle no longer prescribes
  "four corner towers + walls + 24 objects" (every model just built that) — it now asks for a
  believable, detailed castle with a lived-in courtyard and surroundings, and grading rewards richness,
  variety and detail while treating tower/wall/gate/keep as non-mandatory castle *signals* (a keep-and-
  courtyard or asymmetric fort scores fine). G1 arena/coin-collector no longer dictate which primitive
  shape each object must be, and the coin-collector describes the Lua rules in words instead of pasting
  the function bodies — the model must choose shapes and derive the formulas itself. G5 (instruction
  discipline) and G2 (code-transcription baseline) keep their intentional strictness.
- **Circuit breaker for LLM backends** (`CircuitBreakerLlmClientDecorator`). After N consecutive
  TRANSIENT failures (timeout, rate-limit, backend-unavailable, provider/routing error) the breaker
  trips **open** and short-circuits calls with `BackendUnavailable` *without invoking the backend* — so a
  dead primary no longer costs `timeout × (retries+1)` every turn. After a cooldown it half-opens and
  admits one probe: success closes it, failure re-opens it. Caller-caused failures (auth, invalid
  request, context-length, cancellation) never trip it. Covers both `CompleteAsync` and the streaming
  path. Deterministic (injected monotonic clock); 6 EditMode tests. This is an opt-in public decorator;
  production composition/settings wiring remains tracked in `TODO.md`.
- Final release verification: 1,613 EditMode tests discovered (1,609 passed, 4 optional third-party
  ignored, 0 failed); PlayMode `FastNoLlm` 67/67; the local `qwen3.5-4b-mtp` full G1-G8 run scored
  88.1/100 and passed G8 3/3.

## 5.2.0 - stability gate and extension APIs (2026-07-10)

- Added the public `IContentFilter` extension point, passthrough implementation, and baseline
  word-list filter. The filter is host-wired by design; CoreAI does not claim automatic moderation.
- Hardened `ToolExecutionPolicy`: every state-mutating built-in shares one serialization policy;
  streamed mutations are deferred until the complete turn is known; whole-turn echoes are rejected
  before side effects; partial failures retry only the failed slots.
- Added focused regression coverage for streamed mutation replay, partial-success retries, and
  duplicate/error accounting.

## 5.1.0 - audit remediation: safe mutation pipeline, bounded queues/stores (2026-07-10)

- **Tool execution policy (F-01):** per-call duplicate signatures registered only on success (failed
  calls stay retryable), a single serialized mutation chain covering `world_command`,
  `component_command`, `execute_lua`, `call_skill_tool`, and streamed mutating calls deferred to
  turn completion so mutations never overlap and cross-turn echoes become structured
  `{ok, duplicate}` no-ops before side effects.
- **Orchestrator backpressure (F-10):** `AiOrchestrationQueueOptions.MaxPending` admission cap
  (default 64) with `AiOrchestrationQueueFullException`, binary-search insertion instead of
  per-enqueue re-sort, and a Dispose contract that cancels in-flight work and completes all
  pending tasks/streams.
- **Version retention (F-11):** new `VersionRetentionPolicy` bounds Lua-script and data-overlay
  version stores (original + current + last N intermediates, byte budget); revision `Index` is now
  stable across eviction and revert is index-based in both mod runtimes.
- **Audit chain (F-07):** `AuditEntry`/`AuditLogVerifier` support rotation markers and anchored
  genesis (`VerifyChainedSet`) so rotated files verify standalone while staying chained.
- This wave was driven by the 2026-07-10 repository audits (findings F-01…F-25 / A-01…A-06); those audit
  reports have since been removed and any remaining open findings are tracked in `TODO.md`.

## 5.0.10 - version lockstep with coreaiunity 5.0.10 (2026-07-06)

- No changes; released to keep both packages on identical versions. (The self-spawning model-download
  indicator lives entirely in the Unity layer.)

## 5.0.9 - version lockstep with coreaiunity 5.0.9 (2026-07-06)

- No changes; released to keep both packages on identical versions. (The LLMUnity host-configuration
  start-guard fix lives entirely in the Unity layer.)

## 5.0.8 - version lockstep with coreaiunity 5.0.8 (2026-07-05)

- No changes; released to keep both packages on identical versions. (The LLMUnity-as-OpenAI-server
  native tool-calling work lives entirely in the Unity layer.)

## 5.0.7 - version lockstep with coreaiunity 5.0.7 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.6 - version lockstep with coreaiunity 5.0.6 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.5 - version lockstep with coreaiunity 5.0.5 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.4 - version lockstep with coreaiunity 5.0.4 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.3 - version lockstep with coreaiunity 5.0.3 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.2 - version lockstep with coreaiunity 5.0.2 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.1 - skill teaches editing existing mods (2026-07-05)

- **Lua Modding skill**: explicit "improve an existing mod" workflow - `get_source` first, then
  `reload` with the FULL updated source; every reload stores a revision (`versions` / `revert`);
  `forget` = delete (unload + remove the persisted copy). Verified live: a 9B model reads,
  rewrites and reloads an existing mod and deletes one via `forget` from chat alone.

## 5.0.0 - on-demand skills for built-in roles; "Lua Modding" skill (2026-07-04)

- **`AgentMemoryPolicy.AddSkillForRole(roleId, skill)`** - attaches an on-demand skill catalog to
  ANY role (built-in or custom), not only AgentBuilder-assembled agents. First skill registers the
  `read_skill` / `call_skill_tool` meta-tools once over a live `MutableSkillCatalog`; later skills
  (even mid-session) are immediately readable; a same-name skill replaces the previous one.
- **Built-in "Lua Modding" skill for the Programmer role.** The system prompt keeps the
  survival-minimum API list and points at `read_skill('Lua Modding')`; the skill returns the full
  ~9.5 KB reference: every sandbox API family with signatures, timers/input/persistence/cross-mod
  patterns, a complete worked mini-game mod, and a catalog of common errors with causes (incl. the
  JSON `
` double-escaping failure mode observed with small models).
- **`ReadSkillLlmTool` / `CallSkillToolLlmTool` are public** so hosts and installers can attach
  skill catalogs to roles assembled outside AgentBuilder.
- **Audit follow-up (MoonSharp idioms):** Lua `print()` routes to the project logger (and inside
  mods into the same report pipeline as `report()`, honoring the mute flag) instead of MoonSharp's
  invisible `Console.WriteLine`; `LuaExecutionGuard` documents its real guarantee (no CLR-call
  preemption); `LuaApiRegistry` builds `CallbackFunction`s eagerly from the owning script and
  caches `ParameterInfo[]` per registration.
- **Semver:** major (5.0), lockstep with **`com.neoxider.coreaiunity` 5.0.0** - the on-demand skills platform for built-in roles.

## 4.20.0 - hooks_on('tick') alias; {id=...} table coercion for numeric params (2026-07-04)

- **`hooks_on('tick'/'update'/'frame')` registers a real per-frame timer.** `hooks_on` receives only
  NAMED events, but LLM-written mods routinely register these spellings expecting a frame callback and
  got a handler that never fired (observed live: a day/night sun rotator that never rotated). The
  intuitive spellings now route to the timer machinery at the minimum interval (0.05 s / 20 Hz),
  counted against the mod's timer cap.
- **Lua tables with an `id` field coerce to numeric parameters.** `LuaApiRegistry` delegate dispatch:
  when a delegate parameter is numeric and the script passed a table (models constantly pass a whole
  `unity_find_all` entry instead of `entry.id`), the table's numeric `id` member is substituted
  instead of throwing "cannot convert a table to a clr type System.Int32".
- **Semver:** minor, lockstep with **`com.neoxider.coreaiunity` 4.20.0** (mod tick driver, Lua input
  API, mod source editor panel, Lua platform example demo - see that changelog).

## 4.19.0 - WebGL Full Lua fixed; real 429 retry windows; tool-error accounting (2026-07-04)

- **WebGL Full Lua fixed (the "RuntimeError: null function" player crash).** Root cause via a development-build stack trace: MoonSharp's `Script` static ctor loads resources through reflection (`UnityAssetsScriptLoader.LoadResourcesWithReflection` -> `Resources.LoadAll`), and IL2CPP stripped those reflection-only UnityEngine members, so the invoke jumped to a null method pointer and halted the whole wasm player. Fix: preserve `UnityEngine.Resources` + `UnityEngine.TextAsset` in `Assets/link.xml`. Verified live in a browser: the staged diagnostic (Script ctor -> sandbox -> host callback -> `unity_find` -> `unity_set_scale`) passes, and a real model turn found and scaled the demo cube via Full Lua.
- **429 retry now waits the provider's REAL window.** On WebGL, fetch cannot read `Retry-After` (CORS), so the single transient retry used a 2s formula and always landed inside a still-closed TPM window, wasting the whole rescue chain. `ResolveRateLimitBackoffMs` now parses the window from the error body ("Please try again in 14.017s" - Groq format, minutes+seconds, capped 20s, +250ms margin), and `BuildHttpException` surfaces it as `RetryAfterSeconds` on the typed error.
- **Tool-error accounting: partial success is progress.** The consecutive-error abort (3 strikes) now counts only ALL-failed batches/turns; a 4-of-5-successful spawn batch no longer pushes a run toward "max consecutive tool errors reached" (sequential, parallel and streamed paths).
- **Failed tool calls stay retryable verbatim.** Duplicate/echo signatures register only after a batch/turn that made progress; previously a transiently-failed call could never be retried with identical (correct) args - the pre-execution registration suppressed exactly the retry the error feedback asked for.
- **Tool loop parity upgrades (audit close-out).** History trimming now applies to the STREAMING tool loop too (shared `ToolCallHistoryTrimmer`; assistant/tool pairs never orphaned; default `maxToolCallHistoryMessages` is 20, 0 = unlimited). At the roundtrip cap or max-errors guard the model gets ONE final tools-disabled summarization turn instead of empty/canned text. Argument type-conversion failures feed the compact schema back to the model. Deterministic `tool_call_id` synthesis when a provider omits ids (same id on echo and reply); parse-error calls echo the model's raw argument string, not internal markers. Intra-batch/intra-turn identical calls all execute ("spawn tree x3" works; only the cross-turn echo guard remains). Non-streaming turns report whole-turn summed usage (`LlmUsageAccumulator`).
- **Tool-result wire hardening.** A `System.Text.Json.JsonElement` result can no longer reach the model as Newtonsoft's `{"ValueKind":N}` reflection garbage - the wire builder emits the element's actual JSON/string.

## 4.18.4 - transient-HTTP chain: request -> retry -> non-streaming fallback -> typed error (2026-07-04)

- **A transient HTTP failure (429/408/5xx) on the streamed path now walks the full rescue chain
  before any error reaches the player.** Previously 429 got its bounded retries and then threw;
  nothing fell back unless a SECOND backend was configured (FallbackLlmClientDecorator needs a
  secondary). Now: 1 request -> `RateLimitMaxRetries` (default 1) Retry-After-aware retries -> ONE
  plain non-streaming completion with a ZERO extra-retry budget -> only then the typed error
  (RateLimited/BackendUnavailable/...). 408 and 5xx joined 429 as retryable transients; the
  non-streaming path uses the same classification.
- EditMode: `RateLimited429Once_RetriesAndCompletes` (2 opens),
  `RateLimitedPersists_FallsBackToNonStreaming` (2 opens + exactly 1 completion),
  `RateLimited429Exhausted_ThrowsRateLimited` (fallback also 429 -> typed error, no hidden rounds).
  SSE fixture: 36/36.

## 4.18.3 — bounded HTTP 429 retries before the RateLimited error surfaces (2026-07-04)

- **An HTTP 429 no longer fails the turn on the first hit.** Previously 429 was never retried:
  the transient-retry classifier only matched local-model reload texts, so a burst-rate-limited
  provider (routine on OpenRouter `:free` tiers — verified live in a WebGL build) surfaced
  "Error: HTTP error 429" to the player immediately. Now both the non-streaming and the
  stream-open paths absorb up to `RateLimitMaxRetries` (default 2) extra attempts, honoring the
  `Retry-After` header when present (capped at 15s) and falling back to 2s/4s backoff, before the
  typed `LlmErrorCode.RateLimited` error surfaces.
- EditMode: `GetStreamingResponseAsync_RateLimited429Twice_RetriesAndCompletes` (two 429s absorbed,
  3 opens) and `GetStreamingResponseAsync_RateLimited429Exhausted_ThrowsRateLimited` (three 429s →
  typed RateLimited after exactly 1+2 attempts). SSE fixture: 35/35.

## 4.18.2 — starved-stream watchdog: abort keep-alive-only SSE attempts early (2026-07-04)

- **A starved SSE attempt no longer waits for the server to close the connection.** Confirmed in a
  WebGL production build: a proxy hiding an upstream failure behind HTTP 200 held each streaming
  attempt open for ~40s sending only `: keep-alive` comment lines, so the three empty-stream
  retries alone exceeded the host's 120s turn watchdog — the turn was cancelled before the 4.18.1
  non-streaming fallback could even start (`wallMs=120029 chunks=0 | cancelled`). Now, while an
  attempt has produced ZERO parsed deltas and nothing but SSE comment/blank lines has arrived, the
  attempt is aborted after `StarvedStreamFirstDeltaTimeoutSeconds` (default 15s) and the existing
  empty-stream retry/fallback path takes over immediately (starved-aborted retries also skip the
  extra backoff — the wait was already served). Worst case to fallback: ~45s instead of 2+ minutes.
  A genuinely slow model that streams real data lines (or nothing at all) is unaffected — the
  watchdog only fires on comment-only traffic before the first delta.
- EditMode: `GetStreamingResponseAsync_EndlessKeepAliveStream_AbortsEarlyAndFallsBack` (a stream
  that never closes and never sends a data line: 3 early-aborted attempts, exactly 1 non-streaming
  completion, fallback text surfaces through the stream).

## 4.18.1 — starved SSE stream falls back to a non-streaming completion (2026-07-03)

- **An SSE 200 with zero data deltas no longer eats the whole retry budget and no longer ends in
  silence.** A starved stream (typically an upstream rate limit hidden behind a proxy: HTTP 200,
  only keep-alive comments, no tokens — the "HTTP 200 but 0 parsed SSE deltas" warning) previously
  retried the stream up to 10 times with backoff (~a minute of a busy chat turn) and then threw
  `BackendUnavailable`, which surfaced to the player as no answer at all. Now the empty stream is
  retried only 3 times, after which the SAME turn falls back to ONE plain (non-streaming)
  completion — the same provider usually still answers it — and the answer is delivered through the
  streaming iterator as simulated updates (the existing no-SSE-transport path). Only if the plain
  completion also fails does the turn surface a typed error.
- EditMode: `GetStreamingResponseAsync_EmptyStreamRepeated_FallsBackToNonStreamingCompletion`
  (3 stream opens, exactly 1 non-streaming completion, fallback text surfaces through the stream).

### Runtime backend switching cross-reference (2026-07-03)

- **Docs:** `LLM_ROUTING.md` now cross-references the host-side runtime backend switching feature
  (`CoreAiBackend` in `com.neoxider.coreaiunity`) from the execution-modes section. The portable
  core is unchanged — switching is implemented entirely in the Unity host package on top of the
  existing `ILlmClientRegistry` legacy-fallback contract.

### Forced-tool-mode compat + local-transport hardening (2026-07-03)

- **`RequireSpecific` forced tool mode is now provider-portable.** Instead of MEAI's
  `ChatToolMode.RequireSpecific(name)` (whose wire form — a forced-specific `tool_choice` — some
  OpenAI-compatible local servers reject), `MeaiLlmClient.ApplyForcedToolMode` maps it to
  `RequireAny` while narrowing `options.Tools` to the single requested tool; post-call iterations
  restore the full tool list together with the usual switch back to `Auto`. An unknown
  `RequiredToolName` degrades to plain `RequireAny` with a warning instead of a guaranteed provider
  error.
- **`HttpClientOpenAiTransport` bypasses the system proxy for its shared clients** (`UseProxy =
  false`) so local LLM endpoints are never routed through a WinINET proxy/VPN driver. Deliberately
  does NOT also assign `Proxy = null`: on Unity Mono, `HttpClientHandler` defers property writes to
  an inner `MonoWebRequestHandler`, and that assignment throws `InvalidOperationException` lazily on
  the first request, poisoning every request through the shared client.
- **Stream-open failures now log the inner exception** (e.g. `WebException: ConnectFailure`) instead
  of only the generic "An error occurred while sending the request" wrapper, so a refused TCP
  connection is distinguishable from a mid-handshake reset in the log.

### Streaming-by-default task execution + transport-send retry (2026-07-03)

Two changes that make streaming the default execution path everywhere and keep it reliable against
local servers.

- **`AiOrchestrator.RunTaskAsync` now streams by default.** Each completion for a non-interactive
  agent task is obtained via `CompleteStreamingAsync` when `EnableStreaming` is on (new
  `CompleteForTaskAsync` helper collapses the stream into an `LlmCompletionResult`), so task execution
  runs through the same execute-as-you-stream tool path — including bounded-parallel tool calls — as
  chat, instead of the non-streaming `CompleteAsync`. It falls back to `CompleteAsync` only when
  streaming is disabled. All surrounding logic (context-overflow retry, structured-response
  validation, tool-only content synthesis, empty-response handling) is unchanged.
- **`MeaiOpenAiChatClient` retries a transport-level send failure on stream-open.** A pooled
  keep-alive connection the local server has already closed surfaces as
  `"An error occurred while sending the request"`; that no longer fails the whole request — a bounded
  couple of quick retries open a fresh connection, and a genuinely-down backend still surfaces
  promptly as `BackendUnavailable`.

### Parallel execute-as-you-stream tool calls (2026-07-03)

Until now only the batch path ran a turn's tool calls concurrently (`ExecuteBatchAsync`, bounded by
`MaxParallelToolCalls`); the execute-as-you-stream path executed strictly one-by-one, so a slow tool
stalled every call queued behind it even while the model kept streaming.

- **`ToolExecutionPolicy.ExecuteStreamedAsync` now schedules each drained call concurrently**, on a
  `SemaphoreSlim` bounded by the same `MaxParallelToolCalls` setting as the batch path. Its return type
  becomes `Task<ToolCallResult?>` (null when a call was scheduled for parallel execution; the result
  then surfaces at completion). Per-call duplicate suppression and the cross-turn echo guard are still
  decided synchronously at arrival (arrival order defines the turn signature); the per-call timeout is
  enforced inside `ExecuteSingleAsync` in each worker, exactly as on the batch path.
- **Serialized mutating built-ins are unchanged**: `IsSerializedTool` calls (`memory`, `manage_mods`,
  `manage_skills`) still chain on one ordered serial chain so two writes never overlap; independent
  tools run fully in parallel.
- **The turn closes through a new `CompleteStreamedTurnAsync`**: drains all in-flight calls (a
  cancelled/unfinished call becomes a failed slot — finalization never throws, and it also runs on
  mid-stream abort), collates results strictly in ARRIVAL order, then applies the existing turn-level
  semantics unchanged — whole-turn echo → one `RecordFailure`; otherwise one success/failure record;
  combined-signature registration. The drain is **bounded by the per-call tool timeout plus a small
  margin**, so a tool that ignores its cancellation token cannot hang finalization even on the abort
  path (which passes `CancellationToken.None`); only explicitly-disabled tool timeouts wait for natural
  completion.
- **`MaxParallelToolCalls <= 1` keeps the old strictly-sequential inline behavior byte-identical.**

### Streamed tool-call hardening from the independent audit (2026-07-03)

Four fixes to the execute-as-you-stream path, all found by a two-track code audit of the streaming
work below.

- **Multi-call echo turns now trip the consecutive-error guard.** `CompleteStreamedTurn` registers
  the turn's combined signature BEFORE recording the outcome and checks the `Add()` return value:
  a whole-turn echo (identical streamed turn already executed this request) records ONE failure —
  never a success — with a `duplicate` trace, mirroring the all-duplicate batch branch. Previously
  the re-executed calls succeeded and `RecordSuccess()` kept resetting the counter, so a model stuck
  echoing the same multi-call batch ran to the iteration cap instead of tripping max-consecutive-errors.
- **The SSE stall clock no longer counts consumer time.** `lastProgressUtc` re-arms AFTER the parsed
  updates for a line are yielded and consumed: the streaming iterator is pull-based and the consumer
  executes tool calls between `MoveNext`s, so re-arming on line arrival charged tool-execution time
  against the transport stall budget and aborted healthy streams with slow tools.
- **`DrainCompleted()` drains in strict provider index order across chunks.** Only the longest
  contiguous ready prefix of the `(index, sequence)` order is drained; a still-open earlier call
  blocks later closed ones, so dependent pairs (create → configure) can no longer execute out of
  order when a later call's JSON happens to close first.
- **Drained calls leave tombstones.** Fragments referring to an already-drained call (by id, or by
  index with no id) are ignored: OpenAI-compat servers that re-send cumulative argument strings or
  trailing empty deltas after a call drained can no longer create a fresh pending entry and execute
  the call a second time. A fresh id reusing a drained index is still treated as a genuinely new call.

### Wire protocol: one tool message per tool result (2026-07-03)

- **`MeaiOpenAiChatClient` now serializes a Tool-role message carrying several
  `FunctionResultContent` items into one OpenAI `tool` message PER result** (each with its own
  `tool_call_id`). Previously only the FIRST result reached the wire, so after a multi-call turn the
  model saw N `tool_calls` but a single answer and legitimately re-issued the "unanswered" calls on
  every round-trip — observed live in the game benchmark, where a 5-spawn turn ballooned into 15
  executed spawns (5+4+3+2+1 echo cascade) and tanked instruction-adherence scores across models.

### MaxOutputTokens: explicit 0 = unlimited (2026-07-02)

OpenAI-compatible `max_tokens` counts REASONING tokens too, so any finite cap silently starves a
long-thinking model: observed live with glm-5.2 on the benchmark's free-build scenario — the whole
4800-token per-turn cap went to thinking (`finish_reason=length`), zero tool calls, empty scene.

- **`0` now means "explicitly unlimited" at every level of the fallback chain** — per-call
  (`AiTaskRequest.MaxOutputTokens`), per-agent (`AgentBuilder.WithMaxOutputTokens(0)`,
  `AgentMemoryPolicy.SetMaxOutputTokens`), resolved by `AiOrchestrator` — and reaches the LLM client
  as `0`, which suppresses the global `ICoreAISettings.MaxTokens` fallback entirely: no `max_tokens`
  is sent, the provider uses its own default. `null`/negative still means "inherit the next level".
  This matches the existing `MaxToolCallRoundtrips` convention where `0` = unlimited.

### Text tool-call extractor: python-style keyword arguments (2026-07-02)

- **`LlmToolCallTextExtractor` now parses `name(key=value, ...)` calls into real typed arguments**
  (quoted strings unquoted, `true`/`false` → bool, numerics → numbers via invariant culture,
  `{...}`/`[...]` → parsed JSON). Previously a message like
  `world_command(action='spawn', targetName='Goal', x=0, y=0, z=2)` fell into the generic
  positional branch and collapsed into `{"input":"action='spawn'"}`, failing the tool's
  required-argument validation (observed live in the game benchmark). All-or-nothing gate: any
  non-`key=value` part falls back to the legacy positional handling.

### Execute-as-you-stream tool calls (2026-07-02)

Real-API streaming end to end: with a streaming provider, each native tool call now executes the
moment its argument JSON closes on the wire, instead of being buffered until the whole assistant
turn finishes.

- **`SseToolCallAccumulator.DrainCompleted()`** — while a streamed response is still arriving, any
  pending tool call whose accumulated argument JSON is already a complete object (string/escape-aware
  brace scan, trailing junk rejected) is emitted as a `FunctionCallContent` immediately and removed
  from the pending set. Calls with malformed/truncated JSON never drain early; they still finalize
  (with the parse-error marker) at end of stream.
- **`ToolExecutionPolicy.StreamedTurn`** (`BeginStreamedTurn` / `ExecuteStreamedAsync` /
  `CompleteStreamedTurn`) — executes calls one by one as they arrive while preserving the exact
  batch semantics of `ExecuteBatchAsync`: intra-turn duplicate suppression (same trace message),
  one success/failure record per turn for the consecutive-error guard, and end-of-turn echo
  signature registration so a later batch that repeats the streamed turn is still recognized.

### Benchmark: G7 comprehensive integration scenario (2026-07-01)

- `BenchmarkInfo.GroupDifficulty10` gained a `G7` entry (difficulty 9, hardest) for the new
  comprehensive-integration scenario group (world-building + Lua logic cross-consistency) added on the
  CoreAiUnity side — see `com.neoxider.coreaiunity`'s changelog for the scenario itself.
- `FailureAttribution` gained a `NotGraded` value: a scenario that ran fine but is deliberately excluded
  from the model's score (e.g. a fully custom-prompt free-build the built-in checkpoints weren't designed
  for). `BenchmarkReport.GradedResults`/`MeanBaseByScenario()` now both exclude it, consistent with the
  existing `Environment`/`Framework` exclusions.
- `RoleFitness`'s "Orchestrator / Director" role text now notes the current suite mostly measures
  task-level sequencing, not sustained multi-turn orchestration (weights/gates unchanged).

### Tool-calling hardening (2026-07-01 audit)

- **Reliable tool-result success detection.** `ToolExecutionPolicy.IsToolResultSuccess` no longer treats any
  text lacking the word "success" as success: it now recognizes a **truthy** JSON `error` / `ok:false` /
  `succeeded:false` / `success:false` (any casing) and plain-text failure prefixes, and classifies the
  result **before** it is truncated. A merely-present but null/empty/false `error` key (e.g. `MemoryResult`,
  which always serializes `Error`, null on success) is no longer misclassified as a failure.
- **Duplicate detection fixes.** Intra-batch duplicate calls of non-`AllowDuplicates` tools are now caught on
  the first turn (only the later identical calls are rejected, order preserved); duplicate signatures use the
  repaired **canonical** tool name so casing variants are detected; a repeated mixed batch still executes its
  `AllowDuplicates` calls; a batch where every call is a duplicate now correctly counts toward the
  consecutive-error guard instead of letting a repeating model loop past it silently.
- **Fail-closed tool-name repair.** Ambiguous case-insensitive name matches (two tools colliding under
  `OrdinalIgnoreCase`) are rejected as unknown instead of silently routing to the first match.
- **Atomic memory/skill mutations.** Added `IAtomicAgentMemoryStore.MutateAsync` + a keyed-lock fallback and
  `ISkillStore` atomic mutation so `memory` append/edit and `manage_skills` create/update are process-wide
  serialized read-modify-write — concurrent agent turns can no longer lose an append.
- **SSE tool-call parsing.** `SseToolCallAccumulator` keys pending calls by stable id (falling back to index);
  a differing id on an existing index starts a new call, and missing-index with multiple pending calls is
  flagged instead of merging fragments. Provider-native `reasoning_content` is no longer surfaced as visible
  assistant text (consistent with the streaming path).
- **Tool schema/contract truthfulness.** `DelegateLlmTool` derives `ParametersSchema` from its generated
  MEAI function schema; `CompatibilityLlmTool` description matches its `string[]` contract; `WaitLlmTool`
  states over-max seconds are clamped; `InventoryLlmTool` null-checks its provider.

- **Lua WorldEdit prompts/docs use the new world API.** Core Lua tool descriptions and built-in agent
  prompts now point agents at `coreai_world_spawn({...})`, `coreai_world_change(name, {...})`,
  `coreai_world_set_color`, and `coreai_world_destroy` instead of the legacy move/rotate/parent helper set.
  `LUA_GAME_API.md` was updated to match the current Unity world-command surface and to call out
  per-axis scale for meter-accurate objects.
- **Optional per-request system-prompt override** (`AiTaskRequest.SystemPrompt`). When set, the prompt
  composer uses it as the role's base prompt while still prepending the universal prefix; empty = unchanged
  (the registered role prompt). Lets a caller give a task-specific system prompt on a shared role.
- **Native tool schemas are self-describing.** On the native tool-calling path the JSON schema is generated
  from the C# delegate signature, so parameter descriptions reach the model ONLY via
  `[System.ComponentModel.Description]` attributes — the `ParametersSchema` string only feeds the text path.
  Added `[Description]` to the delegate params of the core tools (Memory, GameConfig, Compatibility,
  CallSkill, ManageSkills, ReadSkill, Wait, Lua, LuaMods). Without it the model saw bare unlabeled params.

## 4.17.0 - 2026-06-30

- **Tool-call history defaults to unlimited (`MaxToolCallHistoryMessages = 0`).** It was 20, which silently
  trimmed the oldest assistant+tool pairs during a long tool-calling turn — so a 30+ step build forgot its
  earliest steps and repeated them. `0` = keep the full history; conversation summarization + overflow
  retry still bound very long sessions. Changed across `ICoreAISettings`, the `CoreAISettings` const, and
  `CoreAISettingsOptions`.
- **Per-agent / per-task tool-call roundtrip cap.** `MaxToolCallRoundtrips` can now be overridden per
  agent (`AgentBuilder.WithMaxToolCallRoundtrips(int?)`) and per call (`AiTaskRequest.MaxToolCallRoundtrips`),
  in addition to the global `ICoreAISettings.MaxToolCallRoundtrips`. Priority: per-call &gt; per-agent &gt;
  global. A value of **`0` means UNLIMITED** (no safety valve), `null` inherits the next level, positive
  caps the loop. Wired end-to-end through `LlmCompletionRequest` → `SmartToolCallingChatClient`.
- **Default cap raised 10 → 20**, and the built-in **Programmer** and **Creator** roles now default to
  `0` (unlimited) since they routinely need many tool roundtrips per turn (code iterate / full build).
- **Clearer stop message.** When the cap is hit, the warning now names the role, the cap, its source
  (global vs per-agent/per-call), and exactly how to raise or disable it.
- **Honest throughput metric.** `GenerationTokensPerSecond` is documented and labeled as **provider-call**
  tok/s (prefill + decode), not decode-only — it reads lower than LM Studio, which excludes prefill. True
  decode-only timing needs TTFT (streaming-only); see the new test and `TOKENS_PER_SEC_FIX_PLAN.md`.
- **`BenchmarkInfo.GroupDifficulty10`** — one canonical 1–10 difficulty per group, the single source the
  editor RUN tab and the scenario/progress now both read.

## 4.16.0 - 2026-06-30

- **`AllowWorldPrimitives` setting** (`ICoreAISettings`, default `true`) gates the `world_command` spawn
  primitive fallback; `SetMaxToolCallRoundtrips` override added for benchmark/bootstrap.
- **`component_command` curated catalog** — `ComponentLlmTool` + `ICoreAiComponentCommandExecutor`: add /
  remove / set / list supported Unity components with no reflection. Mirrored by `coreai_component_*` Lua
  bindings.
- **`BenchmarkInfo`** suite identity (name + `v1`), `BenchmarkReportFormatter` / `ModelComparison` tweaks.
- **Decode tok/s fix** — report `max(provider, tokenizer-estimate)` including tool-call JSON so tool-only
  runs no longer undercount throughput to ~0.3 tok/s.

## 4.15.3 - 2026-06-30

- **Free, ambitious castles.** The G6 prompt swaps the rigid coordinate blueprint for full creative freedom
  within the -9..9 range, pushing the model to build the most impressive castle it can (towers, walls, gate,
  keep, flags + as many extras as it wants) — so each model's character shows.
- **G6 is off by default.** The castle hero is a non-scored bonus visual, so its toggle now defaults to off
  in the benchmark window; enable it explicitly (window toggle or `COREAI_BENCHMARK_GROUPS=…,G6`).

## 4.15.2 - 2026-06-30

- **Model name on every scene/castle screenshot.** The baked header now leads with the model id (long
  hyphenated ids wrap onto two lines and shrink to fit) above the scenario/score/verdict line, so each
  hero image is self-identifying.
- **Recognizable castles.** The G6 free-build prompt now hands the model an explicit coordinate blueprint
  (corner towers, perimeter walls with a gateway gap, central keep, flags) and invites extra decorations,
  and the output-token budget is raised, so even small local models produce a clean square castle instead
  of scattered cubes.

## 4.15.1 - 2026-06-30

- **Averaged repetitions.** When a scenario is run multiple times, the report now aggregates each
  scenario by the **mean (average)** of its repetitions instead of the median (suite score = mean of
  per-scenario means). The report section is retitled "Scenario means (average over repetitions)".
- **Opt-out of repetition.** Scenarios expose `Repeatable` (default true); a visual one-off such as the
  G6 castle hero sets it false so it runs exactly once even when the suite repeats every other scenario.

## 4.15.0 - 2026-06-30

- **feat(benchmarking): Game-Creation Benchmark reporting polish.** The portable reporting core now supports
  the G6 castle free-build hero scene, per-model model-card radar/role summaries, role-shaped scene result
  images with ghost markers for missing expected objects, decode-vs-effective tok/s reporting, and
  cross-model comparison data for the TerminalBench-style bar chart with ranked or pinned-first ordering.
- **Benchmark sweep and stability metadata.** Reports preserve repetition counts for median-stability
  comparisons, expose the fields used by LM Studio multi-model sweeps, and keep decode throughput
  comparable to LM Studio while also showing end-to-end effective throughput for the whole agentic session.
- **Audit fixes.** Benchmark rendering and serialization paths were tightened to avoid material/mesh leaks
  and to persist generation-time fields needed by Markdown, JSON, model-card, scene-image, and comparison
  reports.

## 4.14.0 - 2026-06-29

- **feat(benchmarking): portable Game-Creation Benchmark scoring core.** New unit-tested benchmark
  primitives live under `Assets/CoreAI/Runtime/Core/Features/Benchmarking`, with 0..100 scoring across
  Tool correctness, Intent & sequence, Task completion, Determinism, Reasoning, and Instruction adherence.
- **Subtractive instruction-following.** Constraint scenarios score from 100 down — each violation costs
  its compliance checkpoint plus a per-occurrence penalty, with a mandatory core task so "do nothing"
  can never score 100.
- **Game-fitness by role.** A new `RoleFitness` core type turns the dimension scores into a 0..10 fit
  rating per game-dev role (NPC, Mechanic/GameMaster, Scene/Tool Operator, Programmer, Orchestrator/Director,
  QA) with gates and an overall rating, so a tiny 2B/0.8B model reads clearly as 'Not suitable for agentic
  roles' instead of a misleading mid score. A role is only rated when the run actually measured every
  dimension it depends on — a partial (single-group) run marks the affected roles 'Not assessed' instead
  of over-claiming — and the headline overall reflects agentic roles only, so a chatty model cannot inflate
  it through the NPC score.
- **Efficiency scoring.** The benchmark adds a gated efficiency bonus for fewer tokens and less time
  (split token/time, base score must be >=90, capped at 20), reports honest generation tokens/sec from
  completion tokens only, uses per-scenario timeouts and per-scenario medians over repetitions, retries
  transient/crash failures, and excludes environment failures from the model score.
- **Self-explanatory scene screenshots.** World scenarios render a real Unity screenshot with a baked
  header (scenario, score and PASS/PARTIAL/FAIL verdict, tinted by outcome) and a "what it checks" caption.
  Objects are drawn by role (capsule/sphere/coin/post) with a ✓/✗ status, and expected objects the model
  never built appear as ghosts — so the picture alone shows how the model did. A per-model "model card"
  (dimension radar + role bars) makes two models comparable at a glance. `ScenarioResult` carries the PNG
  plus the description for the report.

## 4.13.0 - 2026-06-28

- **feat(tools): parallel tool-call execution.** `ToolExecutionPolicy.ExecuteBatchAsync` now runs a turn's
  tool calls concurrently, bounded by the new `MaxParallelToolCalls` setting (default 4; 1 = sequential
  fast-path). Result order is preserved (collated by index), state-mutating built-ins (`memory`,
  `manage_mods`, `manage_skills`) are serialized so writes never race, and per-call timeout / duplicate
  rejection / forced-tool reset / consecutive-error counting / cancellation semantics are unchanged.
- **feat(tools): Hermes / Qwen-Agent XML tool-call parsing.** `LlmToolCallTextExtractor` now recovers the
  `<tool_call><function=NAME><parameter=KEY>VALUE</parameter>…</function></tool_call>` template many local
  GGUF models emit as text when native `tool_calls` is empty (parameter values kept as strings; wrapper
  tags stripped). Joins the existing JSON / `arguments_json` / `read_skill(...)` / `Action=write` formats.
- **feat(context): real BPE token counting.** `ITokenCounter` + `BpeTokenCounter` implement byte-level BPE
  (cl100k_base / o200k_base, resolved by `BpeEncodingResolver`) for exact pre-flight counts, loaded via
  `IBpeRanksProvider`. Falls back automatically to the calibrating estimator on unknown model / missing
  data / AOT load failure. Activate by adding the tiktoken rank files (see CONTEXT_MANAGEMENT_ROADMAP).
- **feat(skills): agent-authored skills.** New `manage_skills` tool (create/update/list/get/delete) +
  `ISkillStore` lets the model write, version (via `ILuaScriptVersionStore`), persist, and immediately
  reuse its own skills through the same role's `read_skill` catalog. `AgentBuilder.WithSkillAuthoring(...)`.

## 4.12.1 - 2026-06-28

- **fix(prompts): memory-usage instruction now reaches native tool-calling roles.** In
  `AiToolContractPromptFormatter`, the positive memory guidance ("when asked to remember/save/record,
  call the memory tool") lived *after* the `supportsNativeToolCalling` early-return, so native
  tool-calling roles whose base prompt never mentions memory (e.g. Creator) received **no** memory
  instruction at all and silently ignored "remember the …" tasks — leaving it to the model to infer.
  The detection + a strengthened imperative now run ahead of the early-return, so both the native and
  text-shaped paths get it (gated on the role actually having the `memory` tool). Tests:
  `DeterministicToolContractEditModeTests` (native+memory includes the imperative; native without memory
  omits it).

## 4.12.0 - 2026-06-28

- **Lua mod versioning (`manage_mods versions` / `revert`).** Mod load/reload now records a revision per
  edit into the existing `ILuaScriptVersionStore` (keyed `mod:<id>`); `LuaModManifest.Version` auto-derives
  from the revision count. New `manage_mods versions` lists the revision history (revision 0 = original) and
  `manage_mods revert` rolls a mod back to a recorded revision — a non-destructive revert (the reload
  re-records the restored source as the new current revision, so history stays an audit trail). A no-op
  reload (identical source) does not grow history. With no version store wired, load/reload still work and
  `versions` reports no history.
- **Runtime mod-handler error feedback (`manage_mods diagnostics`).** A hook that throws during `Tick`
  previously only raised `ModHandlerErrored` + counted toward host-side auto-unload; it is now also recorded
  in a bounded ring buffer (`MaxRetainedHandlerErrors = 32`, newest kept) so the agent can poll
  `manage_mods diagnostics` next turn to learn of runtime handler failures and repair the mod.
  `GetRecentHandlerErrors` / `ClearRecentHandlerErrors` expose and acknowledge the buffer.

## 4.11.5 - 2026-06-27

- **Lockstep package version.** No portable CoreAI API or runtime behavior changed in this drop; the bump
  keeps `com.neoxider.coreai` aligned with `com.neoxider.coreaiunity` 4.11.5 (WebGL non-streaming
  completions fix) so UPM consumers keep both package versions in sync.

## 4.11.4 - 2026-06-26

- **Lockstep package version.** No portable CoreAI API or runtime behavior changed in this drop; the bump
  keeps `com.neoxider.coreai` aligned with `com.neoxider.coreaiunity` 4.11.4 (WebGL chat render cap fix)
  so UPM consumers keep both package versions in sync.

## 4.11.3 - 2026-06-23

- **Lockstep package version for Unity inspector fix.** No portable CoreAI API or runtime behavior
  changed in this drop; `com.neoxider.coreai` is versioned with `com.neoxider.coreaiunity` so UPM
  consumers can keep both package versions aligned.

## 4.11.2 - 2026-06-23

- **Force-inject a skill into agent history (no model turn).** New
  `AgentSkillInjection.InjectSkillIntoHistory(store, roleId, skill)` pushes a `SkillSet`'s `read_skill`
  payload (instructions + tool schemas) straight into a role's history — exactly as if the agent had
  already called `read_skill` — without running the agent. The agent does not start a response; the skill
  is just available on its next turn. Stored with the internal `"tool"` history role, so the model sees it
  while the visible chat stays clean. `ReadSkillLlmTool.BuildSkillPayloadJson` builds the payload (always
  includes instructions, not gated on the skill having callable tools).

## 4.11.1 - 2026-06-23

- **Tool failures are surfaced again, accurately.** A failed tool-only turn now resolves to
  `Tool call failed: <tool>: <reason>.` (e.g. `manage_mods: attempt to index a function value`) instead of
  the misleading generic `LLM request failed.` / `structured validation failed`. This reverses the 4.10.4
  hide at the orchestrator level; the chat UI still gates these `Tool call …` lifecycle lines symmetrically
  by `ShowToolCallsInChat` (hidden = hidden for both success and failure), so a clean-chat configuration
  stays clean while the model always receives the full error. Restores the `AiOrchestratorHistory` /
  `AiOrchestratorToolFailureFallback` tests that pin this behavior.
- **Documented tool-call result logging.** `TOOL_CALL_SPEC.md` now describes the per-call
  `[ToolCall] … status=OK|FAIL dur=…ms args=… result=…` debug line and the `LogToolCalls` /
  `LogToolCallArguments` / `LogToolCallResults` / `LogMeaiToolCallingSteps` flags, plus how success/failure
  is surfaced to the model vs the user. New EditMode tests assert the FAIL line carries the tool name,
  status, and the real result detail.

## 4.11.0 - 2026-06-23

- **Live-turn diagnostics.** `AgentTurnTrace` now carries the turn `Status` (`Completed`/`Failed`),
  a `RecordedAtUtcTicks` timestamp, and the observed `ToolCalls` (name, success, duration, source,
  detail). `AiOrchestrator.RecordTrace` populates these from the completion result without changing
  orchestration or persistence behavior.
- **Readable turn-trace sink.** New `IAgentTurnTraceReader.TryGetLatestTrace(roleId, out trace)`.
  `InMemoryAgentTurnTraceSink` implements it, retaining the latest trace per role (bounded) in
  addition to the existing ring buffer. The default `NullAgentTurnTraceSink` is unchanged, so the
  feature degrades gracefully when no readable sink is registered.

## 4.10.5 - 2026-06-22

- Restore live token streaming for tool-declared turns (4.10.4 buffered them and lost streaming).
  Keep the failed-tool status suppression.

## 4.10.4 - 2026-06-21

- **Failed tool-only completions stay model/internal-only.** `AiOrchestrator` no longer synthesizes visible
  `Tool call failed: ...` / `Tool calls failed: ...` assistant text when a streaming or non-streaming tool
  round has no real model answer. The Unity streaming retry instruction for failed tools is unchanged.

## 4.10.3 - 2026-06-21

Adversarial module audit fixes (core). Two independent passes (find + verify) over the LLM transport,
orchestration/context, memory/skills, and Lua-execution clusters; the items below were confirmed by both.

- **SSE idle-timeout no longer leaks timers.** `MeaiOpenAiChatClient.ReadWithIdleTimeoutAsync` drove a
  fresh `Task.Delay(timeout)` per 8 KB read and never cancelled it when the read won, leaving one live
  timer + `CancellationTokenRegistration` per read for the full timeout. It now uses a per-read linked
  CTS, cancels it on the hot path, and observes the abandoned read so a post-dispose fault is not raised
  as an unobserved task exception.
- **Unified error path for the exception-based retry loop.** `LoggingLlmClientDecorator` now catches
  `OperationCanceledException` (rethrow) and non-retryable exceptions (structured `Ok=false` result)
  inside the exception-retry loop, matching the result-based loop instead of letting a raw exception
  escape `CompleteAsync`.
- **Order-independent duplicate tool-call detection.** `ToolExecutionPolicy` canonicalizes argument keys
  (sorted) before hashing the call signature, so the same call re-emitted with a different key order is
  recognized as a duplicate.
- **Token-calibration fixes.** The streaming path no longer double-feeds the calibration EMA (the
  redundant `RecordTokenObservation` after `SanitizeAndPublish` was removed), and
  `CalibratingTokenEstimator` persists the scale **after** releasing its lock so estimation no longer
  serializes behind a disk write.
- **Context-overflow retry actually shrinks.** When history summarization is disabled (or a fixed recent
  budget override is set), `AiOrchestrator` now clamps the history budget by the per-retry-shrunk policy
  budget, so a context-overflow retry no longer re-sends a byte-identical oversized request.
- **Streaming consumer cancels its producer.** `QueuedAiOrchestrator` links a consumer-abandonment token
  into the producer, so breaking out of the public stream (without cancelling the caller token) stops the
  inner LLM stream instead of draining it into an unbounded queue off-screen.
- **Injective memory scope keys.** `ScopedAgentMemoryStoreDecorator` length-prefixes each scope part, so
  distinct user/session tuples can no longer collide on the same key (a cross-user isolation breach). The
  unscoped default path (bare role id) is unchanged.
- **Skill tool-name collisions are visible.** `call_skill_tool` keeps the first-registered tool for a
  duplicate name (deterministic) and logs a warning, instead of silent last-write-wins misrouting.
- **Memory tool correctness.** `append` now dedupes on whole trimmed lines (not a case-insensitive
  substring, which silently dropped short facts), and mutations load the state once and thread it through
  `SaveMutation` instead of re-reading the store mid read-modify-write.
- **Lua mod runtime hardening.** Event dispatch snapshots the handler list (so a handler calling
  `hooks_on` for the in-flight event can no longer throw `InvalidOperationException` out of `Tick`),
  honours the no-drop contract by only dequeuing an event when the budget covers all its handlers, and
  guards per-mod dispatch so one mod cannot abort the whole tick. `SecureLuaEnvironment.RunChunk` now
  applies the documented `OneShotHardLimitSteps` (500k) instead of the guard's 200k default.

## 4.10.2 - 2026-06-21

- Версия приведена в соответствие с `com.neoxider.coreaiunity` (пакеты держим в lockstep — одинаковые версии). Функциональных изменений в core нет; правки этого релиза — в Unity-слое (см. `CoreAiUnity/CHANGELOG.md`: фикс отображения tool-call уведомлений в чате + тест-фиксы EditMode).

## 4.10.0 - 2026-06-20

- **Vision / image input (core).** `MeaiOpenAiChatClient` now serializes image content (MEAI
  `DataContent` / `UriContent` with an `image/*` media type) on a message as OpenAI multimodal
  `content` parts (`{type:"text"}` + `{type:"image_url"}` with a data URI), so a vision-capable model
  actually receives attached images. Text-only messages are unchanged. Exposed
  `BuildOpenAiMessageContent(...)` for verification (covered by `MeaiOpenAiVisionEditModeTests`).
- **Persistent file-backed Lua mod packages.** New `ILuaModSourceStore` (with `NullLuaModSourceStore`
  default and a host-side `FileLuaModSourceStore`) persists a mod's **source plus its
  `LuaModManifest`** (`id`, `name`, `description`, `version`, `author`, `capabilities`, `active`,
  `entry`). The file-backed store lays each package out under
  `persistentDataPath/CoreAI/Mods/<id>/` as `manifest.json` + `main.lua`. This is separate from the
  per-mod `store_set`/`store_get` key/value store (`FileLuaModStore`); the source store persists the
  mod itself. With no store wired the runtime uses `NullLuaModSourceStore` and behaves exactly as
  before (in-memory only).
- **Auto-persist + rehydrate.** `LuaModRuntime` gains a `sourceStore` constructor parameter (appended
  with a default, `autoPersistMods` defaults to `true`, so all existing callers compile unchanged).
  Every successful `LoadMod` / `ReloadMod` auto-saves the source and manifest; `UnloadMod` marks the
  stored package dormant (`Active = false`) instead of deleting it. All store calls are best-effort —
  a store failure is logged and never aborts a load. On startup `RehydrateFromStore(hostGrant,
  allowFull = false)` re-loads every active stored mod, returning the count restored, so a mod loaded
  once via chat survives a restart. The `manage_mods` tool auto-persists through this path.
- **Export / import / forget — move mods between players.** `ExportMod(id)` returns a self-contained
  bundle `{"manifest":{...},"source":"..."}` (or `null` for an unknown id); `ImportMod(bundleJson,
  hostGrant, allowFull = false)` loads it on another host; `ForgetMod(id)` permanently removes the
  stored package. The `manage_mods` tool exposes the matching `export`, `import`, and `forget` actions
  alongside `load`, `reload`, `unload`, `list`, and `get_source`. A mod folder can also be copied
  directly between players' `persistentDataPath/CoreAI/Mods/<id>/`.
- **Full OFF by default for persisted/shared mods (security).** `RehydrateFromStore` and `ImportMod`
  both intersect the mod's requested capabilities with the host grant and then strip
  `LuaCapabilities.Full` unless the host explicitly passes `allowFull: true`. Capability parsing is
  fail-closed (an empty/unparsable manifest capability string resolves to `None`, not `All`). A
  persisted, rehydrated, imported, or copied mod can never silently escalate to reflection access.
- **First-class `.lua` TextAssets.** A `.lua` ScriptedImporter imports any `*.lua` file as a
  `TextAsset`, so mods can be authored with a real `.lua` extension (editor recognition, drag-and-drop
  references) instead of the `.lua.txt` workaround; `asset.text` returns the source. The importer is
  text-only with no MoonSharp dependency, so it works in no-Lua builds too.
- **Docs.** Added `FIRST_MOD.md` ("Your first Lua mod in 5 minutes"): what a mod is, a copy-paste
  minimal mod, the capability tiers, the three ways to load (agent / C# / `.lua` TextAsset),
  persistence, sharing, and a Full-mode example with the security note. `LUA_GAME_API.md` gains a
  Persistence & Sharing section; `LUA_ACCESS_MODES.md` notes that persisted/shared mods are non-Full
  by default. Ships with a no-LLM Full-mode mod demo and example `.lua` mods.

## 4.9.0 - 2026-06-20

- **Context pruning — stale reasoning.** `ConversationHistoryPruner` now strips stale
  `<think>…</think>` reasoning blocks from every assistant turn except the newest one (lossless,
  prune-before-summarize, prompt-copy only — durable history is untouched). Assistant turns that are
  pure reasoning are dropped. Completes the roadmap §7 "prune stale thinking" item alongside the
  existing superseded-tool-result and duplicate collapsing.
- **WebGL Lua (core).** `SecureLuaEnvironment.WebGlLuaOptIn` + `ICoreAISettings.EnableLuaOnWebGl`
  (default **`true`**): `IsSupported` now honors the setting on the WebGL player instead of a hard block.
  Added `SecureLuaEnvironment.TryRunSelfTest(out report)` for a player-side sandbox self-test.
- **Audit hardening (core).** Sandbox now caps `string.format` output (allocation-bomb parity with
  `string.rep`). `LuaModRuntime` adds a global per-tick event-dispatch budget on top of the per-mod cap
  (bounds worst-case main-thread stall with many mods; surplus carries over, never dropped). Lua
  world-transaction state is reset/aborted on every top-level run (`ILuaTransactionScope`) so an aborted
  `coreai_world_begin` cannot leak buffered commands into the next script. The streaming SSE tool-call
  accumulator keys parallel calls by index and **surfaces** malformed/truncated argument JSON instead of
  silently sending empty args. `SmartToolCallingChatClient.TrimToolCallHistory` trims assistant+tool turns
  as coupled pairs so a `tool` message is never orphaned (provider 400). Re-audit follow-ups: the
  world-transaction reset also covers the mod tick loop (per guarded handler/timer); malformed streamed
  tool-call arguments are **rejected before execution** (not just logged); mod event dispatch rotates
  round-robin so no mod starves under sustained load; SSE tool calls emit in ascending index order.

## 4.8.1 - 2026-06-19

- **Release sync.** `com.neoxider.coreai` is bumped to `4.8.1` to stay version-aligned with
  `com.neoxider.coreaiunity`. No portable-core API changes; the Unity package adds Unity 6.5
  `PanelRenderer` chat-host compatibility while preserving the Unity 6.3 `UIDocument` path.

## 4.8.0 - 2026-06-18

- **Chat UI text options.** Added optional `ICoreAiChatTextOptions` and matching `CoreAiChatOptions` fields for
  send/stop/clear/collapse/open-chat labels and tooltips. The original `ICoreAiChatOptions` contract remains
  source-compatible for host projects that provide custom options.

## 4.7.0 - 2026-06-18

- **Reflection-free skill proxy path.** `call_skill_tool` no longer manually reflects delegates, `Task.Result`, or
  parameter metadata. The proxy now invokes skill tools through `IJsonInvocableLlmTool` when available, or through
  the existing MEAI `AIFunction` contract.
- **Skill actions and tool calls.** `DelegateLlmTool` now exposes both `IAIFunctionLlmTool` and
  `IJsonInvocableLlmTool`, so delegate-backed tools and void actions can be placed inside `SkillSet` and called via
  `call_skill_tool`; empty/void results return an explicit `{"success":true}` payload to the model.
- **Skill-only agents validate correctly.** `AgentBuilder.ValidateOnBuild()` now treats registered skills as a valid
  tool source for `ToolsAndChat` / `ToolsOnly` agents instead of warning that no tools are present.

## 4.6.2 - 2026-06-18

- **Version alignment.** Patch release aligned with `com.neoxider.coreaiunity` 4.6.2 for UPM consumers that pin
  matching package versions.

## 4.6.1 - 2026-06-18

- **NoLua package compile fix.** Patch release aligned with `com.neoxider.coreaiunity` 4.6.1 so UPM consumers can
  pin matching versions when Lua is disabled.

## 4.6.0 - 2026-06-18

- **SkillSet tool execution hardening.** `read_skill` / `call_skill_tool` now share a resolver that supports
  `DelegateLlmTool`, `IAIFunctionLlmTool`, and `IAIFunctionsLlmTool`, serializes structured MEAI results
  consistently, and returns explicit tool results or errors back to the model.
- **Skill meta-tools respect allowlists.** Restricted tool runs keep the SkillSet meta-tools when the allowlist
  intersects a skill's inner tool names, so agents can still call allowed tools through `call_skill_tool`.
- **Required-tool retry.** Smart tool calling retries a required tool when the model emits plain text or omits
  the call, then switches back to auto tool choice after the forced call succeeds.
- **Agent session diagnostics split.** `AgentSessionSnapshot` now exposes separate system-prompt and history-only
  text views, and live role discovery can include prompt-provider roles from manifests in addition to policy roles.
- **Memory tool argument tolerance.** `memory` write/append/insert/delete/rename operations accept `new_text` as a
  fallback content field when a model fills the edit field instead of `content`.
- **Lua access-mode docs cleanup.** Replaced the old access-mode audit artifact with `LUA_ACCESS_MODES.md` and
  moved non-blocking future Lua/world work into the Unity backlog.

## 4.5.0 - 2026-06-18

- **Repository line-ending normalization.** Added Unity-friendly `.gitattributes` coverage for source, YAML assets,
  Visual Studio project files, and common binary assets to prevent CRLF/LF churn and binary phantom diffs.
- **Streaming context-overflow recovery.** `AiOrchestrator.RunStreamingAsync` now mirrors the bounded
  `MaxContextOverflowRetries` recovery path from `RunTaskAsync`, rebuilding the request with increasing
  `ContextRetryLevel` before any visible stream text is emitted.
- **Cache-safe memory placement.** Tail placement is now the only runtime path, and the old prefix-placement
  setting was removed. The stable `## Memory` system-prefix block is a cached snapshot; mid-session memory edits
  are sent as a separate `## Memory (updates)` system-role tail message, then consolidated back into the prefix
  only at a cold-cache boundary: initial snapshot, conversation compaction, or context-overflow retry.
- **Memory read action.** The built-in `memory` tool now supports `action = "read"` and returns the current durable
  memory document, length, and latest version without mutating the store.
- **Wait tool.** Added portable `WaitLlmTool` and `AgentBuilder.WithWaitTool(...)` so an agent can intentionally
  pause for a bounded number of seconds, receive a normal tool result, and continue the same tool-calling loop.
- **Tool-result pairing regression coverage.** EditMode tests now guard that native tool calls are followed by
  `FunctionResultContent` with the original call id, so the next model iteration receives the actual tool result.
- **Empty tool-result normalization.** A tool that returns `null` or an empty payload is now converted into an
  explicit successful JSON tool result instead of silently looking like a missing return.
- **Token calibration persistence.** Added `ITokenCalibrationStore`; Unity hosts persist calibration scale per model
  while portable hosts keep a no-op default store.
- **Full Lua blacklist policy.** Full-tier reflection bindings can now receive an `IFullLuaAccessBlacklistPolicy`
  so host games can deny selected component types or members even when Full Lua access is enabled.
- **Lua mod MessagePipe bridge.** `LuaModRuntimeTicker` publishes `LuaModEventEmitted` through MessagePipe for host
  UI/telemetry/repair flows that should observe persistent mod `report()` output without polling the runtime.

## 4.4.0 - 2026-06-15

> Context management overhaul (Claude Code / Cline / Kilo-grade): prefix/tail placement, threshold compaction, tool-result policy, API-token calibration, bounded overflow recovery, context pruning, world-state tail, deterministic prefix, prompt-cache verification + tool-call/memory fixes. See entries below.

- **Agent memory clear regression fix.** The memory tool `clear` action now removes the role key instead of saving an empty versioned row.
- **Tool result memory defaults.** Built-in `Programmer` and `CoreMechanicAI` now default to `ToolResultMemoryPolicy.Full`; other built-in roles keep `CompactSummary`.
- **Prompt-cache usage verification.** Added cache read/write token counters to LLM completion/stream
  results, `LlmUsageRecord`, `LlmUsageReported`, and turn diagnostics, with MEAI
  `UsageDetails.AdditionalCounts` parsing for provider cache counters.
- **Compaction by threshold.** Added `ICoreAISettings.ConversationCompactionTriggerRatio` (default `0.8`)
  and `ConversationContextBuildArgs.CompactionTriggerRatio`. Deterministic and LLM-assisted context managers
  now leave all history verbatim and do not call `SaveSummary` while estimated history tokens are below
  `historyBudget * ratio`; unset/invalid request ratios fall back to the CoreAI default threshold.
- **Deterministic tool contract prefix.** Added shared ordinal-by-name tool ordering, canonical
  Newtonsoft JSON schema rendering with recursively sorted object keys for text-shaped tool contracts,
  and EditMode regression coverage that guards stable fixed-input system prefixes from generated
  GUID/timestamp leakage.
- **Dynamic world-state observation placement.** `AiPromptComposer.BuildRuntimeContext` now exposes the
  per-role/global runtime context section independently, and the tail-placement path can send it as the last
  system-role chat-history message headed `## World State`; flag-off placement in the system prompt was removed
  in 4.5.0.
- **Context editing before compaction.** Added `ConversationHistoryPruner` and roadmap §7 settings
  (`EnableContextPruning`, `MaxRetainedToolResultMessages`) so prompt-history copies collapse exact
  consecutive duplicates and retain only the newest durable `tool` / `## Tool Results` observations before
  budget partitioning. Durable chat history stores are not modified.
- **Emergency context-overflow recovery.** `AiOrchestrator.RunTaskAsync` now performs bounded
  multi-pass recovery for `LlmErrorCode.ContextLengthExceeded`: `ICoreAISettings.MaxContextOverflowRetries`
  defaults to `3` (`0` disables), retry passes advance `ContextBudgetRequest.ContextRetryLevel`, and
  `DefaultContextBudgetPolicy` applies a `0.75^level` history-budget factor instead of the old one-shot
  halving.
- **Token accounting calibration.** Added a portable `CalibratingTokenEstimator` registered as the default
  `ITokenEstimator`, with Latin-preserving script-aware estimates, higher Cyrillic/CJK density, and bounded
  EMA calibration from observed real prompt-token usage behind `ICoreAISettings.EnableTokenCalibration`
  (default true). `HeuristicTokenEstimator` remains as the simple fallback.
- **Tool result memory policy.** Added per-role `ToolResultMemoryPolicy` with default
  `CompactSummary`; executed tool results can now persist into chat history as one `tool` entry and
  replay as provider-safe user observations on later turns.
- **Context prefix stability flag.** Added an opt-in setting so `## Conversation Summary` can be sent as the first
  system-role chat-history message, before recent verbatim turns, instead of rewriting the system prompt prefix.
  The opt-in flag was later removed when tail placement became the only supported path.
- **Default context window raised to 128K.** `CoreAISettings.ContextWindowTokens` and related
  last-resort context-budget defaults now use `131072` tokens instead of `8192`; per-role
  `RoleMemoryConfig.ContextTokens` defaults to `0`, meaning inherit the global
  `ICoreAISettings.ContextWindowTokens` unless a role explicitly overrides it.
- **Agent session inspector Edit Mode snapshots.** `AgentSessionInspector` can now build a read-only best-effort snapshot from serialized settings/prompts/policy inputs and marks live request-only fields as `(unavailable in Edit Mode)`.
- **Conditional tool contract prompt.** Native tool-calling backends now receive the minimal
  `## Tool Contract` guidance while text-shaped/local backends keep the full `Available tools`,
  schema, and JSON-call prompt block.
- **OpenAI-compatible reasoning controls.** `IOpenAiHttpSettings` now exposes a tri-state
  `ReasoningMode` (`ProviderDefault`, `Disabled`, `Enabled`), optional `ThinkingBudgetTokens`, and
  `ExtraBodyJson`. `MeaiOpenAiChatClient` merges provider-specific JSON into both streaming and
  non-streaming chat completions and emits Qwen/vLLM-style `enable_thinking` /
  `chat_template_kwargs.enable_thinking` only when the mode is explicitly not provider-default.
- **Lua mod report logging control.** `LuaModRuntime` now mutes persistent mod `report()` output by
  default and exposes per-mod report logging state so hosts can opt into diagnostics without timer
  mods flooding the console.
- **WorldEdit transform coverage.** Non-Full Lua WorldEdit now exposes safe spawn, destroy, parent,
  move, rotate, and set-transform commands, and Programmer guidance directs visible scene edits
  through `coreai_world_*` APIs instead of hard-coded Full-mode visual recipes or invented `game.*`
  APIs.
- **Lua mod runtime errors are observable.** `LuaModRuntime` now raises `ModHandlerErrored` when an
  active mod's hook or timer fails during `Tick`, allowing hosts to route asynchronous mod failures
  into repair or telemetry flows instead of only logging and incrementing `ErrorCount`.
- **TMP-safe strings.** Decorative Unicode glyphs in user-visible strings were replaced with ASCII where the default TMP/WebGL font cannot render them; prompt-context ellipses (`…`) used in conversation-summary budget math were deliberately kept as single characters so the `MaxSummaryChars` accounting and its EditMode coverage stay correct.
- **English-only docs.** Remaining Russian text in `Assets/CoreAI/Docs` was translated to English and the `_RU` doc mirrors were removed.

## [4.2.0] - 2026-06-13

- **Full-tier member visibility split.** `CoreAiFullUnityLuaRuntimeBindings` now exposes only **public** members by default; non-public access is an explicit opt-in (`allowNonPublicMembers` ctor flag). The reflection member cache is keyed by visibility so public-only and private-enabled bindings never collide.
- **Full Lua Mode guidance.** The built-in Programmer prompt and `execute_lua` metadata now document the diagnostic-first Full workflow: inspect with one-shot Lua, read `Success` / `Output` / `Error`, then use `manage_mods` for persistent hook/timer behavior. The guidance explicitly forbids invented Lua APIs such as `game.enemies`, `game.create`, and `GameObject.Find`.
- **Release sync.** `com.neoxider.coreai` is bumped to `4.2.0` to stay version-aligned with the Unity package's mod-driven Unit Forge / Full Access demos and the optional-module editor tool.

## [4.1.0] - 2026-06-12

- **Lua mod lifecycle metadata for host managers.** `LuaModRuntime.ModSourceUnloaded` now reports the unloaded source and capability tier, allowing host UIs to move a mod from active to saved/inactive state without losing source code.
- **Release sync.** `com.neoxider.coreai` is bumped to `4.1.0` so the portable core and Unity package stay version-aligned for the new wave auto-battler mod-management demo.

## [4.0.8] - 2026-06-12

- **Lua mod host persistence hooks.** `LuaModRuntime` now raises `ModSourceLoaded` after successful `LoadMod`/`ReloadMod` and `ModSourceUnloaded` after `UnloadMod`, including automatic unloads. The runtime still does not autoload arbitrary mod source by itself; hosts and demo scenes can now persist their selected mod set without coupling that policy into the generic Lua runtime.

## [4.0.7] - 2026-06-12

- **Release sync.** `com.neoxider.coreai` is bumped to `4.0.7` so portable CoreAI and `com.neoxider.coreaiunity` remain version-aligned. Unity-side LiveMechanics persistence and docs changes are listed in the Unity package changelog.

## [4.0.4] - 2026-06-12

- **Lua tool contract accuracy.** `execute_lua` metadata no longer advertises scene-specific helper globals such as `create_item()` as if they were always available. The tool now points Programmer agents at the real generic rule-slot APIs (`logic_list`, `logic_define`, `logic_reset`, `report`) and includes a working `loot_formula` example for live-mechanics edits.
- **MoonSharp callback guidance.** `manage_mods` metadata now shows valid Lua callback syntax for `hooks_on('event', function(...) ... end)` and `hooks_every(seconds, function() ... end)`, preventing invalid `hooks_on('event') function() ... end` mod code.

## [4.0.3] - 2026-06-12

- **Tool schema repair feedback.** `ToolExecutionPolicy` now validates required arguments from each tool's `ParametersSchema` before invoking the MEAI function binding. Malformed calls such as `manage_mods` with `{}` now return a normal failed tool result that names the missing `action` argument and includes the expected JSON schema, so the Programmer can retry with corrected arguments instead of receiving a low-level `AIFunctionFactory` exception.

## [4.0.2] - 2026-06-12

- **Tool-only chat failure fallback.** `AiOrchestrator` now preserves terminal `ExecutedToolCalls` from streaming completions and turns empty tool-only responses into an explicit tool status message. Failed `Programmer` tool turns now surface the real tool error, for example `manage_mods 'load' failed: attempt to index a function value`, instead of running structured validation and showing `Response is empty or whitespace`.
- **Tool trace diagnostics.** `LlmToolCallTrace` now carries a short `Detail` string for failed native, missing, unknown, duplicate, and timeout tool calls so UI fallbacks and logs can report the actual failure cause.

## [4.0.1] - 2026-06-12

- **Chat source history for tool roles.** `AiOrchestrator` now enables short-term chat history for requests with `SourceTag = "Chat"` even when the target role defaults to history-off (for example `Programmer`). The global role policy is not mutated and disk persistence stays off unless the role explicitly enables it, so non-chat Lua/repair tasks remain isolated while chat panels keep session instructions such as response language.

## [4.0.0] - 2026-06-12

Major release: Lua as a second game language (production-ready), capability tiers, Full opt-in mode, LLM mod tools, demo scenes, and performance hardening.

### Breaking / API

- **`LuaCapabilities.All` no longer includes `Full`.** Full reflection access requires explicit `LuaCapabilities.Full` (host opt-in via `CoreAILifetimeScope.enableFullLuaAccess` or per-mod caps).
- **`ICapabilityScopedLuaBindings`** — binding providers can gate APIs by capability tier; `AggregatingGameLuaRuntimeBindings` implements it.
- **`GameLuaBindingsExtensibility.Register(bindings, requiredCapabilities)`** — extensions declare minimum capability flags.
- **`CoreAiPrefabRegistryAsset.OnValidate`** — invalidates internal prefab cache when edited (fixes stale MCP/asset patches).

### Lua runtime & security

- **`LuaLogicSlots`**, **`LuaModRuntime`** (atomic reload, consecutive error budget, capability-scoped game APIs).
- **`LuaModsLlmTool` (`manage_mods`)** — list/get_source/load/reload/unload; `LuaModRuntime.TryGetModSource`.
- **`GameLuaToolExecutor` + DI** — `execute_lua` / `manage_mods` registered for built-in **Programmer** role in `WorldCommandsInstaller`.
- **`CoreAiFullUnityLuaRuntimeBindings`** — Full-tier `unity_*` reflection APIs (allow-all; planned blacklist documented).
- Scene whitelist: **`luaAllowedScenes`** on `CoreAILifetimeScope` → `coreai_world_load_scene`.
- Sandbox: rate limits, output caps, capability fail-closed for restricted mods.
- `LuaApiRegistry` now exposes callbacks through MoonSharp `CallbackFunction` wrappers, so host validation failures surface to Lua as `ScriptRuntimeException` instead of leaking raw CLR exceptions.

### World commands

- **`ICoreAiCustomWorldCommandHandler`** + `CoreAiWorldCommandExecutor.RegisterCustomHandler` — extend world actions from game code.
- **`set_color`** uses **`MaterialPropertyBlock`** (fixes material instance leak).

### Demos (`Assets/CoreAI.Demos/`)

- LuaMods, WorldCommands, Skills, LiveMechanics (LLM + chat). FullAccess: controller + README at this release; `FullAccessDemo.unity` scene + PlayMode smoke completed in a later release (done).

### Performance

- `LuaModRuntime.Tick` — reusable mod list scratch buffer (no per-frame array alloc).
- See **`Docs/PERF_REVIEW_2026-06-12_RU.md`**.

### Diagnostics

- CoreAiUnity runtime: direct `Debug.*` replaced with **`IGameLogger` / `GameLoggerUnscopedFallback`** (`CoreAi.cs`, chat panels, `LuaCoroutineRunner.SetLogger`, etc.).

### Docs

- `LUA_GAME_API.md`, `LUA_BEST_PRACTICES_RU.md`, `MOONSHARP_NATIVE_APIS_RU.md`, `LUA_ACCESS_MODES.md`, demo READMEs, perf review.

## [v3.2.0] - 2026-06-11

### API design

- **`RoleId`** — strongly-typed agent role identifier (`readonly struct`, ordinal equality, `IsBuiltIn`, statics for all built-in roles like `RoleId.SmartChat`). Implicitly convertible to/from `string`, so it works with every existing API (`AgentBuilder`, `AiTaskRequest.RoleId`, `CoreAi.AskAsync`) without overloads. Inline `"SmartChat"` literals in the runtime replaced with `BuiltInAgentRoleIds.SmartChat`.
- **`AskWithCallback` replaces `Ask` as the fire-and-forget convenience.** The primary idiom is awaitable `AskAsync`; the callback overload is now explicitly named `AskWithCallback(message, onDone?, priority)`. The old `Ask(...)` remains as an `[Obsolete]` alias.

### Lua sandbox

- **Generation rate limit (runaway-loop guard).** New `LuaGenerationRateLimiter` (sliding window, default 20/60 s, injectable clock/limits, `maxPerWindow <= 0` disables) wired into `LuaAiEnvelopeProcessor`: both envelope executions and scheduled Programmer repair generations consume slots. A saturated window fails the envelope with a `Lua rate limit exceeded` message and skips repair scheduling, so a failing script cannot spin a generate→fail→repair loop against the LLM. Per-script instruction/time budgets (`InstructionLimitDebugger`) unchanged.

### Diagnostics

- **`TokenBudgetTextFormatter`** — pure (UnityEngine-free) text layer extracted from the Unity token-budget overlay: `FormatTokens` / `FormatCost` / `FormatLoad` (+ `nearLimit` flag) render the same diagnostic strings for any UI (IMGUI overlay, custom UGUI panels, logs). Covered by new EditMode tests.

## [v3.1.0] - 2026-06-10

### Reliability

- **Retry backoff now uses full jitter.** `LoggingLlmClientDecorator` retry delays are drawn uniformly from `[0, base]` where base is the previous exponential `min(2 * 2^attempt, 30)` seconds, so fleets of agents no longer retry in lockstep after a mass 429 (thundering-herd fix). Explicit `Retry-After` headers still take precedence. Delay computation is exposed as `ComputeBackoffBase` / `ComputeBackoffDelay` for testability.
- **Tool-name repair metric.** `ToolExecutionPolicy.ToolNameRepairCount` (process-wide, `Interlocked`) counts casing repairs performed by `TryRepairToolName`, making systemic prompt degradation observable; `ResetToolNameRepairCount()` for test/session resets.
- **Retry error-feedback reclaimed from history.** After a fully-failed tool-call batch is retried successfully, `SmartToolCallingChatClient` removes the obsolete error-feedback message pairs (assistant tool-call + tool result, removed as whole pairs so the history stays OpenAI-valid) instead of letting them consume tokens until the general trim. Partially-failed batches are kept, since their successful results may still inform the model.

### Lua sandbox

- **Two escape vectors closed.** `StripRiskyGlobals` now also removes `string.dump` (MoonSharp implements it — compiled-bytecode leak; nilling it in the shared string table also blocks `('x'):dump()`) and `collectgarbage` (heap/timing oracle stub).
- New escape-vector EditMode tests: `string.dump` (direct and via string metatable), `coroutine.close`, `collectgarbage`, `getmetatable('')`, `rawget`/`_G` bypass attempts.

### Agent memory

- **Off-main-thread async I/O.** `FileConversationSummaryStore` gains `LoadSummaryAsync` / `SaveSummaryAsync` / `ClearSummaryAsync` that run file I/O on the thread pool, serialized with the sync paths via a per-store `SemaphoreSlim`. Atomic tmp-file write semantics unchanged; `ConfigureAwait(false)` throughout; WebGL falls back to inline execution (no threads).

### Diagnostics

- New `TokenBudgetCalculator` (pure, testable) backing the Unity-side token-budget overlay: tokens/request, optional $/session from configurable per-1K prices, rolling-window request-load aggregation.

## [v3.0.0] - 2026-06-10

### Major — Lua/MoonSharp is now an optional module

- **`COREAI_NO_LUA` scripting define.** Defining `COREAI_NO_LUA` compiles the entire Lua sandbox out of both `CoreAI.Core` and `CoreAI.Source`, exactly mirroring the existing `COREAI_NO_LLM` opt-out convention. Core orchestration, LLM, chat, and agent memory build and run with no MoonSharp usage; with the define set you may also remove the `org.moonsharp.moonsharp` package.
- Whole-file guarded under `#if !COREAI_NO_LUA`: `SecureLuaEnvironment`, `LuaCoroutineHandle`, `LuaApiRegistry`, `LuaExecutionGuard`, `InstructionLimitDebugger`, `LuaAiEnvelopeProcessor` (Core) and `LuaCoroutineRunner` (Source).
- **Graceful no-op when disabled.** `CorePortableInstaller` and `WorldCommandsInstaller` skip Lua registrations under the define; `WorldCommandsInstaller` falls back to the Core-side `CoreDefaultLuaRuntimeBindings` / `NullLuaExecutionObserver` so the DI graph still resolves. `AiGameCommandRouter`'s `LuaAiEnvelopeProcessor` dependency is compiled out (no longer a hard constructor dependency) so command routing degrades to world-command execution only.
- Lua/MoonSharp EditMode and PlayMode tests are guarded so both build configurations compile. Verified: default build (Lua on) and `COREAI_NO_LUA` build both compile with zero errors.

### Reliability hardening (code audit follow-up)

- **`HttpClientOpenAiTransport` — socket-exhaustion fix.** Replaced per-request `new HttpClient` (disposed every call, sockets stuck in `TIME_WAIT`) with shared `Lazy<HttpClient>` instances over an `HttpClientHandler`. Per-request timeouts are now enforced via a linked `CancellationTokenSource` instead of mutating the shared client's `Timeout`; streaming no longer disposes the shared client. (`HttpClientHandler` is used rather than `SocketsHttpHandler` so the transport stays valid on Unity's .NET Standard 2.0 profile.)
- **Crash-safe atomic JSON writes.** `FileAgentMemoryStore` (4 write sites) and `FileConversationSummaryStore` now write to a `.tmp` file and `File.Replace`/`File.Move` into place, so a crash mid-write can no longer corrupt agent memory or conversation summaries.
- **`LuaCoroutineHandle.Kill()` — real termination.** Replaced the empty `try/catch` (which only set `_disposed`) with a forced yield via MoonSharp `Coroutine.AutoYieldCounter`, plus typed exception handling; `_disposed` guarantees the coroutine is no longer resumable.

### Fixes

- **`CoreAIFacade` portable-Core regression.** Removed a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (`UnityEngine`) attribute that had been added to the UnityEngine-free `CoreAI.Core` assembly and broke its compilation. The Play Mode / domain-reload static reset of `CoreAIAgent` now lives in the Unity layer (`CoreAi.Invalidate()` calls `CoreAIAgent.Reset()`).
- **`AgentConfigExtensions.AskAsync` validation order.** Role-registration validation now runs *before* the orchestrator-null check, so an unregistered role reports the clear `role not registered` error regardless of whether the orchestrator is initialized yet (the 2.6.5 fail-fast test previously never compiled and so never caught this).
- Timeout now surfaces as `OperationCanceledException` without an inner `TimeoutException` (HTTP transport change above).

### Core policy registration safety (carried from 2.6.5 dev)

- `AgentBuilder.Build()` applies role configuration to `CoreAIAgent.Policy` when policy is already initialized; `BuildDetached()` for policy-free construction.
- `AgentConfigExtensions` fail-fast coverage for unregistered roles; `CoreAi.SetResolver` edit-mode coverage.

### Semver

- **Major bump to `3.0.0`** (lockstep with `com.neoxider.coreaiunity` `3.0.0`): Lua becoming an optional, compile-out module is a structural change to how the packages are consumed.

## [v2.6.5] - 2026-06-10

### Policy registration and orchestration safety

- Tightened `AgentBuilder` API so `Build()` now applies role config to `CoreAIAgent.Policy` by default; added `BuildDetached()` for detached construction without global side effects.
- Added `AgentMemoryPolicy.HasRole(string roleId)` for explicit role-registration checks.
- Added explicit role validation in `AgentConfigExtensions.AskAsync(...)` so unregistered roles fail fast with a clear `role not registered` error instead of implicit fallback behavior.

## [v2.6.4] - 2026-06-06

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.4` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the backend-managed authorization, streaming tool-loop completion, and chat collapse idempotency fixes live in `com.neoxider.coreaiunity`.

## [v2.6.3] - 2026-06-01

### Chat options parity with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.3` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Added portable chat options `EnableStopGeneration` and `ShowClearButton`. Unity consumes these through `CoreAiChatConfig` / `CoreAiChatPanel`; the portable package remains Unity-free.
- Defaults preserve existing behavior: stop generation is enabled and the clear button is shown unless a host explicitly disables them.
## [v2.6.2] - 2026-06-01

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.2` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Portable package metadata documents the WebGL streaming continuation fix; the runtime and verification changes for WebGL chat Stop/recovery live in `com.neoxider.coreaiunity`.

## [v2.6.0] - 2026-05-29

### WebGL streaming and Lua platform guard

- Bumped `com.neoxider.coreai` to `2.6.0` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching minor versions.
- `MeaiOpenAiChatClient` now treats OpenAI-style `data: [DONE]` SSE frames as terminal stream sentinels. WebGL native streaming can finish promptly without waiting for the browser connection to close.
- `SecureLuaEnvironment` now exposes an explicit platform support guard. WebGL player builds report Lua as unsupported before MoonSharp can initialize reflection-heavy loader paths that crash IL2CPP/WebGL.
- `LuaAiEnvelopeProcessor` now publishes a controlled Lua failure when the runtime is unavailable instead of constructing the sandbox on unsupported platforms.
- Updated Lua sandbox documentation to state that Lua is temporarily unavailable on WebGL and to describe the supported future restoration paths.

## [v2.5.4] - 2026-05-29

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.4` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the WebGL SSE cancellation and Editor Play Mode main-thread marshaling hardening live in `com.neoxider.coreaiunity`.

## [v2.5.3] - 2026-05-27

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.3` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the Unity fixes live in `com.neoxider.coreaiunity`.

## [v2.5.1] - 2026-05-25

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.1` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Added portable `IAIFunctionLlmTool` / `IAIFunctionsLlmTool` contracts so Unity MEAI binding can discover tool functions without reflection duck typing.

## [v2.5.0] - 2026-05-24

### Version Parity With CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.0` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Updated the Unity package dependency contract to `com.neoxider.coreai` `2.5.0`.
- No additional portable runtime behavior change beyond the release-train alignment for the Unity ScriptableObject wrapper and options/snapshot work.

## [v2.4.0] - 2026-05-24

### Portable Options and Snapshot Contracts

- Added Unity-free runtime options/snapshots for Unity-authored configuration: `CoreAiChatOptions`, `CoreAISettingsOptions`, `OpenAiHttpOptions`, `GameLogSettingsOptions`, `AiPermissionsOptions`, `AgentPromptsDefinition`, and `SkillSetDefinition`.
- Moved Unity-free logging contracts (`GameLogFeature`, `GameLogLevel`, `IGameLogSettings`) into the portable CoreAI package.
- Preserved the rule that `Assets/CoreAI` has no `UnityEngine` dependency; Unity-specific authoring stays in `com.neoxider.coreaiunity`.

### Migration Notes

- Runtime/tests should prefer plain options/classes over mutating Unity `ScriptableObject` assets.
- Unity assets remain supported through wrapper methods in `com.neoxider.coreaiunity`.

## [v2.3.1] — 2026-05-08

### LLMUnity Text-Mode Tool Calling

Local GGUF models (Qwen3.5-4B via LLMUnity/llama.cpp) output tool calls as plain text instead of native `FunctionCallContent`. This release ensures the full SkillSet pipeline works end-to-end on text-only backends.

#### `LlmToolCallTextExtractor`

- **Function-call syntax fallback** — `read_skill("Alchemy")`, `read_skill(Crafting)`, `call_skill_tool("tool", '{"args":"..."}')` are now parsed into `Match` objects. Matches only when the entire trimmed response looks like a function call (prose with parentheses is ignored).
- **`arguments_json` key** — `LooksLikeToolCallJson` and `TryExtract` now accept `"arguments_json"` as an alternative to `"arguments"` (Qwen3.5 emits this non-standard key).
- **String-value args re-parsing** — when `"arguments_json"` contains a serialized JSON string (e.g. `"{\"skill_name\":\"Alchemy\"}"`), the value is re-parsed into a proper JSON object before extraction.

#### `ToolExecutionPolicy`

- **JObject → string normalization** — `ExecuteSingleAsync` now normalizes `Newtonsoft.Json.Linq.JObject` and `JArray` values in `FunctionCallContent.Arguments` to JSON strings before calling `AIFunction.InvokeAsync`. This is the **single chokepoint** for all tool calls (native, text-extracted, function-call syntax), ensuring MEAI delegates with `string` parameters never receive raw Newtonsoft tokens.

#### `CallSkillToolLlmTool`

- **`InvokeDelegateWithJson`** — when a delegate parameter expects `System.String` but the JSON token is `JObject`/`JArray`, serialize to `Formatting.None` string instead of throwing `InvalidCastException`.

#### `SmartToolCallingChatClient` / `MeaiLlmClient`

- **`NormalizeJTokenValues`** helper — converts `JObject`/`JArray` values in argument dictionaries to JSON strings, applied in both streaming and non-streaming text extraction paths.
- **`IsValidToolCallJson`** (streaming) — now accepts `"arguments_json"` key.

## [v2.3.0] — 2026-05-08

### Dual-Backend with Auto-Fallback

- **`FallbackLlmClientDecorator`** — new decorator wrapping primary + secondary `ILlmClient`. When the primary backend fails (exception, `BackendUnavailable`, `RateLimited`, `Timeout`, `ProviderError`, `ContextLengthExceeded`), the request is automatically retried on the secondary. User cancellation (`OperationCanceledException`) is never retried.
- **Streaming fallback** — if the primary streaming enumerator throws on the first chunk, the decorator falls back to secondary streaming transparently.
- **`FallbackCount`** property — tracks how many times the secondary was invoked.

### Inspector: Fallback Backend

- **`CoreAISettingsAsset`** — new **🔄 Fallback Backend (secondary)** section:
  - `enableFallbackBackend` — master toggle.
  - `secondaryApiBaseUrl` — secondary HTTP endpoint.
  - `secondaryApiKey` — secondary API key.
  - `secondaryModelName` — secondary model identifier.
- **`HasValidFallbackBackend`** computed property — true when toggle is on AND URL + model are set.
- **`LlmPipelineInstaller`** — when `HasValidFallbackBackend` is true, the primary `ILlmClient` is wrapped in `FallbackLlmClientDecorator` with a secondary `OpenAiChatLlmClient` built from `SecondarySettingsAdapter`.

### Tests

- 5 new EditMode tests: `Fallback_PrimarySucceeds_SecondaryNotCalled`, `Fallback_PrimaryFails_SecondaryIsCalled`, `Fallback_PrimaryReturnsRetryableError_SecondaryIsCalled`, `Fallback_Cancellation_DoesNotFallback`, `Fallback_MultipleFails_CounterIncrements`.

## [v2.2.0] — 2026-05-08

### Tool Call History Truncation

- **`MaxToolCallHistoryMessages`** (default 20) — `SmartToolCallingChatClient` now trims the oldest tool call message pairs (Assistant + Tool result) from the MEAI message list during long tool-calling loops. Prevents unbounded context growth within a single request.
- When the count exceeds the limit, the oldest pairs are removed while preserving system and user messages.
- Setting exposed in `ICoreAISettings`, `CoreAISettings` static proxy, and `CoreAISettingsAsset` Inspector (🛡️ Resilience & Safety). `0` = no limit.

### Rate Limiter Metrics

- **`RateLimiterMetrics`** struct — snapshot of rate limiter state: `MaxRequestsPerWindow`, `WindowSeconds`, `AcceptedInWindow`, `TotalRejected`.
- **`IInGameLlmChatService.GetRateLimiterMetrics()`** — exposes sliding-window rate limiter diagnostics for dashboard / UI display.
- `InGameLlmChatService` now tracks `TotalRejected` count.

### Tool-Level Retry (clarification)

- `maxConsecutiveErrors` already works globally across all tools in `ToolExecutionPolicy`. Per-tool granularity is unnecessary for the current architecture — the global counter resets on any successful execution, which handles mixed-tool scenarios correctly.

## [v2.1.0] — 2026-05-08

### Production Resilience — Runtime Safety Guardrails

Four runtime guardrails to prevent context overflow, infinite hang-loops, and runaway model generation.

#### New settings (`ICoreAISettings` / `CoreAISettings` / Inspector)

| Setting | Default | Location |
|---------|---------|----------|
| **`MaxToolResultChars`** | `8000` | `ToolExecutionPolicy` — soft-truncates tool result strings before they re-enter the LLM context window. |
| **`DefaultToolTimeoutMs`** | `30000` | `ToolExecutionPolicy` — wraps each tool invocation in a linked `CancellationTokenSource`; if the tool (e.g. HTTP call) hangs, the timeout fires and returns an error result instead of blocking forever. |
| **`MaxResponseChars`** | `0` (disabled) | `SmartToolCallingChatClient` — when > 0, truncates final assistant text to prevent runaway generation. |
| **`MaxToolCallRoundtrips`** | `10` | `SmartToolCallingChatClient` — hard cap on tool-calling loop iterations; prevents infinite recursive tool calling. |

#### Design

- **Centralized enforcement.** Timeout + truncation live in `ToolExecutionPolicy` (covers native + text-extracted calls); roundtrip + response limits live in `SmartToolCallingChatClient`.
- **Zero breaking changes.** All features are additive with safe defaults; existing agents behave identically unless settings are overridden.
- **Inspector integration.** All four settings exposed in **CoreAISettingsAsset** under **🛡️ Resilience & Safety** foldout with tooltips and min-value constraints.

#### Tests

- **`ResilienceFeaturesEditModeTests`** — 8 tests validating truncation, timeout, and roundtrip limits independently of LLM backends.

#### Documentation

- **`README.md`**, **`README_RU.md`**, **`CoreAiUnity/README.md`** — resilience bullet points.
- **`AGENT_BUILDER.md`** — Resilience & Safety section with usage examples.

## [v2.0.0] — 2026-05-08

### Major — Skill-Based Tool Orchestration

Introduces **`SkillSet`** — named groups of tools with dedicated prompt instructions, inspired by the **Microsoft Semantic Kernel `KernelPlugin`** pattern. Skills reduce context bloat by injecting only the active skill's instructions into the system prompt at request time.

#### New public API

- **`SkillSet`** (`CoreAI.Ai`) — immutable container: `Name`, `Instructions` (prompt text), `Tools` (`IReadOnlyList<ILlmTool>`), `ToolNames` (cached `string[]` for `AllowedToolNames`).
  - Constructor: `new SkillSet(name, instructions, params ILlmTool[] tools)`.
  - `FromFile(name, filePath, tools)` — load instructions from a `.txt` / `.md` file on disk.
  - `FromTextContent(name, text, tools)` — load instructions from pre-loaded text (e.g. Unity `TextAsset.text`).
  - `MergeToolNames(params SkillSet[])` — combine multiple skills into one allowlist.
  - `BuildActiveInstructions(params SkillSet[])` — compose `## Skill: {Name}` prompt sections from active skills.
- **`AgentBuilder.WithSkill(SkillSet)`** / **`WithSkills(params SkillSet[])`** — register skill tools and instructions in the fluent builder. Tools are added to the agent's tool list; skills are stored on `AgentConfig.Skills`.
- **`AgentConfig.Skills`** (`IReadOnlyList<SkillSet>`) — skills registered via `WithSkill`. Null when no skills.
- **Skill runtime context provider** (internal at the time) — previously injected only the matching skills' instructions into the system prompt. Current builds keep the lightweight skill catalog in the stable system prefix and load full instructions through `read_skill`.

#### Design

- **Zero orchestrator changes.** Uses existing `AllowedToolNames` + `FilterToolsForRequest()` for tool filtering and existing `IAgentRuntimeContextProvider` + `AiPromptComposer.AppendRuntimeContext()` for instruction injection.
- **Zero new dependencies.** Pattern inspired by Semantic Kernel's `KernelPlugin`, implemented purely on CoreAI's existing abstractions.
- **Backwards compatible.** Agents without skills behave identically to v1.x.

#### Usage example

```csharp
var quizSkill = new SkillSet("Quiz",
    instructions: "When quiz is active, generate questions using spawn_quiz. " +
                  "Wait for the answer, then verify with check_answer.",
    new DelegateLlmTool("spawn_quiz", "Create quiz", (string q) => ...),
    new DelegateLlmTool("check_answer", "Check answer", (int idx) => ...)
);

var lessonSkill = new SkillSet("Lesson",
    instructions: "Explain concepts step by step. Use advance_lesson to proceed.",
    new DelegateLlmTool("advance_lesson", "Move to next topic", () => ...)
);

var teacher = new AgentBuilder("Teacher")
    .WithSystemPrompt("You are a teacher.")
    .WithSkill(quizSkill)
    .WithSkill(lessonSkill)
    .WithMemory()
    .Build();

teacher.ApplyToPolicy(policy);

// Activate only quiz tools + instructions for this turn:
await orch.RunTaskAsync(new AiTaskRequest {
    RoleId = "Teacher",
    AllowedToolNames = quizSkill.ToolNames
});
```

#### Tests

- **`SkillSetEditModeTests`** — tests covering: SkillSet construction, instruction injection, per-request filtering, MergeToolNames, and AgentBuilder.WithSkill integration.

### Semver

- **`2.0.0`** with **`com.neoxider.coreaiunity` `2.0.0`**. Major bump — new public API surface (`SkillSet`, `AgentConfig.Skills`, `AgentBuilder.WithSkill/WithSkills`).

## [v1.7.5] — 2026-05-05

### Lockstep with coreaiunity 1.7.5 (Unity-only)

- **Semver:** **`1.7.5`** with **`com.neoxider.coreaiunity` `1.7.5`**. No portable **`CoreAI.Core`** API changes — Unity release adds optional chat tool-call UI and renames **`CoreAISettingsAsset`** temperature override field to **`enableTemperatureOverriding`** (see Unity changelog).

## [v1.7.4] — 2026-05-05

### Lockstep with coreaiunity 1.7.4 (Unity-only)

- **Semver:** **`1.7.4`** with **`com.neoxider.coreaiunity` `1.7.4`**. No portable **`CoreAI.Core`** API changes — Unity release documents LLMUnity runtime host defaults (see Unity changelog).

## [v1.7.3] — 2026-05-05

### Streaming request option (lockstep with coreaiunity 1.7.3)

- **`LlmCompletionRequest.BufferFullStreamingIterationWhenToolsDeclared`** — optional **`bool?`**. When **`Tools`** is non-empty: **`true`** buffers the full assistant iteration before emitting any **`LlmStreamChunk.Text`**; **`null`**/**`false`** (default) keeps the **hybrid JSON hold** (stream only the prefix that cannot be part of incomplete text-shaped tool JSON, then hold until balanced **`{...}`** closes). Intended as an escape hatch for exotic delta fragmentation; Unity **`MeaiLlmClient`** implements both modes.
- **Semver:** **`1.7.3`** with **`com.neoxider.coreaiunity` `1.7.3`**.

## [v1.7.2] — 2026-05-05

### Lockstep with coreaiunity 1.7.2 (WebGL)

- **Semver:** **`1.7.2`** with **`com.neoxider.coreaiunity` `1.7.2`**. No portable **`CoreAI.Core`** API changes — Unity **`CoreAiPersistFs.jslib`** now runs **`FS.syncfs`** single-flight (queues coalesced follow-up) so concurrent **`CoreAi_PersistFsSync`** calls from **`FileAgentMemoryStore`** no longer trigger Emscripten’s *“2 FS.syncfs operations in flight”* warning or related WebGL stalls.

## [v1.7.1] — 2026-05-05

### Lockstep & tests

- **Semver:** **`1.7.1`** with **`com.neoxider.coreaiunity` `1.7.1`**. No portable API changes — Unity EditMode adds **`FailedCompletion_BackendUnavailable_RetriesAndSucceeds`** for **`LoggingLlmClientDecorator`** (result-based **`BackendUnavailable`** retry, same as **`RateLimited`** in v1.7.0).

## [v1.7.0] — 2026-05-05

### Streaming — `LlmStreamChunk` marker for buffered Meai iterations

- **`LlmStreamChunk`** — **`BufferedStreamingNoToolBinding`** plus optional **`BufferedStreamingUseToolProgressHint`**. **`MeaiLlmClient.CompleteStreamingAsync`** yields marker chunks for unbound iterations, hybrid JSON hold, native tool deltas, and text-shaped tool execute (host chat: short **`StreamingToolProgressHint`** vs animated dots — see **`com.neoxider.coreaiunity` ≥ 1.7.0**).
- **Sampling temperature:** **`ICoreAISettings.OverrideTemperature`** (default **off**). When off, **`MeaiOpenAiChatClient`** omits the JSON **`temperature`** field and **`MeaiLlmClient`** does not set MEAI **`ChatOptions.Temperature`** (HTTP + LLMUnity use backend defaults). When on, **`AiOrchestrator`** sets **`LlmCompletionRequest.SendTemperature`** and sends **`ICoreAISettings.Temperature`**. **`LlmCompletionRequest.SendTemperature`** is also set for LLM-assisted compaction. **`ConfigureHttpApi`** enables the override flag so programmatic HTTP setup still sends temperature.
- **HTTP retries:** **`LoggingLlmClientDecorator`** now retries **`LlmCompletionResult`** with **`RateLimited`** / **`BackendUnavailable`** (same backoff as for **`LlmClientException`**). Previously only thrown exceptions retried; **`MeaiLlmClient`** converts HTTP errors to failed results, so 429 produced no **`LLM ↺`** lines and no second attempt. Default **`ICoreAISettings.MaxLlmRequestRetries`** / asset field is **1** retry (minimum clamp **1**).

## [v1.6.19] — 2026-05-05

### Lockstep with coreaiunity 1.6.19 (Unity-only)

- **Semver:** **`1.6.19`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`CoreAILifetimeScope`** registers **`FileAgentMemoryStore`** on WebGL player so chat history and agent memory JSON persist (with existing **`CoreAi_PersistFsSync`** after writes).

## [v1.6.18] — 2026-05-04

### Lockstep with coreaiunity 1.6.18 (Unity-only)

- **Semver:** **`1.6.18`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`FetchSseOpenAiTransport`** uses synchronous **`TaskCompletionSource`** continuations + true async **`ReadAsync`** so WebGL single-threaded awaits no longer park forever on a non-existent thread pool, and **`Stream.Read`** no longer blocks the JS event loop while waiting for fetch chunks.

## [v1.6.17] — 2026-05-04

### Lockstep with coreaiunity 1.6.17 (Unity-only)

- **Semver:** **`1.6.17`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** now await the real **`fetch`** response status before returning, so **`MeaiOpenAiChatClient`** sees the actual HTTP code instead of the default **`HTTP 0`** that was masking CORS / network errors as transport failures.

## [v1.6.16] — 2026-05-04

### Lockstep with coreaiunity 1.6.16 (Unity-only)

- **Semver:** **`1.6.16`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity WebGL **`fetch`** default **`credentials: 'omit'`** for SSE (OpenRouter + CORS `*`).

## [v1.6.15] — 2026-05-04

### Lockstep with coreaiunity 1.6.15 (Unity-only)

- **Semver:** **`1.6.15`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`CoreAISettingsAssetEditor`** moves WebGL streaming toggles under **Advanced**.

## [v1.6.8] — 2026-05-03

### Orchestration — scope cancel and `Task.IsCanceled`

- **`QueuedAiOrchestrator`** — handle **`TaskCanceledException`** explicitly (before **`OperationCanceledException`**) in **`RunOneAsync`** and **`RunOneStreamingAsync`**. When the inner **`RunTaskAsync` / `RunStreamingAsync`** await completes with **`TaskCanceledException`** (e.g. **`TaskCompletionSource.TrySetCanceled()`** on a gate task), the queued task must complete as **canceled**, not **faulted**; **`CancelTasks`** on an active scoped task then reports **`Task.IsCanceled == true`** as expected by **`QueuedAiOrchestratorEditModeTests`**.

### Semver

- Lockstep **`1.6.8`** with **`com.neoxider.coreaiunity`**.

## [v1.6.7] — 2026-05-03

### Lockstep with coreaiunity 1.6.7 (Unity-only)

- **Semver:** **`1.6.7`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`MeaiLlmClient`** incremental streaming + tests.

## [v1.6.6] — 2026-05-03

### Lockstep with coreaiunity 1.6.6 (Unity-only)

- **Semver:** **`1.6.6`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity chat streaming UI thread hop + clear button UXML.

## [v1.6.5] — 2026-05-03

### Lockstep with coreaiunity 1.6.5 (Unity-only)

- **Semver:** **`1.6.5`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity chat WebGL streaming gate alignment (**`CoreAiChatService`** / **`CoreAiChatPanel`**).

## [v1.6.4] — 2026-05-03

### WebGL browser — OpenAI-compatible HTTP headers vs public API CORS

- **`MeaiOpenAiChatClient.BuildTransportHeaders`** — when **`UNITY_WEBGL && !UNITY_EDITOR`**, omit **`X-Request-Id`**, **`Idempotency-Key`**, **`X-Coreai-Role`**, **`X-Tenant-Id`**, **`X-User-Id`**, and **`X-Session-Id`** (and skip the same names from **`IRequestHeaderProvider.GetHeaders()`**), so **`fetch`** preflight to gateways with a narrow **`Access-Control-Allow-Headers`** list (e.g. **openrouter.ai**) is not rejected before the POST runs. Trace and idempotency remain visible in **`LoggingLlmClientDecorator`** / **`RoutingLlmClient`** logs on the client; use a **same-origin proxy** or a backend that whitelists these headers when you need them on the wire in WebGL.

### Semver

- Lockstep **`1.6.4`** with **`com.neoxider.coreaiunity`**.

## [v1.6.3] — 2026-05-03

### Lockstep with coreaiunity 1.6.3 (Unity-only)

- **Semver:** **`1.6.3`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity host **`CoreAILifetimeScope`** registers **`FileAgentMemoryStore` in Editor even when the active build target is WebGL** (`#if !UNITY_WEBGL || UNITY_EDITOR`).

## [v1.6.2] — 2026-05-03

### Lockstep with coreaiunity 1.6.2

- **Semver:** lockstep **`1.6.2`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes in this drop (Unity: marshaler mirror + CraftingMemory / chat persistence tests + **`MaxRolledSummaryTokens`** deterministic compaction EditMode coverage — see Unity changelog).

## [v1.6.1] — 2026-05-03

### Chat history summarization controls (host settings)

- **`ICoreAISettings`** — **`EnableConversationHistorySummarization`** (default true), **`ConversationHistoryRecentTokenBudgetOverride`**, **`ConversationRolledSummaryMaxTokens`** (default interface implementations preserve legacy stubs).
- **`AiOrchestrator`** — applies the above when building **`ConversationContextBuildArgs`**; **`UnlimitedHistoryTokenBudget`** when summarization is disabled.
- **`ConversationContextBuildArgs.MaxRolledSummaryTokens`** — forwarded from settings; **`ConversationRolledSummaryLimiter`** truncates rolled summary text by **`ITokenEstimator`**.
- **`DeterministicConversationContextManager`** / **`LlmAssistedConversationContextManager`** — apply the rolled-summary cap before **`SaveSummary`** and when returning a stored-only snapshot.

### Semver

- Lockstep **`1.6.1`** with **`com.neoxider.coreaiunity`** (Unity: **`CoreAISettingsAsset`** fields + custom inspector foldout **Chat history summarization**; docs **`COREAI_SETTINGS.md`**).

## [v1.6.0] — 2026-05-03

### Minor release — server-managed protocol, ambient LLM context, WebGL SSE bridge

- **`LlmCompletionRequest.IdempotencyKey`** — optional; when empty, **`MeaiLlmClient`** assigns one key per request **instance** so decorator retries (e.g. **`RefreshOnUnauthorizedDecorator`**) reuse the same HTTP **`Idempotency-Key`**.
- **`IOpenAiHttpSettings`** — **`IRequestHeaderProvider? HeaderProvider`** for optional extra headers (defaults **`null`** on adapters until needed).
- **`LlmRequestContext`** — portable `AsyncLocal` ambient frame carrying `AgentRoleId`/`TraceId`/`IdempotencyKey`. **`MeaiLlmClient`** populates it on every `CompleteAsync`/`CompleteStreamingAsync` from `LlmCompletionRequest`; HTTP transports read it during header assembly without having to plumb the request through MEAI's `IChatClient` seam. Use `LlmRequestContext.Begin(...)` / `Scope` for nested manual frames.
- **`LlmAuthContextRegistry`** — portable static for `ILlmAuthContextProvider`. **`MeaiOpenAiChatClient`** emits **`X-Tenant-Id`** / **`X-User-Id`** / **`X-Session-Id`** from the registered provider on server-managed requests.
- **`MeaiOpenAiChatClient.BuildTransportHeaders`** — emits `Idempotency-Key` / `X-Request-Id` / `X-Coreai-Role` from `LlmRequestContext.Current`, then auth headers from `LlmAuthContextRegistry`, then any extra headers from **`IOpenAiHttpSettings.HeaderProvider`**. Earlier sources win; **`HeaderProvider`** idempotency/request-id only fill missing slots.
- **Documentation** — **`LLM_ROUTING.md`** entitlement contracts; **`SERVER_MANAGED_PROTOCOL.md`** wire contract and CORS/SSE checklist.

### Semver

- Lockstep **`1.6.0`** with **`com.neoxider.coreaiunity`** (Unity: WebGL fetch SSE, **`RefreshOnUnauthorizedDecorator`** hardening, **`LlmClientRegistry`** wrapping, validators — see Unity changelog).

## [v1.5.29] — 2026-05-03

### Lockstep with coreaiunity 1.5.29

- **Semver:** lockstep **`1.5.29`** with **`com.neoxider.coreaiunity`** (no Core-only API change in this drop).

## [v1.5.28] — 2026-05-02

### Remove legacy `PlayerChat` built-in role id

- **`BuiltInAgentRoleIds.PlayerChat`** removed — use **`PlainChat`** (simple chat, no MemoryTool by default) or **`SmartChat`** (chat + MemoryTool + persisted history).
- **`BuiltInAgentSystemPromptTexts.PlayerChat`** removed; prompts live under **`PlainChat`** / **`SmartChat`** only.
- **`CompositeRoleStructuredResponsePolicy`** routes **`PlainChat`** and **`SmartChat`** through **`PlayerChatResponsePolicy`** (free-form text).
- **`InGameLlmChatService`** uses **`SmartChat`** for system prompt + **`AgentRoleId`**.
- Demo / defaults: **`CoreAiChatConfig`** default **`RoleId`** is **`SmartChat`** (Unity package).
- **Semver:** lockstep **`1.5.28`** with **`com.neoxider.coreaiunity`**.

## [v1.5.27] — 2026-05-02

### Built-in chat role split: PlainChat + SmartChat

- **`BuiltInAgentRoleIds`** — added **`PlainChat`** and **`SmartChat`** built-in role IDs.
- **`BuiltInDefaultAgentSystemPromptProvider`** + **`BuiltInAgentSystemPromptTexts`** — new default system prompts for both chat roles.
- **`AgentMemoryPolicy`** defaults:
  - **`PlainChat`**: persisted chat history ON, `MemoryTool` OFF.
  - **`SmartChat`**: persisted chat history ON, `MemoryTool` ON (`append`).
- **`LlmConversationalRolePolicy`** treats both **`PlainChat`** and **`SmartChat`** as conversational user-facing roles.
- **Semver:** lockstep **`1.5.27`** with **`com.neoxider.coreaiunity`**.

## [v1.5.26] — 2026-05-01

### HTTP SSE (`HttpClient`) — keep client until body is read

- **`HttpClientOpenAiTransport.OpenSseResponseStreamAsync`** no longer wraps **`HttpClient`** in **`using`** for the streaming path. Returning from the method disposed **`HttpClient`** immediately, which **canceled** the open SSE request (`The request was aborted: The request was canceled.`, `chunks=0`). **`OpenAiHttpSseOpenResult`** now owns **`HttpClient`** and disposes it **after** the content stream and **`HttpResponseMessage`**.
- **Semver:** lockstep **`1.5.26`** with **`com.neoxider.coreaiunity`**.

## [v1.5.25] — 2026-05-01

### WebGL-safe HTTP LLM — pluggable transport

- **`IOpenAiHttpTransport`**, **`OpenAiHttpPostRequest`**, **`OpenAiHttpPostResult`**, **`OpenAiHttpSseOpenResult`** — portable HTTP surface for **`/chat/completions`** without **`UnityEngine`** in the contract.
- **`HttpClientOpenAiTransport`** — default **`System.Net.Http`** implementation (SSE + non-stream); honors **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** in the Editor.
- **`MeaiOpenAiChatClient`** — requires **`IOpenAiHttpTransport`**; convenience ctor **`(settings, log)`** when **`!UNITY_WEBGL || UNITY_EDITOR`**. When **`SupportsSseStreaming`** is false, **`GetStreamingResponseAsync`** uses full JSON completion and **simulated** **`ChatResponseUpdate`** yields.
- **Semver:** lockstep **`1.5.25`** with **`com.neoxider.coreaiunity`** (Unity: **`UnityWebRequestOpenAiTransport`**, WebGL scene guard, docs, tests).

## [v1.5.24] — 2026-05-01

### OpenAI-compatible HTTP streaming (SSE) — local server compatibility

- **`MeaiOpenAiChatClient`** — SSE lines accept **`data:`** with or without a space after the colon (LM Studio / llama.cpp variants). **`ExtractDeltaUpdate`** falls back to **`choices[0].message`** and **`choices[0].text`** when **`delta.content`** is empty so streamed replies are not dropped.
- **Diagnostics** — log **HTTP status** and **Content-Type** immediately after response headers; **Warn** when the stream ends with **zero** parsed deltas (empty or non–OpenAI-shaped chunks).
- **Edit Mode** — extra **`MeaiOpenAiChatClientSseEditModeTests`** cases for `data:` variants and message-only chunks.
- **Semver:** lockstep **`1.5.24`** with **`com.neoxider.coreaiunity`** (Unity package: fullscreen chat option in **`CoreAiChatConfig`**).

## [v1.5.23] — 2026-05-01

### OpenAI-compatible MEAI HTTP — portable `HttpClient`

- **`MeaiOpenAiChatClient`** — moved to **`CoreAI.Infrastructure.Llm`** in portable **`CoreAI.Core`**: **`System.Net.Http.HttpClient`** for non-streaming and SSE (no **UnityEngine** / **UnityWebRequest**). **`await`** without **`ConfigureAwait(false)`** so synchronization context is preserved when the host sets one (e.g. Unity / WebGL main thread).
- **`IOpenAiHttpSettings`**, **`OpenAiHttpConstants`** — live next to the client in portable Core (Unity layer re-exports or implements the same settings surface).
- **`UNITY_EDITOR`:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** — optional **`HttpClient`** factory for EditMode tests with **`HttpMessageHandler`** mocks (**must be cleared after tests**).
- **Semver:** lockstep **`1.5.23`** with **`com.neoxider.coreaiunity`** (Unity package adds **`MeaiOpenAiChatClientHttpEditModeTests`**).

## [v1.5.22] — 2026-05-01

### Lockstep packaging (`com.neoxider.coreaiunity`)

- **Semver:** lockstep **`1.5.22`** with **`com.neoxider.coreaiunity`**. No portable **CoreAI.Core** API or behavior change; **v1.5.22** composition fix (**`RegisterCorePortable` / `IAgentMemoryStore`**) ships in the Unity package only.

## [v1.5.21] — 2026-05-01

### Portable Core — JSON + API hygiene

- **`FileConversationSummaryStore`** — serializes with **Newtonsoft.Json** only; **`System.Text.Json`** removed from **`CoreAI.Core`** asmdef precompiled references.
- **`LlmStructuredPayloadSanitizer`** — JSON/markdown fence helpers moved out of **`ProgrammerLuaResponseParser`** (renamed from duplicate **`LlmResponseSanitizer`** type in **`CoreAI.Ai`**); **`CoreAI.Infrastructure.Llm.LlmResponseSanitizer`** remains for system-prompt echo stripping.
- **`Log.Instance`** backing field is **`volatile`** for safer multi-threaded reads after composition.
- **`AgentConfigExtensions.Ask`** — fire-and-forget uses **`Task`** (`RunAskFireAndForgetAsync`) instead of **`async void`**.
- **Semver:** lockstep **`1.5.21`** with **`com.neoxider.coreaiunity`** (Unity changelog lists WebGL/chat/composition changes).

## [v1.5.20] — 2026-05-01

### Lockstep packaging (WebGL host composition)

- **Semver:** lockstep **`1.5.20`** with **`com.neoxider.coreaiunity`**. No portable **CoreAI.Core** API or **`FileConversationSummaryStore`** implementation change; **`CoreAILifetimeScope`** WebGL registration lives in the Unity package (**`InMemoryConversationSummaryStore`** instead of file-backed summaries).

## [v1.5.19] — 2026-05-01

### Agent memory — LLM compaction contract (documentation)

- **`LlmAssistedConversationContextManager`** — XML `<remarks>` state that the orchestrator’s **main** system prompt (role instructions, universal prefix, memory, tool contract) is **never** included in the auxiliary compaction `LlmCompletionRequest`; only transcript-related text goes into **`UserPayload`**, **`ChatHistory`** stays **null**, and **`LlmContextCompactionOptions.SystemPrompt`** supplies the summarizer instructions.
- **`LlmContextCompactionOptions.SystemPrompt`** — property docs clarify it is **compaction-only**, not the primary role system string.
- **Semver:** lockstep **`1.5.19`** with **`com.neoxider.coreaiunity`** (Unity package ships Edit/Play tests and settings docs for the same contract).

## [v1.5.18] — 2026-04-30

### Offline / stub UX and chat failures (portable Core)

- **`LlmConversationalRolePolicy`** — classifies roles that should get **short user-facing** replies in **stub/offline** flows (e.g. **`PlainChat`**, **`SmartChat`**, **`AINpc`**, ids containing **`teacher` / mentor / tutor`**, names ending with **`chat`**, excluding **`Merchant`**).
- **`StubLlmClient`** — conversational roles return **`[stub] Offline — LLM unavailable (stub).`** instead of echoing **`UserPayload`** or emitting JSON **`ApplyWaveModifier`** for custom ids like **`Teacher`**.
- **`AiOrchestrator.RunTaskAsync`** — when **`AiTaskRequest.SourceTag`** is **`Chat`**, LLM failure / empty result / authority denied returns a **short printable message** (error text or default) instead of **`null`**, so **`CoreAiChatService`** can show text in the bubble. Non-chat callers still get **`null`** on failure.
- **Semver:** lockstep **`1.5.18`** with **`com.neoxider.coreaiunity`**.

## [v1.5.17] — 2026-04-30

### Lockstep packaging (Unity — `UnityMainThreadLlmAsyncMarshaler`)

- **Semver:** lockstep **`1.5.17`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change.
- **`UnityMainThreadLlmAsyncMarshaler`:** **`Application.isPlaying`** is **never** read from non–script-main threads (`ManagedThreadId` vs **`onBeforeRender`** mirror). Avoids **`get_isPlaying` / AggregateException** on MEAI **`Task`/thread-pool paths** (`UnityMainThreadLlmAsyncMarshalerEditModeTests.InvokeAsync_WhenNotPlaying_CompletesUnderMainThreadWait_FromThreadPool`).

## [v1.5.16] — 2026-04-30

### Lockstep packaging (Unity — `UnityMainThreadLlmAsyncMarshaler`)

- **Semver:** lockstep **`1.5.16`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change.
- **`UnityMainThreadLlmAsyncMarshaler`** (Unity package): **`Application.isPlaying`** is not reliably readable from MEAI continuation **threads** (**main thread / `UnityException`**). Use a **`Application.onBeforeRender`** **mirror**: **edit-time / unknown** ⇒ same **inline** path as **`!playing`** (**`ToolCallExtractionParityEditModeTests`**); **mirror says Editor Play Mode** ⇒ **`UniTask.SwitchToMainThread`** (keeps **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** valid in the Editor).

## [v1.5.15] — 2026-04-30

### LLM — `SmartToolCallingChatClient` native tool calls vs MEAI **10.x** `ChatMessage.Contents`

- **`FlattenAssistantContents`** — walks assistant turns using non-generic **`IList`** contents (MEAI **`ChatMessage.Contents`**), instead of LINQ **`SelectMany(... ?? Enumerable.Empty<AIContent>())`**, which could yield **no** **`FunctionCallContent`** items → false “text-only” exits and **premature consecutive-error stops** (`EditMode` **`SmartToolCallingChatClientEditModeTests`** regressions).
- **`ConcatenateAssistantTextContents`** — enumerates **`Contents`** via **`object`** for the same **IList** contract.
- **Semver:** lockstep **`1.5.15`** with **`com.neoxider.coreaiunity`**.

## [v1.5.14] — 2026-04-30

### Lockstep + API clarity (behavior in Unity package)

- **Semver:** lockstep **`1.5.14`** with **`com.neoxider.coreaiunity`** — no new portable **CoreAI.Core** symbols; **Edit Mode** `UnityMainThreadLlmAsyncMarshaler` bypass (**`UNITY_EDITOR`**, **`!Application.isPlaying`**) and regression tests live in the Unity package.
- **Docs / XML:** **`CoreAi`** static entrypoint comments — **non-streaming** chat is async via **`await`** only; discourage **`.Result` / `.Wait()`** on Unity’s managed **main thread**.

## [v1.5.13] — 2026-04-30

### Verification & docs (lockstep packaging)

- **Edit Mode:** **`LlmAsyncMarshalerEditModeTests`**, **`ToolExecutionPolicyEditModeTests.ExecuteSingle_UsesToolInvocationMarshaler_WhenProvided`**, **`CoreAISettingsToolMarshalerEditModeTests`**.
- **Docs (Unity monorepo):** **`ARCHITECTURE.md`**, **`COREAI_SETTINGS.md`**, **`DEVELOPER_GUIDE.md`**, **`Assets/CoreAiUnity/Tests/PlayMode/README.md`** — document **`ToolInvocationMarshaler`** + HTTP main-thread semantics.
- **Semver:** lockstep **`1.5.13`** with **`com.neoxider.coreaiunity`** (no portable API change).

## [v1.5.12] — 2026-04-30

### LLM / tools — Unity thread safety (portable hook)

- **`ILlmAsyncMarshaler`** + **`PassThroughLlmAsyncMarshaler`** — host can marshal MEAI **`AIFunction.InvokeAsync`** before Unity-only tool bodies run.
- **`ICoreAISettings.ToolInvocationMarshaler`** (default: pass-through) — **`ToolExecutionPolicy`** wraps each native tool call.
- **Semver:** lockstep **`1.5.12`** with **`com.neoxider.coreaiunity`**.

## [v1.5.11] — 2026-05-01

### Meta

- **Semver:** lockstep **`1.5.11`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change in this tag; sibling Unity package reorganizes Play Mode tests into **`FastNoLlm`**, **`LlmVerification`**, and **`Scenarios`** assemblies (`Assets/CoreAiUnity/Tests/PlayMode/`).

## [v1.5.10] — 2026-05-01

### Version alignment

- **`com.neoxider.coreai` 1.5.10** is released in lockstep with **`com.neoxider.coreaiunity` 1.5.10** so UPM projects can pin the same version on both packages.
- **Portable Core** (`Assets/CoreAI`): no additional API or behavior changes in this tag beyond the version bump; Unity-side fixes and tooling live in the Unity package changelog.

#### Package **`1.5.10`**.

## [v1.5.9] — 2026-04-30

### Release alignment

- **`com.neoxider.coreai`** and **`com.neoxider.coreaiunity`** use the **same semver (1.5.9)** in this monorepo drop so UPM consumers can pin one version mentally.

### WebGL / IL2CPP — LLM + orchestration continuation hygiene

Single-threaded Unity player loop: avoid unnecessary **SyncContext-captured** continuations in the hot path.

- **`SmartToolCallingChatClient.GetResponseAsync`** — remove per-iteration **`Task.Yield()`**; add **`ConfigureAwait(false)`** on **`_innerClient.GetResponseAsync`** and **`policy.ExecuteBatchAsync`**.
- **`AiOrchestrator.RunTaskAsync`** — **`ConfigureAwait(false)`** on primary **`_llm.CompleteAsync`** (structured retry already had it).
- **`QueuedAiOrchestrator.RunOneAsync`** — **`ConfigureAwait(false)`** on **`_inner.RunTaskAsync`**.
- **`LuaTool.ExecuteAsync`**, **`ScriptedLlmClient` streaming** — **`ConfigureAwait(false)`** / **`Task.Delay(0)`** instead of bare **`Task.Yield()`**.
- **`GameConfigTool`**, **`InventoryTool`** — **`ConfigureAwait`** on inner **`await`**s for consistency.

#### Package **`1.5.9`**.

## [v1.5.6] — 2026-04-30

### LLM — MEAI assistant text helper

- **`SmartToolCallingChatClient.ConcatenateAssistantTextContents(ChatResponse)`** — joins all **`TextContent`** parts in **`response.Messages`**. Used by **`com.neoxider.coreaiunity`** **`MeaiLlmClient.CompleteAsync`** when **`ChatResponse.Text`** is empty but messages still hold text (provider / MEAI shape differences).

#### Package **`1.5.6`**.

## [v1.5.5] — 2026-05-01

### Architecture Refactoring — 3 Improvements

Continuation of the v1.5.4 audit. Addresses remaining deferred items: code deduplication, stale preprocessor guards, and orchestrator decomposition.

#### ARCH-6: Request Builder Extraction

- 🏗 **`AiOrchestrator.BuildCompletionRequest`** — extracted `LlmCompletionRequest` construction into a single private method. Eliminates 3x copy-paste between `RunTaskAsync` (main invocation), `RunTaskAsync` (structured retry), and `RunStreamingAsync`. Adding a new field to `LlmCompletionRequest` now requires updating exactly one method instead of three.

#### ARCH-7: Remove Stale `#if UNITY` from Portable Interfaces

- 🏗 **`ILlmClient.CompleteStreamingAsync`** — removed `#if UNITY_2021_3_OR_NEWER` guard around the Default Interface Method (DIM) fallback. The package minimum is `unity: 6000.0` which fully supports C# 8 DIM and `IAsyncEnumerable`. The streaming interface is now unconditionally available for non-Unity .NET test runners and pure .NET hosts.
- 🏗 **`IAiOrchestrationService.RunStreamingAsync`** — same removal of stale `#if UNITY_2021_3_OR_NEWER` guard.

#### ARCH-3 (partial): Post-Processing Extraction

- 🏗 **`AiOrchestrator.SanitizeAndPublish`** — extracted shared post-processing logic into a single private method: tool-call JSON sanitization (defense-in-depth strip), chat history persistence (`AppendChatMessage`), and game command publishing (`ApplyAiGameCommand`). Both `RunTaskAsync` and `RunStreamingAsync` now call this method instead of duplicating ~35 lines each.

#### Metrics

| Metric | Before | After |
|--------|--------|-------|
| `AiOrchestrator.cs` lines | 803 | 751 |
| `LlmCompletionRequest` construction sites | 3 | 1 |
| Post-processing duplication sites | 2 | 1 |
| `#if UNITY` in portable Core | 2 | 0 |

#### Package **`1.5.5`**.

## [v1.5.4] — 2026-05-01

### Comprehensive Audit — 8 Bug Fixes + 6 Architectural Improvements

Full code audit of CoreAI.Core covering orchestration, LLM pipeline, tool calling, memory, routing, streaming, and sandbox subsystems.

#### Bug Fixes

- 🐛 **BUG-1: `QueuedAiOrchestrator` deadlock risk** — merged `_scopeLock` into `_queueLock` (now a single `_lock`) to eliminate inconsistent lock ordering between `CancelTasks`/`Enqueue` (which nested `_scopeLock` inside `_queueLock`) and `ReleaseScopeToken` (which took `_scopeLock` independently).
- 🐛 **BUG-2: CTS Dispose-after-Cancel race** — `activeToCancel?.Cancel()` in `QueuedAiOrchestrator.Enqueue` and `CancelTasks` now guarded with `SafeCancel` (catches `ObjectDisposedException`) to handle the race with concurrent `ReleaseScopeToken.Dispose()`.
- 🐛 **BUG-3: `ClientLimitedLlmClientDecorator` counter drift** — `_requestCount` now decrements back when the limit is exceeded, so rejected requests don't permanently consume quota.
- 🐛 **BUG-4: `InGameLlmChatService` orphan responses** — split single `_lock` into `_historyLock` and `_rateLock`. History snapshot and append are atomic relative to `ClearHistory()`. Rate limiting no longer contends with history operations.
- 🐛 **BUG-5: `ToolExecutionPolicy` false positive tool failures** — replaced `string.Contains("\"Success\":false")` with `JObject.Parse`-based detection via `IsToolResultSuccess()`. Falls back to string heuristic only for non-JSON results.
- 🐛 **BUG-6: `LlmToolCallTextExtractor.StripCodeBlocks` offset safety** — added `Debug.Assert(result.Length == text.Length)` to catch offset desync if regex behavior changes.
- 🐛 **BUG-7: `MemoryTool.ExecuteAsync` unnecessary state machine** — removed `async` keyword from fully synchronous method. Returns `Task.FromResult` directly, eliminating overhead.
- 🐛 **BUG-8: `SmartToolCallingChatClient` streaming tool-calling bypass** — added runtime warning log when streaming is used with registered tools. Documents that tool-calling loop, duplicate detection, and consecutive error protection are bypassed in streaming mode.

#### Architectural Improvements

- 🏗 **ARCH-1: `CoreAISettings` thread safety** — added `_lock` around `Instance` getter/setter and `ResetOverrides()` to prevent torn reads from parallel test runners or async continuations.
- 🏗 **ARCH-2: `CoreAIAgent` thread safety** — static properties now backed by `volatile` fields to prevent torn reads when `Initialize` is called from Unity main thread and properties are accessed from ThreadPool continuations.
- 🏗 **ARCH-4: `AgentMemoryPolicy` thread safety** — added `_lock` to all dictionary/set operations (`_roleConfigs`, `_customTools`, `_runtimeContextProviders`, `_additionalSystemPrompts`, `_overrideUniversalPrefix`, `_streamingOverrides`). Prevents dictionary corruption from concurrent coroutine/async access.
- 🏗 **ARCH-5: `QueuedAiOrchestrator` `IDisposable`** — implements `IDisposable` to clean up `CancellationTokenSource` objects in `_scopeTokens` on shutdown. Safe for double-dispose.
- 🏗 **ARCH-9: `InMemoryAiOrchestrationMetrics` bounded storage** — added `MaxRoles = 256` cap with least-used eviction to prevent unbounded per-role dictionary growth from dynamically generated roleIds.
- 📝 **TODO.md** — updated version header, marked 4 completed items from this audit.

#### Package **`1.5.4`**.

## [v1.5.3] — 2026-04-30

### LLM-assisted context compaction (portable)

- **`LlmAssistedConversationContextManager`** — optional auxiliary `ILlmClient.CompleteAsync` to fold evicted history into a rolling summary (Kilocode-style); sync **`BuildSnapshot`** remains deterministic via **`DeterministicConversationContextManager`**.
- **`IAsyncConversationContextManager.BuildSnapshotAsync`** — **`AiOrchestrator`** now awaits this path when building chat history (including streaming), passing the orchestration trace id for compaction logs.
- **`ICoreAISettings.EnableLlmContextCompaction`** (default false) — **`RegisterCorePortable`** wires **`ConversationContextManagerFactories.Create(...)`** so Unity can enable LLM compaction from **`CoreAISettingsAsset`** without moving logic out of Core.
- **`SelectingConversationContextManager`** — when global compaction is enabled, each request selects LLM vs deterministic rollup using **`ConversationContextBuildArgs.UseLlmContextCompaction`** (from **`AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction`**, gated by **`ICoreAISettings`**).
- **`RoleMemoryConfig.UseLlmContextCompaction`** — defaults true for **`AgentBuilder`** agents and built-in **`Creator`**, **`Analyzer`**, **`AINpc`**, **`PlainChat`**, **`SmartChat`**, **`Merchant`**, **`CoreMechanicAI`**; built-in **`Programmer`** defaults false (deterministic truncation/summary only). **`AgentBuilder.WithLlmContextCompaction(bool)`** and **`AgentMemoryPolicy.ConfigureLlmContextCompaction`** override per role.
- **`AgentBuilder`** — **`Build()`** logs non-fatal **`Log.Instance`** warnings for common misconfigurations (empty system prompt for custom roles, tool modes without tools, LLM compaction requested while the global gate is off, etc.). Use **`SuppressBuildWarnings`** to silence in tests, or **`ValidateOnBuild()`** / **`AgentBuilderIssue`** for assertions. **`BuiltInAgentRoleIds.IsBuiltIn`** helps skip “missing prompt” noise for stock roles. **`WithSystemPrompt`** XML docs now spell out the three prompt layers and point to **`DEVELOPER_GUIDE.md`**.

#### Package **`1.5.3`**.

## [v1.5.2] — 2026-04-30

### Context budget, compaction, and transcripts (portable core)

- **Budget & estimation** — portable `ContextBudget`, `ContextBudgetRequest`, `IContextBudgetPolicy` (`DefaultContextBudgetPolicy`), and `ITokenEstimator` (`HeuristicTokenEstimator`, ~chars/4). `AiOrchestrator` allocates a `HistoryTokenBudget` from role/context window minus completion reserve and estimated system/user/tool-contract size, fed into `IConversationContextManager.BuildSnapshot` via `ConversationContextBuildArgs`.
- **Persisted summaries** — portable `InMemoryConversationSummaryStore` (process lifetime, per role) is the default backing store for deterministic compaction; `FileConversationSummaryStore` (System.IO + System.Text.Json) for cross-launch persistence under a host-supplied directory. **`RegisterCorePortable`** registers the in-memory implementation unless the host passes **`suppressDefaultConversationSummaryStore: true`** after registering its own `IConversationSummaryStore` (Unity **`CoreAILifetimeScope`** registers `FileConversationSummaryStore` at `%persistentDataPath%/CoreAI/ConversationSummaries` this way). **`AiOrchestrator`** without DI uses **`InMemoryConversationSummaryStore`** instead of **`NullConversationSummaryStore`**. Use **`NullConversationSummaryStore`** only when tests need no accumulation.
- **Context overflow retry** — new `LlmErrorCode.ContextLengthExceeded`. HTTP mapping in `MeaiOpenAiChatClient` (413 + common overload phrases) and provider code mapping in `LlmProviderError`. `AiOrchestrator.RunTaskAsync` may **`CompleteAsync` once more** at `ContextBudgetRequest.ContextRetryLevel = 1` (halved history budget) via `IConversationCompactionCoordinator`.
- **`LlmCompletionRequest.ContextWindowTokens`** is now populated from orchestration.
- **`AgentTurnTrace`** adds `HistoryTokenBudget` / `ChatHistoryMessageCount`; portable `ConversationHistoryBudgetApplied` messaging DTO added.
- **Transcript hooks** — `ConversationEntry`, `IConversationTranscriptStore`, `NullConversationTranscriptStore`; `FileAgentMemoryStore` implements transcript persistence (`transcriptEntriesJson`) plus migration from flat chat.

#### Package **`1.5.2`**.

## [v1.5.1] — 2026-04-30

### WebGL Stability: Retry + Timeout + Error Propagation

Critical fixes for WebGL (Emscripten) production stability. Eliminates LLM pipeline hangs and silent failures in single-threaded environments.

#### Retry Multiplier Fix
- **`AiOrchestrator.RunTaskAsync`** — removed the `for (attempt...)` retry loop. The orchestrator now invokes `_llm.CompleteAsync` exactly **once**. Network-level retries (HTTP 429/5xx, exponential backoff) remain exclusively in `LoggingLlmClientDecorator`, eliminating the `M × N` retry multiplier bug where orchestrator retries × decorator retries caused up to `2 × 3 = 6` redundant requests on a single failure.

#### WebGL-Compatible Timeouts
- **`AiOrchestrator.RunTaskAsync` / `RunStreamingAsync`** — removed all `CancellationTokenSource.CancelAfter()` calls. These relied on `System.Threading.Timer`, which is non-functional in WebGL's Emscripten runtime (single-threaded, no native timer callbacks), causing indefinite hangs on timeout.
- **`LoggingLlmClientDecorator.CompleteAsync` / `CompleteStreamingAsync`** — same removal of `CancelAfter` and linked `CancellationTokenSource` wrapping. `cancellationToken` from the caller is passed through directly.
- **`CoreAiChatService`** — timeout responsibility now lives here, using **`CancelAfterSlim`** from `Cysharp.Threading.Tasks` (UniTask). This mechanism is based on Unity's `PlayerLoop` and is fully compatible with WebGL's execution model. Both `SendMessageAsync` and `SendMessageStreamingAsync` create a linked `CancellationTokenSource` with `CancelAfterSlim(TimeSpan)` when `LlmRequestTimeoutSeconds > 0`.

#### Error Propagation
- **`CoreAiChatService.SendMessageAsync`** — removed the `catch (Exception)` block that silently swallowed errors and returned `null`. Exceptions now propagate to `CoreAiChatPanel`, which already has a `catch (Exception ex)` block that displays the error message to the user (e.g., "Error: Connection refused") instead of showing a generic "No response." message.

#### Package version **`1.5.1`**.

## [v1.5.0] — 2026-04-30

### Architecture: Portable LLM pipeline decoupling

Migrated core LLM pipeline classes into `CoreAI.Core` (portable, `noEngineReferences: true`):

#### Moved from `CoreAI.Source` → `CoreAI.Core`
- **`LoggingLlmClientDecorator`** — `IGameLogger` → `ILog`, `RoutingLlmClient` type-check → `ILlmPreflightAnnotator`.
- **`ToolExecutionPolicy`** — `IGameLogger` → `ILog`, `GlobalMessagePipe` → `IToolCallEventPublisher`, `CoreAi.NotifyToolExecuted` → `IToolExecutionNotifier`.
- **`SmartToolCallingChatClient`** — `IGameLogger` → `ILog`, portable `LlmToolCallTextExtractor`.
- **`ClientLimitedLlmClientDecorator`** — already portable, moved for consistency.

#### New portable abstractions
- **`IToolCallEventPublisher`** + `NullToolCallEventPublisher` — lifecycle events without MessagePipe dependency.
- **`IToolExecutionNotifier`** + `NullToolExecutionNotifier` — subscriber notification without `CoreAi` static dependency.
- **`ILlmPreflightAnnotator`** — replaces hard type-check against `RoutingLlmClient`.

#### Documentation
- Updated `ARCHITECTURE.md`, `STREAMING_ARCHITECTURE.md`, `DEVELOPER_GUIDE.md` to reflect the adapter chain.

- Package version **`1.5.0`**.

## [v1.4.0] — 2026-04-30

### Resilience: TryRepairToolName + HTTP retry with Retry-After

Two production resilience features for robust LLM orchestration.

- ✨ **`ToolExecutionPolicy.TryRepairToolName`** — case-insensitive tool name repair before `AIFunction` resolution. Model writes `MEMORY` → system silently maps to `memory`. Empty tool list → passthrough (backwards compatible). Unknown tool → structured error with available names for self-correction.
- ✨ **`LoggingLlmClientDecorator` HTTP retry** — retries `RateLimited` (429) and `BackendUnavailable` (5xx) with `Retry-After` header or exponential backoff (2s→4s→8s→16s→30s cap). `maxHttpRetryAttempts` injected from `ICoreAISettings.MaxLlmRequestRetries`.
- ✨ **`MeaiOpenAiChatClient.BuildHttpException`** — parses `Retry-After-Ms` (ms precision, Azure/LiteLLM) with priority over `Retry-After` (seconds).
- ✨ **`ComputeBackoff(attempt)`** — exponential backoff helper: `2^(attempt+1)` capped at 30s.
- 🧪 **EditMode:** `TryRepairToolName` (5 tests), `ExecuteSingle` repair (2 tests), `ComputeBackoff` curve, text-extraction edge cases (4 tests).
- 🧪 **PlayMode:** `ToolNameRepairPlayModeTests` — 3 hybrid scripted+real-LLM tests for repair, self-correction, and mixed-case text prefix.
- 🔧 Package version **`1.4.0`**; align `com.neoxider.coreaiunity` to **`1.4.0`**.

## [v1.3.0] — 2026-04-30

### Portable text-extractor + tool-call diagnostic surface

- ✨ **`CoreAI.Ai.LlmToolCallTextExtractor`** — engine-agnostic helper that extracts (`TryExtract`) or strips (`StripForDisplay`) embedded tool-call JSON from assistant text. Same brace-counted, code-block-aware logic that the Unity-side streaming pipeline used internally, now portable so the orchestrator and any other consumer can apply identical rules at boundary points.
- ✨ **`LlmToolCallTrace`** struct in `CoreAI.Ai` — `(Name, Success, DurationMs, Source)` record for one tool call. Source is `native` / `text` / `duplicate` / `missing`.
- ✨ **`LlmCompletionResult.ExecutedToolCalls`** + **`LlmStreamChunk.ExecutedToolCalls`** — non-empty when the turn invoked tools. Stream propagates the list on the `IsDone` chunk; non-streaming on the result. Used by Unity-side `LoggingLlmClientDecorator` to render `tools=[name(ok,12ms)]` on every `LLM ◀` line.
- 🛡 **`AiOrchestrator`** runs `LlmToolCallTextExtractor.StripForDisplay` on the assistant text before persisting to chat history or publishing `ApplyAiGameCommand`, both for sync and streaming paths. Logs a warning if the strip changed anything (defense-in-depth — should be a no-op once Unity-side extraction succeeds).
- Package version **`1.3.0`**; align `com.neoxider.coreaiunity` to **`1.3.0`**.

## [v1.2.1] — 2026-04-29

### AllowedToolNames semantics + streaming facade

- **Breaking (narrow):** `AiTaskRequest.AllowedToolNames` / `LlmCompletionRequest`: **`null`** still means “do not filter role tools”; a **non-null empty array** now means “attach **no** tools” (chat-only allowlist), matching lesson-slot “no quiz/dnd this turn” use cases.
- `AiOrchestrator.FilterToolsForRequest` implements the above; docs updated (`LLM_ROUTING.md`, `LESSON_ORCHESTRATION.md`, `AiTaskRequest` XML).
- **`CoreAi.StreamChunksAsync(AiTaskRequest, CancellationToken)`** (Unity façade) forwards to `CoreAiChatService.SendMessageStreamingAsync` so hosts can pass `AllowedToolNames` / `ForcedToolMode` on the same code path as `RunTaskAsync`.
- **Tests:** `RunTaskAsync_EmptyAllowedToolNames_SendsNoTools`, `RunStreamingAsync_UsesSameToolFiltering_AsRunTaskAsync`.
- **EditMode:** `CoreServicesInstallerEditModeTests` — no invalid `GlobalMessagePipe.SetProvider(null)` in TearDown (MessagePipe does not support null).

Package version **`1.2.1`**; align `com.neoxider.coreaiunity` to **`1.2.2`**.

## [v1.2.0] — 2026-04-29

### RedoSchool lesson/practice orchestration APIs

- Added per-role runtime context providers on `AgentMemoryPolicy` so lesson slots can inject context without UI prompt-spaghetti.
- Added `AllowedToolNames` filtering and chat-only tool suppression on `AiTaskRequest`/`LlmCompletionRequest`.
- Added `ILlmToolCallHistory`, `ScriptedLlmClient`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` for deterministic tests, structured tool results, and diagnostics.
- Package version **`1.2.0`**; aligned with `com.neoxider.coreaiunity` **`1.2.0`**.

## [v1.1.0] — 2026-04-29

### Portable LLM routing and policy contracts

- ✨ **Portable routing model** — added `LlmRouteProfile`, `LlmRouteRule`, `LlmRouteTable`, `ILlmRouteResolver`, and `LlmRouteResolver` under `CoreAI.Core`; `LlmExecutionMode.Stub` is now an alias for offline deterministic responses.
- ✨ **Portable registry and policy contracts** — added `ILlmClientRegistry`, `ILlmAuthContextProvider`, `ILlmEntitlementPolicy`, `LlmEntitlementDecision`, `ILlmUsageSink`, and `LlmUsageRecord`.
- ✨ **Provider error DTO** — added `LlmProviderError` for stable backend/provider codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited`.
- 📝 **Docs:** added `Assets/CoreAI/Docs/LLM_ROUTING.md`.
- 🔧 Package version **`1.1.0`**; aligned with `com.neoxider.coreaiunity` **`1.1.0`**.

## [v1.0.3] — 2026-04-29

### Unity chat UX alignment

- 🔧 Package version **`1.0.3`**; aligned with `com.neoxider.coreaiunity` **`1.0.3`**.

## [v1.0.2] — 2026-04-28

### Long context and tool-call identity

- ✨ **Conversation context management** — added portable `IConversationContextManager`, `ConversationContextSnapshot`, and `IConversationSummaryStore` contracts for long-running chat history compaction.
- ✨ **Deterministic summary fallback** — `DeterministicConversationContextManager` keeps recent messages in chat history and moves older turns into a `## Conversation Summary` system section without requiring an extra LLM call.
- ✨ **Tool-call identity** — added `LlmToolCallInfo` with `CallId`, `TraceId`, role, tool name, and sanitized arguments. Tool lifecycle events now expose `Info` while preserving `ToolName` and `ArgumentsJson` accessors.
- 🔧 Package version **`1.0.2`**; aligned with `com.neoxider.coreaiunity` **`1.0.2`**.

## [v1.0.1] — 2026-04-28

### Production runtime extension points

- ✨ **LLM usage telemetry** — added portable `LlmUsageReported` contract for token accounting and quota integrations.
- ✨ **Typed LLM errors** — `LlmErrorCode`, `LlmClientException`, and structured error fields on completion/stream chunks let UI and retry code handle quota, auth, rate-limit, timeout, and backend failures without parsing strings.
- ✨ **Runtime prompt context** — `IAiPromptContextProvider` lets projects append per-request context to prompts without mutating static role configuration.
- ✨ **Scoped memory contracts** — `AgentMemoryScope`, `IAgentMemoryScopeProvider`, and `ScopedAgentMemoryStoreDecorator` allow user/session/topic isolation while preserving role-only keys by default.
- ✨ **Tool lifecycle events** — added portable `LlmToolCallStarted`, `LlmToolCallCompleted`, and `LlmToolCallFailed` contracts for diagnostics and gameplay integrations.
- 🔧 Package version **`1.0.1`**; aligned with `com.neoxider.coreaiunity` **`1.0.1`**.

## [v1.0.0] — 2026-04-28

### Stable LLM mode contracts

- ✨ **`LlmExecutionMode`** — portable public mode contract for `Auto`, `LocalModel`, `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, and `Offline`.
- ✨ **LLM routing events** — added portable `LlmBackendSelected`, `LlmRequestStarted`, and `LlmRequestCompleted` message contracts for Unity MessagePipe integration without adding MessagePipe dependencies to `CoreAI.Core`.
- 🔧 Package version **`1.0.0`**; aligned with `com.neoxider.coreaiunity` **`1.0.0`**.

## [v0.25.14] — 2026-04-27

### Release

- 🔧 Version **0.25.14**; release train aligned with `com.neoxider.coreaiunity` **0.25.14** (see Unity package changelog for `CoreAiChatPanel` UX fixes).

## [v0.25.13] — 2026-04-27

### MEAI tool argument binding

- 🐛 **`CompatibilityLlmTool` native argument binding** — the MEAI executor parameter is now named `ingredients`, matching the JSON schema. Valid model calls such as `{"ingredients":["Fire","Earth"]}` no longer fail before reaching the tool with a missing `ingredientsObj` argument.
- 🧪 **EditMode coverage:** added an `AIFunction.InvokeAsync` regression for `check_compatibility` using the public `ingredients` argument name.
- 📝 **`MEAI_TOOL_CALLING.md`** — documents that .NET `AIFunction` parameter names must match `ILlmTool.ParametersSchema` property names.
- 🔧 Version **`0.25.13`**; `com.neoxider.coreaiunity` aligned to **`0.25.13`**.

## [v0.25.12] — 2026-04-27

### Queue scheduling hardening

- 🐛 **`QueuedAiOrchestrator` latest-wins scopes** — `CancellationScope` now cancels older active and pending work as soon as a newer task with the same scope is enqueued, including streaming tasks.
- 🐛 **Queue fairness and cancellation** — equal priorities are FIFO, streaming and non-streaming tasks share one effective priority order, and pending tasks observe external cancellation before they start.
- 🧪 **EditMode coverage:** queue tests now cover priority ordering, FIFO tie-breaking, active and pending scope cancellation, pending external cancellation, `CancelTasks(scope)`, and shared sync/stream priority.
- 🔧 Version **`0.25.12`**; `com.neoxider.coreaiunity` aligned to **`0.25.12`**.

## [v0.25.11] — 2026-04-27

### Tool contract hardening

- ✨ **`AiOrchestrator` tool contract injection** — roles with registered tools now get a compact `## Tool Contract` block in the system prompt that lists available tools, schemas, and rules: call tools through the tool interface when requested, pass required arguments structurally, and do not claim registered tools are unavailable. This nudges local models toward real tool calls without weakening tests.
- 🐛 **Structured retry keeps tool context** — the structured-response retry path now preserves `Tools`, `ChatHistory`, `ForcedToolMode`, `RequiredToolName`, and `MaxOutputTokens` from the original request instead of retrying with text-only context.
- 🧪 **EditMode coverage:** orchestrator regression test verifies that tool-enabled roles receive the tool contract, required-tool hint, and parameter schema in `LlmCompletionRequest.SystemPrompt`.
- 🔧 Version **`0.25.11`**; `com.neoxider.coreaiunity` aligned to **`0.25.11`**.

## [v0.25.10] — 2026-04-27

### Agent memory policy defaults

- 🔧 **`AgentMemoryPolicy.RoleMemoryConfig` constructor** — default `persistChatHistory` is now **`false`**. Built-in agent roles that use only the two-argument form (`MemoryTool` + default action) therefore do **not** imply cross-session chat persistence when `WithChatHistory` is off (matches the role table in docs and `AgentBuilderChatHistoryEditModeTests`). **`PlainChat`** / **`SmartChat`** still set `persistChatHistory: true` explicitly in the policy constructor.
- 🔧 Version **`0.25.10`**; `com.neoxider.coreaiunity` aligned to **`0.25.10`**.

## [v0.25.9] — 2026-04-27

### Per-agent MaxOutputTokens (additive)

- ✨ **`AgentBuilder.WithMaxOutputTokens(int? tokens)`** — persistent per-agent response token cap for roles that should stay short (NPC chat) or intentionally verbose (planners) without setting the limit on every call.
- ✨ **`AgentMemoryPolicy.RoleMemoryConfig.MaxOutputTokens`** + **`SetMaxOutputTokens(roleId, int?)`** — policy-level storage for the per-role override. `null` / non-positive values clear the override.
- 🔧 **Priority via orchestrator:** `AiTaskRequest.MaxOutputTokens` (per-call) → `AgentBuilder.WithMaxOutputTokens` / policy (per-agent) → `ICoreAISettings.MaxTokens` (global fallback in the Unity LLM client) → provider default. Direct `LlmCompletionRequest.MaxOutputTokens` remains the highest priority when calling an `ILlmClient` directly.
- 🧪 **EditMode coverage:** orchestrator tests for per-agent forwarding, per-call override priority, and unset role fallback.
- 🔧 Version bumped to **`0.25.9`** so `com.neoxider.coreai` and `com.neoxider.coreaiunity` publish with matching package versions.

## [v0.25.4] — 2026-04-27

### ✨ Unified MaxTokens fallback (additive)

- ✨ **`ICoreAISettings.MaxTokens`** — new interface property with **default-implementation `=> 0`** (DIM, C# 8+); existing implementers (test stubs etc.) compile unchanged. Semantics: `0` / negative = "not set, fallback skipped"; positive = global LLM response token cap that the Unity layer back-fills uniformly into **both** backends (HTTP via `MeaiOpenAiChatClient` and local GGUF via `LlmUnityMeaiChatClient`).
- ✨ **`AiTaskRequest.MaxOutputTokens`** (`int?`) — per-call override, symmetric with `ForcedToolMode`/`RequiredToolName`. Forwarded by `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync`, and the structured-retry path into `LlmCompletionRequest.MaxOutputTokens`.
- 🔧 **Priority**: `LlmCompletionRequest.MaxOutputTokens` (per-request direct client call) → `AiTaskRequest.MaxOutputTokens` (per-call via orchestrator) → `ICoreAISettings.MaxTokens` (global fallback) → provider default. Previously `CoreAISettings.MaxTokens` was a read-only getter with no consumer — visible in the inspector but never applied.
- 🧪 **`MaxTokensFallbackEditModeTests`** — 4 tests covering: settings-default fallback, per-request override, settings=0 leaves provider default, streaming path applies the same fallback.
- 🔧 Version bumped to **`0.25.4`** (minor — additive public API). `coreaiunity 0.25.8 → coreai 0.25.4`.

## [v0.25.7] — 2026-04-27

### Release sync with `com.neoxider.coreaiunity 0.25.7`

- 🔧 **`com.neoxider.coreai`** stays at **`0.25.3`** — no public **`CoreAI.Core`** API changes. Unity-only release: Editor `CoreAISettings` bootstrap, PlayMode recall on 5xx, `TROUBLESHOOTING`. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.7).

## [v0.25.3] — 2026-04-26

### Release sync with `com.neoxider.coreaiunity 0.25.3`

- 🔧 Package version bumped to `0.25.3`. Manifest dependency `coreaiunity 0.25.3 → coreai 0.25.3`.
- ✅ No **`CoreAI.Core`** public API changes — Unity-layer release only. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.3: chat hotkeys C/Esc, `Update` + poll when UITK has no focus, `FocusController` fix, `OnCollapsedStateChanged` hook, UXML/tooltips).

## [v0.25.2] — 2026-04-26

### Release sync with `com.neoxider.coreaiunity 0.25.2`

- 🔧 Package version bumped to `0.25.2`. Manifest dependency `coreaiunity 0.25.2 → coreai 0.25.2`.
- ✅ No `CoreAI.Core` public API changes — release sync only. See CoreAI Unity CHANGELOG 0.25.2 (UXML emoji cleanup + new `Docs/STREAMING_WEBGL_TODO.md` with a plan to fix WebGL SSE streaming in `OpenAiChatLlmClient.CompleteStreamingAsync`).

## [v0.25.1] — 2026-04-26

### Release sync — version alignment with `com.neoxider.coreaiunity 0.25.1`

- 🔧 Package version bumped to `0.25.1` to align with `com.neoxider.coreaiunity 0.25.1` (two WebGL/input fixes — see below).
- 🔧 Manifest dependency `com.neoxider.coreaiunity` now requires `com.neoxider.coreai 0.25.1` (was `0.25.0`).
- ✅ **No breaking changes to `CoreAI.Core` API** — pure release sync. Existing code using `LlmToolChoiceMode`, `AiTaskRequest.ForcedToolMode`, orchestrator, etc. continues to work.

### CoreAI Unity 0.25.1 release context (what actually changed in the Unity layer)

- 🐛 **WebGL TextField focus persistence** — `CoreAiChatPanel` keeps `WebGLInput.captureAllKeyboardInput = false` every frame (Update watchdog under `#if UNITY_WEBGL && !UNITY_EDITOR`). Fixes the “focus lasts one frame then drops” symptom in WebGL builds.
- 🐛 **Both Unity input systems** — `OrchestrationDashboard` no longer crashes with `Active Input Handling = Input System Package (New)`. `CoreAI.Source.asmdef` declares a soft dependency on `Unity.InputSystem` via `versionDefines` (`COREAI_HAS_INPUT_SYSTEM`).
- Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.1 entry).

## [v0.25.0] — 2026-04-26

### Forced Tool Mode — deterministic tool selection per request

- ✨ **`LlmToolChoiceMode` enum** (`CoreAI.Ai`): `Auto` (default, model decides), `RequireAny` (provider must emit at least one tool call from the available set), `RequireSpecific` (provider must call a named tool — uses `RequiredToolName`), `None` (text-only response, tool calls forbidden).
- ✨ **`AiTaskRequest.ForcedToolMode` + `RequiredToolName`** — application-layer code (intent classifiers, retry pipelines) can now request guaranteed tool emission for a single call without changing the agent definition. Default is `Auto`, so existing behaviour is preserved.
- ✨ **`LlmCompletionRequest.ForcedToolMode` + `RequiredToolName`** — propagated 1-to-1 through `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync` and the structured-retry path; LLM adapters in the Unity layer translate this to provider-native tool-choice (Microsoft.Extensions.AI `ChatOptions.ToolMode`).
- 🔧 **Streaming multi-round tool loop is unchanged** — `ForcedToolMode` only applies to the first iteration of a streaming session; after the first tool result is fed back, the model is reset to `Auto` so it can finalise with text instead of being pinned into an infinite tool-call loop.
- 🧪 **Tests:** new `ForcedToolModeEditModeTests` validate `LlmCompletionRequest`/`AiTaskRequest` plumbing and orchestrator forwarding.

### Release sync

- 🔧 Version bumped to `0.25.0` (minor — new public API). Dependency contract `com.neoxider.coreaiunity` `0.25.0+`.

## [v0.24.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.24.2` to match `com.neoxider.coreaiunity` `0.24.2`.
- 🔧 Synced Unity-layer hardening: HTTP error response body logging in `MeaiOpenAiChatClient` (both non-streaming and SSE paths), `ToolExecutionPolicy.maxConsecutiveErrors` clamped to `Math.Max(1, value)`.

## [v0.24.0] — 2026-04-26

### Streaming tool-calling hardening (release sync)

- 🔧 Version bumped to `0.24.0` to match `com.neoxider.coreaiunity` `0.24.0`.
- 🔧 Synced Unity-layer hardening: `ToolExecutionPolicy` (shared duplicate detection / error tracking), pattern-aware text JSON parser with multi-tool and code-block protection, native SSE `delta.tool_calls` parsing, stop/clear race condition fix.

## [v0.23.3] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.3` to match `com.neoxider.coreaiunity` `0.23.3`.
- 🔧 Synced Unity-layer reliability update: idempotent `CoreAIGameEntryPoint` startup guard prevents duplicate CoreAI initialization in scenes with accidental double composition.
- 🧪 Synced test coverage additions in Unity host: `CoreAIGameEntryPointEditModeTests` and additional streaming/tool-cycle guards in `MeaiLlmClientEditModeTests`.

## [v0.23.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.2` to match `com.neoxider.coreaiunity` `0.23.2` (includes non-stream HTTP cancellation fix used by Chat stop / Esc).

## [v0.23.1] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.1` to match `com.neoxider.coreaiunity` `0.23.1` and ensure downstream projects resolve the latest reliability fixes.

## [v0.23.0] — 2026-04-25

### Agent Control API UI
- ✨ **Chat UI updated.** `CoreAiChatPanel` adds a stop control that interrupts agent generation.
- ✨ **Default clear behavior.** The clear control in `CoreAiChatPanel` clears the UI and short-term chat history (`CoreAi.ClearContext(roleId, true, false)`). Full reset (including long-term memory) uses `ClearChat(clearChatHistory: true, clearLongTermMemory: true)`.
- 🔧 `com.neoxider.coreai` / `com.neoxider.coreaiunity` package versions aligned.
- 🔧 Release synced with the Unity layer for streaming + tool calling (`MeaiLlmClient` single-cycle: tool JSON suppressed in UI, tools run inside the same streaming pipeline).
- 🔧 For tool roles (`AgentMode.ToolsAndChat`, `AgentMode.ToolsOnly`) streaming is enabled per-role by default; `ChatOnly` still follows global/explicit overrides.
- 🔧 PlayMode reliability synced: stricter HTTP stream cancellation plus stabilized `Streaming_CancellationToken_StopsStream` and `MemoryTool_AppendsMemory`.

## [v0.22.0] — 2026-04-25

### Agent Control API — Full Lifecycle Management

- ✨ **Granular context clearing.** `CoreAi.ClearContext(string roleId, bool clearChatHistory, bool clearLongTermMemory)` — separate flags for chat history vs long-term memory (`MemoryTool`).
- ✨ **Tool invocation hook.** `CoreAi.OnToolExecuted` — global `ToolExecutedHandler(roleId, toolName, arguments, result)` for reactive integration (audio, VFX, analytics). Subscriber exceptions do not break the LLM pipeline.
- ✨ **`CoreAi.NotifyToolExecuted`** — internal hook invoked from `SmartToolCallingChatClient` after each successful tool call.
- ⚠️ **Breaking:** `SmartToolCallingChatClient` constructor now requires `roleId` (`string`) before `maxConsecutiveErrors`.

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.22.0** (Unity-layer release: `CoreAiChatPanel` stop via `Esc` and send-button stop state + tooltip). No portable-core API changes.

## [v0.21.9] — 2026-04-25

### Agent Control API
- ✨ **Stop + clear APIs.** `IAiOrchestrationService` adds `CancelTasks(string cancellationScope)`. `CoreAi` adds `CoreAi.StopAgent(string roleId)` and `CoreAi.ClearContext(string roleId)` for cancelling in-flight LLM work and clearing chat history.

## [v0.21.8] — 2026-04-25

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.8** (Unity layer: LLMUnity preprocessor guard refactor, automatic `COREAI_HAS_LLMUNITY` via `versionDefines`, fixes `CS0246` when LLMUnity is absent). No portable-core changes.

## [v0.21.7] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.7** (Unity layer: `CoreAiChatPanel` FAB collapse, auto-collapse on small screens, `PlayerPrefs` persistence). No portable-core changes.

## [v0.21.6] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.6** (Unity layer: removed forced `InputField` focus hacks in `CoreAiChatPanel`, WebGL caret flicker / lost keys fix). No portable-core changes.

## [v0.21.4] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.4** (Unity layer: WebGL input focus hardening in `CoreAiChatPanel`). No portable-core changes.

## [v0.21.3] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.3** (Unity layer: `CoreAiChatPanel` WebGL focus/typing stability). No portable-core changes.

## [v0.21.2] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.2** (Unity layer: `TextField` focus fix in `CoreAiChatPanel` after sending a message). No portable-core changes.

## [v0.21.1] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.1** (Unity layer: chat UI/scrollbar, timeouts, tests).

## [v0.21.0] — 2026-04-23

### Orchestrator streaming

- ✨ **`IAiOrchestrationService.RunStreamingAsync(AiTaskRequest, CancellationToken)`** — new interface member (C# 8 DIM fallback calls `RunTaskAsync` and yields one final chunk with `IsDone=true`).
- ✨ **`AiOrchestrator.RunStreamingAsync`** — real streaming implementation. Same path as `RunTaskAsync` (prompt composer, authority, memory, tools, structured validation) but emits chunks as they arrive and publishes `ApplyAiGameCommand` only after the stream completes. Shared request build logic moved to private `BuildRequest`.
- ✨ **Structured validation** runs on the fully accumulated text after streaming ends. On failure, emits a terminal `LlmStreamChunk` with `Error = "structured validation failed: ..."` (no automatic stream retry — caller decides).
- 📚 **`RunStreamingAsync` contract** warns: any wrapper over `IAiOrchestrationService` (queue, logging, timeout, authority) must override this method explicitly or the DIM fallback silently disables streaming.

## [v0.20.3] — 2026-04-23

### Streaming pipeline — end-to-end visibility fix
- 🐛 **Critical: streaming was invisible in the UI.** `ILlmClient.CompleteStreamingAsync()` has a default interface implementation that falls back to `CompleteAsync()` and emits the whole answer as **one** terminal chunk after generation. Wrappers that did not override the method hid real streaming. Fixed in `CoreAiUnity` (see its CHANGELOG).
- 📝 `ILlmClient.CompleteStreamingAsync()` docs now warn that decorators (logging, routing, timeouts) **must** override streaming explicitly or the DIM fallback kills streaming.

## [v0.20.2] — 2026-04-23

### Streaming Configuration
- ✨ **`ICoreAISettings.EnableStreaming`** — global switch for LLM response streaming (SSE for HTTP API, callback queue for LLMUnity). Default `true`.
- ✨ **`AgentBuilder.WithStreaming(bool)`** — per-agent override of the global flag (e.g. chat NPC forced streaming vs strict JSON parser / tool-only non-streaming).
- ✨ **`AgentMemoryPolicy.SetStreamingEnabled(roleId, bool?)`** and **`IsStreamingEnabled(roleId, fallback)`** — per-role override storage and effective flag resolution.
- ✨ **`AgentConfig.EnableStreaming`** (`bool?`) — nullable override propagated to policy via `ApplyToPolicy()`.
- 🔧 **Precedence** (highest to lowest): UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).

## [v0.20.1] — 2026-04-23

### Streaming Robustness

- ✨ **`ThinkBlockStreamFilter`** (`CoreAI.Ai`) — reusable stateful filter that strips `<think>...</think>` from the LLM stream. Unlike regex, handles tags split across chunks (common with DeepSeek / Qwen).
  - `ProcessChunk(string)` — process a chunk, return only visible text.
  - `Flush()` — end the stream (return trailing text if the model cut off mid-response).
  - `Reset()` — reuse the same instance.

### Streaming API
- 📝 **Stream contract:** `ILlmClient.CompleteStreamingAsync()` always ends with a final chunk `IsDone=true` (even on empty model output) so callers can close the UI reliably.
- 📚 `ILlmClient.CompleteStreamingAsync()` docs note implementations should run on Unity’s main thread (`UnityWebRequest`).

## [v0.20.0] — 2026-04-23

### Streaming API
- ✨ **`LlmStreamChunk`** — stream chunk type with `Text`, `IsDone`, `Error`, and usage stats.
- ✨ **`ILlmClient.CompleteStreamingAsync()`** — new interface member returning `IAsyncEnumerable<LlmStreamChunk>`. Default implementation falls back to `CompleteAsync()` with a single chunk.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — real streaming via `IChatClient.GetStreamingResponseAsync()` with `<think>` filtering.

### 3-Layer Prompt Architecture
- 🔧 **Bug fix:** `AgentBuilder.WithSystemPrompt()` did not register prompts in `IAgentSystemPromptProvider`, so AgentBuilder prompts were ignored and AiOrchestrator always used ManifestProvider.
- ✨ **Three-layer system prompt** in `AiPromptComposer.GetSystemPrompt()`:
  - **Layer 1:** `CoreAISettings.universalSystemPromptPrefix` — shared rules for all agents
  - **Layer 2:** Base prompt from ManifestProvider / ResourcesProvider (`.txt` assets)
  - **Layer 3:** Extra prompt from `AgentBuilder.WithSystemPrompt()` (via `AgentMemoryPolicy`)
- 🔧 **`AgentBuilder.Build()`** — no longer appends `universalPrefix` (handled in `AiPromptComposer`)
- 🔧 **`AgentConfig.ApplyToPolicy()`** — registers system prompt via `policy.SetAdditionalSystemPrompt()`
- ✨ **`AgentMemoryPolicy.SetAdditionalSystemPrompt()` / `TryGetAdditionalSystemPrompt()`** — stores AgentBuilder extra prompts
- ✨ **`AgentBuilder.WithOverrideUniversalPrefix()`** — disable `universalPrefix` per role (parsers, validators, fully custom prompts)
- ✨ **`AgentMemoryPolicy.SetOverrideUniversalPrefix()` / `IsUniversalPrefixOverridden()`** — per-role universal prefix control

### Breaking Changes
- **`AiPromptComposer` constructor** — optional `AgentMemoryPolicy` and `ICoreAISettings` parameters (backward compatible with `= null`)
- **`universalPrefix`** now applies to all roles by default (opt out with `.WithOverrideUniversalPrefix()`)

## [v0.19.3] — 2026-04-22

### Prompt Optimization
- 🔧 **Removed duplicate tool-calling rules** from all seven built-in agent prompts (C# constants + `.txt` resources). Saves ~100–150 tokens per request — rules already live in `UniversalSystemPromptPrefix`.
- 📝 **Prompt wording:** added response length limits for AiNpc (1–3 sentences) and built-in chat roles (1–5 sentences).
- 🔧 **Native tool calling:** dropped legacy manual JSON tool-formatting guidance from `Agent.cs` and `AllToolCallsPlayModeTests.cs`; samples and tests use native `MEAI` function calling.

### Editor UX
- ✨ **`CoreAI/Create Scene Setup`** — Unity menu action for quick scene wiring:
  - Adds `CoreAILifetimeScope` with assigned assets
  - Generates default assets (Settings, LogSettings, PromptsManifest, etc.)
  - Creates `LLM` + `LLMAgent` when using LLMUnity backend (or Auto+LlmUnityFirst)
  - Duplicate guard and Undo (Ctrl+Z)

### Stability
- 🐛 **HTTP timeout logging:** `MeaiOpenAiChatClient` — timeout/network issues downgraded from `LogError` to `LogWarning` so PlayMode tests stay green in Unity Test Runner.
- 🐛 **PlayMode tests:** fixed `AllToolCalls_MemoryTool_WriteAppendClear` failure from conflicting text JSON prompts vs native tool calls.
- 🛡️ **UI safety:** `try/catch` in `async void OnSendClicked` (`InGameChatPanel.cs`) to avoid silent UI crashes on network errors.

### Documentation
- 📚 **READMEs (EN + RU)** — full dependency install guide:
  - NuGet DLLs (Microsoft.Extensions.AI, etc.) with version table
  - Git URL packages and transitive deps (VContainer, MoonSharp, LLMUnity, UniTask, MessagePipe)
  - New steps: Create Scene Setup, LLM backend setup
- 🔗 **Link fix:** repaired broken relative links in `README_RU.md` for GitHub repo home navigation.

## [v0.19.2] — 2026-04-14

### Changed
- **AgentMemory:** smarter `ChatHistory` trimming before the LLM client. History is capped by message count (`MaxChatHistoryMessages`, default 30) and approximate token budget (`ContextTokens / 2`). Reduces HTTP context blow-ups and huge bills while older turns stay in JSON.
- **AgentBuilder:** optional `maxChatHistoryMessages` on `.WithChatHistory()`.

## [v0.19.1] — 2026-04-14

### Fixes & Stability
- 🐛 **Duplicate tool-call guard:** documented how `MeaiLlmClient` resets failed-call counters per session; `executedSignatures` scoping isolates each request.
- 🔧 **`Agent.cs` test harness:**
  - Test phrases exposed in Inspector `[TextArea]` for live scenario tweaks and to avoid identical-prompt loops.
  - Added `ClearMemory()` to reset history between button presses so the model does not anchor on prior mistakes.
- 📝 **Docs:** clarified `SceneLlmAgentProvider` with `DontDestroyOnLoad` — needs an `LLMAgent` component or registered agent name.

## [v0.19.0] — 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — ingredient compatibility checks for CoreMechanicAI
  - Rules for arbitrary element counts (pairs, triples, quads, …)
  - `CompatibilityRule.Pair()` and `CompatibilityRule.Group()` factory helpers
  - Element groups (IronOre → Metal, WaterFlask → Water) with automatic resolution
  - Custom validators (`ICompatibilityValidator`) for game logic
  - Weighted scoring: rules covering more elements win
- ✨ **`CompatibilityLlmTool`** — `ILlmTool` wrapper for function calling (LLM can validate before crafting)
- ✨ **`JsonSchemaValidator`** — LLM JSON validation without external deps
  - Required fields and types (string, number, integer, boolean, array, object)
  - Numeric ranges (min/max) and enums
  - Strips markdown fences (`` `json...` ``)
  - `ToPromptDescription()` — schema blurb for system prompts
- 🧪 **45+ EditMode tests** for CompatibilityChecker, JsonSchemaValidator, and CompatibilityLlmTool

## [v0.18.0] — 2026-04-10

### Architecture — DI Migration

- 🔧 **`CoreAISettings` → static proxy** — no longer stores independent field copies; reads delegate to DI-registered `ICoreAISettings Instance`.
  - Direct field writes kept for backward compatibility (override wins over Instance).
  - Added `CoreAISettings.ResetOverrides()` for tests.
- 🔧 **`LuaAiEnvelopeProcessor`** — takes `ICoreAISettings` via constructor (optional). No longer reads `CoreAISettings.MaxLuaRepairRetries` at init.
- ❌ **Removed** `SyncToStaticSettings()` — replaced with `CoreAISettings.Instance = settings`.

## [v0.16.0] — 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — runtime scene inspection for the LLM:
  - `find_objects` — find GameObjects by name/tag
  - `get_hierarchy` — list children
  - `get_transform` / `set_transform` — position, rotation, scale
- ✨ **`CameraLlmTool`** — vision tool: PlayMode `capture_camera` screenshots as Base64 JPEG `dataUri` (multimodal models like LLaVA / gpt-4o).
- 🛠 **Threading** — both tools marshal Unity API work via `UniTask.SwitchToMainThread()` to avoid MEAI background-thread crashes.
- 🛠 **`CoreAiPrefabRegistryAsset` automation** — `OnValidate` fills `Key` from AssetDatabase GUID and syncs `Name` when prefabs are assigned in the Inspector.

## [v0.15.0] — 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON extraction** — rewrote tool-call parsing in `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Fragile regex removed; brace scanning (`IndexOf('{')`) tolerates missing closing fences (\`\`\`) and braces inside string args. PlayMode `MemoryTool_AppendsMemory` passes.
- ⚙️ **Reasoning-mode stripping** — preprocess strips `<think>...</think>` before JSON parse so “thinking aloud” (DeepSeek) does not break tool JSON.

### Editor UX
- ✨ **Auto plugin load** — `[InitializeOnLoadMethod]` in `CoreAIBuildMenu` generates required `ScriptableObject` assets (`CoreAiSettingsAsset`, routing manifests, permissions) under `Settings/` and `Resources/` on project load / import.
- ✨ **Quick Settings** — **CoreAI → Settings** menu opens the global `CoreAISettings.asset` singleton.

## [v0.14.0] — 2026-04-09
### Agent Memory & Persistence
- ✨ **Persistent chat history** — full dialog context survives between play sessions.
  - `WithChatHistory(persistToDisk: true)` on `AgentBuilder` (or `RoleMemoryConfig`) enables disk persistence.
  - Files live under `Application.persistentDataPath/CoreAI/AgentMemory/`.
  - Orchestrator reloads JSON on restart; ephemeral fallback when disk persistence is off.
- 🧪 PlayMode `ChatHistoryPlayModeTests` cover context restore after scene/engine “restart”.

## [v0.13.0] — 2026-04-09
### Action / Event System
- ✨ **`DelegateLlmTool`** — generic `ILlmTool` that exposes any C# delegate (Action/Func) to the LLM via MEAI with JSON schema inferred from the signature.
- ✨ **`CoreAiEvents`** — tiny built-in static pub/sub bus linking agents to game code without extra deps.
- ✨ **`AgentBuilder` extensions:**
  - `WithAction(name, description, delegate)` — wire a method straight to the agent.
  - `WithEventTool(name, description, hasStringPayload)` — emit triggers on `CoreAiEvents`.
- 🧪 EditorMode `CoreAiEventsEditModeTests`.

## [v0.12.0] — 2026-04-08

### Architecture
- **Single `ILog` logger** — collapsed the dual-logger setup
  - `ILog` adds `Debug/Info/Warn/Error(msg, tag)`
  - `LogTag` subsystem strings (`Core`, `Llm`, `Lua`, `Memory`, `Config`, `World`, `Metrics`, `Composition`, `MessagePipe`)
  - `Log.Instance` static + VContainer DI both supported
  - `NullLog` default no-op for tests / pre-DI

- **`MemoryToolAction` unification** — one enum definition
  - Moved to `MemoryToolAction.cs`
  - Removed duplicates from `AgentBuilder.cs` and `AgentMemoryPolicy.cs`
  - `AgentBuilder.WithMemory(defaultAction)` now applies correctly

### Changed
- **Core tool classes** use `ILog` tags:
  - `MemoryTool` → `LogTag.Memory`
  - `LuaTool` → `LogTag.Lua`
  - `GameConfigTool` → `LogTag.Config`
  - `InventoryTool` → `LogTag.Llm`
- `CoreAIGameEntryPoint` — `IGameLogger` → `ILog`
- `CoreServicesInstaller` — registers `ILog` (`UnityLog`) and sets `Log.Instance`
- `GameLoggerUnscopedFallback` — bridges `Log.Instance` before DI boots
- Removed manual `Log.Instance` wiring from `CoreAILifetimeScope` (now in `CoreServicesInstaller`)

### Unity implementation
- `UnityLog` — `ILog` impl mapping `LogTag` to `GameLogFeature` flags
- `IGameLogger` kept for Unity layer (`FilteringGameLogger`, `GameLogSettingsAsset`)
- Tag filtering still driven by `GameLogSettingsAsset` in the Inspector

## [v0.11.0] — 2026-04-07

### Added
- **Universal system prompt prefix** — shared preamble for every agent
  - `CoreAISettings.UniversalSystemPromptPrefix` static property for code-driven setup
  - Prepended to **every** system prompt (built-in and custom)
  - Centralizes cross-model rules without duplication
  - `BuiltInAgentSystemPromptTexts.WithUniversalPrefix()` helper
  - `BuiltInDefaultAgentSystemPromptProvider` applies it automatically
  - `AgentBuilder.Build()` applies it to custom agents
- **Global sampling temperature** — `CoreAISettings.Temperature` (default **0.1**) for all agents and both backends (LLMUnity + HTTP API)
- **`AgentBuilder.WithTemperature(float)`** — per-agent override; `AgentConfig.Temperature` stores it (defaults to `CoreAISettings.Temperature`)
- **`MaxToolCallIterations`** — moved from hardcode to `CoreAISettings.MaxToolCallIterations` (default 2); caps tool rounds per request; `MeaiLlmClient` reads the setting

## [v0.10.0] — 2026-04-06

### Added
- **WorldCommand as MEAI tool call** — LLM-driven world control via function calling
  - `IWorldCommandExecutor` — engine-agnostic contract in **CoreAI**
  - `WorldTool.cs` — MEAI `AIFunction` (CoreAiUnity)
  - `WorldLlmTool.cs` — `ILlmTool` wrapper (CoreAiUnity)
  - Actions: `spawn`, `move`, `destroy`, `load_scene`, `reload_scene`, `bind_by_name`, `set_active`, `play_animation`, `show_text`, `apply_force`, `spawn_particles`, `list_objects`
- **`list_objects`** — enumerate scene hierarchy objects (name, position, active, tag, layer, child count) with optional name filter
- **`play_animation`** — play clips on Animator or legacy Animation via `Animator.runtimeAnimatorController.animationClips`
- **`list_animations`** — list available clips from the AnimatorController; resolve targets by `instanceId` or `targetName`
- **`targetName` on commands** — name-based targeting alongside `instanceId` for move/destroy/set_active/play_animation/apply_force/spawn_particles (`_instances` first, then `GameObject.Find`)
- `WorldToolEditModeTests.cs` / `WorldCommandPlayModeTests.cs` — coverage for world tools
- **Inspector debug logging on `CoreAISettingsAsset`**
  - `LogLlmInput` — prompts (system/user) + tools
  - `LogLlmOutput` — model replies + tool results
  - `EnableHttpDebugLogging` — raw HTTP JSON
- `tool_call_id` on tool messages for LM Studio
- Idempotent `MemoryTool.append` to stop duplicate appends when the model loops

### Changed
- `MeaiOpenAiChatClient` — tool results read from `msg.Contents`
- `MemoryTool.ExecuteAsync` — returns JSON strings for correct serialization
- `TestAgentSetup` — adds `WorldExecutor` for PlayMode
- Dropped `LogAssert.Expect` for connection errors in PlayMode (only when host is down)

### Fixed
- Tool results were empty (`[tool]` content) — fixed `Contents` extraction
- LM Studio 400 — required `tool_call_id` on tool messages
- Memory append triple-writes — idempotency guard
- Write test flakiness — clarified hint text

---

## [v0.9.0] — 2026-04-06

### Added
- `MeaiLlmClient` — single MEAI client for every backend
  - `MeaiLlmClient.CreateHttp(settings, logger, memoryStore)` — HTTP API
  - `MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)` — local GGUF
- `MeaiOpenAiChatClient` — MEAI `IChatClient` for HTTP
- `LlmUnityMeaiChatClient` — MEAI `IChatClient` for LLMUnity (split out)
- `OfflineLlmClient` — deterministic canned replies per role (replaces stub)
- `CoreAISettings.ContextWindowTokens` — default context size (8192)
- `AgentBuilder.WithChatHistory(int?)` — inherit or override history window
- `AgentConfig.ContextWindowTokens` / `WithChatHistory`
- `CoreAISettingsAsset.AutoPriority` — `LlmUnityFirst` vs `HttpFirst`
- Inspector **🔗 Test Connection** button
- `Docs/MEAI_TOOL_CALLING.md` — architecture notes

### Changed
- `MeaiLlmUnityClient` / `OpenAiChatLlmClient` — thin factories delegating to `MeaiLlmClient`
- PlayMode tests build `CoreAISettingsAsset` through the factory
- `LlmBackendType.Stub` → `LlmBackendType.Offline`
- `AGENT_BUILDER.md` — client creation examples
- Removed duplicate docs: `MEAI_FUNCTION_CALLING.md`, `README_MEAI.md`

### Architecture
- Shared MEAI pipeline for HTTP + LLMUnity
- `FunctionInvokingChatClient` handles automatic tool calling
- No manual text parsing for tool calls

---

## [v0.8.0] — 2026-04-06

### Added
- `CoreAISettingsAsset` — single ScriptableObject settings singleton
- `IOpenAiHttpSettings` — adapter interface for HTTP settings
- `OpenAiChatLlmClient(CoreAISettingsAsset)` constructor
- `CoreAISettingsAssetEditor` — custom Inspector
- Default `CoreAISettings.asset` in Resources
- LLMUnity options: `DontDestroyOnLoad`, `StartupTimeout`, `KeepAlive`
- Auto priority: LlmUnityFirst / HttpFirst

---

## [v0.7.0] — 2026-04-06

### Added
- Unified MEAI tool-calling format
- `LuaTool.cs` + `LuaLlmTool.cs`
- `InventoryTool.cs` + `InventoryLlmTool.cs`
- `CoreAISettings.cs` (static)
- `AgentBuilder` — fluent builder for custom agents
- `WithChatHistory()` — dialog history retention
- `WithMemory()` — persistent memory
- `AgentMode` — ToolsOnly, ToolsAndChat, ChatOnly
- Merchant NPC sample with tools

### Removed
- `AgentMemoryDirectiveParser` — superseded by the MEAI pipeline
