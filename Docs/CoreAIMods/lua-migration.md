# CoreAIMods VM migration (MoonSharp → Lua-CSharp) + mod-feature polish — working plan

> **ARCHIVED / HISTORICAL (completed).** This migration shipped in **5.4.0**: MoonSharp was fully
> removed and Lua-CSharp (LuaCs) is now the only, production Lua VM. This document is kept for
> historical context only — its present-tense wording ("MoonSharp stays live", "Prod stays on
> MoonSharp until…") describes the plan as it stood *before* the swap and no longer reflects the
> current codebase. Do not treat it as a description of current state.
>
> Status (historical): DRAFT v2 (repo-state snapshot refreshed 2026-07-10). This was a migration plan,
> not a claim that Lua-CSharp was already the production VM at the time of writing.
> All code, docs, commit messages in English.
> Module name decision: **keep `com.neoxider.coreaimods` / `CoreAI.Mods`** (no rename). A rename to
> CoreAILua, if ever wanted, is a separate GUID-preserving PR — never bundled with the VM swap.

## North star (do not lose this)
CoreAI's core value is **dynamic mechanics & worlds — the game changes LIVE while you play** (AI/mods/Lua
rewrite mechanics and mutate the world at runtime). The whole api/ai/mods/Lua stack exists FOR this. So the
modding design is judged first by: **live hot-apply of changed scripts without restart**, runtime creation/
mutation of mechanics & world state, and — for the future — **host-authoritative sync of those live changes**
to other players. Every phase decision (tick pump, inter-mod data-only rule, per-mod logs, command channel)
should serve live, in-session mutation, not just static mod loading.

## Design principles (from owner Q&A 2026-07-07)
- **Native/Lua boundary.** C# owns per-frame hot loops (movement, camera, physics); Lua **tweaks parameters
  and reacts to discrete events**, it does NOT run the hot loop. Example: a first-person controller stays in
  C#; a mod calls `player.set_move_speed(8)` / `on("player_land", fn)` / adds a dash on a key event — it does
  NOT reimplement WASD+camera each frame. "Rotate N°/s" = a command/declaration, not a per-frame `hooks_every`.
- **Two modding modes for touching C#:**
  1. **Designed surface (recommended):** the C# script exposes fields + events → Lua tweaks/reacts. Fast, safe,
     survives IL2CPP stripping, MP-friendly. Use for anything meant to be moddable.
  2. **Reflection bridge (gated escape hatch):** generic `unity.get_component/get_field/set_field/call_method`
     lets Lua read/set fields and call methods of C# NOT written for Lua — via reflection. Can change STATE and
     invoke existing methods, but **cannot rewrite a C# method body** (no hotfix in Lua-CSharp; Harmony/IL
     patching doesn't work on IL2CPP). Gated behind the top capability tier, **host/singleplayer-only**, stripped
     on network clients; needs link.xml preserves on IL2CPP.
- **Ticks on Update in Lua work on WebGL** via the async frame pump: non-yielding handlers complete inline each
  frame; coroutine handlers resume one step per frame. Only a tight SYNC infinite loop can hang a WebGL frame —
  caught by the `SetHook` instruction budget (resolved). No blocking `.GetResult()` on the tick path.
- **Research posture:** we build our own implementation, so we STUDY proven modding logic to reimplement it
  (license is secondary, only relevant for rare verbatim copies).

## Current development stage (2026-07-07)
- ✅ Done: all de-risk/research GATES — async frame-pump proven on WebGL (coroutines), instruction-budget
  showstopper resolved (`SetHook`), plan red-teamed, external-reuse + inter-mod research done, upstream issue
  filed (nuskey8/Lua-CSharp#327). Plan v2 reflects real repo state (module = CoreAIMods, MoonSharp already out
  of core).
- ✅ Done: deep "modding-logic" research → `Docs/CoreAIMods/modding-logic-research.md` (1533 lines): reimplementation-
  ready algorithms for AceSerializer, AceComm (MP transport), CallbackHandler/AceEvent (event bus), Luanti
  server-authoritative sync, Factorio state-migration + remote, live hot-reload sequence, MP hybrid model
  (command replication + serialized data), and command-channel live mutation. Feeds Phase 3/4b/5.
