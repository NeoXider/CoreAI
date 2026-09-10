# Changelog

## [7.41.0] - 2026-09-10

### Fixed

- **A turn that broke while building its context threw the learner's message away.** 7.40.0 introduced
  `UserTurnHistoryLatch.SummaryPreflightPending`: raised as soon as conversation context building began,
  lowered only once the rolling summary had been durably written, and while raised it made
  `EnsureUserTurnRecorded` return without writing anything. Everything that can go wrong in that window
  landed on the learner: the caller cancelling, a summary file that will not read, an LLM-assisted
  compaction that is itself a network call and takes seconds. The question stayed on screen in the chat
  while the model's history never learned it had been asked, and the next turn answered a conversation
  with a hole in it. The gate is removed; the write-once user turn is unconditional again on every
  terminal path — which is what the surrounding design already assumed, in `IUnstartedAiTurnRecorder`
  and in the authority-denial branch of `RunTaskResultAsync` that exists purely so a refused turn cannot
  bypass the same boundary.
- **The gate could not have protected what it was defending, either.** Its stated purpose was to stop a
  bounded store from evicting old messages that a still-unconfirmed summary retells. Skipping one append
  does not save those messages: the next turn appends and evicts them just the same, so the guard bought
  one turn of delay for the oldest message by destroying the newest one outright. Eviction safety is
  owned where the eviction happens — the fold is committed before dispatch, so by the time any append
  runs, whatever it can evict is already retold. In the shipped stores the two thresholds are not even
  close: `FileAgentMemoryStore` and `InMemoryAgentMemoryStore` trim at 500 messages while folding is
  driven by the context budget and starts around thirty, so the gate would have had to sit through
  hundreds of consecutive failing turns — dropping a message each time — before it protected anything.
- **The price of the fix, stated plainly: a resubmitted turn now records the learner's message twice.**
  The latch is per orchestrator invocation, not per message — nothing in the store identifies a message
  as the one already written — so a host that retries a request that died during context building
  appends the same intent again. Under the removed gate that retry produced exactly one copy, because
  the first attempt had written nothing. This is the trade being made and not a side effect that was
  overlooked: a duplicated question is a conversation the model can still read, a missing one is not.
  The retry scenarios in `AiOrchestratorRefactorEditModeTests`, its streaming mirror and its queued
  mirror assert the full append sequence, so the count cannot drift back to a swallowed turn or on to a
  third copy without a test saying so.

### Changed

- **A failed turn leaves the summary it prepared, and the test now says so.**
  `RunTaskAsync_ContextOverflowRetriesFail_DoesNotPersistAttemptSummaries` was written against the
  pre-7.40.0 ordering, where the summary was committed only after the owning request had succeeded, and
  had been red since that ordering changed. It is renamed `…_StillCommitsTheSummaryTheAppendReliesOn`
  and asserts what the new ordering requires: the fold covering the oldest source is committed, because
  the teardown append relies on it, and the turn records the learner's message once. This is a retelling
  of messages the store still holds, not new content, so the cost of a failed turn is a rolled summary
  rather than a lost message.
- **The summary-preflight tests assert the boundary they actually guard.** `AssertOldSourceRetained` split
  into `AssertPreparationInFlight` (nothing dispatched, published, or appended while context is being
  prepared) and `AssertUndispatchedTurnKeptUserIntent` (a turn that never reached the provider still holds
  the learner's message exactly once, and the bounded window shows the append landing last). The ordinary,
  streaming, and queued mirrors of that scenario were updated together, and `MEMORY_STORE_CUSTOM_BACKENDS.md`
  no longer promises that a failed preflight suppresses the user append — it now spells out the three
  consequences a custom backend has to plan for instead: write-once is per invocation, a teardown append
  that throws is only warned about, and the chat cap has to be sized against the fold window rather than
  against a single turn.

## [7.40.0] - 2026-09-10

### Fixed

- **Every RunService signal fired twice per frame, because the merge left two frame authorities in
  the tree.** The publication wave had made `LuaCsRbxApiBindings.PumpSchedulerPhase` route all six
  scheduler phases instead of only input processing — a real production fix, since the shipped host
  (`LuaModRuntimeTickDriver` → `RbxWorldRuntimeSessionController.PumpFrame`) advances the scheduler and
  does nothing else, so any phase not routed there never fires its signals in a built player at all.
  7.39.0 branched before that change and kept the older host shape, in which the caller fires the
  signals itself through the public `Pump*` methods and then advances the scheduler to drain them.
  Merging the two took the routing and inherited the older callers, so each frame ran twice: `Stepped`,
  `Heartbeat` and `RenderStepped` fired once from the scheduler phase and again from the pump beside it.
  A mod's per-frame counter doubled, a Heartbeat handler cut for a runaway loop was cut a second time in
  the same frame (`ModHandlerErrored` and `ThreadFaulted` each raised twice for one offence), and the
  observable phase order came out `SSDHHIRR` where the contract is `SDHIR`.
- **The scheduler is now the single frame authority, and the duplicate route is gone rather than
  filtered.** `ModScheduler.Advance` walks the phase pipeline and the bindings that own both that
  scheduler and the RunService instance fire each phase exactly once from `PhaseReached`.
  `LuaModRuntimeTickDriver` no longer subscribes to `PhaseReached` and no longer accepts the three
  per-phase pump delegates it used to re-invoke; one frame is one `Advance`, and the reasoning is
  recorded in the driver, in `CoreAiModsInstaller` and on `LuaCsRbxApiBindings.PumpFrame` so the second
  route cannot quietly return. The affected EditMode fixtures emulate the host the same way — one
  `Advance` per frame — instead of pumping and advancing; their expectations were already correct and
  are untouched.
- **A cancelled `LuaCsExecutionGuard.ExecuteAsync` was pinned to an exception type Unity never
  delivers.** `AsyncExecute_HonoursCancellation` demanded Lua-CSharp's `LuaCanceledException`. The VM
  does raise exactly that — measured against `Lua.dll` directly, for a token cancelled before the run
  and for one cancelled mid-loop, with and without the guard hook installed — but it cannot survive the
  trip out: `ExecuteAsync` is an `async Task`, a cancellation escaping one completes the Task as
  *canceled* rather than faulted, and Unity's runtime drops the original exception at that transition
  (desktop .NET 8 keeps it, which is why the expectation reads as correct). Both types are
  `OperationCanceledException`, and that is the contract CoreAI can actually keep: it is what a host
  needs to tell "the run was cancelled" from "the mod's script failed", and nothing in the codebase
  switched on the subtype. The guarantee is now written on `ExecuteAsync` and the test asserts it; the
  test's real subject — the state stays usable, because the guard hook is restored either way — is
  unchanged.
- **A mistyped tool argument cost the learner the whole turn.** `arg-conversion` is emitted again — by
  the structural preflight `ToolExecutionPolicy.TryBindArgumentsStructurally`, which runs MEAI's own
  coercion standalone *before* `function.InvokeAsync` — but `LoggingLlmClientDecorator.TraceIndicatesInvocation`
  was never told, so the source fell through to the fail-safe `default` and counted as "a tool ran".
  The consequence was the exact inverse of the danger that default exists for: a turn in which the model
  merely typed `"many"` where an `int` was declared, and in which **nothing executed at all**, was
  reported as having mutated the world — so a 429 or 5xx arriving in that same turn could neither be
  retried by the decorator's own loop nor failed over by `FallbackLlmClientDecorator`, and the student
  got a hard error instead of an answer. The branch is restored, and the reasoning now sits in the code:
  this source is *proof* of non-invocation, not an inference from a stack shape.
- **The branch had been deleted as dead, correctly, and then the source came back without it.** 7.39.0
  removed `arg-conversion` from the classifier because nothing emitted it any more — argument failures
  had stopped being classified from stack frames (IL2CPP/WebGL strips them) and were all traced `native`.
  That removal even predicted the failure mode, warning that the string's "future reuse" would be
  silently misclassified; the reuse arrived with the structural preflight in the same wave, and the
  prediction came true in the opposite direction. `TraceIndicatesInvocation_ClassifiesSourcesCorrectly`
  now pins the case explicitly, with the history written next to it, so it cannot read as dead a third
  time.
- **A third source had the same defect and had simply never been looked at.** `tools-disabled` is
  recorded when the request arrives with `ToolMode = None` and the policy refuses the call before
  reaching the function - the same shape as `arg-conversion`, nothing executed - yet it sat on the
  fail-safe `default` and suppressed retry and failover exactly as the other one did. Fixed. The
  neighbouring `blocked` is now documented as a deliberate `true` rather than a fourth oversight: it
  marks a turn in which an EARLIER invocation stopped observing its deadline, so that body may still
  be running and replaying the turn could execute it twice. A lost turn is cheaper than a mutation
  applied twice.
- **The door this defect keeps arriving through is now guarded.** Three times the classifier's
  `default` branch - right for an unknown source, silent for a known one - has swallowed a source
  nobody added to the list, and each time the only symptom was a lesson turn lost to an error that
  could have been retried. `EveryEmittedTraceSource_IsClassifiedOnPurpose` reads the sources
  `ToolExecutionPolicy` actually emits and fails if any of them is not pinned by name in the
  classifier test. An omission is now loud where it happens, not where a learner feels it.

### Changed

- **Two tests that pinned the interim "the distinction is gone entirely" state were corrected, not
  deleted.** `DelegateLlmTool_ArgumentCoercionFailure_IsTracedAsInvoked` (in both
  `ToolContractPromisesEditModeTests` and `RetryFallbackToolTraceSuppressionEditModeTests`) asserted
  `native`/invoked for a coercion failure — right for the window in which the argument-vs-body
  distinction had been deleted outright, wrong once it returned as a structural proof. Both are renamed
  to `…_IsRejectedBeforeInvocation` and carry a WHY recording why the previous expectation was correct
  when written and is not now; this supersedes the 7.39.0 note "An argument-coercion failure is traced
  as invoked, not as never-invoked", whose reasoning still holds only for failures thrown *across* the
  invocation boundary. The delegate case is kept rather than folded into the raw-function one because it
  is the only guard that the preflight still sees `UnderlyingMethod` through
  `DelegateExceptionBoundaryAIFunction`'s `DelegatingAIFunction` wrapper — were that forwarding ever to
  stop, the preflight would silently degrade to the conservative verdict with no other test noticing.

### Added

- **A custom character controller can report a measured speed and refuse a jump.**
  `IRbxCharacterMotor` gained `double? MeasuredSpeed` and `bool TryJump(...)`, both with a default
  implementation, so a third-party motor written before this version still compiles and behaves the way
  it did. Why: `Humanoid.Running` multiplied the UNIT `MoveDirection` by `WalkSpeed`, so it could only
  report zero or full speed, and it fired once on entering the state. The mirror says something else:
  the signal fires when running speed CHANGES, and reports zero on stopping. It now does, and
  `UnityRbxCharacterMotor` reports the speed the solver allowed, so a character pressed against a wall
  reads as standing even though its requested speed never changed.
  **Signal order is now part of the contract:** leaving the running state reports the stop BEFORE the
  state changes — that is, before `FreeFalling` or `Jumping` — while entering a run ALWAYS reports the
  speed, even when the value did not change. Without the first rule, a handler that picks the idle pose
  from a zero beat the falling handler in the same frame and the character stood in mid-air. Without
  the second, landing at rest was never reported at all and the script never left the falling pose. The
  bracket sits in the state transition itself, so it covers all three exits from running — flight, jump
  and death. A motor whose body has been destroyed returns `null` rather than zero: a body that is gone
  is not a character that stopped.

### Fixed

- **Foreign code throwing during world shutdown no longer leaves the world half torn down.**
  `IRbxCharacterMotor.Release` is game code, and it was called unguarded in three places. `Dispose` was
  the worst: the "already destroyed" flag was set first, so one exception aborted the rest of the
  teardown forever — the scheduler kept calling the destroyed object, connections and threads stayed
  alive, and a second attempt returned immediately. Motor ownership is now released BEFORE the foreign
  call, each call is isolated, and a failure goes to the registry diagnostics.
- **`ScriptContext:SetTimeout` no longer turns a large value into a small one.** The check caught
  infinity but not range, and casting an out-of-range number to an integer is undefined — on x64 it
  yields the minimum integer, which read back as the short default. The ceiling is now named explicitly
  (2147483.647 s, about 24.8 days), and anything above it is rejected with a stated reason instead of
  being silently replaced.
- **A character's death reports zero speed.** The handler refuses to measure a dead character, so with
  auto-respawn off the animation script kept its last non-zero value forever. Zero is sent once, before
  the death signal, so no late "return to idle" instruction can arrive after the death handler.
- **Jitter at the stopping threshold no longer sends an event every frame.** Instead of a single
  threshold there is hysteresis: a walker stops below 0.1 studs/s, and a standing character has to reach
  0.2 to count as walking again. The band is exactly one resolution step wide, so a change smaller than
  the smallest one ever reported cannot flip the state back and forth.
- **Replication core: three defects.** Diagnostics on the recovery paths went around the wrapper that
  absorbs a throwing receiver — a failing logger aborted the resync request after a partially applied
  batch, and the replica stayed out of sync. Initial player creation carried the display name and the
  character reference past the allowed-field list, so the filter honestly protected edits but leaked on
  the first snapshot. An empty display name was replaced by the login even though the serializer
  explicitly permits it and stores it verbatim; the profile value is now chosen at the moment the player
  is admitted, and both recovery and the replica receive what was stored verbatim.

### Changed

- **Game-creation benchmark: suite version 1.7 → 1.8.** A review of group G6 found several wounds the
  benchmark had inflicted on itself. The prompt promised the model a countdown that this build did not
  have, so the model built blind until the cut-off. The tool carried a limit of 20 calls per minute
  while the prompt requires calling it up to a thousand times, and every refusal counted as a failed
  call and cost points. The advice "set a colour, keep the shades natural" was actively harmful: the
  shader builds colour as texture × part colour, the multiplier is never above one, so any colour set
  only darkens — the example in the prompt itself produced three quarters of stone's brightness. The
  prompt is rewritten around the measured formula, section size is lowered to 10-20 parts, and the cost
  of a single failure is stated honestly. The countdown had to be fixed twice: the first attempt
  corrected the result only when it was a string, while the library returns parsed JSON, so the note
  never appeared at all — proven by a separate program run against the very build of the library that
  sits in this project. In every group a batch spawn is now recorded as N separate spawns with
  production names; a batch used to be one opaque command — invisible to spawn scoring, and a violation
  in the group where only spawning is allowed. The G6 numbers on the leaderboard were produced on the
  OLD tool (the table was last updated 2026-07-11, G6 moved to the Roblox API 2026-09-03) and do not
  carry over to the current suite.

### Notes

- The assumption that large sections do not fit the coroutine budget (10 000 steps) was TESTED AND
  DISPROVED by measurement: a single `execute_lua` runs under its own limit of 50 000 000 steps and
  10 s, and one part costs about 50 instructions. The real ceiling is Lua's limit of 200 local
  variables.

## [7.39.0] - 2026-09-10

### Added

- **The Lua execution budget is the game's to set, and to change while the game runs.** The
  per-resume instruction and wall-clock caps were CoreAI constants: a game could not raise them for
  a heavy simulation or lower them for untrusted mods. `LuaCsCoroutineBudgetSettings` is now a
  serialized field on `CoreAiModsLifetimeScope`, resolved by every coroutine site, and every resume
  re-reads it instead of freezing it at construction — so a change reaches a pooled signal runner
  that was created long before, on its very next resume. The mirror's own
  `ScriptContext:SetTimeout(seconds)` moves the wall-clock half live, gated to the host actor the
  way Roblox gates it to plugins. Non-positive values fall back to CoreAI's defaults rather than
  reading as "no limit", because a per-instruction hook has no sane interpretation of a zero budget.
- **Replication has a core, and it is engine-free.** Nothing in production ever fed the dirty set:
  its only caller was a gateway production never constructs, so the join snapshot and the delta
  publish were both waiting on the same missing wiring. The registry is now the source itself —
  every setter reports the member it changed, a `RevisionAdvanced` event is raised once the mutation
  gate is released, a per-recipient `ReplicationStream` turns the dirty set into an ordered
  Spawn/Patch/Remove plan, and a `ReplicationApplier` applies it to a second registry marked as a
  replica. `GuardedReplicationFilter` is a floor a game's own filter cannot open: the server
  containers, `Camera` and `PlayerScripts` stay invisible to everyone, another player's `Backpack`
  and `PlayerGui` stay invisible to anyone but their owner, and a filter that throws hides rather
  than leaks. Every rule was taken from the mirror's own class tags, and a test walks what the
  bootstrap actually creates and fails if any of it replicates against those tags, so the next
  service added to the tree cannot leak in silence. This is the layer under MVP11/MVP12, not those features themselves — a late joiner with no
  join snapshot still asks for a resync rather than guessing, and that boundary is pinned by a test.

### Fixed

- **A mod could escape a tightened budget through a child coroutine.** The guard armed around a raw
  `coroutine.resume` used its own fixed constants and never read the live settings, so moving a
  runaway loop into a child coroutine outlived a budget the host had just cut — the parent's hook
  cannot intervene until the nested resume returns. The raw-coroutine bound is now derived from the
  live settings rather than declared independently of them, so it keeps its deliberately larger
  headroom and still scales down with the host's number.
- **Two more Lua surfaces ignored the configured budget entirely.** The one-off `execute_lua`
  executor and the AI envelope processor each build their own engine over the shared sandbox, and
  each defaulted its budget — a private object nothing could reach. A host that tightened the budget
  constrained every loaded mod while admin- and AI-issued chunks kept running on the untouched
  default. Both now receive the same live object the runtime reads.
- **A player could steal another player's respawn.** `Character` is writable from Lua with no
  ownership check, which is correct — a script writing to a property it owns needs no authorization.
  But the death handler resolved the owner of a dying character through that field, matching the
  first player whose `Character` pointed at it. An actor that joined earlier could point its own
  `Character` at someone else's and collect their respawn, while the player who actually died was
  never rebuilt. Lifecycle decisions now resolve ownership through the reference the character
  factory itself set; the Lua-visible lookup keeps Roblox's semantics unchanged.
- **A runaway mod was reported as a bad-argument error, not a budget kill.** This one predates the
  branch and was never a regression: the coroutine guard threw with a constructor that carries an
  inner exception, and Lua-CSharp's protected resume reads the error OBJECT, which that constructor
  never sets. The value reaching the scheduler was nil, so the classifier fell through to
  `BAD_ARGUMENT` and the fix hint told the author to fix a Lua error that did not exist. No committed
  test asserted the text on the coroutine path, which is why it survived this long. Budget trips are
  now classified by a typed trip kind rather than by matching a substring in a message, and the
  message itself carries the bound and the author's line again.

### Notes

- The dedicated-thread wrapper around the MCP notification loop is gone. It was added to fix a POST
  that timed out behind an open stream; that cause was later measured to be a client-side connection
  budget instead, and the loop it wrapped awaits inside itself, so it never held a pooled worker
  between iterations. It cost a real thread for the few microseconds before the first await.

## [7.38.0] - 2026-09-09

### Added

- **A game can now drive Rbx characters with its own character controller.** `Humanoid` movement
  was always executed behind `IRbxCharacterMotor`, but the only way to supply a different one was to
  reach into the bindings after composition had already run. A host now registers an
  `IRbxCharacterMotorProvider`: every Humanoid that gets a body asks it first, and returning null
  declines that character to CoreAI's own motor. The common case needs no composition code at all —
  derive from `RbxCharacterMotorProviderBehaviour`, drop it in the scene, and hand it to the
  lifetime scope's new inspector field, the same explicit serialized reference the world host
  already uses. `Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md` is the guide, including the rule that
  matters most: the Humanoid is the only thing allowed to say where a character goes, so a bridged
  controller must stop reading input or the body is driven twice.

### Fixed

- **The fixed-step pump only advanced CoreAI's own motor.** It tested the concrete type, so a host
  motor's `MoveTo` never progressed: the character stood still until the Humanoid's eight-second
  arrival timeout. `IRbxCharacterMotor` gained `Step(deltaSeconds)` with a no-op default and the
  pump now calls it on every motor.
- **A stale motor was only ever rebuilt for CoreAI's own type.** Writing `Anchored` on a root part
  destroys its body and clearing it builds a new one; the rebuild check recognised only the bundled
  motor, so a host motor held a destroyed body forever. Availability is now part of the contract
  (`IsAvailable`, default true) and asked through the interface.
- **No motor was ever stopped or released.** Replacement, unregistration and disposal simply dropped
  the reference. That is harmless for a motor holding a Rigidbody Unity destroys anyway, and a leak
  for a host controller holding subscriptions, a rig, or a walk still in flight. `Release()` (no-op
  default) is now called on every path that drops a motor, exactly once.
- **A destroyed scene provider was still called.** The invocation used C#'s `?.`, which is reference
  equality and not Unity's overloaded comparison, so a provider whose scene had unloaded was called
  anyway and its throw killed character creation. Liveness is checked first, a throwing provider
  falls back to CoreAI's own motor rather than leaving the character unable to move, and both
  failures report through the registry diagnostics the character pipeline already uses.

### Notes

- Two contract limits are recorded in `TODO.md` rather than papered over, and they apply to CoreAI's
  own motor exactly as much as to a bridged one: `Running(speed)` reports the configured
  `WalkSpeed` once on entering the state rather than a measured speed, and `Jump` returns nothing,
  so a controller that refuses a jump cannot say so.

## [7.37.0] - 2026-09-09

### Added

- **The character pipeline is no longer a stub: joining a player now puts a Model into
  Workspace.** `RbxCharacterFactory` builds the mirror's minimum character shape — a Model
  holding a HumanoidRootPart and a Humanoid, parented into the world — and `LoadCharacterAsync`
  genuinely yields to the scheduler instead of resolving inline, so a script awaiting it observes
  the same ordering Roblox does. `CharacterAdded` fires for the new character and
  `CharacterRemoving` for the outgoing one before it is destroyed, in that order, so a handler
  reading `Player.Character` never sees a stale reference. `DistanceFromCharacter` reads the live
  root part position instead of raising `NOT_IMPLEMENTED`. This is a world-visible side effect
  worth stating plainly: any script, demo or save that inspects `Workspace` after a join now finds
  a character Model where before there was none.
- **`Players.CharacterAutoLoads`** now actually gates the join-time spawn described above (it
  previously existed only as inert state a script could set and read back, with nothing
  consuming it).
- **The join-time character spawn is deferred past the triggering dispatch, and a respawn no
  longer parents its replacement while the dead body is still in the tree.** `Players.EnsureActor`'s
  auto-load used to spawn a character inline, mid-dispatch — before a script had any chance to flip
  `CharacterAutoLoads`, and inside whatever remote-dispatch `try`/`catch` was already on the stack.
  It now schedules the spawn as a zero-delay host callback that re-reads `CharacterAutoLoads` and
  re-checks `Player.Character` is still nil once the current dispatch has fully drained — an
  explicit `LoadCharacterAsync` may already have beaten it there. A caller with no `Scheduler`
  bound (raw-registry tests) keeps the old inline spawn, since there is nothing to defer past.
  Separately, `RbxCharacterFactory.Load` now unloads the outgoing character first and assigns
  `player.Character` only once the new model is genuinely parented into the world, so a respawn
  never leaves both models in the world at once for a synchronous registry listener to observe,
  and a refused parent leaves the player with no character rather than with a detached model no
  script can reach.
- **A dead character respawns after `Players.RespawnTime` seconds, when `CharacterAutoLoads` is
  still on.** `RespawnTime` previously had no consumer at all. The Lua-CSharp bindings now wire
  every character's `Humanoid.Died` to a scheduled reload; `CharacterAutoLoads`, whether the player
  is still connected, and whether `Player.Character` still points at the dead body are all
  re-checked when the timer fires, not captured at the moment of death, so a script flipping
  `CharacterAutoLoads` off or an explicit `LoadCharacterAsync` that already replaced the corpse
  correctly cancels the respawn.
- **The character motor now steps on the fixed-step pump, not the render frame.**
  `UnityRbxCharacterMotor.Step()` used to run from `PumpPreSimulation`, driven by render-frame `dt`,
  so a velocity-driven walk applied a variable number of times per simulated physics step moved
  characters at a rate that depended on frame rate. `LuaCsRbxApiBindings.StepCharacterMotors` is now
  pumped from `LuaModRuntimeTickDriver.FixedUpdate`, alongside the physics-step opening and gravity,
  unconditionally — a character motor is driven by the fixed step even in a world whose physics port
  is the null one.
- **The agent skill document gained a `Players & Characters` section.** `BuiltInRbxApiSkillText`
  said nothing about `Players`, `player.Character`, `LoadCharacterAsync`,
  `CharacterAdded`/`CharacterRemoving`, `DistanceFromCharacter` or `RespawnTime` — an agent authoring
  mods had no way to learn about the character API landing in this same release. New §8 documents
  all of it, including that `CharacterAutoLoads` must be set from host composition rather than a
  script, since a joining actor's character can already be queued to spawn before any mod chunk has
  run.

- **A loaded world keeps its physics.** Loading a world at runtime replaces the world host's physics
  port, but the incoming Rbx API was handed the OLD port while the replacement was still being
  staged — the very port `Commit` then disposes. Gravity and character motors hid the damage,
  because the tick driver already resolved those from the live session every step; everything that
  goes through the port itself did not. In any world loaded after the first, `workspace:Raycast`
  always missed and `Touched`/`TouchEnded` never fired, with nothing in the log. The session
  controller now attaches the post-publish port to the incoming API right after `Commit`, and a test
  drives a stage-then-commit cycle and asserts a raycast reaches the new port.
- **A joining player no longer drops an unanchored box on the world origin.** The character's root
  part was materialized from the part sink's plain default — 4x1x2 studs, collidable, at (0,0,0) —
  so every join spawned a falling block inside whatever already stood there. The root part is now
  seeded with the mirror's HumanoidRootPart size and a spawn height above the origin, pushed through
  the part sink by the bindings layer (the character factory lives in the engine-free assembly and
  cannot reach the sink itself).
- **A failed spawn or respawn costs one player its character, not the whole frame.** Both the
  deferred join-time spawn and the death-triggered respawn run from the scheduler's host-callback
  slot, outside any mod's dispatch `try`/`catch`. An instance cap or a refused parent there used to
  propagate out of `Advance` and end the frame for every mod in the world; both are now contained
  and reported through the registry's diagnostics.

## [7.37.0] - 2026-09-09

The release that puts CoreAI back on the Microsoft.Extensions.AI version a Unity consumer can actually
load, and fixes what that move uncovered. Verification state, stated plainly: the portable .NET leg
(`dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj`) is green; the full Unity
EditMode/PlayMode run was not part of this release's gate, and the browser evidence quoted below comes
from a served WebGL player built on 2026-09-09, not from an automated suite.

### Changed

- **Microsoft.Extensions.AI is pinned to 9.10.2 — a ceiling, not a preference.** Unity 6000.x ships its
  own `System.Text.Json` (assembly version **8.0.0.0**) in `Editor/Data/BCLExtensions` and substitutes it
  for any copy a project vendors. Every MEAI **10.x** assembly is built against `System.Text.Json`
  10.0.0.0, so under Unity it does not fail to compile — it fails to **load**, at the first call into
  MEAI, in the running game. 9.10.2 is the newest release built against the 8.0.0.0 line. The pin is now
  stated in one place per consumer and held together mechanically:
  - `Assets/packages.config` — `Microsoft.Extensions.AI` and `.Abstractions` 10.9.0 → **9.10.2**; the
    transitive set follows the same floor (`System.Text.Json` 10.0.11 → **8.0.6**,
    `System.Threading.Channels`, `Microsoft.Bcl.AsyncInterfaces`, `Microsoft.Extensions.Primitives`,
    `Microsoft.Extensions.Caching.Abstractions`, `…DependencyInjection.Abstractions`,
    `…Logging.Abstractions`, `System.Text.Encodings.Web` → 8.x; `Microsoft.Bcl.Numerics`,
    `System.Numerics.Tensors` → 9.0.10; `System.Diagnostics.DiagnosticSource` → 8.0.1;
    `System.IO.Pipelines` dropped, nothing referenced it).
  - `tools/portable/CoreAI.Core.csproj`, `tools/G10Harness*`, `tools/ScaleHarness` and
    `Assets/CoreAI/package.json` (`nugetRefs`) now name the same version. Building the core against the
    consumer's floor turns "the game cannot load this" into a **compile error here** instead of a
    `CS0234` or a link failure days later inside someone's project.
  - **New guard `MeaiVersionFloorEditModeTests`** (6 tests). It reflects the loaded
    `Microsoft.Extensions.AI.Abstractions` version (never older than the floor anywhere; exactly the
    floor inside this checkout), requires `Assets/Packages` to hold exactly the two 9.10.2 folders and
    nothing else (a leftover second vendored copy makes the assembly Unity compiles against a coin
    flip), and re-reads `packages.config` and `tools/portable/CoreAI.Core.csproj` so the pins cannot
    drift apart silently. Outside a CoreAI checkout it ignores itself instead of failing a consuming
    game. It does **not** cover `package.json` `nugetRefs`, `INSTALL.md` or the harness `HintPath`s —
    those are still manual.
  - Source consequence, written down so nobody "modernizes" it back: the approval type is
    `FunctionApprovalRequestContent` (not 10.x's `ToolApprovalRequestContent`),
    `FunctionCallContent.InformationalOnly` does not exist, and `FunctionInvoker` **wraps** what the
    invoker returns. `MEAI_TOOL_CALLING.md` has the full list.
- **Cached-prompt and reasoning token counters travel in `UsageDetails.AdditionalCounts`**, under their
  dotted wire keys, instead of the typed `CachedInputTokenCount` / `ReasoningTokenCount` properties —
  those exist only in MEAI 10.x. `LlmUsageAccumulator.Accumulate` delegates the addition to the native
  `UsageDetails.Add`, which merges `AdditionalCounts` key by key, so vendor counters survive a
  multi-roundtrip tool turn. **A consumer reading the typed properties now gets null.**
- **`AiOrchestrator` and `QueuedAiOrchestrator` expose typed task results** through the new
  `IAiTaskResultService` (`SupportsTaskResults`, `RunTaskResultAsync`). `RunTaskAsync` became a thin
  wrapper over it. The reason is a correctness one: a legacy `string` result cannot distinguish "the
  model answered nothing" from "the provider failed", and the streaming fallback used to infer failure
  from emptiness. Terminal chunks and failure results now carry the model id, every token counter and
  the executed tool calls. `SupportsTaskResults` is false when the inner orchestrator is a legacy
  decorator, and `RunTaskResultAsync` then throws rather than inventing a success.
- **A context-overflow retry is refused once the failed turn already executed tools or produced
  content**, and a structured-output validation failure after tool execution returns an `InvalidRequest`
  failure instead of retrying. Retrying past a side effect re-applies it.
- **Conversation-summary preflight is committed before any provider or tool side effect**, and
  `UserTurnHistoryLatch.SummaryPreflightPending` blocks user-turn appends until the write is
  acknowledged — bounded history can no longer evict the messages a still-unconfirmed summary retells.
  *Half of this is superseded by 7.41.0: the commit-before-side-effect ordering stands and is what
  eviction safety now rests on, but `SummaryPreflightPending` is removed — it dropped the learner's
  message whenever a turn broke during context building. This pointer is here because the symbol is
  gone from the code, so a reader who finds only this entry would take a removed API for a current one.*
- **Async context building requires an async summary store.** `BuildSnapshotAsync` used to call the
  synchronous `LoadSummary`; against `FileConversationSummaryStore` that API is fail-fast while the file
  gate is busy, so an overlapping async operation turned an ordinary compaction into an
  `InvalidOperationException`. A sync-only custom backend must now be wrapped explicitly
  (`BlockingSyncSummaryStoreAsyncAdapter`, or `allowBlockingSyncFallback: true`) — the
  `NotSupportedException` says so by name. `ConversationContextSnapshot.Commit()` likewise rejects
  snapshots built by the async path, and `CommitAsync` rejects sync-path snapshots.
- **Role skill catalogs are case-sensitive and role ids are trimmed.** `_roleSkillCatalogs` moved from
  `OrdinalIgnoreCase` to `Ordinal`, matching every other role dictionary: `"Merchant"` and `"merchant"`
  used to share one skill catalog while holding separate tool lists. `ApplyToPolicy(null)` throws
  `ArgumentNullException` instead of `NullReferenceException`; a blank role id throws
  `ArgumentException`.
- **`AskAsync` no longer replaces a role.** Two configurations for the same role keep first-ready
  behaviour; replacement is `ApplyToPolicyAsync`.

### Fixed

- **The model was shown a type name instead of the tool's answer.** `SmartToolCallingChatClient`'s
  native invoker returned `ToolCallResult.Result` — a `MEAI.FunctionResultContent` — and MEAI wraps
  whatever the invoker returns into a result content of its own. The model therefore received the
  rendered container, i.e. the literal string `Microsoft.Extensions.AI.FunctionResultContent`. The tool
  ran, the game state changed, and the model answered as if the tool had produced garbage. The invoker
  now unwraps the payload (`ToolPayloadOf`) and lets MEAI own the pairing. Pinned by
  `MixedBatch_InventedNameBeforeRealCall_ModelReadsTheRealToolResult` and the strengthened
  `NativeLoop_ServerHandledCallBesideLocalCall_IsNotInvokedAgain`, which assert on the result text.
- **An invented tool name burned the whole roundtrip budget instead of stopping on the error counter.**
  Unknown names were answered through bindings registered into `ChatOptions.AdditionalTools` *after* the
  model had already named the tool — but MEAI resolves `AdditionalTools` **once per request, before the
  first provider call**, so the binding was never consulted. MEAI answered "Requested function … not
  found" itself, `ToolExecutionPolicy` never saw a failure, `_consecutiveErrors` stayed at 0, and the
  loop ran to `MaxToolCallRoundtrips` (**20 full provider requests** of latency, tokens and money) for a
  model that was simply hallucinating a name. Unbound calls are now resolved inside the loop, through
  the policy, where the error counts: the model gets `Error: Unknown tool 'X'. Available tools: […]` and
  the turn ends on `IsMaxErrorsReached` (3 in a row). Pinned by
  `MixedBatch_RepeatedInventedName_StopsOnErrorGuardNotRoundtripBudget`.
- **A mixed batch of known and unknown calls desynchronized its indexes, so a tool that succeeded was
  reported as failed — and could run up to twenty times.** The invoker looked its result up by
  `context.FunctionCallIndex`, MEAI's index over **every** call in the model's message, while
  `_batch.Results` held only the calls the policy was asked to execute — server-handled,
  approval-required and invented neighbours are filtered out. One such neighbour and the lookup returned
  the wrong call's result or ran past the end; the `IndexOutOfRange` reached the model as an invocation
  failure for a call that had in fact succeeded, so the model reissued it, and with varied arguments the
  echo guard does not suppress the repeat. Results are now matched by **call id**, never by position,
  and service-answered calls are looked up separately. Covered by the five new `MixedBatch_*` tests,
  which also assert exactly one result per call id (two is a shape providers reject).
- **The audit log's prompt hash did not cover the conversation.** `AuditContext.SetPromptHash` hashed
  `string.Join("\n", (System.Collections.IEnumerable)chatHistory)` — cast to the non-generic
  `IEnumerable`, that binds to `string.Join(string, params object[])`, so `Join` received a one-element
  array holding the list and called `ToString()` on it. What went into the digest was the constant text
  `System.Collections.Generic.List\`1[Microsoft.Extensions.AI.ChatMessage]`. Two entirely different
  conversations produced the **same** `promptHash` whenever the system prompt and user text matched, so
  the field could neither distinguish nor reconstruct the context a model actually answered on. Replaced
  by `AiOrchestrator.ComputePromptHash`, which hashes system, user and every history message in order.
  Pinned by `PromptHash_CoversTheConversationHistory`.
- **The streaming queue reader could drop the terminal chunk.** `AsyncChunkQueue.Write` enqueues into a
  lock-free queue *before* signalling, so the reader's "empty, then completed" pair of looks had a hole:
  producer enqueues the `IsDone` chunk, marks the queue complete, reader reads `_completed` and stops
  with the chunk still queued. That chunk is the only carrier of the token counters, the executed tool
  call list and — on the failure paths — the error itself, so a turn could end reporting nothing at all.
  The reader now takes one more look after observing completion; completion is set after the write, so
  a single extra dequeue is exact. Pinned by
  `QueueReader_CompletionObservedRightAfterAnEmptyLook_StillDeliversTheLastChunk`, which scripts the
  interleaving instead of hoping for it.
- **A streaming chunk that carries only an error code is a failure, and is now pinned as one.**
  `RetryingStreamingLlmClientDecorator` matched a failing chunk on its error *text*, so a provider that
  set `ErrorCode = RateLimited` (or `Timeout`) with no message rode through as a benign hint: the
  category was dropped, whatever followed the failure was forwarded as if it belonged to the answer, and
  the turn ended as `EmptyResponse`. The decorator now uses the same predicate the orchestrator uses to
  end a turn — error text **or** a non-`None` code — which is what it already did for `IsDone` chunks and
  now also does mid-stream. Three tests hold it: a transient code without text is retried, a permanent
  one (`PaymentRequired`) ends the stream with its own classification, and a chunk with neither text nor
  code stays a forwarded hint. In the same decorator: a failure after the retry budget keeps its real
  classification instead of collapsing to `ProviderError`/"stream failed after retries"; null control
  chunks no longer end the stream; the provider stream is opened lazily and the token is checked at
  entry, per attempt and before each retry, so a pre-cancelled or mid-retry-cancelled request never
  opens (or re-opens) the provider; and a `DisposeAsync` failure is attached to the real outcome instead
  of replacing it.
- **The comment guarding the double-execution check is back.** The order "retryable **and** not already
  committing" is not stylistic: deciding on the error code alone would leave the protection resting on
  producers happening to leave `ErrorCode = None` on a chunk that carries `ExecutedToolCalls`. Label such
  a chunk `Timeout`/`BackendUnavailable` honestly and `spawn_quiz` runs a second time while memory is
  written twice. Covered by `ErrorChunkAfterToolExecution_IsNeverRetried_EvenWithRetryableCode`
  (verified: removing the `!IsCommittingChunk` term turns it red).
- **An argument-coercion failure is traced as invoked, not as never-invoked.** The old classification
  read the stack frames, which IL2CPP/WebGL strips — so a failure thrown by the **tool body** looked
  like a binding failure and the retry decorators replayed a mutation that had already been applied.
  Pinned by `DelegateLlmTool_ArgumentCoercionFailure_IsTracedAsInvoked`.
- **`AgentBuilder` published a half-configured role.** `ApplyToPolicy` mutated the policy step by step
  and read persisted skills from storage in the middle, so a racing ask — or a store read that threw
  partway — left the role registered with tools but without its `read_skill` / `call_skill_tool`
  proxies, its extra system prompt or its streaming override. The role is now assembled detached and
  published under one lock; a failed hydration leaves the previously ready role untouched.
- **A role without skill authoring got no live catalog**, so later catalog changes were invisible to its
  `read_skill` / `call_skill_tool` and the policy's view of skills could disagree with the agent's.
  Re-applying a config also used to silently strip or duplicate the skill proxies; both directions are
  now explicit.
- **`AskWithCallback` never fired its callback on WebGL** — it awaited with `ConfigureAwait(false)`,
  and a detached continuation in a browser player is a continuation that never runs. The request looked
  like a hang with no error.
- **A host scope change mid-request could write one user's summary into another user's partition.** The
  context managers resolved the storage key twice — once to load, once to save, possibly deferred. The
  binding is now pinned for the life of the snapshot (`ScopedConversationSummaryStoreDecorator.BindAsync`).

### Removed

- **The artificial slicing of the model's stream.** Every text-only SSE delta longer than 24 characters
  was thrown away and replaced by manufactured pieces of ~6 characters (`SplitForSmoothStreaming`), with
  a 15 ms `Task.Delay` between them (`DelayBetweenSyntheticStreamPiecesAsync`). On editor and standalone
  that meant the answer was rendered **slower than it actually arrived** — a 300-character delta became
  30–50 pieces, half a second to three quarters of pure invented latency, per delta. On WebGL there was
  no delay, but the delta was still shredded into per-piece allocations on the single thread. And
  everywhere it hid the truth: a provider that batches tokens looked smooth, so nobody could see the
  batching, let alone fix it. The provider delta is now passed through exactly as it arrives. The test
  that pinned the old behaviour (`…_SplitSmoothing_PropagatesNativeContracts`, which asserted the delta
  **must** be split) was replaced with `GetStreamingResponseAsync_LargeTextDelta_ReachesTheConsumerWhole`.
  **Cost worth knowing:** a consumer that paced its display per chunk will now see the provider's real
  chunking.
- **Hand-rolled code that duplicated Microsoft.Extensions.AI.** Each of these was a private
  reimplementation of something the library already does, and each had drifted from it: the
  `DelegatingChatClient` that stripped the native tool channel for text-only endpoints (now the native
  `ConfigureOptionsChatClient`); the hand-built `UsageDetails` summation that added only input/output/total
  and the dictionary, silently dropping every typed counter (now `UsageDetails.Add`, which is what carries
  prompt-cache reads through a multi-roundtrip tool turn); `ReadUsageInt64` and `EnumerableContents`; the
  hand-concatenated streaming text and final-assistant text (now `update.Text` / `message.Text` — note
  `ChatResponse.Text` must never be used, it concatenates **every** message and pastes the tool transcript
  into the reply); the `AdditionalTools` failure-binding trick for unknown tool names, which MEAI resolves
  once per request and therefore never consulted; and `BuildToolsCatalogBlobForWordCount`, which
  materialized the whole tool catalog just to count its words.
- **The dead `arg-conversion` branch in `LoggingLlmClientDecorator.TraceIndicatesInvocation`.** Nothing
  emits that trace source any more — tool-argument coercion failures stopped being classified from stack
  frames (IL2CPP/WebGL strips them) and are traced as `native`, i.e. as having crossed the invocation
  boundary. The branch only made a future reuse of that string silently retry-eligible; without it the
  fail-safe `default` counts an unknown source as invoked. Its assertion in
  `RetryFallbackToolTraceSuppressionEditModeTests.TraceIndicatesInvocation_ClassifiesSourcesCorrectly`
  went with it; the `some-future-source` case still covers the fail-safe.

### Performance

Measured on the portable .NET harness (`CoreHotPathAuditEditModeTests`, 4000 chunks, single-threaded
pump that mimics WebGL). The executable guard is a budget of **≤192 bytes per chunk over the inner
stream**, not the individual figures below; the allocation assertions run on the portable leg only and
`Assert.Ignore` under Unity.

- **`TimeoutLlmClientDecorator`: 512 → 128 bytes per streamed chunk.** The per-chunk wait was
  `MoveNextAsync().AsTask()` + an async helper + `Task.WhenAny` — five or six heap objects for every
  token a learner reads. Replaced by one `IValueTaskSource<bool>` race object per **request**, with a
  reusable core, one cached callback and one cancellation registration; a synchronously completed move
  now allocates nothing.
- **`QueuedAiOrchestrator`: 289 → 96 bytes per chunk.** The reader parked on a `TaskCompletionSource`
  that was replaced on every signal. The queue is now its own `IValueTaskSource<bool>`; a write with no
  parked reader costs nothing.
- **`AuditHash.ComputeParts`** hashes the UTF-8 bytes of a concatenation without building it, through
  pooled buffers and a stateful encoder (a surrogate pair split across parts encodes identically). The
  orchestrator fingerprints the whole prompt every turn; joining first cost a copy of the prompt plus
  its encoding — hundreds of kilobytes on a long lesson — purely to feed the hash. Equality with the
  single-string form is pinned across surrogate and chunk-boundary cases.
- **`AuditContext` retention is bounded** (`MaxTrackedTraces = 256`). The only caller of `Cleanup` is the
  Unity audit interceptor, so a headless or portable host leaked one prompt hash and model name per
  request for the life of the process.
- **Skills over a live catalog stop rebuilding themselves.** `call_skill_tool` and `read_skill` rebuilt
  their whole tool map / skill index on **every call and every per-request allowlist probe** —
  re-creating each skill's MEAI function by reflection and re-serializing every JSON schema — for a
  catalog that changes only when the model authors a skill. Both now cache against a new monotonic
  `MutableSkillCatalog.Version`. `ReadSkillLlmTool.ParametersSchema` is computed once instead of on
  every read, and a skill call's argument JSON is parsed once instead of three times.
- **`AgentMemoryScope` memoizes storage keys** (bounded, 512 entries). Every scoped memory or history
  operation — several per turn — used to build a fresh `SHA256`, encode, and format 32 hex strings. The
  test asserts the memoized key is still byte-identical to the digest that was persisted before.
- **`AiToolContractPromptFormatter`** memoizes canonicalized tool schemas (bounded, 256), so the
  text-shaped tool contract is not re-parsed and re-sorted per request.
- **`LoggingLlmClientDecorator`** computes its prompt-budget line once per request instead of twice, and
  counts the tool catalog's words per field instead of materializing tens of kilobytes of schema text to
  count them; the prompt preview no longer copies the whole prompt just to trim it.
- **`ExtractToolTraceMessage` gained a first-character gate.** A plain-text tool result used to cost a
  thrown-and-caught `JObject.Parse` exception on every tool call.
- **SSE parsing** frames `data:` lines without copies and decides tool-argument completeness
  incrementally; the old code called `StringBuilder.ToString()` and re-scanned everything accumulated so
  far on every line, i.e. quadratically in the argument length. Pinned by measurable claims: a 42-delta
  tool call materializes its buffer **once**, a text-only stream **zero** times.

### Added

- **An asynchronous, cancellable surface for the stores that sit inside an LLM turn.** New
  `IAsyncSkillStore` (`LoadAsync` / `ListAsync` / `MutateAndPublishAsync`), `IAsyncLuaScriptVersionStore`,
  their `InlineAsync…Adapter` opt-ins for stores that really are fast and non-blocking,
  `SkillStoreDurabilityException` (committed, not durable, not published, **do not replay**) and a
  `SkillOperationGate` that fails a synchronous caller **promptly** instead of blocking while async work
  is pending. `SkillAuthoringCoordinator` gained async twins of every operation, sharing one
  `Prepare*` implementation with the sync path so the "an update rewrites only the entry document,
  reference documents pass through" rule has exactly one owner. `manage_skills` is now genuinely async,
  honours its cancellation token, and reports durability and publication failures as typed envelopes.
  Before this, every `manage_skills` action did file IO inside the turn — on a single-threaded player
  that is a freeze, not a stall.
- **`AgentBuilder.BuildAsync` / `ApplyToPolicyAsync` / `WithAsyncMarshaler`,** and a shared registration
  gate: the first `AskAsync` now hydrates persisted skills before dispatching, concurrent first asks
  share one hydration, a ready role skips the storage read, and a cancelled waiter does not poison the
  others. Documented in `Docs/AGENT_BUILDER.md` → "Asynchronous skill readiness".
- **A licence-free CI gate for the core.** New `tools/portable/Tests/CoreAI.Portable.Tests.csproj` runs
  the existing engine-free EditMode fixtures against the real `netstandard2.1` `CoreAI.Core` assembly on
  plain .NET 8 — no Unity, no editor symbols, no licence — and a new `portable-core` job in
  `.github/workflows/ci.yml` runs it first with coverage. Fixtures are linked **file by file, not by
  glob**: a new engine-free test must be added to the project explicitly or it never runs in this leg.
  This is also what makes the MEAI floor enforceable — an API that exists only in a newer MEAI now fails
  here rather than in someone's game.

### Mods / Rbx API

- **`Player:LoadCharacterAsync()`, `LoadCharacter()`, `DistanceFromCharacter()`, `CharacterAdded`,
  `CharacterRemoving`** are implemented; they were loud "planned" stubs. `Players.CharacterAutoLoads`
  now actually spawns a character (a `Model` with a `Humanoid` and a `HumanoidRootPart`). The new
  `RbxCharacterFactory` parents the model **last**, so a `ChildAdded` handler never observes a half-built
  character, and destroys the partial character if anything throws.
- **A disconnect used to leave a ghost character standing in the world** — `RemoveActor` now unloads it
  before destroying the `Player`. Auto-load runs **after** `PlayerAdded` fires, so a handler reading
  `Character` with auto-load off sees `nil` rather than a race.
- **`MoveTo` reported instant arrival and `WalkSpeed` moved nothing in real scenes**: composition never
  supplied an `IRbxCharacterMotor` factory, so every `Humanoid` silently got the null motor with its
  position pinned at zero. **`Humanoid.RootPart` returned the character `Model`** — the wrong object of
  the wrong class, with no error. **In a built player no frame signal fired at all**, because the
  production driver advanced the scheduler for a single phase only.
- **`RunService.Step` boxed its delta before checking for handlers** — one guaranteed allocation per
  frame in a scene with zero connections.
- **`Clone` lost external `BasePart` state** (size, CFrame, colour, anchored came out default on
  non-Lua clones) and could leave an orphan partial subtree on failure. The registry gained a
  `CopyBackingState` seam so the engine-free side can trigger the copy without knowing the Unity sink's
  type.
- **New `LuaCsAllocationBudget` (256 MB) closes the concatenation bomb.** `s = s .. s` is plain VM
  opcodes with no library call site to cap, and it outruns the step and time budgets. It samples the
  cheap `GC.GetTotalMemory(false)` every 4 instructions and confirms a crossing with **one** collecting
  read, re-baselining when the growth turns out to be garbage — so a mod cannot force collections, and
  cannot forge the trip either: it is classified by exception type, not by a text marker. This also
  replaced a hand-copied, already divergent second copy of the rule in `LuaCsSecureEnvironment`.
  The documented mechanism was wrong before, too: the docs claimed a 64 MB budget policed by
  `GC.GetAllocatedBytesForCurrentThread()` on every instruction, and **Unity's Mono returns 0 from that
  API unconditionally**.
- **New `IScriptFrameYielder` / `PlayerLoopScriptFrameYielder`:** a legitimate long `execute_lua` chunk
  used to freeze a single-threaded player — a measured runaway held the browser main thread for ~6 s
  inside its ~10 s wall-clock budget. The async path now releases the frame every 6 ms and subtracts the
  yielded time so the budget still measures executed work. Deliberately **not** armed on the synchronous
  `Execute` overloads (a blocked caller awaiting from inside the hook is a guaranteed deadlock on the
  single WebGL thread), nor on the actor-scoped and mutation-envelope `execute_lua` overloads, which run
  under a monitor with an ambient mutation envelope; that limitation is stated in
  `LUA_SANDBOX_SECURITY.md` rather than papered over.
- **`HttpService:JSONEncode/JSONDecode` and the network codec are not the same serializer**, contrary to
  the MVP2 acceptance text. The new `RbxJsonContractEditModeTests` pins each one's actual behaviour and
  compares them differentially: empty tables, JSON `null` inside arrays, mixed key types, sparse arrays,
  whole numbers, `NaN`/`Infinity` and cross-decoding all diverge, and now that divergence is written down
  instead of assumed away.
- The MVP1 conversion lint no longer misses an **aliased** `MetersPerStud` (`float s = …;` used lines
  later), and it now scans the demos and the Unity layer, not only `Runtime/`.

### MCP

- **A JSON-RPC `id`, tool name or string argument that looked like a timestamp was silently rewritten.**
  `JToken.Parse` applies Newtonsoft's default `DateParseHandling`, which coerces date-shaped **strings**
  into `DateTime` tokens — so a client using timestamp ids could not match its own responses. Parsing now
  runs through an explicit reader with `DateParseHandling.None`, and still rejects trailing content.
- **The dispatcher validated nothing before dispatching.** A *number* was accepted as a method name
  (`42` → `"42"`), an `initialize` with no `jsonrpc` created a real session, and `{"name": 123}` invoked
  a tool registered as `"123"`. The envelope is now checked (`jsonrpc == "2.0"`, string `method`,
  scalar-or-null `id`, object-or-array `params`) and `tools/call` reads `name` only when the token is a
  string; a malformed `id` is echoed back as `null`. Ten `[TestCase]`s plus round-trip pins for
  date-shaped ids, names and arguments.
- `McpServerInfo.Version` is bumped by `tools/bump_version.py` together with the manifests, and
  `McpPackageVersionEditModeTests` fails when they drift.

### Docs

- **Three entry points for three audiences.** The repository `README.md` was rewritten (−666/+326) from
  "LLM agents that play your game" to "a C# agent runtime you embed in your own application" — games
  first, Unity no longer the only door — and `Assets/CoreAI/README.md` is **new**: until now the core
  package had no README at all. Every "properties worth knowing" claim names the test that backs it, and
  each README ends with an explicit **Limits and non-goals** section (no MCP client; no provider
  abstraction beyond OpenAI-compatible HTTP; the tool loop does not run while streaming; "never blocks
  the frame" is deliberately **not** claimed; `netstandard2.1` is not .NET Framework).
- **The tool-calling docs were describing an API that does not exist, and behaviour that is the
  opposite of the truth.** They told hosts to add a name to `ToolExecutionPolicy.SerializedMutatingToolNames`
  (no such member — the extension point is `ILlmTool.IsMutating`) and cited `CheckDuplicate` and hard
  line numbers. They described duplicate suppression as an **error** feeding the consecutive-error
  counter, when it is a success no-op (`{"ok":true,"duplicate":true,…}`) that takes no part in that
  counter — so "show me that again" three times used to look, in the docs, like a turn abort. They
  described mutating streaming calls as deferred to turn finalization when they execute on arrival, and
  they promised tool results reach the model verbatim when the policy makes exactly two documented edits
  (truncation at `MaxToolResultChars` with a marker, and the empty-result envelope). All corrected.
- **New guard `ToolDocsPolicyReferenceEditModeTests` compiles the prose.** It extracts every
  `ToolExecutionPolicy.<member>` mention from the four tool-calling documents and resolves it by
  reflection, and it asserts the docs quote the **exact** duplicate no-op payload the code emits. It
  fails on a broken reference, not on a mention, and it runs on the portable leg too.

## [7.36.0] - 2026-09-08

### Changed

- **Single `LlmToolArgumentNormalizer` chokepoint for tool argument normalization.**
  The three copies of the Newtonsoft-to-CLR rule (native `ToolExecutionPolicy`, text-extracted
  `SmartToolCallingChatClient`, `SkillSetToolResolver`) are unified; behavior is pinned by
  `LlmToolArgumentNormalizerEditModeTests` (9 tests).
- **`RbxInstance.Clone()` completes BasePart spatial/appearance state**, closing the gap recorded
  in the closure audit (MVP1 finding 3): the C# `Clone()` path used to skip Size/CFrame/Color/
  Anchored and the rest, because that state lives outside the engine-free assembly in the Unity
  binder, which `Instances` cannot reference directly. A new seam,
  `IInstanceBackingBinder.CopyBackingState`, is declared on the interface `Instances` already owns
  and implemented on the Unity side (`InstanceGameObjectBinder`), so `CloneSubtree` can ask for the
  copy without ever seeing the sink's type. A clone made from C# now matches what the Lua binding's
  `CopyPartSinkState` already produced. The interface member ships with a default (no-op) body,
  added after the interface had already shipped, so a host implementation outside this repository
  keeps compiling without picking it up.
- **The four modern `RunService` events are bound for C# listeners, not only Lua.**
  `PreAnimation`/`PreSimulation`/`PostSimulation`/`PreRender` shipped in 7.35.0 already worked from
  mod scripts, but only `Heartbeat`/`Stepped`/`RenderStepped` were wired to the scheduler at the C#
  binding layer — a signal with no scheduler bound refuses a direct C# `Connect` outright, so a host
  listening for the modern names had no way to. All seven signals are bound the same way now.
- **`HttpService` and the remote-replication codec are confirmed to be two independent JSON
  encoders, by design, not by omission.** The closure audit (MVP2 finding 2) found the claim that
  they were "the same component" was structurally false — `HttpService` uses `LuaCsRbxJson`,
  remotes use `LuaCsRbxNetworkCodec` — and asked for a decision rather than a test that papers over
  it. The decision: keep them separate, and pin the boundary with a contract test
  (`RbxJsonContractEditModeTests`, 654 lines) so a future change to either encoder's round-trip
  behavior is caught instead of silently diverging further.
- **A failed tool-argument binding is now proven structurally, before the tool body ever runs.**
  `ToolExecutionPolicy.TryBindArgumentsStructurally` round-trips each declared parameter's raw value
  through the same `JsonSerializerOptions` MEAI itself binds with, reflecting the target CLR type off
  `AIFunction.UnderlyingMethod` — entirely before `InvokeAsync` is called. Previously an
  argument-conversion failure was only guessed at by pattern-matching the invocation exception's
  message (`LooksLikeArgumentConversionError`), which cannot tell a true binding failure apart from a
  tool body that legitimately throws `ArgumentException` after already mutating state; every
  exception that still reaches the catch block downstream is now unconditionally traced as `native`
  (blocking retries after a possible side effect), because a binding failure can no longer be one of
  them.
- **`TimeoutLlmClientDecorator` no longer relies on `TaskCompletionSource`'s
  `RunContinuationsAsynchronously` option, and its timeout is now reliably observable as a `Faulted`
  task.** WebGL has no thread pool, so a continuation deferred there under
  `RunContinuationsAsynchronously` is posted nowhere and never resumes — a silent permanent hang.
  `CompleteAsync` is also no longer `async` all the way through: the compiler puts an async method's
  own task into the `Canceled` state (not `Faulted`) when its body throws
  `LlmOperationTimeoutException`, and a caller that re-observes an already-completed `Canceled` task
  (rather than awaiting it live) is not guaranteed the exact exception type back on every runtime. It
  now runs detached against an explicit `TaskCompletionSource`, whose `TrySetException` always
  produces a `Faulted` task that replays the exact exception. A bounded ~20 ms real-time grace window
  also now gives a cooperative inner operation a genuine chance to finish its own cancellation unwind
  before its result is discarded as a bare timeout, instead of a single scheduler hop whose ordering
  against the same cancellation token is not a documented .NET guarantee.

### Tests

- **Criterion 6's scaled-time half is covered.** `RbxScaledTimeEditModeTests` sets a non-unity
  `timeScale` and asserts the scheduler's clocks actually scale with it — previously no test
  anywhere did this, and the roadmap now records that `os.clock()` is monotonic wall time, not CPU
  time, as a deliberate deviation rather than an oversight.
- **U1–U7 stance conformance now has an observable pass condition.** `Mvp2StanceConformanceEditModeTests`
  cites each stance by name; before this, criterion 15 had nothing a reviewer could point at.
- **The conversion-lint text heuristic was widened, closing both escapes the closure audit
  named.** `Mvp1ConversionLintEditModeTests` now also catches `float s = RbxSpace.MetersPerStud; x
  * s;` — a variable aliasing the constant a few statements before the arithmetic, tracked by brace
  depth rather than by line — and the scale-arithmetic check (not the raw-literal one, which stays
  Runtime-only on purpose: a bare `0.28` has unrelated legitimate meanings outside it) now also
  scans `Assets/CoreAI.Demos` and `Assets/CoreAiUnity`, both of which reference `RbxSpace` but were
  previously unscanned.

### Notes

- **Two process defects reached this branch and were caught, not just code defects.** The commit
  that landed the above did not compile as committed — a `double` literal was passed where a
  constructor expected a `float` — and a separate EditMode fixture deadlocked the whole editor: a
  synchronous test blocked the main thread while awaiting a continuation that only the main thread
  could ever run, so nothing ever resumed it. Both are recorded here because the failure mode is
  the process, not the individual line: a single 41-file, ~2900-insertion commit mixing the
  character pipeline with unrelated hardening (`McpRpcDispatcher` JSON-RPC validation, a
  `FileTokenCalibrationStore` rewrite, cross-instance path locking, a new portable test project and
  a CI job) is exactly the shape of change where a compile break or a main-thread deadlock hides
  until the next full run.
- A follow-up review round on this same commit found further defects in the character pipeline
  (join-time spawn ordering, `RespawnTime` having no consumer, the motor stepping on the wrong pump,
  and a skill-text gap for agents); all four are fixed — see the Added/Changed entries above. The
  same round found a broader main-thread-deadlock guard gap in the EditMode test assembly, tracked
  and being closed separately, outside this entry.

## [7.35.0] - 2026-09-06

Release following the MVP1, MVP2 and MVP2.5 closure audit (`dev-docs/MVP_CLOSURE_AUDIT_2026-09-06.md`).
Three independent checkers verified each criterion against a green run, not against the source.
**Honest verdict: MVP1 is closed, MVP2 is not, MVP8 (and therefore MVP2.5) is not.** Below is what was fixed;
what remains is named in TODO and in the audit by name.

### Added

- **Modern per-frame `RunService` events: `PreAnimation`, `PreSimulation`, `PostSimulation`,
  `PreRender`.** They did not exist at all — only internal scheduler phases — and a script copied from
  the current Roblox documentation with `RunService.PreRender:Connect(...)` failed with "not a valid
  member of RunService". This is a parity gap in exactly the direction that matters most: the modern
  names are the ones Roblox teaches today. The legacy aliases `Stepped`/`RenderStepped` keep
  the legacy signatures, the modern ones take only the delta.
- **The render phase does not run where nothing is rendered** — new `IRbxRuntimeTopology.RendersFrames`.
  The gate is exactly this one, not `IsClient`: a solo CoreAI process renders and is the
  server at the same time, while `IsClient` there is intentionally false, so a gate on it would silently kill the per-frame
  handler in every solo game. This is covered by a guard test.
- **`Players.CharacterAutoLoads`, `Players.RespawnTime`, `Players.MaxPlayers`.** All three were listed in
  the plan as shipped but were actually missing — not a stub, but "not a member", which an author
  reads as their own typo. `MaxPlayers` is read-only: assignment is rejected, not
  ignored. A negative or non-finite `RespawnTime` is rejected at the assignment site.
- **`Humanoid` state in the world packet.** It was silently lost: save a world with an NPC at 30/100 —
  get 100/100 with no diagnostics at all. Now `Health`, `MaxHealth`, `WalkSpeed`, `JumpPower`,
  `JumpHeight`, `UseJumpPower` and `DisplayName` survive the round-trip, and a corrupt packet is rejected
  atomically, before any registry mutation.
- **`RbxStubRaiseObserver`** — an observation point for loud-stub firings.

### Fixed

- **`Touched` suppression on `CFrame` teleport was dead code.** `NoteTeleport` is called
  from Lua, i.e. from `Update()`, while `BeginPhysicsStep()` cleared the mark in the next
  frame's `FixedUpdate()` — before the simulation step the mark was made for. `Touched` fired on every
  scripted teleport. The old test passed only because it called everything in an order that never
  happens in production. `Orientation` and `Rotation` writes now also mark a teleport.
- **`Raycast` silently missed** past the 32nd collider: the fixed `RaycastNonAlloc` buffer without
  sorting dropped the needed part and returned a "miss".
- **Contact-pair leak**: a part destroyed while in contact left the pair key behind forever, and when
  the id was reused the next genuine `Touched` was deduplicated.
- **`Humanoid.Jump` always read as `false`** — now answers with the state, otherwise the
  `if humanoid.Jump then` branch is dead by construction.
- **The demo-scene smoke** was not "hanging since August 30". Three causes, two of them introduced by
  the demo work: the frozen list grew to 17 scenes while `Assert.AreEqual(15, ...)`; a failure before the first
  `Single` load left the test-runner bootstrap scene loaded, and the sandbox unloaded the scene
  together with the runner itself — a failed assert turned into a dead batchmode editor. Third:
  a locally installed Mirror halts Play Mode on its `NetworkScenePostProcess`.

### Changed

- **The `SetNetworkOwner` family — loud backlog stubs** instead of a silent absence. Along the way
  this fixes the negative twin of gate R12.5, which was already broken on the missing member.
- **The Rbx API skill for agents has been rewritten.** Until now it told the model that `TweenService` and
  `CollectionService` raise `NOT_IMPLEMENTED`, and it had no sections on `Humanoid`,
  `workspace:Raycast`, `workspace.Gravity`, `Touched`/`TouchEnded` and Value objects. Agents
  writing mods are the main consumer of this API, so this is an operational defect, not a documentation
  trifle. A ratchet was added: the test reads the "shipped/stub" truth from `ServiceCatalog` at
  run time, so the next rung cannot go stale silently.
- **The "zero stub hits" gate in the corpus became real.** Before it grepped the log text for
  `NOT_IMPLEMENTED`, while at runtime nobody writes anything at the throw site — a fixture hiding
  a stub inside `pcall` passed cleanly. Now counting happens at the error-construction site, and a hostile
  fixture proves that such a wrapper is classified as a failure.
  The first attempt used `AppDomain.FirstChanceException` — the framework's standard answer to exactly
  this task — and under Mono in Unity it **never fires**: the counter showed zero on a throw
  that definitely happened. Caught by a run, not by reading.

### Tests

- Destroy sequence order (D6) and connection teardown: the old tests checked only
  the final state and stayed green when steps were reordered.
- A destroyed tween target does not report `Completed`; `timeScale = 0` freezes `Debris`.
- `task.cancel` on a dead thread is pinned to the exact CoreAI text (Roblox gives no wording mirror
  — the divergence is recorded, not papered over).
- `OnServerEvent` does not run synchronously inside `FireServer` — checked BEFORE the scheduler pump.
- `RbxRunService.Step()` has no callers in production; the test keeps its signal order
  equal to `PumpFrame()`, so the unused pump does not drift from the working one.

### Verification

- Full EditMode **3694 total / 3685 passed / 0 failed / 9 skipped** (`artifacts/testresults/verify4.xml`).
- PlayMode `FastNoLlm` + Mods **100 total / 99 passed / 0 failed / 1 skipped**
  (`artifacts/testresults/pm6.xml`), including the 17-scene demo smoke — the first green run of that
  test since August 30.
- Modularity confirmed by a run: with `Assets/Mirror` removed the tree compiles with 0 errors and
  `CoreAI.Net.Mirror.dll` is not built at all.
- Five camera "failures" in PlayMode are a `-nographics` artifact (`RenderTexture.Create failed`), not a
  regression: with graphics all are green. Camera PlayMode runs must not be launched without a graphics
  device.

### Notes

- **What remains unclosed and why** — in `TODO.md` and §4 of the audit: the character pipeline together with
  the motor (one slab: a character that does not move, and a motor without a character, are not worth shipping
  separately), the MVP11 join-snapshot, wiring the intent gateway to the MVP12 wire, a two-process
  run over a real socket, MVP2 criteria #12 and #14 (they need a decision, not a test).
- The note in 7.34.0 that the demo-scene smoke hangs "for reasons unrelated to this work" was wrong; the causes
  were found and recorded above.

## [7.34.0] - 2026-09-06

### Changed

- **IMGUI has been removed from the demos entirely.** All ten controllers were moved to the shared
  `CoreAiDemoPanel` (a real Canvas): DirectorAi, LiveMechanics, LiveMechanicsMods (mod manager
  with rows, an editor and per-mod buttons), LuaMods, ModdableUnits, Qwen Genie, Qwen
  Spellcraft, Skills, WebGL Lua self-test, WorldCommands. The exception list in the demo ratchet
  is empty — a new file with `OnGUI` now fails the test immediately.
  The reason is not cosmetic: immediate-mode UI renders nothing in a built player, so an `OnGUI`
  demo works only on the machine where it was written — and the demo is the first thing opened during evaluation.
- One shared panel instead of a panel per demo: ten scenes, each with its own Canvas, would be ten places
  to get anchors wrong, and ten visually different products. It builds itself at runtime rather than
  arriving as a prefab: a prefab is a binary whose diff is unreadable, and it would have to be copied into every scene.

### Notes

- **The demo-scene smoke (16 scenes) hangs and crashes the editor — this is not from this work.** Verified
  A/B: the same run with temporarily hidden conversions fails exactly the same way, and with the
  two new scenes excluded from the list as well. The last green run of this test was August 30, before the whole
  MVP8–MVP12 series. Recorded in TODO as a separate task with evidence; not claimed green here.

## [7.33.0] - 2026-09-06

### Added

- **MVP11/12 demo scene "Online Authority".** MVP11 and MVP12 consist of denials, and a denial is not visible
  on a screenshot. Here the visitor plays as a guest: they ask to move a statue and watch how one and the same
  request is first denied, then — after the host grants it — succeeds, and is denied again the
  very moment the host revokes the grant; the reason is printed every time. The rules are real: the same
  registry, the same gateway, the same check order that the tests pin down.
  A separate "host moves the statue" button shows what is hardest to explain in words: a host
  write does not travel the intent path at all and does not ask the registry — "the host holds all rights" means
  exactly that, not a row in a table.
  What the demo does **not** claim: that the request travelled over the network. It runs the authorization path in one
  process; the transport has its own gates, and passing a local call off as the wire would be a demo that lies.
- PlayMode gate for it: the "denied → granted → denied" sequence is verified by pressing
  real buttons and reading the real panel, because a demo made only of denials is easiest
  to fake with a label.

## [7.32.0] - 2026-09-06

### Added

- **MVP8 demo scene "Gameplay Services".** A single Lua script any Roblox developer will recognize:
  a door sliding up on a tween over two properties at once; a kill-brick found by tag and killing
  via `Touched`; a coin counted in `leaderstats`; a ray finding the floor; gravity that can be
  toggled before your eyes. The compatibility corpus proves these idioms execute, but
  a passing test cannot be shown — this scene is the same idioms, only with a body, and the claim "a
  Roblox developer's code works here" can be checked with your eyes, not by a test name.
- PlayMode gate for the demo itself: it presses the button the visitor would press and then looks at
  the world the tour was supposed to build. "The scene opens" is what the shared smoke proves, and
  it is compatible with a demo whose buttons do nothing.

### Fixed

- **NeoxiderTools has been cut out of CoreAI.** The demo build referenced `Neo.Tools.Move`, the showcase builder
  imported `Neo.Tools`, and the scene held its component. A dedicated three-line `CoreAiConstantRotator` was written,
  the reference removed, the scene regenerated. The demo is what gets opened first during a package evaluation, and
  a foreign component in it means either installing somebody else's product or a broken scene.
- The package-independence test holds this going forward: no CoreAI assembly references a foreign one, no
  `.cs` imports a foreign namespace, no demo scene stores a foreign component. A line
  in an asmdef is added in a minute and compiles here, where both products are on disk — and fails for the person
  evaluating CoreAI on its own.

## [7.31.0] - 2026-09-05

### Added

- **Intent gateway (MVP12).** A single place where a client's request to change the world is judged — because
  a second path means a second place to forget a check, and checks forgotten here stay invisible until
  somebody exploits them. The order is strict, and on any denial the world state
  stays untouched: the sender is taken from what the bridge stamped, not from a packet field;
  **an unrestricted actor is denied** — host writes are born in the server process and never travel as intents,
  so a forged host identifier can only lead to a denial, never to
  privilege escalation; the intent budget lives in a separate bucket (an intent costs a world mutation plus fan-out —
  far more expensive than a RemoteEvent call); size; a host grant for exactly this action and exactly this target;
  **a world ACL that the grant does not extend** — a client with rights to a Workspace subtree still cannot
  tear down a host-protected singleton or somebody else's owned instance; a mutation envelope making
  a retry idempotent and a stale revision a denial.
- Application is factored into a delegate, while value decoding lives one layer up: this way the whole check
  order is tested without a serializer, transport or scene — twelve gates, each step with
  its own negative twin.

## [7.30.0] - 2026-09-05

### Added

- **Filtered replication (MVP12).** `IReplicationFilter` and the default filter: `Workspace`,
  `ReplicatedStorage` and `Lighting` go out, while `ServerStorage` and `ServerScriptService` never do.
  The word "Server" in the name is the contract: a client that received them would hold the server's
  private contents on its own disk. An instance under none of the listed containers is not
  replicated — what nobody decided to show is safer left unshown.
  A client receiving the whole tree reads other people's private state and the answers to any world puzzles
  with no exploit at all — just by looking at what arrived; that is why the filter sits at the source, the single
  place a modified client cannot bypass.
- **`ReplicationDirtySet`.** A script that wrote five properties in a frame produces one delta per instance,
  not five packets with four stale views of the same object. Deletion always beats a same-step
  change: a client told "changed" but not "gone" would keep
  drawing something that no longer exists. Visibility is computed per recipient at publish time: two clients
  in one world see different things, and a shared batch would either leak to the narrower one or short-change the
  wider one.

## [7.29.0] - 2026-09-05

### Added

- **Client-write policy and mutation intent (MVP12).** `ClientWritePolicy` with exactly two
  values: `RobloxParity` (the write applies locally and is overwritten by the next server
  delta — Roblox's own behavior) and `Strict` (the write fails immediately, so the script breaks where
  the error is instead of silently rolling back). There is no `Open` value **and there never will be**:
  "apply locally and let it stand" is a client authoritative over the world — exactly what server-side
  replication exists to prevent. The absence is verified by a test: a third value would be reachable via settings,
  and settings get copied from tutorials.
- A write covered by a host grant is **not applied locally**; it leaves as an intent and waits for
  the authoritative answer. Applying and sending at the same time means predicting, and a prediction
  the server then rejects leaves the client with a world that never existed.
- `MutationIntent` carries **no actor, no owner, no role, no grant id, no
  capability** — and that is not an omission but the design: a field the client can set is a field the server must not
  take on trust, and a field that does not exist cannot be forgotten in a check. The bridge stamps the sender
  from its own connection map. `ExpectedRevision` makes a stale intent deniable,
  `OperationId` makes a retry harmless.

## [7.28.0] - 2026-09-05

### Added

- **Write-rights registry (MVP12).** `WriteGrantLedger` — what the host uses to let a specific client
  change the world: scope (a single instance / a subtree / the whole world), an action set
  (property, attribute, tag, reparent, creation, deletion), expiry and revocation. "May write" is not
  a property of a person but a permission for a target, granted by someone at some moment, and it can be taken away;
  a boolean flag on the actor would not answer "for what exactly", would never expire, and would leave nothing
  to examine when a damaged world needs explaining.
  Scope resolves against the **live** tree: a part moved out of a subtree stops being
  writable that very instant. Permission from the grant-time snapshot would leave the client with access to
  what the host already took back.
  **There is no host in the registry.** It holds all rights because its writes never enter the client
  path at all — they are born in the server process under an unrestricted context from composition. There is no
  "host" row to forge and no `IsHost` field on the wire; the absence of a special case is itself
  the security property. Only an unrestricted actor can grant and revoke, and such a context
  exists solely inside the server process — no remote message, no Lua, no intent can reach the registry.
  Every grant and revocation is written to the audit log: "who opened the door" is the first question asked.

## [7.27.0] - 2026-09-05

### Added

- **Topology and clock from the bridge (MVP11, N11.6).** `RbxBridgeRuntimeTopology` derives
  `RunService.IsServer`/`IsClient` from the transport, not from a separate setting: CoreAI already had
  a second answer to "am I the host?" in the AI authorization layer, and two independently configurable answers
  sooner or later diverge — and then the script hears one story while the command pipeline hears another. The bridge
  knows the truth because it is the thing connected to the other side.
  On the host `IsClient` answers **false**: in Roblox that is the truth inside a client-side execution
  context, and mods cannot declare a context in CoreAI yet — telling server Lua that it is
  a client is worse than the cautious answer, because it is a wrong answer that scripts branch on.
- **`workspace:GetServerTimeNow()` on the client adds the bridge offset.** Local clocks are the clocks of
  their own machine, and a player whose clock runs an hour ahead would otherwise disagree with the server about when
  things happened. On the server and on loopback the offset is zero, so solo behavior stays byte-for-byte identical.

## [7.26.0] - 2026-09-05

### Added

- **Admission/world linkage (MVP11).** `CoreAiMirrorSessionHost` stands between admission and the world:
  admission decides WHOM to let in, the world decides WHAT a player is, and joining the two in one class would
  give a security decision and a game object a shared lifetime — and a failure of exactly that ("a rejected
  connection still created a Player") is what MVP11 forbids. The order is explicit: admit, bind the
  connection, create the actor; nothing happens before admission returns. If the world refuses after
  an admission "yes", the binding is removed — otherwise the connection would stay bound to a player that does not
  exist, and it is exactly that binding the sender resolution trusts.
- **`IRbxActorIdentitySource`.** `Player.UserId` no longer has to be a session counter: the admitted
  identity comes from admission and is the same on every join. Without it, any script saving by
  `UserId` would write to a new key on every visit and silently lose player data. A host without a source
  (solo, loopback, tests) keeps the old counter — single-player keeps working with zero
  configuration.

## [7.25.0] - 2026-09-05

### Added

- **Mirror-bridge rule gates (MVP11).** Twelve checks on the receive path: a packet from an
  unadmitted connection reaches nowhere and is counted (separately for events and for requests —
  a rule honored in one handler and forgotten in another is only caught by a pair); the sender
  comes from the server map, not from the packet; a response closes a request only when both
  the connection and the correlation number match; a response arriving after a timeout is dropped, not
  applied — otherwise a script would get two results for one question, the second after it already
  gave up; a dropped link fails open calls instead of waiting forever; budget and packet-size
  overruns are refused before the wire.
  The receive paths take a connection id, not a Mirror object — which is exactly why the rules
  are verifiable without a live transport. What these gates do **not** prove and do not claim: that bytes
  travel over a real socket; that is a separate two-process run.

### Notes

- **Modularity verified by a run.** `Assets/Mirror` removed from the project entirely: 0 compile errors,
  `CoreAI.Net.Mirror.dll` not built at all, EditMode 3588/0 failed — exactly 19 fewer than with
  Mirror, and those are only the Mirror package's own gates. The whole Lua layer, mods and host work without it.
- Installing Mirror appends its defines to `ProjectSettings` (WebGL target). This was rolled back: had it
  landed in the repository, every consumer's WebGL would build with `MIRROR` but without Mirror itself — exactly
  the breakage the transport was extracted into a separate package to avoid.

## [7.24.0] - 2026-09-05

### Added

- **Mirror transport: admission and bridge (MVP11).** `CoreAiMirrorAuthenticator` decides admission BEFORE
  the connection is authenticated — the only moment when nothing has been created for the stranger yet:
  no `Player`, no chat session, no mod ownership, no world access. The decision lives in a
  separate `Decide` method because what matters here are the denials (provider not configured, provider
  threw, forged key), and a rule verifiable only through a live socket gets verified
  rarely and late. A provider that throws **does not** admit anyone: otherwise any bug in somebody else's
  authentication would become an open door. The client is only told "not admitted" — the detailed
  reason goes to the host log: telling which half of a forged key to fix is the one thing
  a denial must not do.
- **`MirrorNetworkBridge`.** The on-the-wire envelope carries **no actor id at all**: the sender
  is filled in from the bridge's own connection map, which only the admission adapter populates.
  The missing field is the protection — a field that exists but is ignored is one
  refactoring away from being trusted. A packet from an unadmitted connection is dropped and
  counted. A response closes a request only when BOTH the connection AND the correlation number match,
  so a foreign or replayed number closes nothing. Client traffic goes through
  the same shared limiter as the loopback bridge. The packet ceiling is read from the transport at runtime:
  KCP, websockets and the LAN transport never agreed on a size, and the unreliable channel is exactly the one
  carrying the gameplay burst.
- `InternalsVisibleTo` for the transport: `RbxNetworkRequestResponder` deliberately cannot be constructed
  publicly, so a mod cannot answer a request nobody asked. The first-party transport is
  exactly the caller that needs this, and the only one granted it.

### Notes

- The Mirror package gates run only where Mirror is installed. Without it, assemblies behind
  `defineConstraints: ["MIRROR"]` do not compile at all, while the solo manifest and the package-boundary test
  pass as before — and that is the verifiable half of N11.7.

## [7.23.0] - 2026-09-05

### Added

- **Bridge seam v2 and shared rate limiter (MVP11).** `INetworkBridge` gained
  `MaxPayloadBytes`, `ServerClockOffsetSeconds` and the `PeerDisconnected` event. The payload
  ceiling is asked of the bridge, not taken as a constant: the loopback bridge has no MTU, a real
  transport has one, and a message that passes in solo and silently vanishes on the network is the worst shape
  this seam can take. Disconnect is an event because CoreAI does not initiate it, while
  player teardown (PlayerRemoving, killing threads, freeing quotas) must run both when a client
  says goodbye and when the cable is cut.
- **`RbxNetworkRateLimiter`** was extracted from the loopback bridge into a shared type. The limit grew up inside one
  bridge, and the Mirror bridge, written later, could simply forget to call it — a transport without a budget facing
  a real network, exactly where a budget is needed. Now a new transport cannot lose it through
  forgetfulness: losing it requires deleting the dependency. The window is fixed, not sliding;
  clocks stepping backwards reopen the window — otherwise a host that corrected its system clock would deny
  all clients until time catches up, looking like a dead server.

## [7.22.0] - 2026-09-05

### Added

- **Tier-B compatibility corpus and the MVP8 bar (slice 8.7).** Ten pinned fixtures — not
  probes of individual APIs but whole gameplay idioms: kill-brick, coin pickup with a `leaderstats`
  score, a tweened door, a ground ray, damage over time, a tag spawner with `Debris`, save-on-player-exit,
  tween cancellation, attribute-driven config, low gravity. Each crosses three or four
  services: an API set can pass all twenty Tier-A lines and still fail to run
  a kill-brick — and that gap is exactly what Tier-B catches.
  The gate: **at least 60% of Tier-A + Tier-B run with zero source edits**, and a fixture that
  "passed" through a `pcall` around `NOT_IMPLEMENTED` does not count — the harness treats a stub hit
  as a failure. Negative twins: corrupted copies of three named fixtures must
  fail, each for its own reason.
- The corpus harness gained a connected server-side player and a controllable physics port. A Roblox server
  always has players, and the idioms the corpus measures are server code; an empty `Players` would measure
  the harness, not the API. Collisions for the kill-brick are injected manually: the headless corpus has no engine, and
  without this the most common Roblox idiom would stay unverified — while the fixture source
  remains exactly what a developer writes.

## [7.21.0] - 2026-09-05

### Added

- **`Humanoid` and a built-in character motor (MVP8, slice 8.6).** Health clamped to
  `[0, MaxHealth]`, `WalkSpeed` 16 studs/s, `JumpPower` 50, `JumpHeight` 7.2, `UseJumpPower`,
  `TakeDamage` (negative heals), `MoveTo`, `GetState`, `Humanoid.Jump = true`; `Died` signals
  (exactly once — the dead stay dead), `HealthChanged`, `MoveToFinished`, `Running`,
  `Jumping`, `FreeFalling`, `StateChanged`. The `MoveTo` timeout is eight seconds of **scaled**
  time: while paused the world cannot "give up" on the player's behalf.
  Movement is done by a motor behind the `IRbxCharacterMotor` interface; in the box — `UnityRbxCharacterMotor`.
  No CoreAI package had a controller of its own, and taking somebody else's would mean re-deriving every
  number of the metric contract from foreign settings — and none of them could then be
  asserted in a test. The PlayMode gate measures: 16 studs/s means 16 × 0.28 m/s ±2%.
- `Enum.HumanoidStateType` is registered **in full, per the mirror**, although the state machine only enters five
  states: an enum is a dictionary, and a script writing `Enum.HumanoidStateType.Seated` must
  fail on the `ChangeState` call, where it says what is missing — not on the enum lookup, which would read
  as "Roblox has no such state".

### Notes

- There is no passive regeneration in the class and there never will be: the mirror says directly that it is introduced by a **script**
  placed into the character, and it is disabled by an empty script named `Health`. Regeneration baked into the class
  would make this documented opt-out impossible.

## [7.20.0] - 2026-09-05

### Added

- **World physics (MVP8, slice 8.5): `workspace:Raycast`, `Workspace.Gravity`, `Touched`/`TouchEnded`.**
  A ray is origin + direction, **the direction length is the range**, and anything beyond 15,000 studs
  is rejected, not clamped: clamping would silently test a shorter ray than requested and return
  a miss the script cannot explain. `RaycastParams` carries a descendant filter,
  `Enum.RaycastFilterType` (exactly `Exclude`/`Include` — the mirror does not document the old
  `Blacklist`/`Whitelist`), `RespectCanCollide` and `AddToFilter`. `IgnoreWater` and `BruteForceAllSlow`
  are accepted and do nothing (no Terrain, a single broadphase) — they cannot change the answer;
  but a `CollisionGroup` other than `Default` is refused loudly, because a foreign group would change
  which parts are tested at all.
  Gravity is 196.2 studs/s² by default and applies **per body**: a world changing
  its gravity does not touch the host scene's `Physics.gravity` (DEV-6).
  `Touched`/`TouchEnded` fire on **both** parts and only from physical motion: a part
  moved by assigning `Position`/`CFrame` is not a touch in the mirror — nor here.
- **The mod package's first PlayMode folder** (`Assets/CoreAIMods/Tests/PlayMode/RbxApi/`) and a pump on
  `FixedUpdate`. A force applied on a render frame would be applied a varying number of times per
  simulated step — parts would fall at an FPS-dependent speed.
- **MVP11, admission port.** `IActorAdmissionProvider` with `ActorCredential`/`ActorAdmissionResult`:
  the result cannot be built in a dangerous shape — "admitted" without a context, with the host's unrestricted rights,
  or a denial without a reason are rejected in the constructor. There is no implementation in CoreAI and there never
  will be: "admit everybody" is exactly the hole the port exists for.
- **Seventh package `com.neoxider.coreaimirror`** (skeleton, `defineConstraints: ["MIRROR"]`) and a boundary
  test: only this package may reference Mirror, and no `.cs` outside it writes
  `using Mirror`. The check reads files, not loaded assemblies: an assembly that fails to compile
  is absent from the domain — and such a test would go silent exactly when it must scream.

### Fixed

- The `WorldRoot:Raycast` and `Workspace.Gravity` stubs were removed, and the "not yet implemented" probes moved from tests
  to live examples (`HumanoidStateType`, `RunService:BindToRenderStep`) rather than deleted:
  a test that fails on success is fixed by moving, not by cutting.

## [7.19.0] - 2026-09-05

### Added

- **`Players`/`Player` in full (MVP8, slice 8.3).** `GetPlayerByUserId`, `GetPlayerFromCharacter`,
  `Kick` (reason `CreatorKick`), `Name`/`DisplayName` via the `IRbxPlayerProfileProvider` port
  (a synthetic profile by default), the empty `Backpack`/`PlayerGui`/`PlayerScripts` containers
  Roblox creates on join. Everything beyond the slice — ban APIs, appearance loading,
  the social graph — stayed a loud stub marked "not planned": a silent `nil`
  instead of a platform answer is worse than an honest refusal.
- **`TweenService`, `TweenInfo` and animation enums (MVP8, slice 8.4).** A complete `Play`/`Pause`/`Cancel`
  state machine, 11 easing styles, `repeatCount`/`reverses`/`delayTime`,
  `Completed(PlaybackState)`, cancellation of the previous tween on a same-property conflict.
  The driver lives on the scheduler's `Heartbeat` and counts **scaled** time, so
  `timeScale = 0` freezes tweens exactly like `task.wait`. Engine classes stay unaware —
  property writes go through the `RbxTweenPropertyHost` seam.
- Compatibility corpus: `TAC-019-tween-create` moved from "failing" to **"works unedited"** —
  the canonical `TweenService:Create` + `TweenInfo.new` + `Completed` idiom now executes as written.

### Fixed

- **The `PlayerRemoving` handler could not read the leaving player.** The mirror describes this
  event directly as the place where player data is saved to a `GlobalDataStore` — with
  `player.UserId` as the key. Signals in CoreAI are deferred, so by the time the handler runs the `Player` is already
  destroyed, and only a "tombstone" allowed reading it — covering exactly three members
  (`Name`/`ClassName`/`Parent`). The canonical "save on exit" handler failed on its very first
  line, and since a callback error goes to the mod's error stream rather than outward, it looked like
  "the handler wrote nothing". Now the tombstone allows **any read** — but still only on the instance
  handed to the handler, and read-only: writes, method calls
  and reads of any other destroyed instance are refused as before.

## [7.18.0] - 2026-09-05

### Added

- **Value objects (MVP8, slice 8.1).** `IntValue`, `NumberValue`, `StringValue`, `BoolValue`,
  `ObjectValue`, `Vector3Value`, `CFrameValue`, `Color3Value` — with the `Value` property, the
  `Changed` signal and world-packet support. The last is not a trifle: the serializer **rejects** unknown
  classes, so without it any saved world with `leaderstats` would stop loading.
  The plan prescribed leaving three datatype classes as stubs, but the mirror documents all eight
  as full-fledged — all eight were built.
  `leaderstats` is absent from the mirror: it is **a Roblox convention, not an API**, and it is recorded
  in the class documentation as such, so the convention is not mistaken for a guarantee.
- **`CollectionService` (MVP8, slice 8.2).** `AddTag`, `RemoveTag`, `HasTag`, `GetTags`, `GetTagged`,
  `GetAllTags`, `TagAdded`/`TagRemoved` and per-instance signals — on top of the existing tag
  storage, with no second store. Pinned from the mirror with quotes: a repeated `AddTag` does not fire
  again, and already existing instances do **not** raise the event at subscribe time.

### Fixed

- **A value write moved the revision twice, and an empty write — once.** The setter itself moves the revision
  strictly on a real change, while the Lua binding called `RecordMutation` on top unconditionally.
  Only the second case was visible; nobody caught the double count on the normal path because there was no
  "exactly one" test — only checks that the revision does not change. The revision is responsible for denying
  stale writes and for marking changed objects during MVP12 replication, so a phantom
  increment would mean a broadcast empty update plus a denial of a write that conflicts with nothing.
- **Signal-argument marshalling handed Lua `nil`** for long, CFrame and Color3: `Changed` on three of the
  eight Value types would arrive empty.

## [7.17.0] - 2026-09-05

### Fixed

- **After switching to the fallback provider, memory silently stopped working.**
  `LlmPipelineInstaller.BuildSecondaryHttpClient` built the client with an empty store even though
  `memoryStore` was in scope. The role prompt kept promising `memory`, the model kept
  calling it, and every call was cut out without executing: the agent works, memory is never written, no errors.
- **The live-test harness hid the same problem.** The wrapper returned a client without a store under the
  WebGL target and dropped the allowed streaming settings everywhere else. Now, with a live backend and no
  rebuild path, it throws instead of returning quietly.

### Added

- **The "prompt — tools" contract is pinned by a test.** Every tool name the role's system
  prompt tells the model to call must be a tool CoreAI actually provides. The check looks
  only at call phrasings: the first version caught any snake_case and produced six false hits —
  an argument value, a sub-action, a **negative example** ("never call made-up
  `game_rules`") and payload field names. A check that screams at everything will be disabled.
- **The live scenario names the cause, not the symptom.** A failure used to look like "expected 40
  parts, got 0". Now a dedicated check catches "the model made zero calls" and directly
  distinguishes two identical-looking failures: a weak model wrote the call as prose, or the tools never
  reached the request — those are different investigations.
  **Prose-call parsing was deliberately not added:** a tool call is a protocol, and a guessing
  error would execute something the model never asked for.
- **Per-role MCP residency defaults.** `McpToolResidencyPolicies.Lean(...)`: permanently resident are
  `read_skill` and `memory` — without them the agent cannot even start looking for the rest — and a role pins
  its own with a single line, e.g. `Lean("execute_lua", "manage_mods")` for the programmer. The library
  default stays "everything native" so existing compositions do not change.

### Measured

- Live run on a real model (`qwen2.5-vl-3b-instruct`, LM Studio): **7 of 9**. The eighth is the model's
  limit: the 3B writes `execute_lua('...')` as prose instead of a call. The `ling-3.0-tiny` reasoning model is
  unfit for agent scenarios: asked to answer with a single word, it spent 387 of 411 tokens
  on thinking and never followed the instruction.

## [7.16.0] - 2026-09-05

### Fixed

- **Assistant replies were glued together — the stream had no message boundary.** One request produces
  SEVERAL replies: after each tool round the model speaks again, but it leaves outward as
  one continuous stream of chunks. The `AiOrchestrator` accumulator appended `chunk.Text` back-to-back, and
  the glue landed both in the role history and in `ApplyAiGameCommand`. In production it read as
  "Check yourself:**Turn over — waiting for the student's reply on the card.**" — a colon glued to a capital
  letter, two different messages in one line.
  The contract was fixed, not the display: the boundary cannot be guessed from punctuation — a reply may well end in
  a colon and may well start with a lowercase letter, so any heuristic is wrong in both directions and
  makes the defect unreproducible.

### Added

- **`LlmStreamChunk.StartsNewMessage`** — a flag meaning "this chunk starts the NEXT reply of the same
  stream". `MeaiLlmClient` sets it EXACTLY on the first visible chunk of every tool-loop iteration,
  except the very first one (and before the tool-free final turn). `AiOrchestrator` uses it — and only
  it — to insert an empty line into the accumulator, appending exactly what is missing: a reply that already
  ended with a paragraph gets no extra blank line. A chunk without the flag behaves exactly as
  before, so consumers unaware of the field lose nothing.

- **`StreamedMessageJoiner`** — the single owner of the reply-splitting rule. There are three accumulators:
  the orchestrator (history and `ApplyAiGameCommand`), the chat panel (full answer text) and consumers outside
  CoreAI. While the rule lived as a copy in each, the copies drifted apart: two treated a whitespace-only accumulator
  as "already split", the third appended a blank line to it. Such a divergence does not fail
  a test — it silently changes what the student reads. Now `AiOrchestrator` and `CoreAiChatPanel` call
  one helper, and external consumers receive it as part of the stream's public contract:
  `Docs/STREAMING_ARCHITECTURE.md` shows it in the example instead of `label.text += chunk.Text`.

## [7.15.0] - 2026-09-05

### Fixed

- **The memory gate measured the garbage collector, not the program — and failed everything, including twenty actors.**
  `heapSlopeMegabytesPerMinute` was built from `GC.GetTotalMemory(false)` readings, i.e. live
  objects **plus not-yet-collected garbage**. A collection landing inside the window collapsed the value, and the slope
  came out negative: three identical runs at one hundred actors gave −45.6, −70.4 and −93.3 MB/min against
  a +1 budget. The noise exceeded the threshold 3–48x, and the sign was unstable.
  The gate now stands on two reproducible numbers: retained memory (both window ends taken
  after a full collection with finalizer drain — 0.13 MB spread) and allocations per actor per frame
  (**0.00 bytes** spread). The slope remains in the report for reference and decides nothing anymore.
- **Chat intake denied a hundred actors because of a 64-slot default.** `MaxPending` is a hard
  refusal, not backpressure: an actor past the limit is turned away before any work. On a synchronous
  burst of one hundred actors, 96 of 600 requests were refused.

### Added

- **`AiOrchestrationQueueOptions.ForActorCount(n)`** — queue size and lane count are derived from the
  expected actor count instead of staying a small-session constant. Plain construction
  without this method is unchanged.
- **`Debris:AddItem` — the first slice of MVP2.5 (MVP8).** The service stopped being a stub: `AddItem(item,
  lifetime = 10)`, a hard cap of 1000 objects with instant eviction of the oldest, a rights
  check **at call time** and again at fire time — if ownership changed after scheduling,
  the destruction is declined, one line is logged, the canonical state does not change.
  The caller's identity comes from the trusted `ActorContext`, not from a Lua argument.
  The semantics were checked against the local mirror of the Roblox docs: lifetime there is a **maximum**, not an exact
  duration, and the one-thousand cap is pinned with a quote.

### Measured

- **Two hundred actors pass the 60 Hz frame budget, the memory gate and the chat gate**: 400 requests offered,
  400 served, zero refusals, p95 end-to-end latency 1346 ms against a 5000 budget. Before these fixes not a
  single ladder rung passed, including twenty actors.
  The 4 ms budget at two hundred still does not hold (median 7.37 ms) — that is a 240 Hz-level goal.
  **This is the CoreAI limit, not a deployed system's**: the rig runs a fake provider with a 100 ms delay,
  while measurement on a real one showed p95 17.4–38.5 s on a single lane. No player-count claims may be made
  publicly from these numbers. Analysis — `dev-docs/CAPACITY_UNBLOCKED_2026-09-05.md`.

## [7.14.0] - 2026-09-05

### Added

- **A multi-document skill reads in parts.** `read_skill(name)` now returns the **entry
  document plus a table of contents** for the rest, and `read_skill(name, section)` brings a single section. A skill of
  five documents no longer arrives as one lump for the sake of one paragraph. A skill from a **single** source
  returns exactly as before — no table of contents, no section marker.
  This is the third disclosure level, as in harnesses like Claude Code, but with one deliberate difference:
  there the agent follows a `references/*.md` link with its own file tool, while a CoreAI agent has no file
  system — a link would be dead text, so the second level was made an argument.
  Tool schemas are served on **every** level: a reader that took a single section must still know
  what it may call.
- **MCP tools can be made dynamic instead of permanently resident.** Every tool
  is now either `Native` (as before, in `tools/list`) or `Dynamic` — not in the list, reachable via the
  `coreai_tools` broker (`list` / `describe` / `call`). Two override channels: the host policy
  (a function) and the `COREAI_MCP_NATIVE` / `COREAI_MCP_DYNAMIC` variables. Priority: explicit NATIVE >
  explicit DYNAMIC > host policy > default.
  **The default is unchanged**: a composition configuring nothing behaves as before.
  Measured on a real composition: `tools/list` — **9,324 bytes vs 768** with all dynamic,
  down 91.8%. Hiding from the list saves context; it is **not access control**: a tool
  called by name directly still works.

### Fixed

- **Half the per-frame garbage in the signal phase.** Every signal firing created a new stream
  record: at twenty actors that is 1200 creations per second for handlers that never
  yield. Records of finished handlers are now returned to a pool with a full reset of
  every field — one mod's state cannot leak into another. A failed handler's record is
  **not** returned to the pool, and the generation counter deliberately keeps growing: otherwise a stale
  timeout record could wake somebody else's tenant. The pool does not count toward the emergency thread cap.
  Measured: 103.1 → 51.3 KB per frame at 20 actors, 263.6 → 132.0 at 50.

## [7.13.0] - 2026-09-04

### Added

- **Clocks, and time can be overridden.** `time()`, `os.time()`, `os.clock()`, `tick()` and
  `workspace:GetServerTimeNow()` arrived with Roblox names and semantics, but take their values
  not from the system clock directly but through the `IRbxClockSource` port. A game plugs in its own source
  with a single constructor line — accelerated days, deterministic replays, server clocks.
  The side benefit matters more than the feature itself: `GetServerTimeNow` monotonicity is now verified by a test
  that hands it a backwards-stepping source instead of moving the machine clock.
  `os` here is **not** the standard library: the sandbox holds a table of exactly `time` and `clock`,
  verified by pair count, while `execute`/`remove`/`rename`/`exit`/`getenv`/`tmpname` stay nil.
  `tick()` is marked legacy and warns once per mod, not per call.
- **`RunService` topology queries.** `IsServer`, `IsClient`, `IsStudio`, `IsRunning` are no longer
  loud stubs and answer through a swappable `IRbxRuntimeTopology` — MVP11 will swap the source without
  touching the Lua binding. `BindToRenderStep`/`UnbindFromRenderStep` remain stubs: that is a binding
  to the render step, not topology.
  `IsClient()` returns **false**, and that is not a simplification: per the Roblox documentation `IsClient` describes
  a script context, not a session, and CoreAI mods are server-side. The original task assumed
  the opposite and was corrected against the documentation mirror.

### Changed

- **The roadmap was brought back to the MVP2 truth.** It claimed the clocks, the topology and the Tier-A corpus
  gate "remain". The gate stood and was verified all along (30% threshold, actually three times higher);
  clocks and topology now exist. Also recorded: the G10 capacity gate cannot be closed by code — with a provider p95 of
  17.4–38.5 s and a single lane, forty requests cannot fit into 60 seconds under any
  implementation.

## [7.12.0] - 2026-09-04

### Added

- **Guard observability seam — groundwork for the MVP2 frame gate.** The acceptance manifest
  recorded that the frame budget cannot be honestly measured: the production path exposed zero counters,
  and reading a private field via reflection means bypassing the very path being measured — such a number
  is worth nothing. Now `LuaCsExecutionGuard` takes an `ILuaCsGuardObserver` through the plain
  constructor (and through `LuaCsScriptEngine`, i.e. via the production composition), and after every
  guarded execution hands over a `LuaCsGuardExecutionRecord`: steps charged, ticks, whether execution ran
  to completion and which budget tripped — steps, timeout or memory. Without an observer the path does not change
  at all: one field read and one null check, no allocations.
  The trip kind is written by the hook itself at throw time, not by parsing the error text — a forged `error()` from a mod
  cannot swap the classification.
- **The seam gives a lower bound, not an exact counter, and that is pinned by a test.** The hook fires once per
  `HookInstructionBatch` instructions and charges the whole batch, so a body shorter than the batch honestly
  reports zero. `ShortBodyBelowTheHookBatch_ReportsZeroSteps` pins this as the contract: anyone
  deriving a frame budget from these records must treat `Steps` as a batch-granular lower bound.
- **Regression test for telling cancellations apart from provider failures.** The 2026-09-01 G10 measurement showed
  23 provider failures that were absent from the LM Studio log: those were our own cancellations, which
  `MeaiLlmClient` reports as an unsuccessful result with `ErrorCode = Cancelled`, while the measuring client counted
  any unsuccessful result as a backend failure. The code was fixed back then, but without a test — and every
  capacity conclusion stands on that counter. `G10CancellationClassificationEditModeTests` closes all
  four boundary outcomes, including the negative twin (a genuine backend error never becomes a
  cancellation) and an `Ok` with an empty body that would otherwise inflate the served share.

## [7.11.0] - 2026-09-04

### Fixed

- **Replacing a role's tool list silently disabled all of its skills.** `SetToolsForRole` returned
  `read_skill` and `call_skill_tool` only if the role had a live catalog registered, which appears
  only with skill authorship enabled. For an agent built with a plain `WithSkill(...)`, both proxies
  simply vanished, and no skill tool could be called anymore. There was almost nothing to notice it by:
  `call_skill_tool` answers a missing tool with a plain result, not an exception, so the model
  read "not found", apologized in prose and moved on — from outside only the missing action was visible.
  Now the proxies survive replacement regardless of the catalog
  (`ReplacingARolesTools_KeepsTheSkillMetaToolsReachable`).

### Added

- **A skill can consist of several files.** `SkillSet.FromFiles` and `SkillSet.FromTextParts`
  assemble instructions from multiple sources in order, each under its own
  `## file-name` heading, empty parts skipped. The inspector `SkillSetAsset` gained a list of
  extra `TextAsset` entries — the surface people work with without code. The join rule is one
  for both paths (`SkillSet.JoinInstructionParts`), and the test requires the asset and `FromTextParts` to yield
  an identical document: otherwise the same skill would read differently depending on which path
  built it.
  A skill from a **single** file keeps its text byte-for-byte and gets no heading — otherwise every
  existing asset's instructions would have been silently rewritten.
- **The tool-availability contract is pinned by tests.** A skill tool is callable at any moment; only
  the instructions are deferred. Verified: a tool works without a single `read_skill`, reading one skill
  does not block another skill's tools, the catalog in the system prompt carries only names and descriptions without
  instruction bodies, and a skill added after the proxy was built (the model wrote it for itself)
  is callable immediately.

## [7.10.0] - 2026-09-04

### Added

- **A framework game can define its own materials and swap them live — `MaterialVariant`.**
  Forty-five `Enum.Material` values were the ceiling: there was no way to add your own material or swap one
  on a part mid-game. Now the Roblox mechanism works: a `MaterialVariant` instance in
  `MaterialService` with `BaseMaterial`, `ColorMap`, `NormalMap`, `RoughnessMap`,
  `MetalnessMap`, `StudsPerTile`, and a part picks it via the `BasePart.MaterialVariant` string.
  No custom API and no new `Enum.Material` values — the same script runs in
  Roblox. Map paths are plain project `Resources` paths, so "your own texture set" means dropping
  files in and naming them from Lua. An unset map inherits from the base material, so a variant
  that only changes color takes three lines.
- **Editing a live variant repaints the parts wearing it.** Changes to maps, `BaseMaterial` or
  `StudsPerTile` reach every part wearing the variant; same on rename,
  deletion and reparenting of the variant itself. The shared `Material` is edited in place, not reallocated,
  so no part needs touching.
- **Variants survive world saves.** Both the `MaterialVariant` itself and the part's reference to it travel
  in the world packet; the field is optional, so packets written earlier read unchanged
  (`ReadPackage_WorldJsonWithoutMaterialVariantKeys_DeserializesWithNullVariant`). A packet where a part
  references a nonexistent variant, or where a variant has a dishonest `BaseMaterial`, is rejected with
  a clear error.
- **A live render proof instead of trusting the mock.** Rendering EditMode tests run a fake texture
  loader — that verifies the wiring, not the pixels. `RbxMaterialVariantRenderPlayModeTests`
  photographs three slabs (plain Brick, Brick with a grass-map variant, plain Grass) and requires
  the variant part to leave brick behind and stand next to grass. Measured: brick (155,119,106),
  variant (76,94,62), grass (94,106,61). Snapshot — `artifacts/materialvariant-render.png`.

### Changed

- **An unknown variant name is not an error.** A part naming a nonexistent variant renders with
  its plain `Material`; diagnostic magenta remains only for a material missing from the
  catalog entirely.
- **The packaged-set documentation was brought back to the truth.** `RBX_API.md` and `TEXTURE_MATERIALS.md`
  still claimed the package holds six CC0 sets, while since 7.9.0 there are thirty-six.

## [7.9.0] - 2026-09-04

### Added

- **The package includes all 36 CC0 sets, not six.** The catalog shipped inside the package described
  six materials, while the project override described all thirty-six; so for a consumer who
  did not import the sets themselves, thirty of thirty-six materials fell back to the procedural shader.
  Now `Resources/CoreAIRbxTextures` holds all sets in 1K (Color, NormalGL, Roughness and Metalness
  where the source provides it), and `RbxMaterialTextureCatalog.asset` describes them all.
  Verified like this: the project override was removed from the project entirely, after which all 36 materials were
  photographed again — the picture did not change, meaning the package really serves them, not an ignored folder.
- **The `CoreAI/Materials/Rebuild packaged catalog from packaged textures` command.** Rebuilds the
  packaged catalog from whatever lies in the folder, applying the same surface profiles. Adding a
  set is now two steps: drop the maps in and run the command.
- **A guard against runtime-list vs catalog drift.** `PackagedTexturedMaterialIds` is a handwritten
  list next to a generated asset, and such a pair already drifted apart silently in this repository.
  `PackagedTexturedCatalog_MatchesRuntimeTexturedListExactly` requires the "name → value" pairs to match
  exactly in both directions.

### Changed

- `RbxMaterialCatalogEditorUtility.MergeCatalogAt` takes a path: the same merge now builds
  both catalogs — the project override and the packaged one.
- Materials that previously had no packaged set (`Concrete`, `DiamondPlate` and the other
  thirty) take the textured path instead of the procedural one. The procedural implementation is kept for all of
  them as a fallback: the "every textured material has a procedural twin" contract
  is still verified by a test.

### Known cost

The packaged texture folder grew from 15 MB to 113 MB on disk; resident after load — about 99 MB
versus ~15 MB before (compressed BC formats with a mip chain). Git LFS was deliberately not used: the package
installs via a git URL through UPM, and a consumer without LFS installed would receive
pointer files instead of textures, i.e. materials would break silently.

`RbxTextureMaterialProvider` creates one shared material per catalog entry on the first created
part, and the catalog holds direct texture references, so these ~99 MB are paid regardless of how many
materials a scene actually uses. Storing paths instead of direct references and loading a texture
on first use of a material would make the cost proportional to the scene — recorded as TODO in
`dev-docs/MATERIAL_DEFECT_AUDIT_2026-09-04.md` and worth doing before serious WebGL work.

## [7.8.0] - 2026-09-04

### Fixed

- **Rbx parts received no direct light at all.** The project renders Forward+
  (`Assets/Settings/PC_Renderer.asset`, `m_RenderingMode: 2`), and URP 17 in this mode serves the main
  light through the clustered light loop — the pass must declare
  `#pragma multi_compile _ _CLUSTER_LIGHT_LOOP`, as the URP package's own `Lit.shader` does.
  `RbxTexturedSurface`, `RbxProceduralSurface` and `RbxProceduralTransparent` lacked this line,
  so the non-clustered variant compiled and `UniversalFragmentPBR` never saw the sun. Everything
  was lit by ambient only — no sun and no cast shadows, in the editor and in the player. The bug
  looked like a "flat picture", not a failure, which is why it went unnoticed for so long.
  Slab measurement before and after: Sand `77,67,55` → `227,177,120`, Grass `44,53,41` → `125,135,69`.
  The regression is closed by `RbxShaderClusterLightLoopEditModeTests`, which parses every shader
  into `Pass` blocks and requires the keyword in each pass calling the URP lighting entry point.
- **`AdoptWorldObject` returned a wrong `Size` under a scaled ancestor.** It read
  `transform.localScale`, which ignores the ancestor scale, so an adopted part inside
  a part scaled via `Size` reported a size the user does not see.
  Now `lossyScale` is used; covered by `AdoptWorldObjectScaleEditModeTests`.
- **`Grass` shipped as a defective texture.** `Grass005` is a flat green felt: albedo spread
  8.5 with a normal map too weak to draw blades. Replaced with `Grass004`, tiling
  lowered from 7 to 4.5 studs (at 7 the blades shrank below a screen pixel).

### Changed

- **The CC0 material catalog was rebuilt: sixteen of thirty-six sets replaced.** Each
  `Enum.Material` was photographed in a separate frame on three shapes (slab, cylinder, sphere) and inspected; the verdict
  rested both on source measurement (albedo spread and normal-map deviation) and on the render itself.
  Worst cases: `Leather037` with an albedo spread of 0.37 out of 255 — effectively a flat fill;
  `Fabric081C` at 0.77; `Snow015` — white mossy stone instead of snow; `RoofingTiles013A` — a grid of
  squares instead of roof tiles. Tile widths and normal strengths were retuned together with the replacements: fine
  grain at 13–16-stud tiling drops below a screen pixel, and that was half the reason for
  the "flat" look. Details — `dev-docs/MATERIAL_DEFECT_AUDIT_2026-09-04.md`.
- **A single "material → CC0 set" table.** The mapping lived in two places — the
  ambientCG loader and the local-catalog importer — and silently diverged when the defective sets were replaced
  in only one of them. Both sides now read `RbxCc0TextureSets.Sets`; the divergence is closed
  by `RbxCc0TextureSetsEditModeTests`.
- **World Lua bindings moved to studs.** `coreai_world_pos` and `coreai_world_raycast` gave and
  took raw Unity meters, while the whole Rbx API works in studs — a 1/0.28 ≈ 3.57x
  discrepancy when mixed with `Part.Position`. Covered by `WorldBindingsStudUnitsEditModeTests`.

- **The request allowlist that cut off all skill entries no longer stays silent.** The skill catalog lives in a
  cacheable system prefix and keeps telling the model to go through `call_skill_tool`. If
  a specific request's allowlist names neither the meta-tools nor the skill's own tools,
  the model obeyed the prefix, got "Unknown tool" and wasted the turn — while a human only saw
  that nothing happened. The request still stays exactly as the host built it, but
  the contradiction now lands in the log immediately instead of surfacing on a live run.

### Added

- **The `CoreAI/Materials/Apply import policy to packaged textures` command.** The packaged texture metas
  live in the repository and were correct only because someone once set them by hand; a new map
  arrived with Unity defaults — roughness as sRGB, normal map as a color texture.
  The command runs the packaged folder through the same import policy as the local catalog.
- **Packaged-texture import settings brought under one policy.** All sixteen maps in
  `Resources/CoreAIRbxTextures` were run through the same policy as the local catalog: anisotropy 8,
  an explicit 4096 ceiling and a WebGL override at 1024. Previously there was no platform block at all, so
  the WebGL build shipped full size.

## [7.7.0] - 2026-09-04

Version aligned with `com.neoxider.coreaiunity` 7.7.0 (feed snap-back after a human action
in the feed itself — a Unity-layer change). No core changes of its own in this release.

## [7.6.0] - 2026-09-03

Version aligned with `com.neoxider.coreaiunity` 7.6.0 (request-cancellation policy on panel disable —
a Unity-layer change). No core changes of its own in this release.

## [7.5.0] - 2026-09-03

> Numbers 7.3.0 and 7.4.0 are taken by chat-feed-mode releases on `main`; this
> branch's work, previously tagged 7.3.0 and 7.3.1, was never published outward and is included
> in 7.5.0 in full.

### Added

- **Material surface profiles `RbxMaterialSurfaceProfiles`** — per-`Enum.Material` tile width in
  studs, normal strength, roughness multiplier and `Part.Color` influence. The catalog importers
  (`RbxMegascansCatalogImporter`, `RbxAmbientCgCatalogDownloader`) stamp them
  automatically. The `CoreAI/Materials/Retune surface profiles (tiling + relief)` menu command
  reapplies the table to already built catalogs (packaged and project override).

### Changed

- **Tiling and relief are no longer identical across materials.** The importer used to leave every entry
  at the default 8 studs per tile and normal strength 1, so courtyard cobblestone, a brick wall and grass
  repeated at the same frequency and the whole scene read as flat. Now stone and soil get
  a large tile (Rock 18, Ground 16, Cobblestone 14), masonry a medium one (Brick and Slate 10, Limestone and
  Granite 12), metal and fabric a small one (Metal 3.5, Foil 3, Leather 3.5, Fabric 4); relief is boosted where
  it exists (Cobblestone 1.5, DiamondPlate 1.5, CrackedLava 1.5, Rock 1.4) and muted on smooth surfaces
  (Marble 0.55, Plaster 0.8, Metal 0.85). `Part.Color` influence also became a material property:
  metal and basalt tint weakly (0.45–0.55) so color does not eat the texture, fabric, carpet and plastic
  — almost fully (0.85–0.95), `Neon` and `ForceField` entirely. The manual tuning of the packaged
  CC0 catalog (Wood 10, WoodPlanks 8, Brick 10, Cobblestone 14, Metal 3.5, Grass 7 tiles; their
  roughness 0.68–0.82 and color influence 0.45–0.7) is kept as the table's anchors and pinned by
  `RbxMaterialSurfaceProfilesEditModeTests` — only normal strength changed in the packaged catalog.

### Included in 7.5.0: work called 7.3.1 on the branch (2026-09-02)

### Fixed

- **A model turn with a native tool-call no longer hangs forever in the WebGL player after
  `execute_lua` / `manage_mods`.** Reproduced in the browser on the 7.3.0 player via a scripted
  G11 proxy response (`POST /control/script`): the tool executed (`SUM_PROBE x=4` in the console),
  but the trace broke off at `[ToolPolicy] execute_lua: step=invoke-started async` — no result,
  no second model request, "Processing…" until manual cancel. Same cause as in 7.0.5, only one
  floor down: on the `LuaTool` → `LuaCsGameToolExecutor` → `ConfirmedWorldMutationGate` path the body
  genuinely suspends (the pre-mutation autosave goes through `UniTask.Yield`), and the continuations
  after it were written with `ConfigureAwait(false)` and left for a nonexistent thread pool.
  All seven `ConfigureAwait(false)` in `LuaTool`, `LuaCsGameToolExecutor` and `DelegateLlmTool` were removed.
  That alone is not enough: the first continuation after the tool body belongs to MEAI
  (`AIFunctionFactory` awaits the delegate task with `ConfigureAwait(false)` inside the binary). The new
  `MeaiToolTaskBridge.Publish(task)` completes the task handed to MEAI with the
  `SynchronizationContext` cleared, and the MEAI continuation runs inline on the same call stack;
  `execute_lua` and `manage_mods` publish their result through the bridge. Host async delegates in
  `DelegateLlmTool` must do the same (`Docs/MEAI_TOOL_CALLING.md` §3.2).
  Tests: `MeaiToolTaskBridgeEditModeTests` (inline continuation under a derived context,
  a control test "without the bridge the continuation leaves the thread", exceptions, cancellation) and
  `LuaToolWebGlPublishEditModeTests` (an MEAI call of `execute_lua` completes on the body-completion stack —
  failed before the fix). The rebuilt player was verified in the browser with the same scripted
  tool-call: result delivered, second request sent, turn finished.
- **`HttpService` in a one-off `execute_lua` returns the host's configured refusal, not a
  wait-bridge error.** Policy / security / limit refusals resolve synchronously in
  `_prepareHttpRequest` and are raised by the Lua bridge in any execution context, including chunks without
  a scheduler (mods' threads are the only ones with `task._signalWaitBridge`). A one-off chunk used to
  get "Wait bridge is unavailable" instead of "HttpService policy refused actor …".
  Test: `HttpServiceOneOffRefusalEditModeTests` (failed before the fix).

### Added

- Step-by-step tool-call trace under `LogMeaiToolCallingSteps`: `[ToolPolicy] <tool>:
  step=invoke-started sync|async` / `raced invoke|timeout` / `result-awaited` /
  `streamed-sequential-done`; `LuaTool` under `LogToolCalls` writes `executor returned success=…`.
  This exact trace localized the hang above.
- `tools/G11Proxy`: scripted responses (`POST /control/script` — a `text` / `tool_call` queue,
  served as SSE) and request capture (`GET /control/requests`), so native
  tool-calling can be verified in the browser deterministically, without a model (28 unittests).

### Included in 7.5.0: work called 7.3.0 on the branch (2026-09-02)

MVP3 persistence (in-project — MVP2.5): the world packet, autosaves before every AI mutation,
safe session replacement, all 45 `Enum.Material` with independent `Part.Color`, honest WebGL
persistence. Verified on stamp 7.3.0: batchmode EditMode 3272 / 3263 passed / 0 failed /
9 skipped per plan (`artifacts/testresults/g12.xml`); PlayMode on `ling-3.0-tiny` 114 / 110 / 0 / 4
skipped per plan; Node jslib tests 6/6 and SSE 12/12; browser acceptance G11 — PASS on the built
WebGL player (`dev-docs/G11_RUN_RECORD_2026-09-02.md`), including 45 materials via the public API,
IDBFS persistence and retry/terminal-error/chat-recovery; independent final QA —
`dev-docs/FINAL_QA_2026-09-02.md` (left open: chat throughput G10, heap budget and the
host-recovery envelope).

### Added

- **The `.world` world packet** — a versioned ZIP (`manifest.json`, `world.json`, `Mods/NNNN/`):
  an engine-free codec, stable server-authority ids, hostile-input quotas, projection of
  world-owned state only, `FileRbxWorldPackageStore` with create-once manual slots and an autosave
  ring with two-phase durability, `ConfirmedWorldMutationGate` before `execute_lua` and every
  mutating `manage_mods`, `RbxWorldRuntimeSessionController` with zero-await session replacement and
  rollback. Tools `save_world` / `load_world` (loading only via host confirmation or the
  Hub page → World Loads). Document: `Docs/CoreAIMods/WORLD_PACKAGE.md`.
- **All 45 `Enum.Material` have a runtime mapping**: six hybrid entries on ambientCG CC0 textures
  (Brick, Wood, WoodPlanks, Grass, Cobblestone, Metal) with seamless object box-projection,
  the rest procedural; an invalid id yields a visible magenta fallback, not Unity's pink
  shader. `Part.Color` tints the material via `MaterialPropertyBlock`, as in Roblox; Neon without
  its own palette — its glow equals `Part.Color`. The `ProceduralMaterialsShowcase` judging rig
  counts each material once (46 slots = 45 + fallback).
- A recipient-less `RemoteFunction` completes after 30 scheduler seconds with a refusal-style error
  — a documented difference from Roblox (there the call may hang forever).
- `BasePart.Material` / `Part.Color`, "Saving and loading a world" and `Archivable`-based rejection sections
  in `Assets/CoreAI/Docs/RBX_API.md`; the `RbxApi.txt` skill synced with the code.
- `tools/G11Proxy` — an LM Studio proxy with 503 injection / blocking / delay for browser
  acceptance G11 (21 unittests).
- **The `RbxMaterialTextureCatalog` texture-material catalog** — `RbxTextureMaterialProvider`
  no longer holds a hardcoded table: any of the 45 `Enum.Material` renders with textures if the
  catalog has an entry (albedo, normal with OpenGL/DirectX flag, roughness or smoothness, metalness
  and AO as desired, tile width in studs, own color, `Part.Color` influence, normal strength).
  The packaged catalog from `Resources/CoreAIRbxTextures` is supplemented by a local
  `Assets/CoreAIRbxTexturesLocal/Resources/CoreAIRbxTextureCatalogOverride.asset` (in `.gitignore`),
  an override entry wins per material. The `RbxTexturedSurface` shader gained AO and a
  DirectX-normal flip; box projection and the 0.10 blend band unchanged.
- The `CoreAI/Materials/Import Bridge-Megascans folder...` menu (Quixel Bridge / Fab export folder;
  DirectX normals recognized via json) and `CoreAI/Materials/Download CC0 texture sets (ambientCG)...`
  (36 verified ids, 1K/2K/4K, import settings: sRGB for albedo only, normal type, max 4096, 1024 for
  WebGL, `LICENSE.md` with provenance). Megascans are used only locally in the owner's project:
  Fab Standard License §6(b)(iii) forbids redistribution inside the package.
  Document: `Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md`.
- The Hub → World Loads page shows the autosave ring (name, trigger, local time, size),
  refreshes on open and every second, and the `Load...` action goes through the same confirmation
  pool as manual slots — no direct apply and no confirmation bypass; the request
  receives the current trusted Programmer actor from the binder.
- The `list_autosaves` and `load_autosave` tools: the AI and the host see the autosave ring (name,
  trigger, time, size) and request loading a named autosave through the same
  confirmable pool as manual slots — no confirmation bypass.
- Before a confirmed world load, a safety `load_world-pre` autosave of the current world is written
  with the same confirmed-durability rule; if it fails, the load is declined and the live world is
  untouched.
- `tools/ScaleHarness` — a reproducible 20/50/100/200-actor ladder through the combat composition
  (orchestrator, `ModScheduler`, one Lua mod per actor, loopback remotes, ACL/quotas) with a frozen
  load and a `dev-docs/SCALE_CHARACTERIZATION.md` report: on the host, 100 actors fit into a
  4 ms frame and 200 into 16 ms, but the chat gate starts refusing at 100 actors, and ~4.5 KB/actor/frame
  of allocations fail the heap budget already at 20 — both defects in progress, no "200 players" claim.

### Fixed

- A bare `Instance.new('Part')` writes the default Part state to the sink on creation; previously
  the very first such Part via `execute_lua` permanently blocked all gated AI tools and
  `save_world`, because capture refused on a Part with no recorded state.
- A headless session restores camera and Part state and can load its own save;
  a camera-less scene stages a `PublishableCameraRig`.
- Autosave rotation on a backwards system-clock step no longer deletes the just-confirmed
  backup nor returns `Success` with a deleted file's path.
- Textured-material tiling is recomputed when `MetersPerStud` changes from the world packet;
  previously the scale was "baked" on first touch and textures grew 1.79x denser.
- The Hub → World page reports `Has saved state` only from the `FS.syncfs` callback, not at request
  issue time; the WebGL `LlmUnity` guard no longer scans every `MonoBehaviour` each frame.
- In the WebGL composition the container builds without `ILlmAgentProvider`, hot-swap resolves the provider
  optionally, and the in-browser local model returns a documented limitation.
- The WebGL branch of `FileRbxWorldPackageStore` (chunked-write budget) did not compile in the player
  build: registry types were used without using-directives the editor and EditMode never required.
  Added `tools/webgl_define_check.py` — compiling assemblies with WebGL defines without a full player
  build, so such errors are caught before the batch build.
- **The dead LLM-client retry in the browser (found in G11 acceptance).** After a single 503 on stream open,
  `MeaiOpenAiChatClient` wrote "retrying after 2000ms" and never sent a request again:
  the retry pause, polling and outer read timeout went through `Task.Delay`, which has no timer in single-threaded
  WebGL — chat hung on the typing indicator with no terminal error. Now all client delays
  go through `ILlmAsyncMarshaler.DelayAsync` (constructor injection or the host
  `MeaiOpenAiChatClient.DefaultAsyncMarshaler` set by the Unity installer); `Task.Delay`
  remains only as a portable fallback. The `CoreAI.Core` assembly was outside the CAIU001 analyzer scope, so
  the defect held; pinned by `MeaiOpenAiChatClientWebGlDelayEditModeTests` and a refined allowlist
  of the unsafe-primitives guard.
- `ConfigureAwait(false)` removed from WebGL-reachable paths (mod HTTP, client registry);
  LLM retries and timeouts go through `ILlmAsyncMarshaler.DelayAsync` (PlayerLoop).
- World-packet capture no longer refuses on a world-owned `Model` whose `PrimaryPart` points at a
  mod-owned part: the reference is reset in the snapshot only, as in Roblox on save, and the manifest
  gains a versioned `diagnostics` array; previously one such Model permanently blocked
  gated `execute_lua`, and the AI could not fix it. Old packets read unchanged.
- Slot and autosave names reject reserved Windows device names (`CON`, `PRN`,
  `AUX`, `NUL`, `COM1-9`, `LPT1-9`, with or without extension, case-insensitive).
- The ambientCG loader table: 20 ids pointed at the wrong surface (`Sandstone=Rock035` — black
  cave rock, `DiamondPlate=MetalPlates006` — decorative panels, etc.); replaced with
  API-verified ids, `Foil` added. After a visual pass over all 46 rig slots
  (front and grazing angle, 2K): `Slate` → `Rock022` (layered slate instead of mossy stone),
  `Rock` → `Rock028` added, `Salt` returned to procedural (ambientCG has no salt; concrete did not
  read as salt). Total: 36 texture sets, 9 procedural, no seams and no pink shaders.
- The ambientCG provenance `LICENSE.md` is no longer overwritten by a partial top-up: earlier sets'
  lines are kept and merged per material.

### Security

- **Online-readiness rung zero (per the architecture audit `dev-docs/ARCH_AUDIT_ONLINE_2026-09-02.md`).**
  ACL moved into an engine-free registry: `WorldAclAuthorizer` in `CoreAI.RbxApi.Instances` denies
  `SetAccessControl`, reparenting, property writes and `Destroy` by actor identity — Lua bindings
  are no longer the only protection. Every combat world-mutation path runs under a server envelope
  (plain and MCP `execute_lua`, the mod's main chunk, scheduler resumptions, deferred signals and
  remote handlers, cross-mod calls under the callee's actor); the AI passes no `operation_id`,
  `target_instance_id`, `expected_revision`; an operation duplicate applies once; a registry call
  without an envelope in an ACL world is denied. An inbound `SenderActorId` does not create identity: a message from
  a non-admitted bridge sender is rejected before decoding and allocations. `RemoteEvent`/`RemoteFunction`
  payloads are capped at 65,536 UTF-8 bytes and rejected with `PAYLOAD_TOO_LARGE`
  before string materialization. A single combat actor-teardown seam: `PlayerRemoving` exactly once,
  chat freed, scheduler threads killed, limit windows and client signals removed; 200
  connect/disconnect cycles leave no state. The partial-edit regression (a mod's main chunk
  denied without an envelope — ScaleHarness failed on mod load) is closed by the red test
  `RungZeroEnvelopeProductionPathsEditModeTests`. The flat `ILuaExecutor.ExecuteAsync(code, token)` seam used by demos and host self-tests
  receives the trusted local actor's server envelope (`LuaCsModStackOptions.LocalActorResolver`),
  not a denial. Open: an envelope for host writes during world-packet restore (see `TODO.md`).
- Lint forbids `pow()` with an unclamped base in all catalog shaders (the source of NaN blotches).
- The unsafe-async-primitives guard for WebGL covers `Assets/CoreAIMods/Runtime`.

## [7.4.0] - 2026-09-03

Version aligned with `com.neoxider.coreaiunity` 7.4.0 (`FollowIfAtBottom` feed mode — a Unity-layer
change). No core changes of its own in this release.

## [7.3.0] - 2026-09-03

Version aligned with `com.neoxider.coreaiunity` 7.3.0 ("do not touch scrolling" feed mode — a Unity-layer
change). No core changes of its own in this release.

## [7.2.0] - 2026-09-02

Version aligned with `com.neoxider.coreaiunity` 7.2.0 (chat scroll anchor — a Unity-layer change).
No core changes of its own in this release: everything below in 7.1.2 ships with it under one tag.

## [7.1.2] - 2026-08-31

### Added

- **A tool can set its own call-body limit — `ILlmTool.ToolTimeoutMsOverride`.**
  `DefaultToolTimeoutMs` is sized for a hung HTTP: no answer in 30 seconds — cut it off. But some
  tools WAIT FOR A HUMAN (a quiz card, drag-and-drop, a confirmation request) — their body
  idles exactly as long as the human thinks, and the shared limit cut them off mid-question,
  feeding the model a "Tool timed out" while the student was still reading. The only way out was
  raising the SHARED limit, i.e. removing protection from every other tool at once — which is how
  RedoSchool ended up with 150 seconds on any tool. Now the lever is per-tool: `null` (default) — previous
  behavior, a positive value — its own budget, `0` or less — no limit (the same
  semantics as `0` in the shared setting). The member is declared with a default implementation, so no
  existing tool needs editing. `LlmToolBase` and `DelegateLlmTool` gained it too —
  as a `virtual` property and a setter respectively.
- The override also applies at the **second** place where the same limit is enforced — the bounded
  drain of unfinished calls when a streamed turn closes (`CompleteStreamedTurnAsync`). There the deadline
  is one for all parallel calls, so the LONGEST budget among the scheduled ones is taken: otherwise
  a turn where a waiting card ran next to a plain short tool would drop the card on
  somebody else's budget, and the slot would fold into a "call did not finish" refusal while the student answered.

### Security

- A lifted tool limit does not make the turn infinite, and the code says so explicitly: the body is invoked with
  the request token, so `LlmRequestTimeoutSeconds` still tears it down (`TimeoutLlmClientDecorator`,
  and on Unity also `CoreAiChatService` via a PlayerLoop `CancelAfterSlim` — timers are unreliable in WebGL).
  There are exactly two exceptions, both recorded in the member's documentation: `LlmRequestTimeoutSeconds <= 0`
  and the streamed-turn emergency-drain path, which deliberately passes `CancellationToken.None` —
  there is nothing left above. So a waiting tool is recommended a large FINITE budget, not
  a disabled limit.

## [7.1.1] - 2026-08-31

### Fixed

- **`call_skill_tool` no longer refuses top-level tool calls.** A skill's instructions
  teach the model to reach tools through the wrapper, and the model generalizes the trick to the agent's own tools.
  The refusal arrived as a plain RESULT (`Tool 'X' not found.`), not an error: the model
  apologized in text and moved on, the action never ran and never surfaced anywhere. In RedoSchool this
  looked like "the teacher rarely calls the quiz" — in reality it called it every time, and we refused.
  Now, on a skill-catalog miss, the name is looked up among the same role's top-level tools and
  invoked. The list arrives via callback (`CallSkillToolLlmTool.Create(skills, directToolsProvider)`),
  because at meta-tool creation time the role's tools are still being registered.
- **A rejected tool call is now visible in the log.** Name misses and calls without `tool_name`
  are logged as warnings with the available names listed. Previously they were invisible entirely, and the
  "why doesn't the model call tools" cause could only be guessed at.

### Security

- The widened call intake does not bypass turn limits: a name outside the session allowlist is still
  declined, and the meta-tools are excluded from the search — otherwise `call_skill_tool("call_skill_tool")`
  would recurse.

## [7.1.0] - 2026-08-30

MVP1 tails are closed; the MVP2 scheduler core is laid down. Mods gained rotations through familiar
Roblox properties, both game samples stopped teaching the wrong trick, and the documents again describe
what actually lies on disk.

### Added

- **`BasePart.Orientation` and `BasePart.Rotation` are wired up** — they were loud stubs. `Orientation`
  reads and writes in degrees in YXZ order (like `CFrame.fromOrientation`), `Rotation` — in XYZ
  order (like `CFrame.Angles`); both go through the existing `RbxCFrame.ToOrientation` /
  `ToEulerAnglesXYZ` decomposition, and the setter preserves `Position`. The axis order is pinned by tests that
  compare the resulting CFrame against `CFrame.fromOrientation(...)` and `CFrame.Angles(...)` and require a
  multi-axis rotation to make `Rotation` and `Orientation` DIVERGE — otherwise both bindings to one
  decomposition would pass verification unnoticed.
  The order for `Rotation` is a conclusion, not a verified parity: the offline Roblox mirror only says
  "degrees about three axes", the order is documented nowhere. Recorded as decision **D10** in the roadmap,
  so it can be re-verified rather than taken on trust.
- **The MVP2 scheduler core (`ModScheduler`)** — engine-free Domain code in
  `RbxApi/Instances/Scheduling/`. The canonical R4.2 frame order is set by a single ordered stage
  table (PreAnimation → PreSimulation → PostSimulation → waking deferred threads → Heartbeat
  → PreRender). Per R4.8, `task.defer` runs at the end of the **current** resume point, so
  the deferred queue drains after each of the six phases, not three times per frame. Zero-length
  timer readiness is counted **per stage**, not per frame: a `delay(0)` scheduled before the deferred-thread
  slot fires in the same frame ("on the very nearest Heartbeat" per R4.8), while one scheduled from the slot
  itself, from Heartbeat or from PreRender fires in the next — which preserves the
  minimum of one Heartbeat for `task.wait`. Binary min-heaps for wait/delay keyed by
  `(deadline, earliestFrame, sequence)`, a `ScheduleWaitUntil` primitive over a non-blocking completion
  token, an owning mod on every thread and `KillOwnedBy`, so a budget kill removes
  only the guilty mod while the rest finish the same frame. 19 deterministic tests on
  injectable clocks. Design and all deviations from §5.2.2 — in `dev-docs/MVP2_SCHEDULER_PLAN.md`.
- **`task.*` are connected to the scheduler and work at runtime.** `task.wait/spawn/defer/delay/cancel`
  stopped being stubs: the new `LuaCsRbxSchedulerAdapter` implements the scheduler ports on top of
  the existing `IScriptCoroutine` seam, every thread carries its owner-mod id, and the tick driver calls
  `ModScheduler.Advance` once per frame with a scaled delta. The frame order follows R4.2: scheduler
  phases → waking deferred `task.wait`/`task.delay` → the existing input pump and
  `RunService` on the Heartbeat boundary → `PreRender`. On mod unload, reload and quarantine
  `KillOwnedBy` is called — exactly where its connections are torn down, so a mod's threads never
  outlive the mod itself.
  Two defects caught only by a real in-editor run, not by compilation nor an isolated
  harness: the coroutine seam ignored resume arguments, dropping the varargs of
  `spawn/defer/delay`; and the `true` flag from a protected resume leaked into the yield continuation, so `task.wait`
  returned `true` instead of the actually elapsed time (R4.8 requires exactly the time).
  **Boundaries, named honestly.** `task.wait` works at a mod chunk's top level (the chunk runs
  as a scheduler thread, synchronously to the first yield — the same semantics R4.8 gives `task.spawn`),
  but does **not** work inside plain signal handlers: callbacks are invoked directly and are not
  scheduler threads, so `task.wait` there fails loudly and names the rung. Compatibility with the
  `coroutine` library per **R4.10 is not supported**: `task.*` accept a function or their own
  descriptor, but not a `coroutine.create()` thread, and return a descriptor rather than a native thread —
  the `IScriptEngine`/`IScriptCoroutine` seam currently cannot wrap an existing thread nor hand out its
  native value. That is MVP0 seam work, filed in `TODO.md`.
- **A golden test for whole-scene handedness** (`RbxSpaceSceneHandednessGoldenEditModeTests`). The
  Rbx→Unity bridge was proven analytically, but tests only covered single and yaw poses. Now pinned:
  "which part is on the right" agreement in both spaces, the `mod z = -(Unity z)` rule
  both ways at every trace point, and the handedness flip itself (a right-handed triple in Rbx, a left-handed one in
  Unity) — under both `RobloxSpace` scales.

### Fixed

- **An unimplemented surface now fails LOUDLY instead of pretending not to exist.** Per the
  "loud stubs" principle, an unimplemented member must report its phase and workaround. Part of the surface was neither
  bound nor stubbed and fell through into the generic "no such member" error — for an LLM that is direct
  disinformation: told the member does NOT EXIST, it invents a workaround instead of
  the implemented alternative. `ClassCatalog` gained a data-driven, inheritance-aware catalog
  of known Roblox members; it will later become the source of the generated API manifest.
  There are three statuses and they do not mix: **planned** carries a rung ("planned for MVP8"), **backlog** —
  acknowledged but not yet scheduled work, **unsupported** — a deliberate "never" (like `Terrain`).
  Only planned entries carry the phase; every entry carries a workaround hint. Closed: `Model.PrimaryPart`/`WorldPivot`,
  `Workspace.Gravity`, `Raycast`, `GetServerTimeNow`, `SignalBehavior`, `BasePart` physics and surface properties,
  `Lighting` gaps and `RunService` methods. The reverse side is kept and pinned by a test: **a typo
  still yields "no such member"**, otherwise any name error would read as "coming soon".
- **`game:BindToClose(fn)` validates its argument.** The binding used to pass `null` and never read
  argument 1 at all. Now a non-function yields `BAD_ARGUMENT` naming the received type, and a function reaches
  the previous loud MVP5 stub. The callback itself is still unimplemented — that is MVP5.
- **`BasePart.Material` is no longer unaccounted work.** The property stays a loud stub but names the
  real `MVP2 (materials catalog)` phase, backed by item 12 in roadmap §5.2.1 and the
  `dev-docs/MATERIALS_RESEARCH.md` study.
- **Samples stopped teaching world-axis binding.** `sample_lane_racer` and `sample_tetris3d`
  derive the horizontal and longitudinal axes from `CurrentCamera.CFrame.RightVector`/`LookVector`
  projected onto the ground, instead of assuming "world +X is screen-right". In Tetris
  the grid logic stayed integer and camera-independent: only positions are translated into the world.
  Both games' geometry is numerically unchanged.
- **Lane Racer no longer lets a block pass through the car at large `dt`.** Collisions were checked at the
  block's end point, so a frame after alt-tab or a debugger pause could step over the whole
  contact lane. Now it checks whether the movement SEGMENT crossed the lane this frame.
- **Tetris: gravity stopped losing the remainder and "firing" on speed switches.**
  The accumulator is decreased by the interval instead of zeroed, so the remainder is not lost; all
  accumulated intervals are processed per frame, but no more than eight, and after a long hang the excess
  is cut modulo the interval (the phase inside the interval itself is kept). The worst
  case is closed separately: one accumulator was compared against 0.6 s, then 0.05 s, so pressing S after half a
  second of normal falling released all the stored time at once and dropped the piece eight rows. Now on
  an interval change the stored fraction is rescaled (`accum / oldInterval * newInterval`), so
  switching into soft-drop and back produces no jump. Rotating the piece's cells no longer creates five tables
  per frame. The guarantee here is remainder preservation and jerk-free motion, not full framerate
  independence: the eight-step cap deliberately discards very-long-frame time.

### Docs

- **The Lua-visible surface table was reconciled line-by-line with the bindings and split into THREE
  categories** instead of two: implemented / loud stub / missing and unstubbed. This exposed that some
  members are neither bound nor stubbed — `Model.PrimaryPart`, `Workspace.Gravity`,
  `Raycast`, `GetServerTimeNow`, `Terrain`, `BasePart.Velocity`, `AssemblyLinearVelocity`,
  `Massless`, constraints and surface properties fall into the generic "no such member" error. For
  a model that is a lie: told the member does NOT EXIST, it invents a workaround instead of
  taking the implemented alternative. The table is now honest, and the runtime hole itself is filed
  as a separate item in `TODO.md`. Fixed along the way: `Model.PivotTo`/`GetPivot` were listed as
  implemented while being stubs, and their `the Model pivot follow-up` phase was not a rung at all —
  now it is `MVP2 (Model pivot)` with item 13 in §5.2.1, like `MVP2 (materials catalog)`.
- `Docs/ROADMAP.md` and `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` rebuilt from the facts: current
  package versions, MVP1 marked complete in 6.3.0 (not "Lua wiring in progress"), the `WaitForChild`
  rungs unambiguously separated (an existing child — MVP1, yield and a 5 s warning —
  MVP2), the stale `GetService` claim fixed, the Lua log service recorded as connected
  end-to-end, decision **D10** added, and the test tree in §6.6 aligned with the actually existing
  directories.

## [7.0.7] - 2026-08-27

### Fixed

- **OpenAI-compatible `reasoning_content` no longer becomes the answer text.** In RedoSchool's combat base,
  the model's internal train of thought was saved as the student's finished note, while the real answer
  was cut off once the budget ran out. `MeaiOpenAiChatClient.ParseResponse` now creates visible
  `TextContent` only from `message.content`; `reasoning_content`, `reasoning` and `reasoningContent`
  stay a separate `TextReasoningContent`. With empty `content`, reasoning is not used as a
  fallback answer: the consumer gets an honest empty-response, not the model's internal doubts.
- **SSE reasoning stays a diagnostics-only stream.** Reasoning deltas still count as
  real deltas and travel separately, but completing a reasoning-only stream no longer appends
  the accumulated reasoning as the final visible `TextContent`. This behaves identically under
  `ProviderDefault`, enabled and disabled reasoning mode: the mode changes the request body, not the response
  parsing rules.
- **The result's public contract now explicitly pins the storage boundary.** Only
  `LlmCompletionResult.Content` / `LlmStreamChunk.Text` are the answer for UI, commands, notes and
  history. `ReasoningContent` / `ReasoningText` are short-lived diagnostics; auto-carrying them into
  MemoryTool, ChatHistory, `ApplyAiGameCommand`, the assistant trace and other long-lived
  records is forbidden.

## [7.0.6] - 2026-08-26

### Fixed

- **`AgentBuilder` no longer raises a false `MissingSystemPrompt` when the system prompt deliberately
  arrives in every `AiTaskRequest.SystemPrompt`.** An explicit fluent contract was added:
  `WithPerRequestSystemPrompt()`: it stores and substitutes no text; it only tells the builder's
  validator that the calling code is responsible for a non-empty per-request prompt. This matters for server-side
  method layers and the cacheable request head: previously, with a correct 12+ KB prompt, the build still
  complained about an empty role, teaching the operator to distrust every warning. Indirect
  signs, including `WithOverrideUniversalPrefix()`, deliberately suppress nothing; a custom role without
  `WithSystemPrompt(...)`, without the new declaration and without a built-in fallback still gets a genuine
  `MissingSystemPrompt`. A regression was added to `AgentBuilderEditModeTests`.

## [7.0.5] - 2026-08-20

### Fixed

- **`ToolExecutionPolicy` no longer loses the continuation after a WAITING tool in WebGL.**
  7.0.4 fixed timers on this path (`CancelAfter` → `ILlmAsyncMarshaler.DelayAsync`) and one
  `RunContinuationsAsynchronously`, but the defect survived and reproduced in build b241: the teacher showed
  a quiz card, the student answered, and the teacher's turn never finished — an eternal typing
  indicator, blocked input, no second model request on the wire.
  The cause is not the timer but `ConfigureAwait(false)`: the Unity WebGL player has no thread pool, while
  `SynchronizationContext.Current` exists (`UnitySynchronizationContext`), so the continuation of
  `await x.ConfigureAwait(false)` is deemed non-inlineable and queued to the pool — i.e. it NEVER
  runs. While every tool completed synchronously there was no suspension and the defect never
  showed; the first tool that genuinely waits (the student's card answer) hung the turn
  dead. The failure is silent — not an exception but eternal waiting. It never reproduces in the editor
  at all: a thread pool exists there.
  All 18 `ConfigureAwait(false)` were removed from `ToolExecutionPolicy` — on the streamed path
  (`ExecuteStreamedAsync` → `CompleteStreamedTurnAsync`), on the batch path (`ExecuteBatchAsync`), and in
  `ExecuteSingleAsync`, including the `Task.WhenAny(invokeTask, timeoutDelay)` race: without this fix
  the 7.0.4 per-call tool timeout still would not fire in WebGL — the delay was scheduled
  correctly, but its continuation likewise left for the nonexistent pool. The same rule with the same
  wording is already recorded in `MeaiOpenAiChatClient`, `AiOrchestrator`, `QueuedAiOrchestrator`,
  `LoggingLlmClientDecorator` and `FetchSseOpenAiTransport`; `ToolExecutionPolicy` was the last file
  on the LLM path where the rule was not followed. For portable hosts without a `SynchronizationContext`
  behavior does not change: there is nothing to capture.
  **Important for consumers:** this fix alone is not enough if a waiting tool's body delivers its
  result via `UniTask.AsTask()`. The first continuation after the body belongs to MEAI
  (`AIFunction.InvokeAsync` awaits with `ConfigureAwait(false)`), and MEAI ships as a binary — so
  the host must publish the result with the `SynchronizationContext` cleared. In RedoSchool this is done in
  `ChatInteractiveAwaitChannel`.

## [7.0.4] - 2026-08-19

### Fixed

- **Draining deferred tool calls and the per-call tool timeout no longer hang the WebGL player
  dead.** `TaskCreationOptions.RunContinuationsAsynchronously` forbids inline resumption and
  hands the continuation to the thread pool, which does not exist in WebGL, while `CancellationTokenSource.CancelAfter`
  relies on `System.Threading.Timer`, which never ticks there: the continuation NEVER ran, and
  the deadline simply did not exist. The failure is silent — not an exception but eternal waiting:
  after an interactive tool (a quiz card, drag-and-drop, a confirmation) the student saw
  an endless typing indicator. The same defect class was already fixed in
  `FetchSseOpenAiTransport.StreamState`. Now `ToolExecutionPolicy` waits for completion without
  `RunContinuationsAsynchronously`, and both delays — the drain's outer grace deadline
  (`CompleteStreamedTurnAsync`) and the per-call tool timeout (`ExecuteSingleAsync`, the very
  `Error: Tool 'X' timed out after Nms`) — are scheduled by the host via the new `ILlmAsyncMarshaler.DelayAsync`
  (a `Task.Delay` default for portable hosts, Unity's
  `UniTask.Delay(DelayType.Realtime, PlayerLoopTiming.Update)`). The residual risk is named explicitly: a host that never
  set `ToolInvocationMarshaler` gets `PassThroughLlmAsyncMarshaler` → `Task.Delay` → still no deadline in WebGL.
- **Losing delays and abandoned drain tasks are observed without a scheduler.**
  `ContinueWith(..., TaskScheduler.Default)` was replaced with `await` in the shared helper: `ExecuteSynchronously`
  is a hint, not a guarantee, and TPL may put the continuation into the thread pool, which does not exist in WebGL,
  leaving the exception unobserved. A delay is now observed in every outcome, including the one where
  it won the race: `UniTask…AsTask()` translates cancellation into `OperationCanceledException`, i.e.
  a cancelled delay becomes Faulted rather than Canceled, and "cancellation is not a failure" is wrong here.
- **The defect class is closed in the tool drain and frozen elsewhere: the guard will not let new
  occurrences in clean files through.** `WebGlUnsafeAsyncPrimitivesEditModeTests` scans `Assets/CoreAI/Runtime` and
  `Assets/CoreAiUnity/Runtime` for `RunContinuationsAsynchronously`, `CancelAfter`, `Task.Delay` and
  `Task.Run`: it cuts out comments and string literals and discards only those preprocessor branches
  that are RELIABLY absent from a WebGL build (conditions are evaluated three-valued — an unknown define and its `#else`
  stay under inspection, otherwise the guard would grow blind spots). The inherited places — 12
  entries, each with a reason — still ship in the build and are handled as separate tasks; of them
  `QueuedAiOrchestrator` (3× `RunContinuationsAsynchronously`, 1× `Task.Delay`) and `LlmClientRegistry`
  (2×) are reachable in the WebGL player, i.e. the same defect still lives there. The scanner itself, the condition evaluator,
  the reachability markup and the exception list are covered by planted cases: without them a broken guard
  would look like "no violations found", i.e. green. A deliberate boundary: an allowlist entry is keyed by
  the "file + primitive" pair with no counter, i.e. it covers the file wholesale for that primitive —
  one more occurrence in an already listed file passes silently. A counter was deliberately rejected: it would go red
  on every edit of lines above in the file.
- **The mod log pipeline is wired end-to-end.** `LuaLogService` registers as a singleton
  `ILuaLogService` in `RegisterCoreAiMods` and threaded through `LuaCsModStackOptions.LogService` into
  `LuaCsModRuntime`: `print`/`report` are written at Print level, handler/event/load errors as
  RuntimeError, quarantine as Error. The `get_mod_logs` tool (`GetModLogsLlmTool`) is attached to the
  Programmer role next to `execute_lua`/`manage_mods`, and the MCP `get_mod_logs` tool now resolves the same
  singleton with no MCP-side changes. Previously the whole `ILuaLogService` layer was unwired, and both
  tools read emptiness.
- **Five duplicated `CoreAi_PersistFsSync` DllImports removed** (`FileLuaModStore`,
  `FileLuaModSourceStore`, `FileSkillStore`, `FileAgentMemoryStore`, `WorldStateManager`): all calls go
  through the shared `CoreAiWebGlPersistence.Sync()` (now returning `bool`), and flush errors are always logged —
  the silent swallow in the mod/skill/memory stores is gone.
- **The MCP HTTP server does not start in the WebGL player.** `CoreAiMcpServer.StartListening()` finishes with
  a log warning on `Application.platform == WebGLPlayer` instead of attempting a loopback socket,
  which is impossible on WebGL.
- **The modding documentation was brought to the Lua-CSharp stack.** FIRST_MOD, LUA_ACCESS_MODES, LUA_GAME_API,
  LUA_NATIVE_APIS, LUA_BEST_PRACTICES, LUA_SANDBOX_SECURITY, LLM_TOOLS and AGENT_BUILDER no longer reference
  removed MoonSharp-era APIs (`LuaModRuntime`, `SecureLuaEnvironment`, `log_info`,
  `GameLuaBindingsExtensibility`, `-- name:` headers); tier descriptions and budgets fixed (10 s / 50M
  steps, quarantine instead of unload), language level — Lua 5.2 + Luau downleveler, and examples with withheld
  `coreai_world_*` build APIs marked opt-in-only.

## [7.0.3] - 2026-08-12

### Added

- **Log formatting can opt out of the library and feature prefixes independently.**
  `GameLogSettingsOptions`, `RuntimeGameLogSettings`, and `GameLogFilter` expose
  `IncludeCoreAiPrefix` and `IncludeFeaturePrefix`, both defaulting to `true`.
  The optional `IGameLogFormattingSettings` contract leaves `IGameLogSettings` unchanged, so existing
  third-party settings implementations retain source compatibility and the legacy format.

## [7.0.1] - 2026-08-12

### Changed

- **A patch release synced in the package lockstep graph.** The portable core version and internal dependencies
  were updated to 7.0.1 together with the Unity 6.6 UI Toolkit fix in `com.neoxider.coreaiunity`;
  the portable core public API did not change.
- **The release gate `tools/check_positive_module_opt_in.py` was moved to 7.0.1.** The expected lockstep version
  and ROADMAP / DEVELOPER_GUIDE lines are now built from the `LOCKSTEP_VERSION` / `LOCKSTEP_DATE` constants,
  so the `Package graph (lockstep + deps)` CI job does not fail after every version bump.

## [7.0.0] - 2026-08-01

### Breaking

- **LLM and Lua moved from negative opt-out symbols to independent positive opt-ins.**
  Migrating from 6.x: remove `COREAI_NO_LLM` and `COREAI_NO_LUA` from all Scripting Define Symbols;
  add `COREAI_LLM` to targets needing provider-backed HTTP/MEAI/LLMUnity implementations, and `COREAI_LUA`
  to targets needing the Lua runtime. Without symbols remain portable orchestration/chat,
  scripted/stub clients, public tool contracts and the mandatory MEAI assemblies; both symbols yield full provider + Lua runtime.
  The old negative symbols no longer affect
  active code and are not supported as aliases.
- **The CI matrix is now `core` / `llm` / `lua` / `full`.** Each positive define is added to all
  platform targets and verified on Standalone/WebGL. The sandbox suite must run in `lua`/`full`,
  the LLM suite in `llm`/`full`; the FastNoLlm PlayMode uses `COREAI_LLM` because the suite name means
  no live backend, not a compile-out of the LLM layer.

### Fixed

- **All persistent role-keyed student data uses one opaque scope boundary.** The shared
  `AgentMemoryScopeKey` keeps exact legacy bare-role keys only for `AgentMemoryScope.Empty`, while any non-empty
  scope becomes `scope-v1-<full SHA-256>` with no PII in file names or logs; legacy-key to scoped
  identity migration is explicit only. The same key serves memory/chat,
  structured transcript and compacted conversation summary decorators. `ScopedAgentMemoryStoreDecorator`
  proxies `IAtomicAgentMemoryStore` onto the already computed scoped key and declares no foreign optional capabilities.
  This rules out carrying local memory and compacted context between successive students of one role.
- **The queue cancellation scope gained the same identity boundary.** `QueuedAiOrchestrator` combines the logical
  `CancellationScope` with the role's `AgentMemoryScope`; identical Teacher turns of different students no longer cancel each other.
  The single-argument `CancelTasks(scope)` and the compatible `CoreAi.StopAgent(scope)` now find the current identity
  via the role stored on each admitted item, so they work even when the domain scope differs from the role id.
  `IScopedAiTaskCancellation.CancelTasks(scope, roleId)` remains the explicit per-role variant.
- **The queue remembers the student identity at enqueue time.** A sync/stream work item stores an immutable
  `AgentMemoryScope`; the inner orchestrator launch, cancellation and `RecordUnstartedTurn` use this snapshot even if the
  host managed to switch the mutable scope provider to another student before actual execution.
- **A failed AI turn no longer vanishes from conversation memory — including before the inner
      orchestrator starts.** After HTTP 402, a timeout, an authority refusal, a cancellation during request assembly,
      a stream break, an empty answer or exhausted retries on context overflow, the model's next
      reply sees the student's original question. The production queue applies the same teardown to
      pre-cancel, pending/claimed-before-inner cancel, cancellation-scope replacement, `CancelTasks`, queue-full
      and `Dispose`; sync and streaming paths are supported.
- **One orchestration invocation or one admitted queue item makes at most one attempt to write the
      user turn.** Internal tool-roundtrips and post-overflow retries do not
      duplicate the current question nor slip it into their own retry request; an interrupted stream
      does not save a half-baked assistant answer. This is not a dedup promise for a separate external
      retry of the same logical turn: that needs `IAgentMemoryStore` to gain a stable idempotency key.
- **A history-write error does not replace the failed turn's original error.** On a successful turn the
      assistant answer is not saved separately if the matching user replica failed to write;
      on queue teardown the original cancel, queue-full and dispose outcomes are preserved.
- **The shared provider-cache prefix is separated from the personal tail.** `SystemPrompt` keeps the stable
      role/persona instructions and the role's full canonical tool contract. Per-request system
      instructions, the student's canonical/delta memory, filtered-tool availability and world
      state go after history as ordered system-tail messages, so switching student or slide
      does not rewrite the shared prefix.
- **Dynamic custom headers keep one identity snapshot for the whole outer retry loop.** One logical
      `CompleteAsync` uses identical lesson/cohort headers after a retryable result and
      `LlmClientException`; the streaming pre-commit retry likewise does not re-snapshot the mutable provider. A new invocation
      gets a fresh snapshot even when reusing the same `LlmCompletionRequest`.

### Added

- **`InMemoryAgentMemoryStore` for process-only host policies.** The portable backing implements a memory document,
  bounded flat chat, structured transcript, load diagnostics and atomic mutation without filesystem APIs; values
  are cloned at the store boundary, so calling code cannot mutate stored state by reference.
- **A public safe provider-specific-body API for OpenAI-compatible HTTP.**
  `OpenAiHttpOptions.SetProviderBodyParameter(string, JToken)` and `RemoveProviderBodyParameter` accept
  nested `JObject`/`JArray` without reflection/dynamic, deterministically sort object keys, preserve array
  order and work on AOT/WebGL. C# `null` removes the field, `JValue.CreateNull()` sends JSON `null`.
  `model`, `messages`, `temperature`, `max_tokens`, `stream`, `stream_options`, `tools`, `tool_choice`,
  `enable_thinking`, `thinking_budget`, `chat_template_kwargs` are protected as
  CoreAI-owned; invalid/non-object/duplicate/reserved input is rejected atomically with no JSON values/body leaking into
  exceptions. Raw `ExtraBodyJson` stays as an advanced backwards-compatible escape hatch and can still
  override reserved fields. Migration: new application code should use the safe setters; keep raw JSON
  only where the old unsafe override semantics are deliberately needed.

## [6.14.0] - 2026-07-31

### Fixed

- **HTTP 402 (the provider ran out of credits) is no longer retried: the error returns on the first
      attempt.** In a live game, one doomed request cost the student 5.4 seconds of waiting
      (`wallMs=5373 chunks=0 | HTTP error 402`). The cause was two classification losses in a row.
      First, `MeaiOpenAiChatClient.MapHttpStatus` did not know 402 and folded it into
      `LlmErrorCode.ProviderError` — the generic "unknown provider error" the whole chain treats as
      TEMPORARY. Second — and this is the main one — `RetryingStreamingLlmClientDecorator` caught the
      transport exception and hardcoded `ErrorCode = ProviderError`, discarding both
      `LlmClientException.ErrorCode` and `HttpStatus`: even correctly classified 401s
      (`AuthExpired`) and 400s (`InvalidRequest`) reached the retry predicate as "temporary" and
      were repeated. Now the decorator carries the typed code, HTTP status and `Retry-After` from the
      exception into the terminal chunk, and a permanent refusal goes up immediately without scheduling a single
      backoff pause.
- **408 Request Timeout is classified as `Timeout`,** not as a nameless provider error —
      the only 4xx a retry can genuinely cure (and one the transport already retried
      by status).

### Added

- **`LlmErrorCode.PaymentRequired` (402) and `LlmErrorCode.PermanentProviderError`** — a separate class
      of "repeating the same request yields the same answer". Other unclassified 4xx (404 "no such
      model", 405, 410, 451 …) now land in `PermanentProviderError` instead of `ProviderError`.
      The retry and fallback predicates are whitelists, so the new codes are not in them and are not
      repeated, while `ProviderError`'s meaning for existing consumers does not change: it stays
      temporary. A 402 with a quota body is still classified as `QuotaExceeded` (the same
      permanent-error class) so already pinned behavior does not change.
- Human phrases for the new codes in `LlmErrorPresentation.ForErrorCode`. The gateway-written message
      priority (`error.message` from the response body) is kept: the phrase the student sees
      arrives from the backend and is not replaced by the library's.

## [6.13.1] - 2026-07-31

### Fixed

- **A mod that calls `coroutine.resume` no longer deadlocks a WebGL player.** The bundled Lua VM is
      now stock **Lua-CSharp v0.5.6** (was a locally patched 0.5.5 build). Upstream v0.5.6 stops the
      coroutine machinery from marshalling its continuations through `SynchronizationContext`
      (nuskey8/Lua-CSharp#329, for the deadlock we reported as issue #327): Unity's main thread has a
      `SynchronizationContext`, so a suspended `coroutine.yield` posted its continuation to a thread
      that was already blocked in `GetAwaiter().GetResult()` — on desktop another thread eventually
      picked it up, on single-threaded WASM nothing ever did and the player froze. Both sync-over-async
      drives CoreAI relies on are affected: `LuaCsSecureEnvironment`'s guarded `coroutine.resume`
      wrapper and `LuaCsCoroutineHandle.Resume`. Verified against all three builds outside Unity: the
      old bundled VM leaves the resume task incomplete with one posted continuation, v0.5.6 completes
      it synchronously with none.
- **The local VM patch is retired.** The `NotifyTop` fix we carried on our own Lua.dll since 2026-07-10
      (`#t` returning nil under the sandbox's instruction hook) landed upstream as
      nuskey8/Lua-CSharp#331 and ships in v0.5.6, so the bundled binary is an unmodified upstream build
      again — no local patch to re-apply on the next VM bump. The regression is re-verified: `#t` under
      a count hook is 3 on the new build, nil on stock 0.5.5.

### Changed

- **Lua execution is ~25% faster across the board on the new VM.** Upstream applied a lightweight async
      method builder to the VM internals (nuskey8/Lua-CSharp#330); measured on a tight numeric loop
      outside Unity, raw execution went 30 ms → 22 ms and the same loop under the sandbox's guard hook
      165 ms → 127 ms. The guard's *shape* is unchanged — an empty hook body still triples a tight loop,
      so the cost remains the hook invocation rather than the work inside it (see
      `dev-docs/LUA_PERF_AUDIT_v6.6_2026-07.md` §7).
- `IScriptEngine.EngineVersion` for the Lua-CSharp engine now reports `0.5.6`.

## [6.13.0] - 2026-07-31

### Changed

- **The hidden `gpt-4o-mini` fallback is gone: an unset model is now either an explicit error or an
      explicit decision of the backend.** `OpenAiHttpOptions.Model` no longer defaults to `gpt-4o-mini`,
      and `MeaiOpenAiChatClient` no longer copies whatever the settings hand it straight into the request
      body. It resolves the model once: a non-empty name is sent as before; an empty name under
      `LlmExecutionMode.ServerManagedApi` OMITS the `model` key entirely (the backend owns the choice
      there, and an empty string is not a model id any provider accepts); an empty name in any other mode
      throws `LlmClientException(LlmErrorCode.InvalidRequest)` naming the setting to fix. The old silent
      default was worse than a missing setting: traffic went to a model nobody selected, the invented name
      then appeared in logs, usage history and cost telemetry as if it were real, and it steered the
      client-side token estimator onto the `o200k` encoding of a model that never answered. Callers that
      relied on the implicit default must now name a model — which is the point.
      `IOpenAiHttpSettings` gained `ExecutionMode` (default-implemented as `ClientOwnedApi`) so the client
      can tell the two empty-model cases apart; existing implementations compile unchanged.

### Fixed

- **The completion result named the model the CLIENT asked for, not the one that answered.** With a
      router, a proxy, or `ServerManagedApi` (where the client asks for nothing at all) those are routinely
      different models, so every usage record and cost report attributed tokens to a guess.
      `ParseResponse` now carries the provider's `model` field into `ChatResponse.ModelId`, every SSE delta
      stamps `ChatResponseUpdate.ModelId` from the same field, and both synthetic streaming paths (the
      smooth re-emit of oversized deltas, and `FullResponseToSimulatedStreamingUpdates` used by WebGL
      without native SSE and by the stream→non-stream fallback) propagate it instead of dropping it.

## [6.12.0] - 2026-07-30

### Fixed

- **"All my parts vanished and then Lua threw an error about Workspace."** When the `RbxWorldHost`
      that owns a world is destroyed — a scene load, a domain reload during play, or leaving play
      mode — its teardown destroys the whole DataModel, and with it every part in the world. The mod
      stack does not go away: `LuaCsRbxApiBindings` captures the `InstanceRegistry` once at install
      time, so scripts keep running against a registry whose scene is gone. The next `Instance.new`
      then failed at the *parent assignment* with `PARENT_LOCKED` or `INSTANCE_DESTROYED` naming
      Workspace, which reads as a bug in the mod's own code and says nothing about the world having
      been torn down. The host now marks the registry detached on teardown and scripted creation
      fails immediately with a new `WORLD_DETACHED` code that names the lost host and tells the
      reader to reload the mods. Host-level creation (snapshot restore, service bootstrap) is
      unaffected, so a world can still be rebuilt.
- **The MCP server told every client the wrong version.** `serverInfo.version` in the `initialize`
      handshake is a hand-maintained C# constant that the release tooling never touched, so it answered
      `6.9.0` while the packages shipped `6.11.1` — the number that lands in bug reports and in a
      client's server list. `tools/bump_version.py` now rewrites `McpServerInfo.Version` alongside every
      `package.json`, and `--check` fails when the two disagree, so the drift cannot come back the way it
      came (the test that caught it had been failing, correctly, for three minor versions).
- **A refused MCP request could reach the client as a dropped connection instead of its status code.**
      The server wrote `413`/`415`/`401` and closed immediately, without reading the request body the
      client was still sending; the OS answered the unread body with a reset that discarded the response
      already in flight, so the caller saw a transport error rather than "payload too large". Refusals now
      drain the pending body first, up to 64 KB. A body bigger than that is still dropped mid-flight —
      reading it is exactly the cost the size cap exists to refuse.

### Changed

- **`// WHY:` comments pruned across every package (1065 → 958), and ~55 rambling ones tightened.**
      The convention is that a WHY records something a competent reader would still ask about; it had
      drifted into a comment on nearly every edit, which buries the few that carry a real constraint.
      Removed were the ones restating the next line, the field name, the neighbouring `/// <summary>`,
      or a standard .NET fact. Platform constraints (WebGL has no threads or timers, IL2CPP stripping),
      ordering and main-thread invariants, sandbox and admission-control invariants, and third-party
      workarounds were kept. Comments only — no code changed, verified line by line over the diff.
- **Restored 24 XML doc blocks in `MeaiOpenAiChatClient` that a previous pass had corrupted** into
      `// WHY: / <summary>` comments, and repaired 13 double-encoded em-dashes (`\u0432\u0402"` read as Windows-1251) in three files.
- **Two mods EditMode fixtures no longer assert diagnostics through the Unity console.** Whether a CoreAI
      `ILog` line reaches `LogAssert` depends on the process-wide `Log.Instance` and the live
      `GameLogFilter`, both of which any earlier test in the run may change, so those expectations passed
      or failed by test order. They now register a `RecordingLog` in the container under test and assert
      against it directly.

## [6.11.1] - 2026-07-30

Documentation-only release; no portable-core code changed.

### Added

- **`Docs/RBX_API.md`** — reference for the Roblox-style API a mod builds with (`Instance.new`, the
      supported classes, datatype and service globals, `Part` properties, a working mod, the bundled
      sample mods). It did not exist before, while the docs still taught the retired
      `coreai_world_*` build API in its place.

### Fixed

- **`LUA_GAME_API.md` / `FIRST_MOD.md`** no longer present the classic `coreai_world_*` build
      functions as the `WorldEdit` surface. The default composition withholds them
      (`RegisterWorldEditBuildBindings = false`) and answers a call with `LuaApiWithheldException`;
      both docs now lead with the Rbx API and scope the classic surface to opt-in hosts. The
      read-only world queries are unaffected and are marked as such.

## [6.11.0] - 2026-07-29

Unity-layer release; see `CoreAiUnity/CHANGELOG.md` for the notes
(`CoreAiChatPanel.TurnStreamingBubbles`).

## [6.10.0] - 2026-07-29

Acts on fresh audits of all five packages. The headline is an MCP security hole; the rest is a long
tail of failures that were silent by construction.

### Security

- **The in-game MCP server accepted cross-origin requests.** Binding to `127.0.0.1` stops network
  access but not the user's own browser: with `Content-Type: text/plain` a POST is a "simple" CORS
  request and skips preflight, so any open web page could call `tools/call execute_lua` — and DNS
  rebinding makes the request same-origin, exposing replies including `screenshot`. Requests are now
  screened before routing (`IsLocal`, `Host`, `Origin`, JSON `Content-Type`) and authenticated with a
  bearer token, generated per run or pinned via `COREAI_MCP_TOKEN`. The server remains off by default.

### Fixed

- **Six published demo scenes ran mods headless**, so `Instance.new` produced nothing in them —
  `CoreAiHubDemo`, `LiveMechanicsDemo`, `LiveMechanicsModsChatDemo`, `WaveAutoBattlerModsDemo`,
  `MiniRpgModsDemo`, `ModdableUnitsDemo` now each carry a wired `RbxWorldHost`.
- **Editing a mod in the Hub froze the game**, and **`LLMManager.LoadFromDisk()` wiped the LLMUnity
  model registry in the Editor** — a rescan that erased the user's registered models.
- **A ChainReset past the first line no longer verifies an audit log as intact.** Truncating the tail
  and appending a forged restart used to report `Ok`.
- **A library timeout surfaced as "cancelled"**, so it read as if the user pressed Stop, and the
  timeout branch never ran; an **empty streaming response** was recorded as success while vanishing
  from history and traces; **`MutateAsync` destroyed a role's memory** when the load failed rather
  than being absent; **`game_config update` reported success** when the store rejected the write.
- WebGL: `Task.Delay` in the endpoint drain loop never resumed, wedging activation forever; seven
  unguarded `SwitchToThreadPool` calls hung tool turns; an unclamped transcript entry could crash the
  player.

### Added

- **`GameLogFilter`, `RuntimeGameLogSettings`, `GameLogDefaults`.** Portable runtime log filtering:
  `GameLogFilter` is the entry point for retuning logs while the game runs (minimum level, category
  mask, single-category toggle, snapshot, reset to the authored values). `RuntimeGameLogSettings`
  holds the live values with lock-free reads (`Volatile` / `Interlocked`), so background threads
  (LLM streaming) can log while another thread changes the filter.

### Fixed

- **`GameLogFeature.All` did not include `Metrics`.** `AllBuiltIn` was `Core|Composition|MessagePipe|
  ExampleRoguelite|Llm` (31) and `All` was 799, so "enable every category" silently muted orchestration
  metrics. `AllBuiltIn` is now 63 (adds `Metrics`) and `All` is 831. `GameLogSettingsOptions` defaults
  to `All`.

## [6.9.0] - 2026-07-29

One failure, two audiences: a sentence for the player, everything for the log.

### Added

- **`LlmErrorPresentation`** (portable core, no Unity types). Turns a failed LLM call into the two
  strings it always needed:
  - `ToUserMessage(exception)` — one readable sentence for a chat bubble. A message the BACKEND
    authored for the player wins (parsed from `error.message` in `LlmClientException.ProviderErrorBody`),
    because a gateway that already says "the teacher is unavailable, try again in a minute" knows the
    product better than this library. Otherwise a phrase per `LlmErrorCode` is used — with the
    provider's retry window folded into the `RateLimited` text.
  - `ToDiagnosticText(exception)` — typed code, HTTP status, retry hint and the raw provider body,
    for the log line next to the exception itself.
  - `ExtractProviderMessage` / `StripHttpErrorPrefix` are public: hosts that render errors themselves
    reuse the same parsing instead of string-matching `"HTTP error "`.
- Guardrails, all covered by `LlmErrorPresentationEditModeTests`:
  - JSON dumps, stack traces and bodies longer than `MaxUserMessageLength` (400) are diagnostics, not
    UI text — they fall back to the typed phrase, so a player never reads `{"error":{...}}`.
  - **A 401 body reaches neither the player nor the log.** Providers echo the submitted key back in
    invalid-credentials responses, so the redaction the HTTP adapters already apply to log lines now
    applies here too (`body=[redacted auth error body]`; the bubble shows the "sign in again" phrase).

## [6.8.3] - 2026-07-29

The real fix for "mods spawn nothing in a build", and a correction: 6.8.2 blamed the wrong cause.

### Fixed

- **Parts spawned by mods were invisible in every player build.** The parts were always there —
  active, correctly sized, collidable — they just drew nothing. URP declares
  `UniversalRenderPipelineAsset.defaultMaterial` under `#if UNITY_EDITOR`, so in a player it is null
  and `GameObject.CreatePrimitive` substitutes the **built-in** `Default-Material` (shader
  `Standard`). That material is not null — so a null check never caught it — and URP cannot render a
  built-in shader. `InstanceGameObjectBinder` now builds its default material from the active
  pipeline's own shader whenever a Scriptable Render Pipeline is present, instead of trusting the
  primitive's material; cylinders get it assigned explicitly, `Universal Render Pipeline/Lit` is in
  Always Included Shaders, and an unresolvable shader is now a loud error.
- **Correction to 6.8.2.** That release attributed this to IL2CPP managed stripping and added
  `link.xml` entries. Measured on the live editor, Standalone builds with **Mono2x and managed
  stripping Disabled**, where no managed stripping happens at all — so stripping could not have been
  the cause, and the symptom reproduced identically on Mono and IL2CPP. The `link.xml` entries are
  kept (they are correct and necessary for WebGL, which does strip at Medium), but they fixed
  nothing here.

### Added

- **The failure is no longer silent.** A part that is created but never drawn was indistinguishable
  from one that was never created, and a player has no inspector to tell them apart — the reason this
  bug survived a full debugging session. The binder now reports the first materialized part with its
  renderer and resolved shader (once, not per part), and `InstanceRegistry` gained an engine-free
  `Diagnostics` hook that reports an instance entering a tree the registry does not own — previously
  an early `return` with no log, no exception, and no object.

## [6.8.0] - 2026-07-25

Acts on the 6.7.0 audits: concurrency and cancellation fixes across the core, fail-closed build
guards, Hub state-machine repairs, and two performance changes reverted for trading a guarantee away.

### Security

- **A provider key inside a `Resources/` settings asset now FAILS the build** instead of logging
  "Building anyway". Anything under `Resources/` is packed into the player and recoverable from the
  shipped build, so this is an exposed secret. `CoreAIResourcesApiKeyBuildGuard` reports every
  offending asset. **Migration:** clear `apiKey`/`secondaryApiKey` on committed `Resources` assets
  and supply the key at runtime; the repo's own placeholder was cleared.
- **WebGL key leaks fail the build too.** `CoreAIProductionSettingsValidator` now throws for
  `ClientOwnedApi`/`ClientLimited`/`ServerManagedApi` with a non-empty key, and inspects every
  settings asset in the project rather than an arbitrary one. The streaming-config finding stays a
  warning — no secret is involved.

### Fixed

- **Queue-pump deadlock in `QueuedAiOrchestrator`.** Work was started synchronously while `_lock` was
  held, and its first statement disposed a cancellation registration, which blocks until a running
  `CancelPending` callback returns — a callback waiting for that same lock. `Pump()` now claims slots
  under the lock and starts the work after releasing it, so nothing blocking runs under `_lock`.
- **`OperationCanceledException` was retried as a provider fault** in `AiOrchestrator` (two sites) and
  `RetryingStreamingLlmClientDecorator`; caller cancellation now propagates immediately.
- **`SetTools` silently did nothing** on a chain fronted by `ClientLimitedLlmClientDecorator` or
  `LoggingLlmClientDecorator`, which never declared it and fell through to the no-op default
  interface body. `CircuitBreakerLlmClientDecorator` likewise dropped two capability queries. A
  reflection sweep test now requires every decorator to declare every virtual interface member.
- **Half-open probe slot leaked** in `CircuitBreakerLlmClientDecorator` when the inner client threw
  synchronously, wedging the breaker open.
- **Unbounded static lock tables** in `ISkillStore`/`IAgentMemoryStore` keyed by model-controlled ids
  leaked for the process lifetime and serialized unrelated store instances against each other; both
  are now per-store via `ConditionalWeakTable`.
- **Hub sub-tab lifecycle** did not fire when returning to an already-built tab, **the remove-confirm
  state machine** lost its armed row on list rebuild, and **applying settings silently downgraded**
  `ClientLimited`/`ServerManagedApi` to another mode when the user never touched the dropdown.
- **`Packages/manifest.json` corruption** in `CoreAIDependencyInstaller`: a key under `testables` read
  as installed, and inserting into an empty dependencies object emitted a trailing comma. The result
  is now validated as JSON before anything is written.
- **Endpoint registry saves are atomic** (`File.Replace`), and an `IOException` no longer escapes past
  `Changed?.Invoke()` and desyncs the UI from the in-memory state.
- **WebGL had no thread pool** yet `CoreAiChatService` and `CameraLlmTool` awaited
  `SwitchToThreadPool`, hanging the turn until timeout.
- **Editor capture leak** in `AgentCameraService`/`CameraLlmTool`, and a **main-thread marshaler hang**
  when the sync context stops pumping (now a bounded wait with a clear `TimeoutException`).
- **Editor-load side effect removed:** `CoreAIBuildMenu` no longer auto-creates default assets on
  domain load. Use `CoreAI/Setup/Create Default Assets` or `CoreAI/Settings`.
- **PlayMode suite could not finish.** A `[UnityTest]` guard called
  `Application.CanStreamedLevelBeLoaded`, which reports `false` in the Editor even for a scene that is
  registered and enabled; the resulting `Assert.Ignore` on the first `MoveNext()` wedged the test
  runner and blocked all 139 remaining tests. The guard reads Build Settings directly and yields once
  before skipping; `CoreAiChatDemo` is now registered so the test gives real coverage.

### Changed

- **Breaking (small API surface):** `ICoreAiComponentCommandExecutor.LastListedComponents` is removed in
  favour of `TryExecute(cmd, out List<string> listedComponents)`. Tool calls run in parallel, so a
  `list_components` result carried on shared executor state could be overwritten by a concurrent call
  before its own consumer read it. Implementers of this interface must move the listing to the
  out-parameter.
- **The coroutine resume guard** fired its hook on every instruction (4× the main guard's rate) and
  used a per-resume `Stopwatch`; it now matches the main guard's batch and timestamp accounting, with
  the same step ceiling.
- Allocation removed from hot paths: static `Connect`/`Once`/`Wait`/`Disconnect` functions, no
  per-write string concatenation on spatial property writes, and `Array.Empty<T>()` in the script
  execution guard.

### Reverted

- **Clock sampling in the Lua execution guard (shipped in 6.7.0) is reverted.** It was documented as
  "free and risk-free"; it was not. The count hook does not fire during a host call, so a handler of a
  few hundred instructions that are mostly bindings can blow a per-frame budget while never reaching
  the sampling threshold — defeating the timeout in the case that matters most. The measured cost of
  reverting is ~6%. `dev-docs/LUA_PERF_AUDIT_v6.6_2026-07.md` records how to recover it safely by
  checking the deadline at the host-call boundary.
- **`RbxScriptSignal.Fire1`/`Fire2` were reverted before shipping.** The reusable argument buffer is
  incompatible with the MVP2 scheduler's deferred dispatch, where the argument array outlives the
  `Fire` call, and it saved one array out of N+1. Argument pooling belongs in the scheduler, which
  owns the lifetime.

## [6.7.0] - 2026-07-24

Prompt-delivery fix, a measured Lua guard optimization, and a full read-only audit of the Hub, core
and Unity packages.

### Fixed

- **Built-in agent prompts never reached a Unity host.** `AgentPromptsInstaller` chains the
  `Resources/AgentPrompts/System` provider *ahead* of the built-in one, so a prompt shipped there
  permanently shadows its C# const — and the package shipped copies of seven built-in prompts. The
  shipped `Programmer.txt` had already drifted: it was missing the "answer plain questions directly —
  do NOT call `read_skill` or any tool for those" rule, the `report()/logic_*` globals list, and the
  `Forbidden: io, os, require, load, loadfile, dofile, debug` line. Every Unity host was therefore
  running an older Programmer prompt than the code said. The eight shadowing/dead assets are deleted
  (`PlayerChat.txt` was dead outright — the role id became `PlainChat`), leaving
  `DeveloperSampleAgent.txt`, which has no const. `Resources/AgentPrompts/System` is now purely the
  consumer's override slot, as its own doc comment says. A new test,
  `NoBuiltInRolePromptIsShadowedByAShippedResourceCopy`, fails if such a copy is reintroduced —
  replacing two sibling tests that checked the const and the `.txt` separately and never that they
  agree, which is why the drift went unnoticed.

### Changed

- **Lua execution guard: the wall clock is now sampled every 64th hook fire instead of every fire.**
  Measured on this Editor's Mono, `Stopwatch.GetTimestamp()` costs ~44 ns against ~14 ns for the heap
  read, i.e. it was ~76% of the work done inside a hook that fires every 4 VM instructions. The step
  budget is still charged on every fire, so a runaway is cut exactly as before; the timeout is now
  enforced to within one sampling window (~256 instructions) against budgets measured in seconds.
  Guarded-execution overhead: 3.67× → 3.21× versus an unguarded VM.

### Docs

- `dev-docs/LUA_PERF_AUDIT_v6.6_2026-07.md` — measured Lua performance audit. Two findings worth
  reading: the published Luau comparison was measured **without** the execution guard and therefore
  understates the real gap by ~3.7×; and the guard's cost is dominated by the *number* of hook fires
  (~600 ns per fire, the VM's async hook dispatch), not by the work inside the hook. Raising
  `HookInstructionBatch` to 64 was measured at **1.21×** overhead (a 2.65× speedup) but deliberately
  **not** shipped — the small batch is load-bearing for the allocation-bomb backstop. The document
  specifies the adaptive-batch design that would capture the win safely, and the tests it needs.
- `dev-docs/CODE_AUDIT_v6.6_2026-07.md` — read-only audit of `CoreAIHub`, `CoreAI` and `CoreAiUnity`:
  correctness leads (a lock-ordering deadlock in `QueuedAiOrchestrator`, `OperationCanceledException`
  retried as a provider fault, build guards that fail open, a non-atomic endpoint-registry save),
  architecture-rule violations, and the highest-value missing tests. Backlog, not fixed.

## [6.6.0] - 2026-07-24

`Roblox`→`Rbx` identifier cleanup across the C# codebase, plus two test/doc fixes carried over from 6.5.0.

### Changed

- **"Roblox" removed from C# identifiers (naming convention: `Rbx`, not `Roblox`).** Renamed every C#
  type, interface, member, test class and test namespace that carried `Roblox` in its name to the `Rbx`
  form — `RobloxSpace`→`RbxSpace`, `RobloxWorldHost`→`RbxWorldHost`, `RobloxCameraFollower`→`RbxCameraFollower`,
  `IRobloxCameraRig`→`IRbxCameraRig`, `RobloxApiStubException`→`RbxApiStubException`, `LuaCsRoblox*`→`LuaCsRbx*`,
  the `RobloxApi` member/namespace leaf→`RbxApi`, and all `Roblox*EditModeTests`/`RobloxApi4BLiveCheck*`
  → `Rbx*`. 25 source files renamed (`.cs`+`.meta`, GUIDs preserved); the three affected demo scenes
  updated. The word "Roblox" is intentionally kept where it names the actual platform — comments, XML
  docs, the agent skill text, loud-stub/log messages, and test-method descriptions that assert Roblox
  parity — so the Roblox-compatibility story stays legible. Pure rename: no behaviour change.

### Fixed

- **Two tests reconciled with 6.5.0 behaviour changes.** The `LuaCs_ModsCall_*_CannotDisarmHandlerGuard`
  guard tests now pin a tight per-handler step/time budget instead of relying on the (now Roblox-parity)
  default, so they still prove the outer guard survives a nested `mods_call`. The bundled-seeder test and
  the `BundledModSeeder` class doc were updated to match the shipped "a strictly-newer bundled version is
  canonical and ships, superseding a local edit (prior source kept in the store's revision history)"
  policy. Full EditMode suite green (2395/2395).

## [6.5.0] - 2026-07-24

3D click-picking, a playable block-clicker sample, Roblox-parity execution budgets, and reliable
mod versioning/delivery.

### Added

- **`ClickDetector` — Roblox-1:1 3D click-picking.** `Instance.new("ClickDetector")` parented to a Part
  makes that part clickable: `cd.MouseClick:Connect(function() ... end)` fires only when the actual part
  under the cursor is clicked (nearest raycast hit within `MaxActivationDistance`, default 32 studs) —
  empty space and other blocks fire nothing. Engine-free `RbxClickDetector` + an `IClickPickSource` seam
  (`UnityClickPickSource` raycast adapter, `InMemoryClickPickSource` headless default), pumped on the
  MouseButton1 rising edge; the idle per-frame path is allocation-free.
- **`sample_clicker` — a playable 3D block clicker (pure Roblox API).** Click the gold block to mine;
  click the upgrade blocks to buy (green pop on purchase, red flash when you can't afford, so a gray
  block still gives feedback). Coins and each upgrade's price are physical tier blocks; the passive
  upgrade block grows with its level and pulses green each time it earns. Ships disabled.

### Changed

- **Execution budgets raised to Roblox parity (~10 s).** A Luau script is only terminated after ~10 s of
  continuous execution, so a CoreAI mod is no longer cut sooner: one-shot and mod-handler wall-clock
  timeouts are now 10 s (were 2 s / 500 ms) with a high step ceiling as a secondary net. Mods run under
  the same headroom a Roblox script gets. (`IExecutionBudget`, `LuaCsExecutionGuard`,
  `LuaCsSecureEnvironment`, `LuaCsModRuntime`)
- **Lane Racer → 2.1.0, Tetris 3D → 3.0.0** — smooth per-frame lerped motion (no teleport), visible
  spawn drop-in, and the line-clear/crash scatter.

### Fixed

- **Mod card shows the authored header version, not a revision count.** A mod persisted via load/reload
  used to show its version as the number of stored revisions ("v3"); the manifest now takes `Version`
  (and `Name`/`Description`/`Category`) from the mod's `--[[@coreai ... version: ]]` header, falling back
  to the revision count only when the header omits a version. The seed lineage (Origin/Seeded*) is carried
  over so a runtime load never blanks it. (`LuaCsModRuntime.BuildManifest`)
- **A strictly-newer bundled version always ships.** A sample that had been opened/edited used to stick on
  its old version (the update downgraded to an "UpdateAvailable" flag); a newer bundled version now updates
  in place, with the prior source kept as a recoverable revision. (`BundledModSeeder`)
- **The Hub no longer collapses when you press Space.** The collapse ("–") button was focusable, so the
  game's Space (jump/action) triggered its NavigationSubmit and closed the Hub; it is now pointer-only.
  (`CoreAiHubWindow`)
- **Click-pick Y-flip is consistent under non-fullscreen cameras** — the pick ray now flips against
  `Screen.height` (matching the input source) instead of `Camera.pixelHeight`. (`UnityClickPickSource`)

### Docs

- Added the CoreAI-vs-Luau VM micro-benchmark results (`dev-docs/LUA_VM_BENCHMARK_PLAN.md`): Luau's
  interpreter is ~5–43× faster than the Lua-CSharp interpreter and ~7–127× with `--!native`; the gap is
  architectural (native register VM + JIT vs a managed C# interpreter), which is why mods stay thin.
- Sharpened the comment rule: `WHY:` is the rare exception, not a per-edit default. (`ARCHITECTURE_RULES.md`)

## [6.4.1] - 2026-07-24

### Docs

- The Rbx API skill examples now use `print(...)` instead of the CoreAI-only `report(...)` — the last
  non-Roblox call a model could copy out of the skill. Caught by the post-6.4.0 Roblox-conformance
  re-audit; the shipped samples and runtime were already clean. (`RbxApi.txt`, `BuiltInRbxApiSkillText`)

## [6.4.0] - 2026-07-24

Roblox-idiomatic game loop and pure-Roblox sample mods. The bundled samples were rewritten to use
only the Roblox API (so their Lua imports/exports 1:1 with Roblox), which required implementing the
per-frame hook Roblox games are built on.

### Added

- **`RunService.Heartbeat` — the Roblox per-frame loop.** `game:GetService("RunService")` now resolves
  (RunService left the MVP2 stub list) and exposes `Heartbeat`, `Stepped`, `RenderStepped` dispatching
  signals, pumped once per frame with the frame delta: `RunService.Heartbeat:Connect(function(dt) ... end)`.
  `dt` arrives as a real Lua number, so motion scales by seconds and is smooth/frame-rate independent.
  (`RbxRunService`, wired through the tick driver's per-frame pump.)

### Changed

- **Bundled samples rewritten in pure Roblox API** — `RunService.Heartbeat` instead of `hooks_every`,
  `print` instead of `report`, plain locals instead of `store_*`. So a sample's Lua is portable to/from
  Roblox. Lane Racer and Tetris 3D now move by `dt` (smooth), have correct camera framing (world +X =
  screen right, so A/D aren't mirrored), Anchored gameplay parts (no physics jitter), scatter the wreck
  on loss, and restart on R/Space. The buggy Full-tier `sample_camera_pulse` was removed.
- **`hooks_every` no longer rejects a sub-frame interval.** A timer fires at most once per frame anyway
  (never catches up), so a small/zero interval is a safe per-frame loop, not spam — it is clamped to 0
  ("every frame") instead of throwing. `hooks_every`/`report`/`store_*` remain a pre-MVP stopgap; new
  mods should use the Roblox idioms above. (`LuaCsModRuntime`)

### Fixed

- **Mod-owned signal connections are disconnected on unload/reload/quarantine.** A connection a mod made
  (`RunService.Heartbeat:Connect`, `UserInputService.InputBegan:Connect`, …) used to keep firing against
  the torn-down mod after unload (an `INSTANCE_DESTROYED` log). A `ModConnectionRegistry` now tracks
  connections by owning mod and `ModTearingDown` disconnects them *before* the instance sweep, so unload
  is clean. (covered by `RobloxSignalConnectionTeardownEditModeTests`)

### Docs

- The Rbx API skill (both `Resources/AgentSkills/RbxApi.txt` and `BuiltInRbxApiSkillText`) now teaches
  `RunService.Heartbeat` as the game loop, lists RunService as a service, and carries a camera-framing +
  controls recipe so AI-authored mods don't mirror left/right.

## [6.3.5] - 2026-07-24

Bundled sample mods overhaul — two fixes and two new playable samples, all authored against the
standard Roblox-style tier (no unity_* / Full) and verified live in Play Mode (load, run, input,
camera, and disable/unload cleanup).

### Fixed

- **`sample_welcome`** now logs a clear start line and ticks a counter exactly five times (once every
  two seconds) before pausing, resetting on each (re)load — a cleaner first lesson than the previous
  forever-every-30s tick.
- **`sample_camera_pulse` → Colour Pulse** no longer errors into quarantine. It called `unity_find_all`
  (Full tier); that withheld-capability error unwinds past `pcall`, so the intended graceful-degrade
  never fired and every tick logged a failure. Rewritten to spawn a Part and pulse its `Color3` via the
  standard API (`Instance.new` + `Color3.fromHSV`) — works under the default grant, no Full needed.

### Added

- **`sample_lane_racer` (Lane Racer)** — a 3-lane dodging mini-game: steer with A/D (or Left/Right,
  rising-edge so one press = one lane), weave past oncoming blocks, score as they pass, camera follows
  the car via `CameraSubject`. Ships disabled; opt in from the Mods tab.
- **`sample_tetris3d` (Tetris 3D)** — a compact falling-block puzzle in 3D cubes: A/D move, W rotate,
  S soft-drop, full rows clear, with a visible well frame (floor + walls) and a fixed angled camera set
  via `CameraType = Scriptable` + `CFrame.lookAt` (demonstrates full Roblox camera control — position
  *and* angle). Ships disabled; opt in from the Mods tab.

All four samples parent their spawned parts under a mod-owned Folder, so disabling or deleting a sample
removes everything it created (the runtime's instance-ownership sweep). Bundled-mod delivery is
version-aware: bumping a sample's header version reseeds it into an existing install's store.

## [6.3.4] - 2026-07-24

Optimization loop, iteration 2 (perf re-audit → fix → test). Continues past the 6.3.3 guard
pooling one stack frame higher.

### Performance

- **No per-call closure/delegate on the guarded hot path.** `LuaCsExecutionGuard`'s two call
  shapes used to wrap the VM call in a `Func<CancellationToken, LuaValue[]>` lambda that captured
  `state`/`closure`/`function`/`args`, so a display-class + delegate were heap-allocated on **every**
  guarded call — the same churn 6.3.3's pooled `GuardHook` removed one frame lower, left in place at
  the boundary. The scaffolding is now split into `BeginGuard`/`EndGuard` and each `Execute` overload
  inlines its own VM call, so a steady-state timer/event/`mods_call` allocates nothing here. Behavior
  is identical (re-entrancy, step/time/allocation trips unchanged; covered by the existing
  sandbox/re-entrancy suite). Deferred to a later iteration (need a Play Mode GC capture first):
  result-array/boxing on the discard path, per-event `handlers.ToArray()`, and the `System.Object`
  scripting-seam boxing.

## [6.3.3] - 2026-07-24

Optimization + correctness pass driven by an adversarial self-audit loop (audit → fix → test → verify),
with a Roblox-idiomatic mod-ownership cleanup and vision default change.

### Fixed

- **Mod-owned instances are destroyed when the mod unloads (no leaks).** Objects a mod spawns via the Rbx
  API (`Instance.new(...)` → `workspace`) are tagged with the mod's id in the registry; unloading the mod
  (Disable *or* Delete — both route through `RuntimeUnload` → `ModTearingDown`(Unload)) now sweeps
  `InstanceRegistry.GetOwnedBy(modId)` and `Destroy()`s each, releasing the backing GameObjects. Reload and
  quarantine intentionally do **not** sweep. (`CoreAiModsInstaller`; covered by
  `UnloadMod_DestroysInstancesTheModOwned`)
- **Streamed tool-call replay can no longer double-execute side-effecting tools.** In `MeaiLlmClient`, the
  text-extracted tool path did not add its executed calls to `streamedExecutedCallCount`, so a
  later-roundtrip transport failure threw bare and let the fallback client replay the same turn — running
  `world_command`/`execute_lua` twice. It now finalizes-and-reports like the native path.

### Changed

- **Vision support defaults to On under `Auto`.** `Auto` now assumes a model is vision-capable unless its
  name matches a known text-only utility marker (embeddings, rerankers, whisper/tts, moderation, guard),
  so local vision models (e.g. `qwen3.5-4b-mtp`) get camera frames without a manual toggle.
  (`VisionCapability`; test `IsEnabled_AutoDefaultsOn_ExceptUtilityModels`)

### Performance

- **Guard hot path is now zero-allocation and sampled.** `LuaCsExecutionGuard.ExecuteGuarded` no longer
  allocates a `LuaFunction` + capture closure + `Stopwatch` on every guarded call (hundreds/sec at 20 Hz
  across mods on the single-threaded WebGL Boehm GC); it rents a poolable `GuardHook` (built once, re-armed
  per call) from a `[ThreadStatic]` pool and times out via `Stopwatch.GetTimestamp()` against a precomputed
  ticks budget. The instruction hook now fires once per 4 instructions (was every instruction), cutting the
  per-instruction GC/timestamp reads 4× while keeping the same step ceiling and an allocation-bomb peak
  within ~one doubling of the budget. Re-entrant (`mods_call`) nesting rents a distinct hook so counters
  never clobber. (tests: step/timeout/normal-handler under the sampled hook)

## [6.3.2] - 2026-07-24

Hardening of the 6.3.1 chat/demo fixes after a multi-agent audit, plus the Builder build tool and
AI-Settings UX fixes — all verified live in Play Mode against a local LM Studio model.

### Fixed

- **Timeout idle-budget is now enforced end-to-end.** `TimeoutLlmClientDecorator` — the outermost
  per-request timeout — wrapped the whole streamed tool-calling turn in one fixed budget, silently
  negating 6.3.1's idle timeout for long multi-tool turns. Its streaming path now re-arms on each chunk
  too, so a steadily-progressing turn is never truncated; only a real stall times out. `OnToolCallFailed`
  also re-arms the chat idle deadline, and the role match tolerates an absent event role.
- **Role-switch history no longer leaks or drops answers.** Two bugs in the 6.3.1 per-role transcript
  cache: persisted-store restore and the welcome message were recorded back into the cache (unbounded
  growth on repeated switches), and streamed assistant replies were never cached (switching away and back
  restored the user's turns with no answers). Store restore + welcome are now render-only, and the
  streamed reply is recorded on completion. (`CoreAiChatPanel`)
- **The Builder role can build too.** `world_command` is now attached to both Creator and Builder
  (Builder's system prompt tells the model to use it); registration is idempotent (no duplicate tool
  name on a rebuilt/shared policy). (`WorldCommandsInstaller`)
- **Hub AI Settings:** the fetched-model list now writes the picked model into the HTTP model field (a
  neutral first entry makes the first pick fire), the "Advanced" foldout got a visible header style, and
  the backend Mode dropdown is not resynced while focused (so an in-progress pick is not clobbered).
  (`HubSettingsPage`, `CoreAiHubUss.uss`)

### Docs

- New `Docs/CoreAI/AGENT_ROLES_AND_TOOLS.md` (verified role → purpose → tools → capabilities reference);
  `AGENT_BUILDER.md` (missing `RoleId.Builder`) and `LLM_TOOLS.md` (world_command auto-attached to
  Creator/Builder; execute_lua/manage_mods/camera rows) corrected.
- Internal design/research notes moved out of user docs into a dedicated `dev-docs/` folder (multi-chat,
  mod-instance-ownership, materials research) — user documentation and dev documents are kept separate.

## [6.3.1] - 2026-07-24

Bug-fix pass on the FullAccess demo and chat, from live-testing the Roblox/Lua mod flow on device.

### Fixed

- **Chat turn timeout no longer accumulates over consecutive tool calls.** The chat request timeout
  (`LlmRequestTimeoutSeconds`) was a single deadline over the whole multi-step turn
  (LLM → tool calls → LLM → …), so a turn that made several quick tool calls in a row was cancelled even
  though every individual request was fast. It is now an **idle / no-progress** deadline: each streamed
  chunk (streaming path) and each `OnToolCallStarted`/`OnToolCallCompleted` for the turn's role
  (non-streaming path) re-arms it, so only a genuine stall times out. Per-request transport/decorator
  timeouts are unchanged. (`CoreAiChatService`)
- **Switching the chat agent/role no longer wipes the conversation.** Switching Programmer → Creator →
  back cleared the transcript because the panel reloaded only from the per-role persisted store, which the
  live session may not populate. The panel now keeps an in-memory per-role transcript and restores it on
  switch (store still wins when it has data; Clear purges the role's cache too). (`CoreAiChatPanel`)
- **The Creator and Builder roles can now build the world.** `world_command` (`WorldLlmTool`) was never
  attached to any role, though both roles' system prompts tell the model to place objects with it — so
  "create a castle" fell back to saving a blueprint to memory. It is now wired to both the Creator and
  Builder roles. The Programmer keeps building through the Lua/Rbx surface. (`WorldCommandsInstaller`)
- **The agent's screenshot capability is discoverable.** The vision capture tool was only named
  `camera_capture`, so a model searching for a "screenshot" tool missed it and fell back to blind
  read-only world queries. Added a `screenshot` alias (same capture) and made both descriptions lead with
  "Take a screenshot … to SEE the current scene". (`CameraLlmTool`)
- **FullAccess demo starts with an empty world** — the demo no longer auto-spawns a `TargetCube` in the
  center on startup; a target is used only if one is assigned in the inspector (the info page tolerates
  none). (`FullAccessHubDemoController`)
- **Hub Mods tab: Lua code is no longer clipped on the left.** The syntax-highlight overlay was positioned
  against the input's padding box with zero inset, cropping the first character of every line; it now
  carries the same padding as the input so text lines up. (`HubModEditorPage`)

## [6.3.0] - 2026-07-23

MVP1 "Roblox API" completion — a mod can now build, query, clone and destroy an instance tree,
read keyboard/mouse input, drive the camera, and pick a Part shape, all through the Roblox-1:1 Lua
surface. Plus the reasoning/think-block streaming fix and a per-frame allocation pass.

### Added

- **UserInputService (Roblox 1:1)** — `game:GetService("UserInputService")` (and a `UserInputService`
  global aliasing the same instance): `InputBegan`/`InputEnded`/`InputChanged` firing
  `(InputObject, gameProcessedEvent)` with a real `RBXScriptConnection` (`.Connected`/`:Disconnect()`,
  `:Once`), the poll surface (`IsKeyDown`, `GetKeysPressed`, `GetMouseLocation`, `MouseBehavior`), and
  `Enum.KeyCode`/`UserInputType`/`UserInputState`/`MouseBehavior` with exact Roblox names+values. Input
  reads open at the Read tier. Backed by a swappable `IInputSource` seam (Unity New Input System backend,
  headless in-memory backend for tests); pumped once per frame by the tick driver before mod dispatch.
- **`workspace.CurrentCamera`** — a real Camera instance with `CFrame`/`CameraType` (`Enum.CameraType`)/
  `CameraSubject`, over a swappable camera-rig seam; plus `camera_set_cframe`/`camera_follow` convenience
  globals. Reads ungated, writes WorldEdit-gated. (Pulled forward for mini-game controls.)
- **`Part.Shape`** (`Enum.PartType`) — `Ball` (unit sphere), `Cylinder` (axis-corrected mesh child),
  `Wedge` (custom normalized 1-unit=1-stud ramp mesh + convex collider); `CornerWedge` is accepted but
  draws as a Block until its mesh lands.
- **Rbx skill** (`read_skill("Rbx API")`) now documents input, camera and Shape, with a keyboard-driven
  mini-game example; the `Resources/AgentSkills/RbxApi` override stays byte-identical (pinned test).

### Fixed

- **Reasoning / think-block streaming**: `MeaiOpenAiChatClient` now surfaces `reasoning_content` /
  `reasoning` deltas as reasoning content (and promotes reasoning to visible content when a model emits
  no plain content), so reasoning-only models (e.g. qwen3.5-4b) no longer return empty responses. The
  raw C# SSE parser change also flows to the WebGL fetch transport.
- **Token budget**: `RoutingLlmClient` estimates usage when the server returns none, so the Token Budget
  Hub page and statistics populate instead of reading zero.
- **Long sessions**: history budget no longer collapses to an unlimited branch
  (`AiOrchestrator`/`AgentSessionInspector`), so summarization/budgeting stays in effect.
- **`Instance:Clone()`** now deep-copies BasePart part-sink state (Size/CFrame/Color/Anchored/Shape) onto
  the clone's fresh id, instead of resetting the copy to Part defaults.
- **FullAccessDemo invisible spawns**: `RobloxWorldHost` is wired into the mods lifetime scope.
- **Recursive `FindFirstChild`/`FindFirstChildWhichIsA`** now search depth-first (Roblox parity), not
  level-order.
- **`GetKeysPressed()`** returns keyboard keys only (gamepad buttons excluded); per-frame input events
  fire in device order.
- **Part appearance/collision** target the part's own visual — a Cylinder shape switch on a part that
  already has nested child parts no longer recolors/toggles the wrong object.
- **Shape visual identified by an owned reference**, not the child name "Shape" — a mod that names one
  of its own child instances "Shape" is no longer destroyed (and mistaken for the part's visual) on a
  shape switch.
- **Services and the canonical Camera are locked against a mod removing them** — `Destroy()` errors,
  `Clone()` returns nil, `.Parent = …` errors, and `game:ClearAllChildren()` skips them, so one mod can
  no longer brick `UserInputService`/Lighting/etc. for the whole shared world. Internal world teardown
  still tears them down directly.
- **Full-tier withheld stubs** also register when Full is granted but the Full surface is unwired, so a
  `unity_*` call raises the actionable error instead of a bare "attempt to call a nil value".
- **Reasoning promotion (streaming)** is whitespace-aware, matching the non-streaming path — a lone
  `"\n"` content delta from a reasoning-only model no longer suppresses promoting reasoning to the answer.
- **Hub AI Settings**: placeholder-over-text fix + Advanced foldout; temperature override defaults off.

### Performance

- **Binder**: each Part property setter re-applies only its own aspect (a per-frame `CFrame`/`Size` write
  skips the full re-materialization); Renderer/Collider/Rigidbody refs and the `MaterialPropertyBlock`
  are cached on the binding entry; the cube/sphere meshes and default material are cached once instead of
  a `CreatePrimitive`-plus-destroy GameObject per part.
- **`RbxScriptSignal.Fire`**: reuses the fire-snapshot buffer instead of a per-event `ToArray` copy;
  the input signals pass a cached boxed `false` for `gameProcessedEvent`, so no bool is boxed per event.
- **`RbxCFrame`**: multiply and equality read the struct's fields directly — no per-op `float[12]`.
- **Input event objects are gated on `HasConnections`** — a mouse-move frame with no `InputChanged`
  listener (the common case) allocates no `InputObject`; same gate on key/button edges.
- **Token estimation counts reasoning output** — when the server omits `usage`, reasoning chars now
  feed the completion-token estimate so reasoning-heavy models aren't undercounted on the budget page.

## [6.2.1] - 2026-07-23

### Fixed

- Package **lockstep** restored: the 6.0→6.2 version bumps advanced each package's `version` but left the
  internal `com.neoxider.*` dependency pins at `6.0.0`, so the CI "Package graph (lockstep + deps)" gate
  failed with 11 mismatches. All six packages now pin the shared version. (supersedes the `v6.2.0` tag,
  whose published `package.json` files still carried the stale `6.0.0` pins)

### Added

- `tools/bump_version.py` — one command bumps every `Assets/*/package.json` `version` **and** every
  internal `com.neoxider.*` dependency pin to a target version in lockstep, then self-verifies with the
  same rule the CI gate enforces (`python tools/bump_version.py 6.2.1`, or `--check` to verify only).

### Docs

- Root **README** audit: corrected the package count (six, including the new **CoreAI MCP Server**
  package), fixed a wrong tool class name (`WorldCommandTool` → `WorldLlmTool` / `world_command`), and
  reflected the 6.2.0 features (mod runtime self-heal, vision **Detect** self-probe, Hub sub-tabs).

## [6.2.0] - 2026-07-23

### Added

- Hub **AI Settings → Fetch models** (HTTP backend and endpoint editor): queries an OpenAI-compatible
  `GET {baseUrl}/models` and lists the advertised model ids in a dropdown, so an exact name can be
  copied into the model field instead of typed from memory. (`CoreAiBackend.ListModelsAsync`)
- Hub **AI Settings → Vision** override (Auto / On / Off): forces the vision/camera gate on a multimodal
  model whose name the auto-heuristic does not recognise (e.g. a local `qwen3.5` vision build), so the
  camera tool and image sends become usable without renaming the model. (`CoreAISettingsAsset.SetVisionSupport`)
- Hub **AI Settings → Vision → Detect**: a self-probe sends the model a synthetic image and reads back
  the answer to decide whether it can actually see, then sets the Vision gate accordingly — so Auto
  works even for a vision model the name heuristic misses. (`VisionSelfProbe`)
- Hub **sub-tabs**: a reusable `HubSubTabView` / `HubSubTabPage` groups several pages under one top tab.
  **Settings / Token Budget / Statistics** are now sub-tabs of a single **AI Settings** tab, keeping the
  top tab bar short; new sub-tab groupings are one registration.
- Hub **Lua mod editor** now renders **Lua syntax highlighting** in the inline editor (keywords,
  strings, numbers, comments) while keeping the field editable, including the `goto` keyword.
  (`LuaSyntaxHighlighter`, `LuaTokenizer`)
- Full Access demo now **self-heals a failing mod on its first runtime error**: a `CoreAiLuaModAutoRepair`
  component is wired into the demo Hub with the repair threshold lowered to one consecutive error, so a
  broken mod is handed back to the Programmer to fix without waiting for a streak.
- Programmer skill **`read_skill('Full Lua')`**: the full `unity_*` reflection surface moved behind an
  on-demand skill, so it stays available as a rarely-needed backup without bloating the base prompt.

### Changed

- **The Programmer builds the world Roblox-style only.** The low-level `coreai_world_*` build APIs
  (`spawn` / `change` / `set_color` / `destroy` / `spawn_batch`) are no longer bound for persistent mods
  or the one-off `execute_lua` executor; world building goes through the Rbx surface
  (`Instance.new('Part')`, `game` / `workspace`, `Vector3` / `CFrame` / `Color3`). The read-only queries
  `coreai_world_find` / `coreai_world_pos` / `coreai_world_exists` remain, and the `WorldEdit` capability
  still gates the Rbx surface. Prompts, tool descriptions, the Lua Modding skill and the gallery example
  Lua were updated to match. (`RegisterWorldEditBuildBindings`)
- Hub **Mods** and **Mod Logs** are now grouped under one **Mods** top tab with `[Mods, Logs]` sub-tabs
  instead of two separate top tabs, keeping the tab bar compact. (`HubModsPages`)

- Hub **API-profiles endpoint editor** redesigned: field visibility is applied on first render (HTTP
  endpoints no longer show LLMUnity-only fields), an **Advanced** foldout hides secondary fields
  (Endpoint ID, context window, ports, slots, secret reference, LLMUnity options), text fields carry
  placeholder hints, LLMUnity endpoints pick their model from a **GGUF dropdown**, an empty context
  window now means **no limit**, and endpoints are managed through a **list with a per-row Remove**
  instead of a picker plus one ambiguous Remove button.
- `execute_lua` result envelope is trimmed for the model: a null `Error` and an empty/`nil` `Output`
  are dropped, so a side-effect success serialises to `{"Success":true}` rather than
  `{"Success":true,"Output":"nil","Error":null}`.
- `execute_lua` **table return values now serialise to JSON**: `return coreai_world_list_prefabs()` /
  `coreai_world_find(...)` hand the model `["cube","sphere",...]` instead of an opaque `table: 0x…`
  address, so discovery tools are actually legible.
- Programmer system prompt: "CoreAI MoonSharp sandbox" → "CoreAI Lua sandbox" (the persistent mod
  runtime migrated to Lua-CSharp; MoonSharp is no longer instantiated in production).

## [6.1.3] - 2026-07-23

### Changed

- `CoreAIResourcesApiKeyBuildGuard` now **warns instead of failing the build** when a Resources
  `CoreAISettings` asset carries a non-empty `apiKey`/`secondaryApiKey`. WHY: a harmless local
  placeholder (e.g. an LM Studio key the server ignores) should not hard-block a build; the console
  warning still flags a real secret so it is not shipped by accident. The committed asset keeps its
  `lm-studio` placeholder and now builds an APK/EXE directly.

## [6.1.2] - 2026-07-23

### Fixed

- Android players of the mods-enabled demo failed to compile (`CS0234: the namespace 'Hub' does not
  exist in 'CoreAI.Ai'`): the `COREAI_HAS_HUB` scripting define was set for Standalone but missing on
  Android, so the `CoreAI.Mods.Hub` assembly (namespace `CoreAI.Ai.Hub`) was stripped from Android
  builds while a demo still referenced it. Added `COREAI_HAS_HUB` to the Android PlayerSettings defines,
  so the Full Access demo now builds an APK.

### Changed

- Mods tab rows now show an explicit bold **On / Off** label next to the enable checkbox. WHY: a bare
  checkbox did not read as on/off at a glance — the state only showed on hover or in the meta line — so
  it was unclear how to disable a mod. Editing is unchanged: the per-row **Edit** button opens the inline
  Lua editor.

## [6.1.1] - 2026-07-23

### Removed

- The **Full-Mode Mod** demo tab (`FullModeModHubPage`) is gone from the Full Access demo Hub. It was a
  ported IMGUI panel demonstrating the Full-tier `unity_*` reflection API by moving `TargetCube`, but it
  overlapped the live **Mods** tab and only half-worked unless Full Lua access was toggled on the scope —
  so it read as a broken/redundant tab. Its registration and the now-unused `fullModeModSourceOverride`
  field / helper were removed from `FullAccessHubDemoController`.

### Changed

- Collapsed Hub is now a legible launcher chip: it shows a **"CoreAI"** brand label next to the restore
  button instead of a blank dark bar with a lone floating "+". New `coreai-hub-title` element (shown only
  while collapsed; the tab bar is the header when expanded).

## [6.1.0] - 2026-07-23

### Added

- New built-in chat example **Clicker game** (`CoreAiChatExamples`): a ready-to-run Lua idle/clicker
  mod (left-click the golden cube to earn points, every 10 stacks a gold coin, passive income keeps it
  growing unattended, `r` resets) alongside the existing Tetris one. Both are "create a mod named X
  with this code and load it" prompts, so the Programmer agent only has to wrap the given code in a
  `manage_mods` call — the deterministic path that actually renders a playable game from chat. A
  parse-gate EditMode test (`ClickerExample_LuaParses`) validates the Lua on the real VM.

### Changed

- The Tetris chat-example mod now frames the scene itself: on load it drops the `Main Camera` straight
  in front of its board (`coreai_world_change('Main Camera', ...)`). WHY: the mod rendered correctly
  but the host scene's camera was left pointing elsewhere, so the board fell outside the view and the
  game looked like it "did nothing". A mod that builds a game should own the shot that shows it.

## [6.0.0] - 2026-07-22

### Removed

- `CoreAiBackendPanel` (the uGUI Canvas backend-switch panel, its Editor `CoreAiBackendPanelBuilder`
  and prefab) is gone — the Hub's **AI Settings** tab (`HubSettingsPage`) is the UITK replacement and
  edits the same Base URL / API key / model / execution mode through the unchanged `CoreAiBackend`
  facade. Panel-specific EditMode tests were dropped; the `CoreAiBackend` facade tests remain.

### Fixed

- Lua mod stores can now be namespaced per composition: `CoreAiModsLifetimeScope` gained an optional
  serialized `storeId` (plumbed through `RegisterCoreAiMods` into `FileLuaModStore` /
  `FileLuaModSourceStore`), and every mods-enabled demo scene sets a distinct id, so mods persisted by
  one demo no longer rehydrate in every other demo (and fail under a lower Lua tier). Empty `storeId`
  keeps the shared default path — the main game's store location is unchanged. A mod that still fails
  to rehydrate is now quietly skipped with a single warning (no error-level stack trace) while the
  remaining mods keep loading.

### Changed

- FullAccess demo is now a single UI Toolkit Hub window instead of five floating IMGUI/uGUI panels.
  Its Lua-platform (F6), info (F7), prompt-buttons (F8), mod-manager (F9) and token-budget (F10)
  overlays plus the uGUI backend panel became Hub tabs: Full Access, Full-Mode Mod, Prompts, Lua
  Platform, Token Budget alongside the built-in AI Settings / Statistics / Mods / World / Logs. New
  shared `DemoHubWidgets` UITK toolkit keeps demo pages visually consistent with the built-in Hub
  pages; `LuaPlatformExampleController` and `ChatPromptButtonsController` are now GUI-less drivers.

- Output-token policy: never cap LLM output tightly — every default/live-call `max_tokens` budget is
  now 128000 (effectively uncapped; the HTTP timeout is the real bound) or omitted entirely.
  `CoreAISettingsOptions.MaxTokens` / `OpenAiHttpOptions.MaxTokens` defaults went 2048 → 128000.
  WHY: reasoning models spend their budget in `reasoning_content` before answering, so a tight cap
  silently truncates the answer and masquerades as a model failure. Unit tests that assert budget
  arithmetic with small fake values are unaffected; the Opus preset keeps the provider's own limit.

### Added

- Luau syntax is now accepted everywhere Lua compiles: `LuauSourceGate` runs the engine-free
  downleveler before the Lua 5.2 VM at all three raw-source compile sites (mod load/reload incl.
  auto-repair, one-off `execute_lua`, AI envelope), so `+=`-family compound assignment, `continue`,
  backtick string interpolation, if-then-else expressions, and type annotations/casts just work.
  Fail-loud: malformed Luau surfaces as `LuauDownlevelSyntaxException` with line-tagged diagnostics
  instead of an opaque VM parse error; plain Lua 5.2 passes through byte-identically. Both skill-text
  pairs (RbxApi, LuaModding) now advertise Luau support.

- IMGUI ban ratchet fitness test (`ImguiBanRatchetEditModeTests`): runtime + demo trees are scanned
  for IMGUI tokens and any file off the shrink-only whitelist (seeded with today's 18 offenders)
  fails the suite; stale whitelist entries fail too, so every UITK migration must delete its line.
  Editor folders are soft-reported only. Companion `DEMO_INVENTORY.md` catalogs all demo scenes with
  UI tech and P1-P4 redesign priorities.

- Env-gated live check for the Rbx API against a real local model (`RobloxApi4BLiveCheck*`,
  LM Studio / `COREAI_TEST_BASE_URL`): three scenarios through the production factory + bindings +
  `execute_lua` executor; self-skips when no endpoint is served.

- Roblox API MVP1 wiring: `CoreAiModsInstaller` now installs `RobloxApi` on the production mod stack (headless in-memory by default, or bound to the `RobloxWorldHost` scene host when present), so the Roblox globals (`Vector3`/`CFrame`/`Color3`/`Enum`/`Instance.new`/`game`/`workspace`) are available and the persistent runtime + one-off `execute_lua` executor share one `InstanceRegistry` world.

- Roblox API MVP1 (materialization slice): `CoreAI.RobloxApi.Binding` Unity-adapter assembly
  (`Assets/CoreAIMods/Runtime/RobloxApi/Binding/`) with `InstanceGameObjectBinder` implementing
  the `IInstanceBackingBinder` seam over real GameObjects per D5 — Parts materialize as unit-cube
  primitives scaled `Size × RobloxSpace.MetersPerStud` (asset rule: geometry never rescaled, only
  numbers convert), Folder/Model/containers as empty transforms; the transform hierarchy mirrors
  the registry hierarchy; detach deactivates (not destroys), Destroy releases the GameObject.
  One-way MVP1 Part property push via the engine-free `IPartPropertySink` surface
  (CFrame/Position/Size/Color/Anchored/Transparency/CanCollide + `PartProperties` bundle with
  Roblox Part defaults): pose/size through `RobloxSpace` (D2 single boundary), color+alpha via
  `MaterialPropertyBlock` (`_Color`+`_BaseColor`, `Transparency == 1` hides the renderer),
  `Anchored` toggles a `useGravity: false` Rigidbody (DEV-6 per-body gravity lands MVP8),
  `CanCollide` toggles the collider; reverse physics→registry sync is out of scope until MVP8.
  `RobloxWorldHost` scene entry point (no statics, ARCHITECTURE_RULES §2) owns
  RobloxSpace-configure + binder + registry + `DataModelBootstrap` per scene. The
  `IInstanceBackingBinder` seam gained `OnReparented`/`OnNameChanged` hooks (registry fires them
  for materialized instances; in-memory fake logs `reparent:`/`rename:`). EditMode tests in
  `Tests/EditMode/RobloxApi/Binding/` cover hierarchy mirroring, park/reactivate, destroy
  cleanup, name sync, and the §5.1.8 item-11 goldens (stud cube 4×1×2 → 1.12×0.28×0.56 m at
  0.28 plus the 1:1 zero-asset-change check).

- Roblox API MVP1 (registry slice): engine-free `CoreAI.RobloxApi.Instances` Domain assembly
  (`Assets/CoreAIMods/Runtime/RobloxApi/Instances/`, `noEngineReferences: true`, zero references):
  `InstanceRegistry` as the single identity owner (roadmap §3.3 — `InstanceId` ↔ future Mirror
  `netId` ↔ CoreAI world name reconcile in one `InstanceRecord`, with the `OriginTag` ownership
  ledger `mod:`/`console:`/`ai:`), a monotonic id allocator partitioned by the top authority bit
  (server- vs locally-assigned; a wire-contract guard rejects locally-assigned ids), the Roblox
  `Instance` member core (`Name`/`Parent` with hierarchy validation, `FindFirstChild*` + ancestor
  trio, `GetChildren`/`GetDescendants`, `IsA` over a data-driven `ClassCatalog` for the MVP1 class
  set, `Clone` per R6.5, `Destroy` per R6.2 with `PARENT_LOCKED`/`INSTANCE_DESTROYED`,
  `GetFullName`, attributes per R6.7, tags per R6.8 on the CollectionService-substrate
  `InstanceTagStore`), `RbxDataModel` with ServiceProvider `GetService`/`FindService` semantics
  (exact Roblox `X is not a valid Service name` on unknown names; planned services raise
  phase-naming loud stubs), stable-id `InstanceTreeSnapshot` serialization for the MVP3 world
  file, the `IInstanceBackingBinder` seam (D5) with an in-memory fake (the Unity GameObject
  binder lands with the world-binding task), inert MVP2 signal hook points, and the §5.2.7
  structured `RbxError` surface. EditMode tests in `Tests/EditMode/RobloxApi/Instances/` cover
  the §5.1.8 registry items plus an architecture-fitness test mirroring the scripting
  seam-honesty tripwire.
- Roblox API MVP1 (datatypes slice): engine-free `CoreAI.RobloxApi.Datatypes` Domain assembly
  (`Assets/CoreAIMods/Runtime/RobloxApi/Datatypes/`, `noEngineReferences`, zero references) with
  pure-spec Roblox math datatypes — `RbxVector3`, `RbxVector2`, full `RbxCFrame` (all documented
  constructors incl. quaternion/matrix/lookAt/lookAlong/axis-angle/euler orders, axis vectors with
  right-handed `LookVector = -Z`, To/FromWorld/ObjectSpace, Lerp via slerp, Orthonormalize,
  component/euler/axis-angle decomposition, operator table), `RbxColor3` (new/fromRGB/fromHSV/
  fromHex/Lerp/ToHSV/ToHex), `RbxUDim`/`RbxUDim2`, Enum plumbing (`RbxEnumItem`/`RbxEnum`/
  `RbxEnumRegistry` seeded with Material/PartType/NormalId/Axis/RotationOrder; unknown-enum access
  raises the roadmap's loud stub), and deterministic seedable `RbxRandom` (xoshiro256**, floored
  seed, inclusive `NextInteger`, `NextUnitVector`, Fisher-Yates `Shuffle`, `Clone`). `tostring`
  formats match Roblox so corpus scripts can string-match.
- Roblox API MVP1 (Lua bindings slice): `LuaCsRobloxApiBindings`
  (`Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRoblox*.cs`, adapter layer — the only folder
  allowed to touch the VM) installs the roadmap §5.1.3 Lua surface into mod environments through
  the `IScriptFunctionRegistry` seam: datatype constructor globals (`Vector3`/`Vector2`/`CFrame`/
  `Color3`/`UDim`/`UDim2`/`Random`) as tagged userdata with shared locked metatables (operators
  `+ - * / - == tostring` per Roblox, methods, Roblox `tostring` formats), the interned `Enum`
  registry global (unknown enum/item raise the contract errors), `Instance.new` over the registry's
  scripted-creation whitelist (exact Roblox error for non-creatable classes; deprecated
  `parent` second argument works and logs once per mod), full instance member dispatch on thin
  per-instance proxies (Name/Parent/Archivable, navigation incl. child-by-name sugar,
  Clone/Destroy/ClearAllChildren, attributes/tags, `GetFullName`, `WaitForChild` immediate path),
  the `game`/`workspace` globals over one shared `InstanceRegistry` world, and
  `game:GetService` with the exact `X is not a valid Service name` / phase-naming stub texts.
  Ownership threads the gameplay-bindings owner-mod-id convention: mod-created instances get
  `mod:<id>` origin (hot-reload sweep via `GetOwnedBy`), one-off console scripts get `console:*`
  world-owned origin. Capability-gated per the existing tiers: Read = datatypes + navigation,
  WorldEdit = `Instance.new` + every mutation. Roblox-layer errors cross into Lua preserving the
  §5.2.7 `CODE: message | fix: ...` line verbatim (pcall-able); DEV-7 is enforced strictly at the
  Lua boundary (INSTANCE_DESTROYED on destroyed-instance member access, PARENT_LOCKED on
  re-parent). Loud stubs per §5.1.6: BasePart spatial properties + `Model:PivotTo/GetPivot`
  (→ Unity binder slice, MVP1 task 7), `signal:Connect/Once/Wait`, `task.wait/spawn/defer/delay/
  cancel`, absent-child `WaitForChild` (→ MVP2), `Instance.fromExisting` (backlog);
  `task.synchronize/desynchronize` are DEV-5 no-ops with a once-per-mod note. Wiring: opt-in
  `LuaCsModStackOptions.RobloxApi` shared by the persistent runtime and the one-off executor;
  `LuaCsApiRegistry` gained the engine-specific `RegisterValue` escape hatch for non-function
  globals (fresh per state). EditMode suite `Tests/EditMode/RobloxApi/LuaBindings/` (25 tests)
  runs corpus-style snippets through the real `LuaCsModRuntimeFactory` stack.
- `RobloxSpace` (`CoreAI.RobloxApi.Unity` adapter assembly) — THE single Roblox-to-Unity
  conversion boundary per roadmap D2/D3: configurable session-constant scale (default 1 stud =
  0.28 m), Z-mirror handedness bridge (Roblox right-handed `LookVector = -Z` onto Unity +Z
  forward; quaternion conjugation `(-x, -y, z, w)`), position/rotation/CFrame/velocity/direction/
  size/acceleration conversions both ways, plus an internal test-only scale reset hook for
  dual-scale EditMode runs.
- EditMode test suite under `Tests/EditMode/RobloxApi/Datatypes/`: CFrame golden fixtures
  (chirality, lookAt fallback, nested composition), `RobloxSpace` round-trip property tests at
  0.28 and 1:1 plus scale-config and `z = -z` fixtures, deterministic-Random tests, and the
  architecture-fitness tests keeping the Datatypes Domain engine-free and `RobloxSpace` the only
  conversion point (D2 lint rule).
- Editor Lua/Luau syntax highlighting for mod scripts: a `.luau` `ScriptedImporter` (mirrors the
  existing `.lua` importer, both producing a plain `TextAsset`), a custom `TextAsset` inspector that
  renders highlighted read-only source for `.lua`/`.luau`/`.lua.txt` assets (falling back to a plain
  text view for every other `TextAsset`), and a standalone `CoreAI/Lua Script Viewer` window with a file
  picker, drag-and-drop, a font-size slider, and copy-path/reveal actions. The lexer
  (`CoreAI.LuaAssets.LuaTokenizer`) and rich-text formatter are pure C# with no engine/editor dependency
  (`Assets/CoreAIMods/Runtime/LuaAssets`) so a future in-game console can reuse them; they classify
  keywords, strings (short/long/backtick interpolation), `--`/`--[[ ]]` comments, numbers (hex,
  exponent, underscore separators), function calls, Roblox/Luau globals (`game`/`workspace`/`task`/...),
  and — best-effort — Luau type-annotation colons vs. method-call colons. Very large sources are capped
  before rendering (`LuaSourceCap`, 64 KiB default) to keep the inspector responsive.
- Runtime-first multi-endpoint LLM contracts: dynamic HTTP, LLMUnity, and Offline endpoint descriptors,
  named routing profiles, role assignments, lifecycle snapshots, and safe add/update/activate/remove APIs.
- `AgentBuilder.WithLlmProfile(...)` and per-request `AiTaskRequest.RoutingProfileId`; an explicit request
  profile takes precedence over agent, role, default, and legacy routing.
- `ILlmEndpointSecretProvider` keeps credential resolution behind a portable host boundary; persisted
  descriptors contain a `SecretReference`, never the session credential.
- Portable `ILlmEndpointReadinessProbe` request/result contracts, shared OpenAI status policy, and
  `HttpClientOpenAiReadinessProbe` let ordinary .NET hosts validate endpoints without referencing Unity.
- `LlmEndpointDescriptor` behavior fields for HTTP endpoints — `MaxTokens`, `ReasoningMode`,
  `ThinkingBudgetTokens`, `ExtraBodyJson` — with portable `Validate()` rules, so per-endpoint request
  shaping is part of the persisted descriptor instead of a UI-only concern.
- `LlmRoleRouteSnapshot` — one atomic route observation (client, effective profile id, context window,
  execution mode, `IsRouted`) exposed via `ILlmClientRegistry.ResolveRouteForRole`, so callers can no
  longer pair one endpoint's client with another endpoint's metadata during a concurrent switch.
- Profile-aware `ILlmClient` capability queries: `SupportsNativeToolCallingForRole(roleId, profileId)` and
  `ResolveContextWindowTokensForRole(roleId, profileId)`, forwarded through the timeout, logging, retrying,
  and client-limited decorators.
- `LlmEndpointDescriptor.DeriveEndpointSlug` / `EnsureUniqueEndpointId` — the endpoint-id derivation used by
  the Hub editor now lives in the portable contract and is unit-testable.
- `ILlmClientRegistry.ReportRouteFailure(profileId, generation, errorCode, error)` — routing clients
  report endpoint-level request failures (expired credentials, unreachable backend) so registries can
  surface degraded health instead of keeping a stale Ready state; reports are generation-stamped
  (`LlmRoleRouteSnapshot.Generation`) so a late completion from a replaced endpoint cannot mutate its
  successor's health; default no-op for legacy registries.
- `CoreAISettings.UnlimitedContextWindowTokens` — the effectively-unlimited context-window sentinel used
  when a host asset has no explicit window override, so client-side history budgeting never binds and the
  provider enforces its own real limit.
- `ICoreAiChatOptions.ChatRequiresVisibleCursor` (default `true`) — chat hotkeys (open + Escape) only
  react while the mouse cursor is visible and unlocked, so first-person / locked-cursor gameplay keeps
  WASD and other keys instead of the chat stealing keyboard focus. Set `false` to restore the old
  always-on behavior.
- `CoreAI.Hub.IHubEscapeHandler` — optional hook a Hub page implements to get first refusal on Escape
  while it is active (e.g. stop an in-flight AI request) before the Hub falls back to collapsing itself.
- `ILuaLogService`/`LuaLogService` (`Assets/CoreAIMods/Runtime/Logging/`) — standalone Lua mod log
  service, independent of the Unity console: per-mod + global bounded ring buffers, thread-safe
  append/query, `LuaLogQuery` filter (mod id, min severity, since-sequence, text contains, max count),
  `EntryAppended` event, optional error-only mirror to `IGameLogger`. `LuaLogFormatter.ToPromptText`
  renders a compact, character-budgeted, LLM-friendly view for AI self-repair; `GetModLogsLlmTool`
  (`get_mod_logs`, read-only) exposes it as a tool. Optional off-by-default `LuaLogFileSink` rolls logs
  to `persistentDataPath/CoreAI/Logs`, flushed via `CoreAiWebGlPersistence.Sync()` on WebGL. MVP1 item 8
  (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`) — core only; not yet wired into the mod runtime's
  print/warn/error/runtime-error capture, DI composition, or the Programmer tool set (see `TODO.md`).

- Script-engine abstraction seam (Roblox roadmap MVP1 item 1): new engine-neutral contracts in
  `Assets/CoreAIMods/Runtime/Scripting` (`CoreAI.Scripting`) — `IScriptEngine`, `IScriptState`,
  `IValueMarshaller`, `IScriptFunctionRegistry` (+ `ScriptCallContext`/`ScriptCallResult` var-args
  shape), `IScriptTable`, `IScriptCoroutine`, `IExecutionBudget`/`ExecutionBudget`,
  `IScriptExecutionGuard`, `ScriptRuntimeException` + type-based
  `ScriptExecutionErrors.IsMemoryBudgetTrip`. The Lua-CSharp classes
  (`LuaCsApiRegistry`/`LuaCsSecureEnvironment`/`LuaCsExecutionGuard`/`LuaCsCoroutineHandle`/
  `LuaCsCoroutineRunner`, moved from `Runtime/Sandbox` with their GUIDs) plus new
  `LuaCsScriptEngine`/`LuaCsScriptState`/`LuaCsValueMarshaller`/`LuaCsScriptTable`/
  `LuaCsScriptExecutionGuard`/`LuaCsScriptCoroutine` adapters under `Runtime/Scripting/LuaCs`
  (`CoreAI.Scripting.LuaCs`) are now the single adapter layer, so a future VM swap reimplements
  `Scripting/` only. Scattered CLR-to-Lua conversions (registry `ToLuaValue`/`CoerceArgument`,
  runtime/logic-slots `HostToLua`/`ToClr`, cross-mod `ToPortable`/`FromPortable`) are consolidated
  behavior-compatibly into `LuaCsValueMarshaller`. EditMode coverage: marshaller round-trip truth
  table, typed + var-args registry dispatch, seam-level guard budget cut, coroutine resume, and a
  seam-honesty regression scan asserting no `using Lua` outside `Runtime/Scripting` (the tripwire the
  MoonSharp removal never had).
- Luau → Lua 5.2 downlevel preprocessor (`CoreAI.Infrastructure.Luau.LuauDownleveler.Process`,
  `Runtime/LuauDownlevel/`, standalone — not yet wired into mod loading): strips type
  annotations/declarations/casts and rewrites compound assignments (`+= -= *= /= //= %= ^= ..=`),
  `continue` (goto-free repeat-until-true form; repeat-loop conditions evaluated at the continue
  site per Luau scoping), backtick string interpolation (nested included) to `tostring` concats,
  `if-then-else` expressions to inline closures, floor division to `math.floor`, and Luau-only
  number literals (`0b...`, digit separators) — darklua's rule set as the reference spec.
  Hand-rolled lexer + recursive-descent rewriter over the full Luau grammar (no Loretta dependency
  closure; the API stays parser-agnostic so Loretta can be swapped in after an IL2CPP/WebGL smoke
  test). Plain Lua passes through untouched via a trigger scan; malformed input never throws —
  the original source returns with line/column Error diagnostics; deletions re-emit newlines so
  runtime error lines match the author's source. Side-effecting compound targets
  (`t[key()] += 1`) capture temps to evaluate exactly once. EditMode coverage: 93 tests —
  per-construct rewrites, strings/comments immunity, contextual keywords as identifiers,
  malformed-input passthrough, determinism, line preservation, six original Roblox-style corpus
  scripts parse-gated through the bundled Lua-CSharp VM, and semantic execution checks
  (associativity, floor rounding, continue flow in every loop kind, falsy if-expressions).

### Changed

- Mod error policy is now QUARANTINE, not unload: a mod hitting its consecutive-error threshold
  (`LuaCsModRuntime.MaxErrorsBeforeQuarantine`, default 8, configurable via
  `LuaCsModStackOptions.MaxErrorsBeforeQuarantine`; formerly the `MaxErrorsBeforeUnload` const) stops
  dispatching (handlers, timers, queued events) and reverts its logic-slot overrides to vanilla, but
  STAYS loaded and addressable — `manage_mods list` shows `quarantined: true`, `get_source`/
  `diagnostics` keep working, and a successful `reload` clears the quarantine and the error streak.
  This unbreaks the async "AI repairs a broken mod live" loop: a repair that takes minutes no longer
  races an auto-unload into a `not loaded` failure. New `ModQuarantined(modId, errorCount)` and
  `ModTearingDown(modId, LuaModTeardownReason)` runtime events (per-subscriber isolated), plus
  `LuaModInfo.Quarantined`; the auto-repair prompt and `manage_mods` guidance teach the quarantine
  workflow instead of reload-vs-load workarounds. Documented in `Docs/CoreAIMods/mod-system.md` §5a.
- `LuaCsModRuntime`'s gameplay-bindings seam gained the owning mod id
  (`Action<IScriptFunctionRegistry, LuaCapabilities, string>`); `LuaCsGameplayBindings.Register` and
  `LuaCsLogicSlots.RegisterApis` accept the owner so every `logic_define` override records which mod
  defined it.
- VM-neutral mod stack now depends on the scripting seam instead of Lua-CSharp types:
  `LuaCsModRuntime` (states, handlers, exports, guarded calls), `LuaCsLogicSlots`,
  `LuaCsGameToolExecutor`, `LuaCsAiEnvelopeProcessor` and every gameplay binder register through
  `IScriptFunctionRegistry` (`RegisterGameplayApis(IScriptFunctionRegistry)`);
  `LuaCsWorldRuntimeBindings` reads props via the neutral `IScriptTable` view;
  `LuaCsFullUnityRuntimeBindings` splits into a neutral reflection partial plus a
  `Scripting/LuaCs` marshalling partial. `LuaCsModRuntime`'s gameplay-bindings callback is now
  `Action<IScriptFunctionRegistry, LuaCapabilities>` (+ optional `IScriptEngine` parameter), and
  `LuaCsModRuntimeFactory` wires the single `LuaCsScriptEngine` as composition root. Mod-facing Lua
  behavior is unchanged; `ILuaCsGameRuntimeBindings` keeps its concrete-registry signature as the
  compatibility shape for existing demo/scene bindings.
- Lua/world-command composition is now owned by an optional child module instead of being presented as
  root CoreAI settings; legacy serialized scenes remain compatible during migration.
- Endpoint configuration now explicitly supports zero, one, or many providers, independent `Active` and
  `KeepWarm` policy, and tri-state session-key updates (`null` preserves, empty clears, non-empty replaces).

### Fixed

- `LuaLogFormatter.ToPromptText` truncation (hot-reload audit #11): when the character budget is
  exceeded it now keeps the NEWEST entries (the AI cares about recent events) instead of the oldest,
  emits the `...(+N more)` marker at the top (previously the end-appended marker could silently not
  fit, yielding truncated output with no marker), and coalesces identical consecutive messages into
  one `×N` line before budget accounting so log spam no longer eats the prompt budget.
- Stale-snapshot race in `LuaCsModRuntime.Tick`: the end-of-tick error-threshold check now re-resolves
  the live registry entry and verifies object identity before quarantining, so a repair's `ReloadMod`
  landing mid-tick (e.g. from a `ModHandlerErrored` subscriber) can no longer get the freshly repaired
  instance suspended (previously: unloaded) on the old instance's error streak.
- `logic_define` overrides no longer survive their mod: unload, reload (before the swap, keeping the
  replacement chunk's own fresh defines), and quarantine entry all clear the mod's logic-slot overrides
  via the new `LuaCsLogicSlots.ClearOwnedBy(modId)` teardown, so the game can never keep invoking a
  dead or broken mod version's formula while the AI sees "reload OK".
- Logic-slot override failures are no longer a silent revert-to-vanilla: `LuaCsLogicSlots` raises
  `OverrideFailed(ownerModId, slot, error)` and the runtime records it into the same handler-error
  channel as hook/timer failures (charging the owning mod's streak), so `manage_mods diagnostics` and
  auto-repair see which mod's formula broke.
- `HttpClientOpenAiTransport` now bypasses the system proxy only for loopback URLs; external OpenAI-compatible
  APIs retain the host platform's proxy policy for both non-streaming and SSE requests.
- Full-Lua composition tests now forget their persisted probe mod before disposing the container, so running
  EditMode tests cannot poison a later Hub/Chat startup with a test-only rehydration error.
- The `"fallback"` routing sentinel echoed back by retry decorators as an explicit request profile no longer
  fails resolution as "routing unavailable"; the registry re-resolves it as "no explicit profile" unless a
  real profile or endpoint literally named `fallback` exists.
- The orchestrator's tool strategy (native vs text tool-calling) now follows the endpoint the request is
  actually routed to — an agent pinned via `WithLlmProfile` or re-routed at runtime no longer keeps the old
  endpoint's tool contract.
- Context budgeting follows the routed endpoint: the orchestrator asks the routing client for the effective
  endpoint's context window and takes the minimum with the role's configured budget, instead of always using
  the global settings window.
- `COREAI_NO_LLM` builds compile again: `DelegateLlmTool` no longer references the stripped
  `ToolExecutionPolicy` when the LLM module is compiled out.
- `LlmEndpointRemovalMode.CancelInFlight` semantics documented and enforced: registries that cannot prove
  cancellation throw `NotSupportedException` instead of reporting a false success or an ambiguous `false`.

## 5.8.10 - Live model-behavior verification; fix a false-failing memory-clear test (2026-07-13)

### Fixed

- **`AllToolCalls_MemoryTool_WriteAppendClear` no longer fails when the model clears memory correctly.** The
  test asserted the `clear` tool REMOVED the store entry (`!store.TryLoad(...)`), but the memory tool's
  `clear` action EMPTIES the document (`MemoryMutationPlan.Change("")`) and keeps the record by design
  ("clear empties memory"). So the run failed even though `HasCompletedMemoryAction("clear")` already proved
  the model emitted and completed the real `memory(action=clear)` call and the document was empty. It now
  asserts "no entry OR empty content", matching the documented clear semantics; the misleading "model
  responded with text instead" warning is corrected (the model DID call the tool).

### Verified (live model behavior — LM Studio, reference model `qwen3.5-4b-mtp`)

- Ran a representative subset of the LlmVerification PlayMode suite against a live OpenAI-compatible endpoint:
  tool-calling, custom agents (ToolsOnly / ToolsAndChat / ChatOnly / WithAction), skill self-service
  (read-then-use), skill-tool proxy, skill tool discovery, memory write/append/clear, and the `execute_lua`
  Lua-authoring pipeline (the model writes correct sandbox-scoped Lua) — all pass. The tool-call/skill design
  is sound: a 4B model handles the full surface correctly, and the tool contract explicitly guards against
  narration-instead-of-action. (Z.AI had no balance and no spark/opencode OpenAI endpoint was available, so
  the documented local benchmark reference model was used.)

## 5.8.9 - Demo/benchmark review wave: per-scene gameplay-binding seam + honest fixes (2026-07-13)

### Added

- **`LuaCsModStackOptions.AdditionalGameplayBindings`** — an optional per-scene/host
  `Action<LuaCsApiRegistry, LuaCapabilities>` fed, alongside the built-in world/data/prefab surface, into BOTH
  the persistent runtime and the one-off `execute_lua` executor. Lets a scene inject its own Lua APIs (e.g. a
  demo's `forge_define`/`forge_spawn`) through the existing runtime seam without replacing the core surface;
  it runs AFTER the built-ins so it can add to or override them. Covered by
  `LuaCs_AdditionalGameplayBindings_ReachLoadedMods` (an injected API is callable from a loaded mod's handler).

### Fixed

- **Skills demo no longer throws an NRE when the LLM module is uninitialized.** `SkillsDemoController.Start`
  now guards `CoreAIAgent.Policy == null` before `ApplyToPolicy` and disables gracefully (mirroring the
  DirectorAi demo), instead of an uncaught `NullReferenceException` in `Start`.
- **LiveMechanicsModsChat: a mod activated from the panel button keeps its Full tier.** `ActivateSavedMod`
  hardcoded `LuaCapabilities.All`, so a Full-tier mod (using `unity_*`) silently lost those calls when
  activated from the panel, while the same mod worked when autoloaded at scene start. It now computes the same
  Full-aware capability as the autoload path (`All | Full` when the scope has Full Lua access enabled).
- **Benchmark G6 `clean_tools` no longer passes vacuously for a do-nothing run.** The "no failed tool calls /
  invalid commands" checkpoint trivially held for a run that issued zero tool calls; it now also requires
  `ToolCalls >= 1`, so "clean" means "acted cleanly" rather than "did nothing".

### Docs

- ModdableUnits demo relabelled honestly as aspirational: the `forge_*` scene bindings are authored but not
  yet threaded through the demo's composition layer to running mods (the runtime seam now exists — see Added).
  Tracked as `TODO(moddableunits-binding-seam)` with the exact remaining wiring.

## 5.8.8 - Eighth re-audit: close a coroutine.wrap host-hang; correct the allocation-guard model (2026-07-13)

### Fixed

- **`coroutine.wrap` was an unguarded host-hang / allocation-bomb vector — now removed (CRITICAL).** 5.8.7
  left `coroutine.wrap` native while only wrapping `coroutine.resume`. `wrap`'s returned resumer drives a
  hidden CHILD `LuaState` through the library's OWN internal resume, bypassing the guarded `coroutine.resume`,
  so a wrap body ran with NO step/time/alloc hook: `coroutine.wrap(function() while true do end end)()` hung
  the game thread forever, no cut, no unload. It cannot be safely re-armed on this Lua-CSharp build (a C#
  reimplementation's returned function did not round-trip as callable; a Lua redefinition needs a
  sync-over-async `Load` in `Create()` that deadlocks during domain reload — the 5.8.7 deadlock). It is now
  stripped (`coroutine.wrap = nil`): mods use the guarded `create` + `resume` pair, and calling the absent
  `wrap` raises a clean nil-call error instead of hanging. No mod/demo in the repo used `wrap`.

### Changed

- **Corrected the allocation-guard model to match how it actually behaves, and simplified the charging path.**
  The 5.8.1–5.8.6 design tracked memory trips on a separate "capped streak" meant to unload a mod that
  allocation-bombs on every call. Runtime measurement showed that premise is false: `GC.GetTotalMemory`
  reports the COMMITTED-heap high-water mark, so a repeated fixed-size bomb trips only ONCE — the first call
  grows the heap and trips; every later call reuses that committed space and its per-call delta no longer
  crosses the budget (a mod bombing every tick under an 8 MB budget tripped ~once across 36 ticks, even with a
  forced `GC.Collect()` between ticks). The allocation guard is therefore a per-call FIRST-GROWTH backstop,
  not a cross-call cumulative limiter (Unity's Mono exposes no per-call/per-thread allocation counter to build
  one). A memory trip is now charged to the ordinary consecutive-error streak (reset on success) like any
  failure — the once-per-lifetime trip is forgiven by the next success, so a blameless mod is never unloaded
  by shared-heap noise, and a mod that keeps allocating within the committed envelope is bounded by the
  per-call step/time budgets. The unreachable separate memory-trip counter/streak and its unload branch are
  removed; `LuaMemoryBudgetException`/`IsMemoryBudgetTrip` remain (unforgeable, type-based) for the trip's log
  label. Guard/runtime comments rewritten to state this behaviour honestly.

### Tests

- Added `Coroutine_Wrap_IsRemoved_UnguardablePrimitiveCannotHang` (asserts `coroutine.wrap` is nil and that
  reaching for it raises promptly rather than hanging — a re-native regression would time the test out) and
  `LuaCs_SingleMemoryTrip_ChargedButForgivenByNextSuccess_DoesNotUnload`. Documented (with the empirical
  evidence) why an "every-call bomb is unloaded via a memory streak" test is intentionally absent.
- Verified via batchmode: mods EditMode 118 passed / 0 failed; full EditMode green.

## 5.8.7 - Seventh re-audit: runtime-validate the sandbox guards under batchmode; remove a domain-reload deadlock (2026-07-13)

### Fixed

- **Removed a domain-reload deadlock in the Lua sandbox.** 5.8.4's coroutine-wrap-via-Lua setup ran
  `state.ExecuteAsync(setup).GetAwaiter().GetResult()` inside `LuaCsSecureEnvironment.Create()`. On a thread
  carrying a `SynchronizationContext` (the editor main thread during a domain reload) that sync-over-async
  wait DEADLOCKED the whole editor. `coroutine.wrap` is now left native (guarded transitively via the resume
  path — see `TODO(coroutine-wrap)`), and only `coroutine.resume` is wrapped to arm the per-resume guard.
- **Allocation guard trips on the reliable cheap heap reading.** 5.8.1–5.8.3 gated the trip behind a
  debounced forced-GC confirmation (`GC.GetTotalMemory(true)`) against a garbage-inclusive baseline; on
  Unity's Mono that under-counted and let a doubling-concat bomb reach OutOfMemory before the trip fired.
  Both the main guard and the per-resume coroutine hook now trip on the monotonic cheap reading
  (`GC.GetTotalMemory(false) - baseline`), matching the original design — real bombs are stopped without
  OOM. A memory trip still CUTS the run but is no longer streaked toward auto-unload (transient process-heap
  noise must not unload a blameless mod); a genuine repeat offender is still unloaded by the step/time
  budgets, which ARE charged to `ErrorCount`.
- **Memory-budget trips keep the `LuaRuntimeException` outer type**, with the dedicated
  `LuaMemoryBudgetException` as the CLR cause detected by walking the `InnerException` chain — restoring the
  sandbox error contract while staying unforgeable and pcall-safe.

### Tests

- Made the Lua sandbox/mods EditMode fixtures batchmode-safe (the interactive Unity Test Runner freezes on
  these sync-over-async guard paths by design — batchmode is the reliable runner): allocation-bomb tests run
  under a bounded custom guard (≤64 MB) with a capped doubling count so a guard regression fails the assert
  instead of OOM-ing the process; runaway/coroutine tests use create+resume and assert the cut; dropped the
  redundant 8-cut runaway-unload variant (see `TODO(guard-tight-loop-latency)`).
- Verified via batchmode: **EditMode 1570 passed / 0 failed**; **PlayMode FastNoLlm 56 passed / 0 failed**.

## 5.8.6 - Sixth re-audit: coroutine guard arms only on a suspended coroutine (close re-entrant-ancestor disarm) (2026-07-13)

### Fixed

- **Coroutine guard no longer disarms a running ancestor coroutine (or the main thread).** 5.8.5's
  self-resume guard only excluded the IMMEDIATE caller, so a mod could have coroutine B resume a distinct
  ancestor A that was still executing higher in the call chain: the wrapper overwrote A's live guard hook,
  native resume rejected A (non-suspended) without running, and the `finally` nulled A's hook — leaving A
  unguarded when control unwound (an unbounded-loop/allocation DoS bypass). Arming is now gated on
  `LuaState.CanResume` (suspended-and-resumable): any resume of a state already executing in the call chain
  (self, ancestor, or the main thread) is non-suspended, so its existing guard hook is never touched.

## 5.8.5 - Fifth re-audit: complete the coroutine guard (allocation budget, self-resume, error fidelity) (2026-07-13)

### Fixed

- **Coroutine guard now enforces the ALLOCATION budget, not just step + time.** 5.8.4's per-resume hook
  omitted the allocation backstop, so a doubling-concat bomb inside `coroutine.wrap/resume`
  (`local s=string.rep('x',1e6); for i=1,30 do s=s..s end` — only ~30 VM steps, unbounded memory) still
  OOM-crashed the player. The coroutine hook now samples the process heap between instructions with the same
  debounced forced-GC confirmation and dedicated `LuaMemoryBudgetException` as the main guard.
- **Coroutine guard no longer disarms itself on a self-resume.** The hook is armed/cleared only when the
  resume target is a DISTINCT suspended coroutine state; resuming the caller's own running state (which
  native resume rejects anyway) no longer strips the hook the outer guarded call installed, closing a
  re-opened unbounded-loop DoS.
- **`coroutine.wrap` preserves the original Lua error value/type on re-raise.** The reimplemented wrapper
  re-raises the actual error `LuaValue` instead of stringifying it, so a mod that does `error({code=…})` and
  inspects it via `pcall` sees the original object, matching the native library.

## 5.8.4 - Guard mod-created raw Lua coroutines (fifth re-audit finding) (2026-07-13)

### Fixed

- **Mod-created raw Lua coroutines are now step/time-guarded.** `coroutine.resume` and `coroutine.wrap` are
  wrapped so every resume arms a per-resume step + wall-clock budget hook on the coroutine's own child
  `LuaState` (mirroring `LuaCsCoroutineHandle.Resume`) and clears it afterwards. This closes a DoS where an
  unbounded loop inside a coroutine (e.g. `coroutine.wrap(function() while true do end end)()`) escaped
  every sandbox budget because the native library runs the body on a child state that never inherited the
  `LuaCsExecutionGuard` hook. Fail-safe: if a Lua-CSharp build does not surface the coroutine state, the
  wrappers still delegate `coroutine` semantics unchanged (no behaviour break). **Runtime behaviour needs
  in-editor validation** — coroutine VM execution cannot be exercised by the `dotnet build` compile gate.

## 5.8.3 - Fourth adversarial re-audit: refine the allocation debounce and scope error redaction (2026-07-13)

### Fixed

- **Allocation-guard debounce no longer ratchets the ceiling.** The forced-GC confirmation watermark was set
  to the garbage-inclusive cheap heap reading, so a transient collectible spike inflated the next-confirm
  threshold and could let a mod retain live memory meaningfully above budget without re-confirmation
  (fail-open). The watermark is now capped at the budget, bounding overshoot to a single step.
- **HTTP-error redaction scoped to 401.** 403 (forbidden / permission / geo-block / model-access) responses
  are diagnostic, not credential echoes, so their provider message and log body are kept (truncated) rather
  than fully blanked and mislabeled; only 401 (invalid credentials, which can echo the key) is redacted.

## 5.8.2 - Third adversarial re-audit: close residual gaps in the 5.8.1 fixes (2026-07-13)

### Fixed

- **Memory-trip laundering closed for real.** The 5.8.1 fix used a sticky per-run flag, so a mod could
  trip the budget INSIDE `pcall` (which swallows the trip and arms the flag), then throw an unrelated
  `error()` that the catch laundered into a blameless "memory trip" — re-opening the auto-unload evasion.
  The trip is now matched by the EXACT exception instance (reference identity through any VM re-wrap), so a
  swallowed trip can no longer reclassify a later real error; that error is charged to the error streak.
- **Provider HTTP-error redaction completed for JSON bodies.** The 5.8.1 fix only redacted the non-JSON
  fallback; a JSON 401/403 body's parsed `error.message` (which can echo the submitted key) still reached
  `LlmClientException.Message` and the log. Now 401/403 messages use the redacted detail, and other statuses
  truncate the parsed provider message. The raw body remains available via `ProviderErrorBody`.
- **ImportMod capability-tier comment corrected:** re-importing an ALREADY-LOADED mod keeps its current
  tier (a reload cannot escalate a live mod from an untrusted header); changing the tier requires
  unload/forget then re-import under the desired grant.

## 5.8.1 - Adversarial re-audit of 5.8.0: fix regressions/incomplete-fixes the wave introduced (2026-07-13)

### Fixed

- **Memory-budget trips are classified by a dedicated `LuaMemoryBudgetException` TYPE, not a message
  substring.** A mod could previously put `EXCEEDED_MEMORY_BUDGET` into its own `error("…")` text so its
  real crashes were misclassified as blameless memory trips and never charged toward auto-unload. The
  guard now raises a dedicated type the caller detects by `is`, so a forged message cannot dodge the guard.
- **A genuine allocation-bomb mod is still unloaded.** Memory trips remain uncharged to the general error
  streak (a process-heap false positive can trip a blameless mod), but a separate capped
  consecutive-memory-trip streak (`MaxMemoryTripsBeforeUnload`, reset on any successful call) unloads a mod
  that trips on every call and never completes.
- **The confirming forced GC is debounced.** A process heap that legitimately sits above budget no longer
  induces a full blocking GC on every instruction; the confirmation re-runs only once the cheap reading
  climbs a further step (a doubling bomb still trips promptly).
- **Imported mods persist HOST-MASKED capabilities.** A Full-declaring bundle imported without host Full no
  longer records Full in the store, so a restart's `RehydrateFromStore` — even under a host-wide
  `allowFull=true` — cannot re-grant Full to a mod that was imported without it.
- **Provider HTTP error bodies are redacted at the source.** The raw body no longer leaks through the
  thrown exception's message (and thus downstream `result.Error` logs); the full body stays available
  programmatically (retry-window parsing) via `ProviderErrorBody`.
- **Audit-log docs corrected:** a clean trailing truncation is caught by `Seq` / `ChainReset` /
  `VerifyChainedSet`, not by single-file `AuditLogVerifier.Verify` — the threat model no longer implies it.

## 5.8.0 - Hardening wave: deep audit (runtime / architecture / tests / security), all findings fixed (2026-07-13)

### Fixed

- **CoreAiEvents dispatch hardened.** `Publish` now isolates each subscriber (per-handler try/catch) so
  one stale/throwing handler can no longer break dispatch to the rest; all subscribe/unsubscribe/publish/
  clear operations are guarded by a lock for off-main-thread raises. The Unity layer now clears the bus on
  play-mode entry (see host changelog), fixing a cross-session leak with Domain Reload disabled.
- **Nested `mods_call` can no longer corrupt a caller's open world transaction.** The shared Lua-CSharp
  world bindings used one `_txBuffer`/`_txActive`, so a nested call's `coreai_world_begin`/`commit` flushed
  or cleared the caller's still-open transaction. Transaction state is now a per-run frame stack
  (`ILuaTransactionScope.Push/PopTransactionScope`), pushed around every guarded handler/timer call, nested
  `mods_call`, and load chunk — begin/commit/rollback stay isolated with correct nesting.
- **Process-heap allocation-bomb trips no longer auto-unload blameless mods.** The backstop reads the
  whole-process managed heap, so unrelated allocations could trip a healthy mod and 8 trips unloaded it. A
  trip now confirms with a forced GC (only live memory counts — real bombs still trip) and is no longer
  charged toward the consecutive-error streak. Step and time guards stay real.
- **Hub Mods tab can no longer self-escalate an imported mod to Full.** `CoreAiModsHubBinder.allowFullTier`
  now defaults to false, and imported/shared/rehydrated mods never derive the Full (reflection) tier from
  their own header — Full requires an explicit host opt-in. Documented in `LUA_ACCESS_MODES.md`.
- **LLM prompt/response content is gated behind `LogLlmInput`/`LogLlmOutput` in the logging decorator**
  (was logged unconditionally, ignoring the flags the HTTP client already honored); non-sensitive metadata
  (traceId, role, char counts, tokens, budget) still logs. Provider HTTP error bodies are truncated in logs
  and 401/403 bodies are never logged (an auth body can echo the submitted key).
- **Audit hash chain no longer overstated as tamper-evident.** The default unkeyed SHA-256 chain is an
  integrity checksum (accidental corruption / truncation / reordering), not proof against the party that
  owns the local file; docs corrected. Added an opt-in HMAC-SHA256 keyed chain (`AuditHash.HmacChain`,
  `AuditLogVerifier.Verify(path, hmacKey)`) for genuine tamper-evidence when a host holds a key the file
  owner never sees. Additive; the default writer path is unchanged.

### Changed

- **CoreAI.Benchmarking is now a cross-platform runtime assembly** (removed the Editor-only platform lock);
  the engine-agnostic benchmark scoring/reporting types run in built players, fixing a RUNTIME-first
  layering gap where a PlayMode suite depended on an Editor-locked assembly.
- **`CoreAIFacade.cs` renamed to `CoreAIAgent.cs`** to match the `CoreAIAgent` type it defines.

## 5.7.0 - Hardening release: five adversarial audit waves (2026-07-12)

### Fixed (2026-07-12 audit wave 5 — adversarial review of wave 4, core)

- **`DelegateLlmTool` bodies can no longer be double-executed.** Host delegate exceptions are converted
  to `"Error: …"` results at the wrapper (matching first-party tools): any fault observed after the
  invocation went async is provably a body error (MEAI argument binding is synchronous — this covers
  non-async lambdas returning a `Task`), and synchronous faults are classified by delegate stack frame
  plus the conversion-shape heuristic. Residual: a *synchronous* body throw of a conversion-shaped
  exception with stripped frames (IL2CPP) may still escape as never-invoked. Cancellation propagates.
- **Decorator-level timeouts are reported as `Timeout`, not `Cancelled`.** When the timeout decorator's
  own linked token fires, an inner `Cancelled` result/terminal chunk is reclassified to `Timeout`
  (caller-token cancellation untouched) — the typed-timeout contract works on the non-streaming path
  again.
- **Fold marker hardening:** strict grammar (only `[fold:v1:` + 12-hex groups + `]` counts), `Strip`
  removes only the authentic final-line marker (marker-shaped user prose survives), LLM compaction
  output is stripped before stamping, the session inspector strips the marker from display and token
  estimates, and fold detection skips only *proven-folded* occurrences — a pruned watermark plus a
  later verbatim duplicate (or recurring empty messages) can no longer silently drop unsummarized
  history. Duplicate skipping compares content hashes, not `ChatMessage` struct equality (which
  includes the timestamp), so convergence holds with real timestamps — repeated short replies ("ok")
  no longer pin the fold point and re-summarize a growing region every turn.
- **Scoped lossy keys hash the trimmed id** (padded and unpadded ids map to the same key; affects only
  unreleased hash-suffixed keys).

### Fixed (2026-07-12 audit wave 4 — adversarial review of wave 3, core)

- **Argument-conversion rejections no longer block retries.** A tool call whose arguments MEAI could
  not coerce into the delegate's parameter types (conversion fails before the tool body runs) is
  traced as `arg-conversion` and treated as never-invoked by the retry/fallback replay guard.
- **Scoped memory keys: GUID-shaped ids keep their legacy keys.** Injectivity now comes from using
  the RAW id's length in the key prefix for lossy values (a literal hash-suffixed id has a different
  raw length by construction) instead of remapping lossless ids that merely look hash-suffixed —
  which would have orphaned every GUID-keyed scope's persisted memory on upgrade.
- **Rolling-summary fold state is an explicit marker, not bullet-text inference.** The persisted
  summary ends with a `[fold:v1:…]` line carrying content hashes of the last 8 folded messages; the
  fold point survives pruning/write-side trimming of individual messages, whitespace-only prefixes
  converge, verbatim duplicate messages cannot truncate the fold, and legacy wave-2/3 formats migrate
  with at most one re-summarize. The marker is stripped from every snapshot/LLM-facing string and is
  stamped after the token cap (the limiter can never trim it).
- **Terminal `PromptTokens` is cumulative again** (`Prompt + Completion == Total` restored for every
  usage/cost consumer); the prompt-size calibration reads a new dedicated
  `LastRoundtripPromptTokens` field instead, and zero-emitting providers cannot pollute it.
- **Removed the broad internal-cancellation→Timeout mapping** in the Unity MEAI client: both HTTP and
  WebGL transports already surface their timeouts as typed `Timeout` exceptions, so teardown/marshaler
  cancellations are no longer mislabeled retryable and replayed against the fallback provider.
- **`AgentMemoryPolicy` trims role ids on every public entry point** (`AddToolForRole` etc. — a padded
  id used to populate a different bucket than `SetToolsForRole` cleared).

### Fixed (2026-07-12 audit wave 3 — adversarial review of wave 2, core)

- **Rejected tool calls no longer block retries.** The replay-safety guard now distinguishes traces of
  tools that actually ran from rejected ones (duplicate-suppressed, parse errors, unknown tool names) —
  a hallucinated tool name followed by a 429 is retried and can fall back to the secondary provider
  again; anything that truly executed still suppresses replay.
- **Transport-internal timeouts surface as `Timeout`, not `Cancelled`.** A backend that never sends
  response headers used to be classified as user cancellation — non-retryable and never falling back.
  Non-caller cancellation now throws a typed timeout (both streaming and non-streaming), and
  `TimeoutException` maps to `LlmErrorCode.Timeout`.
- **Terminal `PromptTokens` reports the last roundtrip, not the whole-turn sum.** The cumulative usage
  fix in wave 2 inflated the prompt-size EMA by ~N× on N-roundtrip tool turns, causing premature
  compaction; completion/total stay cumulative for cost metrics.
- **Rolling-summary watermark matching is exact.** The fold-start probe requires a whole-final-line
  match (empty messages never match, a short message no longer matches inside a longer stored bullet,
  duplicated messages no longer fold to the wrong spot), and whitespace-only messages are never stamped
  as the watermark.
- **Summarization-off no longer disables overflow recovery or pruning.** With
  `EnableConversationHistorySummarization=false`, context-overflow retries shrink the history budget
  (was: byte-identical oversized retries) and `EnableContextPruning`/`MaxRetainedToolResultMessages`
  still apply.
- **`ConversationRolledSummaryMaxTokens = 0` means unlimited again** (the documented contract); the
  2048 cap remains only as the interface's default value for fresh installs.
- **Scoped memory keys are injective.** A raw scope id that itself ends in `-<12 hex>` (the hashed-key
  shape) now gets its own hash suffix, so an attacker-chosen literal id can no longer collide with
  another user's hashed key and share their memory bucket; plain ids and the empty-segment `_` are
  unchanged (no key migration).
- **`Clear` really clears agent memory**: version history and the system-prompt snapshot are wiped and
  the snapshot version bumped (cleared memory can no longer be re-injected into system prompts), and a
  corrupt memory file is rewritten with a warning instead of silently keeping its content.
- **Write-side history trim is a backstop, not a window** — raised to 500 chat messages / 2000
  transcript entries so roles configured above 30 no longer lose persisted history at append time.
- **Audit verifier reports chain resets** (`ChainResetCount`, mid-file reset warning) so tail-truncation
  forgery via a self-hashed `ChainReset` line is operator-visible instead of silent.
- **`AgentMemoryPolicy.SetToolsForRole` trims the role id** (an untrimmed id silently skipped the skill
  meta-tool re-assert).

### Fixed (2026-07-12 audit wave 2 — core)

- **Retries can no longer double-execute tools.** A failed completion that carries executed-tool evidence
  is not replayed by the HTTP retry loop or the fallback chain (the failure propagates instead of
  re-mutating the world); error results now retain `ExecutedToolCalls`, and the streaming replay-safety
  guard is cumulative across tool roundtrips (a failure after a tool-only roundtrip no longer looks
  pre-commit to the streaming retry decorator).
- **Streaming usage is summed across the whole turn** (was: reset every roundtrip, underreporting
  multi-roundtrip turns ~N×; the roundtrip-cap fallback reported zero).
- **Rolling summary converges.** Already-folded prefixes are detected (watermark in the *stored* summary
  only — the visible summary stays the clean LLM output within `MaxSummaryChars`) and never re-folded, so
  the summary stops accumulating duplicate bullets; failed overflow retries no longer persist summary
  changes; retries respect `EnableConversationHistorySummarization=false`; the cap default is 2048 tokens
  (was 0 = unbounded) and trimming keeps the newest content, evicting the oldest.
- **`memory(action=clear)` wipes only the memory document** (versioned, revertible); chat history,
  transcripts, and prior versions survive — the model can no longer erase the user's conversation record.
- **Agent-memory scope keys are injective.** Distinct raw scope values that sanitize to the same text
  (`a.b` vs `a/b`) get a stable hash suffix — no more cross-user memory/history sharing; unset and
  clean values keep their legacy on-disk keys (no data migration for the default case).
- **Role files stop growing without bound**: chat history and transcripts are trimmed on write to
  configurable caps (were only trimmed on read).
- **Replacing a role's tool list no longer disconnects skills**: `read_skill`/`call_skill_tool` are
  re-asserted when a live skill catalog exists.
- **Audit chain verifier**: accepts a legitimate mid-file `ChainReset` as a new chain start, and rotation
  stages its anchor before the atomic swap — a crash between the two no longer bricks verification of all
  subsequent files.

### Fixed (2026-07-11 audit wave)

- **SSE connect phase honors `TransportTimeoutSeconds`.** `HttpClientOpenAiTransport` now bounds the
  headers-not-yet-received phase of `OpenSseResponseStreamAsync` with the configured transport timeout
  (the linked CTS is disposed once headers arrive, so the streaming body itself stays unbounded); a backend
  that accepts the socket but never answers fails fast instead of eating the whole turn budget.
- **Lua sandbox: nested guarded calls can no longer disarm the outer guard.** `LuaCsExecutionGuard` keeps a
  per-`LuaState` stack of installed hooks; leaving a nested `mods_call` restores the caller's hook instead of
  removing it, so step/time/alloc budgets stay armed across direct and indirect (`A→B→A`) cross-mod calls.
- **Lua mods: transaction scope is reset after every handler/timer/load.** `LuaCsModRuntime` now accepts the
  shared `ILuaTransactionScope` and resets it in `finally`, so a handler dying between `coreai_world_begin`
  and commit no longer leaks an open transaction that silently swallows later world commands.
- **Mod headers: tolerant capability parsing.** Unknown capability tokens are skipped (`Enum.TryParse`,
  fail-closed to `None` when nothing parses) and `ResourcesBundledModSource` isolates per-mod load failures,
  so one bad header no longer breaks seeding of all bundled mods.
- **Non-streaming responses survive one bad tool call.** `ParseResponse` degrades only the malformed call to
  the parse-error marker contract; the text and remaining calls are preserved (previously the whole message
  was silently replaced with an empty one).
- **Streaming: an index-less tool-call delta no longer poisons every pending call.** The fragment is
  attributed to the sole open call when unambiguous; only genuinely ambiguous open calls are failed, and
  completed calls are never touched.
- **Error classification: `rate` substring false positives removed.** Rate-limit detection now requires
  explicit signals (`rate limit`, `429` status, `too many requests`, `quota`) instead of matching
  "gene**rate**"/"mode**rate**".
- **Circuit breaker: half-open admits exactly one probe** (concurrent calls short-circuit) and an abandoned
  or cancelled stream releases the probe slot without being misclassified as a backend failure; a stream
  abandoned after a terminal error chunk still counts as a failure.
- **Tool result classification: top-level `isError: true` (MCP contract) counts as failure**; nested `error`
  fields in legitimate tool payloads never did and still don't (regression-pinned by test).
- **Text-extracted tool calls: quoted examples are no longer executed.** The extractor now requires exact
  call shape (top-level `name` string + `arguments` object), skips backtick/quote-cited spans and fenced
  code blocks, and preserves parentheses inside quoted arguments via balanced scanning (previously a lazy
  regex truncated them and dropped the call).
- **Reasoning no longer persists into conversation history.** Assistant messages are run through the
  think-block filter before `AppendChatMessage`, so multi-kilobyte reasoning blobs stop inflating every
  subsequent turn's context.
- **`InGameLlmChatService`: overlapping requests are serialized** (snapshot → LLM → append under one gate),
  so a second response always sees the first turn and history order cannot interleave.

## 5.6.1 - Build-time policy registration + code-style pass (2026-07-11)

- **`AgentBuilder.Build()` auto-applies to the global policy.** When `CoreAIAgent.Policy` exists, `Build()`
  now registers the config immediately (on top of the first-Ask auto-registration), so a role is routable
  the moment it is built. `BuildDetached()` still leaves the global policy untouched.
- **Code-style pass.** Solution-wide Rider reformat under the shared `.editorconfig` (attributes stay
  one-per-line, verified 0 re-collapsed); obvious comments stripped and genuine ones tagged
  `// WHY:` / `// TODO:` / `// HACK:` across the whole runtime (portable core, Unity host, Lua-CSharp mods).

## 5.6.0 - Simpler agent API, code-style rules, benchmark comparison (2026-07-11)

- **Code style: shared `.editorconfig` + comment convention.** Attributes now always sit on their own
  line (`resharper_place_attribute_on_same_line = never`; 208 existing one-line attributes split). Comments
  keep only XML docs plus explicitly-tagged `// WHY:` / `// TODO:` / `// HACK:`; obvious restate-the-code
  comments and section-divider banners were stripped across the core. See `CONTRIBUTING.md`.
- **Benchmark: native vertical model-comparison chart.** The frontier chart is now produced by the
  benchmark's own `Build Model Comparison Report` (vertical bars, 8 models ranked best-first) and rendered
  to PNG so GitHub shows it. A note marks the Claude rows as understated (a non-native, unstable API gave
  them a high tool-failure rate, so their scores are a lower bound).
- **`AgentBuilder`: `ApplyToPolicy` is now optional.** `AskAsync`/`AskWithCallback` auto-register the built
  `AgentConfig` with the global `CoreAIAgent.Policy` on first use, so the newcomer flow is just `Build()` →
  `Ask*()` — no manual `ApplyToPolicy(CoreAIAgent.Policy)` step. The explicit call is still available for
  custom policies or up-front registration; re-applying is idempotent. A null policy (uninitialized lifetime
  scope) now fails with a clearer message than the old "role not registered".
- **Benchmark: G7 no longer captures a scene screenshot.** The comprehensive-integration (Player/Gate/Key)
  scenario photographed as an unreadable composite of overlapping primitives and floating world-space labels
  that added nothing to the score. G6 (the free-build castle) is now the only hero image; G7 is graded purely
  on world state + Lua consistency, like the other logic groups.
- **Structured world spawning.** `world_command` `spawn`, `spawn_batch`, and `change` expose
  `worldPositionStays` (default `false`). Parented transforms are local by default; callers can pass `true`
  to preserve world space. The tool contract now recommends named `empty` roots and meaningful child
  hierarchies for compound objects. The visual benchmark executor now preserves those parent relationships
  in generated scene prefabs instead of flattening every spawned object under the benchmark root.

## 5.5.0 - R6 resilience, benchmark v2 tooling, CI/package gates (2026-07-11)

- **Benchmark: model-authored castles export as prefabs.** Every G6 free-build run now saves the built scene
  as a reusable Unity prefab under `Assets/Benchmark/<model>/` (per-model folder, colours baked into real
  material assets in a `Materials/` subfolder, plus a `BuiltBy_<model>__<score>of100` label child), not just a
  screenshot. Written outside the benchmark package and git-ignored.
- **Benchmark: G6 image-feedback prompt no longer coaches vision.** The vision variant's system prompt was
  telling the model to "use the camera and refine", which biased the A/B and made non-vision models score
  worse. It is now identical to the plain free-build prompt — the only difference is the camera tool being
  available — so the scenario measures whether a model discovers and uses vision on its own.
- **R6 resilience: streaming-path retry + portable-core request timeout.** Two new `CoreAI.Core`
  decorators: `RetryingStreamingLlmClientDecorator` retries a stream only BEFORE it commits content (so a
  transient pre-first-token failure recovers without duplicating output or re-firing tool side effects),
  closing the gap where only `CompleteAsync` retried; `TimeoutLlmClientDecorator` bounds both the streaming
  and non-streaming paths off `LlmRequestTimeoutSeconds` so headless/standalone hosts get a request timeout
  too (previously only the Unity `CoreAiChatService` enforced it). 12 EditMode tests.

## 5.4.0 - MoonSharp removed; Lua-CSharp is the only VM (2026-07-10)

- **MoonSharp fully removed — Lua-CSharp is now the single Lua runtime.** The legacy MoonSharp VM and its
  entire binding/sandbox/runtime layer are deleted; the managed, AOT-safe Lua-CSharp stack (already the
  DI-registered runtime for mods, world, hierarchy/components, input, time, logic slots, coroutines) is the
  only VM. The `org.moonsharp.moonsharp` package dependency is gone and the Lua VM (`Lua.dll` +
  `Lua.Annotations.dll`) now ships bundled inside the CoreAI Mods package — no external Lua package to
  install. The `COREAI_HAS_MOONSHARP` scripting define no longer exists; Lua is compiled in by default and
  `COREAI_NO_LUA` still compiles it out.
- Dead `#if COREAI_HAS_MOONSHARP` blocks removed from CoreAI.Source (`CorePortableInstaller`,
  `AiGameCommandRouter`, `CoreAILifetimeScope`, `CoreAiChatExternalDriver.RunLuaDiag`). The Programmer agent
  system prompt now names the Lua-CSharp sandbox instead of MoonSharp.
- **Benchmark G6 image-feedback mode.** The free-build visual can now run with a `off` / `image` / `both`
  vision mode (benchmark-window "Vision feedback" dropdown or `COREAI_BENCHMARK_VISION_MODE`). In `image`,
  the model additionally gets the `camera` tool so it can `camera_capture` a screenshot of its own
  work-in-progress, judge it, and refine — the "look at what you made and fix it" loop; `both` runs the
  text-only and image-feedback builds side by side. Grading is inherited unchanged so the two are directly
  comparable; the camera tool is null-safe and degrades to a text-only build for non-vision models.
- **Speed probe fixed for a fair comparison.** `DirectVsAgent_Speed` now warms each configuration
  immediately before measuring it, so the reported TTFT reflects pipeline prefill cost rather than call
  order (previously the last-run, biggest-prompt role looked fastest because the server kept warming).

## 5.3.0 - benchmark v2 and resilience primitives (2026-07-10)

- **Benchmark suite v2 — new G8 described-state selection group.** `BenchmarkInfo.Version` bumped `v1 → v2`.
  Adds a group that gives the model a DESCRIBED, already-populated scene and grades acting on the named
  existing objects (clear only junk, raise only undersized towers via conditional selection, encode an
  observed rule as Lua) — the "director-AI / beyond the chat box" axis. Prompts state the goal, not the
  tool syntax, so weaker local models visibly fail the conditional-selection step. Scores are only
  comparable within a suite version; v2 starts a new leaderboard section.
- **Benchmark v2 — less hand-holding, more intelligence.** Reworded prompts that previously dictated the
  exact solution so the test measures understanding, not transcription: G6 castle no longer prescribes
  "four corner towers + walls + 24 objects" (every model just built that) — it now asks for a
  believable, detailed castle with a lived-in courtyard and surroundings, and grading rewards richness,
  variety and detail while treating tower/wall/gate/keep as non-mandatory castle *signals* (a keep-and-
  courtyard or asymmetric fort scores fine). G1 arena/coin-collector no longer dictate which primitive
  shape each object must be, and the coin-collector describes the Lua rules in words instead of pasting
  the function bodies — the model must choose shapes and derive the formulas itself. G5 (instruction
  discipline) and G2 (code-transcription baseline) keep their intentional strictness.
- **Circuit breaker for LLM backends** (`CircuitBreakerLlmClientDecorator`). After N consecutive
  TRANSIENT failures (timeout, rate-limit, backend-unavailable, provider/routing error) the breaker
  trips **open** and short-circuits calls with `BackendUnavailable` *without invoking the backend* — so a
  dead primary no longer costs `timeout × (retries+1)` every turn. After a cooldown it half-opens and
  admits one probe: success closes it, failure re-opens it. Caller-caused failures (auth, invalid
  request, context-length, cancellation) never trip it. Covers both `CompleteAsync` and the streaming
  path. Deterministic (injected monotonic clock); 6 EditMode tests. This is an opt-in public decorator;
  production composition/settings wiring remains tracked in `TODO.md`.
- Final release verification: 1,613 EditMode tests discovered (1,609 passed, 4 optional third-party
  ignored, 0 failed); PlayMode `FastNoLlm` 67/67; the local `qwen3.5-4b-mtp` full G1-G8 run scored
  88.1/100 and passed G8 3/3.

## 5.2.0 - stability gate and extension APIs (2026-07-10)

- Added the public `IContentFilter` extension point, passthrough implementation, and baseline
  word-list filter. The filter is host-wired by design; CoreAI does not claim automatic moderation.
- Hardened `ToolExecutionPolicy`: every state-mutating built-in shares one serialization policy;
  streamed mutations are deferred until the complete turn is known; whole-turn echoes are rejected
  before side effects; partial failures retry only the failed slots.
- Added focused regression coverage for streamed mutation replay, partial-success retries, and
  duplicate/error accounting.

## 5.1.0 - audit remediation: safe mutation pipeline, bounded queues/stores (2026-07-10)

- **Tool execution policy (F-01):** per-call duplicate signatures registered only on success (failed
  calls stay retryable), a single serialized mutation chain covering `world_command`,
  `component_command`, `execute_lua`, `call_skill_tool`, and streamed mutating calls deferred to
  turn completion so mutations never overlap and cross-turn echoes become structured
  `{ok, duplicate}` no-ops before side effects.
- **Orchestrator backpressure (F-10):** `AiOrchestrationQueueOptions.MaxPending` admission cap
  (default 64) with `AiOrchestrationQueueFullException`, binary-search insertion instead of
  per-enqueue re-sort, and a Dispose contract that cancels in-flight work and completes all
  pending tasks/streams.
- **Version retention (F-11):** new `VersionRetentionPolicy` bounds Lua-script and data-overlay
  version stores (original + current + last N intermediates, byte budget); revision `Index` is now
  stable across eviction and revert is index-based in both mod runtimes.
- **Audit chain (F-07):** `AuditEntry`/`AuditLogVerifier` support rotation markers and anchored
  genesis (`VerifyChainedSet`) so rotated files verify standalone while staying chained.
- This wave was driven by the 2026-07-10 repository audits (findings F-01…F-25 / A-01…A-06); those audit
  reports have since been removed and any remaining open findings are tracked in `TODO.md`.

## 5.0.10 - version lockstep with coreaiunity 5.0.10 (2026-07-06)

- No changes; released to keep both packages on identical versions. (The self-spawning model-download
  indicator lives entirely in the Unity layer.)

## 5.0.9 - version lockstep with coreaiunity 5.0.9 (2026-07-06)

- No changes; released to keep both packages on identical versions. (The LLMUnity host-configuration
  start-guard fix lives entirely in the Unity layer.)

## 5.0.8 - version lockstep with coreaiunity 5.0.8 (2026-07-05)

- No changes; released to keep both packages on identical versions. (The LLMUnity-as-OpenAI-server
  native tool-calling work lives entirely in the Unity layer.)

## 5.0.7 - version lockstep with coreaiunity 5.0.7 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.6 - version lockstep with coreaiunity 5.0.6 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.5 - version lockstep with coreaiunity 5.0.5 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.4 - version lockstep with coreaiunity 5.0.4 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.3 - version lockstep with coreaiunity 5.0.3 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.2 - version lockstep with coreaiunity 5.0.2 (2026-07-05)

- No changes; released to keep both packages on identical versions.

## 5.0.1 - skill teaches editing existing mods (2026-07-05)

- **Lua Modding skill**: explicit "improve an existing mod" workflow - `get_source` first, then
  `reload` with the FULL updated source; every reload stores a revision (`versions` / `revert`);
  `forget` = delete (unload + remove the persisted copy). Verified live: a 9B model reads,
  rewrites and reloads an existing mod and deletes one via `forget` from chat alone.

## 5.0.0 - on-demand skills for built-in roles; "Lua Modding" skill (2026-07-04)

- **`AgentMemoryPolicy.AddSkillForRole(roleId, skill)`** - attaches an on-demand skill catalog to
  ANY role (built-in or custom), not only AgentBuilder-assembled agents. First skill registers the
  `read_skill` / `call_skill_tool` meta-tools once over a live `MutableSkillCatalog`; later skills
  (even mid-session) are immediately readable; a same-name skill replaces the previous one.
- **Built-in "Lua Modding" skill for the Programmer role.** The system prompt keeps the
  survival-minimum API list and points at `read_skill('Lua Modding')`; the skill returns the full
  ~9.5 KB reference: every sandbox API family with signatures, timers/input/persistence/cross-mod
  patterns, a complete worked mini-game mod, and a catalog of common errors with causes (incl. the
  JSON `
` double-escaping failure mode observed with small models).
- **`ReadSkillLlmTool` / `CallSkillToolLlmTool` are public** so hosts and installers can attach
  skill catalogs to roles assembled outside AgentBuilder.
- **Audit follow-up (MoonSharp idioms):** Lua `print()` routes to the project logger (and inside
  mods into the same report pipeline as `report()`, honoring the mute flag) instead of MoonSharp's
  invisible `Console.WriteLine`; `LuaExecutionGuard` documents its real guarantee (no CLR-call
  preemption); `LuaApiRegistry` builds `CallbackFunction`s eagerly from the owning script and
  caches `ParameterInfo[]` per registration.
- **Semver:** major (5.0), lockstep with **`com.neoxider.coreaiunity` 5.0.0** - the on-demand skills platform for built-in roles.

## 4.20.0 - hooks_on('tick') alias; {id=...} table coercion for numeric params (2026-07-04)

- **`hooks_on('tick'/'update'/'frame')` registers a real per-frame timer.** `hooks_on` receives only
  NAMED events, but LLM-written mods routinely register these spellings expecting a frame callback and
  got a handler that never fired (observed live: a day/night sun rotator that never rotated). The
  intuitive spellings now route to the timer machinery at the minimum interval (0.05 s / 20 Hz),
  counted against the mod's timer cap.
- **Lua tables with an `id` field coerce to numeric parameters.** `LuaApiRegistry` delegate dispatch:
  when a delegate parameter is numeric and the script passed a table (models constantly pass a whole
  `unity_find_all` entry instead of `entry.id`), the table's numeric `id` member is substituted
  instead of throwing "cannot convert a table to a clr type System.Int32".
- **Semver:** minor, lockstep with **`com.neoxider.coreaiunity` 4.20.0** (mod tick driver, Lua input
  API, mod source editor panel, Lua platform example demo - see that changelog).

## 4.19.0 - WebGL Full Lua fixed; real 429 retry windows; tool-error accounting (2026-07-04)

- **WebGL Full Lua fixed (the "RuntimeError: null function" player crash).** Root cause via a development-build stack trace: MoonSharp's `Script` static ctor loads resources through reflection (`UnityAssetsScriptLoader.LoadResourcesWithReflection` -> `Resources.LoadAll`), and IL2CPP stripped those reflection-only UnityEngine members, so the invoke jumped to a null method pointer and halted the whole wasm player. Fix: preserve `UnityEngine.Resources` + `UnityEngine.TextAsset` in `Assets/link.xml`. Verified live in a browser: the staged diagnostic (Script ctor -> sandbox -> host callback -> `unity_find` -> `unity_set_scale`) passes, and a real model turn found and scaled the demo cube via Full Lua.
- **429 retry now waits the provider's REAL window.** On WebGL, fetch cannot read `Retry-After` (CORS), so the single transient retry used a 2s formula and always landed inside a still-closed TPM window, wasting the whole rescue chain. `ResolveRateLimitBackoffMs` now parses the window from the error body ("Please try again in 14.017s" - Groq format, minutes+seconds, capped 20s, +250ms margin), and `BuildHttpException` surfaces it as `RetryAfterSeconds` on the typed error.
- **Tool-error accounting: partial success is progress.** The consecutive-error abort (3 strikes) now counts only ALL-failed batches/turns; a 4-of-5-successful spawn batch no longer pushes a run toward "max consecutive tool errors reached" (sequential, parallel and streamed paths).
- **Failed tool calls stay retryable verbatim.** Duplicate/echo signatures register only after a batch/turn that made progress; previously a transiently-failed call could never be retried with identical (correct) args - the pre-execution registration suppressed exactly the retry the error feedback asked for.
- **Tool loop parity upgrades (audit close-out).** History trimming now applies to the STREAMING tool loop too (shared `ToolCallHistoryTrimmer`; assistant/tool pairs never orphaned; default `maxToolCallHistoryMessages` is 20, 0 = unlimited). At the roundtrip cap or max-errors guard the model gets ONE final tools-disabled summarization turn instead of empty/canned text. Argument type-conversion failures feed the compact schema back to the model. Deterministic `tool_call_id` synthesis when a provider omits ids (same id on echo and reply); parse-error calls echo the model's raw argument string, not internal markers. Intra-batch/intra-turn identical calls all execute ("spawn tree x3" works; only the cross-turn echo guard remains). Non-streaming turns report whole-turn summed usage (`LlmUsageAccumulator`).
- **Tool-result wire hardening.** A `System.Text.Json.JsonElement` result can no longer reach the model as Newtonsoft's `{"ValueKind":N}` reflection garbage - the wire builder emits the element's actual JSON/string.

## 4.18.4 - transient-HTTP chain: request -> retry -> non-streaming fallback -> typed error (2026-07-04)

- **A transient HTTP failure (429/408/5xx) on the streamed path now walks the full rescue chain
  before any error reaches the player.** Previously 429 got its bounded retries and then threw;
  nothing fell back unless a SECOND backend was configured (FallbackLlmClientDecorator needs a
  secondary). Now: 1 request -> `RateLimitMaxRetries` (default 1) Retry-After-aware retries -> ONE
  plain non-streaming completion with a ZERO extra-retry budget -> only then the typed error
  (RateLimited/BackendUnavailable/...). 408 and 5xx joined 429 as retryable transients; the
  non-streaming path uses the same classification.
- EditMode: `RateLimited429Once_RetriesAndCompletes` (2 opens),
  `RateLimitedPersists_FallsBackToNonStreaming` (2 opens + exactly 1 completion),
  `RateLimited429Exhausted_ThrowsRateLimited` (fallback also 429 -> typed error, no hidden rounds).
  SSE fixture: 36/36.

## 4.18.3 — bounded HTTP 429 retries before the RateLimited error surfaces (2026-07-04)

- **An HTTP 429 no longer fails the turn on the first hit.** Previously 429 was never retried:
  the transient-retry classifier only matched local-model reload texts, so a burst-rate-limited
  provider (routine on OpenRouter `:free` tiers — verified live in a WebGL build) surfaced
  "Error: HTTP error 429" to the player immediately. Now both the non-streaming and the
  stream-open paths absorb up to `RateLimitMaxRetries` (default 2) extra attempts, honoring the
  `Retry-After` header when present (capped at 15s) and falling back to 2s/4s backoff, before the
  typed `LlmErrorCode.RateLimited` error surfaces.
- EditMode: `GetStreamingResponseAsync_RateLimited429Twice_RetriesAndCompletes` (two 429s absorbed,
  3 opens) and `GetStreamingResponseAsync_RateLimited429Exhausted_ThrowsRateLimited` (three 429s →
  typed RateLimited after exactly 1+2 attempts). SSE fixture: 35/35.

## 4.18.2 — starved-stream watchdog: abort keep-alive-only SSE attempts early (2026-07-04)

- **A starved SSE attempt no longer waits for the server to close the connection.** Confirmed in a
  WebGL production build: a proxy hiding an upstream failure behind HTTP 200 held each streaming
  attempt open for ~40s sending only `: keep-alive` comment lines, so the three empty-stream
  retries alone exceeded the host's 120s turn watchdog — the turn was cancelled before the 4.18.1
  non-streaming fallback could even start (`wallMs=120029 chunks=0 | cancelled`). Now, while an
  attempt has produced ZERO parsed deltas and nothing but SSE comment/blank lines has arrived, the
  attempt is aborted after `StarvedStreamFirstDeltaTimeoutSeconds` (default 15s) and the existing
  empty-stream retry/fallback path takes over immediately (starved-aborted retries also skip the
  extra backoff — the wait was already served). Worst case to fallback: ~45s instead of 2+ minutes.
  A genuinely slow model that streams real data lines (or nothing at all) is unaffected — the
  watchdog only fires on comment-only traffic before the first delta.
- EditMode: `GetStreamingResponseAsync_EndlessKeepAliveStream_AbortsEarlyAndFallsBack` (a stream
  that never closes and never sends a data line: 3 early-aborted attempts, exactly 1 non-streaming
  completion, fallback text surfaces through the stream).

## 4.18.1 — starved SSE stream falls back to a non-streaming completion (2026-07-03)

- **An SSE 200 with zero data deltas no longer eats the whole retry budget and no longer ends in
  silence.** A starved stream (typically an upstream rate limit hidden behind a proxy: HTTP 200,
  only keep-alive comments, no tokens — the "HTTP 200 but 0 parsed SSE deltas" warning) previously
  retried the stream up to 10 times with backoff (~a minute of a busy chat turn) and then threw
  `BackendUnavailable`, which surfaced to the player as no answer at all. Now the empty stream is
  retried only 3 times, after which the SAME turn falls back to ONE plain (non-streaming)
  completion — the same provider usually still answers it — and the answer is delivered through the
  streaming iterator as simulated updates (the existing no-SSE-transport path). Only if the plain
  completion also fails does the turn surface a typed error.
- EditMode: `GetStreamingResponseAsync_EmptyStreamRepeated_FallsBackToNonStreamingCompletion`
  (3 stream opens, exactly 1 non-streaming completion, fallback text surfaces through the stream).

### Runtime backend switching cross-reference (2026-07-03)

- **Docs:** `LLM_ROUTING.md` now cross-references the host-side runtime backend switching feature
  (`CoreAiBackend` in `com.neoxider.coreaiunity`) from the execution-modes section. The portable
  core is unchanged — switching is implemented entirely in the Unity host package on top of the
  existing `ILlmClientRegistry` legacy-fallback contract.

### Forced-tool-mode compat + local-transport hardening (2026-07-03)

- **`RequireSpecific` forced tool mode is now provider-portable.** Instead of MEAI's
  `ChatToolMode.RequireSpecific(name)` (whose wire form — a forced-specific `tool_choice` — some
  OpenAI-compatible local servers reject), `MeaiLlmClient.ApplyForcedToolMode` maps it to
  `RequireAny` while narrowing `options.Tools` to the single requested tool; post-call iterations
  restore the full tool list together with the usual switch back to `Auto`. An unknown
  `RequiredToolName` degrades to plain `RequireAny` with a warning instead of a guaranteed provider
  error.
- **`HttpClientOpenAiTransport` bypasses the system proxy for its shared clients** (`UseProxy =
  false`) so local LLM endpoints are never routed through a WinINET proxy/VPN driver. Deliberately
  does NOT also assign `Proxy = null`: on Unity Mono, `HttpClientHandler` defers property writes to
  an inner `MonoWebRequestHandler`, and that assignment throws `InvalidOperationException` lazily on
  the first request, poisoning every request through the shared client.
- **Stream-open failures now log the inner exception** (e.g. `WebException: ConnectFailure`) instead
  of only the generic "An error occurred while sending the request" wrapper, so a refused TCP
  connection is distinguishable from a mid-handshake reset in the log.

### Streaming-by-default task execution + transport-send retry (2026-07-03)

Two changes that make streaming the default execution path everywhere and keep it reliable against
local servers.

- **`AiOrchestrator.RunTaskAsync` now streams by default.** Each completion for a non-interactive
  agent task is obtained via `CompleteStreamingAsync` when `EnableStreaming` is on (new
  `CompleteForTaskAsync` helper collapses the stream into an `LlmCompletionResult`), so task execution
  runs through the same execute-as-you-stream tool path — including bounded-parallel tool calls — as
  chat, instead of the non-streaming `CompleteAsync`. It falls back to `CompleteAsync` only when
  streaming is disabled. All surrounding logic (context-overflow retry, structured-response
  validation, tool-only content synthesis, empty-response handling) is unchanged.
- **`MeaiOpenAiChatClient` retries a transport-level send failure on stream-open.** A pooled
  keep-alive connection the local server has already closed surfaces as
  `"An error occurred while sending the request"`; that no longer fails the whole request — a bounded
  couple of quick retries open a fresh connection, and a genuinely-down backend still surfaces
  promptly as `BackendUnavailable`.

### Parallel execute-as-you-stream tool calls (2026-07-03)

Until now only the batch path ran a turn's tool calls concurrently (`ExecuteBatchAsync`, bounded by
`MaxParallelToolCalls`); the execute-as-you-stream path executed strictly one-by-one, so a slow tool
stalled every call queued behind it even while the model kept streaming.

- **`ToolExecutionPolicy.ExecuteStreamedAsync` now schedules each drained call concurrently**, on a
  `SemaphoreSlim` bounded by the same `MaxParallelToolCalls` setting as the batch path. Its return type
  becomes `Task<ToolCallResult?>` (null when a call was scheduled for parallel execution; the result
  then surfaces at completion). Per-call duplicate suppression and the cross-turn echo guard are still
  decided synchronously at arrival (arrival order defines the turn signature); the per-call timeout is
  enforced inside `ExecuteSingleAsync` in each worker, exactly as on the batch path.
- **Serialized mutating built-ins are unchanged**: `IsSerializedTool` calls (`memory`, `manage_mods`,
  `manage_skills`) still chain on one ordered serial chain so two writes never overlap; independent
  tools run fully in parallel.
- **The turn closes through a new `CompleteStreamedTurnAsync`**: drains all in-flight calls (a
  cancelled/unfinished call becomes a failed slot — finalization never throws, and it also runs on
  mid-stream abort), collates results strictly in ARRIVAL order, then applies the existing turn-level
  semantics unchanged — whole-turn echo → one `RecordFailure`; otherwise one success/failure record;
  combined-signature registration. The drain is **bounded by the per-call tool timeout plus a small
  margin**, so a tool that ignores its cancellation token cannot hang finalization even on the abort
  path (which passes `CancellationToken.None`); only explicitly-disabled tool timeouts wait for natural
  completion.
- **`MaxParallelToolCalls <= 1` keeps the old strictly-sequential inline behavior byte-identical.**

### Streamed tool-call hardening from the independent audit (2026-07-03)

Four fixes to the execute-as-you-stream path, all found by a two-track code audit of the streaming
work below.

- **Multi-call echo turns now trip the consecutive-error guard.** `CompleteStreamedTurn` registers
  the turn's combined signature BEFORE recording the outcome and checks the `Add()` return value:
  a whole-turn echo (identical streamed turn already executed this request) records ONE failure —
  never a success — with a `duplicate` trace, mirroring the all-duplicate batch branch. Previously
  the re-executed calls succeeded and `RecordSuccess()` kept resetting the counter, so a model stuck
  echoing the same multi-call batch ran to the iteration cap instead of tripping max-consecutive-errors.
- **The SSE stall clock no longer counts consumer time.** `lastProgressUtc` re-arms AFTER the parsed
  updates for a line are yielded and consumed: the streaming iterator is pull-based and the consumer
  executes tool calls between `MoveNext`s, so re-arming on line arrival charged tool-execution time
  against the transport stall budget and aborted healthy streams with slow tools.
- **`DrainCompleted()` drains in strict provider index order across chunks.** Only the longest
  contiguous ready prefix of the `(index, sequence)` order is drained; a still-open earlier call
  blocks later closed ones, so dependent pairs (create → configure) can no longer execute out of
  order when a later call's JSON happens to close first.
- **Drained calls leave tombstones.** Fragments referring to an already-drained call (by id, or by
  index with no id) are ignored: OpenAI-compat servers that re-send cumulative argument strings or
  trailing empty deltas after a call drained can no longer create a fresh pending entry and execute
  the call a second time. A fresh id reusing a drained index is still treated as a genuinely new call.

### Wire protocol: one tool message per tool result (2026-07-03)

- **`MeaiOpenAiChatClient` now serializes a Tool-role message carrying several
  `FunctionResultContent` items into one OpenAI `tool` message PER result** (each with its own
  `tool_call_id`). Previously only the FIRST result reached the wire, so after a multi-call turn the
  model saw N `tool_calls` but a single answer and legitimately re-issued the "unanswered" calls on
  every round-trip — observed live in the game benchmark, where a 5-spawn turn ballooned into 15
  executed spawns (5+4+3+2+1 echo cascade) and tanked instruction-adherence scores across models.

### MaxOutputTokens: explicit 0 = unlimited (2026-07-02)

OpenAI-compatible `max_tokens` counts REASONING tokens too, so any finite cap silently starves a
long-thinking model: observed live with glm-5.2 on the benchmark's free-build scenario — the whole
4800-token per-turn cap went to thinking (`finish_reason=length`), zero tool calls, empty scene.

- **`0` now means "explicitly unlimited" at every level of the fallback chain** — per-call
  (`AiTaskRequest.MaxOutputTokens`), per-agent (`AgentBuilder.WithMaxOutputTokens(0)`,
  `AgentMemoryPolicy.SetMaxOutputTokens`), resolved by `AiOrchestrator` — and reaches the LLM client
  as `0`, which suppresses the global `ICoreAISettings.MaxTokens` fallback entirely: no `max_tokens`
  is sent, the provider uses its own default. `null`/negative still means "inherit the next level".
  This matches the existing `MaxToolCallRoundtrips` convention where `0` = unlimited.

### Text tool-call extractor: python-style keyword arguments (2026-07-02)

- **`LlmToolCallTextExtractor` now parses `name(key=value, ...)` calls into real typed arguments**
  (quoted strings unquoted, `true`/`false` → bool, numerics → numbers via invariant culture,
  `{...}`/`[...]` → parsed JSON). Previously a message like
  `world_command(action='spawn', targetName='Goal', x=0, y=0, z=2)` fell into the generic
  positional branch and collapsed into `{"input":"action='spawn'"}`, failing the tool's
  required-argument validation (observed live in the game benchmark). All-or-nothing gate: any
  non-`key=value` part falls back to the legacy positional handling.

### Execute-as-you-stream tool calls (2026-07-02)

Real-API streaming end to end: with a streaming provider, each native tool call now executes the
moment its argument JSON closes on the wire, instead of being buffered until the whole assistant
turn finishes.

- **`SseToolCallAccumulator.DrainCompleted()`** — while a streamed response is still arriving, any
  pending tool call whose accumulated argument JSON is already a complete object (string/escape-aware
  brace scan, trailing junk rejected) is emitted as a `FunctionCallContent` immediately and removed
  from the pending set. Calls with malformed/truncated JSON never drain early; they still finalize
  (with the parse-error marker) at end of stream.
- **`ToolExecutionPolicy.StreamedTurn`** (`BeginStreamedTurn` / `ExecuteStreamedAsync` /
  `CompleteStreamedTurn`) — executes calls one by one as they arrive while preserving the exact
  batch semantics of `ExecuteBatchAsync`: intra-turn duplicate suppression (same trace message),
  one success/failure record per turn for the consecutive-error guard, and end-of-turn echo
  signature registration so a later batch that repeats the streamed turn is still recognized.

### Benchmark: G7 comprehensive integration scenario (2026-07-01)

- `BenchmarkInfo.GroupDifficulty10` gained a `G7` entry (difficulty 9, hardest) for the new
  comprehensive-integration scenario group (world-building + Lua logic cross-consistency) added on the
  CoreAiUnity side — see `com.neoxider.coreaiunity`'s changelog for the scenario itself.
- `FailureAttribution` gained a `NotGraded` value: a scenario that ran fine but is deliberately excluded
  from the model's score (e.g. a fully custom-prompt free-build the built-in checkpoints weren't designed
  for). `BenchmarkReport.GradedResults`/`MeanBaseByScenario()` now both exclude it, consistent with the
  existing `Environment`/`Framework` exclusions.
- `RoleFitness`'s "Orchestrator / Director" role text now notes the current suite mostly measures
  task-level sequencing, not sustained multi-turn orchestration (weights/gates unchanged).

### Tool-calling hardening (2026-07-01 audit)

- **Reliable tool-result success detection.** `ToolExecutionPolicy.IsToolResultSuccess` no longer treats any
  text lacking the word "success" as success: it now recognizes a **truthy** JSON `error` / `ok:false` /
  `succeeded:false` / `success:false` (any casing) and plain-text failure prefixes, and classifies the
  result **before** it is truncated. A merely-present but null/empty/false `error` key (e.g. `MemoryResult`,
  which always serializes `Error`, null on success) is no longer misclassified as a failure.
- **Duplicate detection fixes.** Intra-batch duplicate calls of non-`AllowDuplicates` tools are now caught on
  the first turn (only the later identical calls are rejected, order preserved); duplicate signatures use the
  repaired **canonical** tool name so casing variants are detected; a repeated mixed batch still executes its
  `AllowDuplicates` calls; a batch where every call is a duplicate now correctly counts toward the
  consecutive-error guard instead of letting a repeating model loop past it silently.
- **Fail-closed tool-name repair.** Ambiguous case-insensitive name matches (two tools colliding under
  `OrdinalIgnoreCase`) are rejected as unknown instead of silently routing to the first match.
- **Atomic memory/skill mutations.** Added `IAtomicAgentMemoryStore.MutateAsync` + a keyed-lock fallback and
  `ISkillStore` atomic mutation so `memory` append/edit and `manage_skills` create/update are process-wide
  serialized read-modify-write — concurrent agent turns can no longer lose an append.
- **SSE tool-call parsing.** `SseToolCallAccumulator` keys pending calls by stable id (falling back to index);
  a differing id on an existing index starts a new call, and missing-index with multiple pending calls is
  flagged instead of merging fragments. Provider-native `reasoning_content` is no longer surfaced as visible
  assistant text (consistent with the streaming path).
- **Tool schema/contract truthfulness.** `DelegateLlmTool` derives `ParametersSchema` from its generated
  MEAI function schema; `CompatibilityLlmTool` description matches its `string[]` contract; `WaitLlmTool`
  states over-max seconds are clamped; `InventoryLlmTool` null-checks its provider.

- **Lua WorldEdit prompts/docs use the new world API.** Core Lua tool descriptions and built-in agent
  prompts now point agents at `coreai_world_spawn({...})`, `coreai_world_change(name, {...})`,
  `coreai_world_set_color`, and `coreai_world_destroy` instead of the legacy move/rotate/parent helper set.
  `LUA_GAME_API.md` was updated to match the current Unity world-command surface and to call out
  per-axis scale for meter-accurate objects.
- **Optional per-request system-prompt override** (`AiTaskRequest.SystemPrompt`). When set, the prompt
  composer uses it as the role's base prompt while still prepending the universal prefix; empty = unchanged
  (the registered role prompt). Lets a caller give a task-specific system prompt on a shared role.
- **Native tool schemas are self-describing.** On the native tool-calling path the JSON schema is generated
  from the C# delegate signature, so parameter descriptions reach the model ONLY via
  `[System.ComponentModel.Description]` attributes — the `ParametersSchema` string only feeds the text path.
  Added `[Description]` to the delegate params of the core tools (Memory, GameConfig, Compatibility,
  CallSkill, ManageSkills, ReadSkill, Wait, Lua, LuaMods). Without it the model saw bare unlabeled params.

## 4.17.0 - 2026-06-30

- **Tool-call history defaults to unlimited (`MaxToolCallHistoryMessages = 0`).** It was 20, which silently
  trimmed the oldest assistant+tool pairs during a long tool-calling turn — so a 30+ step build forgot its
  earliest steps and repeated them. `0` = keep the full history; conversation summarization + overflow
  retry still bound very long sessions. Changed across `ICoreAISettings`, the `CoreAISettings` const, and
  `CoreAISettingsOptions`.
- **Per-agent / per-task tool-call roundtrip cap.** `MaxToolCallRoundtrips` can now be overridden per
  agent (`AgentBuilder.WithMaxToolCallRoundtrips(int?)`) and per call (`AiTaskRequest.MaxToolCallRoundtrips`),
  in addition to the global `ICoreAISettings.MaxToolCallRoundtrips`. Priority: per-call &gt; per-agent &gt;
  global. A value of **`0` means UNLIMITED** (no safety valve), `null` inherits the next level, positive
  caps the loop. Wired end-to-end through `LlmCompletionRequest` → `SmartToolCallingChatClient`.
- **Default cap raised 10 → 20**, and the built-in **Programmer** and **Creator** roles now default to
  `0` (unlimited) since they routinely need many tool roundtrips per turn (code iterate / full build).
- **Clearer stop message.** When the cap is hit, the warning now names the role, the cap, its source
  (global vs per-agent/per-call), and exactly how to raise or disable it.
- **Honest throughput metric.** `GenerationTokensPerSecond` is documented and labeled as **provider-call**
  tok/s (prefill + decode), not decode-only — it reads lower than LM Studio, which excludes prefill. True
  decode-only timing needs TTFT (streaming-only); see the new test and `TOKENS_PER_SEC_FIX_PLAN.md`.
- **`BenchmarkInfo.GroupDifficulty10`** — one canonical 1–10 difficulty per group, the single source the
  editor RUN tab and the scenario/progress now both read.

## 4.16.0 - 2026-06-30

- **`AllowWorldPrimitives` setting** (`ICoreAISettings`, default `true`) gates the `world_command` spawn
  primitive fallback; `SetMaxToolCallRoundtrips` override added for benchmark/bootstrap.
- **`component_command` curated catalog** — `ComponentLlmTool` + `ICoreAiComponentCommandExecutor`: add /
  remove / set / list supported Unity components with no reflection. Mirrored by `coreai_component_*` Lua
  bindings.
- **`BenchmarkInfo`** suite identity (name + `v1`), `BenchmarkReportFormatter` / `ModelComparison` tweaks.
- **Decode tok/s fix** — report `max(provider, tokenizer-estimate)` including tool-call JSON so tool-only
  runs no longer undercount throughput to ~0.3 tok/s.

## 4.15.3 - 2026-06-30

- **Free, ambitious castles.** The G6 prompt swaps the rigid coordinate blueprint for full creative freedom
  within the -9..9 range, pushing the model to build the most impressive castle it can (towers, walls, gate,
  keep, flags + as many extras as it wants) — so each model's character shows.
- **G6 is off by default.** The castle hero is a non-scored bonus visual, so its toggle now defaults to off
  in the benchmark window; enable it explicitly (window toggle or `COREAI_BENCHMARK_GROUPS=…,G6`).

## 4.15.2 - 2026-06-30

- **Model name on every scene/castle screenshot.** The baked header now leads with the model id (long
  hyphenated ids wrap onto two lines and shrink to fit) above the scenario/score/verdict line, so each
  hero image is self-identifying.
- **Recognizable castles.** The G6 free-build prompt now hands the model an explicit coordinate blueprint
  (corner towers, perimeter walls with a gateway gap, central keep, flags) and invites extra decorations,
  and the output-token budget is raised, so even small local models produce a clean square castle instead
  of scattered cubes.

## 4.15.1 - 2026-06-30

- **Averaged repetitions.** When a scenario is run multiple times, the report now aggregates each
  scenario by the **mean (average)** of its repetitions instead of the median (suite score = mean of
  per-scenario means). The report section is retitled "Scenario means (average over repetitions)".
- **Opt-out of repetition.** Scenarios expose `Repeatable` (default true); a visual one-off such as the
  G6 castle hero sets it false so it runs exactly once even when the suite repeats every other scenario.

## 4.15.0 - 2026-06-30

- **feat(benchmarking): Game-Creation Benchmark reporting polish.** The portable reporting core now supports
  the G6 castle free-build hero scene, per-model model-card radar/role summaries, role-shaped scene result
  images with ghost markers for missing expected objects, decode-vs-effective tok/s reporting, and
  cross-model comparison data for the TerminalBench-style bar chart with ranked or pinned-first ordering.
- **Benchmark sweep and stability metadata.** Reports preserve repetition counts for median-stability
  comparisons, expose the fields used by LM Studio multi-model sweeps, and keep decode throughput
  comparable to LM Studio while also showing end-to-end effective throughput for the whole agentic session.
- **Audit fixes.** Benchmark rendering and serialization paths were tightened to avoid material/mesh leaks
  and to persist generation-time fields needed by Markdown, JSON, model-card, scene-image, and comparison
  reports.

## 4.14.0 - 2026-06-29

- **feat(benchmarking): portable Game-Creation Benchmark scoring core.** New unit-tested benchmark
  primitives live under `Assets/CoreAI/Runtime/Core/Features/Benchmarking`, with 0..100 scoring across
  Tool correctness, Intent & sequence, Task completion, Determinism, Reasoning, and Instruction adherence.
- **Subtractive instruction-following.** Constraint scenarios score from 100 down — each violation costs
  its compliance checkpoint plus a per-occurrence penalty, with a mandatory core task so "do nothing"
  can never score 100.
- **Game-fitness by role.** A new `RoleFitness` core type turns the dimension scores into a 0..10 fit
  rating per game-dev role (NPC, Mechanic/GameMaster, Scene/Tool Operator, Programmer, Orchestrator/Director,
  QA) with gates and an overall rating, so a tiny 2B/0.8B model reads clearly as 'Not suitable for agentic
  roles' instead of a misleading mid score. A role is only rated when the run actually measured every
  dimension it depends on — a partial (single-group) run marks the affected roles 'Not assessed' instead
  of over-claiming — and the headline overall reflects agentic roles only, so a chatty model cannot inflate
  it through the NPC score.
- **Efficiency scoring.** The benchmark adds a gated efficiency bonus for fewer tokens and less time
  (split token/time, base score must be >=90, capped at 20), reports honest generation tokens/sec from
  completion tokens only, uses per-scenario timeouts and per-scenario medians over repetitions, retries
  transient/crash failures, and excludes environment failures from the model score.
- **Self-explanatory scene screenshots.** World scenarios render a real Unity screenshot with a baked
  header (scenario, score and PASS/PARTIAL/FAIL verdict, tinted by outcome) and a "what it checks" caption.
  Objects are drawn by role (capsule/sphere/coin/post) with a ✓/✗ status, and expected objects the model
  never built appear as ghosts — so the picture alone shows how the model did. A per-model "model card"
  (dimension radar + role bars) makes two models comparable at a glance. `ScenarioResult` carries the PNG
  plus the description for the report.

## 4.13.0 - 2026-06-28

- **feat(tools): parallel tool-call execution.** `ToolExecutionPolicy.ExecuteBatchAsync` now runs a turn's
  tool calls concurrently, bounded by the new `MaxParallelToolCalls` setting (default 4; 1 = sequential
  fast-path). Result order is preserved (collated by index), state-mutating built-ins (`memory`,
  `manage_mods`, `manage_skills`) are serialized so writes never race, and per-call timeout / duplicate
  rejection / forced-tool reset / consecutive-error counting / cancellation semantics are unchanged.
- **feat(tools): Hermes / Qwen-Agent XML tool-call parsing.** `LlmToolCallTextExtractor` now recovers the
  `<tool_call><function=NAME><parameter=KEY>VALUE</parameter>…</function></tool_call>` template many local
  GGUF models emit as text when native `tool_calls` is empty (parameter values kept as strings; wrapper
  tags stripped). Joins the existing JSON / `arguments_json` / `read_skill(...)` / `Action=write` formats.
- **feat(context): real BPE token counting.** `ITokenCounter` + `BpeTokenCounter` implement byte-level BPE
  (cl100k_base / o200k_base, resolved by `BpeEncodingResolver`) for exact pre-flight counts, loaded via
  `IBpeRanksProvider`. Falls back automatically to the calibrating estimator on unknown model / missing
  data / AOT load failure. Activate by adding the tiktoken rank files (see CONTEXT_MANAGEMENT_ROADMAP).
- **feat(skills): agent-authored skills.** New `manage_skills` tool (create/update/list/get/delete) +
  `ISkillStore` lets the model write, version (via `ILuaScriptVersionStore`), persist, and immediately
  reuse its own skills through the same role's `read_skill` catalog. `AgentBuilder.WithSkillAuthoring(...)`.

## 4.12.1 - 2026-06-28

- **fix(prompts): memory-usage instruction now reaches native tool-calling roles.** In
  `AiToolContractPromptFormatter`, the positive memory guidance ("when asked to remember/save/record,
  call the memory tool") lived *after* the `supportsNativeToolCalling` early-return, so native
  tool-calling roles whose base prompt never mentions memory (e.g. Creator) received **no** memory
  instruction at all and silently ignored "remember the …" tasks — leaving it to the model to infer.
  The detection + a strengthened imperative now run ahead of the early-return, so both the native and
  text-shaped paths get it (gated on the role actually having the `memory` tool). Tests:
  `DeterministicToolContractEditModeTests` (native+memory includes the imperative; native without memory
  omits it).

## 4.12.0 - 2026-06-28

- **Lua mod versioning (`manage_mods versions` / `revert`).** Mod load/reload now records a revision per
  edit into the existing `ILuaScriptVersionStore` (keyed `mod:<id>`); `LuaModManifest.Version` auto-derives
  from the revision count. New `manage_mods versions` lists the revision history (revision 0 = original) and
  `manage_mods revert` rolls a mod back to a recorded revision — a non-destructive revert (the reload
  re-records the restored source as the new current revision, so history stays an audit trail). A no-op
  reload (identical source) does not grow history. With no version store wired, load/reload still work and
  `versions` reports no history.
- **Runtime mod-handler error feedback (`manage_mods diagnostics`).** A hook that throws during `Tick`
  previously only raised `ModHandlerErrored` + counted toward host-side auto-unload; it is now also recorded
  in a bounded ring buffer (`MaxRetainedHandlerErrors = 32`, newest kept) so the agent can poll
  `manage_mods diagnostics` next turn to learn of runtime handler failures and repair the mod.
  `GetRecentHandlerErrors` / `ClearRecentHandlerErrors` expose and acknowledge the buffer.

## 4.11.5 - 2026-06-27

- **Lockstep package version.** No portable CoreAI API or runtime behavior changed in this drop; the bump
  keeps `com.neoxider.coreai` aligned with `com.neoxider.coreaiunity` 4.11.5 (WebGL non-streaming
  completions fix) so UPM consumers keep both package versions in sync.

## 4.11.4 - 2026-06-26

- **Lockstep package version.** No portable CoreAI API or runtime behavior changed in this drop; the bump
  keeps `com.neoxider.coreai` aligned with `com.neoxider.coreaiunity` 4.11.4 (WebGL chat render cap fix)
  so UPM consumers keep both package versions in sync.

## 4.11.3 - 2026-06-23

- **Lockstep package version for Unity inspector fix.** No portable CoreAI API or runtime behavior
  changed in this drop; `com.neoxider.coreai` is versioned with `com.neoxider.coreaiunity` so UPM
  consumers can keep both package versions aligned.

## 4.11.2 - 2026-06-23

- **Force-inject a skill into agent history (no model turn).** New
  `AgentSkillInjection.InjectSkillIntoHistory(store, roleId, skill)` pushes a `SkillSet`'s `read_skill`
  payload (instructions + tool schemas) straight into a role's history — exactly as if the agent had
  already called `read_skill` — without running the agent. The agent does not start a response; the skill
  is just available on its next turn. Stored with the internal `"tool"` history role, so the model sees it
  while the visible chat stays clean. `ReadSkillLlmTool.BuildSkillPayloadJson` builds the payload (always
  includes instructions, not gated on the skill having callable tools).

## 4.11.1 - 2026-06-23

- **Tool failures are surfaced again, accurately.** A failed tool-only turn now resolves to
  `Tool call failed: <tool>: <reason>.` (e.g. `manage_mods: attempt to index a function value`) instead of
  the misleading generic `LLM request failed.` / `structured validation failed`. This reverses the 4.10.4
  hide at the orchestrator level; the chat UI still gates these `Tool call …` lifecycle lines symmetrically
  by `ShowToolCallsInChat` (hidden = hidden for both success and failure), so a clean-chat configuration
  stays clean while the model always receives the full error. Restores the `AiOrchestratorHistory` /
  `AiOrchestratorToolFailureFallback` tests that pin this behavior.
- **Documented tool-call result logging.** `TOOL_CALL_SPEC.md` now describes the per-call
  `[ToolCall] … status=OK|FAIL dur=…ms args=… result=…` debug line and the `LogToolCalls` /
  `LogToolCallArguments` / `LogToolCallResults` / `LogMeaiToolCallingSteps` flags, plus how success/failure
  is surfaced to the model vs the user. New EditMode tests assert the FAIL line carries the tool name,
  status, and the real result detail.

## 4.11.0 - 2026-06-23

- **Live-turn diagnostics.** `AgentTurnTrace` now carries the turn `Status` (`Completed`/`Failed`),
  a `RecordedAtUtcTicks` timestamp, and the observed `ToolCalls` (name, success, duration, source,
  detail). `AiOrchestrator.RecordTrace` populates these from the completion result without changing
  orchestration or persistence behavior.
- **Readable turn-trace sink.** New `IAgentTurnTraceReader.TryGetLatestTrace(roleId, out trace)`.
  `InMemoryAgentTurnTraceSink` implements it, retaining the latest trace per role (bounded) in
  addition to the existing ring buffer. The default `NullAgentTurnTraceSink` is unchanged, so the
  feature degrades gracefully when no readable sink is registered.

## 4.10.5 - 2026-06-22

- Restore live token streaming for tool-declared turns (4.10.4 buffered them and lost streaming).
  Keep the failed-tool status suppression.

## 4.10.4 - 2026-06-21

- **Failed tool-only completions stay model/internal-only.** `AiOrchestrator` no longer synthesizes visible
  `Tool call failed: ...` / `Tool calls failed: ...` assistant text when a streaming or non-streaming tool
  round has no real model answer. The Unity streaming retry instruction for failed tools is unchanged.

## 4.10.3 - 2026-06-21

Adversarial module audit fixes (core). Two independent passes (find + verify) over the LLM transport,
orchestration/context, memory/skills, and Lua-execution clusters; the items below were confirmed by both.

- **SSE idle-timeout no longer leaks timers.** `MeaiOpenAiChatClient.ReadWithIdleTimeoutAsync` drove a
  fresh `Task.Delay(timeout)` per 8 KB read and never cancelled it when the read won, leaving one live
  timer + `CancellationTokenRegistration` per read for the full timeout. It now uses a per-read linked
  CTS, cancels it on the hot path, and observes the abandoned read so a post-dispose fault is not raised
  as an unobserved task exception.
- **Unified error path for the exception-based retry loop.** `LoggingLlmClientDecorator` now catches
  `OperationCanceledException` (rethrow) and non-retryable exceptions (structured `Ok=false` result)
  inside the exception-retry loop, matching the result-based loop instead of letting a raw exception
  escape `CompleteAsync`.
- **Order-independent duplicate tool-call detection.** `ToolExecutionPolicy` canonicalizes argument keys
  (sorted) before hashing the call signature, so the same call re-emitted with a different key order is
  recognized as a duplicate.
- **Token-calibration fixes.** The streaming path no longer double-feeds the calibration EMA (the
  redundant `RecordTokenObservation` after `SanitizeAndPublish` was removed), and
  `CalibratingTokenEstimator` persists the scale **after** releasing its lock so estimation no longer
  serializes behind a disk write.
- **Context-overflow retry actually shrinks.** When history summarization is disabled (or a fixed recent
  budget override is set), `AiOrchestrator` now clamps the history budget by the per-retry-shrunk policy
  budget, so a context-overflow retry no longer re-sends a byte-identical oversized request.
- **Streaming consumer cancels its producer.** `QueuedAiOrchestrator` links a consumer-abandonment token
  into the producer, so breaking out of the public stream (without cancelling the caller token) stops the
  inner LLM stream instead of draining it into an unbounded queue off-screen.
- **Injective memory scope keys.** `ScopedAgentMemoryStoreDecorator` length-prefixes each scope part, so
  distinct user/session tuples can no longer collide on the same key (a cross-user isolation breach). The
  unscoped default path (bare role id) is unchanged.
- **Skill tool-name collisions are visible.** `call_skill_tool` keeps the first-registered tool for a
  duplicate name (deterministic) and logs a warning, instead of silent last-write-wins misrouting.
- **Memory tool correctness.** `append` now dedupes on whole trimmed lines (not a case-insensitive
  substring, which silently dropped short facts), and mutations load the state once and thread it through
  `SaveMutation` instead of re-reading the store mid read-modify-write.
- **Lua mod runtime hardening.** Event dispatch snapshots the handler list (so a handler calling
  `hooks_on` for the in-flight event can no longer throw `InvalidOperationException` out of `Tick`),
  honours the no-drop contract by only dequeuing an event when the budget covers all its handlers, and
  guards per-mod dispatch so one mod cannot abort the whole tick. `SecureLuaEnvironment.RunChunk` now
  applies the documented `OneShotHardLimitSteps` (500k) instead of the guard's 200k default.

## 4.10.2 - 2026-06-21

- Version aligned with `com.neoxider.coreaiunity` (the packages are kept in lockstep with identical versions). There were no functional core changes; this release's changes were in the Unity layer (see `CoreAiUnity/CHANGELOG.md`: chat tool-call notification display fix and EditMode test fixes).

## 4.10.0 - 2026-06-20

- **Vision / image input (core).** `MeaiOpenAiChatClient` now serializes image content (MEAI
  `DataContent` / `UriContent` with an `image/*` media type) on a message as OpenAI multimodal
  `content` parts (`{type:"text"}` + `{type:"image_url"}` with a data URI), so a vision-capable model
  actually receives attached images. Text-only messages are unchanged. Exposed
  `BuildOpenAiMessageContent(...)` for verification (covered by `MeaiOpenAiVisionEditModeTests`).
- **Persistent file-backed Lua mod packages.** New `ILuaModSourceStore` (with `NullLuaModSourceStore`
  default and a host-side `FileLuaModSourceStore`) persists a mod's **source plus its
  `LuaModManifest`** (`id`, `name`, `description`, `version`, `author`, `capabilities`, `active`,
  `entry`). The file-backed store lays each package out under
  `persistentDataPath/CoreAI/Mods/<id>/` as `manifest.json` + `main.lua`. This is separate from the
  per-mod `store_set`/`store_get` key/value store (`FileLuaModStore`); the source store persists the
  mod itself. With no store wired the runtime uses `NullLuaModSourceStore` and behaves exactly as
  before (in-memory only).
- **Auto-persist + rehydrate.** `LuaModRuntime` gains a `sourceStore` constructor parameter (appended
  with a default, `autoPersistMods` defaults to `true`, so all existing callers compile unchanged).
  Every successful `LoadMod` / `ReloadMod` auto-saves the source and manifest; `UnloadMod` marks the
  stored package dormant (`Active = false`) instead of deleting it. All store calls are best-effort —
  a store failure is logged and never aborts a load. On startup `RehydrateFromStore(hostGrant,
  allowFull = false)` re-loads every active stored mod, returning the count restored, so a mod loaded
  once via chat survives a restart. The `manage_mods` tool auto-persists through this path.
- **Export / import / forget — move mods between players.** `ExportMod(id)` returns a self-contained
  bundle `{"manifest":{...},"source":"..."}` (or `null` for an unknown id); `ImportMod(bundleJson,
  hostGrant, allowFull = false)` loads it on another host; `ForgetMod(id)` permanently removes the
  stored package. The `manage_mods` tool exposes the matching `export`, `import`, and `forget` actions
  alongside `load`, `reload`, `unload`, `list`, and `get_source`. A mod folder can also be copied
  directly between players' `persistentDataPath/CoreAI/Mods/<id>/`.
- **Full OFF by default for persisted/shared mods (security).** `RehydrateFromStore` and `ImportMod`
  both intersect the mod's requested capabilities with the host grant and then strip
  `LuaCapabilities.Full` unless the host explicitly passes `allowFull: true`. Capability parsing is
  fail-closed (an empty/unparsable manifest capability string resolves to `None`, not `All`). A
  persisted, rehydrated, imported, or copied mod can never silently escalate to reflection access.
- **First-class `.lua` TextAssets.** A `.lua` ScriptedImporter imports any `*.lua` file as a
  `TextAsset`, so mods can be authored with a real `.lua` extension (editor recognition, drag-and-drop
  references) instead of the `.lua.txt` workaround; `asset.text` returns the source. The importer is
  text-only with no MoonSharp dependency, so it works in no-Lua builds too.
- **Docs.** Added `FIRST_MOD.md` ("Your first Lua mod in 5 minutes"): what a mod is, a copy-paste
  minimal mod, the capability tiers, the three ways to load (agent / C# / `.lua` TextAsset),
  persistence, sharing, and a Full-mode example with the security note. `LUA_GAME_API.md` gains a
  Persistence & Sharing section; `LUA_ACCESS_MODES.md` notes that persisted/shared mods are non-Full
  by default. Ships with a no-LLM Full-mode mod demo and example `.lua` mods.

## 4.9.0 - 2026-06-20

- **Context pruning — stale reasoning.** `ConversationHistoryPruner` now strips stale
  `<think>…</think>` reasoning blocks from every assistant turn except the newest one (lossless,
  prune-before-summarize, prompt-copy only — durable history is untouched). Assistant turns that are
  pure reasoning are dropped. Completes the roadmap §7 "prune stale thinking" item alongside the
  existing superseded-tool-result and duplicate collapsing.
- **WebGL Lua (core).** `SecureLuaEnvironment.WebGlLuaOptIn` + `ICoreAISettings.EnableLuaOnWebGl`
  (default **`true`**): `IsSupported` now honors the setting on the WebGL player instead of a hard block.
  Added `SecureLuaEnvironment.TryRunSelfTest(out report)` for a player-side sandbox self-test.
- **Audit hardening (core).** Sandbox now caps `string.format` output (allocation-bomb parity with
  `string.rep`). `LuaModRuntime` adds a global per-tick event-dispatch budget on top of the per-mod cap
  (bounds worst-case main-thread stall with many mods; surplus carries over, never dropped). Lua
  world-transaction state is reset/aborted on every top-level run (`ILuaTransactionScope`) so an aborted
  `coreai_world_begin` cannot leak buffered commands into the next script. The streaming SSE tool-call
  accumulator keys parallel calls by index and **surfaces** malformed/truncated argument JSON instead of
  silently sending empty args. `SmartToolCallingChatClient.TrimToolCallHistory` trims assistant+tool turns
  as coupled pairs so a `tool` message is never orphaned (provider 400). Re-audit follow-ups: the
  world-transaction reset also covers the mod tick loop (per guarded handler/timer); malformed streamed
  tool-call arguments are **rejected before execution** (not just logged); mod event dispatch rotates
  round-robin so no mod starves under sustained load; SSE tool calls emit in ascending index order.

## 4.8.1 - 2026-06-19

- **Release sync.** `com.neoxider.coreai` is bumped to `4.8.1` to stay version-aligned with
  `com.neoxider.coreaiunity`. No portable-core API changes; the Unity package adds Unity 6.5
  `PanelRenderer` chat-host compatibility while preserving the Unity 6.3 `UIDocument` path.

## 4.8.0 - 2026-06-18

- **Chat UI text options.** Added optional `ICoreAiChatTextOptions` and matching `CoreAiChatOptions` fields for
  send/stop/clear/collapse/open-chat labels and tooltips. The original `ICoreAiChatOptions` contract remains
  source-compatible for host projects that provide custom options.

## 4.7.0 - 2026-06-18

- **Reflection-free skill proxy path.** `call_skill_tool` no longer manually reflects delegates, `Task.Result`, or
  parameter metadata. The proxy now invokes skill tools through `IJsonInvocableLlmTool` when available, or through
  the existing MEAI `AIFunction` contract.
- **Skill actions and tool calls.** `DelegateLlmTool` now exposes both `IAIFunctionLlmTool` and
  `IJsonInvocableLlmTool`, so delegate-backed tools and void actions can be placed inside `SkillSet` and called via
  `call_skill_tool`; empty/void results return an explicit `{"success":true}` payload to the model.
- **Skill-only agents validate correctly.** `AgentBuilder.ValidateOnBuild()` now treats registered skills as a valid
  tool source for `ToolsAndChat` / `ToolsOnly` agents instead of warning that no tools are present.

## 4.6.2 - 2026-06-18

- **Version alignment.** Patch release aligned with `com.neoxider.coreaiunity` 4.6.2 for UPM consumers that pin
  matching package versions.

## 4.6.1 - 2026-06-18

- **NoLua package compile fix.** Patch release aligned with `com.neoxider.coreaiunity` 4.6.1 so UPM consumers can
  pin matching versions when Lua is disabled.

## 4.6.0 - 2026-06-18

- **SkillSet tool execution hardening.** `read_skill` / `call_skill_tool` now share a resolver that supports
  `DelegateLlmTool`, `IAIFunctionLlmTool`, and `IAIFunctionsLlmTool`, serializes structured MEAI results
  consistently, and returns explicit tool results or errors back to the model.
- **Skill meta-tools respect allowlists.** Restricted tool runs keep the SkillSet meta-tools when the allowlist
  intersects a skill's inner tool names, so agents can still call allowed tools through `call_skill_tool`.
- **Required-tool retry.** Smart tool calling retries a required tool when the model emits plain text or omits
  the call, then switches back to auto tool choice after the forced call succeeds.
- **Agent session diagnostics split.** `AgentSessionSnapshot` now exposes separate system-prompt and history-only
  text views, and live role discovery can include prompt-provider roles from manifests in addition to policy roles.
- **Memory tool argument tolerance.** `memory` write/append/insert/delete/rename operations accept `new_text` as a
  fallback content field when a model fills the edit field instead of `content`.
- **Lua access-mode docs cleanup.** Replaced the old access-mode audit artifact with `LUA_ACCESS_MODES.md` and
  moved non-blocking future Lua/world work into the Unity backlog.

## 4.5.0 - 2026-06-18

- **Repository line-ending normalization.** Added Unity-friendly `.gitattributes` coverage for source, YAML assets,
  Visual Studio project files, and common binary assets to prevent CRLF/LF churn and binary phantom diffs.
- **Streaming context-overflow recovery.** `AiOrchestrator.RunStreamingAsync` now mirrors the bounded
  `MaxContextOverflowRetries` recovery path from `RunTaskAsync`, rebuilding the request with increasing
  `ContextRetryLevel` before any visible stream text is emitted.
- **Cache-safe memory placement.** Tail placement is now the only runtime path, and the old prefix-placement
  setting was removed. The stable `## Memory` system-prefix block is a cached snapshot; mid-session memory edits
  are sent as a separate `## Memory (updates)` system-role tail message, then consolidated back into the prefix
  only at a cold-cache boundary: initial snapshot, conversation compaction, or context-overflow retry.
- **Memory read action.** The built-in `memory` tool now supports `action = "read"` and returns the current durable
  memory document, length, and latest version without mutating the store.
- **Wait tool.** Added portable `WaitLlmTool` and `AgentBuilder.WithWaitTool(...)` so an agent can intentionally
  pause for a bounded number of seconds, receive a normal tool result, and continue the same tool-calling loop.
- **Tool-result pairing regression coverage.** EditMode tests now guard that native tool calls are followed by
  `FunctionResultContent` with the original call id, so the next model iteration receives the actual tool result.
- **Empty tool-result normalization.** A tool that returns `null` or an empty payload is now converted into an
  explicit successful JSON tool result instead of silently looking like a missing return.
- **Token calibration persistence.** Added `ITokenCalibrationStore`; Unity hosts persist calibration scale per model
  while portable hosts keep a no-op default store.
- **Full Lua blacklist policy.** Full-tier reflection bindings can now receive an `IFullLuaAccessBlacklistPolicy`
  so host games can deny selected component types or members even when Full Lua access is enabled.
- **Lua mod MessagePipe bridge.** `LuaModRuntimeTicker` publishes `LuaModEventEmitted` through MessagePipe for host
  UI/telemetry/repair flows that should observe persistent mod `report()` output without polling the runtime.

## 4.4.0 - 2026-06-15

> Context management overhaul (Claude Code / Cline / Kilo-grade): prefix/tail placement, threshold compaction, tool-result policy, API-token calibration, bounded overflow recovery, context pruning, world-state tail, deterministic prefix, prompt-cache verification + tool-call/memory fixes. See entries below.

- **Agent memory clear regression fix.** The memory tool `clear` action now removes the role key instead of saving an empty versioned row.
- **Tool result memory defaults.** Built-in `Programmer` and `CoreMechanicAI` now default to `ToolResultMemoryPolicy.Full`; other built-in roles keep `CompactSummary`.
- **Prompt-cache usage verification.** Added cache read/write token counters to LLM completion/stream
  results, `LlmUsageRecord`, `LlmUsageReported`, and turn diagnostics, with MEAI
  `UsageDetails.AdditionalCounts` parsing for provider cache counters.
- **Compaction by threshold.** Added `ICoreAISettings.ConversationCompactionTriggerRatio` (default `0.8`)
  and `ConversationContextBuildArgs.CompactionTriggerRatio`. Deterministic and LLM-assisted context managers
  now leave all history verbatim and do not call `SaveSummary` while estimated history tokens are below
  `historyBudget * ratio`; unset/invalid request ratios fall back to the CoreAI default threshold.
- **Deterministic tool contract prefix.** Added shared ordinal-by-name tool ordering, canonical
  Newtonsoft JSON schema rendering with recursively sorted object keys for text-shaped tool contracts,
  and EditMode regression coverage that guards stable fixed-input system prefixes from generated
  GUID/timestamp leakage.
- **Dynamic world-state observation placement.** `AiPromptComposer.BuildRuntimeContext` now exposes the
  per-role/global runtime context section independently, and the tail-placement path can send it as the last
  system-role chat-history message headed `## World State`; flag-off placement in the system prompt was removed
  in 4.5.0.
- **Context editing before compaction.** Added `ConversationHistoryPruner` and roadmap §7 settings
  (`EnableContextPruning`, `MaxRetainedToolResultMessages`) so prompt-history copies collapse exact
  consecutive duplicates and retain only the newest durable `tool` / `## Tool Results` observations before
  budget partitioning. Durable chat history stores are not modified.
- **Emergency context-overflow recovery.** `AiOrchestrator.RunTaskAsync` now performs bounded
  multi-pass recovery for `LlmErrorCode.ContextLengthExceeded`: `ICoreAISettings.MaxContextOverflowRetries`
  defaults to `3` (`0` disables), retry passes advance `ContextBudgetRequest.ContextRetryLevel`, and
  `DefaultContextBudgetPolicy` applies a `0.75^level` history-budget factor instead of the old one-shot
  halving.
- **Token accounting calibration.** Added a portable `CalibratingTokenEstimator` registered as the default
  `ITokenEstimator`, with Latin-preserving script-aware estimates, higher Cyrillic/CJK density, and bounded
  EMA calibration from observed real prompt-token usage behind `ICoreAISettings.EnableTokenCalibration`
  (default true). `HeuristicTokenEstimator` remains as the simple fallback.
- **Tool result memory policy.** Added per-role `ToolResultMemoryPolicy` with default
  `CompactSummary`; executed tool results can now persist into chat history as one `tool` entry and
  replay as provider-safe user observations on later turns.
- **Context prefix stability flag.** Added an opt-in setting so `## Conversation Summary` can be sent as the first
  system-role chat-history message, before recent verbatim turns, instead of rewriting the system prompt prefix.
  The opt-in flag was later removed when tail placement became the only supported path.
- **Default context window raised to 128K.** `CoreAISettings.ContextWindowTokens` and related
  last-resort context-budget defaults now use `131072` tokens instead of `8192`; per-role
  `RoleMemoryConfig.ContextTokens` defaults to `0`, meaning inherit the global
  `ICoreAISettings.ContextWindowTokens` unless a role explicitly overrides it.
- **Agent session inspector Edit Mode snapshots.** `AgentSessionInspector` can now build a read-only best-effort snapshot from serialized settings/prompts/policy inputs and marks live request-only fields as `(unavailable in Edit Mode)`.
- **Conditional tool contract prompt.** Native tool-calling backends now receive the minimal
  `## Tool Contract` guidance while text-shaped/local backends keep the full `Available tools`,
  schema, and JSON-call prompt block.
- **OpenAI-compatible reasoning controls.** `IOpenAiHttpSettings` now exposes a tri-state
  `ReasoningMode` (`ProviderDefault`, `Disabled`, `Enabled`), optional `ThinkingBudgetTokens`, and
  `ExtraBodyJson`. `MeaiOpenAiChatClient` merges provider-specific JSON into both streaming and
  non-streaming chat completions and emits Qwen/vLLM-style `enable_thinking` /
  `chat_template_kwargs.enable_thinking` only when the mode is explicitly not provider-default.
- **Lua mod report logging control.** `LuaModRuntime` now mutes persistent mod `report()` output by
  default and exposes per-mod report logging state so hosts can opt into diagnostics without timer
  mods flooding the console.
- **WorldEdit transform coverage.** Non-Full Lua WorldEdit now exposes safe spawn, destroy, parent,
  move, rotate, and set-transform commands, and Programmer guidance directs visible scene edits
  through `coreai_world_*` APIs instead of hard-coded Full-mode visual recipes or invented `game.*`
  APIs.
- **Lua mod runtime errors are observable.** `LuaModRuntime` now raises `ModHandlerErrored` when an
  active mod's hook or timer fails during `Tick`, allowing hosts to route asynchronous mod failures
  into repair or telemetry flows instead of only logging and incrementing `ErrorCount`.
- **TMP-safe strings.** Decorative Unicode glyphs in user-visible strings were replaced with ASCII where the default TMP/WebGL font cannot render them; prompt-context ellipses (`…`) used in conversation-summary budget math were deliberately kept as single characters so the `MaxSummaryChars` accounting and its EditMode coverage stay correct.
- **English-only docs.** Remaining Russian text in `Assets/CoreAI/Docs` was translated to English and the `_RU` doc mirrors were removed.

## [4.2.0] - 2026-06-13

- **Full-tier member visibility split.** `CoreAiFullUnityLuaRuntimeBindings` now exposes only **public** members by default; non-public access is an explicit opt-in (`allowNonPublicMembers` ctor flag). The reflection member cache is keyed by visibility so public-only and private-enabled bindings never collide.
- **Full Lua Mode guidance.** The built-in Programmer prompt and `execute_lua` metadata now document the diagnostic-first Full workflow: inspect with one-shot Lua, read `Success` / `Output` / `Error`, then use `manage_mods` for persistent hook/timer behavior. The guidance explicitly forbids invented Lua APIs such as `game.enemies`, `game.create`, and `GameObject.Find`.
- **Release sync.** `com.neoxider.coreai` is bumped to `4.2.0` to stay version-aligned with the Unity package's mod-driven Unit Forge / Full Access demos and the optional-module editor tool.

## [4.1.0] - 2026-06-12

- **Lua mod lifecycle metadata for host managers.** `LuaModRuntime.ModSourceUnloaded` now reports the unloaded source and capability tier, allowing host UIs to move a mod from active to saved/inactive state without losing source code.
- **Release sync.** `com.neoxider.coreai` is bumped to `4.1.0` so the portable core and Unity package stay version-aligned for the new wave auto-battler mod-management demo.

## [4.0.8] - 2026-06-12

- **Lua mod host persistence hooks.** `LuaModRuntime` now raises `ModSourceLoaded` after successful `LoadMod`/`ReloadMod` and `ModSourceUnloaded` after `UnloadMod`, including automatic unloads. The runtime still does not autoload arbitrary mod source by itself; hosts and demo scenes can now persist their selected mod set without coupling that policy into the generic Lua runtime.

## [4.0.7] - 2026-06-12

- **Release sync.** `com.neoxider.coreai` is bumped to `4.0.7` so portable CoreAI and `com.neoxider.coreaiunity` remain version-aligned. Unity-side LiveMechanics persistence and docs changes are listed in the Unity package changelog.

## [4.0.4] - 2026-06-12

- **Lua tool contract accuracy.** `execute_lua` metadata no longer advertises scene-specific helper globals such as `create_item()` as if they were always available. The tool now points Programmer agents at the real generic rule-slot APIs (`logic_list`, `logic_define`, `logic_reset`, `report`) and includes a working `loot_formula` example for live-mechanics edits.
- **MoonSharp callback guidance.** `manage_mods` metadata now shows valid Lua callback syntax for `hooks_on('event', function(...) ... end)` and `hooks_every(seconds, function() ... end)`, preventing invalid `hooks_on('event') function() ... end` mod code.

## [4.0.3] - 2026-06-12

- **Tool schema repair feedback.** `ToolExecutionPolicy` now validates required arguments from each tool's `ParametersSchema` before invoking the MEAI function binding. Malformed calls such as `manage_mods` with `{}` now return a normal failed tool result that names the missing `action` argument and includes the expected JSON schema, so the Programmer can retry with corrected arguments instead of receiving a low-level `AIFunctionFactory` exception.

## [4.0.2] - 2026-06-12

- **Tool-only chat failure fallback.** `AiOrchestrator` now preserves terminal `ExecutedToolCalls` from streaming completions and turns empty tool-only responses into an explicit tool status message. Failed `Programmer` tool turns now surface the real tool error, for example `manage_mods 'load' failed: attempt to index a function value`, instead of running structured validation and showing `Response is empty or whitespace`.
- **Tool trace diagnostics.** `LlmToolCallTrace` now carries a short `Detail` string for failed native, missing, unknown, duplicate, and timeout tool calls so UI fallbacks and logs can report the actual failure cause.

## [4.0.1] - 2026-06-12

- **Chat source history for tool roles.** `AiOrchestrator` now enables short-term chat history for requests with `SourceTag = "Chat"` even when the target role defaults to history-off (for example `Programmer`). The global role policy is not mutated and disk persistence stays off unless the role explicitly enables it, so non-chat Lua/repair tasks remain isolated while chat panels keep session instructions such as response language.

## [4.0.0] - 2026-06-12

Major release: Lua as a second game language (production-ready), capability tiers, Full opt-in mode, LLM mod tools, demo scenes, and performance hardening.

### Breaking / API

- **`LuaCapabilities.All` no longer includes `Full`.** Full reflection access requires explicit `LuaCapabilities.Full` (host opt-in via `CoreAILifetimeScope.enableFullLuaAccess` or per-mod caps).
- **`ICapabilityScopedLuaBindings`** — binding providers can gate APIs by capability tier; `AggregatingGameLuaRuntimeBindings` implements it.
- **`GameLuaBindingsExtensibility.Register(bindings, requiredCapabilities)`** — extensions declare minimum capability flags.
- **`CoreAiPrefabRegistryAsset.OnValidate`** — invalidates internal prefab cache when edited (fixes stale MCP/asset patches).

### Lua runtime & security

- **`LuaLogicSlots`**, **`LuaModRuntime`** (atomic reload, consecutive error budget, capability-scoped game APIs).
- **`LuaModsLlmTool` (`manage_mods`)** — list/get_source/load/reload/unload; `LuaModRuntime.TryGetModSource`.
- **`GameLuaToolExecutor` + DI** — `execute_lua` / `manage_mods` registered for built-in **Programmer** role in `WorldCommandsInstaller`.
- **`CoreAiFullUnityLuaRuntimeBindings`** — Full-tier `unity_*` reflection APIs (allow-all; planned blacklist documented).
- Scene whitelist: **`luaAllowedScenes`** on `CoreAILifetimeScope` → `coreai_world_load_scene`.
- Sandbox: rate limits, output caps, capability fail-closed for restricted mods.
- `LuaApiRegistry` now exposes callbacks through MoonSharp `CallbackFunction` wrappers, so host validation failures surface to Lua as `ScriptRuntimeException` instead of leaking raw CLR exceptions.

### World commands

- **`ICoreAiCustomWorldCommandHandler`** + `CoreAiWorldCommandExecutor.RegisterCustomHandler` — extend world actions from game code.
- **`set_color`** uses **`MaterialPropertyBlock`** (fixes material instance leak).

### Demos (`Assets/CoreAI.Demos/`)

- LuaMods, WorldCommands, Skills, LiveMechanics (LLM + chat). FullAccess: controller + README at this release; `FullAccessDemo.unity` scene + PlayMode smoke completed in a later release (done).

### Performance

- `LuaModRuntime.Tick` — reusable mod list scratch buffer (no per-frame array alloc).
- See **`Docs/PERF_REVIEW_2026-06-12_RU.md`**.

### Diagnostics

- CoreAiUnity runtime: direct `Debug.*` replaced with **`IGameLogger` / `GameLoggerUnscopedFallback`** (`CoreAi.cs`, chat panels, `LuaCoroutineRunner.SetLogger`, etc.).

### Docs

- `LUA_GAME_API.md`, `LUA_BEST_PRACTICES_RU.md`, `MOONSHARP_NATIVE_APIS_RU.md`, `LUA_ACCESS_MODES.md`, demo READMEs, perf review.

## [v3.2.0] - 2026-06-11

### API design

- **`RoleId`** — strongly-typed agent role identifier (`readonly struct`, ordinal equality, `IsBuiltIn`, statics for all built-in roles like `RoleId.SmartChat`). Implicitly convertible to/from `string`, so it works with every existing API (`AgentBuilder`, `AiTaskRequest.RoleId`, `CoreAi.AskAsync`) without overloads. Inline `"SmartChat"` literals in the runtime replaced with `BuiltInAgentRoleIds.SmartChat`.
- **`AskWithCallback` replaces `Ask` as the fire-and-forget convenience.** The primary idiom is awaitable `AskAsync`; the callback overload is now explicitly named `AskWithCallback(message, onDone?, priority)`. The old `Ask(...)` remains as an `[Obsolete]` alias.

### Lua sandbox

- **Generation rate limit (runaway-loop guard).** New `LuaGenerationRateLimiter` (sliding window, default 20/60 s, injectable clock/limits, `maxPerWindow <= 0` disables) wired into `LuaAiEnvelopeProcessor`: both envelope executions and scheduled Programmer repair generations consume slots. A saturated window fails the envelope with a `Lua rate limit exceeded` message and skips repair scheduling, so a failing script cannot spin a generate→fail→repair loop against the LLM. Per-script instruction/time budgets (`InstructionLimitDebugger`) unchanged.

### Diagnostics

- **`TokenBudgetTextFormatter`** — pure (UnityEngine-free) text layer extracted from the Unity token-budget overlay: `FormatTokens` / `FormatCost` / `FormatLoad` (+ `nearLimit` flag) render the same diagnostic strings for any UI (IMGUI overlay, custom UGUI panels, logs). Covered by new EditMode tests.

## [v3.1.0] - 2026-06-10

### Reliability

- **Retry backoff now uses full jitter.** `LoggingLlmClientDecorator` retry delays are drawn uniformly from `[0, base]` where base is the previous exponential `min(2 * 2^attempt, 30)` seconds, so fleets of agents no longer retry in lockstep after a mass 429 (thundering-herd fix). Explicit `Retry-After` headers still take precedence. Delay computation is exposed as `ComputeBackoffBase` / `ComputeBackoffDelay` for testability.
- **Tool-name repair metric.** `ToolExecutionPolicy.ToolNameRepairCount` (process-wide, `Interlocked`) counts casing repairs performed by `TryRepairToolName`, making systemic prompt degradation observable; `ResetToolNameRepairCount()` for test/session resets.
- **Retry error-feedback reclaimed from history.** After a fully-failed tool-call batch is retried successfully, `SmartToolCallingChatClient` removes the obsolete error-feedback message pairs (assistant tool-call + tool result, removed as whole pairs so the history stays OpenAI-valid) instead of letting them consume tokens until the general trim. Partially-failed batches are kept, since their successful results may still inform the model.

### Lua sandbox

- **Two escape vectors closed.** `StripRiskyGlobals` now also removes `string.dump` (MoonSharp implements it — compiled-bytecode leak; nilling it in the shared string table also blocks `('x'):dump()`) and `collectgarbage` (heap/timing oracle stub).
- New escape-vector EditMode tests: `string.dump` (direct and via string metatable), `coroutine.close`, `collectgarbage`, `getmetatable('')`, `rawget`/`_G` bypass attempts.

### Agent memory

- **Off-main-thread async I/O.** `FileConversationSummaryStore` gains `LoadSummaryAsync` / `SaveSummaryAsync` / `ClearSummaryAsync` that run file I/O on the thread pool, serialized with the sync paths via a per-store `SemaphoreSlim`. Atomic tmp-file write semantics unchanged; `ConfigureAwait(false)` throughout; WebGL falls back to inline execution (no threads).

### Diagnostics

- New `TokenBudgetCalculator` (pure, testable) backing the Unity-side token-budget overlay: tokens/request, optional $/session from configurable per-1K prices, rolling-window request-load aggregation.

## [v3.0.0] - 2026-06-10

### Major — Lua/MoonSharp is now an optional module

- **`COREAI_NO_LUA` scripting define.** Defining `COREAI_NO_LUA` compiles the entire Lua sandbox out of both `CoreAI.Core` and `CoreAI.Source`, exactly mirroring the existing `COREAI_NO_LLM` opt-out convention. Core orchestration, LLM, chat, and agent memory build and run with no MoonSharp usage; with the define set you may also remove the `org.moonsharp.moonsharp` package.
- Whole-file guarded under `#if !COREAI_NO_LUA`: `SecureLuaEnvironment`, `LuaCoroutineHandle`, `LuaApiRegistry`, `LuaExecutionGuard`, `InstructionLimitDebugger`, `LuaAiEnvelopeProcessor` (Core) and `LuaCoroutineRunner` (Source).
- **Graceful no-op when disabled.** `CorePortableInstaller` and `WorldCommandsInstaller` skip Lua registrations under the define; `WorldCommandsInstaller` falls back to the Core-side `CoreDefaultLuaRuntimeBindings` / `NullLuaExecutionObserver` so the DI graph still resolves. `AiGameCommandRouter`'s `LuaAiEnvelopeProcessor` dependency is compiled out (no longer a hard constructor dependency) so command routing degrades to world-command execution only.
- Lua/MoonSharp EditMode and PlayMode tests are guarded so both build configurations compile. Verified: default build (Lua on) and `COREAI_NO_LUA` build both compile with zero errors.

### Reliability hardening (code audit follow-up)

- **`HttpClientOpenAiTransport` — socket-exhaustion fix.** Replaced per-request `new HttpClient` (disposed every call, sockets stuck in `TIME_WAIT`) with shared `Lazy<HttpClient>` instances over an `HttpClientHandler`. Per-request timeouts are now enforced via a linked `CancellationTokenSource` instead of mutating the shared client's `Timeout`; streaming no longer disposes the shared client. (`HttpClientHandler` is used rather than `SocketsHttpHandler` so the transport stays valid on Unity's .NET Standard 2.0 profile.)
- **Crash-safe atomic JSON writes.** `FileAgentMemoryStore` (4 write sites) and `FileConversationSummaryStore` now write to a `.tmp` file and `File.Replace`/`File.Move` into place, so a crash mid-write can no longer corrupt agent memory or conversation summaries.
- **`LuaCoroutineHandle.Kill()` — real termination.** Replaced the empty `try/catch` (which only set `_disposed`) with a forced yield via MoonSharp `Coroutine.AutoYieldCounter`, plus typed exception handling; `_disposed` guarantees the coroutine is no longer resumable.

### Fixes

- **`CoreAIFacade` portable-Core regression.** Removed a `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (`UnityEngine`) attribute that had been added to the UnityEngine-free `CoreAI.Core` assembly and broke its compilation. The Play Mode / domain-reload static reset of `CoreAIAgent` now lives in the Unity layer (`CoreAi.Invalidate()` calls `CoreAIAgent.Reset()`).
- **`AgentConfigExtensions.AskAsync` validation order.** Role-registration validation now runs *before* the orchestrator-null check, so an unregistered role reports the clear `role not registered` error regardless of whether the orchestrator is initialized yet (the 2.6.5 fail-fast test previously never compiled and so never caught this).
- Timeout now surfaces as `OperationCanceledException` without an inner `TimeoutException` (HTTP transport change above).

### Core policy registration safety (carried from 2.6.5 dev)

- `AgentBuilder.Build()` applies role configuration to `CoreAIAgent.Policy` when policy is already initialized; `BuildDetached()` for policy-free construction.
- `AgentConfigExtensions` fail-fast coverage for unregistered roles; `CoreAi.SetResolver` edit-mode coverage.

### Semver

- **Major bump to `3.0.0`** (lockstep with `com.neoxider.coreaiunity` `3.0.0`): Lua becoming an optional, compile-out module is a structural change to how the packages are consumed.

## [v2.6.5] - 2026-06-10

### Policy registration and orchestration safety

- Tightened `AgentBuilder` API so `Build()` now applies role config to `CoreAIAgent.Policy` by default; added `BuildDetached()` for detached construction without global side effects.
- Added `AgentMemoryPolicy.HasRole(string roleId)` for explicit role-registration checks.
- Added explicit role validation in `AgentConfigExtensions.AskAsync(...)` so unregistered roles fail fast with a clear `role not registered` error instead of implicit fallback behavior.

## [v2.6.4] - 2026-06-06

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.4` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the backend-managed authorization, streaming tool-loop completion, and chat collapse idempotency fixes live in `com.neoxider.coreaiunity`.

## [v2.6.3] - 2026-06-01

### Chat options parity with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.3` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Added portable chat options `EnableStopGeneration` and `ShowClearButton`. Unity consumes these through `CoreAiChatConfig` / `CoreAiChatPanel`; the portable package remains Unity-free.
- Defaults preserve existing behavior: stop generation is enabled and the clear button is shown unless a host explicitly disables them.
## [v2.6.2] - 2026-06-01

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.6.2` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Portable package metadata documents the WebGL streaming continuation fix; the runtime and verification changes for WebGL chat Stop/recovery live in `com.neoxider.coreaiunity`.

## [v2.6.0] - 2026-05-29

### WebGL streaming and Lua platform guard

- Bumped `com.neoxider.coreai` to `2.6.0` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching minor versions.
- `MeaiOpenAiChatClient` now treats OpenAI-style `data: [DONE]` SSE frames as terminal stream sentinels. WebGL native streaming can finish promptly without waiting for the browser connection to close.
- `SecureLuaEnvironment` now exposes an explicit platform support guard. WebGL player builds report Lua as unsupported before MoonSharp can initialize reflection-heavy loader paths that crash IL2CPP/WebGL.
- `LuaAiEnvelopeProcessor` now publishes a controlled Lua failure when the runtime is unavailable instead of constructing the sandbox on unsupported platforms.
- Updated Lua sandbox documentation to state that Lua is temporarily unavailable on WebGL and to describe the supported future restoration paths.

## [v2.5.4] - 2026-05-29

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.4` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the WebGL SSE cancellation and Editor Play Mode main-thread marshaling hardening live in `com.neoxider.coreaiunity`.

## [v2.5.3] - 2026-05-27

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.3` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- No portable runtime behavior change; the Unity fixes live in `com.neoxider.coreaiunity`.

## [v2.5.1] - 2026-05-25

### Lockstep patch with CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.1` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Added portable `IAIFunctionLlmTool` / `IAIFunctionsLlmTool` contracts so Unity MEAI binding can discover tool functions without reflection duck typing.

## [v2.5.0] - 2026-05-24

### Version Parity With CoreAI Unity

- Bumped `com.neoxider.coreai` to `2.5.0` so portable CoreAI and `com.neoxider.coreaiunity` publish with matching versions.
- Updated the Unity package dependency contract to `com.neoxider.coreai` `2.5.0`.
- No additional portable runtime behavior change beyond the release-train alignment for the Unity ScriptableObject wrapper and options/snapshot work.

## [v2.4.0] - 2026-05-24

### Portable Options and Snapshot Contracts

- Added Unity-free runtime options/snapshots for Unity-authored configuration: `CoreAiChatOptions`, `CoreAISettingsOptions`, `OpenAiHttpOptions`, `GameLogSettingsOptions`, `AiPermissionsOptions`, `AgentPromptsDefinition`, and `SkillSetDefinition`.
- Moved Unity-free logging contracts (`GameLogFeature`, `GameLogLevel`, `IGameLogSettings`) into the portable CoreAI package.
- Preserved the rule that `Assets/CoreAI` has no `UnityEngine` dependency; Unity-specific authoring stays in `com.neoxider.coreaiunity`.

### Migration Notes

- Runtime/tests should prefer plain options/classes over mutating Unity `ScriptableObject` assets.
- Unity assets remain supported through wrapper methods in `com.neoxider.coreaiunity`.

## [v2.3.1] — 2026-05-08

### LLMUnity Text-Mode Tool Calling

Local GGUF models (Qwen3.5-4B via LLMUnity/llama.cpp) output tool calls as plain text instead of native `FunctionCallContent`. This release ensures the full SkillSet pipeline works end-to-end on text-only backends.

#### `LlmToolCallTextExtractor`

- **Function-call syntax fallback** — `read_skill("Alchemy")`, `read_skill(Crafting)`, `call_skill_tool("tool", '{"args":"..."}')` are now parsed into `Match` objects. Matches only when the entire trimmed response looks like a function call (prose with parentheses is ignored).
- **`arguments_json` key** — `LooksLikeToolCallJson` and `TryExtract` now accept `"arguments_json"` as an alternative to `"arguments"` (Qwen3.5 emits this non-standard key).
- **String-value args re-parsing** — when `"arguments_json"` contains a serialized JSON string (e.g. `"{\"skill_name\":\"Alchemy\"}"`), the value is re-parsed into a proper JSON object before extraction.

#### `ToolExecutionPolicy`

- **JObject → string normalization** — `ExecuteSingleAsync` now normalizes `Newtonsoft.Json.Linq.JObject` and `JArray` values in `FunctionCallContent.Arguments` to JSON strings before calling `AIFunction.InvokeAsync`. This is the **single chokepoint** for all tool calls (native, text-extracted, function-call syntax), ensuring MEAI delegates with `string` parameters never receive raw Newtonsoft tokens.

#### `CallSkillToolLlmTool`

- **`InvokeDelegateWithJson`** — when a delegate parameter expects `System.String` but the JSON token is `JObject`/`JArray`, serialize to `Formatting.None` string instead of throwing `InvalidCastException`.

#### `SmartToolCallingChatClient` / `MeaiLlmClient`

- **`NormalizeJTokenValues`** helper — converts `JObject`/`JArray` values in argument dictionaries to JSON strings, applied in both streaming and non-streaming text extraction paths.
- **`IsValidToolCallJson`** (streaming) — now accepts `"arguments_json"` key.

## [v2.3.0] — 2026-05-08

### Dual-Backend with Auto-Fallback

- **`FallbackLlmClientDecorator`** — new decorator wrapping primary + secondary `ILlmClient`. When the primary backend fails (exception, `BackendUnavailable`, `RateLimited`, `Timeout`, `ProviderError`, `ContextLengthExceeded`), the request is automatically retried on the secondary. User cancellation (`OperationCanceledException`) is never retried.
- **Streaming fallback** — if the primary streaming enumerator throws on the first chunk, the decorator falls back to secondary streaming transparently.
- **`FallbackCount`** property — tracks how many times the secondary was invoked.

### Inspector: Fallback Backend

- **`CoreAISettingsAsset`** — new **🔄 Fallback Backend (secondary)** section:
  - `enableFallbackBackend` — master toggle.
  - `secondaryApiBaseUrl` — secondary HTTP endpoint.
  - `secondaryApiKey` — secondary API key.
  - `secondaryModelName` — secondary model identifier.
- **`HasValidFallbackBackend`** computed property — true when toggle is on AND URL + model are set.
- **`LlmPipelineInstaller`** — when `HasValidFallbackBackend` is true, the primary `ILlmClient` is wrapped in `FallbackLlmClientDecorator` with a secondary `OpenAiChatLlmClient` built from `SecondarySettingsAdapter`.

### Tests

- 5 new EditMode tests: `Fallback_PrimarySucceeds_SecondaryNotCalled`, `Fallback_PrimaryFails_SecondaryIsCalled`, `Fallback_PrimaryReturnsRetryableError_SecondaryIsCalled`, `Fallback_Cancellation_DoesNotFallback`, `Fallback_MultipleFails_CounterIncrements`.

## [v2.2.0] — 2026-05-08

### Tool Call History Truncation

- **`MaxToolCallHistoryMessages`** (default 20) — `SmartToolCallingChatClient` now trims the oldest tool call message pairs (Assistant + Tool result) from the MEAI message list during long tool-calling loops. Prevents unbounded context growth within a single request.
- When the count exceeds the limit, the oldest pairs are removed while preserving system and user messages.
- Setting exposed in `ICoreAISettings`, `CoreAISettings` static proxy, and `CoreAISettingsAsset` Inspector (🛡️ Resilience & Safety). `0` = no limit.

### Rate Limiter Metrics

- **`RateLimiterMetrics`** struct — snapshot of rate limiter state: `MaxRequestsPerWindow`, `WindowSeconds`, `AcceptedInWindow`, `TotalRejected`.
- **`IInGameLlmChatService.GetRateLimiterMetrics()`** — exposes sliding-window rate limiter diagnostics for dashboard / UI display.
- `InGameLlmChatService` now tracks `TotalRejected` count.

### Tool-Level Retry (clarification)

- `maxConsecutiveErrors` already works globally across all tools in `ToolExecutionPolicy`. Per-tool granularity is unnecessary for the current architecture — the global counter resets on any successful execution, which handles mixed-tool scenarios correctly.

## [v2.1.0] — 2026-05-08

### Production Resilience — Runtime Safety Guardrails

Four runtime guardrails to prevent context overflow, infinite hang-loops, and runaway model generation.

#### New settings (`ICoreAISettings` / `CoreAISettings` / Inspector)

| Setting | Default | Location |
|---------|---------|----------|
| **`MaxToolResultChars`** | `8000` | `ToolExecutionPolicy` — soft-truncates tool result strings before they re-enter the LLM context window. |
| **`DefaultToolTimeoutMs`** | `30000` | `ToolExecutionPolicy` — wraps each tool invocation in a linked `CancellationTokenSource`; if the tool (e.g. HTTP call) hangs, the timeout fires and returns an error result instead of blocking forever. |
| **`MaxResponseChars`** | `0` (disabled) | `SmartToolCallingChatClient` — when > 0, truncates final assistant text to prevent runaway generation. |
| **`MaxToolCallRoundtrips`** | `10` | `SmartToolCallingChatClient` — hard cap on tool-calling loop iterations; prevents infinite recursive tool calling. |

#### Design

- **Centralized enforcement.** Timeout + truncation live in `ToolExecutionPolicy` (covers native + text-extracted calls); roundtrip + response limits live in `SmartToolCallingChatClient`.
- **Zero breaking changes.** All features are additive with safe defaults; existing agents behave identically unless settings are overridden.
- **Inspector integration.** All four settings exposed in **CoreAISettingsAsset** under **🛡️ Resilience & Safety** foldout with tooltips and min-value constraints.

#### Tests

- **`ResilienceFeaturesEditModeTests`** — 8 tests validating truncation, timeout, and roundtrip limits independently of LLM backends.

#### Documentation

- **`README.md`**, **`README_RU.md`**, **`CoreAiUnity/README.md`** — resilience bullet points.
- **`AGENT_BUILDER.md`** — Resilience & Safety section with usage examples.

## [v2.0.0] — 2026-05-08

### Major — Skill-Based Tool Orchestration

Introduces **`SkillSet`** — named groups of tools with dedicated prompt instructions, inspired by the **Microsoft Semantic Kernel `KernelPlugin`** pattern. Skills reduce context bloat by injecting only the active skill's instructions into the system prompt at request time.

#### New public API

- **`SkillSet`** (`CoreAI.Ai`) — immutable container: `Name`, `Instructions` (prompt text), `Tools` (`IReadOnlyList<ILlmTool>`), `ToolNames` (cached `string[]` for `AllowedToolNames`).
  - Constructor: `new SkillSet(name, instructions, params ILlmTool[] tools)`.
  - `FromFile(name, filePath, tools)` — load instructions from a `.txt` / `.md` file on disk.
  - `FromTextContent(name, text, tools)` — load instructions from pre-loaded text (e.g. Unity `TextAsset.text`).
  - `MergeToolNames(params SkillSet[])` — combine multiple skills into one allowlist.
  - `BuildActiveInstructions(params SkillSet[])` — compose `## Skill: {Name}` prompt sections from active skills.
- **`AgentBuilder.WithSkill(SkillSet)`** / **`WithSkills(params SkillSet[])`** — register skill tools and instructions in the fluent builder. Tools are added to the agent's tool list; skills are stored on `AgentConfig.Skills`.
- **`AgentConfig.Skills`** (`IReadOnlyList<SkillSet>`) — skills registered via `WithSkill`. Null when no skills.
- **Skill runtime context provider** (internal at the time) — previously injected only the matching skills' instructions into the system prompt. Current builds keep the lightweight skill catalog in the stable system prefix and load full instructions through `read_skill`.

#### Design

- **Zero orchestrator changes.** Uses existing `AllowedToolNames` + `FilterToolsForRequest()` for tool filtering and existing `IAgentRuntimeContextProvider` + `AiPromptComposer.AppendRuntimeContext()` for instruction injection.
- **Zero new dependencies.** Pattern inspired by Semantic Kernel's `KernelPlugin`, implemented purely on CoreAI's existing abstractions.
- **Backwards compatible.** Agents without skills behave identically to v1.x.

#### Usage example

```csharp
var quizSkill = new SkillSet("Quiz",
    instructions: "When quiz is active, generate questions using spawn_quiz. " +
                  "Wait for the answer, then verify with check_answer.",
    new DelegateLlmTool("spawn_quiz", "Create quiz", (string q) => ...),
    new DelegateLlmTool("check_answer", "Check answer", (int idx) => ...)
);

var lessonSkill = new SkillSet("Lesson",
    instructions: "Explain concepts step by step. Use advance_lesson to proceed.",
    new DelegateLlmTool("advance_lesson", "Move to next topic", () => ...)
);

var teacher = new AgentBuilder("Teacher")
    .WithSystemPrompt("You are a teacher.")
    .WithSkill(quizSkill)
    .WithSkill(lessonSkill)
    .WithMemory()
    .Build();

teacher.ApplyToPolicy(policy);

// Activate only quiz tools + instructions for this turn:
await orch.RunTaskAsync(new AiTaskRequest {
    RoleId = "Teacher",
    AllowedToolNames = quizSkill.ToolNames
});
```

#### Tests

- **`SkillSetEditModeTests`** — tests covering: SkillSet construction, instruction injection, per-request filtering, MergeToolNames, and AgentBuilder.WithSkill integration.

### Semver

- **`2.0.0`** with **`com.neoxider.coreaiunity` `2.0.0`**. Major bump — new public API surface (`SkillSet`, `AgentConfig.Skills`, `AgentBuilder.WithSkill/WithSkills`).

## [v1.7.5] — 2026-05-05

### Lockstep with coreaiunity 1.7.5 (Unity-only)

- **Semver:** **`1.7.5`** with **`com.neoxider.coreaiunity` `1.7.5`**. No portable **`CoreAI.Core`** API changes — Unity release adds optional chat tool-call UI and renames **`CoreAISettingsAsset`** temperature override field to **`enableTemperatureOverriding`** (see Unity changelog).

## [v1.7.4] — 2026-05-05

### Lockstep with coreaiunity 1.7.4 (Unity-only)

- **Semver:** **`1.7.4`** with **`com.neoxider.coreaiunity` `1.7.4`**. No portable **`CoreAI.Core`** API changes — Unity release documents LLMUnity runtime host defaults (see Unity changelog).

## [v1.7.3] — 2026-05-05

### Streaming request option (lockstep with coreaiunity 1.7.3)

- **`LlmCompletionRequest.BufferFullStreamingIterationWhenToolsDeclared`** — optional **`bool?`**. When **`Tools`** is non-empty: **`true`** buffers the full assistant iteration before emitting any **`LlmStreamChunk.Text`**; **`null`**/**`false`** (default) keeps the **hybrid JSON hold** (stream only the prefix that cannot be part of incomplete text-shaped tool JSON, then hold until balanced **`{...}`** closes). Intended as an escape hatch for exotic delta fragmentation; Unity **`MeaiLlmClient`** implements both modes.
- **Semver:** **`1.7.3`** with **`com.neoxider.coreaiunity` `1.7.3`**.

## [v1.7.2] — 2026-05-05

### Lockstep with coreaiunity 1.7.2 (WebGL)

- **Semver:** **`1.7.2`** with **`com.neoxider.coreaiunity` `1.7.2`**. No portable **`CoreAI.Core`** API changes — Unity **`CoreAiPersistFs.jslib`** now runs **`FS.syncfs`** single-flight (queues coalesced follow-up) so concurrent **`CoreAi_PersistFsSync`** calls from **`FileAgentMemoryStore`** no longer trigger Emscripten’s *“2 FS.syncfs operations in flight”* warning or related WebGL stalls.

## [v1.7.1] — 2026-05-05

### Lockstep & tests

- **Semver:** **`1.7.1`** with **`com.neoxider.coreaiunity` `1.7.1`**. No portable API changes — Unity EditMode adds **`FailedCompletion_BackendUnavailable_RetriesAndSucceeds`** for **`LoggingLlmClientDecorator`** (result-based **`BackendUnavailable`** retry, same as **`RateLimited`** in v1.7.0).

## [v1.7.0] — 2026-05-05

### Streaming — `LlmStreamChunk` marker for buffered Meai iterations

- **`LlmStreamChunk`** — **`BufferedStreamingNoToolBinding`** plus optional **`BufferedStreamingUseToolProgressHint`**. **`MeaiLlmClient.CompleteStreamingAsync`** yields marker chunks for unbound iterations, hybrid JSON hold, native tool deltas, and text-shaped tool execute (host chat: short **`StreamingToolProgressHint`** vs animated dots — see **`com.neoxider.coreaiunity` ≥ 1.7.0**).
- **Sampling temperature:** **`ICoreAISettings.OverrideTemperature`** (default **off**). When off, **`MeaiOpenAiChatClient`** omits the JSON **`temperature`** field and **`MeaiLlmClient`** does not set MEAI **`ChatOptions.Temperature`** (HTTP + LLMUnity use backend defaults). When on, **`AiOrchestrator`** sets **`LlmCompletionRequest.SendTemperature`** and sends **`ICoreAISettings.Temperature`**. **`LlmCompletionRequest.SendTemperature`** is also set for LLM-assisted compaction. **`ConfigureHttpApi`** enables the override flag so programmatic HTTP setup still sends temperature.
- **HTTP retries:** **`LoggingLlmClientDecorator`** now retries **`LlmCompletionResult`** with **`RateLimited`** / **`BackendUnavailable`** (same backoff as for **`LlmClientException`**). Previously only thrown exceptions retried; **`MeaiLlmClient`** converts HTTP errors to failed results, so 429 produced no **`LLM ↺`** lines and no second attempt. Default **`ICoreAISettings.MaxLlmRequestRetries`** / asset field is **1** retry (minimum clamp **1**).

## [v1.6.19] — 2026-05-05

### Lockstep with coreaiunity 1.6.19 (Unity-only)

- **Semver:** **`1.6.19`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`CoreAILifetimeScope`** registers **`FileAgentMemoryStore`** on WebGL player so chat history and agent memory JSON persist (with existing **`CoreAi_PersistFsSync`** after writes).

## [v1.6.18] — 2026-05-04

### Lockstep with coreaiunity 1.6.18 (Unity-only)

- **Semver:** **`1.6.18`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`FetchSseOpenAiTransport`** uses synchronous **`TaskCompletionSource`** continuations + true async **`ReadAsync`** so WebGL single-threaded awaits no longer park forever on a non-existent thread pool, and **`Stream.Read`** no longer blocks the JS event loop while waiting for fetch chunks.

## [v1.6.17] — 2026-05-04

### Lockstep with coreaiunity 1.6.17 (Unity-only)

- **Semver:** **`1.6.17`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`FetchSseOpenAiTransport`** + **`CoreAiSseFetch.jslib`** now await the real **`fetch`** response status before returning, so **`MeaiOpenAiChatClient`** sees the actual HTTP code instead of the default **`HTTP 0`** that was masking CORS / network errors as transport failures.

## [v1.6.16] — 2026-05-04

### Lockstep with coreaiunity 1.6.16 (Unity-only)

- **Semver:** **`1.6.16`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity WebGL **`fetch`** default **`credentials: 'omit'`** for SSE (OpenRouter + CORS `*`).

## [v1.6.15] — 2026-05-04

### Lockstep with coreaiunity 1.6.15 (Unity-only)

- **Semver:** **`1.6.15`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`CoreAISettingsAssetEditor`** moves WebGL streaming toggles under **Advanced**.

## [v1.6.8] — 2026-05-03

### Orchestration — scope cancel and `Task.IsCanceled`

- **`QueuedAiOrchestrator`** — handle **`TaskCanceledException`** explicitly (before **`OperationCanceledException`**) in **`RunOneAsync`** and **`RunOneStreamingAsync`**. When the inner **`RunTaskAsync` / `RunStreamingAsync`** await completes with **`TaskCanceledException`** (e.g. **`TaskCompletionSource.TrySetCanceled()`** on a gate task), the queued task must complete as **canceled**, not **faulted**; **`CancelTasks`** on an active scoped task then reports **`Task.IsCanceled == true`** as expected by **`QueuedAiOrchestratorEditModeTests`**.

### Semver

- Lockstep **`1.6.8`** with **`com.neoxider.coreaiunity`**.

## [v1.6.7] — 2026-05-03

### Lockstep with coreaiunity 1.6.7 (Unity-only)

- **Semver:** **`1.6.7`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity **`MeaiLlmClient`** incremental streaming + tests.

## [v1.6.6] — 2026-05-03

### Lockstep with coreaiunity 1.6.6 (Unity-only)

- **Semver:** **`1.6.6`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity chat streaming UI thread hop + clear button UXML.

## [v1.6.5] — 2026-05-03

### Lockstep with coreaiunity 1.6.5 (Unity-only)

- **Semver:** **`1.6.5`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity chat WebGL streaming gate alignment (**`CoreAiChatService`** / **`CoreAiChatPanel`**).

## [v1.6.4] — 2026-05-03

### WebGL browser — OpenAI-compatible HTTP headers vs public API CORS

- **`MeaiOpenAiChatClient.BuildTransportHeaders`** — when **`UNITY_WEBGL && !UNITY_EDITOR`**, omit **`X-Request-Id`**, **`Idempotency-Key`**, **`X-Coreai-Role`**, **`X-Tenant-Id`**, **`X-User-Id`**, and **`X-Session-Id`** (and skip the same names from **`IRequestHeaderProvider.GetHeaders()`**), so **`fetch`** preflight to gateways with a narrow **`Access-Control-Allow-Headers`** list (e.g. **openrouter.ai**) is not rejected before the POST runs. Trace and idempotency remain visible in **`LoggingLlmClientDecorator`** / **`RoutingLlmClient`** logs on the client; use a **same-origin proxy** or a backend that whitelists these headers when you need them on the wire in WebGL.

### Semver

- Lockstep **`1.6.4`** with **`com.neoxider.coreaiunity`**.

## [v1.6.3] — 2026-05-03

### Lockstep with coreaiunity 1.6.3 (Unity-only)

- **Semver:** **`1.6.3`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes — Unity host **`CoreAILifetimeScope`** registers **`FileAgentMemoryStore` in Editor even when the active build target is WebGL** (`#if !UNITY_WEBGL || UNITY_EDITOR`).

## [v1.6.2] — 2026-05-03

### Lockstep with coreaiunity 1.6.2

- **Semver:** lockstep **`1.6.2`** with **`com.neoxider.coreaiunity`**. No portable **`CoreAI.Core`** API or runtime behaviour changes in this drop (Unity: marshaler mirror + CraftingMemory / chat persistence tests + **`MaxRolledSummaryTokens`** deterministic compaction EditMode coverage — see Unity changelog).

## [v1.6.1] — 2026-05-03

### Chat history summarization controls (host settings)

- **`ICoreAISettings`** — **`EnableConversationHistorySummarization`** (default true), **`ConversationHistoryRecentTokenBudgetOverride`**, **`ConversationRolledSummaryMaxTokens`** (default interface implementations preserve legacy stubs).
- **`AiOrchestrator`** — applies the above when building **`ConversationContextBuildArgs`**; **`UnlimitedHistoryTokenBudget`** when summarization is disabled.
- **`ConversationContextBuildArgs.MaxRolledSummaryTokens`** — forwarded from settings; **`ConversationRolledSummaryLimiter`** truncates rolled summary text by **`ITokenEstimator`**.
- **`DeterministicConversationContextManager`** / **`LlmAssistedConversationContextManager`** — apply the rolled-summary cap before **`SaveSummary`** and when returning a stored-only snapshot.

### Semver

- Lockstep **`1.6.1`** with **`com.neoxider.coreaiunity`** (Unity: **`CoreAISettingsAsset`** fields + custom inspector foldout **Chat history summarization**; docs **`COREAI_SETTINGS.md`**).

## [v1.6.0] — 2026-05-03

### Minor release — server-managed protocol, ambient LLM context, WebGL SSE bridge

- **`LlmCompletionRequest.IdempotencyKey`** — optional; when empty, **`MeaiLlmClient`** assigns one key per request **instance** so decorator retries (e.g. **`RefreshOnUnauthorizedDecorator`**) reuse the same HTTP **`Idempotency-Key`**.
- **`IOpenAiHttpSettings`** — **`IRequestHeaderProvider? HeaderProvider`** for optional extra headers (defaults **`null`** on adapters until needed).
- **`LlmRequestContext`** — portable `AsyncLocal` ambient frame carrying `AgentRoleId`/`TraceId`/`IdempotencyKey`. **`MeaiLlmClient`** populates it on every `CompleteAsync`/`CompleteStreamingAsync` from `LlmCompletionRequest`; HTTP transports read it during header assembly without having to plumb the request through MEAI's `IChatClient` seam. Use `LlmRequestContext.Begin(...)` / `Scope` for nested manual frames.
- **`LlmAuthContextRegistry`** — portable static for `ILlmAuthContextProvider`. **`MeaiOpenAiChatClient`** emits **`X-Tenant-Id`** / **`X-User-Id`** / **`X-Session-Id`** from the registered provider on server-managed requests.
- **`MeaiOpenAiChatClient.BuildTransportHeaders`** — emits `Idempotency-Key` / `X-Request-Id` / `X-Coreai-Role` from `LlmRequestContext.Current`, then auth headers from `LlmAuthContextRegistry`, then any extra headers from **`IOpenAiHttpSettings.HeaderProvider`**. Earlier sources win; **`HeaderProvider`** idempotency/request-id only fill missing slots.
- **Documentation** — **`LLM_ROUTING.md`** entitlement contracts; **`SERVER_MANAGED_PROTOCOL.md`** wire contract and CORS/SSE checklist.

### Semver

- Lockstep **`1.6.0`** with **`com.neoxider.coreaiunity`** (Unity: WebGL fetch SSE, **`RefreshOnUnauthorizedDecorator`** hardening, **`LlmClientRegistry`** wrapping, validators — see Unity changelog).

## [v1.5.29] — 2026-05-03

### Lockstep with coreaiunity 1.5.29

- **Semver:** lockstep **`1.5.29`** with **`com.neoxider.coreaiunity`** (no Core-only API change in this drop).

## [v1.5.28] — 2026-05-02

### Remove legacy `PlayerChat` built-in role id

- **`BuiltInAgentRoleIds.PlayerChat`** removed — use **`PlainChat`** (simple chat, no MemoryTool by default) or **`SmartChat`** (chat + MemoryTool + persisted history).
- **`BuiltInAgentSystemPromptTexts.PlayerChat`** removed; prompts live under **`PlainChat`** / **`SmartChat`** only.
- **`CompositeRoleStructuredResponsePolicy`** routes **`PlainChat`** and **`SmartChat`** through **`PlayerChatResponsePolicy`** (free-form text).
- **`InGameLlmChatService`** uses **`SmartChat`** for system prompt + **`AgentRoleId`**.
- Demo / defaults: **`CoreAiChatConfig`** default **`RoleId`** is **`SmartChat`** (Unity package).
- **Semver:** lockstep **`1.5.28`** with **`com.neoxider.coreaiunity`**.

## [v1.5.27] — 2026-05-02

### Built-in chat role split: PlainChat + SmartChat

- **`BuiltInAgentRoleIds`** — added **`PlainChat`** and **`SmartChat`** built-in role IDs.
- **`BuiltInDefaultAgentSystemPromptProvider`** + **`BuiltInAgentSystemPromptTexts`** — new default system prompts for both chat roles.
- **`AgentMemoryPolicy`** defaults:
  - **`PlainChat`**: persisted chat history ON, `MemoryTool` OFF.
  - **`SmartChat`**: persisted chat history ON, `MemoryTool` ON (`append`).
- **`LlmConversationalRolePolicy`** treats both **`PlainChat`** and **`SmartChat`** as conversational user-facing roles.
- **Semver:** lockstep **`1.5.27`** with **`com.neoxider.coreaiunity`**.

## [v1.5.26] — 2026-05-01

### HTTP SSE (`HttpClient`) — keep client until body is read

- **`HttpClientOpenAiTransport.OpenSseResponseStreamAsync`** no longer wraps **`HttpClient`** in **`using`** for the streaming path. Returning from the method disposed **`HttpClient`** immediately, which **canceled** the open SSE request (`The request was aborted: The request was canceled.`, `chunks=0`). **`OpenAiHttpSseOpenResult`** now owns **`HttpClient`** and disposes it **after** the content stream and **`HttpResponseMessage`**.
- **Semver:** lockstep **`1.5.26`** with **`com.neoxider.coreaiunity`**.

## [v1.5.25] — 2026-05-01

### WebGL-safe HTTP LLM — pluggable transport

- **`IOpenAiHttpTransport`**, **`OpenAiHttpPostRequest`**, **`OpenAiHttpPostResult`**, **`OpenAiHttpSseOpenResult`** — portable HTTP surface for **`/chat/completions`** without **`UnityEngine`** in the contract.
- **`HttpClientOpenAiTransport`** — default **`System.Net.Http`** implementation (SSE + non-stream); honors **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** in the Editor.
- **`MeaiOpenAiChatClient`** — requires **`IOpenAiHttpTransport`**; convenience ctor **`(settings, log)`** when **`!UNITY_WEBGL || UNITY_EDITOR`**. When **`SupportsSseStreaming`** is false, **`GetStreamingResponseAsync`** uses full JSON completion and **simulated** **`ChatResponseUpdate`** yields.
- **Semver:** lockstep **`1.5.25`** with **`com.neoxider.coreaiunity`** (Unity: **`UnityWebRequestOpenAiTransport`**, WebGL scene guard, docs, tests).

## [v1.5.24] — 2026-05-01

### OpenAI-compatible HTTP streaming (SSE) — local server compatibility

- **`MeaiOpenAiChatClient`** — SSE lines accept **`data:`** with or without a space after the colon (LM Studio / llama.cpp variants). **`ExtractDeltaUpdate`** falls back to **`choices[0].message`** and **`choices[0].text`** when **`delta.content`** is empty so streamed replies are not dropped.
- **Diagnostics** — log **HTTP status** and **Content-Type** immediately after response headers; **Warn** when the stream ends with **zero** parsed deltas (empty or non–OpenAI-shaped chunks).
- **Edit Mode** — extra **`MeaiOpenAiChatClientSseEditModeTests`** cases for `data:` variants and message-only chunks.
- **Semver:** lockstep **`1.5.24`** with **`com.neoxider.coreaiunity`** (Unity package: fullscreen chat option in **`CoreAiChatConfig`**).

## [v1.5.23] — 2026-05-01

### OpenAI-compatible MEAI HTTP — portable `HttpClient`

- **`MeaiOpenAiChatClient`** — moved to **`CoreAI.Infrastructure.Llm`** in portable **`CoreAI.Core`**: **`System.Net.Http.HttpClient`** for non-streaming and SSE (no **UnityEngine** / **UnityWebRequest**). **`await`** without **`ConfigureAwait(false)`** so synchronization context is preserved when the host sets one (e.g. Unity / WebGL main thread).
- **`IOpenAiHttpSettings`**, **`OpenAiHttpConstants`** — live next to the client in portable Core (Unity layer re-exports or implements the same settings surface).
- **`UNITY_EDITOR`:** **`MeaiOpenAiChatClientEditorTestHooks.HttpClientFactory`** — optional **`HttpClient`** factory for EditMode tests with **`HttpMessageHandler`** mocks (**must be cleared after tests**).
- **Semver:** lockstep **`1.5.23`** with **`com.neoxider.coreaiunity`** (Unity package adds **`MeaiOpenAiChatClientHttpEditModeTests`**).

## [v1.5.22] — 2026-05-01

### Lockstep packaging (`com.neoxider.coreaiunity`)

- **Semver:** lockstep **`1.5.22`** with **`com.neoxider.coreaiunity`**. No portable **CoreAI.Core** API or behavior change; **v1.5.22** composition fix (**`RegisterCorePortable` / `IAgentMemoryStore`**) ships in the Unity package only.

## [v1.5.21] — 2026-05-01

### Portable Core — JSON + API hygiene

- **`FileConversationSummaryStore`** — serializes with **Newtonsoft.Json** only; **`System.Text.Json`** removed from **`CoreAI.Core`** asmdef precompiled references.
- **`LlmStructuredPayloadSanitizer`** — JSON/markdown fence helpers moved out of **`ProgrammerLuaResponseParser`** (renamed from duplicate **`LlmResponseSanitizer`** type in **`CoreAI.Ai`**); **`CoreAI.Infrastructure.Llm.LlmResponseSanitizer`** remains for system-prompt echo stripping.
- **`Log.Instance`** backing field is **`volatile`** for safer multi-threaded reads after composition.
- **`AgentConfigExtensions.Ask`** — fire-and-forget uses **`Task`** (`RunAskFireAndForgetAsync`) instead of **`async void`**.
- **Semver:** lockstep **`1.5.21`** with **`com.neoxider.coreaiunity`** (Unity changelog lists WebGL/chat/composition changes).

## [v1.5.20] — 2026-05-01

### Lockstep packaging (WebGL host composition)

- **Semver:** lockstep **`1.5.20`** with **`com.neoxider.coreaiunity`**. No portable **CoreAI.Core** API or **`FileConversationSummaryStore`** implementation change; **`CoreAILifetimeScope`** WebGL registration lives in the Unity package (**`InMemoryConversationSummaryStore`** instead of file-backed summaries).

## [v1.5.19] — 2026-05-01

### Agent memory — LLM compaction contract (documentation)

- **`LlmAssistedConversationContextManager`** — XML `<remarks>` state that the orchestrator’s **main** system prompt (role instructions, universal prefix, memory, tool contract) is **never** included in the auxiliary compaction `LlmCompletionRequest`; only transcript-related text goes into **`UserPayload`**, **`ChatHistory`** stays **null**, and **`LlmContextCompactionOptions.SystemPrompt`** supplies the summarizer instructions.
- **`LlmContextCompactionOptions.SystemPrompt`** — property docs clarify it is **compaction-only**, not the primary role system string.
- **Semver:** lockstep **`1.5.19`** with **`com.neoxider.coreaiunity`** (Unity package ships Edit/Play tests and settings docs for the same contract).

## [v1.5.18] — 2026-04-30

### Offline / stub UX and chat failures (portable Core)

- **`LlmConversationalRolePolicy`** — classifies roles that should get **short user-facing** replies in **stub/offline** flows (e.g. **`PlainChat`**, **`SmartChat`**, **`AINpc`**, ids containing **`teacher` / mentor / tutor`**, names ending with **`chat`**, excluding **`Merchant`**).
- **`StubLlmClient`** — conversational roles return **`[stub] Offline — LLM unavailable (stub).`** instead of echoing **`UserPayload`** or emitting JSON **`ApplyWaveModifier`** for custom ids like **`Teacher`**.
- **`AiOrchestrator.RunTaskAsync`** — when **`AiTaskRequest.SourceTag`** is **`Chat`**, LLM failure / empty result / authority denied returns a **short printable message** (error text or default) instead of **`null`**, so **`CoreAiChatService`** can show text in the bubble. Non-chat callers still get **`null`** on failure.
- **Semver:** lockstep **`1.5.18`** with **`com.neoxider.coreaiunity`**.

## [v1.5.17] — 2026-04-30

### Lockstep packaging (Unity — `UnityMainThreadLlmAsyncMarshaler`)

- **Semver:** lockstep **`1.5.17`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change.
- **`UnityMainThreadLlmAsyncMarshaler`:** **`Application.isPlaying`** is **never** read from non–script-main threads (`ManagedThreadId` vs **`onBeforeRender`** mirror). Avoids **`get_isPlaying` / AggregateException** on MEAI **`Task`/thread-pool paths** (`UnityMainThreadLlmAsyncMarshalerEditModeTests.InvokeAsync_WhenNotPlaying_CompletesUnderMainThreadWait_FromThreadPool`).

## [v1.5.16] — 2026-04-30

### Lockstep packaging (Unity — `UnityMainThreadLlmAsyncMarshaler`)

- **Semver:** lockstep **`1.5.16`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change.
- **`UnityMainThreadLlmAsyncMarshaler`** (Unity package): **`Application.isPlaying`** is not reliably readable from MEAI continuation **threads** (**main thread / `UnityException`**). Use a **`Application.onBeforeRender`** **mirror**: **edit-time / unknown** ⇒ same **inline** path as **`!playing`** (**`ToolCallExtractionParityEditModeTests`**); **mirror says Editor Play Mode** ⇒ **`UniTask.SwitchToMainThread`** (keeps **`UnityMainThreadLlmAsyncMarshalerPlayModeTests`** valid in the Editor).

## [v1.5.15] — 2026-04-30

### LLM — `SmartToolCallingChatClient` native tool calls vs MEAI **10.x** `ChatMessage.Contents`

- **`FlattenAssistantContents`** — walks assistant turns using non-generic **`IList`** contents (MEAI **`ChatMessage.Contents`**), instead of LINQ **`SelectMany(... ?? Enumerable.Empty<AIContent>())`**, which could yield **no** **`FunctionCallContent`** items → false “text-only” exits and **premature consecutive-error stops** (`EditMode` **`SmartToolCallingChatClientEditModeTests`** regressions).
- **`ConcatenateAssistantTextContents`** — enumerates **`Contents`** via **`object`** for the same **IList** contract.
- **Semver:** lockstep **`1.5.15`** with **`com.neoxider.coreaiunity`**.

## [v1.5.14] — 2026-04-30

### Lockstep + API clarity (behavior in Unity package)

- **Semver:** lockstep **`1.5.14`** with **`com.neoxider.coreaiunity`** — no new portable **CoreAI.Core** symbols; **Edit Mode** `UnityMainThreadLlmAsyncMarshaler` bypass (**`UNITY_EDITOR`**, **`!Application.isPlaying`**) and regression tests live in the Unity package.
- **Docs / XML:** **`CoreAi`** static entrypoint comments — **non-streaming** chat is async via **`await`** only; discourage **`.Result` / `.Wait()`** on Unity’s managed **main thread**.

## [v1.5.13] — 2026-04-30

### Verification & docs (lockstep packaging)

- **Edit Mode:** **`LlmAsyncMarshalerEditModeTests`**, **`ToolExecutionPolicyEditModeTests.ExecuteSingle_UsesToolInvocationMarshaler_WhenProvided`**, **`CoreAISettingsToolMarshalerEditModeTests`**.
- **Docs (Unity monorepo):** **`ARCHITECTURE.md`**, **`COREAI_SETTINGS.md`**, **`DEVELOPER_GUIDE.md`**, **`Assets/CoreAiUnity/Tests/PlayMode/README.md`** — document **`ToolInvocationMarshaler`** + HTTP main-thread semantics.
- **Semver:** lockstep **`1.5.13`** with **`com.neoxider.coreaiunity`** (no portable API change).

## [v1.5.12] — 2026-04-30

### LLM / tools — Unity thread safety (portable hook)

- **`ILlmAsyncMarshaler`** + **`PassThroughLlmAsyncMarshaler`** — host can marshal MEAI **`AIFunction.InvokeAsync`** before Unity-only tool bodies run.
- **`ICoreAISettings.ToolInvocationMarshaler`** (default: pass-through) — **`ToolExecutionPolicy`** wraps each native tool call.
- **Semver:** lockstep **`1.5.12`** with **`com.neoxider.coreaiunity`**.

## [v1.5.11] — 2026-05-01

### Meta

- **Semver:** lockstep **`1.5.11`** with **`com.neoxider.coreaiunity`** — no portable **CoreAI.Core** API change in this tag; sibling Unity package reorganizes Play Mode tests into **`FastNoLlm`**, **`LlmVerification`**, and **`Scenarios`** assemblies (`Assets/CoreAiUnity/Tests/PlayMode/`).

## [v1.5.10] — 2026-05-01

### Version alignment

- **`com.neoxider.coreai` 1.5.10** is released in lockstep with **`com.neoxider.coreaiunity` 1.5.10** so UPM projects can pin the same version on both packages.
- **Portable Core** (`Assets/CoreAI`): no additional API or behavior changes in this tag beyond the version bump; Unity-side fixes and tooling live in the Unity package changelog.

#### Package **`1.5.10`**.

## [v1.5.9] — 2026-04-30

### Release alignment

- **`com.neoxider.coreai`** and **`com.neoxider.coreaiunity`** use the **same semver (1.5.9)** in this monorepo drop so UPM consumers can pin one version mentally.

### WebGL / IL2CPP — LLM + orchestration continuation hygiene

Single-threaded Unity player loop: avoid unnecessary **SyncContext-captured** continuations in the hot path.

- **`SmartToolCallingChatClient.GetResponseAsync`** — remove per-iteration **`Task.Yield()`**; add **`ConfigureAwait(false)`** on **`_innerClient.GetResponseAsync`** and **`policy.ExecuteBatchAsync`**.
- **`AiOrchestrator.RunTaskAsync`** — **`ConfigureAwait(false)`** on primary **`_llm.CompleteAsync`** (structured retry already had it).
- **`QueuedAiOrchestrator.RunOneAsync`** — **`ConfigureAwait(false)`** on **`_inner.RunTaskAsync`**.
- **`LuaTool.ExecuteAsync`**, **`ScriptedLlmClient` streaming** — **`ConfigureAwait(false)`** / **`Task.Delay(0)`** instead of bare **`Task.Yield()`**.
- **`GameConfigTool`**, **`InventoryTool`** — **`ConfigureAwait`** on inner **`await`**s for consistency.

#### Package **`1.5.9`**.

## [v1.5.6] — 2026-04-30

### LLM — MEAI assistant text helper

- **`SmartToolCallingChatClient.ConcatenateAssistantTextContents(ChatResponse)`** — joins all **`TextContent`** parts in **`response.Messages`**. Used by **`com.neoxider.coreaiunity`** **`MeaiLlmClient.CompleteAsync`** when **`ChatResponse.Text`** is empty but messages still hold text (provider / MEAI shape differences).

#### Package **`1.5.6`**.

## [v1.5.5] — 2026-05-01

### Architecture Refactoring — 3 Improvements

Continuation of the v1.5.4 audit. Addresses remaining deferred items: code deduplication, stale preprocessor guards, and orchestrator decomposition.

#### ARCH-6: Request Builder Extraction

- 🏗 **`AiOrchestrator.BuildCompletionRequest`** — extracted `LlmCompletionRequest` construction into a single private method. Eliminates 3x copy-paste between `RunTaskAsync` (main invocation), `RunTaskAsync` (structured retry), and `RunStreamingAsync`. Adding a new field to `LlmCompletionRequest` now requires updating exactly one method instead of three.

#### ARCH-7: Remove Stale `#if UNITY` from Portable Interfaces

- 🏗 **`ILlmClient.CompleteStreamingAsync`** — removed `#if UNITY_2021_3_OR_NEWER` guard around the Default Interface Method (DIM) fallback. The package minimum is `unity: 6000.0` which fully supports C# 8 DIM and `IAsyncEnumerable`. The streaming interface is now unconditionally available for non-Unity .NET test runners and pure .NET hosts.
- 🏗 **`IAiOrchestrationService.RunStreamingAsync`** — same removal of stale `#if UNITY_2021_3_OR_NEWER` guard.

#### ARCH-3 (partial): Post-Processing Extraction

- 🏗 **`AiOrchestrator.SanitizeAndPublish`** — extracted shared post-processing logic into a single private method: tool-call JSON sanitization (defense-in-depth strip), chat history persistence (`AppendChatMessage`), and game command publishing (`ApplyAiGameCommand`). Both `RunTaskAsync` and `RunStreamingAsync` now call this method instead of duplicating ~35 lines each.

#### Metrics

| Metric | Before | After |
|--------|--------|-------|
| `AiOrchestrator.cs` lines | 803 | 751 |
| `LlmCompletionRequest` construction sites | 3 | 1 |
| Post-processing duplication sites | 2 | 1 |
| `#if UNITY` in portable Core | 2 | 0 |

#### Package **`1.5.5`**.

## [v1.5.4] — 2026-05-01

### Comprehensive Audit — 8 Bug Fixes + 6 Architectural Improvements

Full code audit of CoreAI.Core covering orchestration, LLM pipeline, tool calling, memory, routing, streaming, and sandbox subsystems.

#### Bug Fixes

- 🐛 **BUG-1: `QueuedAiOrchestrator` deadlock risk** — merged `_scopeLock` into `_queueLock` (now a single `_lock`) to eliminate inconsistent lock ordering between `CancelTasks`/`Enqueue` (which nested `_scopeLock` inside `_queueLock`) and `ReleaseScopeToken` (which took `_scopeLock` independently).
- 🐛 **BUG-2: CTS Dispose-after-Cancel race** — `activeToCancel?.Cancel()` in `QueuedAiOrchestrator.Enqueue` and `CancelTasks` now guarded with `SafeCancel` (catches `ObjectDisposedException`) to handle the race with concurrent `ReleaseScopeToken.Dispose()`.
- 🐛 **BUG-3: `ClientLimitedLlmClientDecorator` counter drift** — `_requestCount` now decrements back when the limit is exceeded, so rejected requests don't permanently consume quota.
- 🐛 **BUG-4: `InGameLlmChatService` orphan responses** — split single `_lock` into `_historyLock` and `_rateLock`. History snapshot and append are atomic relative to `ClearHistory()`. Rate limiting no longer contends with history operations.
- 🐛 **BUG-5: `ToolExecutionPolicy` false positive tool failures** — replaced `string.Contains("\"Success\":false")` with `JObject.Parse`-based detection via `IsToolResultSuccess()`. Falls back to string heuristic only for non-JSON results.
- 🐛 **BUG-6: `LlmToolCallTextExtractor.StripCodeBlocks` offset safety** — added `Debug.Assert(result.Length == text.Length)` to catch offset desync if regex behavior changes.
- 🐛 **BUG-7: `MemoryTool.ExecuteAsync` unnecessary state machine** — removed `async` keyword from fully synchronous method. Returns `Task.FromResult` directly, eliminating overhead.
- 🐛 **BUG-8: `SmartToolCallingChatClient` streaming tool-calling bypass** — added runtime warning log when streaming is used with registered tools. Documents that tool-calling loop, duplicate detection, and consecutive error protection are bypassed in streaming mode.

#### Architectural Improvements

- 🏗 **ARCH-1: `CoreAISettings` thread safety** — added `_lock` around `Instance` getter/setter and `ResetOverrides()` to prevent torn reads from parallel test runners or async continuations.
- 🏗 **ARCH-2: `CoreAIAgent` thread safety** — static properties now backed by `volatile` fields to prevent torn reads when `Initialize` is called from Unity main thread and properties are accessed from ThreadPool continuations.
- 🏗 **ARCH-4: `AgentMemoryPolicy` thread safety** — added `_lock` to all dictionary/set operations (`_roleConfigs`, `_customTools`, `_runtimeContextProviders`, `_additionalSystemPrompts`, `_overrideUniversalPrefix`, `_streamingOverrides`). Prevents dictionary corruption from concurrent coroutine/async access.
- 🏗 **ARCH-5: `QueuedAiOrchestrator` `IDisposable`** — implements `IDisposable` to clean up `CancellationTokenSource` objects in `_scopeTokens` on shutdown. Safe for double-dispose.
- 🏗 **ARCH-9: `InMemoryAiOrchestrationMetrics` bounded storage** — added `MaxRoles = 256` cap with least-used eviction to prevent unbounded per-role dictionary growth from dynamically generated roleIds.
- 📝 **TODO.md** — updated version header, marked 4 completed items from this audit.

#### Package **`1.5.4`**.

## [v1.5.3] — 2026-04-30

### LLM-assisted context compaction (portable)

- **`LlmAssistedConversationContextManager`** — optional auxiliary `ILlmClient.CompleteAsync` to fold evicted history into a rolling summary (Kilocode-style); sync **`BuildSnapshot`** remains deterministic via **`DeterministicConversationContextManager`**.
- **`IAsyncConversationContextManager.BuildSnapshotAsync`** — **`AiOrchestrator`** now awaits this path when building chat history (including streaming), passing the orchestration trace id for compaction logs.
- **`ICoreAISettings.EnableLlmContextCompaction`** (default false) — **`RegisterCorePortable`** wires **`ConversationContextManagerFactories.Create(...)`** so Unity can enable LLM compaction from **`CoreAISettingsAsset`** without moving logic out of Core.
- **`SelectingConversationContextManager`** — when global compaction is enabled, each request selects LLM vs deterministic rollup using **`ConversationContextBuildArgs.UseLlmContextCompaction`** (from **`AgentMemoryPolicy.RoleMemoryConfig.UseLlmContextCompaction`**, gated by **`ICoreAISettings`**).
- **`RoleMemoryConfig.UseLlmContextCompaction`** — defaults true for **`AgentBuilder`** agents and built-in **`Creator`**, **`Analyzer`**, **`AINpc`**, **`PlainChat`**, **`SmartChat`**, **`Merchant`**, **`CoreMechanicAI`**; built-in **`Programmer`** defaults false (deterministic truncation/summary only). **`AgentBuilder.WithLlmContextCompaction(bool)`** and **`AgentMemoryPolicy.ConfigureLlmContextCompaction`** override per role.
- **`AgentBuilder`** — **`Build()`** logs non-fatal **`Log.Instance`** warnings for common misconfigurations (empty system prompt for custom roles, tool modes without tools, LLM compaction requested while the global gate is off, etc.). Use **`SuppressBuildWarnings`** to silence in tests, or **`ValidateOnBuild()`** / **`AgentBuilderIssue`** for assertions. **`BuiltInAgentRoleIds.IsBuiltIn`** helps skip “missing prompt” noise for stock roles. **`WithSystemPrompt`** XML docs now spell out the three prompt layers and point to **`DEVELOPER_GUIDE.md`**.

#### Package **`1.5.3`**.

## [v1.5.2] — 2026-04-30

### Context budget, compaction, and transcripts (portable core)

- **Budget & estimation** — portable `ContextBudget`, `ContextBudgetRequest`, `IContextBudgetPolicy` (`DefaultContextBudgetPolicy`), and `ITokenEstimator` (`HeuristicTokenEstimator`, ~chars/4). `AiOrchestrator` allocates a `HistoryTokenBudget` from role/context window minus completion reserve and estimated system/user/tool-contract size, fed into `IConversationContextManager.BuildSnapshot` via `ConversationContextBuildArgs`.
- **Persisted summaries** — portable `InMemoryConversationSummaryStore` (process lifetime, per role) is the default backing store for deterministic compaction; `FileConversationSummaryStore` (System.IO + System.Text.Json) for cross-launch persistence under a host-supplied directory. **`RegisterCorePortable`** registers the in-memory implementation unless the host passes **`suppressDefaultConversationSummaryStore: true`** after registering its own `IConversationSummaryStore` (Unity **`CoreAILifetimeScope`** registers `FileConversationSummaryStore` at `%persistentDataPath%/CoreAI/ConversationSummaries` this way). **`AiOrchestrator`** without DI uses **`InMemoryConversationSummaryStore`** instead of **`NullConversationSummaryStore`**. Use **`NullConversationSummaryStore`** only when tests need no accumulation.
- **Context overflow retry** — new `LlmErrorCode.ContextLengthExceeded`. HTTP mapping in `MeaiOpenAiChatClient` (413 + common overload phrases) and provider code mapping in `LlmProviderError`. `AiOrchestrator.RunTaskAsync` may **`CompleteAsync` once more** at `ContextBudgetRequest.ContextRetryLevel = 1` (halved history budget) via `IConversationCompactionCoordinator`.
- **`LlmCompletionRequest.ContextWindowTokens`** is now populated from orchestration.
- **`AgentTurnTrace`** adds `HistoryTokenBudget` / `ChatHistoryMessageCount`; portable `ConversationHistoryBudgetApplied` messaging DTO added.
- **Transcript hooks** — `ConversationEntry`, `IConversationTranscriptStore`, `NullConversationTranscriptStore`; `FileAgentMemoryStore` implements transcript persistence (`transcriptEntriesJson`) plus migration from flat chat.

#### Package **`1.5.2`**.

## [v1.5.1] — 2026-04-30

### WebGL Stability: Retry + Timeout + Error Propagation

Critical fixes for WebGL (Emscripten) production stability. Eliminates LLM pipeline hangs and silent failures in single-threaded environments.

#### Retry Multiplier Fix
- **`AiOrchestrator.RunTaskAsync`** — removed the `for (attempt...)` retry loop. The orchestrator now invokes `_llm.CompleteAsync` exactly **once**. Network-level retries (HTTP 429/5xx, exponential backoff) remain exclusively in `LoggingLlmClientDecorator`, eliminating the `M × N` retry multiplier bug where orchestrator retries × decorator retries caused up to `2 × 3 = 6` redundant requests on a single failure.

#### WebGL-Compatible Timeouts
- **`AiOrchestrator.RunTaskAsync` / `RunStreamingAsync`** — removed all `CancellationTokenSource.CancelAfter()` calls. These relied on `System.Threading.Timer`, which is non-functional in WebGL's Emscripten runtime (single-threaded, no native timer callbacks), causing indefinite hangs on timeout.
- **`LoggingLlmClientDecorator.CompleteAsync` / `CompleteStreamingAsync`** — same removal of `CancelAfter` and linked `CancellationTokenSource` wrapping. `cancellationToken` from the caller is passed through directly.
- **`CoreAiChatService`** — timeout responsibility now lives here, using **`CancelAfterSlim`** from `Cysharp.Threading.Tasks` (UniTask). This mechanism is based on Unity's `PlayerLoop` and is fully compatible with WebGL's execution model. Both `SendMessageAsync` and `SendMessageStreamingAsync` create a linked `CancellationTokenSource` with `CancelAfterSlim(TimeSpan)` when `LlmRequestTimeoutSeconds > 0`.

#### Error Propagation
- **`CoreAiChatService.SendMessageAsync`** — removed the `catch (Exception)` block that silently swallowed errors and returned `null`. Exceptions now propagate to `CoreAiChatPanel`, which already has a `catch (Exception ex)` block that displays the error message to the user (e.g., "Error: Connection refused") instead of showing a generic "No response." message.

#### Package version **`1.5.1`**.

## [v1.5.0] — 2026-04-30

### Architecture: Portable LLM pipeline decoupling

Migrated core LLM pipeline classes into `CoreAI.Core` (portable, `noEngineReferences: true`):

#### Moved from `CoreAI.Source` → `CoreAI.Core`
- **`LoggingLlmClientDecorator`** — `IGameLogger` → `ILog`, `RoutingLlmClient` type-check → `ILlmPreflightAnnotator`.
- **`ToolExecutionPolicy`** — `IGameLogger` → `ILog`, `GlobalMessagePipe` → `IToolCallEventPublisher`, `CoreAi.NotifyToolExecuted` → `IToolExecutionNotifier`.
- **`SmartToolCallingChatClient`** — `IGameLogger` → `ILog`, portable `LlmToolCallTextExtractor`.
- **`ClientLimitedLlmClientDecorator`** — already portable, moved for consistency.

#### New portable abstractions
- **`IToolCallEventPublisher`** + `NullToolCallEventPublisher` — lifecycle events without MessagePipe dependency.
- **`IToolExecutionNotifier`** + `NullToolExecutionNotifier` — subscriber notification without `CoreAi` static dependency.
- **`ILlmPreflightAnnotator`** — replaces hard type-check against `RoutingLlmClient`.

#### Documentation
- Updated `ARCHITECTURE.md`, `STREAMING_ARCHITECTURE.md`, `DEVELOPER_GUIDE.md` to reflect the adapter chain.

- Package version **`1.5.0`**.

## [v1.4.0] — 2026-04-30

### Resilience: TryRepairToolName + HTTP retry with Retry-After

Two production resilience features for robust LLM orchestration.

- ✨ **`ToolExecutionPolicy.TryRepairToolName`** — case-insensitive tool name repair before `AIFunction` resolution. Model writes `MEMORY` → system silently maps to `memory`. Empty tool list → passthrough (backwards compatible). Unknown tool → structured error with available names for self-correction.
- ✨ **`LoggingLlmClientDecorator` HTTP retry** — retries `RateLimited` (429) and `BackendUnavailable` (5xx) with `Retry-After` header or exponential backoff (2s→4s→8s→16s→30s cap). `maxHttpRetryAttempts` injected from `ICoreAISettings.MaxLlmRequestRetries`.
- ✨ **`MeaiOpenAiChatClient.BuildHttpException`** — parses `Retry-After-Ms` (ms precision, Azure/LiteLLM) with priority over `Retry-After` (seconds).
- ✨ **`ComputeBackoff(attempt)`** — exponential backoff helper: `2^(attempt+1)` capped at 30s.
- 🧪 **EditMode:** `TryRepairToolName` (5 tests), `ExecuteSingle` repair (2 tests), `ComputeBackoff` curve, text-extraction edge cases (4 tests).
- 🧪 **PlayMode:** `ToolNameRepairPlayModeTests` — 3 hybrid scripted+real-LLM tests for repair, self-correction, and mixed-case text prefix.
- 🔧 Package version **`1.4.0`**; align `com.neoxider.coreaiunity` to **`1.4.0`**.

## [v1.3.0] — 2026-04-30

### Portable text-extractor + tool-call diagnostic surface

- ✨ **`CoreAI.Ai.LlmToolCallTextExtractor`** — engine-agnostic helper that extracts (`TryExtract`) or strips (`StripForDisplay`) embedded tool-call JSON from assistant text. Same brace-counted, code-block-aware logic that the Unity-side streaming pipeline used internally, now portable so the orchestrator and any other consumer can apply identical rules at boundary points.
- ✨ **`LlmToolCallTrace`** struct in `CoreAI.Ai` — `(Name, Success, DurationMs, Source)` record for one tool call. Source is `native` / `text` / `duplicate` / `missing`.
- ✨ **`LlmCompletionResult.ExecutedToolCalls`** + **`LlmStreamChunk.ExecutedToolCalls`** — non-empty when the turn invoked tools. Stream propagates the list on the `IsDone` chunk; non-streaming on the result. Used by Unity-side `LoggingLlmClientDecorator` to render `tools=[name(ok,12ms)]` on every `LLM ◀` line.
- 🛡 **`AiOrchestrator`** runs `LlmToolCallTextExtractor.StripForDisplay` on the assistant text before persisting to chat history or publishing `ApplyAiGameCommand`, both for sync and streaming paths. Logs a warning if the strip changed anything (defense-in-depth — should be a no-op once Unity-side extraction succeeds).
- Package version **`1.3.0`**; align `com.neoxider.coreaiunity` to **`1.3.0`**.

## [v1.2.1] — 2026-04-29

### AllowedToolNames semantics + streaming facade

- **Breaking (narrow):** `AiTaskRequest.AllowedToolNames` / `LlmCompletionRequest`: **`null`** still means “do not filter role tools”; a **non-null empty array** now means “attach **no** tools” (chat-only allowlist), matching lesson-slot “no quiz/dnd this turn” use cases.
- `AiOrchestrator.FilterToolsForRequest` implements the above; docs updated (`LLM_ROUTING.md`, `LESSON_ORCHESTRATION.md`, `AiTaskRequest` XML).
- **`CoreAi.StreamChunksAsync(AiTaskRequest, CancellationToken)`** (Unity façade) forwards to `CoreAiChatService.SendMessageStreamingAsync` so hosts can pass `AllowedToolNames` / `ForcedToolMode` on the same code path as `RunTaskAsync`.
- **Tests:** `RunTaskAsync_EmptyAllowedToolNames_SendsNoTools`, `RunStreamingAsync_UsesSameToolFiltering_AsRunTaskAsync`.
- **EditMode:** `CoreServicesInstallerEditModeTests` — no invalid `GlobalMessagePipe.SetProvider(null)` in TearDown (MessagePipe does not support null).

Package version **`1.2.1`**; align `com.neoxider.coreaiunity` to **`1.2.2`**.

## [v1.2.0] — 2026-04-29

### RedoSchool lesson/practice orchestration APIs

- Added per-role runtime context providers on `AgentMemoryPolicy` so lesson slots can inject context without UI prompt-spaghetti.
- Added `AllowedToolNames` filtering and chat-only tool suppression on `AiTaskRequest`/`LlmCompletionRequest`.
- Added `ILlmToolCallHistory`, `ScriptedLlmClient`, `LlmToolResultEnvelope`, and `IAgentTurnTraceSink` for deterministic tests, structured tool results, and diagnostics.
- Package version **`1.2.0`**; aligned with `com.neoxider.coreaiunity` **`1.2.0`**.

## [v1.1.0] — 2026-04-29

### Portable LLM routing and policy contracts

- ✨ **Portable routing model** — added `LlmRouteProfile`, `LlmRouteRule`, `LlmRouteTable`, `ILlmRouteResolver`, and `LlmRouteResolver` under `CoreAI.Core`; `LlmExecutionMode.Stub` is now an alias for offline deterministic responses.
- ✨ **Portable registry and policy contracts** — added `ILlmClientRegistry`, `ILlmAuthContextProvider`, `ILlmEntitlementPolicy`, `LlmEntitlementDecision`, `ILlmUsageSink`, and `LlmUsageRecord`.
- ✨ **Provider error DTO** — added `LlmProviderError` for stable backend/provider codes such as `quota_exceeded`, `subscription_required`, `model_not_allowed`, and `rate_limited`.
- 📝 **Docs:** added `Assets/CoreAI/Docs/LLM_ROUTING.md`.
- 🔧 Package version **`1.1.0`**; aligned with `com.neoxider.coreaiunity` **`1.1.0`**.

## [v1.0.3] — 2026-04-29

### Unity chat UX alignment

- 🔧 Package version **`1.0.3`**; aligned with `com.neoxider.coreaiunity` **`1.0.3`**.

## [v1.0.2] — 2026-04-28

### Long context and tool-call identity

- ✨ **Conversation context management** — added portable `IConversationContextManager`, `ConversationContextSnapshot`, and `IConversationSummaryStore` contracts for long-running chat history compaction.
- ✨ **Deterministic summary fallback** — `DeterministicConversationContextManager` keeps recent messages in chat history and moves older turns into a `## Conversation Summary` system section without requiring an extra LLM call.
- ✨ **Tool-call identity** — added `LlmToolCallInfo` with `CallId`, `TraceId`, role, tool name, and sanitized arguments. Tool lifecycle events now expose `Info` while preserving `ToolName` and `ArgumentsJson` accessors.
- 🔧 Package version **`1.0.2`**; aligned with `com.neoxider.coreaiunity` **`1.0.2`**.

## [v1.0.1] — 2026-04-28

### Production runtime extension points

- ✨ **LLM usage telemetry** — added portable `LlmUsageReported` contract for token accounting and quota integrations.
- ✨ **Typed LLM errors** — `LlmErrorCode`, `LlmClientException`, and structured error fields on completion/stream chunks let UI and retry code handle quota, auth, rate-limit, timeout, and backend failures without parsing strings.
- ✨ **Runtime prompt context** — `IAiPromptContextProvider` lets projects append per-request context to prompts without mutating static role configuration.
- ✨ **Scoped memory contracts** — `AgentMemoryScope`, `IAgentMemoryScopeProvider`, and `ScopedAgentMemoryStoreDecorator` allow user/session/topic isolation while preserving role-only keys by default.
- ✨ **Tool lifecycle events** — added portable `LlmToolCallStarted`, `LlmToolCallCompleted`, and `LlmToolCallFailed` contracts for diagnostics and gameplay integrations.
- 🔧 Package version **`1.0.1`**; aligned with `com.neoxider.coreaiunity` **`1.0.1`**.

## [v1.0.0] — 2026-04-28

### Stable LLM mode contracts

- ✨ **`LlmExecutionMode`** — portable public mode contract for `Auto`, `LocalModel`, `ClientOwnedApi`, `ClientLimited`, `ServerManagedApi`, and `Offline`.
- ✨ **LLM routing events** — added portable `LlmBackendSelected`, `LlmRequestStarted`, and `LlmRequestCompleted` message contracts for Unity MessagePipe integration without adding MessagePipe dependencies to `CoreAI.Core`.
- 🔧 Package version **`1.0.0`**; aligned with `com.neoxider.coreaiunity` **`1.0.0`**.

## [v0.25.14] — 2026-04-27

### Release

- 🔧 Version **0.25.14**; release train aligned with `com.neoxider.coreaiunity` **0.25.14** (see Unity package changelog for `CoreAiChatPanel` UX fixes).

## [v0.25.13] — 2026-04-27

### MEAI tool argument binding

- 🐛 **`CompatibilityLlmTool` native argument binding** — the MEAI executor parameter is now named `ingredients`, matching the JSON schema. Valid model calls such as `{"ingredients":["Fire","Earth"]}` no longer fail before reaching the tool with a missing `ingredientsObj` argument.
- 🧪 **EditMode coverage:** added an `AIFunction.InvokeAsync` regression for `check_compatibility` using the public `ingredients` argument name.
- 📝 **`MEAI_TOOL_CALLING.md`** — documents that .NET `AIFunction` parameter names must match `ILlmTool.ParametersSchema` property names.
- 🔧 Version **`0.25.13`**; `com.neoxider.coreaiunity` aligned to **`0.25.13`**.

## [v0.25.12] — 2026-04-27

### Queue scheduling hardening

- 🐛 **`QueuedAiOrchestrator` latest-wins scopes** — `CancellationScope` now cancels older active and pending work as soon as a newer task with the same scope is enqueued, including streaming tasks.
- 🐛 **Queue fairness and cancellation** — equal priorities are FIFO, streaming and non-streaming tasks share one effective priority order, and pending tasks observe external cancellation before they start.
- 🧪 **EditMode coverage:** queue tests now cover priority ordering, FIFO tie-breaking, active and pending scope cancellation, pending external cancellation, `CancelTasks(scope)`, and shared sync/stream priority.
- 🔧 Version **`0.25.12`**; `com.neoxider.coreaiunity` aligned to **`0.25.12`**.

## [v0.25.11] — 2026-04-27

### Tool contract hardening

- ✨ **`AiOrchestrator` tool contract injection** — roles with registered tools now get a compact `## Tool Contract` block in the system prompt that lists available tools, schemas, and rules: call tools through the tool interface when requested, pass required arguments structurally, and do not claim registered tools are unavailable. This nudges local models toward real tool calls without weakening tests.
- 🐛 **Structured retry keeps tool context** — the structured-response retry path now preserves `Tools`, `ChatHistory`, `ForcedToolMode`, `RequiredToolName`, and `MaxOutputTokens` from the original request instead of retrying with text-only context.
- 🧪 **EditMode coverage:** orchestrator regression test verifies that tool-enabled roles receive the tool contract, required-tool hint, and parameter schema in `LlmCompletionRequest.SystemPrompt`.
- 🔧 Version **`0.25.11`**; `com.neoxider.coreaiunity` aligned to **`0.25.11`**.

## [v0.25.10] — 2026-04-27

### Agent memory policy defaults

- 🔧 **`AgentMemoryPolicy.RoleMemoryConfig` constructor** — default `persistChatHistory` is now **`false`**. Built-in agent roles that use only the two-argument form (`MemoryTool` + default action) therefore do **not** imply cross-session chat persistence when `WithChatHistory` is off (matches the role table in docs and `AgentBuilderChatHistoryEditModeTests`). **`PlainChat`** / **`SmartChat`** still set `persistChatHistory: true` explicitly in the policy constructor.
- 🔧 Version **`0.25.10`**; `com.neoxider.coreaiunity` aligned to **`0.25.10`**.

## [v0.25.9] — 2026-04-27

### Per-agent MaxOutputTokens (additive)

- ✨ **`AgentBuilder.WithMaxOutputTokens(int? tokens)`** — persistent per-agent response token cap for roles that should stay short (NPC chat) or intentionally verbose (planners) without setting the limit on every call.
- ✨ **`AgentMemoryPolicy.RoleMemoryConfig.MaxOutputTokens`** + **`SetMaxOutputTokens(roleId, int?)`** — policy-level storage for the per-role override. `null` / non-positive values clear the override.
- 🔧 **Priority via orchestrator:** `AiTaskRequest.MaxOutputTokens` (per-call) → `AgentBuilder.WithMaxOutputTokens` / policy (per-agent) → `ICoreAISettings.MaxTokens` (global fallback in the Unity LLM client) → provider default. Direct `LlmCompletionRequest.MaxOutputTokens` remains the highest priority when calling an `ILlmClient` directly.
- 🧪 **EditMode coverage:** orchestrator tests for per-agent forwarding, per-call override priority, and unset role fallback.
- 🔧 Version bumped to **`0.25.9`** so `com.neoxider.coreai` and `com.neoxider.coreaiunity` publish with matching package versions.

## [v0.25.4] — 2026-04-27

### ✨ Unified MaxTokens fallback (additive)

- ✨ **`ICoreAISettings.MaxTokens`** — new interface property with **default-implementation `=> 0`** (DIM, C# 8+); existing implementers (test stubs etc.) compile unchanged. Semantics: `0` / negative = "not set, fallback skipped"; positive = global LLM response token cap that the Unity layer back-fills uniformly into **both** backends (HTTP via `MeaiOpenAiChatClient` and local GGUF via `LlmUnityMeaiChatClient`).
- ✨ **`AiTaskRequest.MaxOutputTokens`** (`int?`) — per-call override, symmetric with `ForcedToolMode`/`RequiredToolName`. Forwarded by `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync`, and the structured-retry path into `LlmCompletionRequest.MaxOutputTokens`.
- 🔧 **Priority**: `LlmCompletionRequest.MaxOutputTokens` (per-request direct client call) → `AiTaskRequest.MaxOutputTokens` (per-call via orchestrator) → `ICoreAISettings.MaxTokens` (global fallback) → provider default. Previously `CoreAISettings.MaxTokens` was a read-only getter with no consumer — visible in the inspector but never applied.
- 🧪 **`MaxTokensFallbackEditModeTests`** — 4 tests covering: settings-default fallback, per-request override, settings=0 leaves provider default, streaming path applies the same fallback.
- 🔧 Version bumped to **`0.25.4`** (minor — additive public API). `coreaiunity 0.25.8 → coreai 0.25.4`.

## [v0.25.7] — 2026-04-27

### Release sync with `com.neoxider.coreaiunity 0.25.7`

- 🔧 **`com.neoxider.coreai`** stays at **`0.25.3`** — no public **`CoreAI.Core`** API changes. Unity-only release: Editor `CoreAISettings` bootstrap, PlayMode recall on 5xx, `TROUBLESHOOTING`. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.7).

## [v0.25.3] — 2026-04-26

### Release sync with `com.neoxider.coreaiunity 0.25.3`

- 🔧 Package version bumped to `0.25.3`. Manifest dependency `coreaiunity 0.25.3 → coreai 0.25.3`.
- ✅ No **`CoreAI.Core`** public API changes — Unity-layer release only. Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.3: chat hotkeys C/Esc, `Update` + poll when UITK has no focus, `FocusController` fix, `OnCollapsedStateChanged` hook, UXML/tooltips).

## [v0.25.2] — 2026-04-26

### Release sync with `com.neoxider.coreaiunity 0.25.2`

- 🔧 Package version bumped to `0.25.2`. Manifest dependency `coreaiunity 0.25.2 → coreai 0.25.2`.
- ✅ No `CoreAI.Core` public API changes — release sync only. See CoreAI Unity CHANGELOG 0.25.2 (UXML emoji cleanup + new `Docs/STREAMING_WEBGL_TODO.md` with a plan to fix WebGL SSE streaming in `OpenAiChatLlmClient.CompleteStreamingAsync`).

## [v0.25.1] — 2026-04-26

### Release sync — version alignment with `com.neoxider.coreaiunity 0.25.1`

- 🔧 Package version bumped to `0.25.1` to align with `com.neoxider.coreaiunity 0.25.1` (two WebGL/input fixes — see below).
- 🔧 Manifest dependency `com.neoxider.coreaiunity` now requires `com.neoxider.coreai 0.25.1` (was `0.25.0`).
- ✅ **No breaking changes to `CoreAI.Core` API** — pure release sync. Existing code using `LlmToolChoiceMode`, `AiTaskRequest.ForcedToolMode`, orchestrator, etc. continues to work.

### CoreAI Unity 0.25.1 release context (what actually changed in the Unity layer)

- 🐛 **WebGL TextField focus persistence** — `CoreAiChatPanel` keeps `WebGLInput.captureAllKeyboardInput = false` every frame (Update watchdog under `#if UNITY_WEBGL && !UNITY_EDITOR`). Fixes the “focus lasts one frame then drops” symptom in WebGL builds.
- 🐛 **Both Unity input systems** — `OrchestrationDashboard` no longer crashes with `Active Input Handling = Input System Package (New)`. `CoreAI.Source.asmdef` declares a soft dependency on `Unity.InputSystem` via `versionDefines` (`COREAI_HAS_INPUT_SYSTEM`).
- Details: `Assets/CoreAiUnity/CHANGELOG.md` (0.25.1 entry).

## [v0.25.0] — 2026-04-26

### Forced Tool Mode — deterministic tool selection per request

- ✨ **`LlmToolChoiceMode` enum** (`CoreAI.Ai`): `Auto` (default, model decides), `RequireAny` (provider must emit at least one tool call from the available set), `RequireSpecific` (provider must call a named tool — uses `RequiredToolName`), `None` (text-only response, tool calls forbidden).
- ✨ **`AiTaskRequest.ForcedToolMode` + `RequiredToolName`** — application-layer code (intent classifiers, retry pipelines) can now request guaranteed tool emission for a single call without changing the agent definition. Default is `Auto`, so existing behaviour is preserved.
- ✨ **`LlmCompletionRequest.ForcedToolMode` + `RequiredToolName`** — propagated 1-to-1 through `AiOrchestrator.RunTaskAsync`, `RunStreamingAsync` and the structured-retry path; LLM adapters in the Unity layer translate this to provider-native tool-choice (Microsoft.Extensions.AI `ChatOptions.ToolMode`).
- 🔧 **Streaming multi-round tool loop is unchanged** — `ForcedToolMode` only applies to the first iteration of a streaming session; after the first tool result is fed back, the model is reset to `Auto` so it can finalise with text instead of being pinned into an infinite tool-call loop.
- 🧪 **Tests:** new `ForcedToolModeEditModeTests` validate `LlmCompletionRequest`/`AiTaskRequest` plumbing and orchestrator forwarding.

### Release sync

- 🔧 Version bumped to `0.25.0` (minor — new public API). Dependency contract `com.neoxider.coreaiunity` `0.25.0+`.

## [v0.24.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.24.2` to match `com.neoxider.coreaiunity` `0.24.2`.
- 🔧 Synced Unity-layer hardening: HTTP error response body logging in `MeaiOpenAiChatClient` (both non-streaming and SSE paths), `ToolExecutionPolicy.maxConsecutiveErrors` clamped to `Math.Max(1, value)`.

## [v0.24.0] — 2026-04-26

### Streaming tool-calling hardening (release sync)

- 🔧 Version bumped to `0.24.0` to match `com.neoxider.coreaiunity` `0.24.0`.
- 🔧 Synced Unity-layer hardening: `ToolExecutionPolicy` (shared duplicate detection / error tracking), pattern-aware text JSON parser with multi-tool and code-block protection, native SSE `delta.tool_calls` parsing, stop/clear race condition fix.

## [v0.23.3] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.3` to match `com.neoxider.coreaiunity` `0.23.3`.
- 🔧 Synced Unity-layer reliability update: idempotent `CoreAIGameEntryPoint` startup guard prevents duplicate CoreAI initialization in scenes with accidental double composition.
- 🧪 Synced test coverage additions in Unity host: `CoreAIGameEntryPointEditModeTests` and additional streaming/tool-cycle guards in `MeaiLlmClientEditModeTests`.

## [v0.23.2] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.2` to match `com.neoxider.coreaiunity` `0.23.2` (includes non-stream HTTP cancellation fix used by Chat stop / Esc).

## [v0.23.1] — 2026-04-26

### Release sync

- 🔧 Version bumped to `0.23.1` to match `com.neoxider.coreaiunity` `0.23.1` and ensure downstream projects resolve the latest reliability fixes.

## [v0.23.0] — 2026-04-25

### Agent Control API UI
- ✨ **Chat UI updated.** `CoreAiChatPanel` adds a stop control that interrupts agent generation.
- ✨ **Default clear behavior.** The clear control in `CoreAiChatPanel` clears the UI and short-term chat history (`CoreAi.ClearContext(roleId, true, false)`). Full reset (including long-term memory) uses `ClearChat(clearChatHistory: true, clearLongTermMemory: true)`.
- 🔧 `com.neoxider.coreai` / `com.neoxider.coreaiunity` package versions aligned.
- 🔧 Release synced with the Unity layer for streaming + tool calling (`MeaiLlmClient` single-cycle: tool JSON suppressed in UI, tools run inside the same streaming pipeline).
- 🔧 For tool roles (`AgentMode.ToolsAndChat`, `AgentMode.ToolsOnly`) streaming is enabled per-role by default; `ChatOnly` still follows global/explicit overrides.
- 🔧 PlayMode reliability synced: stricter HTTP stream cancellation plus stabilized `Streaming_CancellationToken_StopsStream` and `MemoryTool_AppendsMemory`.

## [v0.22.0] — 2026-04-25

### Agent Control API — Full Lifecycle Management

- ✨ **Granular context clearing.** `CoreAi.ClearContext(string roleId, bool clearChatHistory, bool clearLongTermMemory)` — separate flags for chat history vs long-term memory (`MemoryTool`).
- ✨ **Tool invocation hook.** `CoreAi.OnToolExecuted` — global `ToolExecutedHandler(roleId, toolName, arguments, result)` for reactive integration (audio, VFX, analytics). Subscriber exceptions do not break the LLM pipeline.
- ✨ **`CoreAi.NotifyToolExecuted`** — internal hook invoked from `SmartToolCallingChatClient` after each successful tool call.
- ⚠️ **Breaking:** `SmartToolCallingChatClient` constructor now requires `roleId` (`string`) before `maxConsecutiveErrors`.

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.22.0** (Unity-layer release: `CoreAiChatPanel` stop via `Esc` and send-button stop state + tooltip). No portable-core API changes.

## [v0.21.9] — 2026-04-25

### Agent Control API
- ✨ **Stop + clear APIs.** `IAiOrchestrationService` adds `CancelTasks(string cancellationScope)`. `CoreAi` adds `CoreAi.StopAgent(string roleId)` and `CoreAi.ClearContext(string roleId)` for cancelling in-flight LLM work and clearing chat history.

## [v0.21.8] — 2026-04-25

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.8** (Unity layer: LLMUnity preprocessor guard refactor, automatic `COREAI_HAS_LLMUNITY` via `versionDefines`, fixes `CS0246` when LLMUnity is absent). No portable-core changes.

## [v0.21.7] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.7** (Unity layer: `CoreAiChatPanel` FAB collapse, auto-collapse on small screens, `PlayerPrefs` persistence). No portable-core changes.

## [v0.21.6] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.6** (Unity layer: removed forced `InputField` focus hacks in `CoreAiChatPanel`, WebGL caret flicker / lost keys fix). No portable-core changes.

## [v0.21.4] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.4** (Unity layer: WebGL input focus hardening in `CoreAiChatPanel`). No portable-core changes.

## [v0.21.3] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.3** (Unity layer: `CoreAiChatPanel` WebGL focus/typing stability). No portable-core changes.

## [v0.21.2] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.2** (Unity layer: `TextField` focus fix in `CoreAiChatPanel` after sending a message). No portable-core changes.

## [v0.21.1] — 2026-04-23

### Release sync

- 🔧 Version aligned with `com.neoxider.coreaiunity` **0.21.1** (Unity layer: chat UI/scrollbar, timeouts, tests).

## [v0.21.0] — 2026-04-23

### Orchestrator streaming

- ✨ **`IAiOrchestrationService.RunStreamingAsync(AiTaskRequest, CancellationToken)`** — new interface member (C# 8 DIM fallback calls `RunTaskAsync` and yields one final chunk with `IsDone=true`).
- ✨ **`AiOrchestrator.RunStreamingAsync`** — real streaming implementation. Same path as `RunTaskAsync` (prompt composer, authority, memory, tools, structured validation) but emits chunks as they arrive and publishes `ApplyAiGameCommand` only after the stream completes. Shared request build logic moved to private `BuildRequest`.
- ✨ **Structured validation** runs on the fully accumulated text after streaming ends. On failure, emits a terminal `LlmStreamChunk` with `Error = "structured validation failed: ..."` (no automatic stream retry — caller decides).
- 📚 **`RunStreamingAsync` contract** warns: any wrapper over `IAiOrchestrationService` (queue, logging, timeout, authority) must override this method explicitly or the DIM fallback silently disables streaming.

## [v0.20.3] — 2026-04-23

### Streaming pipeline — end-to-end visibility fix
- 🐛 **Critical: streaming was invisible in the UI.** `ILlmClient.CompleteStreamingAsync()` has a default interface implementation that falls back to `CompleteAsync()` and emits the whole answer as **one** terminal chunk after generation. Wrappers that did not override the method hid real streaming. Fixed in `CoreAiUnity` (see its CHANGELOG).
- 📝 `ILlmClient.CompleteStreamingAsync()` docs now warn that decorators (logging, routing, timeouts) **must** override streaming explicitly or the DIM fallback kills streaming.

## [v0.20.2] — 2026-04-23

### Streaming Configuration
- ✨ **`ICoreAISettings.EnableStreaming`** — global switch for LLM response streaming (SSE for HTTP API, callback queue for LLMUnity). Default `true`.
- ✨ **`AgentBuilder.WithStreaming(bool)`** — per-agent override of the global flag (e.g. chat NPC forced streaming vs strict JSON parser / tool-only non-streaming).
- ✨ **`AgentMemoryPolicy.SetStreamingEnabled(roleId, bool?)`** and **`IsStreamingEnabled(roleId, fallback)`** — per-role override storage and effective flag resolution.
- ✨ **`AgentConfig.EnableStreaming`** (`bool?`) — nullable override propagated to policy via `ApplyToPolicy()`.
- 🔧 **Precedence** (highest to lowest): UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).

## [v0.20.1] — 2026-04-23

### Streaming Robustness

- ✨ **`ThinkBlockStreamFilter`** (`CoreAI.Ai`) — reusable stateful filter that strips `<think>...</think>` from the LLM stream. Unlike regex, handles tags split across chunks (common with DeepSeek / Qwen).
  - `ProcessChunk(string)` — process a chunk, return only visible text.
  - `Flush()` — end the stream (return trailing text if the model cut off mid-response).
  - `Reset()` — reuse the same instance.

### Streaming API
- 📝 **Stream contract:** `ILlmClient.CompleteStreamingAsync()` always ends with a final chunk `IsDone=true` (even on empty model output) so callers can close the UI reliably.
- 📚 `ILlmClient.CompleteStreamingAsync()` docs note implementations should run on Unity’s main thread (`UnityWebRequest`).

## [v0.20.0] — 2026-04-23

### Streaming API
- ✨ **`LlmStreamChunk`** — stream chunk type with `Text`, `IsDone`, `Error`, and usage stats.
- ✨ **`ILlmClient.CompleteStreamingAsync()`** — new interface member returning `IAsyncEnumerable<LlmStreamChunk>`. Default implementation falls back to `CompleteAsync()` with a single chunk.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — real streaming via `IChatClient.GetStreamingResponseAsync()` with `<think>` filtering.

### 3-Layer Prompt Architecture
- 🔧 **Bug fix:** `AgentBuilder.WithSystemPrompt()` did not register prompts in `IAgentSystemPromptProvider`, so AgentBuilder prompts were ignored and AiOrchestrator always used ManifestProvider.
- ✨ **Three-layer system prompt** in `AiPromptComposer.GetSystemPrompt()`:
  - **Layer 1:** `CoreAISettings.universalSystemPromptPrefix` — shared rules for all agents
  - **Layer 2:** Base prompt from ManifestProvider / ResourcesProvider (`.txt` assets)
  - **Layer 3:** Extra prompt from `AgentBuilder.WithSystemPrompt()` (via `AgentMemoryPolicy`)
- 🔧 **`AgentBuilder.Build()`** — no longer appends `universalPrefix` (handled in `AiPromptComposer`)
- 🔧 **`AgentConfig.ApplyToPolicy()`** — registers system prompt via `policy.SetAdditionalSystemPrompt()`
- ✨ **`AgentMemoryPolicy.SetAdditionalSystemPrompt()` / `TryGetAdditionalSystemPrompt()`** — stores AgentBuilder extra prompts
- ✨ **`AgentBuilder.WithOverrideUniversalPrefix()`** — disable `universalPrefix` per role (parsers, validators, fully custom prompts)
- ✨ **`AgentMemoryPolicy.SetOverrideUniversalPrefix()` / `IsUniversalPrefixOverridden()`** — per-role universal prefix control

### Breaking Changes
- **`AiPromptComposer` constructor** — optional `AgentMemoryPolicy` and `ICoreAISettings` parameters (backward compatible with `= null`)
- **`universalPrefix`** now applies to all roles by default (opt out with `.WithOverrideUniversalPrefix()`)

## [v0.19.3] — 2026-04-22

### Prompt Optimization
- 🔧 **Removed duplicate tool-calling rules** from all seven built-in agent prompts (C# constants + `.txt` resources). Saves ~100–150 tokens per request — rules already live in `UniversalSystemPromptPrefix`.
- 📝 **Prompt wording:** added response length limits for AiNpc (1–3 sentences) and built-in chat roles (1–5 sentences).
- 🔧 **Native tool calling:** dropped legacy manual JSON tool-formatting guidance from `Agent.cs` and `AllToolCallsPlayModeTests.cs`; samples and tests use native `MEAI` function calling.

### Editor UX
- ✨ **`CoreAI/Create Scene Setup`** — Unity menu action for quick scene wiring:
  - Adds `CoreAILifetimeScope` with assigned assets
  - Generates default assets (Settings, LogSettings, PromptsManifest, etc.)
  - Creates `LLM` + `LLMAgent` when using LLMUnity backend (or Auto+LlmUnityFirst)
  - Duplicate guard and Undo (Ctrl+Z)

### Stability
- 🐛 **HTTP timeout logging:** `MeaiOpenAiChatClient` — timeout/network issues downgraded from `LogError` to `LogWarning` so PlayMode tests stay green in Unity Test Runner.
- 🐛 **PlayMode tests:** fixed `AllToolCalls_MemoryTool_WriteAppendClear` failure from conflicting text JSON prompts vs native tool calls.
- 🛡️ **UI safety:** `try/catch` in `async void OnSendClicked` (`InGameChatPanel.cs`) to avoid silent UI crashes on network errors.

### Documentation
- 📚 **READMEs (EN + RU)** — full dependency install guide:
  - NuGet DLLs (Microsoft.Extensions.AI, etc.) with version table
  - Git URL packages and transitive deps (VContainer, MoonSharp, LLMUnity, UniTask, MessagePipe)
  - New steps: Create Scene Setup, LLM backend setup
- 🔗 **Link fix:** repaired broken relative links in `README_RU.md` for GitHub repo home navigation.

## [v0.19.2] — 2026-04-14

### Changed
- **AgentMemory:** smarter `ChatHistory` trimming before the LLM client. History is capped by message count (`MaxChatHistoryMessages`, default 30) and approximate token budget (`ContextTokens / 2`). Reduces HTTP context blow-ups and huge bills while older turns stay in JSON.
- **AgentBuilder:** optional `maxChatHistoryMessages` on `.WithChatHistory()`.

## [v0.19.1] — 2026-04-14

### Fixes & Stability
- 🐛 **Duplicate tool-call guard:** documented how `MeaiLlmClient` resets failed-call counters per session; `executedSignatures` scoping isolates each request.
- 🔧 **`Agent.cs` test harness:**
  - Test phrases exposed in Inspector `[TextArea]` for live scenario tweaks and to avoid identical-prompt loops.
  - Added `ClearMemory()` to reset history between button presses so the model does not anchor on prior mistakes.
- 📝 **Docs:** clarified `SceneLlmAgentProvider` with `DontDestroyOnLoad` — needs an `LLMAgent` component or registered agent name.

## [v0.19.0] — 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — ingredient compatibility checks for CoreMechanicAI
  - Rules for arbitrary element counts (pairs, triples, quads, …)
  - `CompatibilityRule.Pair()` and `CompatibilityRule.Group()` factory helpers
  - Element groups (IronOre → Metal, WaterFlask → Water) with automatic resolution
  - Custom validators (`ICompatibilityValidator`) for game logic
  - Weighted scoring: rules covering more elements win
- ✨ **`CompatibilityLlmTool`** — `ILlmTool` wrapper for function calling (LLM can validate before crafting)
- ✨ **`JsonSchemaValidator`** — LLM JSON validation without external deps
  - Required fields and types (string, number, integer, boolean, array, object)
  - Numeric ranges (min/max) and enums
  - Strips markdown fences (`` `json...` ``)
  - `ToPromptDescription()` — schema blurb for system prompts
- 🧪 **45+ EditMode tests** for CompatibilityChecker, JsonSchemaValidator, and CompatibilityLlmTool

## [v0.18.0] — 2026-04-10

### Architecture — DI Migration

- 🔧 **`CoreAISettings` → static proxy** — no longer stores independent field copies; reads delegate to DI-registered `ICoreAISettings Instance`.
  - Direct field writes kept for backward compatibility (override wins over Instance).
  - Added `CoreAISettings.ResetOverrides()` for tests.
- 🔧 **`LuaAiEnvelopeProcessor`** — takes `ICoreAISettings` via constructor (optional). No longer reads `CoreAISettings.MaxLuaRepairRetries` at init.
- ❌ **Removed** `SyncToStaticSettings()` — replaced with `CoreAISettings.Instance = settings`.

## [v0.16.0] — 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — runtime scene inspection for the LLM:
  - `find_objects` — find GameObjects by name/tag
  - `get_hierarchy` — list children
  - `get_transform` / `set_transform` — position, rotation, scale
- ✨ **`CameraLlmTool`** — vision tool: PlayMode `capture_camera` screenshots as Base64 JPEG `dataUri` (multimodal models like LLaVA / gpt-4o).
- 🛠 **Threading** — both tools marshal Unity API work via `UniTask.SwitchToMainThread()` to avoid MEAI background-thread crashes.
- 🛠 **`CoreAiPrefabRegistryAsset` automation** — `OnValidate` fills `Key` from AssetDatabase GUID and syncs `Name` when prefabs are assigned in the Inspector.

## [v0.15.0] — 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON extraction** — rewrote tool-call parsing in `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Fragile regex removed; brace scanning (`IndexOf('{')`) tolerates missing closing fences (\`\`\`) and braces inside string args. PlayMode `MemoryTool_AppendsMemory` passes.
- ⚙️ **Reasoning-mode stripping** — preprocess strips `<think>...</think>` before JSON parse so “thinking aloud” (DeepSeek) does not break tool JSON.

### Editor UX
- ✨ **Auto plugin load** — `[InitializeOnLoadMethod]` in `CoreAIBuildMenu` generates required `ScriptableObject` assets (`CoreAiSettingsAsset`, routing manifests, permissions) under `Settings/` and `Resources/` on project load / import.
- ✨ **Quick Settings** — **CoreAI → Settings** menu opens the global `CoreAISettings.asset` singleton.

## [v0.14.0] — 2026-04-09
### Agent Memory & Persistence
- ✨ **Persistent chat history** — full dialog context survives between play sessions.
  - `WithChatHistory(persistToDisk: true)` on `AgentBuilder` (or `RoleMemoryConfig`) enables disk persistence.
  - Files live under `Application.persistentDataPath/CoreAI/AgentMemory/`.
  - Orchestrator reloads JSON on restart; ephemeral fallback when disk persistence is off.
- 🧪 PlayMode `ChatHistoryPlayModeTests` cover context restore after scene/engine “restart”.

## [v0.13.0] — 2026-04-09
### Action / Event System
- ✨ **`DelegateLlmTool`** — generic `ILlmTool` that exposes any C# delegate (Action/Func) to the LLM via MEAI with JSON schema inferred from the signature.
- ✨ **`CoreAiEvents`** — tiny built-in static pub/sub bus linking agents to game code without extra deps.
- ✨ **`AgentBuilder` extensions:**
  - `WithAction(name, description, delegate)` — wire a method straight to the agent.
  - `WithEventTool(name, description, hasStringPayload)` — emit triggers on `CoreAiEvents`.
- 🧪 EditorMode `CoreAiEventsEditModeTests`.

## [v0.12.0] — 2026-04-08

### Architecture
- **Single `ILog` logger** — collapsed the dual-logger setup
  - `ILog` adds `Debug/Info/Warn/Error(msg, tag)`
  - `LogTag` subsystem strings (`Core`, `Llm`, `Lua`, `Memory`, `Config`, `World`, `Metrics`, `Composition`, `MessagePipe`)
  - `Log.Instance` static + VContainer DI both supported
  - `NullLog` default no-op for tests / pre-DI

- **`MemoryToolAction` unification** — one enum definition
  - Moved to `MemoryToolAction.cs`
  - Removed duplicates from `AgentBuilder.cs` and `AgentMemoryPolicy.cs`
  - `AgentBuilder.WithMemory(defaultAction)` now applies correctly

### Changed
- **Core tool classes** use `ILog` tags:
  - `MemoryTool` → `LogTag.Memory`
  - `LuaTool` → `LogTag.Lua`
  - `GameConfigTool` → `LogTag.Config`
  - `InventoryTool` → `LogTag.Llm`
- `CoreAIGameEntryPoint` — `IGameLogger` → `ILog`
- `CoreServicesInstaller` — registers `ILog` (`UnityLog`) and sets `Log.Instance`
- `GameLoggerUnscopedFallback` — bridges `Log.Instance` before DI boots
- Removed manual `Log.Instance` wiring from `CoreAILifetimeScope` (now in `CoreServicesInstaller`)

### Unity implementation
- `UnityLog` — `ILog` impl mapping `LogTag` to `GameLogFeature` flags
- `IGameLogger` kept for Unity layer (`FilteringGameLogger`, `GameLogSettingsAsset`)
- Tag filtering still driven by `GameLogSettingsAsset` in the Inspector

## [v0.11.0] — 2026-04-07

### Added
- **Universal system prompt prefix** — shared preamble for every agent
  - `CoreAISettings.UniversalSystemPromptPrefix` static property for code-driven setup
  - Prepended to **every** system prompt (built-in and custom)
  - Centralizes cross-model rules without duplication
  - `BuiltInAgentSystemPromptTexts.WithUniversalPrefix()` helper
  - `BuiltInDefaultAgentSystemPromptProvider` applies it automatically
  - `AgentBuilder.Build()` applies it to custom agents
- **Global sampling temperature** — `CoreAISettings.Temperature` (default **0.1**) for all agents and both backends (LLMUnity + HTTP API)
- **`AgentBuilder.WithTemperature(float)`** — per-agent override; `AgentConfig.Temperature` stores it (defaults to `CoreAISettings.Temperature`)
- **`MaxToolCallIterations`** — moved from hardcode to `CoreAISettings.MaxToolCallIterations` (default 2); caps tool rounds per request; `MeaiLlmClient` reads the setting

## [v0.10.0] — 2026-04-06

### Added
- **WorldCommand as MEAI tool call** — LLM-driven world control via function calling
  - `IWorldCommandExecutor` — engine-agnostic contract in **CoreAI**
  - `WorldTool.cs` — MEAI `AIFunction` (CoreAiUnity)
  - `WorldLlmTool.cs` — `ILlmTool` wrapper (CoreAiUnity)
  - Actions: `spawn`, `move`, `destroy`, `load_scene`, `reload_scene`, `bind_by_name`, `set_active`, `play_animation`, `show_text`, `apply_force`, `spawn_particles`, `list_objects`
- **`list_objects`** — enumerate scene hierarchy objects (name, position, active, tag, layer, child count) with optional name filter
- **`play_animation`** — play clips on Animator or legacy Animation via `Animator.runtimeAnimatorController.animationClips`
- **`list_animations`** — list available clips from the AnimatorController; resolve targets by `instanceId` or `targetName`
- **`targetName` on commands** — name-based targeting alongside `instanceId` for move/destroy/set_active/play_animation/apply_force/spawn_particles (`_instances` first, then `GameObject.Find`)
- `WorldToolEditModeTests.cs` / `WorldCommandPlayModeTests.cs` — coverage for world tools
- **Inspector debug logging on `CoreAISettingsAsset`**
  - `LogLlmInput` — prompts (system/user) + tools
  - `LogLlmOutput` — model replies + tool results
  - `EnableHttpDebugLogging` — raw HTTP JSON
- `tool_call_id` on tool messages for LM Studio
- Idempotent `MemoryTool.append` to stop duplicate appends when the model loops

### Changed
- `MeaiOpenAiChatClient` — tool results read from `msg.Contents`
- `MemoryTool.ExecuteAsync` — returns JSON strings for correct serialization
- `TestAgentSetup` — adds `WorldExecutor` for PlayMode
- Dropped `LogAssert.Expect` for connection errors in PlayMode (only when host is down)

### Fixed
- Tool results were empty (`[tool]` content) — fixed `Contents` extraction
- LM Studio 400 — required `tool_call_id` on tool messages
- Memory append triple-writes — idempotency guard
- Write test flakiness — clarified hint text

---

## [v0.9.0] — 2026-04-06

### Added
- `MeaiLlmClient` — single MEAI client for every backend
  - `MeaiLlmClient.CreateHttp(settings, logger, memoryStore)` — HTTP API
  - `MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)` — local GGUF
- `MeaiOpenAiChatClient` — MEAI `IChatClient` for HTTP
- `LlmUnityMeaiChatClient` — MEAI `IChatClient` for LLMUnity (split out)
- `OfflineLlmClient` — deterministic canned replies per role (replaces stub)
- `CoreAISettings.ContextWindowTokens` — default context size (8192)
- `AgentBuilder.WithChatHistory(int?)` — inherit or override history window
- `AgentConfig.ContextWindowTokens` / `WithChatHistory`
- `CoreAISettingsAsset.AutoPriority` — `LlmUnityFirst` vs `HttpFirst`
- Inspector **🔗 Test Connection** button
- `Docs/MEAI_TOOL_CALLING.md` — architecture notes

### Changed
- `MeaiLlmUnityClient` / `OpenAiChatLlmClient` — thin factories delegating to `MeaiLlmClient`
- PlayMode tests build `CoreAISettingsAsset` through the factory
- `LlmBackendType.Stub` → `LlmBackendType.Offline`
- `AGENT_BUILDER.md` — client creation examples
- Removed duplicate docs: `MEAI_FUNCTION_CALLING.md`, `README_MEAI.md`

### Architecture
- Shared MEAI pipeline for HTTP + LLMUnity
- `FunctionInvokingChatClient` handles automatic tool calling
- No manual text parsing for tool calls

---

## [v0.8.0] — 2026-04-06

### Added
- `CoreAISettingsAsset` — single ScriptableObject settings singleton
- `IOpenAiHttpSettings` — adapter interface for HTTP settings
- `OpenAiChatLlmClient(CoreAISettingsAsset)` constructor
- `CoreAISettingsAssetEditor` — custom Inspector
- Default `CoreAISettings.asset` in Resources
- LLMUnity options: `DontDestroyOnLoad`, `StartupTimeout`, `KeepAlive`
- Auto priority: LlmUnityFirst / HttpFirst

---

## [v0.7.0] — 2026-04-06

### Added
- Unified MEAI tool-calling format
- `LuaTool.cs` + `LuaLlmTool.cs`
- `InventoryTool.cs` + `InventoryLlmTool.cs`
- `CoreAISettings.cs` (static)
- `AgentBuilder` — fluent builder for custom agents
- `WithChatHistory()` — dialog history retention
- `WithMemory()` — persistent memory
- `AgentMode` — ToolsOnly, ToolsAndChat, ChatOnly
- Merchant NPC sample with tools

### Removed
- `AgentMemoryDirectiveParser` — superseded by the MEAI pipeline
