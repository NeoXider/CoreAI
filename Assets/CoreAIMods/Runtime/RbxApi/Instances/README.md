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
  `Humanoid` and `HumanoidRootPart`, created with `Archivable = false` as in Roblox (so
  `character:Clone()` is nil until a script sets `Archivable = true`). `Player:LoadCharacterAsync()`
  replaces it and yields; `LoadCharacter()` is a deprecated alias. Replacement checks ownership of
  the outgoing character as well as the player. Disconnect removes the character and its signal
  subscriptions.
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
- A character whose `Humanoid` dies is reloaded `Players.RespawnTime` seconds later while
  `CharacterAutoLoads` is still true (scheduled by the Lua bindings layer, not by this assembly).
- This character slice does not implement avatar rigs, animation, or appearance loading. The network
  transport is the optional `com.neoxider.coreaimirror` package, not this assembly.
- `Clone()` copies external part state through the backing binder and removes partially created
  copies if that binder fails. Lua clones receive the calling actor's ownership recursively. A
  `Humanoid` clone keeps `MaxHealth`, `Health`, `WalkSpeed`, `JumpPower`, `JumpHeight`,
  `UseJumpPower` and `DisplayName` (silently — no `Changed`); a clone at 0 health dies on its first
  Heartbeat inside the Workspace.
- A script, `PivotTo` or tween move of a `HumanoidRootPart` ends that Humanoid's `MoveTo` with
  `MoveToFinished(false)` (the Humanoid is found among the part's siblings, never by a world walk).

- Signals are live: `RunService.Heartbeat`, `UserInputService` input events and `ClickDetector`
  clicks all deliver through `RbxScriptSignal`.
- DEV-7 at Domain level: tombstone reads (`Name`, `ClassName`, `Parent`, `IsDestroyed`) stay
  available on destroyed instances in C#; the stricter Lua-context rule is enforced by the
  marshalling layer. Inside a destruction-queued handler (`Destroying`, `AncestryChanged`, and the
  `Parent` change the destruction raises) a destroyed `BasePart` also reads its last-known property
  values: both part sinks keep the most recent 2,048 destroyed parts, and older ones are forgotten.
- Tags: `InstanceTagStore` answers for every registered instance, and its first-use/last-use flag
  (`IsTagInUse`, `InstanceRegistry.TagAdded`/`TagRemoved`) is store-wide and informational.
  `RbxCollectionService` keeps its own count of holders inside the DataModel and fires the Roblox
  `TagAdded`/`TagRemoved` (and answers `GetAllTags`) from that count, so a tagged nil-parented
  instance counts for nothing there.
- Attribute names: `AttributeContract.ValidateNewName` (ASCII letters and digits plus `.`, `-`, `/`,
  `_`) guards a name a script creates; `ValidateName` (letters and digits of any script) guards
  restore, replication and reads, so a world saved with an older non-ASCII name still loads.
- Registration admission: `IInstanceRegistrationAdmission` checks installed with
  `InstanceRegistry.AddRegistrationAdmission` run, in order, before a new record is added or
  `Registered` fires; the first refusal aborts the creation with an `InvalidOperationException`
  carrying its text, earlier admissions are revoked, and nothing is registered, announced or
  destroyed. The mod runtime's per-actor instance quota is one such check.
- `RbxError` lives in this assembly for now; it moves to a shared RobloxApi contracts assembly
  when the Datatypes slice needs it.
- Instances are created only through `InstanceRegistry` (no public `Register(instance)`)—
  identity can therefore never be missing or duplicated.
