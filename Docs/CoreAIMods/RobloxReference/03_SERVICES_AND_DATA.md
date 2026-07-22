# 03 — Services & Data: DataStores, Players, Humanoid, TweenService, Common Services, GUI, Assets

**Status:** NORMATIVE reference for CoreAI's Roblox-like mod system.
**Researched:** 2026-07-22 against current official Roblox documentation (create.roblox.com/docs). Every section lists its sources. Claims that could not be verified against current docs are marked **UNCERTAIN**.
**Rule numbering:** `S<section>.<n>`. Each rule is written to be testable by a conformance suite.
**CoreAI context:** Unity host; persistence is key-value JSON stores; a DataStore emulation layer is planned on top of them.

---

## 1. DataStoreService

### 1.1 Store acquisition

- **S1.1:** `DataStoreService:GetDataStore(name, scope)` returns a `DataStore` (subclass of `GlobalDataStore`) identified by `name` plus an optional `scope` (default scope is `"global"`). The same `(name, scope)` pair from any server refers to the same backing store. The call itself does not yield and does not hit the network.
- **S1.2:** `DataStoreService:GetOrderedDataStore(name, scope)` returns an `OrderedDataStore` — a separate namespace from the regular data store of the same name.
- **S1.3:** Name, key, and scope are each limited to **50 characters**. Exceeding a limit raises a validation error (error-code family 101–107).

### 1.2 Core operations — yield behavior and return values

All five core operations **yield** (suspend the calling Luau coroutine until the backend responds). A compat layer must therefore model them as async operations that suspend the calling script, and must support `pcall` around them, since every one of them can throw on network/validation/throttle failure.

- **S1.4 GetAsync:** `GetAsync(key, options?) -> (value, DataStoreKeyInfo)`. Returns the stored value **and** a `DataStoreKeyInfo` instance ("The value of the entry in the data store with the given key and a `DataStoreKeyInfo` instance"). Returns `(nil, nil)` if the key has never been written.
- **S1.5 GetAsync caching:** "Keys are cached locally for 4 seconds after the first read." Within that window, repeated `GetAsync` on the same key returns the cached value and does **not** count against request budgets. "Modifications to the key by `SetAsync()` or `UpdateAsync()` apply to the cache immediately." Consequently `GetAsync` "sometimes can be out of sync with the backend." `DataStoreGetOptions.UseCache = false` bypasses the cache.
- **S1.6 SetAsync:** `SetAsync(key, value, userIds?, options?)` writes unconditionally (last-writer-wins) and returns "the version identifier of the newly created version" (a string). `userIds` is a table of user IDs to tag the key with; `options` is a `DataStoreSetOptions` whose `SetMetadata(table)` attaches user-defined metadata. Docs warn that `SetAsync` "can cause data inconsistency if two servers try to set the same key at the same time."
- **S1.7 UpdateAsync transform contract:** `UpdateAsync(key, transformFunction) -> (updatedValue, DataStoreKeyInfo)`. The transform receives `(currentValue, DataStoreKeyInfo)` — the "current value of the key prior to the update" and a `DataStoreKeyInfo` "that contains the latest version information". It may return up to three values: the new value, "an array of UserIds", and a metadata table.
- **S1.8 UpdateAsync cancel-on-nil:** "If the callback returns `nil` instead, the current server will stop attempting to update the key" — returning `nil` (or nothing) **cancels the write**; the stored value is untouched and no new version is created.
- **S1.9 UpdateAsync retry-on-conflict:** If another server updated the key between read and write, the engine "will call the function again, discarding the result of the previous call" — the transform must be a **pure function of its inputs** (idempotent, no side effects), because it may run any number of times per call.
- **S1.10 UpdateAsync no-yield:** "The callback function cannot yield." Calling a yielding function inside the transform is an error.
- **S1.11 UpdateAsync read source:** `UpdateAsync` "reads the current key value from the server that last updated it before making any changes" — i.e. it performs a fresh authoritative read (not the 4-second cache) as the transform input.
- **S1.12 IncrementAsync:** `IncrementAsync(key, delta = 1, userIds?, options?)` atomically adds `delta` to a numeric value and returns the updated value. Both the stored value and delta "must be integers." Incrementing a missing key treats it as 0.
- **S1.13 RemoveAsync:** `RemoveAsync(key) -> (value, DataStoreKeyInfo)` — returns "the value of the data store prior to deletion and a `DataStoreKeyInfo` instance." In versioned stores removal writes a tombstone; old versions remain listable until expiry.

**Canonical idiom (the shape a compat layer must run unmodified):**

```lua
local DataStoreService = game:GetService("DataStoreService")
local store = DataStoreService:GetDataStore("PlayerData")

-- Read (S1.4/S1.5): pcall + tuple return
local ok, value, keyInfo = pcall(function()
    return store:GetAsync("Player_" .. player.UserId)
end)

-- Conflict-safe write (S1.7-S1.11): pure transform, may run multiple times
local ok2, newValue = pcall(function()
    return store:UpdateAsync("Player_" .. player.UserId, function(current, keyInfo)
        current = current or { coins = 0 }
        current.coins += 50
        -- returning nil here would abort the write (S1.8)
        return current, keyInfo and keyInfo:GetUserIds(), keyInfo and keyInfo:GetMetadata()
    end)
end)
```

### 1.3 Value constraints

- **S1.14:** Values must be JSON-encodable: `nil`, boolean, number, string, and tables that are either arrays or dictionaries with string keys, containing only these types. Instances, functions, userdata (Vector3, CFrame, …), mixed-key tables, and cyclic tables are not storable — mods must serialize such data manually (typically via `HttpService:JSONEncode`-compatible shapes).
- **S1.15:** Maximum serialized value size per key is **4,194,304 characters** (4 MB). Exceeding it fails the write with a validation error.

