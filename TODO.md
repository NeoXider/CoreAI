# TODO

> Updated 2026-06-28. Tracks open work by priority. Shipped work is in `CHANGELOG.md` (both packages);
> non-blocking future work in `Assets/CoreAiUnity/Docs/BACKLOG.md`. Priorities below reflect the
> 2026-06-28 competitive audit (vs Cursor / Claude Code / Kilo / Cline) and the maintainer's ordering.
> Test baseline: EditMode ~1238, PlayMode `FastNoLlm` ~50 (deterministic).

## Roadmap (prioritized)

### [R1] Parallel tool-call execution  *(in progress)*

> Today `ToolExecutionPolicy.ExecuteBatchAsync` runs a batch of tool calls strictly sequentially
> (`foreach`). Cursor/Claude Code/Cline dispatch independent calls concurrently, so CoreAI multi-tool
> turns are latency-bound. Make execution concurrent while preserving today's semantics.

Plan:
- [ ] Execute the calls of one batch concurrently (`Task.WhenAll`) with a bounded degree of parallelism
      (new setting `MaxParallelToolCalls`, default e.g. 4; 1 = current sequential behavior).
- [ ] **Preserve result order**: results/history entries appended in original call order regardless of
      completion order (await an ordered array, not completion order).
- [ ] **Serialize state-mutating built-ins** that share a store (e.g. `memory` append/edit, `manage_mods`)
      — either run same-tool or same-store calls sequentially within the batch, or document that the model
      must not issue racing writes. Pure/read tools and independent host tools run fully parallel.
- [ ] Keep per-call timeout (linked CTS), duplicate-batch rejection, forced-tool reset, and the
      consecutive-error counter semantics intact; a single failing call must not corrupt siblings' results.
- [ ] Cancellation: cancel all in-flight calls on outer cancel; never fall back on `OperationCanceledException`.
- [ ] Tests (orchestrator-written): order-preserved-under-parallelism, latency-improves (two slow tools run
      concurrently), one-fails-others-succeed, duplicate detection still whole-batch, mutating-tool
      serialization, cancellation cancels all.

### [R2] PlayMode: configurable provider / API / model

> The live PlayMode suite resolves the backend via `PlayModeProductionLikeLlmFactory` / settings / env, but
> there is no single, easy place to point the whole live suite at a chosen OpenAI-compatible endpoint + key
> + model. Make it first-class so anyone can run the live tests against their provider/model.
- [ ] A single config surface (env vars + a gitignored config asset/JSON) for base URL, API key, model name,
      and streaming/native-tools flags consumed by `PlayModeOpenAiTestConfig` / `PlayModeProductionLikeLlmFactory`.
- [ ] Clear `Assert.Ignore` reason when unconfigured; doc in `Docs` on how to run the live suite.
- [ ] Optional: per-test model override (vision-capable model for vision tests, etc.).

### [R3] Real token counting (BPE) with heuristic fallback

> No real tokenizer exists — only `CalibratingTokenEstimator` (char-weight + EMA vs provider `prompt_tokens`).
> Use a real BPE tokenizer when available; fall back to the estimator when the encoding is unknown or the
> runtime can't host the lib (IL2CPP / WebGL / AOT).
- [ ] Integrate a BPE tokenizer for OpenAI-family models (cl100k/o200k) behind an `ITokenCounter` abstraction.
- [ ] Resolve encoding by model name; on unknown model / unavailable lib, fall back to `CalibratingTokenEstimator`.
- [ ] AOT/WebGL safety (encoding data load, stripping); keep the calibrating estimator as the universal fallback.
- [ ] Tests: known model → exact-ish BPE counts vs recorded fixtures; unknown model → estimator path.

### [R4] Skill authoring — model creates & saves new skills

> The model can only `read_skill` / `call_skill_tool` (use EXISTING skills). It cannot author and persist a
> NEW skill, nor refine one. Add agent-authorable, persistent, self-improving skills.
- [ ] `ISkillStore` + file-backed impl (persist `SkillSet` name/description/instructions/tool-allowlist).
- [ ] A `manage_skills` tool: `create` / `update` / `list` / `get` / `delete` (mirrors `manage_mods`), so the
      model can write a new skill and reload it into its own catalog.
- [ ] Self-improvement: let a skill's instructions be revised by the agent (versioned via `ILuaScriptVersionStore`-style history) — "the agent learns a procedure once, saves it as a skill, reuses it."
- [ ] Tests: create→persist→appears in catalog→read_skill returns it; update revises; isolation per role/scope.

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

- [ ] Remove now-dead `MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming` (superseded by `GetHybridSafeSegments`; only its own test references it) and bound the O(n²) per-delta hybrid rescan.
- [ ] Separate inter-token idle timeout (distinct from total request timeout) in SSE streaming.
- [ ] Surface provider-native `reasoning_content` SSE deltas as a collapsible "thinking" channel (currently parsed and dropped).
- [ ] Pin "raw tool-call JSON never leaks into visible Text" as a hard test (today the parity test tolerates a brief flash).
- [ ] Harden `ConversationHistoryPruner.ExtractToolNames` against `Full`-policy tool blocks (name-only markdown parse is brittle).
- [ ] Fix `ToolExecutionPolicy.IsToolResultSuccess` lossy "contains 'success'" heuristic; structured success contract for tool results.
- [ ] Tests: per-tool timeout firing; max-roundtrips cap termination; `SseToolCallAccumulator` state machine across many small deltas; Lua memory/table-growth bomb + blocking-native-binding; EditMode coverage gate in CI.
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite.

## Shipped (recent)

- 4.12.1 — memory instruction now reaches native tool-calling roles (`AiToolContractPromptFormatter` early-return bug).
- 4.12.0 — live streaming through tool calls, partial-SSE accumulation, WebGL Lua AOT hardening, stale-`<think>` prune, Lua mod versioning + diagnostics, vision host send path + gate + lift, P3 nits.