- 🔄 In flight: Codex `lua-foundation` — additive Lua-CSharp foundation for Phase 2 (SetHook budget guard,
  LuaCsApiRegistry marshalling, LuaCsSecureEnvironment) as NEW files; package keeps compiling.
- ⏭ Next to BUILD: Phase 1 (finish install) → **Phase 2 (serial VM swap, 11-step map)** + Phase 2b (verify the existing DI scope after the swap) →
  fan out Phase 3/4/4b/5 → Phase 6 (skill) → Phase 7 (tests + Windows build + docs). Prod stays on MoonSharp
  until Phase 2 is green.

## Reality on disk (verified 2026-07-10)
- Lua module is extracted: `Assets/CoreAIMods/` = `com.neoxider.coreaimods` v5.3.0, asmdef `CoreAI.Mods`.
- MoonSharp already removed from `CoreAI.Core` + `CoreAI.Source` (core is Lua-free). `ILuaExecutor` seam exists
  (`Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs`). All five packages are versioned in lockstep.
- `Assets/CoreAIMods/Runtime` contains 82 C# files; 13 still reference MoonSharp APIs directly and are the
  concrete VM-migration surface.
- `CoreAiModsLifetimeScope` and `CoreAiModsInstaller` exist. The Mods scope is wired in eight demo scenes;
  package consumers still opt in by adding/parenting the scope.
- `link.xml` remains at `Assets/CoreAiUnity/link.xml` and is MoonSharp-oriented. `Lua.dll` and
  `Lua.Annotations.dll` remain dev-harness-only under `Assets/dev/LuaVmComparison/Plugins/`.
- `Microsoft.Bcl.TimeProvider` has one owner: NuGet (`Packages/nuget-packages/...` 10.0.9). The stale
  developer-harness DLL copy was removed to prevent duplicate-assembly imports.

## Proven facts (de-risk done)
- Lua-CSharp on WebGL: sync `.GetResult()` deadlocks on `coroutine.yield`; **frame-pumped async drive fixes it**
  (WebGL smoke `LUAPUMP_RESULT: OK all pumped cases pass (coroutines included)`). Green light for the swap.
- Editor: Lua-CSharp 6–12× faster on compute, cleaner sandbox, runaway-halt via CancellationToken (back-edge
  polling) 502 ms. Selective-library sandbox works (harness opens base/string/table/math/coroutine/bitwise,
  nils load/dofile/loadfile).
- Filed upstream: nuskey8/Lua-CSharp#327 (WebGL coroutine deadlock).

