# Lua Sandbox Security

This guide documents the security boundary for runtime Lua execution in CoreAI.
It is written for teams that expose Lua through `LuaTool`, custom runtime bindings,
or AI-authored gameplay scripts.

## What To Remember

Lua is allowed to request gameplay changes. C# decides whether those changes are
legal.

That single rule keeps the integration understandable. The sandbox should make
scripts useful for gameplay iteration without turning them into a back door into
files, processes, networking, Unity internals, or server authority.

## Scope

CoreAI treats Lua as untrusted gameplay logic:

- AI output and player-provided script text must be validated before execution.
- Lua scripts may read and write only APIs explicitly registered by the host.
- Scripts must be bounded by instruction and timeout limits.
- Host bindings are responsible for domain validation, authority, and rollback.

The sandbox is a defense layer, not a permission system for sensitive server-side
operations.

## Optional Module (`COREAI_LUA`)

Lua is an **optional module** and is off by default. The runtime ships in the CoreAI Mods package
(`com.neoxider.coreaimods`), with Lua-CSharp bundled as `Lua.dll` / `Lua.Annotations.dll` under
`Assets/CoreAIMods/Plugins/` — there is no external package to install. A scene runs Lua only when
it composes `CoreAiModsLifetimeScope` (or a host calls `CoreAiModsInstaller.RegisterCoreAiMods`); the
core `CoreAILifetimeScope` registers no Lua services.

The scripting symbol `COREAI_LUA` (Project Settings → Player → Scripting Define Symbols, or
**CoreAI → Setup → Modules → Lua (Lua-CSharp) → Enable Lua**) is the positive opt-in, absent by
default and independent of `COREAI_LLM`: Lua-only and LLM-only builds are supported, both symbols
enable the full runtime, and no symbols produce the minimal portable core. The package's runtime
assemblies carry no define constraint (`tools/check_positive_module_opt_in.py` refuses an assembly
blanket-gated by a module symbol). When `COREAI_LUA` is set:

- the guarded Lua demos, demo adapters, benchmark paths and Lua-dependent test fixtures compile in;
- the sandbox escape fixture (`LuaCsSecureSandboxEditModeTests`, guarded by `#if COREAI_LUA`)
  executes in CI and is required to report test cases.

Upgrading from a release before v7.0.0: the positive opt-in replaced the former negative opt-out symbol. Remove
the old symbol from project settings and add `COREAI_LUA` only to targets that deliberately enable
scripting.

### Unity scene module

The Lua capability grant is owned by `CoreAiModsLifetimeScope`, the child scope that installs the mod
runtime: **Enable Full Lua Access** (Full tier for `execute_lua`, `manage_mods` and rehydrated mods),
**Enable Full Lua Private Access** (non-public members), **Allowed Lua Scenes** (the Lua
`coreai_world_load_scene` whitelist), **Blacklist Policy** and the coroutine resume budget.

World-command permissions are owned by an optional `CoreAiLuaWorldModule` child of
`CoreAILifetimeScope`: the prefab whitelist and the scene whitelist the world-command executor
enforces. The module has no Full-access checkbox: its legacy serialized Full flags never
granted `Full` to Lua, stay only so old scenes load unchanged, and their accessors are `[Obsolete]`
(read `CoreAiModsLifetimeScope.FullLuaAccessEnabled` / `FullLuaPrivateAccessEnabled`). Existing scenes
with the former flat fields on `CoreAILifetimeScope` migrate into the module without losing serialized
values.

### CI matrix

`.github/workflows/ci.yml` runs EditMode tests in four configurations on pushes to `main`, pull
requests and the merge queue: `core` (no optional symbols), `llm` (`COREAI_LLM`), `lua`
(`COREAI_LUA`), and `full` (both). The `lua` and `full` jobs additionally assert that the
`LuaCsSecureSandboxEditModeTests` escape-test fixture actually executed, so isolation coverage cannot
silently drop out of the suite. A separate `dotnet` job runs the portable Lua-tier tests
(`tools/portable/LuaTests`) without Unity and fails when too few cases pass or too many are not executed. The Unity jobs need the
standard GameCI secrets (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) configured in the
repository.

