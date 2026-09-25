# CoreAI Lua mod authoring guide

> How to write a CoreAI Lua mod. Audience: the AI agent and human authors. Runtime VM: Lua-CSharp.
> Examples: `Assets/CoreAI.Demos/Mods/*.lua`.

## What a mod is
A mod is Lua source that the runtime runs ONCE through `LoadMod`. During that run it **registers hooks**;
afterwards the host drives those hooks. Persistent mods live across frames (and reloads). A one-off script
(`execute_lua`) runs once and is not persisted.

**A reload starts from a clean world.** Save & run on the Hub and `manage_mods reload` remove what the
previous run's main chunk built before its first yield (its *startup objects*) before the new chunk runs,
so a mod that builds a castle at startup can be saved again and again without stacking castles; objects
built later by handlers, tasks or players stay. Pass `keep_objects: true` to `manage_mods reload` (or turn
on **Keep objects on Save & run** in the Hub) to keep everything and build next to it. Build your scene in
the main chunk, before the first `task.wait`, so a reload can replace it (`mod-system.md` §5c).

## Header
Start with a `@coreai` block so the mod is discoverable/manageable:
```lua
--[[@coreai
id: my_mod              -- stable slug = identity (rename = new mod). [a-z0-9_]
name: My Mod            -- display name (cosmetic)
version: 1.0.0          -- semver
active: false           -- seeded enabled/disabled
capabilities: All       -- e.g. All  |  All, Full
author: CoreAI
description: one line.
]]
```

## Always-available mod API (no capability tier needed)
| Function | Meaning |
|---|---|
| `hooks_on(event, fn)` | `fn(eventName, payload)` runs when the game or another mod emits `event`. |
| `hooks_every(seconds, fn)` | repeating timer; `seconds >= 0.05`. `"tick"`/`"update"`/`"frame"` hooks map to a ~20 Hz timer. |
| `events_emit(name, payload)` | emit an event to the game + other mods. `payload` is a **string**. |
| `store_set(key, value)` / `store_get(key)` | per-mod persistent string key/value (survives frames + reloads). |
| `mod_id()` | this mod's id. |
| `report(msg)` / `print(msg)` | diagnostic line back to the host (muted by default; host enables per mod). |
| `warn(...)` | (Rbx API) the arguments joined by spaces, into the mod's log at `Warn` level — `get_mod_logs` shows it. A one-off `execute_lua` chunk, or a composition without a mod log, writes to the host log instead, at most 20 lines per 10 s per script (the rest are counted into the next line). |

## Inter-mod API (cross-mod, plain-data only)
| Function | Meaning |
|---|---|
| `mods_export(name, value)` | publish a value OR function under this mod's id. |
| `mods_get(modId, name)` | read another mod's exported **plain data** (throws for a function export — use `mods_call`). |
| `mods_call(modId, name, ...)` | call another mod's exported **function** on its own state; returns a copied result. |
| `mods_list_exports(modId)` | list export names (introspection — the AI discovers callable APIs). |

**Hard rule (multiplayer-determinism seam):** only PLAIN DATA (numbers/strings/bools/tables) crosses the mod
boundary. Functions, closures, and live references never leave a mod's own state — `mods_call` runs the
function in the provider and copies back the result. Nesting is capped (`CrossModTableDepth = 4`), cross-call
depth is capped (`MaxCrossCallDepth = 8`). See `shared_stats_provider.lua` + `shared_stats_consumer.lua`.

An export runs under the caller's limits, as a Roblox module call runs in its caller's thread: it gets at most
what the calling run has left of steps, time and memory — from a `Heartbeat` handler or a `task.*` thread that
is the rest of its per-resume budget — its steps are charged to the caller, it continues the caller's count
of nested library calls, and it stops as soon as its caller is stopped (the calling mod killed, for example).
An export that runs away ends its caller too, and no `pcall` around `mods_call` catches that.

