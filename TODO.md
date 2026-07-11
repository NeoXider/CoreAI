# TODO

> Updated 2026-07-11. Tracks open work by priority. Shipped work is in `CHANGELOG.md` (both packages);
> non-blocking future work in `Assets/CoreAiUnity/Docs/BACKLOG.md`.
> Released: 5.6.1 (2026-07-11, all five packages in lockstep). Last full verification 2026-07-10:
> EditMode 1,613 total / 1,609 passed / 0 failed / 4 optional third-party ignored; deterministic G8
> PlayMode 3/3; full local `qwen3.5-4b-mtp` G1-G8 benchmark 88.1/100 (G8 3/3); PlayMode `FastNoLlm` 67/67.

## [R0.5] Demo pass (owner request: "показательны и корректны")

- [x] Deterministic startup smoke for all ten published scenes (including Skills and
      LiveMechanicsModsChat): no missing scripts, scope/camera present, supported shaders, no
      unexpected startup errors. Fixed missing Mods scopes, Mirror remnants, Hub wiring, and Wave URP color.
- [ ] Complete manual interaction drivers for every demo (buttons, input, battle loops, F9/F10,
      mod load/unload/restart persistence) with screenshots; startup smoke is not full UX acceptance.
- [x] Representative local 4B live checks: memory write and real `world_command` spawn pass on
      `qwen3.5-4b-mtp`.
- [ ] Verify the AI writes mods through the Hub chat in each kept Hub-enabled demo with local 4B/9B/27B
      (LM Studio) and Opus 4.8 via the bundled preset (`Assets/Resources/CoreAIPresets/`,
      bridge: `agent.sh openai-server -e claude -m opus`). *(API models are already proven through the
      same bridge in the benchmark v2 sweep — remaining gap is specifically Hub-chat mod-writing per demo.)*
- [x] Benchmark package (`com.neoxider.coreaibenchmark`): suite ran through the bridge with `-m spark` —
      full v1.7 G1-G8 run on the leaderboard (92.9, row 3); no scenario breakage from the 5.1.0 wave.

## [R0.6] Release-engineering residuals (from the two 2026-07-10 repository audits)

- [~] **F-12 CI gates**: trusted merge-queue gate added (`merge_group` trigger + `merge-queue-gate` job
      that FAILS, not skips, when UNITY_LICENSE is absent) plus a fork-safe `package-graph` job
      (lockstep + internal-dep check). REMAINING: minimal Standalone/WebGL IL2CPP player builds and a
      package-isolation consumer matrix (need the licensed runner to add).
- [ ] **F-18**: pin floating Git dependencies (tags/commits) + explicit upgrade command.
- [ ] **F-19**: slim the dev project (Epic Toon FX ~522 MiB, unused multiplayer packages) or a minimal
      verification project; demo assets to `Samples~`.
- [ ] **F-20**: performance regression suite (orchestrator enqueue, streaming buffers, 10k-object world
      queries, revision stores, audit burst, WebGL persistence cadence).
- [~] **F-22**: package-local test assemblies so standalone UPM graphs are proven, not just monorepo.
      Added `CoreAI.Core.Tests` (references only `CoreAI.Core`) as the pattern + isolation smoke suite;
      `CoreAI.Mods.Tests` is already package-local. REMAINING: same for coreaiunity/hub/benchmark.
- [ ] **F-21**: replace remaining fixed `Task.Delay` waits in async tests with signal-based waits.
- [x] Streaming mutating-call deferral: mutating calls wait for turn completion, whole-turn echoes
      are rejected before side effects, and partial retries execute only failed slots.
- [ ] Cross-request idempotency: add executor-level stable idempotency keys. Current replay state is
      request-local and `ToolExecutionPolicy.Reset()` intentionally clears it.
- [ ] Full-tier Lua queries: move recursive `unity_list_objects` / `unity_find_all` /
      `unity_find_by_tag` / `unity_find_by_component` implementations onto the shared budgeted walker.
- [ ] Durability: WebGL sync after `WorldStateManager.Reset`; recoverable two-phase audit rotation;
      surface audit worker failures during runtime rather than only at Dispose/testing flush.