## Showstopper (instruction budget) — ✅ RESOLVED 2026-07-07
Lua-CSharp exposes `LuaState.SetHook(LuaFunction hook, string mask, int count)` — a per-instruction COUNT hook
fired synchronously every N instructions with **no timer thread** (confirmed in VM source:
`--hookCount == 0 → ExecutePerInstructionHook`). Plus back-edge `CancellationToken` polling on Jmp/ForLoop
(our 502 ms halt). So the `InstructionLimitDebugger` budget is fully replaceable on ALL platforms incl WebGL —
a buggy `while true do end` mod handler cannot hang a frame. **Mechanism:** in the new `LuaExecutionGuard`,
`SetHook(budgetHook, mask, count:256..1000)` where `budgetHook` (a C# `LuaFunction`) increments a counter and
throws `LuaRuntimeException` past budget (+ in-hook `Stopwatch` for the wall-clock backstop). Verify against the
pinned version: exact `mask` for pure count mode, and that the hook fires inside coroutine bodies.

## Requirements (unchanged, from product owner)
AI one-off scripts + persistent mods; mods save/add/edit/enable/disable/delete/list; categories (folders) +
search; AI enumerate + modify all mods; per-mod isolated logs (MP-ready seam, no replay yet); correct
Lua-CSharp install; skill updated for Lua-CSharp. **Reuse existing code, don't rebuild.**

## Phases

### Phase 0 — async frame-pump — ✅ PROVEN
`EvalAsync` + per-frame pump; WebGL coroutine deadlock gone.

### Phase 0.5 — instruction-budget capability + porting map (GATE, read-only)
Determine Lua-CSharp's cancellation/hook model (per-instruction hook? count? only back-edge token poll?).
Produce a concrete port map of every MoonSharp call site in the ~17 files, classified by path (tool exec /
tick runtime / formulas / bindings / coroutine / auto-repair) with the MoonSharp API used and the Lua-CSharp
equivalent. Gate Phase 2 on a viable budget story.

### Phase 1 — finish install (small; most already done)
Move `Lua.dll` + `Lua.Annotations.dll` (source generator) into `CoreAI.Mods`. Reuse the single NuGet-owned
`Microsoft.Bcl.TimeProvider` dependency; do not vendor another DLL copy. `link.xml`: add AOT preserves
for the Lua assemblies; plan removal of MoonSharp/Resources/TextAsset preserves once MoonSharp is dropped;
decide package ownership (Mods).

### Phase 2 — VM SWAP (SERIAL, single effort, the bulk) — must precede any fan-out
Swap MoonSharp → Lua-CSharp across **all execution entry points**, behind `ILuaExecutor`, VM-agnostic:
- `LuaModRuntime.Tick` (per-frame, coroutine event handlers — the WebGL-critical path): re-express coroutines
  (`LuaCoroutineHandle`/`LuaCoroutineRunner`) on Lua-CSharp; drive async by frame pump.
- `GameLuaToolExecutor`, `LuaLogicSlots` (formulas — decide: if formulas never yield, sync is safe even on
  WebGL), `LuaAiEnvelopeProcessor`, `CoreAiLuaModAutoRepair`.
- Port the ~12 binding files from MoonSharp UserData/`DynValue`/`ScriptExecutionContext` to Lua-CSharp
  `LuaValue`/`LuaFunction` + `[LuaObject]`/`[LuaMember]` source-gen (`SecureLuaEnvironment`, `LuaApiRegistry`,
  `WorldBindings/*`, `CoreAi*RuntimeBindings`).
- **Serialize VM access** (single-flight queue): async pump + tick must not interleave two chunks on the
  shared transaction scope (`GameLuaToolExecutor` resets a singleton scope) → world-command corruption.
- **Disposal/cancellation** on scene unload / play-mode exit (dispose `LuaState`, cancel in-flight pumps).
- **Instruction-budget parity** with `InstructionLimitDebugger` (per Phase 0.5).
- Audit every `ILuaExecutor` caller for hidden `.GetAwaiter().GetResult()`; make the tool-result pipeline
  non-blocking (UniTask/`Awaitable`). Drop MoonSharp from `CoreAI.Mods.asmdef` when done.

### Phase 2b — verify the existing DI scope after the VM swap (GATE test)
`CoreAiModsLifetimeScope` (child of CoreAI scope) + installer already resolve parent services and register the Lua
runtime/tools, in build-callback `AddToolForRole(Programmer, execute_lua/manage_mods)` + `AddSkillForRole
(Lua Modding)` + spawn tick driver. Gate: EditMode test that after both scopes build, Programmer actually has
`execute_lua`/`manage_mods`/`Lua Modding`. Delete dead `CoreAiChatExternalDriver.RunLuaDiag()` (`#if
COREAI_HAS_MOONSHARP`, unreachable from Source) and the stranded `CoreServicesInstaller.cs` MoonSharp `#if`;
check/remove `MeaiLlmClient` `LuaLlmTool` alias if dead.

### Phase 3 — mod store polish (EXTEND existing, then fan-out-safe)
Extend `FileLuaModStore`/`FileLuaModSourceStore`/`LuaModManifest`/`LuaModsLlmTool` — categories (folder tree),
text search (name/category/tags/source), non-destructive disable, undo-on-delete, manifest validation,
safe-boot (disable-all). Design from Thunderstore/BepInEx patterns; no vendored LGPL code.

### Phase 4 — LLM tools (extend `manage_mods` + one-off `run_lua`)
list/get/create/edit/enable/disable/delete, category-aware; wired through the Phase-2b scope, Programmer role.
One-off `run_lua` = ephemeral exec path that skips `FileLuaModStore` (no manifest, no persisted log).

### Phase 4b — Inter-mod API (Factorio `remote` model, C#-authoritative)
- `mods_register(iface, {cmd=fn,...})`, `mods_call(mod, cmd, args)`, `mods_get(mod, key)`,
  `mods_interfaces()`/`mods_has(mod,cmd)` (introspection for the AI). Registry lives C#-side
  (`Dictionary<string, ModInterface>` keyed by iface, tagged owner mod-id + version).
- **Hard rule (Factorio): args/returns are PLAIN DATA only — no closures/functions/live refs.** Deep-copy
  across mod environments; `mods_get` returns state by-value, never a live table. This is the isolation +
  future-multiplayer-determinism seam. Version conflicts → LibStub "highest-version-wins" rule (Public Domain,
  liftable ~40 lines).
- **Callbacks: minimal GATED event bus only** — `mods_on(event,handler)`/`mods_emit(event,data)`, data-only
  payloads, each handler runs under the CALLEE mod's own `SetHook` instruction budget, dispatch order owned by
  C# and deterministic (load-order sorted). Rich live cross-mod callbacks are DEFERRED (non-deterministic for
  host-authoritative MP). Extends the existing `mods_call`/`mods_get` seeds.

