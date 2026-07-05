# Changelog - `com.neoxider.coreaiunity`

Unity host: **CoreAI.Source** build, EditMode / PlayMode tests, Editor menus, documentation. Depends on **`com.neoxider.coreai`**.

## [Unreleased]

### 5.0.7 - warn when a hand-set LLMUnity field conflicts with CoreAI (2026-07-05)

- **`LlmUnityHostConfigurator.ApplyFromSettings` now logs a clear warning** (once, when it configures the
  agent) if the resolved `LLMAgent` has `remote == true` or `overflowStrategy != None` before CoreAI
  overrides them. CoreAI owns both fields on the LLMUnity path (local in-process model; context managed by
  CoreAI's own compaction), so a hand-set value was silently ignored - the warning makes it obvious *why*
  and points `remote` users at CoreAI's `ClientOwnedApi` / `ServerManagedApi` (HTTP) instead, which is
  unrelated to LLMUnity's own `remote` server feature.

### 5.0.6 - force LLMUnity Overflow Strategy to None (CoreAI owns context) (2026-07-05)

- **`LLMAgent.overflowStrategy` is now explicitly set to `ContextOverflowStrategy.None`** wherever CoreAI
  creates the agent - runtime (`LlmUnityHostConfigurator.ApplyFromSettings`) and Editor
  (`CoreAI/Setup/Create LLMUnity Objects` / scene creators). CoreAI builds the whole prompt itself and
  calls `Chat(addToHistory: false)`, so the agent's internal chat history is always empty and LLMUnity's
  built-in overflow handling (`Truncate` / `Summarize`) has nothing to act on - its Inspector default
  (`Truncate`) only *implied* LLMUnity managed the context. Forcing `None` makes the LLMUnity path behave
  like the HTTP/OpenAI path (server never manages history) and keeps context management solely in CoreAI's
  backend-agnostic, role-aware, persisted compaction (`LlmAssistedConversationContextManager`).
- Documented the rationale in `Docs/MemorySystem.md`.

### 5.0.5 - remove brittle default-backend assertion from singleton test (2026-07-05)

- **Removed `CoreAISettingsAssetEditModeTests.Singleton_ShouldLoadFromResources`.** It hardcoded the
  shipped `Resources/CoreAISettings.asset` default (`BackendType == OpenAiHttp`,
  `ExecutionMode == ClientOwnedApi`), so it broke any time that asset's backend choice changed -
  including a local, uncommitted edit - even though nothing about singleton loading was actually
  wrong. Singleton load/reset behavior is already covered by `SetInstance_ShouldOverrideSingleton`.

### 5.0.4 - standalone menu to create the LLMUnity host (2026-07-05)

- **New `CoreAI/Setup/Create LLMUnity Objects (LLM + LLMAgent)` menu item** creates the `LLM` +
  `LLMAgent` objects in the current scene on demand, regardless of `CoreAISettingsAsset.ExecutionMode`
  - useful when a scene already exists and you switch its backend to LLMUnity by hand instead of
  recreating the scene via `Create Chat Demo Scene` / `Create Bare Scene (advanced)`. Reuses the same
  `TryCreateLlmUnityObjects` (idempotent: warns instead of duplicating if `LLM` already exists) and
  selects/pings the created object afterward.

### 5.0.3 - Chat Demo scene creates the LLMUnity host too (2026-07-05)

- **`CoreAI/Setup/Create Chat Demo Scene` now creates `LLM` + `LLMAgent`** in the generated scene
  when `CoreAISettingsAsset` needs a local LLMUnity host (`ExecutionMode.LocalModel`, or `Auto` with
  `AutoPriority.LlmUnityFirst`) - previously only the separate `Create Bare Scene (advanced)` menu
  did this, so the primary, documented "first scene" workflow (`QUICK_START.md`) silently skipped it
  and relied entirely on the runtime lazy-create fallback (`ConfigurableLlmAgentProvider`) to fill
  the gap at play time, with no `LLM`/`LLMAgent` visible in the Editor to configure beforehand.
- **`CoreAIBuildMenu.NeedsLlmUnity(CoreAISettingsAsset)` and `TryCreateLlmUnityObjects` are now
  `internal`** (shared, not duplicated) so any scene creator in the `CoreAI.Editor` namespace can
  reuse the same "does this scene need a local LLMUnity host" decision and creation logic.
- Verified live: with `CoreAISettingsAsset.BackendType = LlmUnity`, `TryCreateLlmUnityObjects` is
  idempotent (a second call detects the existing `LLM` and skips instead of creating a duplicate)
  and `NeedsLlmUnity` returns `false` for a `null` settings reference.

### 5.0.2 - fix no-Lua consumer compile (2026-07-05)

- **`LuaModRuntimeTickDriver` is now guarded by `COREAI_HAS_MOONSHARP && !COREAI_NO_LUA`** - it was
  the only Lua-referencing file without the guard, so projects without the MoonSharp package failed
  to compile the package (`CS0246: LuaModRuntime could not be found`). Audited the whole Runtime
  tree: every other Lua reference already sits behind the define.

### 5.0.1 - package ships the full WebGL link.xml (2026-07-05)

- **`link.xml` now ships complete inside the package** (MoonSharp, `UnityEngine.Resources` /
  `TextAsset`, MEAI assemblies, `CoreAI.Core`, `CoreAI.Source` preserves). Unity picks up package
  link.xml automatically, so consumer projects need NO link.xml of their own - only the two player
  settings (Managed Stripping = Medium, IL2CPP Code Generation = Faster (smaller) builds). The
  project-level `Assets/link.xml` was removed; docs updated with a template for game-side binding
  assemblies (`LUA_ACCESS_MODES.md`).
- `Resources/AgentSkills/LuaModding.txt` regenerated with the edit-existing-mod workflow.

### 5.0.0 - skills via inspector/SO, runtime API, Resources override (2026-07-04)

- **CoreAILifetimeScope "Role Skills"** - bind agent roles to `SkillSetAsset[]` in the inspector
  (instructions from a TextAsset or inline text); registered automatically at container build.
  The FullAccess demo ships `LuaModdingSkill.asset` wired to the Programmer role as the reference
  setup.
- **`CoreAi.AddSkillForRole(roleId, skill)`** - runtime code path over the same live catalog;
  skills added mid-session are immediately readable via `read_skill`.
- **`Resources/AgentSkills/LuaModding.txt`** - canonical text of the built-in Lua Modding skill;
  a host can replace it the same way as `AgentPrompts/System` overrides. A test pins it equal to
  the built-in fallback so the two never drift.
- **WebGL survives Managed Stripping Medium.** `link.xml` preserves the whole `CoreAI.Source`
  assembly (VContainer creates DI types via reflection, MoonSharp reflection-invokes binding
  callbacks - per-type preserve lists broke one type per build); input bindings factory-registered;
  verified in-browser (self-test 16/16, Tetris persists across reload). Recommended WebGL settings
  (Medium + IL2CPP OptimizeSize, fixes the LLVM linker OOM) documented in LUA_ACCESS_MODES.md.
- **Semver:** major (5.0), lockstep with **`com.neoxider.coreai` 5.0.0** - the on-demand skills platform for built-in roles.

### 4.20.0 - Mod timers actually tick; Lua input API; mod editor panel; Lua platform example (2026-07-04)

- **Semver:** minor with **`com.neoxider.coreai` 4.20.0** (`hooks_on('tick')` alias, `{id=...}` table
  coercion - see the core changelog).
- **Mod timers actually tick in players.** The `RegisterEntryPoint<ITickable>` registration never
  produced a dispatched tickable (verified live: `ITickable` unresolvable, every `hooks_every` timer
  frozen in editor AND WebGL). `LuaModRuntimeTickDriver` (plain MonoBehaviour on
  `CoreAI_LuaModTicker`, created in the installer build callback) now drives `LuaModRuntime.Tick`
  every frame. Startup rehydration + the ticker are **play-mode-only**: EditMode containers share the
  real persistent mod store, so rehydrating there injected mods persisted by earlier runs into every
  fresh container, and `DontDestroyOnLoad` throws outside play mode.
- **NEW: Lua input API (Gameplay tier).** `CoreAiInputLuaRuntimeBindings`: `input_key` /
  `input_key_down` / `input_key_up`, `input_mouse_button` / `input_mouse_down`, `input_mouse_x/y`,
  `input_axis` - read-only over `UnityEngine.Input`, KeyCode names case-insensitive plus
  `left/right/up/down` and digit aliases. Game logic (steering, clicks) can now live entirely in a
  mod. Documented in `LUA_GAME_API.md`.
- **Mod manager: per-mod source editor.** Every active/inactive mod row has an Edit button opening a
  closable window with the mod source in a text area. Save reloads a running mod (compile error keeps
  the old mod running and the window open with the error) or updates the stored source of an inactive
  one; Close discards - the buffer is a private copy.
- **Demo: Lua platform example (`LuaPlatformExampleController`, FullAccessDemo, F6).** Creates every
  Lua script itself: a two-mod self-test (timers, tick alias, variables/closures, varargs, coroutines,
  store roundtrip, cross-mod ping/pong events, input API - 11 checks, PASS verdict aggregated from
  `report()`) and a 3D Tetris built from ONE WorldEdit-tier Lua mod: board state in Lua tables,
  gravity/input/HUD/camera-orbit on four `hooks_every` timers, A/D steer + S soft-drop via the input
  API, autopilot after 5 s idle, line clears, score persisted in the mod store, generation-suffixed
  object names so reloads survive Unity's deferred destroy, camera orbit around the board via
  `coreai_world_change('Main Camera', ...)`. Persisted with the mod store: the game auto-resumes
  after restart (the restart test).
- **Demo panels: programmatic + configurable toggling.** `PanelVisible` / `ToggleKey` public APIs on
  the mod manager and the platform example; `KeyCode.None` disables the hotkey (matching the token
  budget overlay convention).
- **Mods-chat autoload grants the host tier.** The persistence controller autoloads saved mods with
  `All|Full` when the scope enables Full Lua (was: hardcoded `All`, silently stripping `unity_*` mods
  on every restart).

### 4.19.0 - WebGL Full Lua on; SSE fetch bridge stabilized + tested; cumulative usage (2026-07-04)

- **Semver:** minor with **`com.neoxider.coreai` 4.19.0** (WebGL Full-Lua crash fix, rate-limit window parsing, tool-error accounting - see the core changelog).
- **Full Lua no longer force-disabled on the WebGL player.** `CoreAILifetimeScope` passes the inspector's `enableFullLuaAccess` through on WebGL now that the IL2CPP crash is fixed; availability is still gated by `SecureLuaEnvironment.WebGlLuaOptIn` (`EnableLuaOnWebGl`). Lua PlayMode test fixtures dropped their `!UNITY_WEBGL` guards (the blind spot that hid the crash).
- **WebGL SSE fetch bridge stabilized + tested.** From a dedicated audit: missing `Content-Type: application/json` fixed (LM Studio's Express server hard-reset such requests as "Failed to fetch"; Groq tolerated them), per-chunk `stringToNewUTF8` allocations now freed (unbounded wasm-heap growth ended in tab OOM on long sessions), rolling body-inactivity watchdog (a stalled stream after headers aborts as a typed Timeout instead of hanging), `Timeout` surfaces as `LlmClientException(Timeout)` instead of a fake HTTP-0 "CORS/network" failure, terminal-state guard against double callbacks incl. synchronous `fetch()` throws, `abortReasons`/registration/parked-read leak fixes, and an empty-chunk dequeue can no longer signal a false EOF. Coverage: protocol helpers extracted to `FetchSseTransportProtocol` (18 EditMode tests) + a node harness driving the real `.jslib` with a mocked browser `fetch` (9 scenarios / 26 assertions, `Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js`).
- **Streaming usage is cumulative and survives cancelled turns.** `MeaiLlmClient` sums provider usage across every tool roundtrip (was: last roundtrip only) and emits it immediately as a usage-bearing chunk, so `RoutingLlmClient` publishes `LlmUsageReported` even when the turn later times out - the token budget panel no longer shows zeros for a turn that burned tokens.
- **Backend panel: close button + translucent background.** Regenerated prefab has a corner "x" (`CoreAiBackendPanel.Close()`, wired via `Wire(..., close)`) and a 0.6-alpha background.
- **Tool loop parity upgrades (audit close-out, see core changelog).** Streaming history trimming, final no-tools summary turn at caps, schema hints on conversion errors, deterministic synthetic tool_call_ids, raw-args echo for parse errors, intra-batch duplicates allowed, non-streaming cumulative usage. Held hybrid prose is flushed before native tool roundtrips (no more lost words after an unclosed `{`).
- **Chat tool bubbles render real results.** `CoreAiToolCallChatFormatter` unwraps `JsonElement` values instead of showing `{"ValueKind":N}`.

### 4.18.4 - lockstep with com.neoxider.coreai 4.18.4 (2026-07-04)

- Transient-HTTP rescue chain (request -> retry -> non-streaming fallback -> typed error) for
  429/408/5xx (see the core package changelog); regression tests live in this package. 36/36.
- `CoreAiChatExternalDriver.ApplyBackendJson` accepts optional `maxTokens`/`temperature`/
  `timeoutSeconds` (Groq counts max_tokens toward TPM); new `RunLuaDiag` staged Lua diagnostic for
  the WebGL null-function investigation.
- FullAccessDemo scene now ships the `CoreAiBackendPanel` prefab (runtime API settings UI).

### 4.18.3 — lockstep with com.neoxider.coreai 4.18.3 (2026-07-04)

- Bounded HTTP 429 retries (2, Retry-After-aware) before the RateLimited error surfaces (see the
  core package changelog); regression tests live in this package's test assembly. SSE fixture: 35/35.
- New opt-in `CoreAiChatExternalDriver` (spawns only with `?coreai-external-driver=1` in the page
  URL or `COREAI_EXTERNAL_DRIVER=1`): SendMessage bridge for WebGL/browser automation —
  `SubmitPrompt` routes a prompt through the real chat turn pipeline, `ApplyBackendJson` switches
  the LLM backend at runtime via `CoreAiBackend.ApplyHttpApi`. Synthetic DOM events cannot reach
  Unity 6's Input System, so this is the supported headless path for WebGL smoke tests.

### 4.18.2 — lockstep with com.neoxider.coreai 4.18.2 (2026-07-04)

- Starved-stream watchdog: keep-alive-only SSE attempts abort after 15s instead of waiting for the
  server to close the connection (see the core package changelog); the EditMode regression test
  lives in this package's test assembly.

### 4.18.1 — lockstep with com.neoxider.coreai 4.18.1 (2026-07-03)

- Starved-SSE fallback to a non-streaming completion (see the core package changelog); the EditMode
  regression test lives in this package's test assembly. SSE fixture: 32/32.

### Runtime backend switching: CoreAiBackend facade + Canvas panel prefab (2026-07-03)

- **New `CoreAiBackend` static facade** — switch the LLM backend at runtime without restarting the
  scene or rebuilding the DI container: `ApplyHttpApi(baseUrl, apiKey, model, ...)`,
  `ApplyServerManagedApi`, `ApplyLlmUnity(ggufModelPath?, agentName?, numGpuLayers?)`,
  `ApplyOffline(...)`, `ApplyAuto()`, plus hot `SetModel` / `SetApiKey` / `SetApiBaseUrl`. A switch
  mutates the shared `CoreAISettingsAsset` and hot-swaps the routed primary client in the live
  `LlmClientRegistry` (via the new shared `LlmPipelineInstaller.BuildRoutedPrimaryClient`, so a
  swapped client has identical semantics to a bootstrapped one — including the secondary-backend
  fallback decorator); the very next request uses the new backend. Pre-bootstrap calls are
  settings-only and return `false`.
- **`CoreAiBackend.VerifyAsync`** — health probe through the active backend returning
  ok/error/latency; never throws. **`CoreAiBackend.Status`** — current mode / base URL / model /
  GGUF path / liveness snapshot. **`OnBackendChanged`** event for UI sync.
- **Drop-in Canvas prefab `Assets/CoreAiUnity/Prefabs/CoreAiBackendPanel.prefab`** — uGUI/TMP panel
  (backend dropdown Auto / LLMUnity / HTTP API / Offline, base URL + write-only API key + model
  fields, Apply and Test buttons, status label) driven by the new `CoreAiBackendPanel` component.
  Also creatable via `GameObject → CoreAI → Backend Panel (Canvas)`; the prefab can be regenerated
  with `CoreAI → UI → Regenerate Backend Panel Prefab`.
- **Settings:** `CoreAISettingsAsset` gains runtime setters `SetApiKey` / `SetModelName`.
- **Docs:** new guide `Docs/RUNTIME_BACKEND_SWITCHING.md`; `DEVELOPER_GUIDE.md` links it from the
  execution-modes section.
- **Tests:** EditMode facade + panel coverage (switches mutate settings, events fire, empty key
  field keeps the configured key, builder hierarchy fully wired); PlayMode end-to-end against a real
  `CoreAILifetimeScope` (offline custom-response switch serves the next request, unreachable-HTTP
  probe fails then offline recovers); live PlayMode test boots Offline and retargets to the
  configured local server at runtime (probe + real model answer).

### Live PlayMode suite green on a stricter local model (2026-07-03)

- **`RequireSpecific` forced tool mode goes through the portable mapping** (`RequireAny` + tools
  narrowed to the one requested function) with an EditMode test asserting the resulting
  `ChatOptions` shape; see `com.neoxider.coreai`'s changelog for the rationale.
- **Live-model PlayMode tests no longer reward prose over tool calls.** The whole LlmVerification
  suite was re-run against a stricter reasoning model (qwythos-9b) and the failures were all
  test-harness assumptions, not runtime bugs: the `AllToolCalls` Lua hint now names the actual
  sandbox API (`create_item`/`report`) instead of letting the generic Programmer prompt advertise
  world APIs the stub does not register; `WorldTool_MoveObject` accepts `change`+position (the tool
  schema's documented way to move — `move` is only a legacy executor alias); the crafting and
  multi-agent workflow steps that verify execute_lua plumbing force the call via
  `ForcedToolMode.RequireSpecific`; the merchant negotiation step states that the purchase must be
  completed with `buy_item` in the same turn; and the crafting quality assert accepts a numeric
  quality computed in Lua from ingredient stats, not only an integer literal.

### Streaming-by-default task execution (2026-07-03)

- **Agent task execution now streams by default.** `AiOrchestrator.RunTaskAsync` runs through the
  streaming tool path (`CompleteStreamingAsync`) when `EnableStreaming` is on, so the parallel
  execute-as-you-stream tool calls apply to tasks, not just chat; non-streaming stays as the fallback
  when streaming is disabled.
- **PlayMode tool-calling diagnostics.** `AllToolCallsPlayModeTests` now writes a per-step LLM debug
  bundle under `TestResults/CoreAI/LlmDebug` — the system prompt (with the tool contract), the tools
  actually offered to the model, the raw model response, and the executed tool-call history — so a
  "model did not call the tool" result can be traced to prompt/tool wiring vs model behaviour vs a
  transport failure. The tests' `CapturingLlmClient` now forwards `CompleteStreamingAsync` to the
  inner client instead of collapsing it to non-streaming, so they exercise the real streamed path.
- **EditMode:** coverage for the transport-send retry on stream-open (a first-attempt send failure
  retries and the stream still completes).

### Parallel execute-as-you-stream tool calls (2026-07-03)

- **`MeaiLlmClient` now awaits the new `ToolExecutionPolicy.CompleteStreamedTurnAsync` on both the
  happy path and the mid-stream-abort path**, closing out the newly parallel streamed turn: calls
  drained mid-stream are scheduled concurrently (bounded by `MaxParallelToolCalls`, default 4), so a
  long multi-call build turn now mutates the world up to `MaxParallelToolCalls` calls at a time while
  the model is still streaming, instead of one-by-one. Results are collated in arrival order; a turn
  aborted mid-stream is still finalized (unfinished calls become failed slots) before the failure
  surfaces. (See `com.neoxider.coreai`'s changelog for the policy-side guarantees.)
- **EditMode coverage added**: policy-level tests for bounded overlap, serialized mutating built-ins,
  and streamed-turn echo semantics, plus a client-level parallel streaming test through
  `MeaiLlmClient`.

### Streaming-loop and benchmark hardening from the independent audit (2026-07-03)

- **A mid-stream transport failure no longer abandons a partially-executed streamed turn.**
  `MeaiLlmClient`'s streaming loop enumerates the inner stream manually so a throw from
  `MoveNextAsync` is intercepted: when tool calls already executed mid-stream, the turn is finalized
  via `CompleteStreamedTurn` (consecutive-error record + echo-signature registration) and the
  failure surfaces as a terminal chunk with `Error` + `ExecutedToolCalls` — a partially-applied turn
  is a graded failure with traces, not a clean transport error a caller might blindly replay.
  Failures before any executed call propagate exactly as before (keeps `FallbackLlmClientDecorator`
  fallback semantics); cancellation finalizes the turn, then rethrows.
- **LLMUnity backend: `MaxOutputTokens = null` now resets `numPredict` to `-1` (unlimited).**
  The shared agent previously kept the PREVIOUS request's cap when a later request resolved to
  "unlimited", silently truncating it (`ApplySamplingToAgent` only assigned on `HasValue`).
- **Benchmark suite timeout chain is now self-consistent.** The soft suite budget (env
  `COREAI_BENCHMARK_SUITE_BUDGET`) is clamped to `NUnit [Timeout] − report margin` (6300s with the
  current 6600s/300s values, warning on clamp; default stays 6000s), and a scenario rep only starts
  if its WORST case — all retry attempts at the full per-scenario timeout, including a
  `COREAI_BENCHMARK_TIMEOUT` override — fits inside the budget. A rep starting just under the budget
  (or hanging through every retry) can no longer blow through the NUnit hard abort, which writes no
  artifacts. The launcher watchdog comment documents the required ordering: soft budget < NUnit
  timeout < watchdog (7200s).
- `.gitignore` now covers `Assets/InitTestScene*` (Unity Test Runner leftovers from aborted runs);
  inset-layout comments in the report harness corrected to the actual 4K composite math.

### Benchmark: custom comparisons from hand-picked reports (2026-07-03)

- **`GameCreationBenchmarkLauncher.ParseSummary(jsonPath)` is now public** (wrapping the private
  parser), so editor scripts can build comparison charts from any explicit set of report JSONs via
  the already-public `WriteComparison` — e.g. cloud models and local models as two separate charts —
  instead of the Compare tab's newest-per-model default. Documented in `Docs/BENCHMARK.md`.

### Execute-as-you-stream tool calls in the streaming loop (2026-07-02)

- **`MeaiLlmClient` now executes each native tool call the moment its streamed argument JSON is
  complete** (via `SseToolCallAccumulator.DrainCompleted()` + `ToolExecutionPolicy.StreamedTurn`),
  instead of collecting the whole assistant turn first. With a real streaming provider the world
  visibly builds up call by call. EditMode parity tests cover intra-turn duplicate suppression, the
  one-record-per-turn consecutive-error guard, cross-turn echo of a single-call turn (suppressed
  before executing), and streamed-turn signatures blocking a later identical classic batch; a
  multi-call echo turn cannot be detected mid-stream (its combined signature only exists at turn
  end) and is prevented upstream by the wire protocol sending every tool result.

### Benchmark suite: no per-turn token caps + gate-level inset (2026-07-02)

- **All per-scenario `MaxOutputTokens` caps removed (suite default is now `0` = unlimited).**
  OpenAI-compatible `max_tokens` counts reasoning tokens, so the old caps (800 base, up to 4800 on
  the free-build) silently zeroed long-thinking models: glm-5.2 spent the entire free-build cap on
  thinking — `finish_reason=length`, no tool calls, empty scene, floor score. The per-scenario
  `TimeoutSeconds` stays as the real runaway guard, and token appetite is still priced by the
  efficiency bonus. (See `com.neoxider.coreai`'s changelog for the `0 = unlimited` semantics.)
- **The top-right report inset is now a gate-level close-up**: the camera stands just outside the
  front entrance (+Z, where models consistently build the gate), low to the ground, looking through
  the gap toward the keep — replacing the old opposite-side wide angle that duplicated the hero view.

### Benchmark harness: front-facing hero shots, sharp insets, no G1 noise (2026-07-02)

- **Hero camera flipped 180° to shoot from the front.** Models consistently build scenes gate-forward
  toward +Z, and the old offset photographed every castle from behind; the report hero shot now uses
  a front-right elevated offset.
- **Side insets render at 960×540 with 2× supersampling** (render at 1920×1080, downscale) instead of
  small direct-render thumbnails that came out blurry in the report.
- **G1 scenario scene screenshots removed** (`CaptureScene => false`): three near-identical
  primitive-spawn shots added noise to every report while scoring never used them.
- **Benchmark launcher watchdog raised 1900s → 7200s.** A full 7-group cloud run (Opus) exceeded the
  old cap mid-run; the watchdog destroyed the TestRunner callbacks and the run finished with no
  report. 2h covers the slowest observed cloud model with margin.

### Example-game logic fixes from the independent demo audit (2026-07-02)

- **RogueliteArena: wave difficulty scaling was silently ignored.** Enemies are instantiated from
  an inactive template, so `ArenaEnemyBrain.Awake` (running on `SetActive(true)`) reset HP/speed/
  damage back to serialized defaults AFTER the director had already applied the wave multipliers.
  Defaults now only apply when no wave stats were set.
- **SymbiosisMode: the `skeleton_attack_nearest` and `skeleton_set_stance` LLM tools were no-ops**
  (the real actions were commented out — the model could "successfully" call them with zero
  gameplay effect). Wired to a new public `TryAttackNearestEnemy` (cooldown-honest) and a working
  stance system (aggressive/defensive/balanced modify attack reach and follow leash).
- **SymbiosisMode: mobile attack presses could be dropped** — the button cleared its
  `WasJustPressed` flag in its own `Update` while the player read it in another, with no script
  execution order guarantee. Replaced with an order-safe consume-on-read latch.
- **SymbiosisMode: card selection hardened** — a single empty inspector slot in
  `AvailableUpgrades` could randomly crash the card screen; missing panel/container/prefab wiring
  now disables the component with a warning instead of NRE-ing at wave end.
- **RogueliteArena: the HUD's "AI thinking" indicator no longer sticks** after the director stops
  waiting for a Creator wave plan and falls back to the linear schedule.
- Also translated the remaining Russian comments/log strings in the touched example-game files.

### Streaming tool-loop cap no longer erases a successful run's stats (2026-07-02)

- **`MeaiLlmClient`: the streaming iteration guard required visible text for a clean completion,
  but a `ToolsOnly` agent (e.g. the G6 free-build) never emits any** — so a build that hit the
  roundtrip cap after hundreds of successful tool calls always took the error path
  ("tool loop exceeded max iterations"), and the benchmark capture recorded turns=0/tools=0/~1
  token while the world plainly held the finished scene (observed live: 96-spawn colored castle,
  report stats all zero). Any successful executed tool call is now sufficient to complete
  cleanly; the guard still hard-terminates the loop either way. Verified via an independent
  Codex audit (no consumer keys off the error string; no loop-masking) plus EditMode 1361/1361
  and PlayMode FastNoLlm 48/48.

### Benchmark report images: 4K, left-column insets, close-up view (2026-07-02)

- **All report images (scene/hero shots and the model card) now render at 4K (3840x2160)** instead of
  1280x720 — the overlay (banner, caption, radar, bars) is world-space and scales for free; only the
  RenderTexture sizes and the pixel-composite metrics changed.
- **The extra-angle inset views moved from the right column to the LEFT, and a third close-up view was
  added**: opposite-side wide, top-down, and a new narrow-FOV (32 deg) low-angle zoom shot — larger
  models build compositions worth zooming into (visible in the first 4K gpt-5.5 castle). Insets are
  676x380 each, rendered natively at that size with 8x MSAA. Composite metrics (margins, banner
  clearance, inset border) scale from the image dimensions; a clamp keeps the third inset clear of the
  bottom caption bar (found by the independent Codex audit of this diff — the initial top-anchored
  stack dipped ~31px into the caption at 4K).
- Verified live: a G6 free-build run (codex/GPT-5.5 via the CLI bridge, 94/100 Pass, 60 spawns)
  produced a 3840x2160 hero PNG with all three left insets and a 4K model card; EditMode 1361/1361 and
  PlayMode FastNoLlm 48/48 green.

### G1/G2/G3/G7: stop penalizing self-verification of logic_define slots (2026-07-02)

- **`ToolCorrectness` was being dragged down by a documented-but-unwarned API gotcha, not real model
  mistakes.** A live GLM-5.2 run showed the model repeatedly calling a slot it had just
  `logic_define`'d directly as a plain Lua global (e.g. `wave_reward(5)`) to self-verify its own work —
  every such call throws `attempt to call a nil value`, since a defined slot is not a real global; only
  the harness can invoke it. This inflated `clean_lua`/`clean_tool` checkpoint failures across nearly
  every G1/G2/G3/G7 scenario using `logic_define`, even ones that otherwise scored a clean Pass. G4 had
  already been given a clarifying `VerificationNote` for this; the same clarification (as a new shared
  `GameBenchmarkScenario.LuaVerificationNote`) is now appended to all 13 other `logic_define`-using Goal
  prompts (G1 x1, G2 x5, G3 x6, G7 x1).
- Verified via an independent Codex audit (no missed `logic_define` scenarios, no concatenation issues)
  plus EditMode 1361/1361 and PlayMode `FastNoLlm` 48/48.

### Benchmark scene lighting + docs restructuring (2026-07-01)

- **G6 scene shadows were nearly invisible despite being enabled.** Both the final hero-shot key light
  and the live preview light defaulted to `LightShadows.Soft` with the default `shadowBias`/
  `shadowNormalBias`, which are tuned for room/level-scale scenes and "peter-pan" (detach/hide) the
  shadow on the ~1m benchmark primitives; `shadowStrength` was also only 0.75. Raised strength to 1
  and set an explicit low bias on both lights. (An initial pass also set `Light.shadowResolution`,
  which is Built-In Render Pipeline only and is a silent no-op under this project's URP, spamming a
  console warning per light per scene — removed once a live multi-scenario run surfaced it.)
- **Live preview light now orbits while a model builds.** `BenchmarkLivePreviewLight` spins its azimuth
  a full 360 deg every 3 minutes (elevation fixed, so the scene never goes dark) purely for a more
  watchable Game-view timelapse when recording a model's build session; the final hero screenshot uses
  a separate, static light and is unaffected.
- **README's per-model G6 castle gallery moved to `Docs/BENCHMARK.md`.** The main README keeps only the
  brief benchmark description, the single combined multi-model ranking, and links out; the full
  per-model screenshot gallery now lives in the benchmark guide's "Castle Gallery" section.
- **Live preview now shows which model is currently building.** A HUD label rigidly parented to
  `BenchmarkLivePreviewCamera` reads "Model: {modelId}" for the duration of each screenshot-capturing
  scenario, so a recorded multi-model sweep video is self-explanatory without reading the console. Hidden
  along with the rest of the live preview before the final hero screenshot, so it never appears in
  report images.
- **Full benchmark sweeps no longer inherit G6's 10-minute pacing window.** The soft whole-suite
  `COREAI_BENCHMARK_SUITE_BUDGET` default is now 100 minutes, with the NUnit hard backstop raised above it;
  G6 keeps its own 10-minute per-scenario timeout.

### Tool-calling hardening (2026-07-01 audit)

- **Streaming fails closed on bad tool JSON.** In the streaming MEAI client, a held text-shaped tool-call
  object that is incomplete or unparseable is no longer leaked to visible output at turn end — it fails
  closed like the native SSE path. The streaming tool loop now honors `MaxToolCallRoundtrips`
  (request/settings, `0` = unlimited) instead of `MaxToolCallRetries + 1`, and the hybrid held-tail buffer is
  bounded (64 KB cap) to avoid O(n²) rescans. A dropped `<think>` block that contained a tool-call-shaped
  object is now logged.
- **Atomic file stores.** `FileAgentMemoryStore` (now `IAtomicAgentMemoryStore`), `FileSkillStore`,
  `FileLuaModSourceStore`, and `FileLuaScriptVersionStore` hold a process-wide per-key lock across
  load→modify→save so concurrent turns cannot lose a `memory` append or a `manage_skills`/`manage_mods` write.
- **Scene/Camera tool robustness.** `SceneLlmTool.find_objects` is null-safe for `searchMethod`/`searchTerm`;
  `set_transform` returns a clear error on a no-op (no fields) call; the intentionally empty aggregate
  `ParametersSchema` on the multi-function Scene/Camera tools is documented (per-function MEAI schema is
  authoritative).
- **`world_command` physics validation.** `apply_force` / `set_velocity` now require at least one vector
  component: a fully-omitted vector returns the advertised missing-parameters error instead of silently
  applying zero, while an explicit `0` on any axis is still honored (so an intentional `set_velocity` stop works).
- **Non-streaming `ExecutedToolCalls` no longer vanishes on an empty final response.** `MeaiLlmClient.CompleteAsync`
  returned `Ok=false` for an empty final assistant response before copying the executed tool traces into the
  result. A turn that genuinely ran a tool (e.g. a `world_command` spawn) and then trailed off into an empty
  response silently lost all evidence that the tool ran for every `ExecutedToolCalls` consumer (orchestrator
  history, logging, telemetry) — found via a live Game-Creation Benchmark report where the G6 hero caption
  claimed `0 tool-calls` alongside `1 spawns`, a logical contradiction.
- Added EditMode tests for concurrency, streaming fail-closed, SSE accumulator, and per-tool correctness.

- **World tools consolidated around `spawn` + `change`.** The public `world_command` surface now uses
  `spawn` for creation and `change` for partial position/rotation/scale/parent edits, with `prefabKey` and
  `targetName` required for spawn and `scaleX/scaleY/scaleZ` available for meter-accurate non-uniform
  pieces. `set_color` stays public; legacy public `move` / `rotate` / `set_scale` / `parent` /
  `set_transform` are no longer advertised, and `update_score` / `spawn_particles` were removed from the
  world-command surface. Lua WorldEdit mirrors the new shape with `coreai_world_spawn({...})`,
  `coreai_world_change(name, {...})`, `coreai_world_set_color`, and `coreai_world_destroy`.
- **G6 free-build benchmark is spatially scored.** The castle/free-build prompt now states that one Unity
  unit is one meter and asks models to use `scaleX/scaleY/scaleZ`; the scorer now checks distinct names,
  bounds, castle structure, transform variety, non-uniform scale, and generic free-build overrides (city,
  character, etc.) without applying castle-only checks to custom subjects. The hero screenshot header has
  more vertical space and wrapped text so model/score/stats lines do not overlap.
- **World tool verification.** Added PlayMode coverage that runs a complex public `WorldLlmTool` task
  through spawn/change/color/UI/audio/animation/physics/list/destroy, plus Lua WorldEdit spawn/change
  coverage. EditMode now checks the complete public action surface and rejects legacy `move` from the
  public LLM tool.
- **Native tool schemas are self-describing.** Tool parameter descriptions now reach the model via
  `[System.ComponentModel.Description]` attributes on the delegate params (the `ParametersSchema` string
  only feeds the text path, not native tool-calling). Added across the World/Scene/Camera/Component tools —
  so models actually use `world_command`'s `fx/fy/fz` rotation and `scale` (verified: qwythos-9b 58/63
  spawns with rotation+scale, ornith-9b 52/52 with scale, fable-27b 79/79). New `Docs/TOOL_AUTHORING_GUIDE.md`.
- **`plane` removed from advertised spawn primitives** (models misused it as a flat ceiling/floor); the
  executor still maps it for backward compatibility.
- **Duplicate tool calls off by default** for the world/component tools (`AllowDuplicates => false`), so the
  args-aware dedup applies — distinct spawns stay allowed, exact-identical calls are skipped. Removed the
  `WithAllowDuplicateToolCalls(true)` override from every benchmark scenario.
- **Benchmark.** Each scenario's authored system prompt now reaches the model (via the new
  `AiTaskRequest.SystemPrompt`); G6 castle timeout back to 10 minutes; the free-build now grades and
  screenshots whatever was built when the model stops (empty response) or the time budget elapses, instead
  of failing the run and discarding the scene.

### WebGL + streaming migration hardening (2026-07-01)

- **Fixed a real EditMode deadlock from the WebGL `ConfigureAwait(false)` sweep.** Stripping
  `ConfigureAwait(false)` everywhere (for the `CAIU001` analyzer) reintroduced a classic sync-over-async
  deadlock: `FileAgentMemoryStoreEditModeTests` blocked the Unity main thread via `.GetAwaiter().GetResult()`
  on `FileAgentMemoryStore.SaveAsync`/`TryLoadAsync`, whose `Task.Run` continuation now needed to marshal
  back onto that same blocked thread. Fixed by converting the test to `async Task`/`await`. Separately,
  `UnityMainThreadLlmAsyncMarshaler`'s `#if UNITY_EDITOR`-only branch (which can never compile into a WebGL
  player) restored its `ConfigureAwait(false)` with a scoped `#pragma warning disable CAIU001`, since that
  code path legitimately needs it to avoid the same deadlock shape.
- **Benchmark harness now always streams**, matching every real production caller
  (`AiOrchestrator.RunStreamingAsync` via a new `DrainStreamingAsync` wrapper) instead of the non-streaming
  `RunTaskAsync` convenience path; the dead `.WithStreaming(false)` scenario overrides were removed.
  `DrainStreamingAsync` re-throws `OperationCanceledException` after the drain loop so its `Task` keeps the
  same Canceled/RanToCompletion semantics `RunScenario`'s timeout classification relies on (an independent
  Codex audit found the streamed drain otherwise completed normally even after a timeout-driven cancel).
  `SessionCapturingLlmClient.CompleteStreamingAsync` now also captures `ExecutedToolCalls` (previously
  dead code since benchmarks forced non-streaming).
- **Empty-response-after-tool-call nudge now gates on genuine success**, not mere attempt. Both
  `SmartToolCallingChatClient` (non-streaming) and `MeaiLlmClient` (streaming) nudge the model to continue
  instead of ending the turn when a response comes back with no text and no tool call after a *successful*
  tool call earlier in the same turn/request — an all-failed batch that trails into an empty response falls
  through to the existing failure-retry path instead (a Codex audit found the first version of this fix
  conflated "attempted" with "succeeded").
- **G6's live pacing note no longer leaks into fixed-count scenarios.** `BenchmarkEnvironment.DeadlineUtc`
  was set for every scenario, so every `world_command` result carried "~Xs left to build — keep going, then
  stop when done" even for G5's "exactly three actions, nothing else" — actively encouraging extra spawns
  in a scenario that wants the model to stop. Now only set for `FreeBuildLayout` scenarios (G6).
- **G6-solo progress now shows elapsed/remaining time.** `BenchmarkProgress` tracks the current scenario's
  own timeout budget; the benchmark window falls back to a time-based bar/label instead of a static "0/1"
  count when only one scenario is running.
- Fixed a `set_velocity` PlayMode test flake: `Rigidbody.AddForce(Impulse)` only lands in `linearVelocity`
  on the next `FixedUpdate`, so a queued `apply_force` impulse could land after a subsequent `set_velocity`
  assignment and add on top of it once the async `WorldLlmTool` tool-call path (unlike the synchronous
  direct-executor calls) let a physics step slip in between the two calls.
- Verified end to end on `qwen3.5-4b-mtp` (local LM Studio): EditMode 1361/1361, PlayMode `FastNoLlm`
  48/48, full G1-G6 live suite 94.1/100 (20 PASS / 3 PARTIAL / 1 FAIL).

### Second benchmark audit pass: grading gaps + custom-prompt scoring (2026-07-01)

- **`RoleFitness` "Orchestrator / Director" now carries an honest scope caveat.** A small model can rate
  9+/10 off G1-G7 alone since almost every scenario resolves in a single LLM turn — high Reasoning/Intent
  scores reflect "parsed the instruction correctly in one shot", not the sustained multi-turn orchestration
  with error recovery the role's own description asks for. The weights/gates are unchanged (that is a
  calibration decision, not a quiet fix); the role's reason text now says so explicitly.
