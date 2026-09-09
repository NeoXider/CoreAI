# Closure audit — MVP1, MVP2, MVP2.5 (2026-09-06)

Three independent hostile reviews, one per rung, run read-only against `main` at `54fa272f` and
checked test-by-test against the last full EditMode run (`artifacts/testresults/verify.xml`,
3670 total / 3661 passed / 0 failed / 9 skipped).

**Verdict: MVP1 is closed. MVP2 is not. MVP8 — and therefore MVP2.5 — is not.** The rest of this
document is the evidence and what was done about it.

Post-fix verification: full EditMode **3694 total / 3685 passed / 0 failed / 9 skipped**
(`artifacts/testresults/verify4.xml`) — 24 more tests than the run this audit was written against,
every one of them added to close a finding below.

---

## 0. A correction the audit produced first

`verify.xml`'s `start-time` is `2026-09-05 20:58:25Z` — **UTC**. Local time in this repository is
`+0500`, so that run started at `01:58` on 2026-09-06, three minutes before `54fa272f` was committed
at `02:01:30`. The run therefore covers HEAD, not a week-old tree: it contains
`ImguiBanRatchetEditModeTests` (modified inside `54fa272f` itself) and every MVP11/MVP12 fixture.
An earlier note in this session claiming the XML was stale was wrong.

The one real evidence gap is PlayMode: `verify.xml` is EditMode-only, and
`Mvp8PhysicsPlayModeTests` has a single recorded green run (`physplay.xml`) that no later run
repeats.

---

## 1. MVP1 — closed, with recorded caveats

13 of the 15 criteria in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §5.1.8 are proven by tests that are
present and green in `verify.xml`, and the proofs are discriminating: the round-trip tests are
parameterised over both 0.28 and 1:1 scales, chirality is pinned to hard literals
(`D1_AnglesChirality_PositiveYawTurnsLeft` expects `(-1,0,0)`), and the id-partition gate asserts
both the positive and the wire-rejection halves.

