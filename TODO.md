# TODO

> Updated 2026-06-20. This file now tracks **only open work**, by priority. All previously
> tracked items are implemented and verified — see `CHANGELOG.md` (both packages). Non-blocking
> future work lives in `Assets/CoreAiUnity/Docs/BACKLOG.md`.
>
> Last verification: 2026-06-20, per-claim code audit of every former `[x]` item —
> 38/39 claims confirmed DONE with tests; the single gap is re-opened below (P2: prune stale-thinking).
> Test baseline (2026-06-18 run): EditMode `CoreAI.Tests` 1142/1142, PlayMode `FastNoLlm` 42/42.

## [P1] Multi-agent orchestration v2.0

> Full design exists in `TODO/MultiAgent_Orchestration_v2.0.md`, but **not implemented**: no
> `SubAgentDefinition` / `AgentRegistry` / `AgentOrchestrator` / `AgentLlmTool` / `AgentCallResult`
> types exist in `Assets/CoreAI/Runtime/Core/Features/Orchestration/`.

- [ ] Declarative `SubAgentDefinition` (roleId, description, custom prompt, tools w/o Task tool, model, maxTokens, maxTurns).
- [ ] `IAgentRegistry` sub-agent registration + lookup; `AgentOrchestrator.ExecuteSubAgentAsync` with **clean context isolation** and bounded `ExecuteSubAgentsParallelAsync` (default `MaxParallelAgents = 3`).
- [ ] `AgentLlmTool` (parent-only; subagents cannot spawn subagents) returning results as tool_result.
- [ ] DI wiring (`CorePortableInstaller`), per-role exposure (`AgentMemoryPolicy.GetToolsForRole`), settings (`MaxParallelAgents`, `AgentCallTimeoutSeconds`, `SubAgentMaxTurns/MaxTokens`).
- [ ] EditMode tests for registry + tool; docs + CHANGELOG entry.

## [P2] Engineering follow-ups

- [ ] **Context-editing prune — stale thinking.** `ConversationHistoryPruner` prunes superseded tool
      results + exact-duplicate dedup (done, tested), but the originally-claimed **stale `<think>`/reasoning
      pruning is not implemented**. Add a lossless reasoning-block prune step ahead of summarization.
- [ ] **Partial SSE `tool_calls` accumulation.** Streaming tool-call args may arrive split across SSE
      chunks; current parser only handles a complete `delta.tool_calls` in one chunk. Add progressive
      accumulation (`STREAMING_ARCHITECTURE.md` §9).
- [ ] **WebGL: Lua in the web build.** Feasibility confirmed (MoonSharp coroutines are stackless;
      bundled MoonSharp already AOT-detects WebGL). Work: capability-flag instead of hard
      `SecureLuaEnvironment.IsSupported == false`, `link.xml` / `[Preserve]` against IL2CPP stripping,
      prefer non-generic `RegisterCallback` bindings for AOT, keep Full tier disabled (or hardwired
      allowlist) on web, and add a WebGL-player self-test scene. See `BACKLOG.md` (Lua & World Runtime).
- [ ] **Engine-agnostic controllers.** `IAudioController` / `IUIController` / `IPhysicsController`
      Unity implementations are still ⏳ in `Assets/CoreAI/Docs/ENGINE_AGNOSTIC_TOOLS.md`.

## [P3] Minor / test-coverage nits (non-blocking)

- [ ] Dedicated test for `LuaModRuntime.DefaultMaxEventsDispatchedPerTick = 64` cap (queue >64 events, assert truncation).
- [ ] `WaitLlmTool` timing/clamping unit test (only builder-registration is covered today).
- [ ] Align the forbidden-API list (`game.rules` / `game_rules`) between `LuaTool.Description` and `BuiltInAgentSystemPromptTexts.Programmer`.
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite (currently EditMode-only; PlayMode validates scene-load only).
