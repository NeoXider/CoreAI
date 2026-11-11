# CoreAI Backlog

This document tracks future work that should not block the current CoreAI MVP gate for production hosts.
Items here are intentionally not active TODO checkboxes; open work by priority is in [`TODO.md`](../TODO.md).
Moved here from `Assets/CoreAiUnity/Docs/` on 2026-10-15.

## Provider-Specific Work

- Add explicit Anthropic-style `cache_control` breakpoints when CoreAI gets an Anthropic-compatible
  backend. Current OpenAI/DeepSeek-compatible local backends rely on provider-side automatic stable-prefix
  caching, and CoreAI already keeps summaries/world state/memory updates out of the frozen prefix.

## Release And Operations

- Create public release tags from the final package version after the verified changes land on the target branch.
- Publish packages to OpenUPM when the repository state, tags, package metadata, and CI release gate are ready.
- Refresh README media with GIF/video captures from `DEMO_RECORDING_GUIDE.md`.
- Keep funding links in `.github/FUNDING.yml` aligned with the current public channels.
- Maintain GameCI secrets (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) in repository settings. CI already
  skips licensed Unity test execution cleanly when secrets are unavailable, such as forked pull requests.

## Benchmarking

- Extend the existing `SkillSetBenchmarkPlayModeTests` into a reproducible multi-model benchmark matrix.
- Capture per-model JSON/Markdown results for crafting, merchant, GameMaster/Lua, tool-calling, and memory
  scenarios: pass/fail, tool-call count, Lua syntax validity, turn duration, real usage tokens, retries, and
  timeout classification.

## Lua And World Runtime

- Add undo for applied world commands. This requires command-specific inverse snapshots for spawn, move,
  transform, active-state, color/material, physics, and custom handlers; scene-loading commands should remain
  explicitly non-undoable unless a host game supplies a checkpoint system.
- Add role-configured Lua capability tiers and optional player confirmation for dangerous capabilities such as
  `WorldEdit` and `Full`.

> **Shipped:** Lua now runs on the WebGL/IL2CPP player via **Lua-CSharp** (`LuaCsSecureEnvironment`), a
> managed, AOT-safe VM; `LuaCsGameToolExecutor.IsSupported` is always `true`. IL2CPP stripping is held off by the
> package `link.xml` (`Assets/CoreAiUnity/link.xml`, preserving the `Lua` / `Lua.Annotations` assemblies plus the
> WebGL-active Lua binding types). Lua-CSharp uses source-generated marshalling rather than a reflection-based
> interpreter fallback, so host-callback marshalling works without emitted IL. The `Full` reflection tier
> (`unity_*` bindings) is granted only by `CoreAiModsLifetimeScope.FullLuaAccessEnabled`. See the
> `WebGlLuaSelfTest` demo (`WebGlLuaSelfTest.TryRunSelfTest`) for an in-player smoke test. Open questions:
> `ICoreAISettings.EnableLuaOnWebGl` is serialized but not read by the Lua runtime (wire it or remove it);
> binary-size impact and how a host can prune unused bindings for the smallest web build.

## Skill System (before MVP2)

- ~~**Structural browsing of a multi-file skill.**~~ **DONE.** The assembly half shipped in 7.11.0
  (`FromFiles`/`FromTextParts`), and the third disclosure level followed: `read_skill(name)` returns
  the entry document plus a `sections` index, and `read_skill(name, section)` fetches one document.
  A single-source skill is unchanged. The one deliberate departure from the Claude Code shape it was
  modelled on: those agents follow a `references/*.md` link with their own file reader, and a CoreAI
  agent has no file system, so the second level is an argument rather than a link.
- **Skill self-improvement.** A mechanism for a skill to update/augment its own instructions from
  experience (learned corrections, new patterns the agent discovered at runtime). Design after the
  structural-browsing work; both land before MVP2.

## UX / Mobile

- **Mobile chat input preview above the keyboard.** On phones, typing into the chat field is awkward —
  add a field shown directly above the on-screen keyboard mirroring what is being typed, so the text is
  visible while the keyboard covers the chat. Deferred (low priority).
- **"Execute Lua" tab in the Hub Mods panel.** Add a second tab next to the mod list where a user can
  write a one-off Lua script in a text field and run it immediately against the live world (a scratchpad
  for quick experiments / debugging without creating a persistent mod). Route it through the same
  one-off `execute_lua` executor the Programmer agent uses; show the result/errors inline. Requested
  2026-07-24.

## Rbx API gaps surfaced by the sample games (2026-07-24)

- **3D picking.** ~~`workspace:Raycast` and `ClickDetector`~~ **DONE** (`LuaCsRbxInstanceBindings`,
  `RbxClickDetector`). Still open: the Lua binding for `Camera:ScreenPointToRay` / `ViewportPointToRay`, so a
  mod can build its own ray from the mouse position (`UserInputService` already gives mouse position + button
  state).
