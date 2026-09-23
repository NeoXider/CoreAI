# Tool Calling Best Practices

This guide documents how to design CoreAI tools that are reliable, economical,
and safe to expose to LLM agents.

## The Core Rule

A good LLM tool should feel boring to the game code: narrow input, predictable
output, clear failure, and no hidden side effects.

The model can be creative in planning. The tool should be conservative in what it
accepts and precise in what it returns.

## Design Goals

Good tools should be:

- Narrow: one clear action or query per tool.
- Deterministic: same input produces the same domain result when game state is the
  same.
- Idempotent where practical, especially for paid/server-managed flows.
- Cheap in tokens: short names, compact schemas, and concise results.
- Repairable: errors tell the model what to fix without leaking internals.

## Tool Shape

Most CoreAI tools should fit one of three shapes:

| Shape | Example | Notes |
|---|---|---|
| Query | `get_inventory(actor_id)` | Read-only; safe to retry and cache when state has not changed. |
| Command | `equip_item(actor_id, item_id, slot)` | Mutates state; validate authority and duplicate behavior. |
| Planner helper | `find_crafting_recipe(item_id)` | Gives the model structured options without changing state. |

Avoid mixing these shapes. A tool named `get_best_weapon_and_equip_it` is harder
to test, harder to retry, and harder to explain to the player.

## Naming And Schema

Use stable snake_case names and compact parameter names:

```csharp
new DelegateLlmTool(
    "get_inventory",
    "Return item ids and counts for an actor.",
    new Func<string, string>(actor_id => ...));
```

Unity compiles C# 9, where a lambda has no natural type. `DelegateLlmTool` and
`AgentBuilder.WithAction` take a `System.Delegate`, so wrap the lambda in an explicit delegate type
(`new Func<...>(...)`, `new Action(...)`); a bare lambda fails with CS1660.

Prefer:

- `actor_id`, `item_id`, `qty`, `slot`, `x`, `y`, `z`.
- Enums or ids instead of free-form display names.
- Small optional fields with documented defaults.

Avoid:

- Long natural-language parameter names.
- Tools that accept arbitrary JSON blobs without validation.
- Outputting full game objects, full scene hierarchies, or large text dumps by
  default.

## Idempotency

Mutating tools should prevent duplicate side effects when the model retries or the
transport retries:

- Include a request/action id when an operation spends currency, grants items, or
  triggers billing-sensitive server work.
- Return the previous result for the same id instead of applying the action again.
- Make "set" operations preferred over "increment" operations when possible.

Example: `set_wave_modifier(id, value)` is easier to retry safely than
`increase_wave_modifier(delta)`.

CoreAI's resilience layer also guarantees retries never double-execute tools: a
failed completion whose turn already **executed** a tool call is not replayed by
the HTTP retry loop or the fallback provider chain.

Only calls the policy rejected **before** the invocation boundary count as
never-invoked and stay retry-eligible: an echo-suppressed duplicate, unparseable
argument JSON, an unknown tool name, a declared tool with no binding, a missing
required argument caught by schema validation, and an argument whose value cannot
be converted to the parameter type. The type check is a structural preflight: it
runs MEAI's own conversion for each argument before `AIFunction.InvokeAsync`, so a
refusal proves the body never ran. `call_skill_tool` applies the same missing-argument
and type checks to its target tool before binding. Everything that reached
`AIFunction.InvokeAsync` is treated as invoked — **including a binding failure the
preflight could not prove**, for example in a hand-written `AIFunction` with no
underlying .NET method. That boundary cannot prove the body was never entered, and
the previous attempt to prove it by reading the exception's stack frames is unusable
on IL2CPP/WebGL, where frames can be stripped: a body exception then looked like a
binding failure and the retry replayed a mutation that had already happened. The
conservative verdict costs a retry; the precise one cost correctness.

## Result Envelope

Use the same result vocabulary across tools where practical:

```json
{
  "ok": true,
  "items": [
    { "item_id": "iron", "qty": 5 }
  ],
  "warnings": []
}
```

For failures:

```json
{
  "ok": false,
  "error": "not_enough_materials",
  "missing": [{ "item_id": "iron", "qty": 2 }]
}
```

This keeps prompts smaller and lets policies reason about tool results without
parsing prose.

Keep results inside `CoreAISettings.MaxToolResultChars` (8000 by default). A longer
result is cut at that limit and the cut is marked for the model, but the tail is
gone — page or summarize inside the tool instead of relying on the truncation. See
[TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) for the two documented
edits the policy makes to a result.

## Duplicate Calls

Keep duplicate suppression on (`AllowDuplicates = false`, the default) for any tool
that mutates state. Set `AllowDuplicates = true` ONLY when repeated identical calls
are meaningful and safe — querying different pages, re-reading live state, rolling
independent samples, or streaming progress through a host-controlled protocol.

