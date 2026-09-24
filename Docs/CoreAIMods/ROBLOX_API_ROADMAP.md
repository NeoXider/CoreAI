# CoreAI MVP ladder and the Roblox-like Mod API — Definitive Roadmap (MVP0..MVP19)

CoreAI's mod system evolves into a **Roblox-shaped Lua API**. Rationale: LLMs know the Roblox API
better than any custom game API (training-data volume), so an AI that writes mods in this dialect
hallucinates less and ships working code faster. Humans get a familiar, documented API for free.

This document is the single plan of record, and its §4 is the one MVP ladder of record for the whole
framework — the Roblox API, multiplayer, the framework consolidation and the Studio (renumbered
2026-09-24; §4.1 maps the old numbers). It supersedes the previous MVP1..MVP4 seed roadmap;
"Standing decisions" below are carried over in substance and refined where the detailed design
forced a choice. MVP0 (the engine abstraction seam) has **landed (2026-07-22)** and **MVP1
landed in 6.3.0 with the §5.1.8 acceptance gate green**, including the pulled-forward input and
camera slice (§MVP1); the Lua log service core has **landed** in
`Assets/CoreAIMods/Runtime/Logging/`, editor Lua/Luau syntax highlighting has **shipped
(editor-side)**, and the three normative Roblox-behavior reference docs are **complete** in
`Docs/CoreAIMods/RobloxReference/` (§2.1).
Since then **MVP2's functional surface has landed** (scheduler, deferred signals, services framework,
loopback remotes, the shared JSON contract, the clocks, the Model pivot slice, and the complete 45-item
`Enum.Material` catalog; two items keep it open — the G10 capacity gate and `BindToRenderStep`, §MVP2),
so has the "Gameplay services I" slice (the old MVP8), and **MVP3 (the world/place package) is code
complete (2026-09-24)** with its Unity verification gate pending; its contract and acceptance evidence
are in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md). Audits of MVP1, MVP2, the gameplay services and the
multiplayer foundation (2026-09-24) were followed by fix waves and two audit rounds over them, recorded
in the rung sections below and in `TODO.md`; they are unreleased. Next is MVP4 (script contexts),
the first of the multiplayer rungs.

**Architecture (normative)**: every deliverable in this ladder is built to
`Docs/ARCHITECTURE_RULES.md` — engine-free Domain assemblies (`noEngineReferences: true`),
inward-only references with the Unity adapter as the sole engine boundary, interface-first
DI via installers, UniTask + CancellationToken discipline, and a per-module
architecture-fitness test (the seam-honesty test is the template). Reviewers reject work
that violates it.

**Versioning (LOCKED; historical record)**: the Roblox-like Lua mod API was a breaking change to the
mod contract, so it shipped as a **major** bump — the CoreAI package family went **5.9.0 → 6.0.0**,
with the five packages that existed then bumped together. Today all **seven** packages
(`com.neoxider.coreai`, `coreaiunity`, `coreaimods`, `coreaihub`, `coreaibenchmark`, `coreaimcp`,
`coreaimirror`) move in lockstep (ROADMAP.md §4, mod-system.md §8); the current release is in each
package's `package.json` and the changelogs. `mod.json` `api_version` is a **separate contract line
starting at 1**, independent of the package version (§MVP18).

**Roblox API parity (LOCKED — user, 2026-07-23):** the Lua-facing API replicates Roblox **1:1** —
identical class / method / property / event / enum names and semantics — so copy-paste Roblox
scripts run unchanged. Keyboard/mouse input is Roblox `UserInputService` exactly
(`InputBegan`/`InputEnded`/`InputChanged`; `InputObject` with `KeyCode`/`UserInputType`/`Position`/
`Delta`; `Enum.KeyCode`/`Enum.UserInputType`; `IsKeyDown`/`GetKeysPressed`/`GetMouseLocation`/
`MouseBehavior`). No CoreAI-invented dialect on the Roblox surface. Unity implementations (New Input
System, primitives, physics, camera) are only **swappable backends behind seams** — the Lua names and
behavior must match Roblox regardless of backend. **Exception:** FullAccess (the Unity-native
`unity_*` reflection surface) is a **separate module with its own skill** and may diverge. Legacy
CoreAI-isms (`input_*` poll, `coreai_world_*`) stay for back-compat but are NOT the canonical Roblox
surface. Applies to every current and future rung.

**Round-trip parity (owner, 2026-09-24):** scripts, models and places are interchangeable with Roblox in
**both** directions — a Roblox script or model runs here unchanged, and what is made here exports and
runs in Roblox. It is the explicit DoD of MVP14, prepared by MVP4 (script instances, `require`,
`RunContext`) and MVP13 (Luau stdlib extensions, coercion parity, the Luau-only export lint, the
welds/constraints subset); the gaps RT1–RT13 are listed under §MVP14.

---

## 1. Principles

Two principles override Roblox fidelity whenever they conflict:

1. **AI-first authoring.** The primary mod author is the in-game LLM (humans second). API choices,
   docs, error messages, and logs are optimized for machine consumption and self-repair loops.
   Every error a mod can trigger carries: mod id, script, line, a stable error code, and a
   *suggested fix* — because the reader is an agent that will immediately try to patch the mod.
   The AI's skill document **is** the documentation: one artifact teaches the LLM and the human, and
   (from MVP18) embeds the machine-readable implemented-vs-stub manifest generated from code.
2. **Realtime.** The game is created and modified *while it runs*. Live edit is the default: hot
   reload, live logs, and in-play AI debugging are core features, not tooling extras. The in-game
   Studio (MVP12) adds an explicit Play/Stop "test run" for a Creator (plan decision D2) — an isolated
   per-creator session, never a rewind of a world other players are in — so edit-then-play is an
   option, never the only loop. Every feature must answer "does this work in a built player on
   device, mid-session?" (see `AGENTS.md` — RUNTIME-first).

Derived rules:

- **Loud stubs.** Anything not implemented yet ships as an **explicit stub** that fails with a
  structured `NOT_IMPLEMENTED` Lua error naming the roadmap phase and a workaround — never
  silently. Stub code carries `// TODO: MVP<n> — <what completes it>`. The stub-error format is
  stable and machine-parsable from day one (§5.2.7). (One documented exception: DEV-5,
  `task.synchronize`/`task.desynchronize`.)
- **Current Roblox shapes.** API names and signatures follow the *current* official Roblox
  reference (verified July 2026; footnotes §9): `task.*`, `RunService.PreSimulation/PostSimulation/
  PreRender`, `Instance` members incl. attributes and tags, `DataStore` async methods,
  `RemoteEvent`/`UnreliableRemoteEvent`. Deprecated legacy names (`wait`, `spawn`, `RenderStepped`,
  `Stepped`) are provided as aliases because tutorial-corpus scripts use them, with a
  once-per-mod deprecation note in the mod log.
- **Multiplayer-shaped from day one.** Single-player is "a server with one local client"
  (Roblox's own model). All networking APIs exist from MVP2 in local-loopback form behind
  `INetworkBridge`; the Mirror bridge (`com.neoxider.coreaimirror`, since 7.42.0) replaces the
  loopback when a scene switches it on, without changing any mod-facing API; host mode is MVP5.
  The bridge is topology-aware from the first interface draft (§2, Network topology).
- **WebGL is first-class** (`AGENTS.md`). It must stay green as a **solo player** (Null loopback
  bridge), as a **pure client** against a dedicated server (MVP8), and as a **creator-mode client**
  of a server (plan decision D6); it never hosts. Every rung's Definition of Done includes the WebGL
  acceptance checklist (§6.5): no threads, no blocking waits, no sync-over-async, and after a
  persistence write `CoreAiWebGlPersistence.Sync()` must report the engine's automatic
  `persistentDataPath` persistence as armed (`false` is a failure; `FS.syncfs` is never driven by
  hand).

## 2. Standing decisions (researched 2026-07; items marked LOCKED are user-confirmed)

- **VM**: keep Lua-CSharp v0.5.6 (bundled `Lua.dll`/`Lua.Annotations.dll` in
  `Assets/CoreAIMods/Plugins`; Lua 5.2, double-only numbers — near-Luau semantics). No MoonSharp return,
  no native Luau on the client (WebGL). Plan decision D8 (2026-09-24): keep Lua-CSharp with a cheaper
  guard first; native Luau **on the server only**, behind `IScriptEngine`, is the fallback if spike S1
  shows the guarded VM cannot meet the MVP9 budget. Stock upstream build: the local `NotifyTop` patch we
  carried on 0.5.5 landed upstream as PR #331, and our WebGL coroutine deadlock report (issue #327)
  as PR #329.
- **Luau syntax**: pure-C# downlevel preprocessor at mod ingestion (strip type annotations, rewrite
  `+=`/`continue`/string interpolation/`if`-expressions/`//`). Parser: the targeted
  **mini-rewriter**, chosen and **implemented on disk** (`Assets/CoreAIMods/Runtime/LuauDownlevel/`:
  `LuauLexer.cs`, `LuauRewriteParser.cs`, `LuauDownleveler.cs`); Loretta is reconsidered only if
  construct coverage proves insufficient (Q1 closed). darklua's rule set is the reference spec.
  Standalone (no VM dependency).
- **Engine abstraction**: all interpreter access goes through neutral interfaces so the VM can be
  swapped later — `IScriptEngine`, `IScriptState`, `IValueMarshaller`, `IScriptFunctionRegistry`,
  `IScriptCoroutine` (with `Kill()`), `IScriptExecutionGuard`, `IExecutionBudget`, plus the value
  contract (`ScriptValueKind`, `IScriptTable`, `ScriptCallContext`, `ScriptCallResult`,
  `ScriptSandboxProfile`, `ScriptRuntimeException`) — **landed** in
  `Assets/CoreAIMods/Runtime/Scripting/` with the `LuaCs*` adapters in `Scripting/LuaCs/`.
  Script values cross the seam as **opaque `object` handles** classified via
  `IValueMarshaller.GetKind` (`ScriptValueKind`); there is no `ScriptValue` wrapper type.
  The existing `LuaCs*` classes (`LuaCsModRuntime`, `LuaCsSecureEnvironment`,
  `LuaCsExecutionGuard`, `LuaCsCoroutineHandle`/`Runner`) become the single adapter layer. The
  Roblox API layer in this roadmap is written **only** against the neutral interfaces.
- **Multiplayer transport**: **Mirror** over kcp, plus a WebSocket transport for WebGL clients,
  behind `INetworkBridge` (plan decision D4, 2026-09-24). CoreAI's own optional package
  `com.neoxider.coreaimirror` (`Assets/CoreAIMirror/`, compiled only with the `MIRROR` define) is
  the bridge: raw Mirror message handlers with CoreAI's own envelopes, shipped since 7.42.0; the
  engine-free replication core has shipped since 7.39.0. NeoxiderTools' `Neo.Network` is **not** the
  bridge (owner decision 2, `dev-docs/MVP25_ONLINE_PLAN.md` §7): a framework package cannot depend on
  a separate product. The first draft (2026-07) borrowed its patterns as a design reference only —
  `NetworkEventDispatcher` ≈ `FireServer`→`FireAllClients`, `NetworkActionRelay` ≈ `FireClient`,
  `NetworkPropertySync` ≈ replicated properties, `NeoNetworkSpawner` ≈ server spawn,
  `NeoNetworkPlayer` ≈ `Players.LocalPlayer`, `NetworkContextActionRelay` ≈ the paired
  request/response of `RemoteFunction`. Host mode, world-state replication and the dedicated server
  are MVP5, MVP6 and MVP8.
- **Network topology (LOCKED)**: supported targets are **host mode** (listen server: server +
  local client in one desktop process) and **dedicated server** (headless, no local client).
  Implementation order: **Null loopback (solo) → host → dedicated** — host mode first because it
  is the fastest dev loop and mirrors Roblox Studio play-testing; dedicated server follows as its
  own MVP step (mostly headless bootstrap + CLI). WebGL can never host or listen: in browsers the
  modes are **solo**, **pure client** of a dedicated/remote server, and **creator-mode client** of a
  server (plan decision D6 — a Creator's edits travel as intents, so no hosting is needed); WebGL is a
  first-class target (`AGENTS.md`). `INetworkBridge` stays topology-agnostic
  (`Solo | Host | DedicatedServer | Client`) and **never** assumes "the server always has a local
  client" (e.g. `PreRender` must be skippable server-side; `Players.LocalPlayer` is nil on a
  dedicated server, Roblox parity).
- **Identity**: one `InstanceRegistry` owns identity from day one. Every Instance gets a stable
  `instanceId`; the record reserves fields for the Mirror `netId` and the CoreAI world-command name
  so the three identity spaces (Roblox Instance ref / Mirror netId / CoreAI name-string) reconcile
  in one place. This is the pre-payment on the most expensive future bridge. Details §3.3.
- **Coordinates (LOCKED)**: Roblox datatypes (`Vector3`, `CFrame`) are implemented as **pure math
  exactly to Roblox spec** — right-handed, `LookVector = -Z`, every constructor/operator/`lookAt`/
  `ToWorldSpace` per the official docs; mods never touch a Unity `Transform`. Exactly **one
  conversion boundary** exists: the static `RbxSpace` class (§5.1.4, D2). Nothing outside it
  converts — enforced by a lint-style test.
- **Units / scale (LOCKED, supersedes the earlier 1:1 default)**: scale is a single configurable
  constant inside `RbxSpace`; **default 1 stud = 0.28 m**. Rationale: the AI author's trained
  priors (`WalkSpeed 16`, `JumpPower 50`, `Gravity 196.2`, part sizes) produce correct *game
  feel* at 0.28 without re-teaching — copy-paste corpus achieves feel-parity, not just
  math-parity (Roblox gravity 196.2 studs/s² = 54.9 m/s² ≈ 5.6 g is the intended snappy feel).
  1 stud = 1 m remains available for meter-integrated games. Corpus tests run at 0.28 as primary
  + 1:1 smoke. Implementation rule: mod-driven rigidbodies get gravity applied **per-body**
  (custom force, `Rigidbody.useGravity = false`), never via global `Physics.gravity`, so
  Roblox-physics mods coexist with a host game running Earth gravity. The AI skill teaches the
  active scale (the "Rbx API" skill; §MVP18).
- **Assets under scale (LOCKED)**: **only numbers convert at the API boundary; assets are never
  rescaled.** (1) Primitives (`Part` Block/Ball/…) use Unity unit primitives; the binder sets
  `localScale = Size × RbxSpace.MetersPerStud`. Shapes Unity lacks (Wedge, CornerWedge, the
  Roblox-oriented Cylinder) get our own meshes authored **normalized to 1 unit = 1 stud** — the
  only stud-authored assets allowed; the same scaling formula applies. (2) Existing
  meter-authored prefabs (host game, NeoxiderTools) are **never** rescaled — that breaks
  physics/colliders/animations/NavMesh and is a forbidden operation; mods read them with a
  meters→studs conversion at the API boundary (a 1.8 m human reads as ~6.4 studs — plausible
  Roblox proportions), and spawning game prefabs via our InsertService-analog keeps authored
  size. (3) The meter-authored character controller stays metric; the Humanoid adapter converts
  numbers (`WalkSpeed` studs/s → m/s, `JumpPower`, …); part mass/density scales by volume
  (×0.28³). That adapter is now a supported extension point rather than a plan: a host registers an
  `IRbxCharacterMotorProvider` and drives Rbx characters with its own controller, with CoreAI's own
  motor answering for whatever the provider declines — see
  [CHARACTER_MOTOR_BRIDGE.md](CHARACTER_MOTOR_BRIDGE.md). (4) Switching the scale config (0.28 ↔ 1:1)
  must require touching **zero assets** — only the `RbxSpace` constant (tested: §5.1.8).
- **Host integration profile**: embedding CoreAI mods into an **existing meter-scale Unity game** is a
  first-class scenario. A per-project host profile (ScriptableObject: `RbxSpace` scale [default
  0.28], capability defaults, which host services/objects are bound, the per-world `ClientWritePolicy`
  — `RobloxParity` [default] / `Strict`, resolved through the single authority-resolver seam so future
  partial-authority rules are a resolver swap, §MVP6; co-building is a host grant, not an `Open` mode
  (owner decision 3) — plus the per-role human tool-surface config and the Players-may-fly flag from
  the roles/locomotion decisions below) makes integration "drop a config and it works".
  `RbxSpace`'s configurable scale **is** the meter-world adapter — no second conversion layer
  exists or is planned. It is part of MVP10's `CoreAiProfile` (§MVP10): since composability became
  normative (`Docs/ARCHITECTURE_RULES.md` §2.1) presets are a foundation item, not a late one.