Wrong arguments to these functions fail the way stock Lua reports them — `bad argument #1 to 'store_set' (string
expected, got table)` — with no CLR type name and no doubled `hooks_on: hooks_on:` prefix. A value that is not
an argument (a table field, a returned value) and has the wrong type reads `bad value in 'fn' (x expected, got
y)`. Both surfaces read arguments by one rule set (`LuaCsValueMarshaller`), the one Luau's `luaL_check*`
functions use: a string parameter takes a number as `tostring` gives it (`store_set(7, 8)` stores `"7"` = `"8"`;
a boolean or a table is refused), a number parameter takes a string `tonumber` accepts (`" 0x10 "`, `"1e3"`,
`"inf"`), an integer parameter truncates toward zero (`4.6` -> `4`; the mod-core API used to round), and a
boolean parameter takes only `true`/`false` (an omitted one is `false`). The Rbx surface converts the same way
(`Part.Name = 5`, `player:Kick(42)`), and an Enum property or instance-method argument also takes the item's
Name or Value (`part.Material = "Wood"`); the full table is in
[RBX_API.md](../../Assets/CoreAI/Docs/RBX_API.md), "How arguments and property writes convert".

## Capability tiers — gate the GAME bindings
The mod-core + inter-mod API above is always present. Tiers gate the **game** bindings:
- **Read** — query only.
- **Gameplay** — `time_*` (time scale) and read-only `input_*`.
- **WorldEdit** — `Instance.new` and the rest of the Rbx API (see
  [RBX_API.md](../../Assets/CoreAI/Docs/RBX_API.md)): creating, changing and destroying instances. The
  `Instance` global itself exists on every tier, so a read-only script's `Instance.new` raises the capability
  error instead of indexing a nil global. The classic
  `coreai_world_*` build calls (spawn/change/destroy/scene/animation/sound) are withheld stubs in the default
  composition; they raise an error that points at the Rbx API.
- **LogicOverride** — `logic_*` formulas.
- **Full** — `unity_*` generic reflection (get/set fields, call methods on ANY component). Host/singleplayer-only;
  never grant it to a networked client. Opt-in (**Enable Full Lua Access** on `CoreAiModsLifetimeScope`); NOT part
  of `All`.
A binding absent from your tier simply doesn't exist in the sandbox (calling it errors).

## Roblox-style signals and scheduling

Every `RbxScriptSignal` uses deferred dispatch. Firing an event queues its connected handlers;
it never runs them at the mutation or engine callback that fired the event. CoreAI drains that
queue at every script-resumption point in its frame loop, so a handler runs at the next resumption
point. This matches Roblox's recommended `Deferred` behavior and the default for new Roblox
templates.

Signal handlers are scheduler-owned threads and may yield with `task.wait()`:

```lua
local handled = false

workspace.ChildAdded:Connect(function(child)
    handled = true
    task.wait(0.25)
    print("resumed for", child.Name)
end)

local folder = Instance.new("Folder")
folder.Parent = workspace
-- handled is still false here; the handler has not reached a resumption point yet.
```

Do not depend on the relative invocation order of multiple connections to the same signal. That
order is not part of the CoreAI or Roblox authoring contract.

A task handle from `task.spawn`/`task.defer`/`task.delay` can be handed back to those functions:
`task.spawn(t)` resumes a parked task and returns `t` itself, and `task.defer(t)`/`task.delay(s, t)`
reschedule it. A native `coroutine.yield()` inside such a task parks it until `task.spawn(t, ...)`, and the
extra arguments are what `coroutine.yield` returns. Threads from `coroutine.create` stay outside the
scheduler: passing one to `task.*` is `BAD_ARGUMENT`, and `task.wait`, `signal:Wait`, `WaitForChild` or a
`RemoteFunction` invoke inside one raise `CONTEXT_VIOLATION` — run such code with `task.spawn`.

