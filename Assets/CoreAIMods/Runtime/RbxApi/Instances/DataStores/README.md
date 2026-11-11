# DataStoreService and leaderstats (MVP16, single player)

Roblox's way to keep a player's progress between sessions, and the player list a `leaderstats` folder fills.
A mod (or the AI writing one) uses the standard Roblox pattern unchanged:
`DataStoreService:GetDataStore("Coins")`, `GetAsync` on join, `UpdateAsync`/`SetAsync` on leave and on a timer,
an `IntValue` under `leaderstats`. Lua reference: `Assets/CoreAI/Docs/RBX_API.md` (DataStoreService and
leaderstats) and the skill section `data` (`skill-sources/RbxApi/57-data.md`).

## Layer map

| Layer | Assembly and folder | Contents |
|---|---|---|
| Rules (engine-free) | `CoreAI.RbxApi.Instances`, `RbxApi/Instances/DataStores` | `RbxDataStoreService`, `RbxGlobalDataStore`, `RbxDataStoreKeyInfo` (detached instances), `RbxDataStoreRequestBudgets` (pacing), `RbxDataStoreDatabase` (one namespace in memory, coalesced writes, caps), `RbxDataStoreBackend` (one per composition), the storage port `IRbxDataStorePersistence` with `InMemoryRbxDataStorePersistence`, `RbxDataStoreOptions`, `RbxDataStoreErrors` |
| Player list (engine-free) | `RbxApi/Instances/Networking/RbxPlayerList.cs`, `RbxApi/Instances/RuntimeUi/RbxPlayerListView.cs` | the model of rows and columns; the view over the runtime UI port `IRbxUiBackend` |
| Storage (game) | `CoreAI.Mods`, `Infrastructure/DataStores` | `FileRbxDataStorePersistence` (JSON files, atomic writes, WebGL durability), `CoreAiDataStoreDefaults` (the default backend and the platform limits) |
| Lua | `CoreAI.Mods`, `Scripting/LuaCs/DataStores`, `Scripting/LuaCs/PlayerList` | `LuaCsRbxDataStoreAdapter` (the yielding surface), `CoreAiLuaDataStoreBindings.Attach`, `CoreAiPlayerListBindings.AttachTo` |
| Tests | `Tests/EditMode/RbxApi/DataStores`, `PlayerList` (portable engine-free suite), `DataStoresLua` (portable Lua suite) | conformance named by rule (S1.x, S2.5) with refused twins and allocation budgets |

The service is detached like `AIService`: `game:GetService("DataStoreService")` returns it, its `Parent` is nil,
and world packages, replication and the instance quota never see it. The world skeleton is unchanged.

## Namespace: whose data a world sees

A namespace is one "experience" in Roblox terms: every store of a namespace is shared by all mods of the worlds
that use it (S1.1).

- **Game.** A composition's namespace is its mod store id (`CoreAiModsInstaller`'s `modStoreId`): `""` for the
  main game, `block-world-demo` for the Block World demo. Each bundled demo has its own store id, so its own data.
- **World.** A world whose `InstanceRegistry.WorldId` is empty (the default world and every package saved from
  it, so also every autosave and `/undo` snapshot) shares the game's data. A world with an id of its own (its
  package's `world_id`) gets the namespace `game + world id`, so a host with several worlds, or a world loaded from
  someone else's package with its own id, keeps separate data.
- **Host rule.** `RbxDataStoreOptions.Namespace = (gameNamespace, worldId) => ...` replaces the rule (for
  example, one namespace per save slot, or one for every world of the game).
- **Files.** `CoreAI/DataStores` under `CoreAiStorageRoot`, then the namespace folder by the one storage naming
  rule (`LuaModStoreId.ApplyTo`: nothing for `""`, `Stores/s-<26>` otherwise), then one file per store,
  `s-<26>.json` (`CoreAiStorageNames.StoreFolderName` of the name and scope). The longest path below the root is
  92 characters (`\CoreAI\DataStores\Stores\s-<26>\s-<26>.json.tmp`), inside the 138 every store fits from a root
  of 121.

## What it is not

- **Not part of a world.** A world package (manual save, autosave, the startup world, a shared world) never
  carries DataStore data, and a world load or a restore does not change it.
- **Not undone.** `/undo` and `/redo` restore world packages and compare world marks; a data write changes
  neither, so undo never reverts it (pinned by `DataStoreLuaEditModeTests`).
- **Not networked yet.** In a single-player world every script may use it (no server-only rule; Roblox refuses
  LocalScripts). Server-only access, one store per experience across servers and session locking belong to the
  multiplayer milestone: the request carries the mod and the store carries its world, so a side check is one
  more refusal in `RbxDataStoreService.Submit`, and the storage port is where a server or cloud backend plugs in.

## Limits and where a host changes them

