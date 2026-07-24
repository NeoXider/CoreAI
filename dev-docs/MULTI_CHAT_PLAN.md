# Multi-Chat Support — Design & Plan

> Audit of the current CoreAI chat architecture and a phased plan for two target
> scenarios: **(A)** two chats running at once (e.g. an NPC world-space dialogue
> alongside the main GameMaster chat) and **(B)** one shared conversation rendered
> across several surfaces (CoreAI chat ↔ Hub chat ↔ a custom game chat), kept in
> sync.

---

## 0. Executive summary / recommendation

Today a **conversation is identified by `roleId`**. History, tool-call routing,
idle-timeout re-arm, stop, and clear are all keyed on the role. Everything that
"multi-chat" needs — two independent live turns, or two views of one live turn —
runs into that single assumption.

The central finding, and the spine of this plan:

- **History is stored per `roleId`** (`IAgentMemoryStore.GetChatHistory(roleId)` /
  `AppendChatMessage(roleId, …)`, driven from `AiOrchestrator.SanitizeAndPublish`,
  `AiOrchestrator.cs:1244-1258`). Two chats on the *same* role share and clobber
  the same transcript.
- **Cancellation scope is the role** — `CoreAiChatPanel.BuildAiTaskRequest` sets
  `CancellationScope = roleId` (`CoreAiChatPanel.cs:2383`) and `StopAgent(roleId)`
  → `QueuedAiOrchestrator.CancelTasks(scope)` matches on that string
  (`QueuedAiOrchestrator.cs:305-333`). Stopping one chat stops *every* in-flight
  turn for that role.
- **Tool-call feedback is role-filtered, not turn-correlated.** The panel's display
  handler drops any event whose `roleId != ActiveRoleId`
  (`CoreAiChatPanel.cs:3009,3019`), and the service's idle-deadline re-arm matches
  only on role (`CoreAiChatService.cs:212-217`). Both use the **static** events on
  `CoreAi` (`CoreAi.cs:533-539`). Two concurrent turns on the same role cross-arm
  and cross-attribute each other.

Good news: **`TraceId` already exists end-to-end** — on `AiTaskRequest.TraceId`
(`AiTaskRequest.cs:57`), honored by `AiOrchestrator.BuildRequestAsync`
(`AiOrchestrator.cs:92-94`), and carried on every low-level tool-call event via
`LlmToolCallInfo.TraceId` (`LlmToolCallInfo.cs:19`; surfaced on
`LlmToolCallStarted/Completed/Failed`). The correlation key is present; consumers
just don't use it yet.

**Recommendation.** Do *not* attempt a global refactor. Proceed in phases:

1. **Scenario A with distinct roles (near-free).** A per-NPC role already gives
   independent history, tools, and context budget. The dropdown in
   `CoreAiChatPanel` proves role-switching works. Ship NPC chat as "one role per
   persona / per NPC-archetype" first — no core change.
2. **Fix concurrent-turn cross-talk (small-to-medium).** Adopt `TraceId` (or a new
   `ConversationId`) as the correlation key in the two consumers that today filter
   by role only, so two live turns — even on the same role — stop cross-arming the
   idle deadline and cross-rendering tool bubbles.
3. **Introduce `ConversationId` for true same-role multi-chat and shared chat
   (deep).** Key history, stop/clear, and event streams on a conversation id
   alongside `roleId`. This is the real refactor and underpins both same-role
   Scenario A and all of Scenario B.
4. **Scenario B shared chat (medium, on top of 3).** One `CoreAiChatService`
   session as source of truth, N views subscribing to a per-conversation event
   stream and re-rendering the same transcript.

Defer: multi-session save/load UI, cross-conversation context sharing, and
parallel turns against a single local endpoint (serialize instead).

---

## 1. Audit — how a "chat" is wired today

### 1.1 The service layer (`CoreAiChatService`)

`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs`

- Every public entry point takes a **`roleId`** (or an `AiTaskRequest` whose
  `RoleId` is the only identity): `SendMessageAsync` (`:151`, `:183`),
  `SendMessageStreamingAsync` (`:260`, `:288`), `SendMessageSmartAsync` (`:511`,
  `:546`), `ClearHistory(roleId)` (`:365`), `TryGetPersistedChatHistory(roleId)`
  (`:373`), `StopAgent(roleId)` (`:392`). There is **no conversation/session id**
  anywhere in this type.