- **Roles: Creator / Player (LOCKED — studio == game)**: every connected player has a per-world
  **player role** — **Creator** (the host's default in their own world; grantable to others = the Team
  Create analog) or **Player** (joiner default). The player role Creator is not the built-in agent
  role `Creator` (the `world_command` designer, `Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md`); documents say
  "player role" or "agent role" wherever both could be meant, and in code the player role is its own
  type on `RbxPlayer` (MVP11). The role gates the **human-driven** AI tool
  surface (Hub chat: `manage_mods`, `execute_lua`, save/load world), configurable per world in
  the host integration profile — from fully locked down to full sandbox. **Critical
  separation: game-sanctioned AI creation is NOT role-gated** — a mod calling the reserved
  `AIService` (e.g. ability-crafting gameplay where the AI generates a new ability as part of
  game logic) runs under the **mod's** capability grants/quotas set by the creator and works
  for pure Players; generated content carries the origin tag `ai:<modId>` and the normal
  budgets/quarantine apply. Role is an input to the authority resolver (§MVP6) — the player
  dimension. None of it is built yet: `RbxPlayer` has no `Role` field
  (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxPlayers.cs`); the role, its tool gating
  and the grant UI are MVP11. Regardless of role, the chat/agent interaction itself is always
  non-blocking — play and task the AI in parallel (§MVP11, async agent workflow).
- **Locomotion: Humanoid / Fly (orthogonal to role)**: `Humanoid` = the normal character;
  `Fly` = free-fly, the in-game analog of the Studio camera. A Creator defaults to Fly while
  building; whether Players may fly is a per-world host-profile config (a cheat in most games,
  normal in creative worlds). Not built yet: Fly as a locomotion mode is MVP7, the Fly camera of the
  Studio is MVP12.
- **Event mode (LOCKED)**: **Deferred** signal behavior only, matching Roblox's current direction
  (templates default to `Enum.SignalBehavior.Deferred` [^10]) — details §5.1.4 (D4).
- **Clocks (LOCKED)**: the scheduler (`task.wait`, `task.delay`) and the frame pipeline run on
  **scaled game time** (they respect `Time.timeScale` — pause/slow-mo pauses/slows mods), and the
  real-time surface ships alongside with Roblox names: `os.time()`, `os.clock()`,
  `workspace:GetServerTimeNow()`, `time()` (`DateTime` is a loud backlog stub, M2-15). Full
  clock-mapping table in §5.2.6 (D9).
- **Hot reload (LOCKED)**: reloading a mod restarts its scripts with clean state; mod-created
  Instances are destroyed by default (opt-out `preserveInstances` flag per mod); mod stores
  **always** survive. Full survival matrix in §6.3.
- **Mod manifest (LOCKED)**: `mod.json` carries `api_version` from day one; the loader
  version-gates mods against the host's mod-API version (§MVP18).
- **One JSON contract (LOCKED)**: a single table↔JSON mapping (`HttpService:JSONEncode/JSONDecode`
  parity — arrays vs dictionaries, null handling, number formatting) implemented once and shared
  by DataStore marshalling **and** remote payload serialization (§5.2.4).
- **Skill = docs (LOCKED)**: the in-game LLM's Lua skill and the human-facing API docs are one
  artifact; every rung that grows the API surface updates it as part of its DoD (§4, implicit item
  (b)). Today it is the hand-written "Rbx API" skill; the generated API manifest embedded in it is
  MVP18.
- **Test hierarchy (LOCKED convention)**: the layout and the rule-citing conformance-test naming
  convention in §6.6; every MVP DoD references it.
- **One-shot execution + ownership ledger (LOCKED)**: `execute_lua` is a first-class **runtime**
  feature (conscious deviation from Roblox's Studio-only command bar — DEV-12): it runs in the
  full Roblox API environment, under the same sandbox and budgets, keeps no persistence of its
  own, and returns results. Instances created by a one-shot are **world-owned by default** —
  they persist, are saved with the world, and survive mod reloads (explicit cleanup only) — but
  carry origin tags (`origin: console:<invocationId>`) enabling selective cleanup/undo ("remove
  everything from invocation N"); an optional `execute_lua` **preview** scope is ephemeral
  (auto-cleanup). One unified ownership ledger, two lifecycle policies: **mod-owned**
  (auto-teardown on reload; a `persistent` flag promotes to world-owned) vs **world-owned**
  (console default) — the same `TeardownModEffects` pipeline serves both.
- **World file / place package (LOCKED)**: a shareable zip (`world.json` + `Mods/` +
  `manifest.json` carrying `format_version` + `api_version`; all JSON per the `RobloxJson`
  contract) containing the world-owned instance tree (with owner/origin metadata), world
  settings (gravity, `RbxSpace` scale), and mod sources. Mod-ephemeral state is **not**
  saved — mods restart clean on world load, per the hot-reload contract (§6.3). **One
  serializer** serves disk save **and** the multiplayer join snapshot the host streams to
  connecting clients (the MVP5/MVP6 join flow is designed to reuse it). Save/load work at
  runtime without restart (load = `TeardownModEffects` for all mods → restore tree → start the
  world's mods); AI tools `save_world`/`load_world`. Evolves CoreAI's existing WorldState save.
  MVP1 consequence (explicit): `InstanceRegistry` records are serializable with stable ids from
  day one. Detailed in §MVP3.
- **Two-tier backups (LOCKED)**: **manual saves** — player-owned named slots in `Saves/Manual/`,
  never auto-overwritten; AI tools can create but never overwrite or delete them, and an
  AI-initiated restore requires player confirmation — vs **autosaves** — a rolling ring (~10)
  in `Saves/Auto/<timestamp>-<trigger>.world`, with a snapshot taken **before** every AI
  mutation (mod load/reload, world-mutating `execute_lua`) and trigger metadata recorded. Both
  tiers use the place-package format. Complements the `ILuaScriptVersionStore` source rollback;
  principle: **undo is never the only recovery path**.
- **Quarantine error policy (LOCKED; core implemented 2026-07-22 — replaces auto-unload/auto-disable;
  see `mod-system.md` §5a; `mod:<id>` chunk names still pending per §5.2.7)**:
  at the error threshold a mod **stops dispatching but stays loaded and addressable**; reload
  clears quarantine. Events: `ModQuarantined` and `ModTearingDown(modId, reason)`; the
  `TeardownModEffects(modId, reason)` pipeline clears logic-slot overrides and (future) owned
  instances/coroutines, and is the single teardown entry point shared by hot reload, quarantine
  escalation, and world load. Script chunk names become `mod:<id>` — the §5.2.7 error contract
  depends on it.
- **AI-call reservations (LOCKED — cheap now, from the AIService feasibility audit)**:
  (i) `LuaCapabilities` gains `Ai = 1<<5` (excluded from `All`, like `Full`) when `AIService`
  lands (MVP11), and `Data = 1<<6` for DataStore access (§MVP16); `LuaCapabilities.cs` currently
  ends at `Full = 1<<4`, so both bits are free;
  (ii) MVP2's `ModScheduler` wait system is built on a generic
  "resume a Lua thread when a host `Task`/callback completes" primitive (`ScheduleWaitUntil`),
  with time waits as the special case — DataStore `GetAsync` (MVP16) and the future `agent:Ask`
  both need it (§5.2.2); (iii) conventions from day one: `AiTaskRequest.SourceTag =
  "Mod:<modId>"`, `CancellationScope = "Mod:<modId>"`, hot-reload teardown calls
  `CancelTasks(scope)`; chat-history keying reserved as `(roleId, sessionKey)`.
- **Frame-budget reservation**: `IExecutionBudget` and the scheduler interfaces carry a
  **per-frame / per-mod slice** concept (accounting + skip/downgrade + `BUDGET_EXCEEDED`
  warning) from the first interface draft, even though slice *enforcement* lands later (MVP9)
  — retrofitting accounting seams is expensive; reserving them is free (§5.2.3).

### 2.1 Normative references (behavior rulebooks)

Three normative documents in `Docs/CoreAIMods/RobloxReference/` specify the target Roblox
behavior in numbered, testable rules. **Policy: the implementation follows these rules;
deviations must be recorded as explicit decisions** (table below). The plan cites rule IDs
instead of restating semantics.

| Doc | Scope | Rule IDs |
|---|---|---|
| `01_SCRIPTS_AND_SCHEDULER.md` | script types/contexts, require, task scheduler, frame order, signals, Instance lifecycle | R1.x–R7.x, plus UNCERTAIN items U1–U7 |
| `02_MULTIPLAYER_REPLICATION.md` | replication model, remotes, authority, serialization/limits appendices | M1.x–M7.x |
| `03_SERVICES_AND_DATA.md` | services, DataStore semantics + emulation table | S1.x–S7.x |

Uncertainty policy: each U1–U7 item in the scheduler doc gets an explicit recorded stance during
MVP2 design review (testable stances become conformance tests; a stance later contradicted by
verified Roblox behavior is re-filed as a deviation below).

Conscious deviations (running list — additions require an entry here):

| ID | Roblox behavior (rule) | CoreAI decision | Why |
|---|---|---|---|
| DEV-1 | cyclic `require` hangs forever (R3.7) | raise `CYCLIC_REQUIRE` naming the cycle path | hangs are hostile to AI self-repair; an error is patchable |
| DEV-2 | `SignalBehavior.Immediate` exists | Deferred only; setter is a loud stub | D4; budget enforcement and reentrancy safety |
| DEV-3 | no per-slice instruction budgets | budget kills + quarantine per mod (§2, quarantine policy) | sandbox safety in a live game |
| DEV-4 | fixed stud scale | configurable `RbxSpace` scale (default 0.28 m) | host-game integration |
| DEV-5 | `task.synchronize/desynchronize` switch Parallel Luau contexts; `RBXScriptSignal:ConnectParallel` runs the handler in parallel | no-op + once-per-mod log note; `ConnectParallel` is `Connect` (serial) with the same once-per-mod note | Parallel-annotated scripts are otherwise runnable; throwing would fail working code |
| DEV-6 | global gravity | per-body gravity forces, host `Physics.gravity` untouched | mods coexist with the host game's physics |
| DEV-7 | destroyed instances stay readable (`Parent` nil + locked, R5.8/R6.2) | member access on a destroyed instance raises `INSTANCE_DESTROYED` — **except** inside destruction-queued handlers (`Destroying`/`AncestryChanged`), which read a **tombstone** (`Name`, `ClassName`, `Parent == nil`; connections gone). A `Parent` change notification raised by the destruction itself is tombstone-readable too, and `BasePart` properties read inside those handlers return the part's last-known values (both part sinks keep the most recent 2,048 destroyed parts; older ones are forgotten) | loud errors drive AI self-repair; the tombstone keeps R5.8's observable post-destruction state |
| DEV-9 | legacy `wait`/`spawn`/`delay` have a ~29 ms floor + load-dependent throttling (R4.9) | preserve the **0.029 s minimum delay**, but omit load-dependent throttling | the real floor preserves timing compatibility; deterministic scheduling avoids machine-load-dependent behavior |
| DEV-10 | `GetAsync`/`UpdateAsync` return a `DataStoreKeyInfo` second value (S1.4/S1.7 — S1.4 is `GetAsync`'s `(value, DataStoreKeyInfo)` tuple; S1.7 is `UpdateAsync`'s transform contract) | second return is `nil` in MVP16 — documented reduced fidelity | version/metadata model not emulated locally; loud stubs cover the explicit version APIs |
| DEV-11 | `GetAsync` results are cached for 4 s (S1.5) | cache **not emulated** — every `GetAsync` reads the store | the local store is fast; emulating the cache would only add staleness surprises |
| DEV-12 | command-bar/one-shot execution is Studio-only | `execute_lua` one-shots are a first-class **runtime** feature (full API env, same sandbox/budgets; §2 one-shot decision) | Realtime principle — the game is authored while it runs |
| DEV-13 | a Lua string is a byte string; non-ASCII text built from UTF-8 byte sequences (e.g. `'\195\169'` for "é") is one Unicode codepoint by Luau's own character-counting (`utf8.len`), even though `#s` counts the 2 raw bytes | crossing the C#/Lua boundary maps each byte to one `System.Char` (UTF-16 code unit) — a non-ASCII codepoint spanning 2+ UTF-8 bytes becomes that many C# chars, not one; pinned by `RoundTrip_UnicodeStrings_SurviveEncodeDecodeUnescaped` (`RbxJsonContractEditModeTests`) | the VM boundary marshals bytes, not codepoints; a mod author, or C# code reading a Lua string (logs, `GetAttribute`, `JSONEncode` output), must not assume `String.Length`/indexing on a crossed string matches Luau's `utf8.len`/perceived character count for non-ASCII text |
| DEV-15 | "The name of an instance cannot exceed 100 characters" — the mirror states the cap, not what an over-long assignment does | an over-long `Name` keeps its first 100 characters (never splitting a surrogate pair) | truncation instead of an error keeps a world saved before the cap existed restorable |
| DEV-16 | `Humanoid.MaxHealth = math.huge` stores infinity (a common "invincible" idiom) | accepted, but stored as the largest finite double, so `Health == math.huge` is false (compare with `MaxHealth`); `TakeDamage(math.huge)` still kills; NaN is refused | a non-finite value cannot be saved in a world package; the idiom keeps working without making the world unsavable |
| DEV-14 | members carrying `security: PluginSecurity` are unreachable from a running game — they exist for Studio and plugins, and the shipped experience never sees them (e.g. `ScriptContext:SetTimeout`, which limits how long a script may run without yielding) | the same members are reachable at RUNTIME, gated to the elevated actor tier (`ActorContext.Grants.IsUnrestricted`); an ordinary mod is refused exactly as it is for any other privileged member | CoreAI's Studio is the running game itself (the in-game creator mode, MVP12): authoring and playing are ONE application, so "Studio-only" has no separate place to live. Mapping these to "unavailable" would delete a control the creator legitimately needs while the game runs; mapping them to "anyone" would let a mod raise its own execution budget. The elevated tier is the only honest reading, and it is the same tier that already gates the rest of the privileged surface. Sibling of DEV-12, which made the same call for one-shot execution |

---

## 3. Architecture overview

### 3.1 Layer diagram

```mermaid
flowchart TD
    subgraph Authoring
        A1[AI agent via manage_mods / mod tools]
        A2[Human via CoreAI Hub / editor]
    end
    A1 --> F[Mod files  Mods/&lt;Name&gt;/ + mod.json\nor single .lua with @coreai header]
    A2 --> F
    F --> P[Luau downlevel preprocessor\nRuntime/LuauDownlevel  LANDED  mini-rewriter Q1]
    P --> E[Engine seam\nIScriptEngine / IValueMarshaller / IScriptCoroutine\nRuntime/Scripting  LANDED\nadapter: LuaCs* + ExecutionGuard sandbox]
    E --> R[Roblox API layer  Runtime/RbxApi  LANDED\nInstanceRegistry + DataModel + pure-spec datatypes\nModScheduler + signals + task.*\nServiceCatalog: GetService + loud stubs]
    R --> W[World / binding layer\nRbxSpace converter → InstanceGameObjectBinder → Unity scene\nexisting WorldBindings / world commands]
    R --> N[INetworkBridge\nNullNetworkBridge solo · MirrorNetworkBridge optional package\ntopology: Solo / Host / DedicatedServer / Client]
    L[Lua log service  Runtime/Logging  LANDED\nLuaLogService ring buffers · ILuaLogService\nLuaLogFormatter · GetModLogsLlmTool · LuaLogFileSink] -.cross-cuts.- P
    L -.-> E
    L -.-> R
    L -.-> N
    S[AI Lua skill + API manifest  MVP18\ngenerated from ServiceCatalog/ClassCatalog] -.teaches.-> A1
    R -.generates.-> S
```

Rules of the arrows:

- Mods never touch Unity types directly; the Roblox API layer is pure C# over the engine seam and
  talks to Unity only through the binder (`InstanceGameObjectBinder`) and existing world bindings
  (`Assets/CoreAIMods/Runtime/WorldBindings/`). All spatial values cross through `RbxSpace`
  (§5.1.4, D2) — the single conversion boundary.
- The Roblox API layer never references `Lua.*` types — only `Runtime/Scripting` interfaces. This
  keeps a future VM swap (or a second VM for tests) a one-adapter job.
- Networking calls never leave the Roblox API layer except through `INetworkBridge`. In solo play
  the `NullNetworkBridge` loops server→client calls back locally through the deferred queue, so
  `RemoteEvent` code written for multiplayer runs unchanged.
- Logging is a cross-cutting sink: preprocessor diagnostics, VM errors, `print`/`warn`/`error`,
  budget kills, stub hits, and network-bridge drops all land in the same per-mod ring buffer
  (`LuaLogService`).
- The skill/manifest is **generated from** the same catalogs that implement `GetService` and the
  class registry — documentation cannot drift from code (§MVP18; until then the hand-written skill is
  updated with every rung).

### 3.2 Execution contexts (Roblox-style single+multi)

Three script contexts, declared per script (§MVP4; by folder for folder mods, §MVP18), semantics per
R1.x/R2.x. Today the side is still derived from the actor's grant; MVP4 makes it declared:

| Context | Roblox analogue | Solo | Host mode | Dedicated server | Pure client (incl. WebGL) |
|---|---|---|---|---|---|
| `server` | Script in ServerScriptService | runs (local "server") | runs | runs | never |
| `client` | LocalScript in StarterPlayerScripts | runs (local "client") | runs (host's own client) | never | runs |
| `shared` | ModuleScript in ReplicatedStorage | on require, both sides | both sides | server side | client side |

In solo play the same process hosts both contexts, but they still communicate **only** via
remotes/replication (loopback). This is enforced from MVP4 so mods do not accidentally develop
solo-only coupling that breaks in host mode (MVP5). Note the host-mode column: host = server + one local
client in one process, but that is a *topology*, not an API assumption — a dedicated server runs
the `server` column with no local client at all.

### 3.3 Identity model (the three-spaces problem)

Three identity spaces must reconcile:

1. **Roblox space** — Lua holds `Instance` references; scripts also address by name-path
   (`workspace.SpawnPad`).
2. **Mirror space** — replicated objects are addressed by `netId` (uint) on the wire.
3. **CoreAI space** — existing world commands and world-query tools address objects by
   name strings (`WorldBindings/LuaCsWorldRuntimeBindings.cs`, `WorldQuerySceneWalker`).

`InstanceRegistry` (MVP1) is the single owner. One record per instance:

```
InstanceRecord {
  InstanceId  Id;         // ulong, monotonic per session, never reused; 0 = invalid
  uint        NetId;      // 0 until replication binds it (MVP6); server-assigned
  string      WorldName;  // CoreAI world-command name; null until bound to a world object
  RbxInstance Instance;   // the live Lua-visible proxy
  string      OwnerModId; // teardown owner; null = host/world-owned
  string      OriginTag;  // ownership ledger (§2): "mod:<id>" | "console:<invocationId>" | null (host)
}
```

Invariants (testable from MVP1):

- Lookup by any of the three keys returns the same record (`TryGet` / `TryGetByNetId` /
  `TryGetByWorldName`).
- `Id` is allocated at registration and stable until `Destroy`; it appears in every log line and
  error concerning the instance, so the AI can correlate logs ↔ tree ↔ world queries.
- MVP6 rule reserved now: on a Mirror client, spawn messages carry the server's `InstanceId` so
  client-side registries mirror server ids 1:1 (no translation table in mod code, ever).
- The id space is **partitioned by an authority bit from MVP1** (e.g. the top bit:
  server-assigned vs locally-assigned); only server-space ids ever cross the wire — the
  network marshal/spawn paths (MVP5 on) reject locally-assigned ids.
- Records are **serializable with stable ids from day one** — the world-file serializer (§2,
  world file; §MVP3) round-trips them without a remap table.
- Host/world objects discovered by CoreAI world queries get lazily wrapped: first Lua access
  creates the record with `WorldName` set and `OwnerModId = null` (positions read through the
  `RbxSpace` inverse, §5.1.4 D2 — consistent both ways).

---

## 4. MVP ladder (the ladder of record)

**Decided 2026-09-24 (tech lead; owner may override).** This section is the one ladder of record for
CoreAI — not only the Roblox API, but multiplayer, the framework consolidation and the Studio as well.
`Docs/ROADMAP.md` §4 carries the same ladder as a one-screen release table, `TODO.md` files open work
under the same rung names, and `PLAN.md` says where the work stands today. Any other plan (the
dev-docs MVP2.5 plans included) is history and defers to this section.

Rules of the ladder:

- **Strictly sequential.** Each rung depends only on earlier rungs and ends testable on its own. A rung
  ships as one minor release after its gate (`Docs/ROADMAP.md` §4).
- **Multiplayer first.** After MVP3 closes, MVP4–MVP9 finish multiplayer — script contexts, host mode,
  replication, characters, the dedicated server and scale — before the framework consolidation
  (MVP10), roles (MVP11) and the Studio (MVP12).
- **Every rung states** its goal, what is done so far (with paths), what remains, how it is tested, a
  measurable Definition of Done (DoD), what it reuses, and what belongs in the packages versus the
  flagship product. Paths without a root are relative to `Assets/CoreAIMods/Runtime/`.
- **Implicit DoD items for every rung from MVP4 on:**
  (a) the WebGL acceptance checklist (§6.5) passes;
  (b) if the rung grows the Lua-visible surface, the "Rbx API" skill (`RbxApi.txt` and
  `BuiltInRbxApiSkillText.cs`, byte-identical) and `Assets/CoreAI/Docs/RBX_API.md` change in the same
  commit — and from MVP18 on the generated API manifest is regenerated and CI-diffed too;
  (c) composability (`Docs/ARCHITECTURE_RULES.md` §2.1): every block the rung adds can be configured,
  replaced or left out without editing package code, a block that claims to be replaceable has a
  replace-with-fake test, and a preset the rung introduces has a boot test.
- **Product split (plan decision D7).** The flagship Studio+Play Roblox-like app lives in its own Unity
  project and consumes the packages through UPM. This repository keeps the packages, presets, samples,
  bot clients and test harnesses. Each rung says which side each part lands on.

Effort: **S** ≈ ≤2 agent-days · **M** ≈ ≤1 agent-week · **L** ≈ multi-week, parallelizable · **XL** ≈
several L-sized milestones; an XL rung is split into milestones when it starts.

Test methods: **EM** Unity EditMode · **PM** PlayMode `FastNoLlm` · **PT** portable engine-free suite
(`tools/portable/Tests`) · **PL** portable Lua tier (`tools/portable/LuaTests`) · **MP** two or more OS
processes (Standalone builds, or Multiplayer Play Mode virtual players — `com.unity.multiplayer.playmode`
2.0.2 is installed) · **LT** load test with bot clients · **MS** a manual scene in a sample or the
flagship, with recorded evidence.

### 4.1 Numbering: old → new

The ladder was renumbered on 2026-09-24. Landed rungs keep their names; the new rungs are numbered in
execution order.

| Old | Old title | Now | Where its content went |
|---|---|---|---|
| MVP0 | Engine abstraction seam | **MVP0** (landed) | unchanged |
| MVP1 | Instance/DataModel core | **MVP1** (landed) | unchanged |
| MVP2 | Scheduler, signals, clocks, services | **MVP2** (surface landed; two items open) | unchanged |
| MVP2.5 | "MVP3 + MVP8 + MVP11 + MVP12" and the 7.3.0 persistence release | **MVP2.5** (history) | its parts are MVP3, Gameplay services I, MVP5 and MVP6 |
| MVP3 | World file + two-tier backups | **MVP3** (closing) | + spikes S1/S2 |
| MVP4 | RBXL import/export | **MVP14** | widened to rbxl/rbxlx/rbxm/rbxmx both ways + the round-trip parity gate |
| MVP5 | Mod system UX | **MVP4** + **MVP16** + **MVP18** | contexts, `require`, script instances and `CONTEXT_VIOLATION` → MVP4; `game:BindToClose` → MVP16; folder mods, `api_version`, enable/disable, hot-reload latency and the AI tools → MVP18 |
| MVP6 | AI Lua skill = the documentation | **MVP18** | generated manifest + CI diff; until then every rung updates the hand-written skill |
| MVP7 | Editor tooling | **dropped** | highlighting shipped; the runtime Studio (MVP12) replaces the rest |
| MVP8 | Gameplay services I | **Gameplay services I** (landed slice, name kept) | Fly → MVP7/MVP12; `Role` on `Player` → MVP11 |
| MVP9 | DataStoreService | **MVP16** | + leaderstats |
| MVP10 | Input services | **MVP7** + **MVP15** | `Humanoid:Move`, default controls and ContextActionService-lite → MVP7; touch buttons and the rest → MVP15 |
| MVP11 | Mirror bridge core (host mode) | **MVP5** | + join snapshot, inbound rate limit, version handshake |
| MVP12 | Replication | **MVP6** | + binary delta codec |
| MVP13 | Dedicated server | **MVP8** | + WebGL client, dedicated-server preset |
| MVP14 | GUI subset | **MVP15** | + remaining input |
| MVP15 | Audio / FX / animation | **MVP17** | unchanged scope |
| MVP16 | In-game console + AI self-repair | **MVP18** + **MVP10** + **MVP11** | console and runtime self-repair → MVP18; host integration profile → MVP10; task queue and HUD → MVP11 |
| MVP17 | Performance + WebGL hardening | **MVP9** + **MVP19** | slice enforcement and the scale work → MVP9; the rest → MVP19 |
| — | (new) | **MVP7, MVP9, MVP10, MVP11, MVP12, MVP13** | networked characters, scale, composable framework, roles, Studio, model packages and templates |

Old numbers stay where history lives: test class prefixes (`Mvp1*`, `Mvp2*`, `Mvp3*`, `Mvp8*`), the
dev-docs files (`MVP2_*`, `MVP8_ACCEPTANCE_MANIFEST.md`, `MVP25_*`), the CHANGELOG entries, and — until
they are retagged (`TODO.md`) — the phase names in the loud stubs of `RbxApi/Instances/ServiceCatalog.cs`
and `ClassCatalog.cs` (`MVP9`, `MVP10`, `MVP11`, `MVP14`, `MVP15`), the `MVP5` phase of
`game:BindToClose` in `RbxDataModel.cs`, and the "Rbx API" skill that quotes them. Read them through
this table: a stub that names "MVP9" means DataStore, which is MVP16 now.

### 4.2 The ladder at a glance

| # | Rung | Status (2026-09-24) | Size | Test | Old |
|---|---|---|---|---|---|
| MVP0 | Engine abstraction seam | landed 2026-07-22 | M | EM | MVP0 |
| MVP1 | Instance/DataModel core | landed in 6.3.0 | L | EM, PT | MVP1 |
| MVP2 | Scheduler, signals, clocks, services | surface landed; G10 and `BindToRenderStep` open | L | EM, PL | MVP2 |
| MVP2.5 | Online foundation + persistence release | landed 7.3.0–7.43.0 (history) | — | EM | MVP2.5 |
| — | Gameplay services I | landed slice | L | EM, PM, PL | MVP8 |
| MVP3 | World/place package + two-tier backups (+ spikes S1/S2) | **closing**: code complete, Unity gate pending | S | EM, PM, manual WebGL | MVP3 |
| MVP4 | Script contexts & client runtime | **next** | M | PL, EM, PT | MVP5 (part) |
| MVP5 | Host mode over a real socket + join snapshot | planned | L | EM, MP, MS | MVP11 |
| MVP6 | World-state replication + write authority | planned | L | PT, EM, MP | MVP12 |
| MVP7 | Networked characters & controls | planned | L | PM, MP, EM | MVP10 (part) + new |
| MVP8 | Dedicated server + WebGL client | planned | M–L | MP, LT | MVP13 |
| MVP9 | Scale to ~100 players per room | planned | XL | LT, PT, EM | MVP17 (part) + new |
| MVP10 | Composable framework: one root, presets, host profile | planned | M | EM, PM, MP | MVP16 (part) + new |
| MVP11 | Roles (Creator/Player) & per-player AI over the network | planned | M | EM, MP | new |
| MVP12 | Studio mode core | planned | XL | PM, EM, MP, MS | new (replaces MVP7) |
| MVP13 | Model packages, template library, Luau parity prerequisites | planned | M–L | PT, PL, EM, PM | new |
| MVP14 | Roblox interchange + round-trip parity gate | planned | L | PT, PL, EM, MS | MVP4 |
| MVP15 | GUI + remaining input | planned | L | PM, PL, MP | MVP14 + MVP10 (rest) |
| MVP16 | DataStore + leaderstats | planned | M | EM, PL, LT | MVP9 |
| MVP17 | Audio / FX / animation | planned | M | PM, MP | MVP15 |
| MVP18 | Mod UX rest, skill manifest, console + self-repair | planned | M–L | EM, PL, PM | MVP5 (rest) + MVP6 + MVP16 |
| MVP19 | Performance, WebGL and mobile hardening | planned | M | LT, manual WebGL/Android | MVP17 (rest) |

### 4.3 Spikes, scale targets and risks

Three spikes turn the largest unknowns into measured numbers before the rungs that depend on them:

| Spike | Where | What is measured | What it decides |
|---|---|---|---|
| **S1** VM cost | MVP3 close | Guarded VM throughput and per-resume cost in an IL2CPP **Linux server** build and a Standalone Mono x64 build, on the `tools/vmbench` workload and the `tools/ScaleHarness` N=100 workload | Whether the cheaper guard (plan decision D8 (a)) is enough for MVP9, or the server-only native Luau fallback (plan decision D8 (b)) is needed |
| **S2** bytes per update | MVP3 close | Bytes per CFrame patch through `Scripting/LuaCs/LuaCsRbxNetworkCodec.cs` (JSON) vs a hand-packed binary record | The bytes-per-patch bar of MVP6's binary delta codec |
| **S3** big snapshot | MVP5 | A 5 MB join snapshot over real kcp | The snapshot chunk size and the reliable-message path |

S1 and S2 are recorded measurements (no pass/fail); their numbers go into `TODO.md`.

**Scale targets (plan decision D5).** The flagship's room size is the gated product goal, frozen before
anything is measured and published only when the MVP9 staircase passes on the reference machine:

| Target | Value |
|---|---|
| Players per room | 100 |
| Server tick | 30 Hz; p99 frame ≤ 33 ms, of which server Lua ≤ 8 ms |
| Downstream per client | average ≤ 50 KB/s, p99 ≤ 100 KB/s |
| Upstream per client | ≤ 10 KB/s |
| Join | ≤ 10 s on LAN |
| Place | 10,000 instances; 30 min run, 0 disconnects, retained heap delta ≤ 100 MB |
| Reference machine | 8-vCPU Linux server |

Today's instance ceilings are 16,384 per desktop world and 2,048 per actor
(`LuaExecution/LuaCsModRuntime.cs`), 4,032 per WebGL world, and 100,000 in the package format
(`Docs/CoreAIMods/WORLD_PACKAGE.md`); MVP9 raises the desktop ceilings with measurement.

The ranked risks (R1–R13) and their mitigations are in `Docs/ROADMAP.md` §6; each mitigation is placed
in a rung below.

### 4.4 Why the order changed on 2026-09-24

- **Multiplayer moved ahead of breadth.** The owner put multiplayer completion first; its skeleton
  (the Mirror bridge, the replication core, the authority model) already existed, so finishing it is
  the shortest path to a playable online product.
- **Script contexts were pulled forward (MVP4).** They are the one hard dependency of host mode: the
  server cannot tell client code from server code while the side is derived from grants
  (`Scripting/LuaCs/LuaCsRbxInstanceBindings.cs`), so the join snapshot would leak server sources.
- **Networked characters became their own rung (MVP7).** `Humanoid:Move` and default controls are
  needed for a playable networked character, so they left the old input rung.
- **RBXL interchange moved after model packages (MVP13 → MVP14).** Round-tripping Roblox content needs
  script instances, `require`, `RunContext`, the Luau stdlib extensions and an export lint first; a
  file format built before them would carry scripts it cannot represent.
- **The Studio became a rung (MVP12)** and replaced the editor-tooling rung, because the flagship needs
  an in-game creator mode and the RUNTIME-first rule puts it in the player, not the Unity editor.
- **The framework consolidation became a rung (MVP10)** once composability became normative
  (`Docs/ARCHITECTURE_RULES.md` §2.1); it lands before roles and the Studio, whose gating is profile
  data.

History — the ordering rationale of the first ladder (2026-07), kept as the record; the numbers are the
old ones, and 4.1 maps them:

- **Networking loopback stubs moved from "MVP1" to MVP2.** `RemoteEvent`/`RemoteFunction` are
  signal-and-yield objects; they cannot exist before `RBXScriptSignal`, the scheduler, and
  `INetworkBridge` (all MVP2). Shipping them earlier would mean stubbing the stubs.
- **World file (MVP3) and RBXL import/export (MVP4) land immediately after MVP2 (user
  decision).** Opening real Roblox maps is a headline capability that exercises the MVP1/MVP2
  surface broadly; the native place package comes first because import produces `world.json` —
  and keeping it a small dedicated rung isolates the RBXL converter from serializer churn.
  (Superseded: RBXL is MVP14 now, above.)
- **Mod-system UX and log wiring moved after the API core (MVP5).** Hot-reload teardown rules are
  meaningless before instance ownership exists (MVP1: `OwnerModId`) and connection lifetimes exist
  (MVP2). The preprocessor is a parallel track that merges here; the log core has already landed.
- **The AI skill is its own early MVP (MVP6), right after the mod-system MVP.** It must exist
  *before* the service breadth arrives, because every later MVP appends to it; and it needs MVP2's
  catalogs (the manifest source) plus MVP5's authoring workflow (which it teaches). Motivation is
  a real incident, not theory: a small 4B model hand-animated a 2-second movement with raw
  `execute_lua` loops instead of using `TweenService` — wrong-tool-choice errors are prevented by
  the skill, not by the API. (Since then the hand-written skill has grown with every rung; the
  generated manifest is MVP18.)
- **Input (MVP10) split from gameplay services (MVP8).** UserInputService interacts with CoreAI's
  cursor gating and the Hub's focus model — an isolatable risk that should not block Players/
  Humanoid work.
- **Host mode before dedicated server (MVP11 → MVP13).** Locked topology order: host mode is the
  fastest dev loop (one desktop process, mirrors Roblox Studio play-testing); the dedicated
  server is mostly headless bootstrap + CLI on top of an already-working bridge, so it lands as
  its own step after replication. (Unchanged: MVP5 → MVP8 now.)
- **The seed's "MVP4 tooling" is split**: editor tooling (MVP7) vs. in-game console + self-repair
  (MVP16), because the latter depends on the Mirror-era log routing and the mature tool surface.
  (Editor tooling is dropped now; the console is MVP18.)

### MVP0 — Engine abstraction seam *(landed 2026-07-22)*

- **Done**: no code outside the adapter references `Lua.*`; the Roblox layer builds on neutral
  interfaces only. The contract lives in `Scripting/`: `IScriptEngine`, `IScriptState`,
  `IValueMarshaller`, `IScriptFunctionRegistry`, `IScriptCoroutine` (with `Kill()`),
  `IScriptExecutionGuard`, `IExecutionBudget`, `ScriptValueKind`, `IScriptTable`, `ScriptCallContext`,
  `ScriptCallResult`, `ScriptSandboxProfile`, `ScriptRuntimeException`; the `LuaCs*` classes form the
  single adapter in `Scripting/LuaCs/` (`LuaCsScriptEngine`, `LuaCsScriptState`,
  `LuaCsValueMarshaller`, `LuaCsScriptCoroutine`, …); the `LuaCsApiRegistry` leak is closed;
  `LuaCsCoroutineHandle`/`Runner` are exposed through `IScriptCoroutine`.
- **DoD (met)**: `CoreAI.Mods.csproj` compiles with the seam in place; the EditMode tests are green; a
  grep for `using Lua;` outside the `Scripting/LuaCs/` and `Infrastructure/` adapters returns nothing
  (`ScriptingSeamHonestyEditModeTests`).

### MVP1 — Instance/DataModel core (detail: §5.1) *(landed in 6.3.0)*

- **Done**: the engine-free datatypes, the `InstanceRegistry`/`RbxDataModel` core, the `RbxSpace`
  conversion boundary, the `IInstanceBackingBinder`/`InstanceGameObjectBinder` materialization slice,
  and the Lua surface (`Instance.new`/`game`/`workspace`, datatype globals, Part spatial properties
  pushed through `IPartPropertySink`) are in `RbxApi/` and `Scripting/LuaCs/LuaCsRbx*.cs`, wired by
  `Composition/CoreAiModsInstaller.cs`. Shipped in 6.3.0 with the §5.1.8 acceptance gate green
  (build/query/clone/destroy, the `RbxSpace` round trip, golden fixtures, the conversion lint),
  `Part.Shape` (Ball/Cylinder/Wedge meshes; CornerWedge has its own mesh now) and two slices pulled
  forward so mini-games are playable on this base: a Roblox-1:1 `UserInputService` (behind the
  swappable `IInputSource` seam) and `workspace.CurrentCamera` (over a swappable camera rig).
- **DoD (met)**: the §5.1.8 list is green, including the `RbxSpace` round-trip property tests, the
  golden fixtures and the conversion lint; a mod builds, queries, clones and destroys an instance tree
  that materializes as GameObjects and is visible to CoreAI world queries. The audit fix waves of
  2026-09-24 (M1-xx, A3-xx; `TODO.md`) are unreleased.

### MVP2 — Scheduler, signals, clocks, services framework (detail: §5.2) *(surface landed; two items open)*

- **Done**: `ModScheduler`, `task.*` plus the legacy aliases, general deferred signal dispatch,
  yieldable signal handlers, `ServiceCatalog` with member-access loud stubs, the shared JSON contract
  and the `HttpService` JSON members, loopback `RemoteEvent`/`UnreliableRemoteEvent`/`RemoteFunction`,
  absent-child `WaitForChild` yield (with the 5 s infinite-yield warning and the timeout overload), the
  `Model`/`PVInstance` pivot slice, and the complete materials catalog — all 45 `Enum.Material` items
  render (catalog-driven `RbxTextureMaterialProvider`: thirty-six packaged CC0 sets, any item
  overridable from a project-local 2K–4K catalog via the Editor menus; the rest procedural via
  `RbxProceduralMaterialProvider`) with an opaque magenta diagnostic material for an unmapped id, plus
  `MaterialVariant`/`MaterialService` for a game's own textures. The clocks (`time`, `os.time`,
  `os.clock`, `tick`, `workspace:GetServerTimeNow`) run on the injectable `IRbxClockSource` port, so a
  game can redefine time and monotonicity is testable without the machine clock; `os` holds exactly
  `time` and `clock`. The RunService topology queries (`IsServer`/`IsClient`/`IsStudio`/`IsRunning`)
  answer through the swappable `IRbxRuntimeTopology` seam (`RbxApi/Instances/RbxRuntimeTopology.cs`).
  The Tier-A corpus gate is green: `TierACorpusEditModeTests` enforces the 30% floor and the frozen
  catalog clears it roughly threefold. The observability seam the frame gate needs
  (`ILuaCsGuardObserver`) has landed.
- **Open (MVP2 is not closed)**: (1) the **G10 capacity gate** fails on the AI backend, not on CoreAI —
  at the measured 17.4–38.5 s provider p95 on one lane, forty requests cannot be served inside a 60 s
  window; the arithmetic in `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` calls for 12–25 backend lanes, and a
  single provider response already exceeds the 5 s p95. That is a model and hardware decision, and it
  belongs with the LLM-capacity risk R7 (MVP11). (2) `RunService:BindToRenderStep`/
  `UnbindFromRenderStep` are still MVP2-phased loud stubs: implement them on the render phase with
  Roblox's priority order, or re-phase the stubs and amend §5.2.4 (`TODO.md`). Neither blocks MVP4.
- **DoD**: the §5.2.9 list is green, including frame order per R4.2 and deferred dispatch per
  R5.4–R5.7; stances for U1–U7 recorded (§2.1); **corpus gate ≥30%** of Tier A (§6.4) — met.

### MVP2.5 — Online foundation + persistence release *(landed 7.3.0–7.43.0; history)*

MVP2.5 was the name of the bundle "MVP3 + MVP8 + MVP11 + MVP12" in `dev-docs/MVP25_ONLINE_PLAN.md` and
`dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` (both history now). What landed under it:

- **Persistence release 7.3.0**: save/load of the world state, the predecessor of the MVP3 package.
- **Authority model** (in process): `WorldAclAuthorizer` (`RbxApi/Instances/WorldAcl.cs`),
  `WriteGrantLedger`, `IntentGateway`, `MutationIntent` and `ClientWritePolicy {RobloxParity, Strict}`
  (`RbxApi/Instances/Replication/`), `InstanceRegistry.Authority` separating server from replica; shown
  by the `Assets/CoreAI.Demos/OnlineAuthority` demo.
- **Replication phase 0** (7.39.0): member-level change reporting, `ReplicationDirtySet`,
  `ReplicationStream`, `ReplicationApplier` and the guarded filter, tested registry-to-registry
  (`dev-docs/REPLICATION_PHASE0.md`).
- **Mirror bridge core** (7.42.0–7.43.0): `com.neoxider.coreaimirror` with `MirrorNetworkBridge`,
  `CoreAiMirrorAuthenticator`, `CoreAiMirrorSessionHost` and the scene provider
  (`Assets/CoreAIMirror/Runtime/`); remotes over Mirror's real handlers, proven over an in-memory
  transport; `Player:Kick()` closes the connection.

Its unfinished parts are rungs now: the world package is MVP3, the gameplay slice is "Gameplay services
I" below, host mode and the join snapshot are MVP5, world-state replication is MVP6.

### Gameplay services I *(landed slice; the old MVP8)*

- **Done**: the minimum service set that makes classic tutorial scripts (kill bricks, pickups, doors,
  speed pads) run with Roblox game feel at the default scale. `Players` (`LocalPlayer`, nil on a
  dedicated server; `PlayerAdded`/`PlayerRemoving`, `GetPlayers`, `GetPlayerByUserId`,
  `GetPlayerFromCharacter`; solo = one synthetic `Player`), `Player` with `Character`/`CharacterAdded`
  and the `leaderstats` convention over `IntValue`/`StringValue`/`NumberValue`
  (`RbxApi/Instances/Networking/RbxPlayers.cs`, `RbxApi/Instances/RbxValueObjects.cs`); characters built
  by `RbxApi/Instances/Networking/RbxCharacterFactory.cs` and driven by
  `RbxApi/Binding/UnityRbxCharacterMotor.cs`, with `IRbxCharacterMotorProvider` for a host's own
  controller ([CHARACTER_MOTOR_BRIDGE.md](CHARACTER_MOTOR_BRIDGE.md)); `Humanoid` basics
  (`RbxApi/Instances/RbxHumanoid.cs`: `Health`, `MaxHealth`, `WalkSpeed`, `JumpPower`/`JumpHeight`/
  `UseJumpPower`, `MoveDirection`, `TakeDamage`, `MoveTo`/`MoveToFinished`, `Died`, `HealthChanged`);
  `BasePart.Touched`/`TouchEnded` through CoreAI's own `RbxApi/Binding/RbxContactRelay.cs` (not a
  NeoxiderTools dependency); `Debris:AddItem`; `TweenService`, `TweenInfo`, `Tween:Play/Pause/Cancel`
  and `Completed`; `workspace:Raycast`; per-body gravity (DEV-6); `CollectionService` tag queries and
  signals. After the 2026-09-24 audit fix waves (unreleased): TweenService advances in O(1) per step,
  refuses non-finite goals, contains a faulting tween, plays `Reverses` as two legs, never fires
  `Touched` for a tweened move, and charges each tween to the creating actor with at most 256 finished
  tweens kept per actor; `Humanoid:TakeDamage`/`MoveTo`/`ChangeState`, `Debris:AddItem` and the Tween
  calls need `WorldEdit` and write authority; `CanCollide = false` behaves as in Roblox; `Died` fires
  only inside the Workspace; `MoveTo` ends with `false` when a script, a `PivotTo` or a tween moves the
  `HumanoidRootPart`; a character `Model` is `Archivable = false`; `ClickDetector.MouseClick` passes the
  clicking player; `Player:Kick(message)` carries its text to the client. Per-item evidence:
  `dev-docs/MVP8_ACCEPTANCE_MANIFEST.md`.
