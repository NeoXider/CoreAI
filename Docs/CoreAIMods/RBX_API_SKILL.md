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

`ScriptContext` resolves as a tree-backed service. Its one member, `SetTimeout(seconds)`, moves the
wall-clock half of the per-resume execution budget live and is host-gated: an ordinary mod is refused
with `NOT_AUTHORITY` (the mirror marks the member `PluginSecurity`; CoreAI maps that tier to the host
actor — roadmap deviation DEV-14). The skill text lists `ScriptContext` among the real services but
does not describe `SetTimeout`.

For the full picture of what has landed and what is planned, see
[`ROBLOX_API_ROADMAP.md`](ROBLOX_API_ROADMAP.md).

## What the skill text currently claims

Keep these in step with the runtime when the skill text is edited:

- Section 4 lists the core creatable classes: `Part`, `Folder`, `Model`, `ClickDetector`,
  `MaterialVariant`, `RemoteEvent`, `UnreliableRemoteEvent`, and `RemoteFunction`. `Camera` is not
  creatable — a mod reaches the world camera through `workspace.CurrentCamera`. `Humanoid` (section 7)
  and the eight value objects `IntValue`, `NumberValue`, `StringValue`, `BoolValue`, `ObjectValue`,
  `Vector3Value`, `CFrameValue`, `Color3Value` (section 9) are creatable too, and so is `Backpack` (a
  plain container). A real Roblox class that is not built yet (`WedgePart`, `SpawnLocation`, `Weld`,
  `Attachment`, …) raises `NOT_IMPLEMENTED` instead of "Unable to create".
- Section 4 describes the service catalog as 42 registrations: the tree-backed `HttpService`,
  `Players`, `Debris`, `TweenService`, `CollectionService` and `ScriptContext`, the `RunService` /
  `UserInputService` fallbacks the standard runtime replaces, and loud placeholders with a rung
  (`DataStoreService`, `ContextActionService`, `SoundService`, `StarterGui`, `AIService`) or a
  backlog/unsupported status (`Teams`, `PhysicsService`, `PathfindingService`, `MarketplaceService`, …).
- Section 1 carries the scheduler and signal rules: `ConnectParallel` is `Connect` (DEV-5, a note
  once per mod); a one-off `execute_lua` chunk gets `CONTEXT_VIOLATION` from
  `Connect`/`Once`/`ConnectParallel`/`Wait`; every handler receives its own copy of a table argument;
  a self-re-firing handler is cut after 10 generations (`SIGNAL_CASCADE`) and more than 16,384 handler
  calls of one mod in one resumption point are dropped (`BUDGET_EXCEEDED`); `math.huge` waits park,
  a NaN duration is `BAD_ARGUMENT`, `task.cancel` of a finished task is a no-op; a thread may loop
  forever as long as it yields, memory is budgeted per resume, string patterns stop after 5,000,000
  steps per call and `gsub`/`format` results at 1,000,000 characters.
- Section 2 lists `Vector2:Angle`, the CFrame `components`/`ToEulerAngles`/`ToOrientation`/
  `ToAxisAngle`/`AngleBetween` family and `CFrame.fromRotationBetweenVectors`, `Color3.toHSV`, the
  constructor coercion rule (numeric string → number, nil → 0, anything else `BAD_ARGUMENT`), 32-bit
  UDim offsets and the ±(2^53−1) `NextInteger` bounds. Section 3 lists `Enum.X:FromName` /
  `:FromValue` and `Enum.KeyCode.None = 0` with `Unknown` as its alias.
- Section 4 also states the instance-core rules: `Instance.Changed` on every instance and
  `GetPropertyChangedSignal` refusing an unknown or wrong-case name (script, tween and `PivotTo`
  writes fire both; an equal assignment and physics movement fire nothing), `AncestryChanged` on every
  descendant, `game:IsLoaded()`, the 100-character `Name`, the 2,048-level depth limit, `Clone`
  remapping `PrimaryPart`/`ObjectValue.Value` onto the copies, `game:Clone()`/`player:Clone()` → nil,
  the Debris refusals (services, `game`, the camera, a `Player`) and the TweenService rules
  (WorldEdit, tweenable types, per-actor retention of 256 finished tweens with the WRONG/RIGHT pair).
- Sections 5 and 6 state the Part write rules (strict booleans, `Size` clamped to [0.001, 2048],
  non-finite spatial values refused, the `Part.Name expects a string, got number` error shape) and
  `CanCollide = false` as in Roblox (bodies pass through, `Touched` and `Raycast` still see the part;
  only `workspace` content is physical). Section 7 states the Humanoid rules (`Died` only inside
  `workspace`, `JumpPower` in [0, 1000], `math.huge` health, write authority for
  `TakeDamage`/`MoveTo`/`ChangeState`, a ~1-stud arrival radius on the ground plane). Section 12 caps
  attributes and tags at 256 per instance. Section 13 documents the `[mod:<id> script:main.lua line:N]`
  prefix on errors raised inside a mod and argument numbers that do not count `self`.
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
  `Player:LoadCharacterAsync()` (yielding; `LoadCharacter()` is its deprecated alias),
  `CharacterAdded` / `CharacterRemoving`, `DistanceFromCharacter` and the respawn after
  `RespawnTime` are described in section 8.
- `BasePart`'s network-ownership family (`SetNetworkOwner`, `GetNetworkOwner`,
  `SetNetworkOwnershipAuto`, `GetNetworkOwnershipAuto`, `CanSetNetworkOwnership`) is a loud stub:
  the server simulates every part, and ownership is deferred to the replication rung.

A ratchet test reads the shipped-versus-stubbed truth out of `ServiceCatalog` at test time, so a
future rung cannot ship a service while the skill text still calls it unimplemented.

The user-facing companion to this skill is
[`Assets/CoreAI/Docs/RBX_API.md`](../../Assets/CoreAI/Docs/RBX_API.md); world saving/loading is
specified in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md).
