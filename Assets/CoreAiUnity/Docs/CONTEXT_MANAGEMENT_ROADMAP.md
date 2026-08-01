# Context Management Roadmap — "Claude Code / Cline / Kilo"-grade

> Status: **planned (v4.3.0+)**. This document fixes the target design so it is built deliberately,
> not ad-hoc. Tracked in root `TODO.md` → *Context management overhaul*.

## Why

Today CoreAI history handling (`AiOrchestrator.BuildChatHistoryAsync` + `DeterministicConversationContextManager`)
folds older turns into a `## Conversation Summary` block that is **re-injected into the system prompt every turn**.
Three problems follow:

1. **Recent turns are not kept verbatim** when summarization is aggressive — the model loses the literal
   last exchanges and behaves "amnesiac" (e.g. an agent re-greeting every turn) even with a huge context window.
2. **Prompt caching is broken.** Caching is a *prefix match*; rewriting the system prefix every turn invalidates
   the provider-side cache, so every turn pays full input price. (Anthropic needs explicit `cache_control`
   breakpoints; OpenAI/DeepSeek-compatible backends auto-cache a *stable* prefix — which we currently churn.)
3. **Tool-call results never enter durable history** — `SanitizeAndPublish` stores only the final `user`/`assistant`
   text, so the model cannot reason across turns from what a tool returned.

