# Rbx API skill

`Rbx API` is an on-demand agent skill: a compact, model-facing reference for the
Roblox-compatible (Rbx) Lua surface that the in-game Programmer agent can pull into context when
it needs to write Roblox-style scripts (`Instance.new`, `game`/`workspace`, `Vector3`/`CFrame`/
`Color3`, Part properties, attributes, tags).

## What a skill is

Skills keep the Programmer system prompt small. The prompt only *names* the skill; the full
reference is loaded lazily. The agent calls the meta-tool:

```
read_skill('Rbx API')
```

and receives the whole reference back as text. The classic Lua surface is a sibling skill,
`read_skill('Lua Modding')`; the Rbx skill covers only the Roblox-style API and assumes the
classic one for timers/persistence/events.

## How it is wired

Same pattern as the Lua Modding skill:

- **Canonical text** — `Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt`. Editor/Resources
  hosts load this override.
- **Built-in fallback** — `BuiltInRbxApiSkillText` in
  `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/BuiltInRbxApiSkillText.cs`. Code-only hosts
  (no Resources) fall back to this constant. The two are byte-identical, pinned by an EditMode
  test (`LuaModdingSkillEditModeTests`).
- **Registration** — `CoreAiModsInstaller.RegisterCoreAiMods` adds the skill to the built-in
  Programmer role (`AddSkillForRole`), loading the Resources override if present and the built-in
  constant otherwise. `read_skill` resolves the skill by its name, `"Rbx API"`.

The Rbx Lua bindings themselves live in `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbx*`
(namespaces `CoreAI.Mods.Rbx.*`, assemblies `CoreAI.RbxApi.*`) and are enabled through
`LuaCsModStackOptions.RbxApi`, which the same installer wires by default.

## Current shipped scheduler, signal, and service behavior

The `task` scheduler and general `RbxScriptSignal` connections now run in the shipped runtime.
Every signal is deferred: firing queues handlers, and handlers run at the next script-resumption
point rather than at the fire site. A handler may call `task.wait()` and resume later on its owning
scheduler thread. Multiple connections to one signal have no documented invocation order; scripts
must not depend on connection order.

`game:GetService()` also resolves registered placeholder services without failing the file's setup code.
The first member access on an unimplemented service raises `NOT_IMPLEMENTED` and names its delivery
rung. See the exact current service table and author-facing examples in
[`mod-authoring.md`](mod-authoring.md#roblox-services-and-deferred-placeholders).

For the full picture of what has landed and what is planned, see
[`ROBLOX_API_ROADMAP.md`](ROBLOX_API_ROADMAP.md).

## What the skill text currently claims

Keep these in step with the runtime when the skill text is edited:

- Creatable classes are `Part`, `Folder`, `Model`, `ClickDetector`, `MaterialVariant`, `RemoteEvent`,
  `UnreliableRemoteEvent`, and `RemoteFunction`. `Camera` is not creatable — a mod reaches the
  world camera through `workspace.CurrentCamera`.
- `BasePart` exposes `Shape`, `Material`, `MaterialVariant` (string; `""` for none), `Orientation`, and `Rotation` in addition to the MVP1
  set. All 45 `Enum.Material` items render; an unmapped id resolves to a magenta diagnostic
  material. `Part.Color` stays an independent tint over the material's own albedo. `MaterialService` is a tree-backed service.
- `WaitForChild` yields for an absent child, warns after five seconds, and honours a timeout
  overload.
- `RemoteFunction` invocation is bounded to 30 scheduler seconds — a documented deviation from
  Roblox, detailed in [`RBX_API.md`](../../Assets/CoreAI/Docs/RBX_API.md).
- `RunService` carries the modern frame events `PreAnimation`, `PreSimulation`, `PostSimulation`
  and `PreRender` alongside the legacy `Stepped`, `Heartbeat` and `RenderStepped` aliases. The
  legacy pair keeps its legacy signature — `Stepped(runTime, step)`, `RenderStepped(delta)` — while
  the modern events each take the delta alone. The render pair is withheld on a process that draws
  nothing (a dedicated server); solo and host both render.
- `Players` exposes `CharacterAutoLoads` (default true), `RespawnTime` (default 5.0) and a
  read-only `MaxPlayers`; assigning `MaxPlayers` from a mod is refused rather than ignored.
- `BasePart`'s network-ownership family (`SetNetworkOwner`, `GetNetworkOwner`,
  `SetNetworkOwnershipAuto`, `GetNetworkOwnershipAuto`, `CanSetNetworkOwnership`) is a loud stub:
  the server simulates every part, and ownership is deferred to the replication rung.

A ratchet test reads the shipped-versus-stubbed truth out of `ServiceCatalog` at test time, so a
future rung cannot ship a service while the skill text still calls it unimplemented.

The user-facing companion to this skill is
[`Assets/CoreAI/Docs/RBX_API.md`](../../Assets/CoreAI/Docs/RBX_API.md); world saving/loading is
specified in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md).
