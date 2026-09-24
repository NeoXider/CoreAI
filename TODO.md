# TODO

> Updated 2026-09-24. Tracks open work by priority. Shipped work is in `CHANGELOG.md` (both packages);
> non-blocking future work in `Assets/CoreAiUnity/Docs/BACKLOG.md`.
> Released: 7.3.1 (2026-09-02, all six packages in lockstep — WebGL tool-turn fix); 7.3.0 (2026-09-02, lockstep — MVP2.5 persistence release); 7.2.0 (2026-09-02, `com.neoxider.coreai` + `.coreaiunity` only); 7.1.1 (2026-08-31, `com.neoxider.coreai` + `.coreaiunity`); 7.1.0 (2026-08-30, all six packages
> in lockstep); 7.0.7 (2026-08-27); 7.0.0 (2026-08-01) added `McpServerInfo.Version`. The browser gate passed on 2026-09-02; see the section below.
> Full positive-module matrix verified 2026-08-01 in Unity 6000.3.14f1: `core` 2056 passed / 0 failed /
> 10 skipped; `llm` 2604 / 0 / 9; `lua` 2068 / 0 / 10; `full` 2616 / 0 / 9. Non-live PlayMode
> `FastNoLlm` with `COREAI_LLM`: 78 passed / 0 failed / 1 platform skip. Live Qwen3.5-0.8B LLMUnity smokes from the
> gate called Genie `grant_gold`; Spellcraft produced `storm|3`, `fire|2`, `poison|1`, and `frost|2` through
> native `cast_spell` with no ToolsOnly error.

## MVP3 closure and the MVP1/MVP2/MVP8/multiplayer audit fix waves (2026-09-24, unreleased)

MVP3 is **code complete; its Unity verification gate is pending**. Five read-only audits of the 7.45.0 tree (MVP1
instance core, 37 findings; MVP2 scheduler/signals/budgets/sandbox, 28; MVP8 gameplay services, 28; multiplayer
foundation, 24, plus MP-25 found while fixing; newcomer ergonomics, triaged in the next section) were followed by
fix waves W1–W6; each fix ships with a regression test that fails on the old code. A first audit round over the
whole wave (snapshot `300eb6a4`: A1 world package, A2 Lua runtime and guard, A3 instances and bindings, A4
multiplayer, A5 tests, docs and conventions) was followed by its own fixes; rounds 2 and 3 are still to run. IDs
are the audits' (M1-xx, M2-xx, M8-xx, MP-xx; A1-xx … A5-xx for round 1); the reports themselves are not kept.
Verified without Unity only: the Roslyn compile gate (C# 9, every asmdef, six configurations plus Mirror, no new
error against 7.45.0) and the two portable `dotnet test` suites on Linux — engine-free 2112 passed / 0 failed /
3 skipped, Lua tier (`tools/portable/LuaTests`, CI job `portable-lua`, floor 1,400 passed, ceiling 42 not
executed) 1575 passed / 0 failed / 37 not executed (32 Inconclusive `PORTABLE_ENGINE_UNAVAILABLE`, 3 ignored in a
`OneTimeSetUp`, 2 skipped), both at `07264057`. This section records everything landed up to `07264057`.

- [ ] **Unity verification gate (owner/CI), then bump and tag:** full EditMode 0 failed and PlayMode `FastNoLlm`
      0 failed on this tree. Every EditMode fixture named below was written without a Unity run.
- [ ] **Check the tests (owner, in Unity 6000.3.14f1).** Run each suite below and fix, never skip or weaken, any
      failure; the Linux suites already pass but prove nothing about Mono/IL2CPP or engine-bound code:
  - [ ] EditMode, every package, in all four positive-module legs (`core`, `llm`, `lua`, `full`) and with Mirror
        installed (`MIRROR` define): 0 failed. Compare the counts with the 2026-08-01 matrix above.
  - [ ] The suites that have never run anywhere: every `CoreAIMirror/Tests/EditMode` fixture (compile-only so far;
        audit round 1 added 13 cases in `MirrorBridgeRulesEditModeTests`, `MirrorClientRemoteRulesEditModeTests`,
        `MirrorClientRemotesEndToEndEditModeTests`, `MirrorKickEditModeTests` and
        `MirrorProviderAdmissionFailureEditModeTests`, `70a4d1ab`), `Mvp3WorldPackageFollowUpEditModeTests` (MVP3 DoD
        (c)/(d)/(f), startup selection including the three `StartupSelection_*` refresh cases of `02249815`, network
        guard — its `[UnityTest]`s cannot run on Linux), `Mvp1AcceptanceGateEditModeTests` (including
        `MaterialVariantEdit_RepaintsOnlyTheCurrentWearers_AndAnEqualWriteNothing`, `4d107ae6`), the binder /
        `RbxWorldHost` / camera / character-motor fixtures, the engine-bound tests moved into Unity-only classes by
        `80f2f30c` (`RbxApiLuaBindingsProductionContainerEditModeTests` — with
        `Lua_MP_10_A4_01_AFloodThroughAHandlerThatSchedulesWork_ChargesTheSender_NeverTheHost`, the compile-only
        production-container twin of the Linux-run A4-01 case in `RbxTaskSchedulerLuaBindingsEditModeTests` —
        `InstanceGameObjectBinderCrossLayerEditModeTests`, `RbxWorldHostLazyWorldWrapEditModeTests`,
        `UnityRbxCharacterMotorLifecycleEditModeTests`), and the G10 no-LLM test
        `RealProviderWithoutLlmModule_RefusesInsteadOfMeasuringAStub` (`core`/`lua` legs only, `f25ca635`).
  - [ ] PlayMode `FastNoLlm` (incl. `Mvp8PhysicsPlayModeTests`): 0 failed.
  - [ ] The 37 Lua-tier cases the Linux runner reports as not executed (listed in `tools/portable/LuaTests/README.md`).
  - [ ] Guard behaviour on Mono and IL2CPP: a budget trip cannot be caught by `pcall`/`xpcall` and the state stays
        guarded (`LuaCsGuardFrameAndAllocationEditModeTests`, `LuaCsSecureSandboxEditModeTests`,
        `LuaCs_RunawayHandlerAfterAnEarlierTrip_IsStillCut_AndTheStreakQuarantines` in `LuaCsModRuntimeEditModeTests`)
        — the fix (`1f667ade`) relies on Lua-CSharp internals verified on .NET 8 only; the same for the sandbox
        fixes of `07264057` (a raw `coroutine.resume` of a handle's thread refused, library calls back into Lua capped
        at 200 nested).
  - [ ] A WebGL build smoke of the world package (save → reload the page → load), see the item below.
- [ ] **Real WebGL page-reload gate** for the world package: save → reload the page → the bytes and the startup
      selection survive (`Docs/CoreAIMods/WORLD_PACKAGE.md`, "Acceptance status").

### Closed

- [x] **MVP3 DoD (a)–(f) each proven by a named, non-vacuous test** (`94019f99`; the positive confirm lands with
      `82649c98`) — the list is in `Docs/CoreAIMods/WORLD_PACKAGE.md`, "Acceptance status (MVP3)".
- [x] **W3.5 tail: a player-confirmed world survives a process restart** (`82649c98`): durable startup selection
      under `Saves/Startup`, restored through the same staged swap, the default world on any failure, Hub reset
      button, `false` durability is a failure. The world tools return JSON failures instead of exceptions
      (`capture_failed`, `not_found`, `invalid_package`, `read_failed`, `list_failed`; `94019f99`, `82649c98`,
      `99eaa660`), and a world load is refused while network sessions are live (`network_sessions_active`).
- [x] **Rung-zero residue: restore runs as one host-enveloped operation; the ACL floor refuses a legacy package in
      an ACL-composed session** (`c7b1f44e`; also closes newcomer finding D7, the raw `FormatException`).
- [x] **Restored trees charge the per-actor instance quota; a restored headless Humanoid gets the scheduler**
      (`632366fa`).
- [x] **A non-finite value or a dangling/out-of-range reference written by one line of Lua no longer blocks every
      save, autosave and gated `execute_lua`** — capture repairs the snapshot and records a diagnostic
      (`8854bb0d`, `30437d0b`).
- [x] **Remote codec** — MP-02 (a 64 KB packet cost 268 MB to decode), MP-17 (NaN travels as a number), MP-01
      (client-authored `Instance` references resolve only if the sender can see them) (`5f1cc8f5`, `700db814`).
- [x] **Scheduler fault containment** — M2-02, M2-11/12/13/17/26, M8-02: one mod's fault no longer breaks the
      frame for the others; cascade width budget; `task.defer` in the same resumption point; `task.cancel` of a
      finished thread is a no-op; `math.huge` parks (`2d6bdee4`).
- [x] **Lua VM budgets** — M2-01 (scheduler threads died silently after a million instructions: no lifetime cap
      now), M2-05 (memory enforced per resume; the 1 GB string under a 256 MB budget) (`a5c453f4`). The
      allocation-backstop item in "MVP2.5 rungs" is updated accordingly.
- [x] **Budgeted string patterns** — M2-04 (a single `string.find` ran 3.3 s under a 500 ms budget), M2-19 (yield
      across a C-call boundary is a clear error) (`c97e6367`).
- [x] **Datatype bindings and ownerless `Connect`** — M2-03 (a one-off `execute_lua` subscription broke every later
      frame), M2-09 (per-handler table copies), M2-28 (`ConnectParallel`), M1-04/07/19/24/26/37 (`6cc8c54c`).
- [x] **TweenService** — M8-01 (a 1e-300 s forever tween hung the host), M8-02/03/07/08/14/20/21 (`1d4b635f`).
- [x] **Instances** — M1-01/02/03/13/22/23 (AncestryChanged, Clone remapping, `Changed`, iterative traversals and
      the 2,048 live depth cap, `game:Clone()`, 100-character names, 256 attributes/tags at the source) (`bca443ca`);
      M2-10, M1-05/21/25/27/36, M8-24 (client intent retries, loud stubs for ~90 known classes and 28 known services,
      class hierarchy from `Object`) (`d2216d38`).
- [x] **Replication resync** — MP-13 (a resync onto a non-empty replica failed on the first known id), MP-20
      server side (`99eaa660`).
- [x] **Binder** — M8-04 (`CanCollide = false` as in Roblox), M1-08/14/15/16/30 (`4b47d48d`); M1-09/10/11/20
      (nested parts independent, live pose of unanchored parts, last values readable in destruction handlers,
      Size clamp at the binder) and the trigger-ignoring ground probe with its PlayMode test (`a571fbd6`).
- [x] **Mods, Debris and bindings pass 1** — M8-05/06/09/12/16/17, M1-12/17/19/20/22/33 (`360c57b0`): a foreign
      actor can no longer kill or move a character; Debris refuses services and does not leak.
- [x] **Mirror bridge** — MP-03/04/05/07/08/09(client)/12(provider)/14/15/16/18/23 and the new MP-25 (kcp2k
      negative connection ids) (`cdf65b52`, which also corrected the Mirror code comments of MP-21). This docs pass
      corrects the "crosses a socket" claims and the stale Phase 0 limits (the rest of MP-21 is open below).
- [x] **Players and Humanoid** — MP-12 world half (no identity source on a server → `NOT_AUTHORITY`), M8-12/18/19/27,
      M1-03 Humanoid/Player half (`e2099108`).
- [x] **Runtime** — M2-08 (quarantine counts faulting frames), ownerless scheduler faults logged once, WebGL
      registration ceiling 4,032 under the 4,096-instance save budget (`2e9ed931`).
- [x] **Bindings pass 2** — M1-03/05/07/21/31, M8-14, M2-10/24 binding half (`f1b8bbb5`).
- [x] **Build and CI** — the `core` and `lua` legs (no `COREAI_LLM`) compile again (`32dbe28d`); engine-free Rbx API
      tests run on Linux in the portable suite (`4a4c80c2`).