## Generation Rate Limit

Lua generation is constrained at multiple stages:

- `LuaTool`/`execute_lua` enforces a sliding-window limiter (`LuaGenerationRateLimiter`, default 20 per 60 s) in the tool path; the Unity composition registers one limiter per `CoreAiModsLifetimeScope`.
- `LuaCsAiEnvelopeProcessor` (a component a host composes itself; the default Unity composition does
  not construct it) enforces the same limiter for envelope runs and scheduled Programmer repair
  generations. The envelope and `execute_lua` share a limiter when the same limiter is injected, so a
  busy model or repair loop is blocked consistently across both layers.

`LuaGenerationRateLimiter` is default-on. `maxPerWindow <= 0` still disables it.

When the limiter is saturated, `execute_lua` returns `Lua rate limit exceeded (... per ...s); call rejected.` without running the chunk, and a composed envelope processor fails the envelope and skips repair, so a failing script cannot spin a runaway generate→fail→repair loop against the LLM.

## Additional hardening

- `string.rep` is capped via a replaced implementation in `LuaCsSecureEnvironment`.
  `LuaCsSecureEnvironment.MaxStringRepLength` is `1_000_000`; attempts that exceed this
  limit fail fast with an explicit error.
- `table.concat` is capped the same way (`MaxTableConcatLength`, same `1_000_000` value): the
  replacement mirrors the real `table.concat` algorithm but aborts as soon as the
  in-progress result exceeds the cap, instead of finishing a potentially huge build first.
- **Per-execution allocation budget (default 256 MB).** `string.rep`, `string.format`, and
  `table.concat` are capped at their library call site, but plain string concatenation (`s = s .. s`)
  is ordinary VM opcodes with no call site to intercept — a ~1 MB allowed string can be doubled
  repeatedly into hundreds of MB well within the instruction budget. The instruction hook
  (`LuaCsExecutionGuard`, and the per-resume coroutine hook in `LuaCsSecureEnvironment`) therefore also
  watches the heap through `LuaCsAllocationBudget` and aborts with `EXCEEDED_MEMORY_BUDGET`. The hook
  fires once per small instruction batch, tight enough that a doubling bomb cannot overshoot the budget
  by more than about one doubling between samples. Configure the budget via the `maxAllocatedBytes`
  constructor parameter on `LuaCsExecutionGuard`, or `LuaCsSecureEnvironment.MaxAllocatedBytesBudget`
  for the default.
  - **A sample is a suspicion, not a trip.** Unity's Mono does not implement
    `GC.GetAllocatedBytesForCurrentThread` (it returns 0 unconditionally), so the cheap reading is
    `GC.GetTotalMemory(false)`: process-wide and garbage-inclusive. In a WebGL player that reading
    crosses a 256 MB budget after a few seconds of a loop that retains nothing, which would cut
    pure-arithmetic runaways with `EXCEEDED_MEMORY_BUDGET` instead of their own wall-clock limit. The
    trip is therefore decided by one confirming `GC.GetTotalMemory(true)`: a real bomb's result string
    is live and survives the collection, while transient runtime garbage does not. The trip reference
    taken at the start of the execution never moves up (a lower post-collection reading may lower it);
    a cleared suspicion instead re-arms the next one a quarter of the budget above the post-collection
    reading, so forced collections stay bounded while live growth can overshoot the budget by at most
    that quarter. The budget is per execution — one guarded call or one coroutine resume — never
    cumulative across a thread's resumes, because the reading is process-wide.
  - **What this does not cover:** the budget bounds live growth, not GC churn — a script that allocates
    and discards memory in a tight loop is bounded by the wall-clock timeout instead. A single host
    callback that allocates a large amount of memory in one call (not driven by VM instructions) is not
    observed by this hook either; host bindings must still bound their own worst-case allocations, the
    same caveat that already applies to the wall-clock guarantee documented on `LuaCsExecutionGuard`.
