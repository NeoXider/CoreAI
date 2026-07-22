# Roblox Reference 01 — Scripts, Containers, Scheduler, Signals, Lifecycle, Errors

**Status: NORMATIVE.** CoreAI's Lua mod system is architected to follow the rules in this
document exactly. Any deviation from a rule below must be a conscious, recorded decision —
not an accident of implementation.

- Researched against the **current official Roblox documentation** (create.roblox.com/docs and
  its open-source repository github.com/Roblox/creator-docs) on **2026-07-22**.
- Every rule is a testable statement. Quotes are verbatim from the docs.
- Statements that the official docs do **not** make, but that are widely relied upon by the
  community, are explicitly marked **UNCERTAIN** — do not treat those as normative without a
  follow-up decision.
- Rule numbering: `R<section>.<n>`.

---

## 1. Script classes and execution contexts

### Rules

- **R1.1** There are three user-facing script classes: `Script` (server code), `LocalScript`
  (client code), and `ModuleScript` (a reusable chunk that returns exactly one value via
  `require`). `LocalScript` inherits from `Script`, which inherits from `BaseScript`
  (which holds `Enabled` and `RunContext`), which inherits from `LuaSourceContainer` → `Instance`.
- **R1.2** A `Script` is "an object that contains and runs Luau code on the server." A
  `LocalScript` is "an object that contains and runs Luau code on the client (player's device)
  instead of the server." Only client contexts can access client-only objects (e.g. the
  `Camera`, `Players.LocalPlayer`).
- **R1.3** A script begins running **in a new thread** when BOTH conditions hold:
  (a) its `Enabled` property is `true`, and (b) it is in a location where its class/RunContext
  permits execution (for a Legacy `Script`: it "is a descendant of the Workspace or
  ServerScriptService").
- **R1.4** `BaseScript.RunContext` (enum `Enum.RunContext`) has four values with these exact
  documented meanings:
  - `Legacy` (0): "Runs in legacy script containers dependent on the type of script uses such
    as LocalScript or Script." (i.e. location decides; see R1.5, R1.6)
  - `Server` (1): "Runs on the server."
  - `Client` (2): "Runs on the client."
  - `Plugin` (3): "Runs as a descendant of Plugin instances."
- **R1.5** With `RunContext = Legacy` (the default for a new `Script`), the script "a) is a
  server-side script and b) only runs if it is in a server container, such as `Workspace` or
  `ServerScriptService`."
- **R1.6** With `RunContext = Server`, the script "can now also run in `ReplicatedStorage`, but
  that's not recommended. The contents of that location are replicated to clients, so it's a
  poor location for server-side scripts." Official recommendation: "Put server scripts into
  `ServerScriptService` with a `RunContext` of `Server` alongside server-only ModuleScripts."
- **R1.7** With `RunContext = Client`, the script "can run in `ReplicatedStorage`. It can also
  run in `StarterCharacterScripts` and `StarterPlayerScripts`." Official recommendation: "Put
  client scripts into `ReplicatedStorage` with a `RunContext` of `Client` alongside client-only
  ModuleScripts." For startup work: "Put the minimal number of client scripts (such as a
  loading script) into `ReplicatedFirst` with a `RunContext` of `Client`."
- **R1.8** "RunContext cannot be used from a LocalScript" — the property is meaningful only on
  `Script` objects.
- **R1.9** Changing `RunContext` at runtime restarts the script: "If RunContext is assigned
  while the script is running any threads created by the script will be terminated and the
  script will start running under the new context if possible."
- **R1.10** `Enabled` semantics (this supersedes the deprecated, inverse `Disabled` property):
  - Setting `Enabled = false` while the script is running: "the current running thread of the
    script will be terminated."
  - Setting it back to `true`: "the script will run again" (fresh run from the top — `Enabled`
    can be toggled false→true to restart a script).
  - A script cannot re-enable itself: disabling from within terminates the thread before any
    following line runs.
- **R1.11** Termination conditions: a running script continues "until the conditions for
  running cease, it terminates, raises an uncaught error, or is destroyed." Additionally:
  "the thread will be stopped if the script or one of its ancestors is destroyed."
- **R1.12** Reparenting nuance: "A script will continue to run even if the Parent property is
  set to `nil` and the Script is not destroyed." Losing a *running-eligible ancestor* by
  reparenting into a non-running container stops eligibility per R1.3, but a mere detach to
  `nil` does not by itself kill an already-running thread — only `Destroy()` (or `Enabled=false`)
  does.
- **R1.13** Each script instance that starts runs its source as **one new thread (coroutine)**;
  all threads it spawns are children of that logical script for termination purposes (see
  R1.9–R1.11: terminating the script terminates "any threads created by the script").

### Implementation notes for CoreAI

- Model a mod script as (source, `Enabled`, `RunContext`, location) → derived "should run"
  predicate; re-evaluate the predicate on any of the four inputs changing.
