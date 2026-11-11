# CoreAI Framework Roadmap

> Top-level orientation map for the whole CoreAI framework. The one MVP ladder of record, with every
> rung's goal, done-so-far, to-do, tests and Definition of Done, is
> [`ROBLOX_API_ROADMAP.md` §4](CoreAIMods/ROBLOX_API_ROADMAP.md#4-mvp-ladder-the-ladder-of-record);
> §4 below is its one-screen summary. Each track links to the document that owns its design; near-term
> work items live in [`TODO.md`](../TODO.md), the current status in [`PLAN.md`](../PLAN.md), shipped
> work in the package changelogs (e.g. [`Assets/CoreAI/CHANGELOG.md`](../Assets/CoreAI/CHANGELOG.md)).

Last updated: 2026-11-11 (release 7.56.0). The plan was re-ordered that day at the owner's request: the
flagship is a realtime play-and-create game with no Studio, and work is ordered by product milestones
(§4). Rungs keep their ids — the ladder was renumbered once, on 2026-09-24, and is not renumbered again.
Plan decisions D1–D8 were decided by the tech lead and D9–D14 follow the owner's request; the owner may
override any of them. All seven package manifests carry the same release version (see §2).

---

## 1. Vision

**CoreAI is a framework of building blocks for AI-driven dynamic games in Unity.** An in-game LLM —
reachable through the Hub chat UI, running on a local GGUF model or any OpenAI-compatible API —
creates and modifies the game *while it runs*: it spawns and edits world objects, writes and repairs
Lua mods, defines game logic, reads its own logs, and (later) builds runtime UI. The framework is
**AI-first** (APIs, error messages, and docs are optimized for machine consumption and self-repair
loops) and **realtime** (everything must work in a built player, mid-session).

**Blocks, not one game** (`AGENTS.md`; normative detail in
[`ARCHITECTURE_RULES.md` §2.1](ARCHITECTURE_RULES.md)). The same blocks build a single-player game, a
player-hosted co-op game, a dedicated-server game, or an AI layer embedded into someone else's Unity
game. Everything is a configurable, replaceable block — agent behaviour (roles, prompts, policies,
memory), LLM endpoints and routing, tools, skills, mods and capability tiers, Hub pages,
stores/persistence, world and scale settings, network topology (solo / player-host / dedicated /
client), host-game embedding — and a game composes them through documented seams (interfaces,
installers, profiles) without forking package code. Opinions live in presets, not in blocks.

**The flagship: one realtime play-and-create game built on the blocks.** Roblox-shaped worlds and
scripts with **no Studio**: there is one mode (plan decision D9). The player walks or flies through the
running world — a free-flight toggle, as in a creative-mode sandbox — talks to the AI in an in-world
chat, and every change is live at once: no publish step, no restart, no separate editor. It is **one
product**, not the framework: it lives in its own Unity project and consumes the packages through UPM
(plan decision D7); this repository keeps the packages, presets, samples, demos, bot clients and
harnesses. What it promises, each a measurable gate of a milestone (§4):

- **Instant.** A sentence in the chat becomes working logic in the running world within seconds, and
  the AI repairs its own errors while the game keeps running. Roblox needs a publish and a server
  restart for a code change and gives players no way to generate game logic in a live server
  ([`dev-docs/COMPETITIVE_SCAN_2026-11-11.md`](../dev-docs/COMPETITIVE_SCAN_2026-11-11.md)).
- **Convenient by default.** A fresh world has controls, a camera, a chat, a good-looking scene and a
  checked model connection without configuration; every default is a replaceable block.
- **Together.** Several players create in one room at once, each with their own AI — the in-game
  analog of Roblox Studio Team Create, without the Studio.
- **Yours.** Any model, a free local one included; the world is a file its author owns, hosts and
  ships as a standalone build.
- **Roblox knowledge carries over.** The mod dialect is the Roblox API, and Roblox places and scripts
  import (MVP14, prepared by MVP4 and MVP13). The owner's round-trip requirement stands; import comes
  first and export no longer gates a release (plan decision D12).

Creation tools live inside play: selecting, moving, painting and block building are in-play actions
that go through the same Instance operations and authority resolver as the AI and mods. The Explorer
and Properties panels and the Play/Stop test run of the earlier plan are backlog.

Two audiences, one framework:

- **Players** create games by talking to the AI inside a running game. The mod dialect is
  Roblox-shaped Lua, because LLMs know the Roblox API from training data better than any invented
  schema — the AI hallucinates less and ships working code faster, and humans get a familiar,
  documented API for free.
- **Developers** embed CoreAI as an AI modding layer into their own, existing Unity games —
  including meter-scale titles (horror, co-op, etc.). Integration is a profile plus the packages the
  game actually needs; the host game keeps its own physics, assets, and controllers. The portable core
  runs without Unity, so the same harness — agents, an orchestrator mode, tools, skills, prompts,
  routing, memory, chat and decision models — also builds applications that are not games.

Live co-creation falls out of pieces the tracks below carry: the multiplayer join snapshot is the same
serializer as the world file (Track C); host grants let a co-builder's writes travel as intents the
server checks and applies (MVP6; there is no `Open` write policy — owner decision 3), while the
partial-authority resolver later enables rules like build-but-not-delete (Track B); each player talks
to the AI through their own chat (Track D); one-shot objects carry origin tags; every connected player
has a per-world **player role** — Creator (grantable, the Team Create analog) or Player — gating the
human-facing AI tool surface (MVP11), while game-sanctioned AI creation (a mod calling the reserved
`AIService`) stays available to pure Players under the mod's own grants; the
autosave-before-every-AI-mutation tier is the shared world's safety net; and a manual save is a
shareable place package anyone can host next.

Behavior that could be an opinion ships as configuration: the per-world `ClientWritePolicy`
(RobloxParity default / Strict, plus host grants for co-building, behind a single authority-resolver
seam), the `RbxSpace` scale (1 stud = 0.28 m default, 1:1 available), capability tiers, LLM endpoint
routing profiles, and — with MVP10 — one composition profile and a set of presets. Everything created
at runtime — world state, mods, memories, (later) UI — is versioned, persisted, revertible, and
shareable.

## 2. Package map

Seven UPM packages, released in lockstep (all currently 7.56.0):

| Package | What it is |
|---|---|
| `com.neoxider.coreai` (`Assets/CoreAI`) | Portable C# core, no UnityEngine dependency: orchestration, function-calling tools, agent memory, `AgentBuilder`, skills, resilience decorators (retry/timeout/circuit-breaker; the fallback decorator lives in the Unity layer), multi-endpoint LLM routing contracts. |
| `com.neoxider.coreaiunity` (`Assets/CoreAiUnity`) | Unity layer: always-available chat UI, orchestration wiring and persistence; provider-backed HTTP/MEAI/LLMUnity implementations compile with `COREAI_LLM`; world commands, settings and demo glue. |
| `com.neoxider.coreaimods` (`Assets/CoreAIMods`) | Lua modding layer: Lua-CSharp sandbox (AOT/WebGL-safe), script-engine seam, mod runtime + stores, Luau downleveler, Lua log service, `execute_lua` / `manage_mods` tools, the Roblox-like API (in progress). |
| `com.neoxider.coreaihub` (`Assets/CoreAIHub`) | UI Toolkit Hub window: tabbed pages (Chat, Settings, Statistics, Mods, C#/Lua-authored pages) over `HubPageRegistry`. |
| `com.neoxider.coreaibenchmark` (`Assets/CoreAIBenchmark`) | Game-creation benchmark harness (G1–G8 PlayMode scenarios), scoring, model leaderboard. |
| `com.neoxider.coreaimcp` (`Assets/CoreAIMcp`) | Optional in-game MCP server for loopback-only control of the running game by an external MCP client. |
| `com.neoxider.coreaimirror` (`Assets/CoreAIMirror`) | Optional Mirror transport (compiles only with the `MIRROR` define): authenticated admission, `RemoteEvent`/`RemoteFunction` traffic over the wire, server-side kick, server clock offset. |

Dependency direction: `coreai` ← `coreaiunity` ← (`coreaimods`, `coreaihub`);
`coreaibenchmark` and `coreaimcp` depend on `coreai` + `coreaiunity` + `coreaimods` (not on the hub);
`coreaimirror` depends on `coreai` + `coreaimods` plus Mirror itself, which is never vendored.
Provider implementations and Lua are independent positive opt-in modules: `COREAI_LLM` enables
provider-backed HTTP/MEAI/LLMUnity clients/transports, while `COREAI_LUA` enables Lua. Portable
orchestration/chat, scripted/stub clients, tool contracts and required MEAI references remain in Core
with neither symbol; both symbols enable the full provider + Lua runtime.

## 3. Tracks

Workstreams across the one ladder. Each lists goal, current state, the rungs that carry it next, and
the owning document. Rung names are the current ones ([`ROBLOX_API_ROADMAP.md` §4.1](CoreAIMods/ROBLOX_API_ROADMAP.md)
maps the old numbers).

### Track A — Roblox-like Lua mod API

**Goal.** Mods are written in Roblox-shaped Lua (`game`, `workspace`, `Instance.new`, `task.*`,
services, datatypes) so the in-game LLM authors them from its training priors, and they round-trip
with Roblox. Luau syntax is downleveled to Lua 5.2 for the bundled Lua-CSharp VM; studs and
right-handed math live inside mods, with exactly one conversion boundary (`RbxSpace`).

**Current state.** MVP0 (the engine seam), MVP1 (the instance core, 6.3.0) and the MVP2 surface have
landed: the scheduler (`task.*`, the R4.2 frame pipeline, deferred signals at the nine R5.5
resumption points), the service catalog with loud stubs, the shared JSON contract, the clocks on
`IRbxClockSource`, loopback remotes, the `Model` pivot slice and all 45 `Enum.Material` items. MVP2 is
not closed: the G10 capacity gate fails on the AI backend (not code), and `BindToRenderStep` is still a
loud stub. The "Gameplay services I" slice (the old MVP8: Players, Humanoid, Touched, Debris,
TweenService, Raycast, CollectionService) has landed too. The 2026-09-24 audits and their fix waves
(released in 7.47.0) made one mod's fault stop breaking the frame for others, budgeted string
patterns and memory per resume, made budget trips uncatchable by `pcall`, and brought `CanCollide`,
`Instance.Changed` and TweenService in line with Roblox. Three audit rounds over those waves followed.
Both script surfaces now convert arguments and property writes by one Luau/Roblox rule (numbers ↔
numeric strings, integer truncation, strict booleans, Enum Name/Value on instance members; round-trip
gap RT4 closed, the `tostring` text residue is DEV-17), and a reload cleans the previous run's startup
objects by default, with a keep mode for the old hot reload
([`mod-system.md`](CoreAIMods/mod-system.md) §5c). Two Lua fixtures run unchanged in Roblox Studio and
CoreAI with identical output (the parity slice of 7.47.2 and the castle quest of 7.47.4,
[`CASTLE_QUEST_PARITY.md`](CoreAIMods/CASTLE_QUEST_PARITY.md)).

**Next.** MVP4 (script contexts, `require`, and `Script`/`LocalScript`/`ModuleScript` instances per
plan decision D1) runs alongside milestone M1, which adds the player-facing surface: `Humanoid:Move`
and the camera members (P1) and a voxel `workspace.Terrain` subset (P4). M2 brings the `Lighting`
members (P9), the solo halves of GUI (MVP15), DataStore (MVP16) and audio/FX/animation (MVP17), the
templates and Luau parity prerequisites (MVP13) and the generated skill manifest (MVP18); their network
halves follow in M3 and Roblox import (MVP14) in M4.

**Detail:** [`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`](CoreAIMods/ROBLOX_API_ROADMAP.md) (the ladder
and every locked decision) · [`Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md`](CoreAIMods/SCRIPT_ENGINE_SEAM.md) ·
[`Docs/CoreAIMods/mod-system.md`](CoreAIMods/mod-system.md) · [`Assets/CoreAI/Docs/RBX_API.md`](../Assets/CoreAI/Docs/RBX_API.md).

### Track B — Multiplayer (milestone M3)

**Goal.** Roblox's own model: single-player is a server with one local client. Transport is Mirror
over kcp, plus a WebSocket transport for WebGL clients, behind `INetworkBridge` (plan decision D4);
topology order is Null loopback (solo) → host mode (listen server) → dedicated server. WebGL is solo,
pure client or creator-mode client — it never hosts (plan decision D6). The room target is ~100
players (plan decision D5, §4).

**Current state.** `INetworkBridge` is topology-agnostic; `NullNetworkBridge` is the solo loopback.
Since 7.42.0 a scene can switch Mirror on: the optional `com.neoxider.coreaimirror` package (`MIRROR`
define) provides `CoreAiMirrorNetworkBridgeProvider`, which `CoreAiModsLifetimeScope` registers as the
`INetworkBridge`, so `RemoteEvent`/`UnreliableRemoteEvent`/`RemoteFunction` traffic runs through
Mirror's real message handlers and batcher — tested over an in-memory transport; no two-process run
over a real socket has been made yet. The bridge has authenticated admission with a deadline,
newest-wins reconnects, per-channel payload limits, a readiness handshake, server clock anchors
(held clocks reach clients, also from a world loaded from a package), kick and supersede notices,
per-sender budgets — 32 remote-started handlers apart from 128 threads of induced work, with signal
listeners deferred in a bounded queue instead of dropped — and reliable sends held until admission
(the fix waves and audit rounds 1–3, released in 7.47.0). The engine-free replication core (member-level change reporting, dirty set,
per-recipient Spawn/Patch/Remove planning, replica-side applier) and the authority model
(`WorldAclAuthorizer`, `WriteGrantLedger`, `IntentGateway`, `ClientWritePolicy`) are built and tested
in process. Missing: script contexts, host mode, the join snapshot over the wire, world-state
replication over the wire, networked characters, the dedicated server, an inbound rate limit and
version negotiation.

**Next.** MVP4 (script contexts — the hard dependency) lands during M1. Milestone M3 is multiplayer for
small rooms: MVP5 (host mode over a real socket + the join snapshot, the player-host preset), MVP6
(world-state replication + write authority, a binary delta codec), the network half of MVP7
(client-owned own character per plan decision D3) and MVP11 (roles and per-player AI over the network).
MVP8 (dedicated server + WebGL client) and MVP9 (scale: interest management, bot load test, VM guard
cost) are M4, with the room size set after spike S1 (plan decision D11). Mod-facing APIs do not change
when the loopback is replaced.

Backlog (live co-creation support): per-player attribution of changes in logs and diagnostics;
per-player undo of one's own recent changes in a shared world (the command stack of MVP12).

**Detail:** [ROBLOX_API_ROADMAP](CoreAIMods/ROBLOX_API_ROADMAP.md) §2 (transport/topology decisions)
and §MVP4–§MVP9 · [`Assets/CoreAIMirror/README.md`](../Assets/CoreAIMirror/README.md) ·
[`dev-docs/REPLICATION_PHASE0.md`](../dev-docs/REPLICATION_PHASE0.md).

### Track C — Worlds, templates & persistence

**Goal.** A world is a shareable **place package** — a zip of `world.json` + mods + manifest — that
serves as both the disk save format and the multiplayer join snapshot. Two-tier backups: manual
player-owned slots plus an autosave before every AI mutation. Model packages and a template library
let the AI and Creators start from templates; Roblox interchange makes existing Roblox content an
on-ramp and an export target.

**Current state.** **MVP3 is closed (2026-09-25, released in 7.47.0)** on the Unity gate (Unity
6000.3.14f1, 2026-09-25): EditMode full 6550 total / 6539 passed / 0 failed / 11 skipped, the `core`,
`llm` and `lua` legs 0 failed (5266, 6234 and 5582 total), the Mirror leg 6550 / 0 failed; PlayMode
`FastNoLlm` 95 total / 94 passed / 0 failed / 1 skipped; the engine-free portable suite 2172 passed /
0 failed, the Lua tier 1846 of 1848 passed / 0 failed / 2 not run. The live-model PlayMode suite was
not part of the gate (4 of 165 failed on that branch, all live-model timeouts while the LM Studio
server was unresponsive); the runs on 7.47.1–7.47.3 were partial, and one full run on a settled tree
is still open. The IL2CPP/WebGL player checks, the WebGL page-reload smoke and spikes S1/S2 are open
follow-ups that gate the next networked rung, not MVP3's product scope. Built: the `.world` ZIP place package,
`FileRbxWorldPackageStore` with create-once manual slots (capped at 64 / 256 MiB) and a
two-phase-durable autosave ring, `ConfirmedWorldMutationGate` in front of every `execute_lua` and
mutating `manage_mods` action, `RbxWorldRuntimeSessionController` for transactional session
replacement, the `save_world`/`load_world`/`list_autosaves`/`load_autosave` tools with the
confirm-before-restore flow and JSON failure statuses, the built-player **Hub → World Loads** page, a
durable startup selection (a player-confirmed world reopens after a restart), at most 256 distinct mod
sources per world, and — since audit round 2 — mods restarting and restoring in their load order
(`LuaModManifest.LoadOrder`); after rounds 2 and 3 the startup selection also follows Hub and host
edits of the mod sources, records world-tree changes at most every 5 s and never for physics or camera
motion, and tells the caller when a change will not reopen. Each MVP3 DoD item is proven by a named
test ([`WORLD_PACKAGE.md`](CoreAIMods/WORLD_PACKAGE.md#acceptance-status-mvp3)). The real WebGL
page-reload smoke is an open follow-up. Model packages, templates and the Roblox formats are not built; the
self-contained mod bundle (`ExportMod`) is.

**Next.** The open MVP3 follow-ups (the IL2CPP/WebGL player checks, the WebGL page-reload smoke,
spikes S1/S2, one full live PlayMode run) are milestone M0. M1 adds a compact voxel entry to the place
package (P4) and a cheaper autosave-before-mutation gate (P3). M2: MVP13 (`.model` packages, the
template library, place templates) and MVP16 (DataStoreService on the shared JSON contract). M4: MVP14
(the Roblox formats, import first) and asset entries in the package — textures, meshes, sounds (P10).

**Detail:** [`Docs/CoreAIMods/WORLD_PACKAGE.md`](CoreAIMods/WORLD_PACKAGE.md) (the shipped format,
limits, durability, and session-replacement contract) · ROBLOX_API_ROADMAP §MVP3/§MVP13/§MVP14/§MVP16 ·
[`Docs/CoreAIMods/MOD_SHARING.md`](CoreAIMods/MOD_SHARING.md) (shipped bundle format + the
community-gallery proposal).

### Track D — AI runtime loop

**Goal.** The loop that makes runtime creation reliable: chat → agent → tools → world/mods →
logs → self-repair. The AI can read what it broke and fix it without a human in the loop — and, in a
multiplayer room, each player has their own AI under their own identity.

**Current state.** Shipped and battle-tested: orchestrator with parallel tool execution, agent
memory, self-service skills (`read_skill`/`manage_skills`; about 91% fewer tool-definition tokens in the
documented 50-tool example, an estimate that P15 replaces with a measurement),
runtime multi-endpoint LLM routing (dynamic endpoint/profile CRUD, per-role/per-agent/per-request
profiles, secret hygiene via `SecretReference`), streaming that survives small-model reality,
resilience decorators, and the quarantine-not-unload mod error policy with auto-repair prompts for
load errors. The Lua log service is wired end to end and the `get_mod_logs` tool is registered for the
Programmer role. Since 7.48.0 a prompt carries images and text files end to end, and any tool can
return images to a vision model (`LlmToolImageResult`; the camera tools use it). Background generation
landed (Esc collapses the Hub, generation continues). The per-player chat is server-side only (`ActorKeyedInGameLlmChatServiceFactory`); there is no network chat
channel. The G10 real-provider capacity gate FAILED (17.4–38.5 s p95 on one lane) — a model and
hardware question (risk R7).

**Next.** M0: results that cannot lie and a loud failure when a module is missing (P11 stage 1). M1:
talking NPCs (P12 stage 1) and decision models as a model kind next to chat (P13). M2: the Connect-API
flow with per-endpoint checks (P11 stage 2), model calls from mods (P12 stage 2), an orchestrator mode
with sub-agents and a task queue (P14, the former R9), roles, tools, skills and prompts as data (P15),
the tool review with camera v2 (P16), runtime self-repair and the generated skill manifest (MVP18) and
AI tools that start from templates (MVP13). M3: MVP11 (player roles, a network chat channel). The
planned runtime UI tools of [`TODO.md`](../TODO.md) [R4] (`ui_command`/`ui_query`, not built) share
their element factory with MVP15.

**Detail:** [`TODO.md`](../TODO.md) (R4–R9) · [`Docs/CoreAI/agent-vision.md`](CoreAI/agent-vision.md) ·
[`Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md`](CoreAI/AGENT_ROLES_AND_TOOLS.md) · ROBLOX_API_ROADMAP
§MVP11/§MVP18.

### Track E — Composition & host-game embedding

**Goal.** Building blocks ([`ARCHITECTURE_RULES.md` §2.1](ARCHITECTURE_RULES.md)): one entry point
assembles any product from presets, every preset boots in a test, and dropping CoreAI into an existing
meter-scale Unity game is a profile, not a fork. Assets are never rescaled; only numbers convert at the
API boundary; mod physics uses per-body gravity so Roblox-feel mods coexist with a host running Earth
gravity.

**Current state.** The blocks are replaceable through DI ports and assets, but there is no single
composition entry point and there are no presets or boot tests yet. Composability audit (2026-09-24):

| Block | Configured or replaced today through | Blocker |
|---|---|---|
| Composition roots | `CoreAILifetimeScope` + `CoreAiModsLifetimeScope` and seven installers | no single root, no presets; editor setup menus hard-code `Assets/<package>` paths (break on a Git-URL install) |
| Agent behaviour | `AgentPromptsManifest`, `AgentBuilder`, `AgentMemoryPolicy.AddToolForRole`/`SetToolsForRole` | the built-in role table lives in the `AgentMemoryPolicy` constructor |
| LLM endpoints and routing | `CoreAISettingsAsset`, `LlmRoutingManifest`, runtime endpoint/profile CRUD | `CoreAISettings.Instance` is a process-wide static read at ~115 sites; a missing `COREAI_LLM` silently swaps in a stub client |
| Tools | `AddToolForRole` in code | every Lua/world tool is wired to `Programmer` in `CoreAiModsInstaller`; no per-role or per-world data |
| Skills | `SkillSetAsset`, `RoleSkillsBinding[]`, `FileSkillStore` | built-in skills are C# string literals |
| Mods and capability tiers | `@coreai` header / manifest capabilities, `IBundledModSource` | Full-access flags duplicated on both scopes; no `api_version`, no contexts |
| Hub pages | `HubPageRegistry` (instance, DI) | — |
| Stores | `storeId`, `AgentMemoryPersistenceMode` | fixed paths under `persistentDataPath/CoreAI/`; `FileLuaModSourceStore` has no root parameter |
| World and scale | `RbxWorldSettings`, `RbxWorldHost` | the `RbxSpace` scale is process-static (one scale per process) |
| Network topology | `RbxNetworkBridgeProviderBehaviour` | a second notion, `AiNetworkExecutionPolicy.AllPeers`, is the default |
| Identity and admission | `IActorIdentityProvider`, `IActorAdmissionProvider` | a client/server id mismatch is a documented trap |
| Host embedding | `IRbxCharacterMotorProvider`, `AdditionalGameplayBindings`, `ChatRequiresVisibleCursor` | no profile asset |
| Static facade | the `CoreAi` static API | a static service locator, which `ARCHITECTURE_RULES.md` §2 forbids |

**Next.** M0 adds the first preset — a one-click play-and-create world (P3) — with a boot test, and P15
(M2) moves the role table and the tools per role into data. MVP5 and MVP8 add the player-host and
dedicated-server presets with boot tests; **MVP10** (M4) consolidates: a `CoreAiProfile` asset (absorbing the host integration profile), one root, five presets
(solo AI sandbox, player-host co-op, dedicated N-player server, client, embed-in-my-game), the statics
removed from the main paths, tools per role as data, and a replace-with-fake test for every block in
the table above. NeoxiderTools is one host that can adapt these seams; the framework packages take no
dependency on it (owner decision 2; `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` §F.4).

**Detail:** [`ARCHITECTURE_RULES.md`](ARCHITECTURE_RULES.md) §2.1 · ROBLOX_API_ROADMAP §2 ("Host
integration profile", "Units / scale", "Assets under scale") and §MVP10 ·
[`CHARACTER_MOTOR_BRIDGE.md`](CoreAIMods/CHARACTER_MOTOR_BRIDGE.md).

### Track F — In-play creation & DX

**Goal.** Creating the game *inside* the running game, in one mode: the player's own controls, an
in-world chat and in-play creation tools instead of a Studio, a runtime-first UI path where editor
tooling is convenience, never a requirement, and readable mod sources everywhere.

**Current state.** Shipped: a runtime Lua editor page in the Hub (highlighting, history, Save & run = a
reload that cleans the previous run's startup objects unless **Keep objects on Save & run** is on; the
Mods and Logs tabs rebuild at most once per panel update, on the main thread); Hub pages for mods,
world loads, settings and statistics; `.lua`/`.luau` importers, the highlighted read-only `TextAsset`
inspector and the `CoreAI/Lua Script Viewer` window, with an engine-independent tokenizer; the Getting
Started window; the benchmark editor window. There is no Explorer, Properties panel, selection, gizmo,
undo/redo, Play/Stop or Fly yet (`IsStudio` is a constant), and the runtime UI interpreter of [R4] has
not started. A fresh world has no default controls, mouse look or camera modes, input is not gated
while a chat is open, the chat's default role has no build tools, and no menu builds a world scene
(repository survey, 2026-11-11). The editor-tooling rung of the old ladder was dropped: its runtime equivalents are MVP12
and MVP18.

**Next.** M0–M1: a one-click world and a cheaper edit loop (P3), default controls with a camera and a
fly toggle (P1), the in-world chat with an agent status line (P2), and the Block world demo (P6) on
voxel Terrain (P4), and game UI as UXML and USS text handed over from Lua at runtime (P18, plan decision
D14). M2: in-play creation tools — selection, grab-move, rotate, scale, paint, duplicate,
an undo stack (P5) — which replace the Studio core of MVP12 (plan decision D9; its Explorer, Properties
and Play/Stop are backlog), the template picker of MVP13 and GUI (MVP15, sharing the [R4] element
factory). The mechanisms go in the packages; the final UX polish goes in the flagship (plan decision
D7).

**Detail:** ROBLOX_API_ROADMAP §MVP12/§MVP15 · [`TODO.md`](../TODO.md) §[R4].

### Track G — Platforms, look, performance & scale

**Goal.** Everything works in built players (RUNTIME-first): Standalone Mono and IL2CPP, WebGL as
solo or as a client of a server (it never hosts), a Linux dedicated server, and Android as a client.
Budgets bound every mod: per-resume step/time/allocation guards, coroutine resume guards, Lua generation
rate limits. Speed is stated only as a published measurement (P19). The room size is chosen after spike
S1; 100 players per room stays the aspiration (§4).

**Current state.** Lua-CSharp is managed and AOT/WebGL-safe; sandbox budgets and the coroutine guard
are shipped and adversarially audited (a budget trip cannot be caught by `pcall` and never disarms the
guard; a nested run gets at most what its enclosing run has left; library calls back into Lua share a
128-level weighted cap that keeps native stack use at most 428 KB on CoreCLR and about 1.38 MB on Mono,
which an IL2CPP or WebGL player cannot otherwise bound; Luau source nested past 200 levels is refused
instead of overflowing the stack — the IL2CPP/WebGL stack checks are on the Unity checklist in
[`TODO.md`](../TODO.md)); WebGL persistence is the engine's own automatic `persistentDataPath`
synchronization, and `CoreAiWebGlPersistence` reports immediately whether it is armed for this page
instead of awaiting an `FS.syncfs` callback Unity 6.3 no longer delivers
(`CoreAIWebGlPersistentDataSyncBuildGuard` fails a build whose web template does not arm it); local
GGUF models are unavailable in a browser player and return a documented limitation message
([KNOWN_ISSUES.md](../Assets/CoreAiUnity/Docs/KNOWN_ISSUES.md)); a WebGL player was checked in real
Chrome for 7.48.0 (chat, image and text attachments, the camera tool, model-written `execute_lua`,
refused outbound HTTP, persistence across a page reload); the benchmark package (G1–G8,
six-dimension scoring, role fitness, model leaderboard) is the standing quality instrument. Scale so
far is measured in process only: `tools/ScaleHarness` drives 20/50/100/200 actors over the loopback on
CoreCLR ([`dev-docs/SCALE_CHARACTERIZATION.md`](../dev-docs/SCALE_CHARACTERIZATION.md)); the guarded VM
on Mono runs about 150 k instructions/s against 24 M on CoreCLR
([`tools/vmbench/RESULTS.md`](../tools/vmbench/RESULTS.md)), and IL2CPP has not been measured (risk
R1). Instance ceilings today: 16,384 per desktop world and 2,048 per actor, 4,032 per WebGL world,
100,000 in the package format. Look (repository survey, 2026-11-11): 36 of the 45 materials carry
normal maps; the nine procedural ones — the default Plastic and Water among them — do not, water is
static, 15 of the 16 demo scenes run without post-processing, and textures load eagerly (about 99 MB).

**Next.** M0: spikes S1 (VM cost on Mono and, where a build target exists, IL2CPP) and S2 (bytes per
update). M1: the default look — post-processing on by default, a Hub Graphics page, a WebGL tier (P7) —
and the first materials pass: detail on Plastic, animated water, glass (P8 stage 1); honest performance —
frame time, garbage per frame and Lua throughput measured in built players, budgets as tests, then
optimization by measured rank (P19). M2: the texture
tier with lazy loading and anti-tiling (P8 stage 2) and the Lighting API (P9). M4: MVP8 (the Linux
dedicated server and the WebGL client); **MVP9** (interest management, a bot client, the VM guard cost,
the O(N) paths, higher ceilings — a staircase against the room size chosen after S1); MVP19
(incremental JSON/ZIP on WebGL, the WebGL client soak, an Android client, the performance regression
suite F-20). CI player builds (Standalone/WebGL IL2CPP) once a licensed runner exists (F-12).

**Detail:** [`TODO.md`](../TODO.md) §[R0.6] and audit-cleanup sections ·
[`Docs/BENCHMARK.md`](BENCHMARK.md) · [`Docs/BENCHMARK_LEADERBOARD.md`](BENCHMARK_LEADERBOARD.md) ·
ROBLOX_API_ROADMAP §4.3, §MVP9, §MVP19 and §6.5.

## 4. Release plan

**The order of record (2026-11-11; plan decisions D9–D14).** Work is ordered by product milestones,
and each milestone ends with playable demos and a release. Packages inside a milestone run in parallel
where their files do not overlap. Rung ids (MVPn) and package ids (Pn) are stable names, not an order.
The goal, done-so-far, to-do, tests and DoD of every rung and package are in
[`ROBLOX_API_ROADMAP.md` §4](CoreAIMods/ROBLOX_API_ROADMAP.md#4-mvp-ladder-the-ladder-of-record).

| Milestone | What a player gets | Contents | Gate |
|---|---|---|---|
| **M0 Ready to play** | A new project reaches a working world and an honest model connection in minutes | P3 (one-click world), P11 stage 1 (truthful failures, install fixes), spikes S1/S2, the open MVP3 player checks, one live-model run with a valid provider key | From a fresh checkout: one menu, one key, the first AI-made object; a missing module fails loudly |
| **M1 Block world** | The Minecraft-style demo, solo: walk and fly, build with blocks and by chat, talk to NPCs, switch on the autopilot | P1, P2, P3, P4, P6, P7, P8 stage 1, the day-cycle slice of P9, P12, P13, P18, P19, P20; MVP4, the solo half of MVP15 and the Luau math slice of MVP13 alongside | The Block world demo — Lua end to end, interface included — passes its recorded script in the editor and in a WebGL player; the P19 budgets hold; the bundled sample mods and published demos run on the defaults |
| **M2 Whole games from chat** | A complete small game — HUD, sound, saving — made by talking; the harness upgrades | The solo halves of MVP16 and MVP17; MVP13; the self-repair and manifest parts of MVP18; P5, P8 stage 2, the rest of P9, P10 stage 1, P11 stage 2, P14, P15, P16, P17 | A person who writes no code gets a playable game with HUD, sound and saving in 15 minutes; a "whole game" benchmark tier measures it |
| **M3 Together** | Several players create in one room | MVP5, MVP6, the network half of MVP7, MVP11, the network halves of MVP15–MVP17 | Four players in a room, two prompting the AI at once; every edit visible within 1 tick + RTT; the world survives joins and leaves |
| **M4 Platform** | Dedicated servers, scale, Roblox import, asset import, hardening | MVP8, MVP9, MVP10, MVP14 (import first), the rest of MVP18, MVP19, P10 stage 2, the flagship's own project | The rungs' own DoDs |

**The release rule.** A milestone ships as one or more 7.x minors. Each release keeps the gate: the DoD
of what it claims, EditMode and PlayMode `FastNoLlm` 0 failed in Unity, the WebGL checklist passed, the
changelogs written, and all seven packages bumped together (`python tools/bump_version.py <version>`).
Patch releases carry fixes only. Work outside the plan is capped: between two releases that move a
milestone, at most one release may carry only out-of-plan work (risk R15).

Rungs and packages on one screen (status at 7.56.0, 2026-11-11):

| Id | Name | Milestone | Status | Size |
|---|---|---|---|---|
| MVP0 | Engine abstraction seam | — | landed | M |
| MVP1 | Instance/DataModel core | — | landed (6.3.0) | L |
| MVP2 | Scheduler, signals, clocks, services | — | surface landed; G10 and `BindToRenderStep` open | L |
| MVP2.5 | Online foundation + persistence release | — | landed 7.3.0–7.43.0 (history) | — |
| — | Gameplay services I | — | landed slice | L |
| MVP3 | World/place package + backups | M0 (follow-ups) | closed 2026-09-25 (7.47.0); S1/S2, the player checks and one live run open | S |
| MVP4 | Script contexts & client runtime | M1 (alongside) | not started | M |
| P1 | Player controls and camera | M1 | not started | L |
| P2 | In-world chat and agent HUD | M1 | not started | M |
| P3 | Instant edit loop and one-click world | M0–M1 | not started | M |
| P4 | Voxel Terrain | M1 | not started | L |
| P6 | Block world demo | M1 | not started | M |
| P7 | Default look and the Hub Graphics page | M1 | not started | M |
| P8 | Materials v2 | M1 (stage 1), M2 (stage 2) | 36 of 45 materials textured with normals | M + M |
| P11 | Truthful results and the Connect-API doctor | M0 (stage 1), M2 (stage 2) | not started | M |
| P12 | Gameplay LLM API, agents from Lua and talking NPCs | M1 | `AIService` is a stub | L |
| P13 | Decision models | M1 | not started | L |
| P18 | Runtime UI from Lua: UXML and USS | M1 | not started; the [R4] specification | L |
| P19 | Honest performance: measurement, budgets, optimization | M1 | `tools/vmbench` numbers only | L |
| P20 | Mod folders | M1 | one flat `category` level | M |
| P5 | In-play creation tools | M2 | click picking only | L |
| P9 | Lighting API | M1 (day-cycle slice), M2 | structure only | M |
| P10 | New materials and asset import | M2 (stage 1), M4 (stage 2) | `MaterialVariant` over built-in textures | M + L |
| P14 | Orchestrator mode | M2 | plan only | L |
| P15 | Roles, tools, skills and prompts as data | M2 | role table hard-coded | M–L |
| P16 | Tool review and camera v2 | M2 | camera tools v1 | M |
| P17 | Benchmark touch-ups | M2 | four reporting defects open | S |
| MVP13 | Model packages, template library, Luau parity prerequisites | M2 | planned | M–L |
| MVP15 | GUI + remaining input: the Roblox GUI classes over the P18 element factory | M1 (solo), M3 (network) | planned | L |
| MVP16 | DataStore + leaderstats | M2 (solo), M3 (network) | planned | M |
| MVP17 | Audio / FX / animation | M2 (solo), M3 (network) | planned | M |
| MVP18 | Mod UX rest, skill manifest, console + self-repair | M2 (self-repair, manifest), M4 (rest) | planned | M–L |
| MVP5 | Host mode over a real socket + join snapshot | M3 | planned | L |
| MVP6 | World-state replication + write authority | M3 | planned | L |
| MVP7 | Networked characters & controls | M1 (controls, as P1), M3 (network) | planned | L |
| MVP11 | Roles (Creator/Player) & per-player AI over the network | M3 | planned | M |
| MVP8 | Dedicated server + WebGL client | M4 | planned | M–L |
| MVP9 | Scale | M4 | planned; the room size is set after S1 | XL |
| MVP10 | Composable framework: one root, presets, host profile | M4 (first preset in M0) | planned | M |
| MVP12 | Studio mode core | backlog (replaced by P5, plan decision D9) | — | — |
| MVP14 | Roblox interchange | M4 (import first) | planned | L |
| MVP19 | Performance, WebGL and mobile hardening | M4 | planned | M |

Sizes: S ≈ ≤2 agent-days, M ≈ ≤1 agent-week, L ≈ multi-week, XL ≈ several L-sized milestones. Calendar
estimates for the milestones are in [`PLAN.md`](../PLAN.md). Tests: EM EditMode · PM PlayMode `FastNoLlm`
· PT portable engine-free suite · PL portable Lua tier · MP two or more processes · LT load test with
bot clients · MS manual scene with recorded evidence.

**Scale targets (plan decisions D5 and D11).** The 2026-09-24 numbers stay as the aspiration, not as a
frozen release promise: 100 players per room; a 30 Hz server tick with p99 frame ≤33 ms (server Lua
≤8 ms); downstream per client average ≤50 KB/s and p99 ≤100 KB/s; upstream ≤10 KB/s; join ≤10 s on LAN;
a 10,000-instance place for 30 min with 0 disconnects and a retained heap delta ≤100 MB; on a reference
8-vCPU Linux server. Spike S1 measures the guarded VM on Mono and IL2CPP first — the estimate in §6 for
100 players on Mono is about 63 ms per frame — and the room size MVP9 gates on is chosen from that
measurement. M3 gates on a small room (2–8 players). No concurrency number is claimed publicly before it
is measured (owner decision 4).

**Plan decisions** (detail in [`ROBLOX_API_ROADMAP.md` §8.1](CoreAIMods/ROBLOX_API_ROADMAP.md)).
D1–D8, decided 2026-09-24 by the tech lead: D1 hybrid script instances (views over the mod source
store); D2 superseded by D9; D3 a client-owned own character with server validation, every other part
server-owned; D4 Mirror/kcp plus WebSocket for WebGL, behind `INetworkBridge`; D5 the scale targets,
revised by D11; D6 WebGL as solo, pure client and creator-mode client, never a listen server; D7 the
flagship in its own Unity project over UPM; D8 Lua-CSharp with a cheaper guard first, native Luau on the
server only as the fallback. D9–D14, 2026-11-11 at the owner's request: D9 one play-and-create mode, no
Studio; D10 demo-gated milestones with parallel packages instead of a strictly sequential,
multiplayer-first ladder; D11 the room size is chosen after S1; D12 Roblox import first, export later
and not a release gate; D13 decision models are a model kind next to chat; D14 game UI is UXML and USS text that
Lua hands to the engine at runtime, with the Roblox GUI classes as a compatibility layer. The owner may
override any of them.

**History.**

- **Current release.** The version in §2 is the latest release. Every release is described in the
  package changelogs ([core](../Assets/CoreAI/CHANGELOG.md),
  [Unity host](../Assets/CoreAiUnity/CHANGELOG.md)); the milestones below are kept as history.
- **7.48.0–7.50.2 (2026-09-30 to 2026-10-15).** Releases outside the ladder: attachments and camera
  images end to end and visible Rbx material relief (7.48.0); safe generation progress and an explicit
  failure for an interrupted SSE stream (7.49.0); a messenger-style chat feed (7.50.0); tool-contract and
  memory prompts that name only real behaviour (7.50.1); `AgentSessionInspector` and the structured retry
  carry the whole request (7.50.2).
- **7.42.0–7.43.0 (2026-09-16).** Mirror multiplayer can be switched on from a scene; remotes run
  through Mirror (proven over an in-memory transport, not yet across a real socket); a kick closes the
  connection.
- **7.1.1 (2026-08-31).** Patch release for `com.neoxider.coreai` /
  `com.neoxider.coreaiunity`: `call_skill_tool` now falls through to the role's own top-level tools
  instead of answering a miss with a plain "not found" result, and a rejected tool call is logged.
- **7.1.0 (2026-08-30).** MVP1 tails closed and the MVP2 scheduler core laid in: Roblox-shaped
  rotation properties on parts, corrected game samples, refreshed docs.
- **7.0.7 (2026-08-27).** Patch release keeping OpenAI-compatible reasoning fields
  diagnostic-only: neither non-streaming nor SSE reasoning is promoted into the visible response
  or carried into persistent history and other long-lived records.
- **7.0.0 (2026-08-01).** Breaking migration to independent positive
  provider/Lua symbols, full-demo repository baseline, four-leg CI matrix, opaque multi-user
  persistence keys, scope-aware cancellation, session-only persistence, prompt-cache layering and
  chat lifecycle hardening. This remains the 7.x breaking baseline.
- **Mod `api_version`.** The `mod.json` `api_version` line (MVP18) is a **separate contract, starting
  at 1**, independent of the package semver. It increments only when the mod-facing API breaks;
  the loader version-gates mods against the host's supported API version. Package minors that only
  *add* API surface do not move it.

## 5. Principles

1. **AI-first.** The primary author is the in-game LLM; humans are second. Errors carry mod id,
   script, line, a stable code, and a suggested fix — the reader is an agent that will
   immediately patch the mod. The AI skill document *is* the documentation.
2. **Realtime.** Creation happens inside the running game — hot reload, live logs, in-play
   debugging are core features. There is one mode: play and creation happen in the same running world,
   and nothing rewinds a shared world. Every feature answers "does this work in a built player, on
   device, mid-session?" (RUNTIME-first, `AGENTS.md`).
3. **A framework of building blocks, not a game.** Every subsystem is a block that a game can
   configure, replace or leave out without editing package code; opinions live in presets, and a
   product (the flagship included) is a preset plus its own code. Each shipped preset has a boot test,
   and a block that claims to be replaceable has a replace-with-fake test
   ([`ARCHITECTURE_RULES.md` §2.1](ARCHITECTURE_RULES.md); from MVP4 on, an implicit DoD item of every
   rung).
4. **Roblox parity by default, explicit deviations.** API shapes follow the current official
   Roblox reference; every intentional deviation is a numbered DEV item in the roadmap doc,
   never a silent difference.
5. **Round-trip parity.** Scripts, models and places are interchangeable with Roblox in both
   directions: Roblox content imports and runs unchanged, and what is made here exports and runs in
   Roblox. CoreAI-only constructs are refused or flagged on export and never dropped silently; the gate
   is a licensed corpus (MVP14).
6. **Loud stubs.** Unimplemented surface fails with a structured `NOT_IMPLEMENTED` error naming
   the roadmap phase and a workaround — machine-parsable from day one, never silent.
7. **Clean architecture, test-enforced.** All new work follows `Docs/ARCHITECTURE_RULES.md`:
   engine-free Domain assemblies, inward-only dependencies, interface-first composition,
   UniTask/CancellationToken discipline — with per-module architecture-fitness tests, so
   layering is verified by CI rather than convention.
8. **Tests as conformance gates.** Every rung has a measurable Definition of Done backed by
   rule-citing conformance tests, the real-script corpus ("paste → runs"), seam-honesty scans,
   and the benchmark as the live quality bar; adversarial re-audits are part of the process.
9. **Safety is layered, not optional.** Sandbox capability tiers with host masking, execution
   budgets, quarantine-not-unload, versioned sources with revert, autosave before every AI
   mutation, redacted secrets and logs.
10. **Instant and convenient by default.** A default exists for everything a first session needs —
    controls, a camera, a chat, the look of the scene, a checked model connection — and each default is
    a replaceable block. A click, a confirmation or a setting that stands between a new player and the
    first result is a defect, and so is a default a host game cannot switch off.

## 6. Risks

Ranked (2026-09-24). Each mitigation is placed in a rung; the three spikes turn the largest unknowns
into measured numbers early ([`ROBLOX_API_ROADMAP.md` §4.3](CoreAIMods/ROBLOX_API_ROADMAP.md)).

| Risk | What could go wrong | Evidence | Mitigation (rung) |
|---|---|---|---|
| R1 | Guarded Lua CPU on a Mono/IL2CPP server makes 100 players infeasible | The production guard fires every 4 instructions and reads the heap each time: Mono 148–158 k instructions/s vs 24.4 M on CoreCLR ([`tools/vmbench/RESULTS.md`](../tools/vmbench/RESULTS.md)); the N=100 workload is 9,434 guarded steps per frame (`dev-docs/SCALE_CHARACTERIZATION.md`), ≈63 ms per frame on Mono (estimate) | **Spike S1** (open MVP3 follow-up, gates the next networked rung); an adaptive batch with an allocation bound or per-thread accounting, thin-Lua guidance in the skill (MVP9); the server-only native Luau fallback (plan decision D8) |
| R2 | Replication bandwidth | JSON codec, no interest management, whole-node marks, synchronous `FireAllClients`; ~30–50 MB/s server out with JSON at 100 players (estimate) | **Spike S2** (open MVP3 follow-up, gates the next networked rung); binary quantized deltas (MVP6); interest tiers (MVP9) |
| R3 | Join snapshot size and time | Non-incremental JSON/ZIP; WebGL budget 4 MiB / 4,032 instances; kcp's reliable message size unverified | A chunked, streamed snapshot (MVP5); incremental decode (MVP19); **spike S3**: a 5 MB snapshot over real kcp (MVP5) |
| R4 | Character feel with server-owned physics | Owner decision 5; `SetNetworkOwner` is a loud stub | Client-owned own character with server validation (plan decision D3, MVP7) |
| R5 | Instance ceilings vs Roblox-scale places | 16,384 per desktop world / 2,048 per actor; WebGL 4,032; `ProcessPreSimulation` visits every instance | Dirty-only processing, ceilings raised with measurement (MVP9) |
| R6 | A single-threaded world with one mutation gate held across operations | `InstanceRegistry`'s gate (`TODO.md`) | One room per process; per-tick batching; contention measured with the publisher (MVP6) |
| R7 | LLM capacity for per-player AI | G10 FAILED: 17.4–38.5 s p95 on one lane; 12–25 lanes needed for 40 requests per 60 s | Role-gate the human AI to Creators, a queue and HUD, `AIService` quotas (MVP11); small local models for routine work |
| R8 | A large unverified surface | Before the MVP3 gate: Mirror fixtures compile-only, no Unity run of the wave. The 2026-09-25 gate ran every leg in Unity (EditMode 0 failed, Mirror leg included; PlayMode `FastNoLlm` 0 failed); 7.48.0 checked a WebGL player in real Chrome (chat, attachments, `execute_lua`); still unverified: IL2CPP players, the MVP3 stack and budget checks in IL2CPP/WebGL players, one full live-model PlayMode run, 2 Lua-tier cases not run on Linux | The MVP3 player-build follow-ups; a licensed CI runner (F-12) |
| R9 | Protocol security and compatibility | No inbound rate limit on decode/dispatch; no version negotiation | Both in MVP5 |
| R10 | In-play creation tools grow into a Studio | Absent today; the earlier plan had an XL Studio rung with many panels | Plan decision D9: tools are in-play actions (P5), panels and Play/Stop are backlog; mechanisms in the packages, UX in the flagship (plan decision D7) |
| R11 | Plan drift across ROADMAP, ROBLOX_API_ROADMAP, TODO, PLAN and dev-docs | 28 stale or contradictory statements found on 2026-09-24 (fixed the same day) | One ladder of record, one status table here; dev-docs plans marked as history |
| R12 | Two network stacks in one project | NGO 2.11 is installed and used by the example game (`Assets/_exampleGame`); the framework uses Mirror | NGO stays in the example game only and is never used by the framework (owner decision 2); whether the demo project keeps it is decided in `TODO.md` |
| R14 | The window closes | Roblox ships a prompt-to-game tab (Build, alpha) and an agentic Studio Assistant; Atomontage pitches live AI editing of a multiplayer world ([scan](../dev-docs/COMPETITIVE_SCAN_2026-11-11.md)) | Demo-gated milestones, a public Block world demo at M1; compete only on live player-authored logic, openness and ownership |
| R15 | Out-of-plan work displaces the plan | Nine releases and no rung between 2026-09-25 and 2026-10-28 | The cap in the release rule (§4); out-of-plan findings become `TODO.md` items, not waves |
| R16 | A block world outgrows the Part model | One GameObject per Part, ceilings of 16,384 per world and 4,032 on WebGL, per-part property blocks defeat the SRP Batcher | Voxel Terrain outside the instance quota (P4); a part-count benchmark before raising ceilings (MVP9) |
| R17 | The decision-model protocol is young | `/v1/systemone` reached llama.cpp on 2026-10-02; LM Studio support is undated; LLMUnity has none | One seam with three backends, the logprob fallback included (P13); the local path runs a `llama-server` sidecar |
| R13 | The round-trip parity scope is larger than the API ladder assumes | RT1–RT13: script containers, stdlib, coercion, DEV deviations, class coverage, four formats, asset ids, no licensed corpus | Script instances (plan decision D1, MVP4); the parity prerequisites (MVP13); the corpus gate with a stated subset and loss reports (MVP14); the licensed-corpus policy (ROBLOX_API_ROADMAP §7) |
