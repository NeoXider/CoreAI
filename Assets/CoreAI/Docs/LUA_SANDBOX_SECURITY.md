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

## Optional Module (`COREAI_LUA`, positive opt-in since v7.0.0)

Lua is an **optional module**. Defining the scripting symbol `COREAI_LUA`
(Project Settings → Player → Scripting Define Symbols) positively opts the target
into the Lua-CSharp runtime and guarded Lua surfaces. It is independent of `COREAI_LLM`:
Lua-only and LLM-only builds are supported, both symbols enable the full runtime, and no
symbols produce the minimal portable core. Lua ships bundled as
`Lua.dll` / `Lua.Annotations.dll` inside the CoreAI Mods package
(`Assets/CoreAIMods/Plugins/`) — there is no external package dependency to
remove.

When `COREAI_LUA` is set:

- guarded Lua demos, adapters, benchmark paths, and sandbox security tests compile in;
- `CoreAiModsLifetimeScope` may register the bundled runtime and expose the host-granted Lua APIs;
- the sandbox escape fixture executes in CI and is required to report test cases.

Default builds leave the symbol unset and therefore keep Lua disabled. This is a breaking v7.0 migration:
remove the former negative opt-out symbol from project settings and add `COREAI_LUA` only to targets that
deliberately enable scripting.

### Unity scene module

Runtime Lua/world-command scene permissions are owned by an optional `CoreAiLuaWorldModule` child of
`CoreAILifetimeScope`. The module contains the prefab whitelist, scene whitelist, Full-tier grant, and
private-member grant. This keeps security-sensitive Lua configuration out of the root CoreAI Inspector
unless the host deliberately adds the module. Existing scenes with the former flat fields migrate without
losing serialized values; new scenes should configure only the child module.

### CI matrix

`.github/workflows/ci.yml` runs EditMode tests in four configurations on every
push/PR: `core` (no optional symbols), `llm` (`COREAI_LLM`), `lua` (`COREAI_LUA`),
and `full` (both). The `lua` and `full` jobs additionally assert that the
`LuaCsSecureSandboxEditModeTests` escape-test
fixture actually executed, so isolation coverage cannot silently drop out of
the suite. The workflow needs the standard GameCI secrets (`UNITY_LICENSE`,
`UNITY_EMAIL`, `UNITY_PASSWORD`) configured in the repository.

## Generation Rate Limit

Lua generation is constrained at multiple stages:

- `LuaAiEnvelopeProcessor` enforces a sliding-window limiter (`LuaGenerationRateLimiter`, default 20 per 60 s) for envelope runs and scheduled Programmer repair generations.
- `LuaTool`/`execute_lua` also enforces rate limiting in the tool path. The
  envelope and `execute_lua` share a limiter when the same limiter is injected,
  so a busy model or repair loop is blocked consistently across both layers.

`LuaGenerationRateLimiter` is default-on. `maxPerWindow <= 0` still disables it.

When the limiter is saturated, the envelope fails with `Lua rate limit exceeded` and repair is skipped, so a failing script cannot spin a runaway generate→fail→repair loop against the LLM.

## Additional hardening (current implementation)

- `string.rep` is now capped via a replaced implementation in `SecureLuaEnvironment`.
  `SecureLuaEnvironment.MaxStringRepLength` is `1_000_000`; attempts that exceed this
  limit fail fast with an explicit error.
- `table.concat` is capped the same way (`MaxTableConcatLength`, same `1_000_000` value): the
  replacement mirrors the real `table.concat` algorithm for both VMs but aborts as soon as the
  in-progress result exceeds the cap, instead of finishing a potentially huge build first.
