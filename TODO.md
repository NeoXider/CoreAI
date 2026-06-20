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

## [P2] Lua mod packages — follow-ups

> File-backed mod persistence + export/import shipped in 4.10.0 (`ILuaModSourceStore` /
> `FileLuaModSourceStore`, `RehydrateFromStore`, `manage_mods export/import/forget`). Remaining:

- [ ] **Mod versioning (agent edits).** Mod source persists, but each `manage_mods reload` **overwrites**
      the source — no revision history or rollback. `LuaModManifest.Version` is a static field, not tracked.
      Wire mod load/reload into the existing `ILuaScriptVersionStore` (keyed by mod id): record a revision
      per edit, auto-increment `LuaModManifest.Version`, and add `manage_mods versions` / `revert` actions
      so the agent (or host) can list and roll back mod changes.
- [ ] **Surface runtime mod-handler errors to the agent.** Load/reload errors are returned to the agent
      via `manage_mods` (it can fix + retry), but a hook that throws later during `Tick` only raises
      `ModHandlerErrored` + counts toward auto-unload host-side — it is not fed back to the agent. Add a
      path (next-turn context block or a `manage_mods` diagnostics action) so the agent learns of runtime
      handler failures and can repair the mod.

## [P2] Vision / multimodal — follow-ups

> 4.10.0 added the core enabler: `MeaiOpenAiChatClient` serializes image content to OpenAI `image_url`,
> and `CameraLlmTool.CaptureCameraJpeg/CaptureCameraImageContent` produce real image content (tested).
> Remaining to make it fully usable:

- [ ] **Host vision send path.** Add a chat/facade method (e.g. `AskWithCameraAsync(prompt, cameraName)`)
      that captures the camera as a `DataContent` and sends it as a user message — the working,
      provider-safe "camera → model" path.
- [ ] **Register `CameraLlmTool`** on a vision-enabled role (behind a flag) so the model can request a
      screenshot; for the autonomous pattern, lift the tool-result image into a follow-up user `image_url`
      message before the next model call (OpenAI tool results cannot carry images).
- [ ] **Model capability gate.** Skip/omit vision when the configured model is text-only.
- [ ] PlayMode round-trip test against a vision-capable model (`[Explicit]`).

## [P3] Minor / test-coverage nits (non-blocking)

- [ ] Dedicated test for `LuaModRuntime.DefaultMaxEventsDispatchedPerTick = 64` cap (queue >64 events, assert truncation).
- [ ] `WaitLlmTool` timing/clamping unit test (only builder-registration is covered today).
- [ ] Align the forbidden-API list (`game.rules` / `game_rules`) between `LuaTool.Description` and `BuiltInAgentSystemPromptTexts.Programmer`.
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite (currently EditMode-only; PlayMode validates scene-load only).