| # | Finding | Status |
|---|---|---|
| 1 | §5.1.6's loud-stub inventory was stale: `WorldRoot:Raycast`, `Workspace.Gravity` and `Workspace:GetServerTimeNow` were listed as planned stubs although all three ship, and "six RunService query/render-step methods" is now two. Criterion 13 ("every stub in §5.1.6 raises `NOT_IMPLEMENTED`") was literally unsatisfiable. | **fixed** — §5.1.6 rewritten to the shipped truth |
| 2 | D6 step 3 (connections disconnect on `Destroy`) had no test, and neither did the ORDER of the destroy sequence: every existing assertion is on the terminal state and survives a reordering. | **fixed** — `R6_2_DestroyOrderEditModeTests` |
| 3 | `RbxInstance.Clone()` (the C# API) does not copy BasePart spatial state; only the Lua binding's `CopyPartSinkState` does. A future C# caller gets a silently incomplete copy. The TODO in the code proposes moving the copy into the registry, which is **impossible**: `IPartPropertySink` lives in `CoreAI.RbxApi.Binding`, which references `CoreAI.RbxApi.Instances`, not the other way round. | **fixed** — see §4 |
| 4 | Criterion 9 asks for "golden fixtures against documented Roblox values", but `Color3.fromRGB` rounding is a recorded CoreAI decision because the mirror documents none. | **open (wording)** |
| 5 | The conversion lint is a text heuristic with two escapes: it misses `float s = RbxSpace.MetersPerStud; x * s;`, and it scans only `Assets/CoreAIMods/Runtime`. No violation exists today. | **open** |
| 6 | The once-per-mod deprecation test loads one mod, so it cannot tell once-per-mod from once-per-process. | **open** |
| 7 | `PROGRESS.qa-mvp1.md` looked like an abandoned checklist in the repo root. It is not: `PROGRESS.*.md` is gitignored agent scratch, never tracked. | **not a finding** |
| 8 | Stale roadmap cross-references around MVP1 (CornerWedge fallback, `RobloxApi/` paths). | **open (docs)** |

---

## 2. MVP2 — not closed

11 of the 16 criteria in §5.2.9 are proven, 4 are weak, 1 has no test at all. Two ordering claims
would survive the behaviour being reversed. The repository already said as much and it was not
acted on: `ROBLOX_API_ROADMAP.md:15` says MVP2 "**largely** landed",
`dev-docs/MVP2_ACCEPTANCE_MANIFEST.md` §5.2 records **"Gate verdict: FAILED"** for G10, and
`TODO.md` still carries that as an unchecked box. MVP2's own definition of done is "§5.2.9 green
**plus** the manifest gates".

| # | Finding | Status |
|---|---|---|
| 1 | **Criterion 14 has no real test.** "A `while true do end` in a Heartbeat handler is killed within its slice while other mods keep running" is proven by a test that *injects* a pre-made `BUDGET_EXCEEDED` error instead of running a loop. The repo's own comment says the guard cuts a tight loop only after ~8 s, and slice enforcement is deferred to MVP17. Nothing asserts the string `BUDGET_EXCEEDED`, the mod/line attribution, or budgets at `timeScale = 0`. | **open** — the criterion promises something the engine does not do; it needs either the enforcement or an amended, measured bound |
| 2 | **Criterion 12 has no test and its central claim is structurally false.** `HttpService` uses `LuaCsRbxJson` (a private field, referenced nowhere else); remotes use `LuaCsRbxNetworkCodec`. Two independent encoders, so "asserted to be the same component" cannot hold. No round-trip contract test exists anywhere. | **fixed (decided)** — the two-encoder reality is kept, not merged: `RbxJsonContractEditModeTests` pins the boundary and the three real divergences (envelope shape, mixed/sparse-key handling, number formatting) fixture by fixture; §5.2.9 item 12 rewritten to record the decision |
| 3 | Criterion 9's ordering half ("Parent nil + locked BEFORE disconnect") is asserted nowhere. | **fixed** — `R6_2_DestroyOrderEditModeTests` (shared with MVP1 finding 2) |
| 4 | **The modern RunService events did not exist.** `PreAnimation`, `PreSimulation`, `PostSimulation` and `PreRender` — the current Roblox names, with `PreSimulation`/`PreRender` documented as the replacements for `Stepped`/`RenderStepped` — were internal scheduler phases only. A copy-pasted current Roblox script failed with "not a valid member of RunService". §5.2.3's dedicated-server note ("`PreRender` never fires where nothing renders") was also unimplemented. | **fixed** — four signals shipped in mirror order, render phase gated on a new `IRbxRuntimeTopology.RendersFrames`, `RbxRunServiceModernEventsEditModeTests` |
| 5 | Criterion 6's scaled-time half is untested (no test anywhere sets a non-unity `timeScale`), and `os.clock()` is monotonic wall time, not CPU time as the roadmap claimed. | **partly fixed** — the roadmap now records the deviation and its source; the scaled-time tests are in progress |
| 6 | Criterion 15 ("U1–U7 stances have conformance tests") has no observable pass condition; no test cites a stance. | **open** |
| 7 | Criteria 11 and 16 had gone stale (TweenService shipped in 7.19.0; the corpus is 18/20, not 17/20). The tests migrated correctly; the document did not. | **fixed** — §5.2.9 item 11 rewritten |
| 8 | Criterion 3's "errors like Lua" is not pinned to any message text. | **in progress** |
| 9 | Criterion 13's "next drain (never same-stack)" has no pre-advance assertion, so it would pass against a synchronous dispatch. | **in progress** |

Also open and not a test problem: `BindToRenderStep`/`UnbindFromRenderStep` are listed in §5.2.4 as
MVP2 deliverables and are still loud MVP2-phased stubs. An MVP2-phased stub cannot be open while
MVP2 is declared closed.

---

## 3. MVP8 (and therefore MVP2.5) — not closed

Five of the six gates in `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` §E.1 have at least one unproven
negative twin, and two gates have **positive**-column requirements the code does not implement.
`TODO.md` said "MVP8 — complete (7 slices)". It was not.

| # | Finding | Status |
|---|---|---|
| 1 | **The Humanoid moves nothing in production.** `AttachCharacterMotorFactory` has exactly one caller in the repository — a test harness. `UnityRbxCharacterMotor` is constructed only inside a PlayMode test and its `Step()` has no production pump. Every Humanoid in a real scene gets `NullRbxCharacterMotor`, whose `Position` is always zero — so `MoveTo(Vector3.zero)` reports success instantly. Gate P8.2's positive column is not achieved. | **fixed** — see §4; a follow-up review found the motor stepping on the wrong pump (render, not fixed), also now fixed (`LuaModRuntimeTickDriver.FixedUpdate`) |
| 2 | **`LoadCharacterAsync` and the character pipeline are stubs, and a gate test pins them as stubs.** §A.1 lists `LoadCharacterAsync`, `LoadCharacter`, `CharacterAdded`, `CharacterRemoving` and `DistanceFromCharacter` as shipping; the catalog registers all five as `Planned/MVP8`, and `Negative_UnshippedMembers_StayLoudStubs` asserts they raise `NOT_IMPLEMENTED`. The gate and the suite assert opposite things. | **fixed** — see §4; a follow-up review found join-time spawn ordering, `RespawnTime` and agent skill-text gaps, all now also fixed |
| 3 | **Humanoid state is silently lost by the one serializer.** `InstanceSnapshot` carries Model, ClickDetector, MaterialVariant and Value payloads and nothing for Humanoid. Save a world with an NPC at 30/100 and it restores at 100/100 with no error. Gate P8.6 requires exactly this round-trip. | **fixed** — `HumanoidSnapshot` through capture, restore, validation and the world DTO, with atomic rejection of malformed payloads |
| 4 | **The `CFrame`-teleport contact suppression was dead code.** `NoteTeleport` is called from Lua, which runs in `Update()`; `BeginPhysicsStep()` cleared the note in `FixedUpdate()` of the following frame — i.e. before the simulation step the note was meant for. `Touched` fired on every scripted teleport. The EditMode test passed only because it drove the calls in an order production never produces. `Orientation`/`Rotation` writes did not note a teleport at all. | **fixed** — pending→active promotion, tests rewritten to the real order, rotation writes note too |
| 5 | **Six §A.1 members were neither shipped nor stubbed.** `Players.RespawnTime`, `Players.CharacterAutoLoads`, `Players.MaxPlayers` and the whole `SetNetworkOwner` family read as "not a valid member" — silent, which is the failure mode the loud-stub rule exists to prevent. The absence also pre-broke MVP12's R12.5 negative twin. | **fixed** — the three `Players` properties ship (with `MaxPlayers` read-only, refusing writes); the five ownership methods are registered as loud backlog stubs |
| 6 | **"The harness asserts zero stub hits" did not exist.** The corpus harness scraped logger text for `NOT_IMPLEMENTED`, and nothing in the runtime logs on raise — so a fixture that `pcall`s a stub passed clean. No Tier-B fixture does that today, so the recorded results are honest; the guard was not. | **fixed** — the errors now announce themselves through `RbxStubRaiseObserver` and the harness counts raises, with a hostile fixture proving a pcall-wrapped stub is classified as failing. The first attempt used `AppDomain.FirstChanceException`, which is the framework's answer to exactly this and which **Unity's Mono never raises** — the counter reported zero for a raise that definitely happened. Caught by running it, not by reading it. |
| 7 | Two P8.4 negative twins had no test: "a destroyed tweened instance never reports completion" and "`timeScale = 0` freezes Debris". | **fixed** |
| 8 | **The agent-facing skill still told the LLM that MVP8 does not exist.** `BuiltInRbxApiSkillText` listed TweenService and CollectionService among the unimplemented registrations, and had no section for Humanoid, `workspace:Raycast`, `workspace.Gravity`, `Touched`/`TouchEnded` or the Value objects. Agents writing mods are this API's primary consumer, so this is an operational defect. | **fixed** — plus a ratchet test that reads shipped-vs-stubbed from `ServiceCatalog` at test time, so the next rung cannot silently re-stale it |
| 9 | Gate P8.5 cites "the exact ids listed in the MVP8 manifest". There is no MVP8 manifest; the ids are frozen in test source only. | **open** |
| 10 | Lower-severity, found in passing: `_openContacts` leaked a pair when a part was destroyed mid-contact; `UnityRbxPhysicsPort.TryRaycast` used a fixed 32-slot buffer so a part behind more than 32 colliders was silently missed; `Humanoid.Jump` always read `false`. | **fixed** |

### Also true of MVP2.5, and named before this audit

- The **join snapshot** (MVP11) is not delivered: an admitted client receives no filtered
  `ExportSnapshot`.
- The **intent gateway is not wired to the wire** (MVP12): every rule is built and gated, the
  plumbing is not.
- There has been **no two-process over-the-wire run** (N11.3–N11.6). Mirror's host mode did not
  deliver client→server inside the batch-mode test runner, so the bridge's rules are gated against
  its receive paths directly and no claim is made that bytes cross a real socket.

---

## 4. What is deliberately left open, and why

- **MVP8 finding 1 (motor wiring) and finding 2 (character pipeline) are implemented, but this
  landing is still not closed — a second and now a third review round on the very same code keep
  finding real defects in it.** `RbxCharacterFactory` builds the mirror's minimum character (Model
  + HumanoidRootPart + Humanoid) and parents it into the world on join; `LoadCharacterAsync`
  genuinely yields, `CharacterAdded`/`CharacterRemoving` fire in the right order, and
  `UnityRbxCharacterMotor` is stepped from production composition instead of only from a test
  harness. A joining player now puts a Model into Workspace — the two findings were one slice, as
  this section originally said, and shipped together.
  **Correction (round 3, §6):** this paragraph used to also claim `DistanceFromCharacter` reads
  the live root position. It does not: it reads whatever position a SCRIPT last wrote into the
  part sink (`RbxPlayers.PartPositionReader`), which nothing keeps in sync with the body's actual
  transform, so the number is stale the moment a character walks or falls under physics rather
  than a script-driven move. Fixed later the same day through a live-transform read on the sink;
  see §6.
  **A follow-up review round on this same landing found four further defects; all four are now
  also fixed:** the join-time spawn used to run inline mid-dispatch, giving a script no real chance
  to flip `CharacterAutoLoads` first — `Players.EnsureActor` now defers it to a scheduled host
  callback that re-reads `CharacterAutoLoads` and re-checks `Player.Character` once the triggering
  dispatch has drained; `RespawnTime` had no consumer at all — the Lua-CSharp bindings now wire
  every character's `Humanoid.Died` to a scheduled reload that re-checks `CharacterAutoLoads` at
  fire time; the motor stepped on the render pump instead of the fixed pump — it now steps from
  `LuaModRuntimeTickDriver.FixedUpdate`; and the skill-text gap that left agents unaware of the
  character API is closed by a new `Players & Characters` section in `BuiltInRbxApiSkillText`. See
  `Assets/CoreAI/CHANGELOG.md` [Unreleased] for the full detail on each.
  **A third and final review round (two independent reviewers) found and fixed four more defects
  reachable through this same landing, and leaves four open on it — see §6 for the complete
  list; do not treat "implemented" above as "closed".**
