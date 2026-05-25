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
    (string actor_id) => ...);
```

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

## Duplicate Calls

Keep duplicate suppression on unless the tool is intentionally repeatable.

Set `AllowDuplicates = true` only when repeated calls are meaningful and safe, such
as querying different pages, rolling multiple independent samples, or streaming
progress through a host-controlled protocol.

For mutating tools, duplicates should usually return a structured no-op result:

```json
{
  "ok": true,
  "duplicate": true,
  "message": "Action already applied."
}
```

## Error Rules

- Use machine-readable `error` codes.
- Include only the fields needed for repair.
- Do not return stack traces, secrets, local file paths, or provider responses.
- Do not hide domain failures as successful prose.

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