### 1.4 Versioning

- **S1.16:** "Versioning happens when you set, update, and increment data. The functions SetAsync(), UpdateAsync(), and IncrementAsync() create versioned backups of your data using the first write to each key in each UTC hour." Later writes within the same UTC hour overwrite that hour's latest version.
- **S1.17:** "Versioned backups expire 30 days after a new write overwrites them. The latest version never expires."
- **S1.18:** `DataStore:ListVersionsAsync(key, sortDirection = Ascending, minDate = 0, maxDate = 0, pageSize = 0) -> DataStoreVersionPages` enumerates version records, filterable by time range. `DataStore:GetVersionAsync(key, version) -> (value, DataStoreKeyInfo)` reads a specific version; version identifiers are **strings**. `GetVersionAtTimeAsync(key, timestamp)` reads the version current at a moment in time. `RemoveVersionAsync(key, version)` deletes one version (leaving a tombstone) and is marked deprecated in the current class reference.
- **S1.19 DataStoreKeyInfo:** exposes `CreatedTime` and `UpdatedTime` (epoch milliseconds), `Version` (string), `:GetUserIds()`, and `:GetMetadata()`.

### 1.5 Metadata & UserIDs tagging

- **S1.20:** Two metadata classes exist: service-defined (create/update time, version) and user-defined, set via `DataStoreSetOptions:SetMetadata(table)` on `SetAsync`/`IncrementAsync`, or via the third return value of an `UpdateAsync` transform. Read back through `DataStoreKeyInfo:GetMetadata()`.
- **S1.21:** Metadata limits: key name ≤ 50 chars, individual value ≤ 250 chars, total serialized metadata ≤ 300 chars per key.
- **S1.22:** `userIds` tagging exists to satisfy privacy/IP tracking (GDPR right-to-erasure lookups). **UNCERTAIN:** the historical limit of at most **4 user IDs per key** is no longer stated in the current public docs pages we checked; treat "small fixed cap, 4" as the compat target but verify before enforcing.
- **S1.23:** **Important interaction:** a `SetAsync`/`UpdateAsync` write that does not re-supply `userIds`/metadata clears them (metadata is per-version, not sticky). Compat layer should reproduce this per-write association.

### 1.6 Budgets, throttling, queueing, errors

Requests are budgeted **per request type**, budgets accrue over time and scale with player count. When a budget is exhausted, requests are queued; when the queue overflows, they fail.

| Limit | Value (current docs) |
|---|---|
| Server-level, standard store — Read (`GetAsync`) | `60 + numPlayers × 40` per minute |
| Server-level, standard store — Write (`Set/Increment/Update`) | `60 + numPlayers × 40` per minute |
| Server-level, standard store — Remove | `60 + numPlayers × 40` per minute |
| Server-level, standard store — List | `5 + numPlayers × 2` per minute |
| Server-level, ordered store — Write/Remove | `30 + numPlayers × 5` per minute |
| Experience-level — Read | `300 + concurrentUsers × 40` per minute |
| Experience-level — Write | `300 + concurrentUsers × 20` per minute |
| Per-key throughput — Read | 25 MB per minute (rolling 60 s window) |
| Per-key throughput — Write | 4 MB per minute (rolling 60 s window) |
| Throttle queue | 30 requests per queue; overflow errors 301–306 |
| Data store name / key / scope | 50 chars each |
| Value size | 4,194,304 chars per key |
| Metadata | 300 chars total per key |

- **S1.24:** `DataStoreService:GetRequestBudgetForRequestType(Enum.DataStoreRequestType)` returns the remaining budget for a request class; well-behaved code polls it and defers work instead of spamming.
- **S1.25:** Error-code families a compat layer should reproduce (or map): 1xx validation (bad key/value/parameters), 3xx throttled-and-queue-full ("Request was throttled but queue was full"), 4xx access (API access disabled, service unavailable), 5xx backend/parse errors.
- **S1.26 Studio behavior:** "By default, games tested in Studio can't access data stores." Access requires the *Enable Studio Access to API Services* setting; even then Studio shares the live game's data — a footgun the docs warn about. CoreAI's editor/play-mode should have an analogous default-off switch (or a sandboxed local store) for "editor" sessions.

### 1.7 OrderedDataStore

- **S1.27:** Values must be **positive integers** (no tables, strings, floats). Ordered stores do **not** support versioning or metadata; "DataStoreKeyInfo is always nil for keys in an OrderedDataStore."
- **S1.28:** `GetSortedAsync(ascending, pagesize, minValue?, maxValue?) -> DataStorePages`. Iterate with `pages:GetCurrentPage()` (array of `{key, value}` entries) and `pages:AdvanceToNextPageAsync()` (yields; errors past the last page; `pages.IsFinished` is true on the last page). This is the canonical global-leaderboard mechanism.
- **S1.29:** Ordered stores share `GetAsync/SetAsync/UpdateAsync/IncrementAsync/RemoveAsync` from `GlobalDataStore` (with the integer restriction) but have their own, lower, write budgets (see table).

**Canonical leaderboard-page idiom:**

```lua
local ods = DataStoreService:GetOrderedDataStore("HighScores")
local pages = ods:GetSortedAsync(false, 10)      -- descending, 10 per page
while true do
    for rank, entry in ipairs(pages:GetCurrentPage()) do
        print(rank, entry.key, entry.value)       -- entry.key / entry.value contract (S1.28)
    end
    if pages.IsFinished then break end
    pages:AdvanceToNextPageAsync()                -- yields; consumes List budget
end
```

### 1.8 MemoryStoreService (contrast)