- **MVP1 finding 3 (`Clone()` completeness) is implemented.** `RbxInstance.Clone()` now copies
  BasePart spatial/appearance state through a new seam, `IInstanceBackingBinder.CopyBackingState`,
  declared on the interface `Instances` already owns and implemented in `Binding`
  (`InstanceGameObjectBinder`) — exactly the shape this section originally proposed, avoiding the
  assembly-direction problem the finding's own TODO could not solve.
- **MVP2 finding 1 (budget kill)** is a real engine limitation, not a missing test. Writing a test
  that passes at the ~8 s the guard actually takes would make the criterion true by lowering it;
  the honest move is to measure it, publish the number, and either enforce the slice or amend the
  criterion. That is a decision, not a fix.

---

## 5. Method note

Each rung was audited by a separate reviewer with no knowledge of the others' findings, instructed
to treat "the useful output is the list of gaps" as the goal. Every cited test was matched by
`fullname` against `verify.xml` rather than assumed green from source. Where a claim was about
ORDER or TIMING, the reviewer was asked the specific question "would this test still pass with the
order reversed?" — which is how findings MVP1-2, MVP2-3 and MVP8-4 were found.

---

## 6. Third and final review round (branch `fix`, two independent reviewers)

Same method as §5 — two reviewers, no knowledge of each other's findings — run a third time
against the character-pipeline landing from §4 and the surrounding Core/Llm code it touches. Eight
findings were fixed in this round; seven remain open and are named below rather than folded
silently into a "done" checkbox. PlayMode has not been re-run against this tree. The last full
EditMode run over the whole tree reported 4303 tests with 5 failures, all of which this round's
fixes are addressing — no results XML for that run is checked into the tree, so that count is
recorded as reported, not independently re-derived here.