- Track every thread spawned by a script so `Enabled=false` / `Destroy()` / RunContext change
  can cancel the whole group (R1.9–R1.11).
- Keep `Legacy` behavior as the default for parity; expose Server/Client run contexts as the
  recommended authoring path (R1.6, R1.7).

Sources:
- https://create.roblox.com/docs/reference/engine/classes/Script
- https://create.roblox.com/docs/reference/engine/classes/LocalScript
- https://create.roblox.com/docs/reference/engine/classes/BaseScript
- https://create.roblox.com/docs/reference/engine/enums/RunContext
- https://create.roblox.com/docs/scripting/locations

---

## 2. Container semantics and startup

### Rules

- **R2.1** Container → replication → execution matrix (from the data-model and locations docs):

  | Container | Replicated to clients? | What runs there |
  |---|---|---|
  | `Workspace` | Yes (the 3D world) | Legacy `Script` (server); RunContext scripts |
  | `ReplicatedFirst` | Yes — **first, before anything else** | `LocalScript`; RunContext-Client `Script` |
  | `ReplicatedStorage` | Yes | Nothing with Legacy context; RunContext Server/Client scripts; `ModuleScript` on require |
  | `ServerScriptService` | **No** ("never replicated to clients") | Legacy `Script`; RunContext-Server `Script`; server `ModuleScript`s |
  | `ServerStorage` | **No** | Nothing — "Scripts don't run when they are parented to this container"; server code may `require` modules stored there |
  | `StarterPlayerScripts` | Template — copied to each `Player.PlayerScripts` | `LocalScript`; RunContext-Client `Script` |
  | `StarterCharacterScripts` | Template — copied into each spawned `Player.Character` | `LocalScript`; RunContext-Client `Script` |
  | `StarterGui` | Template — copied to each `Player.PlayerGui` | `LocalScript` (inside GUI objects) |
  | `StarterPack` | Template — copied to each `Player.Backpack` | `LocalScript` (inside Tools) |

- **R2.2** Server scripts start when the server starts the place (once the DataModel is loaded);
  they do not wait for any player. Client scripts start only after the owning client has
  received the instance (replication) and the container permits execution.
- **R2.3** `StarterPlayerScripts` contents are "copied to the PlayerScripts container **once**
  when a Player joins the game." They are NOT re-copied on character respawn — they persist for
  the session.
- **R2.4** `StarterCharacterScripts` contents are parented into `Player.Character` **each time
  the character spawns**. "Unlike scripts stored in the StarterPlayerScripts folder, these
  scripts will not persist when the character respawns" — they are destroyed with the old
  character and re-cloned into the new one. (Use them for per-life logic; use
  StarterPlayerScripts for per-session logic.)
- **R2.5** `StarterGui` contents are copied into `Player.PlayerGui` on spawn; "When a player
  respawns, the contents of PlayerGui are emptied" and re-copied (subject to
  `ScreenGui.ResetOnSpawn`). `StarterPack` contents are copied into `Player.Backpack` per spawn.
- **R2.6** `ReplicatedFirst` is "a container whose contents are replicated to all clients (but
  not back to the server) **first before anything else**." That is the only ordering guarantee
  it gives: contents-before-rest-of-place. There is **no documented guarantee about the order
  in which multiple scripts inside it start**.
- **R2.7** Scripts in `ReplicatedFirst` must not assume the rest of the place exists:
  "LocalScripts running in ReplicatedFirst will need to wait for any objects they require to
  replicate using `Instance:WaitForChild()`", and "Any objects that are to be used by a
  LocalScript in ReplicatedFirst should also be parented to ReplicatedFirst. Otherwise, they
  may replicate to the client late, yielding the script and negating the benefit of initial
  replication."
- **R2.8** `ReplicatedFirst:RemoveDefaultLoadingScreen()` "immediately removes the default
  Roblox loading screen"; and "if any object has been placed in ReplicatedFirst, the default
  loading screen will be removed after a few seconds regardless if this method has been called
  or not."
- **R2.9** An object created by the server "will not replicate to clients until it is parented
  to some object that is replicated." (Corollary: things created and kept under `ServerStorage`
  / `ServerScriptService`, or unparented, are invisible to clients.)

### Implementation notes for CoreAI

- CoreAI needs the same three-phase startup: (1) world/server scripts at session start,
  (2) per-player scripts on join (copied once), (3) per-character/per-life scripts re-cloned on
  every spawn. Map CoreAI's join/spawn/respawn hooks onto R2.3–R2.5 exactly.
- Replication visibility is per-container, not per-script: model "server-only" containers so
  mod authors can hide server logic/data (R2.1, R2.9).
- Provide a `ReplicatedFirst` analogue only if CoreAI has progressive loading; its sole
  guarantee is "these assets arrive before the rest" (R2.6), not script ordering.