- `TryCreateFromScene()` (`:52`) resolves one orchestrator + policy + store from
  the single `CoreAILifetimeScope`. The facade caches exactly one `CoreAiChatService`
  (`CoreAi.cs:43,733-751`). All chats share the same service singleton.
- **Idle-timeout re-arm is role-filtered.** `SendMessageAsync` subscribes to the
  static `CoreAi.OnToolCallStarted/Completed/Failed` and re-arms the deadline when
  `evt.RoleId` matches `request.RoleId` (`:210-220`). The `Matches` helper is
  *lenient on an absent role* (`:212-214`) — meaning an unattributed event re-arms
  **every** in-flight turn. With two turns on the same role, either turn's tool
  activity re-arms *both* deadlines.
- Streaming re-arm (`:333`) is per-chunk on the returned enumerator, so it is
  naturally per-call — but it shares the single WebGL fetch bridge (see §6.4).

### 1.2 The panel (`CoreAiChatPanel`)

`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` (3637 lines —
already a god-class; see backlog §7).

- Holds `_activeRoleId` and the computed `ActiveRoleId`
  (`:615,633-635`). The **agent dropdown** (`_agentDropdown`, `:864-884`) switches
  `_activeRoleId` and, on switch, **stops the previous role's turn** and reloads
  that role's history (`OnAgentDropdownChanged`, `:886-932`). This is *sequential*
  role-switching, not concurrency.
- **`_roleTranscriptCache`** (`:630`) — an in-memory `Dictionary<roleId, List<(text,
  isUser)>>` of rendered bubbles, so a live-but-unpersisted conversation survives a
  switch-away-and-back. It is keyed **by role**, one panel-local cache. Two panels
  each keep their own; nothing syncs them.
- Startup hydration (`HydrateStartupMessagesFromStore`, `:1587`) clears the scroll,
  reloads persisted history for `ActiveRoleId`, else restores the role cache, else
  shows the welcome message. Purely per-role.
- **Tool-call display is role-filtered.** `TryRegisterToolCallChatDisplay` subscribes
  to `CoreAi.OnToolExecuted` (`:2985-2986`); `OnToolExecutedChatDisplay` early-returns
  unless `roleId == ActiveRoleId` (`:3009,3019`). Note `OnToolExecuted`'s signature
  is `(roleId, toolName, args, result)` (`CoreAi.cs:521`) — it carries **no
  TraceId**, so the panel display path *cannot* tell two same-role turns apart even
  if it wanted to.
- `_currentTurnGeneration` (`:113`) is a monotonic per-panel guard so a superseded
  turn cannot write into a newer turn's bubbles — an *intra-panel* safety valve, not
  a cross-conversation mechanism.
- Request construction: `BuildAiTaskRequest` (`:2375-2385`) sets `RoleId`,
  `RoutingProfileId`, `SourceTag="Chat"`, and `CancellationScope = roleId`. **The
  panel never sets `TraceId`** — so today every chat turn gets a fresh random trace
  from the orchestrator, and the panel has no handle to correlate the events it
  receives back to the specific turn it started.
- Embedded mode (`CreateEmbedded`, `:364`) already lets the *same panel class* render
  into an arbitrary `VisualElement` (used by the Hub). This is the seam Scenario B
  builds on — but each embedded panel is still an independent view over per-role
  state, not a shared session.

### 1.3 The in-world player chat (`InGameChatPanel` + `InGameLlmChatService`)

`Assets/CoreAiUnity/Runtime/Source/Features/PlayerChat/Presentation/InGameChatPanel.cs`
and `Assets/CoreAI/Runtime/Core/Features/Orchestration/InGameLlmChatService.cs`

- A **completely separate, simpler stack**: uWGUI (`TMP_InputField`/`TMP_Text`, not
  UITK), talks to `IInGameLlmChatService.SendPlayerMessageAsync` (`InGameChatPanel.cs:95`).
- `InGameLlmChatService` keeps its **own in-memory `_turns` list**
  (`InGameLlmChatService.cs:18`), bypasses the orchestrator entirely (calls
  `ILlmClient.CompleteAsync` directly, `:127`), has **no tools, no memory policy, no
  role history store, no streaming**, and its own `SmartChat` system prompt.