### Fixed

1. **The whole EditMode run hung with no results file.** In
   `ClientLimitedLlmClientDecoratorEditModeTests`, `ConcurrentRequests_CannotSlipPastTheCapTogether`
   is a synchronous `[Test]` that ends on `Task.Result`; under Unity's `SynchronizationContext` the
   decorator's own continuation is posted back to the very thread `Result` is blocking, so it never
   resumes — the same deadlock shape as the earlier F12 finding, reached through a blocking
   property instead of a blocking assertion
   (`Assert.ThrowsAsync`/`CatchAsync`). Fixed with a fixture-local `[SetUp]`/`[TearDown]`
   `SynchronizationContext` detach in that file. The guard test,
   `SynchronousAsyncAssertionDeadlockGuardEditModeTests`, previously only scanned for
   `ThrowsAsync`/`CatchAsync`; it now also flags a body that assigns a `Task` local and then reads
   its `.Result` or calls unbounded `.Wait()`, while deliberately exempting a bounded
   `Wait(timeout)`, which cannot hang the run.
2. **After loading a world at runtime, `workspace:Raycast` always missed and `Touched`/
   `TouchEnded` never fired again.** `RbxWorldHost.PublishReplacement` (run from
   `IRbxWorldSessionCandidate.Commit`) disposes the outgoing physics port and builds a fresh one
   over the newly published binder, but the incoming Rbx API had already been wired to the port
   that existed at *Stage* time — the one `Commit` was about to dispose. A disposed port doesn't
   throw; it just answers every raycast with a miss and never fires a contact signal again, with
   nothing in the log. Fixed by a new `IRbxWorldSessionHost.PhysicsPort` property and
   `RbxWorldRuntimeSessionController.LoadConfirmedAsync` attaching it to the staged
   `WorldPhysics` right *after* `candidate.Commit()` returns, once the host's `PhysicsPort`
   reflects the port `PublishReplacement` just built. New test:
   `RbxWorldPhysicsPortRepublishEditModeTests`.