- **Two budget families.** A one-shot chunk (`execute_lua`, the AI envelope), each `hooks_on` /
  `hooks_every` handler call and each resume of a mod's main chunk run under a handler-sized budget:
  10 s and 50,000,000 instructions (a mod's budget is set by `LuaCsModStackOptions.HandlerTimeoutMs` /
  `HandlerMaxSteps`; a one-shot chunk always uses these defaults); a logic-slot override gets
  200 ms / 200,000 instructions. Rbx signal handlers and
  `task.*` threads run under the per-resume budget below, and a raw `coroutine.resume` under a multiple
  of it.
- **A long chunk can hold the host frame.** The handler budget allows ~10 s of execution, which on a
  single-threaded player (WebGL) freezes the page for that whole time. `LuaCsGameToolExecutor` has a
  frame-yielding path (`IScriptEngine.RunChunkAsync` / `LuaCsExecutionGuard.ExecuteAsync`): the guard
  hook releases the frame through an `IScriptFrameYielder` every few milliseconds and the yielded time
  is excluded from the wall-clock budget. Only the plain `ILuaExecutor.ExecuteAsync(code, token)` seam
  in a world without a world ACL takes it. The actor-scoped and mutation-envelope overloads — which the
  shipped `execute_lua` tool always uses, and which the plain seam switches to in an ACL-versioned
  world (the default) — run the chunk synchronously, because `InstanceRegistry.ApplyMutation` runs its
  operation under a monitor and under an ambient `MutationEnvelopeScope` held in a registry field;
  yielding there needs an execution-scoped envelope and an async mutation protocol first. The other
  synchronous entries (mod events, timers, handlers) never arm a yielder: their caller is blocked on
  the result, so awaiting a frame from inside the hook could not complete.
- **The per-resume budget is the game's.** Every guarded scheduler-thread resume (a signal handler, a
  `task.*` thread) arms an instruction-step cap and a wall-clock cap read from one
  `LuaCsCoroutineBudgetSettings` — CoreAI's defaults are `LuaCsCoroutineHandle.DefaultBudgetPerResume`
  (10,000 steps) and `DefaultResumeTimeoutMs` (500 ms) — which the host sets as a serialized field on
  `CoreAiModsLifetimeScope`. Non-positive values fall back to the defaults instead of disabling the
  guard. Every such site resolves the same instance and every resume re-reads it, so a change reaches
  handles built long before. A raw coroutine's resume budget is derived from the same live settings
  (`RawCoroutineResumeStepBudgetMultiplier` 50 and `RawCoroutineResumeTimeoutMultiplier` 2: 500,000 steps
  and 1 s at the defaults); the one-off `execute_lua` executor and the AI envelope processor share the
  instance, so a tightened budget constrains coroutines inside admin- and AI-issued chunks as much as
  inside loaded mods. A mod's main chunk keeps the handler budget it was loaded with.
  `ScriptContext:SetTimeout(seconds)` moves the wall-clock half live and is gated to the unrestricted
  host actor (`NOT_AUTHORITY` for an ordinary mod); the instruction half has no Lua-facing setter. A
  trip is classified by a typed trip kind and reported as `BUDGET_EXCEEDED` with the bound and the
  author's line.
- **Scheduler threads have no lifetime cap.** A mod's main chunk, its `task.*` threads and its signal
  handlers are limited per resume only, as in Roblox: a thread may run for the whole session as long as
  every resume yields in time (`LuaCsCoroutineHandle.UnlimitedLifetimeSteps`). A lifetime step cap
  (`DefaultTotalLifetimeSteps`, `1_000_000` across all resumes) remains only on a
  `LuaCsCoroutineHandle` a host constructs directly, and exhausting it fails the resume loudly with
  `EXCEEDED_LIFETIME_STEP_BUDGET` instead of killing the thread in silence.
- **String patterns are budgeted per call.** `string.find`/`match`/`gmatch`/`gsub` run on a port of
  Luau's matcher with a step counter — 5,000,000 steps per call (`MaxPatternMatchSteps`), then
  `EXCEEDED_PATTERN_STEP_BUDGET` — and `gsub`/`string.format` results are capped at 1,000,000 characters
  before the string is built, so a single backtracking pattern cannot run for seconds inside one resume.
