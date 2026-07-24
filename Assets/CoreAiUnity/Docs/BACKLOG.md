# CoreAI Backlog

This document tracks future work that should not block the current CoreAI/RedoSchool MVP gate.
Items here are intentionally not active TODO checkboxes.

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

> **Shipped:** Lua now runs on the WebGL/IL2CPP player via **Lua-CSharp**, a managed, AOT-safe VM.
> `SecureLuaEnvironment.IsSupported` is gated by
> the `SecureLuaEnvironment.WebGlLuaOptIn` capability flag (wired from `ICoreAISettings.EnableLuaOnWebGl` /
> `CoreAISettingsAsset.EnableLuaOnWebGl`, on by default for new assets) instead of a hard `false`. IL2CPP
> stripping is held off by the package `link.xml` (`Assets/CoreAiUnity/link.xml`, preserving the `Lua` / `Lua.Annotations` assemblies plus the WebGL-active Lua
> binding types). Lua-CSharp uses source-generated marshalling rather than a reflection-based interpreter fallback,
> so host-callback marshalling works without emitted IL. The `Full` reflection tier (`unity_*` bindings) stays disabled on WebGL —
> `CoreAILifetimeScope` forces `effectiveFullLuaAccess = false` under `UNITY_WEBGL && !UNITY_EDITOR`. See the
> `WebGlLuaSelfTest` demo and `SecureLuaEnvironment.TryRunSelfTest` for an in-player smoke test. Remaining open
> question: binary-size impact and how a host can prune unused bindings for the smallest web build.

## Skill System (before MVP2)

- **Multi-file skills the model can browse structurally.** Today one `SkillSet` = one `Instructions`
  string, and `read_skill(name)` returns the whole blob. Add a third disclosure level like Claude Code
  (a main doc + `references/*` read lazily): a skill assembled from several files/sections, the model
  SEES the whole structure (index/table of contents) up front, and READS only the file/section it needs
  on demand (e.g. `read_skill_section(name, section)` or a sub-doc index). Lets big references (the Rbx
  API skill is already large) load in pieces instead of one dump.
- **Skill self-improvement.** A mechanism for a skill to update/augment its own instructions from
  experience (learned corrections, new patterns the agent discovered at runtime). Design after the
  multi-file work; both land before MVP2.

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

- **3D picking: `workspace:Raycast`, `Camera:ScreenPointToRay`/`ViewportPointToRay`, and `ClickDetector`.**
  None exist yet, so a mod cannot tell WHICH 3D part the mouse clicked — the Block Clicker sample had to
  drive its upgrade blocks with the U/P keys instead of clicking them. Needed for any point-and-click 3D
  game. (`UserInputService` already gives mouse position + button state.)
- **Physics velocity for parts** (`AssemblyLinearVelocity` / a `BodyVelocity`-like impulse). Today a burst
  effect (Tetris line-clear, racer crash) is hand-animated in Heartbeat because there is no way to fling an
  unanchored part with an initial velocity.
- **`RunService` newer aliases** `PreSimulation`/`PostSimulation`/`PreRender` (Roblox renamed
  Stepped/Heartbeat/RenderStepped); the classic names work, add the aliases for full parity.

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
- **[MEDIUM] Reconcile Rbx-vs-Roblox naming at the adapter edge** — domain assemblies are clean `Rbx*`,
  but binding/scripting public types are still `Roblox*` (`RobloxSpace`, `IRobloxCameraRig`,
  `RobloxWorldHost`, `LuaCsRobloxApiBindings`, …; ~315 `Roblox` vs ~1152 `Rbx` in Mods). Rename to
  `Rbx*` or record the exception in the Rbx roadmap the way FullAccess/`unity_*` is recorded.
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
> marshalling boundary every Lua handler call crosses. All findings cite v6.3.2.

- **[HIGH] Per-guarded-call allocation storm** — `LuaCsExecutionGuard.ExecuteGuarded` (`:156,:171`) and
  `LuaCsScriptExecutionGuard.Invoke` (`:47,:54,:57`) allocate PER CALL: a fresh `LuaFunction` hook + its
  capture closure, `Stopwatch.StartNew()` (ref type), `new LuaValue[]`/`new object[]`, and a box per
  returned `LuaValue`. At `MinTimerIntervalSeconds = 0.05` a `hooks_on("tick")` fires at 20 Hz, so a
  couple of timers/handlers = hundreds of allocs/sec → periodic GC stutter on the single-threaded WebGL
  Boehm GC. Fix: one reusable hook whose closure holds a resettable mutable state object (re-entrancy is
  already covered by `InstalledHooks`); `Stopwatch.GetTimestamp()` (long) instead of `StartNew()`;
  thread-static scratch `LuaValue[]` buffers by arity; skip building `boxed[]` when results are discarded.
- **[HIGH] Per-instruction guard overhead** — the hook is installed with count = 1
  (`LuaCsExecutionGuard.cs:223`), so it runs on EVERY VM instruction, each paying
  `sw.ElapsedMilliseconds` + `GC.GetTotalMemory(false)` (`:207`). A few-thousand-instruction handler pays
  that thousands of times/frame; on IL2CPP this ~multiplies interpreter cost. Fix: sample — install with
  count ≈ 128–256 and scale the step budget, or read `GC.GetTotalMemory` every Nth hook. A doubling
  alloc-bomb still trips within one window, so detection latency is unaffected; per-instruction cost
  drops ~100×.
- **[MEDIUM] Per-event-dispatch churn** — `LuaCsModRuntime.DispatchPendingEvents` (`:1020,:1037`):
  `handlers.ToArray()` per dispatched event + `params object[2]` per handler invoke. Reuse a per-mod
  scratch snapshot list; add a 2-arg `InvokeGuarded` overload avoiding the params array.
- **[MEDIUM] Var-args marshalling boxes every `LuaValue`** — `LuaCsValueMarshaller.Box` at the
  `RegisterVarArgs` surfaces (`hooks_on`, `events_emit`, `mods_export/call`, `print`). Typed accessors and
  the `part.CFrame=` write path already avoid it. Prefer typed accessors; longer-term a `LuaValue`-typed
  fast path on the seam.
- **[MEDIUM] SSE streaming parses a full `JObject` per token** — `MeaiOpenAiChatClient` (`:614,:1754,
  :1811`) does `line+"\n"` → `Split('\n')` → `JObject.Parse` per delta + per-delta LINQ. Network-bound,
  but token-rate JObject churn can hitch the chat UI on WebGL. Parse the single line directly with a
  lightweight reader for the common `choices[0].delta.content` shape; fall back to `JObject.Parse` only
  for tool-call/usage chunks.
- **[MEDIUM] Camera capture stalls the main thread** — `CameraLlmTool.CaptureCameraJpeg` (`:91-128`)
  allocates a `RenderTexture`+`Texture2D` per call, `ReadPixels` (GPU→CPU sync stall) + `EncodeToJPG` +
  `ToBase64String`, all on the main thread. On-demand, but each vision capture visibly hitches the frame.
  Pool the offscreen targets; use `AsyncGPUReadback` where supported.
- **[LOW] `RbxCFrame.GetComponents()`/`ToString()`** allocate arrays — fine unless a mod polls them per
  frame.

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
- **Content-safety auto-wiring**: the `IContentFilter` module + wordlist filter ship and are tested; wiring
  it into the pipeline by default (not just available) is the follow-up that unblocks education/console.
