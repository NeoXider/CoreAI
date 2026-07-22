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
- **Unity adapter (NOT here)** — the GameObject binder implements `IInstanceBackingBinder`
  and lands with the world-binding task; this slice ships only `InMemoryInstanceBackingBinder`.
- **Application layer** — none yet; the Lua-visible installer (`RobloxApiInstaller`) arrives
  with the marshalling slice.

## Recorded deviations / notes

- Signals are inert hook points: the properties exist (final surface shape) but every
  `Connect/Once/Wait` is a loud `NOT_IMPLEMENTED` stub until the MVP2 scheduler.
- DEV-7 at Domain level: tombstone reads (`Name`, `ClassName`, `Parent`, `IsDestroyed`) stay
  available on destroyed instances in C#; the stricter Lua-context rule is enforced by the
  marshalling layer when it lands.
- `RbxError` lives in this assembly for now; it moves to a shared RobloxApi contracts assembly
  when the Datatypes slice needs it.
- Instances are created only through `InstanceRegistry` (no public `Register(instance)`)—
  identity can therefore never be missing or duplicated.