- It has a `SemaphoreSlim _requestGate` (`:26`) that **serializes overlapping
  requests** and a **sliding-window rate limiter** (`_maxRequestsPerWindow`,
  `TryAcquireRateSlot`, `:188-214`). It generates a fresh `TraceId` per call (`:133`).
- Takeaway: there are **already two divergent chat implementations**. Scenario A
  ("NPC chat alongside GameMaster chat") could reuse this lightweight service, but it
  is a *dead-end fork* — no tools, no world-state, no shared history model. A
  multi-chat design should converge NPC chat onto the orchestrator path with a
  distinct role, not extend this fork.

### 1.4 Per-role memory & policy (`AgentMemoryPolicy`)

`Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs`

- **Everything is keyed by `roleId`**: tools (`_customTools`, `:15`), skill catalogs
  (`_roleSkillCatalogs`, `:17`), runtime-context providers (`:20`), per-role config
  (`_roleConfigs`, `RoleMemoryConfig` — history on/off, persist, context tokens, max
  history messages, tool-roundtrip cap, temperature, compaction; `:191-272`),
  additional system prompts (`:698`), universal-prefix override (`:700`), streaming
  override (`:702`).
- There is **no notion of a per-conversation policy or per-conversation history**.
  `ConfigureChatHistory(roleId, …)` (`:274`) and friends all mutate role-global state.
  Two chats on one role therefore *share* tools, skills, context budget, and the
  single persisted transcript.
- Consequence for Scenario A: giving each NPC a **distinct role** is the natural way
  to get independent persona prompt, tools, history, and context budget — the policy
  is already built for exactly that granularity.

### 1.5 The orchestrator (`AiOrchestrator` / `AiTaskRequest`)

`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs`,
`.../AiTaskRequest.cs`

- `BuildRequestAsync` (`:86`) resolves identity as `roleId` (`:91`) and `traceId`
  (`:92-94`, honoring `task.TraceId` when supplied, else `Guid.NewGuid()`). **The
  memory store is read and written by `roleId` only** — `_memoryStore.TryLoad(roleId)`
  (`:104`), `GetChatHistory(roleId, …)` in `BuildChatHistoryAsync` (`:1011`), and the
  three `AppendChatMessage(bundle.RoleId, …)` writes in `SanitizeAndPublish`
  (`:1244-1258`). **`TraceId` never keys history** — it is used only for audit hash,
  trace records, metrics, and command envelopes (`:219,1147,1262-1273`).
- `AiTaskRequest` (`AiTaskRequest.cs`) carries `RoleId` (`:11`), `RoutingProfileId`
  (`:17`), `TraceId` (`:57`), `SourceTag` (`:65`), `CancellationScope` (`:70`) — but
  **no `ConversationId` / `SessionId`**. The closest thing to a conversation handle is
  the `roleId` (identity) or a caller-chosen `CancellationScope` (lifecycle only).
- Context-window budget is per-role/per-request (`roleConfig.ContextTokens` or global,
  clamped to the routed endpoint window; `:127-140`). Two conversations on one role
  contend for one budget derived from one growing transcript.

### 1.6 The facade (`CoreAi`)

`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`

- **Single-scope, single-service, single-set-of-static-events.** One cached
  `CoreAILifetimeScope` (`:42`), one `CoreAiChatService` (`:43`), one orchestrator
  (`:44`).
- **Tool-call events are static** — `OnToolExecuted` (`:530`),
  `OnToolCallStarted/Completed/Failed` (`:533-539`), plus a bounded
  `ToolCallHistory` (`:48`) and `SubscribeToolCalls` (`:548`). All subscribers see all
  events for all roles/turns. Every consumer must filter itself.
- `StopAgent(cancellationScope)` (`:651`) and `ClearContext(roleId, …)` (`:663`) are
  role/scope-scoped, not conversation-scoped.
- Because the events are static and process-wide, they are the single biggest
  obstacle to *parallel* turns: there is no per-turn or per-conversation channel.

### 1.7 The central constraint (restated)

> **A "conversation" == a `roleId`.** History keying, stop, clear, idle-timeout, and
> tool-call attribution all resolve through the role. `TraceId` uniquely identifies a
> *turn* end-to-end but keys nothing durable. There is no `ConversationId`.