- **Moved out**: the `Role` field on `Player` (never built) → MVP11; Fly locomotion (never built) →
  MVP7 (Fly as a locomotion mode) and MVP12 (Fly camera for Creators); `Humanoid:Move` → MVP7.
- **Open**: the 1:1-scale smoke half of gate P8.2 is not covered (no PlayMode test runs
  `RbxSpace.Configure(1)`), and the character motor has known limits (`TODO.md`, "Left on MVP2.5").
- **DoD**: kill-brick, touch-pickup-with-leaderstats and door-tween corpus fixtures pass at 0.28 scale
  (primary) and 1:1 (smoke — the open half above); a dropped part falls with Roblox-feel acceleration
  under per-body gravity while the host scene keeps Earth gravity; **corpus gate ≥60%** of Tier A+B
  (enforced by `TierACorpusEditModeTests` over the frozen Tier-B catalog); the skill's TweenService
  section and its wrong→right pair are verified.

### MVP3 — World/place package + two-tier backups + spikes S1/S2 *(closing: code complete 2026-09-24, Unity gate pending)* (S)

- **Goal**: the world is a savable, shareable artifact — the single serializer that disk save, backups
  and the multiplayer join snapshot share (§2, world file / backups) — released, with the two unknowns
  that most affect scale measured.
- **Done so far**: the deliverables below are built and covered by EditMode tests; the contract, its
  validation limits and the WebGL execution budget are in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md). The
  `.world` ZIP container, `FileRbxWorldPackageStore` (create-once manual slots, capped at 64 / 256 MiB
  per store, and the two-phase durable autosave ring), `ConfirmedWorldMutationGate` in front of every
  `execute_lua` and every mutating `manage_mods` action (`Infrastructure/LuaCsGameToolExecutor.cs`),
  `RbxWorldRuntimeSessionController` transactional session replacement
  (`Infrastructure/RbxWorldPackageContracts.cs`), the `save_world`/`load_world`/`list_autosaves`/
  `load_autosave` tools with the confirm-before-restore flow and JSON failure statuses, and the
  built-player **Hub → World Loads** page. The W3.5 tail: a player-confirmed load is recorded as a
  durable startup selection (`Saves/Startup`) and restored at boot through the same staged swap, with
  the default world on any failure and a Hub reset button. Also closed: restore as one host-enveloped
  operation, the ACL floor, restored trees charging the instance quota, a refusal of world loads while
  network sessions are live (the entry guard of MVP5), at most 256 distinct mod sources per world, a
  confirmed load under the shared gate, and — since audit round 2 — mods restarting and restoring in
  their load order (`LuaModManifest.LoadOrder`, A1-02). Portable suites on Linux at `f817225b`:
  engine-free 2112 passed / 0 failed / 3 skipped; Lua tier 1606 passed / 0 failed (engine-bound cases
  Inconclusive by design).
- **To do**:
  - Finish audit rounds 2 and 3 over the whole wave and their fixes (`TODO.md`).
  - Run the Unity checklist in `TODO.md` ("Check the tests"): EditMode in the four module legs plus
    `MIRROR`, PlayMode `FastNoLlm`, and the fixtures that have never run.
  - Run the real WebGL page-reload gate (save → reload the page → the bytes and the startup selection
    survive).
  - Bump and tag (`python tools/bump_version.py <version>`).
  - **S1** and **S2** (§4.3); record the numbers in `TODO.md`.
  - The MVP3 tail items in `TODO.md` ("Persistence (MVP3 tail)") are follow-ups; they do not gate the
    release.
