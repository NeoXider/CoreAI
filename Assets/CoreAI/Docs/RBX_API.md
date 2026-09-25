# Roblox-style Lua API (Rbx API)

**This is the API a mod uses to create and control world objects.** It mirrors Roblox 1:1, so a
script written here imports and exports without a rewrite. C# identifiers use the `Rbx` prefix
(`CoreAI.RbxApi.*`); the Lua-facing surface keeps the Roblox spelling exactly.

> The older low-level `coreai_world_spawn` / `coreai_world_change` / `coreai_world_destroy` build
> functions are **not registered in the default shipping composition** — `CoreAiModsInstaller` sets
> `RegisterWorldEditBuildBindings = false`, and calling one raises an actionable
> `LuaApiWithheldException` instead of silently doing nothing. Use this API instead. The read-only
> queries (`coreai_world_find` / `_pos` / `_exists`) are unaffected and stay available at the `Read`
> tier. See [LUA_GAME_API.md](LUA_GAME_API.md) for that legacy surface and the hosts that still opt in.

## Capability

Every global below is available at `Read`. Creating and mutating needs the **`WorldEdit`**
capability: `Instance` exists on every tier, but `Instance.new` without `WorldEdit` raises the
capability error before it reads or creates anything, and `camera_set_cframe` / `camera_follow` do
the same, so a mod never sees "attempt to call a nil value".

## Globals

