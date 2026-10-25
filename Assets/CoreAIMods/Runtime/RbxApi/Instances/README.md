# CoreAI.RbxApi.Instances (Domain)

Engine-free Instance/DataModel registry slice of MVP1 (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`
§3.3, §5.1; built to `Docs/ARCHITECTURE_RULES.md`).

## Layer map

- **Domain (this assembly)** — `noEngineReferences: true`, references only `CoreAI.RbxApi.Datatypes`. Identity
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
- The Unity composition attaches motors after the root part exists and steps them once per
  fixed step (`LuaModRuntimeTickDriver.FixedUpdate`, alongside world gravity), never from the
  render-frame pump. Every scheduler phase boundary routes to its matching pump —
  PreAnimation, PreSimulation, PostSimulation, Heartbeat, InputProcessing, PreRender — in
  scheduler order, with the render pair withheld where the topology draws no frames. Anchored
  roots remain anchored. Hosts can supply their own motors through
  `IRbxCharacterMotorProvider` (`Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md`); headless worlds
  retain character health and state without requiring Unity physics.
- A character whose `Humanoid` dies is reloaded `Players.RespawnTime` seconds later while
  `CharacterAutoLoads` is still true (scheduled by the Lua bindings layer, not by this assembly).
- This character slice does not implement avatar rigs, animation, or appearance loading. The network
  transport is the optional `com.neoxider.coreaimirror` package, not this assembly.
- `Clone()` copies external part state through the backing binder and removes partially created
  copies if that binder fails. C# `Clone()` preserves source actor ownership and access scope;
  the ownership-override overload and Lua clones receive the calling actor's ownership recursively.
  ClickDetector activation distance and every implemented MaterialVariant map, base material and
  tile-width property copy silently. Connection handlers never copy. A
  `Humanoid` clone keeps `MaxHealth`, `Health`, `WalkSpeed`, `JumpPower`, `JumpHeight`,
  `UseJumpPower` and `DisplayName` (silently — no `Changed`); a clone at 0 health dies on its first
  Heartbeat inside the Workspace.
- A script, `PivotTo` or tween move of a `HumanoidRootPart` ends that Humanoid's `MoveTo` with
  `MoveToFinished(false)` (the Humanoid is found among the part's siblings, never by a world walk).

- Signals are live: `RunService.Heartbeat`, `UserInputService` input events and `ClickDetector`
  clicks all deliver through `RbxScriptSignal`.
- Destroying either remote-event class disconnects its server signal and every per-actor client
  signal, including when an ancestor is destroyed. Actor signal storage is cleared even when no
  handlers were connected. Already queued invocations retain the normal destruction semantics;
  repeated Destroy calls do not disconnect or deliver anything again.
- DEV-7 at Domain level: tombstone reads (`Name`, `ClassName`, `Parent`, `IsDestroyed`) stay
  available on destroyed instances in C#; the stricter Lua-context rule is enforced by the
  marshalling layer. Inside a destruction-queued handler (`Destroying`, `AncestryChanged`, and the
  `Parent` change the destruction raises) a destroyed `BasePart` also reads its last-known property
  values: both part sinks keep the most recent 2,048 destroyed parts, and older ones are forgotten.
  `ChildRemoved`, `DescendantRemoving` and the `CollectionService` removed signal that a `Destroy`
  raises hand their handler the same readable tombstone — of the instance that handler was given,
  never of another destroyed one.
- Tags: `InstanceTagStore` answers for every registered instance, and its first-use/last-use flag
  (`IsTagInUse`, `InstanceRegistry.TagAdded`/`TagRemoved`) is store-wide and informational.
  `RbxCollectionService` keeps its own count of holders inside the DataModel and fires the Roblox
  `TagAdded`/`TagRemoved` (and answers `GetAllTags`) from that count, so a tagged nil-parented
  instance counts for nothing there. A tag is at most `InstanceTagStore.MaxTagLength` (100)
  characters when it is created (`ValidateNewTag`: script `AddTag`, and the per-tag signal getters
  for a tag no instance holds); the store itself accepts any non-empty tag, so a longer tag from a
  world saved before the limit still restores, replicates, clones, reads and can be removed. The
  per-tag and per-attribute signal tables (`KeyedSignalTable`) hold a connected signal strongly and,
  past 64 keys, a signal without connections only weakly, so asking for many distinct keys leaves
  nothing behind while a signal a script still holds keeps firing (C# code that keeps neither the
  signal nor its connection is not covered).
- Attribute names: `AttributeContract.ValidateNewName` (ASCII letters and digits plus `.`, `-`, `/`,
  `_`) guards a name a script creates; `ValidateName` (letters and digits of any script) guards
  restore, replication and reads, so a world saved with an older non-ASCII name still loads.
- Registration admission: `IInstanceRegistrationAdmission` checks installed with
  `InstanceRegistry.AddRegistrationAdmission` run, in order, before a new record is added or
  `Registered` fires; the first refusal aborts the creation, earlier admissions are revoked, and
  nothing is registered, announced or destroyed. A refusal written as a §5.2.7 line fails the
  creation with that coded `RbxError`, led by the creation that was refused (`Instance.new("Part")`,
  `cloning Part`, `TweenService:Create`, `restoring Part`, `creating Part`); any other text fails it
  with an `InvalidOperationException` carrying exactly that text. The mod runtime's per-actor
  instance quota is one such check and refuses with `BUDGET_EXCEEDED` and a `Destroy()` hint.
- `RbxError` lives in this assembly.
- Instances are created only through `InstanceRegistry` (no public `Register(instance)`)—
  identity can therefore never be missing or duplicated.

## Runtime arithmetic and mutation admission

Raycast direction is a displacement in studs: its magnitude is the maximum range. The Unity port
converts that displacement once, then derives its normalized direction and metre range. Fractional
and diagonal displacements follow the same contract as unit and axis-aligned displacements.

UDim2 interpolation promotes signed integer offsets before subtraction and rounds the result to the
nearest even integer. Endpoints retain their exact offsets, including the signed 32-bit limits.
Extrapolation that cannot produce a representable offset raises `BAD_ARGUMENT` through the datatype
layer's `RbxApiStubException`; it does not wrap or saturate.

An explicit mutation envelope for TweenService:Create or Tween:Play/Pause/Cancel names the **tweened
instance**, rather than the TweenService or Tween handle. The Lua adapter checks that target before
allocating a tween or changing playback. WorldEdit and actor ACL checks still apply. Ordinary actor
calls use server-generated envelopes; scheduled interpolation continues toward the admitted goals
after the initiating call returns. Identity and game-session policy belong to the host.

Remote encoding writes JSON through a bounded UTF-8 stream. The 65,536-byte ceiling includes tags,
keys, escaping and delimiters; an oversized string is refused before allocating its JSON escaping
buffer. Oversized graph expansion stops at the quota without producing the full JSON string or
byte array, and no rejected payload reaches the network bridge. Depth, aggregate-entry and cycle
guards still apply, including to dictionary values replaced by a stringified-key collision. The
quota diagnostic states that encoding stopped rather than claiming an unmeasured final size.

## Mutable render-material ownership

`RbxTextureMaterialProvider` owns its mutable MaterialVariant cache per provider. Compose a separate
provider for each world. Plain finite catalog materials may remain shared; independent providers
with the same variant name never share mutable handles or overwrite one another's maps or tiling.

The optional `IRbxMaterialLeaseProvider<TMaterial>` extends the existing lookup interface with
`TryAcquireMaterial` and `ReleaseMaterial`. A successful acquisition pins its mutable handle until
the matching release or provider disposal. Acquire a replacement before releasing the old handle,
so another renderer wearing the same variant stays valid. The final release reclaims that variant.
The plain `TryGetMaterial` API returns borrowed handles; at most 32 idle variants remain cached, so
long-lived consumers must acquire a lease. Changing VariantSource retires old cache entries while
preserving acquired handles until their consumers release them.

`InstanceGameObjectBinder` uses this seam when supplied, releases leases on replacement, binding
removal and BeginHostTeardown, and makes teardown idempotent before calling cleanup hooks. Cleanup
failures are reported without preventing other lease releases. The binder disposes the provider it
creates; a supplied provider remains owned by the host, which calls its IDisposable.Dispose after
its consumers stop. Provider disposal clears all mutable materials and rejects subsequent lookups.
Unity may defer native destruction until the end of the frame in Play Mode. Existing providers that
implement only `IRbxMaterialProvider<TMaterial>` retain their original integration path.

## Optional script adapter lifetime

`LuaCsRbxApiBindings` implements `ILuaCsRuntimeLifetimeHost` for optional asynchronous script
adapters. `CurrentScriptLifetimeToken` belongs to the currently executing scheduler coroutine in
that world. It is already cancelled outside that coroutine, during a nested `coroutine.create` call or
after binding disposal. Thread completion, cancellation and generation teardown cancel the token;
each invocation of a pooled handler has its own lifetime.

`RegisterRuntimeLifetime` transfers disposal of a participant to the bindings. Registration uses
reference identity, so the same participant is disposed once even if registered repeatedly or
from a disposal callback. Binding disposal contains participant and diagnostic failures, releases
all registered participants, and disposes a new late registration immediately. Host adapters
remain responsible for cancelling their pending work when they are disposed.