Reference implementations we are aligning with:
- Kilo: [Context Management & Condensation](https://deepwiki.com/Kilo-Org/kilocode/3.8-context-management-and-condensation), [Context Window Management](https://deepwiki.com/Kilo-Org/kilocode/5.4-context-window-management)
- Cline: [Optimizing context / narrative integrity](https://cline.bot/blog/inside-clines-framework-for-optimizing-context-maintaining-narrative-integrity-and-enabling-smarter-ai), [Context Management](https://docs.cline.bot/prompting/understanding-context-management)
- Roo: [Intelligent Context Condensing](https://docs.roocode.com/features/intelligent-context-condensing), [Sliding Window (v3.3)](https://docs.roocode.com/update-notes/v3.3)

## Target design

### 1. Transcript model + stable cacheable prefix
- Status: **implemented as the only CoreAI runtime path**. The first provider `system` message contains only
  stable universal/role instructions and a deterministic full role tool contract. The summary is prepended as
  the first transcript-tail message because it represents the evicted oldest turns; recent turns remain verbatim
  after it.
- Keep recent turns as **real `user`/`assistant`/`tool` messages** in the tail; do **not** rewrite the
  `system`/`tools` prefix each turn. The frozen prefix is what the provider caches.
- Volatile, per-turn content goes after the transcript as ordered system-role tail messages, before the current
  user payload.

### 1a. Prefix vs Tail — how live memory & summary coexist with caching (the core decision)
- Status: **implemented as mandatory tail placement**. `AiOrchestrator.BuildChatHistoryAsync` emits
  `## Conversation Summary` as the first system-role
  `ChatHistory` message, before recent verbatim turns, instead of appending it to the system prefix.
  After the transcript it emits, in order, per-request `AiTaskRequest.RequestSystemInstructions`, canonical memory,
  pending memory updates, current-turn tool availability/requirement guidance, and runtime/world-state context.
  Every one of these blocks is a system-role tail message; none can contaminate the shared prefix.

The cache boundary is a strict layering rule, not a best-effort optimization. Any student, slide, time,
progress, allowlist, or request-dependent byte in the first `system` message fragments a platform-wide warm
cache into one entry per turn/student. Context is therefore split into:

- **Frozen prefix (cached):** universal rules, stable role/persona instructions, and the deterministic **full
  role tool contract**. It is byte-identical for every student using the same role and provider route.
- **Volatile tail (cheap to reprocess):** per-request system instructions, the **current memory snapshot** and
  pending updates, conversation summary/recent history, filtered tool availability/forced-mode guidance, and the
  **dynamic world-state observation** (§8) — emitted after the frozen prefix and before the current user payload:
  - Anthropic-style: a future dedicated transport may use a late `role: "system"` / developer-authority message
    when the provider explicitly supports it.
  - OpenAI/DeepSeek-compatible MEAI transport (current backend): orchestration system-tail entries are normalized
    to provider-safe `role: "user"` messages headed `System context update:` because some compatible chat
    templates reject `system` outside position zero. The stable first system message remains unchanged and the
    current user payload remains last.

`AiTaskRequest.SystemPrompt` intentionally retains its legacy behavior: it replaces the request's role base
prompt and therefore changes the frozen prefix. New request/student-specific callers must use
`RequestSystemInstructions`; this explicit API split avoids a silent behavioral migration.

Result: the large role prefix can be warmed once and reused across thousands of students; the small live tail
updates every turn. **Memory executes in full AND the cache survives.** This placement also fixes the "amnesia"
(we stop rewriting the prefix and keep recent turns verbatim). Cache *tiers* help too: a `system` change
invalidates system+messages but **not** tools; a tail-only `messages` change invalidates neither tools nor system.

This is shared-prefix eligibility per stable agent/role prompt version, not a per-student cache design. Memory,
history, quotas/limits, tool policy, and world state are never used as CoreAI cache identities. Provider routing
may create several physical warm copies of the same prefix: OpenRouter sticky routing is scoped by account,
model, and conversation (default conversation fingerprint: first system/developer + first non-system message),
while DeepSeek automatically matches complete prefixes from token zero within its documented API-user isolation.
Do not claim one global cache across endpoints. Provider-specific routing parameters are available through the
safe `SetProviderBodyParameter(string, JToken)` API. For OpenRouter, an optional `session_id` must name an opaque
application/agent cohort (`coreai-teacher-v3`), never a student/user/PII value; use only a small fixed shard set
when throughput design requires it. Exact cache measurement may pin
`provider.order: ["cloudflare/fp8"]` plus `provider.allow_fallbacks: false`, but production failover then becomes
the application's explicit responsibility.

Scaling rule: cache cardinality follows unique stable prefix versions. Identical role instances across thousands
of students share eligibility; unique persona prompts create distinct entries per routed endpoint. Personal state
is isolated separately: `CoreAILifetimeScope` wraps memory, flat chat, structured transcript, and compacted summary
with one scope-key mapping. A multi-tenant host must provide `IAgentMemoryScopeProvider` before container build via
the inspector `AgentMemoryScopeProviderBehaviour` field or `SetAgentMemoryScopeProvider(...)`. The default
`AgentMemoryScope.Empty` is a legacy role-only key and is safe only in a one-user process, with memory disabled, or
when shared memory/history is deliberate.

### 1a caching — verification & provider notes
- Status: **verification implemented.** Provider cache token counts now flow from
  `UsageDetails.AdditionalCounts` into `LlmCompletionResult` / `LlmStreamChunk`,
  `LlmUsageRecord`, `LlmUsageReported`, and turn diagnostics as `CacheReadTokens` /
  `CacheWriteTokens`. Confirm prompt caching by watching non-zero cache-read tokens
  after repeated stable-prefix requests; cache-write tokens show cache creation when a
  provider reports them.
- Current OpenAI/DeepSeek-compatible server backend: no explicit Anthropic-style
  `cache_control` breakpoint is required. The server auto-caches a stable prefix, so
  CoreAI's cooperation work is keeping persona/tool prefix serialization deterministic
  and moving volatile summary/world-state data to the tail.
- TODO for a future Anthropic-style backend: attach `cache_control` breakpoints on the
  frozen prefix through `ChatOptions.AdditionalProperties` / `RawRepresentationFactory`
  in that backend's transport adapter. Do not add those markers to the current
  OpenAI-compatible transport.
- Live validation: `PromptCacheLivePlayModeTests` is an explicit paid probe
  (`COREAI_TEST_PROMPT_CACHE=true`). It sends three different synthetic student tails through the real
  production-like pipeline, asserts a byte-identical long role/tool `SystemPrompt`, and requires a provider
  cache-read report by request three. Diagnostics include provider, model, prompt/completion tokens, cache reads,
  and cache writes. Run it separately for every production route; one green endpoint is not a global guarantee.

### 2. Compaction by threshold (not every turn)
- Status: **implemented.** `ConversationCompactionTriggerRatio` defaults to `0.8`; invalid/unset per-request
  values use the legacy budget boundary. Below the trigger, the prompt keeps all history verbatim and does not
  rewrite the stored rolling summary.
- Run condensation only when approaching the context limit. Produce an **anchored summary** that replaces the
  **oldest** turns; keep the most recent N turns verbatim while they fit.
- Re-summarize infrequently so the volatile tail stays small and deterministic across nearby turns.

### 3. Tool-result hygiene before truncation (`ToolResultMemoryPolicy`) - implemented
- New per-role policy: `None | ErrorsOnly | CompactSummary | Full` (default `CompactSummary`).
- Built-in defaults: `Programmer` and `CoreMechanicAI` use `Full` so exact code/mechanics tool output
  survives across turns; all other built-in roles keep `CompactSummary`.
- Tool-call results are persisted into history per policy as one `tool` chat-history entry headed
  `## Tool Results`; stored tool entries replay as provider-safe user observations.
- Intra-turn duplicate results are collapsed by tool name + normalized detail. Cross-turn pruning of
  outdated/superseded results is now handled by §7 context editing before compaction.
- **Truncate large outputs** (head/tail, byte/line cap) instead of dropping them whole.

### 4. Token accounting from the API, estimate only as fallback
- **Real tokens already flow from MEAI** `ChatResponse.Usage` (`InputTokenCount` / `OutputTokenCount` /
  `TotalTokenCount`) — captured in `MeaiLlmClient` for both non-streaming and streaming paths, and surfaced
  via `LlmUsageRecord`. The provider `UsageDetails.AdditionalCounts` carries extras (e.g. `cache_read` /
  `cache_write` tokens). So the "fact" half is **done**; this is NOT a hand-rolled token parser.
- **Implemented:** the pre-flight estimator now uses a script-aware base (Latin/ASCII/punctuation/whitespace
  preserve legacy `chars/4`; Cyrillic/CJK use a denser ~0.4 token/char bucket) plus bounded EMA calibration
  from observed real prompt tokens. `HeuristicTokenEstimator` remains in the codebase as the simple
  pre-send fallback.
- **Real BPE counter (R3, 4.13.0):** `BpeTokenCounter` (`ITokenCounter`) implements byte-level BPE
  (cl100k_base / o200k_base, resolved from the model name by `BpeEncodingResolver`) for exact pre-flight
  counts. It loads merge ranks via an `IBpeRanksProvider` and **falls back automatically** to the
  calibrating estimator when the model is unknown, the data file is missing, or loading fails (AOT/WebGL).
  **Activation (one manual step):** drop the standard tiktoken rank files at
  `Assets/CoreAI/Runtime/Resources/Tokenizers/cl100k_base.tiktoken.bytes` and `…/o200k_base.tiktoken.bytes`
  (format: one `base64(token-bytes) <rank>` per line, ~100k lines) and wire a Unity `IBpeRanksProvider`
  that opens them as a `Stream`. Until then the estimator path is used — no behavior change without the data.
- CoreAiPro's `ServerUsageSink` consumes the same `LlmUsageRecord`; sourcing it from `UsageDetails` keeps
  `Estimated = TotalTokens == 0` honest. Preserve the `ILlmUsageSink` contract — do not replace the sink.

### 5. Emergency overflow fallback
- **Implemented:** on provider "context length exceeded" error, `AiOrchestrator.RunTaskAsync` and
  `AiOrchestrator.RunStreamingAsync` rebuild the request with a tighter `ContextRetryLevel` and retry up to
  `ICoreAISettings.MaxContextOverflowRetries` times (default 3, 0 disables). Each retry level applies a
  `0.75^level` history-budget factor, dropping roughly 25% more of the oldest context per pass, then fails
  normally after the bounded attempts. Streaming retries are limited to failures before any visible text chunk
  is emitted, so callers never receive mixed chunks from two attempts.

### 6. Persistent memory — incremental, versioned, boundary-consolidated
Status: **implemented** for granular edits, version snapshots, explicit `read`, cache-safe tail updates, and
boundary consolidation.

- **Granular ops:** `read / append / str_replace / insert / delete / rename` over a structured memory doc — the
  agent edits, it does **not** rewrite the whole thing each time (model-decided when useful).
- **Versioned:** snapshot per mutation for audit + rollback (revert a bad self-edit).

**Placement & consolidation (the cache-safe rule).** Canonical and pending memory are both student-scoped and
therefore both remain in system-role tail messages. Storage still keeps a canonical snapshot plus pending
updates: compaction/retry boundaries may consolidate them to shorten the tail, but consolidation never moves
student memory into the first provider `system` message.

So two levels live in the tail: a **canonical snapshot** and **pending deltas** updated as the agent edits. The
model sees the complete effective memory every turn while the shared universal/role/tool prefix remains
byte-identical across students.

- **Memory is always fully readable.** The full memory is present in tail context every turn (canonical snapshot
  plus pending deltas), so the model never loses it; the explicit `read` action lets the agent re-read the whole
  memory on demand. A compaction boundary may merge the two storage levels, but it does not change their
  provider-message layer.

- **Layered scopes**, each mapping onto the above:
  - `global / persona` — stable instructions belong to the role prefix; mutable memory belongs to the tail.
  - `per-user` (e.g. the student profile) and `per-session` — canonical state and updates both remain in the tail.
- Durable across sessions (survives WebGL restart). Game-side requirement: RedoSchool `MVP_TODO.md`
  → *2.2 Personal student memory*.
- **Inspector:** extend the Agent Session Inspector to show memory deltas + version history.

### 6a. Stable role contract + current-turn availability
Status: implemented for native and text-shaped backends.

The first `system` message contains the deterministic full role tool list + JSON schemas, independent of
`AllowedToolNames`, `ForcedToolMode`, and `RequiredToolName`. Native request `Tools` are still filtered to the
actual current-turn set. An ordered `## Tool Availability (current request)` orchestration tail message lists the
effective tools, sorted allowlist entries, selection mode, and required tool. Text-shaped backends therefore do
not call a filtered-out tool merely because its stable definition remains in the shared role contract.

### 7. Context editing (prune) alongside compaction (summarize)
- Status: **implemented** for prompt-history copies. `ConversationHistoryPruner` runs before
  `ConversationHistoryPartition.PartitionByBudget`, collapses exact consecutive duplicates, strips stale
  `<think>…</think>` reasoning from every assistant turn except the newest one (losslessly — only reasoning
  scaffolding is dropped; visible answers and the latest turn's reasoning are preserved, and assistant turns that
  contain nothing but reasoning are removed), and keeps only the newest configured `tool` / `## Tool Results`
  observations. This completes the cross-turn superseded-tool-result and stale-reasoning pruning deferred by
  `ToolResultMemoryPolicy`; durable chat history on disk is not edited.
- Two complementary levers, like Claude Code: **compaction** summarizes old turns when near the limit (§2);
  **context editing** *prunes* stale content without summarizing — drop superseded tool results / old thinking
  blocks once they no longer matter. Prune first (cheap, lossless-ish), summarize only when still over budget.

### 8. Dynamic world-state observation (universal / game agent — beyond Claude Code/Cursor)
- Status: **implemented as mandatory tail placement**. Existing per-role `IAgentRuntimeContextProvider` and global
  `IAiPromptContextProvider` output is still assembled by `AiPromptComposer`, then appended as the final
  system-role `## World State` chat-history message after the summary and recent verbatim turns. The old
  system-prefix placement toggle was removed.
- This agent is not just a coder: it also drives **game mechanics, NPCs, lesson briefings**. Give it, each turn,
  a compact **world-state observation** in the tail — current scene, relevant NPC/quest/player state, the
  "current slide" in a briefing, etc. This is the game analog of Claude Code's file-context, but for live state.
- It is an **observation** (read-only context), not a command: the model still decides what to do. Lives in the
  **tail** so it stays cache-safe, and is built via a per-role provider so each role exposes only what it needs.

### 9. Deterministic serialization & per-role policy
- Status: **implemented** for the frozen prefix/tool contract. Tool rendering and MEAI native tool arrays use
  ordinal-by-name ordering, text-shaped tool schemas are complete valid JSON without character truncation and
  are compacted with recursively sorted object keys, and
  EditMode regressions guard identical fixed-input system prefixes from generated GUID/timestamp leakage.
  Per-role policy is provided by existing `AgentBuilder` / `AgentMemoryPolicy` knobs; see
  `COREAI_SETTINGS.md` ("Per-role policy").
- **Deterministic prefix**: stable tool ordering, sorted JSON keys, no timestamps/UUIDs in the frozen prefix —
  otherwise the cache is silently busted every request.
- **Per-role policy**: history depth, memory scope, `ToolResultMemoryPolicy`, world-state provider, and
  compaction thresholds are configured per role (Teacher / NPC / mechanics agent), since one global setting does
  not fit a universal agent.

### 10. Out of scope: embedding / semantic recall
- The student profile (and per-role memory) fits the 128K window as a **plain-text canonical memory doc**, so
  vector embeddings (`IEmbeddingGenerator` / `System.Numerics.Tensors`) are **NOT** pursued. Semantic recall
  only earns its keep when memory stops fitting the context window (thousands of facts/documents). Revisit only
  then. Plain text is simpler, deterministic, and human-readable — the right default here.

## Notes / constraints
- WebGL/IL2CPP: no local tokenizer; API-usage calibration is the realistic accuracy path (no per-model BPE).
- Changes are core (`com.neoxider.coreai`) + Unity adapter (`com.neoxider.coreaiunity`); ship behind config so
  existing roles keep working. Caching cooperation is provider-dependent (verify via `usage.cache_read_*` when present).
- Repository hygiene: `.gitattributes` pins Unity YAML/source text to deterministic line endings and marks common
  media/build artifacts as binary so context-management commits do not pick up CRLF/LF churn or binary phantom diffs.