| Global | What it is |
|---|---|
| `Instance` | `Instance.new(className)` — the object constructor (creating needs `WorldEdit`) |
| `game` | The DataModel: `game:GetService(name)` |
| `workspace` | The world root; also `workspace.CurrentCamera` |
| `UserInputService` | Alias of `game:GetService("UserInputService")` |
| `Vector3`, `Vector2` | `.new(x, y, z)`, arithmetic, `.Magnitude`, `.Unit` |
| `CFrame` | `.new(...)`, `CFrame.lookAt(from, to)`, `*` composition |
| `Color3` | `.new(r, g, b)` (0..1), `Color3.fromRGB(r, g, b)` (0..255) |
| `UDim`, `UDim2` | Scale/offset pairs |
| `Random` | Seedable RNG |
| `Enum` | `Enum.CameraType.Scriptable`, `Enum.KeyCode.A`, … |
| `TweenInfo`, `RaycastParams` | `.new(...)` for `TweenService:Create` and `workspace:Raycast` |
| `task` | `task.wait`, `task.spawn`, `task.defer`, `task.delay`, `task.cancel` (`spawn`/`defer`/`delay` also take a task handle back — see "Tasks and coroutines") |
| `wait`, `spawn`, `delay` | Roblox's legacy scheduler globals |
| `time`, `tick` | `time()` is scaled game time; `tick()` is deprecated (logs once per mod) |
| `os` | Only `os.time([dateTable])` and `os.clock()` — the stock `os` library stays removed by the sandbox |
| `typeof` | Roblox type names: `"Instance"`, `"Vector3"`, `"CFrame"`, `"EnumItem"`, `"RBXScriptSignal"`, … |
| `warn` | Like `print`, into the mod's log at `Warn` level (`get_mod_logs`) |
| `script` | The running mod's own script instance (mods only, not one-off `execute_lua`) |
| `camera_set_cframe`, `camera_follow` | CoreAI convenience shorthands for camera control (both fire the camera's `Changed`); in a world whose `Camera` a script destroyed they raise `BAD_ARGUMENT` before anything moves |

`BrickColor`, `NumberSequence`, `ColorSequence`, `NumberRange`, `Ray`, `Region3`, `Rect`,
`PhysicalProperties`, `OverlapParams`, `DateTime` and `shared` exist as **loud stubs**: any use raises
`NOT_IMPLEMENTED` with a workaround (`Color3.fromRGB` for `BrickColor`, `workspace:Raycast` for `Ray`,
`os.time()` or `workspace:GetServerTimeNow()` for `DateTime`, `mods_export`/`mods_get` for `shared`)
instead of "attempt to index a nil value".

### Tasks and coroutines

`task.spawn`, `task.defer` and `task.delay` accept a function or a task handle that one of them
returned to the same mod, and `task.spawn(t) == t`. A parked task resumes, a deferred or delayed one
is rescheduled (its old slot never fires), and the running task may re-queue itself only through
`task.defer`/`task.delay`; a finished task, a task inside a scheduler wait, another mod's task and a
`coroutine.create` thread are refused with `BAD_ARGUMENT` (native threads stay outside the scheduler,
roadmap R4.10). A native `coroutine.yield()` inside a task parks it until `task.spawn(t, ...)`, and
`coroutine.yield` returns those arguments; inside a signal handler, a `RemoteFunction` callback, a
legacy `spawn`/`delay` function or the main chunk it stops that thread with `CONTEXT_VIOLATION`,
because nothing could resume it. `task.wait`, `signal:Wait`, `WaitForChild` and a `RemoteFunction`
invoke inside a `coroutine.create` coroutine raise `CONTEXT_VIOLATION` instead of suspending the
wrong thread — run such code with `task.spawn`. `task.cancel` on a finished task does nothing;
handed a `coroutine.create` thread or a `coroutine.running()` value it raises `BAD_ARGUMENT` ("task.cancel
cannot cancel a coroutine.create thread or a coroutine.running() value …"): running code stops by returning.
The reverse is refused as well: `coroutine.resume` of a task, signal-handler or main-chunk thread (a
`coroutine.running()` value) returns `false` and "cannot resume a task or signal-handler thread with
coroutine.resume; …" without touching it — resume a parked task with `task.spawn(t)`.

A run nested inside another — a `coroutine.create` body under `coroutine.resume`, a thread `task.spawn`
runs at once, a `mods_call` export — gets its own budget or what the enclosing run has left, whichever is
smaller, for steps, time and memory alike; the steps it uses are charged back to every run it is nested
in, and when a lent limit runs out the whole chain ends, as uncatchably as the enclosing run itself would.
The enclosing run is the innermost run executing Lua when the nested one starts, whichever mod or
coroutine it belongs to (`LUA_SANDBOX_SECURITY.md`). A thread the scheduler resumes from its own frame
still gets its full budget. Yielding inside a library callback — a `__tostring` run by `tostring`,
`print`, `warn` or `string.format`, a `table.sort` comparator, `__pairs`/`__ipairs`, a `gsub` function or
`__index` — raises `attempt to yield across a C-call boundary`, as in Luau; yielding inside `pcall` works.
Calls from library functions back into Lua share one cap of 128 levels along a chain of nested runs
(`LuaCsSecureEnvironment.MaxCCallDepth`, Luau's `LUAI_MAXCCALLS`), weighted by the native stack they take:
a `pcall` or `xpcall` body opens one level; a `gsub` replacement function (two since 7.47.0), a `__tostring`
run by `tostring`, `print`, `warn` or `string.format`, a `table.sort` comparator, `__pairs`/`__ipairs`, a `gsub`
`__index`, `coroutine.resume`, a thread `task.spawn` runs at once and a guarded call that starts inside a
run (a `mods_call` export included) open two. So `pcall` nests 128 deep and `table.sort` or `tostring` 63,
and a resumed thread continues its resumer's count. The call past the cap raises `C stack overflow
(<function>: more than 128 levels of nested calls from library functions back into Lua)`, an ordinary
error: `pcall` returns it and `xpcall` hands it to its handler. Plain Lua recursion is not limited. The
cap keeps any mix of these calls at most 428 KB of native stack on CoreCLR (about 1.38 MB on Mono, whose
frames are larger), which an IL2CPP or WebGL player cannot otherwise bound (`TODO.md`, "Check the tests").

### How arguments and property writes convert

Both scripting surfaces — the Rbx API and the mod-core functions (`store_set`, `hooks_every`, …) — read
arguments and property writes by one rule set (`LuaCsValueMarshaller`), the one Luau's `luaL_check*`
functions and Roblox's property setters use, so a Roblox script runs here unchanged:

- A **string** parameter or property takes a number as the text `tostring` gives it: `part.Name = 5`,
  `player:Kick(42)`, `root:FindFirstChild(5)` (and `root[5]`) finds the child named `"5"`,
  `inst:SetAttribute(7, v)` sets `"7"`. A boolean, table, function or `nil` is refused.
- A **number** parameter or property takes a string that Luau's `tonumber` accepts — surrounding
  spaces, a sign, `.5`, `1e3`, hex `0x10` and hex floats `0x1p4`, `inf`, `nan`; `1e999` is infinity —
  and reads it up to an embedded NUL character, as Luau does (`"0.5\0junk"` is 0.5). `""`, `"abc"`,
  `"5x"` and a no-break space are refused. The member's own range check still applies
  (`TweenService:GetValue("inf", …)` is refused as a non-finite alpha).
- An **integer** parameter truncates toward zero (`Random:NextInteger(2.7, 2.7)` is 2, `"-2.7"` is -2);
  a number with no integer representation (NaN, `1e300`) is refused. A member keeps its own rule on
  top: `IntValue.Value` rounds half away from zero and refuses a value outside [-2^63, 2^63).
- A **boolean** parameter or property takes only `true` or `false` (an omitted optional one is `false`;
  `HttpService:GenerateGUID()` wraps in braces by default, as in Roblox). `part.Anchored = "true"`,
  `BoolValue.Value = 1`, `FindFirstChild(name, 0)` and `PostAsync(…, 1)` are refused.
- An **Enum** property or instance-method argument takes the `EnumItem`, its `Name` or its integer
  `Value`: `part.Material = "Wood"` or `512`, `part.Shape = "Cylinder"`,
  `UserInputService:IsKeyDown("E")`, `TweenService:GetValue(0.5, "Quad", "Out")`,
  `Humanoid:ChangeState("Jumping")`. Another enum's item, an unknown name, a numeric string, a fraction
  and an unknown value are refused with the value in the message (`Part.Material expects an
  Enum.Material item, got string "Plastik"`), and the property keeps its last value. Datatype members
  (`TweenInfo.new` easing, `CFrame` rotation orders, `RaycastParams.FilterType`) still take only the
  `EnumItem`, and `TweenService:Create` goals are not converted (Roblox refuses a type mismatch there).
- `Vector3` and `Vector2` arithmetic takes a numeric-string scalar (`Vector2.new(1, 2) * "2"`).

The rule table is pinned on both surfaces by `RuleTable_*` in
`Assets/CoreAIMods/Tests/EditMode/RbxApi/Acceptance/Mvp8ValueObjectsEditModeTests.cs`; the Full-tier
`unity_*` reflection surface converts by the same rules (`RuleTable_FullTierReflectedMembers_ConvertByTheSameRules`).
Known differences from Luau (`TODO.md`): the text a number becomes is this VM's `tostring`
(`1E+21`, `Infinity`, `NaN`, and 15 significant digits on Mono) where Luau prints `1e+21`, `inf`, `nan`
and the shortest round-trip digits (DEV-17 in the roadmap), and the sandbox's own `tonumber("inf")` is
still `nil`.

### Clocks

`time()` is scaled game time. `os.time()` is Unix seconds; `os.time({year = ..., month = ..., day =
...})` reads the table as UTC (`hour` defaults to 12, `min` and `sec` to 0, out-of-range fields carry
over, `isdst` is ignored), so every machine computes the same timestamp. As in Luau, a date before
1970-01-01 00:00:00 UTC returns `nil`, and a field that is not a number (nor a numeric string) counts
as missing — `{hour = true}` is noon, a non-number `year`, `month` or `day` is `BAD_ARGUMENT`. The
sandbox has no `os.date`, so there is no round trip through it. `os.clock()` is monotonic
elapsed time. `workspace:GetServerTimeNow()` is Unix seconds that the server and every client agree
on: where this process is the server clock (solo, host, dedicated server) it is the local clock, held
at its last reading if that clock steps back; on a client it is the local clock until the join
handshake, re-based once at the first synchronization, and from then on it never goes backwards — a
backward correction is absorbed by running at half speed. While the server's clock holds after a
backward step, a client's holds too: the server sends the value its own scripts read, with the hold.

## Classes

`Instance.new` accepts: **`Part`**, **`Folder`**, **`Model`**, **`ClickDetector`**, **`MaterialVariant`**,
**`RemoteEvent`**, **`UnreliableRemoteEvent`**, **`RemoteFunction`**, **`Humanoid`**, **`Backpack`**, and
the value objects **`IntValue`**, **`NumberValue`**, **`StringValue`**, **`BoolValue`**,
**`ObjectValue`**, **`Vector3Value`**, **`CFrameValue`** and **`Color3Value`** (each with one
type-checked `Value`). `Camera` is not creatable — the world's one camera is
`workspace.CurrentCamera`. About ninety real Roblox classes CoreAI does not build yet (`WedgePart`,
`SpawnLocation`, `Weld`, `Attachment`, …) raise `NOT_IMPLEMENTED` from `Instance.new`, so a script
learns "not yet" rather than "no such class"; an unknown name still raises `BAD_ARGUMENT`.
The class ancestry (`Object` → `Instance` → `PVInstance` → `BasePart` → `FormFactorPart` → `Part`;
`Camera` is a `PVInstance`, so `GetPivot`/`PivotTo` work on it) is data-driven through `ClassCatalog`,
so `IsA` works the way it does in Roblox.

Services reachable via `game:GetService`: `RunService`, `UserInputService`, `Players`,
`CollectionService`, `TweenService`, `SoundService`, `Lighting`, `Debris`, `HttpService`,
`ReplicatedStorage`, `ServerStorage`, `ServerScriptService`, `ContextActionService`,
`PathfindingService`, `MarketplaceService`, `DataStoreService`, `MaterialService`, `ScriptContext`, and CoreAI's own `AIService`.
Twenty-eight more real Roblox services (`StarterGui`, `Teams`, `PhysicsService`, `ReplicatedFirst`,
`TeleportService`, `BadgeService`, …) resolve to loud placeholders whose first member access raises
`NOT_IMPLEMENTED` with a workaround, instead of the false `X is not a valid Service name`;
`GetService("")` is `UNKNOWN_SERVICE`.

### Change notifications

`Instance.Changed(propertyName)` fires on every instance; a value object passes its new `Value`
instead, as in Roblox. `GetPropertyChangedSignal(name)` fires with no arguments. It refuses, with
`X is not a valid property name.` and a hint, an event, a method or a callback of the class, and a
typo — a name one edit away from a property CoreAI knows (a letter of the wrong case, a letter
missing, added, changed or swapped with its neighbour; an edit of a digit never counts, so
`Attachment0`/`Attachment1` stay distinct). Any other name is accepted: a bound or catalogued
property fires as described below, and a real Roblox property CoreAI does not model yet
(`Humanoid.FloorMaterial`, `Players.NumPlayers`) gets a signal that never fires, with one log note per
class and name (at most 64 per world, then one line saying the rest are suppressed). That signal is kept per
instance and is the same object for a name, as in Roblox, and `Destroy()` disconnects its connections. A name longer than
100 characters is refused. Deviation: Roblox's deprecated lower-case aliases (`archivable`,
`className`, `maxHealth`, `userId`, `localPlayer`, `brickColor`, `focus`) are near misses of the
current names and are refused, with the current spelling in the hint. Writes
from a script, a tween or `PivotTo` fire both, only on a real change, and derived members fire too: a
`CFrame` write also reports `Position`, `Orientation` and `Rotation`. An equal assignment fires nothing,
and movement by the physics engine never fires them. `Workspace.Gravity`, the `Humanoid` numbers and
`Player.Character`/`DisplayName` notify the same way, and so do `ClickDetector.MaxActivationDistance`,
the `Players` settings (`CharacterAutoLoads`, `RespawnTime`, a host's `MaxPlayers` change),
`UserInputService.MouseBehavior`, every `MaterialVariant` property, `Tween.PlaybackState` (including a
tween dropped because its target was destroyed), `Humanoid.RootPart`, `Humanoid.Jump` (on entering and
leaving the jumping state) and `Humanoid.MoveDirection`, which is sampled once per `Heartbeat` and
reported when it moved by more than 0.01 (OURS: the motor derives it from a velocity that wobbles at a
steady walk; a start or a stop is always reported). `AncestryChanged(movedInstance, newParent)` fires
on every descendant of a moved instance. `game:IsLoaded()` is `true` and `game.Loaded` never fires,
because the world is loaded before any script runs.

### `ClickDetector` and `CollectionService`

`ClickDetector.MouseClick` fires with the player who clicked (`nil` only when the host can resolve no
local player). A detector parented to a part, or to a `Model` or `Folder` holding the clicked part,
answers the click, and of several the deepest one above the part wins. `MaxActivationDistance`
(default 32 studs) is measured from the clicking player's `HumanoidRootPart` to the nearest point of
the clicked part; without a character it falls back to the camera distance (OURS — Roblox has no
characterless clicker). A host that runs several local players names the one at this screen with
`LuaCsRbxApiBindings.LocalPlayerActorId`; otherwise the player whose character the camera follows,
or the only connected player, is the clicker.

`CollectionService.TagAdded`/`TagRemoved` fire when a tag enters use inside the DataModel (its first
holder in the tree) and when it leaves use (its last holder in the tree loses the tag, leaves the tree
or is destroyed); a tagged instance parented to `nil` — a pooled coin — counts for neither, and
`GetAllTags` lists only tags in use. The per-tag `GetInstanceAddedSignal`/`GetInstanceRemovedSignal`
fire per instance, as before. When a tagged instance is destroyed, the removed-signal handlers — like
`ChildRemoved` and `DescendantRemoving` handlers — can still read it, as a `Destroying` handler can.
A new tag is at most 100 characters (`AddTag`, and the per-tag signal getters for a tag no instance
holds; OURS — Roblox states no limit, and 100 is its limit for `Name` and attribute names); a longer
tag stored by an older world still loads, reads and can be removed.

### Part properties

`Name`, `Parent`, `Position`, `CFrame`, `Size`, `Color`, `Material`, `MaterialVariant` (string, `""` for none), `Orientation`, `Rotation`,
`Anchored`, `Transparency`, `CanCollide`, and `Shape` (`Ball`, `Block`, `Cylinder`, `Wedge`,
`CornerWedge` — each one materializes its own mesh). A `Part` materializes as a real GameObject as
soon as its `Parent` is set into the world.

`Position` keeps the part's rotation; `CFrame` sets position and rotation together; `Orientation`
(YXZ degrees) and `Rotation` (XYZ degrees) set the rotation and keep the position.

Writes are checked the way Roblox checks them: boolean properties accept only `true`/`false`, string
properties also accept a number (its `tostring` text), number properties also accept a numeric string,
and Enum properties (`Material`, `Shape`, `CameraType`, `MouseBehavior`, `MaterialVariant.BaseMaterial`)
also accept the item's Name (`part.Material = "Wood"`) or Value (`part.Material = 512`) — see "How
arguments and property writes convert". `Size` is clamped to [0.001, 2048] studs per axis, a NaN or
infinite `Position`/`Size`/`CFrame` is `BAD_ARGUMENT`, and a written `CFrame` is orthonormalized. A
wrong type reads `Part.Anchored expects a boolean, got string | fix: assign a boolean to Part.Anchored`,
and assigning a read-only property says it is read only. `BasePart:PivotTo` moves the part's descendants
with it. An unanchored part moved by physics reads its live body pose. Assigning
`workspace.CurrentCamera` raises `NOT_IMPLEMENTED`; drive the one camera through its `CFrame`.

A part parented under another part lives in a `"<Part> (children)"` container GameObject with no scale
or rotation of its own, so a nested part keeps its own size and does not join the parent's compound
collider. Only the Workspace subtree is active: `Lighting`, `Players`, the storage services and parts
directly under `game` materialize inactive, and the flag is recomputed on every re-parent. While a
`Destroying` or `AncestryChanged` handler runs, a destroyed part still reads its last property values
(the most recent 2,048 destroyed parts are kept; older ones are forgotten).

### `RunService`: the frame loop

The modern Roblox frame events all fire, in the mirror's order, each carrying the frame delta:

| Event | When | Legacy alias |
|---|---|---|
| `PreAnimation(deltaTimeSim)` | before the physics simulation, after rendering | — |
| `PreSimulation(deltaTimeSim)` | before the physics simulation | `Stepped(runTime, step)` |
| `PostSimulation(deltaTimeSim)` | after the physics simulation | — |
| `Heartbeat(deltaTime)` | after `PostSimulation` | — |
| `PreRender(deltaTimeRender)` | before the frame is drawn | `RenderStepped(delta)` |

The legacy aliases keep their legacy signatures — `Stepped` still takes `(runTime, step)` while its
replacement `PreSimulation` takes the delta alone — so a script migrating between them reads
different argument positions, exactly as in Roblox.

`PreRender` and `RenderStepped` are withheld on a process that draws nothing (a dedicated server).
Solo and host both render, so both keep firing: the gate is "does anything get drawn here", not
`IsClient` — CoreAI's solo process is the server *and* the renderer.

`RunService:BindToRenderStep`/`UnbindFromRenderStep` are still loud stubs; connect `PreRender`
instead until named render-step binding lands.

### `Players`

`Players.LocalPlayer` (nil in a server context), `PlayerAdded(player)`,
`PlayerRemoving(player, exitReason)`, `GetPlayers()`, `GetPlayerByUserId(userId)` and
`GetPlayerFromCharacter(character)` (the lookups see only live Players under `Players`), plus
`CharacterAutoLoads` (default true), `RespawnTime`
(default 5.0 seconds; a negative or non-finite value is refused at the assignment) and a read-only
`MaxPlayers` — assigning `MaxPlayers` from a mod is refused rather than silently ignored, because
the host owns the capacity.

While `CharacterAutoLoads` is true, a joining player gets a minimal character: a `Model` named after
the player holding a `Humanoid` and a `HumanoidRootPart`. `Player:LoadCharacterAsync()` builds or
replaces it and yields until the deferred `CharacterRemoving` / `CharacterAdded` handlers have run;
`Player:LoadCharacter()` is its deprecated alias. `Player.Character` is that model (assigning it
directly fires no signals, as in Roblox), and `Player:DistanceFromCharacter(point)` returns studs from
the root part, or `0` without a character. A character whose `Humanoid` dies is reloaded
`RespawnTime` seconds later while `CharacterAutoLoads` is still true. Avatar rigs, animation and
appearance loading are not modelled.

A `Player` cannot be destroyed or re-parented from Lua (use `Player:Kick()`; parenting things *into* a Player is
fine) and `player:Clone()` returns `nil`. `Player:Kick(message)` hands its message to the transport, which shows
it to the kicked client (the Mirror bridge sends it before the drop; the in-process loopback has no client to
show it to); the text is cut to 1,024 UTF-8 bytes at a whole character, `nil` leaves the transport's default
text, a number is kicked with the text `tostring` gives it (`Kick(42)` shows `42`), and any other non-string is
`BAD_ARGUMENT` before anything is kicked. A character `Model` is created with `Archivable = false`, as in
Roblox, so `character:Clone()` returns `nil` until a script sets `Archivable = true`. A `Player` destroyed from
host C# code runs the same leave teardown as a disconnect, once: the actor's slot is freed, `PlayerRemoving`
fires once (with a `nil` reason) and the character is unloaded. On a `Host` or `DedicatedServer` topology, an
actor the transport admitted before its `Player` existed is refused with `NOT_AUTHORITY` when the world has no
`Players.IdentitySource`, instead of being handed a session-counter `UserId` that another account could receive
later; local actors, solo and client worlds are unaffected. The Mirror provider sets the identity source for you
(`Assets/CoreAIMirror/README.md`).

`BasePart`'s network-ownership family (`SetNetworkOwner`, `GetNetworkOwner`,
`SetNetworkOwnershipAuto`, `GetNetworkOwnershipAuto`, `CanSetNetworkOwnership`) is a loud stub too:
the server simulates every part, and ownership is deferred to the replication rung.

### `Humanoid`

`Instance.new("Humanoid")` gives a character `Health` (clamped to `[0, MaxHealth]`, default 100),
`MaxHealth`, `WalkSpeed` (16 studs/s), `JumpPower` (50), `JumpHeight` (7.2 studs), `UseJumpPower`
(true), read-only `MoveDirection` and `RootPart`, plus `TakeDamage(amount)` (negative heals),
`MoveTo(location)`, `GetState()` and `Humanoid.Jump = true`. Signals: `Died` (once — a dead humanoid
stays dead), `HealthChanged`, `MoveToFinished(reached)`, `Running`, `Jumping`, `FreeFalling`,
`StateChanged(old, new)`. `MoveTo` arrives within about one stud on the ground plane (height is
ignored) and reports `MoveToFinished(false)` after **eight seconds of scaled time**, so a paused world
never times a walk out.

`MoveTo` also ends with `MoveToFinished(false)` when a script moves the humanoid's `HumanoidRootPart`
— a `CFrame`, `Position`, `Orientation` or `Rotation` write, or a `PivotTo` that carries it — or a
tween changes that part's `CFrame`, as the mirror documents ("a script changes the CFrame"). A
`Humanoid:Clone()` keeps `MaxHealth`, `Health`, `WalkSpeed`, `JumpPower`, `JumpHeight`,
`UseJumpPower` and `DisplayName`; the clone of a dead humanoid keeps `Health = 0` and dies (firing its
own `Died`) on its first Heartbeat inside the Workspace.

`Died` fires only inside the Workspace: a humanoid at 0 health outside it dies on its first Heartbeat
after entering. `JumpPower` is clamped to [0, 1000]. `MaxHealth = math.huge` is accepted and stored as
the largest finite number (so the world stays savable) — `Health == math.huge` is then false, compare
with `MaxHealth` — `TakeDamage(math.huge)` kills, and NaN is refused. Property writes fire `Changed` and
`GetPropertyChangedSignal`. `TakeDamage`, `MoveTo` and `ChangeState` need `WorldEdit`, write authority
over that humanoid and a mutation envelope, exactly like `Health =`: one actor's mod can no longer kill
or steer another actor's character, so player-versus-player damage has to go through server-side code.

Movement is done by a motor behind `IRbxCharacterMotor`: CoreAI ships `UnityRbxCharacterMotor`, and
a host that prefers its own controller implements the interface without changing anything a script
sees — see `Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md`. Composition wires the motor for you when
the scene has an `RbxWorldHost`: every Humanoid whose root part has a body gets a real motor. A
Humanoid with no body — a headless world, storage-only trees, a root part not yet materialized —
keeps the null motor and does not move, and `MoveTo` then reports arrival immediately because the
null motor's position never changes. `Enum.HumanoidStateType` ships its full Roblox item set, but the state machine only enters
`Running`, `Jumping`, `Freefall`, `Landed` and `Dead`; `ChangeState` accepts only `Jumping` and says
so loudly otherwise. Seats, ragdoll, swimming, climbing, accessories and animation raise the loud
stub — they need a character rig CoreAI does not model.

**Passive regeneration is not built in**, exactly as in Roblox: the mirror inserts a regeneration
*script* into humanoids and documents disabling it with an empty `Script` named `Health`, so
regeneration belongs to the character template rather than the class.

### Physics: gravity, raycasts, contacts

`workspace.Gravity` is studs per second squared and defaults to Roblox's 196.2. It is applied per
body, so a world that changes it never touches the host scene's own `Physics.gravity`.

`workspace:Raycast(origin, direction, raycastParams?)` returns a `RaycastResult` (`Instance`,
`Position`, `Normal`, `Material`, `Distance`) or `nil`. The direction's **length is the range** and
may not exceed 15,000 studs — a longer one is refused rather than clamped. `RaycastParams.new()`
carries `FilterDescendantsInstances`, `FilterType` (`Enum.RaycastFilterType.Exclude`/`Include`),
`RespectCanCollide`, and `AddToFilter`. `IgnoreWater` and `BruteForceAllSlow` are accepted and inert
(CoreAI has no Terrain and one broadphase); `CollisionGroup` accepts only `"Default"` and raises
otherwise, because a group that filtered parts differently would return a confidently wrong hit.

`BasePart.Touched(otherPart)` and `BasePart.TouchEnded(otherPart)` fire on **both** parts, and only
from physical movement: at least one part must be unanchored, and a part moved by assigning
`Position` or `CFrame` (or by a tween) fires nothing that step — the same rule Roblox documents.

`CanCollide = false` works as in Roblox: other bodies pass through the part, but it still fires
`Touched`/`TouchEnded`, and `workspace:Raycast` still hits it unless `RespectCanCollide` is set — so a
coin pickup or a trap zone is a `CanCollide = false` part with a `Touched` handler. Cylinders are hit
by raycasts and fire `Touched` too. Only parts under the Workspace are physical. `RaycastParams`
also carries `ExcludeInstances` and `IncludeInstances` (exclusion wins; `IncludeInstances = nil`
means everything, `{}` means nothing), and `AddToFilter` accepts one instance or an array.

### `BasePart.Material` and `Part.Color`

`Material` takes an `Enum.Material` item and **every one of the 45 enum items renders**.
`RbxTextureMaterialProvider` is catalog-driven: the packaged `RbxMaterialTextureCatalog` ships 36 CC0
texture-backed surfaces at 1K, so every material the catalog describes renders from the package alone
with nothing imported. A project-local override catalog (`Assets/CoreAIRbxTexturesLocal/Resources/CoreAIRbxTextureCatalogOverride`,
written by the Editor menus `CoreAI/Materials/Download CC0 texture sets (ambientCG)...` and
`CoreAI/Materials/Import Bridge-Megascans folder...`) can give **any** of the 45 items a 2K–4K PBR set
with normal, roughness, optional AO and metalness maps; the override wins per material. Items without
a catalog entry are procedural surfaces from `RbxProceduralMaterialProvider` (opaque, metallic,
organic, transparent, neon, and force-field shader paths). A catalog entry with a missing texture falls
back to the procedural surface with one logged error, never to Unity's pink error shader, and a
material id that is not in the catalog resolves to an opaque **magenta diagnostic material** instead of
failing quietly, so a wrong value is visible on the first frame. Quixel Bridge / Fab (Megascans) sets
may be imported into your own project this way, but their licence forbids redistributing them inside a
package — only the CC0 sets ship with CoreAI.

```lua
local slab = Instance.new("Part")
slab.Material = Enum.Material.Cobblestone   -- textured PBR surface
slab.Anchored = true
slab.Parent = workspace
```

`Color` stays an independent Roblox-style **tint**, exactly as in Roblox. A part whose `Color` was
never assigned renders the material's own albedo (the stored Roblox default stays medium stone
grey); assigning `Color` marks it explicit and modulates the material's albedo from then on. The
tint rides a `MaterialPropertyBlock`, so parts sharing a material never clone it. `Neon` is the one
material with no palette of its own: its emission *is* `Part.Color`, so a default grey part glows
grey and a red one glows red — the same as Roblox.

Catalog detail lives in
[`PROCEDURAL_MATERIALS.md`](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) and
[`TEXTURE_MATERIALS.md`](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md).

### `MaterialVariant` — your own materials, swapped at runtime

The 45 `Enum.Material` items are CoreAI's defaults, not your limit. A game built on this framework
brings its own surfaces through Roblox's own answer to that problem: a `MaterialVariant` instance
parented to `MaterialService`, selected per part by the string property `BasePart.MaterialVariant`.
No CoreAI-specific API, no additions to `Enum.Material` — the same script runs in Roblox.

```lua
local variant = Instance.new("MaterialVariant")
variant.Name = "MossyBrick"
variant.BaseMaterial = Enum.Material.Brick      -- inherits everything you do not override
variant.ColorMap = "MyGame/Textures/mossy_brick_color"
variant.NormalMap = "MyGame/Textures/mossy_brick_normal"
variant.RoughnessMap = "MyGame/Textures/mossy_brick_rough"
variant.StudsPerTile = 8
variant.Parent = game:GetService("MaterialService")

wall.MaterialVariant = "MossyBrick"   -- swap it on
wall.MaterialVariant = ""             -- and back to plain Enum.Material.Brick
```

The map strings are `Resources` paths inside your own project, so shipping a texture pack is
dropping files under any `Resources/` folder and naming them from Lua. A map you leave empty keeps
the base material's own texture, so a variant that only recolours a surface is three lines.
`StudsPerTile` is the variant's own tile width in studs — it always applies, exactly as in Roblox, so
a variant inherits its base material's *textures* but not its tiling.

Assignments take effect on the frame they are made. So does editing a variant that parts are already
wearing: changing its maps, its `BaseMaterial` or its `StudsPerTile` repaints every part using it, and
so does renaming, destroying or reparenting the variant itself — the shared material is mutated in
place rather than reallocated, so no part has to be touched. Naming a variant that does not exist
renders the plain `Material` instead; it is not an error, and nothing goes magenta. Variants and the
parts referencing them both survive a world save and load.

Supported today: `BaseMaterial`, `ColorMap`, `NormalMap`, `RoughnessMap`, `MetalnessMap`,
`StudsPerTile`. Roblox's `AlphaMode`, `MaterialPattern`, `CustomPhysicalProperties`, the emissive
properties and the `*Content` accessors are not implemented yet.

## A working mod

```lua
local RunService = game:GetService("RunService")
local uis = game:GetService("UserInputService")

-- Own everything under one Folder: destroying it removes the whole mod's world state.
local root = Instance.new("Folder")
root.Name = "MyGame"
root.Parent = workspace

local block = Instance.new("Part")
block.Name = "Player"
block.Size = Vector3.new(2, 1, 3)
block.Color = Color3.fromRGB(60, 140, 255)
block.Anchored = true            -- no physics; the script owns the position
block.Position = Vector3.new(0, 1, 0)
block.Parent = root

local cam = workspace.CurrentCamera
cam.CameraType = Enum.CameraType.Scriptable
cam.CFrame = CFrame.lookAt(Vector3.new(0, 9, 14), Vector3.new(0, 2, -18))

RunService.Heartbeat:Connect(function(dt)
    -- dt is seconds, so motion is frame-rate independent (speeds in studs/second).
    block.Position = block.Position + Vector3.new(0, 0, -8 * dt)
end)
```

Parent every object you create under one `Folder` you own. When the mod is disabled or unloaded the
runtime destroys the instances it created (tracked through the `OriginTag` ownership ledger), and a
single owned root makes that deterministic.

## Bundled sample mods

Five mods ship inside the Mods package at
`Assets/CoreAIMods/Runtime/Resources/CoreAIMods/` and are the reference for idiomatic usage:

| Mod | Ships | What it demonstrates |
|---|---|---|
| `sample_welcome` | **active** | Minimal mod: header manifest, `print`, a tick counter |
| `sample_lane_racer` | disabled | `RunService.Heartbeat`, `UserInputService` rising edges, scripted camera |
| `sample_tetris3d` | disabled | Grid logic in plain Lua tables, smooth part motion, restart |
| `sample_clicker` | disabled | `ClickDetector` 3D click-picking, no UI at all |
| `sample_castle3d` | disabled | A castle built from every `Enum.PartType` shape and 25+ `Enum.Material` values — a reference scene for materials, tiling and `Part.Color` tints |

The four opt-in ones ship `active: false`; the player turns them on from the **Hub → Mods** tab.

**Reloading cleans up the previous run.** A reload — **Save & run** on the Hub, `manage_mods reload`,
`ILuaModRuntime.ReloadMod` — removes the previous run's *startup objects* before the new main chunk runs
(`ModReloadMode.CleanStartupObjects`, the default): the instances that run's main chunk created up to its
first yield and that the mod still owns. A castle-building mod saved five times leaves one castle, not
six. Objects the mod's handlers, tasks or remote calls created later, and objects players or other mods
own, are kept; one of those sitting inside a removed startup object is moved out to its parent first. A
reload that fails puts the previous run's objects back exactly where they were and destroys what the
failed chunk built. `ModReloadMode.KeepObjects` (the Hub's **Keep objects on Save & run** toggle,
`manage_mods` `keep_objects: true`) is the old hot reload: everything stays and the new chunk builds next
to it. Details: [`mod-system.md`](../../../Docs/CoreAIMods/mod-system.md) §5c.

## Execution budget and `ScriptContext`

Every resume of mod code — the main chunk, a signal handler, a `task.*` resume, a raw
`coroutine.resume`, a one-off `execute_lua` chunk — runs under a per-resume budget with two halves:
an instruction-step cap and a wall-clock cap. CoreAI's defaults are 10,000 steps and 500 ms
(`LuaCsCoroutineHandle.DefaultBudgetPerResume` / `DefaultResumeTimeoutMs`). A handler that never
yields (`while true do end`) is cut when either half runs out, and the failure is reported as a budget
kill — `BUDGET_EXCEEDED`, with the bound and the author's line — not as a Lua error. Two budget trips
in a row (no clean call or frame between them) quarantine the mod and suspend its stored package, so a
mod stuck in a loop does not freeze the next start too (`LuaCsModRuntime.MaxBudgetTripsBeforeQuarantine`,
default 2; ordinary errors still quarantine at 8).

A budget trip (steps, time or memory) ends the run it tripped in, and nothing inside that run can
catch it: `pcall` and `xpcall` let it through and `xpcall`'s handler does not run, so a runaway
cannot survive its own budget, and the next run on the same Lua state is guarded exactly as the first
(before, one trip switched the guard off for that state for good). Only the host, or the code that
called `coroutine.resume` on a raw coroutine that tripped, sees the trip: `coroutine.resume` returns
`false` and the trip line, and that coroutine is dead. A sandbox cap (`string.rep`, `table.concat`,
`string.format`, a `gsub` result) and the per-call string-pattern step cap stay ordinary errors that
`pcall` catches.

An error is one line of text. `pcall`, `xpcall` and a protected `coroutine.resume` all get exactly the line the
failure raised — for a Roblox API call the `[mod:<id> script:<path> line:<n>] CODE: message | fix: ...` line,
for a sandbox cap (`string.rep`, `table.concat`, `string.format`, a `gsub` result) its own one-line text, which
starts with `sandbox: ` — never a CLR type name, a managed stack trace or a path on the machine; the line a
budget trip hands the host or a raw coroutine's resumer is such a one-line text too. A fault that ends a
scheduler thread is reported (to the mod's diagnostics and the auto-repair path) with the host error's own code
under one prefix — an uncaught `UNKNOWN_SERVICE` stays `UNKNOWN_SERVICE`; a budget kill is `BUDGET_EXCEEDED`,
and only a plain Lua error is reported as `BAD_ARGUMENT`.

The budget belongs to the game, not to CoreAI. Both halves are the **Lua coroutine resume budget**
field on `CoreAiModsLifetimeScope` (`LuaCsCoroutineBudgetSettings`); a value `<= 0` falls back to the
default rather than reading as "no limit". Every coroutine site in the composition resolves the same
instance and every resume re-reads it, so a change reaches a signal runner that was created long
before — on its next resume.

`game:GetService("ScriptContext"):SetTimeout(seconds)` changes the wall-clock half at runtime, live,
for every subsequent resume. Roblox marks the member `PluginSecurity`; CoreAI has no Studio, so the
member is gated to the host actor instead: host-composed code may call it, an ordinary mod is refused
with `NOT_AUTHORITY` (the same refusal as any other privileged member). A non-finite argument is
`BAD_ARGUMENT`; a value that rounds to zero milliseconds or below falls back to the default. There is
no readable `Timeout` property and no Lua-facing setter for the instruction half — Roblox has neither.

Instances are budgeted too: one actor may own at most 2,048 registered instances
(`LuaCsModRuntime.DefaultMaxRegisteredInstancesPerActor`), and a world at most its emergency ceiling
(16,384; 4,032 in a WebGL player). A creation past either is `BUDGET_EXCEEDED`, led by the call that
was refused — `Instance.new("Part")`, `cloning Part`, `TweenService:Create`, `restoring Part` — with a
fix hint to `Destroy()` what is no longer needed, for example `BUDGET_EXCEEDED: Instance.new("Part"):
actor '<id>' cannot register instance '<id>': registered instances quota reached (limit 2048) | fix:
destroy instances you no longer need with Instance:Destroy() before creating more`. A refusal that an
admission check writes as plain text stays an `InvalidOperationException` carrying that text.

## RemoteFunction timeout compatibility deviation

Roblox documents `RemoteFunction` invocation as yielding until the recipient responds, and explicitly
warns that an `InvokeClient` recipient which never returns can leave the sender yielded forever. CoreAI
intentionally bounds both `InvokeServer` and `InvokeClient` to **30 scheduler seconds**. A missing or
stalled receiver raises an error in this form:

```text
RemoteFunction invoke refused actor '<actor-id>' for remote '<full-name>': response timed out after 30 seconds
```

This is an explicit compatibility deviation for runtime liveness, especially in a single-threaded WebGL
player where an unbounded loopback request would otherwise leave the Lua caller permanently suspended.
Late responses are ignored. Use `RemoteEvent` when no response is required, and wrap a fallible invocation
in `pcall` when the mod can recover.

## Mutation envelopes, access control and disconnects

Every production path that can mutate the world runs under a **server-generated mutation envelope**:
the `execute_lua` tool (plain and MCP), a mod's main chunk, every scheduler resume
(`task.wait`/`task.spawn`/`task.delay`, `Heartbeat`, `RenderStepped`), deferred signal and
`RemoteEvent`/`RemoteFunction` handler dispatch, and cross-mod calls (which run under the callee's
actor). The AI never supplies an operation id, target instance id or expected revision — the tool
schemas expose only `code`. In an ACL-versioned world a caller that reaches the instance registry with
no envelope at all is refused with `BAD_ARGUMENT`, a duplicate operation id applies once, and the
engine-free `WorldAclAuthorizer` inside `CoreAI.RbxApi.Instances` refuses `SetAccessControl`, parent,
property and `Destroy` mutations by actor identity — the Lua bindings are no longer the only guard.

Inbound network input never creates identity: a `RemoteEvent`/`RemoteFunction` message from a sender
that the bridge has not admitted is refused with a structured error before any lookup, decoding or
`Player` allocation.

`RemoteEvent` and `RemoteFunction` argument envelopes have a fixed **65,536-byte UTF-8** wire limit.
CoreAI rejects larger inbound or outbound envelopes with `PAYLOAD_TOO_LARGE` and never truncates
them; inbound size is checked before the UTF-8/JSON string is materialised. Split large application
payloads across several events. An `UnreliableRemoteEvent` carries at most Roblox's 1,000 bytes, and
the in-process loopback refuses a larger one with `PAYLOAD_TOO_LARGE` exactly as the online transport
does, so a script that works in solo does not break online.

What a remote sender's calls start on the server is charged to that sender, never to the handler's
owner, in two budgets that never share threads (`ModScheduler`,
`Assets/CoreAIMods/Runtime/RbxApi/Instances/Scheduling/ModScheduler.cs`):

- **Remote admission (32 per sender).** Only the `OnServerEvent` handlers and `OnServerInvoke` callbacks
  the sender's own remote calls start count here; at most 32 may be alive (suspended) at once. A
  `RemoteFunction` call over that is answered `the RemoteFunction call was refused: threads this
  caller's earlier calls started are still running on the server`; an `OnServerEvent` invocation over it
  is dropped, counted and logged once per sender.
- **Induced work (128 per sender, 192 for all senders together).** Everything those handlers cause: a
  `task.spawn`, `task.defer` or `task.delay` from a charged thread (and every thread those start in
  turn), the signal handlers a charged thread's writes and fires start (`Changed`, attribute and
  `ChildAdded` signals, a `BindableEvent`, …), and the threads created inside a `:Wait()` that a charged
  fire resumed. The all-senders ceiling is the actor quota (256) minus a host reserve of 64, so the
  host's own work always has room. A `task.*` start over the budget raises `BUDGET_EXCEEDED` inside the
  handler; a handler that ends on that refusal is not charged to its owner's error streak. A signal
  handler invocation that finds the budget full is **deferred**, not dropped: it waits in a queue (256
  per sender, 4,096 in all) and starts, in the order the sender's fires happened, once the sender's
  induced threads end; only past the queue is it dropped, counted (`InducedListenerDrops`) and logged
  once per sender. A `Once` connection is used up only when its handler actually starts. The host's own
  fires never wait. A composition changes all four numbers with
  `ModScheduler.ConfigureInducedThreadBudget` (`TODO.md`: not yet exposed through
  `LuaCsModStackOptions`).

A flooding client exhausts its own budgets, not the host's thread quota, and an honest player's next
remote is never refused because the host's own listeners are parked on its earlier ones. Two things a
gameplay author may notice: the order is kept per sender only (a host write can overtake a flooder's
deferred one), and a host loop on `OnServerEvent:Wait()` misses fires while a flooder's resume of it is
deferred, as a Roblox `:Wait()` misses fires between two waits.

A `RemoteFunction` caller is answered with a fixed line whenever the callback does not return: `the
RemoteFunction callback was stopped: it exceeded its execution budget` for a budget cut; `the
RemoteFunction callback was stopped before it returned` when its mod was unloaded, reloaded or
quarantined, the host cancelled its thread, or it yielded outside the task scheduler (a callback that
dies before its first wait is answered at once, not after the 30 s timeout); `the RemoteFunction callback
was stopped: the world serving it was shut down`; and `the RemoteFunction callback could not start`. None
of these names a host mod — on a server the caller is a remote client; the host log keeps the details. An
error the callback raises reaches the caller as its error value (`error("boom")` arrives as `boom`,
without the engine's name or the internal chunk name); a host function's error still carries its
`[mod:<id> …]` prefix (`TODO.md`).

What a client sends cannot flood the server's log either. A malformed client payload is dropped and
counted instead of throwing inside the transport, and a failure text in a network warning is cut to
200 characters. Values a payload names that this world does not have — an unknown `EnumItem`, an
instance the sender cannot see — decode as `nil` and are reported per sender at its 1st, 2nd, 4th,
8th… such payload, with names cut to 64 characters (up to 256 senders are tracked apart, the rest share
one count); a sender is forgotten when its actor disconnects. On a client, a server payload naming an
instance its registry does not hold (a client registry is not a replica yet) decodes as `nil` too and is
reported at the same powers of two.

A mod whose first load fails leaves no `OnServerInvoke` callback, tween or pending wait behind. A load
or reload that fails puts back any `OnServerInvoke` or `OnClientInvoke` callback its chunk replaced or
cleared — another mod's included — as long as the slot is still empty and the callback's owner is still
live; a successful reload replaces the callbacks as before.

Disconnecting an actor is one production seam: it unregisters the actor from the bridge, fires
`Players.PlayerRemoving` **exactly once** (with the documented `PlayerExitReason`), releases the actor's
chat service, kills the actor's scheduler threads and drops its rate windows and client signals. A
second disconnect is a no-op, and other actors are untouched; 200 connect/disconnect cycles leave no
retained state.

The mods loaded for that actor are **unloaded** too (mods loaded with host authority stay). If mod
code is running at that moment — a mod whose script kicked its own player, from a hook, a timer, a
logic-slot formula or a scheduler thread — the unload waits until that code has returned (or until
the next `Tick`), so nothing it scheduled on the way out ever runs as the host. A mod in quarantine is
unloaded the same way. The stored package keeps its active flag and its source, so the mod starts
again when the world is rehydrated or reloaded; it is not restarted automatically when the actor
rejoins. A load or reload whose main chunk disconnects its own actor fails with an
`InvalidOperationException` ("mod '<id>' did not load: its actor '<actor>' disconnected while its
main chunk ran, …") and leaves nothing loaded.

## Saving and loading a world

A world is one `.world` ZIP container (`manifest.json` + `world.json` + indexed `Mods/NNNN/`
entries) holding the **world-owned** instance tree, world settings including `meters_per_stud`,
optional camera state, and the exact Lua mod sources. Mod-ephemeral subtrees (anything under a node
with an `OwnerModId`) are deliberately excluded: mods restart clean and recreate their own objects
after a load.

**Deviation from Roblox — `Archivable`.** Roblox omits instances with `Archivable = false` when a
place is saved. The CoreAI world package keeps them (the flag round-trips as durable state) so a
runtime restart reproduces the exact world an AI built; nothing is silently dropped on the way to
disk. Filter such instances yourself before `save_world` if you rely on the Roblox behaviour.

Four AI-facing tools on the Programmer role live on that format:

| Tool | What it does |
|---|---|
| `save_world` | Writes a **create-once** manual slot. It never overwrites or deletes an existing slot. A store keeps at most 64 manual slots and 256 MiB of them; past that the save is refused and writes nothing. |
| `load_world` | Cannot apply a package. It only returns `player_confirmation_required` plus a one-use request id. |
| `list_autosaves` | Lists the autosave ring: file `name`, `trigger`, UTC `timestamp` and `size` in bytes. |
| `load_autosave` | Same confirmation flow as `load_world`, for one autosave file `name` from that list. |

A manual slot name must be 1-64 letters, digits, `-` or `_` (surrounding whitespace is trimmed) and
must not be a reserved Windows device name such as `CON` or `LPT1`; an autosave name must be exactly
one `.world` file name with no directory part. The tools check the name before they touch the world
service: an invalid one comes back as an ordinary JSON result with `success: false` and an `error`
that names the parameter, states the rule and says the tool was not executed — the load tools also
set `status: "invalid_argument"` — so the model can correct the name and retry. `FileRbxWorldPackageStore`
still throws `ArgumentException` with the same rule text for C# callers that bypass the tools.

No failure crosses the tool boundary as an exception (only cancellation does). `save_world` reports a
capture failure as `capture_failed` and writes nothing. `load_world` and `load_autosave` report
`not_found` (a missing slot, or an autosave that rotated away), `invalid_package` (corrupt, truncated,
over the read limit, or a legacy package refused by a session composed with a world ACL),
`read_failed`, `network_sessions_active` (a load is refused while remote players are connected
through a network bridge, because live sessions cannot be handed to another world yet) and
`session_unavailable` (the game is shutting its world session down); no request is created. A package
the confirmation would refuse — an active `Full`-capability mod, a source store that cannot replace a
source set atomically — is `invalid_package` before the player is asked. `list_autosaves` reports a
store failure as `list_failed`. No tool can choose the startup selection below. An `execute_lua` call
whose world a confirmed load replaced while it waited or ran is told so: none of its changes reached
the live world, and the loaded world's mods are already running.

A world stores at most 256 distinct mod sources, the most one package holds: a load that would add a
257th is refused before its chunk runs, with a hint to `manage_mods` action `forget` (an `unload`
keeps the source), and a load whose source the store did not keep is undone with the reason in the
tool result.

The load flow is deliberately fail-closed: host or UI code subscribes to `ManualLoadConfirmationRequested` (or
reads `GetPendingManualLoads`) and calls `ConfirmManualLoadAsync(requestId, true|false)`. The built-player **Hub
→ World Loads** page renders those pending requests and is the surface where the player accepts or rejects one.
A world the player confirms there also **reopens on the next start**: it is recorded as the durable startup
selection (`Saves/Startup`), restored at boot through the same staged swap, and the default world opens instead
on any failure. While that world stays live, the selection follows it: every change through `execute_lua` or a
mutating `manage_mods` action records the world again, so the changes the AI makes after the confirmation reopen
too. A change to the mod sources is recorded at once — from a tool, the Hub **Mods** page or host code alike —
and a change to the world tree alone at most once every 5 s
(`RbxWorldRuntimeSessionController.StartupRefreshInterval`); a call that changed neither writes nothing, and
physics or the camera moving never counts as a change. A world with an active `Full`-capability mod is not
recorded (the startup restore would refuse it): the previous entry stays, a `manage_mods` result says so in a
`startup_warning` field and `execute_lua` appends the note to its output. A record that fails keeps the previous
one, is logged, and leaves the same kind of note in the tool result and in
`RbxWorldRuntimeSessionController.StartupSelectionNote`. The page shows `Opens on start: <world>` and a **Start
with the default world next time** button. Requests expire after two minutes by default, a newer request for the
same slot replaces the older one, and expired, unknown, rejected, or reused ids never touch the live session.

**Autosaves are separate and automatic.** `ConfirmedWorldMutationGate` sits in front of every
`execute_lua` call that carries code (trigger `execute_lua`; an empty or whitespace-only `code` is
refused with `Lua code is required` before any capture) and every *mutating* `manage_mods` action — `load`,
`reload`, `unload`, `import`, `forget`, `revert` (trigger `manage_mods-<action>`). It captures the
world and writes an autosave *before* the mutation runs; if the capture or the write fails, the
mutation does not happen and the tool returns a structured failure. A confirmed load takes the same
gate, so its `load_world-pre` safety autosave includes an `execute_lua` that was in flight. Read-only
`manage_mods` actions
(`list`, `get_source`, `export`, `versions`, `diagnostics`) bypass the gate entirely. Autosaves
rotate in a ring with a two-phase durability protocol; manual slots are never rotated or rewritten
by the gate.

A confirmed load replaces the runtime session rather than patching it: sources are written to an
isolated version directory and flushed to durable storage first, then a fresh registry, Rbx binding
layer, and Lua stack are staged and published atomically. `ILuaModRuntime`, `LuaCsModStack`,
`LuaCsLogicSlots`, and `ILuaModSourceStore` are stable facades, so a held reference keeps working
across the swap. An active mod that requests the `Full` capability is rejected before staging,
because arbitrary Unity reflection cannot be transactionally isolated.

On WebGL the durability boundary is `CoreAiWebGlPersistence.SyncAsync()`, which returns immediately
and reports whether the engine's automatic `persistentDataPath` persistence is armed for this page.
It waits for nothing: Unity 6.3 deprecated the manual `FS.syncfs` channel and its completion callback
never fired, so awaiting it parked every save until the caller's own timeout. The browser player also refuses to write packages above 4 MiB, more than 4,096
instances, more than 32,768 collection items, or more than 2 MiB of text before entering unbounded
work, and to read one above 4 MiB — those are WebGL execution limits, not format limits.

Format, validation limits, capture/ownership rules, and the compatibility policy are specified in
[`Docs/CoreAIMods/WORLD_PACKAGE.md`](../../../Docs/CoreAIMods/WORLD_PACKAGE.md).

## Platform support

The whole surface runs under **IL2CPP**, including WebGL at managed stripping level **Medium** —
verified on a WebGL player where the DI container builds, bundled mods seed, and mod-driven
`Instance.new` spawns visible parts. The stripping protection comes from the `link.xml` shipped in
the CoreAiUnity package; add your own assemblies to your project's `link.xml` if you resolve them
through DI or reflection.

## Related

- [LUA_GAME_API.md](LUA_GAME_API.md) — the mod runtime itself (hooks, store, events, cross-mod exports)
- [FIRST_MOD.md](FIRST_MOD.md) — writing and loading your first mod
- [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md) — capability tiers and what each one opens
- [WORLD_PACKAGE.md](../../../Docs/CoreAIMods/WORLD_PACKAGE.md) — the `.world` package format, validation limits, autosave durability, and session replacement
- [PROCEDURAL_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) · [TEXTURE_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) — the `Enum.Material` render catalogs
- [CHARACTER_MOTOR_BRIDGE.md](../../../Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md) — driving `Humanoid` with your own character controller via `IRbxCharacterMotor`/`IRbxCharacterMotorProvider`