Sources:
- https://create.roblox.com/docs/projects/data-model
- https://create.roblox.com/docs/scripting/locations
- https://create.roblox.com/docs/reference/engine/classes/ReplicatedFirst
- https://create.roblox.com/docs/reference/engine/classes/StarterPlayerScripts
- https://create.roblox.com/docs/reference/engine/classes/StarterCharacterScripts

---

## 3. ModuleScript / `require` semantics

### Rules

- **R3.1** A `ModuleScript` must **return exactly one value**. `require` "is provided a
  ModuleScript, then runs the code, waiting until it returns a singular value."
- **R3.2** Caching — one result per environment: ModuleScripts "run once and only once per Luau
  environment and return the exact same value for subsequent calls to `require()`." The
  `require` global doc phrases the cache key as: "future require() calls for the same
  ModuleScript (**on the same side of the client-server boundary**) will not run the code
  again."
- **R3.3** Identity, not copy: because the same value is returned, a table returned by a module
  is **shared mutable state** among all requirers within that environment.
- **R3.4** Per-context isolation: return values are "independent with regards to Scripts and
  LocalScripts, and other environments." Requiring the same ModuleScript from a `LocalScript`
  executes its body **again on that client**, even if a server `Script` already required it.
  The server has one module state; each connected client has its own separate module state.
- **R3.5** `require` accepts `ModuleScript | string | number`:
  - **Instance**: the normal path.
  - **String**: Unix-like relative paths with prefixes `./`, `../`, `@self/`, `@game/`.
  - **Number (asset ID)**: only if the uploaded model's root module is named `MainModule`;
    "only works on the server and will error on the client."
- **R3.6** `require` by instance does not wait for the instance to exist: it "fails immediately
  if a ModuleScript doesn't exist; it doesn't wait for creation." (Authors combine it with
  `WaitForChild` on the client.)
- **R3.7** Cyclic require deadlocks silently: "If a ModuleScript is attempting to require()
  another ModuleScript that in turn tries to require() it, the thread will **hang and never
  halt**." Cyclic calls "do not generate errors." (Roblox gives no cycle detection at runtime.)
- **R3.8** If a required module errors during its body, `require` propagates the error to the
  requiring thread; a module that never returns keeps all requirers yielded (R3.1, R3.7).

### Implementation notes for CoreAI

- Implement a require cache keyed by (module identity, VM/context). One Lua state per context
  (server, each client) gives R3.2/R3.4 for free; a shared state would need explicit per-context
  registries.
- Enforce "exactly one return value" at load time; error otherwise.
- Decide consciously whether to copy Roblox's silent cyclic-require hang (R3.7) or improve on
  it with cycle detection + error. Deviation here is developer-friendly but must be recorded.
- Support string-path require (`./`, `../`, `@self/`) — it is the modern documented form and
  maps naturally onto CoreAI mod folder layouts.

Sources:
- https://create.roblox.com/docs/reference/engine/classes/ModuleScript
- https://create.roblox.com/docs/reference/engine/globals/LuaGlobals#require

---

## 4. Task scheduler and frame pipeline

### Rules

- **R4.1** The task scheduler "coordinates tasks done each frame as the game runs, even when it
  is paused," including "detecting player input, animating characters, updating the physics
  simulation, and resuming scripts in a `task.wait()` state."
- **R4.2** The official scheduler-priority diagram (task-scheduler.svg in the docs) defines the
  per-frame cycle in this exact order (side lanes in parentheses):
  1. **Replication receive** (Replication)
  2. **PreAnimation event** → run scripts
  3. **Step Humanoid**
  4. **PreSimulation event** → run scripts
  5. **Step simulation** (Simulation — the physics step)
  6. **PostSimulation event** → run scripts
  7. **Resume delayed threads** (threads whose `task.wait`/`task.delay` deadline passed)
  8. **Heartbeat event** → run scripts (marked `Heartbeat*` — see R4.4)
  9. **Replication send** (Replication)
  10. **Input processing** → run scripts
  11. **PreRender event** → run scripts (Rendering)
  12. **Asynchronous render** (handoff; the engine continues while rendering completes)

  Equivalently, in developer-facing order within a rendered frame: input → `PreRender` → render
  handoff → `PreAnimation` → humanoid/animation step → `PreSimulation` → physics →
  `PostSimulation` → delayed-thread resumption → `Heartbeat` → replication send.
- **R4.3** "Some tasks may not perform work in a frame, while others may run multiple times"
  (e.g. physics can sub-step; several categories can be skipped under load).
- **R4.4** Footnote on the diagram: "Script execution during `RunService.Heartbeat` differs
  depending on your game's `Workspace.SignalBehavior` setting" (see section 5).
