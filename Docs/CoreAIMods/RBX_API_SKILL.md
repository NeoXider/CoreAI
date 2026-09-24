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
  `InvokeServer` inside `coroutine.create`, that `coroutine.resume` refuses a task, handler or
  main-chunk thread (resume a parked task with `task.spawn(t)`), `typeof` with Roblox type names,
  `warn` into the mod log at `Warn`, the clocks (`os.time(t)` reads the table as UTC with `hour`
  defaulting to 12, a non-number field counts as missing and a date before 1970 is `nil`;
  `GetServerTimeNow` follows the server once a client has joined, never goes backwards from then on
  and stands still while the server's clock does; there is no `os.date` and `DateTime` is not
  implemented), that a read-only script's `Instance.new` raises the capability error, and that a
  player's mods are unloaded when that player leaves. Its budget rules say that a budget cut cannot
  be caught by `pcall`/`xpcall` (only the resumer of a cut `coroutine.create` coroutine sees it),
  that such a coroutine (and a thread `task.spawn` runs at once) gets at most what its resumer has
  left of steps, time and memory, and that library calls back into Lua share one 128-level weighted
  cap (`pcall` 128 deep, `table.sort`/`tostring` 63) before a catchable "C stack overflow", and that a
  yield (`task.wait`, `coroutine.yield`) inside a `__tostring`, a sort comparator, a `__pairs`/`__ipairs`
  metamethod, `__index` or a gsub function raises "attempt to yield across a C-call boundary" while one
  inside `pcall` works (Luau parity, audit C3-06). It also
  states the reload rule: `manage_mods reload` and the Hub's Save & run remove the previous run's
  startup objects first, `keep_objects=true` keeps them, and two budget cuts in a row quarantine the
  mod and keep it from starting with the next game.
- Section 2 lists `Vector2:Angle`, the CFrame `components`/`ToEulerAngles`/`ToOrientation`/
  `ToAxisAngle`/`AngleBetween` family and `CFrame.fromRotationBetweenVectors`, `Color3.toHSV`, the
  argument coercion rule (Luau's: number ↔ numeric string, constructor numbers 0 when nil, booleans
  never converted), 32-bit
  UDim offsets and the ±(2^53−1) `NextInteger` bounds. Section 3 lists `Enum.X:FromName` /
  `:FromValue`, `Enum.KeyCode.None = 0` with `Unknown` as its alias, and that an Enum property or
  instance-method argument takes the item, its Name or its Value while datatype members need the
  `EnumItem`.
- Section 4 also states that an `UnreliableRemoteEvent` payload over 1,000 bytes is
  `PAYLOAD_TOO_LARGE` in solo as well as online, that `ClickDetector.MouseClick` passes the clicking
  player, measures `MaxActivationDistance` from that player's character (from the camera when there
  is none) and works for a detector under a `Model` or `Folder` (the deepest detector wins), and that
  `CollectionService.TagAdded`/`TagRemoved`/`GetAllTags` count only holders inside the DataModel.
- Section 4 also states the instance-core rules: the per-actor instance quota (2,048; 4,032 per
  WebGL world) and its `BUDGET_EXCEEDED` refusal naming the call, `Instance.Changed` on every instance
  and `GetPropertyChangedSignal` refusing an event, a method or a near-miss typo of a known property
  while a real unmodelled property gets a never-firing signal (the same signal for a name, disconnected by
  `Destroy()`; script, tween and `PivotTo` writes fire both; an equal assignment and physics movement fire
  nothing), removal handlers of a destroyed
  instance reading the instance they were handed, `AncestryChanged` on every descendant, `game:IsLoaded()`, the 100-character `Name`, the 2,048-level depth limit, `Clone`
  remapping `PrimaryPart`/`ObjectValue.Value` onto the copies, `game:Clone()`/`player:Clone()` → nil,
  the Debris refusals (services, `game`, the camera, a `Player`) and the TweenService rules
  (WorldEdit, tweenable types, per-actor retention of 256 finished tweens with the WRONG/RIGHT pair).
- Sections 5 and 6 state the Part write rules (strict booleans, numbers for strings and numeric
  strings for numbers, Enum properties by Name or Value, `Size` clamped to [0.001, 2048], non-finite
  spatial values refused, the `Part.Anchored expects a boolean, got string` error shape) and
  `CanCollide = false` as in Roblox (bodies pass through, `Touched` and `Raycast` still see the part;
  only `workspace` content is physical). Section 7 states the Humanoid rules (`Died` only inside
  `workspace`, `JumpPower` in [0, 1000], `math.huge` health, write authority for
  `TakeDamage`/`MoveTo`/`ChangeState`, a ~1-stud arrival radius on the ground plane, `MoveTo` ending
  with `false` when a script or a tween moves the `HumanoidRootPart`, `Humanoid:Clone` keeping the
  health and movement values). Section 8 adds the non-archivable character (`character:Clone()` is
  nil until the script sets `Archivable = true`) and `Player:Kick(message)` (the text reaches the
  kicked client, cut to 1,024 UTF-8 bytes; a number is sent as its `tostring` text, any other
  non-string is `BAD_ARGUMENT`). Section 9 says that `.Value` writes convert like arguments and that
  `IntValue.Value` refuses a value outside -2^63..2^63-1. Section 12 caps
  attributes and tags at 256 per instance, limits a new attribute name to ASCII letters, digits,
  `.`, `-`, `/` and `_`, and a new tag to 100 characters. Section 13 documents the
  `[mod:<id> script:main.lua line:N]` prefix on errors raised inside a mod, argument numbers that do
  not count `self`, that `pcall`, `xpcall` and `coroutine.resume` all receive exactly that one line
  (a budget cut excepted), the Lua-style `bad argument #n to 'fn' (x expected, got y)` of the
  mod-core functions, and the one coercion rule of both surfaces: a string parameter takes a number as
  `tostring` writes it (`store_set(7, 8)`, `FindFirstChild(5)`), a number parameter a string `tonumber`
  accepts, an integer parameter truncates toward zero, and a boolean parameter takes only
  `true`/`false` (`FindFirstChild(name, 1)` is `BAD_ARGUMENT`). Section 14 adds that Luau nesting
  deeper than 200 levels is a syntax error. Section 14 lists the loud global stubs
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

The user-facing companion to this skill is
[`Assets/CoreAI/Docs/RBX_API.md`](../../Assets/CoreAI/Docs/RBX_API.md); world saving/loading is
specified in [`WORLD_PACKAGE.md`](WORLD_PACKAGE.md).