- [x] `allowedLuaScenes` contract pinned as deliberately permissive when empty (any Build Settings scene),
      with an explicit security-policy test; Inspector tooltip now matches the runtime contract.
- [ ] Hub "Audit Log" page: viewer + chain-integrity badge over `AuditLogVerifier` (natural home for
      the new read/verify API).

## Roadmap (prioritized)

### [R5] Summarization & context-overflow — live verification

> Compaction is unit-tested with stubs only; `LlmCompactionPerRolePlayModeTests` is FastNoLlm (stub). No live
> test proves the summary actually compresses well AND preserves key facts, nor that overflow-retry converges.
- [ ] Live PlayMode test: build a long conversation, force compaction, assert (a) token reduction and
      (b) key facts survive (probe the model that the summary retained specific details).
- [ ] Integration test: context-overflow retry loop actually shrinks the prompt and eventually succeeds
      (the `0.75^n` clamp converges) — currently only the shrink factor is unit-tested.
- [ ] Default-config guard: cap the rolled summary by tokens (today `ConversationRolledSummaryMaxTokens=0` = uncapped).

### [R6] Advanced resilience (basic fallback already shipped & tested)

> `FallbackLlmClientDecorator` (primary→1 secondary) is shipped and covered by 10 EditMode tests.
> `CircuitBreakerLlmClientDecorator` is also shipped and covered, but remains an opt-in public decorator.
- [x] **Circuit breaker primitive** — transient-failure threshold, open short-circuit, half-open recovery,
      streaming coverage, deterministic clock, and six EditMode tests.
- [ ] Wire circuit-breaker settings into the production composition root (threshold/cooldown/provider scope)
      before claiming that every default backend uses it automatically.
- [ ] **Multi-provider fallback chain** (ordered list, not just 1 secondary) + secondary wrapped in the same
      retry/logging decorators (today the secondary gets no HTTP-retry wrapper).
- [ ] **Per-provider rate limiting** (token/request bucket) distinct from the Lua-generation limiter.
- [x] Streaming-path retry: `RetryingStreamingLlmClientDecorator` (CoreAI.Core) retries the stream only
      before it commits content (7 EditMode tests); wired into `LlmPipelineInstaller`.
- [x] Enforce request timeout in the portable core: `TimeoutLlmClientDecorator` (CoreAI.Core) bounds both
      paths off `LlmRequestTimeoutSeconds` (5 EditMode tests); additive with the Unity WebGL PlayerLoop timer.
- [x] Tests for streaming retry + core-side timeout (12 EditMode tests). Circuit open/half-open already
      covered; multi-provider chain exhaustion remains with the fallback-chain item above.

### [R7] Structured output (schema-constrained generation) — optional, pending decision

> Today "structured output" is post-hoc string validation (`IRoleStructuredResponsePolicy`), not provider-
> enforced. Optional reliability win, not critical.
- [ ] Pass `response_format` / `json_schema` to OpenAI-compatible providers; GBNF grammar for local models
      where supported; keep post-validation as the fallback. (Decide whether to build.)

### [R7.5] Multi-API — per-agent LLM provider configuration (owner request 2026-07-11)

> Per-ROLE provider routing already ships: `LlmRoutingManifest` (role pattern → profile →
> `OpenAiHttpLlmSettings` with its own URL/key/model) resolved per request by `RoutingLlmClient` via
> `ILlmClientRegistry`. What's missing is the code-first/dev-facing layer (est. M, ~5 files).
- [ ] `AgentBuilder.WithLlmProfile(profileId)` / `WithLlmBackend(OpenAiHttpOptions)` → `AgentConfig` →
      policy (`SetLlmProfileForRole`, pattern of existing `SetTemperature`).
- [ ] Explicit profile on the request: make `LlmCompletionRequest.RoutingProfileId` an input hint
      (today write-only diagnostics); `RoutingLlmClient.Prepare` prefers it over role-pattern match.
- [ ] Runtime profile registration: `RegisterProfile(profileId, OpenAiHttpOptions)` on the registry
      (build `OpenAiChatLlmClient` without an SO asset — `IOpenAiHttpSettings` ctor already exists).
- [ ] Per-profile fallback + limits: `FallbackLlmClientDecorator` and timeout/retry settings currently
      apply only to the legacy-fallback client / global settings — decide per-profile story.