- **R4.5** Modern `RunService` events and their documented roles:
  - `PreRender(deltaTimeRender)` — "fires every frame, prior to the frame being rendered."
    Client-only. Warning: "the engine cannot start to render the frame until code running in
    this event has finished executing."
  - `PreAnimation(deltaTimeSim)` — "fires every frame, prior to the physics simulation but
    after rendering" (i.e. after the previous frame's render handoff); for modifying animation
    objects.
  - `PreSimulation(deltaTimeSim)` — "fires every frame, prior to the physics simulation";
    occurs after animations are stepped; for setting velocities/forces. Doc note: it "is the
    last Luau event fired before `Motor6D.Transform` is applied to part positions."
  - `PostSimulation(deltaTimeSim)` — "fires every frame, after the physics simulation has
    completed"; precedes Heartbeat; for reacting to physics results.
  - `Heartbeat(deltaTime)` — "fires every frame, after the physics simulation has completed";
    "when most scripts run"; for periodic/core game logic.
- **R4.6** Legacy aliases: `RenderStepped` "has been superseded by PreRender which should be
  used for new work"; `Stepped(time, deltaTime)` "has been superseded by PreSimulation which
  should be used for new work" (its first argument is total run time). **Heartbeat carries no
  supersession note in the current docs — it is still current API** (see UNCERTAIN note U2).
- **R4.7** `RunService:BindToRenderStep(name, priority, func)` binds "a custom function to be
  called at a specific time during the render step"; lower priority number runs sooner.
  `Enum.RenderPriority` reference points: Player Input = 100, Camera = 200. Docs best practice:
  "For strict control over order, use `BindToRenderStep()` instead of `PreRender`," and don't
  bind to the render step unless the work must happen after input but before rendering.
- **R4.8** `task` library exact semantics:
  - `task.spawn(fn|thread, ...)` → thread: "takes a thread or function and resumes it
    **immediately** through the engine's scheduler."
  - `task.defer(fn|thread, ...)` → thread: "defers it until the end of the current resume point
    within the current frame"; "an optimized version of spawn() that schedules a thread to
    resume as soon as possible (but not immediately) without any throttling." Use it "when a
    similar behavior to task.spawn() is desirable, but the thread does not need to run
    immediately."
  - `task.delay(t, fn|thread, ...)` → thread: schedules resumption "on the next Heartbeat after
    the given amount of time in seconds has elapsed"; "no throttling occurs: on the very same
    Heartbeat step in which enough time has passed, the function is guaranteed to be
    called/resumed"; duration 0 guarantees "the very next Heartbeat."
  - `task.wait(t?)` → elapsed: "Yields the current thread until the given duration (in seconds)
    has elapsed, then resumes the thread on the next Heartbeat step." Default duration 0
    (minimum: one Heartbeat). "The actual amount of time elapsed is returned." No throttling.
  - `task.cancel(thread)`: "Cancels a thread and closes it, preventing it from being resumed
    manually or by the engine's scheduler." Errors if the thread cannot be cancelled (e.g. the
    currently executing thread or one it depends on).
  - `task.synchronize()` / `task.desynchronize()` (Parallel Luau): move the thread to the next
    serial / parallel execution phase; "Only scripts which are descendants of an Actor may call
    this method."
  - `task.spawn`/`task.defer` preserve the caller's serial-vs-parallel execution phase.
- **R4.9** Legacy globals are deprecated with a documented ~30 Hz floor:
  - `wait(t?)` → (elapsedTime, totalRunTime): "The delay will have a minimum duration of
    **29 milliseconds**, but this minimum may be higher depending on the target framerate and
    various throttling conditions." "Superseded by task.wait()."
  - `spawn(fn)`: runs the callback "the next time Roblox's Task Scheduler runs an update cycle.
    This delay will take at least 29 milliseconds but can arbitrarily take longer." Callback
    receives (elapsedTime, engineUptime). "Superseded by task.spawn()."
  - `delay(t, fn)`: same 29 ms minimum and throttling caveat. "Superseded by task.delay()."
  - The 30 Hz legacy pipeline + throttling under load is exactly *why* they are deprecated;
    the `task.*` family is frame-accurate and unthrottled.
- **R4.10** Coroutine interop: `task.spawn`/`defer`/`delay`/`cancel` all accept and return
  plain Luau `thread` values, so the `coroutine` library and the scheduler interoperate — a
  coroutine can be handed to the scheduler and a scheduler-created thread can be cancelled.
  The docs describe `coroutine` as an alternative "which has some additional functionality"
  but define no further guarantees (notably: no documented statement about error reporting
  differences; see U3).
- **R4.11** Delayed threads (`task.wait`/`task.delay`) resume in the dedicated "Resume delayed
  threads" slot immediately **before** the Heartbeat event in the frame cycle (R4.2); the
  task-library docs describe this as resuming "on the next Heartbeat step" — treat these as the
  same scheduling point.

### Implementation notes for CoreAI

- Implement the frame loop as R4.2's ordered categories with named script-resumption points;
  each "run scripts" slot doubles as a deferred-event resumption point (section 5).
- `task.*` is the normative API surface: immediate resume (spawn), end-of-current-resume-point
  queue (defer), Heartbeat-aligned timers with no throttling (wait/delay, elapsed-time return),
  cancellable thread handles (cancel).