- [x] **Mirror sessions (W3)** — MP-06 bridge half (the client's server clock comes from the server's own Unix-time
      anchors, not `NetworkTime.offset`), MP-09 server half (readiness handshake: reliable remotes held until the
      client acknowledges, unreliable dropped and counted, 10 s deadline), MP-22 (kick and supersede notices before a
      deferred drop; a client's own kick disconnects it), and `NullNetworkBridge` refusing an unreliable payload over
      1,000 B in solo too (`d6096dc6`).
- [x] **ApiBindings pass 1** — M2-06 (waits inside `coroutine.create`), M2-07/M2-18 (`ThreadRetired` releases a
      retired thread's bookkeeping, wait connection and unanswered request), M2-14 (task handles rescheduled by
      `task.spawn/defer/delay`), M2-16, M2-19 (rollback of an unfinished yield), M2-20 (native `coroutine.yield`
      parks a task, stops anything else loudly; `THREAD_CAP` names parked threads), MP-10 (per-sender budget of 32
      remote-started handler threads), M1-32, M8-22 Dispose half and unload/quarantine/disconnect destroying a mod's
      tweens, M1-28 (headless part sink wired to the registry) (`20fdd97a`). `IRbxScriptThreadTerminalFault` is
      implemented by `LuaCsRbxScriptThread`.
- [x] **W4 FILLER** — M8-10 (`CollectionService` tag globals count holders inside the DataModel), M1-36 DataModel
      part (`GetService` without a child-list copy), M1-29/M8-23 (culture-invariant error texts), M1-23 attribute
      half: refuse a non-ASCII name on create, accept it on load (`c6392287`).
- [x] **W4 CORE-C** — the `InstanceRegistry` pre-registration admission hook (a quota refusal is no longer thrown
      inside the `Registered` multicast), `Humanoid:Clone` keeps its state, a scripted `RootPart` move ends `MoveTo`
      (M8-18 second half), non-archivable characters, the `BoundProperties` drift guard, the Lua `SetAttribute` wiring
      of M1-23 (`6be0c46f`).
- [x] **W4 AB-2** — M1-06/M2-15 (`typeof`, `warn`, loud global stubs), M1-34 (`Instance` global on every tier), M2-22
      (`os.time(table)`), M2-25 + MP-06 world half (`GetServerTimeNow` re-base and slew;
      `INetworkBridge.IsServerClockSynchronized`), M8-08/M8-11 (`ClickDetector`), MP-16 (network warning throttle),
      `camera_set_cframe`/`camera_follow` fire `Changed`, `OriginTag` interned per mod, a parked task's
      `coroutine.yield` receives `task.spawn(t, ...)` arguments, and `ActorModsDisconnected` (`5fdfbf17`).
- [x] **W5C** — M2-24 runtime half (a disconnect unloads the actor's mods after their running code returns;
      host-authority mods stay; the stored package stays active), `warn` wired into the mod log, a tween of the
      `HumanoidRootPart` ends `MoveTo` (`c233c9cf`); `Player:Kick(message)` reaches the client, `GetServerTimeNow`
      on a server or in solo without drift, `ModActorLedger` as a field of the bindings that drops entries on
      unload/quarantine/failed load (`0e51e982`).
- [x] **Error values (ERRTEXT, W6-A)** — `pcall`/`xpcall`/`coroutine.resume` receive the host's one line, with no C#
      stack trace or machine path (`0321448a`); sandbox caps, guard trips and pattern trips carry the same clean line,
      `RbxError.TryParse`, `IScriptHostFailure`/`ScriptExecutionErrors.NextCause` (so `IsMemoryBudgetTrip` follows
      `HostException` in both classifiers), and a scheduler thread's fault keeps the host's §5.2.7 code under one
      prefix (`354e6248`).
- [x] **W6-B runtime tails** — a failed first load leaves no actor record; a quarantined mod keeps its actor record
      and is unloaded when its actor disconnects; a load whose chunk disconnects its own actor fails with a clear
      `InvalidOperationException`; a logic-slot formula counts as running mod code; `Player:Kick(42)` stays
      `BAD_ARGUMENT`, like every other string parameter of the Rbx surface (`FindFirstChild(5)`, `AddTag(p, 5)`,
      `Part.Name = 5`; Roblox would convert the number — see the number-coercion item below) (`7aa6f47c`).
- [x] **Lua-tier tests on Linux** — the portable runner `tools/portable/LuaTests` (`1459086c`; two platform-dependent
      tests fixed in `61bcfbef`), seven more fixtures linked after their engine-bound tests moved verbatim into
      Unity-only classes (`80f2f30c`), CI job `portable-lua` with a floor of 1,400 passing cases (`b094d878`).
- [x] **Docs (DOCS-1 `984c053f`, DOCS-2 `300eb6a4`)** — everything above is in the roadmaps, `RBX_API.md`, the
      skill, the Mirror, Hub and Instances READMEs, `LUA_SANDBOX_SECURITY.md`, `AGENT_ROLES_AND_TOOLS.md`,
      `MVP2_MULTIPLAYER_PLAN.md`, the acceptance manifests and both changelogs (Mirror runtime entries in the core
      one); MP-21 remainder: the roadmaps no longer describe a `ClientWritePolicy.Open` (owner decision 3).
- [x] **Budget trips are uncatchable and never disarm the guard (security, `1f667ade`; found by W6-A).** Lua-CSharp
      leaves `LuaState.IsInHook` set when a count hook throws, so one trip silenced every later hook on that state:
      the next runaway handler hung the host (a frozen page on WebGL) and `pcall(runaway); pcall(work)` ran `work`
      unguarded. The hook now cancels the run's token and returns; `pcall`/`xpcall` cannot catch the trip, the state
      stays guarded, and the host (or a raw coroutine's resumer) still gets the same one-line trip.
- [x] **Portable runner honesty (audit round 1 A5-02/03/06/07/08/09, `f25ca635`).** A refusal of an engine member in
      a fixture constructor, `OneTimeSetUp`, `SetUpFixture` or `TestCaseSource` makes the dependent tests
      Inconclusive (it used to be discarded, so a false pass was possible); `PortableRunnerSelfTests` pin the
      runner's rules in a nested NUnit run; CI `portable-lua` fails above 42 not-executed cases next to the 1,400
      floor; the shim's `Vector3.normalized` uses Unity's `kEpsilon`; the engine-free test project compiles as C# 9;
      the G10 no-LLM regression test exists (`RealProviderWithoutLlmModule_RefusesInsteadOfMeasuringAStub`, closing
      the open item from W1); narrative test comments became `// WHY:`.
- [x] **World package, audit round 1 (A1-01/03/04/05/06/07/08/09/10/11/13/14, `02249815`).** At most 256 distinct
      stored mod sources (the 257th refused with a forget hint; forget/unload run without backup past the limit);
      the startup selection follows the live world after every gated mutation; a confirmed load takes the shared gate
      and re-checks the network/ACL rules before publication; autosave names checked without `Path.*`; the WebGL text
      budget counts value strings and Humanoid state; request-time `invalid_package` for what the confirmation would
      refuse; manual slots capped at 64 / 256 MiB; `session_unavailable` instead of `ObjectDisposedException`;
      crash-left temporary files swept at open; `execute_lua` whose world a load replaced says so. A1-09
      (packages keep non-archivable instances) and A1-12 (the WebGL read path checks only the byte size) are
      documented in `WORLD_PACKAGE.md`.
- [x] **Instances and bindings, audit round 1 (A3-01/02/03/07/08/09, A4-05, `4d107ae6`, `5e6c8ad3`).** Every
      settable `BoundProperties` row notifies (a drift guard walks them all); `GetPropertyChangedSignal` refuses only
      events, methods and near misses, a real unmodelled property gets a never-firing signal and one log note; tag and
      attribute signal tables stay bounded (`KeyedSignalTable`), a new tag is at most 100 characters; removal
      handlers of a destroyed instance read its tombstone; an equal `MaterialVariant` write repaints nothing;
      `TweenService` reuses its step array; on `Host`/`DedicatedServer` an actor the identity source does not know is
      refused `NOT_AUTHORITY`; quota refusals from `Clone` and `TweenService:Create` name the real call.
- [x] **Mod runtime, audit round 1 (A2-04/06/08/11, A2-10 part, A3-05, `53fb23b3`).** A failed load or reload puts
      back the logic-slot formulas its chunk changed; a successful hook/timer no longer forgives a frame whose
      scheduler fault was charged; mod-API argument errors read like Lua's; `mods_call` passes the caller's token and
      is capped at a signal handler's per-resume budget; the quota attribution map no longer grows; instance quota and
      ceiling refusals are coded `BUDGET_EXCEEDED` lines that name the creation; a cancellation through a stopped
      run's host function stays a cancellation.
- [x] **Multiplayer, audit round 1 (A4-01/02/04/06/07/08/09/10/11/12/13, A3-04/06/10, A2-03/07, `70a4d1ab`).**
      Threads a remote-started handler schedules are charged to the sender; unresolved-value reports are throttled
      per sender; a client sends nothing before its admission; a malformed client payload is dropped and counted;
      `InvokeClient` to an unconnected player fails at once; the server's clock hold reaches clients through the
      anchors and a lone late anchor is set aside; a host's own `DisconnectActor` ends the connection; Mirror's
      host-mode local client is refused loudly; the protocol-mismatch limits are written down; `os.time(t)` returns
      `nil` before 1970; `camera_set_cframe`/`camera_follow` refuse without a world camera; a failed first load
      removes its `OnServerInvoke` callbacks, tweens and waits; an `OnServerInvoke` cut by its budget answers a
      fixed line.
- [x] **Sandbox, audit round 1 (A2-01/02/05/09, `07264057`).** `coroutine.resume` refuses a task, signal-handler or
      main-chunk thread before touching it (a trip inside such a resume was swallowed by `xpcall` and the rest ran
      unguarded, and a later kill of that thread crashed the process); calls from library functions back into Lua
      nest at most 200 deep per thread (a catchable `C stack overflow (…)` instead of an unwind of seconds); a raw
      coroutine is held to the memory budget of the run that resumes it.
- [x] **Docs (DOCS-3)** — the guard, the portable runner and every audit-round-1 fix above are in `RBX_API.md`,
      `LUA_SANDBOX_SECURITY.md`, `WORLD_PACKAGE.md`, `mod-system.md`, `mod-authoring.md`, `RBX_API_SKILL.md`
      (including where the skill text is now behind the runtime), the roadmaps, `AGENT_ROLES_AND_TOOLS.md`, the
      Mirror and Hub READMEs and both changelogs. The report-style files
      `dev-docs/ALLOC_SIGNALS_FINDING_2026-09-05.md` and `dev-docs/MVP_CLOSURE_AUDIT_2026-09-06.md` were folded
      into this file (their open findings are items below) and deleted (A5-11).

### Open follow-ups from the fix waves

**Persistence (MVP3 tail)**

- [ ] **`Workspace.Gravity` assigned by a script is never saved** — capture takes gravity from the host's
      `RbxWorldSettings`, so a script's change is lost on the next save. Decide whether scripted gravity is world
      state; if it is, capture it from the live Workspace.
- [ ] **Autosave the live state on quit and periodically.** Only AI mutations and loads write autosaves, so manual
      play since the last one is lost on a crash.
- [ ] **Package byte limits are reachable from Lua** — about 80 `StringValue`s at the 200,000-character cap exceed
      the 16 MiB entry limit, and the WebGL byte/text budgets are not bounded at the source. Pick a policy (a source
      cap, or a capture diagnostic) so a script cannot build a world that cannot be saved.
- [ ] **Verify that a lone UTF-16 surrogate** reaching a name or string from Lua cannot break the strict UTF-8
      `WritePackage` (a test that writes one through `Instance.Name` and `StringValue.Value`).
- [ ] **Test isolation:** `FileLuaModSourceStore` has no root parameter, so DI tests still write mod sources into the
      real `persistentDataPath`; PlayMode compositions with `applicationIsPlayingProvider => true` and a null
      `storeId` read the shared `Saves/Startup`. The Hub's startup metadata read does not validate the package.
- [ ] **The live-network-session guard is conservative:** it counts any registered actor on a non-`Solo` bridge,
      including one registered for a mod context whose socket is gone. Exact peers are known only to
      `MirrorNetworkBridge`; expose them through `INetworkBridge` if the guard ever refuses a legitimate load.
- [ ] **Mods restart in id order, not in load order (audit round 1 A1-02, MAJOR; fix in progress).** Both restore
      paths (`RehydrateExactOrThrow`, `RehydrateFromStore`) start mods in ordinal id order and capture sorts them by
      id, so a world whose mod B reads at init what mod A created cannot reload its own save — the restore is
      all-or-nothing. *Owner:* the A1-02 fix worker of this wave. *Plan:* a durable `LoadOrder` on the mod manifest,
      stamped on a first load (max over the store + 1) and kept on reload; both restore paths start mods by it, legacy
      manifests without it by id; the package carries the field.
- [ ] **No way to delete a manual save.** A store at its 64-slot / 256 MiB cap (`02249815`) refuses every further
      `save_world`, and until then the player can only delete files under `persistentDataPath/CoreAI/Saves/Manual`
      by hand, which a WebGL player cannot do. *Owner:* Hub + world package. *Plan:* a Hub **Manual saves** section
      with a player-only two-step delete through a trusted surface kept apart from `IRbxWorldRuntimeService` (as
      `IRbxWorldStartupSelection` is), backed by a `FileRbxWorldPackageStore` delete that confirms durability like
      every write; no AI tool gains a delete path (DoD (c)).
- [ ] **Hub Mods-page edits bypass the shared gate.** They take no pre-mutation autosave and do not refresh the
      startup selection, so an edit reaches the startup entry only with the next gated mutation. *Owner:* the owner
      decides, then the Hub pass implements. *Plan:* either route the page's mutating actions through
      `ConfirmedWorldMutationGate` (autosave and refresh like `manage_mods`), or have the page call a controller
      refresh after each edit.
- [ ] **A bare `LuaCsModRuntime` composition gets only the store's 256-source refusal,** which is logged: its 257th
      mod runs without a persisted source and is missing from every save. *Owner:* mods runtime. *Plan:* move the
      distinct-source check from the world-session facade into the runtime's load path, so every composition
      refuses the 257th mod before its chunk runs.
- [ ] **Owner question — manual-slot defaults on WebGL.** 64 slots / 256 MiB per store apply on every platform;
      the browser's storage quota may call for lower defaults there. *Owner:* the owner (decision). *Plan:* pick
      WebGL defaults (or keep these) and pass them through the store constructor in the WebGL composition.

**Scheduler, sandbox and budgets (MVP2)**

- [ ] **Allocation-budget follow-ups:** measure the per-resume heap-read cost on Boehm/IL2CPP (a player row in the
      VM benchmark); a mid-frame forced collection on WebGL is untested; consider an allocation field on
      `LuaCsCoroutineBudgetSettings`; revisit the high-water notes in `LuaCsModRuntimeEditModeTests` (around the
      memory-budget cases).
- [ ] **Pattern budget:** the 5,000,000-step cap is per call; a step-charging API on the resume guard (so pattern
      work counts against the resume's own budget) is optional. The fence has a gap after a nested
      `coroutine.resume`.
- [ ] **Stale prose, one left:** the `WebGlUnsafeAsyncPrimitives` allowlist note for `LuaCsSecureEnvironment`
      ("coroutine resume and the string.format wrapper"). Done: `LUA_SANDBOX_SECURITY.md` (DOCS-2), the
      `IScriptCoroutine` summary and the
      `HandlerMaxAllocatedBytes` docs (`c233c9cf`), the `RbxNumberValue` summary (`0e51e982`).
- [ ] **`RegisterCallback(string, LuaFunction)` still leaks CLR exceptions:** a raw `LuaFunction` registered through
      that escape hatch is not wrapped in `LuaCsHostFunctionException`, so its CLR exception text reaches `pcall`.
      No production code calls it; wrap it or document it as a Lua-errors-only seam.
- [ ] **Scheduler stragglers:** a host that only calls `Advance` (never `Tick`) never releases a disconnect that
      was reached from a scheduler thread; `R4.10` raw-thread interop stays unsupported by design (a
      `coroutine.create` thread cannot be scheduled); M8-22 remainder — a service that is itself destroyed does not
      detach its host (only `Dispose` does).
- [ ] **Runtime ceiling plumbing:** `LuaCsModStackOptions` has no pass-through for `emergencyMaxRegisteredInstances`;
      faults of a failed reload chunk are charged to the live instance's streak; a host that calls only `Advance`
      never closes a quarantine frame.
- [ ] **Error line numbers:** errors raised through a tail call report line 0 (the traceback's existing behaviour).
- [ ] **`__concat` recursion is not counted by the C-call cap** (after `07264057`): Lua-CSharp runs a `__concat`
      metamethod as a nested VM call of its own, where no sandbox code sits, so `mt.__concat = function(a, b) return
      a .. b end` still unwinds in quadratic time with no hook firing — 2.6–4.2 s at a mod-imposed depth of 1,000,
      22.8–28 s unbounded under a 10 s budget. *Owner:* Lua runtime. *Plan:* get the call exposed by `Lua.dll`
      (upstream, with the `IsInHook` report below), or let the guard hook scan the Lua frames for `CONCAT`
      re-entries once the frame count grows and raise the same `C stack overflow` line.
- [ ] **A scheduler-owned thread starts at C-call depth 0** even when host code resumes it from inside a library
      call; the thread quota and the signal-generation cap bound that nesting today. *Owner:* Lua runtime. *Plan:*
      have the scheduler's resume carry the resumer's depth as the thread's base, as a raw coroutine's does.
- [ ] **Host callbacks that re-enter Lua outside `CallCountedAsync`** (`warn`'s `DescribeForLog`, and any other
      host function that runs a script's function or `__tostring`) are not counted by the C-call cap. *Owner:* Lua
      runtime. *Plan:* route every such call through `LuaCsSecureEnvironment.CallCountedAsync`, with a nesting test
      per callback.
- [ ] **Guard invariant: a host function that calls back into mod code must pass the token it received** (after
      `1f667ade`; `07264057` closed the one path a mod could open itself, raw resume of a handle's thread). A
      callback run with another token (or none) lets a trip inside that call be caught by a `pcall` in it.
      *Owner:* Lua runtime. *Plan:* audit every host function that re-enters Lua (`mods_call`, signal
      wrappers, `gsub` replacement functions, `__tostring` in `string.format`, `__index`) for the token it
      passes, and add one uncatchable-trip test per re-entry path.
- [ ] **Host cancellation does not reach a raw coroutine mid-resume.** Each raw coroutine's trip source is its
      own for life and is not linked to the resumer's token, so cancelling the host run waits for the coroutine's
      own budget. *Owner:* Lua runtime. *Plan:* link the per-coroutine source to the resumer's token for the
      duration of each resume only (a registration disposed when the resume ends), keeping a trip from ever
      cancelling the caller.
- [ ] **Report the `IsInHook` leak upstream.** Lua-CSharp's `ExecutePerInstructionHook` resets
      `LuaState.IsInHook` only when the hook returns normally. *Owner:* the maintainer. *Plan:* file an issue with
      a reproduction (and a PR resetting the flag in a `finally`) at `nuskey8/Lua-CSharp`; once a fixed release is
      vendored, drop the `EndGuard` `DebugLibrary.SetHook` workaround and keep the regression tests.
- [ ] **`mods_call` exports from other callers still get the full handler budget** (A2-10 residual, after
      `53fb23b3`): from a `task.*` thread, the main chunk or a `hooks_on`/`hooks_every` handler an export runs under
      50,000,000 steps / 10 s. *Owner:* Lua runtime. *Plan:* expose the running guard's remaining budget from
      `LuaCsCoroutineHandle` and `LuaCsExecutionGuard`, and let `ResolveExportGuard` cap every export at it.
- [ ] **Failures after a successful build skip the rollback** (after `53fb23b3`): "loaded concurrently", the second
      `EnsureModCapacity` and "reloaded concurrently" leave the Rbx candidate, the logic-slot formulas and the quota
      attribution as the failed build left them. Only concurrent loads of one id reach these paths. *Owner:* Lua
      runtime. *Plan:* run the same rollback the build-failure path runs on each of them, with a test that forces
      each branch.
- [ ] **Number-to-string coercion for string parameters.** Mod APIs (`LuaCsValueMarshaller`) and the Rbx surface
      refuse a number where a string is expected; stock Lua and Roblox convert it. *Owner:* the owner (decision).
      *Plan:* keep the refusal everywhere (documented in `mod-authoring.md` and pinned by
      `Negative_Kick_WithANumberMessage_IsRefusedLikeEveryOtherStringArgument`), or convert in one shared reader
      for both surfaces and flip that test.
- [ ] **A2-07 reads every cancellation as a budget cut:** an `OnServerInvoke` callback stopped by any
      `OperationCanceledException` — a world dispose included — answers "the RemoteFunction callback was stopped: it
      exceeded its execution budget". *Owner:* Lua bindings. *Plan:* answer the budget line only when the cause is a
      guard trip (`LuaCsHostFunctionException` with a trip `HostException`), and a disposal line otherwise.
- [ ] **Signal handlers fired from a sender-charged thread are charged to the handler's owner** (A4-01 follow-up,
      after `70a4d1ab`): threads a remote-started handler schedules with `task.*` are charged to the sender, but a
      signal that handler fires starts its handlers on the owners' quotas. *Owner:* scheduler. *Plan:* carry the
      charged actor on the signal invocation and create the handler thread under it, with a flood test like the
      A4-01 one.
- [ ] **`RunService:BindToRenderStep`/`UnbindFromRenderStep` are MVP2-phased loud stubs,** so MVP2 cannot close
      while they are (from the 2026-09-06 closure audit). *Owner:* MVP2 closure. *Plan:* implement them on the
      scheduler's render phase with the priority order Roblox documents, or re-phase the stubs and amend §5.2.4.

**Instance core and bindings (MVP1)**

- [ ] **The tag cap is enforced on `RbxInstance.AddTag` only;** `InstanceTagStore` itself does not count tags per
      instance, so another path could still exceed 256. `ReplicationApplier` should apply removals before additions
      so a replica at the cap cannot refuse a legitimate swap.
- [ ] **`AncestryChanged` during `Destroy`:** the child argument's tombstone readability is not pinned by a test.
- [ ] **Change notifications on a replica:** every settable `BoundProperties` row notifies on the server since
      `4d107ae6` (a drift guard walks them all), but part and camera properties do not reach a replica as patches,
      so their `Changed` never fires there (engine-free members do — `ReplicationApplierEditModeTests`);
      `dev-docs/REPLICATION_PHASE0.md` says so. *Owner:* MVP12. *Plan:* carry part and camera state in replication
      patches and fire `Changed` on apply.
- [ ] **A demoted keyed signal cannot be re-homed without a weak reference** (after `4d107ae6`): C# host code that
      takes a tag or attribute signal, requests 64 or more other keys, then connects — keeping neither reference —
      can connect to a signal the table already let go. *Owner:* instance core. *Plan:* an internal
      first-connection callback on `RbxScriptSignal` so `KeyedSignalTable` re-homes a demoted signal the moment it
      gains a connection.
- [ ] **`ClickDetector.MouseHoverEnter`/`MouseHoverLeave` exist and never fire** (A5-04/A5-05; the backlog `TODO`
      on `PumpClicks` in `LuaCsRbxApiBindings.cs`); the roadmap lists them as backlog now. *Owner:* Roblox ladder
      backlog. *Plan:* track the hovered part across frames in the click pump (one ray per frame, enter/leave on a
      change, same `MaxActivationDistance` rule as `MouseClick`) with a test per edge.
- [ ] **The `Instance.new(className, parent)` deprecation test loads one mod,** so it cannot tell once per mod from
      once per process (`Lua_InstanceNew_DeprecatedParentArgument_WorksAndLogsOnce`; from the 2026-09-06 closure
      audit). *Owner:* test hygiene. *Plan:* load two mods, as `Lua_R4_9_LegacySchedulerDeprecation_LogsExactlyOncePerMod`
      does, and expect two notes.
- [ ] **Assigning to an event** reports "not a valid member" instead of read-only.
- [ ] **Bindings housekeeping** (`ModActorLedger` became a field that drops entries on unload in `0e51e982`;
      `OriginTag` is interned per mod since `5fdfbf17`): `RbxTweenService.DescribeGoal` prints CLR type names; the
      tween property host does not consult the catalog for unknown members (`{Sit = true}` reads as unknown rather than a
      stub); `Camera.FieldOfView` needs a camera-rig API before it can be bound.
- [ ] **Input enum names:** mouse input objects in `RbxUserInputService` (`:195`, `:214`) look up `KeyCode.Unknown`,
      which now resolves only as the alias of the mirror's canonical `None` (value 0); look up `None`, and fix the
      `RbxInputObject.KeyCode` summary ("`Enum.KeyCode.Unknown` for non-key input").
- [ ] **Spatial validation covers Lua writes only;** the tween host and the binder bypass it. `PivotTo` does not note
      a teleport, so a pivoted part can fire `Touched` on arrival.
- [ ] **`ModScheduler` has no host-callback cancel API,** so `Debris` cannot keep exactly one scheduler callback
      (today at most nine).
- [ ] **Binder:** velocity is not read back from the body; host `CFrame` writes are not orthonormalized at the sink;
      an adopted host object's `Size` is not clamped. Host trigger volumes can block click picking (optional).
      `CanTouch`/`CanQuery` stay backlog stubs.
- [ ] **TweenService and hot reload:** tweens survive a hot reload of their mod, like its parts, and a failed
      reload's candidate tweens and waits are not cancelled (the A2-03 reload case; a failed first load cleans them
      since `70a4d1ab`), because `RbxTweenService` has no generations. *Owner:* mods runtime. *Plan:* tag tweens and
      waits with the scheduler generation of the build that created them and cancel a failed candidate's generation;
      decide at the same time whether a successful reload cancels the old instance's tweens.
- [ ] **`RbxVector3.Lerp` overflows** to infinity near ±3e38 (engine-free datatypes).

**Players, Humanoid and multiplayer (MVP8/MVP11/MVP12)**

- [ ] **Disconnect and load tails (after M2-24, `c233c9cf`/`7aa6f47c`):** decide the failed-load policy — sweep the
      instances a failed first load's chunk created and clear their registry attribution (the quota attribution
      `_quotaActorByOwnerModId` is dropped since `53fb23b3`; callbacks, tweens and waits since `70a4d1ab`); an
      active package is not restarted when its actor rejoins (today only `RehydrateFromStore` or the next world load
      start it); `DisconnectActor` of the host's own actor kills the
      host-authority mods' threads while the runtime leaves those mods loaded; a `ModTearingDown` listener that, after
      an installer kill, disconnects the quarantined mod's own actor during the quarantine teardown restores the actor
      record for a departed actor; `ModActorLedger.GetAttributedContext` creates an entry on demand without a load
      record (no current path reaches it).
- [ ] **Characters (after CORE-C):** a `RootPart` set by hand that is not named `HumanoidRootPart` does not end
      `MoveTo` when moved; once MP-11 makes the client registry a replica, decide whether registration admission (the
      instance quota) skips replica applies (`IsApplyingReplication`).
- [ ] **Clicks and players:** no composition sets `LuaCsRbxApiBindings.LocalPlayerActorId` (a host or client
      composition should name its local player; today the pump falls back to the player the camera follows, or the
      only player); `IClickPickSource` reports no hit point, so the range is measured to the part's nearest box point
      instead of where the ray hit.
- [ ] **Tag and service bookkeeping (after M8-10):** `InstanceRegistry.SetSceneRoot` changes the in-tree set without
      an event, so `CollectionService`'s holder counts go stale if the root is swapped under an attached service; a
      service replaced after a snapshot restore stays subscribed (pre-existing). M1-36 remainder: the registry's
      `ProcessPreSimulation` and `ModConnectionRegistry.PruneDead` still allocate.
- [ ] **`typeof` of `TweenInfo`, `RaycastParams` and `RaycastResult`** reads the type name from the `__tostring`
      name of their metatable, because their carrier types are private to the bindings that build them; expose the
      carrier types and name them directly.
- [ ] **Host UserIds:** a local actor's counter `UserId` can collide with a transport-admitted `UserId` on a Host.
      A `Player` destroyed directly from C# runs the leave teardown but does not `DisconnectActor` on the transport.
- [ ] **After a world swap on a Host,** new connections still use the `AttachWorld` connect delegate — acceptable
      only because loads are refused while sessions are live; part of the MVP11 handoff below.
- [ ] **Server→client encode-side filtering:** `FireClient(player, ServerStorage.X)` still sends the reference; only
      the decode side (MP-01) filters. MVP11/MVP12.
- [ ] **MP-20 remainder:** strip `OwnerActorId`/`OwnerModId`/`OriginTag`/`AccessScope` from the snapshot at capture
      for a client, not only on the replica.
- [ ] **Mirror follow-ups (after MIRROR-3, `d6096dc6`):** an authenticator refusal still reaches the client only as
      a dropped connection (the MVP2.5 item "A refused client never learns it was refused") — the owed-drop pattern
      the kick notice now uses is the fix shape; `Players.MaxPlayers` is not checked against the transport's
      capacity; after a Mirror restart without disconnect reports the bridge may keep stale acknowledged-connection
      state; the scene provider measures server time against the system clock and has no public way to take the
      world's `IRbxClockSource` (a composition with its own clock must build the bridge itself with the `wallClock`
      argument).
- [ ] **Test harnesses:** `RungZeroEnvelopeEditModeTests`' `ForgingNetworkBridge` reports `Topology.Host`, so the
      fail-closed identity rule (MP-12) refuses its `ConnectActor` — give the harness an identity source.
- [ ] **The staging bridge does not forward the server clock** (A4-08 follow-up, after `70a4d1ab`):
      `StagedNetworkBridge` passes `IsServerClockSynchronized` and `DisconnectActor` through but not
      `IsServerClockHeld`, `AttachServerClock` and `DetachServerClock`, so a world loaded at runtime never hands its
      clock to the Mirror bridge. *Owner:* multiplayer. *Plan:* forward the three members like the other two, with a
      test that a staged world's `GetServerTimeNow` and hold reach the anchors.
- [ ] **A stale forward anchor during a hold can run a client ahead** (after `70a4d1ab`): while a hold floor is set,
      one reordered non-held anchor ahead is taken at once and can move the client up to 5 s ahead until the next
      anchor. *Owner:* Mirror bridge. *Plan:* a two-strike rule for a non-held forward anchor while a hold floor is
      set, the mirror image of the set-aside rule for a lone anchor behind.
- [ ] **A client drops even reliable remotes sent before its admission** (A4-04, `70a4d1ab`; documented in the
      Mirror README), so a script that fires at startup loses them. *Owner:* Mirror bridge. *Plan:* queue reliable
      sends (bounded, like the server's 256-message / 256 KiB hold for a joining connection) until the admission is
      bound, keep dropping unreliable ones.
- [ ] **Retire `ReplicationDirtySet`'s actor-id overload** (A5-04; the `TODO` in `ReplicationDirtySet.Collect`): it
      is the one path that still hands a recipient removals for ids it never saw. *Owner:* MVP12 wire phase.
      *Plan:* once every caller holds a `ReplicationStream`, remove the overload and its null-stream branch.

**Build and test hygiene**

- [ ] **`SharedLlmUnity.cs` (PlayMode `LlmInfra`) has unreadable fragments** where an earlier commit stripped Russian
      prose from its XML docs and its error string (`///   LLM + LLMAgent    PlayMode .`). Rewrite them in English
      and search the tree for other stripped fragments (runs of three or more spaces inside `///` comments and string
      literals).
- [ ] **Link `Mvp3WorldPackageFollowUpEditModeTests` into the Lua-tier runner:** move its 21 `[UnityTest]` cases
      (`UniTask.ToCoroutine`; three more since `02249815`) and the cases that call the internal
      `RbxWorldStartupSequence` verbatim into a Unity-only class; the rest then run on Linux
      (`tools/portable/LuaTests/README.md`).
- [ ] **Move `Lua_A3_10_CameraGlobals_WithNoCamera_RefuseBeforeMovingAnything`** (`70a4d1ab`) from
      `RbxClockLuaBindingsEditModeTests.cs` into `RbxCameraLuaBindingsEditModeTests.cs`, where the camera tests live.
      *Owner:* test hygiene. *Plan:* move it verbatim in the next test-touching wave.
- [ ] **MVP2 DoD item 13 ("reaches `OnServerEvent` next drain")** — check that a test asserts the handler has NOT
      run before the drain; if none does, a synchronous dispatch would pass (from the 2026-09-06 closure audit, not
      re-checked since). *Owner:* test hygiene. *Plan:* add the pre-drain assertion to the loopback remote test.
- [ ] **The skill text is behind the runtime** (after audit round 1): `GetPropertyChangedSignal`'s near-miss rule,
      uncatchable budget trips, `os.time(t)` returning `nil` before 1970, the client clock hold — listed in
      `Docs/CoreAIMods/RBX_API_SKILL.md`, "Where the runtime has moved past the skill text". The Instances README
      (`Assets/CoreAIMods/Runtime/RbxApi/Instances/README.md`) also still lacks the destruction tombstone of
      `ChildRemoved`/`DescendantRemoving`/the tag removed signal and the 100-character tag rule. *Owner:* the next
      docs pass. *Plan:* edit `RbxApi.txt` and `BuiltInRbxApiSkillText.cs` together (they stay byte-identical) and
      the Instances README, then fold that subsection of `RBX_API_SKILL.md` back into its list.
- [ ] **Stale MVP1 paths in the roadmap** (from the 2026-09-06 closure audit): the §5.1.1 and §5.2.1 task
      breakdowns still name planned `RobloxApi/…` files (`RobloxApi/Spatial/RobloxSpace.cs`,
      `RobloxApi/Scheduling/TaskLibrary.cs`, …); the code lives under `Assets/CoreAIMods/Runtime/RbxApi/` with other
      file names. *Owner:* the next docs pass. *Plan:* map each row to the shipped file and rewrite the paths (the
      MVP1 status paragraph and the `CornerWedge` fallback sentence were corrected in DOCS-3).

## 7.45.0 audit wave: 7.44.x re-audited, pipeline cancellation unified, docs swept (2026-09-24)

Five audits (7.44.2 `call_skill_tool`; 7.44.0/7.44.1 cancellation; 7.44.0/7.44.1 chat panel; English docs,
two passes) and the fixes they led to — see both CHANGELOGs, 7.45.0. Verified in Unity 6000.3.14f1:
full EditMode 5133 total / 5122 passed / 0 failed / 11 skipped (2026-09-19); full PlayMode 157 total against
the local muse bridge, then every failing case re-run against LM Studio (`huihui-ai/qwen3.8-27b-abliterated`):
all pass except the castle showcase (below); both real-model Stop tests pass (2026-09-24); portable
1518 / 1518. The two EditMode failures of the first run and one PlayMode failure were test setups, not code:
a panel built on an inactive object never reaches `OnEnable`, and `DeadlineCancellation` counts in both
`CancelledCompletions` and `DeadlineCancelledCompletions` by design.

- [ ] **Castle showcase live test** (`RbxCastleMaterialsShowcaseLivePlayModeTests`) times out after 3000 s on
      a local 27B model at ~25 tok/s, and the muse bridge produced no tool calls at all. Model speed and
      capability, not a CoreAI defect; re-run it on a fast tool-calling endpoint (OpenRouter free tier is an
      option once a key is configured) before claiming the scenario green.
- [ ] **Owner decisions raised by the audits (code unchanged, docs describe today's behaviour):**
      remove the deprecated `CoreAiLuaWorldModule` Full flags and the ignored `RegisterWorldCommands`
      parameters entirely; wire `LuaCsAiEnvelopeProcessor` into a composition or delete it (nothing constructs
      it); expose custom world-command handlers and `LuaCsModStackOptions.AdditionalGameplayBindings` through
      the standard composition (`TODO(moddableunits-binding-seam)`); `ICoreAISettings.EnableLuaOnWebGl` is
      serialized but read by nothing; `CircuitBreakerLlmClientDecorator` is public but not composed by the
      default pipeline; `LoggingLlmClientDecorator.Unwrap` stops at `TimeoutLlmClientDecorator`, not at the
      backend; the default chat UI strings (stop/send/clear tooltips) are Russian;
      `conversationRolledSummaryMaxTokens` is 0 on the asset but 2048 in the portable defaults; `Max Concurrent`
      below 4 is raised to 4 by `LlmPipelineInstaller`.
- [ ] **Stale XML docs and comments** found during the docs sweep: `LuaCsGameToolExecutor`,
      `LuaCsAiEnvelopeProcessor` and `LuaCsModRuntimeFactory` still name MoonSharp-era types
      (`IGameLuaRuntimeBindings`, `CoreAI.Sandbox.LuaApiRegistry`, `GameLuaToolExecutor`);
      `CoreAIBuildMenu.TryCreateLlmUnityObjects` still says LLMUnity is called through `Chat(...)`;
      `CoreAIG11WebGlBuild.Build` says "frozen 15-scene set" while `FrozenScenePaths` holds 17.
- [ ] **In-place mutation of a client's result outside failure re-classification (low):** `AiOrchestrator`
      rewrites `Content` of a successful result, `StartsNewMessage` and `Text` of streamed chunks, and
      `CoreAiChatPanel.SetExternalFailure` / the formatted-content path rewrite `outcome.Completion` in place.
      Failure rewrites are copies since 7.45.0 (`WithError`); these are the remaining sites.
- [ ] **`FileLuaScriptVersionStore` re-reads its file on every synchronous call** (the planned cache was never
      built) — see `dev-docs/MOD_SYSTEM_DESIGN_NOTES.md`.
- [ ] **CI Unity jobs** still stop at "UNITY_LICENSE is required" — the secret has to be added by the owner.

## Newcomer API ergonomics audit (2026-09-24)

A newcomer-persona audit of the 7.45.0 tree: install from README/INSTALL through UPM Git URLs, then a chat agent, a
custom tool, a Lua mod, save/load. Every finding was re-read against the code before it went in, and an item says
so where verification changed the claim. IDs are the audit's (A bug, B docs/install, C API, D quick win); the
report itself is not kept. Line numbers are at `61ad743c`. D7 (`InstanceTreeSerializer` raw `FormatException`) is
fixed in parallel (`c7b1f44e`) and not listed.

- [ ] **[A1, CRITICAL] Setup menus hardcode `Assets/<package>/…` paths and break on the Git-URL install.**
      `CoreAIChatDemoSceneCreator.cs:28-29,177-183`, `CoreAISettingsAssetEditor.cs:29,90-97`,
      `CoreAiHubSetupMenu.cs:19-22,75`: from `Packages/com.neoxider.*` README step 3 builds a chat with no UXML
      (Play shows an empty screen), the settings inspector falls back to the default one, and `Add Hub` saves into
      a missing folder then instantiates `null`. Fix: load by GUID or `PackageInfo.FindForAssembly`; see F-22. S–M.
- [ ] **[A2+B3+D9, MAJOR] A missing `COREAI_LLM` silently swaps the backend for `StubLlmClient` (`Ok = true`).**
      `LlmPipelineInstaller.cs:271-277,380-381` log nothing and `CoreAiBackend.Status` shows the configured mode;
      the quick starts omit the define (only INSTALL §2.3 names it) and `Enable Providers` sets it for one target,
      so `AskAsync(…, "Blacksmith")` returns `ApplyWaveModifier` JSON as the NPC's line. Fix: warn in the
      `!COREAI_LLM` branches now; then a failing `NotConfigured` client, a build check, a quick-start step. S/M.
- [ ] **[A3, MAJOR] `StopAgent(roleId)` and `CancelTasks(scope)` cancel nothing in the default composition.**
      `QueuedAiOrchestrator.cs:1130-1147` gives every task an `ActorContext` (the inner `AiOrchestrator` resolves
      it) and `CreateScopeEntry` (`:1164`) keeps only its session GUID, so `ResolveCurrentScopeKeys` (`:692-708`)
      never matches a role id or a domain scope (broader than the audit's facade-only claim; the queue tests use a
      fake inner). Fix: keep logical scope + role in `ScopeEntry`; test via the real composition. S.
- [ ] **[A9+C4, MAJOR] `AgentBuilder.Build()` before CoreAI starts registers nothing, and unknown roles run as a
      default agent, silently.** `AgentBuilder.cs:505-515` applies only when `CoreAIAgent.Policy` is set, which
      happens in `IStartable.Start` (`CoreAIGameEntryPoint.cs:96`); an unknown or miscased id gets the memory tool
      only (`AgentMemoryPolicy.cs:631-638`). A blacksmith built in `Awake()` answers with no prompt and no tool.
      Fix: queue configs until `Initialize`, warn once per unknown role, README uses `blacksmith.AskAsync`. S–M.
- [ ] **[A5+C1, MAJOR] `CoreAi.AskAsync` returns a provider error as if it were the reply.** `SourceTag = "Chat"`
      (`CoreAiChatService.cs:384-394`) makes `AiOrchestrator.RunTaskAsync` (`:317`, `:2932-2952`) return the error
      text, so `"HTTP 401 …"` becomes NPC speech or quest JSON; `StreamAsync` throws and `AgentConfig.AskAsync`
      returns `null` instead. Fix: `CoreAi.AskResultAsync` / `SmartAskResultAsync` over the existing
      `SendMessageResultAsync`; document the string contract in COREAI_SINGLETON_API. S.
- [ ] **[A6, MAJOR] `SmartAskAsync` drops streaming errors.** `CoreAiChatService.SendMessageSmartAsync`
      (`:689-709`, `:724-747`) joins `chunk.Text` and never reads `chunk.Error`; the facade adapter
      (`CoreAi.cs:382-400`) forwards text only. After a 500 or a dropped stream the caller saves `""` or half an
      answer (streaming is on by default for tool roles). Fix: throw like `StreamAsync` on a terminal error chunk
      through one shared helper; regression test with an `{IsDone, Error}` stub. S.
- [ ] **[A7, MAJOR] `AgentMode` is not enforced.** `Mode` is read only for build warnings and the streaming default
      (`AgentBuilder.cs:654-669,858`): `ChatOnly` still offers every tool, memory included, and `ToolsOnly` keeps
      chat history, contrary to the enum docs and AGENT_BUILDER. Fix: carry `Mode` into `PreparedAgentRole`
      (ChatOnly strips tools / `ToolMode = None`, ToolsOnly defaults history off) after one release of warnings, or
      rename/deprecate the modes and fix the docs. M.
- [ ] **[A4, MAJOR] `CoreAi` keeps a dead chat service after a scene change.** `TryResolve`
      (`CoreAi.cs:845,878-881`) re-resolves scope and orchestrator (a destroyed scope compares `null`; the audit
      overstated this) but never `_chatService`, which holds the old disposed queue: `IsReady` is true, `AskAsync`
      fails. The documented `Invalidate()` also runs `CoreAIAgent.Reset()`, which nothing re-initialises while the
      new scope owns the facade. Fix: drop caches of a destroyed scope; spare a live owner. S.
- [ ] **[A8, MAJOR] Runtime key/backend switching writes into the shared `CoreAISettingsAsset`.**
      `CoreAiBackend.SetApiKey` / `Apply*` (`:154-181,326-329`) mutate the Resources asset in place: in the Editor
      a key "supplied at runtime" (QUICK_START) outlives Play Mode, aborts the next build in
      `CoreAIResourcesApiKeyBuildGuard` and is saved by the next inspector edit. Fix: a `[NonSerialized]` session
      overlay the getters prefer, or an Editor clone; see the [R7.5] hot-swap item. M.
- [ ] **[A10, MINOR] `AgentMemoryPolicy.AddToolForRole` (`:232-250`) appends duplicates.** A
      `CoreAi.RegisterGameStateTool()` in `OnEnable` publishes two `game_state` functions after one toggle; nothing
      de-duplicates downstream (`AiToolOrder.Canonical`, `MeaiLlmClient.cs:2878-2915`) and strict providers reject
      duplicate names. Fix: replace by name (ordinal) and return whether anything changed. S.
- [ ] **[A11, MINOR] `LlmToolBase.JsonParams` (`ILlmTool.cs:184-199`) does not JSON-escape.** A quote, backslash or
      newline in a description yields an unparseable schema: the text path prints it to the model and the
      `ParametersSchema` half of the required-argument union is lost (the bound function's half still applies, so
      the check is not fully disabled as claimed). Fix: build with `JObject` / `JsonConvert.ToString`; test it. S.
- [ ] **[A12+C11, MINOR] `AgentConfig.AskWithCallback` posts `onDone(null)` on failure and outlives its caller.**
      `AgentConfigExtensions.cs:137-191`: without a `SourceTag` a failed turn returns `null`, so
      `r => label.text = r.ToUpper()` throws although the doc promises "after a successful response"; with no token
      a destroyed NPC still gets the callback. Fix: skip `onDone` on `null` (or add `onError`), add a
      `CancellationToken` overload and a Unity overload bound to `owner.destroyCancellationToken`. S.
- [ ] **[A13, MINOR] With Domain Reload off, `CoreAi` tool events and tool-call history survive Play sessions.**
      `ResetForSubsystemRegistration` (`CoreAi.cs:959-973`) clears `CoreAiEvents` (the [A6] runtime item) but not
      `OnToolExecuted`, `OnToolCall*`, `OnToolCallRecord` or `ToolCallHistory` (replayed by `SubscribeToolCalls`),
      nor `CoreAiBackend.OnBackendChanged`. Fix: null them and call `ClearToolCallHistory()` in that hook only. S.
- [ ] **[B1, MAJOR] The "fix my install" menus cannot compile until the install is fixed.**
      `CoreAIDependencyInstaller` / `CoreAINuGetBootstrapper` sit in `CoreAI.Editor`, which needs `CoreAI.Core`,
      `CoreAI.Source` and VContainer, so with MEAI or a Git dependency missing the menus INSTALL §1.1 and
      QUICK_START prescribe do not exist; §1.1 also runs before §1.2 installs CoreAiUnity. Fix: a reference-free
      setup asmdef (Unity-generated `.meta`), manual manifest block first. S–M.
- [ ] **[B2, MAJOR] `Newtonsoft.Json` is an undeclared dependency.** The `CoreAI.Core` / `Source` / `Editor`
      asmdefs reference `Newtonsoft.Json.dll`, but no CoreAI `package.json` declares
      `com.unity.nuget.newtonsoft-json`; here it arrives via LLMUnity and others, and INSTALL §1.1 tells HTTP-only
      users to remove `ai.undream.llm`, after which a fresh project fails with CS0246. Fix: declare it in
      `com.neoxider.coreai` and list it in INSTALL. S.
- [ ] **[B4, MAJOR] `[Inject]` MonoBehaviour samples leave the field null.** QUICK_START_FULL §5.2 Option A,
      EXAMPLES.md (four samples), DEMO_RECORDING_GUIDE: a scene MonoBehaviour is injected only through Auto Inject
      Game Objects, `RegisterComponent` or the resolver, none mentioned, and `CoreAILifetimeScope` is sealed; the
      copied script throws `NullReferenceException` in `Start`. Fix: `CoreAi.*` / `GetOrchestrator()`, `[Inject]`
      only in an advanced-DI note that names Auto Inject Game Objects. S.
- [ ] **[B5, MAJOR] The canonical tool template hangs on WebGL once its body awaits.** TOOL_AUTHORING_GUIDE
      (`:92-114`, checklist `:147-165`) hands an `async Task<string>` straight to `AIFunctionFactory.Create` and
      never mentions `MeaiToolTaskBridge.Publish` (MEAI_TOOL_CALLING §3.2): a `UnityWebRequest` in the body works
      in the Editor and hangs silently in the player. Fix: template through `Publish(ExecuteCoreAsync(...))` plus a
      checklist line; the framework half is C2/C3. S.
- [ ] **[B7, MINOR] Agent samples double-register and pair `ChatOnly` with `WithMemory`.** QUICK_START §7 and
      AGENT_BUILDER Recipes 4–5 call `ApplyToPolicy(CoreAIAgent.Policy)` after `Build()`, which throws
      `ArgumentNullException` before CoreAI starts; `AskAsync` already registers lazily. Fix: drop those lines and
      `.WithMemory()` from the ChatOnly sample (or fix A7 first). S.
- [ ] **[B6, MINOR] EXAMPLES.md:24 still offers "implement `ILlmTool` / subclass `LlmToolBase`" as the escape
      hatch;** such a tool is skipped at request time (`MeaiLlmClient.cs:2910-2913`). Fix: "and implement
      `IAIFunctionLlmTool`", link TOOL_AUTHORING_GUIDE. S.
- [ ] **[B8, MINOR] No host-facing "save and load a world" recipe.** `Docs/CoreAIMods/WORLD_PACKAGE.md` has no C#;
      `IRbxWorldRuntimeService` (`RbxWorldPackageContracts.cs`) is named only in the FullAccess demo README. Fix: a
      short section (resolve the service, save, load, handle `player_confirmation_required`) once the in-flight
      world-package changes land. S.
- [ ] **[B9, MINOR] RBX_API.md:3-4 promises "mirrors Roblox 1:1 … without a rewrite"** while the same page lists a
      closed `Instance.new` set and `BindToRenderStep` / network ownership as stubs. Fix: "Roblox names and
      semantics for the subset below; unsupported members fail loudly" plus a short "Not supported yet" table. S.
- [ ] **[C3] Class-based tools are unsafe by default.** An `LlmToolBase` without `IAIFunctionLlmTool` passes
      `WithTool` and is skipped per request with a warning; `ParametersSchema` defaults to `"{}"`, which the text
      path omits even when the bound function has parameters (`AiToolContractPromptFormatter.cs:215`; the audit's
      AGENT_BUILDER example has none, so it is unharmed). Fix: `LlmFunctionToolBase` with the WebGL bridge and a
      derived schema, plus a `ToolWithoutFunctionBinding` build issue. M.
- [ ] **[C2] Generic delegate overloads, so C# 9 callers need no `new Func<…>(…)` wrapper.**
      `DelegateLlmTool(string, string, Delegate)` (`:54`) and `AgentBuilder.WithAction(…, Delegate)` (`:339`) force
      the wrapper README has to teach. Add `Func<…>` / `Action<…>` overloads whose `Task` variants apply
      `MeaiToolTaskBridge.Publish` (closes B5 for delegates). Additive. S.
- [x] **[C7] `Instance` is nil below `WorldEdit`** — fixed as M1-34 (`5fdfbf17`): the `Instance` global exists on
      every tier with `Read`, and `Instance.new` without `WorldEdit` raises the capability error before it reads or
      creates anything. Was: `LuaCsRbxApiBindings.cs:2064-2067` registered it only for WorldEdit, so `Instance.new`
      in the FIRST_MOD sample (`Read | LogicOverride`) failed with "attempt to index a nil value".
- [ ] **[C10] `AgentConfig.ClearMemory()` clears chat history only** (`AgentConfigExtensions.cs:196-201`), while
      "memory" means the long-term store everywhere else. Fix: `ClearChatHistory()`, `ClearLongTermMemory()`,
      `ClearContext(chat, longTerm)` mirroring `CoreAi.ClearContext`; mark `ClearMemory` obsolete. S.
- [ ] **[C9] Two "backend" dropdowns in the settings Essentials card** (`backendType` "LLM Backend" and
      `executionMode` "LLM Mode", `CoreAISettingsAssetEditor.uxml:14-17`); `executionMode` wins unless `Auto`
      (`CoreAISettingsAsset.cs:1273`), yet TROUBLESHOOTING's stub fix names the other one. Fix: show `backendType`
      only under `Auto` as a legacy preference; point TROUBLESHOOTING at `LLM Mode`. S.
- [ ] **[C8] `SmartToolCallingChatClient` takes 13 constructor parameters (two bools) and the tools twice**
      (`SmartToolCallingChatClient.cs:49-55` and `ChatOptions.Tools`); the core README and console sample build the
      second list with an `(IAIFunctionLlmTool)` cast that throws for `IAIFunctionsLlmTool`. Fix: an options object
      and a `ToAITools` helper used when `ChatOptions.Tools` is null; old constructor `[Obsolete]`. S–M.
- [ ] **[C5] No `CoreAiMods` facade.** FIRST_MOD (b) makes every host resolve `ILuaModRuntime` and an unrestricted
      `ActorContext` from the mods container and pass it to each call; listeners are positional
      `Action<string, string, string>`. Fix: a static facade in `com.neoxider.coreaimods` (load, emit, a typed
      disposable `OnModEvent`). Additive. M.
- [ ] **[C6] No portable composition root.** README's "plain .NET app" row advertises agents, but outside
      VContainer nothing builds `AiOrchestrator` (11 required dependencies, seven without null checks,
      `AiOrchestrator.cs:57-81`), so the .NET sample stops at `SmartToolCallingChatClient`. Fix:
      `CoreAiRuntime.Create(llm, settings, …)` returning the queued orchestrator and policy, plus null guards. M.
- [ ] **[D1] `AgentMemoryPolicy.GetToolsForRole(null)` throws `ArgumentNullException`** (`:835-841`), while
      `GetRoleConfig(null)` falls back to `Creator`. Fix: the same fallback. S.
- [ ] **[D2] The `AgentBuilder.WithTemperature` remark is wrong** (`:363-373`): "applied only when the active
      settings allow temperature overrides", but `AiOrchestrator.cs:2911` sends a role temperature whenever set. S.
- [ ] **[D3] `CoreAi.AskWithImageFollowUpAsync` returns `Task<string>` under `#nullable enable`** (`CoreAi.cs:310`)
      although its doc says it returns `null`. Fix: `Task<string?>`. S.
- [ ] **[D4] `new AgentBuilder("  ")` is accepted** (`AgentBuilder.cs:78-82`) and fails only later in
      `ApplyPreparedRole`, and only when a policy exists. Fix: an `IsNullOrWhiteSpace` check in the constructor. S.
- [ ] **[D5] The license-free `portable-core` CI job compiles the core with `LangVersion latest`**
      (`tools/portable/CoreAI.Core.csproj:4`), so a fork PR can land C# 10+ syntax that Unity rejects. Fix: `9.0`,
      as the RbxApi and Luau csprojs already do. S.
- [ ] **[D8] `Instance.new` of an unsupported class** (`InstanceRegistry.cs:806-812`) hints only "like Part,
      Folder, or Model". Fix: list the creatable classes from `ClassCatalog` and say when a known Roblox class is
      not supported yet. S.

Checked and not defects: D6 (`num.ToString("G")` in `LuaCsWorldRuntimeBindings.cs:542` only formats the `parent`
name of the legacy `coreai_world_spawn` / `coreai_world_change` bindings, which the shipping composition does not
register; an integer name formats identically in every culture).

## 7.44.2 skill-tool required arguments (2026-09-17)

Production trace (RedoSchool b303): the model called `submit_task_verdict` through `call_skill_tool` with
`comment` instead of the required `reason`; MEAI threw a bare binder message from inside the invocation, the
retry round ran into the host's turn deadline. Fixed: `call_skill_tool` checks required arguments before
binding with an actionable message; one `LlmToolRequiredArguments` rule for both tool paths (empty string
is present, `null` is missing); delegated refusals are logged; the `call_skill_tool` description no longer
demands `read_skill` when the exact call is given. Verified in Unity 6000.3.14f1 by the full EditMode
suite on 2026-09-17: 4949 total, 4938 passed, 0 failed, 11 skipped (new cases in
`SkillToolAvailabilityEditModeTests` and `ToolExecutionPolicyEditModeTests`).

- [x] **Release:** committed, pushed, tagged `v7.44.2`.
- [ ] Move RedoSchool's manifest pins to the current tag and refresh its `packages-lock.json` hashes.
- [x] **Built-in tools and the empty-string change:** `execute_lua` refuses whitespace-only code itself
      (`LuaToolEditModeTests.ExecuteAsync_WhitespaceOnlyCode_ReturnsErrorWithoutCallingExecutor`). The 7.45.0
      audit found three more built-ins that relied on the policy refusing `""` — `save_world`, `load_world`
      and `load_autosave` threw from inside the tool body; they now refuse an invalid name as a JSON result.
- [ ] **Watch the empty-string change in consumer tools:** tools outside this repo that relied on the
      policy refusing `""` for a required parameter now receive it. A consumer-reported case goes here.

## 7.44.0 timeout-vs-cancel wave (2026-09-17; gate run 2026-09-19)

The fix (`LlmCancellation`, the panel's `ResolveCancelledMessage`, resend dedupe in `AiOrchestrator`,
caller-token guards in fallback/retry) was verified only by `dotnet build` of `CoreAI.Core`,
`CoreAI.Source`, `CoreAI.Core.Tests` and `CoreAI.Tests`, plus a console harness that ran the orchestrator
history, streaming-timeout, `EndsTurn` and classifier scenarios against the built `CoreAI.Core.dll` (all
green; a mutation that disabled the dedupe and the streaming classification turned five of them red).
The Unity-side fixtures never ran.

- [x] **Verification gate — ran with 7.45.0:** the full EditMode suite is green (5133 / 5122 / 0 / 11) and
      `AiOrchestratorHistoryEditModeTests` now runs in the portable CI leg as well. Originally listed:
      run `CoreAiChatPanelResolveTimeoutMessageEditModeTests`,
      `AiOrchestratorHistoryEditModeTests`, `MeaiStreamingToolCallEditModeTests`,
      `ResilienceFeaturesEditModeTests`, `LoggingLlmClientDecoratorEditModeTests`,
      `RoutingLlmClientEditModeTests` (assembly `CoreAI.Tests`, namespace `CoreAI.Tests.EditMode`) and the core
      `LlmErrorPresentationEditModeTests` (assembly `CoreAI.Core.Tests`, namespace
      `CoreAI.Core.Tests.EditMode`), then the full EditMode suite. The `MeaiStreamingToolCall` fixture compiles
      only with `COREAI_LLM`.
- [ ] **Open question, not a defect:** the resend rule compares the history tail byte-for-byte. A host that
      stamps its re-sent payload (timestamp, attempt counter) still stores two copies. If that shape shows
      up, the fix belongs in the host (a stable payload), or in an explicit request-level idempotency key -
      not in fuzzy matching here.

## Idle-timeout watchdog: our allocation-free variant was dropped, on purpose (2026-09-10)

The hot-path wave replaced `CoreAiChatService.IdleTimeoutDeadline` with a single watchdog task per
turn: `Rearm()` became two writes (a timestamp and a counter) instead of disposing a scheduled handle
and creating a new one for every chunk. At 30-60 tokens a second that difference is the whole point
of the wave.

It was **dropped in the merge with main anyway**, and the reason is worth keeping. `main` had grown a
virtual-clock test suite around the timer shape (`SchedulerOverride`, ~10 tests that advance a fake
clock), while our watchdog measured real time and its own fixture had never once run green in the
editor - the assembly it lives in was hanging. Shipping the unverified variant and deleting the
tested one, on nothing but a plausible allocation argument, would have been trading a measured
property for an unmeasured one.

**To redo it properly:** keep the watchdog, but route its sleep through the same injectable seam the
timer variant exposes, so the virtual-clock tests keep working against it. Then measure - a Profiler
capture of a real streamed turn, before and after - and let the number decide. Without that number
this is a preference, not an optimisation.

## One frame, one driver — the trap that cost sixteen tests is still armed (2026-09-10)

**Fixed:** the merge of `fix` and `main` left two frame drivers in the tree. `LuaModRuntimeTickDriver`
subscribed to `Scheduler.PhaseReached` while `LuaCsRbxApiBindings.PumpSchedulerPhase` had just been
widened from input-only to all six phases, so every phase ran twice: `SDHIR` came out as `SSDHHIRR`,
"fires exactly once" became two, sixteen tests failed at once. The driver's subscription is gone; a
frame is one `Scheduler.Advance()`, the same call the shipping host makes.

**Still open, and it is a trap rather than a defect:** the public per-phase surface (`PumpInput`,
`PumpPreAnimation`, `PumpPreSimulation`, `PumpPostSimulation`, `PumpHeartbeat`, `PumpPreRender`,
`PumpFrame`) is still there, and seven fixtures still call one of them next to `Advance`. They are
green only because nothing they assert counts occurrences — which is exactly what makes it a trap:
the shape survived the wave that was cleaning it up, and the next person to copy one of those
fixtures as a template re-arms it.

**The fix is to make `Advance` the only entry point** and delete the public `Pump*` surface, moving
the twelve call sites over. That is the real change; it was left out of the 7.40.0 wave deliberately,
to keep a defect fix from turning into an API change under time pressure.

**A text guard was tried and rejected**, and the reason is worth keeping so nobody re-tries it: a
check for "one method that both pumps and advances" fires 31 times on the current tree, and a large
share of those are legitimate — `Mvp8PlayersCompletionEditModeTests` deliberately asserts that the
character motor does NOT move on the render pump, which requires calling both in one test. Telling
intent apart needs more than a regex, and a guard shipped with a 31-entry allowlist would be worse
than none: it teaches people to add entries.

## WebGL storage gate shipped with its fix (2026-09-09) — closes a 7.37.0 pre-publication blocker

`CoreAIWebGlPersistentDataSyncBuildGuard` travelled inside `com.neoxider.coreaiunity` while the
template satisfying it lived in CoreAI's own `Assets/WebGLTemplates`, outside both packages: a
consumer got an unconditional build failure pointing at a path that does not exist in their project.
Confirmed on RedoSchool, whose own template lacked the line.

- [x] The template ships in the package as `Assets/CoreAiUnity/WebGLTemplates~/CoreAI`. Unity builds
      the template list from `Application.dataPath/WebGLTemplates` plus the editor installation
      (`WebGLTemplateManager` / `WebGlBuildPostprocessor.UpdateHTMLTemplatePath`), so a package cannot
      publish a selectable template — `CoreAIWebGlTemplateInstaller` + `CoreAI/Setup/Install WebGL
      Template` copy it into `Assets/WebGLTemplates/CoreAI` and select it.
- [x] Every failure message names project-local actions: the exact line, the menu item, the opt-out.
- [x] `COREAI_WEBGL_NO_PERSISTENCE` (Web platform scripting define) stands the gate down and logs one
      warning per build; the warning says the symbol is redundant when the template arms storage anyway.
- [x] Tests: 20 for the guard, 6 for the installer, including a drift check between the packaged
      template and the copy installed in this repository. Verified with 13 planted defects, each caught
      by the intended test, executed outside Unity against the real sources.
- [ ] **Verification gate (next editor session):** run
      `CoreAIWebGlPersistentDataSyncBuildGuardEditModeTests` and
      `CoreAIWebGlTemplateInstallerEditModeTests` in the Unity Test Runner — the editor was held by a
      full EditMode run, so only `dotnet build` (CoreAI.Editor, CoreAI.Tests) and the portable suite
      (1317/1317) were used as gates here.

### Demo scene smoke — cause found and fixed, 2026-09-06

`CoreAiDemoScenesSmokePlayModeTests` did not "hang since 2026-08-30". Three separate causes, two of
them introduced by the demo work in `ebe8c365`/`374bafbb`:

1. the frozen list grew to 17 scenes while `Assert.AreEqual(15, FrozenDemoScenePaths.Length, ...)`
   was left behind, so the test failed on its first statement;
2. failing *before* the first `LoadSceneMode.Single` left the Test Framework's bootstrap scene
   loaded, and `PlayModeSceneSandbox.UnloadToEmptyScene` then unloaded the scene holding the test
   runner itself — the run never finished and no results file was written. A failing assertion
   turned into a dead batchmode editor. The sandbox now skips the runner's scene.
3. Independently: with the local, gitignored `Assets/Mirror` present, Mirror's
   `NetworkScenePostProcess` calls `EditorApplication.isPlaying = false` on a scene `NetworkIdentity`
   without a sceneId, which aborts the whole PlayMode run ("Playmode tests were aborted because the
   player was stopped"). PlayMode demo-scene evidence must therefore be taken **without** Mirror
   installed — which is also the shipped configuration, since Mirror is optional.

The earlier A/B that concluded "pre-existing, unrelated to the demo work" was wrong; it is recorded
here so the wrong conclusion is not inherited.

Once it ran to the end it immediately earned its keep: it caught that `OnlineAuthorityDemo` — a
scene built in the same session — carried no `CoreAILifetimeScope` at all. The builder now creates
the standard composition like every other demo, and the scene was regenerated.

## MVP2.5 rungs — status 2026-09-10

Verified on the settled tree (Unity 6000.3.14f1 batchmode): full EditMode
**4481 total / 4472 passed / 0 failed / 9 skipped** (`artifacts/testresults/r17.xml`), and the
full-assembly PlayMode sweep **149 total / 135 passed / 4 failed / 10 skipped**
(`artifacts/testresults/pmall3.xml`) — the 4 failures are environmental (3 need a loaded live
LLM, 1 needs a graphics device), the same rows that were invisible while the sweep aborted
before reaching them. The earlier figure on this line was 4424/4415 on 2026-09-10
(`final2.xml`).

**MVP2.5 is MVP3 + MVP8 + MVP11 + MVP12** (`dev-docs/MVP25_ONLINE_PLAN.md` §3, one rung per
release; `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` :3, "the three remaining MVP2.5 rungs (MVP8,
MVP11, MVP12)"; entry gates P1–P5, with P4 a full pass of the MVP2 manifest). The persistence
release 7.3.0 (see below) shipped save/load; MVP3 as the roadmap defines it now — the world/place
package — is code complete and closed on the Linux suites, and its release waits for the Unity gate
(top section); MVP8 is acceptance-manifested in the next item;
**MVP11 and MVP12 are unimplemented as rungs** — concretely, verified against the tree:
`INetworkBridge` has no `SendIntent`/`IntentReceived`/`SendDelta`/`DeltaReceived`, there is no
`ReplicationPublisher` type, `LuaModManifest` has no context field (and no raise site checks a
mod's script context — the two production `ContextViolation` raises guard an ownerless executor
and an orphan signal owner, not server/client contexts), and nothing in `Assets/CoreAIMirror`
calls `ExportSnapshot`/`Stage`/`Commit`. MVP2.5 is therefore half built, not nearly closed.

**Two different bars, and they do not agree — read both before calling anything closed.**
The paragraph above is the PLAN's bar: four rungs, the last two of which move world state over a
socket. The OWNER's bar, stated 2026-09-10, is narrower and is the one that decides a release:
MVP1, MVP2 and MVP2.5 exist so that **core Lua scripting works and the core FOUNDATION of
multiplayer exists** — not so that a finished networked product ships.

Against the owner's bar, verified against the tree on 2026-09-10:
- **Lua scripting core: done.** Roblox-shaped API, sandbox, per-resume execution budgets,
  scheduler, instance tree, save/load. 4481 EditMode tests, 0 failed.
- **Multiplayer foundation: present, and not as stubs.** `MirrorNetworkBridge` is a real Mirror
  implementation — connections, actor admission, request timeouts, packet/byte counters, server
  clock offset — with `CoreAiMirrorSessionHost`, `CoreAiMirrorAuthenticator` and a message format
  beside it. The authority model exists: `WorldAclAuthorizer`, `WriteGrantLedger`, `MutationIntent`,
  `IntentGateway`, and `InstanceRegistry.Authority` separating server from replica. The replication
  core exists with tests: `ReplicationDirtySet`, `ReplicationStream`, `ReplicationApplier`. And
  RemoteEvent/RemoteFunction — the Roblox-facing multiplayer surface a mod author actually uses —
  cross that bridge from PRODUCTION code, not only from tests.
- **Two gaps on this bar, not one, and the composition gap was the deeper of them.** The claim that
  stood here until 2026-09-10 — "the one gap is that the replication layer and the transport are not
  connected" — was wrong by omission, and a gpt-6 closure audit caught it. Nothing in PRODUCTION ever
  built the transport at all: `MirrorNetworkBridge` and `CoreAiMirrorSessionHost` were constructed
  only in EditMode tests, `Assets/CoreAIMirror/Runtime/` held four plain classes and no
  MonoBehaviour, and nothing registered an `INetworkBridge`, so `CoreAiModsInstaller.cs:171` and
  `:371` always fell back to `NullNetworkBridge`. A game that installed Mirror still could not switch
  multiplayer on without writing the wiring itself. There was nothing to connect replication TO.
  - **Composition gap: CLOSED 2026-09-10.** `RbxNetworkBridgeProviderBehaviour` (explicit serialized
    reference per ARCHITECTURE_RULES par.2 — no scene reflection, no static singleton), an optional
    field on `CoreAiModsLifetimeScope` registering it LAZILY as `INetworkBridge`, and
    `CoreAiMirrorNetworkBridgeProvider` behind the `MIRROR` define. An empty field leaves behaviour
    byte-identical, so every existing scene is untouched. Known limits, recorded rather than hidden:
    the side (server/client) is an explicit serialized choice, because the bridge is built inside the
    installer's build callback — still in `Awake`, where `NetworkServer.active` is false and there is
    nothing live to read; and the game must still call `provider.AttachWorld(...)` and set
    `Players.IdentitySource` itself, both documented on the provider type. (Since `cdf65b52` the
    `AttachWorld(Func<LuaCsRbxApiBindings>)` overload sets the identity source itself, and a server world
    without one refuses a transport-admitted actor with `NOT_AUTHORITY`.)
  - **Replication gap: STILL OPEN.** The replication layer and the transport are not connected to
    each other. There is no publisher joining them, world state never crosses a socket, and no
    two-process run has been done. Remotes replicate; world state does not.
  - **And the seam proved the foundation is NOT one gap short.** A gpt-6 closure audit
    (2026-09-10, its verdict in `PROGRESS.audit20.md`) was asked to attack the belief that the seam
    made no dead defect live. It destroyed that belief: making the transport reachable exposed three
    HIGH defects that could not be seen while nothing constructed it. All three verified by hand
    afterwards. **Do not describe the owner's bar as one replication gap short — it is not.**
    - [x] **Server-to-client remotes do not arrive (HIGH, blocks the owner's bar).**
          `MirrorNetworkBridge.OnClientEvent` and `OnClientRequest` built
          their message with `null` identity, so `LuaCsRbxApiBindings.cs:1421` -> `RbxRemotes.cs:90`
          /`:122` rejects the event and the function callback lookup at `:1575` rejects the null.
          `FireClient`/`FireAllClients` — half the Roblox remote surface — never reach a mod. Broken
          even when the remote ids match. This is the first thing a real user would hit.
          **Fixed in 7.42.0 (commit 85dce51d).** Root cause was one level deeper: the client never
          learned its own actor id — `CoreAiAdmissionResponseMessage` carried only `Admitted` and
          `Reason`. And the refusal was a kick, not a drop: `DeliverNetworkEvent` rethrows the
          `RbxError`, Mirror's `exceptionsDisconnect` then disconnects the client on the first
          `FireClient`. Now the response names the admitted actor on acceptance (still nothing on a
          refusal — `CoreAiMirrorAuthenticator.Respond` is the one place that decides it), the
          provider binds it on the client bridge from the accept event and forgets it on the frame
          the client side stops, the client bridge stamps it as the recipient of every inbound remote
          and keeps a broadcast's `ServerToAllClients` direction, and a remote with no admission
          bound is dropped, counted in `UnadmittedPacketsDropped` and said once. Host contract made
          explicit: the client composition's `IActorIdentityProvider` must issue the same durable
          actor id the server's `IActorAdmissionProvider` admits, or `FireClient` reaches a signal no
          local script holds — the bridge says so once. Tests: `MirrorClientRemotesEndToEndEditModeTests`
          (real authenticator both ends over `OfflineMirror`'s new loopback, real world bindings;
          compiles against the pre-fix code and fails there), `MirrorClientRemoteRulesEditModeTests`,
          `MirrorClientAdmissionEditModeTests`. Run in Unity 2026-09-16: all 65 `CoreAI.Net.Mirror.Tests`
          cases pass (`artifacts/testresults/edit_r3.xml`). The "compiles against the pre-fix code"
          claim above is the author's, from an intra-day working state; it is NOT replayable from
          history, because the provider it references is new in the same change set.
    - [x] **A server broadcast reached unadmitted connections (HIGH) — fixed in 7.42.0.** `SendEvent`'s
          broadcast now iterates the ADMITTED set instead of every Mirror connection; pinned by
          `MirrorBroadcastEditModeTests`, including a witness-validity negative that proves the
          observer would see an event that did reach a stranger. Recorded here because the INBOUND
          half of the same finding is still open (next item). What it was: `SendEvent`
          iterated every `NetworkServer.connections` entry, not the admitted actors, and Mirror's
          `NetworkConnection.Send` checks size, not admission. Related and STILL OPEN: the limiter is applied
          only outbound (`SendEvent`, `SendRequest`), never on receive (`ReceiveServerEvent` and
          `ReceiveServerRequest` bind
          the sender and dispatch), so an admitted custom client bypasses the intended server gate.
    - [ ] **No inbound rate limit on the server's world dispatch (HIGH, now reachable).**
          `MirrorNetworkBridge` calls `_rateLimiter.Admit` only on the OUTBOUND path (`SendEvent`,
          `SendRequest`); `ReceiveServerEvent` and `ReceiveServerRequest` resolve the sender and
          dispatch with no budget at all, so an ADMITTED custom client can flood the server's Lua
          scheduler. Before the composition seam nothing in production built this bridge and the hole
          was dead; the seam makes it live, which is why it is listed here rather than as a nicety.
          Deliberately NOT fixed in 7.42.0 and called out in the release notes: `Admit` throws
          `RbxError`, and these two methods run inside Mirror's handler wrapper where a throw
          disconnects the client, so the fix needs a drop-and-count design rather than a two-line
          insertion. The broadcast half of the same finding WAS fixed in 7.42.0 (a broadcast now goes
          to the admitted set instead of every Mirror connection). Partly bounded since `20fdd97a` (MP-10): the
          handler threads a sender's remotes start are capped at 32 per sender (a `RemoteFunction` over it is answered
          `BUDGET_EXCEEDED`, an event is dropped and counted), and since `5fdfbf17` (MP-16) receive warnings are
          logged once per sender and kind every 10 s — but decoding and dispatch themselves stay unmetered.
    - [ ] **A Mirror HOST has no client-side remotes (HIGH).** `MirrorNetworkBridge.AttachHandlers`
          installs the handlers of ONE side, and `CoreAiMirrorNetworkBridgeProvider`'s role guard
          refuses a client bridge while the server is active, so a host - server plus local client in
          one process, the most common Mirror setup - has no client-side remotes at all. Pinned, but
          honestly: `MirrorRestartEditModeTests.KnownLimitation_HostMode_AServerBridgeDoesNotServeTheLocalClient`
          records today's behaviour and its message says outright that it is a limitation and not a
          contract, and that it must be rewritten rather than kept green when host mode is fixed.
          (Until 7.42.0 that same test asserted the behaviour as intended.) Since `cdf65b52` actors registered in
          the server process are served in process (`LocalDeliveries`), which is not a local Mirror client.
    - [x] **`Player:Kick()` did nothing over the transport — fixed in 7.43.0.** `KickPlayer` fired
          `PlayerRemoving`, removed the `RbxPlayer` and unloaded the character, but never told the bridge:
          the socket stayed open, the connection still resolved to that actor, and the kicked client's NEXT
          remote re-created its player through `EnsureNetworkActor`. The session host leaked the entry too,
          because a later disconnect returned early once the peer binding was gone. A moderation primitive
          that silently does nothing. Dead while nothing in production built the transport; live the moment
          the composition seam shipped in 7.42.0, and found by the round-2 audit of that release.
          `INetworkBridge` now carries `DisconnectActor(actorId)`; the Mirror bridge runs the teardown as
          `ServerClosed` and drops the connection. Pinned by `MirrorKickEditModeTests` (the kicked
          connection's next remote is dropped as unadmitted and no player is re-created) and by a session-
          host test asserting `PlayerRemoving` fires ONCE, with the kick reason rather than a second
          `Unknown` from the drop's own teardown.
    - [ ] **A runtime world load does not hand live Mirror sessions to the new world (MEDIUM) — the MVP11 entry
          item, a known limit since 7.43.0.** Mitigated, not solved: since `82649c98` a world load is refused
          (`network_sessions_active`, the live world unchanged) at request, confirmation and raw host load while
          the bridge lists registered actors on a non-`Solo` topology, and since `cdf65b52`
          `AttachWorld(() => stack.GameplayBindings.RbxApi)` makes the session host the identity source of
          whichever world is live. The handoff itself (re-announcing admitted sessions to the new world) is
          still MVP11. Original analysis:
          `RbxWorldRuntimeSessionController.LoadConfirmedAsync` builds a NEW `LuaCsRbxApiBindings` over a
          `StagedNetworkBridge` around the same `INetworkBridge`, publishes it as `CurrentRbxApi` and
          disposes the outgoing one in `ShutdownOutgoing`. `CoreAiMirrorNetworkBridgeProvider.AttachWorld`
          is once per provider (a second call throws) and keeps the two lambdas it was given; nothing
          re-targets them and nothing sets the new world's `Players.IdentitySource`. So sessions admitted
          before the load are not re-announced to the new world: their Player appears there on its first
          remote (`EnsureNetworkActor`; `PlayerAdded` fires then, with a counter UserId unless the game
          set IdentitySource again), and their later disconnect reaches whichever world the lambdas
          resolve at call time — the disposed first world, if the game captured
          `stack.GameplayBindings.RbxApi` once as the README used to show. The README now reads the
          `LuaCsModStack` facade inside the lambdas and lists this under Known limits. Recommended
          follow-up: either refuse `LoadConfirmedAsync` while a Mirror provider holds live sessions, or
          give the provider a re-target seam guarded by zero live sessions that also re-sets
          `Players.IdentitySource` on the newly published world.
    - [ ] **`INetworkBridge.DisconnectActor` has an empty default body (LOW).** A third-party bridge
          that owns sockets compiles without overriding it and silently keeps the socket open on kick —
          the 7.43.0 defect again, one implementation over. The 7.43.0 upgrade note covers it for now.
          Consider making it abstract at the next major, or a one-time warning when a non-loopback
          bridge (`Topology` other than `Solo`) still lists the actor in `ActorIds` after
          `DisconnectActor` returned.
    - [ ] **A refused client never learns it was refused (functional, not a security hole).**
          `CoreAiMirrorAuthenticator.OnAdmissionRequest` does `conn.Send(Respond(result))` and then
          `ServerReject(conn)` on the very next line. With kcp2k — Mirror's default transport, and the
          only one CoreAI scenes would use — the disconnect is synchronous, so `conn.Cleanup()` wipes
          the still-unflushed batch before Mirror's late-update flush. The `Admitted=false` branch of
          `OnAdmissionResponse` and `ClientReject()` are therefore dead in production: a refused client
          cannot tell "refused" from "server vanished". Mirror's own `BasicAuthenticator` guards against
          exactly this with `DelayedDisconnect(conn, 1f)`. The fix is to defer the reject by at least a
          frame — `LateUpdate` of the same frame is NOT enough, because `NetworkLoop` schedules
          `NetworkLateUpdate` at the END of `PreLateUpdate`, after `MonoBehaviour.LateUpdate`, and the
          batch flush lives there. Verified 2026-09-16 while fixing the loopback harness; the refusal
          itself is sound (the connection IS dropped, no actor and no admission record are created).
    - [ ] **Restart ordering is unproven, not proven.** `CoreAiMirrorNetworkBridgeProvider.HookTransport`
          reattaches during
          `Update`, after Mirror's `NetworkLoop` EarlyUpdate, and the new tests insert a `Frame()`
          before delivering a packet, so they do not exercise a packet arriving in the same frame the
          transport came back. The stop/start-within-one-frame window is still open and documented in
          the `HACK:` comment.

So on the plan's bar MVP2.5 is half built; on the owner's bar it is NOT close — the composition
gap closed and three transport defects opened in its place.
Whichever bar a future reader uses, they should say WHICH — most of the disagreement in this file's
history comes from two people silently using different ones.

**The live streaming test, resolved 2026-09-16 — and a correction to an earlier claim.**
`CoreAiChatDemoRealModelWebGlPlayModeTests.CoreAiChatDemo_RealModel_StreamsStopAndRecovers` requires
that partial text becomes visible in the UI while the turn is still running. EIGHT endpoints were
measured against it; every one fails, for one of two OPPOSITE reasons:

| endpoint | visible-content window | why it fails |
|---|---|---|
| opencode CLI bridge | whole answer at once | nothing partial to sample |
| claude CLI bridge | whole answer at once | same |
| LM Studio `ling-3.0-tiny` | 0.16 s | same |
| LM Studio `minicpm5-2b` | 0.53 s | same |
| LM Studio `spark-x2.5-4b` on GPU | 0.24 s | same |
| LM Studio `spark-x2.5-4b` on CPU | 13 s, but 52 s to first token | borderline |
| LM Studio `qwen3.5-4b-mtp` | none — whole budget in `reasoning_content` | nothing visible at all |
| remote `qwen3.8-27b`, thinking disabled | — | 90 s timeout, turn still running |

Two of those models ignore `enable_thinking: false` outright, which is the remedy the test's own
failure message recommends, so the documented workaround does not work on them either.

**What was done.** The test's SECOND phase already treated this exact condition as a skip
(`if (stopTask.IsCompleted) Assert.Ignore("Real model completed before Stop could cancel it…")`); the
first phase hard-failed on it. That asymmetry was the defect, and the first phase now classifies the
outcome instead: a turn that ERRORS still fails, a turn that completes with an EMPTY answer still
fails, and a turn that ignores Stop still fails as a product hang. Only "produced a real non-empty
answer with no observable intermediate state" became a skip — the one case a live test cannot
distinguish from "the model was too fast". Stop is used as the live probe that splits a slow endpoint
from a hung product.

**Coverage is not lost**, and that is checked rather than asserted: the SAME demo scene runs the SAME
stream/stop/stream-again scenario against a stub orchestrator
(`CoreAiChatDemoScene_WithStubOrchestrator_StreamsStopsAndStreamsAgain`),
`StopAgent_BusyFalseCallback_StartsSuccessorWithoutOldTailClobber` asserts chunk text in the live
label while the panel is busy, and `StreamingChat_ReasoningDeltas_StayOutOfIncrementalVisibleText`
pins incremental visible text directly. All green.

- [ ] **What IS lost, and worth revisiting.** No deterministic test drives the live HTTP-SSE path all
      the way to the label — only this test did. And a skip in phase one skips phases two and three
      with it, even though phase two's 80-line prompt is the MOST observable request in the file. If a
      non-reasoning local model ever lands on this machine, re-check whether phase one can go back to
      hard-failing, or split the phases so a fast endpoint still exercises the Stop contract.

**The correction:** it was recorded here on 2026-09-10 that the bridge proved the live tests only ever
needed "a model that answers". That holds for the four live rows that went green. It does NOT hold for
this one — and the reason is not the chunking strategy, since it fails identically against a bridge
that replays a finished answer and one that streams tokens live. It is first-token latency plus a
visible phase shorter than the sampler.

**Endpoint choice for the mandatory sweep:** `opencode muse`. Measured against `codex spark 5.3` on the
same tree: muse 149 passed / 1 failed, spark 146 passed / 3 failed — spark's emulated tool-calling
produces no tool calls, so the castle, crafting and backend-switch rows collapse. Tool-calling fidelity,
not model size, is what the live suite needs from a bridge.

- [ ] **An empty string is reported as a MISSING required argument.**
      `ToolExecutionPolicy.IsMissingArgumentValue` counts a null, empty or whitespace string as an
      ABSENT required argument, so a `string` parameter the model deliberately sends as `""` is refused
      at schema validation with "missing required argument(s)". MEAI itself would bind it: the value is
      assignable and the binder passes it through. It reads like a deliberate rule rather than an
      oversight and is not enum-related, so it was left alone when the enum false-rejection was fixed on
      2026-09-16 — but it is the same class of defect (the preflight refusing a call the binder would
      have run) and the message misleads: the argument is present, it is empty. Decide whether the rule
      is intended; if it is, the message should say "empty", not "missing".
- [ ] **The allocation backstop's trip can be delayed by a whole confirmation on a dirty heap.**
      `LuaCsAllocationBudget.Reset` takes its baseline with `GC.GetTotalMemory(false)` — deliberately
      garbage-INCLUSIVE, because Unity's Mono returns 0 from `GetAllocatedBytesForCurrentThread`. The
      class documents the consequence honestly: live growth is understated by whatever garbage was on
      the heap at `Reset`, so a trip can be LATE but never FALSE. That bias is the right one for a
      backstop — a false trip kills a legitimate script. Recorded because it is a security backstop whose
      latency is heap-dependent, not because it is wrong: a bomb launched into a session that has just
      accumulated garbage gets one extra confirmation window before it is cut. **Changed by `a5c453f4`
      (M2-05):** the non-trip path no longer re-baselines the trip reference from the post-collection
      reading — that rule forgave the live growth each confirmation had just measured, and a doubling bomb
      passed a 256 MB budget on its way to a 1 GB string. The reference now only ever moves down; a cleared
      suspicion re-arms the next forced collection a quarter of the budget above the post-collection
      reading; and the budget is per resume for every thread of a mod (`HandlerMaxAllocatedBytes`), not
      only for guarded one-off calls. The dirty-heap latency described here is unchanged.
      Found 2026-09-16 when `LuaCsGuardFrameAndAllocationEditModeTests.RetainedGrowthBeyondBudget_IsAMemoryBudgetTrip`
      began failing in the full sweep while passing alone — new tests upstream left more garbage behind
      and the fixture asserted a single-call trip the documented contract does not promise. The TEST was
      fixed (it now collects before taking its baseline, so it measures the rule it states rather than
      its neighbours); production was deliberately left alone. If the latency ever matters, the fix is a
      live-heap baseline at `Reset`, which costs a forced collection per execution — measure first.

**Documentary findings from the same audit (2026-09-10), all verified by hand afterwards:**

- [ ] **The G10 verdict cites evidence that is not in the repository.**
      `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md:117` names `artifacts/g10-real.json` and
      `artifacts/g10-real.err` as the evidence for the FAILED verdict. `.gitignore:154` excludes
      `artifacts/`, `git ls-files artifacts/` is empty, and the file is no longer on disk (an agent
      last read it on 2026-09-01). The transcribed table survives; the measurement behind it cannot be
      checked from a clean clone. Either commit a curated copy of the two files or restate the verdict
      as a transcription whose source is gone.
- [ ] **"No counters/observability seam prerequisite" is stale.**
      `dev-docs/MVP2_ACCEPTANCE_MANIFEST.md:266-270` says so; `LuaCsExecutionGuard.cs:196` and
      `:401-418` plus `G10MeasurementRunner.cs:149` contradict it.
- [ ] **The G10 impossibility argument is overstated, even though the verdict stands.** The manifest
      argues from backend parallelism 1 and p95 provider latency that "forty requests cannot be served
      inside a 60 s window". The audit's objection is fair: provider p95 alone does not prove aggregate
      impossibility — the served fraction and the end-to-end latency are what actually failed. Keep the
      FAILED verdict; drop the impossibility proof or replace it with a throughput argument.

**MVP2.5 is NOT closed.** A three-rung closure audit (2026-09-06; the report was folded into this file and
deleted on 2026-09-24 — its open findings are the items here and under "Open follow-ups from the fix waves")
found that MVP8 — previously recorded here as "complete" — has gates whose positive column the code
does not implement. MVP1 is closed; MVP2 is not (its own manifest still records G10 FAILED).

Fixed on 2026-09-06 in response to the audit:

- [x] The modern RunService events (`PreAnimation`/`PreSimulation`/`PostSimulation`/`PreRender`) —
      a direct Roblox-1:1 break; they did not exist at all. Render phase now withheld where nothing
      renders (`IRbxRuntimeTopology.RendersFrames`).
- [x] The `CFrame`-teleport contact suppression, which was dead code in production: the note was
      taken in `Update` and cleared by the next frame's `FixedUpdate` before the step it was meant
      for. `Orientation`/`Rotation` writes now note a teleport too.
- [x] Humanoid state through the one serializer (it was silently dropped on every save).
- [x] `Players.RespawnTime` / `CharacterAutoLoads` / `MaxPlayers`, and the `SetNetworkOwner` family
      as loud backlog stubs — six members that were previously silently absent.
- [x] The corpus harness's "zero stub hits" guard, which did not exist.
- [x] The agent-facing skill text, which still told the LLM that TweenService and CollectionService
      raise `NOT_IMPLEMENTED`, plus a ratchet so it cannot go stale silently again.
- [x] Destroy ORDER and connection teardown; the demo-scene smoke; the raycast buffer cap; the
      open-contact leak; `Humanoid.Jump` reading a constant `false`.

### Left on MVP2.5 (named, not hidden)

- [ ] **The character pipeline and the character motor** (MVP8 gates P8.2/P8.6) — landed, still not
      closed: a second and now a third review round on this same landing keep finding real
      defects in it. **Correction:** this line was previously checked off as fully implemented,
      including a claim that `DistanceFromCharacter` reads the live root position. It does not —
      see the third-round entry below. What IS true: `RbxCharacterFactory` builds a Model +
      HumanoidRootPart + Humanoid and parents it into the world on join; `LoadCharacterAsync`
      genuinely yields, `CharacterAdded`/`CharacterRemoving` fire in order, and
      `UnityRbxCharacterMotor` is stepped from production composition. A joining player now puts a
      Model into Workspace — see `Assets/CoreAI/CHANGELOG.md` [Unreleased].
      **A follow-up review of the same landing found four more defects; all four are now also
      fixed:** the join-time spawn is deferred past the triggering dispatch instead of running
      inline mid-join, so a script has a real chance to flip `CharacterAutoLoads` first
      (`Players.EnsureActor`, `Scheduler.ScheduleHostCallback`); a dead character respawns after
      `RespawnTime` seconds via a `Humanoid.Died` handler that re-checks `CharacterAutoLoads` at
      fire time; the motor now steps from `LuaModRuntimeTickDriver.FixedUpdate`, not the render
      pump; and `BuiltInRbxApiSkillText` gained a `Players & Characters` section. See
      `Assets/CoreAI/CHANGELOG.md` [Unreleased] for detail.
       **A third and final review round (2026-09-09, two independent reviewers) found and fixed
       four more defects on this same landing — see "Third review round" below for the complete list. Genuinely left from
       this landing: the false landing between a jump and the fall (recorded separately under
       "Character motor contract" below), and the uncovered 1:1-smoke half of gate P8.2 (its own
       unchecked item below). Every other line this bullet lists now has a green test in
       `artifacts/testresults/r17.xml` (4481 total / 4472 passed / 0 failed / 9 skipped).**
- [ ] **Gate P8.2's "1:1 smoke" half is NOT covered.** The only WalkSpeed measurement runs at the
      default metres-per-stud (`Mvp8PhysicsPlayModeTests` measures 16 studs/s as 16 × 0.28 m/s);
      nothing anywhere runs the 1:1 side — no PlayMode test calls `RbxSpace.Configure(1)` — so a
      1:1 run that reported 0.28-scale speeds would still pass. The gate demands both halves
      (`dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` P8.2 row).
- [x] **The MVP8 acceptance manifest.** Gate P8.5 cites frozen ids "listed in the MVP8 manifest" —
      `dev-docs/MVP8_ACCEPTANCE_MANIFEST.md` exists, frozen 2026-09-06 against `main` (commit
      `09469f77`): 20 Tier-A fixtures, cross-checked by `FrozenTierBCatalog_MatchesItsFilesAndIds` and
      `FrozenCatalog_HasTwentyUniqueFixturesAndCompleteClassificationMetadata`. Verified present
      2026-09-09.
- [ ] **The join snapshot** (MVP11): an admitted client still receives no filtered `ExportSnapshot`
      over the wire. Phase 0 landed the layer under it on 2026-09-10 — `ReplicationStream.PlanWorld`
      seeds a recipient with every visible instance, and client Lua resolves
      `ReplicatedStorage.RemoteX` by reference in `ReplicatedWorldConvergenceEditModeTests` — but
      that runs registry-to-registry, not socket-to-socket. A stream created over a non-empty
      registry it has never observed now REFUSES to plan rather than silently sending an empty
      world.
- [ ] **Wiring the gateway to the wire** (MVP12): `SendIntent`/`IntentReceived` on the bridge. The
      dirty set and the client-side apply are no longer missing — as of 2026-09-10 the registry
      itself feeds `ReplicationDirtySet` through `RevisionAdvanced`, `ReplicationStream` plans
      ordered Spawn/Patch/Remove per recipient, and `ReplicationApplier` applies a plan to a replica
      registry with duplicate/gap/violation handling. What remains is the transport: these carry
      captured snapshots beside a sequence number, not bytes on a socket.
- [ ] **Two named Phase 0 limits of the replication core** (found by the 2026-09-10 audit, left open
      on purpose rather than half-built). First, a replicated `Player` arrives as a plain instance:
      the snapshot carries no player payload, so the replica's copy has no `UserId`, no identity, no
      `Character`, and does not appear in `Players:GetPlayers()`. Client Lua that looks a player up
      through the service will not find one. Second, a deferred reference resolves only inside the
      batch that carried it: an `ObjectValue` pointing at something the recipient cannot see becomes
      nil and the wanted id is forgotten, so moving that target into view later spawns the target but
      never repairs the reference. Both belong to the join-snapshot and transport work above; neither
       can affect anyone today, because nothing in production constructs
       ReplicationStream/Applier/DirtySet/Filter — the one exception is `IntentGateway`, which the
       shipped `OnlineAuthority` demo constructs without a dirty set, so it judges intents but feeds
       no replication.
      **Both were implemented on 2026-09-10**: a Player now travels with its identity and is admitted
      into the service on the replica, and an unresolved reference is remembered and settled when its
      target arrives. What is still open is the composition around them — a Player is minted
      whenever ANY non-server mod context is created (`LuaCsRbxApiBindings` → `EnsureNetworkActor`
      → `RbxPlayers.EnsureActor`, which never checks `registry.Authority`), so `GetLocalPlayer` on
      a replica is only one trigger among several, and a script that asks before the seed arrives
      makes the replicated Player for that actor a protocol violation. The client composition
      must stop minting Players on a replica; there is nothing to break today because the replica
      path is not wired.

- [ ] **A two-process over-the-wire run** (N11.3–N11.6). Mirror's host mode did not deliver
      client→server inside the batch-mode test runner, so the bridge's rules are gated against its
      receive paths directly and **no claim is made that bytes cross a real socket**.
- [x] **MVP2 criterion 14** (budget kill within a frame slice) — decided and implemented on
      2026-09-10. The decision: the per-resume budget is the GAME's, not CoreAI's. Both halves are a
      serialized `LuaCsCoroutineBudgetSettings` on `CoreAiModsLifetimeScope`, every coroutine site
      resolves the same instance, and every resume re-reads it, so a change reaches a pooled signal
      runner built long before. The mirror's `ScriptContext:SetTimeout(seconds)` moves the wall-clock
      half live, gated to the host actor; the instruction half stays composition-only because Roblox
      has no scriptable equivalent. Two escapes were closed with it: a raw `coroutine.resume` armed
      its own fixed constants, and the one-off `execute_lua` and AI-envelope surfaces each held a
      private default. Pinned by `RbxHeartbeatBudgetKillEditModeTests` and
      `RbxScriptContextEditModeTests`, including a runaway cut on an ALREADY-warmed handle. (Criterion 12, one JSON encoder, is decided: `HttpService` and the remote
      codec stay two independent encoders, pinned by `RbxJsonContractEditModeTests` — see
      `Assets/CoreAI/CHANGELOG.md` [Unreleased] and the audit.)
- [ ] **MVP11/MVP12 scalability debt** (2026-09-02 online-readiness architecture audit, re-verified
      against today's tree 2026-09-09 — most of that audit's security findings are now stale and are
      NOT repeated here, see below). Still open and unchanged: dispatch is O(actors)+O(pending) under
      one lock (`QueuedAiOrchestrator.SelectNextActorIdLocked`/`FindNextTaskIndexLocked`/
      `FindNextStreamIndexLocked`, `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs:314,350,363`);
      the scheduler's per-actor thread quota is an O(live-threads) scan on every `task.spawn`, bounded
      only by the global `EmergencyMaxThreads = 4096` (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs:30,1374`);
      the mod-load quota is the same O(loaded-mods) shape bounded by the global `EmergencyMaxMods = 256`
      (`Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:79`); `InstanceRegistry.ProcessPreSimulation`
      visits every registered instance every frame regardless of dirtiness
      (`Assets/CoreAIMods/Runtime/RbxApi/Instances/InstanceRegistry.cs:478-483`); `FireAllClients` fans
      out synchronously with no batching or per-tick coalescing
      (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs:1187`);
      `PublishSubscriptionSnapshotLocked` rebuilds a fresh dictionary on every subscribe
      (`Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:1146` + 5 more call sites); and one
      process-wide `_mutationGate` serializes every mutation and is held across the whole user operation
      (`InstanceRegistry.cs:88,216,229,288,469`). Already fixed since the audit and NOT open any more:
      the global chat-admission ceiling (see the Scale characterization item above,
      `AiOrchestrationQueueOptions.ForActorCount`, 7.15.0); the world ACL, now `public` in the
      engine-free `WorldAclAuthorizer` (`Assets/CoreAIMods/Runtime/RbxApi/Instances/WorldAcl.cs:54`)
      instead of `internal` to `CoreAI.Mods`; the disconnect seam, which now prunes per-actor remote
      signals (`RbxRemotes.RemoveActor`, `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/RbxRemotes.cs:96`)
      and calls `UnregisterActor` from real production callers
      (`Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs:138`, `LuaCsRbxApiBindings.cs:932`);
      remote payloads, now capped at `LuaCsRbxNetworkCodec.MaxPayloadBytes = 65536` bytes and refused
      with `PAYLOAD_TOO_LARGE` before decode (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxNetworkCodec.cs:98,113-130`);
      and the enveloped `execute_lua` protocol, which no longer exposes `operation_id`/
      `target_instance_id`/`expected_revision` as caller-supplied parameters on either the in-game or
      MCP surface (`Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs`,
      `Assets/CoreAIMcp/Runtime/Tools/ExecuteLuaMcpTool.cs:78-91`). Source audit
      (`dev-docs/ARCH_AUDIT_ONLINE_2026-09-02.md`) deleted after this digest per the audit-report
      policy; full original findings are in git history.

### Third review round — 2026-09-09 (branch `fix`, two independent reviewers)

Every finding of that round is listed below (the report's file-level evidence stays in git history). Not run yet:
PlayMode against this tree. The last full EditMode run over the whole tree reported 4303 tests
with 5 failures, all addressed by the fixes below.

Fixed:

- [x] The whole EditMode run hung with no results file — a synchronous test blocked the main
      thread on `Task.Result` while its continuation was posted to the very thread it was
      blocking (same shape as the earlier F12 deadlock). Fixed with a fixture-local
      `SynchronizationContext` detach; the deadlock guard test now also catches a blocking
      `.Result`/unbounded `.Wait()` on a local `Task`, not only `ThrowsAsync`/`CatchAsync`.
- [x] Loading a world at runtime silently broke `workspace:Raycast` and `Touched`/`TouchEnded`
      for good: the incoming Rbx API stayed wired to the physics port `Commit` was about to
      dispose, instead of the one it had just published. The session controller now attaches the
      post-publish port right after `Commit`.
- [x] A joining player spawned an unanchored 4x1x2 collidable box at the world origin instead of
      a HumanoidRootPart-sized body above it — the root part is now seeded (size + spawn height)
      through a seeder wired into production composition, not only a test harness.
- [x] Neither the deferred join spawn nor the death-triggered respawn had an error boundary; a
      failure in either used to end the scheduler frame for every mod. Both are now contained and
      reported through the registry diagnostics.
- [x] `TimeoutLlmClientDecorator` relied on a WebGL-forbidden `TaskCompletionSource` option and
      could leave a caller hanging if its own cleanup threw. Rewritten to run detached against an
      explicit `TaskCompletionSource`, completed only after every resource is disposed (each
      dispose independently guarded), with a bounded ~20 ms grace window for a cooperative inner
      operation to finish unwinding before its result is discarded as a timeout.
- [x] A tool argument that cannot bind is now rejected structurally before the tool body runs
      (`ToolExecutionPolicy.TryBindArgumentsStructurally`), carrying the same schema-retry hint the
      downstream MEAI-side rejection used to carry.
- [x] `FileConversationSummaryStore` now logs a write failure caused by the save directory itself
      failing to create — previously the one storage error that reached the caller silently.
- [x] `QueuedAiOrchestrator`'s reconnect test was pinning the cross-tenant memory merge round 1
      removed as a data leak; it now pins what replaced it (a named actor's durable key is
      `(actor, tenant, user, session, topic, role)`), with a new assertion that a different tenant
      sharing the same actor id still reads no history.

Closed after that list was written, in the same session:

- [x] One actor could destroy another actor's character. The lifecycle now owns its own
      `LoadedCharacter` reference and tears that down; `Player.Character` stays readable and
      re-assignable but can no longer aim destruction. Two tests hijack a foreign character and
      then disconnect and self-kick.
- [x] `DistanceFromCharacter` reads the live transform through `GetLivePositionStuds`, falling back
      to the stored value only for a part with no backing object.
- [x] A dead character's motor stops: the fixed step skips a dead humanoid and clears the motor's
      own walk target.
- [x] Height-based jumping reads the host's current physics port every time, so both a world loaded
      at runtime and a script writing `Workspace.Gravity` reach the jump solver.
- [x] A nested pair of `TimeoutLlmClientDecorator`s keeps the inner timeout's type, with a companion
      test proving a genuine caller cancellation is not reclassified as a timeout.

Still open, recorded honestly:

- [x] `Assets/CoreAIMcp`: **this entry was wrong and is now closed.** It claimed
      `NotificationCapacity_IsBounded_AndPostContinuesWorking` had never passed; it was written
      before the fix that made it pass, and two recorded Unity runs since then show it Passed in
      0.25 s (`artifacts/testresults/mcp3.xml` 17:34Z and `final4.xml` 21:34Z, both 2026-09-09).
      A headless harness on the editor's own Mono then established what the failure had been, and
      it was never the server: the client allows two connections per origin, the open notification
      stream and the unread 409 took both, and the POST never left the machine — measured, with a
      raw socket answered by the same server in 1 ms during the stall. The test now states that
      budget on its own loopback endpoint and holds BOTH responses open while the POST runs, which
      is stronger than the form that was failing. The same harness confirmed the `LongRunning`
      removal is not implicated: the notification loop waits on `SemaphoreSlim.WaitAsync` and
      async writes and holds no thread.
- [x] **The full-assembly PlayMode sweep — diagnosed and fixed 2026-09-10.** It was never a CoreAI
      defect. `Assets/Mirror` is optional and gitignored, so a committed scene bakes no
      `NetworkIdentity`; with Mirror installed the shared first-person controller in several demo
      scenes becomes a `NetworkBehaviour` requiring one, Unity auto-creates it with sceneId 0 during
      the load, and Mirror answers with an error and `EditorApplication.isPlaying = false`
      (`Assets/Mirror/Editor/NetworkScenePostProcess.cs:77-78`). That ends the PLAYER, so the run
      aborted and wrote NO results file — one optional local package cost the entire sweep, which is
      why "did it ever work" looked unanswerable. It works; it cannot work on a Mirror machine
      unrepaired. The demo smoke now normalises those auto-created identities at
      `[PostProcessScene(0)]`, one order ahead of Mirror, gated on being in play mode, NOT building a
      player, and armed for exactly one scene path at a time (domain reload is disabled in this
      project, so the hook also disarms on `ExitingPlayMode` — static state outlives a play session).
      Cancelling the stop from inside the test was tried and is impossible: measured, Mirror's error
      is logged AFTER the test's post-load code runs. A named-scene skip list was tried and is the
      wrong shape: skipping the Hub demo just moved the stop to MiniRpg.
      **Evidence:** before — no XML, abort. After — `artifacts/testresults/pmall2.xml`, 149 total /
      134 passed / 6 failed / 9 skipped, no abort. The 6 are environmental and were INVISIBLE while
      the sweep aborted before reaching them: 5 need a loaded LLM (deliberately strict live tests),
      1 needs a graphics device. All 6 now report honestly instead of hiding behind the abort.
- [ ] **Neo's order-100 scene post-processor restores objects to active, not to their original
      `activeSelf`.** Found by the 2026-09-10 audit of the Mirror repair and confirmed as a real
      behaviour defect, not a test artefact: an object Mirror disables is re-enabled to `true` rather
      than to whatever it was. On a Mirror machine a rig that shipped INACTIVE can therefore come up
      active, which among other things could satisfy a scene smoke's camera assertion for the wrong
      reason. No evidence it explains any current pass; recorded so it is not rediscovered.
- [x] `ProjectSettings/ProjectSettings.asset` and `CoreAI.slnx` carry **no** Mirror-injected
      artefacts in HEAD (verified 2026-09-10: no MIRROR symbol in the committed settings, no
      Mirror/kcp/Telepathy project in the committed solution — the item as written is closed).
      The editor re-introduces them whenever Mirror is installed locally, which stays a live
      hazard; the `tools/check_positive_module_opt_in.py` release gate now checks for it.
- Local Mirror re-injects `MIRROR;...;EDGEGAP_PLUGIN_SERVERS` into the WebGL define row on every
  editor load, so the working tree is legitimately dirty there: run
  `python tools/check_positive_module_opt_in.py --staged` (validates the INDEX) before committing,
  stage a stripped row, and never `git add` `ProjectSettings/ProjectSettings.asset` wholesale.

- [x] **The live PlayMode tests are NOT blocked on LM Studio — proven 2026-09-10.** They were
      recorded as needing a live model, and LM Studio could not load the configured one ("Failed to
      load model qwen2.5-vl-3b-instruct"), so the suite showed 6 failures. Pointing them at a local
      OpenAI-compatible bridge instead (`agent.sh openai-server -e opencode -m muse -p 8801`, then
      `COREAI_PLAYMODE_LLM_BACKEND=http`, `COREAI_TEST_BASE_URL=http://127.0.0.1:8801/v1`,
      `COREAI_TEST_MODEL=opencode/muse`) takes the full sweep from 136 passed / 6 failed to
      **140 passed / 2 failed** (`artifacts/testresults/pmbridge1.xml` vs `pmall5.xml`). Everything
      that needed a model passes when a model answers — the nine built-in roles, the castle build,
      the crafting-memory chain, the runtime backend switch. Recipe and its limits are written up in
      the agents skill's AGENTS.md; the bridge is slow (one CLI process per call, serialized) and
      emulates tool-calling through prompting, so it proves the path works and does not replace a
      run against a real provider.
- [ ] **One live test ignores the test env vars and always goes to LM Studio.**
      `CoreAiChatDemoRealModelWebGlPlayModeTests.CoreAiChatDemo_RealModel_StreamsStopAndRecovers`
      reads the project's `CoreAISettingsAsset` instead of `COREAI_TEST_BASE_URL`/`COREAI_TEST_MODEL`,
      so it is the one live test the bridge above cannot help: it still fails with LM Studio's
      "Failed to load model". Either give it the same env-var path the other live fixtures use, or
      record deliberately that it is an asset-configured smoke and must be skipped when the asset's
      backend is unavailable.
- [ ] **A PlayMode test fails on `main` and it is not the merge's doing — proven.**
      `CoreAiChatPanelNonStreamingPlayModeTests.TypedBufferedFailure_IsAdmittedWithoutCompletionEventOrLegacyExecution`
      fails with an unhandled log message: "[CoreAI] [Core] [CoreAiChatPanel] UIDocument component not
      found on this GameObject!". It is NOT a headless artefact: the two sibling tests in the same
      fixture pass under the same run. It arrived with the publication-wave commit `43c007ec`.
      **Evidence it predates the 7.41.1 merge:** the tree was checked out at `12309ac7` (the parallel
      line's tip, before the merge) and the fixture run there gives the same 2 passed / 1 failed with
      the same message (`artifacts/testresults/theirs1.xml`). Left for whoever owns that line rather
      than fixed blind, because two collisions with that work already cost a rebuild this session.

### Character motor contract — known limits, not defects of the bridge seam

- [ ] **A false landing between a jump and the fall.** With a real motor, on the first fixed step
      after a jump the ground probe (0.12 m) still reports contact, so the machine walks
      `Jumping -> Landed -> Running -> Freefall` and a Lua listener sees a momentary landing and a
      speed report that did not happen. Pre-existing behaviour of the `Jumping -> Landed`
      transition, found while fixing the signal ORDER on 2026-09-10 and recorded rather than
      folded into that change. The fix is a probe or a grace window that knows a jump just started.

Both apply equally to CoreAI's own motor, so they are `Humanoid` contract gaps rather than something
the host-provider seam introduced. Recorded so a bridge author is not surprised by them.

- [x] `Running(speed)` — **closed 2026-09-10.** It reported the CONFIGURED `WalkSpeed` (the old
      expression multiplied a UNIT `MoveDirection` by it, so it could only ever be 0 or full speed)
      and fired only on entering the Running state. `IRbxCharacterMotor` gained `double?
      MeasuredSpeed` as a DEFAULT interface member, so an external motor keeps compiling and falls
      back to the derived value; `UnityRbxCharacterMotor` reports the solver-resolved planar
      velocity, so a body walking into a wall reads 0 while its commanded WalkSpeed is unchanged.
      `Running` now fires whenever the speed changes, and fires 0 when the character stops — which
      is what the mirror says (`Humanoid.yaml`: "Fires when the speed at which a Humanoid is running
      changes", and "with a speed of 0" on stopping). Death reports that 0 once, before `Died`, so a
      walk cycle driven by this signal alone cannot keep running forever with auto-respawn off.
      Hysteresis, not one epsilon: a moving character stops below 0.1 stud/s, a stopped one must
      reach 0.2 to move again, so a body hovering at the boundary no longer emits an event per step.
- [x] `Jump` — **closed 2026-09-10.** `IRbxCharacterMotor.TryJump` (also a default interface member,
      so existing implementers are unaffected) lets a controller refuse — no clearance, mid-animation
      — and a refusal now leaves the state machine where it was instead of entering Jumping and
      reading as Freefall on the way up. `NullRbxCharacterMotor` deliberately still ACCEPTS: nothing
      is there to refuse, and the frozen Tier-A fixture `TBC-010-gravity-low-jump.lua` pins that a
      bodyless Humanoid still fires `Jumping`. A round-13 audit proposed changing that; it was
      declined for this reason.

**Modularity proved by removal, not by argument**: with `Assets/Mirror` taken out of the project the
tree compiles with **0 errors**, `CoreAI.Net.Mirror.dll` is not built at all, and EditMode runs
**3588 / 0 failed** — exactly the Mirror-present total minus that package's own 19 gates. The Lua
layer, mods and host need nothing from the transport.

## MVP2.5 persistence release — status 2026-09-02

Verified on the merged tree (commit `5c62c43d`, Unity 6000.3.14f1 batchmode, XML in
`artifacts/testresults/`): full EditMode **3256 total / 3247 passed / 0 failed / 9 skipped**
(`g9.xml`); full PlayMode with the live `ling-3.0-tiny` model **114 total / 110 passed / 0 failed / 4 skipped**
(`pm4.xml`); Node jslib tests 6/6 and SSE 12/12; ScaleHarness quick smoke green after the rung-zero
envelope fix. Independent QA rounds (world package, materials, WebGL, demos, docs, architecture,
texture catalog) are recorded in `PROGRESS.qa-*.md`, `PROGRESS.texqa.md`; the architecture and docs
audit reports were absorbed into this file and deleted per the audit-report policy (2026-09-09).

- [x] W3.1–W3.4 world package: codec, one serializer, clean mod restart, backup safety — implemented,
      EditMode green, QA blocker (bare `Instance.new('Part')` locking every gated tool) fixed with a
      red-first test.
- [x] All 45 `Enum.Material` mapped at runtime; public Lua sets `Material` and an independent
      `Part.Color`; Neon follows `Part.Color`; fallback is the visible magenta hazard. Judging rig
      dumps `MATERIAL_CATALOG complete slots=46 mapped=45 fallback=1 failures=0 result=PASS` in the
      Editor (Editor.log, 2026-09-02). Every material was inspected face-on and grazing at 1200px:
      no seams, no NaN, no pink.
- [x] Hub World page reports `Has saved state` only from the `FS.syncfs` callback (W3.5 code side).
- [x] 15 frozen demo scenes in `EditorBuildSettings`; smoke test pinned to the frozen list.
- [x] **W3.5 / G11 browser acceptance — PASS on the rebuilt player `c38fe6fe` (`dev-docs/G11_RUN_RECORD_2026-09-02.md`).**
      Real WebGL build (`CoreAIG11WebGlBuild.Build`, 15 scenes, 103 MB) served on `127.0.0.1:8777` and
      driven in the in-app Chromium: boot ✓, Lua self-test `SELFTEST_DONE checks=16 fails=0` ✓, Tetris
      ✓, `Save Now` → `Yes` → reload → `Yes` and `Reset World` → `No` → reload → `No` ✓, real-provider
      nonce answer through the G11 proxy ✓, 46-slot material evidence `result=PASS` with 36 textured
      slots ✓, all 15 demos boot with 0 console errors ✓. FOUND AND FIXED: a single retryable 503 left
      the chat on the typing indicator forever (`Task.Delay` in `MeaiOpenAiChatClient` never completes
      on WebGL; now `ILlmAsyncMarshaler.DelayAsync`). Re-run on the rebuilt player: 503 retried
      (attempt 2 → 200 → nonce), persistent block → terminal error in 46 s with controls re-enabled,
      recovery without reload ✓. Not run: MCP loopback / mod HTTP probes; template crops the fixed
      960×600 canvas at 768 px (responsive template recommended).
- [x] **Material quality pass (owner decision 2026-09-02) — Editor side DONE.** Catalog-driven
      `RbxTextureMaterialProvider` (`RbxMaterialTextureCatalog` + project-local override),
      `RbxTexturedSurface` AO + DirectX-normal keyword, Editor menus
      `CoreAI/Materials/Import Bridge-Megascans folder...` and `Download CC0 texture sets (ambientCG)...`.
      Packaged default catalog materialized (6 × 1K CC0); local override holds 36 ambientCG 2K sets
      (476 MB, gitignored, LICENSE with provenance). Judging rig evidence with the override:
      `MATERIAL_CATALOG complete slots=46 mapped=45 fallback=1 failures=0 result=PASS`, 36 slots on the
      textured shader, 9 procedural. Every slot inspected face-on and grazing at 900 px: no seams, no
      pink, no NaN; three ids corrected after the pass (`Slate=Rock022`, `Rock=Rock028`, `Salt` back to
      procedural). Fab/Megascans: local use only (`dev-docs/SHADER_SOURCES_RESEARCH_2026-09-02.md`).
      OPEN: the same 46-slot evidence and a visual pass in the WebGL player (G11).
- [ ] **Material catalog memory + import defects** (2026-09-04 material defect audit and local-texture
      QA pass, both deleted after this digest). `RbxTextureMaterialProvider.EnsureSharedCache`
      (`Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxTextureMaterialProvider.cs:431-509`) eagerly builds
      one shared `Material` for **every** catalog entry the first time any Rbx part is created — the
      catalog holds direct `Texture2D` references, so this pulls in all 36 materials' textures
      (~99 MB resident) even when a scene uses one. Cost should be proportional to what a scene
      touches: store asset paths in the catalog and load a texture on first use of that material.
      Worth doing before WebGL is taken seriously. Separately,
      `Assets/CoreAIMods/Editor/RbxMaterials/RbxLocalTextureCatalogImport.cs:108` hardcodes
      `IsSmoothnessMap = false` instead of copying `surface.IsSmoothnessMap` the way
      `RbxMegascansCatalogImporter.cs:317-318` does; latent today (no local Sets folder has a
      smoothness/gloss map) but a future one would get `InvertRoughness` wrong at runtime
      (`RbxTextureMaterialProvider.cs:325`) with no warning. Both confirmed still present, 2026-09-09.
- [x] **Rung zero (MVP2 entry gates, from the architecture audit) — landed (`PROGRESS.rungzero.md`,
      24 focused tests green off-device):** ACL in the engine-free registry, server-generated
      envelopes on every production Lua entry (execute_lua plain/MCP, mod chunks, scheduler resumes,
      signal/remote dispatch, cross-mod calls), disconnect seam firing `PlayerRemoving` once,
      inbound `SenderActorId` cannot create identity, `PAYLOAD_TOO_LARGE` at a frozen 65,536-byte cap.
- [x] **MVP2.5 slice 8.0 `Debris:AddItem` — landed (`PROGRESS.mvp8-debris.md`, 12 gate tests):**
      engine-free `RbxDebris` (default 10 s, 1,000-item cap evicting the oldest instantly, negative
      clamps to 0, re-add replaces the deadline), call-time ACL `Demand` + enveloped destroy at fire
      with drop-and-log-one-line on ownership change, ownerless `ModScheduler.ScheduleHostCallback`
      surviving unload/`KillOwnedBy`, `Debris` tree-backed in catalog/bootstrap/binding (stub row
      removed). File-only gates green (`CoreAI.RbxApi.Instances` / `Mods` / `Mods.Tests`, 0 errors;
      temp `<Compile Include>` entries reverted — Unity regenerates them on next refresh). OPEN:
      full EditMode verification gate on next editor start (Unity held by another process).
- [x] **Rung zero residue — host restore envelope — closed (`c7b1f44e`).** `InstanceTreeSerializer.Restore(snapshot,
      registry, hostActorId)` validates, registers every node, then applies every write as one server-generated
      host operation and stamps the captured revisions last; `RestoreFresh` passes the composition's local host
      (`RbxWorldPackageRestoreOptions.HostActorId` overrides it). The RED test goes through production composition
      (`RungZeroHostRestore_AclPackageLoad_RestoresTreeAsOneHostEnvelopedOperation`: retained operations 0 → 1,
      scene and headless hosts). Same commit: a session composed with a world ACL refuses a legacy package.
- [x] **World package follow-ups (`PROGRESS.wffin.md`, 6 new tests green off-device):** dangling
      `PrimaryPart` is dropped in the snapshot only and recorded in the manifest `diagnostics` array
      (capture never fails, the AI is never locked out), `list_autosaves` / `load_autosave` through the
      same confirm/reject pool as manual slots, pre-load safety autosave `load_world-pre`, reserved
      Windows device names rejected. Hub World Loads page lists autosaves and routes `Load...` through the
      same confirmation pool (task `hub6`, UI tests need the Editor run).
- [x] **Scale characterization 20/50/100/200 actors** measured through production composition with a
      frozen workload (`tools/ScaleHarness`, `dev-docs/SCALE_CHARACTERIZATION.md`, host CoreCLR, not
      the player): frame-only budget holds 100 actors in 4 ms and 200 in 16 ms. The chat admission cap
      and the heap budget, both previously recorded OPEN here, shipped fixed in **7.15.0** (`ae160cc2`):
      the heap gate was measuring the garbage collector, not the program (`GC.GetTotalMemory` noise 3x
      to 47x the threshold, sign not even stable), replaced with a retained-heap-delta +
      allocation-bytes-per-actor-per-frame pair (spread 0.13 MB / 0.00 bytes across repeats); the chat
      admission ceiling is now sized to the actor count via `AiOrchestrationQueueOptions.ForActorCount(n)`
      instead of the fixed `MaxPending=64`/`MaxConcurrent=4` defaults. With both fixes, 200 actors pass
      the 16 ms frame budget, the memory gate and the chat gate (`dev-docs/CAPACITY_UNBLOCKED_2026-09-05.md`,
      "largest measured passing N: 200"). The 4 ms budget still fails at 200 (median 7.37 ms — a
      240 Hz-equivalent target, not 60 Hz). No capacity claim until the staircase is repeated in a
      Standalone player against a real provider.
- [ ] Demo scene-level fixes needing the Editor: orphan `ChatPromptButtonsController` in
      LiveMechanicsModsChatDemo / MiniRpgModsDemo / ModdableUnitsDemo (enable the chat example menu
      or remove), `DemoHubPagesBinder.EnableExamplePrompts()` no-op in WaveAutoBattlerModsDemo, and two
      empty `Prompt:` fields in `Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity` (verified
      2026-09-09, still present) where the copy `"Start a battle"`/`"Endless waves"` is expected — the
      third `Prompt:` field is populated. (A separate claim from the same 2026-09-04 demo-scene audit,
      that `CoreAiHubDemo.unity`/`MiniRpgModsDemo.unity` carry dangling script GUIDs `4ef82c39…`/
      `66c209b3…`, does NOT hold: both resolve to `PlayerController3DAnimatorDriver`/
      `PlayerController3DPhysics` via the declared `com.neoxider.tools` git dependency in
      `Packages/manifest.json` and `Packages/packages-lock.json`, cached at
      `Library/PackageCache/com.neoxider.tools@.../Scripts/Tools/Move/` — not actually broken.)
- [ ] IMGUI migration backlog: 3 files remain on the `ImguiBanRatchetEditModeTests` allowlist — all
      runtime diagnostics overlays (`AiDashboardPresenter.cs`, `CoreAiTokenBudgetOverlay.cs`,
      `OrchestrationDashboard.cs` under `CoreAiUnity/Runtime/Source/Features/...`); every demo
      controller moved off IMGUI onto the shared `CoreAiDemoPanel` in 7.34.0 (`54fa272f`). The ratchet
      only shrinks.
- [ ] Incrementally encode/decode JSON/ZIP on WebGL. The current player path yields chunked file I/O
      and fails fast beyond a 4 MiB / 4,096-instance / bounded-collection budget.
- [ ] Stream or quota JSON token materialization before semantic tree validation to cap hostile
      browser peak memory, and enforce hostile-reader preorder/sorted collection canonicality.

- [x] **WebGL native tool-call turn (7.3.1).** The 7.3.0 player dead-waited after `execute_lua`
      (reproduced with the proxy's scripted replies, `dev-docs/G11_RUN_RECORD_2026-09-02.md`). Fixed:
      no `ConfigureAwait(false)` on the tool path, `MeaiToolTaskBridge.Publish` at the MEAI boundary,
      guard primitive in `WebGlUnsafeAsyncPrimitivesEditModeTests`; §6.5 MCP loopback (N/A) and
      outbound mod HTTP (refused) probed in the browser on the fixed player.
- [ ] **Inherited `ConfigureAwait(false)` sites (19 files) frozen in the WebGL guard allowlist.** The
      agent-memory contour, the `ILlmClient` decorators/orchestrator and five tool bodies
      (Inventory / Memory / GameConfig / CallSkillTool / Wait) still carry the primitive; none is on
      the browser-verified path. Convert each to host-context awaits (plus
      `MeaiToolTaskBridge.Publish` for tool bodies), then drop its allowlist entry.
- [ ] **Browser-gate harness: keep the pane painted.** With the in-app browser pane hidden the player
      runs at ~1 frame/s (no `requestAnimationFrame`), after five minutes Chrome stalls it; timings in
      the record are dilated and a 30 s tool timeout can trip inside the autosave. Add a visibility /
      frame-rate check to the G11 protocol before any timed row.
- [ ] **Weak tests flagged by the independent final QA (2026-09-02 pass, `dev-docs/FINAL_QA_2026-09-02.md`,
      deleted after this digest per the audit-report policy).**
      `Lint_BinderOutput_IsExactlyRobloxSpaceOutput` computes actual and expected through the same
      `RbxSpace` helper (a symmetric sign/scale bug passes; only the chirality goldens catch it);
      `Lua_InstanceNew_DeprecatedParentArgument_WorksAndLogsOnce` invokes twice inside one load, so a
      once-per-load implementation would also pass (needs a reload twin); `AssetRule_At1To1_*` proves
      the second scale per shape only. Rewrite each with an independent oracle or a negative twin.
- [ ] **Four more vacuous tests from the same 2026-09-02 final QA pass, beyond the three above** (three left):
      `D3_MeterAuthoredHostObjectReadsAsStuds` (`1.8`/`0.28`) was flagged for doing bare arithmetic without
      wrapping a host object — it now builds an `RbxWorldHost`, wraps a meter-authored scene object through the lazy
      world-name lookup and reads its studs position, so that part is resolved; it lives in
      `RbxApi/Binding/RbxWorldHostEditModeTests.cs`, class `RbxWorldHostLazyWorldWrapEditModeTests`, since
      `80f2f30c` moved it out of `RbxSpaceGoldenFixtureEditModeTests.cs`. Still open: `CatalogOverrideWinsPerMaterial`
      (`RbxMaterialTextureCatalogEditModeTests.cs`) drives `RbxMaterialTextureCatalog` directly instead
      of the production `InstanceGameObjectBinder` path, so it would pass even if the binder still
      resolved the packaged texture; `CatalogShaders_NeverRaiseAnUnclampedBaseToAPower`
      (`RbxMaterialCatalogQaEditModeTests.cs`) is a static shader-source text scan with no runtime
      shader instantiation, so a runtime `pow(negative, …)` NaN would not flip it; and
      `ManyLuaParts_ReuseOneTexturedHandleWithoutNativeMaterialAllocation`
      (`RbxTextureMaterialsAcceptanceEditModeTests.cs`) asserts handle-name reuse only, so it would
      pass even if every Part still re-created its own material under an equal name. These three were
      confirmed still present, unchanged, 2026-09-09. Rewrite each with an independent oracle or a
      negative twin.
- [ ] **Final QA verdict (2026-09-02):** MVP2 G10 (chat throughput with a real provider, admission cap
      `MaxPending=64`) and the heap-slope budget stay open (the host-restore envelope closed in `c7b1f44e`); every other
      G1–G12 / P1–P5 / W3.1–W3.5 row is PASS on `g11.xml` + the browser record. See the reconciliation
      section at the end of the QA report for the findings that were stale against `g11.xml`.
      **Heap slope, where it stands** (from the 2026-09-05 allocation finding, folded here on 2026-09-24): 99.6% of
      the per-frame allocation was one fresh Lua thread per signal handler fire (~5.2 KB each); pooled signal
      runners and `ThreadRecord`s halved it on the host CoreCLR (N=20: 105.6 → 52.5 KB/frame; N=50: 269.9 → 135.1
      KB/frame; in-process 2,887 → 847 B per handler), and three per-resume costs went later (the reserved resume
      operation id, M2-10; the cached actor context; the interned `OriginTag`). The heap slope still fails at N=50:
      medium-lived garbage is promoted to gen1/gen2 (the ~300 chat requests per N=50 window are the first suspect),
      and the slope metric itself varies about 3× between runs of identical code (`dev-docs/SCALE_CHARACTERIZATION.md`
      §12). *Owner:* capacity work (MVP17). *Plan:* re-measure with the frozen workload and full repeats in a
      Standalone player (not the host CoreCLR), then profile gen1/gen2 promotion at N=50 before any capacity claim.

## MVP1 residue closed + MVP2 scheduler core (2026-08-30) — 7.1.0 prepared

- [x] Every open item in the "Roblox conformance" block below is closed; see it for what each one
      actually turned out to be. Two of those findings were themselves wrong and are corrected in
      place rather than silently dropped: `BindToClose` was never a silent argument drop, and the
      recorded handedness trace had flipped Z signs.
- [x] `BasePart.Orientation` (YXZ) and `BasePart.Rotation` (XYZ) are wired in degrees over the
      existing `RbxCFrame` decomposition; setters preserve `Position`. Seven regressions in
      `RbxApiLuaBindingsEditModeTests` cover the pair; of those exactly TWO pin the ORDER rather than
      a round trip — one asserts the built CFrame equals `CFrame.fromOrientation(...)`, the other
      that `Rotation` and `Orientation` genuinely disagree on a multi-axis rotation, so wiring both
      to one decomposition fails. The rest cover round trips, position preservation, and the
      single-axis case where the two orders legitimately coincide.
- [x] `BasePart.Material` remains a loud stub, now naming the tracked phase `MVP2 (materials
      catalog)` and matching ladder deliverable §5.2.1 item 12.
- [x] **MVP2 scheduler core (task 2 of §5.2.1) — engine-free `ModScheduler` in
      `RbxApi/Instances/Scheduling/`.** Canonical R4.2 pipeline as one ordered stage table
      (PreAnimation → PreSimulation → PostSimulation → resume delayed → Heartbeat → PreRender, with
      deferred drains at the §5.2.3 points), binary min-heaps for wait/delay keyed
      `(deadline, earliestFrame, sequence)`, `ScheduleWaitUntil` over a nonblocking completion token,
      per-thread owning mod id, and `KillOwnedBy` so a budget kill takes one mod without disturbing
      others in the same frame. 19 deterministic tests. Design: `dev-docs/MVP2_SCHEDULER_PLAN.md`.
- [x] Architecture decision for that core: it does NOT reference `IScriptCoroutine` (that type lives
      in `CoreAI.Mods`, so referencing it would invert the assembly dependency). It defines its own
      `IRbxScriptThread` / `IRbxScriptThreadFactory` / `IRbxTimeSource` ports, matching the existing
      `IPartPropertySink` / `IInputSource` pattern. Time is injected, so every test is deterministic.
- [x] **The scheduler is WIRED and live.** `LuaCsRbxSchedulerAdapter` implements the ports over
      `IScriptCoroutine`; `task.wait/spawn/defer/delay/cancel` call the real scheduler; the tick driver
      advances it once per frame with the scaled delta in R4.2 order (scheduler phases → delayed
      resumption → existing input/RunService pump at the Heartbeat boundary → PreRender); teardown
      calls `KillOwnedBy` where the mod's connections are severed. Two product defects surfaced ONLY
      in the real editor run — the coroutine seam dropped resume arguments (so `spawn/defer/delay`
      lost their varargs) and the protected-resume `true` flag leaked into the yield continuation (so
      `task.wait` returned `true` instead of the elapsed time R4.8 requires). Both fixed in the
      product; the tests that caught them were left untouched.
- [ ] **Top-level `task.wait` — DELIBERATELY its own rung, not a loose end of this wave.** In Roblox a
      script's main chunk IS a scheduler thread, so `task.wait()` at the top level works there; ours
      refuses. Making mod entry scheduler-owned is not a small wiring change: `LoadMod` would stop
      running the chunk synchronously to completion, which moves error reporting off the load call and
      changes `IsLoaded`, quarantine-on-load-failure and every synchronous `LoadFails` assertion.
      Measured blast radius: **20 EditMode test files call `LoadMod(`, the largest fixture 38 times.** That is a design pass with its own acceptance criteria, not something to bolt onto
      the end of a release — attempting it here would either be reckless or would quietly weaken the
      tests that currently pin synchronous load errors.
- [ ] **Deferred drain points must be revisited when signals arrive** — R5.4–R5.7 add a drain between
      the delayed resumption and Heartbeat, on top of the R4.8 drains that shipped in this wave.
- [x] Both samples derive their axes from the camera; three deferred v6.4 audit findings
      (LOW-2/LOW-3/LOW-4) closed in the same wave. Mod versions bumped so the seeder delivers them.
- [x] `Docs/ROADMAP.md` and `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` re-synced with reality: current
      version, MVP1 landed in 6.3.0, the `WaitForChild` rung split, the Lua log service recorded as
      wired end-to-end (it is: `CoreAiModsInstaller.cs:86/143/335`), decision **D10**, and a §6.6
      test tree that matches the directories that actually exist.
- [x] Lua-CSharp checked for updates: **none available.** v0.5.6 (2026-07-29) is still the newest
      release and the newest NuGet package; upstream `main` is 2 commits ahead with an unreleased
      `LightAsyncValueTaskMethodBuilder<T>` allocation fix (PR #339) touching the perf area of our
      issue #338. Not adopted — building from an untagged `main` would re-create exactly the local
      patched DLL we retired in 6.13.1. Revisit when v0.5.7 ships.
- [x] File-only gates green: `CoreAI.Core` / `Source` / `Mods` / `RbxApi.Instances` / `Mcp` /
      `Mods.Tests` all 0 errors. Note the trap this wave walked into: Unity had not regenerated the
      project files, so the new `Scheduling/` sources and the new handedness test were absent from
      every generated `.csproj` and a plain `dotnet build` compiled NONE of them while reporting
      green. Their `<Compile Include>` entries were added by hand before the gates above were run
      (those files are gitignored build artifacts; Unity overwrites them on its next refresh).
      Also note `CoreAI.Tests.csproj` is the CORE test assembly — mod tests live in
      `CoreAI.Mods.Tests.csproj`, and building the wrong one proves nothing about them.
- [x] **CLOSED — unimplemented surface that was not a loud stub.** `ClassCatalog` now carries a
      data-driven, inheritance-aware catalog of known Roblox members with a phase and workaround each,
      and the binding dispatch raises a structured `NOT_IMPLEMENTED` for them. A genuinely unknown
      name still raises the invalid-member error — both directions are pinned by tests, because
      turning every typo into "coming soon" would be worse than the original bug. The sweep found more
      than the audit listed: `Model.WorldPivot`, `Workspace.SignalBehavior`, BasePart collision/physics
      and six surface properties, `Lighting` holes and six `RunService` methods. Rung proposals still
      needing a decision: dynamic BasePart/collision → MVP8, deprecated surface properties → explicit
      non-goal, `Lighting` → MVP15; `Terrain` stays the documented non-goal.
      Original finding:  Principle 5
      says unimplemented API must fail with a structured `NOT_IMPLEMENTED` naming its roadmap phase.
      A row-by-row re-verification of the Lua-visible surface table found members that are neither
      wired NOR stubbed: they fall through to the generic `X is not a valid member of Y` path
      (`LuaCsRbxInstanceBindings.cs:204`, and the spatial fallthrough at `:674`). Confirmed cases:
      `Model.PrimaryPart`; `Workspace.Gravity` / `Raycast` / `GetServerTimeNow` / `Terrain`;
      `BasePart.Velocity` / `AssemblyLinearVelocity` / `Massless`, constraints and surface properties;
      container behaviour beyond storage. This is worse than a doc bug and it is AI-facing: the model
      is told the member DOES NOT EXIST, so it invents a workaround instead of picking an implemented
      alternative or waiting for the rung. The roadmap table now separates shipped / loud-stubbed /
      absent honestly, but the runtime still lies. Fix: give each known-Roblox-but-unimplemented
      member a phase-naming stub, driven by the class catalog rather than a hand-written list.
      `Model` pivot got its rung in this wave (§5.2.1 item 13, phase `MVP2 (Model pivot)`); the rest
      still need one.
- [x] **Verification gate — DONE via batchmode (2026-08-30).** Unity 6000.3.14f1 batchmode, full
      EditMode, latest run: **2744 total / 2735 passed / 0 failed / 9 skipped**
      (`artifacts/testresults/editmode8.xml`). The run before the wiring was 2708/2699/0/9; the run
      immediately after it was 2730/2716/**5 failed** — three of those five were product defects, not
      test bugs, and none of them showed up in compilation or in the out-of-editor harness. Confirmed
      from the result XML that the new fixtures actually ran rather than the suite merely being
      green: `ModScheduler` 19, `SceneHandedness` 4, `BasePartOrientation` 4, `BasePartRotation` 3,
      `BasePartMaterial` 1, `BindToClose` 2, Model-pivot 2 — zero non-passing cases overall. This
      also closes the earlier gap where the `LuaBindings` group could not run outside the editor at
      all (a Unity native ECall during mod-stack construction), so those tests were compile-verified
      only. The Unity MCP bridge was unavailable all session, but the lockfile turned out to be
      STALE (no live editor process), so batchmode was free to run and gives more than MCP would.
      Artifacts: `artifacts/testresults/editmode.xml` + `.log`.
- [ ] **PlayMode gate — non-live part green, LIVE part BLOCKED on the local LLM backend.** Two
      batchmode runs: before the `task.*` wiring **112 / 106 passed / 2 failed / 4 skipped** (270 s),
      after it **112 / 104 / 4 / 4** (15 s). The count of failures grew because the backend got worse
      mid-session, not the code: the first run failed with `HTTP 400: Invalid model identifier
      "qwen_qwen3.5-4b"`, the second with `HTTP 400: No models loaded. Please load a model` — so two
      more live tests that had scraped by now fail too, and the whole run finishes in 15 s because
      nothing reaches a model. All four failures name the LLM backend
      (`Orchestrator_EachBuiltInRole_PublishesEnvelope_WithProductionLikeLlm_Auto`,
      `CoreAiChatDemo_RealModel_StreamsStopAndRecovers`,
      `OfflineScope_SwitchedToLiveHttp_AnswersThroughRealModel`,
      `CraftingMemoryOpenAi_ThreeCrafts_AllUnique`); none touch Lua, the Rbx API or the scheduler.
      **To close this gate:** load a model in LM Studio and point `COREAI_TEST_MODEL` / the settings
      preset at it, then re-run PlayMode. Until then the live portion is unverified — it is NOT
      evidence that the wave is fine, only that the failures are attributable elsewhere.
      Artifacts: `artifacts/testresults/playmode.xml`, `playmode2.xml` + logs.
- [x] **CLOSED — the bundled samples now have regression coverage.**
      `BundledLuaSamplesEditModeTests` reads the REAL resources: it pins each `@coreai` header and
      version (so a future edit cannot silently drop the bump the seeder needs), that both load and
      run under the real Lua runtime without hitting a loud stub, that Lane Racer's swept collision
      catches a large-`dt` crossing without false-positiving on a normal frame, and all three Tetris
      gravity properties including the normal↔soft-drop transition. Both of its initial failures were
      test bugs, not sample bugs — the rescale arithmetic was re-derived by hand
      (`0.59 → 0.0491667 → 0.59`, phase preserved in both directions) before either expectation moved.
      Original finding:  Two
      behaviour fixes shipped in them this wave (Lane Racer swept collision, Tetris gravity remainder
      + soft-drop transition) and the repo rule is that every bug fix ships with a regression test.
      Nothing tests them: `BundledModSeederEditModeTests` uses fake sources and
      `DemoModProductionSurfaceEditModeTests` loads a different Tetris source — neither reads
      `Runtime/Resources/CoreAIMods/sample_*.lua`, verified by grep. An earlier version of this
      section claimed those two fixtures covered the sample version bumps; that was false and is
      corrected above. Needed: a fixture that loads the real resource, asserts its `@coreai` header
      (id/version/capabilities), and drives the deterministic parts — obstacle tunnelling at a large
      `dt`, the gravity remainder and per-frame cap, and the normal↔soft-drop interval transition.
- [ ] **Play-test both samples in Play Mode.** The camera-axis rewrites and the Lane Racer collision
      change were verified by hand-walking the arithmetic, not by playing them.

## OpenAI-compatible reasoning isolation (2026-08-27) — 7.0.7 prepared

- [x] `message.content` remains the single source of the visible non-streamed response;
      `reasoning_content`/`reasoning`/`reasoningContent` are kept as diagnostics only.
- [x] SSE reasoning deltas are never promoted to the final visible text; the consumer receives them only in
      `LlmStreamChunk.ReasoningText`, and assembles the finished answer from `LlmStreamChunk.Text`.
- [x] An enabled reasoning mode changes only provider request controls and does not change the response split.
- [x] 4 deterministic PlayMode tests `ReasoningIsolationPlayModeTests` run fake transport →
      real provider/LLM/orchestrator/chat layers and check the UI consumer, diagnostics, ChatHistory, and
      command publication with no model, API key, or network.
- [x] The XML API and the package `AGENT_BUILDER.md`, `DEVELOPER_GUIDE.md`, `STREAMING_ARCHITECTURE.md` pin down:
      reasoning is only short-lived diagnostics, never a MemoryTool/ChatHistory/note/command/auto-record.
- [x] Six packages, ten internal dependency pins, and `McpServerInfo.Version` are synced to 7.0.7.
- [x] File-only gates: `CoreAI.Core.csproj`, `CoreAI.Source.csproj`, `CoreAI.Tests.csproj` and
      `CoreAI.Tests.PlayMode.FastNoLlm.csproj` (including the new fixture) build with 0 errors; package/MCP
      lockstep and `git diff --check` pass.
- [x] **Verification gate (next Editor session):** run fixtures — closed: covered by the full green EditMode runs 3256/3247/0/9 (`artifacts/testresults/g9.xml`) and the 2026-09-02 browser gate (verified 2026-09-04)
      `MeaiOpenAiChatClientSseEditModeTests`, `MeaiOpenAiChatClientHttpEditModeTests` and
      `MeaiLlmClientEditModeTests`, then the PlayMode fixture `ReasoningIsolationPlayModeTests` from assembly
      `CoreAI.Tests.PlayMode.FastNoLlm`. In this task Unity and running tests are directly forbidden.

## AgentBuilder per-request system prompt declaration (2026-08-26) — 7.0.6 prepared

- [x] `WithPerRequestSystemPrompt()` explicitly declares that every call supplies
      `AiTaskRequest.SystemPrompt`; `MissingSystemPrompt` is suppressed only for that declaration.
- [x] The genuine custom-role warning and built-in fallback behavior remain pinned by the existing tests;
      a new regression pins the per-request declaration branch.
- [x] `CoreAI.Core.csproj` and `CoreAI.Tests.csproj` compile with 0 errors; package/MCP lockstep and
      `git diff --check` pass.
- [x] **Verification gate (next editor session):** run the focused `AgentBuilderEditModeTests` fixture. — closed: covered by the same full green EditMode runs (verified 2026-09-04)
      Unity Editor was not opened by requirement for this fix wave.

## Independent log-prefix controls (2026-08-12) — 7.0.3

- [x] `[CoreAI]` and feature prefixes are independently optional while both remain enabled by default.
      `IGameLogSettings` is unchanged; the optional formatting contract preserves external implementations.
- [x] Game Log Settings exposes clear omission checkboxes for hosts whose own logging facade identifies messages.
      Scoped DI and the unscoped fallback pass the live settings through the real logger/sink chain.
- [x] Package checks are green: analyzer `dotnet test` 8/8, positive-module contract PASS, and 7.0.3
      package/MCP lockstep PASS.
- [x] **Verification gate:** Unity 6000.3.14f1 full EditMode is 2654 passed / 0 failed / 9 skipped
      (2663 total, 118.609 s). All four exact final-output cases and the DI sink case pass. The shared
      LlmInfra `ChatMessage` collision is fixed with an explicit CoreAI alias; the DI test restores
      process-wide `Log.Instance` so later tests keep their authored prefix expectations.
- [x] Unity-regenerated dotnet gates pass sequentially: Core 0 warnings / 0 errors; Source 0 / 0;
      Core.Tests 2 / 0; Tests 4 / 0. The prior `AiAttachment` CS0246 errors disappeared after successful
      Unity compilation regenerated project inputs.

## Unity 6.6 UI Toolkit UXML wave (2026-08-12) — 7.0.1

- [x] `CoreAiChatMessageBubbleElement` migrated from the `UxmlFactory` / `UxmlTraits` removed in Unity 6.6 to
      `[UxmlElement]` + `partial` + `[UxmlAttribute]`. UXML attribute names (`is-user`, `message-text`,
      `avatar-sprite`), the fully qualified type name in `CoreAiChatMessageBubble.uxml`, defaults, and the public
      parameterless constructor are preserved. An audit of the rest of the repo found no legacy UITK API.
- [x] The Unity 6.0–6.6 Input System is guarded by a double gate: `ENABLE_INPUT_SYSTEM` enables the backend only with the
      package `COREAI_HAS_INPUT_SYSTEM`; Unity 6.7+ uses the built-in module via version define. This
      keeps old projects with New/Both in Player Settings compiling, but without the UPM package, and does not bring back
      silent input loss on 6.7. The license-free package-graph gate pins all three asmdefs and eight `#if`s.
- [x] **Verification gate (next editor session)**: run `CoreAiChatMessageBubbleElementEditModeTests` — closed: 7.0.1 released, later full runs green (verified 2026-09-04)
      (including the new UXML-contract pin) and the full EditMode set. Static verification on the installed
      Unity 6000.3.14f1 passed: the UI Toolkit generator produced `UxmlSerializedData`, and the Input backend builds
      both with and without the package. A full `dotnet build CoreAI.Source.csproj` is still blocked by stale
      Unity-generated project inputs (`AiAttachment` and the deleted `CoreAiBackendPanel.cs`); Unity was not
      launched in this check per a direct task constraint.

## Post-7.0.0 audit fix wave (2026-08-09) — unreleased

Done (compile gates green via `dotnet build`; EditMode suite runs on next editor start — verification gate):

- [x] **WebGL: `WorldStateManager.Reset()` flushed nothing after deleting the save file**, so the deleted
      save resurrected from IndexedDB on reload. Reset now calls `CoreAiWebGlPersistence.Sync()` like `Save()`
      does; regression pinned by `WorldStateManagerResetPersistenceEditModeTests` (new seam:
      internal `WebGlFlushSync`).
- [x] **Mod-log pipeline wired end-to-end.** `LuaLogService` is registered as singleton `ILuaLogService`
      in `RegisterCoreAiMods`, threaded through `LuaCsModStackOptions.LogService` into `LuaCsModRuntime`
      (print/report → Print, handler/event/load errors → RuntimeError, quarantine → Error), and
      `get_mod_logs` (`GetModLogsLlmTool`) is attached to the Programmer role. MCP `get_mod_logs` now
      resolves the same singleton with no MCP-side change. Tests: installer wiring + five runtime-append
      tests + two tool tests.
- [x] **Five duplicate `CoreAi_PersistFsSync` DllImport wrappers deleted** (FileLuaModStore,
      FileLuaModSourceStore, FileSkillStore, FileAgentMemoryStore, WorldStateManager); all call the shared
      `CoreAiWebGlPersistence.Sync()` (now returns `bool`), and flush failures are always logged — the
      silent swallow in the mod/skill/memory stores is gone.
- [x] **MCP HTTP server no longer starts on WebGL players** — `CoreAiMcpServer.StartListening()` early-outs
      with a logged warning when `Application.platform == WebGLPlayer` (pinned by
      `CoreAiMcpServerWebGlEditModeTests`).
- [x] **Repo hygiene per AGENTS.md "no audit reports in repo":** deleted root `PROGRESS.*.md` (3),
      `Docs/Audits/2026-07-16/` (8), finished audit/investigation reports in `dev-docs/` (9) and
      `Assets/CoreAI/Docs/PERF_REVIEW_2026-06-12.md`; `dev-docs/README.md` no longer sanctions audit notes.
      Deleted empty husk folders (`Assets/Tests/Edit`, `Assets/_source`, 7 empty dirs under
      `Assets/CoreAI/Runtime/Core/`). `bundleVersion` synced 6.11.1 → 7.0.3 (lockstep).
- [x] **Convention/doc nits:** `var` → explicit in `FetchSseOpenAiTransport.cs`; `Docs/ARCHITECTURE_RULES.md`
      stale `RobloxSpace`/`RobloxApi` identifiers → `RbxSpace`/`RbxApi`.
- [x] **Demo mods ported off the withheld `coreai_world_*` build APIs.** FullAccess Tetris
      (`LuaPlatformExampleController.cs` embedded source) and LuaMods `WaveDirectorMod.lua.txt` now use the
      Rbx API (`Instance.new('Part')`, `Position`/`CFrame`, `Color3.fromHex`, `instance:Destroy()`);
      `coreai_world_exists` (Read tier) intentionally kept. Regression pinned by new
      `DemoModProductionSurfaceEditModeTests` (source lint + headless load in production configuration).
      `KNOWN_ISSUES.md` Tetris/WaveDirector section removed as resolved.
- [x] **Lua mod-authoring docs audited and fixed against the Lua-CSharp stack** (FIRST_MOD,
      LUA_ACCESS_MODES, LUA_GAME_API, LUA_NATIVE_APIS, LUA_BEST_PRACTICES, LUA_SANDBOX_SECURITY, LLM_TOOLS,
      AGENT_BUILDER recipe 5): dead MoonSharp-era names (`LuaModRuntime`, `SecureLuaEnvironment`,
      `log_info`, `GameLuaBindingsExtensibility`, `-- name:` headers), wrong tiers/budgets (10 s / 50M
      steps, quarantine-not-unload), Lua 5.4 → 5.2 + Luau downleveler, withheld-API examples fenced as
      opt-in-only. Logging README `warn` claim dropped (no `warn` binding exists).
- [x] **Engine-free tests executed outside Unity** (editor holds the project lock): a NUnitLite
      runner under `Temp/nunitlite-runner` ran **93 passed / 0 failed** — all 7
      `Assets/CoreAI/Tests/EditMode` fixtures (67), `LuaLogServiceEditModeTests` (19, incl. the two new
      `get_mod_logs` tool tests), the 5 new `LuaCsModRuntime` log-append tests, and the new
      `DemoModProductionSurface` Tetris tests (source lint + full headless autopilot game under the exact
      production composition). Excluded as Unity-native (ECall: ScriptableObject/GameObject/
      Application.dataPath): WaveDirector runtime test, `LuaModsLlmToolEditModeTests`,
      `WorldStateManagerResetPersistenceEditModeTests`, `CoreAiMcpServerWebGlEditModeTests` — these run in
      the editor Test Runner on next editor start (verification gate).
- [x] **Full EditMode suite executed in Unity batchmode (2026-08-09, editor closed):**
      **2658 test cases — 2649 passed / 0 failed / 9 skipped** in 125.8 s (via a temporary
      `TestRunnerApi` batch entry point, deleted after the run; standard `-runTests` CLI is ignored by
      UTF 1.6 in this environment). Includes all wave tests: `WorldStateManagerResetPersistence`,
      `DemoModProductionSurface` (all 4), `LuaModsLlmTool` wiring, `CoreAiMcpServerWebGl`. Note for future
      batch runs: this shell needs standard Windows env vars restored (`SystemDrive`, `ProgramData`,
      `ComSpec`, …) or Unity's UPM IPC fails; `TEMP`/`TMP` must be Windows-style paths.

Open:

- [ ] **Comment-convention cleanup wave (~3,208 narrative comment lines in ~2,015 blocks / ~340 files,
      plus 38 `/* */` blocks).** Convert rationale blocks to `// WHY:`, delete section banners and
      change-description narration. Heaviest: `CoreAIBenchmark/.../GameCreationBenchmarkHarness.cs` (236),
      `MeaiLlmClient.cs` (150), `ToolExecutionPolicy.cs` (109), `ToolExecutionPolicyEditModeTests.cs` (89),
      `MeaiOpenAiChatClientSseEditModeTests.cs` (72); `CoreAiUnity/Tests` and `CoreAIBenchmark` hold the bulk.
      NOTE: `tools/strip_non_doc_comments.py` exists but deletes `// WHY:`/`// HACK:` too and only covers
      2 packages — fix or replace it before any mass run.
- [ ] **`SemaphoreSlim.Wait()` reentrancy on WebGL**: synchronous main-thread gates in
      `FileAgentMemoryStore`, `FileLuaModSourceStore`, `FileLuaModStore`, `FileSkillStore`,
      `FileLuaScriptVersionStore`, `ISkillStore` have no reentrancy protection — a mod callback re-entering
      the same store self-deadlocks the single WASM thread. Decide: guard (reentrancy detection) or document.
- [ ] **WebGL canary for the Lua↔C# sync-over-async bridge**: `LuaCsExecutionGuard.cs:143,177`,
      `LuaCsSecureEnvironment.cs:205,390`, `LuaCsCoroutineHandle.cs:198` rely on Lua-CSharp ≥ 0.5.6
      completing suspended resumes synchronously. Add a WebGL-build canary test so a broken upstream
      invariant fails CI instead of deadlocking players.
- [ ] **WebGL persistence decision for conversation summaries + token calibration**:
      `FileConversationSummaryStore` and `FileTokenCalibrationStore` never flush and are excluded on WebGL
      only by DI gating (`CoreAILifetimeScope.cs:381-417`); `CoreAI.Core` cannot reference
      `CoreAiWebGlPersistence` (`references: []`). Decide: won't-fix (document) or injectable flush sink.
- [ ] **Editor tooling has zero test coverage** (16/20 editor classes unreferenced): scene creators,
      `CoreAIModuleManager`, build guards, `LuaScriptedImporter`/`LuauScriptedImporter` — the primary
      onboarding path. Add EditMode coverage for the top user-facing paths.
- [ ] **Untested MCP tools**: `GetModLogsMcpTool` (now live — cover it), `ManageModsMcpTool`,
      `WorldCommandMcpTool`, `MainCameraScreenshotSource`. Untested Hub UI: `HubAboutPage`,
      `HubBuiltInPages`, `HubPageWidgets`.
- [ ] **Vision wire-format unverified live**: `VisionSelfProbe.cs:100` image-content shape never checked
      against a real backend; `HubSettingsPage.cs:1381` probe round-trip likewise. Run one live probe per
      supported backend.

## Deleted-audit residue (verified 2026-08-09) — findings still open after the dev-docs cleanup

> Source: the nine audit/investigation reports deleted from `dev-docs/` per AGENTS.md
> (ARCH_AUDIT, CODE_AUDIT v6.4/v6.6, LUA_PERF_AUDIT, PERF_AUDIT_ITER2, ROBLOX_API_CONFORMANCE,
> ROBLOX_CONFORMANCE_REAUDIT, COORD_ROTATION_INVESTIGATION, ALLOC_BACKSTOP_FLAKE). Every finding
> below was re-verified against the current code on 2026-08-09 and is still present; everything
> the audits flagged that is NOT listed here was confirmed fixed (mostly in the 6.8.0 wave).

**Architecture (from ARCH_AUDIT):**

- [ ] **`CoreAiChatPanel` god-view (H1, grew 3636 → 4185 lines).** Extract `ChatTranscriptRenderer`
      (bubbles/streaming/render-cap + think filter), `ChatRoutingUiController` (agent + API-profile
      dropdowns; `ICoreAiRoutingUiController` seam exists), `ChatInputGate` (`IsChatInputAllowed`
      already static-internal). Panel becomes a thin wiring view.
- [ ] **Service-locator leak past the composition edge (M1).** No shared scene-scope seam exists;
      `FindAnyObjectByType<CoreAILifetimeScope>` + `Container.Resolve` is re-implemented at 7
      production sites (`CoreAiChatService.cs:55`, `TokenBudgetRuntimeSource.cs:75`, `CoreAi.cs:820`,
      `InGameChatPanel.cs:36`, `VisionSelfProbe.cs:216`, `AiScheduledTaskTrigger.cs:90`,
      `LlmUnityAutoDisableIfNoModel.cs:61`; `CoreAiBackend.cs:666` at least funnels through one
      private helper). Introduce `CoreAiSceneScopeLocator`; only the facade calls it.
- [ ] **Oversized adapters (M2, both grew).** `MeaiLlmClient.cs` 2790 lines (push transport creation
      into `LlmEndpointClientFactory`, hybrid-JSON parsing behind `LlmToolCallTextExtractor`);
      `LuaCsModRuntime.cs` 2194 lines (extract `ModPersistenceCoordinator` + `CrossModExportBridge`).
- [ ] **Stale MoonSharp dual-VM docs (L2).** `LuaCsModRuntime.cs:30-34` class summary still claims
      "both VMs coexist"; `LuaModsLlmTool.cs:91` still teaches "MoonSharp/Lua callback syntax".
      MoonSharp was removed in 5.4.0 — rewrite to the single-VM reality.
- [ ] **Ambient mutable statics (L3).** `Log.Instance`, `CoreAISettings.Instance`,
      `CoreAISettingsAsset.Instance`, `GameLoggerUnscopedFallback.Instance` are public mutable
      statics; `CoreAiBackend.cs:583/588/672` still falls back to them. Constrain so logic never
      reads them instead of injected dependencies.
- [ ] **Copy-paste XML doc (L4).** `MeaiLlmClient.cs:37-41` — `LiveUiStreamMaxCharsPerChunk` summary
      contains a stray "Initializes a new instance of the current component." sentence.
- [ ] **Stale generated csproj (L5).** `CoreAI.RobloxApi.{Binding,Datatypes,Instances,Unity}.csproj`
      at repo root are leftovers from the Rbx rename (no matching asmdef) — delete/gitignore.

**Correctness (from CODE_AUDIT v6.4 / v6.6):**

- [ ] **Connection registry never prunes dead connections (v6.4 MEDIUM-1).** `RbxScriptConnection.
      Disconnect` and `:Once` auto-disconnect remove the connection from the signal but not from
      `ModConnectionRegistry._byMod` — a chatty long-lived mod leaks dead entries until teardown.
      Notify the registry on disconnect or compact `!Connected` entries.
- [ ] **Connection-registry threading invariant is assumed, not enforced (v6.4 MEDIUM-3).**
      `ModConnectionRegistry`/`RbxScriptSignal` are unsynchronized while `DisconnectOwnedBy` can be
      driven from mod-lifecycle tool paths. Assert main-thread at `Track`/`DisconnectOwnedBy` (dev
      builds) or lock; verify `manage_mods` marshals to the main thread.
- [ ] **Bare `catch { }` blocks (v6.6 core #5).** `AiOrchestrator.cs:1057` (also 780, 1956),
      `ToolExecutionPolicy.cs:309` (also 786, 852, 893) — a cancelled predecessor is
      indistinguishable from a faulted one; outer cancel does not short-circuit serialized calls.
- [ ] **`CoreAISettings` lock asymmetry (v6.6 core #8).** `ResetOverrides` locks, ~30 readers don't;
      partially-reset override set is observable (now documented in a WHY comment — decide: fix or
      accept explicitly).
- [ ] **IMGUI overlay in shipped demo scenes (v6.6 unity #10, partial).** `CoreAiTokenBudgetOverlay`
      still referenced in `MiniRpgModsDemo.unity` + `LiveMechanicsModsChatDemo.unity`; UITK
      replacement exists. `OrchestrationDashboard`/`AiDashboardPresenter` no longer in any scene.
- [ ] **Test quality leftovers (v6.6).** `GameConfigPlayModeTests.cs:62,71,97,119` still `.Result`
      on the main thread in `[UnityTest]`; `PlayModeTestAwait` 3-arg overload still leaks the task
      on timeout (4-arg overload fixed); machine-mangled comments in ~18 test files (fold into the
      comment-cleanup wave above, but note Cyrillic restoration, not just re-tagging).
- [ ] **Hub duplication/dead code (v6.6).** `MakeGgufModelDropdown`/`MakeEndpointGgufDropdown`
      byte-identical twins (`HubSettingsPage.cs:1791/1834`); `SetPlaceholder` installs a 200 ms
      polling timer per text field (~13 sites).

**Lua/Rbx performance (from PERF_AUDIT_ITER2 + LUA_PERF_AUDIT):**

- [ ] **Boxing guard seam on the hot path (ITER2 HIGH-2 rest + MEDIUM-1 + MEDIUM-2).**
      `InvokeGuarded` (`LuaCsModRuntime.cs:1076`) routes every timer/event call through the boxing
      `IScriptExecutionGuard.Invoke` and discards the result — add a non-boxing void overload +
      reusable scratch `LuaValue[]`; `handlers.ToArray()` snapshot per event (:1051) — reuse the
      `RbxScriptSignal.Dispatch` scratch-buffer pattern; `InvokeGuarded(mod, fn, evt.Key,
      evt.Value)` (:1068) allocates `new object[2]` — add a non-params overload.
- [ ] **`DynamicInvoke` per call on the polled binding surface (LUA_PERF #3, largest remaining).**
      `LuaCsApiRegistry.cs:207` uses reflection invoke for the whole `unity_*`/`input_*` surface.
- [ ] **Per-property-read dispatch probes (LUA_PERF #5).** `LuaCsRbxInstanceBindings.cs:149-207`
      read path does up to five type probes ending in a `ClassCatalog.IsA` ancestry walk per read;
      compute a class-kind bitmask once at wrap time.
- [ ] **Double allocation per datatype crossing (LUA_PERF #1).** `LuaCsRbxValues.Box` boxes the
      struct then wraps the box — a generic `LuaCsRbxValueBox<T>` halves it (identity documented
      non-semantic).
- [ ] **Adaptive hook batch (LUA_PERF §4, measured 2.65× speedup available, needs design).**
      Re-arm the count hook sized by allocation-budget headroom; feasibility proven upstream
      (`SetHook` from inside a hook works on Lua-CSharp v0.5.6). Open items: charge step budget by
      the batch actually in force; `EndGuard` must restore the enclosing window; ship only with an
      allocation-bomb test at several live-heap sizes. Relates to `TODO(guard-tight-loop-latency)`.
- [ ] **World-command JSON alloc per command (ITER2 MEDIUM-3, guardrail/docs).**
      `LuaCsWorldRuntimeBindings.cs:584-599` — `JsonUtility.ToJson` + envelope + command per call;
      document that per-frame animation belongs on the zero-alloc Rbx part sink, not
      `coreai_world_change`.
- [ ] **Guard-overhead microbenchmark (LUA_PERF §6).** No checked-in EditMode benchmark (no-hook /
      batch-4 / batch-64 fixed loop) — fold into F-20.

**Roblox conformance (from ROBLOX_API_CONFORMANCE + REAUDIT + COORD_ROTATION):**

- [x] **`game:BindToClose(fn)` now validates its argument (#2.9, 7.1.0).** The finding as written was
      wrong: `RbxDataModel.BindToClose` already raised a loud `NOT_IMPLEMENTED` (phase MVP5), so the
      call was never silent. What was missing is argument checking — the binding passed `null` and
      never read arg 1. It now raises `BAD_ARGUMENT` naming the received type for a non-function and
      reaches the MVP5 stub for a function. Implementing the callback itself stays MVP5.
- [x] **BasePart `Orientation`/`Rotation` wired; `Material` is now tracked (#2.5, 7.1.0).**
      `Orientation` (YXZ, degrees) and `Rotation` (XYZ, degrees) go through the existing
      `RbxCFrame.ToOrientation`/`ToEulerAnglesXYZ`; setters preserve `Position`. `Material` stays a
      loud stub but its phase string is now `MVP2 (materials catalog)`, matching a real ladder
      deliverable (ROBLOX_API_ROADMAP §5.2.1 item 12) — no longer untracked work. The `Rotation`
      Euler order is an inference, recorded as decision **D10**: the offline mirror documents only
      "degrees for the three axes" and the API dump gives no order.
- [x] **Scene-level handedness golden test landed (COORD_ROTATION follow-up 1, 7.1.0).**
      `RbxSpaceSceneHandednessGoldenEditModeTests` pins screen-side agreement in both spaces,
      `mod z = -(Unity z)` on every trace point in both directions, and the right-handed→left-handed
      chirality flip, under both RobloxSpace scales. **The trace recorded here was wrong**: with
      eye=(0,9,-14) → target=(0,2,18) the camera looks along +Z, so `RightVector` is (-1,0,0) and
      Roblox +X is on the LEFT. The test uses the real Lane Racer camera instead — eye=(0,9,14) →
      target=(0,2,-18), where `RightVector` is (+1,0,0).
- [x] **Both samples now derive their axes from the camera (COORD_ROTATION follow-up 2, 7.1.0).**
      Lane Racer builds lane/track axes from `CurrentCamera.CFrame.RightVector`/`LookVector`
      projected onto the ground; Tetris 3D does the same for its horizontal/depth axes while its grid
      logic stays integer and camera-independent. Both mod versions were bumped so the bundled-mod
      seeder actually delivers the fix to installed copies.
- [x] **Rung ambiguity resolved (7.1.0).** `WaitForChild` is MVP1 for an existing child; the yield,
      the 5 s `Infinite yield possible` warning and the timeout overload are MVP2. Stated
      unambiguously everywhere it appears in ROBLOX_API_ROADMAP.

**Deferred by the audits themselves (no action needed now, recorded so the context survives):**

- v6.4 MEDIUM-2 (RenderStepped fired last in the pump) and LOW-1 (delta boxed before the
      `HasConnections` gates) — both deferred to the MVP2 scheduler rewrite by design.
- v6.4 LOW-2/LOW-3/LOW-4 — **done in 7.1.0** with the camera-axis sample wave. Tetris returns piece
      cells as scalars instead of allocating five tables per frame; Lane Racer tests whether the
      obstacle's movement SEGMENT crossed the car band instead of sampling its endpoint, so a frame
      spike can no longer tunnel a block through the car; Tetris subtracts the gravity interval
      instead of zeroing the accumulator, steps every elapsed interval, caps catch-up at eight steps
      per frame and drops the surplus when that cap is hit.
- ALLOC_BACKSTOP_FLAKE — resolved: `GC.GetAllocatedBytesForCurrentThread` re-verified returning 0
      on the current editor (recorded in `LuaCsExecutionGuard.cs:287`); the first-growth limitation
      is the tracked item at "Allocation guard is a per-call first-growth backstop" below. The
      flaky test stays as the honest trip-wire per the report's own recommendation.

## Turn teardown + positive module opt-in wave (2026-08-01) — shipped in 7.0.0

- [x] Breaking provider/Lua define migration uses independent positive `COREAI_LLM` and `COREAI_LUA` symbols.
      No symbols retain portable orchestration/chat, scripted/stub clients, and required MEAI contracts;
      `COREAI_LLM` adds concrete HTTP/MEAI/LLMUnity providers, `COREAI_LUA` adds Lua, and both enable the
      full provider + Lua runtime.
      Setup Enable adds each symbol and Disable removes it. CI names are `core` / `llm` / `lua` / `full`,
      verifies Standalone + WebGL injection, gates sandbox coverage in `lua`/`full`, and gates LLM coverage
      in `llm`/`full`; FastNoLlm PlayMode compiles with `COREAI_LLM`. The license-free contract guard has
      mutation RED/GREEN evidence; active source/tests/CI/setup/current docs contain no legacy negative
      symbols, while released changelog/audit history retains original names and semantics. Asmdef
      assemblies are not blanket-gated.

- [x] Failed/cancelled/abandoned turns persist one raw user message per orchestration invocation / admitted
      queue item after all internal passes, including queue exits before inner and direct authority denial;
      context-overflow retries never receive the in-flight message as their own history, and a failed user
      append cannot leave an assistant-only history pair. A separate external retry is intentionally outside
      this guarantee until `IAgentMemoryStore` has a stable idempotency key.
- [x] Stable provider-cache layering keeps role/persona + the complete canonical role tool contract in the
      shared prefix; per-request instructions, student memory, request tool availability and world state are
      ordered system-tail messages. The paid OpenRouter/Cloudflare probe kept 6383 prompt tokens stable and
      reported cache reads `0 -> 6144 -> 6144` with exact `cloudflare/fp8` routing and fallback disabled.
- [x] `ServerManagedApi` accepts dynamic host-specific headers without rebuilding the client. Inner/global
      providers compose into one immutable snapshot per invocation; transport/auth retries, outer sync
      result/exception retries and streaming pre-commit retries reuse it, while a later invocation reads the
      current lesson/cohort even if the host reuses the request object. The custom hook cannot replace
      auth/content type/trace/idempotency, and the backend validates attribution values.
- [x] Production memory, flat chat history, structured transcript and compacted summary share one canonical
      `AgentMemoryScope` boundary. Host-provided scope wins the legacy empty default; two sequential students
      with the same role are isolated, while `AgentMemoryScope.Empty` preserves exact legacy role keys.
- [x] Streaming bubble ownership has one release path; stale teardown cleans only its own bubbles, while
      `OnDisable` releases the active USS class before embedded UI references are dropped.
      Verification evidence (2026-08-01): the post-restore focused turn/panel suite is 28/28 GREEN after three
      mutation RED runs; provider-cache and memory-scope guards each demonstrated RED and restored GREEN; the
      common full EditMode gate is 2615 passed / 0 failed / 9 skipped. `python tools/bump_version.py --check`
      and the touched Core/Source/Tests/LlmInfra/LlmVerification compile gates are green. This evidence does not
      claim the later v7.0 positive-module matrix or non-live PlayMode gates recorded above/below.

## Chat / LLM host wave (2026-07-31) — shipped in 6.13.0, see both CHANGELOGs

- [x] **Typing indicator moved to USS.** Three `coreai-typing-dot` elements + one class flip instead of
      rewriting `TypingLabel.text` every 400 ms; timing/stagger in
      `Assets/CoreAiUnity/Resources/CoreAI/UI/CoreAiChatTypingDots.uss`, which the panel attaches to the
      chat root so hosts with their own chat UXML keep a working indicator.
- [x] **`CoreAiChatPanel.CreateMessageBubbleRow` is `protected`** and applies the row side through the new
      `ICoreAiChatMessageBubble` interface, so a host that only swaps bubble CONTENT stops copying the row
      scaffolding.
- [x] **Deferred scroll jobs no longer crash a closing panel.** `ResetUiReferences()` nulls the UI fields
      while the jobs `ScrollToBottom()` queued (immediate + 80/200/500 ms) keep firing on the still-live
      visual tree, so one landing in that window threw `NullReferenceException` out of
      `BaseRuntimePanel.Update`. They all go through the single guarded `ScheduleOnMessageScroll` helper
      now; `ScheduleStreamingScrollToBottom()` carried the identical unguarded dereference and is fixed by
      the same change. Found from a failing PlayMode fixture in RedoSchool.
- [x] **No hidden `gpt-4o-mini`.** An unset model is an explicit `LlmErrorCode.InvalidRequest` for
      client-owned modes and an omitted `model` key under `ServerManagedApi`; the result's `Model` comes
      from the provider response instead of the client setting; the inspector hides the model field under
      `ServerManagedApi` and warns when a client-owned profile has none.
      Verification gate: run `CoreAiChatPanelRenderPathsEditModeTests` (including the two new pins —
      `ScheduledScrollJob_AfterUiReferencesCleared_IsSkippedInsteadOfDereferencingNull` and
      `TypingDotsPulseInterval_CoversTheStyleSheetWave`), `MeaiOpenAiChatClientHttpEditModeTests`,
      `CoreAISettingsAssetEditModeTests` and the full EditMode suite on next editor start; visually confirm
      the dot wave in the chat demo scene (compile gates all green via `dotnet build CoreAI.sln`, 0 errors).

## Roblox API ladder (MVP0-MVP17) — foundation items (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`)

> **Pending (2026-07-23):** `UserInputService` pulled into **MVP1** (from MVP10) for mini-game controls,
> built on the **New Input System** (`com.unity.inputsystem` 1.19.0; `activeInputHandler = 2` Both).
> **TODO: this dependency may be dropped later** — keep the Lua `UserInputService` behind a swappable
> input seam (`IInputSource`-style) so the New-Input-System backend can be replaced/removed without
> touching the Lua-facing API or breaking mods. Legacy `input_*` poll bindings stay for back-compat.

- [x] **1. Engine abstraction seam** *(rung: MVP0)* (2026-07-22) — neutral `CoreAI.Scripting` contracts
      (`IScriptEngine`/`IScriptState`/`IValueMarshaller`/`IScriptFunctionRegistry` +
      `ScriptCallContext`/`IScriptTable`/`IScriptCoroutine`/`IExecutionBudget`/`IScriptExecutionGuard`);
      `LuaCs*` classes moved under `Runtime/Scripting/LuaCs` as the single adapter layer; runtime,
      logic slots, tool executor, envelope processor and all binders migrated off Lua types;
      marshalling consolidated into `LuaCsValueMarshaller`; seam-honesty scan test guards the boundary.
      Verification gate: run `ScriptEngineSeamEditModeTests` + `ScriptingSeamHonestyEditModeTests` and
      the full EditMode suite on next editor start (compile gates all green via `dotnet build`).
- [x] **1a. Quarantine error policy + logic-slot teardown** *(rung: §2 quarantine decision)* (2026-07-22) — `LuaCsModRuntime` now
      QUARANTINES a mod at the consecutive-error threshold (`MaxErrorsBeforeQuarantine`, default 8,
      `LuaCsModStackOptions` option) instead of unloading it: dispatch (handlers/timers/queued events)
      suspends, the mod stays listed/addressable, `ReloadMod` clears quarantine + streak; stale-tick-
      snapshot identity check so a mid-tick repair reload is never suspended on the old instance's
      streak. New `ModQuarantined`/`ModTearingDown(reason)` events; `TeardownModEffects` clears
      `logic_define` overrides on unload/reload/quarantine (`LuaCsLogicSlots.ClearOwnedBy`, owner mod
      id threaded through the bindings seam); override failures are attributed into the handler-error
      diagnostics channel instead of a silent vanilla revert. `manage_mods` list/diagnostics surface
      `quarantined`; auto-repair prompt rewritten. Docs: `mod-system.md` §5a.
      Verification gate: run the new `LuaCs_Quarantine_*` / `LuaCs_LogicSlots_*` tests in
      `LuaCsModRuntimeEditModeTests`, `List_QuarantinedMod_SurfacesQuarantineFlagAndHint` in
      `LuaModsLlmToolEditModeTests`, and the full EditMode suite on next editor start (compile gates
      green via `dotnet build`: Mods, Source, Mods.Tests, Tests, Demos, Mods.Hub, Benchmarking).
- [x] 2. Instance/DataModel registry *(rung: MVP1)* — LANDED 6.3.0. `game`, `workspace`,
      `Instance.new`, `.Parent`, `FindFirstChild`, `GetChildren/GetDescendants`, `Clone`, `Destroy`,
      attributes; multiplayer-ready ids (`InstanceRegistry` owns identity; reserves Mirror `netId` +
      world-command name fields). `WaitForChild` resolves an existing child here; its yield is MVP2.
- [x] 3. Core datatypes *(rung: MVP1)* — LANDED 6.3.0. `Vector3`, `CFrame`, `Color3`, `UDim2`,
      `Enum.*` with golden fixtures against documented Roblox values.
- [x] 4. Task scheduler *(rung: MVP2)* — `task.wait/spawn/defer/delay`, legacy `wait`, on the existing coroutine — closed: `ModScheduler` + `LuaCsRbxSchedulerAdapter` shipped and wired in 7.1.0 (verified 2026-09-04)
      substrate; `RunService.Heartbeat/PostSimulation`; connection objects with `:Disconnect()`.
- [x] 5. `game:GetService` *(rung: MVP2)* — whitelist registry; unimplemented services = loud stubs. — closed: `ServiceCatalog.cs` (Assets/CoreAIMods/Runtime/RbxApi/Instances) — allowlist + loud `RbxStubService` stubs (verified 2026-09-04)
- [x] 6. Luau preprocessor *(rung: MVP5 — `LoadMod` wiring)* — LANDED as the targeted mini-rewriter
      (`Assets/CoreAIMods/Runtime/LuauDownlevel/`: `LuauLexer`/`LuauRewriteParser`/`LuauDownleveler`,
      standalone, 93 EditMode tests; Q1 resolved — Loretta reconsidered only if construct coverage
      proves insufficient). Remaining work is the MVP5 wiring into `LoadMod` with source maps.
- [ ] 7. Mod system UX *(rung: MVP5)* — `Mods/<ModName>/` with `mod.json` manifest, script contexts mapped to
      folders, enable/disable without deletion, hot reload, C# management API.
- [x] 8. Lua log service *(rung: MVP5, deliverable 7)* — per-mod ring buffers + `get_mod_logs` AI tool (core shipped; wire into the — closed: the service is registered in `CoreAiModsInstaller`, the tool is wired up, `LuaCsModRuntime` writes into the buffers (verified 2026-09-04)
      mod runtime's print/warn/error capture, DI composition, and the Programmer tool set).
- [x] 9. Editor syntax highlighting *(rung: MVP7)* — importer + highlighted inspector/editor window (shipped in — closed: `LuaScriptedImporter`, `LuauScriptedImporter`, `LuaScriptViewerWindow` exist (verified 2026-09-04)
      `[Unreleased]`; keep in sync with Luau constructs from item 6).
- [x] 10. Networking API stubs *(rung: MVP2)* — `RemoteEvent`/`RemoteFunction`/`ReplicatedStorage` in local-loopback — closed: `Networking/INetworkBridge.cs`, `NullNetworkBridge.cs`, `RbxRemotes.cs` exist (verified 2026-09-04)
      via `INetworkBridge`; `NullNetworkBridge` default.
- [x] 11. Test corpus *(rung: MVP2)* — ~20 real-world Roblox tutorial-grade scripts as fixtures (preprocessor + API — closed: 40 fixtures in `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/Fixtures/` + `LuauDownlevelerRbxCorpusEditModeTests` (verified 2026-09-04)
      smoke: "paste → runs").

## [A7] Post-remediation audit residue (2026-07-17) — accepted-risk items from the a2a2311e audits

> Two codex reviewers + one Fable 5 deep audit over the 5.9.0 remediation wave. Everything confirmed
> and tractable was fixed in the follow-up commit (streaming health, generation-stamped health reports,
> LlmClientException code preservation, bounded host drain, snapshots under the lock, abandoned-stream
> terminal publish, two test hardenings, routing docs). Remaining accepted-risk items:

- [x] **Route pinning across one request's construction.** Tool capability and context window are — closed: `ILlmClientRegistry.cs:8` `LlmRoleRouteSnapshot`, `:79` `ResolveRouteForRole` (verified 2026-09-04)
      resolved at prompt-build time, the client at send time; a role reassignment landing inside that
      millisecond window can build one request with the old endpoint's budget/tool contract and send it
      to the new endpoint (self-heals next request). Proper fix: resolve one `LlmRoleRouteSnapshot` per
      task turn and pin its profile id into the outgoing request (needs an effective-profile
      resolution hook on `ILlmClient`; weigh against explicit-pin failover semantics).
- [ ] **No generation lease between resolve and execution.** `InFlightRequests` increments only inside
      `CompleteAsync`; a caller that resolves a client and holds it before invoking can race a
      removal/re-save that force-releases the host. Window is microseconds on the routing path;
      relevant only to external `ResolveClientForRole` users holding clients across awaits.
- [ ] **Disposed registry keeps resolving.** `LlmClientRegistry.Dispose` requests host release but
      public resolution APIs never check `_disposed`; resolution during scope teardown can hand out a
      client whose host is being released. Consider `ObjectDisposedException` guards under `_gate`.
- [ ] **Offline + persisted role→profile assignment surfaces `RoutingUnavailableClient`.** (Opus 4.8
      audit, 2026-07-17, Minor.) In Offline mode the restore gate keeps endpoints Inactive; if the user
      also has a persisted `_runtimeRoleProfiles` assignment, `ResolveClientForRole` returns
      `RoutingUnavailableClient(profile)` ("routing profile 'X' is unavailable") instead of falling
      through to the offline/legacy client. Arguably more correct than the pre-fix behavior (which booted
      the backend behind the user's back), and default demos have no runtime role assignments, so the
      Offline smoke never hits it. Suggested direction: when settings mode is Offline, treat an
      inactive-routed role as falling through to `_legacyFallback`. Not a 5.9.0 blocker.

## [A6] Deep audit wave (2026-07-13) — runtime / architecture / tests / security, all 22 findings fixed

> Multi-agent audit across four dimensions with adversarial verification (22 confirmed, 9 refuted).
> Each fix ships with a regression test; compile-gated via `dotnet build` on Core / Source / Mods /
> Mods.Hub / Tests / Mods.Tests / ExampleGame.Tests (all green).

- [x] **Security:** Hub Mods tab no longer self-escalates imported mods to Full (`allowFullTier` default
      false; Full only via explicit host opt-in). LLM prompt/response content gated behind
      `LogLlmInput`/`LogLlmOutput`; provider error bodies truncated in logs, 401/403 bodies never logged.
      Audit hash de-overstated (docs) + opt-in HMAC-SHA256 keyed chain added.
- [x] **Runtime:** CoreAiEvents reset on play-mode entry + per-handler try/catch + lock; `CoreAi.IsReady`
      under `SyncRoot`; `CoreAIGameEntryPoint` guard reset on SubsystemRegistration; `CoreAILifetimeScope`
      fail-fast on missing settings; nested `mods_call` world-transaction isolation (frame stack);
      allocation-guard forced-GC confirm + no auto-unload of blameless mods; WorldStateManager CTS disposed.
- [x] **Tests:** killed hollow PlayMode tests — Offline-stub now skips (not fake-pass), built-in-role sweep
      asserts, `calledTool`/Tools-Only asserted, marshaler `WaitUntil`→timed `WaitTask`.
- [x] **Architecture:** CoreAI.Benchmarking un-Editor-locked (runs in players); `CoreAIFacade.cs` →
      `CoreAIAgent.cs`; example-game static hub → injected `IArenaKillXpService`; meta-save → JSON;
      `CoreAi` vs `CoreAI` naming convention documented in CONTRIBUTING (facade keeps `CoreAi` by design).
- [x] **Re-audit round (5.8.1):** adversarial re-audit of the 5.8.0 diff found 9 confirmed
      regressions/incomplete-fixes, all fixed: forgeable memory-trip marker → dedicated type; real bombs
      still unload (capped memory-trip streak); forced-GC debounced; import persists host-masked caps (no
      Full on restart); `Invalidate()` no longer wipes persistent event subscribers; error-body redacted at
      source; tautological tool-call test now asserts real execution; audit-truncation docs qualified.
- [x] **Re-audit round (5.8.2):** third adversarial pass found 3 residual gaps in the 5.8.1 fixes, all
      fixed: sticky memory-trip flag → exact-instance match (closes pcall-swallow laundering); JSON 401/403
      error.message redaction completed (was only non-JSON); ImportMod already-loaded-tier comment corrected.
- [x] **Re-audit round (5.8.3):** fourth adversarial pass found 3 (2 mine, 1 pre-existing). Fixed the two:
      allocation-guard debounce watermark capped at budget (no transient-garbage ratchet); HTTP-error
      redaction scoped to 401 only (403 kept, was over-blanked). See the open coroutine item below.
- [x] **[SECURITY, HIGH] Guard mod-created RAW Lua coroutines — VALIDATED under batchmode (5.8.7).** The
      per-resume step/time/alloc hook fires during native resume; `Coroutine_RunawayLoop_IsCutByResumeBudget`
      passes. 5.8.7 also removed a `Create()` sync-over-async domain-reload deadlock (see CHANGELOG 5.8.7),
      gated arming on `LuaState.CanResume`, and left `coroutine.wrap` native (see `TODO(coroutine-wrap)`).
      Original 5.8.4 implementation note retained below for history.
- [~] **[SECURITY, HIGH] Guard mod-created RAW Lua coroutines — IMPLEMENTED (5.8.4), NEEDS EDITOR VALIDATION.**
      `LuaCsSecureEnvironment.HardenCoroutineLibrary` now wraps `coroutine.resume`/`wrap` (option a): every
      resume arms a per-resume step + wall-clock hook on the coroutine's child `LuaState` (mirrors
      `LuaCsCoroutineHandle.Resume`). Compile-gated green; a regression test
      (`LuaCsSecureSandboxEditModeTests.Coroutine_RunawayLoop_IsCutByResumeBudget`) asserts a runaway
      `coroutine.wrap` loop is cut. UPDATE (5.8.5): the per-resume hook now also enforces the ALLOCATION budget (a concat bomb inside a coroutine was still an OOM DoS), skips self-resume (would disarm the outer guard), and preserves the original Lua error value. REMAINING (editor gate): run the coroutine tests to confirm the child `LuaState` is
      surfaced by `LuaValue.Read<LuaState>()` and the hook fires during native resume at runtime; verify the
      `coroutine_countdown` demo still yields correctly; if `Read<LuaState>()` returns null on-device the
      wrappers fail-open (no guard) — fall back to option (b), a `LuaCsCoroutineHandle`-backed shim.
- [x] **Verification gate — DONE via batchmode (5.8.7).** Full EditMode 1,570 passed / 0 failed and PlayMode
      `FastNoLlm` 56/56 (with graphics). The interactive Unity Test Runner freezes on the sync-over-async Lua
      guard fixtures by design, so `Unity.exe -runTests -batchmode` is the reliable runner (helper scripts:
      `unity_ctl.py`, `hunt_spy.py`). New/updated tests all green: CoreAiEvents/EntryPoint/Facade/LifetimeScope
      guard, LLM content-gating + error-redaction, audit HMAC, Lua nested-tx + memory-trip (forge +
      repeat-unload) + import/rehydrate Full-mask, hub Full-tier, WorldStateManager CTS, example-game
      save/killxp, chat-service tool-call assertion.

### Open engineering TODOs (tracked in source as `TODO(...)`)

- [ ] **`TODO(guard-tight-loop-latency)`** — a tight, body-less infinite loop (`while true do end`) is cut
      only after ~8 s: the Lua-CSharp instruction hook fires coarsely for body-less loops, so the sub-second
      step/time budgets aren't enforced promptly (bounded but noticeable freeze; not a bypass — it IS cut).
      Consider a wall-clock watchdog thread or finer hook granularity. Bombs WITH a loop body are cut promptly.
- [x] **`coroutine.wrap` RESOLVED (5.8.8):** was left native/unguarded in 5.8.7 (a host-hang vector). It
      cannot be safely guarded on this Lua-CSharp build, so it is now stripped (`coroutine.wrap = nil`); mods
      use the guarded `create` + `resume` pair. If a future Lua-CSharp version round-trips a C# wrap shim,
      restore a guarded `wrap` that routes through `ResumeWithPerResumeGuard`.
- [ ] **Allocation guard is a per-call first-growth backstop (documented limitation, 5.8.8).** `GC.GetTotalMemory`
      reports the committed-heap high-water mark, so a repeated fixed-size allocation bomb trips only ONCE (later
      calls reuse committed space); Unity's Mono exposes no per-call/per-thread allocation counter to build a
      cross-call cumulative limiter. Sustained bounded allocation is bounded by the per-call step/time budgets,
      not by unloading. A finer control (e.g. a per-mod wall-clock allocation-rate watchdog) is possible future
      work but not currently feasible on Mono.
- [ ] **LLM-API PlayMode gate (spark/opencode):** `AgentMemoryOpenAiApiPlayModeTests` and the LlmVerification
      assembly need a live OpenAI-compatible endpoint; the full PlayMode run aborts on them without one (the
      Offline path skips the deterministic-stub tests but the API-integration tests genuinely hit HTTP). Stand
      up a local OpenAI-compatible server (spark/opencode) and run these + the Lua-mod authoring pipeline +
      model-behavior audit (tool-call/skill quality).

## [R0.5] Demo pass (owner request: "representative and correct")

- [x] Portable endpoint readiness boundary: CoreAI contract/shared policy plus `HttpClient` adapter for .NET;
      CoreAiUnity injects the `UnityWebRequest` adapter into both hot activation and normal LLMUnity autostart.
      The dead registry probe path was removed; native llama.cpp lifecycle stays Unity-only.
- [x] Runtime multi-endpoint LLM routing: dynamic endpoint/profile CRUD, built-in/custom-agent selection,
      hidden-by-default Chat API selector, Hub endpoint editor, two-phase LLMUnity readiness, zero-downtime
      candidate replacement, redacted persistence, and EditMode/PlayMode regression tests.
- [x] Endpoint lifecycle covers zero/one/many APIs, independent Active/KeepWarm state, restart restoration,
      shared first-request readiness, external-API `/models` probing with a guarded
      `/chat/completions` fallback for APIs without that optional route, LLMUnity native +
      `/v1/chat/completions` probing, tri-state session-key updates, injectable secret resolution,
      Automatic chat routing, and removal cleanup for persisted role assignments.
- [x] Parallel local routing documents and enforces separate named LLMAgent hosts with unique ports; unsafe
      same-host mutation is rejected instead of interrupting the currently published generation.
- [x] Native LLMUnity startup diagnostics report llama.cpp model-load and HTTP-readiness durations as
      separate structured phases for both runtime endpoints and legacy autostart, with deterministic
      format/redaction regression tests.
- [x] Native endpoint lifecycle uses prompt-free cancellable readiness, exact inactive-agent resolution,
      pre-activation fingerprints, and reference-counted ownership leases. Deactivate/remove/dispose drain
      tracked calls before unloading only CoreAI-activated llama.cpp hosts; external active hosts stay owned
      by their scene/application.
- [x] Lua/world-command settings extracted from the root CoreAI inspector into an optional child module with
      backwards-compatible serialized migration and updated demos/editor creation paths.

- [x] Added standalone Qwen3.5-0.8B scenes for the Genie and Spellcraft demos under
      `Assets/CoreAI.Demos/QwenDemo`; each uses a dedicated LocalModel settings asset and CoreAI-created
      LLMUnity runtime host, with EditMode composition regression coverage.
- [x] Qwen Genie and Spellcraft now run as `ToolsOnly` and require a native tool call per request;
      compact Game views use non-overlapping responsive HUD panels, wait for native startup plus the
      `/v1/chat/completions` connection probe, and
      reject any result other than exactly one successful expected tool call. EditMode and no-model PlayMode
      regressions cover the contract.
- [x] Qwen Spellcraft single-tool turns use `RequireSpecific(cast_spell)` and an explicit bilingual
      element contract; all four Russian element presets have regression coverage and passed live-model
      smoke on the compact 0.8B model.
- [x] Deterministic startup smoke for all ten published scenes (including Skills and
      LiveMechanicsModsChat): no missing scripts, scope/camera present, supported shaders, no
      unexpected startup errors. Fixed missing Mods scopes, Mirror remnants, Hub wiring, and Wave URP color.
- [ ] Complete manual interaction drivers for every demo (buttons, input, battle loops, F9/F10,
      mod load/unload/restart persistence) with screenshots; startup smoke is not full UX acceptance.
- [~] **Demo review wave (5.8.9, from a multi-agent demo/benchmark audit).** Fixed: Skills demo now guards
      `CoreAIAgent.Policy == null` before `ApplyToPolicy` (no NRE when the LLM module is uninitialized);
      LiveMechanicsModsChat `ActivateSavedMod` now grants the SAME Full-aware capability as the autoload path
      (a panel-activated Full-tier mod no longer silently loses `unity_*`); benchmark G6 `clean_tools` now
      requires `ToolCalls >= 1` so a do-nothing run cannot bank the points vacuously.
- [ ] **`TODO(moddableunits-binding-seam)`** — make the ModdableUnits demo actually functional. The mod
      runtime seam now exists (`LuaCsModStackOptions.AdditionalGameplayBindings`, added 5.8.9, with an EditMode
      test proving an injected API reaches a loaded mod). REMAINING (demo composition, ~a dozen lines + a
      lifetime decision): thread that option through `CoreAiModsInstaller.RegisterCoreAiMods` and
      `CoreAiModsLifetimeScope`, and register `UnitForgeLuaBindings` with a LAZY `IUnitForge` lookup (the scene
      forge is only available at controller `Start`, after the mods scope builds and rehydrates mods). Then
      PlayMode-validate the scene and restore the README claim (currently relabelled aspirational).
- [ ] **Demo hygiene (low):** untrack `Assets/Scenes/AutoSaves/` (11 Hub crash-protection autosave scenes,
      committed before `.gitignore:107`); do not commit the local-LLM wiring in the working-tree
      `MiniRpgModsDemo.unity` (machine-specific `localhost:13333` + a gguf not in the repo); optionally register
      all 10 demo scenes in build settings (currently only Hub + FullAccess; the CoreAI menu auto-inserts on
      open, which is editor-only). Also non-blocking: `CoreAiDemoScope.ResolveModsContainer` throws on a
      mis-wired scope (unreachable in shipped scenes); `ChatPromptButtonsController` input-insert uses
      reflection that can no-op under IL2CPP stripping (degrades gracefully).
- [x] Representative local 4B live checks: memory write and real `world_command` spawn pass on
      `qwen3.5-4b-mtp`.
- [x] **Model-behavior verification (5.8.10, LM Studio `qwen3.5-4b-mtp` OpenAI endpoint).** Ran a
      representative LlmVerification PlayMode subset live: tool-calling, custom agents (all 4 modes), skill
      self-service (read-then-use), skill-tool proxy, skill tool discovery, memory write/append/clear, and the
      `execute_lua` Lua-authoring pipeline (model writes correct sandbox-scoped Lua) — all pass. Verdict: the
      tool-call/skill design is sound (a 4B model handles the whole surface); the tool contract explicitly
      guards narration-instead-of-action. Found + fixed one false-failing test (memory-clear asserted entry
      removal vs the documented empty-document semantics). Path A LLM tests need the asset's `qwopus3.5-9b`
      model loaded; running 4B+9B + Unity PlayMode together OOMs this machine (env resource limit, not a bug).
- [ ] Verify the AI writes mods through the Hub chat in each kept Hub-enabled demo with local 4B/9B/27B
      (LM Studio) and Opus 4.8 via the bundled preset (`Assets/Resources/CoreAIPresets/`,
      bridge: `agent.sh openai-server -e claude -m opus`). *(API models are already proven through the
      same bridge in the benchmark v2 sweep — remaining gap is specifically Hub-chat mod-writing per demo.)*
- [x] Benchmark package (`com.neoxider.coreaibenchmark`): suite ran through the bridge with `-m spark` —
      full v1.7 G1-G8 run on the leaderboard (92.9, row 3); no scenario breakage from the 5.1.0 wave.

## [R0.6] Release-engineering residuals (from the two 2026-07-10 repository audits)

- [~] **F-12 CI gates**: trusted merge-queue gate added (`merge_group` trigger + `merge-queue-gate` job
      that FAILS, not skips, when UNITY_LICENSE is absent) plus a fork-safe `package-graph` job
      (lockstep + internal-dep check). REMAINING: minimal Standalone/WebGL IL2CPP player builds and a
      package-isolation consumer matrix (need the licensed runner to add).
- [ ] **F-18**: pin floating Git dependencies (tags/commits) + explicit upgrade command.
- [ ] **F-19**: slim the dev project (Epic Toon FX ~522 MiB, unused multiplayer packages) or a minimal
      verification project; demo assets to `Samples~`.
- [ ] **F-20**: performance regression suite (orchestrator enqueue, streaming buffers, 10k-object world
      queries, revision stores, audit burst, WebGL persistence cadence).
- [~] **F-22**: package-local test assemblies so standalone UPM graphs are proven, not just monorepo.
      Added `CoreAI.Core.Tests` (references only `CoreAI.Core`) as the pattern + isolation smoke suite;
      `CoreAI.Mods.Tests` is already package-local. REMAINING: same for coreaiunity/hub/benchmark.
- [ ] **F-21**: replace remaining fixed `Task.Delay` waits in async tests with signal-based waits.
- [x] Streaming mutating-call deferral: mutating calls wait for turn completion, whole-turn echoes
      are rejected before side effects, and partial retries execute only failed slots.
- [ ] Cross-request idempotency: add executor-level stable idempotency keys. Current replay state is
      request-local and `ToolExecutionPolicy.Reset()` intentionally clears it.
- [x] Full-tier Lua queries: move recursive `unity_list_objects` / `unity_find_all` / — closed: shared `WorldQuerySceneWalker.cs` (RbxApi/Binding), used by `WorldInstanceAdapter` and `LuaCsWorldQueryBindings` (verified 2026-09-04)
      `unity_find_by_tag` / `unity_find_by_component` implementations onto the shared budgeted walker.
- [~] **WebGL build: `LlamaLib.GetPlatform()` throws on Emscripten/WASM.** LLMUnity's native
      llama.cpp wrapper uses `RuntimeInformation.IsOSPlatform` which does not recognise WebGL/Emscripten
      (`Unix 3.1.39.0`). `CoreAiWebGlLlmUnitySceneGuard` disables scene-placed `LLM` objects, but
      any programmatic `new LlamaLib()` / `LLMService()` path still hits `GetPlatform()` and crashes.
      **Containment implemented, verification pending:** browser composition no longer constructs a
      local provider, staged scenes lose LLMUnity behaviours, and the current staging DLL's eager
      initializer is replaced and verified fail-closed. Runtime polling is only post-`Awake`
      containment; the RUNTIME-first debt still requires an upstream platform gate, assembly split,
      or maintained fork. Fresh EditMode and clean/incremental WebGL builds remain before closure.
- [ ] **Hub: log text should be copyable.** The Hub log viewer (Mod Logs / diagnostics) does not
      allow selecting/copying text. Add text selection support so users can copy error messages and
      diagnostics.
- [ ] **WebGL build: Hub has no Mods tab.** The Mods/Logs sub-tabs are missing from the Hub window
      on WebGL. Likely the `HubModsPages` registration is gated behind `COREAI_HAS_LUA` or
      `COREAI_HAS_HUB` which is not set for the WebGL scripting defines, or the mods composition
      module is not present in the WebGL scene hierarchy. Investigate and restore the Mods tab.
- [x] **Builds: enabled mods do not create or spawn anything.** Mods load and enable successfully in
      the Editor but `Instance.new` / `workspace` / world mutations produce no visible objects in both
      Windows Standalone and Android builds (works fine in Editor Play Mode). Likely the
      `InstanceGameObjectBinder` (Roblox→Unity materialisation layer) is IL2CPP-stripped, not composed
      in the build scene hierarchy, or the `RobloxWorldHost`→binder wiring is Editor-only. Investigate
      the build-time DI composition, `link.xml` coverage for the binder types, and whether
      `IInstanceBackingBinder` registration survives into builds.
      **Fixed:** added `CoreAI.RbxApi.{Instances,Datatypes,Binding,Unity}` + `VContainer` to
      `link.xml`; the root cause was IL2CPP stripping the entire mods DI container
      (`BuilderCallbackDisposable`) and all Roblox API types.
- [x] **WebGL build: VContainer `BuilderCallbackDisposable` constructor stripped by IL2CPP.** The
      `internal` VContainer class `BuilderCallbackDisposable` loses its constructor under WebGL IL2CPP
      managed code stripping, so `CoreAiModsLifetimeScope.Configure()` → `RegisterDisposeCallback()`
      → VContainer build fails → `Container` stays null → `CoreAiDemoScope.ResolveModsContainer` throws
      "missing or not initialized". **Fixed:** added `<assembly fullname="VContainer" preserve="all"/>`
      to `link.xml`.
- [ ] Durability: WebGL sync after `WorldStateManager.Reset`; recoverable two-phase audit rotation;
      surface audit worker failures during runtime rather than only at Dispose/testing flush.
- [x] `allowedLuaScenes` contract pinned as deliberately permissive when empty (any Build Settings scene),
      with an explicit security-policy test; Inspector tooltip now matches the runtime contract.
- [ ] Hub "Audit Log" page: viewer + chain-integrity badge over `AuditLogVerifier` (natural home for
      the new read/verify API).

## Roadmap (prioritized)

### [Roblox ladder] Roblox-like Mod API — foundation (see `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`)

> Tracks the seed foundation list above as work lands. Items are worked by multiple agents
> in parallel on separate files; update this list per item, don't rewrite others' lines.

- [x] **#8 Lua log service — closed.** Standalone `ILuaLogService` in
      `Assets/CoreAIMods/Runtime/Logging/`: `LuaLogEntry`/`LuaLogLevel`/`LuaLogQuery` model,
      `LuaLogService` (per-mod + global ring buffers, thread-safe, optional error mirror to
      `IGameLogger`), `LuaLogFormatter.ToPromptText` (AI-facing compact text with truncation),
      `GetModLogsLlmTool` (`get_mod_logs`, read-only), optional `LuaLogFileSink` (off by default,
      `persistentDataPath/CoreAI/Logs`, `CoreAiWebGlPersistence.Sync()` on write). EditMode tests cover
      ring-buffer eviction/caps, the query filter matrix, sequence monotonicity, `EntryAppended`,
      formatter truncation, and a concurrent append/query smoke test. `CoreAI.Mods`/`CoreAI.Mods.Tests`
      `dotnet build` both green. Wired: `CoreAiModsInstaller` registers `ILuaLogService`
      (`CoreAiModsInstaller.cs:151`) and threads it into `LuaCsModStackOptions` (`:248,828`) so the mod
      runtime's print/warn/error/runtime-error paths append to it, and `GetModLogsLlmTool` is on the
      Programmer tool set (`:625`) with no `// TODO:` left in the file. Verified 2026-09-09 — agrees
      with the closed entry in the foundation list above.
- [x] **#9 Editor Lua/Luau syntax highlighting.** `.luau` `ScriptedImporter` (`.lua` already had one in
      `CoreAiUnity/Editor`) → `TextAsset`; custom `TextAsset` inspector highlights `.lua`/`.luau`/
      `.lua.txt`, falls back to a plain view for other text assets; standalone `CoreAI/Lua Script
      Viewer` window (file picker, drag-drop, font-size slider, copy-path/reveal). Reusable
      editor-independent tokenizer + rich-text formatter in
      `Assets/CoreAIMods/Runtime/LuaAssets` (`CoreAI.LuaAssets`) for a future in-game console. Read-only
      for MVP1; editing stays in external IDEs (`// TODO:` left in `LuaScriptViewerWindow`).

### [R4] Runtime UI (UI Toolkit) — AI & mods build in-game interfaces (owner request 2026-07-12)

> **Flagship of the next minor after the audit-wave release.** The agent (and Lua mods) must be able to
> create, style, animate, and evolve game UI **at runtime**, in one consistent visual theme, and the UI
> must **persist** across sessions exactly like world state. Runtime target: `UIDocument` on the current
> Unity 6000.3, `PanelRenderer` behind a version define once the project moves to 6.5+ (it is the successor
> runtime path). Reference patterns: `CoreAiChatPanel` (already dual UIDocument/embedded-host) and the
> uitk-6-5 playbook (reload-callback lifecycle, state-class animation, no per-frame `Q`).
>
> **Source of truth = native UXML/USS text** (LLMs know these formats from training — better generation
> than any invented JSON schema), persisted in the version store. **Primary rendering path — the runtime
> interpreter**: parse the stored UXML text into the element factory and apply a supported USS subset
> programmatically, identically in a built player and in editor play mode — CoreAI's premise is creating
> the game (UI included) *inside the running game*, never editor tooling. UXML/USS import and AssetBundle
> building are editor-only, which is exactly why the interpreter is the core path: on-device generation
> renders the stored text directly, no import step, no editor. **Secondary (optional, editor-only
> convenience)**: materialize stored text as real `Assets/CoreAI.Generated/UI/` assets so a developer can
> "graduate" AI-built screens into shippable project files; AssetBundles/cloud-build remain an optional
> extension for post-ship UGC delivery. The *theme* always ships as real USS/TSS assets with semantic
> classes and design tokens; generated UI references those classes, which is what keeps every screen in
> one style.

- [ ] **UXML element factory + USS subset interpreter** (`CoreAI.Source`): parse UXML text → element tree
      (`Label/Button/Toggle/Slider/TextField/ProgressBar/Image/ListView/ScrollView/VisualElement` +
      templates), unknown node types degrade to a labeled placeholder (fail-soft, model self-corrects via
      query); USS parser covers the documented subset (selectors: class/name/state pseudo, properties:
      layout/box/text/color/background/border/transition) and reports unsupported rules honestly.
      EditMode: UXML→tree roundtrip, malformed-markup degradation, USS subset application; parity test
      interpreter vs editor-import on the same source pins interpreter correctness.
- [ ] **Editor materialization (secondary, after the interpreter ships)**: store text →
      `Assets/CoreAI.Generated/UI/<screen>/` files + import, for graduating AI-built screens into
      shippable assets; the store stays authoritative, live screens keep rendering via the interpreter.
- [ ] **Theme system**: one shipped `CoreAiRuntimeTheme.uss` (+`.tss`) with design tokens (USS variables:
      colors/spacing/radius/font sizes) and semantic classes (`cai-panel`, `cai-btn-primary`, `cai-h1`,
      `cai-row`, state classes `is-open/is-hidden/is-selected/is-disabled`); token *values* editable at
      runtime (custom-property overrides applied at panel root) and persisted, so "make the UI darker"
      restyles every screen at once. Reusable style fragments = named class bundles in the spec store
      (USS-reuse without runtime USS import). EditMode: token override application, class resolution.
- [ ] **`CoreAiUiRuntime` host** (scene component, DI-registered): owns one `UIDocument` (or
      `PanelRenderer` via `#if UNITY_6000_5_OR_NEWER`) per screen, follows the reload-callback lifecycle
      (bindings re-attached idempotently on tree recreation, `Unwire()` before rebind, zero `Q<>` outside
      the callback), screen router (show/hide/stack), safe teardown with the scope.
- [ ] **Persistence — UI survives restarts**: `FileUiSourceStore` (UXML/USS text + binding manifest per
      screen) following the Lua version-store pattern *post-audit-fixes* (atomic tmp+`Replace` writes,
      original/current/history revisions, revert, `CoreAiWebGlPersistence.Sync()` on WebGL), auto-restore
      all saved screens on scene start like `WorldStateManager`, save-on-mutation; in the editor the
      materialized assets double as the shippable form. EditMode: persistence roundtrip, crash-torn-file
      recovery, revision revert.
- [ ] **LLM tools** (`ui_command` + `ui_query`): create/update/delete screen, add/remove/move element,
      set text/value/classes/tokens, bind element event → mod function, show/hide/animate; `ui_query`
      returns the current spec tree so the model can inspect before editing (mirror of `world_query`).
      Registered through the standard tool policy; results honest (missing element/screen = failure, so
      the model can self-correct — same lesson as `destroy`).
- [ ] **Mods ↔ UI two-way binding** (`CoreAI.Mods`): new `LuaCapabilities.Ui` tier (granted with
      `Gameplay` by default); Lua API `ui_create(spec)`, `ui_set(screen, element, props)`,
      `ui_on(screen, element, event, fn)` (click/change/submit → sandboxed handler through the execution
      guard, budgets enforced), `ui_show/ui_hide/ui_animate`, `ui_tokens(overrides)`; reverse direction:
      UI events raise mod hooks (`on_ui_event`) so a mod can react to any screen including agent-built
      ones; mod unload/reload detaches its bindings (no dead-handler leaks — same class of bug as the
      router static event). EditMode: binding registry attach/detach, capability gating, budget
      enforcement on UI handlers.
- [ ] **Animations**: state-class + USS-transition first (theme ships transitions for
      opacity/translate/scale on `is-open/is-hidden`), `ui_animate` presets (fade/slide/scale/pulse) as
      class toggles, C# `schedule`-based tween fallback for value animation (progress bars, counters);
      never animate layout properties; `display:none` sequencing handled per the playbook. PlayMode:
      state-class transition fires, animate preset completes, hidden screen doesn't consume input.
- [ ] **Hub page**: list runtime screens with spec source, revision history + revert button, theme token
      editor, "open/close" toggles (reuses the version-store UI patterns from the Lua pages).
- [ ] **Built-in "ui-builder" skill** (owner requirement 2026-07-12): ship the UI know-how as a CoreAI
      skill through the existing skill system (`FileSkillStore` + `read_skill`/`call_skill_tool`) — theme
      class reference, UXML patterns for common screens (HUD/menu/dialog/inventory), binding recipes,
      "diagnose & repair" checklist (read `ui_query` → find the broken element/style → minimal fix). The
      skill is what makes small local models capable: it carries the knowledge so the model only routes.
- [ ] **Small-model acceptance gate** (owner requirement 2026-07-12, hard criterion): a **9B model WITH
      the ui-builder skill** — or a **27B model minimum without it** — must both (a) build a working
      interface from a prompt and (b) repair a deliberately broken one (bad USS class, dead binding,
      malformed UXML) to working state. Encoded as benchmark scenario "G9: agent builds a functional HUD
      (health bar + inventory button wired to a mod)" + "G9r: agent repairs a broken HUD", scored on spec
      validity, binding roundtrip, theme-class usage, and repair success; run against the local small-model
      tier in the benchmark matrix, not only frontier models. Tool/skill design must serve this bar:
      few tools, forgiving inputs, honest errors the model can act on.
- [ ] **Verification gate**: EditMode suites above + PlayMode (screen instantiation, event→mod dispatch
      roundtrip, persistence across scene reload, animation classes) + the G9/G9r benchmark scenarios
      passing at the small-model bar above.
- [ ] **Docs**: `Docs/CoreAIUnity/runtime-ui.md` (architecture, spec schema, theme tokens, recipes),
      mods docs section for the `ui_*` Lua API, INSTALL quick-start ("agent, build me a settings menu"),
      README feature bullet. Release: minor bump, all five packages lockstep.

### [R5] Summarization & context-overflow — live verification

> Compaction is unit-tested with stubs only; `LlmCompactionPerRolePlayModeTests` is FastNoLlm (stub). No live
> test proves the summary actually compresses well AND preserves key facts, nor that overflow-retry converges.
- [ ] Live PlayMode test: build a long conversation, force compaction, assert (a) token reduction and
      (b) key facts survive (probe the model that the summary retained specific details).
- [ ] Integration test: context-overflow retry loop actually shrinks the prompt and eventually succeeds
      (the `0.75^n` clamp converges) — currently only the shrink factor is unit-tested.
- [x] Default-config guard: cap the rolled summary by tokens (today `ConversationRolledSummaryMaxTokens=0` = uncapped). — closed: `ICoreAISettings.cs:34` `DefaultConversationRolledSummaryMaxTokens = 2048`, plumbed into `AiOrchestrator` (verified 2026-09-04)

### [R6] Advanced resilience (basic fallback already shipped & tested)

> `FallbackLlmClientDecorator` (primary→1 secondary) is shipped and covered by 10 EditMode tests.
> `CircuitBreakerLlmClientDecorator` is also shipped and covered, but remains an opt-in public decorator.
- [x] **Circuit breaker primitive** — transient-failure threshold, open short-circuit, half-open recovery,
      streaming coverage, deterministic clock, and six EditMode tests.
- [ ] Wire circuit-breaker settings into the production composition root (threshold/cooldown/provider scope)
      before claiming that every default backend uses it automatically.
- [ ] **Multi-provider fallback chain** (ordered list, not just 1 secondary) + secondary wrapped in the same
      retry/logging decorators (today the secondary gets no HTTP-retry wrapper).
- [ ] **Per-provider rate limiting** (token/request bucket) distinct from the Lua-generation limiter.
- [x] Streaming-path retry: `RetryingStreamingLlmClientDecorator` (CoreAI.Core) retries the stream only
      before it commits content (7 EditMode tests); wired into `LlmPipelineInstaller`.
- [x] Enforce request timeout in the portable core: `TimeoutLlmClientDecorator` (CoreAI.Core) bounds both
      paths off `LlmRequestTimeoutSeconds` (5 EditMode tests); additive with the Unity WebGL PlayerLoop timer.
- [x] Tests for streaming retry + core-side timeout (12 EditMode tests). Circuit open/half-open already
      covered; multi-provider chain exhaustion remains with the fallback-chain item above.

### [R7] Structured output (schema-constrained generation) — optional, pending decision

> Today "structured output" is post-hoc string validation (`IRoleStructuredResponsePolicy`), not provider-
> enforced. Optional reliability win, not critical.
- [ ] Pass `response_format` / `json_schema` to OpenAI-compatible providers; GBNF grammar for local models
      where supported; keep post-validation as the fallback. (Decide whether to build.)

### [R7.5] Multi-API — per-agent LLM provider configuration (owner request 2026-07-11)

> The 5.9 runtime endpoint registry now ships the code-first/dev-facing layer on top of per-role routing.
- [x] `AgentBuilder.WithLlmProfile(profileId)` applies an agent-default profile through `AgentConfig` and
      `AgentMemoryPolicy`. Shipped and verified in 5.9.
- [x] `AiTaskRequest.RoutingProfileId` is an explicit per-request input hint; `RoutingLlmClient.Prepare`
      prefers it over agent, role-pattern, and default selection. Shipped and verified in 5.9.
- [x] Runtime endpoint/profile registration ships through `ILlmEndpointRegistry` endpoint CRUD and
      `AddOrUpdateProfile`, without requiring ScriptableObject assets. Shipped and verified in 5.9.
- [ ] Per-profile fallback + limits: `FallbackLlmClientDecorator` and timeout/retry settings currently
      apply only to the legacy-fallback client / global settings — decide per-profile story.
- [ ] Hot-swap consistency: `CoreAiBackend.SetApiKey/SetApiBaseUrl` rebuilds only the legacy fallback;
      profile clients need re-`ApplyManifest`/`ApplyRouteTable` on change.
- [x] Key hygiene ships via write-only session keys plus `SecretReference` and
      `ILlmEndpointSecretProvider`; secrets are excluded from persisted endpoint snapshots. Verified in 5.9.
- [ ] Docs: "per-agent providers" recipe (inspector-only path needs zero code: N × `OpenAiHttpLlmSettings`
      + manifest on `CoreAILifetimeScope`).

### [R8] Vision — finish (feature already shipped)

- [ ] PlayMode round-trip `[Explicit]` test against a real vision-capable model (capture → model → assert).
      (Host send path, gate, and tool-result lift already shipped in 4.12.0; FastNoLlm camera test exists.)

### [R9 — lowest priority] Multi-agent / sub-agent orchestration

> Design in `TODO/MultiAgent_Orchestration_v2.0.md`. The decisive parity gap vs Claude Code Task tool /
> Cursor background agents / Cline subtasks — but explicitly LAST per maintainer.
- [ ] `SubAgentDefinition` (roleId, description, prompt, tools w/o Task tool, model, maxTokens, maxTurns).
- [ ] `IAgentRegistry` + `AgentOrchestrator.ExecuteSubAgentAsync` (clean context isolation) + bounded `ExecuteSubAgentsParallelAsync`.
- [ ] `AgentLlmTool` (parent-only) returning results as tool_result; DI wiring; per-role exposure; settings.
- [ ] EditMode tests + docs + CHANGELOG.

## Audit cleanup & cheap test gaps (from 2026-06-28 audit, non-blocking)

- [ ] Audit log retention: rotated 50 MB files are never deleted (WebGL: IndexedDB quota exhaustion) and the
      writer keeps appending to a corrupt-tail file — add retention policy + corrupt-tail quarantine
      (2026-07-12 audit, deferred from the crash-anchor/ChainReset verifier fixes).
- [ ] Lost-update races, all latent on the current single-threaded host model (2026-07-12 audit):
      `IAgentMemoryStore.Revert` vs `MutateAsync` without a per-role lock; `FileSkillStore.Save/Delete`
      bypass the path-keyed `MutationLocks`; `FileDataOverlayVersionStore` lacks the cross-instance
      lock + reload-on-change the Lua store has; rolling-summary read-modify-write is non-atomic.
- [ ] Test flake: `QueuedAiOrchestratorEditModeTests.Dispose_CompletesPendingTask_InsteadOfHangingForever`
      intermittently gets `TaskCanceledException` instead of `ObjectDisposedException` (dispose race in
      `QueuedAiOrchestrator` vs its pending task; observed 2026-07-12 in CLI NUnit, pre-existing).
- [ ] Small confirmed-but-cheap items (2026-07-12 audit): case-insensitive role/skill file collisions
      ("Guard"/"guard"); `manage_skills update` with empty `tool_names` silently wipes the allowlist;
      `WorldStateManager` Save/Reset in the same frame snapshots pending-destroy ghosts;
      ~~transport-internal timeouts map to `Cancelled`~~ *(fixed 2026-07-12 wave 3: non-caller
      cancellation now surfaces as typed `Timeout`, retry- and fallback-eligible)*;
      benchmark zombie scenario task past the 5 s grace leaks orphan primitives between reps;
      `WorldStateAutoSaveHook` interval=0 ("off") silently resurrects to 60 s.
- [ ] `SmartToolCallingChatClient` mutable per-request statics pattern (2026-07-12 wave-3 review, latent):
      `LastExecutedToolCalls` / `LastRoundtripUsage` are unsynchronized instance properties reset per
      request — two concurrent `CompleteAsync` calls through one shared client instance interleave.
      Harmless on the current one-turn-at-a-time host; fails if the DI graph ever shares one client
      across concurrent roles. Replace with per-call context (return alongside the response) when
      concurrency arrives.
- [ ] Audit `ChainReset` accounting limits (2026-07-12 wave-3 review, accepted design): a forged
      self-hashed `ChainReset` after tail truncation still verifies `Ok=true` (chain is unkeyed by
      design) — `ChainResetCount`/warn only make it operator-visible. A keyed HMAC chain would be the
      real fix if tamper-evidence ever becomes a requirement.
- [ ] Summarization-off hard truncation semantics (2026-07-12 wave-3 review, note): with summarization
      disabled the overflow clamp partitions at raw budget with no trigger ratio and can split mid
      tool-call/answer exchange — no wire-contract violation, but consider a `ShouldPartition`-style
      hysteresis for coherence.
- [ ] Pending-parent ownership in additive scenes (2026-07-12 wave-4 review #2, residual after the
      claim-on-success fix): ownership is still last-successful-loader-wins — a detach of scene A's
      child after scene B loaded routes `ForgetPendingParent` to B's map, and disposing B leaves the
      owner null while A is alive (A reclaims only on its next `TryLoad`). Real fix = per-manager
      routing (executor resolves its scene's manager via DI instead of the static owner). Multi-manager
      additive topology is already broken more basically (Save/DestroyAll use global FindObjectsByType).
- [ ] Persisted summary exceeds the token cap by the fold-marker line (~113 chars, 2026-07-12 wave-4
      review #2): self-healing internally (managers strip+re-limit on load), but an external reader of
      `FileConversationSummaryStore` sees over-cap text with the marker. Consider reserving marker
      headroom inside the limiter.
- [ ] `AgentMemoryPolicy.ConfigureChatHistory` lacks the null/whitespace roleId guard the other entry
      points have (pre-existing; null throws from Dictionary.TryGetValue).
- [x] **5.7.0 editor verification gate (next Unity editor session)**: run the FULL EditMode suite — closed: covered by the full 7.x runs (`editmode8.xml`, `g9.xml`, `g11.xml`) (verified 2026-09-04)
      (1658 discovered; the CLI NUnit workaround can't execute ECall-dependent fixtures —
      `Application.persistentDataPath`, `EditorPrefs`, `Debug.Log`, UI Toolkit) + PlayMode FastNoLlm
      (incl. the new WorldStateManager pending-parent tests) + one live G1 scenario on the configured
      `Qwen3.5-4B-Q4_K_M.gguf` to smoke the wave-3..5 LLM-path changes. Blocked on 2026-07-12: the
      Unity MCP plugin rejects test-runner calls (`user cancelled MCP tool call`) — approve them in the
      plugin window or run from Test Runner manually.
- [ ] `DelegateLlmTool` boundary, IL2CPP verification (2026-07-12 wave-5 review): the sync-fault
      classification relies on exception stack frames + `AsyncStateMachineAttribute` reflection —
      under IL2CPP release builds frames can inline away and stripping can remove the attribute, so a
      SYNCHRONOUS conversion-shaped body throw could escape as never-invoked (async faults are safe by
      construction). Verify in a built player per the RUNTIME-first rule.
- [ ] `TryLoad` skipped-load edge (2026-07-12 wave-5 review, pre-existing): scene-mismatch/no-file
      returns clear the pending-parent map but keep `_unresolvedObjects` — a later Save() re-appends
      the unresolved parents while the children's links were wiped. Align the two lifetimes.
- [ ] Benchmark: a user stop with zero results now passes green (could mask a stopped CI run) — the
      partial report is written and a warning logged, but consider `Assert.Inconclusive` for CI.
- [ ] Timeout decorator streaming rewrite mutates the inner client's chunk instance in place — safe
      for in-repo clients (fresh instances per chunk), latent for third-party `ILlmClient`s that cache
      chunks (2026-07-12 wave-5 review).
- [ ] Router cross-generation clear theft (2026-07-12 wave-4 review, latent, pre-existing): a stale
      pre-`ResetStatics` router disposing AFTER a new-generation router incremented the refcount can
      take the count 1→0 and null `CommandReceived` mid-session. A generation token alongside the
      counter would close it; only reachable with domain reload off + leaked routers.
- [ ] Benchmark window/menu G8 divergence (2026-07-12 wave-4 review, cosmetic): until any run writes
      `PrefG8`, the window's visible G8 toggle and what the one-click menu computes can diverge if
      other group prefs changed in between (migration is re-evaluated per launch).
- [ ] `LastRoundtripPromptTokens` producer coverage (2026-07-12 wave-4 review, latent): the field is
      only set by the MEAI client when `response.Usage != null`; a future non-MEAI usage-reporting
      client would silently calibrate on cumulative PromptTokens again. Add the field to any new
      client's terminal path.
- [ ] Multi-scope audit-writer design (2026-07-12 adversarial review of wave 2): two coexisting scopes
      (now officially supported by the additive-scene router fix) each own an `AuditLogWriter` on the SAME
      `audit.jsonl` with independent `_seq`/`_prevHash` — interleaved appends break the hash chain. Needs a
      design decision: per-scope audit files, or a process-wide shared writer singleton.
- [ ] Extractor vs lax local models (2026-07-12 review, deliberate-tradeoff watch item): the hardened
      `LlmToolCallTextExtractor` skips backtick/quote-cited spans (so quoted examples never execute) — but
      Qwen-class local models sometimes wrap REAL tool JSON in backticks; those calls now render as text and
      the tool loop stalls. Monitor G-benchmarks on LLMUnity models; if stalls appear, add a
      trailing-lone-cited-block exception rather than reverting the citation guard.
- [ ] Verify `File.Replace` on Unity WebGL (Emscripten VFS) at runtime — version stores + audit rotation now
      depend on it; if unsupported there, every non-first save fails (caught + logged but not persisted).
      Add a WebGL smoke check or a `File.Move`-based fallback on that platform.
- [ ] Mods threading hardening (2026-07-12 adversarial review, both latent — no active bug on the current
      main-thread model): (a) the shared `ILuaTransactionScope` between the persistent Tick runtime and the
      one-off `LuaCsGameToolExecutor` means a cross-thread `ResetTransactions()` in one surface's `finally`
      can wipe the other's open transaction — give each surface its own scope or document/assert the
      single-thread contract; (b) `LuaCsExecutionGuard`'s per-`LuaState` hook `Stack` relies on the
      "guard entry per state is never concurrent" invariant — add a debug assert (thread id check) so a
      future background runner fails loudly instead of silently corrupting the hook stack.

- [ ] Remove now-dead `MeaiLlmClient.GetExclusiveEndForSafeUnboundRawStreaming` (superseded by `GetHybridSafeSegments`; only its own test references it). *(The O(n²) per-delta hybrid rescan is now bounded by a 64 KB held-tail cap — 2026-07-01.)*
- [ ] Separate inter-token idle timeout (distinct from total request timeout) in SSE streaming.
- [ ] Surface provider-native `reasoning_content` SSE deltas as a collapsible "thinking" channel. *(Now handled consistently as internal — not surfaced as visible text in either path — 2026-07-01.)*
- [x] ~~Pin "raw tool-call JSON never leaks into visible Text"~~ — streaming now fails closed on incomplete/unparseable text-shaped tool JSON (2026-07-01); a dedicated hard leak test would still be nice.
- [x] Harden `ConversationHistoryPruner.ExtractToolNames` against nested `Full`-policy detail blocks.
- [x] ~~Fix `ToolExecutionPolicy.IsToolResultSuccess` lossy "contains 'success'" heuristic~~ — done 2026-07-01 (JSON `error`/`ok:false`/`succeeded:false` + failure prefixes, classified before truncation).
- [x] ~~`world_command` `apply_force`/`set_velocity` accept an all-zero vector~~ — fixed 2026-07-01 (require at least one vector component; explicit per-axis `0` still honored).
- [ ] Tests: per-tool timeout firing; Lua memory/table-growth bomb + blocking-native-binding; EditMode coverage gate in CI.
      *Max-roundtrips cap termination is covered; `SseToolCallAccumulator` many-small-deltas coverage was added 2026-07-01.*
- [ ] Chat: queue outgoing user messages while a turn is in progress (buffer sends, flush in order when the
      active turn completes) instead of only disabling the send button / dropping input.
- [ ] Move the `unity_find` / `unity_set_position` mutation assertion into the PlayMode suite.
- [x] ~~`MeaiLlmClient.CompleteAsync` drops `ExecutedToolCalls` on an empty final response~~ — fixed
      2026-07-01, found via a live G6 benchmark report contradiction (`0 tool-calls` / `1 spawns`); the same
      root cause explained every "tool ran but stats say 0" symptom (benchmark `ToolCalls`/`FailedToolCalls`
      undercounts, `ToolErrorRate` misreporting, "used tool" checkpoints failing despite executor state
      proving a tool ran). *(The Codex audit doc with the full trace was removed per the "no audits in
      the project" rule; the trace survives in git history.)*
- [ ] Benchmark harness: `RecordingWorldExecutor.InvalidCommandCount` is tracked separately from `ToolCalls`
      (invalid/malformed world commands are invisible in the "Tool calls" column). Defensible as a distinct
      metric, but worth an explicit decision — either document the split or fold invalid attempts into
      `ToolCalls` too. Low severity (labeling nuance, not a scoring bug).
- [ ] Make the benchmark's manually-built orchestrator turn-trace visible in the Agent Session Inspector
      (today it only resolves a trace reader from a scene DI scope).
- [x] ~~G4 playthrough scenarios (Combat/Crafting/Shop) score PARTIAL on weak models mainly from failed Lua
      calls right after a successful `logic_define`~~ — fixed 2026-07-01 (Codex audit): added a `VerificationNote`
      to each G4 goal clarifying `logic_define` does not create a directly-callable global; the harness invokes
      registered slots with hidden samples.
- [x] ~~G1 world-building scenarios (Coin collector, Constraint budget) can PASS while spawning every object
      at the same `(0,0,0)` position~~ — fixed 2026-07-01 (Codex audit): added `DistinctSpawnPositionCells` +
      `spatial_spread` checkpoints and a prompt requirement for distinct x/z positions, across all three G1 scenarios.
- [x] ~~G6 free-build: generic-subject prompt says "AT LEAST 24 objects" but `substantial_scene` grading
      accepts 18 for custom free-builds~~ — fixed 2026-07-01 (Codex audit): generic-build grading now also
      requires 24 objects / 20 distinct names, matching the prompt.
- [x] ~~G6 bounds grading (`CountBoundsViolations`) checks only the spawn pivot, not the scaled extent~~ —
      fixed 2026-07-01 (Codex audit): added `HalfExtents()` (per-primitive-shape, including the real 2m
      cylinder/capsule height) and bounds now check the full scaled extent.
- [x] ~~G6 `IsTowerLike()` treats any cylinder/capsule near a corner as a tower regardless of scale/name~~ —
      fixed 2026-07-01 (Codex audit): now also requires height >= 2.5m and footprint >= 1m.
- [x] ~~G5 `exact_count`-style constraints count `env.World.Commands.Count`, not actual tool-call attempts~~ —
      fixed 2026-07-01 (Codex audit): `g5_exactly_three` now uses `max(recorded commands, actual world_command
      tool-call attempts)`.
- [x] ~~G6 full-prompt override (`COREAI_BENCHMARK_FREEBUILD_PROMPT`) was still graded against the built-in
      castle/generic checkpoints, unfairly failing a custom task~~ — fixed 2026-07-01: added
      `FailureAttribution.NotGraded` + `GameBenchmarkScenario.ExcludeFromScoring`; a full-prompt override now
      still runs/screenshots but is excluded from `SuiteBaseScore`/pass-rate/dimension breakdown (a
      subject-only override still uses the known `GenericGoal` scaffold and stays gradeable). Verified live
      against qwen3.5-4b-mtp: a 3-cube custom prompt now shows "No graded groups" instead of a punishing FAIL.
- [ ] `RoleFitness` "Orchestrator / Director" can rate a small model 9+/10 off G1-G8 alone, since almost
      every scenario resolves in a single LLM turn (`RunObservation.Turns` = 1 nearly everywhere) — high
      Reasoning/Intent scores reflect "parsed the instruction correctly in one shot", not sustained
      multi-turn orchestration with error recovery, which is what the role's own description asks for. G4's
      "playthrough" doesn't cover this either — the harness simulates the multi-step trajectory in C# after
      the model installs Lua slots, not the model itself across real turns. Added an honest caveat to the
      role's `Note` text (2026-07-01, Codex audit) without touching the formula/weights — changing those
      would affect every historical comparison and needs a user decision, not a quiet fix. A real fix likely
      needs a genuinely multi-turn scenario (adversarial tool failures forcing retries, or a task that can't
      complete in one turn by construction) feeding into the Director gate/weights specifically.
      G8 adds described-state conditional selection, but it is still single-turn and does not close this gap.

## Shipped (recent)

- 5.6.x — build-time policy registration; simpler agent API (`AgentBuilder.Build()` auto-applies to the
  global policy); solution-wide code-style pass (WHY/TODO/HACK comment rules); benchmark castle
  comparison scene + Stop-with-partial-report; native-API free models in README.
- 5.5.0 — [R6] resilience wave: `TimeoutLlmClientDecorator` + `RetryingStreamingLlmClientDecorator`
  wired into the pipeline, `CircuitBreakerLlmClientDecorator` primitive; benchmark v2 tooling; CI
  merge-queue gate + package-graph job (F-12 partial).
- 5.4.0 — MoonSharp removed; Lua-CSharp is the only VM.
- 5.3.0 — benchmark v2; resilience primitives (`FallbackLlmClientDecorator` covered by tests).
- 5.1.0-5.2.0 — audit remediation: safe mutation pipeline, bounded queues/stores; stability gate and
  extension APIs; streaming mutating-call deferral; `allowedLuaScenes` contract pinned.
- 5.0.x — on-demand skills for built-in roles ("Lua Modding" skill); benchmark package extracted to
  `com.neoxider.coreaibenchmark`; version lockstep across five packages.
- 4.17.0 — tool-call history unlimited by default (`MaxToolCallHistoryMessages = 0`); per-agent / per-call
  `MaxToolCallRoundtrips` override (`0` = unlimited, Programmer/Creator default unlimited), default cap raised
  10 → 20; clearer cap-reached stop message; honest provider-call tok/s labeling; `BenchmarkInfo.GroupDifficulty10`
  single source of difficulty. Full-tier Lua `unity_add_component` / `unity_destroy` + Unity-object-reference coercion;
  `world_command` spawn accepts rotation + scale inline with schema docs; demos reorganized into `Scripts/` subfolders.
- 4.16.0 — `AllowWorldPrimitives` setting; `component_command` curated reflection-free component catalog (+ `coreai_component_*`
  Lua bindings); `unity_list_members` discovery + rich Color/Vector/Quaternion coercion + did-you-mean errors; G6 free-build
  subject overridable; decode tok/s fix; configurable benchmark roundtrip cap.
- 4.15.x — Game-Creation Benchmark reporting polish: G6 castle free-build hero, per-model model-card radar/role bars,
  role-shaped scene screenshots with ghost markers, decode-vs-effective tok/s, cross-model comparison + Models leaderboard tab,
  LM Studio multi-model sweep, mean-over-repetitions aggregation, `Repeatable` opt-out, model-name-on-screenshot, audit
  material/mesh-leak fixes.
- 4.14.0 — portable Game-Creation Benchmark scoring core + live PlayMode suite (G1–G5 scenario groups, 0..100 across six
  dimensions, subtractive instruction-following, `RoleFitness` per game-dev role, gated efficiency bonus, self-explanatory
  scene screenshots, per-model comparison card, Editor **CoreAI > Benchmarks** window).
- 4.13.0 — **[R1] parallel tool-call execution** (`ToolExecutionPolicy.ExecuteBatchAsync` runs a batch concurrently,
  bounded by `MaxParallelToolCalls`, default 4; order preserved, state-mutating built-ins serialized, timeout/duplicate/
  forced-tool/consecutive-error/cancellation semantics intact). **[R3] real BPE token counting** (`ITokenCounter` +
  `BpeTokenCounter` for cl100k/o200k via `BpeEncodingResolver` / `IBpeRanksProvider`, falls back to the calibrating
  estimator). **[R4] agent-authored skills** (`manage_skills` create/update/list/get/delete + file-backed `FileSkillStore`,
  versioned, surfaced into `read_skill`; `AgentBuilder.WithSkillAuthoring`). **[R2] configurable live PlayMode provider**
  (`PlayModeOpenAiTestConfig`: env vars + gitignored `coreai-live-tests.local.json`, see `Docs/RUNNING_LIVE_TESTS.md`).
  Also Hermes/Qwen-Agent XML tool-call parsing.
- 4.12.1 — memory instruction now reaches native tool-calling roles (`AiToolContractPromptFormatter` early-return bug).
- 4.12.0 — live streaming through tool calls, partial-SSE accumulation, WebGL Lua AOT hardening, stale-`<think>` prune, Lua mod versioning + diagnostics, vision host send path + gate + lift, P3 nits.

## Active release gates — 2026-09-08

- [ ] Root + memory-boundary owner: remove blocking memory/history I/O from async turns end to end, including scoped capabilities and append/rejected-turn paths; verify cold reads and contention with a responsive host. A flush-held-lock deadlock has not been demonstrated: the current file store releases gates before confirmation.
- [ ] Root + skills owners: integrate async persistence/readiness, preserve both meta tools, prove first-waiter cancellation and replacement recovery, complete independent audit and real Unity/WebGL checks.
- [ ] Root + typed-chat owner: preserve admitted turn identity and explicit failure/tool metadata through queue/service/panel; update Redo only after verified release.
- [ ] Root: diagnose the stalled summary Unity run, settle the Mods signal-quota fixture with real event evidence, then rerun the complete release suites against frozen sources.