- [ ] Hot-swap consistency: `CoreAiBackend.SetApiKey/SetApiBaseUrl` rebuilds only the legacy fallback;
      profile clients need re-`ApplyManifest`/`ApplyRouteTable` on change.
- [ ] Key hygiene: N providers = N plaintext `apiKey` fields in `.asset` files; provide an out-of-asset
      key source (env var / `IServerManagedAuthProvider`-style) before promoting multi-API.
- [ ] Docs: "per-agent providers" recipe (inspector-only path needs zero code: N × `OpenAiHttpLlmSettings`
      + manifest on `CoreAILifetimeScope`).

### [R8] Vision — finish (feature already shipped)

- [ ] PlayMode round-trip `[Explicit]` test against a real vision-capable model (capture → model → assert).
      (Host send path, gate, and tool-result lift already shipped in 4.12.0; FastNoLlm camera test exists.)

### [R9 — lowest priority] Multi-agent / sub-agent orchestration

> Design in `TODO/MultiAgent_Orchestration_v2.0.md`. The decisive parity gap vs Claude Code Task tool /
> Cursor background agents / Cline subtasks — but explicitly LAST per maintainer.
- [ ] `SubAgentDefinition` (roleId, description, prompt, tools w/o Task tool, model, maxTokens, maxTurns).
- [ ] `IAgentRegistry` + `AgentOrchestrator.ExecuteSubAgentAsync` (clean context isolation) + bounded `ExecuteSubAgentsParallelAsync`.
- [ ] `AgentLlmTool` (parent-only) returning results as tool_result; DI wiring; per-role exposure; settings.
- [ ] EditMode tests + docs + CHANGELOG.

## Audit cleanup & cheap test gaps (from 2026-06-28 audit, non-blocking)

- [ ] Remove now-dead `MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming` (superseded by `GetHybridSafeSegments`; only its own test references it). *(The O(n²) per-delta hybrid rescan is now bounded by a 64 KB held-tail cap — 2026-07-01.)*
- [ ] Separate inter-token idle timeout (distinct from total request timeout) in SSE streaming.
- [ ] Surface provider-native `reasoning_content` SSE deltas as a collapsible "thinking" channel. *(Now handled consistently as internal — not surfaced as visible text in either path — 2026-07-01.)*
- [x] ~~Pin "raw tool-call JSON never leaks into visible Text"~~ — streaming now fails closed on incomplete/unparseable text-shaped tool JSON (2026-07-01); a dedicated hard leak test would still be nice.
- [x] Harden `ConversationHistoryPruner.ExtractToolNames` against nested `Full`-policy detail blocks.
- [x] ~~Fix `ToolExecutionPolicy.IsToolResultSuccess` lossy "contains 'success'" heuristic~~ — done 2026-07-01 (JSON `error`/`ok:false`/`succeeded:false` + failure prefixes, classified before truncation).
- [x] ~~`world_command` `apply_force`/`set_velocity` accept an all-zero vector~~ — fixed 2026-07-01 (require at least one vector component; explicit per-axis `0` still honored).
- [ ] Tests: per-tool timeout firing; Lua memory/table-growth bomb + blocking-native-binding; EditMode coverage gate in CI.
      *Max-roundtrips cap termination is covered; `SseToolCallAccumulator` many-small-deltas coverage was added 2026-07-01.*
