# TODO

> Updated 2026-06-15. Completed v4.0.0 work is in `CHANGELOG.md` (both packages) and the git log. This file lists only open tasks.

## v4.0.0 - done (2026-06-12)

- [x] Lua as a second language: phases 1-5, capability tiers, `manage_mods`, sandbox/audit fixes.
- [x] Demo `Assets/CoreAI.Demos/`: LuaMods, WorldCommands, Skills, LiveMechanics (+ LLM chat).
- [x] `ICoreAiCustomWorldCommandHandler`, scene whitelist, perf (MPB `set_color`, `LuaModRuntime.Tick` scratch).
- [x] Documentation: `LUA_GAME_API`, `LUA_BEST_PRACTICES`, `MOONSHARP_NATIVE_APIS`, `LUA_ACCESS_MODES_AUDIT`, `PERF_REVIEW_2026-06-12`.
- [x] Version **4.0.0** in `com.nexoider.coreai` / `com.nexoider.coreaiunity`.
- [x] `IGameLogger` instead of `Debug.*` in CoreAiUnity Runtime.

## [P1] Full mode

> Currently available: `LuaCapabilities.Full`, reflection bindings `CoreAiFullUnityLuaRuntimeBindings` (`unity_*`), opt-in `enableFullLuaAccess`, audit `LUA_ACCESS_MODES_AUDIT.md`.

- [x] **Demo scene** `FullAccess/FullAccessDemo.unity` (chat + scope with Full + auto-`TargetCube`, prompt buttons move/grow/inspect).
- [x] **PlayMode tests** Full: `unity_find` / `unity_set_position` on a scene object.
- [x] **Member visibility:** public-by-default, non-public is opt-in (`enableFullLuaPrivateAccess` / ctor `allowNonPublicMembers`) + EditMode tests.
- [ ] **Migration to MoonSharp `UserData.RegisterType`** - *audit conclusion 2026-06-13:* reflection cannot be removed completely without losing allow-all semantics (addressing a member by string = reflection; `UserData` in Reflection mode does the same). `LazyOptimized` breaks on IL2CPP (no JIT), and hardwiring is impossible for types that are not known in advance. The current cached reflection is the most AOT-portable option and is not on a hot path (admin/debug tier). Migration would provide more idiomatic syntax, not performance; not a priority.
- [ ] **Blacklist** types/members for Full (idea from the audit, Planned - do not implement until a separate task, but document the `IFullLuaAccessBlacklistPolicy` API when introducing it).

## [P1] Context management overhaul (Claude Code / Cline / Kilo-grade)

> Design fixed in `Assets/CoreAiUnity/Docs/CONTEXT_MANAGEMENT_ROADMAP.md`. Goal: history/context like
> Claude Code — recent turns verbatim, old turns condensed by threshold, tool results kept (policy-gated),
> token budgeting from real API `usage`, and a **stable cacheable prefix** (today's per-turn re-summarization
> into `system` breaks prompt caching). Build behind config; do not regress existing roles.

- [x] **Prefix vs Tail (summary-only first step).** Behind `PlaceLiveContextInTail` (default off), move
      `## Conversation Summary` OUT of top-level `system` into the **tail** of `messages[]` as the first
      system-role message, before recent verbatim turns, because it summarizes the evicted oldest turns.
      Frozen prefix = persona + tool defs (+ current canonical memory) -> cacheable; live tail -> cheap.
- [x] **Stable cacheable prefix + verbatim recent turns.** Recent turns are kept as real `user`/`assistant`/`tool`
      messages in the tail; the `system`/`tools` prefix is no longer rewritten per turn for summary/world-state
      (both moved to the tail behind `PlaceLiveContextInTail`) and is deterministic (sorted tools/JSON, no
      timestamps/GUIDs). Remaining churn source = the canonical `## Memory` block — see follow-ups below.
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

- [ ] **Run EditMode + targeted PlayMode after wave 2.** Agent-authored wave-2 tests compile but have not been
      run in Unity yet (wave 1 needed 3 fixes after a real run). Run the EditMode suite + a small PlayMode subset
      (e.g. `AiOrchestrator*`, role/history tests) and fix any regression before flipping defaults.
- [x] **`.gitattributes` line-ending normalization.** Add a Unity-standard `.gitattributes` (`* text=auto` +
      binary/asset rules) — every commit currently warns `LF will be replaced by CRLF`; risks phantom diffs.
- [ ] **Memory deltas in tail + boundary consolidation (roadmap §6 placement).** Mid-session memory edits go into
      a small `## Memory (updates)` tail block; at a boundary (session start / after summarization / explicit
      reload) consolidate deltas into the canonical `## Memory` prefix snapshot and clear the tail. Behind
      `PlaceLiveContextInTail`. This removes the last per-turn prefix-churn source and completes §1a/§6.
- [ ] **Flip `PlaceLiveContextInTail` default ON.** After verifying on WebGL/IL2CPP with the live server (use the
      new `cache_read`/`cache_write` metrics to confirm caching), make tail placement the default.
- [x] **Streaming overflow retry.** `RunStreamingAsync` has no bounded context-overflow recovery; mirror the
      `RunTaskAsync` `MaxContextOverflowRetries` loop for symmetry.
- [ ] **Persist token-calibration scale per model.** `CalibratingTokenEstimator._scale` resets each session; add
      an `ITokenCalibrationStore` (no-op default in core, file-backed in the Unity layer) keyed by model id.
- [ ] **Cross-turn superseded tool-result pruning.** `ConversationHistoryPruner` keeps the newest N `## Tool Results`;
      extend it to drop an older result for the SAME tool when a newer one exists (Cline "narrative integrity").