- Do not reproduce legacy `wait/spawn/delay` unless mod-compat demands it; if reproduced, keep
  the 29 ms floor + throttling so behavior matches (R4.9).
- Keep `PreRender`-equivalent work blocking the render handoff (R4.7 warning) so mod authors
  get the same perf model.

Sources:
- https://create.roblox.com/docs/performance-optimization/microprofiler/task-scheduler
  (order diagram: content/en-us/assets/optimization/task-scheduler/task-scheduler.svg in
  github.com/Roblox/creator-docs)
- https://create.roblox.com/docs/reference/engine/libraries/task
- https://create.roblox.com/docs/scripting/scheduler
- https://create.roblox.com/docs/reference/engine/classes/RunService
- https://create.roblox.com/docs/reference/engine/globals/RobloxGlobals

---

## 5. Event / signal model (RBXScriptSignal, SignalBehavior)

### Rules

- **R5.1** `RBXScriptSignal` API:
  - `Connect(fn)` → `RBXScriptConnection`: "Establishes a function to be called when the event
    fires."
  - `Once(fn)` → connection: same, but "only the first event will be delivered"; the
    "connection to the function will be automatically disconnected prior the function being
    called" (so re-firing from inside the handler cannot re-enter it).
  - `Wait()`: "Yields the current thread until the signal fires and returns the arguments
    provided by the signal."
  - `ConnectParallel(fn)`: handler runs "in a desynchronized state"; "more efficient than using
    Connect followed by a call to task.desynchronize()"; the script "must be rooted under an
    Actor."
  - `RBXScriptConnection:Disconnect()` severs the connection; `Connected` reports liveness.
- **R5.2** `Workspace.SignalBehavior` (enum `Enum.SignalBehavior`) governs when handlers run:
  - `Default` (0): "currently equivalent to `Immediate` but this will eventually change to
    `Deferred`."
  - `Immediate` (1): "Event handlers are resumed immediately when the event occurs."
  - `Deferred` (2): "All events are deferred and their handlers resumed at specific resumption
    points each frame."
  - `AncestryDeferred` (3): "Equivalent to Deferred but only for events triggered by changes in
    ancestry."
  - New template places default to **Deferred**; docs recommend Deferred ("helps improve the
    performance and correctness of the engine").
- **R5.3** Immediate behavior: firing an event invokes handlers instantly, so an event fired
  inside a handler nests — the inner handlers complete before the outer handler continues.
- **R5.4** Deferred behavior: a fired event does not invoke handlers at the fire site; the
  invocation is queued and runs at the **next resumption point**, "along with any newly
  triggered event handlers" (queued invocations processed in sequence, not nested).
- **R5.5** The documented resumption points for deferred handlers are: input processing;
  `RunService.PreRender`; legacy script resumption (`wait()`, `spawn()`, `delay()`);
  `RunService.PreAnimation`; `RunService.PreSimulation`; `RunService.PostSimulation`;
  task-based resumption (`task.wait()`, `task.spawn()`, `task.delay()`); `RunService.Heartbeat`;
  `DataModel.BindToClose`. (These align with the "run scripts" slots of R4.2.)
- **R5.6** Deferred re-entrancy is capped: an event chain that keeps re-firing itself is cut
  off at a fixed depth — "The current limit for this is 10."
- **R5.7** Deferred + disconnect edge cases:
  - "Multiple event handler invocations can be queued before you disconnect from the event."
  - "Calling `Disconnect()` drops all pending event handler invocations."
  - "Any other method of disconnection — such as calling `Destroy()` — disconnects the signal
    immediately, but **runs the associated event handler for any events that are still
    pending**."
- **R5.8** Deferred + destruction ordering: handlers for events fired during a `Destroy()` run
  *after* the destruction completes, so the handler observes post-destruction state (e.g.
  `Parent` already `nil`, connections gone).
- **R5.9** Under Deferred, `Once()` and `Wait()` deliver the **first queued** invocation
  (single delivery is preserved; delivery is postponed to the resumption point).
- **R5.10** Event arguments are passed as Luau values: for engine signals, arguments are
  provided per-invocation with the values the event was fired with. Roblox does not document
  copy-on-fire for plain signals — but for **Bindable** events it does: "Tables passed as
  arguments to bindable events and callbacks are **copied**, meaning they will not be exactly
  equivalent to those provided when firing"; non-string keys are converted to strings; "If a
  table has a metatable, all of the metatable information is lost"; mixed numeric/string-key
  tables "can result in removed elements"; avoid `nil` holes. `BindableEvent:Fire()` "does not
  yield, even if no script has connected to the event" (no queueing for late connectors).
- **R5.11** Handler invocation order for multiple connections to one signal is **not
  documented**. Do not depend on connection order (see U4).
- **R5.12** `Instance:Destroy()` disconnects all connections on that instance's signals
  (section 6, R6.2) — under Deferred, subject to the pending-invocation rule of R5.7.

### Implementation notes for CoreAI

- Ship **Deferred** as CoreAI's default (Roblox's recommended/template default), with the queue
  flushed at every script-resumption point of the frame loop; keep an Immediate switch only if
  mod-compat requires it.