- **S1.30:** `MemoryStoreService` is the *ephemeral* counterpart: "fast in-memory data storage accessible from all servers in a live session," offering SortedMap, Queue, and HashMap structures. Items always carry an expiration (maximum 45 days) and vanish when it lapses; quota scales with player count (64 KB + 1.2 KB per user; requests 1000 + 120 × concurrent users per minute). Use it for matchmaking, cross-server queues, live leaderboards, shared caches — never as durable player data. CoreAI mapping: an in-process (or Unity-networking-backed) TTL dictionary satisfies the contract; durability must NOT be provided, or mods will depend on it.

### 1.9 Session locking and the ProfileService/ProfileStore pattern (community standard)

The engine itself provides **no session locking**: two servers can both hold a player's data and clobber each other with `SetAsync`. The community-standard consistency layer (ProfileService, and its successor ProfileStore) implements, on top of `UpdateAsync`:

1. **Lock acquisition on load** — the profile value embeds lock metadata (session/server id + timestamp); a server takes the lock inside an `UpdateAsync` transform, and other servers either wait, steal a stale lock (crash recovery via lock timeout), or kick the player.
2. **Single in-memory source of truth** — during the session, all mutation happens on the in-memory table; the store is only written by periodic autosaves and a final release-write.
3. **Release on leave / BindToClose** — `PlayerRemoving` and `game:BindToClose` flush and release the lock so the next server can load immediately.

- **S1.31:** CoreAI's save layer should provide session-locking semantics natively (per-player profile lock + load/release lifecycle), because every serious Roblox game assumes this pattern; emulating raw DataStores without it reproduces Roblox's worst data-loss traps.
- **S1.32:** `game:BindToClose(callback)` gives shutdown callbacks up to 30 seconds to flush data; a compat layer needs an equivalent application-quit hook. (30-second figure is long-standing; **UNCERTAIN** only in that we did not re-verify the exact number this pass.)

**Sources (1):**
- https://create.roblox.com/docs/cloud-services/data-stores
- https://create.roblox.com/docs/cloud-services/data-stores/error-codes-and-limits
- https://create.roblox.com/docs/cloud-services/data-stores/versioning-listing-and-caching
- https://create.roblox.com/docs/reference/engine/classes/GlobalDataStore
- https://create.roblox.com/docs/reference/engine/classes/DataStore
- https://create.roblox.com/docs/reference/engine/classes/DataStoreKeyInfo
- https://create.roblox.com/docs/reference/engine/classes/OrderedDataStore
- https://create.roblox.com/docs/cloud-services/memory-stores
- https://devforum.roblox.com/t/profileservice/667805 (community pattern)
- https://devforum.roblox.com/t/profilestore/3190543 (community pattern, successor)

---

## 2. Players service & player data

- **S2.1:** `Players.PlayerAdded(player)` fires when a player joins; `Players.PlayerRemoving(player, reason)` fires as they leave (note: current signature includes an `Enum.PlayerExitReason`). `Players:GetPlayers()` returns the current player list. Scripts must handle players who joined *before* the script connected (iterate `GetPlayers()` after connecting) — a canonical Roblox idiom the compat layer's event ordering must permit.
- **S2.2:** `Players.LocalPlayer` is non-nil only on the client ("Read Only, Not Replicated"); server scripts see it as nil. `Players:GetPlayerFromCharacter(model)` maps a character Model back to its Player.
- **S2.3:** Key `Player` properties: `UserId` (number; stable unique account id — THE canonical save key: `"Player_" .. player.UserId`), `Name` (unique login name), `DisplayName` (non-unique display string), `Character` (Model or nil), `Team`/`TeamColor`/`Neutral`, `RespawnLocation`. `Player.CharacterAdded(character)` / `CharacterRemoving(character)` bracket each spawn.
- **S2.4:** `Players.CharacterAutoLoads` (default true) controls automatic spawning; with it off, `Player:LoadCharacterAsync()` spawns manually. `Players.RespawnTime` sets the delay before auto-respawn.
- **S2.5 leaderstats contract:** the default player-list leaderboard displays stats from a container named **exactly `leaderstats`** (all lowercase — "Roblox doesn't add the player to the leaderboard if you name it any other way"), parented **directly to the Player**. Children of type `IntValue`, `NumberValue`, or `StringValue` become columns; each child's `Name` is the column header and its `Value` is displayed live. Ordering: creation order by default; a `BoolValue` child named `IsPrimary` (true) or a `NumberValue` child named `Priority` reorders ("IsPrimary takes precedence over any Priority values"). Hide the whole list via `StarterGui:SetCoreGuiEnabled(Enum.CoreGuiType.PlayerList, false)` from a LocalScript.
  - *CoreAI mapping:* implement `leaderstats` as a data-driven scoreboard: watch for a child container named `leaderstats` on the player entity, mirror its Value-object children into the default UI. The container is conventionally a `Folder` but any Instance works; match by name, not by class.
**Canonical leaderstats idiom (must produce a working default leaderboard):**

```lua
game:GetService("Players").PlayerAdded:Connect(function(player)
    local leaderstats = Instance.new("Folder")
    leaderstats.Name = "leaderstats"          -- exact lowercase name (S2.5)
    leaderstats.Parent = player               -- direct child of Player

    local gold = Instance.new("IntValue")
    gold.Name = "Gold"                        -- column header
    gold.Value = 0                            -- displayed live
    gold.Parent = leaderstats
end)
```