| Case | Works today? | Why |
|---|---|---|
| Two chats, **different** roles, **sequential** | ✅ | Dropdown switch stops prev role, reloads new role history |
| Two chats, **different** roles, **parallel** | ⚠️ partial | Independent history/tools, but static events cross-attribute in same-role tools; single endpoint serializes; idle-deadline lenient re-arm |
| Two chats, **same** role, either mode | ❌ | Shared/clobbered history, stop stops both, tool bubbles cross-render |
| One conversation, **N views** (shared) | ❌ | No shared-session object; each view is an independent per-role reader with its own cache |

---

## 2. Scenario A — two chats at once (NPC dialogue ⧺ GameMaster chat)

### 2.1 Approach A1 — distinct role per NPC/persona (RECOMMENDED MVP)

Give each NPC (or NPC archetype) its **own `roleId`** (`npc.blacksmith`,
`npc.guard`, or per-instance `npc.<guid>`). Register the persona via the existing
per-role machinery:

- persona system prompt → `AgentMemoryPolicy.SetAdditionalSystemPrompt(roleId, …)`
  (`AgentMemoryPolicy.cs:707`) or a registered `AgentConfig`;
- persona tools/skills → `SetToolsForRole` / `AddSkillForRole` (`:26,89`);
- independent history + budget → `RoleMemoryConfig` per role (`:191`);
- world-space UITK render → `CoreAiChatPanel.CreateEmbedded(worldSpaceHost, …)`
  (`CoreAiChatPanel.cs:364`) with a `PanelRenderer` in `WorldSpace` mode (the panel
  already handles world-space sizing, `:505-531`), **or** a separate screen panel.

**Pros:** zero core change; independent history, tools, persona, context budget for
free; the GameMaster chat is just another role. Matches how `AgentMemoryPolicy` is
already designed.

**Cons / gaps to close even in A1:**

