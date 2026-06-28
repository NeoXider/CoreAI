# TODO

> Updated 2026-06-28. This file tracks **only open work**, by priority. All previously
> tracked items are implemented and verified — see `CHANGELOG.md` (both packages). Non-blocking
> future work lives in `Assets/CoreAiUnity/Docs/BACKLOG.md`.
>
> 4.12.0 (2026-06-28) cleared the whole P2 engineering + Lua-mod + vision backlog and most of P3:
> live streaming through tool calls, partial-SSE accumulation, WebGL Lua AOT hardening, stale-`<think>`
> prune, mod versioning + runtime-error diagnostics, vision host send path + capability gate + tool-result
> lift, plus P3 event-cap / WaitLlmTool / forbidden-API-drift tests. Remaining open work below.
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

- [x] **Context-editing prune — stale thinking.** Shipped: `ConversationHistoryPruner.StripStaleThinking`
      losslessly strips `<think>` from older assistant turns (newest turn preserved) ahead of summarization.
- [x] **Partial SSE `tool_calls` accumulation.** Shipped: `MeaiOpenAiChatClient.SseToolCallAccumulator`
      assembles id/name/arguments across split `delta.tool_calls` chunks.
- [x] **Keep streaming live through tool calls (Kilo/Cline-style).** Shipped in 4.12.0:
      `MeaiLlmClient` on-the-fly hybrid hold (`GetHybridSafeSegments`) — prose streams live before and
      after a tool call; only the tool-call JSON is hidden; no full-turn buffering.
- [x] **WebGL: Lua in the web build.** Shipped: `EnableLuaOnWebGl` capability flag + Full tier disabled on
      web + 4.12.0 `link.xml` AOT preserve for the binding types. A WebGL-player self-test scene wiring
      (`WebGlLuaSelfTest`) still needs to be attached to a scene before a real WebGL build (manual step).

## [P2] Lua mod packages — follow-ups

- [x] **Mod versioning (agent edits).** Shipped in 4.12.0: revision history via `ILuaScriptVersionStore`,
      auto-derived `LuaModManifest.Version`, `manage_mods versions` / `revert`.
- [x] **Surface runtime mod-handler errors to the agent.** Shipped in 4.12.0: `manage_mods diagnostics`
      poll + bounded handler-error ring buffer.

## [P2] Vision / multimodal — follow-ups

- [x] **Host vision send path.** Shipped in 4.12.0: `CoreAiChatService.AskWithCameraAsync` / `CoreAi` facade.
- [x] **Register `CameraLlmTool`** + autonomous tool-result image lift. Shipped: `CoreAi.RegisterCameraVisionTool`
      + `AskWithImageFollowUpAsync`.
- [x] **Model capability gate.** Shipped: `VisionCapability` + `CoreAISettingsAsset.VisionSupport`.
- [ ] PlayMode round-trip test against a vision-capable model (`[Explicit]`).

## [P3] Minor / test-coverage nits (non-blocking)

- [x] Dedicated test for `LuaModRuntime.DefaultMaxEventsDispatchedPerTick = 64` cap.
- [x] `WaitLlmTool` timing/clamping unit test (clamp / below-cap / zero / NaN covered in `WaitLlmToolEditModeTests`).
- [x] Align the forbidden-API list (`game.rules` / `game_rules`) between `LuaTool.ExecuteLuaDescription` and
      `BuiltInAgentSystemPromptTexts.Programmer` (already identical; now guarded by a drift test).
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite (currently EditMode-only; PlayMode validates scene-load only).
