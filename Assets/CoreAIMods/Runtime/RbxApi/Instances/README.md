# CoreAI.RbxApi.Instances (Domain)

Engine-free Instance/DataModel registry slice of MVP1 (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`
§3.3, §5.1; built to `Docs/ARCHITECTURE_RULES.md`).

## Layer map

- **Domain (this assembly)** — `noEngineReferences: true`, references: none. Identity
  (`InstanceId` with the authority-bit partition, `InstanceIdAllocator`, `InstanceRecord`,
  `InstanceRegistry`), the Roblox `Instance` member core (`RbxInstance`, `RbxDataModel`),
  data-driven class ancestry (`ClassCatalog`), tags (`InstanceTagStore` — CollectionService
  substrate), attributes (`AttributeContract`), the ownership ledger (`OriginTag`), stable-id
  snapshots (`InstanceSnapshot`/`InstanceTreeSerializer`), and the structured error surface
  (`RbxError`, §5.2.7 format).
- **Unity adapter (NOT here)** — `InstanceGameObjectBinder` in `../Binding/` implements
  `IInstanceBackingBinder` and materializes instances as GameObjects; this slice ships only
  `InMemoryInstanceBackingBinder`, for engine-free tests.
- **Application layer** — `LuaCsRbxApiBindings` (`../../Scripting/LuaCs/`) exposes the surface to
  Lua. The user-facing reference is [RBX_API.md](../../../../CoreAI/Docs/RBX_API.md).

## Recorded deviations / notes

- `Players.CharacterAutoLoads` creates a minimal runtime character on join: a `Model` with a
  `Humanoid` and `HumanoidRootPart`. `Player:LoadCharacterAsync()` replaces it and yields;
  `LoadCharacter()` is a deprecated alias. Replacement checks ownership of the outgoing
  character as well as the player. Disconnect removes the character and its signal subscriptions.
- Character signals are deferred. `CharacterRemoving` permits the outgoing model's tombstone
  identity reads (for example `Name`); it does not preserve the destroyed descendant tree.
- The Unity composition attaches motors after the root part exists and steps them at
  the scheduler's PreSimulation boundary, once per `Scheduler.Advance` alongside the
  PreSimulation/Stepped signals. Every scheduler phase boundary routes to its matching
  pump — PreAnimation, PreSimulation, PostSimulation, Heartbeat, InputProcessing,
  PreRender — in scheduler order, with the render pair withheld where the topology
  draws no frames. Anchored roots remain anchored. Hosts can supply an
  `IRbxCharacterMotor` factory; headless worlds retain character health and state
  without requiring Unity physics.
- This character slice does not implement avatar rigs, animation, appearance loading, automatic
  death/respawn scheduling, or a production multiplayer transport. `RespawnTime` remains a stored
  setting until automatic respawn scheduling is implemented.
- `Clone()` copies external part state through the backing binder and removes partially created
  copies if that binder fails. Lua clones receive the calling actor's ownership recursively.

- Signals are live: `RunService.Heartbeat`, `UserInputService` input events and `ClickDetector`
  clicks all deliver through `RbxScriptSignal`.
- DEV-7 at Domain level: tombstone reads (`Name`, `ClassName`, `Parent`, `IsDestroyed`) stay
  available on destroyed instances in C#; the stricter Lua-context rule is enforced by the
  marshalling layer.
- `RbxError` lives in this assembly for now; it moves to a shared RobloxApi contracts assembly
  when the Datatypes slice needs it.
- Instances are created only through `InstanceRegistry` (no public `Register(instance)`)—
  identity can therefore never be missing or duplicated.
