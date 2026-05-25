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