3. **A joining player spawned an unanchored 4x1x2 collidable box at the world origin.** The
   character's `HumanoidRootPart` was materialized from the part sink's plain `Part` default —
   4x1x2 studs, collidable, at `(0,0,0)` — because nothing pushed a real size or position into the
   sink before the model entered the world. Fixed with `RbxCharacterFactory.RootPartSize`
   (2×2×1 studs, the mirror's `HumanoidRootPart` size) and `DefaultSpawnPosition` (5 studs above
   the origin), pushed through a new `RbxPlayer.RootPartSpawnSeeder` delegate before
   `character.Parent = worldRoot` runs (parenting is what materializes the part from the sink, so
   seeding has to happen before it, not after). The seeder is wired in production —
   `LuaCsRbxApiBindings` assigns `_players.RootPartSpawnSeeder = SeedCharacterRootPart` at
   construction, pushing through the same `IPartPropertySink` the bindings already hold — not only
   from a test harness.
4. **Neither the deferred join spawn nor the death-triggered respawn had an error boundary.** Both
   run from the scheduler's host-callback slot, outside any mod's dispatch `try`/`catch` — a slot
   neither `Advance` nor the tick driver catches — so an instance cap or a refused parent there used
   to propagate out and end the scheduler frame for every mod in the world. `RbxPlayers.EnsureActor`'s
   deferred callback and `LuaCsRbxApiBindings.WireRespawnOnDeath`'s scheduled reload now each wrap
   their `RbxCharacterFactory.Load` call in its own `try`/`catch`, reporting a failure through
   `registry.Diagnostics` (`LogFailedAutoLoad`/`LogSkippedAutoLoad`/`LogFailedRespawn`) and leaving
   only that one player without a character.