- **Physics velocity for parts** (`AssemblyLinearVelocity` / a `BodyVelocity`-like impulse). Today a burst
  effect (Tetris line-clear, racer crash) is hand-animated in Heartbeat because there is no way to fling an
  unanchored part with an initial velocity.
- ~~**`RunService` newer aliases** `PreSimulation`/`PostSimulation`/`PreRender`~~ **DONE** (together with
  `PreAnimation`; the classic names still work).

## Architecture Hardening (before MVP2 — from the 2026-07-24 architecture audit)

> Macro-architecture is healthy: engine-free portable Core (`noEngineReferences:true`), clean Rbx
> domain/adapter split, Lua VM inverted out via a child scope, seam+decorator LLM pipeline with fitness
> tests. The items below are erosion at the outer edges that violates CoreAI's own rules.

- **[HIGH] Kill ambient `CoreAISettings.Instance` / `CoreAISettingsAsset.Instance` reads in runtime
  services** — §4.1 forbids static service locators, yet `OpenAiChatLlmClient`, `CoreAiChatService`,
  `VisionSelfProbe`, `LlmUnityAutoDisableIfNoModel`, `CoreAiBackend` pull settings from the static
  instead of the DI-registered `ICoreAISettings` (the two can diverge — a "works in scene, wrong in
  test/child-scope" hazard). Inject `ICoreAISettings`; keep the static only as an editor convenience.
- **[HIGH/MEDIUM] Decide the `CoreAi` static facade's status explicitly** — it's the shipped public
  entry point but is itself a static service locator that §4.1 bans ("zero escape hatches"). Sanction it
  as the ONE documented exception AND guarantee no *internal* service resolves through it (internals must
  use DI), else the single exception becomes the norm.
- **[MEDIUM] Break up god-classes** — `CoreAiChatPanel` (~3636 LOC MonoBehaviour, 263 members: move
  view-model logic behind the existing `CoreAiChatService` seam) and `AiOrchestrator` (~1866 LOC, 15
  ctor deps: extract a context/compaction collaborator + a telemetry facade). Also large:
  `MeaiOpenAiChatClient`, `MeaiLlmClient`, `LuaCsModRuntime`, `ToolExecutionPolicy`.
- ~~**[MEDIUM] Reconcile Rbx-vs-Roblox naming at the adapter edge**~~ **DONE** — the binding/scripting
  public types were renamed to `Rbx*` (`RbxWorldHost`, `LuaCsRbxApiBindings`, …).
- **[MEDIUM] Fitness-test + contract-boundary gaps** — no architecture-fitness test for `CoreAI.Hub.UI`
  (and it ships no tests at all); Hub pages reach into infrastructure namespaces
  (`HubSettingsPage` → `CoreAI.Infrastructure.Llm`, `WorldStateHubPage` → `CoreAI.Infrastructure.World`)
  instead of documented public contracts. Add a Hub fitness test; route through contracts.
- **[LOW] Fix the rules-doc contradiction** — §3 mandates UniTask over `System.Threading.Tasks.Task`,
  but §1 mandates an engine-free Core (UniTask needs UnityEngine), so Core correctly uses `async Task`.
  Scope the "Task banned" rule to the Unity-layer assemblies only.
- **[LOW] Keep composition roots strictly declarative** — `CoreAILifetimeScope.Configure` writes the two
  static singletons and mutates `AgentMemoryPolicy` inline in a build callback; §4.3 wants roots free of
  business logic. (Positive: no `FindObjectsByType` in any installer — §4.2 holds.)

## Performance (before MVP2 — from the 2026-07-24 perf audit)

> All prior optimizations verified still holding (binder per-aspect apply, cached components + reused
> MaterialPropertyBlock, cached primitive meshes, `RbxScriptSignal.Fire` reuse, `RbxCFrame` struct fields,
> input gating on `HasConnections`). The remaining cost moved UPSTREAM of the binder, into the guard /
> marshalling boundary every Lua handler call crosses. The findings cited v6.3.2.
>
> **Re-verified 2026-11-11** against HEAD `654bb318` with measurements: status, file:line and the measured
> bytes and times for every item below are in [PERF_BASELINE_2026-11.md](PERF_BASELINE_2026-11.md) §5,
> together with twelve newer findings (the per-Part enum rebuild in the world-package capture, the idle-frame
> garbage, the per-fire and per-write allocations) and the ranked optimization list (§7) P19 works from.

- **[HIGH] Per-guarded-call allocation storm** — **partly fixed.** The hook is pooled and re-armed, the clock is
  `Stopwatch.GetTimestamp()` and the token source is pooled (`LuaCsExecutionGuard`). Still allocating:
  `LuaCsScriptExecutionGuard.Invoke` (`new LuaValue[]`, an `object[]` and a box per result: 240 B for a
  1-argument call) and the VM call's `Task<LuaValue[]>` (72 B per guarded call). Original: `ExecuteGuarded`
  and `Invoke` allocated a fresh hook, closure, `Stopwatch`, argument and result arrays per call.
- **[HIGH] Per-instruction guard overhead** — **partly fixed.** `LuaCsExecutionGuard` fires every 4 instructions,
  but every scheduler thread, signal runner and main chunk still runs under `LuaCsCoroutineHandle`'s hook at
  count 1 (a clock read per instruction, a heap read per millisecond and per resume). Measured 8.9 M vs
  18.9 M instructions/s (CoreCLR) and 1.17 M vs 2.95 M (Mono, Boehm). The SGen-based 158 k figure in
  `tools/vmbench/RESULTS.md` overstates the heap-read cost: Unity's Boehm collector answers
  `GC.GetTotalMemory(false)` in about 20 ns, not 14 µs.
- **[MEDIUM] Per-event-dispatch churn** — **present.** `handlers.ToArray()` per event and `params object[]` per
  handler in `LuaCsModRuntime.DispatchPendingEvents`, plus, since the audit, a string concatenation and a
  closure per invocation for the mutation envelope: 600 B per event.
- **[MEDIUM] Var-args marshalling boxes every `LuaValue`** — **present.** `LuaCsValueMarshaller.Box` (40 B per
  number) at every `LuaCsScriptCallContext` read; `RegisterVarArgs` allocates a call context per call and a
  `LuaValue[]` per return.
- **[MEDIUM] SSE streaming parses a full `JObject` per token** — **fixed for content and reasoning deltas**
  (2026-11-11): `SseContentDeltaReader` reads that shape in place (7,528 → 368 B per token on CoreCLR) and
  hands every other chunk to the unchanged full parse. Tool-call argument fragments still take the
  `JObject` path (about 8.9 KB per fragment); the remaining streaming costs are ranked in
  [PERF_BASELINE_2026-11.md](PERF_BASELINE_2026-11.md) §11.
- **[MEDIUM] Camera capture stalls the main thread** — **partly fixed.** The render target is pooled and the
  readback texture cached (`AiAttachmentUnityExtensions`); the synchronous `ReadPixels` and `EncodeToJPG`
  remain on the main thread, no `AsyncGPUReadback`. Not measured yet (needs Unity).
- **[LOW] `RbxCFrame.GetComponents()`/`ToString()`** allocate arrays — **present** (72 B and 936 B per call);
  fine unless a mod polls them per frame. The world-package capture calls `GetComponents()` for every Part.

## Roblox-API parity (blocks MVP — no CoreAI-own Lua API)

- **Replace the CoreAI-own mod Lua layer with Roblox idioms.** Cross-script sharing must move from the
  bespoke `mods_export`/`mods_get`/`mods_call`/`mods_list_exports` + `hooks_every`/`events_emit` to
  Roblox's `require(ModuleScript)` (shared functions/variables), `BindableEvent`/`BindableFunction`
  (in-place events), and `RemoteEvent`/`RemoteFunction` (networked) + `_G`/shared. The `mods_*` surface is
  a stopgap; the AI skill and docs must teach the Roblox way, not `mods_*`. Until MVP, do NOT add new
  CoreAI-own Lua APIs — implement the Roblox equivalent.
- **Bidirectional Roblox↔CoreAI import/export (maps + mods).** A Roblox place/map/mod must import into
  CoreAI and a CoreAI map/mod export back, so content works identically in both. Drives format /
  serialization choices (instance tree, scripts, assets); keep portability a first-class constraint.

## Product Ideas

- STT -> Agent -> TTS for NPCs.
- Visual AgentBuilder editor workflow.
- Streaming emotions / function-driven animations.

## Idea / positioning bets (from the 2026-07-10 idea-improvement audit, since deleted)

> The idea audit concluded the moat is pillars 1+3: "local-first agentic runtime for Unity games, proven
> by the only LLM game-creation benchmark." These are the open, non-code bets it recommended pursuing.
> Most concrete artifacts it asked for already shipped (SHIPPING_PLAYER_MACHINES, DETERMINISM_AND_REPLAY,
> CONTENT_SAFETY, CLOUD_COST_BUDGETING, BENCHMARK_LEADERBOARD, MOD_SHARING); these are what remained open.

- **Director-AI demo scene** (beyond the chat box): a standalone `.unity` scene for the ambient/scheduled
  agent that observes game state and acts through the same tools with no chat UI. The controller recipe
  exists (`Assets/CoreAI.Demos/DirectorAi/`); a featured scene + PlayMode acceptance is the remaining step.
- **Audit-log replay** → determinism / anti-cheat / debugging. The hash-chained audit log + the
  `DETERMINISM_AND_REPLAY.md` contract exist; the replayer itself is the next build.
- **Public mod gallery** on top of the shared mod format (`MOD_SHARING.md`): a curated `.lua` repo with
  one-click import from the Hub — the one moat that grows through other people's hands (UGC loop).
- **Self-serve indie commercial tier with a public price** (owner decision): removes the "email me for a
  license" friction that filters out exactly the solo devs who would evangelize the project.
- **Ship one real jam-scale game** on CoreAI local-first and write the postmortem — the single highest
  trust-per-effort artifact (one shipped case study beats any feature list). Needs an owner.
