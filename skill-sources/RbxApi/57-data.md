# DataStoreService, leaderstats

`DataStoreService` keeps data between sessions, Roblox's way. A store is shared by every mod of the world
(same name and scope = same store); name collisions are yours to avoid. Needs the `Data` capability (part of
`All`; a header that lists its tiers adds `Data`).

```lua
local DataStoreService = game:GetService("DataStoreService")
local store = DataStoreService:GetDataStore("PlayerData")   -- scope defaults to "global"
local Players = game:GetService("Players")

local coinsOf = {}                               -- userId -> coins: the session copy you save from

Players.PlayerAdded:Connect(function(player)
    local leaderstats = Instance.new("Folder")
    leaderstats.Name = "leaderstats"          -- exact lowercase name, directly under the Player
    leaderstats.Parent = player
    local coins = Instance.new("IntValue")
    coins.Name = "Coins"                      -- column header of the player list
    coins.Parent = leaderstats
    local ok, saved = pcall(function() return store:GetAsync("Player_" .. player.UserId) end)
    if ok and saved then coins.Value = saved.coins end
    coinsOf[player.UserId] = coins.Value
    coins.Changed:Connect(function(value) coinsOf[player.UserId] = value end)
end)

local function save(userId)
    local coins = coinsOf[userId]
    if coins == nil then return end
    pcall(function()
        store:UpdateAsync("Player_" .. userId, function(old)
            return { coins = coins }                  -- return nil to cancel the write
        end)
    end)
end
-- In PlayerRemoving the Player is already gone: read only player.UserId, save from your table.
Players.PlayerRemoving:Connect(function(player) save(player.UserId) coinsOf[player.UserId] = nil end)
task.spawn(function() while true do task.wait(60) for userId in pairs(coinsOf) do save(userId) end end end)
```

- `GetAsync(key)` -> value, keyInfo (nil, nil for a new key). `SetAsync(key, value, userIds?)` -> version
  string. `UpdateAsync(key, function(old, keyInfo) return new, userIds?, metadata? end)` -> new, keyInfo;
  the function must not yield and should only compute (return nil = no write). `IncrementAsync(key, delta=1)`
  integers only. `RemoveAsync(key)` -> old value, keyInfo. keyInfo: `Version`, `CreatedTime`, `UpdatedTime`,
  `GetUserIds()`, `GetMetadata()`.
- Every one of them YIELDS: call them from the mod's chunk, `task.spawn`, or a signal handler, and wrap them
  in `pcall`: they fail like Roblox's (the code is inside the message). An execute_lua chunk cannot wait:
  save from a mod.
- Values: booleans, numbers, strings and tables that are arrays OR string-keyed dictionaries of those. No
  Instances, Vector3/CFrame/Color3, functions or mixed tables (`104: Can't store ...`): store
  `{ x = v.X, y = v.Y, z = v.Z }` instead. Keys, names and scopes are 1-50 characters (`101`/`102`); a value
  is at most 4,194,304 characters of JSON (`105`). One string you build is at most 1,000,000 characters
  (`string.rep`, `table.concat`), so a value that large is a table of strings.
- Pacing per world: 100 reads, 100 writes and 100 removes a minute in a solo world (60 + 40 per player).
  Over budget a request waits in a queue of 30; a full queue fails at once with
  `30x: SetAsync request dropped. Request was throttled but queue was full.` Retry after a `task.wait`, keep
  the data in a table, save on leave and every minute, and check
  `DataStoreService:GetRequestBudgetForRequestType(Enum.DataStoreRequestType.SetIncrementAsync)`.
- A world keeps at most 64 MiB (8 MiB on WebGL) and 16,384 keys; past that writes fail with
  BUDGET_EXCEEDED. `501` means a stored file was unreadable (set aside, the store starts empty); `502` means the
  game's storage refused a write: retry later.
- Data is NOT part of the world: a world save or load does not carry it and `/undo` does not change it.
  A write is kept in memory at once and reaches disk within about a second and when the world closes.
- Not built yet (NOT_IMPLEMENTED, MVP16): `GetOrderedDataStore` (keep a top-10 table in one key instead),
  `ListKeysAsync`, the version calls, and the options objects (`DataStoreSetOptions`, ...): leave the options
  argument out.

## leaderstats
- A `Folder` named exactly `leaderstats` directly under a Player, with `IntValue`, `NumberValue` or
  `StringValue` children, shows the player list at the top right: one column per value (its Name, in creation
  order, at most 4), one row per player, live values. Nothing is drawn while no player has a stat.
- `game:GetService("StarterGui"):SetCoreGuiEnabled(Enum.CoreGuiType.PlayerList, false)` hides it (true shows).
- It draws under your own UI: put your HUD anywhere, the list never covers it.