5. **`TimeoutLlmClientDecorator` relied on a WebGL-forbidden primitive and could leave a caller
   hanging.** `TaskCompletionSource(RunContinuationsAsynchronously)` defers its continuation to the
   thread pool, which WebGL does not have — the conversion lint refuses it in WebGL-reachable code
   for exactly this reason. `CompleteAsync` is also no longer `async` all the way through: the
   compiler puts an async method's own task into the `Canceled` state (not `Faulted`) when its body
   throws `LlmOperationTimeoutException`, so a caller that re-observes an already-completed
   `Canceled` task (rather than awaiting it live) was not guaranteed the exact exception type back
   on every runtime. It now runs detached (`RunCompleteWithDeadlineAsync`) against an explicit
   `TaskCompletionSource` whose `TrySetException`/`TrySetCanceled`/`TrySetResult` fire only after
   `finally` disposes `deadline`/`signal`/`timeoutCts` — each dispose wrapped in its own
   `try`/`catch` so one throwing cannot skip the other two or skip completing the task and hanging
   the caller forever. A new `IdleDeadline.Elapsed` flag, set before `Cancel()` runs, replaces
   asking a possibly-already-disposed `CancellationTokenSource` for its own state. A bounded ~20 ms
   real-time grace window (`GraceWindowMilliseconds`, via `ILlmAsyncMarshaler.DelayAsync`) now gives
   a cooperative inner operation a genuine chance to finish its own cancellation unwind before its
   result is discarded as a bare timeout. Five new tests in `TimeoutLlmClientDecoratorEditModeTests`
   cover the rewrite's behaviours, including disposal-throws and delay-registration-throws-
   synchronously paths that used to have no coverage at all.
6. **A tool argument that cannot bind used to reach the tool body before anyone noticed.**
   `ToolExecutionPolicy.TryBindArgumentsStructurally` now round-trips each declared parameter's raw
   value through the same `JsonSerializerOptions` MEAI itself binds with, reflecting the target CLR
   type off `AIFunction.UnderlyingMethod` — entirely before `InvokeAsync` runs, and only for the raw
   `AIFunction` path (a resolved delegated invocation already validated its own arguments). A
   rejection here carries the same schema-retry hint (`BuildSchemaRetryHint`) the downstream
   MEAI-side rejection used to carry, and is traced `"arg-conversion"`. Because a genuine binding
   failure can no longer reach the downstream catch block, every exception that still lands there is
   now unconditionally traced `"native"` — it has already crossed into (or past) the tool body, so
   retries stay blocked after a possible side effect regardless of the exception's type or message.
7. **`FileConversationSummaryStore` logged every write failure except the one where the directory
   itself could not be created.** `EnsureDir()` used to run before the `try` in `WriteSummaryCore`;
   it now runs inside it, so a directory-creation failure is caught and logged like every other
   storage error instead of reaching the caller silently.
