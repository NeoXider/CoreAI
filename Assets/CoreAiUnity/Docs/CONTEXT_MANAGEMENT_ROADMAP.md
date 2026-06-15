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
- Status: **partially implemented behind `PlaceLiveContextInTail` (default off)** for `## Conversation Summary`
  only. The summary is prepended as the first tail message because it represents the evicted oldest turns;
  recent turns already remain verbatim after it. Memory deltas and world-state tail placement are still pending.
- Keep recent turns as **real `user`/`assistant`/`tool` messages** in the tail; do **not** rewrite the
  `system`/`tools` prefix each turn. The frozen prefix is what the provider caches.
- Volatile, per-turn content goes at the **end**, after the cache breakpoint.

### 1a. Prefix vs Tail — how live memory & summary coexist with caching (the core decision)
- Status: **summary-to-tail implemented behind `PlaceLiveContextInTail` (default off)**. When enabled,
  `AiOrchestrator.BuildChatHistoryAsync` emits `## Conversation Summary` as the first system-role
  `ChatHistory` message, before recent verbatim turns, instead of appending it to the system prefix.
  `## Memory` remains in the prefix for now; memory deltas/world-state are later steps near the end of the tail.

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

### 2. Compaction by threshold (not every turn)
- Run condensation only when approaching the context limit. Produce an **anchored summary** that replaces the
  **oldest** turns; keep the most recent N turns verbatim while they fit.
- Re-summarize infrequently so the cached prefix survives across turns.

### 3. Tool-result hygiene before truncation (`ToolResultMemoryPolicy`) - implemented
- New per-role policy: `None | ErrorsOnly | CompactSummary | Full` (default `CompactSummary`).
- Tool-call results are persisted into history per policy as one `tool` chat-history entry headed
  `## Tool Results`; stored tool entries replay as provider-safe user observations.
- Intra-turn duplicate results are collapsed by tool name + normalized detail. Cross-turn pruning of
  outdated/superseded results remains planned for §7.
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
- **Implemented:** on provider "context length exceeded" error, `AiOrchestrator.RunTaskAsync` rebuilds the
  request with a tighter `ContextRetryLevel` and retries up to `ICoreAISettings.MaxContextOverflowRetries`
  times (default 3, 0 disables). Each retry level applies a `0.75^level` history-budget factor, dropping
  roughly 25% more of the oldest context per pass, then fails normally after the bounded attempts.

### 6. Persistent memory — incremental, versioned, boundary-consolidated
Today the `memory` tool is coarse — `write / append / clear` (confirmed via the Agent Session Inspector).
Upgrade to a real editable memory:

- **Granular ops:** `append / str_replace / insert / delete / rename` over a structured memory doc — the agent
  edits, it does **not** rewrite the whole thing each time (model-decided when useful).
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
  tail deltas), so the model never loses it; additionally expose an explicit `view`/read action so the agent can
  re-read the whole memory on demand. Caching is *not* the goal — keeping the system prompt stable is only a
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
- Two complementary levers, like Claude Code: **compaction** summarizes old turns when near the limit (§2);
  **context editing** *prunes* stale content without summarizing — drop superseded tool results / old thinking
  blocks once they no longer matter. Prune first (cheap, lossless-ish), summarize only when still over budget.

### 8. Dynamic world-state observation (universal / game agent — beyond Claude Code/Cursor)
- This agent is not just a coder: it also drives **game mechanics, NPCs, lesson briefings**. Give it, each turn,
  a compact **world-state observation** in the tail — current scene, relevant NPC/quest/player state, the
  "current slide" in a briefing, etc. This is the game analog of Claude Code's file-context, but for live state.
- It is an **observation** (read-only context), not a command: the model still decides what to do. Lives in the
  **tail** so it stays cache-safe, and is built via a per-role provider so each role exposes only what it needs.

### 9. Deterministic serialization & per-role policy
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
- Changes are core (`com.nexoider.coreai`) + Unity adapter (`com.nexoider.coreaiunity`); ship behind config so
  existing roles keep working. Caching cooperation is provider-dependent (verify via `usage.cache_read_*` when present).
