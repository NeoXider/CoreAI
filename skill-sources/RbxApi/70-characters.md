# Humanoid, Players, Player.Character, respawn

- `Instance.new("Humanoid")` — health, movement parameters and a small state machine; NOT a full
  character rig (no body parts, no auto-created Model/Script). Defaults: `MaxHealth` 100, `Health`
  100, `WalkSpeed` 16, `JumpPower` 50, `JumpHeight` 7.2, `UseJumpPower` true.
- `Health` is clamped to `[0, MaxHealth]`; lowering `MaxHealth` clamps `Health` down with it.
  `:TakeDamage(amount)` lowers Health (a negative amount heals). Reaching 0 fires `Died` exactly
  once; a dead Humanoid does not resurrect from further Health writes and stays `IsDead == true`.
  There is NO passive health regeneration in the class itself (the mirror puts that in a Script
  inserted into the character; nothing heals a CoreAI Humanoid on its own).
  `Died` fires only inside `workspace` (a Humanoid at 0 outside it dies on its first Heartbeat
  inside). `MaxHealth = math.huge` is allowed, but then `Health == math.huge` is false — compare
  with `MaxHealth`; `TakeDamage(math.huge)` kills; NaN is BAD_ARGUMENT. `JumpPower` is clamped to
  [0, 1000]. TakeDamage/MoveTo/ChangeState need WorldEdit and write access to that Humanoid, so a
  mod cannot damage another actor's character (route PvP damage through server code). Writes to
  Health, MaxHealth, WalkSpeed, JumpPower, JumpHeight, UseJumpPower and DisplayName fire Changed.
- `:MoveTo(position)` walks toward `position` and fires `MoveToFinished(reached: bool)` — `true`
  on arrival (within ~1 stud on the ground plane; height is ignored), `false` after an
  8-SCALED-second timeout (a paused world never times it out), and `false` when a script or a
  tween moves the HumanoidRootPart (CFrame/Position/Orientation/Rotation, or PivotTo).
  `:MoveTo(position, part)` (follow a moving part) is refused with BAD_ARGUMENT, not silently
  downgraded. Setting `Jump = true` requests one jump (fires `Jumping(true)`) using
  `JumpPower`/`JumpHeight`/`UseJumpPower`; jumping and moving both do nothing while dead.
- `:Move(direction, relativeToCamera = false)` walks along a direction until called again;
  `Vector3.new(0, 0, -1)` with `true` walks where the camera looks (flattened to the ground). A
  non-zero Move ends a MoveTo in progress (MoveToFinished(false)); `Move(Vector3.zero)` stops.
  The bundled `coreai_player_controls` mod already drives the local character with WASD/arrows,
  the gamepad left stick and Space; turn it off with
  `game:GetService('StarterPlayer').DevComputerMovementMode = Enum.DevComputerMovementMode.Scriptable`
  before writing your own controls.
- `:GetState()` -> an `Enum.HumanoidStateType` item; `StateChanged(old, new)` fires on transitions
  the character motor reports (e.g. Freefall/Landed/Running from ground contact). `:ChangeState`
  may force `Enum.HumanoidStateType.Jumping`, `Flying` (free flight: no gravity, Move keeps its Y
  part; CoreAI-specific) and `Freefall` (ends a flight) — any other state raises NOT_IMPLEMENTED. `Sit`
  is a loud stub (`h.Sit = true` raises NOT_IMPLEMENTED).
- `MoveDirection` (read-only Vector3) mirrors the character controller's current motion.
- `humanoid:Clone()` keeps Health, MaxHealth, WalkSpeed, JumpPower, JumpHeight, UseJumpPower and
  DisplayName; a clone of a dead Humanoid (Health 0) dies on its first Heartbeat in `workspace`.

## Players & characters

- `game:GetService("Players")` — `:GetPlayers()` -> array of connected Players; `:GetPlayerByUserId(id)`
  and `:GetPlayerFromCharacter(model)` -> Player or nil (never error on a miss; only live Players
  under Players count). `.PlayerAdded(player)`
  and `.PlayerRemoving(player, reason)` fire on connect/disconnect. `Players.LocalPlayer` is the Player
  of the actor a mod was loaded for (the bundled control script runs that way); a mod the host loads is
  a server script and reads nil: reach players there with `GetPlayers()` and `PlayerAdded`.
- `Players.CharacterAutoLoads` (bool, default true) — when true, a joining actor gets a character
  automatically. Set it from HOST composition, not from a script: a joining actor can already be
  connected (and its character already queued to spawn) before any mod chunk has run, so a script
  cannot reliably race the join to flip this in time.
- `Players.RespawnTime` (number, studs-per-second-free — plain seconds, default 5) — how long after
  a character's `Humanoid.Died` fires before a fresh one autoloads, when `CharacterAutoLoads` is
  still true when the timer elapses.
- `player.Character` — the Model driven for this player, or nil until loaded. Assigning it directly
  does NOT fire the signals below (only `LoadCharacterAsync` does), though it fires `Changed`.
- `player.CharacterAdded(character)` / `player.CharacterRemoving(character)` fire on load/replace.
  Replacing an existing character fires `CharacterRemoving` for the outgoing one BEFORE
  `CharacterAdded` for its replacement.
- `player:LoadCharacterAsync()` — builds (or rebuilds) the character: a Model named after the player,
  parented to `workspace`, holding a `Humanoid` and the R6 body (section `rig`;
  `humanoid.RootPart` is its `HumanoidRootPart`). YIELDS until the deferred CharacterAdded/CharacterRemoving handlers have run, so code after
  the call sees the finished character and its signals already delivered. `player:LoadCharacter()` is
  a deprecated non-yielding alias (logs once per mod) with the same effect.
- `player:DistanceFromCharacter(point)` -> studs from the character's root part to `point`, or `0`
  when the player has no character (never errors on a characterless player).
- A character Model has `Archivable = false`, so `character:Clone()` returns nil — set
  `character.Archivable = true` first to copy it.
- `player:Kick([message])` removes the player; the message (a string, cut to 1,024 UTF-8 bytes) is
  shown to that player's client. A number is sent as its tostring text; any other non-string is
  BAD_ARGUMENT.
    local Players = game:GetService("Players")
    Players.PlayerAdded:Connect(function(player)
      player.CharacterAdded:Connect(function(character)
        character.Humanoid.Died:Connect(function() print(player.Name .. " died") end)
      end)
    end)