- **Total per-execution allocation budget (default 64MB, F-08).** `string.rep`,
  `string.format`, and `table.concat` are capped at their library call site, but plain string
  concatenation (`s = s .. s`) is ordinary VM opcodes with no call site to intercept — a ~1MB allowed
  string can be doubled repeatedly into hundreds of MB well within the instruction budget. The
  per-instruction hook (`LuaCsExecutionGuard`) now also samples `GC.GetAllocatedBytesForCurrentThread()`
  against a baseline captured at the start of the guarded call and aborts with
  `EXCEEDED_MEMORY_BUDGET` once the delta exceeds the budget. This check runs on **every** instruction,
  not on a coarse sample interval: concatenation doubling grows exponentially, so a handful of loop
  iterations can jump from megabytes to gigabytes, and any sampling interval wide enough to matter for
  performance is also wide enough to either miss the attack or let the runtime approach a real
  out-of-memory condition before the next sample point. `GC.GetAllocatedBytesForCurrentThread()` is a
  thread-local counter read (not a GC pass), the same cost class as the wall-clock check already
  performed unconditionally on the same hot path, so this adds no new performance risk. Configure the
  budget via the `maxAllocatedBytes` constructor parameter on `LuaCsExecutionGuard`, or
  `LuaCsSecureEnvironment.MaxAllocatedBytesBudget` for the default.
  - **What this does not cover:** the budget is a total-allocation backstop, not a live heap-size cap —
    a script that allocates and discards memory in a tight loop can still cause GC churn without
    tripping it (GC pauses are bounded by the existing timeout instead). A single host callback that
    allocates a large amount of memory in one call (not driven by VM instructions) is not observed by
    this hook either; host bindings must still bound their own worst-case allocations, the same caveat
    that already applies to the wall-clock guarantee documented on `LuaCsExecutionGuard`.
- Coroutine abuse has a total-lifetime budget through `LuaCoroutineHandle`.
  `LuaCoroutineHandle.DefaultTotalLifetimeSteps` is `1_000_000` across all resumes
  for one handle, and the handle is forcibly killed when exceeded.
- `LuaAiEnvelopeProcessor` normalizes and truncates results:
  result summary is capped at **4,000 characters** and error messages are normalized and capped at **500 characters** before entering the payload/repair path.
- `LuaCsApiRegistry` wraps host callbacks and converts host validation exceptions into `LuaRuntimeException`, so Lua callers see script errors instead of raw CLR exception types.
- `coreai_world_load_scene` supports an optional scene whitelist check.
- World bindings validate coordinate inputs before touching state:
  coordinates must be finite and `abs(value) <= 100000`.
- `time_set_scale` validates input: `NaN`/`Infinity` are rejected and valid scales are clamped to `[0, 10]`.
- `play_sound` clamps volume to `[0, 1]`.

## Platform Support

When `COREAI_LUA` is compiled in, runtime Lua execution is supported on all platforms,
including WebGL player builds. On WebGL, execution is **on by default** — toggle with
`CoreAISettingsAsset.EnableLuaOnWebGl`. The Full `unity_*` reflection tier
stays disabled on WebGL; IL2CPP stripping protection (`link.xml` preserving
`Lua.dll` / `Lua.Annotations.dll`) ships in the package.

## Recommended Flow

1. Register only the bindings needed for the current scene or game mode.
2. Validate every binding argument in C# before touching game state.
3. Run the script through timeout and instruction limits.
4. Return a compact structured result to the LLM.
5. Persist only host-approved state changes.

## Removed Or Restricted APIs

The sandbox must not expose APIs that allow file, process, reflection, or runtime
escape by default:

- `io`, `os`, `debug`, `package`, `require`, `loadfile`, and `dofile`.
- Arbitrary CLR/Unity reflection entry points.
- Direct filesystem, networking, shell, or environment access.
- Host object references that expose broad mutable state without a narrow wrapper.

If a game needs one of these capabilities, wrap it in a purpose-built C# binding
with explicit validation and tests.

## Execution Limits

Every script path should have bounded execution:

- Instruction step budget for CPU-bound loops.
- Timeout/cancellation token for host-driven async flows.
- Coroutine lifecycle ownership, including cleanup on scene unload and game reset.
- Per-agent or per-session rate limits when scripts can be generated repeatedly.

The host should treat a timeout as a failed script execution and return a compact,
structured error to the LLM instead of retrying indefinitely.

## Binding Rules

Prefer small, deterministic bindings:

- Use narrow method names such as `spawn_prefab`, `set_stat`, or `award_item`.
- Validate all ids, enum values, numeric ranges, positions, layers, and ownership.
- Return structured results: `{ ok = true }` or `{ ok = false, error = "..." }`.
- Make mutating calls idempotent when practical.
- Keep authority in C#; Lua requests changes, C# decides whether they are legal.

Avoid passing Unity objects directly into Lua. Use ids or handles, then resolve and
validate them in C#.

## Binding Example

Prefer a small verb that maps to a validated host operation:

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
- Coroutines that exceed total lifetime budgets.
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