### Phase 5 — per-mod isolated logging (MP seam only)
`IModLogSink` per mod (separate file/scope), events tagged `{modId, scope host|client, tick, seq}`,
frame-boundary flush, allocation-light. **No replay/determinism now** — documented MP seam only.
- Reuse: BepInEx `ManualLogSource` PATTERN (named source per mod, central registry) + **MelonLogger**
  (Apache-2.0 → copyable with attribution) for the per-mod logger-instance API.
- Inject the sink as the mod's `print`/`log` via `LuaPlatform.StandardIO` redirection — don't expose real stdio.
  Host-owned + keyed by mod-id ⇒ already the MP-friendly seam (host tags each line with mod-id + tick).

### Phase 6 — skill diff for Lua-CSharp
Diff libraries Lua-CSharp actually opens vs what `Resources/AgentSkills/LuaModding` + `BuiltInLuaModdingSkillText`
promise; flag Lua 5.2(MoonSharp)-vs-5.4(Lua-CSharp) semantics (int/float subtype, bit ops, partial stdlib).

### Phase 7 — tests + Windows build + docs
EditMode/PlayMode (executor/tick/store/logging + DI-timing gate); **Windows build** for stability; docs inside
`CoreAI.Mods`. Commit at green checkpoints; bump core/unity/mods in lockstep.

## Parallelization (corrected)
Phase 2 rewrites the type vocabulary (`DynValue`/`Table` → `LuaValue`/`LuaFunction`) that Phases 3–5 compile
against, and `LuaModRuntime.cs` is touched by Phases 2/3/4/5 → **cannot** fan out during Phase 2.
- Truly parallel NOW: Phase 0.5 (read-only scoping), Phase 6 (skill diff — text only).
- After Phase 2+2b green: fan out Phase 3 (store), Phase 4 (tools), Phase 5 (logging) — now non-overlapping.
Orchestrator owns Phase 2/2b (serial), builds, integration, commits.

## Phase 2 serial port order (from the port map, dependency-first)
1. `InstructionLimitDebugger` → `LuaState.SetHook` count-hook inside `LuaExecutionGuard` (budget primitive;
   prove parity vs the Editor harness first).
2. `LuaApiRegistry` — reflected `LuaFunction` wrappers for `Register(string, Delegate)` (preserve
   auto-marshalling → keeps ~45 fns across 7 pure-delegate files unchanged); new VM-agnostic `RegisterCallback`.
3. `SecureLuaEnvironment` — `LuaState.Create()` + selective libs + nil load/dofile/loadfile + string.rep/format caps.
4. `LuaCoroutineHandle` + `LuaCoroutineRunner` — `ResumeAsync` frame pump (kill `AutoYieldCounter`; per-resume
   `SetHook` re-arm + cancellation).
