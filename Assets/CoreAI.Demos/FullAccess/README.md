# CoreAI — Live Full Access Demo

Demonstrates the **Full tier** (`LuaCapabilities.Full`): through the Programmer role the LLM
can reach arbitrary scene `GameObject`s and components via reflection bindings
(`unity_find`, `unity_set_position`, `unity_set_member`, `unity_get_member`,
`unity_list_components`, `unity_call`) and scene helpers (`unity_list_objects`,
`unity_find_by_component`, `unity_describe_object`, transform/hierarchy setters), when
**Enable Full Lua Access** is on for the `CoreAILifetimeScope`.

The scene `FullAccessDemo.unity` ships ready to run: the controller auto-creates a
`TargetCube`, Full access is enabled on the scope, and the prompt buttons load real mods.

## Member visibility (public by default)

Full access is split by member visibility:

- **Default** — reflection exposes only **public** fields, properties and methods. Private
  internals stay hidden.
- **Opt-in** — turn on **Enable Full Lua Private Access** on the `CoreAILifetimeScope` to
  additionally reach non-public members. This is a strictly stronger grant; leave it off
  unless a tool genuinely needs private state.

(The split is implemented in `CoreAiFullUnityLuaRuntimeBindings`; the DI flag is
`enableFullLuaPrivateAccess`, wired through `WorldCommandsInstaller.RegisterWorldCommands`.)

## Requirements

- MoonSharp present, `COREAI_NO_LUA` not defined.
- LM Studio / OpenAI-compatible endpoint in `Resources/CoreAISettings`.
- On the `CoreAI` scope: **Enable Full Lua Access = true** (already set in the demo scene).

## Try it

Press Play and use the **Full Access mod prompts** buttons:

1. **Lift the cube** — `unity_find('TargetCube')` + `unity_set_position(id, 0, 2, 0)`.
2. **Grow the cube** — writes `Transform.localScale` via `unity_set_member`.
3. **Inspect the cube** — reads `Transform.position` via `unity_get_member`.

The Programmer prompt also includes a **Full Lua Mode** workflow: run a one-shot diagnostic
`execute_lua`, read `Success` / `Output` / `Error`, then load or reload a persistent mod if the
change needs hooks/timers.

### Example mod (what the AI writes)

```lua
-- name: Mover
-- description: Lifts TargetCube through Full reflection.
local id = unity_find("TargetCube")
if id ~= 0 then
  unity_set_position(id, 0, 2, 0)
end
report("TargetCube lifted to (0,2,0).")
```

### Scene inspection example

```lua
local matches = unity_find_all("Target", 10)
if #matches == 0 then return '{"found":false}' end
local desc = unity_describe_object(matches[1].id)
return '{"found":true,"path":"' .. desc.path .. '","children":' .. desc.child_count .. '}'
```

## Safety

Full access is **opt-in** and gated behind the Full capability tier. Public-only is the
default member surface (see above). The MoonSharp sandbox (no `io`/`os`/`load`),
instruction and time limits, and the auto-unload-on-repeated-errors policy still apply.
A type/member blacklist is available through `IFullLuaAccessBlacklistPolicy`; see
`Assets/CoreAI/Docs/LUA_ACCESS_MODES.md`.