- **Static-event cross-attribution.** If the GameMaster and an NPC turn are in flight
  simultaneously *and happen to share a role* (they won't in A1) the tool bubbles
  cross-render. With *distinct* roles this is mostly fine because both the panel
  filter (`CoreAiChatPanel.cs:3019`) and the service re-arm (`CoreAiChatService.cs:214`)
  key on role — **except** for the "absent role ⇒ matches everyone" leniency
  (`:212-214`), which can let an unattributed event re-arm the wrong turn's deadline.
  Fix in Phase 2 (§5) by also correlating on `TraceId`.
- **Per-instance NPC history.** "Every guard shares `npc.guard` history" may be
  undesirable (guard A remembers guard B's conversation). Per-instance roles
  (`npc.<guid>`) fix it but multiply persisted transcripts and long-term memory
  entries (see risks §6.6) — this is really the same need as a `ConversationId`
  (Phase 3).
- **Model contention (single local endpoint).** Two live turns against one LM Studio
  model serialize at the provider; the second NPC "thinks" only after the first
  finishes. Acceptable for MVP; see §6.4.

### 2.2 Approach A2 — same role, two conversations (needs Phase 3)

If two chats must share a persona/role but keep separate transcripts (e.g. the same
GameMaster talking to two players), a **`ConversationId`** is mandatory (§4). Without
it the two turns write into one `roleId` transcript and clobber each other via
`SanitizeAndPublish` (`AiOrchestrator.cs:1244-1258`).

### 2.3 Render surface

- **World-space UITK**: reuse `CoreAiChatPanel` embedded/world-space paths. Keeps
  streaming, tool bubbles, markdown, the whole feature set. Heavier per NPC.
- **Separate screen panel**: a second `CoreAiChatPanel` instance bound to a distinct
  role — trivial today.
- **Lightweight bubble** (barks): the `InGameChatPanel`/`InGameLlmChatService` path is
  cheaper but a feature dead-end (§1.3). Prefer converging on the orchestrator path
  with `ForcedToolMode.None` and a small context budget for barks rather than growing
  the fork.

---

## 3. Scenario B — one shared conversation across surfaces

Goal: the CoreAI chat, the Hub chat, and a custom game chat are the **same session**
(same history, same live stream), rendered in ≥2 places and kept in sync.

### 3.1 The shape of the problem

Today each surface is an independent `CoreAiChatPanel` (or the Hub's embedded panel)
that *reads the same per-role store*. If they all point at the same `roleId` they get
the same **persisted** history on hydrate — but:

- they do **not** share the in-memory `_roleTranscriptCache` (`CoreAiChatPanel.cs:630`),
  so unpersisted turns diverge;
- during **streaming**, only the panel that *initiated* the turn renders chunks (it
  owns the `IAsyncEnumerable`); the other panels see nothing until they re-hydrate;
- a turn sent from panel 1 does not appear in panel 2 until panel 2 reloads;
- tool bubbles render only in the panel whose `ActiveRoleId` matches *and* that is
  looking (`:3019`) — but both would render if both are on the role, with no deltas.

### 3.2 Approach B1 — shared session object + observer views (RECOMMENDED)

Introduce a **`ChatSession`** (a.k.a. conversation aggregate) that is the single
source of truth for one conversation:

- Owns the ordered transcript (the authoritative in-memory list, hydrated once from
  the store), the current streaming buffer, and the in-flight turn handle.
- Exposes an **event stream**: `MessageAppended`, `StreamChunk(convId, turnId, text)`,
  `ToolBubble(convId, turnId, …)`, `TurnStarted/Completed`, `HistoryCleared`.
- Runs exactly one turn pipeline; **views never call the orchestrator directly** —
  they call `session.Send(text)` and *subscribe* for render.

Each surface becomes a thin **view** that:

1. on attach, renders `session.Transcript` (replaces per-panel hydration);
2. subscribes to the session's events and appends/updates bubbles;
3. forwards user input to `session.Send`.

`CoreAiChatPanel.CreateEmbedded` (`:364`) already proves a panel can be a pure view
over an external host; B1 generalizes that: the panel binds to a `ChatSession`
instead of resolving its own service and per-role cache.

**Streaming sync.** The session accumulates chunks and re-broadcasts them as
`StreamChunk` events; every subscribed view appends to *its* streaming bubble. Only
the session holds the enumerator, so N views stay in lock-step regardless of which
one hit "send". The per-view `_currentTurnGeneration` guard (`:113`) still protects
each view locally.

**Pros:** true sync, single history, single live stream, single stop/clear. Reuses
the existing render code as views. **Cons:** requires lifting session state out of
`CoreAiChatPanel` (which today *is* the session), i.e. the god-class split in §7;
needs the per-conversation event stream from Phase 3 (§4) to avoid the static-event
cross-talk.

### 3.3 Approach B2 — store-as-bus (cheap, weak)

Make the per-role store observable (`ChatHistoryChanged(roleId)`) and have every panel
re-hydrate on change. Cheap, but: no streaming sync (only final messages appear
post-turn), re-hydration flicker, and still no way to render a live turn started
elsewhere. Acceptable only as an interim "eventually-consistent" step for
non-streaming surfaces. Not recommended as the target.

### 3.4 Where the per-role cache and store fit

- `_roleTranscriptCache` moves *into* `ChatSession` and becomes the session
  transcript; the per-panel dictionary (`:630`) is deleted.
- Store hydration happens once per session (not once per view). `TryGetPersistedChatHistory`
  (`CoreAiChatService.cs:373`) seeds the session; `AppendChatMessage`
  (`AiOrchestrator.cs:1244`) still persists — but the session must reconcile the
  orchestrator's persisted turn with its own in-memory copy to avoid double-append
  (the orchestrator writes history *inside* `SanitizeAndPublish`, so the session
  should treat the store as write-through, not re-append).

---

## 4. The core change — introduce `ConversationId`

Both A2 and B need a conversation identity distinct from `roleId`. `TraceId` is
per-*turn* (a new one each turn), so it cannot key durable history. Add a
**`ConversationId`** (stable across turns; a role may host many).

Minimum surface area:

1. **`AiTaskRequest.ConversationId`** (new; `AiTaskRequest.cs`). Empty ⇒ default to
   `roleId` for 100% back-compat (existing single-chat installs behave identically).
2. **Memory store keyed by (roleId, conversationId).** `IAgentMemoryStore` gains
   conversation-scoped overloads: `GetChatHistory(roleId, conversationId, max)`,
   `AppendChatMessage(roleId, conversationId, …)`, `ClearChatHistory(roleId,
   conversationId)`. Legacy overloads delegate with `conversationId == roleId`. This
   is the highest-effort, highest-value change — it touches `BuildChatHistoryAsync`
   (`AiOrchestrator.cs:1011`) and the three appends in `SanitizeAndPublish`
   (`:1244-1258`).
3. **Cancellation scope becomes the conversation.** `CoreAiChatPanel.BuildAiTaskRequest`
   sets `CancellationScope = conversationId` instead of `roleId`
   (`CoreAiChatPanel.cs:2383`); `StopAgent`/`CancelTasks` already match on the scope
   string (`QueuedAiOrchestrator.cs:305-333`) so **no orchestrator change** is needed
   — just pass the conversation id as the scope. Stop now scopes to one chat.
4. **Per-conversation event correlation.** Tool-call events already carry `TraceId`
   (`LlmToolCallInfo.cs:19`); add `ConversationId` to `LlmToolCallInfo` (populated
   from the request), and have consumers filter on it:
   - `CoreAiChatService` idle re-arm: match `evt.ConversationId == request.ConversationId`
     (replace the role match at `CoreAiChatService.cs:212-217`), and drop the
     "absent role matches everyone" leniency in the multi-chat case.
   - Panel tool display: filter on conversation id, not `ActiveRoleId`
     (`CoreAiChatPanel.cs:3009,3019`). This requires threading conversation id through
     `OnToolExecuted` (`CoreAi.cs:521`), which today lacks even `TraceId`.
5. **Policy stays role-scoped** (tools, skills, persona, context budget) — that is
   correct; personas belong to roles. Only *history* and *lifecycle* become
   conversation-scoped. (A later option: per-conversation context-budget override if
   two conversations on one role need different windows.)

This keeps `roleId` = "who the agent is" and `ConversationId` = "which thread", which
is the clean separation the codebase is missing.

---

## 5. Parallel LLM turns — what breaks with 2 in-flight

Concretely, with two turns live at once:

1. **Idle-timeout cross-arm.** `CoreAiChatService` re-arms turn X's deadline on turn
   Y's tool events because the filter is role-only and *lenient on absent role*
   (`CoreAiChatService.cs:212-217`). Two same-role turns → each keeps the other alive
   (or, worse, an unattributed event keeps a stalled turn alive). **Fix:** correlate
   on `ConversationId`/`TraceId` (§4.4).
2. **Tool-bubble cross-render.** Both panels on the same role render both turns' tool
   bubbles (`CoreAiChatPanel.cs:3019`), interleaved and misattributed. `OnToolExecuted`
   carries no correlation id at all (`CoreAi.cs:521`). **Fix:** add conversation/trace
   id to the event and filter on it.
3. **Static event fan-out.** All of `OnToolCall*` (`CoreAi.cs:533-539`) and
   `ToolCallHistory` (`:48`) are process-global. Every subscriber sees every turn.
   Per-turn correlation (`TraceId`, already present) or a per-conversation event
   channel is required to demux. Recommended: keep the static events (back-compat) but
   add a `ConversationId` field and a helper `SubscribeConversation(convId, …)` that
   filters.
4. **Command envelope attribution.** `SanitizeAndPublish` publishes `ApplyAiGameCommand`
   with `SourceRoleId`/`TraceId` (`AiOrchestrator.cs:1262-1273`). Downstream command
   consumers that key on role will merge two conversations. Add `ConversationId` to the
   envelope.
5. **History interleave (same role).** Two turns both `AppendChatMessage(roleId, …)`
   in undefined order (`:1244-1258`) → corrupted transcript. Only §4.2 (conversation
   keying) fixes this; there is no lock that would make same-role same-store appends
   coherent.

**Verdict:** per-turn `TraceId` correlation is necessary and *already available* for
the timeout/bubble consumers (Phase 2). Durable same-role parallelism additionally
needs `ConversationId` history keying (Phase 3).

---

## 6. Problems & risks (enumerated)

1. **History clobber (same role).** Two chats on one role share one persisted
   transcript and interleave appends (`AiOrchestrator.cs:1244-1258`,
   `AgentMemoryPolicy` role-keyed). Blocks A2 and B until §4.2.
2. **Event cross-talk.** Static role-filtered tool events cross-attribute concurrent
   turns (`CoreAi.cs:533-539`, `CoreAiChatService.cs:214`, `CoreAiChatPanel.cs:3019`);
   `OnToolExecuted` has no correlation id (`CoreAi.cs:521`).
3. **Stop/clear scope too coarse.** `StopAgent(roleId)` / `ClearContext(roleId)`
   (`CoreAi.cs:651,663`) and the panel's switch-time `StopAgent(previousRole)`
   (`CoreAiChatPanel.cs:908`) hit *all* conversations on the role. Fix by scoping to
   `ConversationId` (§4.3).
4. **Idle-deadline leniency.** "Absent role matches everyone"
   (`CoreAiChatService.cs:212-214`) mis-arms deadlines under concurrency.
5. **Rate limits.** `InGameLlmChatService` has a per-service sliding window
   (`InGameLlmChatService.cs:188-214`) — many NPCs on that path share one budget and
   throttle each other; the orchestrator path has none there but relies on provider
   limits. Multiple live conversations multiply request rate against cloud quotas.
6. **Context-window budget.** Per-role/global window
   (`AiOrchestrator.cs:127-140`); N conversations on one role derive their budget from
   one ever-growing transcript. Per-instance NPC roles multiply long-term memory and
   persisted transcripts (`AgentMemoryPolicy` role-keyed everything). Need per-conversation
   budgeting + eviction (Phase 3).
7. **Model contention.** One local LM Studio endpoint = one model, serialized; parallel
   turns queue. `QueuedAiOrchestrator._maxConcurrent` (`:17,160`) governs pipeline
   concurrency but the provider still serializes a single local model.
8. **UI sync races (Scenario B).** N views, one stream: without a single session owner
   (§3.2) views diverge; streaming appears only in the initiating view. The
   `_currentTurnGeneration` guard (`CoreAiChatPanel.cs:113`) is per-panel only.
9. **Memory growth.** Per-instance NPC roles/conversations grow the store unbounded;
   the `_roleTranscriptCache` (`:630`) grows per role per panel. Need conversation
   eviction/TTL and a cap on live sessions.
10. **Save/load of multiple sessions.** No schema for enumerating/persisting many
    conversations per role; the store is keyed by role only. Requires §4.2 plus a
    conversation index. Defer the UI.
11. **Two divergent chat stacks.** `CoreAiChatPanel`/orchestrator vs
    `InGameChatPanel`/`InGameLlmChatService` (§1.3). Multi-chat work risks a third
    fork unless NPC chat converges on the orchestrator path.
12. **WebGL single fetch bridge.** Concurrent streaming turns share one native SSE
    bridge (`CoreAiChatService.cs:397-436`, `WebGlNativeStreaming`); parallel streams
    on WebGL are effectively serialized/interleaved and must be tested (§6.4 below).
13. **God-class coupling.** `CoreAiChatPanel` (3637 lines) *is* the session; Scenario B
    cannot cleanly add views without extracting a session object (§7).

---

## 7. Intersection with existing backlog

- **Per-role history refactor** → this plan's `ConversationId` (Phase 3) is the
  natural home; do them together (both touch `IAgentMemoryStore` keying,
  `BuildChatHistoryAsync` `AiOrchestrator.cs:1011`, `SanitizeAndPublish` `:1244`).
- **`TraceId` correlation** → already threaded (`AiTaskRequest.cs:57`,
  `LlmToolCallInfo.cs:19`); Phase 2 simply *consumes* it in the timeout/bubble filters.
  Adding `ConversationId` beside it is incremental.
- **`CoreAiChatPanel` god-class split** → Scenario B *requires* extracting a
  `ChatSession` (state) from the panel (view). Doing the split for multi-chat also pays
  down that backlog item. Prioritize extracting: transcript ownership, streaming
  buffer, turn lifecycle, `_roleTranscriptCache` → `ChatSession`.

---

## 8. Phased plan

### Phase 0 — Foundations (no behavior change)
- Add `AiTaskRequest.ConversationId` (default empty ⇒ falls back to `roleId`).
- Add `ConversationId` to `LlmToolCallInfo` (populated from the request) and to
  `ApplyAiGameCommand`.
- Add `ConversationId` to the `OnToolExecuted` handler signature (or introduce a
  richer `OnToolExecutedDetailed`) so the panel can correlate.
- **Deliverable:** ids flow end-to-end; nothing filters on them yet. Green tests.

### Phase 1 — Scenario A MVP (distinct roles) — *smallest shippable*
- NPC persona = distinct `roleId` via `AgentConfig`/`AgentMemoryPolicy`
  (`SetAdditionalSystemPrompt`, `SetToolsForRole`).
- Render NPC chat with `CoreAiChatPanel.CreateEmbedded` (world-space) or a second
  screen panel; GameMaster chat unchanged.
- Serialize turns against a single local endpoint; document cloud rate-limit
  expectations.
- **Deliverable:** an NPC dialogue running alongside GameMaster chat, independent
  history/persona, sequential (or provider-serialized) turns. **No core refactor.**

### Phase 2 — Concurrent-turn correctness (correlation)
- `CoreAiChatService` idle re-arm filters on `ConversationId`/`TraceId`
  (`CoreAiChatService.cs:212-217`); remove the absent-role leniency under multi-chat.
- Panel tool-bubble display filters on conversation/trace id
  (`CoreAiChatPanel.cs:3009,3019`).
- Panel sets `TraceId` per turn and `ConversationId` in `BuildAiTaskRequest`
  (`:2375-2385`).
- **Deliverable:** two live turns no longer cross-arm deadlines or cross-render tool
  bubbles. Enables reliable *parallel* Scenario A (distinct roles) and same-role
  turns' UI.

### Phase 3 — `ConversationId` history & lifecycle (the real refactor)
- `IAgentMemoryStore` conversation-scoped overloads; thread through
  `BuildChatHistoryAsync` (`AiOrchestrator.cs:1011`) and `SanitizeAndPublish`
  (`:1244-1258`).
- `CancellationScope = ConversationId` (`CoreAiChatPanel.cs:2383`); `StopAgent`/
  `ClearContext` scope to conversation.
- Optional per-conversation context-budget override.
- Conversation index + eviction/TTL for memory growth (risk §6.6/§6.9).
- **Deliverable:** two chats on the **same** role with independent, non-clobbering
  history and scoped stop/clear. Unlocks A2 and the data model for B.

### Phase 4 — Scenario B shared chat (views over a session)
- Extract `ChatSession` from `CoreAiChatPanel` (transcript, stream buffer, turn
  lifecycle, cache) — pays down the god-class backlog (§7).
- `CoreAiChatPanel` becomes a view bound to a `ChatSession`; Hub embedded panel and
  custom game chat bind to the same session instance.
- Per-conversation event stream (`MessageAppended`, `StreamChunk`, `ToolBubble`,
  `TurnStarted/Completed`) drives all views; single owner holds the enumerator so
  streaming stays in sync.
- **Deliverable:** CoreAI chat ↔ Hub chat ↔ game chat as one live, synced session.

### Deferred
- Multi-session save/load UI and cross-conversation memory sharing.
- True parallel inference against a single local model (needs multiple endpoints /
  model instances) — serialize until then.
- Converging/retiring the `InGameLlmChatService` fork onto the orchestrator path
  (worthwhile cleanup, not on the multi-chat critical path).

---

## 9. Key file/line reference

| Concern | Location |
|---|---|
| Chat service, role-only API, idle re-arm | `CoreAiChatService.cs:151,183,212-220,260,365,392` |
| Panel active role, dropdown switch, caches | `CoreAiChatPanel.cs:615,630,633-635,864-932,1587` |
| Panel request build (`CancellationScope=roleId`) | `CoreAiChatPanel.cs:2375-2385` |
| Panel tool-bubble role filter | `CoreAiChatPanel.cs:3009,3019` |
| Panel embedded/world-space view seam | `CoreAiChatPanel.cs:364,505-531` |
| In-world player chat fork | `InGameChatPanel.cs:95`; `InGameLlmChatService.cs:18,26,127,188-214` |
| Per-role policy (tools/skills/history/budget) | `AgentMemoryPolicy.cs:15-20,191-272,274,707` |
| Orchestrator identity + history keying | `AiOrchestrator.cs:91-94,104,1011,1244-1258` |
| Orchestrator context budget | `AiOrchestrator.cs:127-140` |
| `AiTaskRequest` fields (no ConversationId) | `AiTaskRequest.cs:11,17,57,65,70` |
| Facade static events, stop/clear scope | `CoreAi.cs:43,521,530-539,548,651,663` |
| Cancellation scope matching | `QueuedAiOrchestrator.cs:17,160,305-333` |
| Tool-call event carries TraceId | `LlmToolCallInfo.cs:19`; `LlmToolCallStarted.cs:24` |