5. `LuaModRuntime` — tick runtime + cross-mod `mods_export`/`mods_call`/`mods_get` `LuaValue`/`LuaTable` marshalling.
6. `LuaLogicSlots` — fix public `DynValue` leak (`TryInvoke` → VM-agnostic); formulas never yield → sync drive OK.
7. `LuaAiEnvelopeProcessor` + `GameLuaToolExecutor` — thin glue.
8. `CoreAiWorldLuaRuntimeBindings` (14 fns, `Table` arg parsing).
9. `CoreAiFullUnityLuaRuntimeBindings` (22 fns — largest single rewrite).
10. Retest pure-delegate bindings (Versioning/Input/Time/Component/WorldQuery/Logging) — no body change expected.
11. Swap `CoreAI.Mods.asmdef` reference MoonSharp → Lua-CSharp. Consider retiring `WebGlLuaOptIn` gate.

Public MoonSharp leaks to make VM-agnostic: `LuaApiRegistry.RegisterCallback`, `LuaLogicSlots.TryInvoke`.

## External reuse decisions (research 2026-07-07)
No drop-in external mod framework for Lua-CSharp exists → build orchestration on our own base
(`FileLuaModStore`, `LuaModManifest`, `LuaModsLlmTool`, `LuaModRuntime`). The two hard runtime primitives are
already in the MIT VM: sandbox (`LuaPlatform` + selective libs) and instruction budget (`LuaState.SetHook`).
- **Lift as code:** WoW **LibStub** (Public Domain) — inter-mod version rule; **MelonLogger** (Apache-2.0,
  attribution) — per-mod logger instance.
- **Patterns only (do NOT copy code — restrictive/viral licenses):** Factorio `remote` (inter-mod semantics +
  the plain-data rule), OpenMW interfaces/events (override chain, global/local split for MP), BepInEx logging,
  Garry's Mod `hook` (reimplement the ~50-line event pattern), uLua/LuaCsForBarotrauma (paid/EULA — design only).

## Cut (over-engineered)
Log determinism/replay/host-vs-client machinery (ship per-mod sink + seam only). No CoreAILua rename now.
Rich live cross-mod callbacks (data-only gated event bus is enough for now).
Factorio `on_configuration_changed` (persisted-data schema migration on version bump) and the
`data.lua`/`control.lua` two-stage load — deferred, no current consumer. NOTE: these are NOT hot-reload
and NOT error-repair — both of those are live (see below).

---

## Current state & remaining checklist (verified 2026-07-08, branch feat/coreaimods-extraction)

### Done and green
- **LuaCs stack (additive):** sandbox (`LuaCsSecureEnvironment`/`LuaCsExecutionGuard`/`LuaCsApiRegistry`),
  runtime (`LuaCsModRuntime`), coroutines (frame-pump), all gameplay bindings (`LuaCsGameplayBindings`,
  78 fns), one-off executor (`LuaCsGameToolExecutor : LuaTool.ILuaExecutor`), factory
  (`LuaCsModRuntimeFactory`). Compiles clean; `LuaCsModRuntimeEditModeTests` 10/10.
- **Step A re-wire:** `CoreAiModsInstaller.RegisterCoreAiMods` + child `CoreAiModsLifetimeScope` restore the
  Lua DI that the extraction stripped (still MoonSharp behind the `IGameLuaRuntimeBindings`/`ILuaExecutor`
  seam). The two DI gate tests (`RegisterCoreAiMods_AttachesLuaTools_*`) are green; EditMode 20/20.
- **LuaCs persistence/versioning parity (2026-07-08):** `LuaCsModRuntime` gained `ExportMod`/`ImportMod`/
  `ForgetMod`/`ListModVersions`/`TryRevertMod`/`RehydrateFromStore` + source/version-store plumbing
  (`_sourceStore`/`_versionStore`/`_autoPersistMods`, `VersionKeyPrefix="mod:"`), factory options threaded.
  `LuaCsModRuntimePersistenceEditModeTests` (14 parity tests). Both `dotnet build` = 0 errors.
