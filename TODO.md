# TODO

> Updated 2026-07-01. Tracks open work by priority. Shipped work is in `CHANGELOG.md` (both packages);
> non-blocking future work in `Assets/CoreAiUnity/Docs/BACKLOG.md`. Priorities below reflect the
> 2026-06-28 competitive audit (vs Cursor / Claude Code / Kilo / Cline) and the maintainer's ordering.
> Test baseline: EditMode ~1314, PlayMode `FastNoLlm` ~50 (deterministic).

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

> `FallbackLlmClientDecorator` (primary→1 secondary) is shipped and covered by 10 EditMode tests. Missing:
- [ ] **Circuit breaker** — trip a backend "open" after N consecutive failures so a dead primary doesn't cost
      `timeout × (retries+1)` every turn; half-open probe to recover.
- [ ] **Multi-provider fallback chain** (ordered list, not just 1 secondary) + secondary wrapped in the same
      retry/logging decorators (today the secondary gets no HTTP-retry wrapper).
- [ ] **Per-provider rate limiting** (token/request bucket) distinct from the Lua-generation limiter.
- [ ] Streaming-path retry (today only `CompleteAsync` retries; `CompleteStreamingAsync` is single-shot).
- [ ] Enforce request timeout in the portable core, not only in the Unity `CoreAiChatService` (default 300s).
- [ ] Tests for each (circuit open/half-open, chain exhaustion, streaming retry, core-side timeout).

### [R7] Structured output (schema-constrained generation) — optional, pending decision

> Today "structured output" is post-hoc string validation (`IRoleStructuredResponsePolicy`), not provider-
> enforced. Optional reliability win, not critical.
- [ ] Pass `response_format` / `json_schema` to OpenAI-compatible providers; GBNF grammar for local models
      where supported; keep post-validation as the fallback. (Decide whether to build.)

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
- [ ] Harden `ConversationHistoryPruner.ExtractToolNames` against `Full`-policy tool blocks (name-only markdown parse is brittle).
- [x] ~~Fix `ToolExecutionPolicy.IsToolResultSuccess` lossy "contains 'success'" heuristic~~ — done 2026-07-01 (JSON `error`/`ok:false`/`succeeded:false` + failure prefixes, classified before truncation).
- [ ] `world_command` `apply_force`/`set_velocity` accept an all-zero vector despite the "required component" error text (audit W4 #1) — deferred while the World Lua API refactor is in flight.
- [ ] Tests: per-tool timeout firing; max-roundtrips cap termination; Lua memory/table-growth bomb + blocking-native-binding; EditMode coverage gate in CI. *(`SseToolCallAccumulator` many-small-deltas coverage added 2026-07-01.)*
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite.

## Shipped (recent)

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
