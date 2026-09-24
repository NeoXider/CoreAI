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
  test (`LuaModdingSkillEditModeTests`; its phrase checks also run on Linux in the portable
  Lua-tier suite, `tools/portable/LuaTests`, while the `Resources` comparison needs Unity).
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
- Section 1 also states the task-handle rules (`task.spawn/defer/delay` accept a handle the mod's own
  `task.*` call returned and `task.spawn(t) == t`; a finished task, one inside a scheduler wait,
  another mod's task or a `coroutine.create` thread is `BAD_ARGUMENT`), native `coroutine.yield`
  (parks a task until `task.spawn(t, ...)`, whose arguments it returns; `CONTEXT_VIOLATION` in a
  handler or the main chunk), `CONTEXT_VIOLATION` for `task.wait`/`signal:Wait`/`WaitForChild`/
  `InvokeServer` inside `coroutine.create`, `typeof` with Roblox type names, `warn` into the mod log
  at `Warn`, the clocks (`os.time(t)` reads the table as UTC with `hour` defaulting to 12;
  `GetServerTimeNow` follows the server once a client has joined and never goes backwards from then
  on; `DateTime` is not implemented), that a read-only script's `Instance.new` raises the capability
  error, and that a player's mods are unloaded when that player leaves.
- Section 2 lists `Vector2:Angle`, the CFrame `components`/`ToEulerAngles`/`ToOrientation`/
  `ToAxisAngle`/`AngleBetween` family and `CFrame.fromRotationBetweenVectors`, `Color3.toHSV`, the
  constructor coercion rule (numeric string → number, nil → 0, anything else `BAD_ARGUMENT`), 32-bit
  UDim offsets and the ±(2^53−1) `NextInteger` bounds. Section 3 lists `Enum.X:FromName` /
  `:FromValue` and `Enum.KeyCode.None = 0` with `Unknown` as its alias.
- Section 4 also states that an `UnreliableRemoteEvent` payload over 1,000 bytes is
  `PAYLOAD_TOO_LARGE` in solo as well as online, that `ClickDetector.MouseClick` passes the clicking
  player, measures `MaxActivationDistance` from that player's character (from the camera when there
  is none) and works for a detector under a `Model` or `Folder` (the deepest detector wins), and that
  `CollectionService.TagAdded`/`TagRemoved`/`GetAllTags` count only holders inside the DataModel.
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
  `TakeDamage`/`MoveTo`/`ChangeState`, a ~1-stud arrival radius on the ground plane, `MoveTo` ending
  with `false` when a script or a tween moves the `HumanoidRootPart`, `Humanoid:Clone` keeping the
  health and movement values). Section 8 adds the non-archivable character (`character:Clone()` is
  nil until the script sets `Archivable = true`) and `Player:Kick(message)` (the text reaches the
  kicked client, cut to 1,024 UTF-8 bytes; a non-string is `BAD_ARGUMENT`). Section 12 caps
  attributes and tags at 256 per instance and limits a new attribute name to ASCII letters, digits,
  `.`, `-`, `/` and `_`. Section 13 documents the `[mod:<id> script:main.lua line:N]` prefix on errors
  raised inside a mod, argument numbers that do not count `self`, and that `pcall`, `xpcall` and
  `coroutine.resume` all receive exactly that one line. Section 14 lists the loud global stubs
  (`BrickColor`, `NumberSequence`, `ColorSequence`, `NumberRange`, `Ray`, `Region3`, `Rect`,
  `PhysicalProperties`, `OverlapParams`, `DateTime`, `shared`), each with its workaround.
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

### Where the runtime has moved past the skill text

The skill text has not been edited since these runtime changes, so it is behind the runtime in three
places (the edit is tracked in `TODO.md`; `RBX_API.md` already describes the runtime):

- **`GetPropertyChangedSignal`.** Section 4 says it refuses an unknown or wrong-case name. The runtime
  refuses only an event, a method or a callback, a near miss of a known property name (one letter of
  the wrong case, missing, added, changed or swapped; never a digit) and a name over 100 characters;
  a real property CoreAI does not model gets a signal that never fires and one log note.
- **Budget trips.** Section 13 says to catch errors with `pcall`. A budget trip (steps, time, memory)
  cannot be caught: `pcall`/`xpcall` inside the tripped run let it through and `xpcall`'s handler
  does not run; only a raw coroutine's resumer sees it (`coroutine.resume` returns `false`, the
  coroutine is dead). An instance-quota `BUDGET_EXCEEDED` refusal and the per-call pattern-step
  refusal are ordinary errors and are caught.
- **Clocks.** Section 1 does not say that `os.time(t)` returns `nil` for a date before 1970, that a
  non-number field counts as missing, or that a client's `GetServerTimeNow` holds while the server's
  clock holds.

The user-facing companion to this skill is
[`Assets/CoreAI/Docs/RBX_API.md`](../../Assets/CoreAI/Docs/RBX_API.md); world saving/loading is
specified in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md).