- **Hot-reload + error→agent auto-repair are LIVE:** `ReloadMod` + durable `store_*`; `ModHandlerErrored`
  → `CoreAiLuaModAutoRepair` → debounced Programmer `lua_repair` task; `manage_mods diagnostics`.
- **Research→code:** Factorio `remote` (mods_export/get/call), AceSerializer/AceComm plain-data boundary,
  AceEvent fan-out with error isolation + round-robin, Roblox capability tiers — implemented in the runtime.

### The flip — two commits, orchestrator-owned (do NOT fan out; single-file, in-place)

**Commit 1 — switch runtime to LuaCs (MoonSharp stays dormant, still compiles):**
1. Extract `ILuaModRuntime` (the 11 members `LuaModsLlmTool` uses: ListMods, TryGetModSource, LoadMod,
   ReloadMod, UnloadMod, ExportMod, ImportMod, ForgetMod, ListModVersions, TryRevertMod,
   GetRecentHandlerErrors + `ModHandlerErrored` event). `LuaModInfo`/`LuaCsModInfo` have IDENTICAL fields —
   unify on the shared `LuaModInfo`/`LuaModHandlerError`, delete the LuaCs duplicates; both runtimes implement.
2. Repoint `LuaModsLlmTool` ctor param `LuaModRuntime` → `ILuaModRuntime`.
3. `CoreAiModsInstaller`/scope: build via `LuaCsModRuntimeFactory`; register `LuaCsGameToolExecutor` as
   `LuaTool.ILuaExecutor`; attach `manage_mods` on the LuaCs runtime (via `ILuaModRuntime`).
4. Switch the tick driver to `LuaCsModRuntime.Tick` (LuaCs ticker/driver already exist).
5. Repoint `CoreAiLuaModAutoRepair` to `ILuaModRuntime.ModHandlerErrored` so auto-repair follows the LuaCs VM.
6. Verify: targeted EditMode (LuaCs + persistence + LuaModsLlmTool) green; `dotnet build` 0 errors.

**Commit 2 — purge MoonSharp:**
7. Delete the MoonSharp runtime: `LuaModRuntime`, `SecureLuaEnvironment`, `LuaApiRegistry`, MoonSharp
   `*LuaRuntimeBindings`, `GameLuaToolExecutor`, `LuaCoroutineRunner`, `LuaModRuntimeTicker/TickDriver`,
   `LuaAiEnvelopeProcessor`, MoonSharp store variants replaced by LuaCs. (These have NO `#if` guards — must
   be deleted, not disabled.)
8. Remove `"MoonSharp.Interpreter"` + the `COREAI_HAS_MOONSHARP` versionDefine from `CoreAI.Mods.asmdef`.
9. Remove dead `#if COREAI_HAS_MOONSHARP` blocks in CoreAI.Source (`CorePortableInstaller`,
   `CoreServicesInstaller`, `GlobalMessagePipeMinimalBootstrap`, `AiGameCommandRouter`, `CoreAILifetimeScope`,
   `CoreAiChatExternalDriver.RunLuaDiag`).
10. `link.xml`: add `Lua.dll` AOT preserves, drop MoonSharp preserves; keep it with CoreAIMods for WebGL/IL2CPP.
11. Verify: targeted EditMode green; Windows player build; WebGL smoke.

### Future (post-flip, separate phases)
- **Scene wiring:** add `CoreAiModsLifetimeScope` child to target scenes + `CoreAI → Setup → Add Mods` menu
  helper (until then, live scenes do NOT get Lua tools even though tests are green).
- PlayMode `FastNoLlm` run (safe, no backend); avoid full-suite via interactive runner (deadlocks).
- Phase 3 mod store: CRUD + categories (folders) + search + persistence polish.
- Phase 5 per-mod isolated logging (MP-ready `IModLogSink`, {modId, scope, tick, seq}).
- Phase 6 update the Lua Modding skill for Lua-CSharp 5.4 semantics.
- Dependencies/soft-hard + version ranges + load-order topo-sort (BepInEx patterns — researched, not built).
- Deterministic tick seam (`IModClock` fixed-step) + store-command DTO for host-authoritative MP.