- **No yield through a library function that called back into Lua** (Luau parity). A yield inside a
  `__tostring` run by `tostring`, `print`, `warn` or `string.format`, a `table.sort` comparator, a
  `__pairs`/`__ipairs` metamethod, a `gsub` replacement function or a `gsub` `__index` raises
  `attempt to yield across a C-call boundary` (`YieldAcrossCallBoundaryMessage`, Luau's "attempt to
  yield across metamethod/C-call boundary"), and the thread that tried runs on; a yield across `pcall`
  still works, as in Luau. The refusal holds even after the callback resumed a coroutine of its own. A
  callback that merely runs long is not a yield: under a frame-yielding run the call is awaited and
  completes, and a counted call suspended that way allocates nothing per suspension. A coroutine whose
  body IS `coroutine.yield`, or ends in `return coroutine.yield(...)`, cannot be suspended by Lua-CSharp
  (no Lua function below the yield); its resume fails with that same line and the fix
  (`local r = coroutine.yield(...) return r`), which differs from Luau.
- `coroutine.wrap` is removed from the secured environment (its resumer would bypass the guard hook);
  `coroutine.resume` is replaced by a budget-guarded wrapper that arms the per-resume step, time and
  allocation limits on the coroutine's own state. The allocation limit is the budget of the run that
  resumes the coroutine — a mod's `HandlerMaxAllocatedBytes` for its handlers, task threads and main
  chunk — and a coroutine it resumes in turn inherits it.
- **A nested run borrows from the run it is nested in.** A raw coroutine under `coroutine.resume`, a
  scheduler thread `task.spawn` runs at once, and a guarded call that starts inside a run — re-entering
  the same state, or a `mods_call` export on another mod's state — each get their own budget or what
  the enclosing run has left, whichever is smaller, for steps, time and memory alike; the steps they use
  are charged back to the enclosing run, and when a limit they borrowed runs out the whole chain ends
  with the enclosing run's trip line, which no `pcall` or `xpcall` inside it can catch. So a raw
  coroutine inside a task thread is held to that thread's remaining 10,000 steps / 500 ms instead of its
  own 500,000 / 1 s, a spawned child that exhausts a lent allowance also ends its spawner, a
  memory-heavy immediate `task.spawn` in a main chunk can fail the load, and a runaway `mods_call` export
  ends its caller, as a Roblox module call runs in its caller's thread. **The enclosing run** is the
  innermost guarded run *executing* on the current OS thread, whichever Lua state or thread it belongs
  to (`LuaCsGuardedRun` in `LuaCsExecutionGuard.cs`): Lua runs on one OS thread, so the run executing
  when a new one starts is physically below it on the native stack. The runs begun on a thread are kept
  in a list, not a stack, because an `execute_lua` chunk parked in a frame yield lets other runs begin
  and end on the same thread: a parked run reports itself as not executing and is skipped, and a run
  that resumes executing moves to the end of the list. A resume the scheduler drives from its own frame
  finds no executing run and keeps its full budget. **Steps reach every ancestor:** what a nested run
  charges includes what its own nested runs charged to it, and `ConsumedSteps` and the guard's
  observability record count them once.
- **`coroutine.resume` never touches a scheduler-owned thread.** A task thread, a signal runner or a
  mod's main chunk belongs to its `LuaCsCoroutineHandle`, which runs it with the handle's own token for
  life; the wrapper looks the thread up (`LuaCsCoroutineHandle.IsHandleThread`) and refuses it before
  arming anything, returning `false` and "cannot resume a task or signal-handler thread with
  coroutine.resume; …" (`SchedulerThreadResumeRefusal`). The wrapper names itself in a bad-argument line
  as the native one does (`resume` when called from Lua, `coroutine.resume` when called from host code,
  as in `pcall(coroutine.resume)`; the same for `string.rep`, `string.find` and the other replaced
  functions). WHY: resumed raw, such a body would run under a hook whose trip cancels a token the body
  never reads, so a trip could not stop it, and the thread would die with live registrations on the
  handle's token. The rule this keeps: no mod code runs with a token other than the one its own hook
  cancels.
- **Calls from library functions back into Lua share one cap of 128 weighted levels per chain of nested runs**
  (`LuaCsSecureEnvironment.MaxCCallDepth`, Luau's `LUAI_MAXCCALLS` scaled to native frame size). A call whose
  native frames take up to about 4 KB opens one level (`LightCallLevels`): a function run by `pcall` or `xpcall`
  (whose message handler runs inside the same call). One that takes up to about 8 KB opens two
  (`HeavyCallLevels`): a `gsub` replacement function, a `__tostring` run by `tostring`, `print`, `warn` or
  `string.format` (a mod's own `tostring` included, so `tostring = warn; warn(1)` stops at the cap instead of
  overflowing the .NET stack and ending the process), a `table.sort` comparator, a `gsub` `__index`, a
  `__pairs`/`__ipairs` metamethod, a coroutine run by `coroutine.resume`, a scheduler thread `task.spawn` runs
  at once and a guarded call that starts inside a run (re-entering the state, or a `mods_call` export on
  another). So `pcall` nests 128 deep and `table.sort`, `tostring`, `print`, `pairs` or `ipairs` 63. A thread
  run inside a run continues the count of the run it starts in, so a chain of nested runs is counted as a
  whole across states. The call that would pass the cap raises `C stack overflow (<boundary>: more than 128
  levels of nested calls from library functions back into Lua)` (`CStackOverflowMessage`), an ordinary error:
  a `pcall` past the cap returns `false` and the line, and an `xpcall` hands it to its handler, which gets an
  eighth more room before "error in error handling". Plain Lua recursion that fills the engine's own value
  stack is caught the same way: `pcall` returns `false` and the engine's `stack overflow` line, and an
  `xpcall` runs its handler with that line. `pcall` and `xpcall` are counted, native calls: the caller's
  context, the same error values, no allocation on the success path, and a budget trip still uncatchable.
  WHY 128 weighted levels: each of these calls is a nested VM run on the .NET stack, and the only other bound,
  `RuntimeHelpers.TryEnsureSufficientExecutionStack` inside Lua-CSharp, may be a constant `true` on IL2CPP,
  where a deep enough chain would end the process. The weights keep the worst chain of any mix under
  128 × 4 KB = 512 KB of native stack on CoreCLR, plus the extra room an `xpcall` handler gets; Mono's JIT lays
  out frames about 2.8 times larger, so a chain at the cap takes up to about 1.35 MB there
  (`NativeStack_EveryChannelStaysWithinItsWeight_AtTheCap` in `LuaCsSecureSandboxEditModeTests` measures every
  channel at the cap on a 16 MB thread). On the Unity editor's main thread Mono's own stack check comes first:
  measured in Unity 6000.3.14f1
  (`NativeStack_OnTheCallingThread_EveryChannelEndsInACatchableLine_AndGivesItsLevelsBack`), every channel
  stops after 24-39 levels with a line `pcall` catches (`stack overflow`; an `xpcall` whose handler overflows
  too ends with `error in error handling`) and gives every level back, so a mod in the editor nests `pcall`
  about 39 deep, not 128; the tests that pin the cap itself run on a 16 MB thread. IL2CPP and WebGL are not
  measured yet. WHY a cap at all: an error raised N levels deep is rethrown once per level with a growing
  stack trace, so unwinding costs about N² with no instruction running and no hook able to fire. Plain Lua
  recursion and the metamethods the VM runs in its own loop are not counted. Not counted yet: `__concat`,
  which the VM calls as a nested run where no sandbox code sits (the guard hook can only end such a run, not
  raise a catchable error), and host callbacks other than `warn` that re-enter Lua outside
  `CallCountedAsync`; a composition cannot configure the cap.
- `execute_lua` (`LuaCsGameToolExecutor`) and `LuaCsAiEnvelopeProcessor` normalize and truncate results:
  the result summary is capped at **4,000 characters** and error messages are normalized and capped at **500 characters** (`LuaCsAiEnvelopeProcessor.MaxResultSummaryLength` / `MaxErrorMessageLength`) before they reach the model or the repair path.
- **An error value is one line, never a CLR dump.** `LuaCsApiRegistry` (and the Rbx bindings) turn a
  failing host callback into a `LuaCsHostFunctionException` whose error value is exactly `name:
  message` — on the Rbx surface the `[mod:<id> script:<path> line:<n>] CODE: message | fix: ...` line.
  `pcall`, `xpcall` and a protected `coroutine.resume` all receive that same string, with no CLR type
  name, managed stack trace or absolute source path. The sandbox's own refusals (the `string.rep`,
  `table.concat` and `string.format` caps, a `gsub` result cap) raise the same kind of error at level 0,
  so `pcall`, `xpcall` and `coroutine.resume` read the same line for them too. Every refusal and trip
  line the sandbox itself raises starts with `sandbox: ` (`LuaCsSecureEnvironment.SandboxLinePrefix`);
  `table.concat` names Lua types (`table`, not a CLR type); and a bad argument to a sandbox library
  wrapper reads as Lua's, with one closing parenthesis and `got no value` for a missing one
  (`bad argument #1 to 'rep' (string expected, got no value)`; a bad-argument line Lua-CSharp itself
  builds, such as native `xpcall`'s `got nil))`, still ends in two). A trip of the guard's step, time or
  memory budget carries its own one-line error value of the same kind — `sandbox:
  EXCEEDED_HARD_LIMIT_STEPS (<steps>)`, `sandbox: EXCEEDED_MEMORY_BUDGET (<bytes> bytes)`, `sandbox: Lua
  exceeded <N> ms.`, and for a coroutine resume `sandbox: Lua coroutine resume exceeded <N> ms.` — which
  is the line a host sees; unlike the refusals above, no script ever catches it (next item). Code
  classifies a trip by the type of its cause, never by this text: `LuaCsExecutionGuard.IsStepBudgetTrip`
  (`LuaStepBudgetException`, marker `StepBudgetTripMarker`), `IsMemoryBudgetTrip`
  (`LuaMemoryBudgetException`) and a `TimeoutException`. C# code reaches the original exception through
  `HostException`, which engine-neutral code reads via `IScriptHostFailure` /
  `ScriptExecutionErrors.NextCause` (the memory-trip classifiers walk the chain that way). A Lua error
  raised inside a registered delegate crosses unchanged. The one path left unwrapped is a raw
  `LuaFunction` registered through `RegisterCallback(string, LuaFunction)`, which no production code uses.
- **A budget trip is final and cannot be caught.** A trip of the guard's step, time or memory budget,
  of a scheduler thread's per-resume budget or of a raw coroutine's per-resume budget ends the run
  it tripped in: `pcall` and `xpcall` inside that run let it through, `xpcall`'s handler does not run,
  and the state stays guarded for every later run. Only the host sees the trip line — or, for a raw
  coroutine, the code that called `coroutine.resume`, which gets `false` plus the line while the
  coroutine is dead. Mechanism: Lua-CSharp's per-instruction hook sets `LuaState.IsInHook` and clears it
  only when the hook returns normally, so a hook that threw would leave every later hook on that state
  silent and the next runaway unguarded. The hook therefore records the trip, cancels the token the run
  executes with and returns; the VM raises the cancellation itself at its next jump, loop or call,
  `pcall`/`xpcall` do not catch a cancellation, and the guard's entry points turn it back into the trip's
  one-line `LuaCsHostFunctionException` (the typed cause in `HostException`). Each raw coroutine has its
  own trip source for its whole life. A hook that fires inside host code running Lua with its own token
  still has to throw; `EndGuard` then clears the stuck flag through the public `DebugLibrary.SetHook` (no
  reflection). Sandbox cap refusals and the per-call string-pattern step refusal stay ordinary errors
  that `pcall` catches. This relies on Lua-CSharp internals verified on .NET 8; the Mono/IL2CPP check
  is still open.
- `coreai_world_load_scene` supports an optional scene whitelist check. (This is one of the classic
  build bindings: in the default production composition it is **disabled** — a stub raises an error
  pointing at the Rbx API — and the whitelist only matters on hosts that opt into the build bindings.)
- The classic world build bindings (same opt-in caveat) validate coordinate inputs before touching state:
  coordinates must be finite and `abs(value) <= 100000`. The always-available `coreai_world_raycast`
  query also rejects non-finite arguments.
- `time_set_scale` validates input: `NaN`/`Infinity` are rejected and valid scales are clamped to `[0, 10]`.
- `coreai_world_play_sound` (opt-in build binding, as above) clamps volume to `[0, 1]`.

## Platform Support

When `COREAI_LUA` is compiled in, runtime Lua execution is supported on all platforms,
including WebGL player builds. On WebGL, execution is **on by default** — toggle with
`CoreAISettingsAsset.EnableLuaOnWebGl`. CoreAI has no WebGL-specific switch for the Full `unity_*`
reflection tier: it follows the host's **Enable Full Lua Access** grant on every platform, and in an
IL2CPP player it reaches only the members stripping kept. IL2CPP stripping protection (a `link.xml`
preserving `Lua.dll` / `Lua.Annotations.dll` and the CoreAI Lua assemblies) ships in the
`com.neoxider.coreaiunity` package; see [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md#webgl--il2cpp-stripping-requirements).

## Recommended Flow

1. Register only the bindings needed for the current scene or game mode.
2. Validate every binding argument in C# before touching game state.
3. Run the script through timeout and instruction limits.
4. Return a compact structured result to the LLM.
5. Persist only host-approved state changes.

## Removed Or Restricted APIs

The sandbox must not expose APIs that allow file, process, reflection, or runtime
escape by default:

- `io`, `os`, `debug`, `package`, `require`, `load`, `loadstring`, `loadfile`, `dofile`,
  `collectgarbage`, and `string.dump`.
- The stock `os` library stays removed. With the Rbx API attached (the default Unity composition), `os`
  is a CoreAI table that holds only `os.time` and `os.clock`.
- Arbitrary CLR/Unity reflection entry points.
- Direct filesystem, networking, shell, or environment access.
- Host object references that expose broad mutable state without a narrow wrapper.

If a game needs one of these capabilities, wrap it in a purpose-built C# binding
with explicit validation and tests.

## Execution Limits

Every script path should have bounded execution:

- Instruction step budget for CPU-bound loops (per resume, game-configurable — see the hardening list above).
- Timeout/cancellation token for host-driven async flows.
- Coroutine lifecycle ownership, including cleanup on scene unload and game reset.
- Per-agent or per-session rate limits when scripts can be generated repeatedly.

The host should treat a timeout as a failed script execution and return a compact,
structured error to the LLM instead of retrying indefinitely.

## Binding Rules

Prefer small, deterministic bindings:

- Use narrow method names such as `spawn_prefab`, `set_stat`, or `award_item` (illustrative host
  bindings, not CoreAI built-ins).
- Validate all ids, enum values, numeric ranges, positions, layers, and ownership.
- Return structured results: `{ ok = true }` or `{ ok = false, error = "..." }`.
- Make mutating calls idempotent when practical.
- Keep authority in C#; Lua requests changes, C# decides whether they are legal.

Avoid passing Unity objects directly into Lua. Use ids or handles, then resolve and
validate them in C#.

## Binding Example

Prefer a small verb that maps to a validated host operation (`spawn_prefab` is a hypothetical host
binding):

```lua
spawn_prefab("training_dummy", 4, 0, 12)
```

The C# binding should still check the prefab id, scene permissions, position,
collision rules, budget limits, and ownership before spawning anything. Lua gets a
clean result; the host keeps authority:

```json
{
  "ok": true,
  "object_id": "dummy_042"
}
```

When validation fails, return a result the model can repair:

```json
{
  "ok": false,
  "error": "blocked_spawn_position",
  "message": "The requested position overlaps a non-trigger collider."
}
```

## Known Attack Vectors To Test

Maintain EditMode tests for attempts to:

- Access `io`, `os`, `debug`, `package`, `require`, `loadfile`, or `dofile`.
- Reconstruct globals through `_G`, `_ENV`, metatables, or debug-style helpers.
- Use `string.dump`, coroutine APIs, or garbage collection as escape/timing probes.
- Run infinite loops, deep recursion, or huge table allocations.
- Call host bindings with invalid ids, extreme numbers, NaN/Infinity, or oversized
  strings.
- Reuse stale object handles after scene reload or despawn.
- `string.rep` repeat-capping behavior (`MaxStringRepLength`) and malformed `package` access checks.
- `table.concat` output capping (`MaxTableConcatLength`) and plain-concatenation allocation bombs
  (`s = s .. s` doubling) hitting the total per-execution allocation budget (`EXCEEDED_MEMORY_BUDGET`).
- `pcall` loop recursion and unbounded call-stack behavior.
- A directly constructed coroutine handle that exceeds its total lifetime budget, and a scheduler
  thread that loops forever while yielding (it must keep running).
- `pcall`/`xpcall`/`coroutine.resume` of a failing host call and of a sandbox cap: the error value must
  be the one line, with no CLR type name, stack trace or path; a guard trip's error text likewise.
- A budget trip (steps, time, memory; the guard, a scheduler thread's resume, a raw coroutine's resume)
  wrapped in `pcall`/`xpcall` inside the tripped run: it must end the run, `xpcall`'s handler must not
  run, a later runaway on the same state must trip again, and an ordinary Lua error must still be
  caught (`LuaCsGuardFrameAndAllocationEditModeTests`, `LuaCsSecureSandboxEditModeTests`,
  `LuaCsModRuntimeEditModeTests`).
- `coroutine.resume(coroutine.running())` from a task, a signal handler or the main chunk, with a
  runaway inside an `xpcall`: refused before the thread is touched, nothing runs unguarded, and the
  thread still ends safely on kill or unload (`RbxTaskSchedulerLuaBindingsEditModeTests`,
  `LuaCsSecureSandboxEditModeTests`).
- Library calls back into Lua nested past the 128-level weighted cap (`pcall`, `xpcall`, `table.sort`,
  `tostring`, `print`, `string.format`, `gsub`, `pairs`, `ipairs`, `coroutine.resume`, an immediate
  `task.spawn`, and mixes of them): one catchable line, fast; deep plain recursion still allowed; a raw
  coroutine and an immediate `task.spawn` held to what their resumer has left
  (`LuaCsGuardFrameAndAllocationEditModeTests`, `LuaCsSecureSandboxEditModeTests`).
- Nested runs across states and threads: a guarded call from a raw coroutine, a `mods_call` to another mod or
  back to the same mod from a coroutine, and a run resumed from a frame yield inside another run — each
  nested in the innermost executing run, its steps reaching every ancestor, no `xpcall` handler running
  after a trip, and the call count continuing across hops (`LuaCsGuardFrameAndAllocationEditModeTests`,
  `LuaCsSecureSandboxEditModeTests`); a yield through every counted call refused while the thread runs on.
- A signal handler that replaces `coroutine.yield`: the signal runners park with the native yield
  captured before any mod code ran, so a mod's override cannot break every `Heartbeat` handler
  (`LuaCsRbxSignalRunner`).
- World binding validations for NaN/Infinity and coordinate bounds (`|value| <= 100000`).
- Rate-limit behavior for `execute_lua` and repair-generation lockout.

## Error Handling

Lua errors should be normalized before they reach the model:

- Include the failed command name and a short error code.
- Avoid dumping full stack traces into prompts by default.
- Do not include secrets, local paths, or server-only implementation details.
- Give repairable errors enough context for one corrective retry.

Example:

```json
{
  "ok": false,
  "error": "invalid_prefab_id",
  "message": "Prefab id 'dragon_boss' is not registered for this scene."
}
```

## Host Checklist

- Register only the bindings required for the current game mode.
- Keep all mutating operations behind C# validators.
- Run sandbox escape tests when changing Lua runtime setup.
- Run PlayMode tests for coroutine execution, cancellation, scene reload, and reset.
- Document each custom binding with ownership, inputs, outputs, and failure modes.

## Reader Checklist

After reading this guide, you should be able to answer four questions for every
Lua binding:

- Who owns the authority for this operation?
- Which inputs can be hostile or malformed?
- What happens on timeout, cancellation, scene unload, or reset?
- What compact error should the LLM receive when the action is denied?

## Related docs

- [LUA_NATIVE_APIS.md](LUA_NATIVE_APIS.md) — which Lua-CSharp features CoreAI uses natively vs custom wrappers.
- [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) — integration do's and don'ts.
- [LUA_GAME_API.md](LUA_GAME_API.md) — game API reference.
