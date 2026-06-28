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
- Status: **implemented as the only CoreAI runtime path** for conversation summary, world-state, and live
  memory-update placement. The summary is prepended as the first tail message because it represents the evicted
  oldest turns; recent turns already remain verbatim after it.
- Keep recent turns as **real `user`/`assistant`/`tool` messages** in the tail; do **not** rewrite the
  `system`/`tools` prefix each turn. The frozen prefix is what the provider caches.
- Volatile, per-turn content goes at the **end**, after the cache breakpoint.

### 1a. Prefix vs Tail — how live memory & summary coexist with caching (the core decision)
- Status: **implemented as mandatory tail placement**. `AiOrchestrator.BuildChatHistoryAsync` emits
  `## Conversation Summary` as the first system-role
  `ChatHistory` message, before recent verbatim turns, instead of appending it to the system prefix.
  Runtime/world-state context from the existing per-role/global context providers is emitted as the final
  system-role `## World State` tail message. `## Memory` remains a stable prefix snapshot; changes after that
  snapshot are emitted as `## Memory (updates)` tail content and consolidated only at compaction/retry boundaries.

The reason today's design breaks caching is **placement, not content**: the `## Memory` block and
`## Conversation Summary` are injected into the **top-level `system`** (the prefix), so every memory update or
re-summarization changes the prefix and invalidates the provider cache. Fix it by splitting context into:

- **Frozen prefix (cached):** persona / system playbook, **tool definitions**, and (optionally) a stable
  *global* memory slice. Never rewritten mid-session.
- **Volatile tail (cheap to reprocess):** the **current memory snapshot**, the conversation summary, and the
  **dynamic world-state observation** (§8) — emitted at the **end** of `messages[]`, after the cache breakpoint:
  - Anthropic-style: a `role: "system"` message appended after history (beta `mid-conversation-system`) — carries
    operator authority, leaves the cached prefix intact.
  - OpenAI/DeepSeek-compatible (current backend): a system/user message in the tail; auto prefix-caching covers
    the stable head.

Result: the large prefix caches; the small live tail updates every turn. **Memory executes in full AND the cache
survives.** Caching is not the priority — but this placement costs nothing extra and also fixes the "amnesia"
(we stop rewriting the prefix and keep recent turns verbatim). Cache *tiers* help too: a `system` change
invalidates system+messages but **not** tools; a tail-only `messages` change invalidates neither tools nor system.

### 1a caching вЂ” verification & provider notes
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

### 2. Compaction by threshold (not every turn)
- Status: **implemented.** `ConversationCompactionTriggerRatio` defaults to `0.8`; invalid/unset per-request
  values use the legacy budget boundary. Below the trigger, the prompt keeps all history verbatim and does not
  rewrite the stored rolling summary.
- Run condensation only when approaching the context limit. Produce an **anchored summary** that replaces the
  **oldest** turns; keep the most recent N turns verbatim while they fit.
- Re-summarize infrequently so the cached prefix survives across turns.

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

**Placement & consolidation (the cache-safe rule).** Tie memory placement to *cache boundaries*, not to each turn:

1. **Mid-session** an incremental edit goes into a small **"memory delta" block in the tail** — the cached
   prefix stays intact, the change is visible to the model immediately.
2. **At a boundary** — session start, **after summarization/compaction**, or an explicit reload — the prefix is
   rebuilt anyway (cache already cold), so **consolidate** the accumulated deltas into the **canonical memory
   snapshot in the prefix** and clear the tail deltas. Re-embedding memory into `system` at a boundary is free.

So two levels live in the prompt: a **canonical snapshot** (prefix, refreshed only at boundaries) and
**this-segment deltas** (tail, updated as the agent edits). This is the formal answer to "recompute memory into
the system prompt when the session summarizes/restarts" — yes, the boundary is exactly the consolidation point.

- **Memory is always fully readable.** The full memory is present in context every turn (canonical prefix +
  tail deltas), so the model never loses it; the explicit `read` action lets the agent re-read the whole memory
  on demand. Caching is *not* the goal — keeping the system prompt stable is only a
  side effect of "the model already remembers"; at boundaries (new session / summarization / truncation) the
  cache breaks anyway, so that is exactly when we refresh memory into the system prompt.

- **Layered scopes**, each mapping onto the above:
  - `global / persona` — rarely changes → canonical **prefix**.
  - `per-user` (e.g. the student profile) and `per-session` — change often → tail deltas, consolidated at boundaries.
- Durable across sessions (survives WebGL restart). Game-side requirement: RedoSchool `MVP_TODO.md`
  → *2.2 Персональная память ученика*.
- **Inspector:** extend the Agent Session Inspector to show memory deltas + version history.

### 6a. Conditional tool contract (native vs text-shaped backends)
Status: implemented for the current prompt formatter path.

The `## Tool Contract` block now keeps the full tool list + JSON schema in the **system prompt** only when the
backend cannot receive native tools (local GGUF / text-shaped extraction). Native tool-calling backends receive
only the minimal contract guidance because the schema is already sent in the native `tools` array. This saves
tokens and keeps the prefix lean while remaining deterministic for caching.

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
  ordinal-by-name ordering, text-shaped tool schemas are compacted with recursively sorted object keys, and
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