- **Test**: EM, PM, manual WebGL; S1 and S2 as recorded measurements.
- **DoD**: EditMode 0 failed and PlayMode `FastNoLlm` 0 failed on Unity 6000.3.14f1; the WebGL reload
  survives; the release tag exists; the S1 and S2 numbers are in `TODO.md`. The original DoD — save →
  load round-trips the world-owned tree with stable ids (golden comparison); mods restart clean on
  load; a manual slot is provably untouchable by AI tools (negative test); the autosave ring rotates and
  records triggers; a save is durable on WebGL (since Unity 6.3 the engine's automatic
  `persistentDataPath` persistence, reported by `CoreAiWebGlPersistence.SyncAsync`) — is proven item by
  item:
  (a) golden round trip — `WritePackage_AuthoredWorld_MatchesLiteralGoldenJson`,
  `ReadPackage_HandWrittenLiteralPackage_RestoresLiteralIdsParentsRevisionsAndPartState`;
  (b) clean restart — `ConfirmedPackageLoad_SwapsEveryFacadeAndRestartsOnlyActiveModsOnce` (active mods
  start once; the outgoing scheduler has `LiveThreadCount == 0`);
  (c) manual slots untouchable, restore only after player confirmation —
  `SaveWorldTool_SecondSaveToSameSlot_IsRefusedAsResultAndKeepsFirstBytes`,
  `WorldPersistenceSurface_ExposesNoDeleteOverwriteRemoveOrReplacePath`,
  `ProgrammerRole_WorldTools_AreExactlySaveLoadListAndLoadAutosave`, and the positive confirm in
  `StartupSelection_ConfirmedManualLoad_RestartRestoresSameTreeAndExactSources`;
  (d) ring and triggers — `FileStore_DefaultAutosaveCapacity_IsTenAndRotatesOnlyTheOldest`,
  `ListAutoSaves_HyphenatedTriggers_RoundTripExactly`, `ListAutoSavesTool_ReturnsExactNameTriggerTimestampAndSize`,
  `ConfirmedBackup_GatedExecuteLua_WritesExactlyOneExecuteLuaAutosaveToFileStore`;
  (e) persistence after save — `FileStores_WithoutInjectedHook_DefaultToCoreAiWebGlPersistenceSyncAsync`
  (the real-browser reload is the open gate above);
  (f) invalid names as JSON results — `SaveWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`,
  `LoadWorld_InvalidSlot_IsRefusedAsResult_WithoutCallingService`,
  `LoadAutoSave_InvalidName_IsRefusedAsResult_WithoutCallingService`.
  Rung-zero envelope, ACL floor, startup selection and load order: the full list is in
  [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md#acceptance-status-mvp3).
- **Deliverables (as designed)**:
  1. Place-package format: zip of `world.json` + `Mods/` + `manifest.json` (`format_version`,
     `api_version`), all JSON via `RobloxJson`; the world-owned instance tree (with owner/origin
     metadata from the ownership ledger), world settings (gravity, `RbxSpace` scale) and mod sources.
     Mod-ephemeral state is **not** saved — mods restart clean on world load (§6.3 contract).
  2. **One serializer** for disk save and the join snapshot of MVP5/MVP6 — the join flow is designed
     against this component from the start.
  3. Runtime save/load without restart: load = `TeardownModEffects` for all mods → restore tree →
     start the world's mods.
  4. AI tools `save_world`/`load_world` (§6.2); evolves CoreAI's existing WorldState save.
  5. Two-tier backups (§2): `Saves/Manual/` named slots (AI tools can create but never
     overwrite/delete; an AI-initiated restore requires player confirmation) + a rolling
     `Saves/Auto/<timestamp>-<trigger>.world` ring (~10), a snapshot taken before each AI mutation (mod
     load/reload, world-mutating `execute_lua`) with trigger metadata.
- **Reuses**: MVP1 (registry with stable, serializable ids), MVP2 (`RobloxJson`).
- **Package/flagship**: packages only.

### MVP4 — Script contexts & client runtime *(next)* (M)

- **Goal**: every script declares `server | client | shared`; the process topology, not the actor's
  grant, decides which side runs it; a client process runs only client and shared code against a
  replica registry. This is the one hard dependency of multiplayer: without declared contexts the join
  snapshot would ship server sources to clients, and a host-mode server script written by a restricted
  actor would run as "client".
- **Done so far**:
  - `RbxNetworkTopology {Solo, Host, DedicatedServer, Client}` (`RbxApi/Instances/Networking/INetworkBridge.cs`)
    and the `IRbxRuntimeTopology` seam (`RbxApi/Instances/RbxRuntimeTopology.cs`).
  - `IsNetworkServer`/`RequireNetworkSide` (`Scripting/LuaCs/LuaCsRbxInstanceBindings.cs`) — derived
    today from the topology and the actor's grant (`Topology != Client && IsHost`).
  - `InstanceRegistry.Authority` with `RegistryAuthority.Replica` (`RbxApi/Instances/InstanceRegistry.cs`).
  - Non-creatable `Script`/`LocalScript` descriptors (`RbxApi/Instances/ClassCatalog.cs`) and one
    synthetic `script` per mod under ServerScriptService (`Scripting/LuaCs/LuaCsRbxApiBindings.cs`).
  - `LuaModManifest.LoadOrder` (`LuaExecution/LuaModManifest.cs`) and the Luau downlevel at every load
    (`LuaExecution/LuauSourceGate.cs`, line numbers preserved).
- **To do**:
  - **Script instances (plan decision D1 (c))**: `Script`, `LocalScript` and `ModuleScript` become real,
    creatable instances whose `Source` is a view over the mod source store (versioned stores, revert,
    quarantine and the world package's `Mods/` stay the only storage); `Enabled` and `RunContext` are
    real members; `script.Parent` is the containing instance. This removes the round-trip blockers
    RT1–RT3 (§MVP14) before any template or importer depends on the script model.
  - A declared context in the `@coreai` header, `LuaModManifest` and the package's
    `Mods/NNNN/manifest.json` (decide whether this needs a `format_version` bump); an old package
    without contexts loads as `shared` or is refused — the rung decides which, with a test.
  - Rewrite `IsNetworkServer` on the declared context plus the topology.
  - `require` for `shared` modules with Roblox caching (R3.2/R3.4: one execution per VM, cached table
    identity) and `CYCLIC_REQUIRE` for a cycle (DEV-1).
  - Enforce the §3.2 matrix: a server-only API from a `client` script raises `CONTEXT_VIOLATION` naming
    the rule (old MVP11 deliverable 5).
  - Stop minting `Player`s on a replica (`TODO.md`, "Left on MVP2.5": `EnsureNetworkActor` never checks
    `registry.Authority`).
  - A client composition that stages a replica world through `IRbxWorldSessionHost`
    (`HeadlessRbxWorldSessionHost` in `Infrastructure/RbxWorldPackageContracts.cs` is the model).
- **Test**: PL (context enforcement, the R3_x `require` conformance tests named per §6.6, e.g.
  `R3_2_RequireExecutesOnce`, `DEV1_CyclicRequireRaisesError`), EM (composition, script instances in
  the tree), PT (manifest and package round trip with contexts).
- **DoD**:
  - A server-only API called from a `client` script raises `CONTEXT_VIOLATION`, and its negative twin
    (the same call from a `server` script) passes.
  - Solo runs all three contexts talking through loopback remotes only.
  - `script.Parent` of a `Script` inside a `Part` is that part, and `script.Parent.Touched` works from it.
  - An old package without contexts loads (or is refused) exactly as decided, pinned by a test.
  - The Tier-A corpus pass rate does not drop; **corpus gate ≥50%** of Tier A (§6.4).
- **Reuses**: the MVP2 scheduler and sandbox, the MVP3 package.
- **Package/flagship**: packages only.

### MVP5 — Host mode over a real socket + join snapshot (L) *(the old MVP11)*

- **Goal**: a desktop host (server plus local player) and remote clients play one world over kcp, and
  a joining client receives the world. Topology order stays Null loopback (solo) → host → dedicated
  (MVP8).
- **Done so far**:
  - `MirrorNetworkBridge : INetworkBridge` (`Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs`) with
    authenticated admission (`CoreAiMirrorAuthenticator`), the session host (`CoreAiMirrorSessionHost`)
    and the scene provider (`CoreAiMirrorNetworkBridgeProvider`): readiness handshake, server clock
    anchors, kick and supersede notices, per-channel payload limits, reliable sends held until
    admission, in-process delivery for server-local actors (`LocalDeliveries`) —
    [Mirror README](../../Assets/CoreAIMirror/README.md), Sessions.
  - The world-session staging bridge (`StagedNetworkBridge`, forwarding the clock since audit round 2)
    and the world-load guard `network_sessions_active`.
  - In-memory Mirror tests (`Assets/CoreAIMirror/Tests/EditMode/OfflineMirror.cs`).
  - The MVP3 serializer — the snapshot source (`Infrastructure/RbxWorldPackageSerializer.cs`,
    `RbxApi/Instances/InstanceTreeSerializer.cs`).
- **To do**:
  - Host mode: serve Mirror's host-mode local connection as the host's own `Player` (today it is refused
    loudly, `CoreAiMirrorSessionHost.HostModeConnectionsRefused`), and rewrite
    `KnownLimitation_HostMode_AServerBridgeDoesNotServeTheLocalClient` as the positive test.
  - A filtered join snapshot over the wire: no ServerStorage/ServerScriptService, no `server` sources
    (MVP4), owner/ACL metadata stripped at capture (MP-20 remainder); chunked; decoded as a stream.
  - **S3**: a 5 MB snapshot over real kcp (§4.3).
  - A roster message; an **inbound rate limit** on decode/dispatch with drop-and-count (Mirror README,
    Known limits); a deferred refusal notice so a refused client learns why; a protocol version
    handshake.
  - Live-session handoff when a world loads at runtime, or keep the `network_sessions_active` refusal
    and document it as the contract.
  - Server→client encode-side filtering (`FireClient(player, ServerStorage.X)` still sends the
    reference; only the decode side filters).
  - Make the client registry a replica once the join snapshot fills it (MP-11, deferred to this rung by
    the audit fix waves because it would break the remote demos before a snapshot exists), and decide
    whether registration admission (the instance quota) skips replica applies.
  - The first MP harness (Multiplayer Play Mode or two Standalone players).
  - **Preset "player-host co-op"** with a boot test (implicit item (c)).
- **Test**: EM (`OfflineMirror` rules), MP (host + 2 clients), MS (sample scene "remote chat + kill
  brick").
- **DoD**:
  - Two processes, host + 2 clients, on localhost kcp.
  - The join snapshot of a 2,000-part world applies in ≤3 s and the client tree equals the server's
    filtered tree (golden by ids).
  - The `RemoteEvent` chat fixture and a `RemoteFunction` round trip with its timeout pass.
  - A custom client flooding 10,000 remotes/s leaves the server's p99 frame < 33 ms, with the drops
    counted.
  - A client of another CoreAI version is refused with a reason.
  - All solo tests pass without `MIRROR`; the player-host preset boots in its test.
- **Reuses**: MVP3 (snapshot = world-file serializer), MVP4 (contexts decide what a client receives).
- **Package/flagship**: packages (bridge, presets, sample scene, MP harness); matchmaking and hosting UX
  are flagship.
- **Design detail carried from the old MVP11** (wire behaviour per the M-rules in
  `02_MULTIPLAYER_REPLICATION.md`, including its serialization/limits appendices):
  1. `MirrorNetworkBridge : INetworkBridge` behind the `MIRROR` define; `NullNetworkBridge` remains the
     no-define/solo path. Topologies of this rung: `Host` + `Client`; the `DedicatedServer` bootstrap is
     MVP8 (the interface already carries it). As built, CoreAI uses Mirror's raw message handlers with
     its own envelopes, not `NetworkIdentity` components, and NeoxiderTools' `Neo.Network` is **not**
     the bridge (owner decision 2, `dev-docs/MVP25_ONLINE_PLAN.md` §7); the `Neo.Network` classes the
     first draft named (`NetworkEventDispatcher`, `NetworkActionRelay`, `NetworkContextActionRelay`) were
     patterns only.
  2. `RemoteEvent` over the wire: `FireServer` → client-to-server message, `FireClient(player, ...)` →
     that player's connection, `FireAllClients` → every admitted connection. `UnreliableRemoteEvent` on
     the unreliable channel with a loud `PAYLOAD_TOO_LARGE` over the limit (Roblox's documented drop
     threshold is 1,000 B, [^6-note]). As built, the limit is per channel: an unreliable payload is
     capped at 1,000 B, or at the transport's datagram when that is smaller; a reliable remote or
     `RemoteFunction` payload at the codec's 64 KiB, or at the transport's reliable message size when
     that is smaller.
  3. `RemoteFunction` request/response over paired messages, with timeout → Lua error, matching the
     "InvokeClient is hazardous" guidance [^7].
  4. Payload marshalling per the M-doc serialization appendix — the wire envelope is the **identical
     MVP2 loopback envelope** (§5.2.4: `RobloxJson` + tagged datatype/`InstanceId` entries; table-key
     stringification — conformance tests like `M3_8_TableKeysStringified` per §6.6); Instances marshal
     as `InstanceId` and resolve through the registry on the far side (nil + warning if unknown).
  5. `Players` becomes real: one `Player` per admitted connection, `PlayerAdded`/`PlayerRemoving` from
     connect/disconnect (built; identity through `Players.IdentitySource`, set by
     `AttachWorld(Func<LuaCsRbxApiBindings>)`).
  6. `workspace:GetServerTimeNow()` stays Unix-epoch seconds (D9 table): the bridge side is
     `INetworkBridge.ServerClockOffsetSeconds` plus `IsServerClockSynchronized`, fed by the server's
     clock anchors (`CoreAiServerClockMessage`); the world side re-bases at the first synchronization and
     slews afterwards (built).
  - Old MVP11 DoD, kept inside the DoD above: host + client playtest — chat-via-RemoteEvent fixture,
    server-authoritative kill brick, `RemoteFunction` round trip with timeout; all solo corpus fixtures
    still pass with `MIRROR` absent.
  - State of the bridge before this rung (7.45.0 plus the unreleased fix waves): one connection per
    actor with the newest winning on reconnect (teardown keyed by connection); one admission attempt per
    connection and an admission deadline (10 s); per-channel payload limits; client handlers that do not
    require Mirror authentication; stale answers, malformed envelopes and orphaned requests dropped and
    counted; kcp2k's negative connection ids handled (MP-25); `AttachWorld` wiring
    `Players.IdentitySource`, and a server world refusing a transport-admitted actor without one
    (`NOT_AUTHORITY`); client-authored `Instance` references resolving only if the sender can see them
    (MP-01); a 64 KiB packet decoded without hundreds of megabytes (MP-02); NaN/±Infinity as bare
    numbers (MP-17); the readiness handshake (MP-09); a server clock from the server's own Unix-time
    anchors (MP-06); kick and supersede notices with `Player:Kick(message)`'s text (MP-22); a per-sender
    budget of 32 live handler threads (MP-10); a disconnected actor's mods unloaded once their running
    code returns (M2-24). After audit rounds 1 and 2: threads a remote-started handler schedules are
    charged to the sender (A4-01), unreliable sends are dropped and reliable ones held until admission
    (A4-04, B1-07), an admission belongs to its connection (B1-08), the server's clock hold reaches
    clients (A4-08, A4-13, B1-10) — also from a world loaded from a package (B1-01) — a host's own
    `DisconnectActor` ends the connection (A4-09), Mirror's host-mode local client is refused loudly
    (A4-10), and unresolved-value reports are throttled (A4-02, A4-12, B1-06). Mixed CoreAI versions
    must not run together (a missing message fails loudly with Mirror's default `exceptionsDisconnect`;
    an added field is not detected).

### MVP6 — World-state replication + write authority (L) *(the old MVP12)*

- **Goal**: server-created and server-changed instances and part properties replicate to clients;
  client writes follow `ClientWritePolicy`, and host grants travel as intents. The identity promise
  (§3.3) is cashed in.
- **Done so far**:
  - Replication phase 0 (`RbxApi/Instances/Replication/`): `ReplicationDirtySet`, `ReplicationStream`
    (ordered Spawn/Patch/Remove per recipient), `ReplicationApplier` (duplicate/gap/violation handling,
    resync in place with `BeginResync(worldSequence)`), `GuardedReplicationFilter` — tested
    registry-to-registry (`dev-docs/REPLICATION_PHASE0.md`).
  - `WriteGrantLedger`, `IntentGateway`, `MutationIntent`, `ClientWritePolicy {RobloxParity, Strict}`;
    the `Assets/CoreAI.Demos/OnlineAuthority` demo (in process).
- **To do**:
  - A per-tick `ReplicationPublisher` with coalescing; it replaces the synchronous `FireAllClients`
    fan-out (`Scripting/LuaCs/LuaCsRbxApiBindings.cs`).
  - A **binary, revision-stamped delta codec below the bridge**, sized against S2.
  - Member-level marks for `BasePart` and camera state and tweens, removing the whole-node limit (today
    part and camera properties do not reach a replica as patches, so their `Changed` never fires there).
  - `SendIntent`/`IntentReceived` on `INetworkBridge`.
  - Strip owner/ACL metadata at capture for a client (if MVP5 did not already).
  - The authority-resolver seam `(instance, member) → WriteVerdict`.
  - Retire `ReplicationDirtySet`'s actor-id overload once every caller holds a `ReplicationStream`.
- **Test**: PT (codec, dirty set, stream), EM (resolver, grants), MP (3 processes).
- **DoD**:
  - A server mod moves 200 parts at 20 Hz; clients match ids, names and CFrames within 1 tick + RTT.
  - A late joiner converges.
  - `RobloxParity`, `Strict` and grant tests pass over the wire (below).
  - The 100-instance churn soak runs 10 min with no violation and within its rate budget.
  - A measured average of ≤24 B per CFrame patch on the wire, unless S2 sets another bar before the
    rung starts.
- **Reuses**: MVP3 (the join snapshot is the world-file serializer), MVP5 (bridge and snapshot).
- **Package/flagship**: packages only.
- **Design detail carried from the old MVP12**: instance trees and properties replicate; semantics per
  the M-rules (authority, ownership, replication order). Server-side `Instance.new` under
  `workspace`/`ReplicatedStorage` replicates to clients; property sync for the whitelisted mutable set
  (`Name`, transform/CFrame, `Color`, `Transparency`, `Anchored`, attributes), dirty-flagged and
  batched per tick; the `instanceId ↔ netId` binding in `InstanceRegistry` (server assigns, clients
  mirror). Client-write behaviour is a configurable per-world **`ClientWritePolicy`**:
  **`RobloxParity` (default)** — a client mod writing to a server-owned replicated instance succeeds
  **locally**, never replicates, and the next server sync overwrites it (M2.6/M1.5; local-only VFX are a
  feature, not an error) — default because the Roblox corpus and the AI's priors assume it;
  **`Strict`** — such writes are rejected with `NOT_AUTHORITY` (+ the "use a RemoteEvent" hint), for
  competitive games. There is **no `Open` policy** (owner decision 3,
  `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` §D): creative/co-build worlds use a **host grant**
  (`WriteGrantLedger`) — under either policy, a client write covered by a grant for `(instance, action)`
  is not applied locally but sent as a `MutationIntent` that the server checks, applies and replicates;
  nothing a client sends selects authority. Under every policy `NOT_AUTHORITY` also fires on an explicit
  replication attempt (a client calling a server-only bridge operation). The policy is resolved through
  a single **authority resolver** `(instance, property/action) → WriteVerdict { ApplyLocalOnly |
  Replicate | Reject }`, so future partial-authority rules (per instance / property / player — "clients
  may move furniture but not delete walls") are a new resolver, not a replication rewrite; they are a
  backlog item. Per-connection rate limits on remotes and property writes; late-joiner snapshot replay
  **reuses the MVP3 serializer**, never a second tree-serialization path. Old DoD, kept: the server mod
  builds a tree and the client sees identical ids/names/positions; under `RobloxParity` a client write
  to a server-owned instance stays local and is overwritten by the next sync (M2.6) while an explicit
  replication attempt raises `NOT_AUTHORITY` and is logged; a test for `Strict`, one for a host grant
  (the write travels as an intent, is applied by the server and replicates) and one asserting the
  policy enum has exactly two values; conformance tests cite M-rule ids (§6.6).

### MVP7 — Networked characters & controls (L)

- **Goal**: each player walks, jumps and sees the others move smoothly — the base of any Roblox-like
  game.
- **Done so far**: `RbxCharacterFactory`, `RbxHumanoid` (`MoveTo`, `Jump`, `WalkSpeed`, `Died`),
  `UnityRbxCharacterMotor` and `IRbxCharacterMotorProvider` (`RbxApi/Binding/`); `UserInputService`
  over `UnityNewInputSource`; `workspace.CurrentCamera`, `CameraSubject` and the position-only
  `RbxCameraFollower`.
- **To do**:
  - `Humanoid:Move` (a loud stub phased `MVP10` in `ClassCatalog.cs`).
  - A default control script and camera (a ControlModule/PlayerModule-lite, as Lua in a `client`
    context).
  - ContextActionService-lite (below).
  - Character replication at tick rate with interpolation.
  - Own-character authority per **plan decision D3 (b)**: the client owns its own character, the server
    validates it (speed and teleport limits); every other part stays server-owned.
  - `Player:GetNetworkPing`.
  - Fly as a locomotion mode (Creators use it in MVP12).
- **Test**: PM (solo WASD moves the character, at 0.28 and at 1:1 — this also closes the uncovered
  1:1 half of gate P8.2), MP (3 players), EM (validation rules with negative twins).
- **DoD**:
  - Solo input-to-motion ≤1 frame.
  - Networked: remote characters render with ≤100 ms added latency on LAN and no visible teleport
    jitter (position error p95 ≤0.5 stud).
  - The server corrects a speed hack (a sustained move above 1.5× `WalkSpeed`), with a negative twin.
  - **Corpus gate ≥70%** of Tier A+B (§6.4), with the sprint-on-shift and click-to-place fixtures
    passing with the Hub open and closed.
- **Reuses**: Gameplay services I (characters, Humanoid), MVP4 (client context), MVP6 (replication).
- **Package/flagship**: packages (control scripts, validation, interpolation); character art and feel
  tuning are flagship.
- **Design detail carried from the old MVP10 (input services)**: mods read input the Roblox way
  without fighting CoreAI's own input/cursor model. `UserInputService` already landed in MVP1
  (`InputBegan`/`InputChanged`/`InputEnded(input, gameProcessedEvent)`, `IsKeyDown`,
  `GetMouseLocation`, `MouseBehavior`, `InputObject`, `Enum.KeyCode`/`UserInputType`/
  `UserInputState`); `TouchTap` and touch buttons go with MVP15. `ContextActionService`:
  `BindAction(actionName, functionToBind, createTouchButton, ...inputTypes)`, `BindActionAtPriority`,
  `UnbindAction`, `SetTitle`, `SetImage`; the handler receives `(actionName, inputState, inputObject)`
  and may return `Enum.ContextActionResult.Pass/Sink` [^13]. **Cursor-gating aware**: when the CoreAI
  Hub or chat has focus, `gameProcessedEvent = true` (Roblox's meaning: the engine consumed it) — mods
  keep receiving events and can filter, exactly as Roblox tutorials teach. Input services exist only
  in contexts that have a client (never on a dedicated server), bridged to the Unity Input System
  through one adapter so the host game's input stack stays authoritative. Old DoD: sprint-on-shift and
  click-to-place fixtures pass with the Hub open and closed (above); a touch button appears on a touch
  device (MVP15).

### MVP8 — Dedicated server + WebGL client (M–L) *(the old MVP13)*

- **Goal**: the same bridge and mod stack runs headless on Linux; desktop and WebGL clients join.
- **Done so far**: `HeadlessRbxWorldSessionHost` (`Infrastructure/RbxWorldPackageContracts.cs`); the
  `RendersFrames` topology flag and `Players.LocalPlayer = nil` on a dedicated server;
  `Logging/LuaLogFileSink.cs`; `com.unity.dedicated-server` 2.0.2 installed (`Packages/manifest.json`).
- **To do**:
  - A Dedicated Server build-target bootstrap and CLI (port, world/place, mod set, max players,
    admission provider).
  - Headless log routing plus remote log fetch for the AI.
  - A WebSocket transport, multiplexed with kcp, for WebGL clients (plan decision D4: Mirror/kcp +
    WebSocket behind `INetworkBridge`).
  - Enforce `Players.MaxPlayers` at admission (today a property that is never checked,
    `RbxApi/Instances/Networking/RbxPlayers.cs`).
  - **Preset "dedicated server"** with a headless boot test.
- **Test**: MP (Linux server + desktop + WebGL), LT-lite (10 bots, 1 h — the bot client starts here and
  grows in MVP9).
- **DoD**:
  - The Linux binary hosts 2 desktop clients and 1 WebGL client through the MVP5–MVP7 fixtures.
  - A 1 h soak with 10 bots: retained heap delta ≤50 MB and 0 disconnects.
  - Solo and host modes are unaffected; the dedicated-server preset boots headless in its test.
- **Reuses**: MVP5–MVP7.
- **Package/flagship**: packages (bootstrap, CLI, preset, WebSocket transport); server hosting and
  operations are flagship.
- **Design detail carried from the old MVP13**: headless bootstrap (the Unity dedicated-server build
  target / `-batchmode -nographics`), a CLI/config surface (port, world/save selection, mod set,
  capability grants); the `DedicatedServer` topology live end to end: `Players.LocalPlayer = nil`,
  `client` scripts never load, `PreRender` never fires, input/GUI services absent (§3.2/§5.2.3);
  headless log routing (no Hub): `LuaLogFileSink` + remote log fetch for the host-side AI; a **WebGL
  pure client validated against the dedicated server** (the browser connects out and never hosts —
  plan decision D6). Old DoD: the dedicated binary hosts 2 clients (one desktop, one WebGL) through the
  client fixture set; a 1-hour soak without leaks; solo/host modes unaffected.

### MVP9 — Scale to ~100 players per room (XL)

- **Goal**: one room holds 100 players within the budgets of the scale targets (§4.3), proven by a
  load test.
- **Done so far**: `tools/ScaleHarness` (loopback, CoreCLR) and the staircase method
  (`dev-docs/SCALE_CHARACTERIZATION.md`); `AiOrchestrationQueueOptions.ForActorCount`
  (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs`); the per-sender
  handler cap; the per-recipient filter seam (`RbxApi/Instances/Replication/ReplicationFilter.cs`);
  `tools/vmbench` and the S1 numbers.
- **To do**:
  - **Bot client**: a headless client that speaks the real protocol and drives characters and remotes;
    100 bots from 2–4 processes.
  - **Interest management**: a distance/cell `IReplicationFilter` plus per-tier send rates (a
    StreamingEnabled-lite; the Roblox names stay loud stubs until built).
  - **VM guard cost** (risk R1): an adaptive batch with a bounded allocation exposure, or per-thread
    allocation accounting; per-mod slice enforcement on the accounting seams reserved since MVP2 (§2,
    frame-budget reservation; the old MVP17 item); if S1 says the guard cannot reach the budget, the
    server-only native Luau fallback behind `IScriptEngine` (plan decision D8 (b)).
  - Retire the known O(N) paths (`TODO.md`, "MVP5/MVP6 scalability debt": `ProcessPreSimulation`
    visiting every instance, the O(live) thread-quota scan, synchronous fan-out, one mutation gate held
    across an operation).
  - Raise the instance ceilings with measurement.
- **Test**: LT (frozen workload, staircase 20/50/100, 3 repeats), PT/EM for the filters.
- **DoD**: the scale targets of plan decision D5 (§4.3), frozen before measuring, met on the reference
  8-vCPU Linux server:
  100 bots in a 10,000-instance place for 30 min — 30 Hz tick with p99 frame ≤33 ms (server Lua
  ≤8 ms); downstream average ≤50 KB/s and p99 ≤100 KB/s per client; upstream ≤10 KB/s; join ≤10 s on
  LAN; 0 disconnects; retained heap delta ≤100 MB. The staircase results are published.
- **Reuses**: MVP5–MVP8, S1, S2.
- **Package/flagship**: packages (bot client, filters, harness); room sizing and matchmaking are
  flagship.

### MVP10 — Composable framework: one composition root, presets, host profile (M)

- **Goal**: building blocks (`AGENTS.md`, `Docs/ARCHITECTURE_RULES.md` §2.1). One entry point assembles
  any product from presets, and every preset boots in a test. This rung consolidates what MVP5 and MVP8
  started with their own presets and must land before MVP11–MVP12, whose role and tool gating is
  profile data.
- **Done so far**:
  - Installers and DI ports: `IActorIdentityProvider`, `IActorAdmissionProvider`,
    `IRbxCharacterMotorProvider`, `IBundledModSource`, the network-bridge provider
    (`RbxApi/Binding/RbxNetworkBridgeProviderBehaviour.cs`), `HubPageRegistry`
    (`Assets/CoreAI/Runtime/Core/Features/Hub/HubPageRegistry.cs`).
  - Configuration assets: `AgentPromptsManifest`, `LlmRoutingManifest`, `SkillSetAsset`,
    `CoreAISettingsAsset`, `AiPermissionsAsset`, `CoreAiPrefabRegistryAsset`.
  - The player-host (MVP5) and dedicated-server (MVP8) presets. The solo AI sandbox is today's default
    composition (two lifetime scopes, `CoreAILifetimeScope` and `CoreAiModsLifetimeScope`), not yet a
    named preset.
  - The principle is normative (`Docs/ARCHITECTURE_RULES.md` §2.1); existing violations are tracked in
    `TODO.md` under the grandfathering rule.
- **To do**:
  - A `CoreAiProfile` asset composing agent roles, tools per role, skills, LLM routing, capability
    tiers, Hub pages, stores, world settings and scale, topology, admission and the
    `ClientWritePolicy`. It absorbs the **host integration profile** (§2; the old MVP16 deliverable).
  - One scope, or one root that builds both scopes.
  - Presets: solo AI sandbox, player-host co-op, dedicated N-player server, client, embed-in-my-game.
  - Remove the process-wide statics from the main paths, or document the ones that stay:
    `CoreAISettings.Instance` (set by `CoreAILifetimeScope`, read at ~115 sites), the static scale of
    `RbxSpace` (one scale per world, so one process can host two rooms), and the static `CoreAi` locator
    (`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`).
  - Retire `AiNetworkExecutionPolicy.AllPeers` as the default in favour of the bridge topology.
  - Tool enable/disable per role as data (today every Lua/world tool is wired to the `Programmer` role in
    `Composition/CoreAiModsInstaller.cs`).
  - Fix the editor setup menus that hard-code `Assets/<package>` paths and break on a Git-URL install
    (`TODO.md`).
- **Test**: EM (profile → container validation), PM (each preset boots), MP (host and dedicated
  presets).
- **DoD**:
  - Five presets each boot in a test (PM, or headless for the server) with 0 errors.
  - Every block of the composability audit (`Docs/ROADMAP.md` §3, Track E) has a replace-with-fake test.
  - The difference between two presets is data only.
  - An architecture-fitness test forbids a new `static Instance` in feature code
    (`Docs/ARCHITECTURE_RULES.md` §2).
- **Reuses**: MVP5 and MVP8 presets, every existing installer.
- **Package/flagship**: packages (profile, presets, boot tests); the flagship is one more preset plus
  its own code (plan decision D7).
- **Design detail carried from §2 and the old MVP16 (host integration profile)**: embedding CoreAI mods
  into an existing meter-scale Unity game is a first-class scenario. A per-project profile
  (ScriptableObject: the `RbxSpace` scale [default 0.28], capability defaults, which host services and
  objects are bound, the per-world `ClientWritePolicy` resolved through the authority-resolver seam, the
  per-role human tool surface and the Players-may-fly flag) makes integration "drop a config and it
  works"; the configurable `RbxSpace` scale **is** the meter-world adapter.

### MVP11 — Roles (Creator/Player) & per-player AI over the network (M)

- **Goal**: every connected player has a per-world **player role** — Creator or Player (§2, roles
  decision) — and their AI chat runs server-side under their identity, gated by that role.
- **Done so far**: the actor-keyed chat factory `ActorKeyedInGameLlmChatServiceFactory`
  (`Assets/CoreAI/Runtime/Core/Features/Orchestration/InGameLlmChatServiceFactory.cs`) and
  `AttachChatFactory` in the Rbx bindings; per-actor quotas; the in-process
  `Assets/CoreAI.Demos/MultiplayerFoundation` demo; the autosave gate before every AI mutation; the
  `AIService` name reserved as a stub (`RbxApi/Instances/ServiceCatalog.cs`); background generation
  (Esc collapses the Hub, generation continues).
- **To do**:
  - A `Role` on `RbxPlayer` plus a grant/revoke API and UI (the Team Create analog).
  - Tool-surface gating per role, as profile data from MVP10.
  - A network chat channel (request/stream/cancel) over the bridge.
  - `AIService` with an `Ai` capability bit (`Ai = 1<<5`, excluded from `All`, §2 AI-call reservations)
    and the origin tag `ai:<modId>`.
  - The **async agent workflow** (carried from the old MVP16): a task queue, so new instructions enqueue
    instead of blocking or derailing the current work, and an unobtrusive HUD status ("agent building:
    X…" plus a completion notification) that needs no open Hub.
  - Resolve the "Creator" name collision: `Creator` is also a built-in **agent** role (the
    `world_command` designer, `Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md`). Documents say "player role
    Creator" and "agent role Creator"; in code the player role is its own type on `RbxPlayer`, never an
    agent role id.
  - Re-measure G10 (MVP2) with role gating and the task queue in place (risk R7).
- **Test**: EM (gating matrix with negative twins), MP (a Player cannot call `manage_mods`; a granted
  Creator can).
- **DoD**:
  - With 3 players, role gating holds on every human-facing tool.
  - A Creator's AI edit replicates as an intent.
  - A Player's AIService-driven mod still works under the mod's grants.
  - Chat privacy between actors holds over the wire.
- **Reuses**: MVP6 (intents), MVP10 (profile data).
- **Package/flagship**: packages (roles, gating, chat channel, AIService); the grant UI's polish is
  flagship.

### MVP12 — Studio mode core (XL)

- **Goal**: an in-game creator mode and a switch between creating and playing, in one application —
  runtime-first (`AGENTS.md`): every panel works in a built player. This reverses the old non-goal
  "a Studio-like 3D editing UI" (§7) and replaces the old MVP7 (editor tooling).
- **Done so far**: the Hub shell and `HubPageRegistry`; a runtime Lua editor
  (`HubIntegration/HubModEditorPage.cs`, `HubIntegration/LuaSyntaxHighlighter.cs`); the click pick
  source (`RbxApi/Binding/IClickPickSource.cs`, `UnityClickPickSource.cs`); the world package as the
  snapshot (staged swap); the autosave ring, revert and origin tags (`RbxApi/Instances/OriginTag.cs`);
  `world_command`/`execute_lua`; grants and intents (MVP6); roles (MVP11).
- **To do**:
  - A runtime Explorer (a UI Toolkit tree over `InstanceRegistry` with live `ChildAdded`/`Destroying`)
    and a Properties panel driven by `ClassCatalog` and the binding member tables.
  - Selection and highlight (`SelectionBox`/`Highlight` are backlog stubs today); multi-select.
  - Move/rotate/scale gizmos that write through Instance operations as intents.
  - Insert from a basic parts palette.
  - An undo/redo command stack over registry mutations, per player and network-aware.
  - The Fly camera.
  - **Play/Stop (plan decision D2 (c))**: live edit stays the default for Creators; Play/Stop is an added
    "test run" — capture the edit state, run it in a per-creator isolated session, restore on Stop. In a
    room with other players present it is never a world rewind.
  - `RunService:IsStudio/IsRunning/Run/Stop` semantics (today constants in `RbxRuntimeTopology.cs`), with
    a DEV entry; re-check DEV-14 (PluginSecurity members reachable at runtime by the elevated tier)
    against the creator mode once it exists.
  - Scripts shown in the tree (MVP4's script instances).
  - A selection-aware AI copilot context ("this part").
- **Test**: PM (UI Toolkit panels, gizmo operations, undo), EM (command stack), MP (2 Creators
  co-editing, 1 Player playing), MS.
- **DoD**:
  - A Creator builds a 50-part obby with gizmos only, presses Play, dies on a kill brick, presses Stop,
    and the edit state is byte-identical to before Play (golden).
  - Undo/redo holds 100 steps.
  - A second Creator's edits appear within 1 tick + RTT.
  - A Player cannot open creator mode.
- **Reuses**: MVP3 (snapshot), MVP6 (intents), MVP11 (roles).
- **Package/flagship**: the mechanisms (Explorer, Properties, gizmo, selection, undo services, Play/Stop)
  go in the packages as Hub pages or a new package; the final UX polish goes in the flagship (risk R10).
- **Carried from §7 (the reversed non-goal)**: manual building is architecturally cheap because manual
  operations go through the same Instance operations and authority resolver as the AI and mods — a UI
  layer, not a new system.

### MVP13 — Model packages, template library, Luau parity prerequisites (M–L)

- **Goal**: save or insert any subtree plus its scripts as a model; place templates come from a local
  library and the AI starts from them; close the script-level gaps a Roblox round trip needs before any
  file format is built (MVP14).
- **Done so far**: the world package codec and validation (`Infrastructure/RbxWorldPackageSerializer.cs`,
  `RbxApi/Instances/InstanceTreeSerializer.cs`); the single-mod bundle (`ExportMod`,
  [MOD_SHARING.md](MOD_SHARING.md)); `FileRbxWorldPackageStore`; the Luau downlevel at load
  (`LuaExecution/LuauSourceGate.cs`); script instances and contexts (MVP4); mod-core number→string
  coercion (B2-12; the Rbx surface is in progress, `TODO.md`).
- **To do**:
  - A `.model` package: a subtree of the world codec, with id remapping on insert and scripts carried as
    instances.
  - An InsertService analog and a template store (local first, then shareable).
  - Place templates: Baseplate, Obby, Tycoon-lite, Arena.
  - AI tools `list_templates`/`insert_template`/`new_place_from_template`; a Studio "Toolbox" panel.
  - **Parity prerequisites**: the Luau standard-library extensions (RT5); a "Luau-only" export lint that
    refuses `goto`, `_ENV` and CoreAI-only APIs (RT6, RT7); a round-trip decision per DEV item (map
    DEV-16 back to `math.huge` on export; DEV-7 reads as last-known values or stays documented; RT8); a
    Roblox-save mode that drops non-archivable instances (RT10); `Weld`/`WeldConstraint`/`Attachment`/
    `SpawnLocation`, the minimum the templates need (RT9); one coercion rule on both surfaces (RT4).
- **Test**: PT (codec), PL (stdlib conformance against the documented behaviour), EM (insert and remap,
  lint), PM (Toolbox), a benchmark scenario (the AI builds from a template).
- **DoD**:
  - A 1,000-instance model with scripts round-trips through the `.model` package with zero diff.
  - Three insertions of the same model get distinct ids and working `script.Parent` wiring.
  - Each place template boots through the MVP10 presets.
  - The export lint flags 100% of a seeded list of CoreAI-only constructs.
- **Reuses**: MVP3, MVP4, MVP10, MVP12 (Toolbox panel).
- **Package/flagship**: packages (formats, store, tools, lint, a minimal template set as samples); the
  template content library is flagship.

### MVP14 — Roblox interchange + round-trip parity gate (L) *(the old MVP4, widened)*

- **Goal**: Roblox places and models open here and run unchanged, and what is made here exports and runs
  in Roblox — proven by a corpus, not by fixtures written for the implementation (owner requirement:
  scripts and templates interchangeable both ways).
- **Done so far**: none of the four formats; MVP4 and MVP13 supply script instances, contexts, the
  stdlib extensions and the export lint.
- **To do**:
  - The reader/writer for **rbxl, rbxlx, rbxm and rbxmx**. First task: the licence and IL2CPP check of
    the candidate library (below).
  - Import into the world package or a `.model`: `Script` → `server`, `LocalScript` → `client`,
    `ModuleScript` → `shared`, honouring `RunContext`; imported scripts stay disabled until reviewed.
  - An asset-id substitution table and placeholders (RT12).
  - Export of the supported subset; anything outside it is reported, never silently dropped, and
    CoreAI-only constructs are refused or flagged (RT6, RT7).
  - `import_rbxl`/`export_rbxl` AI tools.
  - A **licensed round-trip corpus** (RT13): owner-authored or permissively licensed Roblox scripts,
    models and places, in tiers — scripts only, models with scripts, whole places.
- **Test**: PT (format codecs, golden files), PL (the corpus runs), EM (import/export), MS (the exported
  file opened in Roblox Studio and played; recorded evidence).
- **DoD (round-trip parity, explicit)**:
  - (a) Every corpus item imports and **runs unchanged** here: zero `NOT_IMPLEMENTED` hits, its
    assertions green.
  - (b) Export → re-import gives a **zero diff** on the supported subset: class, name, parent,
    properties, attributes, tags, script source, `RunContext`. The subset is defined by the
    `ClassCatalog`/durable surface, and the diff tool is committed.
  - (c) Export → open in Roblox Studio → run: the same corpus assertions pass there. This is a recorded
    manual gate per release, since Roblox cannot run in CI.
  - (d) Items outside the subset produce a machine-readable loss report, never a silent loss.
  - (e) A size gate: at least N corpus items per tier; the owner sets N before measuring.
- **Reuses**: MVP3 (package), MVP4 (script instances), MVP13 (models, lint, stdlib).
- **Package/flagship**: packages (codecs, tools, corpus harness); the corpus content that is not
  licensable for a public repository stays with the owner.
- **Round-trip gaps (RT1–RT13)** and the rung that closes each:

  | # | Gap | Direction broken | Closed in |
  |---|---|---|---|
  | RT1 | Script containers are not instances: a `Script` inside a `Part` using `script.Parent.Touched` cannot be represented | both | MVP4 |
  | RT2 | No `require`/`ModuleScript` (the sandbox removes `require`) | import | MVP4 |
  | RT3 | No `RunContext` or declared client/server split | both | MVP4 |
  | RT4 | Coercion differs from Roblox: the mod-core API takes a number for a string (B2-12); the Rbx surface still refuses one (`Part.Name = 5`) | import | MVP13 (in progress now) |
  | RT5 | Missing Luau stdlib extensions (`string.split`, `table.find`, `table.clear`, `table.freeze`, `table.create`, `math.clamp`, `math.round`, `math.sign`, `math.noise`, `utf8`, `buffer`) | import | MVP13 |
  | RT6 | Lua 5.2-only syntax (`goto`, labels, `_ENV`) is accepted and would fail in Roblox | export | MVP13 (lint), MVP14 (export) |
  | RT7 | CoreAI-only surface (`hooks_*`, `report`, `coreai_world_*`, `unity_*`, `AIService`, PluginSecurity members reachable under DEV-14) | export | MVP13 (lint), MVP14 (export) |
  | RT8 | DEV deviations that change observable behaviour (DEV-7, DEV-16, DEV-13, DEV-10/11, DEV-2, DEV-15) | both | MVP13 (a decision per DEV item) |
  | RT9 | Class coverage: welds, constraints, `Attachment`, `MeshPart`, `SpawnLocation`, `Seat`, `Decal`/`Texture`, lights, `Tool`, `Team`, `BindableEvent`, `Terrain` | both | MVP13 (the template minimum), later rungs and backlog |
  | RT10 | Package semantics: the world package keeps `Archivable = false` instances; scripted gravity is not saved; `Lighting` properties are backlog | export | MVP13 (Roblox-save mode) |
  | RT11 | The four file formats are absent | both | MVP14 |
  | RT12 | `rbxassetid://` content is never fetched (ToS); CoreAI has no asset-id system of its own | both | MVP14 (substitution table) |
  | RT13 | No licensed corpus of real Roblox scripts, models and places | test | MVP14 |

- **Design detail carried from the old MVP4**: the converter is layered over the native place package —
  import produces `world.json` + mod entries and never bypasses the native format.
  1. **First task — reader verification**: candidate C# implementation MaximumADHD/Roblox-File-Format
     (pure C#) — verify licence and netstandard/Unity (IL2CPP) compatibility; fall back to an own reader
     against the rbx-dom spec if it fails either.
  2. `import_rbxl` (binary and XML) → `world.json` + mod entries. Scope tiers: (1) geometry/instance tree
     — `Part`/Wedge/`Model` sizes, CFrames, colours, materials → `InstanceRegistry` through `RbxSpace`;
     (2) embedded Luau scripts imported as **disabled** scripts — untrusted code: the player/AI enables
     them after review, the downleveler processes them on enable, quarantine applies; (3) Lighting,
     SpawnLocations, value objects and attributes as API coverage allows.
  3. Explicit limits: `rbxassetid://` marketplace assets (meshes/textures/sounds) are **not**
     downloaded (ToS + third-party rights) — placeholders keep the id, with a user-supplied substitution
     table; `Terrain` instances are skipped with an info diagnostic (Terrain voxel import is a non-goal,
     §7).
  4. `export_rbxl` — **in scope, not secondary**; the export path reuses the reader's file-format
     library. The first draft said "import breadth exceeds export breadth by design: export emits only
     the Roblox-shaped subset" — that stands for breadth, but export now also **refuses or flags**
     CoreAI-only constructs instead of dropping them (DoD (d)).
  - Old DoD, kept inside the DoD above: (a) a real `.rbxl` map imports (geometry tier) with instance
    counts and transforms verified through the registry and a golden comparison; (b) self round trip:
    export our world to `.rbxl` and re-import it, lossless for the supported subset; (c) embedded Luau
    scripts arrive disabled, source preserved, downleveled on enable. Fixture files live under
    `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/` (a new subfolder of the existing
    corpus).

### MVP15 — GUI + remaining input (L) *(the old MVP14 and the rest of the old MVP10)*

- **Goal**: tutorial-grade GUI scripts run on host, client and WebGL: `ScreenGui`/`Frame`/`TextLabel`/
  `TextButton`/`ImageLabel`/`TextBox` + `UICorner`/`UIListLayout`, rendered as runtime UI Toolkit
  (UXML/USS interpreted at runtime — RUNTIME-first, the same approach as the Hub).
- **Done so far**: the class descriptors are planned loud stubs (`ClassCatalog.cs`, phase `MVP14`); the
  `PlayerGui` container; `UserInputService`. The runtime UI interpreter of `TODO.md` [R4] has not
  started.
- **To do**:
  - The runtime UI Toolkit mapping for the class set above; `UDim2` layout (Scale+Offset → USS
    percent+px); `MouseButton1Click`, `Activated`, `FocusLost(enterPressed)`; `player.PlayerGui`;
    z-order via `DisplayOrder`/`ZIndex`; everything else in the GUI family stays a loud stub.
  - Replication of `StarterGui` → `PlayerGui` (client context, MVP4).
  - Touch buttons (ContextActionService `createTouchButton`) and `TouchTap` for mobile.
  - Share the element factory with the runtime UI of [R4], which is re-prioritized behind the
    multiplayer rungs.
- **Test**: PM, PL, MP.
- **DoD**: the "score label + shop button" fixtures pass on host, client and WebGL; GUI survives a hot
  reload (rebuilt); a touch button appears on a touch device or the device simulator; **corpus gate
  ≥75%** of Tier A+B+C (§6.4).
- **Reuses**: MVP4 (client context), MVP6 (replication), MVP7 (ContextActionService).
- **Package/flagship**: packages (GUI classes, UI mapping); HUD and menu design are flagship.
- **Design detail carried from the old MVP14**: the class set above; `UDim2` layout semantics; the
  events above; the `player.PlayerGui` container; z-order via `DisplayOrder`/`ZIndex`; it coexists with
  Hub windows. Old dependencies (MVP2 signals, Players, input focus rules) are all met by earlier rungs.

### MVP16 — DataStore + leaderstats (M) *(the old MVP9)*

- **Goal**: Roblox-shaped persistence over CoreAI's existing store, semantics per the S-rules
  (`03_SERVICES_AND_DATA.md`, DataStore emulation table), running server-side on a dedicated server.
- **Done so far**: the `DataStoreService` stub (`ServiceCatalog.cs`, phase `MVP9`);
  `ScheduleWaitUntil` (the yield primitive); `FileLuaModStore` with atomic writes; the `game:BindToClose`
  stub (`RbxApi/Instances/RbxDataModel.cs`, phase `MVP5`).
- **To do**: the design below, with the server-only context (MVP4) and dedicated-server storage (MVP8);
  `game:BindToClose` (carried from the old MVP5, below) so a place can save on shutdown.
- **Test**: EM, PL, LT (a 100-player concurrent `UpdateAsync` soak).
- **DoD**: a save/load round-trip test including the WebGL durability check; concurrent `UpdateAsync`
  calls serialize correctly; conformance tests named per §6.6 (e.g. `S1_8_UpdateAsyncNilAborts`); the
  corpus DataStore fixtures pass; a 100-player concurrent `UpdateAsync` soak loses no write.
- **Reuses**: MVP4, MVP8, the JSON contract of MVP2.
- **Package/flagship**: packages (the service, a pluggable backend); cloud storage is a host or
  flagship backend.
- **Design detail carried from the old MVP9**: `DataStoreService:GetDataStore(name, scope?)` →
  `GlobalDataStore` facade: `GetAsync(key)`, `SetAsync(key, value, userIds?, options?)`,
  `UpdateAsync(key, transformFunction)`, `IncrementAsync(key, delta = 1)`, `RemoveAsync(key)` [^4] — all
  *yield* the calling Lua thread through the scheduler's `ScheduleWaitUntil` primitive (§2 AI-call
  reservations; never block the frame), resolving next Heartbeat. `UpdateAsync` semantics (including a
  nil return aborting the write, S1.8) per S1.x. **Store identity is world-global (S1.1 parity)**: the
  key is `"ds:<name>:<scope>:<key>"` with **no modId prefix** — the same `(name, scope)` from any mod
  hits the same store; access is capability-gated (`Data = 1<<6`, reserved by the capability parsing,
  §2 AI-call reservations). The skill must teach that stores are shared across mods — name collisions
  are the author's responsibility, as in Roblox. Values are stored as JSON strings in `FileLuaModStore`
  (atomic writes; durability through `CoreAiWebGlPersistence` on WebGL, as for every store) behind a
  pluggable save-provider interface (a host can adapt its own backend). `UpdateAsync` is a
  read-modify-write under the store lock. Table↔JSON through the **shared JSON contract** (`RobloxJson`,
  §5.2.4); non-JSON values (functions, Instances) raise `BAD_ARGUMENT` naming the key path. Loud stubs:
  `GetVersionAsync`, `ListKeysAsync`, `ListVersionsAsync`/`GetVersionAtTimeAsync`/`RemoveVersionAsync`
  (S1.18), `GetRequestBudgetForRequestType` (S1.24) and `GetOrderedDataStore` (S1.2, S1.27–S1.29; a
  minimal integer-sorted implementation is a cheap fast-follow worth costing — leaderboards are
  corpus-common); the `DataStoreKeyInfo` second return of `GetAsync`/`UpdateAsync` is `nil` (DEV-10);
  the 4-second `GetAsync` cache is not emulated (DEV-11). A `leaderstats` + DataStore sample mod ships as
  a bundled fixture; per-player keys use the `u:<UserId>` convention.
- **`game:BindToClose(fn)`** (carried from the old MVP5, deliverable 9), M6.1 semantics: several
  callbacks may be bound; on shutdown they run **in parallel** with a bounded flush window (~30 s);
  invoked on world unload, app quit, server shutdown and world-load teardown (it rides the
  `TeardownModEffects` pipeline).

### MVP17 — Audio / FX / animation (M) *(the old MVP15)*

- **Goal**: presentation-layer breadth, networked.
- **Done so far**: loud stubs only (`Sound`, `ParticleEmitter` in `ClassCatalog.cs`, `SoundService` in
  `ServiceCatalog.cs`, all phased `MVP15`).
- **To do**: `Sound` (`Play/Stop/Pause`, `Playing`, `Volume`, `Looped`, `Ended`) over the host audio
  manager; `SoundService:PlayLocalSound`; a minimal `ParticleEmitter` (`Enabled`, `Rate`, `Color`,
  `Emit(count)`) over a pooled Unity particle prefab; an Animator-lite so MVP7's characters animate
  (`AnimationController` otherwise stays loud stubs, except a documented CoreAI extension
  `humanoid:PlayAnimationByName(name)`; R15 rig fidelity is a non-goal); networked playback of sound and
  animation.
- **Test**: PM, MP.
- **DoD**: sound-on-touch and emit-burst fixtures pass on desktop and WebGL solo; a sound and an
  animation started on the server play on every client.
- **Reuses**: MVP6, MVP7.
- **Package/flagship**: packages (services); audio and animation content are flagship.

### MVP18 — Mod UX rest, skill manifest, console + self-repair (M–L) *(the old MVP5 rest, MVP6 and MVP16)*

- **Goal**: close the realtime loop — the AI (and the player) see, diagnose and fix mods during play with
  zero editor involvement — and make the skill generated from the code so it cannot drift.
- **Done so far**: the Lua log service is wired end to end (`Logging/`); `get_mod_logs`
  (`Logging/GetModLogsLlmTool.cs`) is registered for the Programmer role; skills
  (`read_skill`/`manage_skills`); load-error auto-repair (`LuaExecution/LuaModAutoRepairPolicy.cs`,
  `Presentation/CoreAiLuaModAutoRepair.cs`); the preprocessor at load with author line numbers
  (`LuaExecution/LuauSourceGate.cs`); the hand-maintained "Rbx API" skill kept byte-identical in
  `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt` and
  `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/BuiltInRbxApiSkillText.cs`.
- **To do**: folder mods and `api_version`; `list_mods`/`enable_mod`/`disable_mod`/`get_api_surface`; a
  generated API manifest with a CI diff; `watch_mod_logs`, the in-game console and runtime self-repair
  (below).
- **Test**: EM, PL, PM (console), a scripted chaos test in a built player.
- **DoD**: the three carried DoDs below — the AI fixes a broken folder mod through tools only; the
  manifest is deterministic and CI-diffed with a prompt-eval fixture set; a mod that starts failing at
  runtime is fixed autonomously within N repair attempts in a built player.
- **Reuses**: everything before it.
- **Package/flagship**: packages.
- **Design detail carried from the old MVP5 (mod system UX, the rest)**: the Roblox-inspired but
  file-native layout, enable/disable, hot reload and the AI management tool surface, equally usable by
  humans and the LLM.
  1. Folder mods: `Mods/<ModName>/mod.json` + `server/`, `client/`, `shared/` script folders (with
     MVP4's script instances and declared contexts). `mod.json` schema: `id`, `name`, `version`,
     **`api_version`** (a single int starting at 1 — the mod-API contract version; the loader refuses a
     mod whose `api_version` is above the host's and warns with `API_VERSION_MISMATCH` when it is below),
     `enabled`, `loadOrder` (the manifest already records `LoadOrder`, A1-02), `capabilities` (maps to
     `LuaCapabilities`; parsing also reserves `Ai = 1<<5`, used by MVP11, and `Data = 1<<6`, used by
     MVP16), `contexts` (implicit from folders, overridable), `preserveInstances` (§6.3), `category`,
     `description`.
  2. Single-file mods keep the `--[[@coreai ...]]` frontmatter (`LuaModHeader.cs`, `mod-system.md` §1)
     as one-script mods; the seeder (`BundledModSeeder`) keeps working; the header gains an optional
     `api_version:` key with the same gating.
  3. (`require` → MVP4.)
  4. Enable/disable without deletion (persisted `enabled`; a disabled mod keeps its source and store).
  5. Hot reload pipeline: file/store change → preprocessor → teardown per §6.3 → re-run; reload latency
     target < 250 ms for a 500-line mod.
  6. Preprocessor at load with author line numbers — **done** (`LuauSourceGate`, line-preserving).
  7. Log service end to end — **done** (`get_mod_logs` registered; `LuaLogFileSink` optional).
  8. AI tools (§6.2): `list_mods`, `enable_mod`, `disable_mod`, `get_api_surface` next to the existing
     `manage_mods` actions (`LuaModsLlmTool.cs`) and `get_mod_logs`.
  9. (`game:BindToClose` → MVP16.)
  - Old DoD: the AI can, through tools only, create a folder mod, break it, read the error from
    `get_mod_logs` (correct file/line), patch it, hot-reload and confirm recovery — as an automated
    integration test; a disabled mod provably runs zero instructions.
- **Design detail carried from the old MVP6 (AI Lua skill = the documentation)**: one artifact that (a)
  is injected as the in-game LLM's modding skill, (b) serves as the human-facing API docs and (c) embeds
  the machine-readable API manifest — so the LLM never calls unimplemented API and never picks the wrong
  tool for a job the API already solves. Motivating incident: a 4B in-game model animated a 2-second
  movement with a raw `execute_lua` loop instead of one `TweenService:Create` call.
  1. The skill document (single source, English) with per-service teaching sections and short correct
     examples verified against the corpus harness.
  2. A **"Common mistakes"** section of wrong→right pairs, grown from observed repair sessions: manual
     per-frame movement loops → `TweenService:Create`; polling → events or `WaitForChild`; busy-wait →
     `task.wait(seconds)`; per-frame `FindFirstChild`/`GetService` in `Heartbeat` → cache the reference;
     `while wait() do` → `RunService.Heartbeat:Connect`; forgetting `:Disconnect()` → the connection
     lifetime rules (§5.2.5); assuming a `shared` ModuleScript is one shared object → each context gets
     its own copy (R3.4, §3.2); **scale confusion** — the active `RbxSpace` scale and how canonical Roblox
     numbers (`WalkSpeed 16`, `Gravity 196.2`, `JumpPower 50`) translate under it; the **units-per-channel
     rule** — the mod API and `execute_lua` speak studs and right-handed Roblox space, the Unity-side C#
     tools speak meters and Unity space, and numbers are never mixed across channels without converting.
  3. **API manifest**: machine-readable JSON listing every class/member/service with status
     `implemented | stub(planned rung) | not_planned`, **generated** from `ServiceCatalog` +
     `ClassCatalog` (the same source as `get_api_surface`).
  4. Delivery: the skill is assembled at build time from the doc and the generated manifest, replacing
     the hand-maintained C# string literals (`BuiltInRbxApiSkillText.cs`, `BuiltInLuaModdingSkillText.cs`).
  5. Update protocol: every API-surface rung updates the skill in the same change (implicit DoD (b)); a
     CI check fails when the generated manifest and the committed skill disagree.
  - Old DoD: manifest generation is deterministic and CI-diffed against the catalogs; the skill covers
    100% of the implemented services (every catalog entry has a doc section); a prompt-eval fixture set
    exists — "move a part smoothly over 2 s" answered with TweenService, "wait for a child" answered with
    a yielding `WaitForChild` — runnable against a small model as a regression harness.
- **Design detail carried from the old MVP16 (in-game console + AI self-repair loop)**: a Hub "Console"
  page — the merged live log stream from `LuaLogService` (filter by mod/level/context, including client
  logs forwarded to the host's AI, rendered through `LuaLogFormatter.ToPromptText` for the agent path); a
  REPL line executing in a chosen mod's sandbox — REPL/`execute_lua` one-shots follow the §2 ownership
  rules (world-owned instances with `console:<invocationId>` origin tags, selective undo by invocation,
  an auto-cleaning preview scope); self-repair extended from load errors to *runtime* errors — the agent
  subscribes to `watch_mod_logs` (§6.2), correlates structured errors (id + file + line + code + hint),
  patches through `manage_mods reload` and verifies through the same stream; repair attempts are
  rate-limited (`LuaGenerationRateLimiter`) and every attempt is a recorded revision (rollback through
  `TryRevertMod`); repair outcomes feed the skill's "Common mistakes". (The async agent workflow moved to
  MVP11, the host integration profile to MVP10.) Old DoD: a scripted chaos test — a mod that starts
  failing at runtime is fixed by the agent within N repair attempts in a built player, with no Unity
  console consulted.

### MVP19 — Performance, WebGL and mobile hardening (M) *(the old MVP17, rest)*

- **Goal**: the whole stack holds its budget on the weakest targets: WebGL client and Android client.
- **Done so far**: the per-resume budgets and quarantine; `ILuaCsGuardObserver`; the WebGL browser gate
  (2026-09-02, `TODO.md`); the WebGL save budget (4 MiB / 4,032 instances,
  [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md)); MVP9's scale work.
- **To do**: lazy material textures (the ~99 MB eager load, `TODO.md`); incremental JSON/ZIP on WebGL;
  the WebGL client soak; an Android client build; the performance regression suite (F-20); the carried
  items below.
- **Test**: LT, manual WebGL and Android runs with recorded evidence.
- **DoD**: the old MVP17 DoD below, plus the WebGL client and the Android client in a 20-player room.
- **Reuses**: everything before it.
- **Package/flagship**: packages.
- **Design detail carried from the old MVP17**: the benchmark corpus (Tier C, §6.4) as a performance
  suite; per-phase scheduler budget telemetry into Statistics; a zero-allocation audit of hot paths
  (signal fire, marshalling, `RbxSpace` conversions, tick); a WebGL soak as solo **and** as a pure
  client (single-thread yields; durability through `CoreAiWebGlPersistence`, never a hand-driven
  `FS.syncfs`); a stress profile of 50 mods / 5,000 instances / 2,000 connections on the weakest target;
  kill-switch UX for runaway mods (quarantine after K consecutive budget kills —
  `ModQuarantined`/`ModTearingDown` over the `ILuaModRuntime.ModHandlerErrored` consumers; tune and
  surface it). (Per-mod slice enforcement moved to MVP9.) Old DoD: the mod stack's frame cost ≤2 ms at the
  stress profile on mid-tier hardware; the WebGL build passes the same ≥85% A+B+C corpus gate as desktop,
  restricted to the solo-eligible subset (multi-client fixtures excluded), plus the client-mode fixture
  set.

### Dropped: the old MVP7 (editor tooling)

Dropped on 2026-09-24. Under the RUNTIME-first rule and the Studio-in-game goal, the runtime Studio
(MVP12) replaces its value; the Lua/Luau syntax highlighting it contained has shipped (editor-side:
`.lua`/`.luau` importers, the highlighted inspector, the `CoreAI/Lua Script Viewer` window, and the
engine-independent tokenizer in `Runtime/LuaAssets`). Kept as the record of what it planned: a
read-only script viewer with revision diff (`ILuaScriptVersionStore` data); a Mod Manager editor window
mirroring the Hub Mods-tab actions (enable/disable/reload/logs); a mod log console window (an
`ILuaLogService` view with a per-mod filter). Its runtime equivalents are the Hub pages of MVP12 and
MVP18. The `diff_mod_versions` AI tool it listed (§6.2) moves to MVP18.

---

## 5. MVP1 and MVP2 in detail

This section is the MVP1/MVP2 design as it was written and then built. Rung numbers in it are the
**old** ones (map them through §4.1: MVP5 → MVP4/MVP16/MVP18, MVP6 → MVP18, MVP8 → Gameplay services I,
MVP9 → MVP16, MVP10 → MVP7/MVP15, MVP11 → MVP5, MVP12 → MVP6, MVP13 → MVP8, MVP14 → MVP15, MVP15 →
MVP17, MVP17 → MVP9/MVP19), and the stub phase names it quotes are the ones the code raises today.

Code home: `Assets/CoreAIMods/Runtime/RbxApi/` — engine-free assemblies `CoreAI.RbxApi.Datatypes`
(`RbxApi/Datatypes/`) and `CoreAI.RbxApi.Instances` (`RbxApi/Instances/`), the Unity-side
`CoreAI.RbxApi.Binding` (`RbxApi/Binding/`) and `CoreAI.RbxApi.Unity` (`RbxApi/Unity/`, which holds
`RbxSpace`) — plus the Lua bindings in `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbx*.cs`, guarded
`#if COREAI_LUA` where they touch the VM adapter. (The first draft planned one `RobloxApi/` folder inside
`CoreAI.Mods`; the tables below keep the planned names next to the shipped files.)
C# classes use the `Rbx` prefix to avoid colliding with Unity types (`RbxInstance` vs. Unity
`Object`); the *Lua-visible* names are unprefixed Roblox names. Rule IDs cited below refer to
the normative docs in §2.1.

### 5.1 MVP1 — Instance/DataModel core

#### 5.1.1 Task breakdown

| # | Task | Planned file (first draft) | Shipped in (`Assets/CoreAIMods/Runtime/`) |
|---|------|----------------------------|--------------------------------------------|
| 1 | Identity: `InstanceId`, `InstanceRecord`, `InstanceRegistry` | `RobloxApi/Identity/InstanceId.cs`, `InstanceRegistry.cs` | `RbxApi/Instances/InstanceId.cs`, `InstanceIdAllocator.cs`, `InstanceRecord.cs`, `InstanceRegistry.cs` |
| 2 | `RbxInstance` base: hierarchy, navigation, lifecycle, attributes, tags | `RobloxApi/Instances/RbxInstance.cs` | `RbxApi/Instances/RbxInstance.cs`, `InstanceTagStore.cs`, `AttributeContract.cs` |
| 3 | Class set: `Folder`, `Model`, `BasePart`/`Part`, `Workspace`, container services, `DataModel` | `RobloxApi/Instances/*.cs` | `RbxApi/Instances/ClassCatalog.cs`, `DataModelBootstrap.cs`, `RbxDataModel.cs` |
| 4 | Pure-spec datatypes: `Vector3`, `CFrame`, `Color3`, `UDim`, `UDim2`, `Enum` registry | `RobloxApi/Datatypes/*.cs` | `RbxApi/Datatypes/Rbx*.cs` |
| 5 | **`RbxSpace` conversion boundary** (D2/D3): `ToUnity`/`FromUnity` for position/rotation/CFrame/velocity + the scale constant | `RobloxApi/Spatial/RobloxSpace.cs` | `RbxApi/Unity/RbxSpace.cs` |
| 6 | Marshalling: datatypes + instances over `IValueMarshaller` | `RobloxApi/Marshalling/RobloxValueMarshaller.cs` | `Scripting/LuaCs/LuaCsRbxValues.cs`, `LuaCsRbxDatatypeBindings.cs` |
| 7 | Unity binder: materialize/track GameObjects for spatial instances (all transforms via `RbxSpace`) | `RobloxApi/Binding/InstanceGameObjectBinder.cs` | `RbxApi/Binding/InstanceGameObjectBinder.cs` |
| 8 | World bridge: lazy-wrap CoreAI world objects as Instances (reads via `RbxSpace` inverse) | `RobloxApi/Binding/WorldInstanceAdapter.cs` | `RbxApi/Binding/WorldInstanceAdapter.cs`, `WorldQuerySceneWalker.cs` |
| 9 | Lua global installation: `game`, `workspace`, `Instance`, datatype constructors | `RobloxApi/RobloxApiInstaller.cs` | `Scripting/LuaCs/LuaCsRbxApiBindings.cs`, `LuaCsRbxInstanceBindings.cs` (registered from `Composition/CoreAiModsInstaller.cs`) |
| 10 | Error surface: `RbxError` codes + formatter (§5.2.7, shared) | `RobloxApi/RbxError.cs` | `RbxApi/Instances/RbxError.cs` |
| 11 | Tests: datatype goldens, `RbxSpace` suite, instance lifecycle | `Tests/EditMode/RobloxApi/{Datatypes,Instances}/` | `Assets/CoreAIMods/Tests/EditMode/RbxApi/{Datatypes,Instances,Acceptance}/` (§6.6) |

The `RbxSpace` test suite (task 5/11), named explicitly:

- `RbxSpaceRoundTripEditModeTests` — property-based: `FromUnity(ToUnity(x)) == x` over randomized
  positions/rotations/CFrames (and the Unity-first direction), at both scales. Dual-scale
  EditMode runs require a **test-only reset hook / per-test config injection** for the scale
  (production keeps the constant-per-session rule).
- `RbxSpaceGoldenFixtureEditModeTests` — golden fixtures against documented Roblox values:
  `CFrame.lookAt` cases, `CFrame.Angles` chirality, nested `ToWorldSpace`/`ToObjectSpace`
  compositions, `LookVector/RightVector/UpVector` handedness.
- `Mvp1ConversionLintEditModeTests` — lint-style scan of the `RbxApi` layer sources: no direct
  `UnityEngine.Transform`/`Vector3`/`Quaternion` math outside `RbxSpace` and the binder's
  single call sites (the tripwire against scattered double conversions).

#### 5.1.2 Public C# API sketch

```csharp
// Identity/InstanceId.cs
public readonly struct InstanceId : IEquatable<InstanceId>
{
    public readonly ulong Value;                  // 0 = invalid; monotonic, never reused in-session
    public static InstanceId None => default;
}

// Identity/InstanceRegistry.cs
public sealed class InstanceRegistry
{
    public InstanceId Register(RbxInstance instance, string ownerModId);
    public bool TryGet(InstanceId id, out RbxInstance instance);
    public bool TryGetByNetId(uint netId, out RbxInstance instance);        // empty until MVP12
    public bool TryGetByWorldName(string worldName, out RbxInstance instance);
    public void BindNetId(InstanceId id, uint netId);                        // MVP12
    public void BindWorldName(InstanceId id, string worldName);
    public void Unregister(InstanceId id);
    public IReadOnlyList<RbxInstance> GetOwnedBy(string modId);              // hot-reload teardown
    public event Action<InstanceRecord> Registered;
    public event Action<InstanceRecord> Unregistered;
}

// Spatial/RbxSpace.cs — THE single conversion boundary (D2/D3). Pure static, no state
// beyond the configured scale. Nothing else in the API layer converts (lint-enforced).
public static class RbxSpace
{
    /// <summary>Meters per stud. Default 0.28; configurable at host bootstrap, constant per session.</summary>
    public static float MetersPerStud { get; }   // set once via RbxSpace.Configure;
                                                 // internal test-only reset hook for dual-scale EditMode runs (§5.1.1)

    public static UnityEngine.Vector3 ToUnity(RbxVector3 position);       // scale + negate Z
    public static RbxVector3 FromUnity(UnityEngine.Vector3 position);
    public static UnityEngine.Quaternion ToUnity(in RbxCFrameRotation r); // matching handedness flip
    public static RbxCFrameRotation FromUnity(UnityEngine.Quaternion q);
    public static (UnityEngine.Vector3 pos, UnityEngine.Quaternion rot) ToUnity(in RbxCFrame cf);
    public static RbxCFrame FromUnity(UnityEngine.Vector3 pos, UnityEngine.Quaternion rot);
    public static UnityEngine.Vector3 VelocityToUnity(RbxVector3 v);      // scale + flip, no translation
    public static RbxVector3 VelocityFromUnity(UnityEngine.Vector3 v);
    public static float AccelerationToUnity(float studsPerSecSq);         // gravity etc.
}

// Instances/RbxInstance.cs — mirrors the Roblox Instance member set [^3]
public abstract class RbxInstance
{
    public InstanceId Id { get; }
    public string ClassName { get; }
    public const int MaxNameLength = 100;                   // longer names are truncated (DEV-15)
    public string Name { get; set; }
    public bool Archivable { get; set; }                    // honored by Clone()
    public RbxInstance Parent { get; set; }                 // full re-parent pipeline; throws PARENT_LOCKED after Destroy

    public RbxInstance FindFirstChild(string name, bool recursive = false);
    public RbxInstance FindFirstChildOfClass(string className);
    public RbxInstance FindFirstChildWhichIsA(string className, bool recursive = false);
    public RbxInstance FindFirstAncestor(string name);
    public RbxInstance FindFirstAncestorOfClass(string className);
    public RbxInstance FindFirstAncestorWhichIsA(string className);
    public IReadOnlyList<RbxInstance> GetChildren();
    public IReadOnlyList<RbxInstance> GetDescendants();
    public RbxInstance Clone();                             // deep; skips Archivable == false
    public void Destroy();                                  // atomicity per R6.2
    public void ClearAllChildren();
    public bool IsA(string className);                      // walks the class ancestry incl. "Instance"
    public bool IsDescendantOf(RbxInstance ancestor);
    public bool IsAncestorOf(RbxInstance descendant);

    public object GetAttribute(string attribute);           // opaque seam value handle (IValueMarshaller)
    public void SetAttribute(string attribute, object value);
    public IReadOnlyDictionary<string, object> GetAttributes();
    public void AddTag(string tag);                         // CollectionService arrives later;
    public void RemoveTag(string tag);                      // tags themselves work from MVP1
    public bool HasTag(string tag);
    public IReadOnlyList<string> GetTags();

    // Signals: all use the general deferred Connect/Once/Wait path.
    public RbxScriptSignal ChildAdded { get; }
    public RbxScriptSignal ChildRemoved { get; }
    public RbxScriptSignal DescendantAdded { get; }
    public RbxScriptSignal DescendantRemoving { get; }
    public RbxScriptSignal Destroying { get; }
    public RbxScriptSignal AncestryChanged { get; }         // (movedInstance, newParent), on every descendant
    public RbxScriptSignal Changed { get; }                 // (propertyName); a ValueBase passes its new Value
    public RbxScriptSignal AttributeChanged { get; }        // (attributeName)
    public RbxScriptSignal GetAttributeChangedSignal(string attribute);
    public RbxScriptSignal GetPropertyChangedSignal(string property);
    // fires Changed and the per-property signal, only on a real change
    protected internal void NotifyPropertyChanged(string property);
}
```

#### 5.1.3 Lua-visible surface (MVP1)

Globals installed: `game`, `workspace` (== `game.Workspace`), `Instance`, `Vector3`, `CFrame`,
`Color3`, `UDim`, `UDim2`, `Enum`.

| Class | Lua members shipped by the current release | Planned loud stubs (has rung) | Backlog loud errors (no rung) | Unsupported loud errors (deliberate) |
|---|---|---|---|---|
| `Instance` (static) | `Instance.new(className)`; deprecated second `parent` arg accepted with a once-per-mod log; about ninety known Roblox classes that are not built (`WedgePart`, `SpawnLocation`, `Weld`, `Attachment`, …) raise `NOT_IMPLEMENTED` instead of "Unable to create" | — | `Instance.fromExisting` | — |
| `Instance` (members) | §5.1.2 navigation/lifecycle/attributes/tags; `WaitForChild` including the absent-child yield, 5 s infinite-yield warning and timeout overload; general Instance-tree signals; `Changed` on every instance; `GetPropertyChangedSignal` refusing an event, a method or a near miss of a known property name (a typo), and handing a never-firing signal with one log note to a real property CoreAI does not model | — | `FindFirstDescendant`, `QueryDescendants`, `GetActor`, the styling and sandboxing members | — |
| `Folder` | pure container | — | — | — |
| `PVInstance` descendants | ancestry/API shape plus `PivotTo`, `GetPivot` | — | — | — |
| `Model` | container plus `PrimaryPart`, `WorldPivot`, `GetPivot`, `PivotTo` | — | `MoveTo` (use `PivotTo`), `TranslateBy`, `GetBoundingBox`, `GetExtentsSize`, `ScaleTo`/`GetScale`, the persistent-player and legacy primary-part members | — |
| `BasePart`/`Part` | `Position`, `Size`, `CFrame`, `Orientation`, `Rotation`, `Color`, `Transparency`, `Anchored`, `CanCollide`, `Shape`, `Material` (all 45 `Enum.Material` items render; unmapped ids fall back to a magenta diagnostic material), `MaterialVariant` (string; `""` for none; an unknown name renders the plain `Material`, not an error) | — | `Velocity`, `AssemblyLinearVelocity`, `AssemblyAngularVelocity`, `Massless`, `CanQuery`, `CanTouch`, `CollisionGroup`, `CustomPhysicalProperties`, six legacy surface properties | — |
| `Workspace` | child navigation; `CurrentCamera`; `SignalBehavior` reads `Enum.SignalBehavior.Deferred` (D4); inherited `PivotTo`/`GetPivot` | `Raycast`/`Gravity` → MVP8; `GetServerTimeNow` → MVP2 | — | `Terrain`; setting `SignalBehavior` (Deferred-only, D4) |
| `Camera` | `CFrame`, `CameraType`, `CameraSubject`; a `PVInstance`, so `GetPivot`/`PivotTo` | — | `FieldOfView` (needs a rig API) and the other projection/viewport members | — |
| `DataModel` (`game`) | class-scoped `GetService`/`FindService`; `GetService` resolves tree-backed services, registered placeholders and 28 further known Roblox services as loud placeholders; placeholder member access raises `NOT_IMPLEMENTED` with its rung or workaround; `FindService` returns nil for a registered placeholder not yet resolved; unknown names (and `""`) raise `UNKNOWN_SERVICE`; `IsLoaded()` is true and `Loaded` never fires | class-scoped `BindToClose` → MVP5 | `PlaceId`, `GameId`, `JobId` and the other place-metadata members | — |
| containers | `ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `StarterPlayer` tree nodes | — | — | — |
| `Lighting` | structural service/tree node | — | `ClockTime`, `Ambient`, `GeographicLatitude` | — |
| `UserInputService` | `InputBegan`, `InputEnded`, `InputChanged`; `MouseBehavior`; `IsKeyDown`, `GetKeysPressed`, `GetMouseLocation`; signal `Wait` | — | — | — |
| `RunService` | `PreAnimation`, `PreSimulation`, `PostSimulation`, `PreRender`; legacy `Heartbeat`, `Stepped`, `RenderStepped`; `IsServer`, `IsClient`, `IsRunning`, `IsStudio`; signal `Wait` | `BindToRenderStep`, `UnbindFromRenderStep` → MVP2 | — | — |
| `ClickDetector` | `MouseClick`, `MaxActivationDistance`; signal `Wait` | — | `MouseHoverEnter`, `MouseHoverLeave` — they exist as signals but never fire: the click pump tracks no hovered part across frames | — |
| `InputObject` | read-only `KeyCode`, `UserInputType`, `UserInputState`, `Position`, `Delta` | — | — | — |
| `RBXScriptSignal` | deferred `Connect`/`Once`/`Wait` on every shipped signal; handlers may yield with `task.wait` | — | — | — |
| `RBXScriptConnection` | read-only `Connected`; `Disconnect()` | — | — | — |

The concurrently wired end state is now verified: `GetService`/`FindService` are scoped to
ServiceProvider, `BindToClose` to DataModel, and `Workspace.SignalBehavior` reads Deferred while
writes resolve through the catalog's deliberate-unsupported status.

Datatypes — **pure Roblox math, spec-exact (LOCKED)**: right-handed, `LookVector = -Z`; all
constructors/operators per official docs, validated by golden fixtures (§5.1.1). No Unity type
appears in any datatype signature.

- `Vector3.new(x, y, z)`, `.zero`, `.one`, `.xAxis/.yAxis/.zAxis`, `X Y Z`, `Magnitude`, `Unit`,
  `:Dot`, `:Cross`, `:Lerp`, `+ - * /` (scalar and component-wise per Roblox).
- `CFrame.new()`, `.new(x,y,z)`, `.new(pos)`, `CFrame.lookAt(pos, target, up?)`,
  `CFrame.Angles(rx,ry,rz)`, `CFrame.fromEulerAnglesXYZ`, `Position`, `LookVector`, `RightVector`,
  `UpVector`, `*` (CFrame·CFrame, CFrame·Vector3), `:Inverse()`, `:ToWorldSpace()`,
  `:ToObjectSpace()`, `:GetComponents()`.
- `Color3.new(r,g,b)` (0..1), `Color3.fromRGB(0..255)`, `Color3.fromHSV`, `Color3.fromHex`.
- `UDim.new(scale, offset)`, `UDim2.new(xs, xo, ys, yo)`, `UDim2.fromScale`, `UDim2.fromOffset`.
- `Enum.<Type>.<Item>` registry: `Material`, `PartType` in MVP1; each Enum item has `.Name`,
  `.Value`, `.EnumType`; unknown enum access = loud stub naming the phase that adds it.

#### 5.1.4 Semantics where Unity differs from Roblox (decisions D1–D10)

- **D1 — Pure-spec datatypes. LOCKED.** `Vector3`/`CFrame` are pure math exactly to Roblox spec:
  right-handed coordinate system, `LookVector = -Z`, every constructor/operator/`lookAt`/
  `ToWorldSpace`/`ToObjectSpace` matching the official reference. Mods never touch a Unity
  `Transform`. Scripts that hand-build rotation matrices from raw components therefore work
  unmodified. Golden-fixture tests pin the behavior to documented Roblox values (§5.1.1).
- **D2 — One conversion boundary: `RbxSpace`. LOCKED.** A single static class owns
  `ToUnity`/`FromUnity` for position/rotation/CFrame/velocity: the canonical handedness flip
  (negate Z position + the matching quaternion adjustment) so Roblox `LookVector` (−Z) maps onto
  Unity `transform.forward` (+Z). Nothing outside `RbxSpace` converts — enforced by
  `Mvp1ConversionLintEditModeTests`, a lint-style test scanning the API layer for direct Transform math
  (the tripwire against scattered double conversions, which are this design's primary failure
  mode). Reading existing Unity scene objects (world wrap, §3.3) goes through the same
  converter's inverse, so the mapping is consistent both ways. Documented visible artifact:
  **mod-space `z` = −Unity `z`** — stated in the skill and in world-query tool docs.
- **D3 — Scale: configurable, default 1 stud = 0.28 m. LOCKED (supersedes the earlier 1:1
  default).** One constant inside `RbxSpace` (`MetersPerStud`); positions, velocities, and
  gravity all flow through it. 0.28 is the default because the AI's trained priors
  (`WalkSpeed 16`, `JumpPower 50`, `Gravity 196.2`, part sizes) then produce correct game feel
  without re-teaching — feel-parity, not just math-parity (196.2 studs/s² × 0.28 = 54.9 m/s²
  ≈ 5.6 g, the intended snappy Roblox feel). 1 stud = 1 m stays available for meter-integrated
  games. `Workspace.Gravity` defaults to `196.2` studs/s² and is applied **per body**
  (`Rigidbody.useGravity = false` + custom force; DEV-6) so the host game's `Physics.gravity`
  is never touched. **Asset rule (LOCKED)**: only numbers convert — the binder scales unit
  primitives by `Size × MetersPerStud`; the only stud-authored assets are our normalized
  1-unit-=-1-stud meshes for Wedge/CornerWedge/oriented Cylinder; meter-authored prefabs are
  never rescaled and read back through the meters→studs inverse; switching 0.28 ↔ 1:1 touches
  zero assets, only the constant. Part mass/density scales by volume (×scale³). Corpus tests run
  at 0.28 primary + 1:1 smoke (§6.4); the skill teaches the active scale as a named "common
  mistakes" entry (§MVP18).
- **D4 — Signal mode: Deferred only. LOCKED (DEV-2).** Handlers never run inside the C# mutation
  that fired them; they are queued and drained at defined resumption points per R5.4–R5.7
  (§5.2.3), matching Roblox's deferred-signal direction (templates default to Deferred [^10]).
  `Workspace.SignalBehavior` reads `Enum.SignalBehavior.Deferred`; setting it is a loud stub
  ("Immediate mode is not planned; restructure with task.defer if you need ordering"). Deferred
  is also what makes budget enforcement sane: each drained handler runs under its own
  `IExecutionBudget` slice, so one storming signal cannot re-enter and stall the host mutation.
- **D5 — `Parent = nil` and materialization.** An instance with `Parent == nil` (fresh
  `Instance.new`, or explicitly detached) exists only in the registry: no GameObject, no physics,
  no rendering, no world-query visibility — mirroring Roblox, where unparented instances are not
  simulated. The binder materializes the GameObject when the instance first enters the
  `workspace` subtree and *deactivates* (not destroys) it when detached, so re-parenting is
  cheap. As built, the whole parented DataModel materializes, but only the Workspace subtree is
  active: `Lighting`, `Players`, the storage services and parts directly under `game` materialize
  inactive (no physics, no rendering), and the flag is recomputed on every re-parent. A part's
  children live in a `"<Part> (children)"` container with no scale or rotation, so a nested part does
  not inherit its parent's scale or join its compound collider.
- **D6 — `Destroy()` vs Unity `Object.Destroy` timing.** `instance:Destroy()` follows R6.2
  (atomicity and ordering), concretely: (1) `Destroying` is **enqueued** on the deferred queue —
  handlers do *not* run at the fire site, (2) Parent set to nil and **locked** — any later
  `.Parent =` raises `PARENT_LOCKED` ("The Parent property of X is locked, use a new
  Instance instead"), (3) all connections on its signals disconnect (pending invocations per
  R5.7), (4) children destroy recursively, (5) registry record unregistered, (6) GameObject
  destroyed via Unity `Object.Destroy` (takes effect end-of-frame — invisible to Lua because
  every Lua observation path goes through the registry). Per **R5.8**, the queued
  `Destroying`/`AncestryChanged` handlers run at the **next resumption point after destruction
  completes** and observe post-destruction state: `Parent == nil`, connections gone. Inside
  those destruction-queued handlers the instance reads as a **tombstone** (DEV-7): `Name`,
  `ClassName`, `Parent` (nil) stay readable; everywhere else, any member access on a destroyed
  instance raises `INSTANCE_DESTROYED` with the id and destruction site.
- **D7 — `WaitForChild(name, timeOut?)`.** With the child present: returns immediately (shipped in
  MVP1). When absent, MVP2 adds the calling-thread yield, the no-timeout overload's Roblox-style
  warning `Infinite yield possible on 'workspace:WaitForChild("X")'` after 5 s while it keeps
  waiting, and the timeout overload returning `nil` after `timeOut` seconds [^3].
- **D8 — `Clone()`** deep-copies the subtree, skipping `Archivable == false` nodes (returns nil
  if the root itself is non-archivable, Roblox parity); the clone's `Parent` is nil; attributes
  and tags copy; new `InstanceId`s are allocated (identity is never cloned). References inside the
  cloned subtree are remapped: `Model.PrimaryPart` and `ObjectValue.Value` point at the copies (a
  reference outside the subtree is kept), and `WorldPivot` is kept. `game:Clone()`, a service's
  `Clone()` and `player:Clone()` return nil. Traversals (`FindFirstChild` recursive,
  `GetDescendants`, `Clone`, `Destroy`) are iterative, and the live tree is capped at the snapshot
  depth (2,048 levels) — a deeper `Parent` assignment is `BAD_ARGUMENT`.
- **D10 — `BasePart.Rotation` uses XYZ Euler order (INFERRED).** The offline Roblox creator docs
  describe only degrees around three axes; the `Rotation` API-dump entry supplies
  `Vector3`/`NotReplicated` but no order.
  CoreAI therefore uses XYZ (`CFrame.Angles`/
  `CFrame.fromEulerAnglesXYZ`) because `Rotation` predates `Orientation`, whose order is explicitly
  documented as YXZ. This is an inference from an undocumented Roblox detail, not verified parity;
  re-check it if the mirror or a behavioral fixture gains a normative answer.

(D9 — the clock model — lives in §5.2.6 with the scheduler it governs.)

#### 5.1.5 Marshalling rules

- Datatypes cross the seam **by value** as tagged userdata with metatables providing operators
  and members; `tostring(v)` matches Roblox formatting (`"1, 2, 3"` for Vector3) because corpus
  scripts string-match on it.
- Instances cross **by reference**: the Lua object is a thin proxy holding `InstanceId`; property
  access resolves through the registry each time (this is what makes `INSTANCE_DESTROYED`
  reliable and hot-reload-safe).
- `nil`/boolean/number/string pass through natively. Tables only cross at explicit boundaries
  (attributes reject tables — Roblox parity; DataStore and remotes JSON-marshal them via
  `RobloxJson`, §5.2.4).

#### 5.1.6 Loud-stub inventory (MVP1) and TODO wording

Every stub throws via the shared formatter (§5.2.7) with code `NOT_IMPLEMENTED`. The inventory
records its C# marker; scheduled stubs normally use `// TODO: MVP<n> — ...`.

| Status | Surface | Phase / meaning | Source |
|---|---|---|---|
| shipped | `PVInstance:PivotTo/GetPivot`; `Model.PrimaryPart/WorldPivot` | landed in the MVP2 Model-pivot slice; no longer a stub | `ClassCatalog` |
| shipped | `WorldRoot:Raycast`; `Workspace.Gravity` | landed in MVP8 slice 8.5: `Raycast(origin, direction, raycastParams?)` with the mirror's 15,000-stud cap, `Gravity` 196.2 studs/s² per-body | `ClassCatalog` |
| shipped | `Workspace:GetServerTimeNow` | landed in MVP2: epoch seconds over `IRbxClockSource` plus the bridge's server offset on a client; monotonic after a client's first synchronization, backward corrections slewed, and held while the server's own clock holds (D9 table) | Lua binding |
| shipped | `BasePart.Material` | landed: all 45 `Enum.Material` items render, unmapped ids resolve to a magenta diagnostic material ([research](../../dev-docs/MATERIALS_RESEARCH.md)) | `ClassCatalog` |
| shipped | `MaterialVariant` / `MaterialService` | landed in 7.10.0: `Instance.new("MaterialVariant")` parented to `MaterialService`; parts wear one via the string property `BasePart.MaterialVariant` (`""` for none); an unknown name renders the plain `Material`, not an error | `ClassCatalog` |
| planned | `RunService:BindToRenderStep`/`UnbindFromRenderStep` | MVP2; the four topology queries (`IsServer`/`IsClient`/`IsStudio`/`IsRunning`) and the modern frame events (`PreAnimation`/`PreSimulation`/`PostSimulation`/`PreRender`) have shipped | `ClassCatalog` |
| backlog | BasePart velocity, mass, collision-query, physical-properties, and legacy-surface members listed in §5.1.3 | known member; no rung assigned | `ClassCatalog` |
| backlog | `Lighting.ClockTime/Ambient/GeographicLatitude` | known member; no rung assigned | `ClassCatalog` |
| unsupported | `Workspace.Terrain` | deliberate non-goal; use Parts | `ClassCatalog` |
| shipped | absent-child `WaitForChild` | landed: scheduler yield, 5 s infinite-yield warning, timeout overload | Lua binding |
| planned | `game:BindToClose` | MVP5 | DataModel binding |
| backlog | `Instance.fromExisting` | not scheduled; use `Clone()` | Lua binding |
| backlog / unsupported | `Instance.new` of about ninety known Roblox classes (parts, meshes, constraints, body movers, `Tool`, `Sound`, …) | each class names its status and a workaround instead of "Unable to create" | `ClassCatalog` |
| backlog / unsupported | 28 known Roblox services (`StarterGui` → MVP14; `Teams`, `PhysicsService`, `ReplicatedFirst`, … backlog; platform services such as `BadgeService`, `TeleportService`, `TextChatService` unsupported) | a loud placeholder instead of `UNKNOWN_SERVICE` | `ServiceCatalog` |
| backlog | extended member tables of `Instance`, `Model`, `WorldRoot`, `Camera`, `DataModel`, `BasePart` (`Model:MoveTo`, spatial queries, collision groups, `Camera.FieldOfView`, place metadata, mass/impulse members, CSG) | known member; no rung assigned | `ClassCatalog` |

The concurrent end state moves `Workspace.SignalBehavior` out of the catalog's planned row: reads
return Deferred, while writes are deliberately unsupported per D4.

`WorldRoot:Raycast`/`Touched`/`TouchEnded` shipped in MVP8 slice 8.5 but stayed silently broken
across a **runtime** world reload until a third audit round (2026-09-09) traced it: the Rbx API
handed to a newly loaded world stayed wired to the physics port the load was about to dispose,
instead of the one it had just published, so every raycast against the second (and any later)
world missed and no contact signal fired again — with nothing in the log. Fixed by attaching the
post-publish port after the world commits rather than while it is still staging
(`RbxWorldRuntimeSessionController.LoadConfirmedAsync`); see `TODO.md`, "Third review round —
2026-09-09".

#### 5.1.7 Risks and mitigations

| Risk | Mitigation |
|---|---|
| **Scattered coordinate conversions** — a second ad-hoc Z-flip or scale factor sneaks in somewhere (double conversion = subtly mirrored/mis-scaled worlds; the classic failure mode of this design) | single boundary rule (D2) enforced mechanically by `Mvp1ConversionLintEditModeTests`; property-based round-trip tests catch asymmetry; golden fixtures catch chirality regressions |
| Registry proxy indirection too slow for hot loops (`part.Position` per frame) | property access resolves via a cached record reference invalidated on destroy, not a dictionary hit per call; benchmark in MVP1 tests, budget: ≤1 µs/access editor-Mono |
| Class hierarchy sprawl | class ancestry is data (`ClassCatalog` table: name → parent, creatable flag), not C# inheritance depth; adding a class = one row + optional behavior class; the same catalog feeds the API manifest (§MVP18) |
| GameObject leak on mod crash | binder subscribes to `InstanceRegistry.Unregistered` *and* hot-reload teardown enumerates `GetOwnedBy(modId)` — two independent sweeps |
| Divergent `tostring`/format breaking string-matching scripts | corpus fixtures assert formatting; formatting rules centralized in one class |

#### 5.1.8 Acceptance criteria (MVP1 test list, EditMode; names per §6.6)

1. `Instance.new("Part")` → ClassName/Name defaults; registry has record; no GameObject yet (D5).
2. Parent into `workspace` → GameObject appears with the `RbxSpace`-converted transform;
   detach → deactivated.
3. Navigation: `FindFirstChild` (+recursive), `FindFirstChildOfClass/WhichIsA`, ancestor trio,
   `GetChildren` order = insertion order, `GetDescendants` preorder.
4. `IsA("BasePart")`, `IsA("Instance")` true for `Part`; false cases. (The hierarchy is rooted at
   `Object`; `Part` is a `FormFactorPart`, and `Camera` is a `PVInstance`.)
5. `Clone` deep-copies, respects `Archivable = false`, allocates fresh ids (D8).
6. `Destroy` sequence per D6/R6.2 incl. `PARENT_LOCKED` and `INSTANCE_DESTROYED` on later access.
7. Attributes: set/get/enumerate; wrong types rejected with `BAD_ARGUMENT` naming the type.
8. Tags: add/remove/has/list.
9. Datatypes: operator table (Vector3 arithmetic, CFrame composition), `tostring` formats,
   `Color3.fromRGB` of whole channels — golden fixtures against documented Roblox values; a
   fractional channel follows CoreAI's locked nearest-integer, midpoint-away-from-zero rounding,
   because the mirror documents none (`Color3_FromRGB_FractionalChannelsUseLockedCoreAiRounding`).
10. `RbxSpace` suite (§5.1.1): round-trip identity at 0.28 and 1:1; lookAt/Angles chirality
    goldens; usage lint clean; mod-space z = −Unity z asserted explicitly.
11. Asset-scale rule: `Part` with `Size = Vector3.new(4, 1, 2)` produces
    `localScale = (4, 1, 2) × MetersPerStud` — asserted under **both** scale configs with zero
    asset differences (only the `RbxSpace` constant changes); wedge/corner-wedge meshes obey
    the same formula. Holds for a part nested in a part too: the child container carries no scale.
12. Identity: `TryGetByWorldName` resolves a CoreAI world object lazily wrapped (position equals
    the `RbxSpace` inverse of its Unity position — a 1.8 m-tall host object reads ~6.4 studs
    at default scale); same record via `TryGet(id)`. **PARTIAL** for writes: an adopted host
    object follows pose writes only — the binder never changes the scale, shape, material or
    Rigidbody of a GameObject the host owns (M1-16).
13. Every stub in §5.1.6 raises `NOT_IMPLEMENTED` with mod id, line, and phase name.
14. `Instance.new("Part", parent)` works and logs the deprecation note exactly once per mod.
15. `InstanceId` authority partition (§3.3): server-assigned and locally-assigned ids are
    distinguishable by the authority bit; the allocator never collides the two spaces; a
    locally-assigned id is rejected by the (future) wire-marshal path.

### 5.2 MVP2 — Scheduler, signals, clocks, services framework

#### 5.2.1 Task breakdown

Every task has landed except the `DateTime` part of task 4 (a loud backlog stub, M2-15) and two
members of task 5 (`BindToRenderStep`/`UnbindFromRenderStep`, MVP2-phased loud stubs; §MVP2 lists them
as open). The RunService topology queries answer through `IRbxRuntimeTopology`; the Tier-A corpus
gate (task 11) is green.

| # | Task | Planned file (first draft) | Shipped in (`Assets/CoreAIMods/Runtime/`) |
|---|------|----------------------------|--------------------------------------------|
| 1 | `RbxScriptSignal` / `RbxScriptConnection` + deferred queue (R5.x) | `RobloxApi/Events/RbxScriptSignal.cs` | `RbxApi/Instances/RbxScriptSignal.cs`, `ModConnectionRegistry.cs`; `Scripting/LuaCs/LuaCsRbxSignalRunner.cs` |
| 2 | `ModScheduler`: phases, thread pool over `IScriptCoroutine`, wait/delay heaps (R4.x), `ScheduleWaitUntil` completion primitive | `RobloxApi/Scheduling/ModScheduler.cs` | `RbxApi/Instances/Scheduling/ModScheduler.cs`, `RbxSchedulerCompletion.cs` |
| 3 | `task` library + legacy aliases (`wait`, `spawn`, `delay`) | `RobloxApi/Scheduling/TaskLibrary.cs` | `Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs`, `LuaCsRbxApiBindings.cs` |
| 4 | Clock surface (D9): `time`, `os.time`, `os.clock`, `DateTime`, `GetServerTimeNow` — landed on `IRbxClockSource` except `DateTime` (a loud backlog stub, M2-15; no `RbxDateTime` exists) | `RobloxApi/Scheduling/RbxClocks.cs`, `RobloxApi/Datatypes/RbxDateTime.cs` | `RbxApi/Datatypes/IRbxClockSource.cs`; `Scripting/LuaCs/LuaCsRbxApiBindings.cs` |
| 5 | `RunService` service + tick-driver wiring | `RobloxApi/Services/RunServiceImpl.cs`; edit `Infrastructure/LuaModRuntimeTickDriver.cs` | `RbxApi/Instances/RbxRunService.cs`, `RbxRuntimeTopology.cs`; `Infrastructure/LuaModRuntimeTickDriver.cs` |
| 6 | `ServiceCatalog`: `GetService`, registration, stub factory | `RobloxApi/Services/ServiceCatalog.cs` | `RbxApi/Instances/ServiceCatalog.cs` |
| 7 | Shared JSON contract + `HttpService` JSON members | `RobloxApi/Marshalling/RobloxJson.cs`, `RobloxApi/Services/HttpServiceImpl.cs` | `Scripting/LuaCs/LuaCsRbxJson.cs`, `LuaCsRbxHttpServiceAdapter.cs`, `RbxHttpPolicy.cs` |
| 8 | `INetworkBridge` + `NullNetworkBridge` + `RemoteEvent`/`UnreliableRemoteEvent`/`RemoteFunction` loopback | `RobloxApi/Networking/INetworkBridge.cs`, `NullNetworkBridge.cs`, `RobloxApi/Instances/RemoteEvent.cs` | `RbxApi/Instances/Networking/INetworkBridge.cs`, `NullNetworkBridge.cs`, `RbxRemotes.cs`; `Scripting/LuaCs/LuaCsRbxNetworkCodec.cs` |
| 9 | Absent-child `WaitForChild` yield, 5 s warning, and timeout overload (the immediate-child path shipped in MVP1); `signal:Wait()`; `Destroying`-order guarantees | edits in `RbxInstance` | `RbxApi/Instances/RbxInstance.cs`; `Scripting/LuaCs/LuaCsRbxInstanceBindings.cs` |
| 10 | Error formatter finalized + budget-kill integration | `RobloxApi/RbxError.cs`; edits `Scripting/LuaCs/LuaCsExecutionGuard.cs` adapter | `RbxApi/Instances/RbxError.cs`; `Scripting/LuaCs/LuaCsExecutionGuard.cs` |
| 11 | Tier-A corpus harness (20 fixtures) | `Tests/EditMode/RobloxApi/Corpus/` (+`Fixtures/`) | `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/` (`Fixtures/`, `FixturesB/`, `TierACorpusEditModeTests.cs`) |
| 12 | `BasePart.Material` materials catalog, following the [materials research](../../dev-docs/MATERIALS_RESEARCH.md) — **landed** as `RbxProceduralMaterialProvider` + `RbxTextureMaterialProvider` (all 45 items, magenta fallback) | — | `RbxApi/Unity/Rbx*MaterialProvider.cs`; part binder + Lua bindings |
| 13 | `Model` pivot — **landed**: `PivotTo`/`GetPivot` aggregating child-part CFrames, plus `PrimaryPart`/`WorldPivot` | `RbxApi/Instances/RbxModel.cs` | `RbxApi/Instances/RbxInstance.cs`; `Scripting/LuaCs/LuaCsRbxInstanceBindings.cs` |

#### 5.2.2 Public C# API sketch

```csharp
// Events/RbxScriptSignal.cs
// Seam value convention (the LANDED Runtime/Scripting contract): script values cross as opaque
// `object` handles classified by IValueMarshaller.GetKind (ScriptValueKind); handlers are the
// engine's opaque callable handle (the same `callable` IScriptEngine.CreateCoroutine takes);
// table arguments read via IScriptTable; host callbacks return ScriptCallResult. There is no
// ScriptValue / IScriptFunctionRef type.
public sealed class RbxScriptSignal
{
    public RbxScriptConnection Connect(object callable);           // engine callable handle
    public RbxScriptConnection Once(object callable);              // auto-disconnect after first fire
    public object[] Wait();                                        // yields calling Lua thread; raw seam values
    internal void Fire(params object[] args);                      // enqueue on the deferred queue
    internal void DisconnectAll();                                 // Destroy / teardown path
}

public sealed class RbxScriptConnection
{
    public bool Connected { get; }
    public void Disconnect();                // pending-invocation semantics per R5.7
    public string OwnerModId { get; }        // teardown bookkeeping (§6.3)
}

// Scheduling/ModScheduler.cs
public enum SchedulerPhase { PreSimulation, PostSimulation, Heartbeat, PreRender }

public interface IRbxScriptThreadTerminalFault   // a thread that can name the fault that ended it
{
    RbxError TerminalFault { get; }
}

public sealed class ModScheduler
{
    // task library backing — signatures mirror Roblox task.* [^1]; all timing on SCALED game time (D9)
    // `callable` = the engine's opaque callable handle; args = raw seam values (object handles)
    public IScriptCoroutine Spawn(object callable, object[] args);   // resume now
    public IScriptCoroutine Defer(object callable, object[] args);   // next resumption point (R4.8)
    public IScriptCoroutine Delay(double seconds, object callable, object[] args);
    public void Cancel(IScriptCoroutine thread);
    // called from a yielded Lua thread; returns actual elapsed (scaled) on resume
    public double ScheduleWait(IScriptCoroutine caller, double seconds);
    // generic completion primitive (§2, AI-call reservations): resumes `caller` at the next
    // resumption point after the host Task/callback completes. ScheduleWait is the time special
    // case; DataStore GetAsync (MVP9) and the future agent:Ask ride this same path.
    public void ScheduleWaitUntil(IScriptCoroutine caller, System.Threading.Tasks.Task completion);

    // host pump — called by LuaModRuntimeTickDriver (frame mapping §5.2.3, order per R4.2)
    public void RunPhase(SchedulerPhase phase, double deltaSeconds);

    public event Action<string /*modId*/, RbxError> ThreadFaulted;   // handler faults of owned connections,
                                                                      // SIGNAL_CASCADE (firing mod), BUDGET_EXCEEDED,
                                                                      // dead-thread resumes
    public event Action<string /*source*/, Exception> HostFaulted;   // faults no mod owns (host callbacks, ownerless
                                                                      // handlers, throwing subscribers)
    public event Action<string /*modId*/, bool /*completed*/> ThreadResumeSucceeded;
    public const int DefaultMaxSignalInvocationsPerOwner = 16384;   // per resumption point
    public const int DefaultMaxQueuedSignalInvocations = 65536;
    public void ConfigureSignalBudget(int maxInvocationsPerOwner, int maxQueuedInvocations);
    public void ScheduleHostCallback(double seconds, Action callback); // a throwing callback goes to HostFaulted;
                                                                        // the rest of the slot still runs
}

// Services/ServiceCatalog.cs
public sealed class ServiceCatalog
{
    public void Register(string serviceName, RbxInstance service);
    public void RegisterStub(string serviceName, string plannedMvp, string workaroundHint);
    /// <summary>Unknown name throws UNKNOWN_SERVICE ("X is not a valid Service name").
    /// A registered stub RETURNS a StubService object; the error fires on first member
    /// access, so the failure points at the usage line, not the GetService line.</summary>
    public RbxInstance GetService(string serviceName);
    /// <summary>Feeds get_api_surface and the MVP6 manifest generator.</summary>
    public IReadOnlyList<ServiceSurfaceEntry> DescribeSurface();
}

// Networking/INetworkBridge.cs — full surface now, loopback impl now, Mirror in MVP11 (host)
// and MVP13 (dedicated). Topology-explicit: NOTHING here may assume the server has a local client.
public enum NetworkTopology { Solo, Host, DedicatedServer, Client }

public interface INetworkBridge
{
    NetworkTopology Topology { get; }      // NullNetworkBridge: Solo
    bool IsServer { get; }                 // Solo/Host/DedicatedServer
    bool IsClient { get; }                 // Solo/Host/Client — false on DedicatedServer
    void FireServer(InstanceId remote, byte[] payload);                    // client → server
    void FireClient(InstanceId remote, int playerId, byte[] payload);      // server → one client
    void FireAllClients(InstanceId remote, byte[] payload);                // server → all
    void FireServerUnreliable(InstanceId remote, byte[] payload);
    void FireClientUnreliable(InstanceId remote, int playerId, byte[] payload);
    void FireAllClientsUnreliable(InstanceId remote, byte[] payload);
    // RemoteFunction: request/response with correlation id; loopback resolves next drain
    void Invoke(InstanceId remote, int targetPlayerId, byte[] payload, uint correlationId);
    void Respond(InstanceId remote, int targetPlayerId, uint correlationId, byte[] payload);
    event Action<InstanceId, int /*fromPlayerId*/, byte[]> ServerEventReceived;
    event Action<InstanceId, byte[]> ClientEventReceived;
    // receive side of Invoke: the handler host answers via Respond with the same correlationId
    event Action<InstanceId, int /*fromPlayerId*/, uint /*correlationId*/, byte[]> InvokeReceived;
    event Action<InstanceId, uint /*correlationId*/, byte[]> ResponseReceived;
    int LocalPlayerId { get; }             // solo: 1; dedicated server: 0 (no local player)
    double ServerTimeNow { get; }          // D9: backs workspace:GetServerTimeNow()
}
```

#### 5.2.3 Frame pipeline: Unity ↔ Roblox mapping (order per R4.2)

`LuaModRuntimeTickDriver` (exists; today it only pumps `ILuaModRuntime.Tick`) grows three hook
points. Order within one Unity frame:

| Unity callback | Scheduler work, in order | Roblox event fired |
|---|---|---|
| `FixedUpdate` (0..n per frame) | drain deferred queue; fire phase | `RunService.PreAnimation(dt)` immediately before `PreSimulation(dt)` (cheap alias in the same slot — avoids a stub) (+ legacy `Stepped(time, dt)` alias) |
| `Update` | drain deferred queue → fire `PostSimulation(dt)` → **resume expired `task.wait`/`task.delay` threads (before Heartbeat, per R4.2/R4.11)** → fire `Heartbeat(dt)` | `RunService.PostSimulation(dt)`, then `Heartbeat(dt)` |
| `LateUpdate` | fire phase → drain deferred queue | `RunService.PreRender(dt)` (+ legacy `RenderStepped` alias) |

Notes:

- **What is actually implemented, 2026-09-06.** The three-callback split below is the design; the
  shipped driver (`LuaModRuntimeTickDriver`) pumps every RunService phase from a single `Update()`
  through `LuaCsRbxApiBindings.PumpFrame`, in the order PreAnimation → PreSimulation (+ legacy
  `Stepped`) → PostSimulation → Heartbeat → input → PreRender (+ legacy `RenderStepped`), and uses
  `FixedUpdate()` only to open the physics step and apply gravity. The observable ORDER matches the
  table; the callback each phase is fired from does not. That matters for one thing only — a phase
  fired from `Update` runs once per rendered frame, not once per simulated step — and it is
  recorded here rather than left for the next reader to discover.
- Roblox fires `PostSimulation`/`Heartbeat` after physics; Unity's physics step happens inside
  the FixedUpdate loop, so firing them from `Update` preserves the "after simulation" contract.
  `PreRender`/`RenderStepped` fire only in a topology that renders — a dedicated server never runs
  them (Roblox parity: `PreRender` is client-only). The gate is `IRbxRuntimeTopology.RendersFrames`,
  **not** `IsClient`: CoreAI's solo process both renders and is the server, and `IsClient` stays
  false there on purpose, so gating on it would silently kill every solo game's per-frame render
  handler.
- All `dt` arguments are **scaled** game-time deltas (D9): `Time.deltaTime` /
  `Time.fixedDeltaTime`. At `Time.timeScale = 0` the FixedUpdate row stops entirely and wait
  heaps freeze; the deferred queue still drains (so UI-ish mods stay responsive during pause).
- `task.defer` is **not** part of the delayed-threads slot: deferred threads resume at the end
  of the current resumption point (R4.8) — i.e. with the drain that follows whichever slot
  spawned them, never on the wait/delay heap.
- **Deferred dispatch follows R5.4–R5.7**: drain points = the table rows above plus "after each
  batch of resumed threads" (matching Roblox's resumption points [^10]); handlers that fire more
  signals queue for the *same* drain up to the re-entrancy cap of **10 generations per R5.6**,
  then `SIGNAL_CASCADE` — loud, with the offending chain in the message; the
  Disconnect-vs-Destroy pending-handler asymmetry is exactly R5.7.
- **Fault containment (as built).** `ModScheduler.Advance` contains every fault: a throwing handler,
  a cascade, a resume of a dead thread, a throwing host callback or `Wait`-timeout factory drops only
  the guilty chain. A cascade is a fault of the mod that fired; a fault is attributed to its mod
  through `ThreadFaulted`, and one no mod owns goes to `HostFaulted`. The other phases and mods run
  on in the same frame, and an unobserved fault is rethrown only after the whole frame.
  `PhaseReached`/`ThreadFaulted` subscribers are contained one by one. A width budget caps the fan-out:
  16,384 handler invocations per owner and 65,536 queued per resumption point, beyond which the rest
  are dropped with `BUDGET_EXCEEDED`. A `task.defer` issued by a handler runs in the same resumption
  point (at most 10 drain rounds). The tick driver contains `Advance` and `Tick` separately. A
  runtime composed with a logger logs each distinct ownerless fault once (at most 64, then one summary
  line); without a logger the scheduler still rethrows.
- **Budgets (DEV-3)**: each resumed thread / drained handler runs under `IExecutionBudget`
  (`LuaCsExecutionGuard` semantics: instruction + wall-clock caps per slice, defaults from
  `LuaCsCoroutineHandle`: 10k steps / 500 ms, tuned down per phase; since 7.39.0 both halves are
  the GAME's — one serialized `LuaCsCoroutineBudgetSettings` on `CoreAiModsLifetimeScope`, resolved
  by every coroutine site and re-read on every resume, with `ScriptContext:SetTimeout(seconds)`
  moving the wall-clock half live for the host actor per DEV-14 and the instruction half staying
  composition-only because Roblox has no scriptable equivalent). Breach kills that
  thread only, logs `BUDGET_EXCEEDED` with the mod/site, and counts toward the mod's quarantine
  streak (existing `ModHandlerErrored` flow → **quarantine** policy, §2). For scheduler threads the
  streak counts faulting frames: any number of faults in one frame counts once, a frame whose threads
  ran cleanly resets it, idle frames change nothing, and quarantine is decided at the end of `Tick`. Budget
  wall-clocks are always **unscaled** real time — `timeScale = 0` must not grant infinite
  budgets. The budget interfaces carry **per-frame/per-mod slice accounting**
  (skip/downgrade + `BUDGET_EXCEEDED` warning) from MVP2 even though slice *enforcement* lands
  in MVP17 (§2, frame-budget reservation).

#### 5.2.4 Lua-visible surface (MVP2)

- `task.spawn(fn | taskHandle, ...) → taskHandle`; `task.defer(fn | taskHandle, ...) → taskHandle`
  (semantics per R4.8); `task.delay(duration, fn | taskHandle, ...) → taskHandle`;
  `task.wait(duration = 0) → elapsed`; `task.cancel(taskHandle)`. `task.synchronize`/
  `task.desynchronize`: **no-op +
  once-per-mod log note** (deviation DEV-5). `math.huge` as a `task.wait`/`task.delay`/`WaitForChild`
  duration parks the thread until it is cancelled or its mod unloads; a NaN duration raises
  `duration must be a number, not NaN`; `task.cancel` on a finished thread is a no-op.
  As built (M2-14, 2026-09-24): `task.spawn`/`task.defer`/`task.delay` take back a handle the same
  mod's `task.*` call returned, and `task.spawn(t) == t`. A parked thread (suspended by a native
  `coroutine.yield`) resumes; a deferred or delayed one is detached from its old slot, which never
  fires; the running thread may re-queue itself through `task.defer`/`task.delay` only. A dead
  thread, a thread suspended in a scheduler wait, another mod's or another scheduler's thread, and
  the running thread passed to `task.spawn` are refused with `BAD_ARGUMENT`.
- **Native `coroutine.yield` and nested coroutines (M2-06, M2-19, M2-20).** A native
  `coroutine.yield` in a task thread parks it for `task.spawn(t, ...)`, and those arguments are what
  `coroutine.yield` returns; in a signal handler, a `RemoteFunction` callback, a legacy
  `spawn`/`delay` function or the main chunk nothing could resume it, so the thread is stopped with
  `CONTEXT_VIOLATION` (reported through `IRbxScriptThreadTerminalFault`), and `THREAD_CAP` names
  how many parked threads hold slots. `task.wait`, `signal:Wait`, `WaitForChild` and a
  `RemoteFunction` invoke from inside a `coroutine.create` coroutine raise `CONTEXT_VIOLATION`
  instead of suspending the outer task thread; a yield inside a `string.format`/`gsub` callback no
  longer poisons the thread's later waits (the unfinished wait is rolled back). Every thread exit
  raises the scheduler's `ThreadRetired` once, which releases its tracking entry, its pending
  `signal:Wait` connection and an unanswered `RemoteFunction` request (M2-07, M2-18).
- **R4.10 native coroutine-library interop is UNSUPPORTED.** A boxed CoreAI task handle may be
  rescheduled/cancelled, but a thread from `coroutine.create()` cannot be accepted or returned:
  `IScriptEngine` cannot wrap an existing native thread, and `IScriptCoroutine` exposes neither
  the native VM value nor a stable native identity. Passing one (or a `coroutine.running()` value)
  to `task.spawn`/`defer`/`delay` is `BAD_ARGUMENT` naming this rule. The other direction is refused
  too: the sandbox's `coroutine.resume` of a task, signal-handler or main-chunk thread (a
  `coroutine.running()` value) returns `false` and "cannot resume a task or signal-handler thread with
  coroutine.resume; …" without touching the thread, because only its handle may resume it (A2-01: a
  budget trip inside such a resume was swallowed by `xpcall` and the rest ran unguarded) — a parked task
  is resumed with `task.spawn(handle)`.
- Legacy aliases: `wait(t)` → `task.wait(t)` but returning the legacy pair
  `(elapsed, time())` per R4.9; `spawn(fn)` → `task.defer` passing the legacy args
  `(elapsedTime, engineUptime)` per R4.9; `delay(t, fn)` → `task.delay` — each logs a
  deprecation note once per mod. All three enforce the **0.029 s floor**; load-dependent
  throttling is deliberately omitted (DEV-9).
- Clock surface per D9 (§5.2.6): `time()`, `os.time()`, `os.clock()`, `tick()` (legacy, with
  deprecation note), `DateTime.now()`/`DateTime.fromUnixTimestamp()`/
  `DateTime.fromUnixTimestampMillis()` (+ `UnixTimestamp`/`UnixTimestampMillis` fields),
  `workspace:GetServerTimeNow()`. As built: `DateTime` is a loud backlog stub (M2-15) whose error
  points at `os.time()` and `GetServerTimeNow()`; `os.time(t)` reads the date table as UTC (Luau's
  choice), `hour` defaulting to 12, out-of-range fields carrying over, a missing `year`/`month`/`day`
  `BAD_ARGUMENT` (M2-22); as in Luau, a field that is not a number counts as missing and a date
  before 1970 returns `nil` (A3-06). The sandbox has no `os.date`, so no round trip through it is
  promised. `typeof` answers Roblox type names and `warn` writes to the mod's log at
  `Warn` (M1-06); `BrickColor`, `NumberSequence`, `ColorSequence`, `NumberRange`, `Ray`,
  `Region3`, `Rect`, `PhysicalProperties`, `OverlapParams` and `shared` are loud stubs too.
- `RunService`: events per §5.2.3; `IsServer()`, `IsClient()`, `IsRunning()` — answered from
  `INetworkBridge.Topology` (solo: both true; dedicated server: server only; pure client: client
  only); `IsStudio()` → false in player, true in editor; `BindToRenderStep(name, priority, fn)` /
  `UnbindFromRenderStep(name)` [^2].
- Signals: `:Connect(fn) → RBXScriptConnection`, `:Once(fn)`, `:Wait() → ...`,
  `connection.Connected`, `connection:Disconnect()` — dispatch per R5.x. All MVP1 Instance
  signals now live (`ChildAdded`, `Destroying`, `GetPropertyChangedSignal`,
  `GetAttributeChangedSignal`, …). Every signal uses the same deferred path: firing only queues
  handlers, which run at the next script-resumption point. Signal handlers are scheduler-owned
  and may call `task.wait()`. Handler order across multiple connections is not guaranteed (R5.11).
  As built: `GetPropertyChangedSignal` and `Changed` fire for engine-free members and for script,
  tween and `PivotTo` writes to parts and the camera; `ConnectParallel` is `Connect` (DEV-5); each
  handler receives its own copy of a table argument (the R5.10 bindable sanitization: no metatable,
  cycle-safe, depth 64, instances rebound); and the ownerless one-off `execute_lua` surface refuses
  `Connect`/`Once`/`ConnectParallel`/`Wait` with `CONTEXT_VIOLATION`, because a connection with no
  owning mod could never be torn down.
- **Shared JSON contract**: `RobloxJson` — one table↔JSON mapping (empty-table→`{}` vs `[]`
  rule, `null` handling, number formatting, string escapes — per the M-doc serialization
  appendix and S-rules) with `HttpService:JSONEncode(value) → string` and
  `HttpService:JSONDecode(input) → Variant` as its Lua face. The DataStore marshaller (MVP9) and
  the remote payload path (below, and Mirror in MVP11) reuse this exact component — one
  serialization semantics everywhere. The rest of `HttpService` (`GetAsync`, `RequestAsync`)
  stays loud-stubbed as not-planned (no open internet egress — Non-goals).
- `game:GetService(name)`: tree-backed services in the standard runtime are `Workspace`,
  `Lighting`, `ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `StarterPlayer`,
  `UserInputService`, `MaterialService`, and `RunService`, plus the catalog's tree-backed
  `HttpService`, `Players`, `Debris`, `TweenService`, `CollectionService` and `ScriptContext`.
  Registered placeholder names also resolve, but return a placeholder whose first member access
  raises `NOT_IMPLEMENTED` with its catalog rung or status: `DataStoreService` (MVP9);
  `ContextActionService` (MVP10); `SoundService` (MVP15); `StarterGui` (MVP14); `AIService`
  (`a future MVP (reserved)`); `PathfindingService`/`MarketplaceService` (unsupported); and, since
  the 2026-09-24 fix wave, 27 further known Roblox services as backlog or unsupported placeholders
  (42 registrations in all). `ServiceCatalog` also retains fallback registrations for `RunService`
  and `UserInputService`, which the normal game tree replaces with the live implementations; a bare
  fallback's error names the missing attachment. Unknown names (and `""`) still raise
  `UNKNOWN_SERVICE` at resolution with the Roblox-shaped message `X is not a valid Service name`.
- Remotes (loopback via `NullNetworkBridge`): `Instance.new("RemoteEvent")` under
  `ReplicatedStorage`; `:FireServer(...)`, `:FireClient(player, ...)`, `:FireAllClients(...)`,
  `OnServerEvent(player, ...)`, `OnClientEvent(...)` [^5]; `UnreliableRemoteEvent` same
  surface [^6]; `RemoteFunction`: `:InvokeServer(...) → ...` (yields), `OnServerInvoke = fn`,
  `:InvokeClient(player, ...)` (yields) + `OnClientInvoke` [^7]. Loopback delivery lands on the
  next deferred drain (never same-stack) so solo behavior matches wire behavior in shape.
  **Payload envelope (from MVP2)**: `RobloxJson` **plus** tagged entries for Roblox datatypes
  (marshalled by value) and `InstanceId` references (resolved via the registry on receive;
  unknown id → `nil` + warn — the same rule MVP11 keeps). MVP11 reuses this identical envelope
  on the wire. Two minimal MVP2-scoped helpers make loopback testable **without** pulling in
  later MVPs: (a) a **harness-level context tag** marking a test script as `server`/`client`
  (not the MVP5 folder layout — just enough for the loopback to route correctly), and (b) a
  **synthetic local Player placeholder** instance (`Name`, `UserId = 1`) handed to
  `OnServerEvent`; MVP8 upgrades it to the real `Player` class.
- `game.BindToClose` remains a loud stub (→MVP5, where it lands as a deliverable with M6.1
  semantics — parallel callbacks, ~30 s flush window).

#### 5.2.5 Semantics notes (MVP2)

- `task.wait(0)`/`task.wait()` resumes on the **next Heartbeat** (one-frame minimum, R4.x);
  returns actual elapsed **scaled** seconds (double).
- `task.spawn` resumes the new thread **immediately** (inside the current resumption), while
  `task.defer` queues it for the current/next drain point (R4.8) — preserving the Roblox
  distinction scripts rely on for ordering [^1].
- `signal:Wait()` for a signal that cannot fire in this context/topology (e.g. a client-only
  signal on a dedicated server, later MVPs) errors rather than deadlocks — context checks run at
  connect/wait time.
- Connection lifetime: a connection dies when (a) `Disconnect()` (pending-invocation behavior
  per R5.7), (b) its instance is destroyed, (c) its owner mod unloads/reloads (§6.3). Firing a
  signal with zero live connections is free (no queue entry).
- Thread lifetime: all threads spawned by a mod are tracked by owner; mod unload cancels them
  (`IScriptCoroutine.Kill` — semantics already implemented in `LuaCsCoroutineHandle`: killed
  threads report Dead and never resume again). Scheduler threads have **no lifetime step cap**: the
  per-resume budget is the only CPU limit, as in Roblox, so `while true do task.wait() end` runs for
  the whole session; memory is budgeted per resume (`EXCEEDED_MEMORY_BUDGET`). A lifetime cap remains
  only on handles built directly (`LuaCsCoroutineHandle.UnlimitedLifetimeSteps` marks its absence),
  and exhausting it fails loudly with `EXCEEDED_LIFETIME_STEP_BUDGET`.
- Reserved but inert in MVP2: every remote payload passes through the `INetworkBridge` byte
  path even in loopback (encode → decode via the §5.2.4 envelope: `RobloxJson` + tagged
  datatype/`InstanceId` entries), so marshalling bugs surface in solo play, not first in MVP11.
- U1–U7 stances (§2.1) are recorded alongside these notes during implementation; testable ones
  become conformance tests in `Tests/EditMode/RbxApi/Scheduling/`.

#### 5.2.6 Clock model (D9, LOCKED)

Game logic runs on scaled time; wall-clock APIs exist under their Roblox names. Mapping:

| Lua API | Semantics (Roblox) | Backing Unity/C# clock | Scaled? |
|---|---|---|---|
| `task.wait` / `task.delay` / tween durations / `Debris` | scheduler time | `Time.time` accumulation via the phase pump | **yes** |
| `RunService` event `dt` args | frame/physics deltas | `Time.deltaTime` / `Time.fixedDeltaTime` | **yes** |
| `time()` | game-simulation time since world start | `Time.time` minus world-start offset | **yes** |
| `workspace:GetServerTimeNow()` | server-synchronized clock, monotonic, **Unix epoch seconds** | `IRbxClockSource` Unix seconds plus `INetworkBridge.ServerClockOffsetSeconds`. Where this process is the server clock (no bridge, `Solo`, `Host`, `DedicatedServer`): the local clock, held at its last reading while that clock steps back; the server world hands that reading, hold included, to its bridge (`INetworkBridge.AttachServerClock`). On a `Client`: the local clock, unsmoothed, until `INetworkBridge.IsServerClockSynchronized`; re-based once at the first synchronization; from then on never decreasing — an estimate ahead is taken at once, one behind is slewed onto at half speed (`ServerTimeSlewRate` 0.5; Roblox's 0.6% would take half an hour for a ten-second correction), and while `INetworkBridge.IsServerClockHeld` says the server's clock holds, the client holds too. The Mirror offset comes from the server's own clock anchors (sent at readiness, every 5 s and at once when the server's clock holds, releases or jumps; corrected by half the round trip; a single anchor more than 1 s behind is set aside until the next agrees), not from `NetworkTime.offset`, which compares process uptimes | no |
| `os.time()` | Unix epoch seconds (UTC), integer; `os.time(t)` reads `t` as UTC and returns `nil` before 1970 | `IRbxClockSource.UnixTimeSeconds` | no |
| `os.clock()` | CPU time for benchmarking | `Stopwatch`-based process time | no |
| `DateTime.now()` etc. | calendar datatype | planned: `System.DateTimeOffset` wrapped as `RbxDateTime`; as built a loud backlog stub (M2-15) pointing at `os.time()`/`GetServerTimeNow()` | no |
| `tick()` (legacy) | epoch seconds with fraction | `DateTimeOffset.UtcNow` fractional epoch; deprecation note once per mod | no |

Rules: pausing (`timeScale = 0`) freezes the first three rows and everything built on them;
budget enforcement always uses unscaled time (§5.2.3); the skill documents "use `task.wait` for
gameplay, `os.time`/`DateTime` for timestamps, `GetServerTimeNow` for cross-client sync" as a
teaching point with a wrong→right pair (busy-wait on `os.clock()` → `task.wait`).

#### 5.2.7 Error-message style (the AI self-repair contract)

One format everywhere (VM errors, stubs, budget kills, bridge errors). Human-readable line:

```
[mod:speed_pad script:server/main.lua line:12] NOT_IMPLEMENTED: TweenService:Create is planned
for MVP8. | fix: animate manually with RunService.Heartbeat + lerp until then.
```

Structure (also emitted as a structured record to `LuaLogService` — fields, not regex bait; the
format is **stable and machine-parsable from day one**, LOCKED):

- `modId`, `script` (author-relative path), `line` (post-source-map, i.e. the author's line)
- `code` — stable machine enum: `NOT_IMPLEMENTED`, `BAD_ARGUMENT`, `UNKNOWN_SERVICE`,
  `INSTANCE_DESTROYED`, `PARENT_LOCKED`, `BUDGET_EXCEEDED`, `SIGNAL_CASCADE`, `THREAD_CAP`,
  `CYCLIC_REQUIRE` (MVP5, DEV-1), `API_VERSION_MISMATCH` (MVP5), `NOT_AUTHORITY` (MVP12),
  `PAYLOAD_TOO_LARGE` (MVP11), `CONTEXT_VIOLATION` (MVP11), `WORLD_DETACHED` (the owning
  `RbxWorldHost` was destroyed — scene load, domain reload, or play-mode exit — so the registry the
  mods captured no longer backs a scene)
- `message` — states what happened and (for stubs) the exact MVP phase
- `fix` — one actionable suggestion, present tense, ≤1 sentence; for `BAD_ARGUMENT` it names the
  expected type and position (`fix: pass a Vector3, got string at argument 2`)

Rules: no stack-trace-only errors (the top frame is resolved to mod/script/line even through the
scheduler); identical repeated errors are coalesced in the ring buffer with a counter (AI context
budget is finite; `LuaLogFormatter.ToPromptText` renders the coalesced view); `debug.traceback`
remains available for depth. The `[mod:<id> script:… line:…]` prefix **is attached** to errors
raised inside a persistent mod — instance and service errors, and (since the 2026-09-24 fix wave)
datatype errors too — through `RbxError.WithContext(modId, script, line)`, with `script:main.lua`
and the author's post-downlevel line from the live traceback. One-off `execute_lua` errors have no
owning mod and stay unprefixed. What is still deferred to MVP5 is the VM chunk name `mod:<id>`, so
that raw VM tracebacks resolve to the owning mod too. Argument positions in `BAD_ARGUMENT` never count
`self`, and a property-assignment error reads `Part.Name expects a string, got number | fix: assign a
string to Part.Name`.

The same line is the error value a script receives: `pcall`, `xpcall` and a protected
`coroutine.resume` all get exactly the line a failing host call raised (as built since 2026-09-24:
`LuaCsHostFunctionException`, error level 0), and a sandbox cap its own one-line text the same way —
never a CLR type name, a managed stack trace or a source path, which used to cost about 1,600
characters per refusal and overflowed `execute_lua`'s 4,000-character result. A budget trip's error
value is a one-line text of the same kind, and no script can catch it: a trip of the step, time or
memory budget (the guard, a scheduler thread's resume, a raw coroutine's resume) ends the run it
tripped in — `pcall`/`xpcall` let it through, `xpcall`'s handler does not run, the state stays guarded
for later runs — and only the host, or the resumer of a tripped raw coroutine (`coroutine.resume`
returns `false` and the line; the coroutine is dead), sees it. Sandbox cap refusals and the per-call
pattern-step refusal stay catchable. C# still reads the original exception (`HostException`,
`IScriptHostFailure`, `ScriptExecutionErrors.NextCause`). When such a line ends a scheduler thread,
the thread's fault keeps its code and context under one prefix (`RbxError.TryParse` is the exact
inverse of the formatter); only a plain Lua error is wrapped as `BAD_ARGUMENT`, and a budget kill is
`BUDGET_EXCEEDED`.

#### 5.2.8 Risks and mitigations

| Risk | Mitigation |
|---|---|
| Deferred queue reorders where corpus scripts assumed immediate | corpus gate catches it; per-drain generation cap (R5.6) keeps cascades diagnosable; author docs warn that multi-connection handler order is not guaranteed (R5.11) |
| Scheduler heap churn / GC in Heartbeat | binary heap keyed by resume-time, pooled nodes; zero-alloc drain measured in MVP2 tests, budget ≤ 1 KB/frame steady-state |
| `signal:Wait()` leaks threads if the signal never fires | threads owned by mod → reaped on unload; `Wait` counts against the mod's live-thread cap (default 256, `THREAD_CAP` error beyond) |
| Loopback remotes hide serialization cost | payloads always round-trip `RobloxJson` bytes (§5.2.5), perf measured in MVP17 |
| JSON round-trip loses number precision / table identity | contract documented in the skill (doubles only, no cycles, empty-table rule); cycle detection errors with the key path |
| Tick-driver ordering fights other CoreAI systems | single integration point: `LuaModRuntimeTickDriver` keeps its existing execution-order slot; new phases are methods on it, not new MonoBehaviours |

#### 5.2.9 Acceptance criteria (MVP2 test list; names cite rule IDs per §6.6)

1. `task.wait()` resumes at the next "resume delayed threads" slot — which R4.11 says is the same
   scheduling point the task docs call "the next Heartbeat step" — and returns the actual elapsed
   time; `task.wait(0.5)` within one frame of 0.5 s (test clock). Note the slot is *stage*-relative,
   not frame-relative: a wait issued before the current frame's delayed slot resumes in THAT slot
   (elapsed can be well under a frame delta), while one issued from the slot itself, from
   `Heartbeat` or from `PreRender` resumes next frame. Only the latter — the steady state for a
   thread the scheduler resumed — gives elapsed ≥ one frame delta, so do not assert that bound
   unconditionally.
2. `task.spawn` runs to first yield synchronously; `task.defer` does not run before the current
   drain completes (R4.8); relative ordering test from the Roblox docs example.
3. `task.cancel` on a waiting thread: never resumes; cancelling a thread that already finished is a
   no-op — Roblox's `task.cancel` closes the thread, and closing a dead thread is not an error (only
   the currently running thread, or a thread another scheduler owns, raises).
4. `task.delay(0, fn)` fires on the next Heartbeat.
5. Frame order matches R4.2 — including **delayed threads resume BEFORE Heartbeat**
   (`R4_2_DelayedThreadsResumeBeforeHeartbeat`); `Heartbeat`/`PostSimulation`/`PreSimulation`/
   `PreRender` fire in §5.2.3 order with plausible dt; legacy `Stepped`/`RenderStepped` aliases
   fire with legacy signatures; with a simulated `DedicatedServer` topology, `PreRender` never
   fires and `IsClient()` is false.
6. Clocks (D9): at `timeScale = 0.5`, `task.wait(1)` takes ~2 real seconds and returns ~1;
   `os.time()` is unaffected by timeScale; `time()` advances with scaled time;
   `GetServerTimeNow()` is monotonic, unscaled, and **epoch-comparable with `os.time()`**
   (|difference| ≤ 1 s); `os.clock()` measures monotonic elapsed wall time since the clock source
   was created — **a recorded deviation**: stock Lua's `os.clock` is process CPU time, but the
   offline mirror defines Luau's as monotonic elapsed wall time, and CoreAI follows the mirror.
7. Deferred dispatch per R5.4–R5.7: `ChildAdded` handler runs deferred, mutations inside the
   handler do not re-enter; re-entrancy cap of 10 generations raises `SIGNAL_CASCADE` (R5.6),
   attributed to the firing mod while the rest of the frame runs.
8. `Once` fires exactly once; `Wait` resumes with fire args; Disconnect-vs-Destroy
   pending-handler asymmetry exactly per R5.7.
9. Destroy → all connections dead, Parent nil + locked before disconnect/child teardown
   (R6.2/R5.7); `Destroying` handlers run at the next resumption point **after** destruction
   completes and observe post-destruction state — `Parent == nil`, connections gone (R5.8) —
   reading tombstone members per DEV-7.
10. `WaitForChild`: the pre-existing-child immediate path remains green from MVP1; MVP2 adds
    created 3 frames later → resumes, the 5 s warning text matching `Infinite yield possible…`,
    and the timeout overload returning nil.
11. `GetService("RunService")` works; a service that is still planned errors **at the member**,
    not at `GetService`, naming its rung — the frozen example moved from `TweenService`/MVP8 to
    `DataStoreService`/MVP9 when TweenService shipped in 7.19.0, and the tests moved with it;
    `GetService("Bogus")` → `Bogus is not a valid Service name`.
12. `HttpService:JSONEncode/JSONDecode` round-trips the contract fixtures (arrays, dicts,
    nested, empty table, null, unicode). **The "same component" half of this criterion was wrong
    and is retired**: `LuaCsRbxJson` (HttpService) and `LuaCsRbxNetworkCodec` (remotes) are two
    encoders answering two different questions, and measurement (2026-09-08) found three real
    divergences — the remote codec wraps every table in a `$rbx` envelope and carries an argument
    LIST at the root, it *rejects* mixed numeric/string keys and sparse arrays where the HTTP
    encoder silently follows Roblox and drops or null-pads them, and whole numbers format as `12`
    over HTTP versus `12.0` on the wire (`NaN`/`±Infinity` used to differ as well — bare over HTTP,
    quoted on the wire — until MP-17 made the wire encoder emit bare tokens too). String
    escaping agrees exactly. Forcing them together would change observable behaviour on both
    paths, so what is asserted instead is the divergence itself, fixture by fixture, in
    `RbxJsonContractEditModeTests` — a test that goes red the day either side drifts.
13. RemoteEvent loopback: `FireServer` from a test script tagged `client` (harness context tag,
    §5.2.4) reaches `OnServerEvent` next drain, args intact through the JSON byte round-trip;
    the first `OnServerEvent` argument is asserted to be the **synthetic local player proxy**
    (`UserId == 1`; the full `Player` class lands in MVP8); `RemoteFunction` invoke returns;
    two-mod cross-talk works.
14. Budget: `while true do end` in a Heartbeat handler is killed within its slice, other mods'
    handlers still run that same frame, `BUDGET_EXCEEDED` logged with mod/line, and K consecutive
    faulting frames **quarantine** only that mod (it stays loaded/addressable; reload clears — §2);
    budgets enforce at `timeScale = 0`. Library calls no longer escape the slice: string patterns
    are capped at 5,000,000 matcher steps per call (`EXCEEDED_PATTERN_STEP_BUDGET`), `gsub` and
    `string.format` results at 1,000,000 characters, and memory is enforced per resume.
15. Stances for U1–U7 recorded (§2.1); testable stances have conformance tests.
16. Corpus: ≥30% of Tier-A fixtures pass end-to-end (preprocess → load → run → assert).

---

## 6. Cross-cutting concerns

### 6.1 Lua logging integration points per rung

Log core **(landed and wired)**: `Assets/CoreAIMods/Runtime/Logging/` — `LuaLogService` per-mod ring
buffers behind `ILuaLogService` (C# query + subscription), `LuaLogFormatter.ToPromptText` for
agent-facing rendering, `GetModLogsLlmTool` (`get_mod_logs`, registered for the Programmer role),
optional `LuaLogFileSink`. No Unity console dependency anywhere in the mod path.

| Rung | What starts logging |
|---|---|
| MVP1 | instance lifecycle at debug level (create/parent/destroy with `InstanceId`), stub hits, deprecation notes |
| MVP2 | scheduler: thread spawn/kill, budget kills, signal cascade traces; structured error records (§5.2.7) |
| MVP3 | world save/load timeline; backup snapshots with trigger metadata (§MVP3) |
| Gameplay services I | service-level warnings (Touched without collider, tween on destroyed instance) |
| MVP4 | `CONTEXT_VIOLATION`; `require` cycles (`CYCLIC_REQUIRE`) |
| MVP5–MVP6 | bridge: send/receive counters, drops, rate-limit trips; client logs forwarded to host buffers tagged with player id |
| MVP7 | input while unfocused; speed-hack corrections |
| MVP8 | headless routing: `LuaLogFileSink` + remote log fetch (no Hub on a dedicated server) |
| MVP14 | RBXL import diagnostics: skipped `Terrain` (info), asset placeholders, script→disabled entries, the loss report |
| MVP18 | load/reload/teardown timeline per mod in the console; live stream to the Hub Console + `watch_mod_logs` push tool |
| MVP19 | log-path overhead audit (target: disabled-level record ≤ 100 ns, zero alloc) |

### 6.2 AI toolchain per rung (what the LLM can call)

Existing today: `manage_mods` (`LuaModsLlmTool.cs`) — load/reload/unload/forget/export/import/
versions/revert/diagnostics; `get_mod_logs` (`GetModLogsLlmTool`, wired for the Programmer role); the
world tools `save_world`, `load_world`, `list_autosaves`, `load_autosave`; `execute_lua` and
`world_command`; the hand-written "Rbx API" skill (`read_skill`). Tool-to-role wiring is hard-coded
today (every Lua/world tool goes to `Programmer`); MVP10 makes it profile data.

| Rung | Tool additions |
|---|---|
| MVP3 | `save_world`/`load_world` — **shipped**: `save_world` writes a create-once manual slot; `load_world` never applies a package, it returns `player_confirmation_required` plus a one-use request id that the player accepts or rejects through the Hub World Loads page ([WORLD_PACKAGE.md](WORLD_PACKAGE.md)); `list_autosaves`/`load_autosave` — shipped |
| MVP5–MVP8 | `get_network_stats` (per-mod send rates, drops), log filters gain a `player:` scope; remote log fetch on a dedicated server |
| MVP11 | role-gated human tool surface; `AIService` for mods |
| MVP12 | selection-aware context ("this part") for the copilot |
| MVP13 | `list_templates`/`insert_template`/`new_place_from_template` |
| MVP14 | `import_rbxl`/`export_rbxl` (imported scripts arrive disabled; asset placeholders and losses reported) |
| MVP18 | `list_mods` (id, enabled, contexts, load order, error count), `enable_mod`/`disable_mod`, `get_api_surface` (machine-readable: every class/member → implemented / stub-with-rung — **kills hallucinated-API loops**; generated from the same catalogs as the skill manifest), `diff_mod_versions` (revision diff text for repair context; from the dropped old MVP7), `watch_mod_logs` (push subscription for the repair agent), `run_console` (REPL in a mod sandbox; one-shot ownership rules §2), auto-repair policy upgraded (§MVP18) |

### 6.3 Realtime / hot-reload rules (LOCKED)

Reload = teardown + fresh run of the mod's scripts with clean state. What survives:

| Thing | Survives reload? | Survives disable? | Rationale |
|---|---|---|---|
| Mod store (`ILuaModStore` / DataStore data) | **yes — always** | **yes — always** | persistence is the contract |
| Log ring buffer | yes | yes | the AI needs pre-crash history |
| Revision history (`ILuaScriptVersionStore`) | yes | yes | rollback path |
| Signal connections | **no** — always disconnected | no | stale closures over dead upvalues are the classic hot-reload bug; loud and predictable beats subtle |
| Running threads (`task.*`) | **no** — cancelled via `IScriptCoroutine.Kill` | no | same |
| Mod-owned instance tree (`OwnerModId`) | **no by default** — destroyed; **yes** with `mod.json: "preserveInstances": true`, then the new run re-acquires by `WaitForChild`/names (`WaitForChild` is immediate for an existing child from MVP1; absent-child yield is MVP2) | no (destroyed) | default favors deterministic re-runs; opt-out flag favors stateful builds (e.g. a mod that spent minutes generating a level) |
| Attributes the mod set on *host* instances | yes | yes | the mod decorated someone else's object; teardown must not vandalize the world |
| GUI (MVP15) | rebuilt | destroyed | GUI is a projection of code |

Additional rules: reload is atomic per mod (old version torn down only after the new source
preprocesses successfully — a syntax error leaves the old version running and logs the failure);
`loadOrder` respected on bulk reload; the world itself (host objects) is never touched by mod
teardown. **Quarantine (§2) rides the same pipeline**: at the error threshold a mod stops
dispatching but stays loaded/addressable (no auto-unload); reload clears quarantine;
`TeardownModEffects(modId, reason)` — announced by `ModTearingDown` — is the single teardown
entry point shared by hot reload, quarantine escalation, and world load (§MVP3), clearing
logic-slot overrides and (future) owned instances/coroutines, and cancelling AI tasks via
`CancelTasks("Mod:<modId>")` (§2, AI-call reservations).

### 6.4 Compatibility policy (the measurable corpus gate)

Corpus home: `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/` (`Fixtures/`, `FixturesB/`)
— real-world, tutorial-grade Roblox scripts (rewritten from scratch to the same shape — never copied;
the licensed round-trip corpus of real Roblox content is MVP14's, §7) with a tiny assertion harness.
Fixtures run at the default 0.28 m/stud scale as primary and 1 stud = 1 m as smoke (D3). Tiers:

- **Tier A (MVP2, 20 scripts)**: language + core API — loops with `task.wait`, part spawning,
  parenting, signals, kill-brick-shaped logic with a fake Touched, Luau syntax constructs
  (through the preprocessor).
- **Tier B (Gameplay services I, MVP7, MVP16; frozen at 10 fixtures today)**: Players/Humanoid/
  leaderstats, Touched for real, tweens, Debris, DataStore save/load, input binding.
- **Tier C (MVP5–MVP19, +20)**: remotes over the wire, replication assertions, GUI, perf
  micro-benchmarks (Tier C doubles as the MVP19 benchmark suite).

Gates (CI-enforced EditMode tests; a gate is part of the owning rung's DoD):
MVP2 ≥ 30% of A (met) · Gameplay services I ≥ 60% of A+B (enforced) · MVP4 ≥ 50% of A (already exceeded;
it must not drop) · MVP7 ≥ 70% of A+B · MVP15 ≥ 75% of A+B+C · MVP19 ≥ 85% of A+B+C. "Pass" =
unmodified source preprocesses, loads, runs to completion, assertions green, zero `NOT_IMPLEMENTED`
hits. Corpus fixtures double as the verified-example pool for the skill — a fixture that passes is
eligible to be quoted as a documented example. MVP14 adds a separate gate: the licensed round-trip
corpus (§MVP14, DoD (a)–(e)).

### 6.5 WebGL acceptance checklist (gate on every rung)

WebGL is a first-class target (`AGENTS.md`). Every rung's DoD includes:

1. No threads, no `Task.Wait()`/`.Result`, no blocking waits anywhere in the new code
   (single-threaded WASM; the coroutine substrate already resumes synchronously —
   `LuaCsCoroutineHandle` docs).
2. No sync-over-async: anything that must wait yields through the scheduler (D9), never spins.
3. After a persistence write, `CoreAiWebGlPersistence.Sync()` (or `SyncAsync()`) is asked whether
   the engine's automatic `persistentDataPath` persistence is armed, and `false` is surfaced as a
   failure — already done by `FileLuaModStore`, `FileLuaModSourceStore` and
   `FileRbxWorldPackageStore`; new stores must match. `FS.syncfs` is never driven by hand (deprecated
   since Unity 6.3; its callback never fires), and no code awaits a durability confirmation the
   browser never sends.
4. New plugins/parsers (Loretta, JSON) pass an IL2CPP/WebGL AOT smoke test before adoption.
5. Networking code compiles and runs with the browser constraint: WebGL is only ever `Solo` or
   `Client` topology (a pure client, or a creator-mode client of a server, decision D6) — never
   `Host`/`DedicatedServer` (no listen sockets in browsers).

### 6.6 Test layout and conformance naming (LOCKED convention)

Test hierarchy (current layout plus the rung-owned folders that are still planned):

```
Assets/CoreAIMods/Tests/
  EditMode/
    ScriptEngineSeamEditModeTests.cs       # engine seam (MVP0; flat at EditMode root)
    ScriptingSeamHonestyEditModeTests.cs   # engine-seam architecture guard
    LuaLog*EditModeTests.cs                # log service/formatter fixtures (flat)
    LoggingSeamHonestyEditModeTests.cs     # logging architecture guard
    LuauDownlevel*EditModeTests.cs         # preprocessor + corpus/wiring fixtures (flat)
    RbxApi/
      Acceptance/             # MVP1 acceptance gate + goldens
      Binding/                # GameObject binder and world-host wiring
      Datatypes/              # Vector3/CFrame goldens, RbxSpace suite
      Instances/              # registry, lifecycle, navigation, service provider
      LiveCheck/              # small-model live-check harness
      LuaBindings/            # Lua-visible Rbx surface and pulled-forward services
      Scheduling/             # task.*, phases and budget attribution
      Networking/             # bridge, remotes, serialization, topology and clock
      Replication/            # replication core (registry-to-registry harness)
      Unity/                  # engine-bound Rbx fixtures
      CompatibilityCorpus/    # Tier-A/Tier-B corpus (Fixtures/, FixturesB/) and its gates (§6.4)
      Services/               # planned — per-service fixtures (today they live in LuaBindings/)
  PlayMode/
    RbxApi/                   # physics/Touched, real tick-order, per-body gravity
Assets/CoreAIMirror/Tests/EditMode/   # Mirror bridge fixtures (compile against Mirror; OfflineMirror.cs)
```

**Conformance-test naming convention**: tests that pin behavior specified by a normative rule
(§2.1) cite the rule ID in the test name — `R4_2_DelayedThreadsResumeBeforeHeartbeat`,
`R5_6_DeferredReentrancyCapIs10`, `M3_8_TableKeysStringified`, `S1_8_UpdateAsyncNilAborts`.
This gives auditors rule→test traceability: grep a rule ID, find its tests. Deviation tests cite
the deviation ID (`DEV1_CyclicRequireRaisesError`).
Tests that pin a recorded DEV decision use the `DEV<n>_` prefix in the same way — for example,
`DEV3_BudgetKillTargetsOnlyOwningModAndOtherModRunsSameFrame`.

---

## 7. Non-goals

- Full Roblox fidelity: R15/R6 rigs, `MarketplaceService`, avatar/catalog systems, real
  DataStore cloud semantics (versioning, ordered stores), Parallel Luau actors, Terrain,
  `HttpService` open internet access (CoreAI's own AI backend is the only network egress;
  `HttpService` exposes JSON members only).
- Copying Roblox **documentation text or assets** — API *shape* compatibility only; the corpus
  scripts of §6.4 are written from scratch. Revised 2026-09-24 for the round-trip requirement: the
  round-trip corpus of MVP14 needs **real** Roblox scripts, models and places, so it is built only
  from owner-authored or permissively licensed content, each item carrying its licence (RT13).
- Immediate signal mode (D4/DEV-2).
- Auto-downloading Roblox-hosted assets: `rbxassetid://` content (meshes/textures/sounds) is
  **never** fetched automatically (ToS + third-party rights) — RBXL import (§MVP14) leaves
  placeholders carrying the original id for user-supplied substitution.
- Terrain voxel import: far future, revisited only on demand — RBXL import simply skips
  `Terrain` instances with an info diagnostic.
- WebGL as a server of any kind (host or dedicated) — browsers are solo, pure client or
  creator-mode client only (plan decision D6).
- **Reversed 2026-09-24 — no longer a non-goal: a Studio-like 3D editing UI.** It was listed here
  ("CoreAI's editor is conversation + Hub + (optionally) Unity"; manual building a backlog note,
  "explicitly NOT a priority"). The flagship needs an in-game creator mode, so it is **MVP12**, built
  runtime-first in the player (never as Unity editor tooling). The reasoning that made it cheap still
  holds: manual operations go through the same Instance operations and authority resolver as the AI
  and mods — a UI layer, not a new system.

## 8. Open questions

### 8.1 Plan decisions D1–D8

**Decided 2026-09-24 (tech lead; owner may override).** The options and the reasoning are the plan
review's; each row names the rung that depends on it. This document calls them "plan decision D1–D8"
to keep them apart from the semantics decisions D1–D10 of §5.1.4 (for example D4 = Deferred signals,
D9 = the clock model), which keep their names.

| # | Decision | Options considered | Decided | Why | Rung |
|---|---|---|---|---|---|
| D1 | Script model | (a) mods as files with a declared context; (b) Roblox-style `Script`/`LocalScript`/`ModuleScript` instances holding source; (c) hybrid: instances are views over the mod source store | **(c)** | The Explorer, `require(script.Parent.X)`, RBXL import and templates all want scripts in the tree, and round-tripping Roblox models is impossible without it (RT1); versioned stores, revert, quarantine and the package's `Mods/` stay the only storage | MVP4 |
| D2 | Default creator workflow | (a) Play/Stop snapshot (Roblox Studio); (b) live edit only; (c) both | **(c)** | Live edit stays the default for Creators (the realtime principle survives); Play/Stop is an added per-creator "test run" in an isolated session — never a world rewind while other players are present | MVP12 |
| D3 | Own-character authority at 100 players (revisits owner decision 5) | (a) server-owned physics + input over remotes; (b) client-owned own character + server validation (Roblox's model); (c) server-authoritative + client prediction/reconciliation | **(b)**, for characters only | (a) makes the character feel RTT-bound; (c) needs deterministic physics PhysX does not give; (b) matches the corpus priors. Every other part stays server-owned | MVP7 |
| D4 | Transport | (a) Mirror/kcp (+ WebSocket for WebGL); (b) NGO; (c) a custom UDP library | **(a)** | Decided before (owner decision 2) and already built; the codec and interest management are CoreAI's own either way, so a later change stays behind `INetworkBridge`. Revisit only if spike S3 fails | MVP5, MVP8 |
| D5 | Scale target and release promise (revisits owner decision 4) | (a) keep the 20-client bar; (b) adopt the scale targets (100 per room, 30 Hz, ≤50 KB/s/client) as the gated product goal; (c) no numbers | **(b)** | The flagship goal needs a target the plan can be judged against; the numbers (§4.3) are frozen before measuring and published only when the staircase passes | MVP9 |
| D6 | WebGL role | (a) solo + pure client; (b) plus creator-mode client of a server; (c) plus a browser-hosted listen server | **(b)** | Creator edits travel as intents, so no hosting is needed; (c) is impossible (no listen sockets in browsers) | MVP8, MVP12 |
| D7 | Where the flagship lives | (a) a separate Unity project consuming the packages via UPM; (b) a demo in this repo; (c) a package sample | **(a)** | It enforces the framework/product split and dogfoods the Git-URL install (broken today, `TODO.md`); this repo keeps packages, presets, samples, bots and harnesses | all |
| D8 | Server Lua VM if S1 is bad | (a) keep Lua-CSharp with a cheaper guard (adaptive batch / accounting); (b) native Luau on the server only, behind `IScriptEngine` (WebGL keeps Lua-CSharp); (c) cap per-player server script work | **(a) first, (b) as the fallback** | The seam exists (§2, engine abstraction), so (b) is one adapter, but two VMs double the conformance surface | MVP9 |

### 8.2 Earlier questions

| # | Question | Blocking | Current lean |
|---|---|---|---|
| Q1 | Loretta vs mini-rewriter after the IL2CPP/WebGL smoke test | — | **Resolved**: mini-rewriter chosen and implemented on disk (`Assets/CoreAIMods/Runtime/LuauDownlevel/`: `LuauLexer.cs`, `LuauRewriteParser.cs`, `LuauDownleveler.cs`); Loretta reconsidered only if construct coverage proves insufficient |
| Q2 | Golden-fixture source values for datatype tests — hand-derived from docs vs captured from a live Roblox session | MVP1 | hand-derive core cases from documented examples; capture a verification set from a live session if licensing-clean |
| Q3 | Do `instanceId`s persist into world save files (stable across sessions)? | MVP3 | **Resolved** by the world-file decision (§2): registry records serialize with stable ids from day one — no remap table |
| Q4 | Mirror unreliable-channel MTU per transport (KCP vs others) for `PAYLOAD_TOO_LARGE` | MVP5 | **Resolved**: per channel, read from Mirror's `NetworkMessages.MaxContentSize` at runtime — unreliable capped at Roblox's 1,000 B or the transport's datagram when smaller (kcp2k: 1194 B), reliable and `RemoteFunction` at the codec's 64 KiB or the transport's reliable message size |
| Q5 | `shared` context double-execution: run on both sides (Roblox ModuleScript = per-VM copy) — confirm no shared-state illusions in docs/samples | — | **Resolved**: per-context isolated copies per R3.4 + the §3.2 matrix; "document loudly in the skill" is on the MVP18 common-mistakes list |
| Q6 | WebGL DataStore quota (browser storage) — cap per mod? | MVP16 | 1 MB/mod soft cap with `warn`, hard cap 5 MB |
| Q7 | Do we need `CollectionService:GetTagged` + `GetInstanceAddedSignal` earlier than the gameplay services (tags already work in MVP1)? | no | **Resolved**: shipped with Gameplay services I (S5.2/S5.3) |
| Q8 | Player identity mapping when Mirror auth lands (UserId source) | MVP5 | **Resolved**: host-supplied `IActorIdentityProvider`/`IActorAdmissionProvider` (owner decision 1, no anonymous online fallback); `AttachWorld(Func<LuaCsRbxApiBindings>)` makes the session host `Players.IdentitySource`, and a server world without one refuses a transport-admitted actor |
| Q9 | Skill size vs small-model context budget (4B-class models) — full skill or tiered (core + per-service on demand)? | MVP18 | tiered: compact core skill + `get_api_surface`/per-service sections fetched on demand |
| Q10 | Binary packing of remote payloads (perf) while keeping `RobloxJson` semantics | MVP6 | **Decided**: world-state replication gets a binary, revision-stamped delta codec below the bridge (MVP6, sized by spike S2); remote payloads stay `RobloxJson` until S2 or MVP9 measurements say otherwise |
| Q11 | Character controller interop at 0.28 scale: does the existing CoreAI avatar controller take `RbxSpace`-scaled speeds directly, or need a calibration shim? | — | **Resolved**: there was no such controller; `UnityRbxCharacterMotor` was written for the Humanoid adapter, which converts numbers at the binding, and a host plugs its own controller in through `IRbxCharacterMotorProvider` ([CHARACTER_MOTOR_BRIDGE.md](CHARACTER_MOTOR_BRIDGE.md)) |

Name reservation: **`AIService`** is reserved for CoreAI agent/chat access from Lua — registered as a
stub from MVP2 (§5.2.4) with the §2 AI-call reservations keeping the path open, implemented in MVP11;
the name must not be reused for anything else.

---

## 9. Footnotes — Roblox official docs consulted (July 2026)

- [^1]: task library — https://create.roblox.com/docs/reference/engine/libraries/task
  (`task.spawn/defer/delay(fn|thread, ...) → coroutine`, `task.wait(duration=0) → number`,
  `task.cancel(thread)`, `task.synchronize/desynchronize`)
- [^2]: RunService — https://create.roblox.com/docs/reference/engine/classes/RunService
  (`PreSimulation(deltaTimeSim)`, `PreAnimation`, `PostSimulation`, `PreRender(deltaTimeRender)`,
  `Heartbeat(deltaTime)`, legacy `RenderStepped`, `Stepped(time, deltaTime)`; `BindToRenderStep`,
  `IsServer/IsClient/IsStudio/IsRunning`)
- [^3]: Instance — https://create.roblox.com/docs/reference/engine/classes/Instance
  (`FindFirstChild(name, recursive)`, `WaitForChild(childName, timeOut)` returns an immediate child
  from MVP1 and gains absent-child yield, 5 s warning, and timeout overload in MVP2,
  `GetAttribute/SetAttribute/GetAttributes/GetAttributeChangedSignal`,
  `AddTag/RemoveTag/HasTag/GetTags`, events `ChildAdded/ChildRemoved/DescendantAdded/
  DescendantRemoving/Destroying/AncestryChanged/AttributeChanged`)
- [^4]: GlobalDataStore — https://create.roblox.com/docs/reference/engine/classes/GlobalDataStore
  (`GetAsync(key, options)`, `SetAsync(key, value, userIds, options)`,
  `UpdateAsync(key, transformFunction)`, `IncrementAsync(key, delta=1, userIds, options)`,
  `RemoveAsync(key)`; all yield)
- [^5]: RemoteEvent — https://create.roblox.com/docs/reference/engine/classes/RemoteEvent
  (`FireServer(...)` client-side, `FireClient(player, ...)`/`FireAllClients(...)` server-side,
  `OnServerEvent(player, ...)`, `OnClientEvent(...)`)
- [^6]: UnreliableRemoteEvent —
  https://create.roblox.com/docs/reference/engine/classes/UnreliableRemoteEvent (same member
  surface as RemoteEvent). [^6-note]: documented drop threshold 1000 B; practical budget ~900 B
  (community-measured, UNCERTAIN) — from Roblox announcements/DevForum, not the class page; we
  enforce our transport's real limit (Q4).
- [^7]: RemoteFunction — https://create.roblox.com/docs/reference/engine/classes/RemoteFunction
  (`InvokeServer`/`InvokeClient` yield; `OnServerInvoke(player, ...)`, `OnClientInvoke`)
- [^8]: Workspace — https://create.roblox.com/docs/reference/engine/classes/Workspace
  (`SignalBehavior: Enum.SignalBehavior` not-scriptable, `Gravity`)
- [^9]: SignalBehavior enum —
  https://create.roblox.com/docs/reference/engine/enums/SignalBehavior
  (Default=0, Immediate=1, Deferred=2, AncestryDeferred=3)
- [^10]: Deferred engine events — https://create.roblox.com/docs/scripting/events/deferred
  (resumption points: input processing, RunService callbacks, task library resumes,
  `BindToClose`; template places default to Deferred)
- [^11]: Humanoid — https://create.roblox.com/docs/reference/engine/classes/Humanoid
  (`Health/MaxHealth/WalkSpeed/JumpHeight/JumpPower (default 50, selected by UseJumpPower,
  default true — S3.5)/MoveDirection(read-only)`,
  `TakeDamage(amount)`, `MoveTo(location, part)`, `Died`, `HealthChanged(health)`,
  `MoveToFinished(reached)`)
- [^12]: TweenService — https://create.roblox.com/docs/reference/engine/classes/TweenService
  (`Create(instance, tweenInfo, propertyTable) → Tween`; TweenInfo(time, easingStyle,
  easingDirection, repeatCount, reverses, delayTime))
- [^13]: ContextActionService —
  https://create.roblox.com/docs/reference/engine/classes/ContextActionService
  (`BindAction(actionName, functionToBind, createTouchButton, inputTypes...)`,
  `BindActionAtPriority`, `UnbindAction`, `SetTitle`, `SetImage`; handler
  `(actionName, inputState, inputObject)`, `Enum.ContextActionResult.Pass/Sink`)
- [^14]: Players — https://create.roblox.com/docs/reference/engine/classes/Players
  (`LocalPlayer`, `PlayerAdded(player)`, `PlayerRemoving(player, reason)`, `GetPlayers()`,
  `GetPlayerByUserId(userId)`, `GetPlayerFromCharacter(character)`, `CharacterAutoLoads`)
- [^15]: Debris — https://create.roblox.com/docs/reference/engine/classes/Debris
  (`AddItem(item, lifetime)`, default lifetime 10)