- **S2.6:** `StarterGui` is a server-side template container: "When a player joins a game and their character first spawns, the ScreenGui and its contents clone into the PlayerGui container for that player." Each player owns a `PlayerGui` under their Player object; scripts mutate the *clone* in PlayerGui, never StarterGui, at runtime. Respawn behavior is governed by `ScreenGui.ResetOnSpawn` (see S6.8).
- **S2.7 Attributes:** every Instance (Players included) supports attributes: `SetAttribute(name, value)`, `GetAttribute(name)`, `GetAttributes()`, `GetAttributeChangedSignal(name)`, `AttributeChanged`. Supported value types: "string, boolean, number, UDim, UDim2, BrickColor, Color3, Vector2, Vector3, CFrame, NumberSequence, ColorSequence, NumberRange, Rect, Font." Attributes "are saved with your place and assets" and "are replicated so that clients can access them immediately." Names may not start with the reserved `RBX` prefix and are length-limited (**UNCERTAIN:** exact cap, commonly cited as 100 chars, not re-verified this pass). Attributes are the idiomatic NoCode-ish data channel for mods — CoreAI should back them with its serializable key-value component and replicate them server→client.

**Sources (2):**
- https://create.roblox.com/docs/reference/engine/classes/Players
- https://create.roblox.com/docs/reference/engine/classes/Player
- https://create.roblox.com/docs/players/leaderboards
- https://create.roblox.com/docs/studio/properties (instance attributes)
- https://create.roblox.com/docs/ui/on-screen-containers (StarterGui→PlayerGui)

---

## 3. Humanoid contract

The `Humanoid` is the component that turns a character Model into a walking, jumping, damageable avatar. CoreAI maps it onto its character-controller + Health module; the numbered defaults below are the compatibility surface mods rely on.

- **S3.1 Health:** `Humanoid.Health` (default 100) "is restricted to the range between 0 and MaxHealth" — writes are clamped, never rejected. `MaxHealth` default 100. When Health reaches 0, the humanoid dies. "By default, a passive health regeneration script is automatically inserted" (regenerates 1% of MaxHealth per second unless a script named `Health` is present in the character) — reproduce both the regen and its opt-out convention.
- **S3.2 TakeDamage vs direct Health:** `Humanoid:TakeDamage(amount)` "lowers the Health ... if it is not protected by a ForceField"; writing `Humanoid.Health -= x` directly **ignores ForceFields**. A compat layer must keep these two damage paths distinct.
- **S3.3 Died:** "This event fires when the Humanoid dies, usually when Health reaches 0." Fires once per life; typical mod code connects per `CharacterAdded`.
- **S3.4 WalkSpeed:** default **16** studs/s (seeded from `StarterPlayer.CharacterWalkSpeed`). Setting it to 0 immobilizes walking without anchoring.
- **S3.5 Jump — two mechanics:** `UseJumpPower` (default **true**) selects between `JumpPower` ("defaults to 50 and is constrained between 0 and 1000") and `JumpHeight` (default **7.2** studs, from `StarterPlayer.CharacterJumpHeight`). Only the selected property has effect; a compat layer should implement jump as impulse-derived-from-whichever-is-active.
- **S3.6 AutoRotate:** "describes whether or not the Humanoid will automatically rotate to face in the direction they are moving"; default true. Turning it off is the standard idiom for strafe/aim characters.
- **S3.7 MoveTo/Move:** `Humanoid:MoveTo(position, part?)` walks toward a world position; "the movement operation will time out after 8 seconds if the humanoid doesn't reach its goal", after which `MoveToFinished` fires with `reached = false` (true if reached within the timeout). The refresh-before-8-seconds loop is a canonical NPC idiom. `Humanoid:Move(direction, relativeToCamera?)` sets a continuous walk direction ("causes the Humanoid to walk in the given Vector3 direction") and must be re-asserted each frame by controllers.
- **S3.8 States:** `Enum.HumanoidStateType` (Running, Freefall, Jumping, Seated, Climbing, Swimming, Ragdoll, Dead, Physics, …) with `GetState()`, `ChangeState(state)`, `SetStateEnabled(state, bool)` and the `StateChanged` event. Minimum viable compat: Running / Jumping / Freefall / Seated / Dead plus `SetStateEnabled(Jumping, false)` to block jumping.
- **S3.9:** Convenience flags `Sit` and `PlatformStand` (booleans) force seated / physics-limp states.
**Canonical character/NPC idioms:**

```lua
-- Per-life wiring (S2.3 + S3.3): CharacterAdded -> find Humanoid -> Died
player.CharacterAdded:Connect(function(character)
    local humanoid = character:WaitForChild("Humanoid")
    humanoid.Died:Connect(function()
        print(player.Name .. " died")
    end)
end)

-- ForceField-respecting damage (S3.2)
humanoid:TakeDamage(25)

-- NPC walk with 8-second timeout handling (S3.7)
npcHumanoid:MoveTo(targetPosition)
local reached = npcHumanoid.MoveToFinished:Wait()   -- false if timed out
```

- **S3.10 UNCERTAIN / evolving:** Roblox is actively evolving character control (new `ControllerManager`/`ControllerPartSensor` physics controllers, avatar unification); `Humanoid` itself is not deprecated (only members like `LoadAnimation` are), but treat anything beyond S3.1–S3.9 — especially physics-state minutiae, HipHeight math, and the new controller stack — as UNSTABLE surface. CoreAI should target the property/event contract above, not Humanoid's internal state machine.

**Sources (3):**
- https://create.roblox.com/docs/reference/engine/classes/Humanoid
- https://github.com/Roblox/creator-docs/blob/main/content/en-us/reference/engine/classes/Humanoid.yaml (exact defaults quoted)

---

## 4. TweenService