8. **`QueuedAiOrchestrator`'s reconnect test was pinning the cross-tenant memory merge round 1
   removed as a data leak.** `ProductionAdmission_ReconnectResumesActorMemoryAcrossSessions` used
   to reconnect the same actor id under two different tenant+user pairs and still expect one shared
   history — which only passed because the store used to pin the first scope it saw for an actor,
   merging two tenants into one file. Renamed to `...AcrossConnections` and rewritten: two
   connections now share one `AgentMemoryScope` and differ only in connection id (what a named
   actor's durable key — `(actor, tenant, user, session, topic, role)` — actually says should
   survive a reconnect), and a third connection under a different tenant asserts its history stays
   empty. `AgentMemoryActorScopeKeyEditModeTests` gained
   `NamedActor_OneLongLivedDecorator_TenantChangeStillForksMemory` and
   `..._TopicChangeStillForksMemory`, pinning that production's single, process-wide
   `ScopedAgentMemoryStoreDecorator` Singleton doesn't merge two tenants or topics that happen to
   share an actor id — a shape only a long-lived decorator instance could expose, which every
   earlier test built fresh per call and so could not see.

### Closed after that list was written

The five items below were open when this section was first drafted and were fixed in the same
session, verified by a full EditMode run of 4313 tests with one failure (the MCP one recorded
below).

- **One actor could destroy another actor's character.** `RbxPlayer` now keeps
  `LoadedCharacter`, the lifecycle's own reference to the character it actually built, and
  `RbxCharacterFactory.Unload` tears that down instead of whatever `Character` currently points
  at. A script may still redirect `Character` for reading — that is the mirror's contract, and it
  is deliberate — but it can no longer aim destruction. Two tests hijack a foreign character and
  then disconnect and self-kick, asserting the victim keeps its character.
- **`DistanceFromCharacter` reads the live transform.** `IPartPropertySink.GetLivePositionStuds`
  reads the materialized body's own transform, falling back to the stored value only for a part
  with no backing object yet.
- **A dead character's motor stops.** The fixed-step pump skips a dead humanoid and clears the
  motor's own walk target, which `Humanoid` death never touched.
- **Height-based jumping uses the world's gravity.** `CoreAiModsInstaller.CreateCharacterMotor`
  now passes a delegate that reads the host's CURRENT physics port every time, so a world loaded
  at runtime and a script writing `Workspace.Gravity` both reach the jump solver.
- **A nested pair of `TimeoutLlmClientDecorator`s keeps the inner timeout's type.** The classifier
  recognises an `LlmOperationTimeoutException` arriving from an inner decorator and faults with it
  rather than folding it into a generic cancel. Two tests pin it, including the companion case
  where a genuine caller cancellation must NOT be reclassified as a timeout.

### Still open — recorded honestly, not claimed done

- **`Assets/CoreAIMcp` — an open notification stream blocks ordinary requests.**
  `NotificationCapacity_IsBounded_AndPostContinuesWorking` has never passed: it arrived with the
  branch commit and is absent from every earlier results file. With one long-lived notification
  stream open at capacity, the POST that follows times out. Reproduced on an idle machine with
  that fixture running alone, so it is not load flakiness.
- **`ProjectSettings/ProjectSettings.asset` and `CoreAI.slnx` carry Mirror-injected local
  artefacts that must not be committed.** With the optional, gitignored `Assets/Mirror` present
  locally, Unity added `Mirror*`/`Edgegap`/`kcp2k`/`Telepathy`/`SimpleWebTransport` project entries
  to `CoreAI.slnx` and five `MIRROR*`/`EDGEGAP_PLUGIN_SERVERS` scripting define symbols to the
  WebGL player settings in `ProjectSettings.asset`. Both are working-tree-local side effects of the
  optional package being present, not intentional changes; neither should be committed.
- **PlayMode has not been re-run against this tree.** The last full EditMode run over the whole
  tree was 4303 tests with 5 failures, all of which are being addressed by the fixes above; no
  PlayMode run has been taken since.