- Implement: per-signal FIFO invocation queue; args captured at fire time; re-entrancy counter
  with cap 10; `Disconnect` = drop pending, `Destroy` = run pending then disconnect (R5.7).
- `Once` must disconnect *before* invoking (R5.1) — this is load-bearing for re-entrancy safety.
- For CoreAI's bindable-style custom events, adopt the table-copy + string-key + no-metatable
  sanitization (R5.10) or consciously deviate (documented deviation: pass by reference).
- Never expose or document a handler ordering guarantee (R5.11).

Sources:
- https://create.roblox.com/docs/reference/engine/datatypes/RBXScriptSignal
- https://create.roblox.com/docs/scripting/events/deferred
- https://create.roblox.com/docs/reference/engine/enums/SignalBehavior
- https://create.roblox.com/docs/reference/engine/classes/BindableEvent
- https://create.roblox.com/docs/scripting/events/bindable

---

## 6. Instance lifecycle

### Rules

- **R6.1** Construction best practice: "When creating an object and setting many properties,
  it's recommended to set the `Parent` property **last**. This ensures the object replicates
  once, instead of replicating many property changes." Also: "An object created by the server
  will not replicate to clients until it is parented to some object that is replicated."
- **R6.2** `Instance:Destroy()` — exact documented effects: "Sets the Instance.Parent property
  to `nil`, **locks** the Instance.Parent property, **disconnects all connections**, and calls
  `Destroy()` on all children." A destroyed instance can never be reused (Parent is locked);
  docs advise to "set any variables referencing the object (or its descendants) to `nil`."
- **R6.3** Destroying a script or an ancestor of a script stops its thread (R1.11).
- **R6.4** `Debris:AddItem(instance, lifetime = 10)` "schedules a given Instance for
  destruction within the specified lifetime … the object is destroyed in the same manner as
  `Instance:Destroy()`." Advantages over `task.delay(t, …Destroy)`: Debris runs outside the
  script's lifetime (still fires if the scheduling script is disabled/destroyed). Cap: 1,000
  items; beyond that "the oldest debris will be destroyed instantly to make room" (lifetime is
  a maximum, not a guarantee). `MaxItems` is deprecated (setting it errors).
- **R6.5** `Instance:Clone()` "creates a copy of an instance and all of its descendants,
  ignoring all instances that are not `Archivable`." Returns `nil` if the instance itself has
  `Archivable = false`. Reference-property fixup: "If a reference property refers to an
  instance that was also cloned, the copy will refer to the copy. If a reference property
  refers to an instance that was not cloned, the same value is maintained."
- **R6.6** `Archivable` also controls whether the instance is saved/published with the place.
- **R6.7** Attributes = user-defined properties:
  - API: `SetAttribute(name, value)` (value `nil` removes), `GetAttribute(name)`,
    `GetAttributes()`, `AttributeChanged`, `GetAttributeChangedSignal(name)` (fires with no
    parameters).
  - Naming: "Names must only use alphanumeric characters", plus periods, hyphens, slashes,
    underscores; "No spaces or unique symbols"; max 100 characters; names must not start with
    `RBX` (reserved for Roblox core scripts).
  - Supported value types (exact documented list): string, boolean, number, `UDim`, `UDim2`,
    `BrickColor`, `Color3`, `Vector2`, `Vector3`, `CFrame`, `NumberSequence`, `ColorSequence`,
    `NumberRange`, `Rect`, `Font`. "When attempting to set an attribute to an unsupported type,
    an error will be thrown."
  - Attributes are saved with the place/asset and "are **replicated** so that clients can
    access them immediately"; same-type changes (e.g. two attribute changes) arrive in order,
    but cross-channel ordering (attribute change vs a RemoteEvent) is not guaranteed.
- **R6.8** Tags (`CollectionService`): string tags on instances; `Instance:AddTag/RemoveTag/`
  `HasTag/GetTags` mirror `CollectionService` methods. `GetTagged(tag)` "returns an array of
  instances with a given tag which are descendants of the DataModel."
  `GetInstanceAddedSignal(tag)` fires when the tag is assigned or a tagged instance enters the
  DataModel; `GetInstanceRemovedSignal(tag)` on removal/exit — the canonical pattern is
  GetTagged-at-startup + both signals for add/cleanup. Tags serialize with the place and
  replicate server→client **as a whole set per instance** (server changes overwrite client-side
  tags; under streaming, re-streamed instances lose client-side tag edits).
- **R6.9** `Instance:WaitForChild(name)` "returns the child of the Instance with the given
  name. If the child does not exist, it will **yield** the current thread until it does."
  Overload `WaitForChild(name, timeOut)` "will time out after the specified number of seconds
  and return `nil`." Warning behavior: "If a call to this method exceeds **5 seconds** without
  returning, and no timeOut parameter has been specified, a warning will be printed" (the
  "Infinite yield possible" warning).