- **S4.1 TweenInfo:** `TweenInfo.new(time = 1, easingStyle = Enum.EasingStyle.Quad, easingDirection = Enum.EasingDirection.Out, repeatCount = 0, reverses = false, delayTime = 0)`. `repeatCount < 0` loops forever (long-standing behavior; the datatype page documents only the default — **UNCERTAIN** flag limited to the negative-value wording, not the behavior, which the TweenInfo guide examples rely on). `reverses = true` plays back to the start each cycle; `delayTime` also applies between repeats.
- **S4.2 Create:** `TweenService:Create(instance: Instance, tweenInfo: TweenInfo, propertyTable: Dictionary) -> Tween`. The dictionary maps property *names* (strings) to target values. The Tween is bound to that one instance; `Create` itself does nothing until `Play`.
- **S4.3 Tweenable types (exact list):** `number`, `boolean`, `CFrame`, `Rect`, `Color3`, `UDim`, `UDim2`, `Vector2`, `Vector2int16`, `Vector3`, `EnumItem`. Discrete types (bool, EnumItem) snap; continuous types interpolate. Anything else in the property table is an error.
- **S4.4 Play/Pause/Cancel:** `Play()` starts or resumes. `Pause()` halts, *preserving* progress — a later `Play()` resumes from the paused position. `Cancel()` halts and **resets progress variables**: after `Cancel()`, `Play()` restarts and "the tween will take 5 seconds to complete as the tween variables have been reset by tween:Cancel()" (docs example, 5 s tween). Cancel does **not** reset already-written property values — the instance stays wherever it was.
- **S4.5 Completed:** `Tween.Completed(playbackState: Enum.PlaybackState)` fires when the tween finishes **or** is cancelled; handlers must check the argument (`Enum.PlaybackState.Completed` for natural finish, `Cancelled` for `Cancel()`). Read-only `Tween.PlaybackState` reflects the live state (`Begin/Delayed/Playing/Paused/Completed/Cancelled`).
- **S4.6 Conflicts:** "If two tweens attempt to modify the same property, the initial tween will be cancelled and overwritten by the most recent tween." Per-property last-writer-wins with cancellation of the older tween — a compat layer must cancel the older tween (firing its Completed with Cancelled), not blend.
- **S4.7 Replication:** tweens animate the local copy of the property. A tween run **on the server** writes replicated property values every frame, so clients see it (at network-update granularity, i.e. potentially choppy); a tween run **on a client** is visible only to that client (and, for a player's own character or network-owned parts, subject to ownership rules). Standard practice: fire a RemoteEvent and tween on each client for smoothness. This paragraph restates the platform replication model rather than a single doc sentence; mark implementation tests accordingly.
- **S4.8:** `TweenService:GetValue(alpha, easingStyle, easingDirection) -> number` exposes the raw easing curve for manual interpolation.

**Canonical tween idiom (exercises S4.1–S4.5):**

```lua
local TweenService = game:GetService("TweenService")
local info = TweenInfo.new(0.5, Enum.EasingStyle.Back, Enum.EasingDirection.Out,
                           0, false, 0)                      -- time, style, dir, repeat, reverses, delay
local tween = TweenService:Create(part, info, {
    Position = part.Position + Vector3.new(0, 10, 0),        -- Vector3: interpolates
    Transparency = 0.5,                                      -- number: interpolates
})
tween.Completed:Connect(function(playbackState)
    if playbackState == Enum.PlaybackState.Completed then    -- distinguish from Cancelled (S4.5)
        part:Destroy()
    end
end)
tween:Play()
```
  - *CoreAI mapping:* Tween maps 1:1 onto a DOTween-style tweener; the parts to get exactly right are S4.4 (Pause resumes / Cancel restarts), S4.5 (single Completed event carrying the state), and S4.6 (per-property preemption).

**Sources (4):**
- https://create.roblox.com/docs/reference/engine/classes/TweenService
- https://create.roblox.com/docs/reference/engine/classes/TweenBase
- https://create.roblox.com/docs/reference/engine/datatypes/TweenInfo
- https://github.com/Roblox/creator-docs/blob/main/content/en-us/reference/engine/classes/TweenService.yaml (tweenable types + conflict rule quoted)

---

## 5. Frequently-used service surface (compat-layer checklist)

### 5.1 Debris

- **S5.1:** `Debris:AddItem(item: Instance, lifetime: number = 10)` schedules `item:Destroy()` after `lifetime` seconds. Non-yielding, fire-and-forget, survives the calling script's death — the idiomatic "temporary projectile/effect" cleanup. `MaxItems` is deprecated. CoreAI: a timer queue calling the engine Destroy path (must fire Destroying/AncestryChanged exactly like a manual Destroy).

### 5.2 CollectionService

- **S5.2:** Tag API: `AddTag(instance, tag)` / `RemoveTag(instance, tag)` / `HasTag(instance, tag)` / `GetTags(instance)` / `GetTagged(tag) -> {Instance}`. Modern `Instance:AddTag/RemoveTag/HasTag/GetTags` are equivalent aliases.
- **S5.3:** `GetInstanceAddedSignal(tag)` / `GetInstanceRemovedSignal(tag)` return per-tag signals that fire when a tag is applied to / removed from an instance **that is in the DataModel** — including when an already-tagged instance enters or leaves the game tree (this is what makes the connect-then-iterate-GetTagged idiom race-free). Tags on instances replicate server→client with the instance. CoreAI: tags are the primary NoCode "mark this object as X" mechanism; back with a tag registry keyed by scene membership, and fire Added for pre-existing tagged instances only via `GetTagged` iteration (signals only fire on transitions).

### 5.3 RunService

- **S5.4:** Context predicates: `IsServer()`, `IsClient()`, `IsStudio()`, `IsRunning()`, `IsRunMode()`. A compat layer must answer these consistently with where the mod script executes (server sim vs client presentation vs editor).
- **S5.5:** Frame events with delta-time args — legacy names `RenderStepped` (client-only, pre-render), `Stepped` (pre-physics, `(time, dt)`), `Heartbeat` (post-physics); modern names `PreRender`, `PreAnimation`, `PreSimulation`, `PostSimulation`. Both name sets remain functional; map legacy→modern internally. `BindToRenderStep(name, priority, fn)` still exists but is "not recommended" versus the event set. CoreAI: PreRender ⇒ Update (camera/late visual in LateUpdate), PreSimulation/PostSimulation ⇒ FixedUpdate boundaries, Heartbeat ⇒ post-physics step.

### 5.4 UserInputService + ContextActionService

- **S5.6:** `UserInputService` is client-only (LocalScripts). `InputBegan(input, gameProcessedEvent)` / `InputChanged` / `InputEnded` deliver an `InputObject` (`KeyCode`, `UserInputType`, `Position`, `Delta`). The boolean `gameProcessedEvent` is true when the engine/UI already consumed the input (e.g. typing in a TextBox, pressing a GuiButton) — mods must ignore such events for gameplay, and a compat layer must set this flag from its UI event system (UI Toolkit focus/pointer-over in CoreAI).
- **S5.7:** Mouse/cursor: `GetMouseLocation() -> Vector2`, `MouseBehavior` (`Default` / `LockCenter` / `LockCurrentPosition`), `MouseIconEnabled`. Device caps: `TouchEnabled` / `KeyboardEnabled` / `GamepadEnabled` / `MouseEnabled` for input-mode branching.
- **S5.8:** `ContextActionService:BindAction(actionName, callback, createTouchButton, ...inputTypes)`; callback is `(actionName, inputState: Enum.UserInputState, inputObject)`. Sink model: "Returning nil implicitly sinks inputs" — return `Enum.ContextActionResult.Pass` to let lower handlers see the input, anything else (including no return) consumes it. Stacking: for the same input, "the most recently bound action is called first"; `BindActionAtPriority(..., priorityLevel, ...)` overrides stack order with explicit priorities. `UnbindAction(actionName)` removes a binding (and its auto-created touch button). CoreAI: implement as an ordered interceptor chain in front of the raw UIS events; this is the contract that lets mods temporarily steal keys (vehicle enter/exit etc.).

**Canonical input idioms (exercise S5.6 + S5.8):**

```lua
-- UserInputService: respect gameProcessedEvent (S5.6)
UserInputService.InputBegan:Connect(function(input, gameProcessedEvent)
    if gameProcessedEvent then return end            -- UI already consumed it
    if input.KeyCode == Enum.KeyCode.E then
        interact()
    end
end)

-- ContextActionService: sink-by-default, Pass to fall through (S5.8)
ContextActionService:BindAction("Reload", function(actionName, inputState, inputObject)
    if inputState ~= Enum.UserInputState.Begin then
        return Enum.ContextActionResult.Pass
    end
    reload()                                         -- no return => input is sunk
end, true, Enum.KeyCode.R, Enum.KeyCode.ButtonX)
```

### 5.5 SoundService / Sound

- **S5.9:** `Sound.SoundId` takes an asset URI (`"rbxassetid://<id>"`). `Play()` starts from current `TimePosition`, `Stop()` halts and resets `TimePosition` to 0, `Pause()`/`Resume()` preserve position. `Playing` (bool), `Looped`, `Volume`, `PlaybackSpeed` (rate multiplier). `IsLoaded` / `Loaded` event gate playback on asset load.
- **S5.10:** Spatialization by parent: a Sound parented to a `BasePart`/`Attachment` is 3D-positional (attenuates per `RollOffMaxDistance` etc.); a Sound parented to `SoundService`/`Workspace` plays globally (non-positional).
- **S5.11 Replication:** `Sound.Playing`/`TimePosition` state set on the server replicates so all clients hear it; `SoundService:PlayLocalSound(sound)` plays for the local client only. `SoundService.RespectFilteringEnabled` governs whether client-initiated `Sound:Play()` replicates to others (**UNCERTAIN:** current default value not re-verified; behavior description per class reference). CoreAI: 2D/3D split maps to AudioSource spatialBlend; "server plays sound" becomes a replicated play event.

### 5.6 Lighting (replication-relevant bits only)

- **S5.12:** `Lighting` is a replicated singleton: property changes made on the server (`ClockTime`/`TimeOfDay`, `Brightness`, `Ambient`, `FogEnd`, atmosphere/sky children, post-effects) replicate to all clients; changes made in a LocalScript affect only that client — the standard trick for per-player visual states. A compat layer needs Lighting as a replicated global-environment object with client-local override capability. Depth (day/night math, effect instances) is out of scope here.

### 5.7 Chat / TextChatService

- **S5.13:** The modern chat API is `TextChatService` ("a singleton class responsible for managing the overall chat system, including chat message filtering, moderation, and user permissions"): `TextChannel:SendAsync(text)` sends, `DisplaySystemMessage` shows local system lines, `OnIncomingMessage` customizes rendering, `TextChatMessage` carries sender + filtered text; `TextChatService.ChatVersion` selects modern vs `LegacyChatService`. Filtering is automatic. **Depth deferred to a later doc** — for now CoreAI needs only: a channel abstraction, a send API that is async and may rewrite/censor text, and a system-message API.

### 5.8 MarketplaceService — NON-GOAL

- **S5.14:** `MarketplaceService` (game passes, developer products, `PromptPurchase`, `ProcessReceipt`) is **explicitly a NON-GOAL** for CoreAI's compat layer: it is inseparable from Roblox's economy/account backend. The shim should exist only to not-crash: prompts immediately fail/deny, ownership queries return false, and calls log a clear "not supported" diagnostic.

### 5.9 HttpService

- **S5.15 JSON contract:** `JSONEncode(table) -> string` / `JSONDecode(string) -> table` are the JSON round-trip mods use for manual serialization (including into DataStore values). Supported: `nil`, boolean, number, string, arrays, string-keyed dictionaries; JSON `null` decodes to `nil` (holes in arrays). Non-encodable values (Instances, functions, userdata, mixed-key tables, cycles) do not round-trip (**UNCERTAIN:** exact failure mode per type — error vs silent null — not spelled out in current docs; conformance tests should pin CoreAI's choice and document it). These two functions are **not** gated by `HttpEnabled` and work on client and server.
- **S5.16:** `GenerateGUID(wrapInCurlyBraces = true) -> string`, e.g. `"{4c50eba2-d2ed-4d79-bec1-02a967f49c58}"`; braces omitted when the arg is false. Cheap, local, no gating.
- **S5.17 Policy-gated networking:** `RequestAsync(options)` / `GetAsync` / `PostAsync` yield, are server-side, require the experience-level *Allow HTTP Requests* setting (`HttpEnabled`), and share a budget of **500 requests per minute per game server** (HTTP 429 on excess). CoreAI: mark the whole outbound-HTTP surface **policy-gated** — off by default for mods, allow-listed per host by the embedder.

**Sources (5):**
- https://create.roblox.com/docs/reference/engine/classes/Debris
- https://create.roblox.com/docs/reference/engine/classes/CollectionService
- https://create.roblox.com/docs/reference/engine/classes/RunService
- https://create.roblox.com/docs/reference/engine/classes/UserInputService
- https://create.roblox.com/docs/reference/engine/classes/ContextActionService
- https://create.roblox.com/docs/reference/engine/classes/Sound
- https://create.roblox.com/docs/reference/engine/classes/SoundService
- https://create.roblox.com/docs/reference/engine/classes/Lighting
- https://create.roblox.com/docs/chat/in-experience-text-chat
- https://create.roblox.com/docs/reference/engine/classes/MarketplaceService
- https://create.roblox.com/docs/reference/engine/classes/HttpService
- https://create.roblox.com/docs/cloud/reference/rate-limits (HTTP 500/min)

---

## 6. GUI object model minimum (for the UI Toolkit mapping)

- **S6.1 Hierarchy:** `ScreenGui` (root canvas, lives in PlayerGui at runtime) → containers/`Frame` → leaves: `TextLabel`, `TextButton`, `ImageLabel`, `ImageButton`, `TextBox`. Only descendants of a ScreenGui (or SurfaceGui/BillboardGui) render. All 2D elements derive from `GuiObject`.
- **S6.2 UDim2 layout:** `Position` and `Size` are `UDim2` — per axis a `UDim {Scale, Offset}` where **Scale is a fraction of the parent's size** and **Offset is pixels**; final = `scale × parentExtent + offset`. Constructors: `UDim2.new(xs, xo, ys, yo)`, `UDim2.fromScale(xs, ys)`, `UDim2.fromOffset(xo, yo)`.
- **S6.3 AnchorPoint:** `Vector2` in [0,1]² selecting which point of the element `Position` places (default (0,0) = top-left; (0.5,0.5) centers). Position/AnchorPoint/Scale together are the responsive-layout core the UI Toolkit mapping must reproduce exactly (Scale → percent lengths, Offset → px, AnchorPoint → translate(-x%, -y%)).
- **S6.4 Size scaling modifiers:** `SizeConstraint` (RelativeXY default / RelativeXX / RelativeYY — which parent axes Scale reads from), plus constraint children `UIAspectRatioConstraint`, `UISizeConstraint`, and layout children (`UIListLayout`, `UIGridLayout`, `UIPadding`, `UIScale`). Minimum bar: SizeConstraint + UIListLayout + UIPadding + UIScale; the rest can be staged.
- **S6.5 Visibility & stacking:** `GuiObject.Visible` (false ⇒ not rendered, no input, excluded from layout); `ZIndex` orders siblings; `ScreenGui.ZIndexBehavior` defaults to `Sibling` (z-order relative among siblings, children always above parents). `ScreenGui.DisplayOrder` orders whole ScreenGuis; `ScreenGui.Enabled` false disables render+input+updates ("contents will not render, process user input, or update in response to changes"). `IgnoreGuiInset`/`ScreenInsets` control whether the canvas extends under the top bar / device notches.
- **S6.6 Button events:** `GuiButton` (TextButton/ImageButton) exposes `Activated(inputObject, clickCount)` — the recommended cross-platform "pressed" event (fires on click or touch release regardless of device) — alongside device-flavored `MouseButton1Click` / `MouseButton1Down` / `MouseButton1Up` / `MouseButton2*` and `TouchTap` from GuiObject. Compat rule: `Activated` must fire for mouse click, touch tap, and gamepad A-press on a selected button; `MouseButton1Click` mouse-only (though on Roblox touch also fires it — **UNCERTAIN**, verify before relying on it). `AutoButtonColor` gives automatic pressed/hover tinting; `Modal` (while visible) unlocks the mouse.
- **S6.7 GuiObject input events** (needed for draggable/hover UI): `InputBegan/InputChanged/InputEnded`, `MouseEnter/MouseLeave`, `MouseMoved` — same InputObject as UserInputService but pre-filtered to the element.
- **S6.8 StarterGui → PlayerGui rule:** contents of StarterGui clone into each player's PlayerGui when the character first spawns ("When a player joins a game and their character first spawns, the ScreenGui and its contents clone into the PlayerGui container"; with `Players.CharacterAutoLoads` off, "the contents of StarterGui will not be cloned until Player:LoadCharacterAsync() is called"). On respawn, a ScreenGui is destroyed and re-cloned unless it is a **direct child** of StarterGui with `ResetOnSpawn = false` (indirect descendants always reset). Runtime UI code therefore lives in the PlayerGui clones; server-side writes to StarterGui after join do not appear for existing players.
  - *CoreAI mapping:* treat StarterGui as a prefab-template collection instantiated per player into a per-player UI root; `ResetOnSpawn` maps to destroy-and-reinstantiate on respawn.

**Canonical UI idiom (exercises S6.2, S6.3, S6.6, S6.8):**

```lua
-- LocalScript inside the ScreenGui (runs in the PlayerGui clone, S6.8)
local playerGui = game:GetService("Players").LocalPlayer:WaitForChild("PlayerGui")
local screenGui = script.Parent

local button = Instance.new("TextButton")
button.AnchorPoint = Vector2.new(0.5, 0.5)                  -- center anchor (S6.3)
button.Position = UDim2.new(0.5, 0, 0.9, -10)               -- scale + pixel offset (S6.2)
button.Size = UDim2.fromScale(0.2, 0.08)                    -- fraction of parent
button.Text = "Attack"
button.Parent = screenGui

button.Activated:Connect(function()                          -- cross-platform press (S6.6)
    remoteEvent:FireServer()
end)
```

**Sources (6):**
- https://create.roblox.com/docs/ui/on-screen-containers
- https://create.roblox.com/docs/reference/engine/datatypes/UDim2
- https://create.roblox.com/docs/reference/engine/classes/GuiObject
- https://create.roblox.com/docs/reference/engine/classes/GuiButton
- https://create.roblox.com/docs/reference/engine/classes/ScreenGui

---

## 7. Identity & assets

- **S7.1 URI schemes** accepted by content properties:
  - `rbxassetid://<numericId>` — uploaded cloud asset by id (the dominant form: `Sound.SoundId`, `ImageLabel.Image`, `MeshPart` content, `Decal`, particle textures).
  - `rbxasset://<path>` — files in "Roblox's content folder on the user's device" (built-in engine content, e.g. `rbxasset://textures/face.png`).
  - `rbxthumb://type=Asset&id=<id>&w=150&h=150` — generated thumbnails (also avatar headshots etc.).
  - `rbxgameasset://<Name>` — "access to assets by a user-friendly name instead of ID"; valid only inside the owning experience.
  - `https://www.roblox.com/asset/?id=<id>` — legacy web form on approved Roblox domains; normalize to `rbxassetid://`.
- **S7.2 Content properties:** image/sound/mesh properties are `Content`-typed — a string URI resolved asynchronously by the engine (moderation-checked, cached, may stream late; hence `Sound.IsLoaded`, `ContentProvider:PreloadAsync`). Assets are private-by-default to their uploader/group and pass moderation before serving.
- **S7.3 CoreAI substitution (short):** CoreAI will NOT resolve Roblox cloud ids — ids are tied to Roblox's CDN, ownership, and moderation pipeline. The compat layer keeps the *syntax* (`rbxassetid://` and friends parse and flow through Content properties unchanged) but routes resolution through CoreAI's own asset registry: mod-package-relative paths, embedder-registered catalogs (Addressables), and an id-remap table so ported experiences map known ids to local assets. Unresolvable URIs must degrade exactly like Roblox's failure mode: no crash, empty/placeholder content, one console warning.

**Sources (7):**
- https://create.roblox.com/docs/projects/assets

---

## Appendix A — DataStore emulation quick table (CoreAI targets)

| Contract point | Roblox | CoreAI emulation target |
|---|---|---|
| Store identity | `(name ≤ 50, scope ≤ 50)`, default scope `global` | key-prefix `ds/<name>/<scope>/` in KV JSON store |
| Key | string ≤ 50 chars | same limit, validated |
| Value | JSON-encodable, ≤ 4,194,304 chars | same, enforced at write |
| GetAsync | yields; `(value, keyInfo)`; 4 s local cache | async; return snapshot + info record; optional cache with UseCache switch |
| SetAsync | last-writer-wins; returns version id string | write + monotonically increasing version string |
| UpdateAsync | fresh read → pure transform → CAS write, auto-retry, nil aborts, no yield in transform | compare-and-swap loop over the KV store |
| IncrementAsync | integer add, atomic | CAS specialization |
| RemoveAsync | returns prior `(value, keyInfo)` | delete + return prior |
| Versions | 1 backup per key per UTC hour; 30-day expiry; latest never expires | optional; can stub ListVersions to latest-only initially (flag as reduced fidelity) |
| Budgets | per-type, `base + k × players`, queue of 30, 3xx errors | configurable rate limiter; must be introspectable (`GetRequestBudgetForRequestType`) |
| Ordered stores | positive-int values, `GetSortedAsync` pages, no metadata | sorted index over the same KV namespace |
| Session locking | none built-in; ProfileService pattern | provide natively (profile lock + autosave + release on quit/BindToClose) |
| Studio gating | disabled unless *Enable Studio Access to API Services* | editor play-mode uses sandbox store by default |

---

## Appendix B — UNCERTAIN flags (verify before freezing conformance tests)

1. **S1.22** — max UserIDs per key (historically 4) no longer stated in checked pages.
2. **S1.32** — `BindToClose` 30-second allowance not re-verified this pass.
3. **S2.7** — attribute name length cap (commonly 100 chars) not re-verified; `RBX` prefix reservation long-standing.
4. **S3.10** — Humanoid vs new ControllerManager character-controller stack: evolving surface, do not deep-emulate Humanoid internals.
5. **S4.1** — negative `repeatCount` = infinite looping: behavior is long-standing and example-supported, but the datatype page documents only the default.
6. **S5.11** — `SoundService.RespectFilteringEnabled` current default value.
7. **S5.15** — exact JSONEncode failure mode per unsupported type (error vs null) unspecified in current docs.
8. **S6.6** — whether touch input also fires `MouseButton1Click` (in addition to `Activated`).
