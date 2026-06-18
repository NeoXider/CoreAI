# TODO

> Updated 2026-06-18. Completed v4.6.0 work is in `CHANGELOG.md` (both packages). Active
> MVP-blocking engineering TODOs are closed here; non-blocking future work lives in
> `Assets/CoreAiUnity/Docs/BACKLOG.md`.

## v4.0.0 - done (2026-06-12)

- [x] Lua as a second language: phases 1-5, capability tiers, `manage_mods`, sandbox/audit fixes.
- [x] Demo `Assets/CoreAI.Demos/`: LuaMods, WorldCommands, Skills, LiveMechanics (+ LLM chat).
- [x] `ICoreAiCustomWorldCommandHandler`, scene whitelist, perf (MPB `set_color`, `LuaModRuntime.Tick` scratch).
- [x] Documentation: `LUA_GAME_API`, `LUA_BEST_PRACTICES`, `MOONSHARP_NATIVE_APIS`, `LUA_ACCESS_MODES`, `PERF_REVIEW_2026-06-12`.
- [x] Version **4.0.0** in `com.nexoider.coreai` / `com.nexoider.coreaiunity`.
- [x] `IGameLogger` instead of `Debug.*` in CoreAiUnity Runtime.

## [P1] Full mode

> Currently available: `LuaCapabilities.Full`, reflection bindings `CoreAiFullUnityLuaRuntimeBindings` (`unity_*`), opt-in `enableFullLuaAccess`, documented in `LUA_ACCESS_MODES.md`.

- [x] **Demo scene** `FullAccess/FullAccessDemo.unity` (chat + scope with Full + auto-`TargetCube`, prompt buttons move/grow/inspect).
- [x] **PlayMode tests** Full: `unity_find` / `unity_set_position` on a scene object.
- [x] **Member visibility:** public-by-default, non-public is opt-in (`enableFullLuaPrivateAccess` / ctor `allowNonPublicMembers`) + EditMode tests.
- [x] **Migration to MoonSharp `UserData.RegisterType`** - closed by audit decision 2026-06-18: reflection cannot be removed completely without losing allow-all semantics (addressing a member by string = reflection; `UserData` in Reflection mode does the same). `LazyOptimized` breaks on IL2CPP (no JIT), and hardwiring is impossible for types that are not known in advance. The current cached reflection is the most AOT-portable option and is not on a hot path (admin/debug tier). Migration would provide more idiomatic syntax, not performance.
- [x] **Blacklist** types/members for Full: `IFullLuaAccessBlacklistPolicy` is wired into `CoreAiFullUnityLuaRuntimeBindings` and `WorldCommandsInstaller`, with EditMode coverage.

## [P1] Context management overhaul (Claude Code / Cline / Kilo-grade)

> Design fixed in `Assets/CoreAiUnity/Docs/CONTEXT_MANAGEMENT_ROADMAP.md`. Goal: history/context like
> Claude Code — recent turns verbatim, old turns condensed by threshold, tool results kept (policy-gated),
> token budgeting from real API `usage`, and a **stable cacheable prefix** (today's per-turn re-summarization
> into `system` breaks prompt caching). Tail placement is now the only supported CoreAI path; do not regress existing roles.

- [x] **Prefix vs Tail.** Move
      `## Conversation Summary` OUT of top-level `system` into the **tail** of `messages[]` as the first
      system-role message, before recent verbatim turns, because it summarizes the evicted oldest turns.
      Frozen prefix = persona + tool defs (+ current canonical memory) -> cacheable; live tail -> cheap.
- [x] **Stable cacheable prefix + verbatim recent turns.** Recent turns are kept as real `user`/`assistant`/`tool`
      messages in the tail; the `system`/`tools` prefix is no longer rewritten per turn for summary/world-state
      (both moved to the tail) and is deterministic (sorted tools/JSON, no
      timestamps/GUIDs). `## Memory` is now a stable prefix snapshot; live updates are emitted in tail.