- **R6.10** `FindFirstChild(name)` returns the child or `nil` without yielding. Documented
  cost: it "takes about 20% longer than using the dot operator and almost 8 times longer than
  simply storing a reference to an object" — avoid in hot paths. Family:
  `FindFirstChild`, `FindFirstChildOfClass`, `FindFirstChildWhichIsA`, `FindFirstAncestor*`,
  `FindFirstDescendant` (name-recursive lookup via the `recursive` parameter).
- **R6.11** Change signals:
  - `Object.Changed(property)` "fires immediately after a property of the object changes";
    the new value is NOT passed — read `object[property]`. For `ValueBase` objects (IntValue,
    StringValue, …) `Changed` fires **only** for the `Value` property, passing its contents.
  - `Object:GetPropertyChangedSignal(property)` "behaves exactly like the Changed event, except
    that it only fires when the given property changes"; passes **no arguments**; repeated
    calls with the same property "return the same event."
  - Limitations (both): they do "**not** fire for physics-related changes" (CFrame/Position/
    Orientation/AssemblyLinearVelocity/AssemblyAngularVelocity changing due to simulation) —
    use `RunService.PreSimulation`-style polling instead; very-frequently-changing properties
    "may not fire on every modification … and/or may not fire at all."
- **R6.12** Hierarchy signals: `ChildAdded`/`ChildRemoved` (direct children);
  `DescendantAdded`/`DescendantRemoving` (all descendants); `AncestryChanged(child, parent)`
  "fires when this instance is reparented or when any of its ancestors is reparented."

### Implementation notes for CoreAI

- Destroy must be a single atomic primitive: detach + parent-lock + disconnect-all + recursive
  destroy (R6.2); make script-thread termination ride on it (R6.3).
- Provide a Debris-style engine-side TTL destroyer decoupled from mod script lifetime (R6.4).
- Attributes: implement the documented type whitelist + name validation verbatim (R6.7) — it is
  cheap and makes save/replication formats stable.
- Change-signal design: per-property signals with no args + a coarse `Changed`; exclude
  simulation-driven transform changes from both (R6.11) so mods poll in the sim phase instead.
- `WaitForChild` needs the 5-second "infinite yield" warning — it is Roblox's single most
  useful diagnostics affordance for mod authors (R6.9).

Sources:
- https://create.roblox.com/docs/reference/engine/classes/Instance
- https://create.roblox.com/docs/reference/engine/classes/Object
- https://create.roblox.com/docs/reference/engine/classes/Debris
- https://create.roblox.com/docs/reference/engine/classes/CollectionService
- https://create.roblox.com/docs/studio/properties (attribute types)
- https://create.roblox.com/docs/scripting/attributes

---

## 7. Error handling and logging, Roblox-style

### Rules

- **R7.1** `pcall(f, ...)` → `(true, ...results)` on success, `(false, errorMessage)` on
  failure. Protected calls are the standard Roblox idiom around anything that can fail
  (DataStores, network, user callbacks). Yielding inside `pcall` is permitted in Roblox Luau
  (docs do not restrict it; UNCERTAIN flag U5 covers the absence of an explicit statement).
- **R7.2** `xpcall(f, handler, ...)` is `pcall` with a custom message handler; "The `err`
  function preserves the stack trace of function `f`, which can be inspected using
  `debug.info()` or `debug.traceback()`" — i.e. the handler runs **before** unwinding, which is
  the only way to capture the erroring stack.
- **R7.3** `error(message, level)` semantics:
  - level `0`: "avoids the addition of error position information to the message."
  - level `1` (default): "the error position is where the error function was called."
  - level `2`: "points the error to where the function that called error was called; and so on."
  - Non-string error values are allowed and pass through `pcall` unchanged.
- **R7.4** `assert(value, message?)` "throws an error if the provided value is `false` or
  `nil`. If the assertion passes, it returns all values passed to it." Default message:
  `"assertion failed!"`.
- **R7.5** An uncaught error terminates only its own thread (script/coroutine), never the whole
  VM or other scripts (R1.11: a script stops if it "raises an uncaught error").
- **R7.6** Global error hook: `ScriptContext.Error(message: string, stackTrace: string,
  script: Instance)` fires for uncaught runtime errors; usable from ordinary scripts (security:
  None). `ScriptContext` itself "controls all BaseScript objects. Most of the properties and
  methods of this service are locked for internal use."
- **R7.7** Log stream: `LogService.MessageOut(message: string, messageType: Enum.MessageType,
  context)` "fires when the client outputs text"; `LogService:GetLogHistory()` returns "a table
  of tables, each with the message string, message type, and timestamp."
  `Enum.MessageType` values: `MessageOutput`, `MessageInfo`, `MessageWarning`, `MessageError`.
  Docs warning: LogService "might have unexpected or unreliable behavior and content might be
  truncated. Don't rely on contents of events and messages emitted by this service for any
  important game logic."
