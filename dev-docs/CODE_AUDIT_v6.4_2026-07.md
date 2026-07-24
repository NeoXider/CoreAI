# CoreAI Code Audit — v6.3.3 → v6.4.1 (RunService / connection ownership / tick pump / bundled samples)

Date: 2026-07-24
Scope: READ-ONLY correctness + code-quality audit of the newly written runtime code and bundled
samples. No code changes, no git, no Play Mode.

Files audited:

- `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxRunService.cs`
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxScriptSignal.cs` (+ `RbxScriptConnection`, same file)
- `Assets/CoreAIMods/Runtime/RbxApi/Instances/ModConnectionRegistry.cs`
- `Assets/CoreAIMods/Runtime/Infrastructure/LuaModRuntimeTickDriver.cs`
- `Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs`
- `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs`
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRobloxDatatypeBindings.cs` (signal Connect path)
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRobloxInstanceBindings.cs` (TrackConnection, RunService reads)
- `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRobloxApiBindings.cs` (PumpFrame)
- `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/{sample_welcome,sample_lane_racer,sample_tetris3d}.lua`

---

## Findings, ranked by severity

### HIGH-1 — Hot-reload disconnects the RELOADED mod's own signal connections (dead game loop after reload)

**Where:**
- `LuaCsModRuntime.cs:642` (`BuildMod` runs the new chunk) then `LuaCsModRuntime.cs:648`
  (`TeardownModEffects(..., Reload, ...)`)
- Fires `CoreAiModsInstaller.cs:145-149` → `ownedConnections?.DisconnectOwnedBy(modId)`
- `ModConnectionRegistry.cs:60-81` (`DisconnectOwnedBy` drops the whole `_byMod[modId]` list)
- Connections tracked at `LuaCsRobloxDatatypeBindings.cs:983-986` → `ModConnectionRegistry.Track` under
  `context.OwnerModId` (the mod id, `LuaCsRobloxInstanceBindings.cs:80-83`)

**What's wrong:**
The connection registry is keyed only by mod id string — it has no notion of "instance generation."
`ReloadMod` intentionally builds and runs the replacement chunk FIRST (so a failed build leaves the
old mod untouched). That replacement chunk's top-level `RunService.Heartbeat:Connect(...)` /
`UserInputService.*:Connect(...)` calls run inside `BuildMod`, and each is immediately `Track`ed into
`_byMod[modId]` — the SAME key the old instance uses. `TeardownModEffects` then raises
`ModTearingDown(Reload)`, whose installer handler calls `DisconnectOwnedBy(modId)`, which disconnects
**every** connection under that id — including the freshly created ones — and removes the entry.

Net effect: after a reload the replacement mod is `_mods[modId]` and looks loaded, but all of its
RunService/UserInputService handlers are already `Disconnect`ed. Its per-frame loop never runs again.

The design intent is documented at `CoreAiModsInstaller.cs:133-135` ("the re-run chunk re-Connects
fresh handlers on reload, so the stale ones must always be dropped") — but the implementation drops
the fresh ones too because they were created before teardown. Note the logic-slot path solves exactly
this "new chunk already ran" problem with the `keepState` argument
(`LuaCsModRuntime.cs:923-929`, `TeardownModEffects(..., replacement.State)`); the connection teardown
has no equivalent exclusion, which is precisely the gap.

**Concrete failure scenario:**
Every bundled sample connects at chunk top level (`sample_welcome.lua:27`, `sample_lane_racer.lua:96`,
`sample_tetris3d.lua:207`). Enable `sample_lane_racer`, then edit + reload it (or let auto-repair
`ReloadMod` it). The car stops responding to A/D and blocks stop advancing — the Heartbeat handler is
dead — while `manage_mods list` still shows it loaded and un-quarantined. Same for any repaired mod:
the LLM "fixes" it, the reload succeeds, and the mod is silently inert.

**Severity:** HIGH — reload is both the user-edit path and the auto-repair path; this makes both
silently no-op for any signal-driven Rbx mod (the primary sample pattern).

**Minimal fix (do NOT implement):** make teardown target only the OLD instance's connections. Options:
key `ModConnectionRegistry` by the `Mod`/context instance (or a per-load generation token) instead of
the bare mod-id string, so `DisconnectOwnedBy` on reload releases only the previous generation; OR give
`DisconnectOwnedBy` a "keep these" exclusion mirroring the logic-slot `keepState`; OR in `ReloadMod`,
snapshot-and-disconnect the old connections BEFORE calling `BuildMod`. The instance-generation key is
cleanest and also fixes MEDIUM-1 below.

---

### MEDIUM-1 — Registry accumulates dead connections (Once handlers + manual Disconnect leak until unload)

**Where:** `ModConnectionRegistry.cs:26-40` (`Track`), `RbxScriptConnection.Disconnect`
(`RbxScriptSignal.cs:29-38`), Once handling `RbxScriptSignal.cs:149-152`.

**What's wrong:** When a connection ends by any path other than `DisconnectOwnedBy` — i.e. a Lua
`conn:Disconnect()`, or a `:Once` connection that auto-disconnects after firing — the connection is
removed from the signal's `_connections` but the registry is never notified, so its entry stays in
`_byMod[modId]` forever. The list only shrinks on mod teardown. A long-lived mod that uses `:Once`
per event, or reconnects periodically, grows an unbounded list of dead `RbxScriptConnection` refs for
its whole lifetime.

**Concrete failure scenario:** A mod that does `part.Touched:Once(...)`-style re-arming (or reconnects
Heartbeat on a state change) every second accumulates thousands of dead entries per hour; teardown
then also walks all of them calling idempotent no-op `Disconnect()`s. Not a crash, but steady memory
growth and a slow teardown for chatty mods.

**Severity:** MEDIUM (latent leak; bounded only by mod lifetime, not by live connection count).

**Minimal fix:** have `RbxScriptConnection.Disconnect` (or `RbxScriptSignal.Remove`) notify the owning
registry so the entry is pruned when the connection actually ends; or periodically compact `_byMod`
lists by dropping `!Connected` entries. An instance-keyed registry (HIGH-1 fix) does not by itself fix
this — pruning on disconnect is still needed.

---

### MEDIUM-2 — RunService signal order deviates from Roblox (RenderStepped fired last)

**Where:** `RbxRunService.cs:53-66` (`Step` fires Stepped → Heartbeat → RenderStepped).

**What's wrong:** Roblox's per-frame resumption fires **RenderStepped first** (immediately before the
frame renders), then Stepped (before physics), then Heartbeat (after physics). This pump fires
RenderStepped LAST, after Heartbeat. Given the memory-noted "Rbx API = Roblox 1:1" bar, a mod that
relies on RenderStepped running before Heartbeat within a frame (e.g. camera/UI positioning that must
lead the physics/Heartbeat logic) will observe the wrong relative order.

**Concrete failure scenario:** A mod updates a camera CFrame in RenderStepped and reads it in Heartbeat
expecting the RenderStepped value from the SAME frame; here it reads the previous frame's value because
RenderStepped for this frame hasn't fired yet.

**Severity:** MEDIUM for API fidelity / LOW practical impact today (all three bundled samples use only
Heartbeat, so nothing is affected in-box).

**Minimal fix:** reorder to `RenderStepped` → `Stepped` → `Heartbeat`, and update the `<summary>` /
`WHY:` comments that assert the current order. (Confirm against the authoritative Roblox scheduler
doc before changing, since the exact RenderStepped placement is easy to get wrong.)

---

### MEDIUM-3 — Registry / signal single-thread invariant is assumed, not enforced

**Where:** `ModConnectionRegistry.cs:14-20` ("single-threaded, main-thread-only by invariant … the
dictionary is unsynchronized"); `RbxScriptSignal._connections` is likewise a plain `List<>`.

**What's wrong:** `Step`/`PumpFrame` fire on the Unity main thread (`LuaModRuntimeTickDriver.Update`),
but `DisconnectOwnedBy` runs from `ModTearingDown`, which is raised by `UnloadMod`/`ReloadMod` — driven
by the `manage_mods` tool. If any mod-lifecycle call (unload/reload) can be dispatched off the main
thread (tool executor, async repair loop), it mutates both the unsynchronized `_byMod` dictionary AND a
firing signal's `_connections` list concurrently with `Update`'s dispatch — a data race / possible
`InvalidOperationException` mid-fire. In contrast, `LuaCsModRuntime` itself guards its `_mods` with
`_gate`; the Rbx connection layer has no such guard.

**Severity:** MEDIUM (correctness depends entirely on an unchecked threading assumption; benign if every
lifecycle path really is marshaled to the main thread).

**Minimal fix:** document + assert the main-thread requirement at the `Track`/`DisconnectOwnedBy`
entry points (e.g. a main-thread check in editor/dev builds), or lock the registry and signal
connection lists. At minimum, verify the `manage_mods` executor marshals to the main thread and record
that invariant here.

---

### LOW-1 — Per-frame allocations in the RunService fire path

**Where:** `RbxRunService.cs:52` (`object delta = deltaSeconds;`), `RbxRunService.cs:55/60/65`
(`Fire(params object[])`), `RbxScriptSignal.Fire` (`RbxScriptSignal.cs:106`), and the handler wrapper
`LuaCsRobloxDatatypeBindings.cs:1005` (`new LuaValue[args.Length]` per call).

**What's wrong:** Even with the `HasConnections` gates, a frame with a live Heartbeat listener
allocates: one boxed `float` (`delta`, boxed unconditionally at line 52 before the gates), one
`object[]` per `Fire` (`params` array), a boxed `_runTime` for Stepped, and a `LuaValue[]` per handler
invocation. At 60 fps with the always-connected sample handlers this is steady GC churn. The class
comment ("an unlistened signal boxes nothing") is accurate but only covers the unlistened case.

**Severity:** LOW (steady-state garbage, not a correctness bug; `Step` is explicitly a
pre-MVP2-scheduler pump per the `TODO`).

**Minimal fix:** move the `object delta = deltaSeconds;` box after the first `HasConnections` check (or
skip it entirely when no signal has connections); add fixed-arity `Fire(object)` / `Fire(object,object)`
overloads on `RbxScriptSignal` to avoid the `params` array; reuse a cached args array. Defer to the
MVP2 scheduler rewrite if not worth churning now.

---

### LOW-2 — Tetris `render()` allocates ~72 tables per frame via `occupied()`

**Where:** `sample_tetris3d.lua:120-142` (`render` loops WIDTH×HEIGHT and calls `occupied`),
`sample_tetris3d.lua:110-118` (`occupied` calls `piece_cells(piece)`), `sample_tetris3d.lua:90-100`
(`piece_cells` builds a fresh table + subtables every call).

**What's wrong:** `render` calls `occupied(x,y)` for every one of the 72 cells each frame; for every
cell not already set in `grid`, `occupied` calls `piece_cells(piece)`, which allocates a new 4-entry
table (plus a `{x=,y=}` subtable per cell) and re-runs the rotation loop. That is up to ~72 piece-cell
rebuilds per frame just to draw. GC churn on a Lua VM that runs under a per-call allocation budget.

**Concrete failure scenario:** On a slow/WebGL target the render loop's garbage can dominate frame
time and, in pathological cases, brush the per-call allocation guard.

**Severity:** LOW (sample; still playable). Worth fixing as an exemplar since samples are teaching
material ("demos must be bright selling mini-tutorials").

**Minimal fix:** compute `piece_cells(piece)` ONCE per frame into a small `{ [y*W+x]=kind }` lookup set
and have `occupied` consult that set instead of rebuilding the piece cells per grid cell.

---

### LOW-3 — Lane Racer collision can tunnel on a frame hitch

**Where:** `sample_lane_racer.lua:117` (`o.z = o.z + SPEED * dt`) and `:127`
(`o.lane == lane and o.z <= 1.5 and o.z >= -1.5`).

**What's wrong:** The crash window is a fixed ±1.5-stud band around the car. Advancement is
`SPEED*dt` (20 studs/s). At dt ≈ 0.2 s (a hitch or a low-fps device) a block moves 4 studs in one
frame and can step from `z < -1.5` to `z > 1.5`, skipping the band entirely — a same-lane block passes
through the car with no crash and is instead scored on despawn.

**Severity:** LOW (sample; only manifests on large `dt`).

**Minimal fix:** detect a crash by interval overlap across the step (test whether the segment
`[oldZ, newZ]` crosses the car band) rather than sampling the post-step `z`; or clamp `dt` used for
motion.

---

### LOW-4 — Tetris gravity accumulator resets to 0 instead of subtracting the interval

**Where:** `sample_tetris3d.lua:229-230` (`gravAccum = gravAccum + dt`; on fire `gravAccum = 0`).

**What's wrong:** Unlike the racer's `spawnTimer = spawnTimer - SPAWN_INTERVAL`
(`sample_lane_racer.lua:135`, which preserves the remainder), the tetris gravity resets the accumulator
to 0 on each drop, discarding sub-interval overshoot. Minor drop-cadence jitter, not a gameplay bug.

**Severity:** LOW (cosmetic timing).

**Minimal fix:** `gravAccum = gravAccum - (soft and SOFT_INTERVAL or GRAV_INTERVAL)` for consistency
with the racer.

---

## Items checked and found FINE (no action)

- **`hooks_every` sub-frame / zero / NaN / negative interval** (`LuaCsModRuntime.cs:1350-1378`,
  `TickTimers` :978-993): the clamp to `0` is safe. `DueIn` resets to `IntervalSeconds` (0) each fire
  and `TickTimers` fires at most once per `Tick`, so interval 0 is a clean "every frame" loop — no
  busy-loop, no catch-up burst, and there is no division anywhere so no divide-by-zero. The
  once-per-tick guarantee holds. Correctly implemented.
- **`LuaModRuntimeTickDriver.Update`** (`:29-36`): `dt = Time.deltaTime` is read once and passed to
  both `_preTick` and `Tick`, so pump and timers share one clock. Both invocations are null-guarded.
  Order (input+RunService pump → runtime tick) is correct: Heartbeat fires before `hooks_every` timers
  advance. Good.
- **`PumpFrame` wiring** (`CoreAiModsInstaller.cs:243-244`, `LuaCsRobloxApiBindings.cs:147-151`):
  `stackRobloxApi` may be null → a null `preTick` is passed and the driver tolerates it; inside
  `PumpFrame`, `_userInputService`/`_runService` are null-guarded. Order is input then RunService. Good.
- **`RbxScriptSignal.Dispatch` re-entrancy / connect-or-disconnect during fire**
  (`RbxScriptSignal.cs:121-165`): iterates a snapshot; the shared `_fireBuffer` is used only on the
  non-nested path guarded by `_firing`, and a nested fire takes a private copy, so the shared buffer is
  never clobbered. A handler that Connects during fire is added to `_connections` (not the snapshot) and
  fires next frame — matches Roblox. A handler that Disconnects is skipped via the `Connected` check.
  `Once` disconnects BEFORE invoking, matching Roblox. Solid.
- **`DisconnectOwnedBy` called mid-fire** (`ModConnectionRegistry.cs:60-81`): `Disconnect` →
  `RbxScriptSignal.Remove` mutates `_connections` while `Dispatch` iterates the copy — safe. Idempotent,
  no double-free. (Its ONLY problem is the reload timing of HIGH-1, not mid-fire safety.)
- **`RbxRunService._runTime` accumulation** (`:48`): monotonic sum of deltas; a faithful match for
  Roblox `Stepped`'s first `(time)` argument. Fine.
- **Tetris line-clear** (`sample_tetris3d.lua:157-179`): the clear loop is correct — a full row shifts
  every higher row down (`grid[yy] = grid[yy+1]`), clears the top row, and does NOT advance `y` so the
  shifted-in row is re-tested; the `y = HEIGHT` top-row case degenerates cleanly (empty shift loop +
  top cleared). No off-by-one. `partAt` cells are positional (not piece-bound), so `render` correctly
  repaints them after a shift. `collides` allows cells above `HEIGHT` (spawn overflow) and blocks
  `y < 1` (floor). Correct.
- **Obstacle/table growth bounds** (`sample_lane_racer.lua:60-73`): `spawn_obstacle` caps at
  `MAX_OBSTACLES`; despawn removes from the table; `restart` clears it. Bounded.
- **Welcome sample** (`sample_welcome.lua`): correct accumulate-and-subtract timing; after `MAX_TICKS`
  it early-returns each frame (the Heartbeat connection is intentionally left connected — a no-op, not a
  leak).

---

## What is DONE WELL

- **Teardown ORDER in the installer** (`CoreAiModsInstaller.cs:145-168`): connections are disconnected
  BEFORE the instance sweep, so no still-live handler fires against a just-destroyed instance
  (`INSTANCE_DESTROYED`). Instance sweep is correctly gated to `Unload` only (not Reload/Quarantine),
  and relies on `GetOwnedBy` returning a snapshot + idempotent `Destroy`. The reasoning is well
  documented. (The connection half is undermined only by HIGH-1's reload timing.)
- **`RbxScriptSignal` re-entrancy design**: the shared-buffer-with-private-fallback pattern and the
  "Once disconnects before invoke" semantics are a careful, correct match to Roblox.
- **`LuaCsModRuntime` quarantine staleness guard** (`:887-914`): re-checks `ReferenceEquals(live, mod)`
  under `_gate` before quarantining, so a repair `ReloadMod` landing mid-tick can't suspend the fresh
  instance. Subtle and correct.
- **`LuaCsModRuntime` teardown `keepState`** (`:923-943`): the logic-slot teardown correctly excludes
  the replacement's fresh defines — this is exactly the pattern the connection teardown is MISSING
  (see HIGH-1); the slot path is the model the fix should follow.
- **No-drop event budgeting + round-robin rotation** (`:844-880`, `:1000-1045`): global/per-mod caps
  with fair rotation and carry-over of undelivered events; handler-list snapshot under `_gate` prevents
  mutation-during-enumeration. Well thought through.
- **Per-subscriber isolation** on every `Raise*` (`:1152-1297`): a throwing host subscriber never skips
  the remaining subscribers or fails the mod lifecycle. Consistent and correct.
- **`hooks_on("tick"/"update"/"frame")` → timer routing** (`:1312-1331`): a pragmatic guard against the
  common LLM mistake of registering a per-frame handler as a named event that nothing emits.
- **Bundled samples are genuinely pure-Roblox** (RunService.Heartbeat + UserInputService +
  Instance.new), frame-rate-independent (dt-scaled motion), own a single Folder for clean teardown, and
  use rising-edge input — good teaching material and a real 1:1 import/export test.

---

## Recommended priority

1. **HIGH-1** — fix before shipping: reload silently kills any signal-driven mod, including every
   sample and every auto-repaired mod. Instance-generation-keyed registry fixes it and also enables the
   MEDIUM-1 pruning fix.
2. **MEDIUM-1 / MEDIUM-3** — connection-leak pruning and the threading invariant (assert or lock).
3. **MEDIUM-2** — RenderStepped ordering, for the "Roblox 1:1" bar.
4. **LOW-1..4** — allocation/tunneling/timing polish; batch into the MVP2 scheduler pass or a samples
   cleanup.
