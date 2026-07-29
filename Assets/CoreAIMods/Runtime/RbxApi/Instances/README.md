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

- Signals are live: `RunService.Heartbeat`, `UserInputService` input events and `ClickDetector`
  clicks all deliver through `RbxScriptSignal`.
- DEV-7 at Domain level: tombstone reads (`Name`, `ClassName`, `Parent`, `IsDestroyed`) stay
  available on destroyed instances in C#; the stricter Lua-context rule is enforced by the
  marshalling layer.
- `RbxError` lives in this assembly for now; it moves to a shared RobloxApi contracts assembly
  when the Datatypes slice needs it.
- Instances are created only through `InstanceRegistry` (no public `Register(instance)`)—
  identity can therefore never be missing or duplicated.