- **R7.8** Output producers map to message types: `print()` → MessageOutput, `warn()` →
  MessageWarning, uncaught errors → MessageError (the Output window shows "errors captured from
  running scripts, messages from Roblox Engine, messages from calls to print(), and errors from
  calls to warn()"). The Output window can additionally show timestamps, context, and source
  (script name + line number).
- **R7.9** Error position format: runtime error messages are prefixed with the script's
  full name and line — `<full.script.path>:<line>: <message>` (this is what `error` level
  1/2 controls, R7.3). The human-readable stack trace that follows in the Output
  (`Stack Begin` / `Script '<path>', Line <n> - function <name>` / `Stack End`) is empirical,
  not documented — see U6.
- **R7.10** `debug.traceback(message?, level?)` "returns a string of **undefined format** that
  describes the current function call stack." Normative doc warning: it "will often return
  inaccurate results … and the format of the returned traceback may change at any time. You
  should **not** parse the return value for specific information such as script names or line
  numbers."
- **R7.11** Roblox-native conventions an AI-facing error should follow:
  - one line, `path:line: message`, no exception classes;
  - `warn()` for recoverable issues (yellow), `error()`/uncaught for failures (red);
  - stack trace as a separate multi-line block, innermost frame first;
  - machine consumers subscribe to `ScriptContext.Error` (message + stackTrace + script ref)
    and `LogService.MessageOut`, never parse `debug.traceback`.

### Implementation notes for CoreAI

- Reproduce the two-channel model: a per-thread protected-call mechanism (`pcall`/`xpcall`
  with pre-unwind handler, R7.2) + a global uncaught-error signal carrying
  (message, stackTrace, scriptRef) (R7.6) + a log stream with the four MessageTypes (R7.7).
- Format CoreAI mod errors as `<mod.path.to.script>:<line>: <message>` with `error`-level
  position control (R7.3) — this is what makes errors "feel Roblox-native" to an AI trained on
  Roblox output.
- Keep traceback strings explicitly "undefined format" in CoreAI docs (R7.10) and provide
  structured frames via the error signal instead.

Sources:
- https://create.roblox.com/docs/reference/engine/globals/LuaGlobals (pcall, xpcall, error, assert)
- https://create.roblox.com/docs/reference/engine/classes/ScriptContext
- https://create.roblox.com/docs/reference/engine/classes/LogService
- https://create.roblox.com/docs/reference/engine/enums/MessageType
- https://create.roblox.com/docs/reference/engine/libraries/debug
- https://create.roblox.com/docs/studio/output

---

## Appendix: UNCERTAIN items (docs vs community knowledge)

- **U1 — Default SignalBehavior.** The enum doc says `Default` is "currently equivalent to
  Immediate but this will eventually change to Deferred," while new template places ship with
  Deferred. Community often states "Deferred is the default" unconditionally. Normative
  reading: the *enum default* is still Immediate-equivalent; the *authoring default* is
  Deferred. CoreAI ships Deferred.
- **U2 — Heartbeat supersession.** Community folklore says "Heartbeat is superseded by
  PostSimulation." The current RunService reference marks `RenderStepped` → `PreRender` and
  `Stepped` → `PreSimulation` as superseded, but **Heartbeat has no supersession note** and
  fires *after* PostSimulation as a distinct event. Treat Heartbeat and PostSimulation as two
  separate, both-current scheduling points.
- **U3 — coroutine.resume error visibility.** Community knowledge: errors inside threads
  resumed via `coroutine.resume` do not reach the console/`ScriptContext.Error`, unlike
  `task.spawn`. The current docs do not state this. Verify empirically before relying on it.
- **U4 — Handler invocation order.** Neither `RBXScriptSignal` nor the deferred-events doc
  specifies the order in which multiple connections to one signal are invoked (community
  reports LIFO for Immediate mode). Not documented → not guaranteed → CoreAI must not promise
  an order.
- **U5 — Yielding inside pcall.** Universally relied upon in Roblox code (DataStore idiom) and
  true in Roblox Luau, but the current globals reference does not explicitly say "pcall may
  yield." Safe to implement as allowed; flagged only because the doc statement is absent.
- **U6 — Output stack-trace format.** The `Stack Begin` / `Script '<path>', Line <n>` /
  `Stack End` block format is stable empirical behavior, not documented; `debug.traceback` is
  explicitly documented as undefined-format. CoreAI may mimic the visual format but must not
  spec it as parseable.
- **U7 — ReplicatedFirst script start order.** Community sometimes claims ReplicatedFirst
  LocalScripts "run before all other scripts" as an ordering guarantee between scripts. The
  docs only guarantee *content replication* order ("replicated … first before anything else")
  and advise `WaitForChild` for everything else; inter-script start ordering is not documented.