**A player who leaves takes their mods along.** When an actor disconnects, the mods loaded for that
actor are unloaded (mods loaded with host authority stay), including one in quarantine; if mod code is
running at that moment — a mod whose own script kicked its player — the unload waits until that code has
returned. The stored package stays active, so the mod starts again on the next world load or rehydrate; it
is not restarted automatically when the player rejoins. A mod whose main chunk disconnects its own actor
while it loads does not load (`InvalidOperationException`, "did not load: its actor … disconnected while its
main chunk ran").

**What a client's remote call causes is charged to that client.** Handlers started by a player's
`FireServer`/`InvokeServer` count against that player's remote budget (32 alive), and everything they
cause — `task.*` threads, the signal handlers their writes and fires start, a `:Wait()` they resume —
against the player's induced budget (128). When that is full, a signal handler invocation waits in a
queue and starts later, in the order that player's fires happened; so a host listener on such a signal
can run late while a client floods, and the host's own writes can overtake the flooder's. Details in
[RBX_API.md](../../Assets/CoreAI/Docs/RBX_API.md), "Mutation envelopes, access control and disconnects".

A `Humanoid:MoveTo` ends with `MoveToFinished(false)` when your script or a tween moves the character's
`HumanoidRootPart` (its `CFrame`, `Position`, `Orientation` or `Rotation`, or a `PivotTo` that carries it),
as in Roblox; teleport the character after the walk has finished if you need both.

## Roblox services and deferred placeholders

In the standard runtime, `game:GetService()` resolves these tree-backed services: `Workspace`,
`Lighting`, `ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `StarterPlayer`, `Players`,
`HttpService`, `UserInputService`, `MaterialService`, `Debris`, `CollectionService`, `TweenService`,
`RunService`, and `ScriptContext`. A tree-backed service can still have
service-specific members that have not landed; resolution alone does not promise that every Roblox
member exists.

A known catalog service without an implementation also resolves successfully. It returns a
placeholder, and the first member read, write, or method lookup raises `NOT_IMPLEMENTED` with the
delivery rung recorded by `ServiceCatalog`. The phase a stub names is the old ladder's number until the
stubs are retagged (`TODO.md`); the rung that delivers it now is in the last column
([ROBLOX_API_ROADMAP.md](ROBLOX_API_ROADMAP.md) §4.1 maps the two):

| Service | Phase the stub names | Rung now |
|---|---|---|
| `DataStoreService` | MVP9 | MVP16 |
| `ContextActionService` | MVP10 | MVP7 (ContextActionService-lite), MVP15 (touch buttons) |
| `SoundService` | MVP15 | MVP17 |
| `AIService` | a future MVP (reserved) | MVP11 |
| `PathfindingService` | no planned MVP (not planned) | — |
| `MarketplaceService` | no planned MVP (not planned) | — |

The catalog also retains fallback registrations for `RunService` (MVP2) and `UserInputService`
(landed in MVP1). The standard runtime replaces those fallbacks with their live tree-backed
implementations
before returning them.

This delayed failure is intentional. Roblox scripts commonly acquire services at the top of a file
but use them only in a later code path. Failing during `GetService()` would prevent the file from
loading and stop unrelated supported code from running. For example:

```lua
local DataStoreService = game:GetService("DataStoreService") -- resolves

print("unrelated setup still runs")

-- The first member lookup fails loudly and names its phase, MVP9 (the old number of MVP16).
DataStoreService:GetDataStore("Saves")
```

An unknown, unregistered name still fails immediately at `GetService()` with `UNKNOWN_SERVICE`.

`ScriptContext` has one member, `SetTimeout(seconds)`, and it is host-gated — see
[Sandbox & limits](#sandbox--limits).

## Coroutines (work across frames, WebGL-safe)
`coroutine.create/resume/yield/status` are available (`coroutine.wrap` is removed; `coroutine.resume` runs under
the per-resume budget). A coroutine lets a mod spread a sequence over time
without blocking — it yields, the host advances the frame, and you resume it next tick. Under Lua-CSharp this
is frame-pumped, so `coroutine.yield` works on WebGL too (a blocking wait would deadlock single-threaded WASM;
this needs the bundled VM at v0.5.6 or newer — older builds froze the player on the first yield).
See `coroutine_countdown.lua`. Do NOT busy-wait; yield and resume from a timer/handler.

- `coroutine.resume` works only on a coroutine your code made with `coroutine.create`. Handed a task,
  signal-handler or main-chunk thread (a `coroutine.running()` value) it returns `false` and "cannot resume a task
  or signal-handler thread with coroutine.resume; resume a parked task thread with task.spawn(thread, ...), passing
  the handle task.spawn, task.defer or task.delay returned", without touching the thread: resume a parked task with
  `task.spawn(handle)`.
- A `coroutine.create` body — and a thread `task.spawn` runs at once — is held to its own budget or what the
  run that resumes it has left, whichever is smaller, for steps, time and memory alike (inside a task thread
  that is the thread's remaining 10,000 steps / 500 ms, not the coroutine's own 500,000 / 1 s). The steps
  it uses are charged back to its resumer, and when a limit it borrowed runs out the whole chain ends,
  uncatchably. Memory is your mod's `HandlerMaxAllocatedBytes` from a handler, a task or the main chunk, and
  what the coroutine keeps alive counts toward its resumer too. A thread the scheduler resumes from its own
  frame gets its full budget.

## Design rule: native/Lua boundary
C# owns per-frame hot loops (movement, camera, physics). Lua **tweaks parameters and reacts to discrete
events** — it does not run the hot loop. "Change a mechanic while playing" should DECLARE the change / emit a
command, not spin a transform every frame. Prefer routing world changes through events/commands (the
authoritative channel) over direct mutation — it stays deterministic and multiplayer-ready. See
`day_night_cycle.lua`.

## Sandbox & limits
- No `io`/`debug`/`package`/`require`; `load`/`loadstring`/`dofile`/`loadfile` are removed. The stock `os` library is
  removed too; the Rbx API provides an `os` table with only `os.time()` and `os.clock()`.
- Every resume of your code — the main chunk, a signal handler, a `task.*` resume, a one-off `execute_lua` chunk
  — runs under a **per-resume budget** with two halves: an instruction-step cap and a wall-clock cap (CoreAI's
  defaults: 10,000 steps / 500 ms; a mod's own `coroutine.resume` gets a larger bound derived from the same
  setting, capped at what the resuming run has left — see Coroutines above). It is enforced through Lua-CSharp's
  per-instruction hook, so a runaway handler (`while true do end`) is cut on ALL platforms incl. WebGL — a buggy
  mod cannot hang a frame — and the failure reaches you as a budget kill, `BUDGET_EXCEEDED`, naming the bound
  and your line, not as a Lua error to "fix". A budget kill cannot be caught: `pcall`/`xpcall` inside the run
  that tripped let it through (the `xpcall` handler does not run), so a runaway never outlives its budget. Only
  code that resumed a raw `coroutine.create` coroutine sees its trip, as `false` plus the line from
  `coroutine.resume`; that coroutine is dead.
- **A thread may run forever as long as it yields.** The per-resume budget is the only CPU limit on a
  scheduler thread (the main chunk, `task.*`, signal handlers), exactly as in Roblox: `while true do
  task.wait() end` runs for the whole session. (A lifetime step cap survives only on coroutine handles
  a host builds directly, and exhausting it fails loudly with `EXCEEDED_LIFETIME_STEP_BUDGET`.)
- **Memory is budgeted per resume too.** Live heap growth inside one resume of any of the mod's threads
  is capped by the mod's allocation budget (`HandlerMaxAllocatedBytes`, 256 MB by default); a resume
  that exceeds it is cut with `BUDGET_EXCEEDED` / `EXCEEDED_MEMORY_BUDGET` and the fix hint "keep less
  memory alive between two yields" — build big data across several yields, or keep less of it.
- **Library calls back into Lua share one cap of 128 levels along a chain of nested runs.** A `pcall` or
  `xpcall` body opens one level; a `gsub` replacement function (two since 7.47.0), a `__tostring` run by
  `tostring`, `print`, `warn` or `string.format` (your own `tostring` included, so `tostring = warn; warn(1)`
  raises the catchable error below instead of crashing the game), a `table.sort` comparator, a `gsub` `__index`, a
  `__pairs`/`__ipairs` metamethod, a coroutine run by `coroutine.resume`, a thread `task.spawn` runs at once
  and a guarded call that starts inside a run (a `mods_call` export included) open two — so `pcall` nests 128
  deep and `table.sort` or `tostring` 63. A resumed thread, and a `mods_call` export, continue the count of
  the run they start in. The call past the cap raises
  `C stack overflow (<function>: more than 128 levels of nested calls from library functions back into Lua)`,
  an ordinary error: `pcall` returns it, `xpcall` hands it to its handler. Plain Lua recursion and the
  metamethods the VM runs itself (`__index` on a table access, arithmetic, comparisons, `__call`) are not
  limited by it.
- **String patterns are budgeted per call.** `string.find`/`match`/`gmatch`/`gsub` stop after 5,000,000
  matcher steps with `BUDGET_EXCEEDED` (`EXCEEDED_PATTERN_STEP_BUDGET`), and a `gsub` or `string.format`
  result may not exceed 1,000,000 characters. Pattern semantics are Luau's.
- **No yield inside a library callback (Luau parity).** Yielding (`task.wait`, `coroutine.yield`) inside a
  `__tostring` run by `tostring`, `print`, `warn` or `string.format`, a `table.sort` comparator, a
  `__pairs`/`__ipairs` metamethod, a `gsub` replacement function or `__index` raises `attempt to yield across a
  C-call boundary` and the thread runs on; yielding inside `pcall` is fine. The refusal holds after the callback
  resumed a coroutine of its own. A callback that merely runs long is not a yield: under `execute_lua` the frame
  is handed back inside it and the call completes. A coroutine whose body is `coroutine.yield`, or ends in
  `return coroutine.yield(...)`, fails its resume here (Luau runs it): write `local r = coroutine.yield(...)
  return r`.
- **Sandbox lines start with `sandbox: `.** Refusals and budget lines the sandbox itself raises —
  `sandbox: string.rep result would exceed 1000000 chars.`, `sandbox: EXCEEDED_HARD_LIMIT_STEPS (<steps>)`,
  `sandbox: Lua exceeded <N> ms.`, `sandbox: EXCEEDED_MEMORY_BUDGET (<bytes> bytes)` — carry the prefix, and
  its library wrappers report a bad argument as Lua does, a missing one included: `bad argument #1 to 'rep'
  (string expected, got no value)`.
- **The budget is the game's, not CoreAI's.** The host sets both halves on `CoreAiModsLifetimeScope`
  (**Lua coroutine resume budget**; `<= 0` falls back to the defaults) and every resume re-reads them, so a
  game may tighten the budget for untrusted mods or loosen it for a heavy simulation while mods are already
  running. `game:GetService("ScriptContext"):SetTimeout(seconds)` moves the wall-clock half live for every
  subsequent resume — but only from host-composed code: an ordinary mod calling it is refused with
  `NOT_AUTHORITY`, the way Roblox limits the member to plugins. The instruction half has no Lua-facing
  setter, because Roblox has none.
- Caps: timer min interval `0.05 s`, exports/mod, dispatch per tick (no events dropped; serviced on later
  ticks), quarantine after consecutive failures (the mod stays loaded; reload resumes it). A failed
  hook/timer call counts once and a successful one resets the streak; for scheduler threads a frame with
  any number of faults counts once and a frame whose threads ran cleanly resets it, so an error repeated
  every frame (a signal cascade) is quarantined while a rare one is not. Two budget cuts in a row
  quarantine the mod at once and suspend its stored copy, so it does not start with the next game either;
  loading or saving it again by hand clears that.

## Lua version note
Lua-CSharp targets **Lua 5.2** semantics with **double-only numbers** — there is no integer/float
subtype and no native bitwise operators (`&`, `|`, `~`, `<<`, `>>` postdate 5.2). Luau sources are
run through the downleveler (Luau → Lua 5.2) at ingestion. Source nested more than 200 levels deep
(brackets, tables, functions, blocks, if-expressions, interpolations) is refused with a `syntax error:`
line — the same limit the VM's parser has, for Luau and plain Lua alike — instead of overflowing the stack,
which used to end the process on Save & run, `execute_lua` or a world restore
(`Assets/CoreAIMods/Runtime/LuauDownlevel/README.md`). Stdlib coverage is partial — a missing library
function errors, so keep to common `string`/`table`/`math` calls.

## Bundled mods — ship a game with ready-made mods
Drop `.lua` files (each with an `@coreai` header) into a **`Resources/CoreAIMods/`** folder. On the first
run `BundledModSeeder` (wired in `CoreAiModsInstaller`, runs before rehydrate) installs them into the
persistent store; `active: true` mods load immediately, `active: false` ones ship dormant (enable from the
Hub Mods tab). Five samples live in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/`:
`sample_welcome.lua` (active) and the opt-in `sample_lane_racer.lua`, `sample_tetris3d.lua`,
`sample_clicker.lua` and `sample_castle3d.lua`.

Updates are version-driven and player-respectful:
- Bump the header `version:` and re-ship → the seeder **updates** an unmodified copy, keeping the player's
  enabled/disabled choice.
- If the player edited the mod, it is **not** overwritten — the entry is flagged `UpdateAvailable` for a
  manual update in the UI.
- A same-or-older version, or a mod the player authored under the same id, is left untouched.

Hosts can add more `IBundledModSource`s (StreamingAssets, Addressables, remote) alongside the Resources
one; see `Docs/CoreAIMods/mod-system.md` §3.

## Example mods (`Assets/CoreAI.Demos/Mods/`)
- `hello_world.lua` — minimal: one event hook, one timer, a persistent counter.
- `score_tracker.lua` — events + store persistence.
- `day_night_cycle.lua` — a live mechanic via timer + `events_emit` (native/Lua boundary).
- `coroutine_countdown.lua` — a coroutine yielding across ticks (WebGL-safe).
- `shared_stats_provider.lua` / `shared_stats_consumer.lua` — inter-mod `mods_export`/`mods_get`/`mods_call`.
- `full_mode_cube.lua` / `first_person_controller.lua` — Full-tier `unity_*` (reflection) examples.