`ToolExecutionPolicy` enforces the following for `AllowDuplicates = false` tools,
matching how Claude/Cursor stay reliable — no duplicate world mutations from an
echoed or retried turn, while a legitimate retry is never blocked:

- **Cross-turn echo → structured no-op.** A call whose exact canonical
  `name(sorted-args)` signature already **succeeded** earlier in the same request is
  suppressed and answered with a no-op the model can understand:

  ```json
  {
    "ok": true,
    "duplicate": true,
    "message": "Duplicate tool call 'world_command' with identical arguments: this exact call already succeeded earlier in this request and was NOT executed again. Use its earlier result; do not repeat the call."
  }
  ```

- **Intra-turn repeats always execute.** Three identical `spawn tree` calls in ONE
  turn are a legitimate request and all run — signatures are compared only against
  earlier turns, never against sibling slots in the same turn.
- **Failed calls stay retryable.** A call's signature is registered **only after it
  succeeds**. A transient failure — including the failed slot of a partially
  successful batch — is never registered, so retrying exactly that call with
  identical arguments is always allowed.
- **An echoed turn is not a failed turn.** The no-op is `ok: true` and its trace is
  a SUCCESS with `source=duplicate`; suppressed slots take no part in the
  consecutive-error counter. A user asking "show that card again" three times in a
  row therefore cannot trip the max-errors guard and get the turn aborted. A model
  that echoes forever is bounded by the roundtrip cap, not by the error counter.

You do NOT need a tool-side idempotency key just to get echo suppression; the policy
provides it by signature. Keep an explicit request/action id (below) for
billing-sensitive or currency-spending work, where you want idempotency to survive
even across independent requests.

## Policy-Enforced Mutation Ordering

**Declare mutation on the tool.** A tool that writes to shared state — the world,
memory, files, a registry, a server — sets `IsMutating => true` (`ILlmTool` /
`LlmToolBase`), or `IsMutating = true` on a `DelegateLlmTool`. Every mutating call
of a turn shares ONE ordered serialization chain, so no two mutations ever overlap
and they apply in original call order even when `MaxParallelToolCalls > 1`.
Read-only tools (the default, `IsMutating => false`) still run fully in parallel.

The built-in names `world_command`, `component_command`, `execute_lua`,
`manage_mods`, `manage_skills`, `memory` and — conservatively — `call_skill_tool`
are recognized by name as well, so a host that registered them keeps the guarantee
without changing anything. That name list is **backward compatibility only**: it is
not an extension point, and a host must never patch the package source to add a
name to it. Your own mutating tool is invisible to the name list and will run
concurrently until it declares the flag.

In the **streaming** path a call executes the moment it ARRIVES, mutating or not.
The echo check runs synchronously at arrival and therefore **before any side
effect**: a per-call signature needs no knowledge of the rest of the turn, so an
echoed streamed mutation is answered with the no-op above without re-applying
anything. Mutating arrivals still join the turn's serialization chain and never
overlap each other; read-only arrivals run gate-bounded in parallel.

Leave `AllowDuplicates = false` on mutating tools. The two flags are independent:
`IsMutating` decides *ordering*, `AllowDuplicates` decides *echo suppression*, and
a tool that sets `AllowDuplicates = true` is exempt from the echo check even when
its name is one of the built-in mutating names above.

## Error Rules

- Use machine-readable `error` codes.
- Include only the fields needed for repair.
- Do not return stack traces, secrets, local file paths, or provider responses.
- Do not hide domain failures as successful prose.

**Required arguments: missing means absent or `null`.** A required argument counts
as missing only when its key is absent or its value is `null` / JSON `null`; such a
call is refused before the tool runs, with the expected schema in the error. The
required names come from the tool's `ParametersSchema` and from the bound
function's own schema (`AIFunction.JsonSchema`), so a tool that declares `{}` as its
metadata schema is still checked. An empty or whitespace-only string is a
**present** value and reaches your tool — validate it yourself when `""` is not
meaningful, and return a structured error instead of acting on it.

**`call_skill_tool` refuses before binding.** When the model calls a skill tool
through `call_skill_tool` with a missing required argument or an argument of the
wrong type (for example `"yes"` for a `bool`), the proxy refuses the call before the
tool is bound. The refusal names the tool and the argument, lists the expected
parameters, states that the tool was **not** executed, and shows how to retry; it
is traced as a schema-validation failure, so the turn may still be retried. Enum
names and JSON-object arguments bind exactly as MEAI binds them.

**Multi-function tools are called by function name.** A tool that implements
`IAIFunctionsLlmTool` (for example `camera` → `camera_capture` / `screenshot` /
`camera_look` / `camera_list`, or `scene_tool` → `find_objects` / `get_hierarchy` /
`get_transform` / `set_transform`) is exposed to the model as its functions. The
model calls those names, and each call runs under the wrapper's
`ToolTimeoutMsOverride`, `EndsTurn`, `IsMutating` and `AllowDuplicates`. Casing
repair covers the function names, the "Available tools" list in the prompt and in
refusals shows them instead of the wrapper name, and text-shaped calls to them are
extracted and stripped like any other tool call.