- [ ] **Per-role compaction-threshold override.** `ConversationCompactionTriggerRatio` is global only; add a
      nullable per-role override (`AgentBuilder.WithCompactionTriggerRatio` / `AgentMemoryPolicy.Set...`).
- [ ] **Native `tool_call_id` linkage.** Tool results are replayed as `ChatRole.User` text (OpenAI-safe). For
      backends that expose tool-call ids, link `FunctionResultContent` properly; keep the User-text fallback.
- [ ] **Anthropic `cache_control` breakpoints (deferred).** Only when an Anthropic-style backend is added: attach
      `cache_control` on the frozen prefix via `ChatOptions.AdditionalProperties`/`RawRepresentationFactory`.

## Infrastructure

- [ ] **GameCI secrets** (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) - without them the CI matrix moonsharp / no-lua will not run.
- [ ] **GitHub Release / tag v4.0.0** after push.
- [ ] **EditMode test integrity audit** - project-wide rules are documented in `Assets/CoreAiUnity/Docs/ARCHITECTURE.md`; PlayMode live-model tests are the priority now. Later, review EditMode fixtures and keep exact payloads only where they test parsers, repair, serialization, migration, deterministic extraction logic, or other exact-byte contracts.
- [ ] **Crafting determinism PlayMode test** - `ThreeCrafts_AllUnique` now checks only three unique crafts. Add a separate targeted repeat-ingredients test for determinism so the full PlayMode suite does not hide a fourth long LLM turn inside a uniqueness scenario.
- [x] **LLMUnity crafting timeout triage** - targeted `CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` with `qwen3.6-27b-mtp-ud` passed after bounding live craft turns to 2048 output tokens, exposing the generic `logic_*` Lua APIs advertised by `execute_lua`, and canonicalizing verified craft memory between turns.
- [x] **Merchant negotiation timeout triage** - targeted `MerchantChatWithTools_FullNegotiationFlow_CompletesPurchase` with `qwen3.6-27b-mtp-ud` passed after making the negotiation mechanically necessary (Iron Sword costs 60, player has 40) and bounding each live step to 2048 output tokens. The scenario remains explicit/targeted rather than a mandatory full-suite gate.
- [ ] **Merchant negotiation decomposition** - the full merchant negotiation scenario is explicit and now cancels/bounds long turns. Consider splitting Step3 into smaller targeted tests for discount application and purchase completion so failures identify tool-choice versus final-sale behaviour.
- [x] **Targeted PlayMode follow-up before full suite** - attached failures and known slow live-model tests were triaged one by one before the full rerun. FullAccess demo scene checks now include targeted Full Lua API smoke, PlayMode scene-smoke, and manual visual layout verification.
- [x] **Full PlayMode rerun after targeted fixes** - full mandatory PlayMode suite with `qwen3.6-27b-mtp-ud` passed after targeted fixes: 114 total, 109 passed, 0 failed, 5 skipped/explicit.

## [P1] Model benchmark & test harness

- [ ] **Полноценный бенчмарк для тестов и сравнения моделей.** Прогон PlayMode/LLM-сценариев
      (crafting, merchant, GameMaster/Lua, tool-calling, memory) как воспроизводимого бенчмарка по
      набору моделей (qwen3.6-27b, qwen ~4B, и т.д.). На выходе — таблица per-model: pass/fail по
      сценарию, кол-во tool-calls, корректность Lua-синтаксиса, длительность турна, токены (real
      `usage`), число ретраев. Цель — отделять «4B не вытягивает» от наших регрессий объективно,
      а не вручную по логам. Сохранять результаты (JSON/markdown) для сравнения между версиями CoreAI.
      Решает текущую ручную триаж-работу: capability модели vs баг кода.

## [P1] Lua - remaining work (does not block v4)

- [ ] Undo applied world commands (inverse spawn/move commands).
- [ ] Capability tier from AI role config + optional player confirmation for dangerous levels.
- [ ] Bridge `ModEventEmitted` -> MessagePipe.
- [ ] World-command budget per tick for mods.
- [ ] **Lua skill by access mode.** Create an agent-facing Lua guide/skill that routes tasks to the right API surface: Safe/Logic (`logic_define`, `report`), Mods (`manage_mods`, `hooks_on`, `hooks_every`, `store_get/set`), WorldEdit (`coreai_world_*`), and Full (`unity_*`). It must explicitly forbid hallucinated APIs such as `game.rules`, `game_rules`, `game.enemies`, `game.create`, `game.destroy` unless a host game registers them.
- [ ] **Reusable file-backed Lua mods.** Design a portable mod package layout for games, e.g. `Mods/<mod_id>/manifest.json` + `main.lua`, with `id`, `name`, `description`, `version`, `capabilities`, `entry`, `author`, and `active`. The runtime/panel should load, activate/deactivate, reload, and forget mods from files instead of only `ILuaScriptVersionStore`.

## [P2] WebGL: Lua in the web build (research)

- `SecureLuaEnvironment.IsSupported` = false in WebGL player; investigate MoonSharp+IL2CPP, size, and no-thread limits.

## [P2] Ideas

- [ ] STT -> Agent -> TTS for NPCs.
- [ ] Visual AgentBuilder in the editor.
- [ ] Streaming emotions / function-driven animations.

## Media / promotion

- [ ] GIF demos for README (`DEMO_RECORDING_GUIDE.md`).
- [ ] Publish to OpenUPM.
- [ ] Boosty link in `FUNDING.yml`.