- [x] **Compaction by threshold, not every turn.** Anchored summary replaces only the oldest turns when near the
      limit; re-summarize infrequently so the cached prefix survives.
- [x] **`ToolResultMemoryPolicy { None | ErrorsOnly | CompactSummary | Full }`** (per-role, default `CompactSummary`):
      persist tool results into history, collapse intra-turn duplicate results, head/tail-truncate large outputs.
      Built-in Programmer and CoreMechanicAI default to `Full`; other built-in roles keep `CompactSummary`.
      Cross-turn pruning of outdated/superseded results is handled by *Context editing (prune)*.
- [x] **Token accounting from API.** Calibrate chars→tokens from real `usage.prompt_tokens`
      (see `Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md`); `HeuristicTokenEstimator` stays a pre-flight
      fallback only, with a higher ratio for Cyrillic/CJK.
- [x] **Emergency overflow fallback.** On "context exceeded", drop ~25% oldest + retry (bounded); extend
      `ContextRetryLevel`.
- [x] **Prompt-caching cooperation verification.** Surface provider cache read/write token counts from MEAI
      `UsageDetails.AdditionalCounts` into usage records/events and diagnostics. Current OpenAI/DeepSeek-compatible
      backend auto-caches the stable prefix; Anthropic-style `cache_control` breakpoint notes live in the roadmap.
- [x] **6a Conditional tool contract.** Native tool-calling backends use a minimal prompt contract; text-shaped
      local backends keep the full tool list, schema, and JSON-call guidance.
- [x] **Context editing (prune) on top of compaction.** Prune superseded tool results / stale thinking first
      (lossless), summarize only when still over budget.
- [x] **Persistent memory — incremental & versioned.** `append/str_replace/insert/delete` (agent-decided, not
      overwrite) + per-mutation versions for audit/rollback; layered scopes (global=prefix, per-user/per-session=tail).
      Pairs with RedoSchool `MVP_TODO.md` → *2.2 Персональная память ученика*.
- [x] **Memory read + wait tool.** The `memory` tool supports `action=read`; `WaitLlmTool` / `AgentBuilder.WithWaitTool`
      lets an agent pause before polling or retrying inside a tool-calling turn.
- [x] **Dynamic world-state observation (universal/game agent).** Per-role provider injects a compact live
      world/scene/NPC/quest/slide block into the **tail** each turn (read-only observation, model still decides) —
      the game analog of Claude Code's file-context. Cache-safe because it's in the tail.
- [x] **Deterministic serialization + per-role policy.** Stable tool order, sorted JSON keys, no timestamps/UUIDs
      in the frozen prefix; history depth / memory scope / tool-result policy / world-state / compaction thresholds
      configured per role (Teacher / NPC / mechanics agent).

## [P1] Context overhaul — follow-ups (after T1–T9)

> Wave 2 (T1–T9) is committed and `dotnet build` green; full Unity EditMode/PlayMode rerun still pending.
> These are the remaining gaps + improvements found while building it. Ship behind config; keep the CoreAiPro
> extension points (`ILlmUsageSink`, `ILlmEntitlementPolicy`, `ServerManagedAuthorization`, `LlmUsageReported`) intact.

- [x] **Run EditMode + targeted PlayMode after wave 2.** 2026-06-18 verification: Unity MCP full EditMode
      `CoreAI.Tests` passed 1142/1142; PlayMode `CoreAI.Tests.PlayMode.FastNoLlm` passed 42/42. Regressions found
      during the run (runtime-context tail tests and Unity main-thread marshaler) were fixed before marking done.
- [x] **`.gitattributes` line-ending normalization.** Add a Unity-standard `.gitattributes` (`* text=auto` +
      binary/asset rules) — every commit currently warns `LF will be replaced by CRLF`; risks phantom diffs.
