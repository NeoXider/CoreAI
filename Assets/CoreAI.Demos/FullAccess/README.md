# CoreAI — Live Full Access Demo

Demonstrates the **Full tier** (`LuaCapabilities.Full`): through the Programmer role the LLM
can reach arbitrary scene `GameObject`s and components via reflection bindings
(`unity_find`, `unity_set_position`, `unity_set_member`, `unity_get_member`,
`unity_list_components`, `unity_call`) and scene helpers (`unity_list_objects`,
`unity_find_by_component`, `unity_describe_object`, transform/hierarchy setters), when
**Enable Full Lua Access** is on for the `CoreAILifetimeScope`.

The scene `FullAccessDemo.unity` ships ready to run: Full access is enabled on the scope and the
prompt buttons load real mods. The scene starts with an **empty world** — nothing is auto-spawned.
The `unity_*` reflection examples below target a GameObject named `TargetCube`; assign one on the
`FullAccessHubDemoController` in the inspector (or have a mod spawn it) before running them.

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

- `COREAI_LUA` defined.
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

## Lua Platform Example (F6)

`LuaPlatformExampleController` (also on the `FullAccessDemo` scene) is a no-LLM reference for what a
Lua mod can do on its own: it writes and loads its Lua sources itself, no chat/model involved. Toggle
its panel with **F6**.

- **Run self-test** — loads a two-mod pair that checks timers, the `hooks_on('tick')` alias,
  variables/closures, varargs, coroutines, the `store_*` roundtrip, cross-mod events, and the
  `input_*` API, then reports a PASS/FAIL verdict per check.
- **Start/Restart/Stop Tetris** — loads a self-playing 3D falling-blocks game entirely in Lua:
  board state in tables, gravity/animation/camera on `hooks_every` timers, steering through the
  `input_*` API, and the score persisted in the mod store across restarts. The mod builds the
  playfield through the Rbx API (`Instance.new('Part')`, `workspace.CurrentCamera`) — the
  production surface, since `CoreAiModsInstaller` withholds the classic `coreai_world_*` build
  bindings (`RegisterWorldEditBuildBindings = false`). The bundled `sample_tetris3d` mod
  (Hub → Mods tab) is a second, pure-Rbx take on the same game.

WebGL builds can drive the same controller via `SendMessage("LuaPlatformExample", "RunSelfTest" |
"StartTetris" | "StopTetris" | "DumpStatus")`.

## Safety

Full access is **opt-in** and gated behind the Full capability tier. Public-only is the
default member surface (see above). The Lua-CSharp sandbox (no `io`/`os`/`load`),
instruction and time limits, and the quarantine-on-repeated-errors policy still apply.
A type/member blacklist is available through `IFullLuaAccessBlacklistPolicy`; see
`Assets/CoreAI/Docs/LUA_ACCESS_MODES.md`.