Every number is a property of `RbxDataStoreOptions` (one instance per composition, given to `RbxDataStoreBackend`
or `CoreAiDataStoreDefaults.CreateBackend`).

| Limit | Default | Source |
|---|---|---|
| name, scope, key | 1-50 characters (`101`, `102`) | mirror, limits page |
| value | 4,194,304 characters of JSON (`105`); one string a script builds is at most 1,000,000 characters (the sandbox's `string.rep`/`table.concat` cap), so a value near the limit is a table of strings | mirror |
| storable values | booleans, numbers, strings, arrays or string-keyed tables of those (`103`, `104`); a string with a lone UTF-16 surrogate is refused (`104`) | mirror (S1.14, "must be valid UTF-8") |
| metadata | 300 characters, keys 50, values 250 (`511`, `513`) | mirror |
| user ids per key | 4 (`512`, `513`) | S1.22 |
| reads, writes, removes per minute | 60 + 40 per player each (100 solo); UpdateAsync takes a read and a write | mirror server defaults |
| list per minute | 5 + 2 per player (reported only; listing is not built) | mirror |
| waiting requests | 30 per budget queue (`301`-`306` when full) | mirror |
| waiting requests per mod | 30 over all queues | CoreAI |
| stored per world | 64 MiB of characters (8 MiB on WebGL), 16,384 keys (4,096 on WebGL), 256 stores (`BUDGET_EXCEEDED`) | CoreAI |
| write delay | 1 s after a store's first unsaved change, at most 1 file written a frame, everything on close | CoreAI |
| retry after a failed write | 5 s; new writes fail with `502` until a write succeeds | CoreAI |

A budget refills continuously, holds at most one minute of requests and starts full. Over budget, a request
waits in its queue (Read, Write or Remove) and the calling thread keeps waiting; a full queue, or a mod already
holding 30 waiting requests, fails at once with Roblox's throttle text, so standard retry code works.

## Storage and failures

- **Desktop and WebGL.** `FileRbxDataStorePersistence` writes a temporary file and swaps it into place, then asks
  `CoreAiWebGlPersistence.Sync()`; on WebGL the engine's automatic `persistentDataPath` persistence keeps the file
  in IndexedDB. A page that did not arm it is a failed write (`502` for the next writes, an error in the log),
  never a silent loss.
- **Unreadable file.** It is renamed to `.json.corrupt` and reported (log and `RbxDataStoreBackend.Problems`); the
  first request on that store fails with `501`, later ones work on the empty store. The game keeps running.
- **Coalescing.** Requests change memory at once; a store's file is written once its first unsaved change is a
  second old, so a burst of writes is one file write. `RbxDataStoreBackend.Dispose` (the composition scope ends,
  leaving Play Mode, quitting) and a world's retirement write everything pending.
- **Allocation.** An idle service and an unchanged player list allocate nothing per frame
  (`AnIdleService_AllocatesNothingPerFrame`, `AnUnchangedList_AllocatesNothingPerFrame`, each with its twin).

## The player list

`RbxPlayerList` watches the Players service: a child named exactly `leaderstats` directly under a Player, its
`IntValue`, `NumberValue` and `StringValue` children as columns (their `Name`, creation order, at most 4), one
row per player sorted by the first column highest first, then by name. `RbxPlayerListView` draws it as one host
screen (`coreai.playerlist`) of the runtime UI: top right, 56 px from the top and 14 px from the right, so it
sits below a HUD's top line (the Block World status chips are top left, its autopilot banner at the top right
ends above 56 px), above a key legend at the bottom right and away from the chat at the bottom left. Its sort
order is -100, so a mod's own screens (0 by default) always draw over it, and every element ignores the pointer.
More players are more rows (12 shown, then "+N more"). `RbxPlayerListOptions` changes the USS, the caps and the
sort order. Nothing is drawn while no player has a stat.

## Members

Real: `GetDataStore`, `GetGlobalDataStore`, `GetRequestBudgetForRequestType`, `GetAsync`, `SetAsync` (with user
ids), `UpdateAsync` (with user ids and metadata), `IncrementAsync`, `RemoveAsync`, `DataStoreKeyInfo`.
Loud stubs, planned for the rest of MVP16: `GetOrderedDataStore`, `ListDataStoresAsync`, `ListKeysAsync`,
`GetVersionAsync`, `GetVersionAtTimeAsync`, `ListVersionsAsync`, `RemoveVersionAsync`, the options argument of
every call and the classes `DataStoreSetOptions`, `DataStoreIncrementOptions`, `DataStoreGetOptions` and
`DataStoreOptions`. Unsupported: `OnUpdate` (deprecated in Roblox) and `SetRateLimitForRequestType` (the host
sets the budgets).

The deviations from Roblox are listed in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`: DEV-10 (retired), DEV-11,
DEV-72 and DEV-94 to DEV-104.
