# CoreAI Framework Roadmap

> Top-level orientation map for the whole CoreAI framework. Each track links to the detailed
> document that owns its design; this file does not duplicate them. Near-term work items live in
> [`TODO.md`](../TODO.md); shipped work in the package changelogs
> (e.g. [`Assets/CoreAI/CHANGELOG.md`](../Assets/CoreAI/CHANGELOG.md)).

Last updated: 2026-08-12. Current lockstep package version: **7.0.1**.
The patch restores Unity 6.6 UI Toolkit compilation; see [Release plan](#4-release-plan).

---

## 1. Vision

**CoreAI is a runtime AI game-creation framework for Unity.** An in-game LLM — reachable through
the Hub chat UI, running on a local GGUF model or any OpenAI-compatible API — creates and modifies
the game *while it runs*: it spawns and edits world objects, writes and repairs Lua mods, defines
game logic, reads its own logs, and (next) builds runtime UI. The framework is **AI-first**
(APIs, error messages, and docs are optimized for machine consumption and self-repair loops) and
**realtime** (no edit-then-play cycle; everything must work in a built player, mid-session).

Two audiences, one framework:

- **Players** create games by talking to the AI inside a running game. The mod dialect is
  Roblox-shaped Lua, because LLMs know the Roblox API from training data better than any invented
  schema — the AI hallucinates less and ships working code faster, and humans get a familiar,
  documented API for free.
- **Developers** embed CoreAI as an AI modding layer into their own, existing Unity games —
  including meter-scale titles (horror, co-op, etc.). Integration is a host profile plus the
  packages the game actually needs; the host game keeps its own physics, assets, and controllers.

The headline scenario is **live co-creation**: everyone joins the running game and builds it
together — the in-game analog of Roblox Studio Team Create, except CoreAI needs no separate
editor product because the game *is* the editor (the realtime principle). It falls out of
pieces the tracks below already carry: the multiplayer join snapshot is the same serializer as
the world file (Track C), `ClientWritePolicy: Open` will admit everyone as a builder (MVP12)
while the partial-authority resolver later enables rules like build-but-not-delete (Track B),
each player talks to the AI through their own Hub chat (Track D), one-shot objects carry origin
tags, every connected player carries a per-world role — Creator (grantable, the Team Create
analog) or Player — gating the human-facing AI tool surface, while game-sanctioned AI creation
(a mod calling the reserved AIService) stays available to pure Players under the mod's own
grants (ROBLOX_API_ROADMAP §2), the
autosave-before-every-AI-mutation tier is the shared world's safety net, and a manual save is a
shareable place package anyone can host next.

CoreAI is a **framework, not a game**. Behavior that could be an opinion ships as configuration:
the per-world `ClientWritePolicy` (RobloxParity default / Strict / Open, behind a single
authority-resolver seam so partial `(instance, property)` authority is a later resolver swap),
the `RobloxSpace` scale constant (1 stud = 0.28 m default, 1:1 available), capability tiers,
LLM endpoint routing profiles, and the host integration profile. Everything created at runtime —
world state, mods, memories, (soon) UI — is versioned, persisted, revertible, and shareable.

## 2. Package map

Six UPM packages, released in lockstep (all currently 7.0.2):

| Package | What it is |
|---|---|
| `com.neoxider.coreai` (`Assets/CoreAI`) | Portable C# core, no UnityEngine dependency: orchestration, function-calling tools, agent memory, `AgentBuilder`, skills, resilience decorators (retry/timeout/fallback/circuit-breaker), multi-endpoint LLM routing contracts. |
| `com.neoxider.coreaiunity` (`Assets/CoreAiUnity`) | Unity layer: always-available chat UI, orchestration wiring and persistence; provider-backed HTTP/MEAI/LLMUnity implementations compile with `COREAI_LLM`; world commands, settings and demo glue. |
| `com.neoxider.coreaimods` (`Assets/CoreAIMods`) | Lua modding layer: Lua-CSharp sandbox (AOT/WebGL-safe), script-engine seam, mod runtime + stores, Luau downleveler, Lua log service, `execute_lua` / `manage_mods` tools, the Roblox-like API (in progress). |
| `com.neoxider.coreaihub` (`Assets/CoreAIHub`) | UI Toolkit Hub window: tabbed pages (Chat, Settings, Statistics, Mods, C#/Lua-authored pages) over `HubPageRegistry`. |
| `com.neoxider.coreaibenchmark` (`Assets/CoreAIBenchmark`) | Game-creation benchmark harness (G1–G8 PlayMode scenarios), scoring, model leaderboard. |
| `com.neoxider.coreaimcp` (`Assets/CoreAIMcp`) | Optional in-game MCP server for loopback-only control of the running game by an external MCP client. |

Dependency direction: `coreai` ← `coreaiunity` ← (`coreaimods`, `coreaihub`);
`coreaibenchmark` and `coreaimcp` depend on `coreai` + `coreaiunity` + `coreaimods` (not on the hub).
Provider implementations and Lua are independent positive opt-in modules: `COREAI_LLM` enables
provider-backed HTTP/MEAI/LLMUnity clients/transports, while `COREAI_LUA` enables Lua. Portable
orchestration/chat, scripted/stub clients, tool contracts and required MEAI references remain in Core
with neither symbol; both symbols enable the full provider + Lua runtime.

## 3. Tracks

Parallel workstreams. Each lists goal, current state, next milestones, and the owning document.

### Track A — Roblox-like Lua mod API (priority #1)

**Goal.** Mods are written in Roblox-shaped Lua (`game`, `workspace`, `Instance.new`, `task.*`,
services, datatypes) so the in-game LLM authors them from its training priors. Luau syntax is
downleveled to Lua 5.2 for the bundled Lua-CSharp VM; studs/right-handed math live inside mods,
with exactly one conversion boundary (`RobloxSpace`).

**Current state.** MVP0 (engine abstraction seam: neutral `CoreAI.Scripting` contracts, `LuaCs*`
adapters as the single VM layer, seam-honesty tests) has landed, plus the quarantine error
policy, the Luau→Lua 5.2 downlevel preprocessor (standalone, 93 EditMode tests), the Lua log
service core, and editor Lua/Luau syntax highlighting. The **MVP1 core** — pure-spec datatypes,
`InstanceRegistry`/`RbxDataModel`, the `RobloxSpace` conversion boundary (1 stud = 0.28 m
default), the GameObject materialization binder, and the Lua Instance/datatype surface — is on
disk with its Lua wiring in progress.

**Next milestones.** Finish MVP1 (Lua wiring + §5.1.8 acceptance gate), MVP2 (task scheduler,
signals, clocks, `game:GetService` with loud stubs), then the ladder through MVP17 (world files, RBXL, mod UX, skill-as-docs, gameplay services,
DataStore, input, Mirror, replication, dedicated server, GUI, audio/FX, in-game console,
performance/WebGL).

**Detail:** [`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`](CoreAIMods/ROBLOX_API_ROADMAP.md)
(the definitive MVP0–MVP17 ladder and all locked decisions) ·
[`Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md`](CoreAIMods/SCRIPT_ENGINE_SEAM.md) ·
[`Docs/CoreAIMods/mod-system.md`](CoreAIMods/mod-system.md).

### Track B — Multiplayer (Mirror)

**Goal.** Roblox's own model: single-player is a server with one local client. Transport is
Mirror (via NeoxiderTools `Neo.Network`); topology order is Null loopback (solo) → host mode
(listen server) → dedicated server. WebGL is solo or pure client only — it never hosts.

**Current state.** Designed-first: CoreAI has zero multiplayer code today. `INetworkBridge` is
topology-agnostic from the first interface draft; `RemoteEvent`/`RemoteFunction`/
`ReplicatedStorage` arrive as local-loopback stubs in the mod-API ladder well before Mirror,
and `InstanceRegistry` reserves the Mirror `netId` field from day one.

**Next milestones.** Loopback networking stubs (with Track A), then MVP11 (Mirror bridge core,
host mode), MVP12 (replication + `ClientWritePolicy` enforcement through the authority-resolver
seam), MVP13 (dedicated headless server). Mod-facing APIs do not change when the loopback is
replaced.

Backlog (live co-creation support):

- Per-player attribution of changes in logs and diagnostics (who spawned/edited/deleted what).
- Per-player undo of one's own recent changes in a shared world.
- Creator/Player roles: the `Role` field rides the Player record from the early rungs (a
  player-dimension input to the authority resolver); role granting + UI (Team Create analog)
  land with MVP12 (ROBLOX_API_ROADMAP §2, roles decision).

**Detail:** ROBLOX_API_ROADMAP §2 (transport/topology decisions), §MVP11–13.

### Track C — Worlds & persistence

**Goal.** A world is a shareable **place package** — a zip of `world.json` + mods + manifest —
that serves as both the disk save format and the multiplayer join snapshot. Two-tier backups:
manual player-owned slots plus an autosave before every AI mutation. RBXL import/export makes
existing Roblox places a content on-ramp.

**Current state.** World state persistence (`WorldStateManager`), versioned mod source stores
with revert, and the self-contained shareable mod bundle (`ExportMod`/import with capability
masking) are shipped. The unified place package, backup tiers, and RBXL are not yet built.

**Next milestones.** MVP3 (place package + two-tier backups), MVP4 (RBXL import/export),
MVP9 (DataStoreService on the shared JSON contract).

**Detail:** ROBLOX_API_ROADMAP §MVP3/§MVP4/§MVP9 ·
[`Docs/CoreAIMods/MOD_SHARING.md`](CoreAIMods/MOD_SHARING.md) (shipped bundle format + the
community-gallery proposal).

### Track D — AI runtime loop

**Goal.** The loop that makes runtime creation reliable: chat → agent → tools → world/mods →
logs → self-repair. The AI can read what it broke and fix it without a human in the loop.

**Current state.** Shipped and battle-tested: orchestrator with parallel tool execution, agent
memory, self-service skills (`read_skill`/`manage_skills`, ~91% token savings vs. inlining),
runtime multi-endpoint LLM routing (dynamic endpoint/profile CRUD, per-role/per-agent/per-request
profiles, secret hygiene via `SecretReference`), streaming that survives small-model reality,
resilience decorators, and the quarantine-not-unload mod error policy with auto-repair prompts.
The standalone Lua log service (`ILuaLogService`, `get_mod_logs` read-only tool, LLM-friendly
formatter) has its core landed but is not yet wired into the mod runtime's print/error capture,
DI, or the Programmer tool set.

**Next milestones.** Wire the Lua log service end-to-end; the skill-equals-docs artifact with a
generated implemented-vs-stub API manifest (MVP6); the in-game console + closed AI self-repair
loop (MVP16); the **async agent workflow** — play and task the AI in parallel, in any role:
background generation already landed (Esc collapses the Hub, generation continues); remaining
are a task queue (new instructions enqueue instead of blocking or derailing current work) and
an unobtrusive HUD status indicator ("agent building: X…" + completion notification) that needs
no open Hub — the loop is closed by existing pieces (autosave before every AI mutation,
quarantine, the agent reading Lua logs to self-fix while the player keeps playing); the
human-driven tool surface (Hub chat: manage_mods / execute_lua / save-load world) is role-gated
per the Creator/Player decision (ROBLOX_API_ROADMAP §2) — the play-while-tasking pattern itself
is never gated; runtime UI tools (Track F's R4 `ui_command`/`ui_query`); later, sub-agent
orchestration (R9, explicitly last).

**Detail:** [`TODO.md`](../TODO.md) (Roblox ladder foundation item 8 — MVP5 deliverable 7 —
plus R4, R5–R9) ·
[`Docs/CoreAI/agent-vision.md`](CoreAI/agent-vision.md) · ROBLOX_API_ROADMAP §MVP6/§MVP16.

### Track E — Host-game embedding

**Goal.** Dropping CoreAI into an existing meter-scale Unity game is a first-class scenario:
one **host integration profile** (ScriptableObject: `RobloxSpace` scale — 0.28 default —
capability defaults, host service/object bindings, per-world `ClientWritePolicy`) and it works.
Assets are never rescaled; only numbers convert at the API boundary; mod physics uses per-body
gravity so Roblox-feel mods coexist with a host running Earth gravity.

**Current state.** The building blocks exist piecemeal: capability tiers with host-masked
grants, `AdditionalGameplayBindings` seam for injecting host APIs into mods, chat cursor-safety
option (`ChatRequiresVisibleCursor`) for first-person games, optional-module compilation, and
the Hub as an overlay. The single profile asset that bundles them does not exist yet.

**Next milestones.** The host integration profile ships as a deliverable in MVP16; NeoxiderTools
interplay stays the reference host (Mirror bridge mappings via `Neo.Network`, demo host games).
Cursor/hub UX polish continues incrementally.

**Detail:** ROBLOX_API_ROADMAP §2 ("Host integration profile", "Units / scale",
"Assets under scale") and §MVP16.

### Track F — Editor & DX

**Goal.** The developer-facing surface: readable mod sources in the editor, honest inspectors,
and — because CoreAI's premise is creating the game *inside* the running game — a runtime-first
UI path where editor tooling is convenience, never a requirement.

**Current state.** Shipped: `.lua`/`.luau` importers, highlighted read-only `TextAsset`
inspector, standalone `CoreAI/Lua Script Viewer` window, with an engine-independent tokenizer
reusable by a future in-game console; Hub pages for mods/settings/statistics; Getting Started
window; benchmark editor window.

**Next milestones.** The R4 runtime UI wave (flagship of the next minor after the release):
UXML/USS-as-source-of-truth interpreted at runtime, one shipped theme with design tokens,
`ui_command`/`ui_query` LLM tools, Lua `ui_*` bindings, persistence via the version-store
pattern, a built-in "ui-builder" skill, and a hard small-model acceptance gate (9B with the
skill / 27B without must build and repair a HUD). Editor materialization of AI-built screens to
real assets is the secondary path. Keep syntax highlighting in sync with Luau constructs; a Hub
Audit Log page. Backlog (explicitly NOT a priority — AI does everything first): manual editing
for Creators (hand-placing/moving/gizmos besides the AI chat) — architecturally free later
because manual ops go through the same Instance operations + authority resolver as AI/mods
(a UI layer, not a new system).

**Detail:** [`TODO.md`](../TODO.md) §[R4] (full spec inline) · ROBLOX_API_ROADMAP §MVP7/§MVP14.

### Track G — Platforms & performance

**Goal.** Everything works in built players (RUNTIME-first rule): Standalone Mono and IL2CPP,
WebGL as solo/pure-client, dedicated headless server. Budgets bound every mod: per-call
step/time/allocation guards, coroutine resume guards, Lua generation rate limits.

**Current state.** Lua-CSharp is managed and AOT/WebGL-safe; sandbox budgets and the coroutine
guard are shipped and adversarially audited; WebGL persistence syncs through
`CoreAiWebGlPersistence`; the benchmark package (G1–G8, six-dimension scoring, role fitness,
model leaderboard) is the standing conformance/quality instrument, runnable in players.

**Next milestones.** CI player builds (Standalone/WebGL IL2CPP) once a licensed runner exists
(F-12); `File.Replace`-on-WebGL verification; IL2CPP verification of the `DelegateLlmTool`
boundary; performance regression suite (F-20); MVP17 (10k-instance world targets, per-mod
budget accounting, WebGL acceptance checklist); benchmark G9/G9r scenarios for the runtime-UI
gate.

**Detail:** [`TODO.md`](../TODO.md) §[R0.6] and audit-cleanup sections ·
[`Docs/BENCHMARK.md`](BENCHMARK.md) · [`Docs/BENCHMARK_LEADERBOARD.md`](BENCHMARK_LEADERBOARD.md) ·
ROBLOX_API_ROADMAP §MVP17/§6.5.

## 4. Release plan

- **7.0.1 (current, 2026-08-12).** Patch release migrating the chat bubble custom UI Toolkit element
  to `[UxmlElement]` / `[UxmlAttribute]` for Unity 6.6 while preserving existing UXML type and attribute names.
- **7.0.0 (2026-08-01).** Breaking migration to independent positive
  provider/Lua symbols, full-demo repository baseline, four-leg CI matrix, opaque multi-user
  persistence keys, scope-aware cancellation, session-only persistence, prompt-cache layering and
  chat lifecycle hardening. This remains the 7.x breaking baseline.
- **7.x minors.** Subsequent Roblox API and runtime-UI rungs ship as compatible 7.x minors. Patch
  releases carry fixes only; every release passes the full EditMode/PlayMode gates and updates the
  changelog for every touched package.
- **Mod `api_version`.** The `mod.json` `api_version` line is a **separate contract, starting
  at 1**, independent of the package semver. It increments only when the mod-facing API breaks;
  the loader version-gates mods against the host's supported API version (ROBLOX_API_ROADMAP
  §MVP5). Package minors that only *add* API surface do not move it.

## 5. Principles

1. **AI-first.** The primary author is the in-game LLM; humans are second. Errors carry mod id,
   script, line, a stable code, and a suggested fix — the reader is an agent that will
   immediately patch the mod. The AI skill document *is* the documentation.
2. **Realtime.** Creation happens inside the running game — hot reload, live logs, in-play
   debugging are core features. Every feature answers "does this work in a built player,
   on device, mid-session?" (RUNTIME-first, `AGENTS.md`).
3. **Framework, not a game.** Opinions ship as configuration — write policies, scale, capability
   tiers, routing profiles, host profiles — never hardcoded behavior. Embedding into someone
   else's game is a config drop, not a fork.
4. **Roblox parity by default, explicit deviations.** API shapes follow the current official
   Roblox reference; every intentional deviation is a numbered DEV item in the roadmap doc,
   never a silent difference.
5. **Loud stubs.** Unimplemented surface fails with a structured `NOT_IMPLEMENTED` error naming
   the roadmap phase and a workaround — machine-parsable from day one, never silent.
6. **Clean architecture, test-enforced.** All new work follows `Docs/ARCHITECTURE_RULES.md`:
   engine-free Domain assemblies, inward-only dependencies, interface-first composition,
   UniTask/CancellationToken discipline — with per-module architecture-fitness tests, so
   layering is verified by CI rather than convention.
7. **Tests as conformance gates.** Every MVP rung has a Definition of Done backed by
   rule-citing conformance tests, the real-script corpus ("paste → runs"), seam-honesty scans,
   and the benchmark as the live quality bar; adversarial re-audits are part of the process.
8. **Safety is layered, not optional.** Sandbox capability tiers with host masking, execution
   budgets, quarantine-not-unload, versioned sources with revert, autosave before every AI
   mutation, redacted secrets and logs.