- **G6 free-build with a full custom prompt (`COREAI_BENCHMARK_FREEBUILD_PROMPT`) is no longer graded
  against the built-in castle/generic checkpoints.** It still runs and screenshots, but a new
  `FailureAttribution.NotGraded` + `GameBenchmarkScenario.ExcludeFromScoring` keep it out of
  `SuiteBaseScore`/pass-rate/dimension breakdown — previously an arbitrary custom prompt (e.g. "spawn 3
  cubes") failed hard against the "at least 24 objects" castle checkpoint. A subject-only override
  (`COREAI_BENCHMARK_FREEBUILD_SUBJECT`) still uses the known `GenericGoal` scaffold and stays gradeable.
- **G1 world-building scenarios can no longer PASS by stacking every object at the same position** —
  added `spatial_spread` checkpoints (distinct occupied x/z cells) across all three G1 scenarios, plus a
  prompt requirement to place objects at distinct positions.
- **G6 free-build grading fixes**: the generic-subject prompt said "at least 24 objects" but grading only
  required 18 (now unified to 24, with distinct-names raised from 14 to 20 to match); bounds checking
  (`CountBoundsViolations`) checked only the spawn pivot, not the scaled extent, so an oversized object
  could sit mostly outside the -9..9 build volume and still pass (now checks real half-extents, including
  the true 2m cylinder/capsule height); `IsTowerLike()` accepted any cylinder/capsule near a corner
  (four thin flag poles could satisfy "four corner towers") — now also requires height >= 2.5m and
  footprint >= 1m.
- **G5 `exact_count`-style constraints now count actual tool-call attempts, not just recorded world
  commands** — a 4th malformed/failed `world_command` after 3 valid spawns previously still read as
  `total == 3`; `g5_exactly_three` now uses `max(recorded commands, actual tool-call attempts)`.
- **G4 playthrough scenarios now clarify that `logic_define` does not create a directly-callable global** —
  weak models sometimes tried invoking their own defined slot as a plain Lua function afterward and hit
  `attempt to call a nil value`; the harness invokes registered slots itself with hidden samples.
- **`BenchmarkReport.MeanBaseByScenario()` now excludes `Environment`/`Framework`/`NotGraded` results**,
  consistent with every other aggregate (`Scenarios()`, `SuiteBaseScore`, `DimensionBreakdown()`) — it
  previously averaged over the raw, unfiltered result list.
- Cleaned up stale `quad` references left over from its removal (README, `ICoreAISettings.cs`,
  `LLM_TOOLS.md`, `WORLD_COMMANDS.md`, `JSON_COMMAND_FORMAT.md`, `TOOL_CALL_SPEC.md`) and stale
  "median" comments/tooltips describing the (already mean-based) repetition aggregation.
- Removed a blank placeholder entry from `CoreAiPrefabRegistry.asset` (harmless — `TryResolve` rejects
  blank keys — but noisy in the inspector).
- Two independent Codex audits (one scoped to `RoleFitness`, one a fresh full-harness sweep) found and
  fixed all of the above; verified via `dotnet build`, EditMode 1361/1361, PlayMode `FastNoLlm` 48/48, and
  a live G6 custom-prompt run against `qwen3.5-4b-mtp` confirming the excluded scenario now shows "No
  graded groups" instead of a punishing FAIL.

### Benchmark report + harness fixes, G7 comprehensive scenario (2026-07-01)

- **Modelcard PNG: fixed text overflow and role-fitness number clipping.** The header (model id + score)
  used a fixed `characterSize` regardless of length — a long OpenRouter-style id could overflow the card;
  it now shrinks by length and hard-truncates past 68 characters, matching the policy the hero screenshot
  already used. Role-fitness rating numbers were clipped at the right edge of the perspective frustum
  (`barX0+barW+0.04=1.19` vs a ~1.326-unit half-width, ~0.136 margin); `barW` and the number offset were
  reduced so the margin is now ~0.276.
- **Scene screenshots had no shadows.** `AddComponent<Light>()` defaults to `LightShadows.None`; neither the
  key nor fill light in the benchmark's scene-screenshot setup ever enabled shadows. The key light now casts
  soft shadows (fill stays shadowless, avoiding a second conflicting shadow direction).
- **`quad` removed as a spawnable primitive.** Flat, single-sided, and a source of confusion; dropped from
  `CoreAiPrimitiveFactory`, `world_command`'s tool schema/descriptions, the G6 free-build prompts, and the
  `allowWorldPrimitives` tooltip. `plane` remains supported but unadvertised (unchanged).
- **`world_command` now documents that cylinder/capsule are 2m tall unscaled** (cube/sphere/plane are 1m) —
  found via a real bug this caused: G6's castle-skeleton prompt told the model `scaleY=3` for a corner
  tower cylinder, which at the true 2m base height produced a 6m tower half-buried in the ground
  (`y=-1.5..4.5`), with the "flag on top" landing inside the tower instead of above it. Fixed to
  `scaleY=1.5` (3m tower, `y=0..3`), consistent with the battlements/flags already anchored at `y=3`/`y=4`.
- **G5 "Ordered spawn" no longer passes with a trailing extra spawn.** `exact_order` checked only that the
  first three spawn names were `Gate, Player, Flag` (`spawnNames.Count >= 3`); a fourth spawn after `Flag`
  still satisfied it despite the goal saying "Flag must be the last". Now requires `Count == 3`.
  (Found via a Codex audit of the benchmark harness.)
- **Scene screenshots now reflect `change`/`set_color`.** `VisualBenchmarkWorldExecutor.OnCommand` handled
  only the legacy `move`/`set_scale`/`rotate` actions; a model correctly using the current `change` action
  to reposition/rescale/rotate an object after spawning it, or `set_color` to tint it, had that update
  silently dropped from the screenshot. (Found via the same audit.)
- **G7 — new "comprehensive integration" scenario group.** G1-G6 each isolate one skill, so even a 4B
  reference model scores 90+ across the suite. G7's "Key Puzzle" requires `world_command` spawns (exact
  zones/order/shapes) AND an `execute_lua` distance-threshold slot together, then feeds the model's OWN
  recorded spawn position back into its OWN logic slot to check they stay consistent — a model can pass an
  isolated spatial check and an isolated logic check while still being inconsistent between the two.
  `Difficulty` 9 (hardest), runs once regardless of suite reps (see below).
- **Per-scenario repeat count generalized.** `GameBenchmarkScenario.Repeatable` (bool) is now
  `RepsOverride` (`int?`): `null` inherits the suite's `COREAI_BENCHMARK_REPS`, a concrete value (typically
  `1`) always runs exactly that many times — used by both G6 (visual hero) and G7 (comprehensive). Also
  fixed the progress-bar total, which previously assumed every scenario ran the full suite rep count.
- Verified end to end on `qwen3.5-4b-mtp`: EditMode 1361/1361, PlayMode `FastNoLlm` 48/48 (re-run 3× across
  this round's fixes), full G1-G7 live suite 91.3/100 (19 PASS / 4 PARTIAL / 1 FAIL, G7 100/100 1/1 pass).

## 4.17.0 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.17.0**.

- **Tool-call history is unlimited by default (0).** `maxToolCallHistoryMessages` defaulted to 20, so a
  long tool-calling turn silently dropped the model's earliest steps — a 30+ step build (e.g. the
  benchmark castle, or a multi-file refactor) would forget the first ~15 things it did and repeat them.
  The default is now **0 = unlimited**: the **Programmer** role and the orchestrator keep full sight of
  everything they did in a turn. Conversation summarization + context-overflow retry still bound truly
  long sessions. Set a positive cap only to deliberately bound context growth.
- **Flexible tool-call roundtrip limits.** The roundtrip cap (LLM call + tool batch per iteration) is now
  configurable per agent and per task, not just globally:
  `new AgentBuilder("Builder").WithMaxToolCallRoundtrips(0)` removes the cap for a free-build agent;
  `WithMaxToolCallRoundtrips(5)` tightens a quiet NPC; `AiTaskRequest.MaxToolCallRoundtrips` overrides a
  single call. `0` = unlimited, `null` = inherit. Default raised **10 → 20**; built-in **Programmer** and
  **Creator** roles default to unlimited. When the cap is hit the warning explains how to raise or disable
  it. The global `CoreAISettingsAsset` value now also accepts **0 = unlimited** in the inspector.
- **Full-tier Lua: create & wire game objects.** Added `unity_add_component(id, type)` (reflection
  AddComponent for any Component type) and `unity_destroy(id)`. Coercion now also handles Rect, Bounds,
  Color32, all numeric widths, enum-by-number, and — most importantly — **Unity object references by
  instance id**, so a mod can assign a Material/Texture/Transform, not just set value types. Full Lua is
  now a complete game-authoring surface (create objects, add/configure/wire components, call methods, run
  `hooks_every` timers, react to events). 16 EditMode cases cover it.
- **`world_command` spawn accepts rotation + scale inline**, and the tool schema now documents it so the
  model can discover it. Unnamed spawns get a readable name (`cube_1`) instead of a GUID hash.