- [ ] Chat: queue outgoing user messages while a turn is in progress (buffer sends, flush in order when the
      active turn completes) instead of only disabling the send button / dropping input.
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite.
- [x] ~~`MeaiLlmClient.CompleteAsync` drops `ExecutedToolCalls` on an empty final response~~ — fixed
      2026-07-01, found via a live G6 benchmark report contradiction (`0 tool-calls` / `1 spawns`); the same
      root cause explained every "tool ran but stats say 0" symptom (benchmark `ToolCalls`/`FailedToolCalls`
      undercounts, `ToolErrorRate` misreporting, "used tool" checkpoints failing despite executor state
      proving a tool ran). *(The Codex audit doc with the full trace was removed per the "no audits in
      the project" rule; the trace survives in git history.)*
- [ ] Benchmark harness: `RecordingWorldExecutor.InvalidCommandCount` is tracked separately from `ToolCalls`
      (invalid/malformed world commands are invisible in the "Tool calls" column). Defensible as a distinct
      metric, but worth an explicit decision — either document the split or fold invalid attempts into
      `ToolCalls` too. Low severity (labeling nuance, not a scoring bug).
- [ ] Make the benchmark's manually-built orchestrator turn-trace visible in the Agent Session Inspector
      (today it only resolves a trace reader from a scene DI scope).
- [x] ~~G4 playthrough scenarios (Combat/Crafting/Shop) score PARTIAL on weak models mainly from failed Lua
      calls right after a successful `logic_define`~~ — fixed 2026-07-01 (Codex audit): added a `VerificationNote`
      to each G4 goal clarifying `logic_define` does not create a directly-callable global; the harness invokes
      registered slots with hidden samples.
- [x] ~~G1 world-building scenarios (Coin collector, Constraint budget) can PASS while spawning every object
      at the same `(0,0,0)` position~~ — fixed 2026-07-01 (Codex audit): added `DistinctSpawnPositionCells` +
      `spatial_spread` checkpoints and a prompt requirement for distinct x/z positions, across all three G1 scenarios.
- [x] ~~G6 free-build: generic-subject prompt says "AT LEAST 24 objects" but `substantial_scene` grading
      accepts 18 for custom free-builds~~ — fixed 2026-07-01 (Codex audit): generic-build grading now also
      requires 24 objects / 20 distinct names, matching the prompt.
- [x] ~~G6 bounds grading (`CountBoundsViolations`) checks only the spawn pivot, not the scaled extent~~ —
      fixed 2026-07-01 (Codex audit): added `HalfExtents()` (per-primitive-shape, including the real 2m
      cylinder/capsule height) and bounds now check the full scaled extent.
- [x] ~~G6 `IsTowerLike()` treats any cylinder/capsule near a corner as a tower regardless of scale/name~~ —
      fixed 2026-07-01 (Codex audit): now also requires height >= 2.5m and footprint >= 1m.
- [x] ~~G5 `exact_count`-style constraints count `env.World.Commands.Count`, not actual tool-call attempts~~ —
      fixed 2026-07-01 (Codex audit): `g5_exactly_three` now uses `max(recorded commands, actual world_command
      tool-call attempts)`.
- [x] ~~G6 full-prompt override (`COREAI_BENCHMARK_FREEBUILD_PROMPT`) was still graded against the built-in
      castle/generic checkpoints, unfairly failing a custom task~~ — fixed 2026-07-01: added
      `FailureAttribution.NotGraded` + `GameBenchmarkScenario.ExcludeFromScoring`; a full-prompt override now
      still runs/screenshots but is excluded from `SuiteBaseScore`/pass-rate/dimension breakdown (a
      subject-only override still uses the known `GenericGoal` scaffold and stays gradeable). Verified live
      against qwen3.5-4b-mtp: a 3-cube custom prompt now shows "No graded groups" instead of a punishing FAIL.
- [ ] `RoleFitness` "Orchestrator / Director" can rate a small model 9+/10 off G1-G8 alone, since almost
      every scenario resolves in a single LLM turn (`RunObservation.Turns` = 1 nearly everywhere) — high
      Reasoning/Intent scores reflect "parsed the instruction correctly in one shot", not sustained
      multi-turn orchestration with error recovery, which is what the role's own description asks for. G4's
      "playthrough" doesn't cover this either — the harness simulates the multi-step trajectory in C# after
      the model installs Lua slots, not the model itself across real turns. Added an honest caveat to the
      role's `Note` text (2026-07-01, Codex audit) without touching the formula/weights — changing those
      would affect every historical comparison and needs a user decision, not a quiet fix. A real fix likely
      needs a genuinely multi-turn scenario (adversarial tool failures forcing retries, or a task that can't
      complete in one turn by construction) feeding into the Director gate/weights specifically.
      G8 adds described-state conditional selection, but it is still single-turn and does not close this gap.

## Shipped (recent)

- 5.6.x — build-time policy registration; simpler agent API (`AgentBuilder.Build()` auto-applies to the
  global policy); solution-wide code-style pass (WHY/TODO/HACK comment rules); benchmark castle
  comparison scene + Stop-with-partial-report; native-API free models in README.
- 5.5.0 — [R6] resilience wave: `TimeoutLlmClientDecorator` + `RetryingStreamingLlmClientDecorator`
  wired into the pipeline, `CircuitBreakerLlmClientDecorator` primitive; benchmark v2 tooling; CI
  merge-queue gate + package-graph job (F-12 partial).
- 5.4.0 — MoonSharp removed; Lua-CSharp is the only VM.
- 5.3.0 — benchmark v2; resilience primitives (`FallbackLlmClientDecorator` covered by tests).
- 5.1.0-5.2.0 — audit remediation: safe mutation pipeline, bounded queues/stores; stability gate and
  extension APIs; streaming mutating-call deferral; `allowedLuaScenes` contract pinned.
- 5.0.x — on-demand skills for built-in roles ("Lua Modding" skill); benchmark package extracted to
  `com.neoxider.coreaibenchmark`; version lockstep across five packages.
- 4.17.0 — tool-call history unlimited by default (`MaxToolCallHistoryMessages = 0`); per-agent / per-call
  `MaxToolCallRoundtrips` override (`0` = unlimited, Programmer/Creator default unlimited), default cap raised
  10 → 20; clearer cap-reached stop message; honest provider-call tok/s labeling; `BenchmarkInfo.GroupDifficulty10`
  single source of difficulty. Full-tier Lua `unity_add_component` / `unity_destroy` + Unity-object-reference coercion;
  `world_command` spawn accepts rotation + scale inline with schema docs; demos reorganized into `Scripts/` subfolders.
- 4.16.0 — `AllowWorldPrimitives` setting; `component_command` curated reflection-free component catalog (+ `coreai_component_*`
  Lua bindings); `unity_list_members` discovery + rich Color/Vector/Quaternion coercion + did-you-mean errors; G6 free-build
  subject overridable; decode tok/s fix; configurable benchmark roundtrip cap.
- 4.15.x — Game-Creation Benchmark reporting polish: G6 castle free-build hero, per-model model-card radar/role bars,
  role-shaped scene screenshots with ghost markers, decode-vs-effective tok/s, cross-model comparison + Models leaderboard tab,
  LM Studio multi-model sweep, mean-over-repetitions aggregation, `Repeatable` opt-out, model-name-on-screenshot, audit
  material/mesh-leak fixes.
- 4.14.0 — portable Game-Creation Benchmark scoring core + live PlayMode suite (G1–G5 scenario groups, 0..100 across six
  dimensions, subtractive instruction-following, `RoleFitness` per game-dev role, gated efficiency bonus, self-explanatory
  scene screenshots, per-model comparison card, Editor **CoreAI > Benchmarks** window).
- 4.13.0 — **[R1] parallel tool-call execution** (`ToolExecutionPolicy.ExecuteBatchAsync` runs a batch concurrently,
  bounded by `MaxParallelToolCalls`, default 4; order preserved, state-mutating built-ins serialized, timeout/duplicate/
  forced-tool/consecutive-error/cancellation semantics intact). **[R3] real BPE token counting** (`ITokenCounter` +
  `BpeTokenCounter` for cl100k/o200k via `BpeEncodingResolver` / `IBpeRanksProvider`, falls back to the calibrating
  estimator). **[R4] agent-authored skills** (`manage_skills` create/update/list/get/delete + file-backed `FileSkillStore`,
  versioned, surfaced into `read_skill`; `AgentBuilder.WithSkillAuthoring`). **[R2] configurable live PlayMode provider**
  (`PlayModeOpenAiTestConfig`: env vars + gitignored `coreai-live-tests.local.json`, see `Docs/RUNNING_LIVE_TESTS.md`).
  Also Hermes/Qwen-Agent XML tool-call parsing.
- 4.12.1 — memory instruction now reaches native tool-calling roles (`AiToolContractPromptFormatter` early-return bug).
- 4.12.0 — live streaming through tool calls, partial-SSE accumulation, WebGL Lua AOT hardening, stale-`<think>` prune, Lua mod versioning + diagnostics, vision host send path + gate + lift, P3 nits.