- [x] **Memory deltas in tail + boundary consolidation (roadmap §6 placement).** Mid-session memory edits go into
      a small `## Memory (updates)` tail block; at a boundary (session start / after summarization / explicit
      reload) consolidate deltas into the canonical `## Memory` prefix snapshot and clear the tail. This removes
      the last per-turn prefix-churn source and completes §1a/§6.
- [x] **Remove legacy prefix-placement toggle.** Tail placement is the only CoreAI runtime path; the old Core/Unity
      setting and Inspector field were removed so live context cannot rewrite the system prefix by configuration.
- [x] **Streaming overflow retry.** `RunStreamingAsync` has no bounded context-overflow recovery; mirror the
      `RunTaskAsync` `MaxContextOverflowRetries` loop for symmetry.
- [x] **Persist token-calibration scale per model.** `CalibratingTokenEstimator` uses `ITokenCalibrationStore`
      (no-op default in core, file-backed in the Unity layer) keyed by model id.
- [x] **Cross-turn superseded tool-result pruning.** `ConversationHistoryPruner` keeps the newest N `## Tool Results`;
      extend it to drop an older result for the SAME tool when a newer one exists (Cline "narrative integrity").
- [x] **Per-role compaction-threshold override.** `ConversationCompactionTriggerRatio` is global only; add a
      nullable per-role override (`AgentBuilder.WithCompactionTriggerRatio` / `AgentMemoryPolicy.Set...`).
- [x] **Native `tool_call_id` linkage.** Native tool-call loops replay assistant `FunctionCallContent` and tool
      `FunctionResultContent` pairs with the original call id; OpenAI-compatible payloads emit `tool_call_id`.
      User-text fallback remains for provider-safe persisted observations.
- [x] **Anthropic `cache_control` breakpoints (deferred).** Not implemented without an Anthropic-style backend;
      provider-specific work moved to `Assets/CoreAiUnity/Docs/BACKLOG.md`.

## Infrastructure

- [x] **GameCI secrets** (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) - CI now skips licensed Unity
      execution cleanly when secrets are unavailable and uses `githubToken` when tests run; repository-secret
      maintenance is tracked as release ops in `Assets/CoreAiUnity/Docs/BACKLOG.md`.
- [x] **GitHub Release / tag v4.0.0** after push. Historical v4.0.0 task is obsolete after v4.5.0/v4.6.0 work;
      release/tagging belongs to `Assets/CoreAiUnity/Docs/RELEASE_CHECKLIST.md` and backlog release ops.
- [x] **EditMode test integrity audit** - project-wide rules are documented in
      `Assets/CoreAiUnity/Docs/ARCHITECTURE.md` and `Assets/CoreAiUnity/Tests/README.md`; the old
      separate audit log was removed.
- [x] **Skill creation/editing + action/toolcall support** - `SkillSetAsset.ApplyDefinition(...)` lets editor and
      bootstrap code create/update skill definitions without private-field reflection; skills can contain
      delegate-backed actions/tools and direct `IJsonInvocableLlmTool` implementations, and `call_skill_tool`
      returns explicit results to the model for void actions.
- [x] **Crafting determinism PlayMode test** - added a separate targeted repeat-ingredients
      `CraftingMemoryOpenAi_RepeatIngredients_SecondMatchesFirst` check and a fast normalization
      contract test so determinism is not hidden inside `ThreeCrafts_AllUnique`.
- [x] **LLMUnity crafting timeout triage** - targeted `CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` with `qwen3.6-27b-mtp-ud` passed after bounding live craft turns to 2048 output tokens, exposing the generic `logic_*` Lua APIs advertised by `execute_lua`, and canonicalizing verified craft memory between turns.
- [x] **Merchant negotiation timeout triage** - targeted `MerchantChatWithTools_FullNegotiationFlow_CompletesPurchase` with `qwen3.6-27b-mtp-ud` passed after making the negotiation mechanically necessary (Iron Sword costs 60, player has 40) and bounding each live step to 2048 output tokens. The scenario remains explicit/targeted rather than a mandatory full-suite gate.
- [x] **Merchant negotiation decomposition** - added targeted merchant economy tests for
      insufficient-gold no-mutation and discount-enabled purchase completion; the long live-model
      negotiation scenario remains explicit.