- **Benchmark.** Fixed the silent 10-roundtrip throttle that zeroed the castle score; soft 10-min suite
  budget that still writes the report on timeout (vs NUnit's hard abort); the model is told its time
  budget with a live countdown per spawn; hero image bakes tool-calls/spawns/gen-seconds/tokens/tok/s;
  a single source of difficulty (1–10) so the editor and history agree; honest throughput labeling
  (provider-call vs decode); a live TTFT-based decode tok/s test comparable to LM Studio.
- **Demos** reorganized: each demo's scripts moved into a `Scripts/` subfolder (GUIDs preserved). The
  FullAccess demo showcases `unity_list_members` + member coercion.

## 4.16.0 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.16.0**.

- **Spawn with rotation + scale in one call.** The `world_command` `spawn` action now accepts optional
  rotation (`fx/fy/fz` degrees) and uniform `scale`, so a model can place an object with the right
  orientation and size in a single tool call instead of three.
- **`component_command` tool** — add / remove / set / list Unity components through a curated, reflection-free
  catalog (rigidbody, colliders, light, audiosource, camera, renderers, particlesystem, …). Matching Lua
  bindings: `coreai_component_add/remove/set_number/set_bool/set_text/set_vector`.
- **Full-tier Lua UX** — `unity_list_members` discovery, rich Color/Vector/Quaternion coercion from Lua
  tables, and did-you-mean errors that list valid members, making Full reflection as convenient as the
  curated path.
- **G6 free-build is overridable** — ask for a city, a character, a spaceship, … via the benchmark window
  field or `COREAI_BENCHMARK_FREEBUILD_SUBJECT` / `COREAI_BENCHMARK_FREEBUILD_PROMPT`. Default stays the
  detailed castle.
- **Benchmark fixes** — decode tok/s now reports real throughput (`max(provider, tokenizer-estimate)` incl.
  tool-call JSON; was undercounting to ~0.3), RunId uses local time, a live preview camera shows the scene
  building in real time, and the tool-call roundtrip cap is configurable (`COREAI_BENCHMARK_ROUNDTRIPS`,
  default 40) so free-build scenes can emit 24+ spawns.
- **8-model G6 castle gallery** in the README, plus `AllowWorldPrimitives` settings toggle.

## 4.15.3 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.15.3**.

- **Castle = creative freedom.** G6 now asks for the most impressive castle the model can build within the
  coordinate bounds (no fixed blueprint), so the hero image reflects each model's own design.
- **G6 off by default** in the benchmark window (RUN tab toggle) — it is a bonus visual, not a scored core
  group. Enable it per run when you want a castle hero.

## 4.15.2 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.15.2**.

- **Scene/castle screenshots show the model name** as the headline (long ids wrap to two lines).
- **Castle hero blueprint.** The G6 prompt gives an explicit coordinate plan + higher output budget, so
  the castle reads as a real square castle (towers, walls, gate, keep, flags) across models.

## 4.15.1 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.15.1**.

- **Report uses averages.** Multi-repetition runs are aggregated per scenario by the **mean (average)**
  over repetitions (see the core 4.15.1 entry).
- **Castle runs once.** The G6 castle hero (`Repeatable = false`) always runs a single time, even when the
  suite is set to repeat every other scenario (e.g. reps = 3 for G1–G5, G6 once).
- **Window shortcuts.** The UITK benchmark window toolbar gains **Open folder** (reveals
  `TestResults/CoreAI/Benchmarks`) and **Open report** (opens the most recent `.md` report) buttons.

## 4.15.0 - 2026-06-30

Depends on **`com.neoxider.coreai` 4.15.0** (Game-Creation Benchmark reporting polish and audit fixes).

- **feat(benchmarks): G6 castle free-build hero.** The live benchmark now includes a free-form castle
  showcase scenario and embeds the resulting hero screenshot while preserving the model-authored layout.
- **Visual model reports.** Per-model cards show the dimension radar and role fitness bars; role-shaped
  scene screenshots now include ghost markers for expected objects the model missed.
- **Speed clarity.** Reports distinguish decode tok/s (time inside LLM calls, comparable to LM Studio) from
  effective tok/s for the whole agentic session, and show repetitions so median stability is visible.
- **Cross-model comparison.** The UITK benchmark window adds Run, History, **Models** (a sortable
  leaderboard ranked by score / speed / pass-rate / game-fit) and Compare tabs; the comparison flow builds a
  TerminalBench-style bar chart from per-model JSON, with default ranked ordering or a pinned-first mode for
  baseline-vs-candidate reviews.
- **LM Studio sweep workflow.** Multi-model sweeps can load/unload models with `lms`, run each benchmark
  model, and rebuild the comparison from the newest JSON reports.
- **Audit fixes.** Benchmark rendering and report generation fixed material/mesh leak risks and
  generation-time serialization gaps used by screenshots, model cards, and comparison output.

## 4.14.0 - 2026-06-29

Depends on **`com.neoxider.coreai` 4.14.0** (portable Game-Creation Benchmark scoring core).

- **feat(benchmarks): Game-Creation Benchmark.** New live PlayMode suite where a model builds a game by
  driving the real `execute_lua` and `world_command` tools, scored 0..100.
- **Scenario groups:** G1 build-a-game (world + Lua), G2 runtime mechanic authoring (pure Lua), G3
  reasoning and design (harder, no spoon-fed code), G4 playable game (the harness simulates a full
  playthrough through the model's rule slots and verifies the trajectory), and G5 strict
  instruction-following (subtractive scoring — prohibitions, exact counts, forbidden tools, tool budgets,
  ordering; violations detected deterministically from the tool-call trace and world commands).
- **Summary dimensions:** Tool correctness, Intent & sequence, Task completion, Determinism, Reasoning,
  and Instruction adherence.
- **Efficiency and reliability accounting:** token/time efficiency bonus for fewer tokens and less time
  (base score >=90, capped at 20), honest generation tokens/sec from completion tokens only,
  transient/crash retries, environment failures excluded from the model score, per-scenario timeouts, and
  per-scenario medians over repetitions.
- **Visual results:** world-building scenarios spawn real GameObjects and a screenshot of the built scene
  is captured and embedded in the report (and shown inline in the History window); skipped headlessly.
  Screenshots are limited to G1 build-a-game scenarios (where the scene is the deliverable). Each object is
  rendered by its ROLE (capsule = player, sphere = enemy, puck = coin, post = goal, etc.) so the scene reads
  like a real prototype, and status is shown per object: expected = role colour + ✓, unexpected/extra = red
  ✗, and objects the model never built appear as faint grey ghosts marked ✗ — so a weaker model's picture
  literally looks incomplete. Each image bakes a header (scenario, score and PASS/PARTIAL/FAIL verdict,
  tinted by outcome) and a 'what it checks' caption.
- **Model card (per-model comparison image):** after a run, a 1280×720 card is rendered — a 6-axis radar of
  the benchmark dimensions plus a game-fitness bar per role and the headline score. A strong model fills the
  hexagon; a weak one is small and dented on its weak axis, so two models' cards compare at a glance. Leads
  the Markdown report.
- **Game-fitness by role:** the report and the History window rate the model 0..10 for each game-dev role
  (NPC, Mechanic, Tool Operator, Programmer, Orchestrator, QA) with a verdict and a reason — the headline
  'usable for my game, and for which role' answer. Roles whose dimensions a partial run did not measure are
  shown as 'Not assessed' rather than over-rated, and the overall reflects agentic roles only.
- **Reports:** Markdown with an embedded SVG results card, scene screenshots, and full model session
  transcript, machine-readable JSON, rolling `INDEX.md`, and a cross-model comparison report.
- **Editor window:** **CoreAI > Benchmarks** adds Run and History tabs, live progress, per-run dimension
  breakdown, inline scene-screenshot thumbnails, open/delete actions, and model comparison.

## 4.13.0 - 2026-06-28

Depends on **`com.neoxider.coreai` 4.13.0** (parallel tool calls, XML tool-call parsing, real BPE tokens,
agent-authored skills).

- **feat(tools): parallel tool-call execution** + `CoreAISettingsAsset.MaxParallelToolCalls` ([Range 1–16],
  default 4). See the Core 4.13.0 entry for the ordering/serialization guarantees.
- **feat(tools): Hermes/Qwen-Agent XML tool-call parsing** — local GGUF models that emit
  `<tool_call><function=…>` text now have their calls executed (Core `LlmToolCallTextExtractor`).
- **feat(skills): agent-authored skills** — `manage_skills` + file-backed `FileSkillStore`
  (`Application.persistentDataPath/CoreAI/Skills`), versioned and surfaced into `read_skill`.
- **feat(context): real BPE token counting** (Core) with calibrating-estimator fallback; activate by adding
  tiktoken rank files (see `Docs/CONTEXT_MANAGEMENT_ROADMAP.md`).
- **test/infra: configurable live PlayMode provider** — point the suite at any OpenAI-compatible
  provider/model via env vars or a gitignored `coreai-live-tests.local.json` (see `Docs/RUNNING_LIVE_TESTS.md`).
- **test:** EditMode coverage for parallel batch execution (order, concurrency, mutating-tool serialization),
  XML extraction, BPE counter (encoding resolve + synthetic-ranks merge + estimator fallback), and skill
  authoring (create/update/delete/rehydrate/allowlist).

## 4.12.1 - 2026-06-28

Depends on **`com.neoxider.coreai` 4.12.1** (native-role memory-instruction fix).

- **fix(prompts): native tool-calling roles with the memory tool now get the memory instruction.**
  See the Core 4.12.1 entry — the guidance was gated behind the native early-return in
  `AiToolContractPromptFormatter`, so a role like Creator ignored "remember the …" tasks. Validated by the
  live multi-agent crafting workflow (`MultiAgentCraftingWorkflowPlayModeTests`): Creator now reliably
  persists the design summary. Lockstep version bump.

## 4.12.0 - 2026-06-28

Depends on **`com.neoxider.coreai` 4.12.0** (Lua mod versioning + runtime handler-error diagnostics).

- **feat(streaming): keep streaming live through tool calls (Kilo/Cline-style on-the-fly hold).** A
  tool-calling turn no longer stops token streaming. `MeaiLlmClient.CompleteStreamingAsync` now walks the
  accumulated text into prose segments (streamed live, token-by-token) and completed text-shaped tool-call
  JSON spans (hidden in place), holding only from the first still-incomplete `{`. Prose **after** a tool
  call's closing `}` resumes live in the same turn; only the tool-call JSON is ever hidden, never the
  surrounding prose/preamble. **No full-turn buffering is introduced** — the 4.10.4 regression (buffering all
  bound-tool turns, which killed token-by-token streaming, reverted in 4.10.5) is explicitly avoided. New
  internal helpers `GetHybridSafeSegments` / `GetHybridUnemittedSuffix` / `HybridProseSegment`.
- **feat(vision): host camera→model send path + capability gate.** New
  `CoreAiChatService.AskWithCameraAsync` / `CoreAi.AskWithCameraAsync` capture a camera as a `DataContent`
  and send it as a user `image_url` message (the provider-safe camera→model path). `CoreAi.RegisterCameraVisionTool`
  registers `capture_camera` on a vision role behind a gate; `AskWithImageFollowUpAsync` lifts a
  tool-result image into a follow-up user `image_url` message (OpenAI tool results cannot carry images).
  New `VisionCapability` + `CoreAISettingsAsset.VisionSupport` (`Auto`/`On`/`Off`) gate both the send path and
  tool registration so text-only models never receive an image part.
- **feat(lua): WebGL Lua IL2CPP/AOT hardening.** Lua already ran on WebGL behind
  `CoreAISettingsAsset.EnableLuaOnWebGl` (default on) with the Full `unity_*` tier disabled on web; this drop
  adds `link.xml` `preserve` entries for the six WebGL-active Lua binding types so the IL2CPP managed
  stripper cannot drop the reflection-invoked delegates.
- **test:** hybrid-segment streaming units, vision capability gate + tool-result image lift, Lua mod
  versioning / revert / runtime-error diagnostics, per-tick event dispatch cap, and a forbidden-Lua-API
  drift guard between the `execute_lua` contract and the Programmer prompt.

- **fix(webgl): non-streaming completions work alongside native SSE streaming.** When
  `WebGlNativeStreaming=true`, `MeaiLlmClient.CreateHttp` built a streaming-only
  `FetchSseOpenAiTransport` for *all* calls. Its `PostNonStreamingAsync` throws
  `"Use UnityWebRequestOpenAiTransport for non-streaming in WebGL."`, so every non-streaming agent
  (e.g. `TeacherLessonFeedback` hidden lesson feedback, structured-output analyzers) failed on WebGL
  with `BackendUnavailable` after retries — the per-mission teacher feedback never reached the host/DB.
  New `WebGlCompositeOpenAiTransport` routes SSE chat through the fetch bridge and non-streaming POSTs
  through `UnityWebRequestOpenAiTransport`, so both paths work in one player. The gateway already serves
  `stream:false` (non-streaming `POST /chat/completions`), so this is wiring-only.
  Test: `MeaiOpenAiWebGlTransportEditModeTests.GetResponseAsync_SseCapableTransport_StillServesNonStreamingCompletion`.

Depends on **`com.neoxider.coreai` 4.11.5**.

- **fix(chat): WebGL GPU-buffer backstop for oversized assistant messages.** A single very long message
  (a real incident leaked ~16 000 chars of model reasoning) rendered into one bubble overflows UI Toolkit's
  vertex/index buffer in WebGL (`GfxDevice::CopyBufferRanges: range reads out of bounds` →
  `memory access out of bounds` → app crash). New `CoreAiChatPanel.ClampAssistantForRender` hard-caps
  assistant text (`MaxAssistantRenderChars = 4000`, closes a dangling ``` fence), applied in `AddMessage`
  for the assistant branch. Render-only — the full message still lives in chat memory/history; only the
  drawn text is bounded. Pure string logic (no UITK API), so it holds on every supported Unity (6.0+).
  Tests: `CoreAiChatPanelRenderLimitTests` (EditMode). Hosts may still cap earlier (RedoSchool caps at its
  markdown render chokepoint); this is the package-level backstop for every consumer.
- **uGUI chat integration docs.** `README_CHAT.md` now documents the supported Canvas/uGUI path: reuse
  `CoreAiChatService` or the static `CoreAi` facade for chat logic while the host game owns its own
  `TMP_InputField` / `ScrollRect` / `Button` view.
- **Tool audit + `LLM_TOOLS.md`.** Audited every `ILlmTool`: no broken/orphaned tools remain. New
  `Assets/CoreAI/Docs/LLM_TOOLS.md` documents built-in tools vs **host-wired** tools (`world_command`,
  scene query, `capture_camera`, game config, inventory, compatibility) that are functional and tested
  but require game-specific context, so the host opts in via `AgentBuilder.WithTool(...)`.

## 4.11.3 - 2026-06-23

Depends on **`com.neoxider.coreai` 4.11.3**.

- **Agent Session Inspector history tab rendering.** The Saved detail tabs now keep independent scroll
  positions and clamp stale scroll offsets to the current panel height, so switching from a long
  Session/System view no longer leaves the History tab visually blank. `Copy history` continues to copy
  the same non-system history text.

## 4.11.2 - 2026-06-23

Depends on **`com.neoxider.coreai` 4.11.2**.

- **`CoreAi.InjectSkillIntoHistory(roleId, skill)`.** Force-inject a skill into an agent's history at any
  moment (preloads the `read_skill` payload) without running a model turn — the agent does not start
  answering, the skill is just there on its next turn. Resolves the memory store from the active scope and
  stores the row with the hidden `"tool"` role (model sees it, chat does not). New EditMode tests cover the
  injection, the hidden-row role, and invalid-input handling.

## 4.11.1 - 2026-06-23

Depends on **`com.neoxider.coreai` 4.11.1**.

- **Tool failures surfaced accurately (chat-gated).** A failed tool-only turn now resolves to
  `Tool call failed: <tool>: <reason>.` instead of `LLM request failed.` The chat panel still hides these
  `Tool call …` lifecycle lines (success and failure alike) when `ShowToolCallsInChat` is off, so a
  clean-chat setup is unchanged while the model always receives the full error.
- **Docs + tests.** `TOOL_CALL_SPEC.md` documents the `[ToolCall] … status=OK|FAIL … result=…` debug log
  and the logging flags. New `ToolExecutionPolicy` EditMode tests verify the debug line records FAIL status
  and the result detail (and OK on success).

## 4.11.0 - 2026-06-23

Depends on **`com.neoxider.coreai` 4.11.0**.

- **Agent Session Inspector "Live turn" view.** New `Saved | Live turn` toggle in the
  **CoreAI > Agent Session Inspector** window. *Saved* keeps the existing persisted-snapshot behavior.
  *Live turn* reads the latest turn trace from the running container (`IAgentTurnTraceReader`) and renders
  the composed user prompt, each tool call (name, status, duration, source, result), the assistant text,
  and the turn status/timing — including mid-turn and failed turns that never reach persisted chat history.
  When no readable trace sink is registered (the default `NullAgentTurnTraceSink`), the view explains how
  to opt in; when none is recorded yet, it shows a clear "No turn recorded yet" message.
- EditMode coverage for `InMemoryAgentTurnTraceSink` latest-per-role retention and live-turn fields.

## 4.10.5 - 2026-06-22

Depends on **`com.neoxider.coreai` 4.10.5**.

- Restore live token streaming for tool-declared turns (4.10.4 buffered them and lost streaming).
  Keep the failed-tool status suppression.

## 4.10.4 - 2026-06-21

Depends on **`com.neoxider.coreai` 4.10.4**.

- **Streaming chat hides tool-turn preambles.** `MeaiLlmClient` now buffers bound-tool streaming iterations
  until it knows whether the model actually called a tool. Native and text-extracted tool turns still add
  pre-tool prose to the assistant message history for model context, but no longer yield that prose into the
  visible chat bubble before tool execution.

## 4.10.3 - 2026-06-21

Adversarial module audit fixes (Unity layer). Confirmed by two independent passes (find + verify).

- **Memory rollback + prompt-cache restored (HIGH).** `FileAgentMemoryStore` dropped
  `AgentMemoryState.Versions`, `SystemPromptMemorySnapshot/Version`, and `MaxMemoryVersions` on every
  save (the persisted DTO had no fields for them), so the documented `ListVersions`/`Revert` rollback
  always failed and the stable-prefix `## Memory (updates)` tail optimization never engaged across
  requests. These fields are now round-tripped (versions serialized via `JsonConvert`).
- **`coreai_world_grid` DoS fixed (HIGH).** The cell-count guard computed `xCount * zCount` in `int`,
  which overflowed to a small value (e.g. `2^16 * 2^16` wraps to 0) and slipped past the `MaxBatchSize`
  cap, then ran a multi-billion-iteration CLR loop the Lua instruction limiter cannot interrupt. The
  product is now computed in `long` and rejected before any allocation.
- **`load_scene` honours the scene whitelist on every path.** The native `world_command load_scene` tool
  bypassed the `allowedLuaScenes` whitelist that the Lua binding enforced. Validation now lives in
  `CoreAiWorldCommandExecutor`, so both the native tool and the Lua binding honour it.
- **Lua mod persistence on WebGL.** `FileLuaModSourceStore` and `FileLuaModStore` now flush IDBFS
  (`CoreAi_PersistFsSync`) after writes, so saved mod packages/source/key-values survive a WebGL tab
  reload instead of being lost.
- **`LlmAuthExpired` event delivered.** The MessagePipe broker for `LlmAuthExpired` (published by
  `RefreshOnUnauthorizedDecorator` on a failed auth refresh) is now registered in `CoreServicesInstaller`
  and `GlobalMessagePipeMinimalBootstrap`; previously `GetPublisher` threw and the re-login event was
  silently swallowed.
- **Full-tier Lua fail-open is no longer silent.** `CoreAiFullUnityLuaRuntimeBindings` logs a warning
  when Full reflection runs with no blacklist policy (allow-all).
- **Leak/cleanup fixes.** The auto-created `CoreAiPrefabRegistryAsset` ScriptableObject is now destroyed
  on scope teardown (was leaked per container build), and `FileTokenCalibrationStore` deletes its temp
  file when the atomic swap throws (matching the other file stores).

## 4.10.2 - 2026-06-21

- **Chat UI hides internal tool notifications.** `CoreAiChatPanel` now skips persisted `tool`/`system`
  rows in the normal chat transcript and hides assistant fallback lifecycle strings such as
  `Tool call completed: ...` unless tool-call diagnostics are explicitly enabled, while preserving
  normal user and assistant prose.

## 4.10.1 - 2026-06-20

- **Unity 6.5 compile fix (CS0619).** Unity 6.5 marked BOTH the `EntityId -> int` implicit cast and
  `Object.GetInstanceID()` as obsolete ERRORS. `GetObjectId()` in `SceneLlmTool` and
  `CoreAiFullUnityLuaRuntimeBindings` now use `obj.GetEntityId().GetHashCode()` — a stable, session-unique
  int for the in-session object lookups these ids feed — without any obsolete API.

## 4.10.0 - 2026-06-20

Depends on **`com.neoxider.coreai` 4.10.0**.

- **Camera vision tool fixed end-to-end.** `CameraLlmTool` previously only returned a base64 string the
  model could not see. Capture is now a reusable `CameraLlmTool.CaptureCameraJpeg(...)` (offscreen render
  → JPEG, resolution clamped, render state restored) plus `CaptureCameraImageContent(...)` that returns a
  MEAI `DataContent` (`image/jpeg`). Attached to a user message, it flows through the core's new
  `image_url` serialization so a vision-capable model receives the frame. Tests:
  `CameraLlmToolPlayModeTests` (valid JPEG render) + `MeaiOpenAiVisionEditModeTests` (image serialization).
- **Persistent file-backed Lua mod packages.** Mods are now durable and shareable through the portable
  `ILuaModSourceStore`. A `FileLuaModSourceStore` persists each mod's source plus its `LuaModManifest`
  (`id`, `name`, `description`, `version`, `author`, `capabilities`, `active`, `entry`) under
  `persistentDataPath/CoreAI/Mods/<id>/` as `manifest.json` + `main.lua`. This is separate from the
  per-mod `store_set`/`store_get` key/value store; the source store persists the mod itself. Without a
  wired store the runtime falls back to the in-memory `NullLuaModSourceStore` (previous behavior).
- **`manage_mods` auto-persists and survives restart.** Loading or reloading a mod through chat now
  auto-saves it; `unload` marks the stored package dormant instead of deleting it. On startup
  `LuaModRuntime.RehydrateFromStore(hostGrant)` re-loads every active stored mod, so a mod created in
  chat is back the next time you press Play. The `manage_mods` tool adds `export`, `import`, and
  `forget` actions alongside `load`, `reload`, `unload`, `list`, and `get_source`.
- **Export / import / forget to move mods between players.** `export` returns a self-contained
  `{"manifest":{...},"source":"..."}` bundle that another player can `import`; a mod folder can also be
  copied directly between `persistentDataPath/CoreAI/Mods/<id>/` paths. `forget` permanently removes a
  stored package.
- **Full OFF by default for persisted/shared mods.** Rehydrate and import intersect the mod's requested
  capabilities with the host grant and strip `Full` unless the host explicitly opts in, so a persisted,
  imported, or copied mod can never silently gain reflection (`unity_*`) access. Capability parsing is
  fail-closed.
- **First-class `.lua` TextAssets.** New `LuaScriptedImporter` imports any `*.lua` file as a
  `TextAsset`, so mods can be authored with a real `.lua` extension (editor recognition, drag-and-drop)
  instead of the `.lua.txt` workaround; `asset.text` returns the source. Text-only, no MoonSharp
  dependency, works in no-Lua builds.
- **Docs and demos.** Added `Assets/CoreAI/Docs/FIRST_MOD.md` ("Your first Lua mod in 5 minutes"),
  linked from `DOCS_INDEX.md`; `LUA_GAME_API.md` gains a Persistence & Sharing section and
  `LUA_ACCESS_MODES.md` notes the non-Full default. Ships a no-LLM Full-mode mod demo plus example
  `.lua` mods. Marked the reusable file-backed Lua mod packages item done in `BACKLOG.md`.

## 4.9.0 - 2026-06-20

- **Full Lua fix on Unity 6000.3+.** `CoreAiFullUnityLuaRuntimeBindings.Resolve` resolved object ids
  via `Resources.InstanceIDToObject`, but `GetObjectId` hands out `GetEntityId().GetHashCode()` on
  Unity 6000.3+ — a different id scheme — so every `unity_*` call that round-trips an id (`unity_get_position`,
  `unity_set_position`, `unity_get_member`, `unity_call`, describe/hierarchy) failed with
  "object id … not found" in Edit mode. `Resolve` now matches by the same `GetObjectId` scheme, fixing
  it across Unity versions and in both Edit and Play mode (verified: EditMode `CoreAI.Tests` 1168/1168).
- **WebGL Lua opt-in (Unity).** `CoreAISettingsAsset.EnableLuaOnWebGl` inspector flag (**on by default**
  for new assets) wires `SecureLuaEnvironment.WebGlLuaOptIn` at bootstrap; `CoreAILifetimeScope`
  force-disables the Full `unity_*` reflection tier on the WebGL player. `Assets/link.xml` preserves
  `MoonSharp.Interpreter` against IL2CPP stripping. New `WebGlLuaSelfTest` demo component runs the
  sandbox self-test in a build.
- **Audit hardening (Unity).** Full-tier `unity_*` object ids now use the stable entity-id value instead
  of `GetEntityId().GetHashCode()` (eliminates hash-collision → wrong-object mutations); overloaded-member
  reflection (`unity_call` / member access) raises a clear `ScriptRuntimeException` instead of a raw
  `AmbiguousMatchException`. Added EditMode/PlayMode coverage: mod global event budget, `WaitLlmTool`
  clamping, SSE split-arg / parallel-index / malformed `tool_calls`, trim-pair history, and a PlayMode
  `unity_find`/`unity_set_position` mutation test. Verified: EditMode `CoreAI.Tests` 1180/1180,
  PlayMode `FastNoLlm` 43/43.

## 4.8.2 - 2026-06-19

- **World-space chat auto-focus opt-out.** `CoreAiChatPanel` now exposes `protected virtual bool AutoFocusInputFieldEnabled => true;`, checked inside `FocusInputField()`. Subclasses used in world-space / gaze scenes can override it to `false` to suppress automatic keyboard-focus stealing after a message is sent, after each AI turn, and when the panel is expanded — default screen-space behaviour is unchanged.

## 4.8.1 - 2026-06-19

- **Unity 6.5 PanelRenderer chat host.** `CoreAiChatPanel` now uses `PanelRenderer.RegisterUIReloadCallback`
  on Unity 6.5+ so host projects can migrate runtime UI Toolkit chat panels away from `UIDocument`.
- **Unity 6.3 compatibility preserved.** `PanelRenderer` references are compiled only for
  `UNITY_6000_5_OR_NEWER`; older Unity 6.x projects continue to compile and run through the existing
  `UIDocument.rootVisualElement` initialization path.

## 4.8.0 - 2026-06-18

- **Chat config text overrides.** `CoreAiChatConfig` now exposes Inspector/runtime overrides for the chat panel's
  send/stop/clear/collapse/open labels and tooltips, including the default `>` send button text.
- **Chat panel copy mapping.** `CoreAiChatPanel` applies the new text overrides from assets or runtime options while
  preserving defaults for older `ICoreAiChatOptions` implementations; EditMode coverage verifies defaults,
  round-tripping, and custom send/stop labels.

## 4.7.0 - 2026-06-18

- **Skill authoring updates.** `SkillSetAsset.ApplyDefinition(...)` lets editor/bootstrap code create or update
  designer-authored skill assets from portable `SkillSetDefinition` snapshots without private-field reflection.
- **Skill proxy regression coverage.** EditMode tests now cover skill actions returning explicit success, direct
  `IJsonInvocableLlmTool` invocation through `call_skill_tool`, skill-only agent validation, and
  `SkillSetAsset` create/update mapping.

## 4.6.2 - 2026-06-18

- **Agent Session Inspector scope selection.** In Play Mode the editor window now prefers the live scope with the
  richest role set, so game child scopes such as RedoSchool's `GameLifetimeScope` expose project agents and tools
  instead of falling back to the parent CoreAI scope.

## 4.6.1 - 2026-06-18

- **NoLua compile fix.** `IFullLuaAccessBlacklistPolicy` is now available outside the Lua/MoonSharp compile guards,
  so `WorldCommandsInstaller.RegisterWorldCommands(...)` keeps a stable public signature and projects with
  `COREAI_NO_LUA` compile without requiring Lua-only runtime bindings.

## 4.6.0 - 2026-06-18

- **OpenAI-compatible system-tail safety.** Non-leading system-role context messages are converted into
  provider-safe context text before the OpenAI-compatible payload is sent, while the stable first system prompt
  remains cacheable.
- **World audio tool contract.** `world_command play_sound` now requires both `targetName` and clip name
  (`stringValue`) and preserves the requested volume in the emitted world-command envelope; EditMode tests cover
  success and missing-argument errors for audio commands.
- **Scenario test decomposition.** Added targeted merchant economy tests for insufficient-gold no-mutation and
  discount-enabled purchase, plus a separate repeat-ingredients crafting determinism probe so long live-model
  scenarios are easier to diagnose.
- **Agent Session Inspector split views.** The editor window can copy/view the full session, system prompt, or
  history without system messages separately, and Play Mode role discovery refreshes manifest-defined custom roles
  such as `Teacher`.
- **Audit cleanup and backlog split.** Removed tracked audit documents, replaced Lua access-mode references with
  `LUA_ACCESS_MODES.md`, added `BACKLOG.md` for non-MVP future work, and updated release/funding metadata.

## 4.5.0 - 2026-06-18

- **Repository line-ending normalization.** Added Unity-friendly `.gitattributes` coverage plus an EditMode guard
  test so Unity/source text and binary asset classifications stay stable across contributors.
- **Streaming context-overflow recovery.** Added EditMode coverage for streaming context overflow: two retryable
  `ContextLengthExceeded` terminal chunks are hidden from callers while the orchestrator rebuilds with tighter
  `ContextRetryLevel` budgets and then streams the successful response.
- **Tail placement is mandatory.** The old `CoreAISettingsAsset` prefix-placement toggle and Inspector field were
  removed: summaries, world-state, and memory updates stay out of the stable system prefix until compaction or
  context-overflow retry creates a natural cache boundary.
- **Persistent token calibration store.** `CoreAILifetimeScope` registers `FileTokenCalibrationStore` on non-WebGL
  targets and keys calibration by the active model name.
- **Memory/read, wait, and tool-result tests.** EditMode coverage now includes `memory action=read`, the portable
  `wait` tool, tool-result call-id pairing, memory tail consolidation on summarization, and consolidation on
  context-overflow retry.
- **Empty tool-result normalization.** Tests cover the execution-policy guard that turns `null` tool output into
  an explicit payload while preserving the original call id.
- **Full Lua blacklist hook.** `WorldCommandsInstaller.RegisterWorldCommands(...)` can pass an
  `IFullLuaAccessBlacklistPolicy` into Full Lua reflection bindings; tests cover denied members and component types.
- **Lua mod events through MessagePipe.** Unity composition registers and publishes `LuaModEventEmitted`, making
  persistent mod events visible to MessagePipe subscribers in the same way LLM/tool diagnostics are.

## 4.4.0 - 2026-06-15

> Context management overhaul (Claude Code / Cline / Kilo-grade) + tool-call/memory/Lua fixes. Depends on `com.neoxider.coreai` 4.4.0. See entries below.

- GameMaster Lua mechanics test now advertises the logic_* slot API, matching the crafting test.
- **Agent memory clear regression fix.** The memory tool `clear` action now removes the role key instead of saving an empty versioned row.
- **Tool result memory defaults.** Built-in `Programmer` and `CoreMechanicAI` now default to full tool-result retention; other built-in roles keep compact summaries.
- **Prompt-cache usage verification.** CoreAI Unity now reads provider cache read/write token counts from
  MEAI `UsageDetails.AdditionalCounts` in both non-streaming and streaming paths, publishes them through
  `LlmUsageReported`, and documents that the current OpenAI/DeepSeek-compatible backend auto-caches a stable
  prefix without explicit `cache_control` markers.
- **Compaction by threshold.** `CoreAISettingsAsset` now exposes **Compaction trigger ratio** (default `0.8`)
  under Chat history summarization. Below `historyBudget * ratio`, CoreAI keeps all history verbatim and
  does not rewrite the stored rolling summary; `0`/invalid values fall back to the CoreAI default threshold.
- **Deterministic provider tool ordering.** MEAI native tool arrays now use the shared CoreAI
  ordinal-by-name tool order, matching the text-shaped tool contract order so identical role/tool inputs
  do not churn provider prompt-cache prefixes.
- **Dynamic world-state observation placement.** Tail placement now keeps per-role runtime/world-state context out
  of the stable system prefix by appending a final system-role `## World State` chat-history message after recent
  turns; the old flag-off system-prefix behavior was removed in 4.5.0.
- **Context editing before compaction.** Unity settings now expose `EnableContextPruning` (default on)
  and `MaxRetainedToolResultMessages` (default `3`). CoreAI prunes only the in-memory prompt history copy
  before summarization, dropping stale cross-turn `## Tool Results` observations while leaving stored chat
  history intact.
- **Emergency context-overflow recovery.** CoreAI Unity exposes `MaxContextOverflowRetries` on
  `CoreAISettingsAsset` (default `3`, `0` disables). The orchestrator now retries bounded
  context-length-overflow failures across multiple `ContextRetryLevel` passes, with each pass using the
  portable `0.75^level` history-budget rule.
- **Token accounting calibration.** CoreAI Unity now registers the portable `CalibratingTokenEstimator` as
  the shared pre-flight token estimator, exposes `EnableTokenCalibration` on `CoreAISettingsAsset` (default
  true), and reports the current estimate scale in Agent Session Inspector diagnostics.
- **Tool result memory policy.** Core chat history can now persist executed tool results per role
  (`None`, `ErrorsOnly`, `CompactSummary`, `Full`) with default compact summaries, intra-turn
  de-duplication, and provider-safe replay as user observations.
- **Context prefix stability flag.** `CoreAISettingsAsset` gained an opt-in cache-stability setting, letting
  `## Conversation Summary` travel as the first system-role chat-history message before recent verbatim turns.
  That toggle was later removed when tail placement became mandatory.
- **Agent Session Inspector JSON export.** Added a `Copy JSON` button that copies the full inspected `AgentSessionSnapshot` as indented JSON.
- **Default context window raised to 128K.** Unity settings assets, route profiles, and routing
  fallbacks now inherit the shared `131072` token default instead of `8192`; per-role
  `ContextTokens` defaults to `0`, meaning inherit the global `ICoreAISettings.ContextWindowTokens`
  unless a single role explicitly overrides it.
- **Agent Session Inspector works in Edit Mode.** When no live VContainer scope is available, the editor window reads the active scene's serialized `CoreAILifetimeScope`, prompt/settings assets, and persistent CoreAI memory read-only, then labels the snapshot source as `edit-mode (serialized scene)`.
- **Agent memory granular edits and versions.** `MemoryTool` now supports `str_replace`, `insert`,
  `delete`, and `rename` alongside existing `write` / `append` / `clear`, with bounded mutation
  snapshots on `AgentMemoryState` and store extension APIs for version listing and rollback. Runtime
  chat history now defaults on for built-in and builder-created roles (30-message cap, persistence
  still opt-in).
- **Version bump to 4.3.0** (`com.neoxider.coreai` + `com.neoxider.coreaiunity`).
- **Context management roadmap documented.** New `Assets/CoreAiUnity/Docs/CONTEXT_MANAGEMENT_ROADMAP.md`
  fixes the target design for Claude Code / Cline / Kilo-grade history handling: stable cacheable prefix +
  verbatim recent turns, threshold-based compaction (not per-turn re-summarization), a per-role
  `ToolResultMemoryPolicy`, token budgeting calibrated from real API `usage`, an emergency overflow fallback,
  and persistent cross-session agent memory. Tracked in root `TODO.md` → *Context management overhaul*. Design
  only in this release — implementation lands in follow-up tasks.
- **Reasoning-model HTTP controls and diagnostics.** `CoreAISettingsAsset` and
  `OpenAiHttpLlmSettings` now expose a tri-state **Reasoning Mode** for OpenAI-compatible backends:
  **Provider Default** sends no thinking controls, while **Disabled** / **Enabled** send compatible
  `enable_thinking` / `chat_template_kwargs.enable_thinking` request fields. The
  `OpenAiChatLlmClient(CoreAISettingsAsset)` adapter now forwards the same reasoning and extra-body
  settings used by lower-level HTTP clients, so PlayMode factory clients and demos do not silently
  fall back to provider defaults. The real-model chat streaming test now reports
  empty/reasoning-only output distinctly. Live-model PlayMode waits now use a consistent 120s budget
  for medium prompts and 240s for complex tool/crafting/benchmark turns, and the local qwen settings
  asset uses a 240s HTTP/LLM timeout with 128k context and 20k output tokens. Streaming think-block
  filtering now handles OpenAI-compatible reasoning output that arrives without an opening
  `<think>` tag but includes an orphan `</think>` before the visible answer. Crafting-memory /
  scenario tests no longer retry with exact Lua, tool payloads, or response text that helps the
  model pass. The ChatService all-modes integration test now treats the full mode-swap sequence as a
  complex scenario, the Lua runtime modification test validates the real `logic_define` rule-slot
  contract instead of a stale global-function assumption, and the merchant negotiation scenario now
  requires a real Iron Sword discount because the player budget is below the item price. The real
  chat streaming stop test now uses the complex first-token budget and stops an active turn before
  failing, crafting-name extraction covers escaped `execute_lua` tool arguments, and the duplicate
  same-ingredients TwoCrafts live-model probe is now explicit/targeted so the mandatory full suite
  keeps the stronger ThreeCrafts coverage without repeating the same expensive LLM path. The merchant
  negotiation prompt now states the player's purchase goal clearly without prescribing tool names or
  arguments, and the long full-negotiation scenario is explicit/targeted instead of a mandatory
  full-suite gate for slow local reasoning models. Shared PlayMode task waits can now cancel the
  test-owned LLM request on timeout so one timed-out turn does not keep the local backend busy for
  following tests; the two-phase full-pipeline memory read/write probe now uses the 240s complex
  turn budget. Targeted crafting regression coverage now handles `CreateItem("weapon", "Name")`
  Lua payloads and `item_name` assignments without treating escaped newline fragments such as
  `nlocal` as item names. The LLMUnity crafting harness now reports failed `execute_lua` records
  with arguments/result/error instead of downgrading them to inconclusive. The LLMUnity crafting
  harness also exposes the generic `logic_define` / `logic_reset` / `logic_list` APIs advertised by
  `execute_lua`, requires every craft to produce an extractable item name from the completed
  `execute_lua` arguments rather than accepting memory/prose-only output, canonicalizes verified
  craft memory between turns so model prose cannot pollute later prompts, and bounds each live craft
  response to 2048 tokens. The historical LLMUnity/backend-parity ThreeCrafts probe is explicit
  targeted; the mandatory full suite keeps `CraftingMemoryOpenAi_ThreeCrafts_AllUnique` as the
  representative Lua-backed ThreeCrafts gate. The shorter Creator/CoreMechanic multi-agent duplicate
  is now explicit/targeted because the full Creator/CoreMechanic/Programmer workflow is the
  mandatory representative scenario. The `AllToolCalls` memory test now uses required memory-tool
  mode as a narrow tool-binding mechanics check; autonomous memory-tool selection remains covered by
  the dedicated AgentMemory and resilience PlayMode tests. The AINpc tools/chat test now bounds its
  live response and cancels the active request on timeout. The SkillSet benchmark now treats
  with-skill and direct-tools timeouts symmetrically as benchmark data and cancels the active request
  on either path. The unknown-tool repair PlayMode test now uses a multi-turn timeout budget and
  cancels the active streaming request, matching its scripted failure plus real-model correction
  flow. The explicit merchant
  negotiation scenario cancels the active turn on timeout and bounds each live step response to 2048
  tokens so a failed or overly long negotiation cannot poison later targeted runs.
- **Crafting PlayMode scope and extraction cleanup.** `ThreeCrafts_AllUnique` now verifies exactly
  three unique Lua-backed crafts; the previous fourth repeat/determinism turn is tracked separately
  so it cannot hide a long LLM call inside a uniqueness test. The crafting name extractor now
  prefers actual crafted item names over ingredient `name` fields and has regression coverage for
  the material-name false positive.
- **Test integrity rule.** The Unity architecture docs and PlayMode test README now define the
  project-wide testing standard: tests must validate behaviour without answer-shaped prompt hints;
  exact payloads are reserved for parser, serializer, migration, repair, or deterministic fixtures.
- **FullAccess demo smoke coverage.** Added a PlayMode scene-smoke for
  `FullAccessDemo.unity` that loads the demo, verifies Full Lua is enabled with private reflection
  disabled, confirms the `TargetCube` bootstrap, and checks prompt buttons reserve enough room for
  the chat panel. Prompt buttons now reserve a wider chat area by default so manual Full Lua demo
  checks do not overlap the chat input.
- **Batch test runner for targeted verification.** Added `CoreAiBatchTestRunner` so CI/agents can run
  exact EditMode or PlayMode test names through `-executeMethod` and still get NUnit XML when Unity's
  built-in `-runTests` path is unavailable in a local editor session.
- **LiveMechanicsModsChatDemo scene validation.** Added EditMode coverage that opens the demo scene
  and verifies the auto-repair bridge, persistence panel, prompt buttons and user-facing prompt text.
- **Full Lua mods-demo prompt and autoload hardening.** The resource-backed Programmer prompt now
  includes the Full Lua diagnostic workflow, warns against invented APIs such as `print()` /
  `GameObject.Find`, and both mods demos opt in to Full Lua scene APIs. The mods-chat persistence
  controller now ignores transient validation ids such as `auto_repair_smoke` so smoke-test mods do
  not autoload in playable demo sessions.
- **Lua mod report logging control.** Persistent mod `report()` output is now muted by default and
  can be toggled per active mod in the F9 mod manager. The mods demo also registers a visible
  `enemy.basic` prefab for `coreai_world_spawn`, and Programmer guidance now steers visible scene
  edits through real `coreai_world_*` commands instead of invented `game.*` APIs.
- **Non-Full Lua transform commands.** `WorldEdit` Lua now includes `coreai_world_rotate` and
  `coreai_world_set_transform`, so spawn/delete/hierarchy/transform control does not require Full
  mode.
- **Active Lua mod auto-repair.** `LuaModRuntime` now surfaces runtime hook/timer failures through
  `ModHandlerErrored`, and `CoreAiLuaModAutoRepair` bridges those failures into the existing
  Programmer Lua repair flow with the broken source, runtime error, and saved version key. The
  `LiveMechanicsModsChatDemo` F9 panel shows the current auto-repair status.
- **Demo panels reworked into two independent draggable windows.** The mod manager (`LiveMechanicsModsChatPersistenceController`) is now a draggable `GUILayout.Window` toggled with **F9**, showing an `active N / inactive N` summary in its title and per-mod `[ACTIVE]` / `[ inactive ]` badges so it is obvious which mods are loaded. **F10** is reserved for the draggable Token Budget / usage overlay (`CoreAiTokenBudgetOverlay`) — model, token counts and estimated session cost — which was restored to F10 after the mod manager had taken it over.
- **Prompt buttons moved out of the way.** `ChatPromptButtonsController` is now bottom-anchored next to the chat panel (with a configurable `chatReserveWidth`) so it no longer overlaps the usage overlay or the mod manager.
- **TMP-safe demo strings.** Decorative glyphs that the default TMP/WebGL font (LiberationSans SDF) renders as missing boxes were replaced with ASCII in demo UI strings; prompt-context ellipses used in LLM budget math were left untouched.
- **English-only demo docs.** Remaining Russian text in demo READMEs and `Assets/CoreAI/Docs` / `Assets/CoreAiUnity/Docs` was translated to English; the `_RU` doc mirrors were removed.
- Fixed a `CS0308` compile error in `ModdableUnitsDemoController` by importing `VContainer` for the generic `Container.Resolve<LuaModRuntime>()` call.

## [4.2.0] - 2026-06-13

Depends on **`com.neoxider.coreai` 4.2.0**.

- Added `Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity`: a mod-driven game where chat-authored Lua mods create entirely new content. `UnitForgeLuaBindings` exposes `forge_define`/`forge_spawn`/`forge_count`/`forge_clear`/`forge_reset` (WorldEdit tier, via `GameLuaBindingsExtensibility`); the host runs a small auto-battle and emits `unit_spawned`/`unit_died`/`team_wiped` events back to mods. The bindings use only plain CLR types so the demo assembly never hard-references the optional MoonSharp package.
- Added `Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity`: the previously controller-only Full demo now ships a runnable scene with Full Lua access enabled and prompt buttons that move/grow/inspect an auto-created `TargetCube`.
- **Full Lua private access opt-in.** `CoreAILifetimeScope` gains `enableFullLuaPrivateAccess` (default off), wired through `WorldCommandsInstaller.RegisterWorldCommands` to `CoreAiFullUnityLuaRuntimeBindings`. Full reflection is public-members-only unless this is enabled.
- **Full Lua scene tools.** Full-tier Lua now has GameObject discovery and hierarchy helpers: `unity_list_objects`, `unity_find_all`, `unity_find_by_tag`, `unity_find_by_component`, `unity_describe_object`, `unity_get_transform`, `unity_set_rotation_euler`, `unity_set_scale`, `unity_parent`, and `unity_get_children`. Programmer keeps direct `execute_lua` / `manage_mods` tools, with a Full Lua Mode instruction instead of a runtime `SkillSet` proxy. When Full is enabled on the host, `manage_mods` now grants loaded mods the same Full tier so persistent mods can use `unity_*` APIs after a diagnostic one-shot script.
- Added EditMode coverage for the public-only default vs non-public opt-in, plus a Full-tier PlayMode test (`unity_find` + `unity_set_position` on a live scene object).
- **New editor tool `CoreAI → Setup → Modules` (`CoreAIModuleManager`):** enable/disable/update the optional MoonSharp (Lua) and LLMUnity packages, soft-disable Lua via `COREAI_NO_LUA`, and report effective module status — installs missing packages and bumps installed ones to the latest branch tip via UPM.
- Added `Assets/CoreAiUnity/Docs/OPTIONAL_MODULES.md` documenting the optional-module defines, the editor tool, and CI parity with the `no-lua` matrix; linked from `DOCS_INDEX.md`.
- **Docs are now English-only.** Removed the Russian `README_RU.md` mirror and the Russian language switcher from the READMEs; documentation is maintained in English.

## [4.1.0] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.1.0**.

- Added `WaveAutoBattlerModsDemo.unity`: a hero-vs-waves auto-battler where waves scale upward, the hero levels up, and Lua mods can change real combat slots (`hero_damage`, `hero_regen`, `enemy_count`, `enemy_hp`, `enemy_damage`, `wave_reward`) and react to battle events.
- Upgraded `LiveMechanicsModsChatPersistenceController` into an F10-style mod manager: active mods, saved/unloaded mods, metadata display from Lua comments (`-- name:` / `-- description:`), deactivate with `X`, activate saved mods, and forget saved sources.
- Added `ChatPromptButtonsController` and prompt buttons to both LiveMechanics mods and Wave Auto-Battler scenes so users can insert ready prompts for creating and modifying mods.
- `manage_mods unload` in demo scenes now behaves like deactivation: the source remains in the saved/unloaded list and can be activated again from the panel.

## [4.0.8] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.8**.

- Added `Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`, a copy of LiveMechanics focused on chat-driven `manage_mods` workflows.
- Added `LiveMechanicsModsChatPersistenceController`, a scene-level host policy that persists successful Lua mod `load`/`reload` sources and removes them from autoload on `unload`.
- The new mods-chat scene autoloads saved mod sources on the next scene start after the base LiveMechanics slots are declared, so mods can safely call `logic_define`.
- Demo docs now distinguish the generic LiveMechanics rule-edit scene from the mods-chat copy where loaded mod sources are saved and restored.

## [4.0.7] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.7**.

- LiveMechanics now persists successful `execute_lua` rule-slot edits that touch its known slots (`damage_formula`, `attack_interval`, `loot_formula`, `boss_reward`) through `ILuaScriptVersionStore` and reapplies the saved Lua on scene start.
- `GameLuaToolExecutor` now raises a successful-code notification so scene demos can persist scene-specific Lua policy without making the generic executor own scene state.
- Docs clarify the current Lua mod persistence boundary: `store_set` / `store_get` data is file-backed, while loaded mod source/autoload is still explicit host policy rather than automatic `LuaModRuntime` behavior.

## [4.0.6] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.4**.

- LiveMechanics chat guidance now steers local Programmer models toward `logic_define('loot_formula', function(...) return 1000 end)` for boss reward edits instead of hallucinated `create_item()` calls.
- LiveMechanics now also declares a `boss_reward` Lua logic-slot alias and uses it as a fallback for boss loot, matching the natural slot name small models often infer from player wording like "boss reward".
- `manage_mods` tool metadata now documents valid MoonSharp/Lua callback syntax so persistent mod retries do not repeat malformed `hooks_on('event') function() ... end` code.
- Added EditMode metadata coverage to keep generic `execute_lua` docs aligned with the scene-independent Lua rule-slot API.

## [4.0.5] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.3**.

- Demo/chat tool recovery now handles malformed `manage_mods` calls before MEAI invocation. Missing required arguments such as `action` are returned to the model as schema-aware tool failures, enabling the configured Programmer retry loop to repair the call instead of repeatedly surfacing `The arguments dictionary is missing a value for the required parameter 'action'`.
- Added EditMode coverage for required tool-argument validation and schema repair feedback.

## [4.0.4] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.2**.

- Fixed a full PlayMode-suite-only `UnityMainThreadLlmAsyncMarshaler` regression where the Editor play-state mirror could keep a stale thread id and run Unity tool bodies inline on a ThreadPool thread. The mirror now records UniTask's player-loop main thread id, and the PlayMode regression primes the mirror before switching to the ThreadPool.
- Hardened `MultiAgentCraftingWorkflowPlayModeTests`: if a local model completes the Programmer memory step but skips `execute_lua`, the scenario performs an exact `execute_lua` retry with `ForcedToolMode.RequireSpecific` before failing.

## [4.0.3] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.2**.

- Streaming tool-call recovery now feeds an explicit retry instruction back to the model when a tool call fails and the next model turn is empty/whitespace. This keeps `Programmer` in the correction loop for failed Lua/mod tool calls instead of ending the chat with only a fallback diagnostic.
- Added PlayMode coverage for the failed-tool -> empty-model-turn -> corrected-tool retry flow.

## [4.0.2] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.2**.

- Chat/PlayMode regression coverage added for `Programmer` tool-only failure turns. When `manage_mods` or another tool fails and the model returns only whitespace after the tool call, the chat now shows the real tool failure instead of the misleading structured-validation message `Response is empty or whitespace`.

## [4.0.1] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.1**.

- Chat panels using tool-oriented roles such as `Programmer` now keep short-term session context through the orchestrator when sending `SourceTag = "Chat"`. This fixes follow-up instructions like "answer in Russian" being dropped on the next turn, without enabling persisted chat history or LLM compaction for normal Programmer/Lua tasks.

## [4.0.0] - 2026-06-12

Depends on **`com.neoxider.coreai` 4.0.0**. Major Unity-layer release aligned with portable core Lua v4.

### Added

- **`CoreAiFullUnityLuaRuntimeBindings`** — Full-tier `unity_*` reflection APIs; wired through capability gating and `enableFullLuaAccess` on `CoreAILifetimeScope`.
- **`GameLuaToolExecutor`** — production `execute_lua` backend; Programmer role gets `execute_lua` + `manage_mods` via `WorldCommandsInstaller`.
- **`ICoreAiCustomWorldCommandHandler`** — extend `CoreAiWorldCommandExecutor` from game code.
- Demo scenes under **`Assets/CoreAI.Demos/`** (LuaMods, WorldCommands, Skills, LiveMechanics). FullAccess: bindings + README at this release; **`.unity` scene + PlayMode smoke completed later** (done).
- **`luaAllowedScenes`** whitelist on `CoreAILifetimeScope`.

### Changed / fixed

- **`set_color`** — `MaterialPropertyBlock` instead of `renderer.material` (perf leak fix).
- **`AggregatingGameLuaRuntimeBindings`** — capability-scoped registration incl. Full tier.
- Runtime logging: **`IGameLogger`** replaces direct `Debug.*` in chat/Lua/API paths.
- **`LuaModRuntime.Tick`** perf: reusable scratch list.
- Stream-gap diagnostics now log at warning level so the default `IGameLogger` filtering does not hide long streaming stalls.
- Lua callback exception expectations were aligned with MoonSharp script-error semantics after `LuaApiRegistry` normalized host validation failures.

See portable core **[CHANGELOG](../CoreAI/CHANGELOG.md)** and **`Docs/PERF_REVIEW_2026-06-12_RU.md`**.

## [3.2.0] - 2026-06-11

### Token budget on your own Canvas (UGUI)

- **`CoreAiTokenBudgetUiView`** — new component for game-styled UIs: instead of drawing anything, it periodically pushes formatted text through `UnityEvent<string>` outputs (`OnTokensTextChanged` / `OnCostTextChanged` / `OnLoadTextChanged`) that you bind in the inspector to your own `TMP_Text.text` / `Text.text` on any Canvas. State outputs `OnNearLimitChanged` (rate-limiter saturation, for alert colors) and `OnServiceAvailableChanged` (CoreAI scope found) fire on change. No hotkey, no IMGUI — show/hide with your own UI logic. `Source` / `Calculator` are exposed for fully code-driven UIs.
- **`TokenBudgetRuntimeSource`** — shared runtime data source (scope discovery, `LlmUsageReported` subscription, `TokenBudgetCalculator`) now backs both the IMGUI overlay and the UGUI view; text rendering moved to the core `TokenBudgetTextFormatter`.
- **`CoreAiTokenBudgetOverlay`** — hotkey can now be disabled by setting the toggle key to `None`; `ShowOverlay` / `ToggleKey` exposed as public properties for code control. Rendering unchanged.

### API design & CI

- Core `3.2.0` ships the typed `RoleId` struct, `AskWithCallback` (callback `Ask` is now an `[Obsolete]` alias of the awaitable-first API), and the Lua generation rate limiter — see the core changelog. Docs and samples updated to `AskAsync` / `AskWithCallback`.
- **CI matrix (GitHub Actions).** `.github/workflows/ci.yml` runs EditMode tests both with MoonSharp and in a `no-lua` job (package removed from `manifest.json`/`packages-lock.json`, `COREAI_NO_LUA` appended to all platform defines). The MoonSharp job fails if the `SecureLuaSandboxEditModeTests` escape suite did not execute, locking sandbox-isolation coverage. Lua-dependent test files that were missing `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` guards are now wrapped, so the project compiles without MoonSharp.
- New EditMode coverage: `RoleId` (conversions, equality, flow into string APIs), `AskWithCallback`/obsolete alias, `LuaGenerationRateLimiter` (window math) and envelope-processor rate-limit behavior.

#### Package **`3.2.0`** - dependency **`com.neoxider.coreai` `3.2.0`**.

## [3.1.0] - 2026-06-10

### Reliability hardening + diagnostics overlay

- Bumped `com.neoxider.coreaiunity` to `3.1.0` and aligned the dependency on `com.neoxider.coreai` `3.1.0`.
- **`LuaCoroutineRunner` — active-coroutine cap.** New `MaxActiveCoroutines` (default 64); `Register()` prunes dead handles and hard-stops with `InvalidOperationException` + error log when the cap is reached, so LLM-generated scripts can no longer spam unbounded coroutines. Completed/killed coroutines free their slot immediately (previously cleanup only happened in `Update()`).
- **`FileAgentMemoryStore` — off-main-thread async I/O.** New `TryLoadAsync` / `SaveAsync` / `ClearAsync` / `ClearChatHistoryAsync` / `AppendChatMessageAsync` / `AppendTranscriptEntryAsync` run file I/O on the thread pool, serialized with the sync paths via a per-store `SemaphoreSlim` (no frame freeze on large agent memory). Atomic tmp-file writes unchanged; WebGL runs inline (no threads). New optional `rootDirectory` ctor parameter for testability.
- **Token-budget overlay.** New `CoreAiTokenBudgetOverlay` (IMGUI, F10 toggle by default) showing current token usage, tokens/request, optional $/session from configurable per-1K prices, and a rolling-window request-load indicator on top of `IInGameLlmChatService.GetRateLimiterMetrics()`. Works in Editor and Play Mode; degrades gracefully when no service is available.
- Core `3.1.0` ships retry-backoff full jitter, the `ToolNameRepairCount` metric, retry error-feedback history reclamation, async I/O for `FileConversationSummaryStore`, and two closed Lua sandbox escape vectors (`string.dump`, `collectgarbage`) — see the core changelog.
- New EditMode coverage: backoff jitter bounds, repair-counter increments, error-feedback removal with valid pairing, coroutine-limit behavior, sandbox escape vectors, concurrent async store writes, token-budget calculator.
- Repository: versioned `hooks/pre-commit` guard against junk files in the repo root (logs, `*.db`, orphan root `.meta`); enable with `git config core.hooksPath hooks` (see `CONTRIBUTING.md`).

#### Package **`3.1.0`** - dependency **`com.neoxider.coreai` `3.1.0`**.

## [3.0.0] - 2026-06-10

### Major — optional Lua module + core reliability hardening

- Bumped `com.neoxider.coreaiunity` to `3.0.0` and aligned the dependency on `com.neoxider.coreai` `3.0.0`.
- **Lua/MoonSharp is now an optional module via the `COREAI_NO_LUA` scripting define** (mirrors the existing `COREAI_NO_LLM` opt-out). With the define set, `LuaCoroutineRunner` and all Lua registrations in `CorePortableInstaller` / `WorldCommandsInstaller` are compiled out, `AiGameCommandRouter` drops its `LuaAiEnvelopeProcessor` dependency, and the DI graph falls back to `CoreDefaultLuaRuntimeBindings` / `NullLuaExecutionObserver`. Both build configurations compile with zero errors.
- Core audit follow-up shipping in `com.neoxider.coreai` `3.0.0`: shared `HttpClient` over an `HttpClientHandler` (socket-exhaustion fix, valid on .NET Standard 2.0), crash-safe atomic JSON writes for `FileAgentMemoryStore` / `FileConversationSummaryStore`, and real `LuaCoroutineHandle.Kill()` termination.
- Fixed a portable-Core regression where a `[RuntimeInitializeOnLoadMethod]` (`UnityEngine`) attribute had been added to the UnityEngine-free `CoreAI.Core`; the Play Mode static reset of `CoreAIAgent` now runs from the Unity layer via `CoreAi.Invalidate()`.
- Fixed `AgentConfigExtensions.AskAsync` so role-registration validation runs before the orchestrator-null check (unregistered roles now report `role not registered` consistently).

#### Package **`3.0.0`** - dependency **`com.neoxider.coreai` `3.0.0`**.

## [2.6.5] - 2026-06-10

### Policy bootstrap safety

- Added `CoreAi.SetResolver(Func<IAiOrchestrationService>)` for deterministic resolver injection in tests/CI.
- Added `CoreAIFacade` and `CoreAi` runtime reset hooks on `SubsystemRegistration` to clear static state across Play Mode/domain transitions.
- `AgentBuilder.Build()` now applies role config to `CoreAIAgent.Policy` by default; `BuildDetached()` introduced for detached config creation.
- Added explicit role-registration fail-fast check in `AgentConfigExtensions.AskAsync(...)` so unregistered roles fail with `role not registered` instead of silently using fallback behavior.

## [2.6.4] - 2026-06-06

### Backend-managed streaming and chat reliability

- Bumped `com.neoxider.coreaiunity` to `2.6.4` and kept the dependency aligned with `com.neoxider.coreai` `2.6.4`.
- Fixed `ServerManagedLlmClient` so `ServerManagedApi` HTTP and streaming calls forward the dynamic `ServerManagedAuthorization` bearer header to backend proxy requests, including routed `LlmRoutingManifest` profiles.
- Fixed streaming tool-loop completion so successful repeated tool calls that already emitted assistant text finish cleanly instead of showing `tool loop exceeded max iterations` in chat.
- Fixed `CoreAiChatPanel.SetCollapsed` idempotency so setting the current collapse state does not notify override hooks again.
- Documented that `ServerManagedApi` reads `ServerManagedAuthorization` for every HTTP and streaming request.
- Added EditMode coverage for server-managed authorization forwarding and successful visible-text completion after repeated tool calls.
- Added EditMode coverage for `CoreAiChatMessageBubbleElement` default, user, toggle, and null-text states.

#### Package **`2.6.4`** - dependency **`com.neoxider.coreai` `2.6.4`**.

## [2.6.3] - 2026-06-01

### Chat UI authoring and host control toggles

- Bumped `com.neoxider.coreaiunity` to `2.6.3` and kept the dependency aligned with `com.neoxider.coreai` `2.6.3`.
- Added `CoreAiChatConfig.EnableStopGeneration` / `CoreAiChatOptions.EnableStopGeneration`. When disabled, active AI turns cannot be stopped from the chat UI: the send button stays as `>` and is disabled until the response completes, and Esc no longer cancels the request.
- Added `CoreAiChatConfig.ShowClearButton` / `CoreAiChatOptions.ShowClearButton`. When disabled, the header clear button is physically hidden; `CoreAiChatPanel.ClearChat(...)` remains available for code-driven resets.
- Added an authorable `CoreAiChatMessageBubble.uxml` and `CoreAiChatMessageBubbleElement` with `is-user`, `message-text`, and `avatar-sprite` attributes for UI Builder workflows. `CoreAiChatPanel.messageBubbleTemplate` is optional, so existing scenes keep working without assigning a template.
- Reworked the default chat scrollbar USS to use the current official UI Toolkit selector chain (`.unity-scroll-view__vertical-scroller`, `.unity-scroller__slider`, `.unity-base-slider__tracker`, `.unity-base-slider__dragger`) and removed old shotgun selectors.
- Fixed startup hydration scroll positioning so restored persisted chat sessions land on the newest messages after UI Toolkit layout settles.
- Added EditMode coverage for chat config defaults, options round-tripping, disabled stop presentation, and disabled send button state while a request is running.

#### Package **`2.6.3`** - dependency **`com.neoxider.coreai` `2.6.3`**.
## [2.6.2] - 2026-06-01

### WebGL chat streaming, Stop recovery, and settings diagnostics

- Bumped `com.neoxider.coreaiunity` to `2.6.2` and kept the dependency aligned with `com.neoxider.coreai` `2.6.2`.
- Added a real-backend WebGL Player verification path for `CoreAiChatDemo`: first message must show live streaming text, the second long streaming message must be cancellable with `Stop`, and a third message must still submit and receive a non-empty model response after cancellation.
- Added deterministic PlayMode coverage for `CoreAiChatPanel.StopAgent()` so cancellation unlocks the panel, stops active streaming, and allows the next streaming request to run.
- Hardened HTTP streaming fallback behavior so a primary backend that completes a streaming response without visible chunks can fall through to the configured secondary backend instead of producing an empty assistant turn.
- Updated `CoreAISettingsAsset` connection testing: the test prompt now asks for an exact `OK` response with a larger token budget, and the inspector displays the result directly below the **Test Connection** button.
- Documented the WebGL Stop verification flow and clarified that very short real-model replies can complete before tests observe a transient streaming label; recovery is verified by the final non-empty assistant response.

#### Package **`2.6.2`** - dependency **`com.neoxider.coreai` `2.6.2`**.

## [2.6.0] - 2026-05-29

### WebGL SSE stop/done handling and Lua WebGL guard

- Hardened WebGL native SSE streaming so `data: [DONE]` ends the stream promptly and the chat UI exits busy/stop state after a completed response.
- Hardened WebGL stop-button cancellation. Browser `fetch` abort is deferred and guarded, C# abort calls are wrapped, and stale/disposed cancellation sources no longer escape into the browser main loop.
- Tightened `CoreAiChatPanel` stop/send state handling so the stop control is enabled only while a live generation token exists and the UI unlocks after cancellation, completion, or stale busy-state cleanup.
- Added WebGL Player coverage for JS-to-C# bridge round-trip, guarded abort with no active controller, and explicit Lua unsupported behavior on WebGL.
- Disabled direct Lua PlayMode integration tests on WebGL while MoonSharp is unsupported there; Editor and non-WebGL player Lua paths remain covered.
- Documented the temporary WebGL Lua limitation and the future restoration options: AOT-safe Lua runtime, trusted server-side execution, or a restricted command interpreter with WebGL Player tests.
- Restored the full `CoreAISettingsAsset` custom inspector that exposes Essentials, Advanced foldouts, WebGL player settings, model helpers, and connection testing instead of falling back to the generic serialized field view.
- Switched the restored settings inspector UI text to ASCII-only strings and added an explicit `Fallback Backend (secondary)` foldout for the secondary URL, API key, model, and enable toggle.
- Added regression coverage for `CoreAISettingsAsset` URL/model normalization paths introduced by the restored settings inspector: HTTP base URL trim/default behavior and `ModelName` fallback in Auto/Local execution modes.
- Removed stale Unity project warning debt from this repo by deleting an empty test asmdef and a missing PathTracing resource reference in URP global settings.

#### Package **`2.6.0`** - dependency **`com.neoxider.coreai` `2.6.0`**.

## [2.5.4] - 2026-05-29

### WebGL native SSE callback and Play Mode hardening

- Hardened `CoreAiSseFetch.jslib` so browser-side `open`, `chunk`, `done`, and `error` callbacks into C# are routed through guarded wrapper functions. A callback failure is now logged as a bridge warning instead of escaping as a browser `Uncaught undefined` / main-loop exception.
- Cancelled browser `fetch` / `ReadableStream` paths no longer call the C# error callback for the expected `cancelled` reason, reducing noisy stop-button failures when a WebGL user interrupts generation before the model finishes.
- Hardened `UnityMainThreadLlmAsyncMarshaler` Editor Play Mode detection for WebGL build-target test runs. Worker-thread tool invocations now refuse the Edit Mode inline path once runtime Play Mode has entered, even if the volatile editor mirror is still stale.
- Restored the serialized `WorldRenderPipelineResources` entry in `UniversalRenderPipelineGlobalSettings.asset`, avoiding render-pipeline resource drift after WebGL settings changes.
- Added `CoreAiSseFetchJslibEditModeTests` coverage to pin the safe-wrapper contract and guarded abort path.
- Updated `CoreAISettingsAsset` resource/preset tests for committed blank API keys, keeping provider presets safe to publish without embedded secrets.
- Updated WebGL streaming docs with the native bridge callback-safety note.

#### Package **`2.5.4`** - dependency **`com.neoxider.coreai` `2.5.4`**.

## [2.5.3] - 2026-05-27

### WebGL chat stop cancellation

- Hardened `CoreAiChatPanel` user-stop handling: `SendToAIFromUiAsync` now observes user-triggered `OperationCanceledException`, and `StopActiveGeneration` / `StopAgent` guard cancellation callback failures.
- This keeps the stop button path aligned with the existing `RunAgentTurnAsync` cancellation handling and avoids browser-side `Uncaught undefined` crashes when the user stops a generation around streaming/completion boundaries.
- Cleared `coreai-long-request-hint` as soon as streaming starts and prevented it from reappearing while a streaming bubble is active, so the long-wait timer is only shown before the assistant begins responding.
- Hardened `UnityMainThreadLlmAsyncMarshaler` Play Mode detection in the Unity Test Runner by priming the Editor play-state mirror from `EditorApplication.update`, not only `Application.onBeforeRender`.
- LLM memory verification tests now report external HTTP backend no-response cases as inconclusive instead of failing later memory assertions after the orchestrator returns an empty terminal string.
- Aligned LM Studio local-server documentation, presets, resource assets, and `CoreAISettingsAsset` resource-loading tests with the active loopback endpoint `http://127.0.0.1:1234/v1` and `qwen3.5-4b`.

#### Package **`2.5.3`** - dependency **`com.neoxider.coreai` `2.5.3`**.

## [2.5.1] - 2026-05-25

### VContainer prefab registry registration

- Fixed `WorldCommandsInstaller.RegisterWorldCommands` double-registering the same `CoreAiPrefabRegistryAsset` instance. Newer VContainer versions reject overlapping concrete/interface registrations with `Conflict implementation type`; the installer now keeps the combined `ICoreAiPrefabRegistry` / `CoreAiPrefabRegistryAsset` registration only.
- Added `WorldCommandsInstallerEditModeTests` coverage for building a VContainer with world-command services and resolving both prefab registry contracts.

### PlayMode stability

- Fixed `UnityMainThreadLlmAsyncMarshaler` editor Play Mode detection so worker-thread invocations no longer inline Unity-bound delegates when `Application.onBeforeRender` has not primed the mirror yet.
- Updated real-backend memory verification tests to report external LLM infrastructure failures (`timeout`, cancelled request, unloaded HTTP model) as inconclusive instead of failing memory assertions.
- Updated the crafting memory LLM verification scenario to stop synthesizing `unknown_N` craft names after a missing tool result; the test now reports the external-model/tool-call precondition as inconclusive instead of failing a later determinism assertion against fake data.
### Tool-call observability API

- Added public `CoreAi` tool lifecycle observability: `OnToolCallStarted`, `OnToolCallCompleted`, `OnToolCallFailed`, `SubscribeToolCalls(...)`, `GetToolCallHistorySnapshot()`, and `ClearToolCallHistory()`.
- `MessagePipeToolCallEventPublisher` now mirrors every tool lifecycle event into the public `CoreAi` facade, so gameplay code, analytics, QA tools, and PlayMode tests can subscribe without depending on `GlobalMessagePipe`.
- Tightened crafting and multi-agent PlayMode scenarios to assert real completed `execute_lua` tool calls instead of accepting assistant prose or synthetic fallback names as successful execution.
- Tightened AINpc and self-service skill PlayMode scenarios to assert real completed `memory`, `read_skill`, and `call_skill_tool` lifecycle events plus domain side effects, instead of accepting textual tool-call attempts as success.
- Replaced the Unity MEAI tool-binding reflection fallback (`CreateAIFunction` duck typing) with explicit `IAIFunctionLlmTool` / `IAIFunctionsLlmTool` contracts for built-in tools.

### Settings preset coverage

- Added focused EditMode regression tests for `CoreAISettingsAsset` resource loading and provider presets in `Assets/CoreAiUnity/Tests/EditMode/CoreAISettingsAssetEditModeTests.cs`.
- Added preset smoke coverage for `Assets/Resources/{open,minmaxFree,grok}.preset` application (`Preset.ApplyTo`) and kept `COREAI_SETTINGS.md` updated with provider preset guidance.

#### Package **`2.5.1`** - dependency **`com.neoxider.coreai` `2.5.1`**.

## [2.5.0] - 2026-05-24

### Options + ScriptableObject Wrapper Rule

All framework ScriptableObjects now follow the accepted rule: assets are Unity authoring wrappers, while runtime code should consume options, interfaces or snapshots.

- `CoreAiChatConfig` implements `ICoreAiChatOptions` and exposes `ToOptions()` / `ApplyOptions(...)`.
- `CoreAiChatPanel` accepts `SetRuntimeOptions(...)`, so tests and hosts can configure chat behavior without mutating private serialized fields.
- `CoreAISettingsAsset`, `OpenAiHttpLlmSettings`, `GameLogSettingsAsset`, and `AiPermissionsAsset` expose `ToOptions()` / `ApplyOptions(...)` wrappers.
- `AgentPromptsManifest` builds `AgentPromptsDefinition` from `TextAsset.text`; prompt providers consume the definition snapshot.
- `SkillSetAsset` builds `SkillSetDefinition`; `BuildSkillSet(...)` uses that portable definition plus code-supplied tools.
- `LlmRoutingManifest` exposes `ToOptions()` as an alias for the portable `LlmRouteTable` snapshot.
- `CoreAiPrefabRegistryAsset` implements `ICoreAiPrefabRegistry`; consumers depend on the registry interface, not the concrete asset type.

### Tests and Migration

- `CoreAiChatPanelBusyApiEditModeTests` no longer writes `_roleId` / `_showToolCallsInChat` via reflection; mutable chat setup uses `CoreAiChatOptions`.
- Added targeted wrapper/snapshot EditMode coverage for chat, settings, HTTP, logging, permissions, routing, prompts, skills and prefab registry contracts.
- Asset-specific tests remain for Inspector defaults and serialization behavior.

### Documentation

- Added `DOCS_INDEX_RU.md`, `RELEASE_CHECKLIST_RU.md`, and `KNOWN_ISSUES_RU.md`.
- Updated `SCRIPTABLE_OBJECTS.md` with the `Options + ScriptableObject wrapper` rule.
- Updated chat README with runtime options vs config asset guidance.

#### Package **`2.5.0`** - dependency **`com.neoxider.coreai` `2.5.0`**.
## [2.4.0] - 2026-05-08

### ChatPanel — public busy API + tool-round event + scroll/diagnostic fixes

Previously external code (RedoSchool's `ChatExternalSubmitUnlock` gate, similar host gates) had to read private `_isSending` / `_isStreaming` / `_isStopping` / `_isClearing` flags on `CoreAiChatPanel` via reflection. This release exposes a stable contract — and fixes a long-standing scroll glitch at the end of streamed turns.

#### New public API on `CoreAiChatPanel`

- **`bool IsBusy`** — `_isSending || _isStreaming || _isStopping || _isClearing`.
- **`event Action<bool> BusyStateChanged`** — fires on the UI thread on every transition. Funnelled through `UpdateSendButtonVisualState()`, which every state-flag mutation already calls, so the contract holds even when subclasses change flags directly.
- **`int CurrentTurnGeneration`** — monotonic counter, incremented at the start of each `RunAgentTurnAsync`. External code can compare values across awaits to detect "a newer turn is already in flight".
- **`void ResetBusyStateWithoutCancellation()`** — clears all four busy flags without cancelling the active HTTP/streaming request (in contrast to `StopActiveGeneration`). For hosts that know the turn is logically closed and just want to unlock input.
- **`event Action<int, string> ToolRoundStarted`** — fires before each LLM iteration inside a turn (after a tool result). Args: 1-based iteration index, last executed tool name (or `null`). Lets hosts show "tool X (k/N)" badges without reflection.

#### Scroll fix at end of streaming

`FinishStreaming()` removes the `coreai-streaming-active` USS class from the active bubble, which changes `padding` / `border` and therefore the content height of `ScrollView`. The previous two-pass `schedule.Execute` snap finished before that style invalidation settled, leaving the tail of the last AI message clipped below the visible area. `ScrollToBottom()` now schedules a third snap with `StartingIn(80)`ms.

#### Stream-gap diagnostics

`SendStreamingAsync` measures inter-chunk latency and emits a single `Debug.Log` line whenever a gap exceeds 5s, with `BufferedNoToolBinding` / `ToolHint` / `TextLen` / `IsDone` flags. Helps tell "model is slow on a tool-roundtrip" from "UI lost a chunk".

#### Tool-name tracking always on

`OnToolExecutedChatDisplay` now subscribes to `CoreAi.OnToolExecuted` regardless of `ShowToolCallsInChat`, because `ToolRoundStarted` listeners want the tool name even when the tool-call bubble is suppressed. Bubble rendering itself is still gated by config.

#### Migration

- **No breaking changes.** Reflection-based busy gates continue to work; switch to `BusyStateChanged` / `IsBusy` to drop the `BindingFlags.NonPublic` reflection.
- New event handlers can be added safely: `ChatPanel.BusyStateChanged += isBusy => …;` / `ChatPanel.ToolRoundStarted += (iter, name) => …;`.

#### Tests

- New `EditMode/CoreAiChatPanelBusyApiEditModeTests`: `IsBusy` reflects each flag, `BusyStateChanged` fires on transitions only (not on every mutation), `ResetBusyStateWithoutCancellation` clears all flags, `ToolRoundStarted` delivers iteration index and tool name.

#### Package **`2.4.0`** — dependency **`com.neoxider.coreai` `2.3.1`** (no Core changes).

## [2.3.1] - 2026-05-08

### LLMUnity Text-Mode Tool Calling — Lockstep with CoreAI 2.3.1

Local GGUF models (Qwen3.5-4B via LLMUnity/llama.cpp) cannot emit native function calls — they output tool calls as plain text. This release adds three extraction layers so the full SkillSet pipeline (read_skill → call_skill_tool → execute) works end-to-end on local backends.

#### Text-Mode Extraction

- **Function-call syntax** — `read_skill("Alchemy")`, `read_skill(Crafting)`, `call_skill_tool("brew_potion", '{"secret_code":"ARCANUM-7"}')` are now parsed into `FunctionCallContent` and executed by the pipeline.
- **`arguments_json` key** — Qwen3.5 emits `{"name":"read_skill","arguments_json":"{...}"}` instead of `"arguments"`. Both keys are now accepted in `LlmToolCallTextExtractor`, `SmartToolCallingChatClient`, and `MeaiLlmClient` (streaming path).
- **JObject → string normalization** in `ToolExecutionPolicy.ExecuteSingleAsync` — the single chokepoint where all tool calls pass. MEAI's `AIFunctionFactory` cannot convert `Newtonsoft.Json.Linq.JObject` to `System.String`; nested JSON objects are now serialized to strings before `AIFunction.InvokeAsync`.

#### Fixes

- **`CallSkillToolLlmTool.InvokeDelegateWithJson`** — when a delegate expects `string` but receives `JObject`/`JArray`, serialize to JSON string instead of throwing `InvalidCastException`.
- **`IsValidToolCallJson` / `LooksLikeToolCallJson`** — heuristic checks now accept `"arguments_json"` alongside `"arguments"`.

#### Tests

- **8 new EditMode tests** in `ToolCallExtractionParityEditModeTests`: `arguments_json` key extraction, function-call syntax (quoted/unquoted/multi-arg), prose-with-parens safety, and end-to-end `SmartToolCallingChatClient` integration.
- **3 PlayMode LLM verification tests** now validate the self-service Skill flow through real tool lifecycle events and domain side effects: `SelfService_ModelMustReadSkill`, `SelfService_ModelCallsReadSkill`, `Model_ReadsSkill_ThenCallsSkillToolViaProxy`.

#### Package **`2.3.1`** — dependency **`com.neoxider.coreai` `2.3.1`**.

## [2.3.0] - 2026-05-08

### Dual-Backend — Lockstep with CoreAI 2.3.0

- **Dependency:** **`com.neoxider.coreai` `2.3.0`**.
- **Inspector:** New **🔄 Fallback Backend** section in `CoreAISettingsAsset` — `enableFallbackBackend`, `secondaryApiBaseUrl`, `secondaryApiKey`, `secondaryModelName`.
- **`FallbackLlmClientDecorator`** — auto-fallback primary → secondary on failure.
- **`LlmPipelineInstaller`** — DI wiring wraps primary client in fallback decorator when secondary is configured.
- **5 EditMode tests** for fallback decorator behavior.

#### Package **`2.3.0`** — dependency **`com.neoxider.coreai` `2.3.0`**.

## [2.2.0] - 2026-05-08

### Lockstep with CoreAI 2.2.0

- **Dependency:** **`com.neoxider.coreai` `2.2.0`**.
- **Inspector:** `MaxToolCallHistoryMessages` field added to **🛡️ Resilience & Safety** foldout (default 20, 0 = no limit).
- **`RateLimiterMetrics`** exposed via `IInGameLlmChatService.GetRateLimiterMetrics()`.

#### Package **`2.2.0`** — dependency **`com.neoxider.coreai` `2.2.0`**.

## [2.1.0] - 2026-05-08

### Production Resilience — Lockstep with CoreAI 2.1.0

- **Dependency:** **`com.neoxider.coreai` `2.1.0`** — four runtime guardrails: `MaxToolResultChars`, `DefaultToolTimeoutMs`, `MaxResponseChars`, `MaxToolCallRoundtrips`.
- **Inspector:** **`CoreAISettingsAsset`** — new **🛡️ Resilience & Safety** foldout with four fields, tooltips, and min-value constraints.
- **Tests:** **`ResilienceFeaturesEditModeTests`** — 8 tests covering truncation, timeout, and roundtrip limits without LLM backends.
- **PlayMode prompts:** Anti-thinking instructions added to `CraftingMemoryViaOpenAiPlayModeTests`, `MultiToolChainPlayModeTests`, `AgentMemoryOpenAiApiPlayModeTests` for Qwen3.5 compatibility.
- **Docs:** `README.md` resilience row; `AGENT_BUILDER.md` Resilience & Safety section.

#### Package **`2.1.0`** — dependency **`com.neoxider.coreai` `2.1.0`**.

## [2.0.0] - 2026-05-08

### Major — Lockstep with CoreAI 2.0.0 (SkillSet)

- **Dependency:** **`com.neoxider.coreai` `2.0.0`** — **SkillSet** (named tool+instruction groups with per-request activation). No Unity-layer API changes; all public SkillSet types live in the portable **`CoreAI.Core`** assembly.
- **Tests:** **`SkillSetEditModeTests`** — tests covering construction, instruction injection, per-request filtering, `MergeToolNames`, and `AgentBuilder.WithSkill` integration.

#### Package **`2.0.0`** — dependency **`com.neoxider.coreai` `2.0.0`**.

## [1.7.5] - 2026-05-05

### Chat — optional in-chat tool-call rows

- **`CoreAiChatConfig.ShowToolCallsInChat`** (default **off**) — when enabled, **`CoreAiChatPanel`** subscribes to **`CoreAi.OnToolExecuted`**, marshals to the main thread, and appends a muted diagnostic row for **`roleId`** matching the panel **`RoleId`**. Not persisted to **`IAgentMemoryStore`**. Override **`FormatToolExecutedForChat`** or reuse **`CoreAiToolCallChatFormatter.BuildDisplayText`**.
- **`CoreAiChat.uss`** — **`.coreai-tool-call-row`** / **`.coreai-tool-call-message`** styles.

### Core AI Settings — temperature flag YAML

- **`CoreAISettingsAsset`** — serialized field **`overrideTemperature`** renamed to **`enableTemperatureOverriding`** with **`[FormerlySerializedAs("overrideTemperature")]`** (default still **off**). Inspector label **Enable temperature overriding**. Public API remains **`OverrideTemperature`** ( **`ICoreAISettings`** ).
- **Resources** sample assets and **`CoreAISettingsAssetEditor`** updated.

### Tests / docs

- **EditMode:** **`CoreAiToolCallChatFormatterEditModeTests`**; **`CoreAiChatConfigEditModeTests`** asserts **`ShowToolCallsInChat`** default **false**.
- **`README_CHAT.md`**, **`DEVELOPER_GUIDE.md`**, **`COREAI_SETTINGS.md`**.

#### Package **`1.7.5`** — dependency **`com.neoxider.coreai` `1.7.5`** (lockstep).

## [1.7.4] - 2026-05-05

### LLMUnity — runtime host, GGUF from Core AI Settings, autostart

- **`ConfigurableLlmAgentProvider`** — replaces scene-only **`SceneLlmAgentProvider`** on desktop: if no **`LLMAgent`** is found and **`LlmUnityAutoCreateRuntimeHost`** is on (default), creates **`CoreAI_LLMUnity_Runtime`** (`LLM` + **`LLMAgent`**, **`remote`** off) and applies **`CoreAISettingsAsset`** (GPU layers, **DontDestroyOnLoad**, GGUF).
- **`LlmUnityHostConfigurator`** + **`TryAssignModelFromGgufHint`** — assigns **`LLM.model`** from **`GgufModelPath`** (exact filename vs Model Manager, or full disk path) **before** Model Manager fallback (fixes wrong **0.8B** pick when **9B** is set only on the Core AI asset).
- **`LlmUnityAutostartEntryPoint`** — optional warm-up of the local server after DI (**`LlmUnityAutostartLocalServer`**, default on); timeout uses **`LlmUnityStartupTimeoutSeconds`**.
- **`LlmUnityAutoDisableIfNoModel`** — tries **`CoreAISettingsAsset.GgufModelPath`** before disabling empty **`LLM.model`**.
- **Tests:** **`LlmUnityGgufHintNormalizationEditModeTests`** (`NormalizeGgufHintToFileName`).
- **Docs:** **`LLMUNITY_SETUP_AND_MODELS.md`**.
- **Dependency:** **`com.neoxider.coreai` `1.7.4`** — lockstep semver.

#### Package **`1.7.4`**.

## [1.7.3] - 2026-05-05

### Streaming — hybrid JSON hold for bound tools + suffix after Path 2

- **`MeaiLlmClient.CompleteStreamingAsync`** — when **`Tools`** is non-empty and **`BufferFullStreamingIterationWhenToolsDeclared`** is not **`true`**, uses the same **hybrid JSON hold** previously applied mainly to unbound iterations: avoids leaking incomplete tool JSON into live UI tokens while still streaming safe prefixes. After native **`delta.tool_calls`** (Path 2) or text extraction, any assistant text not yet forwarded is reconciled via **`GetCleanedTextSuffixAfterHybridPrefix`** and emitted as trailing **`Text`** chunks.
- **`LlmCompletionRequest.BufferFullStreamingIterationWhenToolsDeclared`** — portable flag (**`com.neoxider.coreai` `1.7.3`**): full-iteration buffer vs hybrid (default).
- **Tests:** **`MeaiLlmClientEditModeTests.GetCleanedTextSuffixAfterHybridPrefix_*`**; **`MultiToolChainPlayModeTests`** — second **`AiTaskRequest`** if the memory marker is missing after the first hop (flaky LLMUnity timing).
- **Docs:** **`STREAMING_ARCHITECTURE.md`**, **`DEVELOPER_GUIDE.md`**, **`TESTING_TOOL_CALLING.md`**.
- **Dependency:** **`com.neoxider.coreai` `1.7.3`** — lockstep semver.

#### Package **`1.7.3`**.

## [1.7.2] - 2026-05-05

### WebGL — IDBFS `FS.syncfs` single-flight (`CoreAiPersistFs`)

- **`CoreAiPersistFs.jslib`** — **`CoreAi_PersistFsSync`** no longer calls **`FS.syncfs`** while a previous sync is still in flight. Overlapping calls (e.g. **`FileAgentMemoryStore`** persisting chat JSON and memory in quick succession) set a **queued** flag and run **at most one** follow-up sync after the current callback — avoids Emscripten’s *“2 FS.syncfs operations in flight”* warning and reduces risk of the main thread / IDBFS getting into a bad state after several turns.
- **Docs:** [`TROUBLESHOOTING.md`](Docs/TROUBLESHOOTING.md) (WebGL row in agent-memory table).
- **Dependency:** **`com.neoxider.coreai` `1.7.2`** — lockstep semver.

#### Package **`1.7.2`**.

## [1.7.1] - 2026-05-05

### Chat typing + EditMode coverage

- **`CoreAiChatPanel`** — when a buffered marker uses **`BufferedStreamingUseToolProgressHint`** (static **`StreamingToolProgressHint`**) and later a marker **without** that flag arrives **before** any visible assistant text, **`ShowTypingIndicator()`** runs unconditionally so the default animated dots return (hint path had stopped the animation).
- **Tests:** **`LoggingLlmClientDecoratorEditModeTests.FailedCompletion_BackendUnavailable_RetriesAndSucceeds`** — mirrors the v1.7.0 **`RateLimited`** result-retry test for **`LlmErrorCode.BackendUnavailable`**.
- **Docs:** settings guide (**override temperature**, **max LLM request retries**), index / quick start / developer guide / agent roles — UPM **`1.7.1`**.
- **Dependency:** **`com.neoxider.coreai` `1.7.1`** — lockstep semver.

#### Package **`1.7.1`**.

## [1.7.0] - 2026-05-05

### Portable streaming marker + chat typing hint (buffered tool iteration)

- **`LlmStreamChunk.BufferedStreamingNoToolBinding`** (**`com.neoxider.coreai`**) — marker chunk (no `Text`) so host UI can refresh the typing row during special streaming phases (unbound iteration, tool JSON hold, etc.).
- **`LlmStreamChunk.BufferedStreamingUseToolProgressHint`** — when set with the marker, chat shows the short static line from **`CoreAiChatConfig.StreamingToolProgressHint`** (native / text-shaped tool execution, hybrid hold). When the marker arrives **without** this flag (e.g. unbound iteration waiting for the model step), **`CoreAiChatPanel`** keeps the default animated typing dots.
- **`MeaiLlmClient`** — yields marker(s) for unbound streaming, hybrid JSON hold, native tool deltas, and text-shaped tool execute; logs optional hold start.
- **`CoreAiChatConfig`** — **`StreamingToolProgressHint`** (Inspector): short default **`Action...`**; empty falls back to **`CoreAiChatPanel`** default.
- **`CoreAiChatPanel`** — applies the short hint only when **`BufferedStreamingUseToolProgressHint`** is true.
- **Tests:** **`CoreAiChatConfigEditModeTests`**, **`StreamingAndPromptsEditModeTests`** (`BufferedStreamingNoToolBinding` / `BufferedStreamingUseToolProgressHint` default **false**).
- **Sampling temperature:** **`CoreAISettingsAsset`** — **`overrideTemperature`** (Inspector **Override temperature**, default **off**); when on, **`temperature`** is sent via orchestrator (**`LlmCompletionRequest.SendTemperature`**) to HTTP and LLMUnity. **`ConfigureHttpApi`** turns the override on. **`MeaiLlmClient`** only assigns MEAI **`ChatOptions.Temperature`** when **`SendTemperature`** is true.
- **HTTP retries:** **`LoggingLlmClientDecorator`** retries failed completions with **`RateLimited`** / **`BackendUnavailable`** (not only thrown **`LlmClientException`**), matching **`MeaiLlmClient`**’s result-based HTTP errors. Default **`maxLlmRequestRetries`** on **`CoreAISettingsAsset`** is **1** (inspector minimum **1**).
- **Dependency:** **`com.neoxider.coreai` `1.7.0`** — lockstep semver.

#### Package **`1.7.0`**.

## [1.6.19] - 2026-05-05

### WebGL — persisted chat / agent memory (`FileAgentMemoryStore`)

- **`CoreAILifetimeScope`** — WebGL **player** now registers **`FileAgentMemoryStore`** as **`IAgentMemoryStore`** and **`IConversationTranscriptStore`** (same as desktop). Previously the player used **`NullAgentMemoryStore`**, so **`TryGetPersistedChatHistory`** always saw an empty history and nothing was written for session restore.
- **`FileAgentMemoryStore`** — unchanged contract; on WebGL it already calls **`CoreAi_PersistFsSync`** after writes (**`CoreAiPersistFs.jslib`** → **`FS.syncfs`**) so IDBFS changes reach IndexedDB when the user reloads or closes the tab without **`Application.Quit`**.
- **`CoreAiPersistFs.jslib`** — success-path **`console.log`** removed; warnings remain when **`FS.syncfs`** is missing or fails.
- **`CoreAiSseFetch.jslib`** — verbose **`console.log`** (open / response / done) commented by default to reduce browser console noise; **`console.warn`** remains for read errors and **`fetch.catch`** (CORS / network / timeout).
- **`CoreAISettingsAsset`** — Editor **`Reset()`** sets **Global streaming** + **WebGL: native SSE (fetch)** to **on**; **`CoreAIBuildMenu.EnsureAsset`** applies the same via **`SerializedObject`** after **`CreateInstance`** (auto-generated **`CoreAISettings`** and any path that skipped **`Reset()`** / legacy YAML without the fields).
- **`CoreAILifetimeScope.RegisterAgentMemoryStore`** — **`internal`** helper used by **`Configure`** and **`CoreAILifetimeScopeConversationStoreEditModeTests`** (`RegisterAgentMemoryStore_Resolves_FileAgentMemoryStore_SharedSingleton`).
- **Chat UI (default `CoreAiChat.uxml` / `.uss`):** floating panel defaults **650×910** (~**+30%** vs legacy **500×700**); **`CoreAiChatConfig`** default width/height match. **Vertical scrollbar** — strip horizontal inset on **`unity-scroll-view__content-and-vertical-scroll-container`** and scroller/slider parts; **move message inset to `unity-scroll-view__content-container`** (not padding on the `ScrollView` root) and set **`content-viewport` `min-width: 0`** so the track stays **flush to the inner right** (fixes a wide empty strip beside the bar when the old `>` selector missed the real DOM). **`coreai-long-request-hint`** — optional `Label` under **`#coreai-typing-indicator`**; **`TickLongRequestHint`** uses **`LongRequestHintFormat`** with **`{elapsed}`**; arms after **~3 s while the LLM turn is in flight** (`_isSending`, same spirit as RedoSchool activity text); **Stop** clears the hint; empty format disables the line. **`LlmStreamChunk.BufferedStreamingNoToolBinding`** / **`BufferedStreamingUseToolProgressHint`** — **`MeaiLlmClient`** typing markers; **`CoreAiChatPanel`** shows short **`StreamingToolProgressHint`** only when **`BufferedStreamingUseToolProgressHint`** is set, else keeps animated typing dots for unbound step wait.
- **Docs:** **`ARCHITECTURE.md`**, **`DGF_SPEC.md`**, **`README_CHAT.md`**, root **`README.md`** / **`README_RU.md`**, **`DOCS_INDEX.md`**, **`QUICK_START.md`**, **`TODO.md`**, **`STREAMING_WEBGL_TODO.md`**, **`STREAMING_ARCHITECTURE.md`**, **`TROUBLESHOOTING.md`**, **`MemorySystem.md`**, **`HTTP_TRANSPORT_SPEC.md`**, **`DEVELOPER_GUIDE.md`** (session restore + WebGL + jslib logging; chat template sizing + scrollbar + long-hint).
- **Dependency:** **`com.neoxider.coreai` `1.6.19`** — lockstep semver.

#### Package **`1.6.19`**.

## [1.6.18] - 2026-05-04

### WebGL `fetch` SSE — single-threaded await + non-blocking stream (fix silent hang after `POST (stream)`)

- **`FetchSseOpenAiTransport.StreamState`** — **`TaskCompletionSource<OpenInfo>`** no longer uses **`RunContinuationsAsynchronously`**. WebGL builds have no real thread pool, so `RunContinuationsAsynchronously` queued the awaiting continuation onto a scheduler that never ran — the await of **`WaitForOpenAsync`** parked forever after `POST (stream)` and the chat UI animated indefinitely with no response. With **synchronous continuations**, the C# **`await`** resumes inside the JS **`onOpen`** callback's call stack, on the same Unity main thread, immediately after **`fetch().then`** delivers the response headers.
- **`FetchSseStream.ReadAsync`** — true async via **`TaskCompletionSource<int>`**. Previously **`Stream.Read`** blocked the calling thread on **`AutoResetEvent.WaitOne`**, which on WebGL froze the JS event loop and prevented further fetch chunks from ever being delivered (deadlock: read waits for chunks; chunks wait for the event loop; event loop waits for read to return). Now **`ReadAsync`** returns synchronously when bytes are queued and a parked **`Task<int>`** otherwise; **`EnqueueChunk` / `SignalDone` / `SignalError`** call **`PumpPendingRead`** to fulfil the parked task from the JS callback, so the consumer (**`StreamReader.ReadLineAsync`** in **`MeaiOpenAiChatClient`**) gets data without blocking the main thread. **`Read` (sync)** is now non-blocking too — returns 0 instead of waiting — so any caller that bypasses **`ReadAsync`** simply observes EOF instead of hanging.
- **`CoreAiSseFetch.jslib`** — added optional **`console.log`** lifecycle traces (**`[CoreAiSseFetch] open / response / done`**) for DevTools (**v1.6.18**); **`console.warn`** for read errors / **`fetch.catch`** kept. **v1.6.19:** those **`console.log`** lines are commented out by default (uncomment in the jslib to trace **`fetch`** / SSE); **`console.warn`** unchanged.
- **Dependency:** **`com.neoxider.coreai` `1.6.18`** — lockstep semver.

#### Package **`1.6.18`**.

## [1.6.17] - 2026-05-04

### WebGL `fetch` SSE — await real HTTP status before returning (fix `HTTP 0`)

- **`FetchSseOpenAiTransport`** — `OpenSseResponseStreamAsync` is now genuinely **`async`**: it waits for the JS bridge to deliver the real **`response.status`** + headers (or a CORS / network error) before constructing the **`OpenAiHttpSseOpenResult`**. Previously the method returned **`Task.FromResult`** synchronously with the default **`StatusCode = 0`**, so **`MeaiOpenAiChatClient`** logged **`stream HTTP 0 FAILED`** and aborted before **`fetch`** had even reached the gateway — masking real CORS errors as transport failures and making WebGL chat appear silently broken even when the server would have answered.
- **`CoreAiSseFetch.jslib`** — adds an **`onOpen(callId, status, errorBody, headersFlat)`** callback fired right after **`fetch().then(response =>)`**. On **`response.ok`** the JS side starts pumping chunks via **`onChunk`** as before; on a non-2xx the body is read once and forwarded to **`onOpen`** so C# can populate **`OpenAiHttpSseOpenResult.ErrorBodyText`** and surface a proper **`LlmClientException`** with the gateway's actual status. **`fetch.catch`** (CORS / DNS / network) signals **`onOpen(callId, 0, message, "")`** + **`onError`** so the consumer sees a real diagnostic instead of a 120 ms timeout.
- **`CoreAi_FetchSseAbort`** — now keyed by **`callId`** (was a controller pointer); the JS side keeps a **`controllers[callId]`** map and aborts on demand. Aligned with the **`CancellationToken`** registration in C#.
- **`FetchSseStream`** — wraps each JS-extracted delta back into a single **`data: {"choices":[{"delta":{"content":"…"}}]}\n\n`** SSE event so the existing **`MeaiOpenAiChatClient`** parser can read it via **`StreamReader.ReadLineAsync`** without a special code path. Includes minimal JSON escaping so chunks containing quotes / control chars don't corrupt the framed event.
- **Dependency:** **`com.neoxider.coreai` `1.6.17`** — lockstep semver.

#### Package **`1.6.17`**.

## [1.6.16] - 2026-05-04

### WebGL fetch — default `credentials: omit` (OpenRouter / CORS `*`)

- **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** — when **`SameOriginCredentials`** is **off** (default), **`fetch`** now uses **`credentials: 'omit'`** instead of **`'include'`**, so providers that respond with **`Access-Control-Allow-Origin: *`** (e.g. OpenRouter) no longer fail the browser preflight. **`Authorization: Bearer …`** is still sent. **`SameOriginCredentials` on** still maps to **`same-origin`**.
- **`CoreAISettingsAsset`** tooltips, **`CoreAISettingsAssetEditor`**, **`COREAI_SETTINGS.md`**, **`TROUBLESHOOTING.md`** — document the behaviour.
- **Dependency:** **`com.neoxider.coreai` `1.6.16`** — lockstep semver.

#### Package **`1.6.16`**.

## [1.6.15] - 2026-05-04

### Inspector — WebGL streaming toggles under Advanced

- **`CoreAISettingsAssetEditor`** — **Essentials → Streaming** keeps only **Global streaming**. **WebGL: native SSE (fetch)** and **WebGL: fetch credentials** moved to **Advanced Settings → WebGL player (browser build)** (foldout state persisted via `EditorPrefs`). When the active build target is **WebGL** and global streaming is on but native SSE is off, **Essentials** still shows a short warning pointing to that foldout.
- **`COREAI_SETTINGS.md`**, **`README_CHAT.md`** — document the new layout.
- **Dependency:** **`com.neoxider.coreai` `1.6.15`** — lockstep semver with this package.

#### Package **`1.6.15`**.

## [1.6.14] - 2026-05-04

### Documentation — WebGL streaming defaults and version drift

- Synced **`WebGlNativeStreaming`** default-**on** story and fetch-bridge wording across **`TODO.md`**, **`Docs/WEBGL_SERVER_MANAGED_PLAN_RU.md`** (header + stage 2), **`STREAMING_WEBGL_TODO.md`**, **`STREAMING_ARCHITECTURE.md`**, **`HTTP_TRANSPORT_SPEC.md`**, root **`README.md`** / **`README_RU.md`**, **`Assets/CoreAiUnity/README.md`**, and the historical WebGL note in **`CHANGELOG`** (**0.25.2**).

#### Package **`1.6.14`**.

## [1.6.13] - 2026-05-04

### CoreAISettings — streaming defaults + Inspector

- **`CoreAISettingsAsset`** — **`webGlNativeStreaming`** defaults to **`true`** (new instances + clearer WebGL-first streaming). **`sameOriginCredentials`** tooltip corrected (true → fetch `same-origin`, false → `include`).
- **`CoreAISettingsAssetEditor`** — **WebGL** toggles (**native SSE**, **fetch credentials**) always visible under **Streaming** (not only when the active build target is WebGL), with a short note that they apply in the browser player.
- **Resources:** **`CoreAISettings.asset`**, **`CoreAISettings 35b.asset`**, **`open.preset`**, **`LocalCoreAi.preset`**, **`CoreAISettingsAssetOpenRouter.preset`** — **`webGlNativeStreaming: 1`** for consistency.
- **`COREAI_SETTINGS.md`** — defaults and third-toggle explanation.
- **EditMode:** defaults assert **`WebGlNativeStreaming`** / **`SameOriginCredentials`**.

#### Package **`1.6.13`**.

## [1.6.12] - 2026-05-04

### Inspector — global streaming visible

- **`CoreAISettingsAssetEditor`** — **Essentials** block now draws **`enableStreaming`** (**Global streaming**, default on) and, when the active build target is **WebGL**, **`webGlNativeStreaming`** + **`sameOriginCredentials`**, with a warning if global streaming is on but native SSE is off.
- **`CoreAISettingsAsset`** — **`[Header(\"Streaming\")]`** on the serialized group for clarity in fallback inspectors.
- **`COREAI_SETTINGS.md`**, **`README_CHAT.md`** — document where the toggles live and the override hierarchy.
- **EditMode:** **`CoreAISettingsAssetEditModeTests.SerializedObject_HasEnableStreaming_ForInspectorBinding`**.

#### Package **`1.6.12`**.

## [1.6.11] - 2026-05-04

### WebGL — why chat is not streaming

- **`CoreAiChatService`** — in the **WebGL player**, when **`WebGlNativeStreaming`** is off but the UI still wants streaming, log a **one-time** warning explaining that **`IsStreamingEnabled` is false**, **`LLM ◀`** logs have **no `(stream)`**, and replies arrive in one block; point to **`WebGlNativeStreaming`** + **`CoreAiSseFetch.jslib`**.
- **`CoreAIProductionSettingsValidator`** — WebGL preprocess warning when **`EnableStreaming`** is on and **`WebGlNativeStreaming`** is off now covers **ClientOwnedApi** and **ClientLimited** (not only **ServerManagedApi**), excluding **Offline** / **LocalModel**.
- **`README_CHAT.md`** — WebGL section updated (native fetch bridge vs forced non-streaming).

#### Package **`1.6.11`**.

## [1.6.10] - 2026-05-04

### Docs / sample — WebGL build hygiene

- **`Docs/WEBGL_BUILD_TROUBLESHOOTING.md`** — LLVM **out of memory** during WebGL IL2CPP (`Il2CppGenericMethodPointerTable.c`), `IOException` under `ProjectSettings/Packages`, and the CoreAI **StreamingAssets** preprocess log; mitigations (stripping, exceptions, RAM, Defender).
- **`Docs/STREAMING_WEBGL_TODO.md`** — cross-link to the troubleshooting doc.
- **Sample:** `Assets/_exampleGame/.../ArenaBootstrap/Infrastructure.meta` — fixed **`guid:`** (was 33 hex chars; Unity rejected the folder asset).
- **EditMode:** **`RogueliteArenaInfrastructureMetaEditModeTests`** — asserts the folder `.meta` keeps a valid 32-char Unity GUID.

#### Package **`1.6.10`**.

## [1.6.9] - 2026-05-03

### Chat / LLM — visible streaming when the gateway sends one long delta

- **`MeaiLlmClient.CompleteStreamingAsync`** — when live token streaming is enabled, each non-empty visible string from the inner client is **split into outward chunks** (default **48** characters, without splitting UTF-16 surrogate pairs) if it exceeds that size. Some OpenAI-compatible hosts (e.g. **OpenRouter**) often emit the full assistant text in a **single** `delta.content`, which previously produced **`LLM ◀ (stream) … chunks=1`** and no incremental UI repaint even though the transport was SSE. Tool extraction still uses the **full** accumulated text per provider update; only the consumer-facing stream is fanned out.
- **EditMode:** **`CompleteStreamingAsync_SingleLargeInnerDelta_FansOutToMultipleTextChunks`**.

#### Package **`1.6.9`**.

## [1.6.8] - 2026-05-03

### Portable dependency + EditMode parity

- **`com.neoxider.coreai` `1.6.8`** — **`QueuedAiOrchestrator`** treats **`TaskCanceledException`** from the inner orchestrator as cancellation on the public **`RunTaskAsync` / streaming** tasks (fixes **`CancelTasks_SpecificScope_CancelsActiveTask`** when the stub completes the gate with **`TrySetCanceled()`**).
- **`ToolCallExtractionParityEditModeTests.Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues`** — no longer requires that concatenated streamed **`Text`** omit raw tool-call JSON; with live deltas and bound tools, that JSON can appear before tools run; assertions still cover tool invocation count, terminal chunk, **`ExecutedToolCalls`**, and **`All good.`** in the aggregate.

#### Package **`1.6.8`**.

## [1.6.7] - 2026-05-03

### Chat / LLM — real streaming with bound tools (`MeaiLlmClient`)

- **`MeaiLlmClient.CompleteStreamingAsync`** — emit each visible assistant delta **during** `GetStreamingResponseAsync` when tools are **not** requested, or when tools are requested **and** AIFunctions are actually bound (`aiTools.Count > 0`). Previously the client buffered the entire SSE turn and only yielded after the stream closed, so **`LLM ◀ (stream) … chunks=1`** and the chat UI showed one block even when OpenRouter streamed many deltas.
- **Unchanged buffering** when the policy lists tools but **zero** AIFunctions are bound (e.g. missing memory store) so text-shaped tool JSON can still be stripped before any outward chunk (see **`ToolCallExtractionParityEditModeTests.Streaming_RequestedButNotBound_StripsJsonAndEmitsClean`**).
- **`GetStreamingUpdateText`** — fall back to **`TextContent`** in **`ChatResponseUpdate.Contents`** when **`Text`** is empty.
- **EditMode:** **`CompleteStreamingAsync_NoTools_YieldsOneChunkPerInnerUpdateBeforeTerminal`**; relaxed JSON-absence assertions on streaming tool tests where live chunks may briefly include raw tool JSON.

#### Package **`1.6.7`**.

## [1.6.6] - 2026-05-03

### Chat — streaming UI thread + clear button label

- **`CoreAiChatPanel.SendStreamingAsync`** — after each streamed chunk (and before final hooks), **`await CoreAiWebGlUiThreadMarshaling.SwitchToMainThreadForUiOptional`** runs on **all** targets, not only `#if UNITY_WEBGL`. The LLM stack resumes on the thread pool via **`ConfigureAwait(false)`**; without this hop, UI Toolkit often did not repaint incrementally so streaming looked “broken” in Editor / standalone.
- **`CoreAiChat.uxml`** — header clear control: label **`*` → `C`** (Clear); tooltip **`Clear context (Clear)`**.

#### Package **`1.6.6`**.

## [1.6.5] - 2026-05-03

### Chat — WebGL streaming gate (single source of truth)

- **`CoreAiChatPanel.ShouldUseStreamingForRole`** no longer applies a second WebGL / **`WebGlNativeStreaming`** check against **`CoreAISettingsAsset.Instance`** only (that could disagree with DI and force non-streaming even when **`CoreAiChatService.IsStreamingEnabled`** would allow SSE).
- **`CoreAiChatService`** — WebGL native-SSE prerequisite now uses **`WebGlNativeStreamingBridgeEnabled`**: **`ICoreAISettings`** when it is a **`CoreAISettingsAsset`**, else **`CoreAISettingsAsset.Instance`**, matching the panel’s effective settings more reliably in the player.

#### Package **`1.6.5`**.

## [1.6.4] - 2026-05-03

### WebGL player — LLM requests and public API CORS

- **Portable dependency:** **`com.neoxider.coreai` 1.6.4** — **`MeaiOpenAiChatClient`** no longer attaches correlation / idempotency / tenant headers in the **WebGL player** build, avoiding browser preflight failures against APIs that do not list those headers in **`Access-Control-Allow-Headers`** (typical when calling **OpenRouter** directly from **`http(s):…`** game origins).

#### Package **`1.6.4`**.

## [1.6.3] - 2026-05-03

### Editor — file-backed agent memory when build target is WebGL

- **`CoreAILifetimeScope`** — register **`FileAgentMemoryStore`** when **`!UNITY_WEBGL || UNITY_EDITOR`**, so **Editor Play Mode** keeps persisted chat / `IAgentMemoryStore` behaviour while the active **Build Target** is **WebGL** (previously **`UNITY_WEBGL`** forced **`NullAgentMemoryStore`**, which broke history and host tests).
- **WebGL player** (pre-**v1.6.19**): **`NullAgentMemoryStore`** + **`NullConversationTranscriptStore`** (avoid synchronous `File` on IndexedDB). **v1.6.19** registers **`FileAgentMemoryStore`** for the player too, with **`CoreAi_PersistFsSync`** after writes.
- **Compile:** **`CoreAiChatService`** / **`CoreAiChatPanel`** — add **`using CoreAI.Infrastructure.Llm`** for **`CoreAISettingsAsset`** / **`WebGlNativeStreaming`** checks under **`UNITY_WEBGL && !UNITY_EDITOR`** (fixes **CS0246** when building WebGL player).
- **WebGL link:** **`CoreAiSseFetch.jslib`** — use Unity’s documented macro **`makeDynCall`** (lowercase), not **`MakeDynCall`**, so Emscripten **6000.3** no longer throws **`ReferenceError: MakeDynCall is not defined`** during **`build.js`** / **jsify**.
- **WebGL link:** avoid **`?.` optional chaining** in **`CoreAiSseFetch.jslib`** (Emscripten’s **Node** parser rejects it — **`SyntaxError: Unexpected token '.'`**); use plain **`&&`** property access for OpenAI **`delta.content`** extraction.
- **Dependency:** **`com.neoxider.coreai` 1.6.3** (lockstep semver; portable assembly unchanged).

#### Package **`1.6.3`**.

## [1.6.2] - 2026-05-03

### Reliability, tests, chat session coverage

- **`UnityMainThreadLlmAsyncMarshaler`** — **`RuntimeInitializeOnLoadMethod` (BeforeSceneLoad / AfterSceneLoad)** primes the Editor **`Application.isPlaying` mirror**; off the mirrored main thread, **inline** tool execution runs only when the mirror reads **`0`** (confirmed Edit idle), fixing Play Mode tool bodies executing on the CLR thread pool.
- **Play Mode:** **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** — initial **`yield return null`** so the mirror can refresh before the thread-pool assertion.
- **CraftingMemory:** **`CraftingMemoryViaLlmUnityPlayModeTests`** — determinism **`Assert`**, craft-4 prompt injects the real weapon name, **`ExtractCraftInfo`** prefers memory-backed craft lines; **`CraftingMemoryItemNameExtractor`** — pattern for **`**Weapon crafted**: …`** prose.
- **CraftingMemory OpenAI harness:** prompts require a **numeric** `create_item` quality literal; **`AssertExecuteLuaUsesNumericQualityIfPresent`** in **`ExtractCraftInfo`** and the two-craft scenario.
- **Edit Mode:** **`CoreAiChatServiceEditModeTests`** — **`TryGetPersistedChatHistory`** (empty / tail / no store) and **`PersistedChat_UiFormattingRoundTrip_MatchesCoreAiChatPanelRules`** (same path as **`CoreAiChatPanel.TryAppendPersistedChatHistoryFromStore`** via service + **`FormatPersistedMessageForUi`**).
- **Edit Mode:** **`ConversationContextCompactionEditModeTests`** — **`DeterministicManager_MaxRolledSummaryTokens_TruncatesBeforeSave`** and **`DeterministicManager_MaxRolledSummaryTokens_TruncatesStoredOnlySnapshot`** assert **`MaxRolledSummaryTokens`** truncation and **`InMemoryConversationSummaryStore`** parity for rolled summaries.
- **Docs:** **`README_CHAT.md`** (session-restore test pointers); **`ARCHITECTURE.md`** (v1.6.2 marshaler note).
- **Dependency:** **`com.neoxider.coreai 1.6.2`** (lockstep; no portable code changes in this drop).

#### Package **`1.6.2`**.

## [1.6.1] - 2026-05-03

### CoreAISettings — chat history summarization

- **`CoreAISettingsAsset`** — **`enableConversationHistorySummarization`**, **`conversationHistoryRecentTokenBudgetOverride`**, **`conversationRolledSummaryMaxTokens`**; **`enableLlmContextCompaction`** moved into the same Inspector group (**Advanced → Chat history summarization**).
- **`CoreAISettingsAssetEditor`** — dedicated foldout + tooltips; LLM compaction toggle removed from the General foldout to avoid burying summarization options.
- **Dependency:** **`com.neoxider.coreai 1.6.1`** (portable summarization wiring + **`ConversationRolledSummaryLimiter`**).
- **Edit Mode:** **`AiOrchestratorHistoryEditModeTests`**, **`CoreAISettingsAssetEditModeTests`**, **`ConversationRolledSummaryLimiterEditModeTests`**.
- **Docs:** **`COREAI_SETTINGS.md`** (table + portable property names).

#### Package **`1.6.1`**.

## [1.6.0] - 2026-05-03

### Minor release — WebGL server-managed LLM (fetch SSE, auth refresh, settings)

- **`CoreAiSseFetch.jslib`** + **`FetchSseOpenAiTransport`** — when **`CoreAISettingsAsset.WebGlNativeStreaming`** is on in the **WebGL player**, **`MeaiLlmClient.CreateHttp`** uses **`fetch`** + **`ReadableStream`** for incremental SSE (Editor keeps **`HttpClient`**).
- **`SameOriginCredentials`** maps to **`credentials: 'same-origin'`** vs **`'include'`** on the fetch bridge.
- **`RefreshOnUnauthorizedDecorator`** — publishes **`LlmAuthExpired`** only when refresh fails; non-streaming path catches **`LlmClientException`** / **`AuthExpired`**; streaming uses **`MoveNextAsync`** for mid-stream auth errors; skips unsafe retry after visible text.
- **`LlmClientRegistry`** — per-profile **`ServerManagedApi`** clients wrapped with **`RefreshOnUnauthorizedDecorator`** (aligned with **`LlmPipelineInstaller`** fallback).
- **`ServerManagedCoreSettingsAdapter`** — relative **`ApiBaseUrl`** (`/api/...`) resolves against **`Application.absoluteURL`**.
- **`CoreAISettingsAsset`** — **`WebGlNativeStreaming`**, **`SameOriginCredentials`**; **`CoreAiChatService`** / **`CoreAiChatPanel`** respect native streaming on WebGL when the bridge is enabled.
- **`CoreAIProductionSettingsValidator`** — extended WebGL warnings (keys, streaming without native bridge, **`ClientLimited`** key leak).
- Portable **`com.neoxider.coreai`** — **`LlmRequestContext`**, **`LlmAuthContextRegistry`**, **`MeaiOpenAiChatClient`** header layering, **`LlmCompletionRequest.IdempotencyKey`**, **`IOpenAiHttpSettings.HeaderProvider`** (see core changelog).
- **Edit Mode:** **`MeaiLlmClientEditModeTests`** (idempotency); **`RefreshOnUnauthorizedDecoratorEditModeTests`**.
- **Dependency:** **`com.neoxider.coreai 1.6.0`**.

#### Package **`1.6.0`**.

## [1.5.29] - 2026-05-03

### `CoreAiChatPanel` — pluggable timeout bubble after cancel

- **`ResolveTimeoutMessage(bool stopRequestedByUser)`** — hosts may return **`null`** / empty to skip **`AddMessage`** when they already posted contextual diagnostics (e.g. watchdog + external stop).
- **`SendNonStreamingAsync`** — passes **`CancellationToken`** to **`SendMessageAsync`** so HTTP / orchestration timeouts can cancel the turn (WebGL non-streaming audit; no behavioral change beyond **`ResolveTimeoutMessage`** hook).
- **Dependency:** **`com.neoxider.coreai 1.5.29`** (lockstep).

#### Package **`1.5.29`**.

## [1.5.28] - 2026-05-02

### Drop legacy `PlayerChat` role string

- **Dependency:** **`com.neoxider.coreai 1.5.28`** — removes **`BuiltInAgentRoleIds.PlayerChat`**; use **`PlainChat`** or **`SmartChat`**.
- **Defaults:** **`CoreAiChatConfig`** / **`CoreAiChatPanel`** fall back to **`SmartChat`** when **`RoleId`** is unset.
- **Docs:** examples and routing tables updated (**`README_CHAT.md`**, **`COREAI_SINGLETON_API.md`**, **`QUICK_START.md`**, etc.).
- **Edit / Play tests:** assertions and routing manifests use **`SmartChat`** instead of **`PlayerChat`**.
- **PlayMode:** fixture renamed to **`SmartChatAndAINpcPlayModeTests`**; **`SmartChat_ClearHistory_Works`** validates **`InGameLlmChatService.ClearHistory()`** (replaces a mislabeled duplicate AINpc case).

#### Package **`1.5.28`**.

## [1.5.27] - 2026-05-02

### Two built-in chat agents: PlainChat and SmartChat

- **Dependency:** **`com.neoxider.coreai 1.5.27`** (new built-in chat roles and memory defaults).
- **Demo chat config:** **`CoreAiChatConfig_Demo.asset`** now uses `RoleId = SmartChat` (memory-enabled chat out of the box).
- **Docs:** updated chat-role guidance in **`Docs/AI_AGENT_ROLES.md`** and **`Runtime/Source/Features/Chat/README_CHAT.md`**.
- **Edit Mode tests:** updated built-in role and memory-policy expectations for `PlainChat` / `SmartChat`.

#### Package **`1.5.27`**.

## [1.5.26] - 2026-05-01

### Dependency: Core 1.5.26 — SSE HttpClient lifetime

- **`HttpClientOpenAiTransport`** streaming path no longer disposes **`HttpClient`** before the SSE body is consumed (fixes aborted stream / zero chunks right after HTTP 200).
- **Dependency:** **`com.neoxider.coreai 1.5.26`**.

#### Package **`1.5.26`**.

## [1.5.25] - 2026-05-01

### WebGL HTTP transport + scene guard

- **`UnityWebRequestOpenAiTransport`** — **`IOpenAiHttpTransport`** for **`UNITY_WEBGL && !UNITY_EDITOR`** (`UnityWebRequest`, non-SSE); **`MeaiLlmClient.CreateHttp`** selects it vs **`HttpClientOpenAiTransport`**.
- **`CoreAiWebGlLlmUnitySceneGuard`** — optional early-execution component to **`SetActive(false)`** **LLMUnity** roots on WebGL player so **LlamaLib** never initializes from scene objects.
- **Docs:** **`HTTP_TRANSPORT_SPEC.md`**, **`ARCHITECTURE.md`** (WebGL HTTP + guard), **`STREAMING_ARCHITECTURE.md`** (transports + simulated stream), **`TROUBLESHOOTING.md`** (CORS), **`DOCS_INDEX.md`**.
- **Edit Mode:** **`MeaiOpenAiWebGlTransportEditModeTests`** — non-SSE transport yields assistant text from full completion.
- **Dependency:** **`com.neoxider.coreai 1.5.25`**.

#### Package **`1.5.25`**.

## [1.5.24] - 2026-05-01

### Chat layout + docs

- **`CoreAiChatConfig`:** **`UseFullscreenChat`** (default **off**) — stretch the panel to nearly the full viewport with margins; **`CoreAiChatPanel`** applies class **`coreai-chat-fullscreen`** and the same stretch behaviour as small-screen auto layout. **`CoreAiChatLayoutOptionAttribute`** marks layout options for Inspector clarity / future drawers.
- **`CoreAiChat.uss`:** optional **`coreai-chat-fullscreen`** border-radius tweak.
- **Edit Mode:** **`CoreAiChatConfigEditModeTests`** asserts default fullscreen **off**.
- **Docs:** **`README_CHAT.md`**, **`STREAMING_ARCHITECTURE.md`** (HTTP SSE = **`HttpClient`**, paths, WebGL note).
- **Dependency:** **`com.neoxider.coreai 1.5.24`** (SSE parsing + logging in **`MeaiOpenAiChatClient`**).

#### Package **`1.5.24`**.

## [1.5.23] - 2026-05-01

### Portable HTTP client + EditMode coverage

- **Dependency:** **`com.neoxider.coreai 1.5.23`** — **`MeaiOpenAiChatClient`** in **`CoreAI.Core`** uses **`System.Net.Http.HttpClient`** (not **UnityWebRequest**).
- **Edit Mode:** **`MeaiOpenAiChatClientHttpEditModeTests`** — non-streaming success, HTTP 429 + **`Retry-After`**, SSE aggregation; asserts client assembly is **`CoreAI.Core`**.
- **Docs:** root **`README`**, **`COREAI_SETTINGS`**, **`ARCHITECTURE`**, **`DEVELOPER_GUIDE`**, **`PROJECT_ANALYSIS`**, **`CoreAiUnity/README`** — HTTP transport wording updated for **`HttpClient`**.

#### Package **`1.5.23`**.

## [1.5.22] - 2026-05-01

### VContainer — single `IAgentMemoryStore` registration

- **`RegisterCorePortable`:** optional **`suppressDefaultAgentMemoryStore`** (default **`false`**). When **`true`**, the portable layer does not register **`NullAgentMemoryStore`** as **`IAgentMemoryStore`**.
- **`RegisterConversationSummaryForCoreAiLifetimeScope`:** passes **`suppressDefaultAgentMemoryStore: true`** for both non-WebGL and WebGL branches so **`CoreAILifetimeScope`** remains the only place that registers **`IAgentMemoryStore`** for the Unity host (**`FileAgentMemoryStore`** on all players since **v1.6.19**, including WebGL; previously WebGL player used **`NullAgentMemoryStore`**). Fixes **`VContainerException: Conflict implementation type`** when building **`CoreAILifetimeScope`** (regression after **v1.5.21** WebGL memory registration).
- **Edit Mode:** **`CorePortableAgentMemoryRegistrationEditModeTests`** — suppress path yields a single **`IReadOnlyList<IAgentMemoryStore>`** entry; without suppress, portable Null + host File yields **two** list entries (VContainer does not throw on **Build** for distinct implementation types; duplicate **same** type e.g. WebGL double Null still throws). **`CoreAILifetimeScopeConversationStoreEditModeTests`** also asserts **`IAgentMemoryStore`** type.
- **Docs:** **`ARCHITECTURE.md`**, **`DGF_SPEC.md`**, **`COREAI_SETTINGS.md`**.
- **Dependency:** **`com.neoxider.coreai 1.5.22`**.

#### Package **`1.5.22`**.

## [1.5.21] - 2026-05-01

### WebGL, composition, diagnostics

- **`CoreAILifetimeScope`:** **`UNITY_WEBGL`** registers **`NullAgentMemoryStore`** + **`NullConversationTranscriptStore`** instead of **`FileAgentMemoryStore`** (avoids sync IndexedDB **`File.*`** for agent memory); conversation summaries remain in-memory as in v1.5.20.
- **`CoreAiChatService.IsStreamingEnabled`:** returns **`false`** when **`UNITY_WEBGL && !UNITY_EDITOR`** so HTTP chat uses the non-streaming path (see **`STREAMING_WEBGL_TODO.md`**).
- **`CoreAiChatPanel`:** **`ShouldUseStreamingForRole`** (default off on WebGL player).
- **Diagnostics:** **`CoreAi`**, **`CoreAiChatService`**, **`MessagePipeToolCallEventPublisher`** log resolver/publish failures with **`Debug.LogWarning`** instead of silent **`catch {}`**.
- **JSON:** **`FileAgentMemoryStore`** transcript JSON uses Newtonsoft; **`System.Text.Json.dll`** removed from **`CoreAI.Source`** and test asmdefs.
- **Constants:** **`OpenAiHttpConstants`**, **`CoreAiPersistentPaths`**; **`OpenAiHttpLlmSettings`** / **`MeaiOpenAiChatClient`** / **`CoreAISettingsAssetEditor`** use shared defaults.
- **Docs:** **`ARCHITECTURE.md`**, **`DGF_SPEC.md`** (Core asmdef vs VContainer), **`MEAI_TOOL_CALLING.md`** (IL2CPP **`CreateAIFunction`**), **`STREAMING_WEBGL_TODO.md`** (Solution C status).
- **Dependency:** **`com.neoxider.coreai 1.5.21`**.

#### Package **`1.5.21`**.

## [1.5.20] - 2026-05-01

### WebGL — `CoreAILifetimeScope` conversation summaries

- **`CoreAILifetimeScope`:** under **`UNITY_WEBGL`**, skips **`FileConversationSummaryStore`** and calls **`RegisterCorePortable(suppressDefaultConversationSummaryStore: false)`** so summaries use **`InMemoryConversationSummaryStore`**, avoiding synchronous **`File`** I/O on IndexedDB-backed **`persistentDataPath`** each chat turn.
- **`RegisterConversationSummaryForCoreAiLifetimeScope`:** internal helper used by **`Configure`** (documented on the type).
- **Docs:** **`ARCHITECTURE.md`** — Runtime Context (WebGL vs file-backed registration).
- **Edit Mode:** **`CoreAILifetimeScopeConversationStoreEditModeTests`** — compile-time WebGL contract + non-WebGL resolve of **`FileConversationSummaryStore`**.
- **Dependency:** **`com.neoxider.coreai 1.5.20`**.

#### Package **`1.5.20`**.

## [1.5.19] - 2026-05-01

### Context compaction — main system prompt vs auxiliary summarizer

- **Docs:** **[`MemorySystem.md`](Docs/MemorySystem.md)** — *Separation from the main system prompt* for **`EnableLlmContextCompaction`**: compaction calls use **`LlmContextCompactionOptions.SystemPrompt`** and transcript payload only; **`## Conversation Summary`** is merged into the **primary** turn afterward. **[`COREAI_SETTINGS.md`](Docs/COREAI_SETTINGS.md)** — chat history compaction section notes the same for Inspector users.
- **Edit Mode:** **[`ConversationContextCompactionEditModeTests`](Tests/EditMode/ConversationContextCompactionEditModeTests.cs)** — asserts **`ChatHistory`** null, default vs custom compaction **`SystemPrompt`**, payload headings, and that orchestrator-only marker / **`## Tool Contract`** never leak into compaction input.
- **Play Mode (`FastNoLlm`):** **[`LlmCompactionPerRolePlayModeTests`](Tests/PlayMode/FastNoLlm/LlmCompactionPerRolePlayModeTests.cs)** — records last auxiliary **`__CoreAI_ContextCompaction`** request and asserts **`ChatHistory`** null, **`DefaultSystemPrompt`**, and **`UserPayload`** shape.
- **Dependency:** **`com.neoxider.coreai 1.5.19`**.

#### Package **`1.5.19`**.

## [1.5.18] - 2026-04-30

### Offline client and docs

- **`OfflineLlmClient`:** conversational roles use **`OfflineCustomResponse`** only (no **`[Offline] <payload>`** echo); generic offline JSON drops the **`echo`** field. Log level **Info** for offline path.
- **Docs:** **`COREAI_SETTINGS.md`** (Offline table), **`DEVELOPER_GUIDE.md`**, **`TROUBLESHOOTING.md`** (offline/stub chat symptoms).
- **Edit Mode tests:** **`LlmConversationalRolePolicyEditModeTests`**, **`OfflineLlmClientEditModeTests`** (PlainChat / SmartChat / Teacher), **`AiOrchestratorRefactorEditModeTests`** (Chat vs non-chat failure paths, authority denied).
- **Dependency:** **`com.neoxider.coreai 1.5.18`**.

#### Package **`1.5.18`**.

## [1.5.17] - 2026-04-30

### Editor / MEAI — never probe `Application.isPlaying` off script main

- **`UnityMainThreadLlmAsyncMarshaler`:** gate **`Application.isPlaying`** reads with mirrored **`ManagedThreadId`** (**`Application.onBeforeRender`**); **`SubsystemRegistration`** no longer primes mirrors (**wrong thread risk**).
- **Dependency:** **`com.neoxider.coreai 1.5.17`**.

#### Package **`1.5.17`**.

## [1.5.16] - 2026-04-30

### Editor / MEAI — main-thread probe from thread pool

- **`UnityMainThreadLlmAsyncMarshaler`:** under **`UNITY_EDITOR`**, probes **`Application.isPlaying`** safely; **thread-pool** threads use a **`Application.onBeforeRender`** snapshot so Edit Mode tooling stays **inline** when **not playing** / unknown, while **Editor Play Mode** still **marshals** to the player loop (fixes **`get_isPlaying` off-main** + **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** coherence).
- **Edit Mode tests:** **`UnityMainThreadLlmAsyncMarshalerEditModeTests.InvokeAsync_FromThreadPool_CompletesWithAsyncAwait_AvoidsIsPlayingOnWorker`**.
- **Dependency:** **`com.neoxider.coreai 1.5.16`**.

#### Package **`1.5.16`**.

## [1.5.15] - 2026-04-30

### Portable Core fix (MEAI `ChatMessage.Contents`)

- Dependency **`com.neoxider.coreai 1.5.15`**: **`SmartToolCallingChatClient`** correctly observes native **`FunctionCallContent`** when **`Contents`** is the MEAI **`IList`** model (fixes **three inner iterations → max consecutive errors** behaviour in **`SmartToolCallingChatClientEditModeTests`**).

#### Package **`1.5.15`**.

## [1.5.14] - 2026-04-30

### Editor / Test Runner — MEAI tool marshaling without deadlocks

- **`UnityMainThreadLlmAsyncMarshaler`:** under **`UNITY_EDITOR`** when **`Application.isPlaying`** is **false**, skips **`UniTask.SwitchToMainThread`** and invokes the MEAI **`AIFunction`** factory **inline** (same thread as the calling continuation). Avoids **deadlock** when Edit Mode code blocks the managed **main thread** on **`Task.Wait` / `.Result`** while **`SmartToolCallingChatClient`** continuations use **`ConfigureAwait(false)`** on the **thread pool**.
- **Edit Mode tests:** **`UnityMainThreadLlmAsyncMarshalerEditModeTests`** (`Task.Run` + main-thread **`Wait`** + thread-pool **`InvokeAsync`**; sync-factory thread affinity).
- **Docs:** **`ARCHITECTURE.md`**, **`COREAI_SETTINGS.md`**, **`DEVELOPER_GUIDE.md`**, **`COREAI_SINGLETON_API.md`**; **`CoreAi` API** XML on **`AskAsync`** / class summary.
- **Dependency:** **`com.neoxider.coreai 1.5.14`**.

#### Package **`1.5.14`**.

## [1.5.13] - 2026-04-30

### Tests & documentation — threading contract

- **Edit Mode:** **`LlmAsyncMarshalerEditModeTests`**, **`CoreAISettingsToolMarshalerEditModeTests`**, extended **`ToolExecutionPolicyEditModeTests`** for **`ToolInvocationMarshaler`**.
- **Play Mode (`FastNoLlm`):** **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** verifies **`UniTask.SwitchToThreadPool`** then marshaler restores the Unity test thread’s **`ManagedThreadId`** inside the tool factory (inequality pre-check only when not **`UNITY_WEBGL`**).
- **Docs:** **`ARCHITECTURE.md`**, **`COREAI_SETTINGS.md`**, **`DEVELOPER_GUIDE.md`**, **`Tests/PlayMode/README.md`**.
- **Dependency:** **`com.neoxider.coreai 1.5.13`**.

#### Package **`1.5.13`**.

## [1.5.12] - 2026-04-30

### LLM / tools — main thread for `UnityWebRequest` and MEAI tools

`SmartToolCallingChatClient` keeps **`ConfigureAwait(false)`** on the inner loop (WebGL-friendly). After the first model response, continuations can run on the **thread pool**, which breaks **`UnityWebRequest`** construction and GameObject-based tools.

- **`MeaiOpenAiChatClient.GetResponseAsync`** / **`GetStreamingResponseAsync`** — **`await UniTask.SwitchToMainThread(PlayerLoopTiming.Update)`** at entry so every HTTP round-trip creates UWR on the player loop.
- **`UnityMainThreadLlmAsyncMarshaler`** — implements **`ICoreAISettings.ToolInvocationMarshaler`**; **`CoreAISettingsAsset`** returns this instance so tool bodies run on the main thread.
- **Dependency:** **`com.neoxider.coreai 1.5.12`**.

#### Package **`1.5.12`**.

## [1.5.11] - 2026-05-01

### Testing — Play Mode layout (3 suites + support DLLs)

Legacy single assembly **`PlayModeTest`** is replaced by:

| Assembly | Folder | Role |
|----------|--------|------|
| **`CoreAI.Tests.PlayMode.FastNoLlm`** | `Tests/PlayMode/FastNoLlm` | **Fast** runs: stubs, **`CoreAiChatPanel`** smoke, Lua integration, orchestrator built-in roles with **`StubLlmClient`**, compaction gates — **no LLMUnity assembly reference** on this DLL. |
| **`CoreAI.Tests.PlayMode.LlmVerification`** | `Tests/PlayMode/LlmVerification` | **LLM checks**: streaming/HTTP, tools, memory, chat service, full-pipeline resilience (**`Assert.Ignore`** when no backend). **`AiOrchestratorBuiltInRolesProductionLlmPlayModeTests`** lives here. |
| **`CoreAI.Tests.PlayMode.Scenarios`** | `Tests/PlayMode/Scenarios` | **Game-style** multi-step flows (crafting memory, multi-agent workflow, merchant scenario). |

Support: **`CoreAI.Tests.PlayMode.Shared`** (`PlayModeTestAwait`, **`AiOrchestratorBuiltInRolesPlayModeHarness`**), **`CoreAI.Tests.PlayMode.LlmInfra`** (`SharedLlmUnity`, **`PlayModeProductionLikeLlmFactory`**, **`TestAgentSetup`**, teardown). Index: **`Tests/PlayMode/README.md`**.

- **Docs:** **`DEVELOPER_GUIDE.md`**, **`QUICK_START.md`**, **`DGF_SPEC.md`**, **`DOCS_INDEX.md`** (Crafting readme path).
- **Editor:** **`FixLlmUnityAsmdefWiring`** targets the three Play Mode asmdefs that include **`COREAI_HAS_LLMUNITY`** (not **FastNoLlm** / **Shared**).

#### Package **`1.5.11`**. Dependency **`com.neoxider.coreai 1.5.11`**.

## [1.5.10] - 2026-05-01

### Editor / player — responsive game during LLM HTTP wait

Polling **`UnityWebRequest`** on the main thread with **`await Task.Delay(0)`** could complete too eagerly and **busy-wait** the player loop: the scene appeared frozen until the HTTP response returned.

- **`MeaiOpenAiChatClient`** — non-streaming and SSE poll loops now **`await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken)`** instead of **`Task.Delay(0)`**, so **Update / input / rendering** keep running while waiting for **`op.isDone`**.
- **`MeaiOpenAiChatClient`** — poll and retry **`await Task.Delay(...)`** paths stay **without** **`ConfigureAwait(false)`** so continuations remain compatible with **WebGL / main-thread UWR** rules.
- **`MeaiLlmClient.CompleteAsync`** / **`RoutingLlmClient.CompleteAsync`** — removed **`ConfigureAwait(false)`** on inner completion **`await`**s so post-LLM work stays on the Unity sync context where appropriate.

### UPM / tooling

- **`package.json`** — **`author`** block (`NeoXider` + repo URL) so Package Manager is not **Author unknown**.
- **Roslyn analyzer** **`CAIU001`** — warns on **`ConfigureAwait(false)`** in **`CoreAiUnity` Runtime/Editor** (deployed under **`Assets/CoreAiUnity/RoslynAnalyzers`**); **`Tools/build-analyzers.ps1`**, **`Tools/CoreAI.UnityAsyncAnalyzer.Tests`**.
- **EditMode** — **`RoslynAnalyzerDeploymentTests`**; test sources use classic namespace blocks for **C# 9** compatibility.

#### Package **`1.5.10`**. Dependency **`com.neoxider.coreai 1.5.10`**.

## [1.5.9] - 2026-04-30

### WebGL / single-threaded async — HTTP poll + MEAI completion chain

> **Update:** The **`UnityWebRequest`** poll implementation described in the first bullet below was **replaced in 1.5.10** by **`UniTask.Yield(PlayerLoopTiming.Update)`** and **no `ConfigureAwait(false)`** on the poll `await`s (see **1.5.10**). Portable Core items (**`SmartToolCallingChatClient`**, orchestrator, queue) from **com.neoxider.coreai 1.5.9** are unchanged.

Continuations that always capture `UnitySynchronizationContext` can stall after **`SmartToolCallingChatClient`** returns a text response (logs show **Text response, stopping** but no **GetResponseAsync completed** in **`MeaiLlmClient`**).

- **`MeaiOpenAiChatClient`** — replace **`Task.Yield()`** in **`UnityWebRequest`** poll loops with **`await Task.Delay(0, ct).ConfigureAwait(false)`**; retry backoff **`Task.Delay(...).ConfigureAwait(false)`** (non-stream + stream paths).
- **`MeaiLlmClient.CompleteAsync`** — **`ConfigureAwait(false)`** on **`SmartToolCallingChatClient.GetResponseAsync`**.
- **Dependency:** **`com.neoxider.coreai 1.5.9`** (same semver as this package; portable changelog: **`SmartToolCallingChatClient`**, **`AiOrchestrator`**, **`QueuedAiOrchestrator`**, tools).

#### Package **`1.5.9`**.

## [1.5.8] - 2026-04-30

### LLM: visible assistant text for WebGL / HTTP completions

Some OpenAI-compatible providers return **no usable `ChatResponse.Text`** even though the JSON body carries text (multimodal **`content` as an array**, or **`reasoning_content`** with empty **`content`**). **`MeaiLlmClient.CompleteAsync`** would then return **`EmptyResponse`**, so logs could show **`[SmartToolCall] Text response, stopping`** while the chat bubble never appeared.

- **`MeaiOpenAiChatClient.ParseResponse`** — parses **`message.content`** as string **or** array of parts (`text` fields); if still empty after stripping `<think>` / legacy blocks, uses **`reasoning_content`**. **Parsing uses `JObject.Parse`**: `DeserializeObject<Dictionary<string,object>>` left **`choices`** in a shape where **`as JArray`** was null, so **assistant text was always empty** in tests and at runtime.
- **`MeaiLlmClient.CompleteAsync`** — if **`response.Text`** is empty, concatenates **`TextContent`** from **`response.Messages`** via **`SmartToolCallingChatClient.ConcatenateAssistantTextContents`** (portable Core **1.5.6**).
- **EditMode tests:** `ParseCompletion_EmptyContent_UsesReasoningContent`, `ParseCompletion_ContentAsTextPartsArray_JoinsText`.
- **Dependency:** **`com.neoxider.coreai 1.5.6`**.

### Meta

- Package **`1.5.8`**.

## [1.5.7] - 2026-04-30

### WebGL: chat stack main-thread affinity (orchestrator `ConfigureAwait(false)`)

Orchestrator / LLM pipeline resumes on thread-pool continuations in several places. In **WebGL** that can leave **`CoreAiChatPanel`** and **`CoreAi.AskAsync`** callers past `RunTaskAsync` **off** the Unity player loop so logs show `SmartToolCall` complete but **UI Toolkit** never updates.

- **`CoreAiChatService.SendMessageAsync`** — after **`RunTaskAsync`**, **`await UniTask.SwitchToMainThread(PlayerLoopTiming.Update)`** before returning (no-op if already on main thread).
- **`CoreAi.AskAsync`** — removed **`ConfigureAwait(false)`** so the default sync-context capture applies to Unity-hosted code.
- **`CoreAiChatPanel.RunAgentTurnAsync`** — **`OperationCanceledException`** / general **`catch`**: **`SwitchToMainThread`** before **`AddMessage`**; **`finally`** uses **`PlayerLoopTiming.Update`** explicitly.
- **`CoreAiChatPanel.SendNonStreamingAsync`** — **`SwitchToMainThread`** uses **`PlayerLoopTiming.Update`** + **`CancellationToken.None`** for the post-HTTP marshal (avoid spurious cancel while switching); **`finally`** unchanged pattern with explicit timing.
- **`CoreAiChatPanel.SendStreamingAsync`** — **`#if UNITY_WEBGL`**: **`SwitchToMainThread`** at **each** streamed chunk before UI mutations (and again before **`OnResponseReceived`**); **`finally`** marshals before **`FinishStreaming`** / **`HideTypingIndicator`** (all platforms).

### Meta

- Package **`1.5.7`**. Dependency **`com.neoxider.coreai 1.5.5`**.

## [1.5.6] - 2026-04-30

### Fixed / hardening — chat panel main thread (WebGL / async continuations)

- **`CoreAiChatPanel.RunAgentTurnAsync`** — outer `finally` now **`await UniTask.SwitchToMainThread`** (best-effort; warning on failure) before **`FinishStreaming`**, **`HideTypingIndicator`**, **`_isSending = false`**, and send-button refresh, so the **streaming/agent** path matches the non-streaming marshaling story (UI Toolkit must not be mutated from arbitrary thread-pool continuations after HTTP).
- **`CoreAiChatPanel.SendNonStreamingAsync`** — **`try`/`finally`** structure: always **`await UniTask.SwitchToMainThread`** in `finally` before a defensive **`HideTypingIndicator`**; empty **`FormatResponseText`** result shows the configured **“no response”** line (same as empty raw body) and does not fire completion callbacks with empty text.

### Meta

- Package **`1.5.6`**. Dependency **`com.neoxider.coreai 1.5.5`**.

## [1.5.5] - 2026-04-29

### Tests: non-streaming chat panel (WebGL-relevant path)

- **PlayMode:** `CoreAiChatPanelNonStreamingPlayModeTests` — `SubmitMessageFromExternalAsync` with **streaming off**, stub `CoreAiChatService` / `IAiOrchestrationService`, **no** `UIDocument`: asserts **`OnAiResponseCompleted`**, return text, and **`_isSending == false`** after the turn; second case overrides **`FormatResponseText`** to empty and asserts **null** result and **no** completion fire (matches “No response” UI path).
- **Dependency:** unchanged **`com.neoxider.coreai 1.5.5`**.

### Meta

- Package **`1.5.5`**. Dependency **`com.neoxider.coreai 1.5.5`**.

## [1.5.4] - 2026-04-29

### WebGL / browser: chat UI after non-streaming LLM turns

- **`CoreAiChatPanel.RunAgentTurnAsync`** — `finally` now **`await UniTask.SwitchToMainThread`** before `HideTypingIndicator`, reset of `_isSending`, and send-button refresh so UI Toolkit updates always run on the Unity player loop (WebGL runs inside the browser; continuations after HTTP must not mutate `VisualElement` off-thread).
- **`CoreAiChatPanel.SendNonStreamingAsync`** — after **`SendMessageAsync`**, **`await UniTask.SwitchToMainThread`** before hiding typing and appending the assistant bubble; nested `try`/`finally` also switches before a defensive **`HideTypingIndicator`**.
- **Empty formatted replies** — if **`FormatResponseText`** yields empty text, show **`No response.`** (same as empty raw response) instead of an empty bubble.
- **Dependency:** bumped to **`com.neoxider.coreai 1.5.5`**.

### Meta

- Package **`1.5.4`**. Dependency **`com.neoxider.coreai 1.5.5`**.

## [1.5.3] - 2026-04-30

### LLM-assisted context compaction wiring

- **`ConversationContextManagerFactories.Create`** — wired into **`RegisterCorePortable`** so **`CoreAILifetimeScope`** honours **`ICoreAISettings.EnableLlmContextCompaction`** without moving logic out of Core.
- **`SelectingConversationContextManager`** — registered when global compaction is enabled; each request selects LLM vs deterministic rollup via **`ConversationContextBuildArgs.UseLlmContextCompaction`** (from **`AgentMemoryPolicy.RoleMemoryConfig`**).
- **Per-role defaults:** built-in **`Creator`**, **`Analyzer`**, **`AINpc`**, **`PlainChat`**, **`SmartChat`**, **`Merchant`**, **`CoreMechanicAI`** default **on**; **`Programmer`** defaults **off** (deterministic only). Override via **`AgentBuilder.WithLlmContextCompaction(bool)`**.
- **EditMode tests:** `ConversationContextCompactionEditModeTests` (factory routing, selecting wrapper, LLM skip/invoke), `LlmCompactionPerRoleEditModeTests` (orchestrator per-role gate with `SplitCountingLlm`, `AgentBuilder` API).
- **PlayMode tests:** `LlmCompactionPerRolePlayModeTests` (same gates under Unity lifecycle with stub LLM).
- **`ARCHITECTURE.md`** — updated context manager narrative.
- **`README.md`** — documents v1.5.3 features including LLM-assisted compaction and `AgentBuilder` API.
- **Editor onboarding** — **`CoreAI → Setup → Install Git Dependencies`** merges missing Git UPM entries into `Packages/manifest.json`; scene menu **`CoreAI → Setup → Create Bare Scene (advanced)`** supersedes the old root **`CoreAI → Create Scene Setup`** path; **`CoreAISettingsAsset` Inspector** shows **Essentials** + collapsed **Advanced** (global LLM compaction toggle under Advanced → General).
- **README / README_RU** — Quick Start documents the manifest menu shortcut, Chat Demo vs bare scene, and clarifies that detailed Markdown guides ship in English.
- **Documentation** — **`EXAMPLES.md`**, **`AGENT_BUILDER.md`**, **`QUICK_START.md`**, **`COREAI_SINGLETON_API.md`** steer beginners toward **`WithAction`** before custom **`ILlmTool`**.
- **EditMode tests:** `AgentBuilderEditModeTests` coverage for **`ValidateOnBuild`** / compaction gate / built-in-role prompt fallback.
- **Dependency:** bumped to **`com.neoxider.coreai 1.5.3`**.

### Meta

- Package **`1.5.3`**. Dependency **`com.neoxider.coreai 1.5.3`**.

## [1.5.2] - 2026-04-30

### Context & persistence wiring

- **`RegisterCorePortable()`** registers default **`InMemoryConversationSummaryStore`**; **`CoreAILifetimeScope`** registers **`FileConversationSummaryStore`**, then **`RegisterCorePortable(suppressDefaultConversationSummaryStore: true)`** (`persistentDataPath/CoreAI/ConversationSummaries`).
- **`FileAgentMemoryStore`** now exposes **`IConversationTranscriptStore`** (structured transcript JSON + lazy migration).
- **`MeaiOpenAiChatClient`** maps HTTP **413** and common context-overload payloads to **`LlmErrorCode.ContextLengthExceeded`**.
- **EditMode tests:** `AiOrchestratorHistoryEditModeTests` context-overflow retry (`RunTaskAsync_RetriesOnce_OnContextLengthExceeded`), `FileConversationSummaryStoreEditModeTests`.
- **`ARCHITECTURE.md`**, **`MemorySystem.md`** — budget/summary/transcript narrative.
- **Dependency:** bumped to **`com.neoxider.coreai 1.5.2`**.

### Meta

- Package **`1.5.2`**. Dependency **`com.neoxider.coreai 1.5.2`**.

## [1.5.1] - 2026-04-30

### 🛡️ WebGL Stability: UniTask-based timeout + error propagation

- **`CoreAiChatService.SendMessageAsync`** / **`SendMessageStreamingAsync`** — timeout is now enforced at the Unity layer via **`CancelAfterSlim`** (`Cysharp.Threading.Tasks`), which uses Unity's `PlayerLoop` and is fully compatible with WebGL's single-threaded execution model. Previously, timeout relied on `CancellationTokenSource.CancelAfter` (backed by `System.Threading.Timer`), which hangs indefinitely in WebGL/Emscripten.
- **Error propagation** — `SendMessageAsync` no longer swallows exceptions with a `catch` block that returned `null`. Errors now propagate to `CoreAiChatPanel`, which displays the error message to the user instead of a generic "No response."
- **Dependency:** bumped to **`com.neoxider.coreai 1.5.1`** (retry multiplier fix, `CancelAfter` removal from orchestrator and decorator).

### Meta

- Package **`1.5.1`**. Dependency **`com.neoxider.coreai 1.5.1`**.

## [1.5.0] - 2026-04-30

### 🏗️ Architecture: Portable LLM pipeline decoupling

Migrated core LLM pipeline components from `CoreAI.Source` (Unity-dependent) to `CoreAI.Core` (portable, `noEngineReferences: true`). These classes now run in any .NET host without `UnityEngine`.

#### Moved to `CoreAI.Core` (`CoreAI.Infrastructure.Llm` namespace)
- **`LoggingLlmClientDecorator`** — LLM request/response logging, retry with exponential backoff, prompt budget diagnostics. Now uses `ILog` (portable) instead of `IGameLogger` (Unity).
- **`ToolExecutionPolicy`** — duplicate detection, consecutive-error tracking, per-call `[ToolCall]` diagnostics. Now uses `ILog`, `IToolCallEventPublisher`, `IToolExecutionNotifier` instead of Unity-specific `IGameLogger`, `GlobalMessagePipe`, `CoreAi.NotifyToolExecuted`.
- **`SmartToolCallingChatClient`** — MEAI `IChatClient` wrapper with automatic tool-call loop, text-based extraction fallback, and error tracking. Now uses `ILog` and portable `LlmToolCallTextExtractor`.
- **`ClientLimitedLlmClientDecorator`** — per-session request/prompt-size limits (already had no engine dependencies).

#### New portable abstractions (`CoreAI.Core`)
- **`ILlmPreflightAnnotator`** — replaces hard type-check against `RoutingLlmClient` in `LoggingLlmClientDecorator`.
- **`IToolCallEventPublisher`** + `NullToolCallEventPublisher` — portable contract for tool-call lifecycle events.
- **`IToolExecutionNotifier`** + `NullToolExecutionNotifier` — portable contract for tool execution subscriber notification.

#### New Unity-side adapters (`CoreAI.Source`)
- **`MessagePipeToolCallEventPublisher`** — bridges `IToolCallEventPublisher` to `GlobalMessagePipe`.
- **`CoreAiToolExecutionNotifier`** — bridges `IToolExecutionNotifier` to `CoreAi.NotifyToolExecuted`.
- **`RoutingLlmClient`** — now implements `ILlmPreflightAnnotator`.

#### Breaking changes
- `LoggingLlmClientDecorator` constructor: `IGameLogger` → `ILog`.
- `ToolExecutionPolicy` constructor: `IGameLogger` → `ILog`, adds optional `IToolCallEventPublisher` + `IToolExecutionNotifier`.
- `SmartToolCallingChatClient` constructor: `IGameLogger` → `ILog`, adds optional `IToolCallEventPublisher` + `IToolExecutionNotifier`.

#### Tests
- All EditMode tests updated to use `ILog` / `NullLog.Instance` instead of `IGameLogger` stubs.
- **`MessagePipeEventPublishingEditModeTests`** — 12 new tests verifying all 8 MessagePipe event types:
  - Bootstrap & broker smoke (7 event types publish/subscribe roundtrip + idempotency).
  - `ToolExecutionPolicy` → `MessagePipeToolCallEventPublisher` integration (success, fail, throw, not-found, batch).
  - `SmartToolCallingChatClient` end-to-end (non-streaming tool lifecycle).
  - Streaming/non-streaming parity (identical event counts and tool names).
  - VContainer child scope subscription (parent publishes, child receives all 8 types).
  - `ApplyAiGameCommand` roundtrip via VContainer.

#### Documentation
- `ARCHITECTURE.md` — updated MessagePipe boundary to describe `IToolCallEventPublisher` → `MessagePipeToolCallEventPublisher` adapter chain.
- `STREAMING_ARCHITECTURE.md` — updated file paths for `ToolExecutionPolicy` and `SmartToolCallingChatClient` (now in `CoreAI.Core`); updated notification row.
- `DEVELOPER_GUIDE.md` — updated assembly table, section 3.3 Tool Call Observability, **new section 3.4 Logging Architecture** (ILog vs IGameLogger), guide version 1.5.
- `TESTING_TOOL_CALLING.md` — added `MessagePipeEventPublishingEditModeTests`, new "Gotcha: dual logging system" section.
- `TOOL_CALL_SPEC.md` — engine-agnostic pattern updated for v1.5.0.
- `GameTemplateGuides/02_AiOrchestration.md` — updated for portable `LoggingLlmClientDecorator`, added tool-call lifecycle point.
- `GameTemplateGuides/03_AgentRolesAndProfiles.md` — debugging section updated for `ILog` / MessagePipe events.
- `CoreAI/CHANGELOG.md` — added v1.5.0 entry.

#### Fixes
- `ToolCallStreamingParityPlayModeTests.SpyLogger` — now implements both `IGameLogger` and `ILog`; sets `Log.Instance = spy` so `[ToolCall]` diagnostic lines are captured. Added `[TearDown]` to reset `Log.Instance`.
- `LoggingLlmClientDecoratorEditModeTests` — removed dead `AllOnSettings : IGameLogSettings` stub (leftover from pre-1.5.0).
- `CoreAI.Core/AssemblyInfo.cs` — added `InternalsVisibleTo("CoreAI.Tests")` for EditMode test access to `internal` helpers.
- `CoreAI.Tests.asmdef` — added `MessagePipe.VContainer` assembly reference.

### Meta

- Package **`1.5.0`**. Dependency **`com.neoxider.coreai 1.5.0`**.


## [1.4.1] - 2026-04-30

### 🐛 Fix: `IAgentMemoryStore` not propagated to HTTP clients

- **`LlmPipelineInstaller.BuildHttpClient`** — now accepts and forwards `IAgentMemoryStore` to `OpenAiChatLlmClient` and `ServerManagedLlmClient`.
- **Root cause:** `BuildHttpClient` was called without `memoryStore` in all HTTP execution modes (`ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, and `Auto` HTTP fallback). The memory tool's `AIFunction` was never bound in `MeaiLlmClient`, causing tool calls to `memory` to be silently stripped from streaming output.
- **Impact:** `memory` tool now works correctly in all LLM execution modes, not just `LocalModel`.
- **`TryResolveHttpApiClient`** — also updated to propagate `memoryStore`.
- **Test:** `LlmPipelineInstallerEditModeTests.BuildHttpClient_PassesMemoryStore_ToOpenAiChatLlmClient`.

## [1.4.0] - 2026-04-30

### 🛡️ Resilience: HTTP retry with Retry-After + exponential backoff

Production-grade HTTP retry at the transport layer, independent of the tool-calling retry loop.

- **`MeaiOpenAiChatClient.BuildHttpException`** — parses `Retry-After-Ms` header (millisecond precision, used by Azure / LiteLLM) with priority over `Retry-After` (seconds). Both convert to `LlmClientException.RetryAfterSeconds`.
- **`LoggingLlmClientDecorator`** — new retry loop for `RateLimited` (429) and `BackendUnavailable` (5xx):
  - Attempts: `settings.MaxLlmRequestRetries` (injected via `LlmPipelineInstaller`).
  - Delay: server `Retry-After` header → exponential backoff `2s → 4s → 8s → 16s → 30s` (capped).
  - Log: `LLM ↺ traceId=… | RateLimited — retry 1/2 after 30s`.
  - Tool-call errors are **not affected** — same immediate, count-based retry as before.
- **`LlmPipelineInstaller`** — passes `settings.MaxLlmRequestRetries` to `LoggingLlmClientDecorator`.

### 🔧 Resilience: TryRepairToolName — automatic tool name casing repair

Automatic tool name casing repair — model writes `MEMORY` instead of `memory`, system silently fixes it before execution.

- **`ToolExecutionPolicy.TryRepairToolName`** — case-insensitive lookup among registered `ILlmTool` names. Returns a new `FunctionCallContent` with the corrected name, or `null` if the tool is genuinely unknown.
- Called in `ExecuteSingleAsync` before `AIFunction` resolution — completely transparent to calling code.
- When no tools are registered (e.g. tools only in `ChatOptions`), skips repair and passes through.
- On unknown tool: structured error with available tool names for model self-correction.

### 🧪 Tests

**EditMode (12 new tests):**
- `TryRepairToolName_ExactMatch_ReturnsSameFc` — exact match passes through.
- `TryRepairToolName_WrongCase_ReturnsRepaired` — `MEMORY` → `memory`.
- `TryRepairToolName_MixedCase_ReturnsRepaired` — `Spawn_Quiz` → `spawn_quiz`.
- `TryRepairToolName_UnknownTool_ReturnsNull` — genuinely unknown tool.
- `TryRepairToolName_NullFc_ReturnsNull` — null guard.
- `ExecuteSingle_WrongCaseName_IsRepaired` — end-to-end: `MEMORY` executes successfully.
- `ExecuteSingle_TrulyUnknownTool_ReturnsFailed` — error with available tool list.
- `ComputeBackoff_ExponentialCurve_CappedAt30` — backoff curve: `2→4→8→16→30`.
- `ToolCallInMiddleOfLongText_PrefixAndSuffixPreserved`.
- `CodeBlockFollowedByRealToolCall_OnlyRealCallExtracted`.
- `ToolCallWithArrayArguments_ExtractedCorrectly`.
- `CleanedText_IsTrimmable_NoLeadingTrailingJson`.

**PlayMode (3 new hybrid real-LLM tests — `ToolNameRepairPlayModeTests.cs`):**
- `WrongCasing_Repair_ToolExecuted_RealLlmContinues` — scripted `MEMORY` → repair → tool executes → real LLM responds.
- `UnknownTool_ErrorFedBack_RealLlmSelfCorrects` — scripted unknown tool → error in chat history → real LLM self-corrects.
- `MixedCaseWithTextPrefix_ToolRepaired_TextPreserved` — `"Working on it... {\"name\":\"Memory\",...}"` → repair + prefix preserved.

### Coverage matrix

All changes work across **every LLM mode**: `Auto`, `Local Model`, `Client Owned Api`, `Client Limited`, `Server Managed Api`. All modes delegate internally to `MeaiLlmClient` → `SmartToolCallingChatClient` → `ToolExecutionPolicy`.

### Meta

- Package **`1.4.0`**. Dependency **`com.neoxider.coreai 1.4.0`** (bumped — adds `TryRepairToolName`, retry backoff).


### Tool-calling test coverage: chain, parallel, native+text, fail/success reset

Adds the scenarios that the 1.3.0 fix did not pin down explicitly. Code paths from 1.3.0 are unchanged — these tests guard the **edges** so future regressions in any one of them surface fast.

- **`ToolCallExtractionParityEditModeTests.NonStreaming_ChainOfTwoToolsThenText_ExecutesBothAndStripsAll`** — model emits `tool_a → tool_b → "Done."` across three iterations (text-shape JSON each time). Both tools execute exactly once, both traces captured in order, the final assistant text contains `"Done."` and **neither** tool's JSON.
- **`ToolCallExtractionParityEditModeTests.NonStreaming_TwoParallelToolCalls_BothExecuteInSameIteration`** — single response returns two native `FunctionCallContent` items. `ToolExecutionPolicy.ExecuteBatchAsync` runs both; trace list has both with `source=native`; loop terminates after the second iteration's text reply.
- **`ToolCallExtractionParityEditModeTests.NonStreaming_NativeToolCallWithTextPrefix_NativeWins_TextNotLeaked`** — response has both a `TextContent` containing pseudo-JSON and a real `FunctionCallContent`. Native path takes priority (text-extraction is gated on `nativeCalls.Count == 0`), so no phantom call is invented from the prefix.
- **`ToolCallExtractionParityEditModeTests.Streaming_FailureThenSuccess_ResetsConsecutiveErrorsAndContinues`** — flaky tool fails on iteration 1, succeeds on iteration 2 (different args), text on iteration 3. Confirms `ToolExecutionPolicy.RecordSuccess()` resets the counter so the third turn doesn't trip max-errors. Final `IsDone` chunk carries `[fail, success]` traces and `Error == null`.

### Meta

- Package **`1.3.1`**. Dependency **`com.neoxider.coreai 1.3.0`** (unchanged — this is a tests-only release).

## [1.3.0] - 2026-04-30

### Tool calling: stream/non-stream parity + diagnostics

Unifies the tool-calling cycle so providers emitting tool calls as **JSON-in-text** (Ollama, llama.cpp, LM Studio, some Qwen builds) behave identically across streaming and non-streaming paths. Production symptom: `memory` tool emitted as text after a goodbye line, JSON leaked into the chat panel, no persistence happened.

- **`SmartToolCallingChatClient.GetResponseAsync`** — non-streaming loop now also scans every `MEAI.TextContent` in the response for tool-call JSON and feeds the resulting `FunctionCallContent` through the same `ToolExecutionPolicy` path as native calls. The cleaned text replaces the raw assistant text on the next iteration so the model does not see its own JSON twice. Public read-only **`LastExecutedToolCalls`** mirrors what the streaming path returns.
- **`MeaiLlmClient.CompleteStreamingAsync`** — extraction gate switched from "AIFunction count > 0" to "request.Tools count > 0". When a tool was *requested* but no AIFunction was bound (e.g., `MemoryLlmTool` with `IAgentMemoryStore == null`), the loop now strips the JSON, logs a warning, and emits cleaned text instead of leaking the raw tool call. A startup warning fires once when this mismatch is detected.
- **`MeaiLlmClient.BuildAIFunctions`** — now logs a warning when `MemoryLlmTool` is requested for a role but `_memoryStore` is `null`, instead of silently dropping it.
- **`AiOrchestrator`** (defense-in-depth) — both sync and streaming paths now run **`LlmToolCallTextExtractor.StripForDisplay`** on the assistant text before persisting to chat history or publishing **`ApplyAiGameCommand`**. Logs `tool-call JSON leaked through extraction; stripped for chat/envelope` if the strip changed anything.

### Diagnostics: per-call log line + tail summary

- **`ToolExecutionPolicy`** — emits a dedicated `[ToolCall]` log line after every tool invocation (native, text-extracted, missing, duplicate). Format: `[ToolCall] traceId=… role=… tool=memory status=OK dur=12ms args={…} result=…`. Honours **`ICoreAISettings.LogToolCalls`** / `LogToolCallArguments` / `LogToolCallResults` independently of the verbose `LogMeaiToolCallingSteps` switch.
- **`LlmToolCallTrace`** (new portable struct in `CoreAI.Ai`) — one `(name, success, durationMs, source)` record per call. Source is one of `native` / `text` / `duplicate` / `missing`.
- **`LlmCompletionResult.ExecutedToolCalls`** / **`LlmStreamChunk.ExecutedToolCalls`** — same trace list, populated for both paths (final chunk only on streaming).
- **`LoggingLlmClientDecorator`** — appends `tools=[memory(ok,12ms),other(fail,0ms,duplicate)]` to the final `LLM ◀` line. Empty tail when no tools fired so plain text turns stay one-line.

### Portable extractor

- **`CoreAI.Ai.LlmToolCallTextExtractor`** (new in `com.neoxider.coreai 1.3.0`) — engine-agnostic `TryExtract` / `StripForDisplay`, available to anything that depends on the portable core. Existing **`MeaiLlmClient.TryExtractToolCallsFromText`** / `StripEmbeddedToolCallJsonForDisplay` keep their public surface for backward compatibility.

### Tests

- **EditMode:** `ToolCallExtractionParityEditModeTests` — non-streaming text-shaped tool execution + strip; missing-tool synthetic trace; streaming JSON-strip when AIFunction not bound; pass-through when no tools requested; `FormatExecutedTools` rendering; portable-extractor multi-match.

### Meta

- Package **`1.3.0`**. Dependency **`com.neoxider.coreai 1.3.0`** (bumped — adds `LlmToolCallTextExtractor` and `LlmToolCallTrace` / `ExecutedToolCalls`).

## [1.2.6] - 2026-04-30

### Composition: `GlobalMessagePipe` in minimal PlayMode fixtures

- **`GlobalMessagePipeMinimalBootstrap.EnsureInitializedForLlmDiagnostics`** — registers MessagePipe brokers for `LlmRequestStarted` / `LlmRequestCompleted` / `LlmUsageReported` / `LlmToolCallStarted` / `LlmToolCallCompleted` / `LlmToolCallFailed` / `LlmBackendSelected` and calls **`GlobalMessagePipe.SetProvider`** when no provider exists yet. **`ToolExecutionPolicy`** otherwise skips publishing tool-call events (`GlobalMessagePipe.IsInitialized` guard).
- **`TestAgentSetup.Initialize`** — invokes the bootstrap at start so PlayMode tests without `CoreAILifetimeScope` can subscribe to **`GlobalMessagePipe.GetSubscriber<LlmToolCallCompleted>()`** and observe real tool traffic.
- **`TestAgentSetup` orchestrator** — uses **`CoreAISettingsAsset.Instance`** (when present) as **`ICoreAISettings`** for `AiOrchestrator` so timeouts and logging flags match the HTTP/MEAI client settings.
- **PlayMode:** `AgentMemoryOpenAiApiPlayModeTests` — verbose LLM logging toggle, explicit `AgentMemoryState.Memory` assertions (non-empty write, append preserves baseline + marker, clear removes row), orchestrator reply logging.
- **EditMode:** `GlobalMessagePipeMinimalBootstrapEditModeTests` — idempotent bootstrap + publish/subscribe smoke for `LlmToolCallCompleted`.
- **Docs:** `ARCHITECTURE.md`, `DEVELOPER_GUIDE.md` — note bootstrap + PlayMode `TestAgentSetup` behaviour.
- Package **`1.2.6`**. Dependency **`com.neoxider.coreai 1.2.1`** (unchanged).

## [1.2.5] - 2026-04-30

### Chat: hide leaked tool-call JSON in assistant bubble (LLMUnity / text-shaped tools)

- **`MeaiLlmClient.TryExtractToolCallsFromText`** — second pass runs **`FindToolCallJsonSpans`** on **raw** assistant text when the first pass (which ignores brace characters inside fenced `` ``` `` blocks ) finds nothing, so JSON wrapped as `` ```json ... ``` `` is discoverable again and tools still execute in-stream.
- **`MeaiLlmClient.StripEmbeddedToolCallJsonForDisplay`** — host UI can strip any remaining leaked JSON using the same rules (no tool execution).

### Meta

- Package **`1.2.5`**. Dependency **`com.neoxider.coreai 1.2.1`** (unchanged).

## [1.2.4] - 2026-04-29

### Docs + tests: custom chat roles and `ToolsOnly`

- **`README_CHAT.md`** — section *Custom roles — not locked to “one persona”* (`CoreAiChatConfig.RoleId`, registering multiple roles, **`AgentMode.ToolsOnly`** expectations, host-only `BuildAiTaskRequest` policy, EN + RU `<details>` summary). Cross-links to tool policy and streaming/tool sections.
- **EditMode:** `CoreAiChatPanelBuildRequestEditModeTests` — default `BuildAiTaskRequest` shape (`RoleId`, `Hint`, `SourceTag`, `AllowedToolNames` null) and subclass allowlist injection.
- **PlayMode:** `CoreAiChatPanelBuildRequestPlayModeTests` — same checks in a player frame (no LLM; complements EditMode for lifecycle/domain differences).
- Package **`1.2.4`**. Dependency **`com.neoxider.coreai 1.2.1`** (unchanged).

## [1.2.3] - 2026-04-29

### Docs: chat host hook for tool policy (`BuildAiTaskRequest`)

- **`CoreAiChatPanel.BuildAiTaskRequest(string, string)`** — clarified in xmldocs: default minimal `AiTaskRequest` (`RoleId` + `Hint` + `SourceTag=Chat`); hosts override to inject **tool policy** (`AllowedToolNames`, `ForcedToolMode`, `RequiredToolName`, etc.). The same override is used for **typed UI sends** and **`SubmitMessageFromExternalAsync`** (both build the request through this method).
- **`README_CHAT.md`** — new subsection *Custom `AiTaskRequest` (tool policy)* describing the override pattern and parity with streaming / orchestrator.
- **`IChatRequestConfigurator`** — xmldocs corrected: no longer reference non-existent `CoreAiChatExternalSubmitOptions.ConfigureRequest` or claim registration on `CoreAiChatPanel`; the interface remains a **preview** contract for future DI-style wiring; until then **`BuildAiTaskRequest`** is the supported extension point.
- Package **`1.2.3`**. Dependency **`com.neoxider.coreai 1.2.1`** (unchanged).

## [1.2.2] - 2026-04-29

### Streaming parity + `AllowedToolNames` empty = no tools

- **`CoreAi.StreamChunksAsync(AiTaskRequest, CancellationToken)`** — forwards to `CoreAiChatService.SendMessageStreamingAsync` so hosts pass `AllowedToolNames` / `ForcedToolMode` on streaming turns.
- Depends on **`com.neoxider.coreai 1.2.1`** (orchestrator: empty allowlist strips tools; see Core CHANGELOG).
- **EditMode:** `AiOrchestratorHistoryEditModeTests` — empty allowlist + sync vs streaming tool parity; `CoreServicesInstallerEditModeTests` — TearDown no longer calls `SetProvider(null)`.

## [1.2.1] - 2026-04-29

### WebGL packaging + DI regression test

- **UPM `link.xml`** at package root `Assets/CoreAiUnity/link.xml` (the monorepo file `Assets/link.xml` is **not** inside `path=Assets/CoreAiUnity`, so consumers need the copy in the package folder).
- **EditMode:** `CoreServicesInstallerEditModeTests.RegisterCore_Builds_AndResolves_IAiGameCommandSink_As_MessagePipeSink` — guards `RegisterCore` + factory-registered `IAiGameCommandSink` against VContainer constructor-analysis failures on IL2CPP.
- **Docs:** WebGL / IL2CPP note in `DEVELOPER_GUIDE.md` §2.1.
- Package **`1.2.1`**. Dependency **`com.neoxider.coreai 1.2.0`** (unchanged).

## [1.2.0] - 2026-04-29

### WebGL / IL2CPP DI

- **`MessagePipeAiCommandSink`** — registered via an explicit factory in `CoreServicesInstaller` so VContainer does not rely on constructor metadata analysis (fixes `VContainerException: Type does not found injectable constructor` on WebGL builds). `[Preserve]` on the sink and `link.xml` entry for `CoreAI.Source` avoid managed-code stripping edge cases.

### RedoSchool orchestration support

- Added Unity DI registration for the default tool-call history and no-op agent trace sink.
- Added EditMode coverage for per-role runtime context, allowed tool filtering, chat-only tool suppression, scripted LLM responses, structured tool result envelopes, and tool-call history.
- Package **`1.2.0`**. Dependency **`com.neoxider.coreai 1.2.0`**.

## [1.1.0] - 2026-04-29

### Portable LLM routing adapter

- ✨ **Manifest to core route table** — `LlmRoutingManifest` now converts profiles and rules into portable `LlmRouteTable`.
- 🔧 **Registry uses core resolver** — `LlmClientRegistry` keeps Unity-specific client construction, but route matching now goes through `CoreAI.Core` `LlmRouteResolver`.
- ✨ **Production policy surface** — Unity can now build on core entitlement, usage, auth context, and provider error contracts while keeping ScriptableObjects, VContainer, HTTP/SSE, and LLMUnity in the Unity package.
- 🧪 **EditMode coverage:** added route resolver priority, route table validation, manifest conversion, provider error mapping, and usage aggregation tests.
- 🔧 Package **`1.1.0`**. Dependency **`com.neoxider.coreai 1.1.0`**.

## [1.0.3] - 2026-04-29

### Chat UX and HTTP model selection

- 🐛 **Stop button availability** — chat Stop is now available for any active request, including non-streaming requests and the tail after the final streaming chunk.
- 🔧 **Enter/Shift+Enter default** — new chat configs use `Enter` to send and `Shift+Enter` for a newline. Legacy Shift+Enter-to-send remains available through `CoreAiChatConfig.SendOnShiftEnter`.
- ✨ **HTTP model presets** — `CoreAISettingsAssetEditor` keeps the free-form model field and adds a preset dropdown for common OpenAI-compatible model ids.
- 🧪 **EditMode coverage:** updated chat config defaults and added hotkey contract regressions.
- 📝 **Docs:** chat README updated for the new send/newline behavior.
- 🔧 Package **`1.0.3`**. Dependency **`com.neoxider.coreai 1.0.3`**.

## [1.0.2] - 2026-04-28

### Long context and tool-call identity

- ✨ **Context compaction in orchestration** — `AiOrchestrator` now asks the portable context manager to prepare chat history. Recent turns stay in `ChatHistory`; older turns become a `## Conversation Summary` system section when the token budget is tight.
- ✨ **Tool lifecycle identity** — `ToolExecutionPolicy` publishes `LlmToolCallInfo` with `CallId` for start/completed/failed events, making async and parallel diagnostics correlate to the exact provider tool call.
- 🧪 **EditMode coverage:** added regressions for deterministic context summary behavior and awaited async tool execution.
- 📝 **Docs:** architecture and developer guide updated for context management and tool-call event identity.
- 🔧 Package **`1.0.2`**. Dependency **`com.neoxider.coreai 1.0.2`**.

## [1.0.1] - 2026-04-28

### Production runtime extension points

- ✨ **ServerManagedApi production path** — added `ServerManagedLlmClient` and `ServerManagedAuthorization.SetProvider(...)` so WebGL and SaaS projects can call a backend proxy with dynamic user/session tokens while keeping provider keys off the client.
- ✨ **Usage and typed error propagation** — `RoutingLlmClient` now publishes `LlmUsageReported`, forwards typed `LlmErrorCode` values, and maps HTTP auth/quota/rate-limit/backend failures into stable categories.
- ✨ **Runtime prompt context and scoped memory** — Unity composition can consume the new Core contracts for per-request context and user/session/topic memory isolation.
- ✨ **Tool lifecycle observability** — Unity registers brokers and publishes tool start/completed/failed events around MEAI tool execution.
- ✨ **Production diagnostics** — `CoreAI/Validate Production Settings` and the settings inspector warn when WebGL is configured with `ClientOwnedApi` and a non-empty API key.
- 🧪 **EditMode coverage:** targeted production-extension run passed `12/12` for routing usage events, ServerManaged auth hook, scoped memory, and runtime prompt context.
- 📝 **Docs:** architecture, settings, developer guide, and changelogs updated for production extension points.
- 🔧 Package **`1.0.1`**. Dependency **`com.neoxider.coreai 1.0.1`**.

## [1.0.0] - 2026-04-28

### LLM execution modes and routing

- ✨ **Four public LLM modes** — `LocalModel`, `ClientOwnedApi`, `ClientLimited`, and `ServerManagedApi` are now first-class runtime concepts over the existing LLMUnity / OpenAI-compatible HTTP / Offline clients.
- ✨ **Single-mode and mixed-mode routing** — `CoreAISettingsAsset` configures a simple global mode, while `LlmRoutingManifest` profiles can keep several modes active at once for different roles in the same scene.
- ✨ **ClientLimited guard** — added local request and prompt-size limits through `ClientLimitedLlmClientDecorator`.
- ✨ **MessagePipe observability** — Unity registers brokers for `LlmBackendSelected`, `LlmRequestStarted`, and `LlmRequestCompleted`; `RoutingLlmClient` publishes routing diagnostics for UI subscribers.
- 🔧 **Editor UX** — `CoreAISettingsAssetEditor` exposes the public LLM mode field, single-mode vs routing-profile guidance, ClientLimited limit fields, and ServerManagedApi key-safety guidance.
- 🧪 **EditMode coverage:** focused tests for settings helpers, routing metadata/events, ClientLimited limits, and mixed-mode manifest resolution. Targeted run: 16/16 passed.
- 📝 **Docs:** architecture, settings, quick start, developer guide, docs index, chat README, package READMEs, and changelogs updated for the 1.0.0 mode surface.
- 🔧 Package **`1.0.0`**. Dependency **`com.neoxider.coreai 1.0.0`**.

## [0.25.14] - 2026-04-27

### CoreAiChatPanel (streaming, stop, history, display)

- 🐛 **Second message no longer cancels the first** — streaming stays “busy” until the full `RunStreamingAsync` enumerator completes (including orchestrator post-work after the last token). Enter no longer triggers the stop path while a turn is still finishing; stop remains on the send button (`X`) and Esc (when enabled).
- 🐛 **Per-turn request CTS** — avoids a race where the previous turn’s `finally` could dispose the active linked token for a new message.
- 🐛 **Persisted chat UI** — user rows saved as composer JSON (`hint`, `telemetry`, …) hydrate as the **`hint`** text instead of raw JSON.
- 🐛 **Assistant bubble layout** — leading whitespace/newlines from the model are trimmed for display so empty gaps do not appear above the first line.
- 🧪 **EditMode:** `FormatPersistedMessageForUi`, `NormalizeAssistantDisplayText` regressions.
- 📝 **`README_CHAT.md`** — documents send vs stop semantics, streaming completion, persisted `hint`, display trimming, and an in-editor screenshot (`chat-readme-example.png`) with the chat panel next to Unity Console (`[CoreAI] [Llm]`, MessagePipe).
- 🔧 Package **`0.25.14`**. Dependency **`com.neoxider.coreai 0.25.14`**.

## [0.25.13] - 2026-04-27

### MEAI compatibility tool binding

- 🐛 **`CompatibilityLlmTool` native argument binding** — the MEAI executor parameter is now named `ingredients`, matching the JSON schema. Valid model calls such as `{"ingredients":["Fire","Earth"]}` no longer fail before reaching the tool with a missing `ingredientsObj` argument.
- 🧪 **EditMode coverage:** added an `AIFunction.InvokeAsync` regression for `check_compatibility` using the public `ingredients` argument name.
- 🧪 **PlayMode stability:** `CoreAiChatServiceIntegrationPlayModeTests` now falls back to the returned task result when a streaming callback receives no text chunks, avoiding false failures on providers that emit only terminal chunks for short answers.
- 📝 **`MEAI_TOOL_CALLING.md`** — documents that .NET `AIFunction` parameter names must match `ILlmTool.ParametersSchema` property names.
- 🔧 Package **`0.25.13`**. Dependency **`com.neoxider.coreai 0.25.13`**.

## [0.25.12] - 2026-04-27

### Queue scheduling stability

- 🐛 **`QueuedAiOrchestrator` latest-wins scopes** — `CancellationScope` now cancels older active and pending work as soon as a newer task with the same scope is enqueued, including streaming tasks.
- 🐛 **Queue fairness and cancellation** — equal priorities are FIFO, streaming and non-streaming tasks share one effective priority order, and pending tasks observe external cancellation before they start.
- 🧪 **EditMode coverage:** added queue regressions for FIFO priority ties, pending scope supersession, pending external cancellation, pending stream supersession, `CancelTasks(scope)` for pending streams, and shared sync/stream priority.
- 📝 **`DEVELOPER_GUIDE.md`** — documents the queue contract: `MaxConcurrent`, `Priority`, `CancellationScope`, `CancelTasks(scope)`, and sync/stream scheduling.
- 🔧 Package **`0.25.12`**. Dependency **`com.neoxider.coreai 0.25.12`**.

## [0.25.11] - 2026-04-27

### Tool calling stability + world tool hardening

- ✨ **CoreAI tool contract prompt** — `AiOrchestrator` now injects a concise tool contract whenever a role has registered tools, so local models are nudged by the framework to call real tools with structured arguments instead of simulating them in prose.
- 🐛 **Structured retry keeps tool context** — structured-response retries preserve registered tools, chat history, forced tool mode, required tool name, and response-token budget.
- 🐛 **`WorldLlmTool` main-thread execution** — direct `world_command` tool calls now marshal `ICoreAiWorldCommandExecutor.TryExecute(...)` through `UniTask.SwitchToMainThread` instead of forcing `Task.Run`. This avoids ThreadPool execution for Unity-facing world executors and aligns direct tool calls with the MessagePipe router contract.
- 🔧 **`WorldLlmTool` tool contract hardening** — descriptions now explicitly require `targetName` for animation commands such as `list_animations`, and invalid/missing argument responses use one centralized valid-action list plus action-specific missing-parameter messages.
- 🧪 **EditMode coverage:** added regressions for orchestrator tool-contract injection, `WorldLlmTool` missing `targetName` feedback, and world executor thread handling.
- 📝 **`MEAI_TOOL_CALLING.md` / `WORLD_COMMANDS.md` / `DEVELOPER_GUIDE.md`** — documented the orchestrator-level tool contract, direct world-command main-thread execution, and beginner/pro MessagePipe extension points.
- 🔧 Package **`0.25.11`**. Dependency **`com.neoxider.coreai 0.25.11`**.

## [0.25.10] - 2026-04-27

### File-backed memory store + docs

- 🐛 **`FileAgentMemoryStore.ClearChatHistory`** — after dropping in-memory chat for a role, the internal “history loaded” flag is reset so the **same store instance** can call `GetChatHistory` / `AppendChatMessage` again without `KeyNotFoundException` (regression covered by **`FileAgentMemoryStoreEditModeTests.ClearChatHistory_SameStoreInstance_GetChatHistory_IsSafe`**).
- 📝 **`MemorySystem.md`** — notes that `RoleMemoryConfig` defaults treat persisted chat as off unless chat history is enabled or set explicitly (see **`com.neoxider.coreai` 0.25.10**).
- 📝 **`MEMORY_STORE_CUSTOM_BACKENDS.md`** — custom `IAgentMemoryStore` implementations should invalidate any per-role RAM cache when implementing `ClearChatHistory`, same contract as the reference file store.
- 🔧 Package **`0.25.10`**. Dependency **`com.neoxider.coreai 0.25.10`**.

## [0.25.9] - 2026-04-27

### Per-agent MaxOutputTokens + LLMUnity asmdef wiring helper

- ✨ **Per-agent output budget:** `AgentBuilder.WithMaxOutputTokens(int? tokens)` stores a role-level response token cap in `AgentMemoryPolicy.RoleMemoryConfig.MaxOutputTokens`. Orchestrator priority is now `AiTaskRequest.MaxOutputTokens` (per-call) → per-agent (`WithMaxOutputTokens`) → global `CoreAISettings.MaxTokens` → provider default.
- 🐛 **LLMUnity package detection:** all CoreAI asmdefs use the real UPM package name **`ai.undream.llm`** in `versionDefines` (`COREAI_HAS_LLMUNITY`). The assembly references remain `undream.llmunity.Runtime` / `.Editor`, which are the assembly names exposed by the package.
- ✨ **`CoreAISettingsAssetEditor` — LLMUnity status helper.** The LLMUnity foldout now reports whether package `ai.undream.llm` is installed and whether `COREAI_HAS_LLMUNITY` is active. If the package is installed but the define is missing, **Auto-fix asmdef wiring** updates the four CoreAI asmdefs and refreshes the AssetDatabase.
- 🧪 **EditMode:** added per-agent MaxOutputTokens priority tests in the orchestrator plumbing suite.
- 🔧 Package version **`0.25.9`**. Dependency `com.neoxider.coreai 0.25.9+`; package versions are aligned.

## [0.25.8] - 2026-04-27

### 🎛️ CoreAISettings inspector + unified MaxTokens fallback for both backends

- 🐛 **`CoreAISettingsAssetEditor` — GGUF model picker fixed.** Previously a stray `EditorGUILayout.TextField` rendered **below** the popup re-read `ggufPathProp.stringValue` and overwrote the popup's just-applied selection on the same frame (symptom: pick a model in the dropdown → it reverts to `[ Auto / Fallback ]` on next repaint). Refactored into `DrawGgufModelDropdown(SerializedProperty)`:
  - Popup lists `LLMManager.modelEntries` (already-downloaded GGUF files in LLMUnity Model Manager) plus a leading `[ Auto / Fallback ]` entry.
  - **`LLMManager.LoadFromDisk()`** is invoked on first paint (and via the new **↻** refresh button) so the popup is populated even when entries were not lazy-loaded yet.
  - **Browse…** opens `EditorUtility.OpenFilePanel` and writes the selected `.gguf` filename to the property.
  - Separate **Manual override** `DelayedTextField` for typing a filename by hand (applies on Enter / focus-loss, no longer races with the popup).
  - Empty Model Manager — informative `HelpBox` instead of an empty silent popup.
  - Without the LLMUnity package — graceful fallback to a plain `PropertyField` + `HelpBox`.
- ✨ **`Max Output Tokens` moved from HTTP-only section into General settings** with an explicit tooltip stating it now applies uniformly to both HTTP API and LLMUnity. Previously it was hidden under the HTTP API foldout, suggesting it was provider-specific — that was misleading once the field actually became consumed by both backends.
- ✨ **Unified `MaxTokens` for HTTP API and LLMUnity.** Previously `CoreAISettings.MaxTokens = 4096` was a read-only getter with **no consumer**: visible in the inspector, never applied to either backend (request stayed `null` → provider default). Now `MeaiLlmClient.ResolveMaxOutputTokens(perRequest)` back-fills `ChatOptions.MaxOutputTokens` from `ICoreAISettings.MaxTokens` (when positive) on **both** non-streaming (`CompleteAsync`) and streaming (`CompleteStreamingAsync`) paths. Both `MeaiOpenAiChatClient` (HTTP `req["max_tokens"]`) and `LlmUnityMeaiChatClient` (`_unityAgent.numPredict`) consume the same `ChatOptions` value, so behaviour is symmetric.
- ✨ **Per-call override via `AiTaskRequest.MaxOutputTokens` (`int?`)** — symmetric with `ForcedToolMode`/`RequiredToolName`. Forwarded by `AiOrchestrator` (`RunTaskAsync`, `RunStreamingAsync`, structured-retry) into `LlmCompletionRequest.MaxOutputTokens`. Application code can still call the LLM client directly with `LlmCompletionRequest.MaxOutputTokens` for finer control.
- 🔧 **Effective priority:** `LlmCompletionRequest.MaxOutputTokens` (per-request direct call) → `AiTaskRequest.MaxOutputTokens` (per-call via orchestrator) → `ICoreAISettings.MaxTokens` (global default in `CoreAISettings.asset`) → provider default. Set `MaxTokens = 0` in the asset to opt out of the global fallback.
- 🧪 **`MaxTokensFallbackEditModeTests`** — 4 new tests through `MeaiLlmClient` covering: settings-default fallback (non-streaming + streaming), per-request override wins, `MaxTokens=0` leaves provider default. Existing 552 EditMode tests continue to pass.
- 🔧 **TODO (next):** dual-backend at runtime (primary + secondary, per-role routing via existing `RoutingLlmClient` + `LlmRoutingManifest`). Captured in [`TODO.md`](../../TODO.md).
- 🔧 Package version **`0.25.8`**. Dependency `com.neoxider.coreai 0.25.4+` (new `MaxTokens` interface member with default-impl, new `AiTaskRequest.MaxOutputTokens`).

## [0.25.7] - 2026-04-27

### 🔧 Editor bootstrap + PlayMode resilience to HTTP 5xx

- 🔧 **`CoreAIBuildMenu`** — auto-creation of `CoreAISettings.asset` is deferred to **`EditorApplication.delayCall`**: avoid duplicating or overwriting the asset in the same frame as domain reload; if the file already exists on disk but import has not picked it up yet — **`ImportAsset(ForceSynchronousImport)`** instead of creating a new asset with defaults.
- 🧪 **PlayMode (real model):** `AgentMemoryWithRealModelPlayModeTests` — up to **3** recall attempts with **`WaitForSecondsRealtime(1s)`** between them; after retries, an empty response (HTTP 5xx from LM Studio, etc.) yields **`Assert.Ignore`** with a short hint instead of failing on an empty command sink; orchestrator unchanged.
- 📝 **`TROUBLESHOOTING.md`** — section **PlayMode: HTTP 500 from LM Studio / local API** (symptoms, cause, checklist).
- 🔧 Package version **`0.25.7`**.

## [0.25.6] - 2026-04-27

### 💬 Chat UI — stop during streaming and “fast” backends

- 🐛 **`CoreAiChatPanel`** — busy flag for UI send is set **before** the first `await`; streaming sets `_isStreaming` right after `Task.Yield()` (also reset in `finally` / on `Stop`) so the **X** button and `StopActiveGeneration` are not lost on stub / zero-delay backends; after cancel, **`FinishStreaming` + `HideTypingIndicator`** run.
- 🐛 **Stop button** — `TrySendInput()` handles the active request first, then the global lock; send stays enabled while generating because in that state it is the stop control.
- 📝 **Docs** — updated `README.md`, `CoreAiUnity/README.md`, `README_CHAT.md`, `DEVELOPER_GUIDE.md`, `STREAMING_ARCHITECTURE.md`, `DOCS_INDEX.md`.
- 🧪 **PlayMode:** `CoreAiChatPanelStopPlayModeTests` asserts active streaming/request cancels CTS and clears busy state via public `StopAgent()`.
- 🔧 Package version **`0.25.6`**.

## [0.25.5] - 2026-04-26

### 💬 Chat UI — header without duplicate Stop, programmatic API

- 🧹 **`CoreAiChat.uxml`** — removed **`coreai-chat-stop`** from the header: stopping generation remains the **send button** in **X** mode plus **Esc** (as in 0.22.0), no duplicate header control.
- ✨ **`CoreAiChatPanel.SubmitMessageFromExternalAsync(messageText, options, cancellationToken)`** — for code-driven flows (cutscenes, quests, world buttons): **`CoreAiChatExternalSubmitOptions.AppendUserMessageToChat`** (default `true`, user bubble), **`SimulatedAssistantReply`** — show assistant text **without calling the LLM**; returns final assistant text or `null` when the panel is busy / cancelled / empty text after **`OnMessageSending`**.
- ✨ Shared internal path **`RunAgentTurnAsync`** for UI and external submits; streaming / non-streaming return the final string to callers.
- 📝 **`README_CHAT.md`** — programmatic submit section; Stop section no longer references a header button.
- 🧪 **EditMode:** `CoreAiChatExternalSubmitOptionsEditModeTests` (option defaults); **`CoreAiChatConfigEditModeTests`** — **`LoadPersistedChatOnStartup`** / **`MaxPersistedMessagesForUi`**.
- 🔧 Package version **`0.25.5`**.

## [0.25.4] - 2026-04-26

### 💬 Chat UI — session restore on startup

- ✨ **`CoreAiChatPanel`** — after `OnEnable` and `InitService`, **`HydrateStartupMessagesFromStore()`** clears the message list and loads persisted history from **`IAgentMemoryStore`** for **`CoreAiChatConfig.RoleId`** when **`Load Persisted Chat On Startup`** is enabled (default **on**). If history is **non-empty**, the welcome line is **not** shown on top; if empty, **`Welcome Message`** shows as before.
- ✨ **`CoreAiChatConfig`** — **“Session / history”** section: **`Load Persisted Chat On Startup`**, **`Max Persisted Messages For Ui`** (0 = all messages from the store).
- ✨ **`CoreAiChatService.TryGetPersistedChatHistory`** — reads `ChatMessage[]` for UI/integrations without duplicating store access.
- 📝 **`README_CHAT.md`** — session restore section and conditions (persist chat in `AgentMemoryPolicy`, `FileAgentMemoryStore` path).
- 🔧 Package version **`0.25.4`**.
- 🐛 **`CoreAiChat.uss` + `ScrollToBottom`** — message list: **`justify-content: flex-end`** and **`min-height: 100%`** on the scroll content so short threads **stick to the bottom** (near the input) without the “welcome sat under the header then jumped down” effect on first message. Scroll-to-bottom runs **twice** on adjacent schedule ticks to account for `highValue` relayout.

### 📊 LLM — prompt budget logs (`LoggingLlmClientDecorator`)

- ✨ **`LLM ▶` / `LLM ◀`** lines include an expanded **`promptBudget`**: **system** split into total / **core** / **memory** (`## Memory` marker as in `AiOrchestrator`) / **tools** catalog estimate from `request.Tools`; **chat** (user payload); rough **estTok** and **words**; when the API returns **usage**, the same metrics appear in the suffix plus **`outWords≈`** for completions.

## [0.25.3] - 2026-04-26

### 💬 Chat UI — C / Esc hotkeys, global poll, UITK focus

- ✨ **`CoreAiChatPanel`** — while **collapsed** (FAB), **C** (Latin, no Ctrl/Cmd/Alt) opens the panel; while **expanded**, **Esc** stops active generation if a request/stream is running, otherwise **collapses** chat. **Esc** is handled at `TrickleDown` on the root (`OnRootKeyDown`) so `TextField` does not duplicate logic.
- ✨ **`Update()`** runs on **all** platforms: calls `PollChatToggleShortcuts()` (Legacy `Input.*`) when the UITK root has **no** focused element (`Root.focusController.focusedElement`) — works with character controls when UI is unfocused. On WebGL, `WebGLInput.captureAllKeyboardInput` is still cleared. Subclasses overriding **`Update()`** should call `base.Update()` first.
- 🐛 **UITK API compatibility:** use **`Root.focusController.focusedElement`** instead of `IPanel.focusedElement` (missing on some Unity versions).
- ✨ **App hook:** `protected virtual void OnCollapsedStateChanged(bool collapsed)` — invoked after each `SetCollapsed` (gameplay/cursor wiring stays in your subclass or DI).
- 📝 **`CoreAiChat.uxml`** — tooltips: “Open chat (C)”, “Collapse chat (Esc)”, “Clear history”; clear-history button uses **`*`** so it is not confused with chat hotkey **C**.
- 🧪 **Tests:** `CoreAiChatPanelEditModeTests` — `IsOpenChatHotkeyFromKeys` (C / modifiers / other keys).
- ⚙️ **`CoreAiChatConfig`** — **“Hotkeys”** options: enable/disable keyboard open while collapsed, **`OpenChatHotkey`** (`KeyCode`, default `C`), enable/disable **Esc** (stop generation / collapse). FAB tooltip and FAB letter refresh from config.
- ⚙️ **`CoreAiChatPanel` runtime API** — `SetRuntimeOpenChatKeyboardShortcutEnabled` / `SetRuntimeOpenChatHotkey` / `SetRuntimeEscapeChatShortcutsEnabled` (`null` = use config again), `ClearRuntimeHotkeyOverrides()`, **`Effective*`** properties for resolved behavior.

## [0.25.2] - 2026-04-26

### 💬 Chat UI — header emoji glyphs replaced with ASCII (empty WebGL buttons fix)

- 🐛 **`Runtime/Source/Features/Chat/UI/CoreAiChat.uxml`** — `coreai-chat-stop` now renders as `■` (Geometric Shapes U+25A0, present in LiberationSans / default TMP fallback) instead of `⏹` (Misc Technical U+23F9, missing from default WebGL fonts and drew as an empty rectangle).
- **Context:** shipped WebGL players do not load emoji fallbacks (Noto Color Emoji, etc.), so emoji-plane symbols (U+1F300–U+1FAFF, parts of U+2600–U+27BF, U+23F0–U+23FF) render as `□` or disappear inside round buttons. ASCII / Latin-1 / Geometric Shapes (U+2580–U+25FF) are present in the default font asset — hence the move to `■`.
- **Compatibility:** cosmetic only, no API changes. Projects overriding button text in custom UXML are unaffected. Tooltip unchanged (“Stop agent and generation”), so UX stays clear.
- **Known TODO (out of scope for 0.25.2):** on WebGL, `UnityWebRequest` does not deliver SSE incrementally, so `OpenAiChatLlmClient.CompleteStreamingAsync` can deliver one terminal chunk instead of a stream — streaming UI appears stuck (“no typed reply + endless typing animation”). Details and fix plan: [`Docs/STREAMING_WEBGL_TODO.md`](Docs/STREAMING_WEBGL_TODO.md). *Update (v1.6.0+ / v1.6.13):* use **`WebGlNativeStreaming`** + fetch jslib (default **on** for new settings assets). Legacy app workaround (force `CoreAiChatConfig.EnableStreaming = false` under `#if UNITY_WEBGL && !UNITY_EDITOR`, e.g. `RedoSchool/...`) only if you intentionally avoid the fetch bridge.

## [0.25.1] - 2026-04-26

### 💬 Chat UI — WebGL TextField focus persistence (“focus lasts one frame” fix)

- 🐛 **`CoreAiChatPanel.Update()` (WebGL only)** keeps `WebGLInput.captureAllKeyboardInput = false` every frame.
  - **Symptom:** in a WebGL build, clicking the chat `TextField` focuses for exactly one frame, then focus drops and typing fails. Not reproduced in the Editor.
  - **Cause:** the WebGL player periodically flips `captureAllKeyboardInput` back to `true` (JS keyboard handler re-attach on canvas focus return, scene switches, DOM input churn under UITK `TextField` focus). The previous one-shot `ConfigureWebGlKeyboardInput()` in `Awake()` ran only once at panel startup.
  - **Fix:** `CoreAiChatPanel` adds `protected virtual void Update()` under `#if UNITY_WEBGL && !UNITY_EDITOR` that compares `WebGLInput.captureAllKeyboardInput` to `false` and resets when it diverges. Cheap (one bool compare; write only on change); stripped in Editor / Standalone.
  - **Not restored:** the `FocusOutEvent` loop from 0.21.4 (it fought the caret — see 0.21.6). The Update watchdog targets the capture flag, not UITK focus itself.
  - **Override-friendly:** `protected virtual`; subclasses can extend (extra WebGL watchdogs) calling `base.Update()` first.

### 🎮 Input compatibility — Legacy Input Manager + new Input System Package

- 🐛 **`OrchestrationDashboard` crashed** with `Active Input Handling = Input System Package (New)`. Direct `Input.GetKeyDown()` throws `InvalidOperationException` when the legacy Input Manager is disabled, breaking the metrics panel every frame.
- ✅ **New helper `IsToggleKeyPressedThisFrame()`** wraps both stacks via `#if ENABLE_LEGACY_INPUT_MANAGER` / `#if ENABLE_INPUT_SYSTEM && COREAI_HAS_INPUT_SYSTEM`. With `Both`, both paths run (legacy first as fast path).
- ✅ **`CoreAI.Source.asmdef`** declares a soft dependency on `Unity.InputSystem` in `references` + `versionDefines` (`com.unity.inputsystem >= 1.0.0` → `COREAI_HAS_INPUT_SYSTEM`). If Input System is not installed, `using UnityEngine.InputSystem;` and all new-input code strip out cleanly.
- ✅ **`KeyCode → Key` mapping** via `ToInputSystemKey()` covers F1–F12, BackQuote, Tab, Escape, Enter, Space (dashboard use; unsupported keys return `Key.None` — extend on demand).
- ⚠️ **Compatibility:** legacy-only projects behave as in 0.25.0. New Input System projects should install `com.unity.inputsystem` (versionDefines enable the branch automatically).

## [0.25.0] - 2026-04-26

### Forced Tool Mode (provider tool_choice) — deterministic tool calls

- **`MeaiLlmClient.ApplyForcedToolMode`** maps the new `LlmToolChoiceMode` (introduced in `com.neoxider.coreai 0.25.0`) onto Microsoft.Extensions.AI `ChatOptions.ToolMode`:
  - `Auto` → provider default (model decides),
  - `RequireAny` → `ChatToolMode.RequireAny`,
  - `RequireSpecific` → `ChatToolMode.RequireSpecific(name)` (validated against the available `AIFunction` set; falls back to `RequireAny` with a warning if the named tool isn't present),
  - `None` → `ChatToolMode.None`.
- **Streaming + forced tools fixed for multi-round loops.** `MeaiLlmClient.CompleteStreamingAsync` applies the forced mode only on the **first** iteration; after each tool result is fed back to the model, options are cloned with `ChatToolMode.Auto` (`CloneOptionsWithAutoToolMode`), so the model can finalise with text instead of being pinned into an infinite tool-call loop.
- **Both code paths supported.** Forced tool mode flows through both `CompleteAsync` (non-streaming, via `SmartToolCallingChatClient`) and `CompleteStreamingAsync` (streaming, native + text-based tool extraction).
- **Tool-call JSON stays out of streaming text by default.** The existing native (SSE `delta.tool_calls`) and text-based extraction paths already strip tool-call JSON before yielding text chunks; `ForcedToolMode` does not change that.
- 🧪 **Tests:** new `ForcedToolModeEditModeTests` verify forced-mode mapping, RequireSpecific validation, and the per-iteration reset in streaming.
- **HTTP SSE `reasoning_content` (Qwen / LM Studio)** — `MeaiOpenAiChatClient.ExtractDeltaUpdate` applies deltas so reasoning chains in a separate field do not leak into visible `content`; `ParseResponse` is documented as “`message.content` only for assistant text”. EditMode: `MeaiOpenAiChatClientSseEditModeTests`; PlayMode: `Streaming_ThinkBlocks_StrippedFromResponse` timing aligned with `RequestTimeoutSeconds` plus margin.
- 🔧 Bumped package versions to `0.25.0`. Dependency: `com.neoxider.coreai 0.25.0+`.

## [0.24.2] - 2026-04-26

### HTTP error diagnostics & policy hardening

- **HTTP 400 response body logging** — `MeaiOpenAiChatClient` now includes the API's response body in error messages for both non-streaming and SSE paths. Previously, only the status code was logged (e.g., `HTTP/1.1 400 Bad Request`), making it impossible to diagnose *why* the API rejected a request. Now the full rejection reason (e.g., `model not found`, `invalid tool schema`) is visible in the log.
- **`ToolExecutionPolicy` safety normalization** — `maxConsecutiveErrors` is now clamped to `Math.Max(1, value)` in the constructor. Passing `0` or negative values previously made `IsMaxErrorsReached` immediately `true`, causing agents to abort before executing any tools.
- **Documentation refresh** — both root `README.md` and `Assets/CoreAiUnity/README.md` updated to v0.24.2 with vivid "imagine this" descriptions of the AI pipeline, accurate version badges, and production-ready framing.
- Bumped package versions to `0.24.2`.

## [0.24.1] - 2026-04-26

### SSE tool-call accumulation & UI stop fix

- **`SseToolCallAccumulator`** — new stateful accumulator in `MeaiOpenAiChatClient` that properly collects `delta.tool_calls` spread across multiple SSE chunks (cloud providers like OpenAI split `id`+`name` in chunk 1 and `arguments` fragments across chunks 2..N). Flushed at stream end into `FunctionCallContent`. Removes the "Partial SSE tool_calls" known limitation.
- **UI stop deduplication** — removed redundant `AddMessage("Stopped by user")` from `StopAgent()`. The `OperationCanceledException` handler in `SendToAI` already displays the stop message via `_stopRequestedByUser` flag, eliminating double feedback.
- **New PlayMode tests** — `StreamingToolCallingPlayModeTests`:
  - `Streaming_WithToolCapablePrompt_CompletesSuccessfully` — smoke test for full tool-capable pipeline.
  - `Streaming_EarlyCancellation_StopsCleanly` — validates clean cancellation mid-stream.
  - `Streaming_ThenNonStreaming_NoStateContamination` — verifies no state leaks between streaming and non-streaming modes.
- Updated `STREAMING_ARCHITECTURE.md` — removed "not yet implemented" known limitation for partial SSE accumulation, documented `SseToolCallAccumulator` lifecycle.
- Bumped package versions to `0.24.1`.

## [0.24.0] - 2026-04-26

### Streaming tool-calling hardening

- **`ToolExecutionPolicy`** — new shared class for tool execution guarantees (duplicate detection, consecutive error tracking, `CoreAi.NotifyToolExecuted`). Both streaming (`MeaiLlmClient`) and non-streaming (`SmartToolCallingChatClient`) paths now delegate to this single policy, eliminating behavior divergence.
- **Hardened `TryExtractToolCallsFromText`** — pattern-aware JSON parser replaces naive `firstBrace/lastBrace` approach:
  - Supports multiple tool calls in a single text response.
  - Ignores JSON inside fenced code blocks (` ```...``` `) to prevent false positives.
  - Only matches JSON objects containing both `"name"` and `"arguments"` keys.
  - Gracefully skips malformed/partial JSON.
- **Native SSE `delta.tool_calls`** — `MeaiOpenAiChatClient` now parses `choices[0].delta.tool_calls` from cloud providers (OpenAI, Anthropic via OpenRouter). Text-based extraction remains the primary fallback for local models (Ollama, llama.cpp, LM Studio).
- **Stop/Clear race fix** — unified `StopActiveGeneration()` with `_isStopping` reentrance guard; `StopAgent()` now delegates to the same internal path, eliminating potential double-stop from concurrent Escape + button click.
- **New tests:**
  - `ToolExecutionPolicyEditModeTests` — 14 tests: duplicate detection (global, per-tool, reset), error counter, batch execution, max errors, safety normalization.
  - `TryExtractToolCallsFromTextTests` — 11 tests: single/multi tool, code block protection, malformed JSON, nested braces, edge cases.
- Updated `STREAMING_ARCHITECTURE.md` — new §7 "Streaming tool-calling" documenting dual-path architecture and execution policy guarantees.
- Bumped package versions to `0.24.0` (`com.neoxider.coreaiunity` and dependency on `com.neoxider.coreai`).

## [0.23.3] - 2026-04-26

### Composition stability and streaming test coverage

- Fixed duplicate CoreAI bootstrap in `CoreAIGameEntryPoint`: repeated `Start()` calls are now idempotent and do not reinitialize `CoreAIAgent`.
- Added graceful duplicate-start warning in Composition logs to make accidental double-container startup visible.
- Added new EditMode tests for composition guard: `CoreAIGameEntryPointEditModeTests`.
- Expanded streaming + tool-calling tests in `MeaiLlmClientEditModeTests`:
  - keeps visible prefix text while suppressing tool JSON from UI;
  - terminates with explicit terminal error when tool-loop iteration limit is exceeded.
- Bumped package version to `0.23.3` and synced dependency to `com.neoxider.coreai` `0.23.3`.

## [0.23.2] - 2026-04-26

### Chat stop reliability

- Fixed non-stream HTTP cancellation in `MeaiOpenAiChatClient.GetResponseAsync`: when `Esc` or stop button cancels the active request, UnityWebRequest is now aborted immediately instead of waiting for full response timeout.

## [0.23.1] - 2026-04-26

### Packaging and release pin

- Bumped `com.neoxider.coreaiunity` to `0.23.1`.
- Pinned dependency `com.neoxider.coreai` to `0.23.1` to force package consumers to pick the build with streaming/tool-calling reliability fixes.

## [0.23.0] - 2026-04-26

### LLM Streaming + Tool Calling (single cycle)

- Added unified streaming tool-cycle in `MeaiLlmClient.CompleteStreamingAsync`: stream assistant output, detect tool-call JSON, execute tools, append tool result messages, continue generation in the same request flow.
- Tool-call JSON is now suppressed from chat UI during streaming; the user sees only human-readable assistant text.
- Added EditMode coverage in `MeaiLlmClientEditModeTests` for scenario: `streamed tool JSON -> tool execution -> continued streamed text`.
- Updated agent defaults for tool modes: `ToolsAndChat` and `ToolsOnly` now enable per-role streaming by default (can still be overridden with `AgentBuilder.WithStreaming(...)`).
- Strengthened HTTP streaming cancellation in `MeaiOpenAiChatClient`: active request is aborted both on token cancellation and on early enumerator disposal.
- Stabilized PlayMode tests: `Streaming_CancellationToken_StopsStream` uses fallback timed cancellation; `MemoryTool_AppendsMemory` now retries with strict tool-only prompt before failing.
- Added complex behavior scenario test in dedicated folder: `Tests/PlayModeTest/Scenarios/Complex/MerchantBehaviorChatWithToolsPlayModeTests.cs`.
- Updated package versions to `0.23.0` (`com.neoxider.coreaiunity` and dependency on `com.neoxider.coreai`).

## [0.22.0] - 2026-04-25

### ✨ Agent Control API — Full agent lifecycle control

A new level of control over agents: stop, clear memory, subscribe to tool invocations.

### 💬 Chat UI — stop generation from the UI

- 🛑 **Stop via the send button.** While the model is generating a reply, `coreai-chat-send` switches to stop mode (`■`) and calls `CoreAi.StopAgent(roleId)` plus cancellation of the active token.
- ⌨️ **Stop via `Esc`.** During active generation, pressing `Esc` in the chat stops the current request the same way as the button.
- 🎨 **Busy-state visual cue.** The send button gets a dedicated red style (`.coreai-chat-send-button-stop`) to make it clear it now acts as the stop control.
- 🧪 **EditMode tests.** Added `CoreAiChatPanelEditModeTests` (Escape detection and send/stop button state rendering).
- 📝 **Docs.** Updated `README_CHAT.md` and `DEVELOPER_GUIDE.md` (sections on stopping generation from the UI).

#### Stopping the agent (`CoreAi.StopAgent`)
- **`CoreAi.StopAgent(string cancellationScope)`** — atomically cancels all current and pending orchestrator tasks (`QueuedAiOrchestrator`).
- Cancels the `CancellationToken` for active generations and clears the internal queue for the given scope (usually `roleId`).
- Safe to call from any thread.

#### Clearing context (`CoreAi.ClearContext`)
- **`CoreAi.ClearContext(string roleId, bool clearChatHistory = true, bool clearLongTermMemory = true)`** — granular reset of agent memory.
- `clearChatHistory = true` — clears short-term chat history (session context).
- `clearLongTermMemory = true` — clears long-term memory (agent state/facts via `MemoryTool`).
- You can combine flags: chat only, long-term memory only, or both.

#### Subscribing to tools (`CoreAi.OnToolExecuted`)
- **`CoreAi.OnToolExecuted`** — global event fired when the model invokes a tool through the MEAI pipeline.
- Delegate: `ToolExecutedHandler(string roleId, string toolName, IDictionary<string, object?> arguments, object? result)`.
- Ideal for: playing sounds, triggering VFX, analytics, logging.
- Wrapped in `try/catch` — subscriber errors do not tear down the LLM pipeline.

#### Chat clear button in the UI
- 🆕 Added a **🗑** button to the `CoreAiChatPanel` header (on the right, before the collapse control).
- By default it clears UI messages and short-term chat history (`ClearChat()` → `CoreAi.ClearContext(roleId, true, false)`).
- For a full reset (chat + long-term memory), use `ClearChat(clearChatHistory: true, clearLongTermMemory: true)`.
- **`ClearChat(bool clearChatHistory, bool clearLongTermMemory)`** — new overload with granular control.

#### `SmartToolCallingChatClient` constructor
- Added a required `roleId` parameter — passed to `CoreAi.NotifyToolExecuted` on every tool invocation.
- ⚠️ **Breaking:** all direct `SmartToolCallingChatClient` constructions now require `roleId` before `maxConsecutiveErrors`.

### 📝 Documentation
- Updated `DEVELOPER_GUIDE.md`: new "Control API" section with code examples for `StopAgent`, `ClearContext`, and `OnToolExecuted`.

### 🧪 Tests
- New EditMode tests: `ClearContext_ClearsOnlyChatHistory`, `ClearContext_ClearsOnlyLongTermMemory`, `OnToolExecuted_FiresOnToolCall`.
- All `SmartToolCallingChatClient*` tests updated — `roleId` parameter added.
- Test `CancelTasks_SpecificScope_CancelsActiveAndPendingTasks` for the orchestrator.

## [0.21.9] - 2026-04-25

### ✨ Agent Control API (task cancellation and context clearing)

- Public methods were added to the core (via the `CoreAi` facade) to stop agent work and reset its memory.
- **`CoreAi.StopAgent(string cancellationScope)`**: Cancels the `CancellationToken` for all current generations and clears the `QueuedAiOrchestrator` internal queue for tasks with the given scope.
- **`CoreAi.ClearContext(string roleId)`**: Programmatically clears chat history (`IAgentMemoryStore.ClearChatHistory`) and internal memory (`MemoryTool`) for the specified agent role.
- Documentation updated (`DEVELOPER_GUIDE.md`).

## [0.21.8] - 2026-04-25

### 🔧 LLMUnity — automatic package detection (`COREAI_HAS_LLMUNITY`)

- 🐛 **Fixed compile error `CS0246: MeaiLlmUnityClient` in projects without LLMUnity.** Code depending on `undream.llmunity` was previously gated behind `#if !COREAI_NO_LLM`, which was never defined automatically. As a result, if LLMUnity was not installed (and `COREAI_NO_LLM` was not set manually), `LLMUnity` type references still compiled and then failed.
- ✨ **`versionDefines` in asmdefs.** All four assembly definition files (`CoreAI.Source`, `CoreAI.Editor`, `CoreAI.Tests`, `PlayModeTest`) now include a `versionDefines` block that defines `COREAI_HAS_LLMUNITY` automatically when the `ai.undream.llm` package is present in the project.
- ♻️ **Preprocessor guard refactor.** All LLMUnity-dependent `#if` conditions were changed from `!COREAI_NO_LLM && !UNITY_WEBGL` to `COREAI_HAS_LLMUNITY && !UNITY_WEBGL`:
  - `MeaiLlmUnityClient.cs`, `LlmUnityModelBootstrap.cs`, `LlmUnityMeaiChatClient.cs`, `LlmUnityAutoDisableIfNoModel.cs` — entire file wrapped.
  - `MeaiLlmClient.cs` — `using LLMUnity` and `CreateLlmUnity()` method.
  - `ILlmAgentProvider.cs` — `SceneLlmAgentProvider`.
  - `LlmPipelineInstaller.cs` — fallback when LLMUnity is absent → `StubLlmClient`.
  - `RoutingLlmClient.cs` — inner client type resolution.
  - `LlmClientRegistry.cs` — `LlmBackendKind.LlmUnity` case.
  - `CoreAISettingsAssetEditor.cs` — Editor-only `LLMManager` / `LLMAgent` checks.
  - `CoreAIBuildMenu.cs` — `TryCreateLlmUnityObjects`.
  - **PlayMode tests:** `AllToolCallsPlayModeTests`, `PlayModeLlmUnityTestHarness`, `PlayModeProductionLikeLlmFactory.LlmUnityWarmup`, `MeaiLlmClientPlayModeTests`, `SharedLlmUnity`, `PlayModeProductionLikeLlmTestSupport`, `LlmUnityGlobalSetup`, `TestAgentSetup`.
  - **EditMode tests:** `MeaiToolCallsEditModeTests` — `#if` guard for `LlmUnityMeaiChatClient.TryParseToolCallFromText`.
- 📝 **`COREAI_NO_LLM` remains** as a manual opt-out to disable all LLM functionality (HTTP + LLMUnity). `COREAI_HAS_LLMUNITY` is the new automatic environment detection.

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.21.8**

## [0.21.7] - 2026-04-23

### 💬 Chat UI — collapse to a floating action button (FAB)

- ✨ **Chat collapse.** The `CoreAiChatPanel` header now includes a `coreai-chat-collapse` (`—`) button. When collapsed, the container is hidden via the `.coreai-collapsed` class and a round floating `coreai-chat-fab` appears in the bottom-right corner — clicking it expands the chat again and returns focus to the `InputField`.
- 📱 **Auto-collapse on small screens.** On startup, when the screen is `≤ 720×560`, the chat starts collapsed by default so it does not cover the game world; the user opens it by tapping the FAB. User choice (collapsed vs expanded) is persisted in `PlayerPrefs` and overrides the default on later launches.
- 🧩 **API.** Public method `SetCollapsed(bool collapsed, bool persist = true)` plus `IsCollapsed` — for programmatic control from game code or gameplay (for example, collapse the chat during a cutscene).
- 🎨 **USS.** New classes: `.coreai-chat-header-btn` (round header button), `.coreai-chat-container.coreai-collapsed` (hidden state), `.coreai-chat-fab` / `.coreai-chat-fab-icon` (floating button + icon).
- 📦 **UXML.** Added `coreai-chat-collapse` (in the header) and `coreai-chat-fab` (on the root panel, default `display: none`).
- ✅ Linter clean; existing WebGL input-focus fixes from `0.21.6` preserved.

## [0.21.6] - 2026-04-23

### 💬 Chat UI — removed forced focus (WebGL caret flicker fix)

- 🐛 **Removed `PointerDown`/`PointerUp` force-focus on `InputField`.** Forcing focus to the inner `unity-text-input` on every click/tap in WebGL fought UI Toolkit’s own focus management. Focus bounced between the outer `TextField` and its inner editor every frame, which caused:
  - border flicker (focused/unfocused every frame),
  - missing caret (`|` invisible because re-focus reset cursor position),
  - broken combos like `Ctrl+A` (selection cleared),
  - dropped characters when typing fast.
- 🐛 **Removed the `FocusOutEvent` loop.** Auto-restore on any `FocusOut` created a loop: “focus left → force back → left again”. UITK now handles focus on clicks.
- ✅ **Kept only what WebGL needs:**
  - `WebGLInput.captureAllKeyboardInput = false` — Unity does not steal keyboard from the browser;
  - `SendButton.focusable = false` — send does not steal focus after click;
  - one-shot `InputField.Focus()` in `TrySendInput` / `SendToAI.finally` so you can type the next message immediately.
- ♻️ **`FocusInputField()` simplified** to plain `InputField.Focus()` — manual inner `unity-text-input` lookup was unnecessary and harmful.

## [0.21.4] - 2026-04-23

### 💬 Chat UI — WebGL input focus hardening

- 🐛 **`WebGLInput.captureAllKeyboardInput = false`** on chat `Awake`. In WebGL builds Unity defaults to capturing all keyboard events from the browser, so UITK `TextField` inside the runtime panel lost focus and “ate” characters.
- 🐛 **PointerDown/PointerUp on `InputField`:** force focus to the inner `unity-text-input` on any click/tap. Focus no longer sticks to the outer `TextField` composite.
- 🐛 **`FocusOutEvent` auto-restore:** if focus drops for a reason other than send (common for multiline `TextField` on WebGL), it returns on the next tick.
- 🐛 **No focus stealers:** message-history `ScrollView`, header title, and header icon are `focusable = false` so clicks do not pull focus off the `TextField`.

## [0.21.3] - 2026-04-23

### 💬 Chat UI — WebGL typing stability

- 🐛 **Fixed focus loss after send-button submit.** `CoreAiChatPanel.BindUI()` sets `SendButton.focusable = false` so keyboard focus does not stick on send and the next keystrokes go to the `TextField`.
- 🐛 **Stabilized typing the next message.** Explicit `FocusInputField()` plus non-focusable send removes the WebGL case where some characters failed after the first send.

## [0.21.2] - 2026-04-23

### 💬 Chat UI — input focus

- 🐛 **Focus returns to the text field after send.** `CoreAiChatPanel.TrySendInput` / `SendToAI.finally` now focus the inner `unity-text-input` (not the outer `TextField` shell). Previously, after the first send in multi-line mode focus stayed on the outer shell and keystrokes did not reach the editor until you clicked again.
- ✨ **`CoreAiChatPanel.FocusInputField()`** — private helper wrapping `TextField.textInputUssName` lookup. Used when clearing the field after send and in `SendToAI` `finally` so you can keep typing after the assistant finishes.

## [0.21.1] - 2026-04-23

### 💬 Chat UI polish & layout stability

- 💅 **ScrollView layout:** overlap/shrink fix — `ScrollView` shrinks correctly in the column (`min-height: 0`); header/input/typing no longer squash when content is huge (`flex-shrink: 0`).
- 💅 **Scrollbar theming (UI Toolkit / Unity 6):** explicit `Scroller` styles (`.unity-scroll-view__vertical-scroller`, `.unity-scroller__tracker`, `.unity-scroller__dragger`); arrow buttons hidden (`.unity-scroller__low-button` / `.unity-scroller__high-button`) so the default bright bar no longer bleeds through.
- 💅 **InputField readability:** stronger selectors for inner `TextField` classes across Unity versions; caret/selection colors tuned so player input stays readable on dark theme.
- 🔧 **Scroll bottom padding:** last bubble no longer hides under typing/input.

### ⏱️ Timeouts

- ⏱️ **`CoreAISettingsAsset.LlmRequestTimeoutSeconds`:** default raised from **15s → 120s** (streaming/tool-calling on local/slow models often needs more time).

### 🧪 Tests (PlayMode)

- 🧪 **`CraftingMemoryViaLlmUnityPlayModeTests`:** test no longer fails when the backend ran tool calls but never returned a final `AiEnvelope` — item name is recovered from memory (prompt contract).
- 🧪 **`CraftingMemoryViaOpenAiPlayModeTests`:** determinism check is now asserted (craft #4 must match craft #2), not only logged. Prompt memory uses canonical `Craft #N - Name made from X + Y` so the model can repeat by ingredients.
- 🧪 **`CraftingMemoryItemNameExtractor`:** more tolerant of free-form model text (quotes, “crafted with quality”, bold markdown, etc.).

## [0.21.0] - 2026-04-23

### 🎯 `CoreAi` singleton — unified one-line entry point

Previously, calling the LLM from game code meant knowing VContainer (`container.Resolve<CoreAiChatService>()`), rolling your own singleton, or calling `CoreAiChatService.TryCreateFromScene()` every time. Now there is one static class that covers the common paths.

- ✨ **`CoreAI.CoreAi`** (static facade, `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`) — lazy thread-safe singleton auto-resolved from the first `CoreAILifetimeScope` in the scene.
  - `CoreAi.TryGetChatService(out CoreAiChatService?)` / `CoreAi.TryGetOrchestrator(out IAiOrchestrationService?)` — **non-throwing**, handy for UI buttons and optional AI (unlike `Get*` which throws `InvalidOperationException`).
  - `CoreAi.AskAsync(message, roleId, ct)` → `Task<string>` — simple chat.
  - `CoreAi.StreamAsync(message, roleId, ct)` → `IAsyncEnumerable<string>` — streamed text chunks.
  - `CoreAi.StreamChunksAsync(message, roleId, ct)` → `IAsyncEnumerable<LlmStreamChunk>` — stream with metadata (`IsDone`, `Error`, usage).
  - `CoreAi.SmartAskAsync(message, roleId, onChunk, uiStreamingOverride, ct)` — chooses stream vs sync from the flag hierarchy, returns full text, invokes `onChunk` per fragment.
  - `CoreAi.OrchestrateAsync(AiTaskRequest, ct)` — full orchestrator pipeline (snapshot → prompt composer → authority → queue → structured policy → publish `ApplyAiGameCommand` → metrics).
  - `CoreAi.OrchestrateStreamAsync(AiTaskRequest, ct)` — streaming variant of the same pipeline.
  - `CoreAi.OrchestrateStreamCollectAsync(task, onChunk, ct)` — stream + accumulate full text + `onChunk`, returns `string`.
  - `CoreAi.IsReady` / `CoreAi.Invalidate()` / `CoreAi.GetChatService()` / `CoreAi.GetOrchestrator()` / `CoreAi.GetSettings()` — cache control and direct service access.
- ✨ **`QueuedAiOrchestrator.RunStreamingAsync`** — streaming through the orchestrator queue honoring `MaxConcurrent` and `CancellationScope`. Portable producer/consumer queue on `SemaphoreSlim + ConcurrentQueue` (no `System.Threading.Channels`, which is not available in this Unity build).

### 🧪 Tests

- ✅ **`AiOrchestratorStreamingEditModeTests`** (new file, 5 tests):
  - `DefaultFallback_EmitsSingleTextChunkThenDone` — default interface implementation yields exactly 2 chunks (text + terminal).
  - `DefaultFallback_EmptyResult_EmitsErrorChunk` — empty result → terminal with `Error="empty result"`.
  - `QueuedAiOrchestrator_Streaming_DelegatesRealChunks` — 4 deltas → 5 chunks (4 + terminal), not 1 (fallback).
  - `QueuedAiOrchestrator_Streaming_RespectsMaxConcurrent` — two parallel streams with `MaxConcurrent=1` both complete.
  - `QueuedAiOrchestrator_Streaming_ExternalCancellation_EmitsCancelledTerminal` — cancel mid-stream → terminal with `Error="cancelled"`.
- ✅ **`CoreAiFacadeEditModeTests`** (new file, 7 tests):
  - `IsReady_WithoutLifetimeScope_ReturnsFalse`.
  - `Invalidate_DoesNotThrow_WhenCalledMultipleTimes`.
  - `GetSettings_WithoutLifetimeScope_ReturnsNull`.
  - `GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation` — clear message.
  - `GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation`.
  - `TryGetChatService_WithoutLifetimeScope_ReturnsFalse` / `TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse`.

### 📚 Docs

- ✨ **`Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md`** — full facade reference: **beginner block** (3 steps + FAQ), **method cheat sheet**, **pro stack** (when to keep static vs DI), `TryGet*`, threading, extensions.
- 🔧 **`STREAMING_ARCHITECTURE.md`** — new §`6. Orchestrator streaming` comparing `CoreAiChatService.SendMessageStreamingAsync` vs `IAiOrchestrationService.RunStreamingAsync` (authority, structured validation, queue, publish command, metrics).
- 🔧 **`QUICK_START.md`** — “One-line alternative — `CoreAi` singleton” section.
- 🔧 **`DOCS_INDEX.md`** — link to `COREAI_SINGLETON_API` under Chat & Streaming.
- 🔧 **`README_CHAT.md`** — programmatic usage documented both via `CoreAi` (recommended) and direct `CoreAiChatService`.

## [0.20.3] - 2026-04-23

### 🐛 Chat panel & streaming hotfix

- 🐛 **Streaming was invisible in the UI (regression).** In the chain `LoggingLlmClientDecorator` → `RoutingLlmClient` → `OpenAiChatLlmClient` / `MeaiLlmUnityClient`, no link overrode `CompleteStreamingAsync`. The `ILlmClient` default fallback always ran: it called `CompleteAsync` and emitted **one** terminal chunk after generation finished — users saw “Typing…” then the full answer with no streaming effect.
  - **`OpenAiChatLlmClient.CompleteStreamingAsync`** → delegates to `MeaiLlmClient.CompleteStreamingAsync` (SSE via `UnityWebRequest`, `ThinkBlockStreamFilter`).
  - **`MeaiLlmUnityClient.CompleteStreamingAsync`** → delegates to `MeaiLlmClient.CompleteStreamingAsync` (LLMUnity callback → `ConcurrentQueue`).
  - **`RoutingLlmClient.CompleteStreamingAsync`** → picks inner client by `AgentRoleId` and forwards chunks with `await foreach`.
  - **`LoggingLlmClientDecorator.CompleteStreamingAsync`** → forwards chunks without buffering while appending to `StringBuilder` for the final log (`LLM ◀ (stream) … chunks=N | tokens … | content …`). `LlmRequestTimeoutSeconds` applies to the whole stream and becomes a terminal chunk `Error = "LLM stream timeout (Ns)"`.
- 🐛 **Shift+Enter in multi-line `TextField` did not send.** `KeyDownEvent` used the Bubble phase (default), so UITK’s multiline `TextField` consumed Enter as newline before our handler. Callback is now `TrickleDown.TrickleDown`, and key handling includes `KeyCode.KeypadEnter` and `character == '\n' | '\r'` for IME/keyboard mappings.

### 💅 Typing indicator

- 💅 **Typing animation is plain dots `...`** instead of a long “typing…” prefix. `CoreAiChatConfig.TypingIndicatorText` defaults empty; dots animate `. → .. → ... → .` every 400 ms with padded width so the bubble does not jump. Classic prefix text can be set in the Inspector if desired.

### 🧪 Tests

- ✅ **`LoggingLlmClientDecoratorEditModeTests`** extended:
  - `Streaming_DelegatesRealChunks_NotSingleShotFallback` — four real delta chunks from a mock yield five user-visible chunks (4 + terminal), not one fallback shot.
  - `Streaming_LogsStartAndFinish` — asserts log contains `LLM ▶ (stream)`, `LLM ◀ (stream)`, `chunks=2`, and `traceId`.
- ✅ **`RoutingLlmClientEditModeTests`** (new file, 3 tests):
  - `Streaming_RoutesToInnerClient_ForRole` — router picks the right client and hits the streaming path (not `CompleteAsync`).
  - `Streaming_UsesFallbackClient_ForUnknownRole` — unknown role → legacy fallback.
  - `Streaming_NullRequest_YieldsErrorChunk` — null request yields one terminal error chunk without breaking `IAsyncEnumerable`.

## [0.20.2] - 2026-04-23

### 🗨️ Chat & Streaming

- ✨ **Streaming works for both backends** — HTTP API (SSE) and LLMUnity (`LLMAgent.Chat(callback)` + `ConcurrentQueue` with delta diff under lock). Duplicate regex `<think>` filtering removed from `LlmUnityMeaiChatClient`; single `ThinkBlockStreamFilter` at `MeaiLlmClient`.
- ✨ **`CoreAISettingsAsset.enableStreaming`** — global Inspector toggle (“General settings”). Turn off to force non-streaming (debugging or backends without streaming).
- ✨ **`CoreAiChatService.IsStreamingEnabled(roleId, uiFallback)`** — effective flag from hierarchy: UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).
- ✨ **`CoreAiChatPanel`** honors all three layers; disabling any layer forces non-streaming for the panel.

### 🎬 Demo Scene

- ✨ **`CoreAI → Setup → Create Chat Demo Scene`** — new menu item; creates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with:
  - `Main Camera`, `Directional Light`, `EventSystem`;
  - `CoreAILifetimeScope` wired to `CoreAISettings`, `AgentPromptsManifest`, `LlmRoutingManifest`, `PrefabRegistry`, `GameLogSettings`;
  - `UIDocument` with `CoreAiChat.uxml` + `CoreAiChat.uss` and `PanelSettings` (1920×1080, ScaleWithScreenSize);
  - `CoreAiChatPanel` + demo asset `CoreAiChatConfig_Demo.asset`.
- ✨ **`CoreAI → Setup → Open Chat Demo Scene`** — opens the created scene in one click.

### 🧪 Tests

- ✅ **`ThinkBlockStreamFilterEditModeTests`** — full coverage of `CoreAI.Ai.ThinkBlockStreamFilter`: split tags (one char at a time), multiple blocks, `Flush()` / `Reset()`, case insensitivity, pseudo-tags (`<b>`, `2 < 3`), unclosed `<think>`, long reasoning (50+ chunks).
- ✅ **`CoreAiChatServiceEditModeTests`** — `IsStreamingEnabled` hierarchy (UI → per-agent → global) for both overloads (`uiFallback` / `uiOverride`), `SendMessageAsync` / `SendMessageStreamingAsync` / `SendMessageSmartAsync` with fake `ILlmClient`.
- ✅ **`CoreAiChatConfigEditModeTests`** — ScriptableObject defaults (including `EnableStreaming == true`).
- ✅ `CoreAISettingsAssetEditModeTests` — default `EnableStreaming` assertion added.
- ✅ **`SecureLuaSandboxEditModeTests`** — `SecureLuaEnvironment.StripRiskyGlobals` per removed global (`io`, `os`, `debug`, `load`, `loadfile`, `dofile`, `require`), plus `LuaExecutionGuard` (timeout / max steps / fast code / non-function argument) and `LuaCoroutineHandle` (Resume/Kill/budgetPerResume).
- ✅ **`LuaToolEditModeTests`** — `LuaTool.ExecuteAsync` (success, empty code, null code, executor throws, cancellation), `CreateAIFunction`, constructor null-arg validation, `LuaLlmTool` metadata (`Name`, `AllowDuplicates`, `Description`, `ParametersSchema`).
- ✅ `SmartToolCallingChatClientEditModeTests` — duplicate detection (`allowDuplicateToolCalls=false`), per-tool `AllowDuplicates=true`, `tool not found`, `tool throws exception`.
- ✅ `InGameLlmChatServiceEditModeTests` — rate limiter: window overflow, `maxRequestsPerWindow=0` (disabled), rejected request not stored, sliding window.

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.20.2**

## [0.20.1] - 2026-04-23

### 🐛 Streaming Fixes

- 🐛 **Fixed `Create can only be called from the main thread`** in `StreamingPlayModeTests`. `Streaming_ReturnsChunks_WithDoneFlag`, `Streaming_CancellationToken_StopsStream`, `Streaming_ThinkBlocks_StrippedFromResponse`, and `ThreeLayerPrompt_AllLayersApplied` used to wrap `await foreach` in `Task.Run`, so `UnityWebRequest` / `DownloadHandlerBuffer` were created on the thread pool and failed. Streaming and `CompleteAsync()` now run as async methods on the Unity main thread via `UnitySynchronizationContext`.
- 🐛 **Stream cancellation:** `MeaiOpenAiChatClient.GetStreamingResponseAsync()` now calls `webReq.Abort()` when `CancellationToken.IsCancellationRequested`, not only throwing `OperationCanceledException` (important for OpenRouter/remote HTTP where sessions may keep billing).
- 🔧 **`MeaiLlmClient.CompleteStreamingAsync()`** rewritten around stateful `ThinkBlockStreamFilter` (old per-chunk regex missed split `<think>` / `</think>` across SSE chunks). Guarantees a final `IsDone=true` chunk.
- 🔧 **`CoreAiChatPanel`** — local state machine replaced with shared `ThinkBlockStreamFilter` (DRY between UI and LLM layers).

### Tests

- ✅ All four `StreamingPlayModeTests` pass (previously all four failed).
- ✅ 27 EditMode tests (`ThinkBlockFilterEditModeTests` + `StreamingAndPromptsEditModeTests`) pass.

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.20.1**

## [0.20.0] - 2026-04-23

### 🗨️ Universal Chat Module (NEW)
- ✨ **`CoreAiChatConfig`** — ScriptableObject chat settings in Inspector (`Assets → Create → CoreAI → Chat Config`): roleId, title, welcome, icons, streaming on/off, sizes, input limits.
- ✨ **`CoreAiChatPanel`** — MonoBehaviour + UI Toolkit controller: works out of the box, streaming + non-streaming, think-block filtering, virtual hooks (`OnMessageSending`, `OnResponseReceived`, `FormatResponseText`, `CreateMessageBubble`).
- ✨ **`CoreAiChatService`** — chat without UI: streaming, history, 3-layer prompts. `TryCreateFromScene()` for DI resolve.
- ✨ **UXML/USS template** — `CoreAiChat.uxml` + `CoreAiChat.uss` dark theme, `coreai-` class prefix.
- ✨ **Think-block filtering** — streaming state machine hides `<think>...</think>`; typing indicator while the model “thinks”.
- 📚 **`README_CHAT.md`** — quick start, extension, programmatic API, custom styles.

### Streaming API
- ✨ **Real SSE streaming in `MeaiOpenAiChatClient`** — `stream: true`, parsing `data:` lines and `delta.content` chunks.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — streams via `IChatClient.GetStreamingResponseAsync()` with automatic `<think>` filtering.
- 🔧 **DRY `MeaiOpenAiChatClient`** — `BuildMessagesPayload()` and `BuildToolsPayload()` shared by `GetResponseAsync` and `GetStreamingResponseAsync`.

### 3-Layer Prompt Architecture
- 🔧 **`AiPromptComposer`** — constructor extended with `AgentMemoryPolicy` and `ICoreAISettings` for 3-layer prompt build.

### Tests
- 🧪 **EditMode**: `StreamingAndPromptsEditModeTests` (13 tests: 3-layer composition, AgentMemoryPolicy, LlmStreamChunk, default streaming fallback).
- 🧪 **EditMode**: `ThinkBlockFilterEditModeTests` (10 tests: regex + state machine for `<think>` blocks).
- 🧪 **PlayMode**: `StreamingPlayModeTests` (4 tests: streaming chunks, cancellation, think-block stripping, 3-layer prompt with a real LLM).

### Dependencies
- Bumped dependency on `com.neoxider.coreai` to **0.20.0**

## [0.19.1] - 2026-04-14

### Fixes & Stability

- 🐛 **Duplicate tool-call protection:** clarified how `MeaiLlmClient` resets failed-call counters within a session. Per-request `executedSignatures` fully isolates each call.
- 🔧 **Test harness `Agent.cs`:**
  - Test phrases exposed in the Inspector `[TextArea]` for live scenario tweaks and to avoid the LLM looping on identical prompts.
  - Added `ClearMemory()` to deliberately clear history (reset bot context between button presses so the model does not anchor on prior mistakes).
- 📝 **Docs:** clarified `SceneLlmAgentProvider` with `DontDestroyOnLoad` — you need an `LLMAgent` component in the scene or a registered `LlmUnityAgentName`.

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.19.1**

## [0.19.0] - 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — ingredient compatibility checks (2/3/4+ item rules, groups, custom validators)
- ✨ **`CompatibilityLlmTool`** — `ILlmTool` wrapper for function calling
- ✨ **`JsonSchemaValidator`** — validates JSON from the LLM (types, ranges, enums)
- 🧪 **45+ EditMode tests** (`CompatibilityAndSchemaEditModeTests.cs`)
- 🧪 **3 PlayMode tests** (`CompatibilityToolPlayModeTests.cs`) with a real LLM

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.19.0**

## [0.18.0] - 2026-04-10

### Architecture — LifetimeScope Decomposition & DI Cleanup

- 🔧 **`CoreAILifetimeScope.Configure()`** — split from 200+ lines into modular installers:
  - `LlmPipelineInstaller` — LLM clients, routing, logging decorator, orchestrator metrics.
  - `WorldCommandsInstaller` — Lua bindings, prefab registry, world executor, game config store.
  - `Configure()` is now ~40 lines with clear sections.
- ✨ **`ILlmAgentProvider` / `SceneLlmAgentProvider`** — abstraction for resolving `LLMAgent` with lazy caching. Removed `FindFirstObjectByType<LLMAgent>` from the DI composition root.
- 🔧 **`CoreAISettings.Instance = settings`** — replaces the 17-line `SyncToStaticSettings()` block. The static `CoreAISettings` proxy now delegates to the DI instance automatically.
- ❌ **`SyncToStaticSettings()`** — removed (replaced by `CoreAISettings.Instance = settings`).
- 🧪 **Tests**:
  - `CoreAISettingsSyncEditModeTests` — rewritten for `Instance` delegation (4 tests instead of 1).
  - `LuaAiEnvelopeProcessorEditModeTests` — cleanup updated via `ResetOverrides()`.

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.18.0**

## [0.16.0] - 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — runtime scene inspection tool. Lets the LLM search/analyze hierarchy and adjust `Transform` on `GameObject`s safely on the main thread via UniTask.
- ✨ **`CameraLlmTool`** — vision tool for PlayMode screenshots (`capture_camera`) returning a Base64 JPEG `dataUri`.
- 🛠 **`CoreAiPrefabRegistryAsset` automation** — `OnValidate` fills `Key` from AssetDatabase GUID and syncs `Name` when prefabs are assigned in the Inspector.

## [0.15.0] - 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON extraction** — rewrote tool-call parsing in `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Fragile regex removed; flexible brace scanning (`IndexOf('{')`).
- ⚙️ **Reasoning-mode stripping** — preprocess responses before tool parsing: strips `<think>...</think>` chains so JSON parsing does not break on “thinking aloud” (DeepSeek/Qwen).

### Editor UX
- ✨ **Auto asset bootstrap** — `[InitializeOnLoadMethod]` in `CoreAIBuildMenu` ensures required `ScriptableObject` assets exist when the project loads.
- ✨ **Quick Settings menu** — **CoreAI → Settings** jumps to the global `CoreAISettings.asset`.

## [0.13.0] - 2026-04-09

### Action / Event System
- ✨ `DelegateLlmTool`, `CoreAiEvents`, and `AgentBuilder` extensions (via `com.neoxider.coreai 0.13.0`).
- 📝 Updated `TOOL_CALL_SPEC.md` and `AGENT_BUILDER.md` with examples and trigger prompting.
- 🧪 **EditMode tests** for `CoreAiEvents` and `AgentBuilder.WithAction`.
- 🧪 **PlayMode test** `CustomAgentsPlayModeTests.CustomAgent_Helper_WithAction` for `DelegateLlmTool`.

## [0.12.0] - 2026-04-08

### Unified Logger (`ILog`)

- 🔧 **UnityLog** — `ILog` implementation from CoreAI.Core; maps `LogTag` → `GameLogFeature`
- 🔧 **CoreServicesInstaller** — registers `ILog` (`UnityLog`) as DI singleton and sets `Log.Instance`
- 🔧 **GameLoggerUnscopedFallback** — automatic `Log.Instance` fallback before DI init
- 🔧 **CoreAIGameEntryPoint** — migrated from `IGameLogger` to `ILog`
- 🔧 **WorldTool** — logging migrated to `ILog` with `LogTag.World`
- ❌ Removed manual `Log.Instance` wiring from `CoreAILifetimeScope`
- 🔧 **`MemoryToolAction` unification** (Core 0.12.0) — single enum; `AgentBuilder.WithMemory()` applies correctly via `policy.ConfigureRole()`.
- ℹ️ `IGameLogger` kept as an internal Unity-layer interface (`FilteringGameLogger`, `GameLogSettingsAsset` unchanged)

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.12.0**

---

## [0.11.0] - 2026-04-07

### Universal System Prompt Prefix

- ✨ **`CoreAISettingsAsset.universalSystemPromptPrefix`** — Inspector field (“General settings”)
- ✨ **`CoreAISettings.UniversalSystemPromptPrefix`** — static property for programmatic override
- ✨ **`SyncToStaticSettings()`** — synced on startup from `CoreAILifetimeScope`
- ✨ Prefix applies automatically to all agents (built-in and custom)

### Temperature (shared across backends)

- ✨ **`CoreAISettingsAsset.temperature`** — default changed from `0.2` to `0.1`
- ✨ **`CoreAISettings.Temperature`** — static property (default `0.1`)
- ✨ Temperature applies to LLMUnity and HTTP API
- ✨ **`AgentBuilder.WithTemperature(float)`** — per-agent override
- ✨ **`AgentConfig.Temperature`** — config property
- ✨ Inspector field “Temperature” under “General settings”

### MaxToolCallIterations (no longer hard-coded)

- ✨ **`CoreAISettingsAsset.maxToolCallIterations`** — Inspector field (default 2)
- ✨ **`CoreAISettings.MaxToolCallIterations`** — static property
- ✨ **`MeaiLlmClient`** reads from settings instead of hard-coded `MaximumIterationsPerRequest = 2`

## [0.7.0] - 2026-04-06

### Unified MEAI tool-calling format (MAJOR)

**All tool calls now use one MEAI function-calling shape**

#### Added
- ✨ **`LuaTool`**: MEAI `AIFunction` for Programmer Lua execution
- ✨ **`LuaLlmTool`**: `ILlmTool` wrapper for Lua
- ✨ **`InventoryTool`**: MEAI `AIFunction` for Merchant inventory reads
- ✨ **`InventoryLlmTool`**: `ILlmTool` wrapper for inventory
- ✨ **Merchant agent**: NPC merchant with tools (`get_inventory` + memory)
- ✨ **`AgentBuilder`**: fluent builder for custom agents/tools
- ✨ **`AgentMode`**: `ToolsOnly`, `ToolsAndChat`, `ChatOnly`
- ✨ **`WithChatHistory()`**: session dialog context in RAM
- ✨ **`WithMemory()`**: persistent memory across sessions (JSON file)
- ✨ **Tool-call retry**: up to 3 automatic attempts on failed tool calls with error feedback (`CoreAISettings.MaxToolCallRetries`)

#### Changed
- 🔧 **`LlmUnityMeaiChatClient.TryParseToolCallFromText`**: normalized to `{"name": "...", "arguments": {...}}`
- 🔧 **All tools via MEAI**: Memory + Lua flow through `FunctionInvokingChatClient`
- 🔧 **`ProgrammerResponsePolicy` simplified**: no fenced-block checks
- 🔧 **`AgentMemoryPolicy.SetToolsForRole()`**: attach custom tools to a role
- 🔧 **Prompts updated**: Programmer + Merchant use the unified format

#### Removed
- ❌ **`AgentMemoryDirectiveParser`**: removed — MEAI pipeline only
- ❌ **Fallback parsing in `AiOrchestrator`**: memory via `FunctionInvokingChatClient`
- ❌ **Fenced blocks** (```memory, ```lua): not used for tool calls

#### Breaking changes
- **Programmer** now calls the `execute_lua` tool instead of fenced ```lua blocks
- **Memory tool** shape: `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- **`MaxLuaRepairRetries`** (formerly `MaxLuaRepairGenerations`) changed from 4 → 3

#### Tests
- ✨ **`AgentBuilderEditModeTests`** — 8 builder tests
- ✨ **`CustomAgentsPlayModeTests`** — 3 custom-agent tests (Merchant, Analyzer, Storyteller)
- 🔧 **`MeaiToolCallsEditModeTests`** — MemoryTool, LuaTool, JSON parsing
- 🔧 **`LuaExecutionPipelineEditModeTests`** — expected retries updated (4→3)
- 🔧 **`RoleStructuredResponsePolicyEditModeTests`** — Programmer allows any text
- 🔧 All PlayMode tests updated for unified tool format v0.7.0

#### Documentation
- 📝 **`AGENT_BUILDER.md`** — full builder guide
- 📝 **`TOOL_CALL_SPEC.md`** — updated tool spec
- 📝 **`CHAT_TOOL_CALLING.md`** — Merchant NPC tool calling
- 📝 **`DEVELOPER_GUIDE.md`** — refreshed sections

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.7.0**

---

## [0.6.1] - 2026-04-06

### Tool-calling fallback for LLMs without structured `tool_calls`

- 🔧 **`LlmUnityMeaiChatClient.TryParseToolCallFromText`**: fallback parses JSON tool calls from plain model text
- 🔧 **Qwen3.5-2B support**: model returns tool JSON as text, not structured `tool_call` — now detected and converted to `FunctionCallContent` for MEAI
- 🔧 **Recognized shapes:**
  - `{"tool": "memory", "action": "write", "content": "..."}`
  - `{"name": "memory", "arguments": {...}}`
  - ```json\n{...}\n``` fenced blocks

### Fixes

- ✅ **Memory tool works**: `FunctionInvokingChatClient` recognizes the call and runs `MemoryTool.ExecuteAsync()`
- ✅ **Memory persists across calls**: Craft 2 sees Craft 1 memory

### Documentation

- Troubleshooting sections updated in `LLMUNITY_SETUP_AND_MODELS.md`

---

## [0.6.0] - 2026-04-05

### Microsoft.Extensions.AI full integration

- ✨ **`MeaiLlmUnityClient`**: full Microsoft.Extensions.AI integration for LLMUnity
- ✨ **`FunctionInvokingChatClient`**: MEAI automatic tool calling
- ✨ **`IChatClient` implementation**: internal wrapper over `LLMAgent`
- ✨ **`MemoryTool.CreateAIFunction()`**: builds MEAI `AIFunction`

### Removed

- ❌ **`LlmUnityLlmClient`**: replaced by `MeaiLlmUnityClient`
- ❌ **`MeaiChatClientAdapter`**: removed — integration is `MeaiLlmUnityClient`

### Documentation

- Updated: `MemorySystem.md`, `DEVELOPER_GUIDE.md`, `DGF_SPEC.md`, `LLMUNITY_SETUP_AND_MODELS.md`

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.6.0**

---

## [0.5.0] - 2026-04-05

### LLM response validation

- ✨ **Role-specific validation policies**: six classes validating each role’s output
- ✨ **`CompositeRoleStructuredResponsePolicy`**: routes validation by `roleId`
- ✨ **20 new EditMode tests**: broad policy coverage
- ✅ **Automatic retry**: failed validation triggers a follow-up request with hints

### GameConfig system

- ✨ **`UnityGameConfigStore`**: `IGameConfigStore` backed by ScriptableObjects
- ✨ **DI wiring**: registered in `CoreAILifetimeScope`
- ✨ **EditMode tests**: nine tests (policy, read, update, round-trip)
- ✨ **PlayMode tests**: three tests (AI read/modify/write, no access, multi-key)
- ✨ **`GAME_CONFIG_GUIDE.md`**: developer guide

### Analyzer tests

- ✨ **`AnalyzerEditModeTests`**: ten tests (prompts, telemetry, validation, orchestrator)

### Tests

- ✨ **`RoleStructuredResponsePolicyEditModeTests.cs`**: 20 policy tests
- ✨ **`GameConfigEditModeTests.cs`**: nine tests for `GameConfigTool` / `GameConfigPolicy`
- ✨ **`GameConfigPlayModeTests.cs`**: three tests with a real AI
- ✨ **`AnalyzerEditModeTests.cs`**: ten Analyzer-role tests

### Dependencies

- Bumped dependency on `com.neoxider.coreai` to **0.5.0**

---

## [0.4.0] - 2026-04-05

### Tool calling support

- ✨ **`LlmUnityLlmClient.SetTools()`**: LLMUnity tool calling
- ✨ **Tools in system prompt**: tools appended to the model system prompt
- ✨ **`OpenAiChatLlmClient` tools**: OpenAI-compatible `tools` array support

### Architecture

- Shared **`ILlmClient`** surface for:
  - **OpenAI HTTP** (CoreAI) — tools in JSON body
  - **LLMUnity** (CoreAI Unity) — tools in system prompt
- **`CoreAILifetimeScope`** registers tool-capable clients

### Tests

- ✨ Tests updated for tool calling
- PlayMode coverage for LLMUnity + memory tool

---

## [0.3.0] - 2026-04-04

### MEAI integration

- Updated for **Microsoft.Extensions.AI** function calling
- Agent system prompts use the MEAI format
- Tests updated for the MEAI pipeline

### Tests

- ✨ **`MemoryToolMeaiEditModeTests.cs`**: eight MEAI integration tests
- ✅ PlayMode tests updated for JSON/MEAI format
- ✅ Removed legacy `AgentToolCallParser` tests
- **+50 tests** overall for MEAI coverage

### Documentation

- **`AI_AGENT_ROLES.md`**: roles updated for MEAI
- New MEAI function-calling guides

## [0.2.0] - 2026-04-04

### Layout

- **CoreAI.Source** sources live under **`Assets/CoreAiUnity/Runtime/Source/`** (previously under `Packages/com.neoxider.coreai/Runtime/Source/`). UPM dependencies for this package: **MessagePipe**, **MessagePipe.VContainer**, **UniTask**, **LLMUnity** (plus **`com.neoxider.coreai`** transitively).

### Logging (release requirement)

- **Editor:** menu/setup messages go through **`CoreAIEditorLog`** (single `Debug.*` choke point for the Editor layer).
- **Tests:** version stores and LLM helpers use **`NullGameLogger`** or **`GameLoggerUnscopedFallback`** — no raw **`Debug.Log`** in core test logic.

### Other

- Version aligned with **`com.neoxider.coreai` 0.1.3** (`package.json` dependency).

## [0.1.2] - earlier

Baseline Unity host package. See git history.