**Exceptions in `DelegateLlmTool` bodies never escape the pipeline.** If your
delegate body throws, `DelegateLlmTool` converts the exception into an
`"Error: ..."` tool result (matching first-party tools), so the model sees a
repairable error message and the turn continues instead of the request faulting.
Cancellation is the exception: `OperationCanceledException` from the caller's
token propagates. Treat this as a safety net, not the primary error channel —
still return structured `error` codes for expected domain failures.

## Roundtrip Limits

One **roundtrip** = one LLM call + one tool-execution batch. The cap (`MaxToolCallRoundtrips`,
default 20) stops runaway loops, but the right value depends on the agent role:

- **Tight caps for conversational NPCs** — a guard or merchant rarely needs more than a few tool rounds;
  `WithMaxToolCallRoundtrips(5)` keeps a misbehaving model from burning tokens.
- **Unlimited for builders and code agents** — a free-build visual agent that emits 24+ `spawn` calls, or a
  Programmer that iterates Lua (generate → run → read error → fix), should set
  `WithMaxToolCallRoundtrips(0)` so it is never cut off mid-task. The built-in **Programmer** and
  **Creator** roles already default to unlimited.

Set it per agent (`AgentBuilder.WithMaxToolCallRoundtrips`), per call (`AiTaskRequest.MaxToolCallRoundtrips`),
or globally (`CoreAISettings.MaxToolCallRoundtrips`); priority is per-call → per-agent → per-role policy (`AgentMemoryPolicy`, where the Programmer/Creator unlimited default lives) → global.

## SkillSet Organization

When a role has many tools, group them into `SkillSet`s:

- Put always-needed tools directly on the agent.
- Put domain-specific tools in skills, such as Crafting, Combat, Trading, Quests,
  or WorldEditing.
- Keep skill instructions procedural and short.
- Use `read_skill` + `call_skill_tool` to avoid sending every tool schema on every
  request.

This keeps the visible tool surface small and reduces prompt cost as the project
grows.

## Result Size

Large results are expensive and can break tool-call loops.

Prefer:

- Summaries plus ids for follow-up queries.
- Pagination or `limit` parameters.
- Short result envelopes with `ok`, `items`, `next_cursor`, and `warnings`.
- Host-side truncation through `MaxToolResultChars`.

Avoid returning full save files, full logs, or entire scene state unless the user is
explicitly in a diagnostics workflow.

## Bad Smells

Revisit a tool design when you see any of these:

- The description is longer than the implementation.
- The tool accepts a free-form command string and then parses it again.
- The result contains full objects when ids and summaries would work.
- The tool can spend currency, grant items, or call a paid backend without an
  idempotency key.
- The only failure mode is `"error": "failed"`.

## Result Contracts Per Action (world_command example)

A tool with many actions should tell the model what each action returns *in the
Description*, so it never has to spend a round-trip discovering the shape by
trial and error. `world_command` documents its own contract this way — one
line per action, `params -> result` — instead of prose, since the Description
is resent on every request:

```
spawn(prefabKey,targetName,x/y/z?,...) -> ok, echoes applied transform
spawn_batch(...,itemsJson) -> {ok,spawned,failed,names:[first few]}
list_prefabs() -> {prefabs:[...],primitives:[...]}
```

Guidelines that follow from this:

- **Batch actions return a summary, never an echo.** `spawn_batch` spawns up to
  100 objects from one call but reports only `{ok, spawned, failed, names}` —
  the first few names, not every item's transform back. If the caller needs to
  know exactly what was placed, follow up with `list_objects`.
- **An unknown-key error lists the valid keys.** When `prefabKey` does not
  resolve, the error names the available primitives and (truncated to ~20) the
  registered prefab keys, so the model can retry correctly in the same
  round instead of guessing or calling a separate discovery tool first.
- **Discoverability actions exist for expensive-to-guess inputs.**
  `list_prefabs` lets the model learn valid `prefabKey` values before spawning,
  the same way `list_animations` and `list_objects` let it learn valid
  `animationName`/`targetName` values.
- **Spawn meaningful hierarchies for compound objects.** Create a named `empty` root before its parts,
  then set the parent on posts, roofs, props and decorations. Parented coordinates are local by default;
  use `worldPositionStays=true` only when an existing world transform must be preserved. This costs no
  extra parent call when the parent is supplied during `spawn` or per `spawn_batch` item.

## Verification Checklist

For each new tool, add focused tests for:

- Valid request.
- Invalid ids and invalid enum values.
- Permission/authority denial.
- Duplicate call behavior.
- Cancellation/timeout when the implementation is async.
- Oversized output and truncation behavior when applicable.

For gameplay tools, also verify reset/reload behavior in PlayMode when the tool
touches scene objects, physics, UI, or persistent state.