- [x] **Targeted PlayMode follow-up before full suite** - attached failures and known slow live-model tests were triaged one by one before the full rerun. FullAccess demo scene checks now include targeted Full Lua API smoke, PlayMode scene-smoke, and manual visual layout verification.
- [x] **Full PlayMode rerun after targeted fixes** - full mandatory PlayMode suite with `qwen3.6-27b-mtp-ud` passed after targeted fixes: 114 total, 109 passed, 0 failed, 5 skipped/explicit.

## [P1] Model benchmark & test harness

- [x] **Полноценный бенчмарк для тестов и сравнения моделей.** Existing baseline:
      `SkillSetBenchmarkPlayModeTests`; full multi-model result matrix moved to
      `Assets/CoreAiUnity/Docs/BACKLOG.md`.
      (crafting, merchant, GameMaster/Lua, tool-calling, memory) как воспроизводимого бенчмарка по
      набору моделей (qwen3.6-27b, qwen ~4B, и т.д.). На выходе — таблица per-model: pass/fail по
      сценарию, кол-во tool-calls, корректность Lua-синтаксиса, длительность турна, токены (real
      `usage`), число ретраев. Цель — отделять «4B не вытягивает» от наших регрессий объективно,
      а не вручную по логам. Сохранять результаты (JSON/markdown) для сравнения между версиями CoreAI.
      Решает текущую ручную триаж-работу: capability модели vs баг кода.

## [P1] Lua - remaining work (does not block v4)

- [x] Undo applied world commands (inverse spawn/move commands) moved to
      `Assets/CoreAiUnity/Docs/BACKLOG.md`; it requires per-command inverse snapshots and host policy.
- [x] Capability tier from AI role config + optional player confirmation for dangerous levels moved to
      `Assets/CoreAiUnity/Docs/BACKLOG.md`; current runtime remains host-gated through `LuaCapabilities`.
- [x] Bridge `ModEventEmitted` -> MessagePipe (`LuaModEventEmitted` broker + `LuaModRuntimeTicker` publisher).
- [x] World-command/event budget per tick for mods (`LuaModRuntime.DefaultMaxEventsDispatchedPerTick = 64`).
- [x] **Lua skill by access mode.** Agent-facing guidance now lives in `LuaTool.Description`,
      `BuiltInAgentSystemPromptTexts`, `LUA_ACCESS_MODES.md`, and `LUA_GAME_API.md`; it routes safe/mod/world/full
      APIs and forbids hallucinated APIs such as `game.rules`, `game_rules`, `game.enemies`, `game.create`,
      and `game.destroy` unless a host game registers them.
- [x] **Reusable file-backed Lua mods.** Runtime support covers `manage_mods`, source inspection, lifecycle
      events, MessagePipe events, and persistent per-mod storage through `FileLuaModStore`; portable package
      discovery/activation layout moved to `Assets/CoreAiUnity/Docs/BACKLOG.md`.

## [P2] WebGL: Lua in the web build (research)

- `SecureLuaEnvironment.IsSupported` = false in WebGL player; investigate MoonSharp+IL2CPP, size, and no-thread limits.

## [P2] Ideas

- [x] STT -> Agent -> TTS for NPCs moved to `Assets/CoreAiUnity/Docs/BACKLOG.md`.
- [x] Visual AgentBuilder in the editor moved to `Assets/CoreAiUnity/Docs/BACKLOG.md`.
- [x] Streaming emotions / function-driven animations moved to `Assets/CoreAiUnity/Docs/BACKLOG.md`.

## Media / promotion

- [x] GIF demos for README (`DEMO_RECORDING_GUIDE.md`) moved to release ops backlog.
- [x] Publish to OpenUPM moved to release ops backlog.
- [x] Boosty link in `FUNDING.yml`.
