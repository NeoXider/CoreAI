# MVP2.5 build plan — 2026-09-04

Build plan for the three remaining MVP2.5 rungs (MVP8, MVP11, MVP12) against the owner decisions
recorded in `dev-docs/MVP25_ONLINE_PLAN.md` §7 ("DECIDED 2026-09-04"). Every repository fact below
was read from source on `main` at `dc86623f` (7.13.0); every Roblox semantic was checked against the
local mirror `D:\Git\RobloxDocs\creator-docs\content\en-us\reference\engine\{classes,enums,datatypes}`.
Where the brief, the roadmap, or the mirror disagree with the repository, §0 says which one wins.

This document does not re-argue the rung order (MVP3 → MVP8 → MVP11 → MVP12, one rung per release),
the entry gates P1–P5, or the measurement discipline of §6 of the online plan.

---

## 0. Verified state and corrections to the brief

### 0.1 What is actually on disk

| Item | State (verified) |
|---|---|
| `INetworkBridge` | Engine-free byte port in `Assets/CoreAIMods/Runtime/RbxApi/Instances/Networking/INetworkBridge.cs`: `Topology`, `ActorIds`, `EventReceived`, `RequestReceived`, `RegisterActor`, `UnregisterActor`, `SendEvent`, `SendRequest`. Messages carry `SenderActorId`/`RecipientActorId` **strings**; there is no connection handle, no wire correlation id, no disconnect event, no max-payload or rate member. The roadmap's §5.2.2 sketch (`int playerId`, `Invoke/Respond`) is stale; the shipped shape is the one above. |
| Loopback | `NullNetworkBridge` (same folder): `Topology => Solo`, synchronous FIFO drain inside `SendEvent`, per-actor per-group 500/s admission raising `BUDGET_EXCEEDED`, `RateWindowCount` for leak tests. |
| Bridge decorators | `StagedNetworkBridge` (private, `Infrastructure/RbxWorldPackageContracts.cs:2124`) wraps any `INetworkBridge` during session replacement with a 256-operation queue; `tools/ScaleHarness/ScaleInstrumentation.cs:60 ScaleLoopbackBridge` decorates it for counters. A Mirror bridge must survive both unchanged. |
| Older authority abstraction | `Assets/CoreAI/Runtime/Core/Authority/` also holds `IAiNetworkPeer` (`IsHostAuthority`/`IsPureClient`), `DefaultSoloNetworkPeer`, `AiNetworkExecutionPolicy`, `NetworkedAuthorityHost : IAuthorityHost`. It gates AI command execution, not the Rbx layer, and has only a solo implementation. It is a second "am I the host" source that MVP11 must derive from the bridge topology or retire. |
| Identity | `IActorIdentityProvider.GetActorContext(string roleId)` is the only member; no credential input, no failure value. `ActorContext.IssueRestricted` refuses unrestricted grants (`ActorContext.cs:182-187`); unrestricted contexts exist only through `ActorIdentityComposition` and the private nested proof type in `CoreServicesInstaller.cs:20-30`. |
| `Players` | **The brief is wrong here.** `Players` is not a placeholder. `ServiceCatalog.CreateMvp2` registers it tree-backed (`ServiceCatalog.cs:173`), `ClassCatalog.cs:383` binds it to `RbxPlayers`, `ClassCatalog.cs:357` registers `Player`, `DataModelBootstrap.CreateGame` instantiates it, and Lua already reads `Players.LocalPlayer` (nil in server context), `PlayerAdded`, `PlayerRemoving(player, exitReason)`, `GetPlayers()`, `Player.UserId` (`LuaCsRbxInstanceBindings.cs:662-686`). What is missing is the rest of the MVP8 surface (§A.1). |
| Player lifecycle | Rung zero landed (`PROGRESS.rungzero.md`, commit `5c62c43d`): `LuaCsRbxApiBindings.ConnectActor/DisconnectActor` (`:408-470`) admit a trusted actor once, fire `PlayerAdded`/`PlayerRemoving` exactly once, release chat, kill owned threads, drop rate/signal state; an inbound `SenderActorId` must already be admitted (`DemandAdmittedNetworkSender`, `:972`) and cannot create identity; the codec cap of 65,536 bytes raises `PAYLOAD_TOO_LARGE` both ways (`LuaCsRbxNetworkCodec.cs:98-132`). The 2026-09-02 architecture audit's findings 1, 2, 3, 4, 6 and 9 are closed; do not re-derive from that audit. |
| Contexts | **No mod declares a script context.** `LuaModManifest.cs` has no context field; server-ness is `IsNetworkServer => Bindings.NetworkBridge.Topology != Client && IsHost` with `IsHost => ActorContext.Grants.IsUnrestricted` (`LuaCsRbxInstanceBindings.cs:119,145`). A restricted actor *is* a client, an unrestricted actor *is* the server. MVP5 ("contexts declared") is not landed; the roadmap lists it as an MVP11 dependency. |
| Mutation envelope | `InstanceRegistry.ApplyMutation` (`:243`) and `ApplyServerGeneratedMutation` (`:296`) sit on every production Lua entry (`LuaCsGameToolExecutor.cs:279,333`, `LuaCsModRuntime.cs:1943,2439`, `LuaCsRbxApiBindings.cs:626`, `LuaCsRbxInstanceBindings.cs:392-399,541,810`). Open residue: world-package restore and `RbxWorldHost` writes are not enveloped (TODO.md "Rung zero residue"). |
| ACL | `WorldAclAuthorizer.Demand` and `WorldAclDecision` are engine-free (`RbxApi/Instances/WorldAcl.cs`); `InstanceAccessScope {Owned, SharedWritable, HostProtected}`, `OwnerActorId`, `Revision`, `NetId` (reserved; `BindNetId`/`TryGetByNetId` exist) on `InstanceRecord`. Checks are live only when `IsWorldAclEnabled` (production passes `CurrentWorldAclVersion = 1`, `CoreAiModsInstaller.cs:80,176,192`; a restored legacy package may carry `null`). |
| Ids | `InstanceId.AuthorityBit` partition; `InstanceIdWireContract.EnsureWireSafe` rejects locally-assigned ids in the message constructors. |
| Clocks | `IRbxClockSource` (`RbxApi/Datatypes/IRbxClockSource.cs`): `GameTimeSeconds`, `UnixTimeSeconds`, `ProcessTimeSeconds`, `UnixTimeSecondsFractional`; `workspace:GetServerTimeNow()` is implemented over it (`LuaCsRbxApiBindings.cs:320`) with no server offset. `RunService.IsServer/IsClient/IsStudio/IsRunning` answer through `IRbxRuntimeTopology` (`RbxRunService.cs:36`, default `RbxSoloRuntimeTopology`). |
| World package | One serializer: `RbxWorldPackageSerializer.ExportSnapshot` (`:172`) for disk and snapshot; `IRbxWorldSessionHost.Stage → IRbxWorldSessionCandidate.Commit` (`RbxWorldPackageContracts.cs:211-249`) for restore; `HeadlessRbxWorldSessionHost` (`:903`) is the engine-free host. The serializer **rejects `Player` nodes** (`RbxWorldPackageSerializer.cs:59,865`), **packages every mod source**, and `InstanceTreeSerializer` **rejects any class it does not know** (`:366`). All three shape MVP8 and MVP11. |
| Physics on bound parts | `InstanceGameObjectBinder` gives parts a `Collider` (`:727-814`) and a `Rigidbody` when unanchored (`:93-97`); no `Touched` relay, no per-body gravity. `LuaModRuntimeTickDriver` pumps from `Update()` only (`:71`); no `FixedUpdate` hook. NeoxiderTools `PhysicsEvents3D` (`D:\Git\NeoxiderTools\Assets\Neoxider\Scripts\Tools\InteractableObject\PhysicsEvents3D.cs`) exposes `CollisionEnter/Exit` and `TriggerEnter/Exit` events. |
| Character controller | **The roadmap's "backed by CoreAI's existing player/controller" has no referent in the CoreAI packages.** The only motor in the repo is the example game's `Assets/_exampleGame/RogueliteArena/Features/ArenaCombat/Infrastructure/ArenaPlayerMotor.cs`; NeoxiderTools ships `KeyboardMover`/`ConstantMover` and a `CharacterMovementFundamentals` sample. |
| Networking stacks installed | **Mirror: none** (no `Mirror` token in any `.cs`/`.asmdef`; scripting defines `DOTWEEN;COREAI_HAS_HUB;COREAI_LLM;COREAI_LUA`). **Unity Netcode for GameObjects 2.11.0 IS installed** (`Packages/manifest.json`: `com.unity.netcode.gameobjects`, `com.unity.multiplayer.*`, `com.unity.services.multiplayer`, `com.unity.dedicated-server`; `Assets/DefaultNetworkPrefabs.asset`) **and used** by the example game (`Assets/_exampleGame/SymbiosisMode/Scripts/SymbiosisGhostPlayer.cs : NetworkBehaviour`; `ArenaSurvivalDirector.cs:144`). The brief did not mention this; decision 2 adds a *second* network stack. |
| Mirror reference install | `D:\Git\NeoxiderTools\Assets\Mirror` is an Asset-Store-layout import (no root `package.json`; asmdefs `Mirror`, `Mirror.Components`, `Mirror.Authenticators`, KCP). It injects `MIRROR` and `MIRROR_96_OR_NEWER` into the project defines itself through `CompilerSymbols/PreprocessorDefine.cs`. `Neo.Network.asmdef` references `Mirror`/`Mirror.Components` and declares a `versionDefines` entry for package `com.mirrornetworking.mirror`, which only fires for a UPM install; the load-bearing switch in practice is the injected `MIRROR` define. |
| Packages | Six lockstep packages under `Assets/*/package.json`; `tools/bump_version.py` globs `Assets/*/package.json` (a seventh is picked up automatically); the CI `package-graph` job (CONTRIBUTING.md:31) must be checked for a hard-coded six. |
| Capacity | `dev-docs/SCALE_CHARACTERIZATION.md`: frame budget holds 100 actors in 4 ms on host CoreCLR; chat gate fails from 100 actors (`MaxPending=64`); **heap-slope budget fails already at 20 actors** (~4.5 KB/actor/frame, TODO `alloc-fix` OPEN). Decision 4's 20-client bar is blocked on solo work before any transport exists. |
| Tests | Full EditMode 3256 total / 3247 passed / 0 failed / 9 skipped on `5c62c43d` (TODO.md). The editor holds the lock, so today's verification is `dotnet build` on the generated csproj plus the next editor start. Engine-free slices are verifiable now; PlayMode slices are not. |

### 0.2 Where the docs mirror corrects the brief or the roadmap

- `Debris:AddItem(item, lifetime = 10)` — the mirror adds a **hard cap of 1,000 items; the oldest is destroyed instantly** when exceeded. The roadmap omits the cap; MVP8 implements it.
- `TweenService` has `Create`, `GetValue(alpha, easingStyle, easingDirection)` and `SmoothDamp`; the roadmap lists only `Create`. `GetValue` is pure math and ships; `SmoothDamp` is a loud stub.
- `CollectionService` has `GetAllTags`, `TagAdded`, `TagRemoved` beyond the roadmap's list; all three are cheap over `InstanceTagStore` and ship. `GetCollection`/`ItemAdded`/`ItemRemoved` are Deprecated and are not delivered.
- `Player:LoadCharacter` is `[Yields, Deprecated]`; the modern member is `LoadCharacterAsync`. MVP8 implements `LoadCharacterAsync` and aliases `LoadCharacter` with a once-per-mod deprecation note.
- `Humanoid.Health` carries the `NotReplicated` tag. It is not in the MVP12 whitelist anyway; recorded so nobody later "fixes" replication by adding it blindly.
- `Players.PlayerRemoving(player, exitReason)` with `Enum.PlayerExitReason {Unknown, PlatformKick, CreatorKick}` is already implemented; `Player:Kick` maps to `CreatorKick`. From a LocalScript only the local client may be kicked (mirror) — the MVP11 client-side rule.
- `Workspace.FilteringEnabled` is `Hidden, NotReplicated, Deprecated`, "discontinued and no longer takes effect": the mirror supports decision 3; there is no Roblox mode in which a client write replicates.
- `Humanoid:MoveTo` times out after **8 s** with `MoveToFinished(false)`; `TakeDamage` accepts negatives; S3.1 says a passive regeneration script (1 % MaxHealth/s) is inserted by default — the roadmap's Humanoid list is silent on regeneration (§F.4 g).
- `WorldRoot:Raycast` direction length is the range, max 15,000 studs; default `RaycastParams{IgnoreWater=false, RespectCanCollide=false, CollisionGroup=Default, FilterDescendantsInstances={}}`; `Enum.RaycastFilterType` is `Exclude`/`Include` (`Blacklist`/`Whitelist` deprecated).

---

## A. Rung-by-rung scope

Notation: **ships** = 1:1 member bound to production behaviour; **stub** = registered loud `NOT_IMPLEMENTED` naming the rung or "not planned"; **not** = absent from the descriptor (unknown-member error). The 1:1 rule means nothing ships under a non-Roblox name; CoreAI-only behaviour (grants, admission) has no Lua surface at all.

### A.1 MVP8 — full Players and gameplay services

Lua-facing surface, member by member (mirror-checked):

| Class | Ships | Stub / not |
|---|---|---|
| `Players` | `LocalPlayer` (nil in server context), `PlayerAdded(player)`, `PlayerRemoving(player, exitReason)`, `GetPlayers()`, `GetPlayerByUserId(userId)`, `GetPlayerFromCharacter(character)`, `CharacterAutoLoads` (default true), `RespawnTime` (default 5.0), `MaxPlayers` (read-only, from host profile) | stub "not planned (platform backend)": `BanAsync`, `UnbanAsync`, `GetBanHistoryAsync`, `CreateHumanoidModel*`, `GetCharacterAppearance*`, `GetFriendsAsync`, `GetHumanoidDescription*`, `GetNameFromUserIdAsync`, `GetUserIdFromNameAsync`, `GetUserThumbnailAsync`, `Chat`/`TeamChat`/`SetChatStyle` (PluginSecurity), `PlayerMembershipChanged`, `UserSubscriptionStatusChanged`; not: deprecated aliases (`localPlayer`, `numPlayers`, `getPlayers`, `playerFromCharacter`, `players`), `BubbleChat`, `ClassicChat`, `PreferredPlayers`, `UseStrafingAnimations`, `BanningEnabled` |
| `Player` | `UserId`, `Name` (username), `DisplayName`, `Character` (Model or nil), `CharacterAdded(character)`, `CharacterRemoving(character)`, `LoadCharacterAsync()` (yields via `ScheduleWaitUntil`), `LoadCharacter()` (deprecated alias + note), `Kick(message?)`, `DistanceFromCharacter(point)`; child containers `PlayerGui`, `PlayerScripts`, `Backpack` created empty on join (M2.1 templates; contents are MVP14/MVP10) | stub: `Team`, `TeamColor`, `Neutral` (no `Teams` service in this rung), `ReplicationFocus`/`AddReplicationFocus`/`RemoveReplicationFocus` (decision 5), `GetMouse` (MVP10), `Chatted` (chat is `TextChatService`, non-goal), `GetNetworkPing` (MVP11), `RespawnLocation`, `CameraMode`/`Dev*` modes, `CanLoadCharacterAppearance`, `CharacterAppearanceId`; not: `AccountAge`, `MembershipType`, `HasVerifiedBadge`, `LocaleId`, `FollowUserId`, group/friend methods, all deprecated `Save*`/`Load*` members |
| `Humanoid` | `Health` (clamped [0, MaxHealth], S3.1), `MaxHealth`, `WalkSpeed` (16), `JumpPower` (50), `JumpHeight` (7.2), `UseJumpPower` (true; impulse from whichever is active, S3.5), `Jump` (write true = jump), `MoveDirection` (read-only), `RootPart` (read-only), `DisplayName`, `TakeDamage(amount)` (S3.2; negative heals; no ForceField class → no protection), `MoveTo(location, part?)` with the 8 s timeout, `GetState()` (subset: Running, Jumping, Freefall, Landed, Dead), `Died`, `HealthChanged(health)`, `MoveToFinished(reached)`, `Running(speed)`, `Jumping(active)`, `FreeFalling(active)`, `StateChanged(old, new)` for the subset | stub: `ChangeState` (except `Jumping`), `Sit`, `PlatformStand`, `Seated`, `Climbing`, `Swimming`, `Ragdoll`, `FallingDown`, `GettingUp`, `PlatformStanding`, `Strafing`, Humanoid-level `Touched`, `AutoRotate`, `HipHeight`, `MaxSlopeAngle`, `CameraOffset`, `RigType`, accessory/description/animation members, `Move` (MVP10 input) |
| `IntValue`, `NumberValue`, `StringValue`, `BoolValue`, `ObjectValue` | `Value`, `Changed(value)`, `GetPropertyChangedSignal("Value")`; serialised by the one serializer | not: `changed` deprecated alias; `Vector3Value`/`CFrameValue`/`Color3Value`/`BrickColorValue`/`RayValue` stub "MVP-later" |
| `Debris` | `AddItem(item, lifetime = 10)`; 1,000-item cap evicting the oldest instantly | not: `MaxItems`, `addItem` (deprecated) |
| `TweenService` | `Create(instance, tweenInfo, propertyTable)`, `GetValue(alpha, easingStyle, easingDirection)` | stub: `SmoothDamp` |
| `TweenInfo` (datatype) | `TweenInfo.new(time=1, easingStyle=Quad, easingDirection=Out, repeatCount=0, reverses=false, delayTime=0)`; fields `Time`, `EasingStyle`, `EasingDirection`, `RepeatCount`, `Reverses`, `DelayTime`; `repeatCount < 0` loops (S4.1) | — |
| `Tween` / `TweenBase` | `Instance`, `TweenInfo`, `PlaybackState` (read-only), `Play()`, `Pause()`, `Cancel()`, `Completed(playbackState)`; tweenable: number, Vector3, CFrame, Color3, UDim2 | stub naming the backlog for bool, EnumItem, Rect, UDim, Vector2, Vector2int16 (S4.3 lists them as tweenable — a recorded partial, loud not silent) |
| `CollectionService` | `AddTag`, `RemoveTag`, `HasTag`, `GetTags`, `GetTagged(tag)` (DataModel descendants only), `GetAllTags()`, `GetInstanceAddedSignal(tag)`, `GetInstanceRemovedSignal(tag)` (S5.3: fire on tag transitions and on tree entry/exit of tagged instances), `TagAdded(tag)`, `TagRemoved(tag)` | not: `GetCollection`, `ItemAdded`, `ItemRemoved` (deprecated) |
| `BasePart` | `Touched(otherPart)`, `TouchEnded(otherPart)` from real contact (at least one part unanchored; `CFrame` teleports do not fire — mirror) | stub "deferred (owner decision 5)": `SetNetworkOwner`, `GetNetworkOwner`, `SetNetworkOwnershipAuto`, `GetNetworkOwnershipAuto`, `CanSetNetworkOwnership` |
| `Workspace` / `WorldRoot` | `Gravity` (196.2 studs/s², per-body force via `RobloxSpace`, host `Physics.gravity` untouched — DEV-6), `Raycast(origin, direction, raycastParams?)` → `RaycastResult{Instance, Position, Normal, Material, Distance}` | `RaycastParams.CollisionGroup` accepts only `Default` (loud otherwise); `IgnoreWater` accepted with no effect (no Terrain); `Terrain` stays the existing stub |
| Enums | `EasingStyle` (Linear, Sine, Back, Quad, Quart, Quint, Bounce, Elastic, Exponential, Circular, Cubic), `EasingDirection` (In, Out, InOut), `PlaybackState` (Begin, Delayed, Playing, Paused, Completed, Cancelled), `RaycastFilterType` (Exclude, Include; Blacklist/Whitelist deprecated aliases with note), `HumanoidStateType` (subset above) | — |

Explicitly **not** delivered by MVP8: any transport, any replication, network ownership, `Teams`, `StarterCharacterScripts`/`StarterPlayerScripts` execution, GUI contents, `DataStoreService` (MVP9), input (`Humanoid:Move`, `Player:GetMouse`; MVP10), sound/animation (MVP15). The Creator free-fly locomotion mode rides the Humanoid adapter behind a host-profile flag and is measured with it, but it is not a Lua member because Roblox has none.

C# areas MVP8 touches (all verified to exist unless marked **new**):

- Engine-free (`Assets/CoreAIMods/Runtime/RbxApi/Instances/`): `ClassCatalog.cs` (descriptor registrations `:341-420`; remove the `WorldRoot.Raycast`/`Workspace.Gravity` planned-member stubs at `:405-409`), `ServiceCatalog.cs` (replace the three `RegisterStub` lines `:174-176` with `RegisterTreeBacked`), `DataModelBootstrap.cs` (create `Debris`, `TweenService`, `CollectionService`; back-fill in `AttachWorldRoot`), `Networking/RbxPlayers.cs` (profile, lookups, character slot, `Kick`), **new** `RbxHumanoid.cs`, `RbxValueObjects.cs`, `RbxDebris.cs`, `RbxTweenService.cs`/`RbxTween.cs`, `RbxCollectionService.cs`; `InstanceTagStore.cs` (`GetTagged` substrate exists at `:79`); `Scheduling/ModScheduler.cs` (**new** ownerless host-timer primitive for Debris and the tween driver; `ScheduleWaitUntil` `:573` for `LoadCharacterAsync`); `InstanceTreeSerializer.cs` (`:366` unsupported-class rejection — Value classes and `Humanoid` state must be added or every saved world containing `leaderstats` is rejected).
- Datatypes (`RbxApi/Datatypes/`): `RbxEnum.cs` (`RbxEnumRegistry` at `:112` gains the five enums), **new** `RbxTweenInfo.cs`, `RbxRaycastParams.cs`, `RbxRaycastResult.cs`, easing functions.
- Infrastructure: `RbxWorldPackageSerializer.cs` (durable surface for Value objects; `Player` rejection at `:59,865` stays), `LuaModRuntimeTickDriver.cs` (**new** `FixedUpdate` pump for `PreSimulation` physics hooks; today `Update()` only, `:71`).
- Lua binding (`Runtime/Scripting/LuaCs/`): `LuaCsRbxInstanceBindings.cs` (`TryReadNetworkMember` `:662`, method table around `:998`), `LuaCsRbxApiBindings.cs` (`GetLocalPlayer` `:658`, `EnsureNetworkActor` `:776`, `ConnectActor` `:408`), `LuaCsRbxDatatypeBindings` (TweenInfo/RaycastParams globals next to the `:1319-1324` registrations).
- Unity adapter (`RbxApi/Binding/`, `RbxApi/Unity/`): `InstanceGameObjectBinder.cs` (colliders `:727-814`, rigidbody entry `:93-97`; per-body gravity force and a touch relay), `IPartPropertySink.cs` (no change), `RbxWorldHost.cs` (owns the character prefab and the raycast layer mask), `RbxSpace.cs` (all conversions), **new** `RbxHumanoidControllerAdapter.cs` (metric controller behind it — §F.4 a), **new** `RbxTouchRelay.cs` over NeoxiderTools `PhysicsEvents3D`.
- Tests: `Tests/EditMode/RbxApi/CompatibilityCorpus/TierACorpusCatalog.cs` + `Fixtures/` (Tier-B fixtures with frozen ids), **new** `Assets/CoreAIMods/Tests/PlayMode/RbxApi/` (the roadmap §6.6 planned folder; does not exist) for Touched, gravity, Humanoid.

### A.2 MVP11 — authenticated Mirror transport (host mode)

MVP11 adds **no new Lua members**. It changes where existing members answer from:

| Member | MVP11 behaviour |
|---|---|
| `RunService:IsServer()/IsClient()` | Answer per process and per declared script context through new `IRbxRuntimeTopology` implementations (`Host`, `Client`); dedicated server stays MVP13. |
| `Players.LocalPlayer` | On a client process: the admitted `Player`; in the host's server context: nil (unchanged). |
| `Players.PlayerAdded/PlayerRemoving`, `GetPlayers()` | Driven by admission and disconnect over Mirror; the roster reaches clients by an explicit roster message (the serializer rejects `Player` nodes, so the roster is never inside a snapshot). |
| `Player:Kick(message?)` | Server: disconnects the connection with `CreatorKick`; client: only self. |
| `RemoteEvent`/`UnreliableRemoteEvent`/`RemoteFunction` | Same surface; packets move over Mirror reliable/unreliable channels; `OnServerEvent`'s first argument is the connection-bound `Player`, never a payload field. `UnreliableRemoteEvent` payloads over the transport MTU raise `PAYLOAD_TOO_LARGE` (Q4 answered at runtime from the transport). |
| `RemoteFunction:InvokeServer/InvokeClient` | Correlated request/response with a wire correlation id; the existing 30 s timeout (`LuaCsRbxApiBindings.cs:33`) becomes the documented Lua error on both sides. |
| `workspace:GetServerTimeNow()` | Server epoch seconds plus the measured Mirror clock offset on clients (`NetworkTime.offset`), monotonic-smoothed; tolerance frozen before the run. |
| Script contexts | `server` / `client` / `shared` declared per mod (manifest field + header key); a client-only member used in server context raises `CONTEXT_VIOLATION` (code exists in `RbxError.cs`), replacing the grant-derived `IsNetworkServer`. |

Explicitly **not** delivered: shared-state replication of any kind after the join moment (no deltas, no property sync, no late-join convergence — that is MVP12), `ReplicatedFirst`, `DataModel.Loaded`/`IsLoaded`, `TeleportService`, `MessagingService`, the `NetworkServer`/`NetworkClient`/`NetworkSettings` classes (present in the mirror, not usefully scriptable — they stay unknown members), dedicated server (MVP13), matchmaking/relay/NAT, WebGL hosting (WebGL is `Client` only and needs a WebSocket transport, §C.5).

One deliberately pulled-forward item, stated so it is not mistaken for replication: at admission the server sends the client a **static, filtered join snapshot** produced by the existing serializer (`ExportSnapshot`) so the client owns a registry with the server's `InstanceId`s and can resolve `ReplicatedStorage.RemoteX` by reference. Nothing updates after that moment in MVP11. Without it no client Lua can address a remote at all (remotes marshal by `InstanceId`, `RbxRemotes.cs`) and N11.3 could only be passed by a C# harness client, which is not a production path. The filter (Workspace, ReplicatedStorage, Lighting; never ServerStorage/ServerScriptService; never any `server`-context source) is the same `IReplicationFilter` MVP12 reuses for deltas.

C# areas MVP11 touches:

- Identity: `Assets/CoreAI/Runtime/Core/Authority/IActorIdentityProvider.cs` (additive admission port, §C.4/§D), `ActorContext.cs` (no change — `IssueRestricted` is the guarantee), `CoreAiUnity/Runtime/Source/Composition/CoreServicesInstaller.cs` (default host identity stays), `CoreAI.Core/Authority/IAiNetworkPeer.cs` + `NetworkedAuthorityHost.cs` (derive from topology or retire).
- Bridge: `RbxApi/Instances/Networking/INetworkBridge.cs` (additive v2 members, §C.4), `NullNetworkBridge.cs` (implements them trivially), `RbxRemotes.cs` (no surface change), `RbxPlayers.cs` (profile from admission; a durable `UserId` replaces `_nextUserId++` at `:38,74`), `RbxRuntimeTopology.cs` (**new** `RbxHostRuntimeTopology`/`RbxClientRuntimeTopology`), `RbxRunService.cs:36`.
- Binding: `LuaCsRbxInstanceBindings.cs:119,145` (`IsHost`/`IsNetworkServer` → declared context + topology), `LuaCsRbxApiBindings.cs` (`ConnectActor` `:408` takes the admission result; `DeliverNetworkEvent` `:814` unchanged in shape; RemoteFunction correlation `:1895-1963`), `LuaCsRbxNetworkCodec.cs` (unchanged; cap stays 65,536), `LuaExecution/LuaModManifest.cs` (**new** `Context` field) and the `--[[@coreai ...]]` header parser (`LuaModHeader.cs`, per roadmap MVP5).
- Session/snapshot: `Infrastructure/RbxWorldPackageContracts.cs` (`StagedNetworkBridge` `:2124` must forward the v2 members; `IRbxWorldSessionHost` `:232` and `HeadlessRbxWorldSessionHost` `:903` are the client-side restore path), `RbxWorldPackageSerializer.cs` (**new** capture filter; `ExportSnapshot` `:172`).
- Composition: `CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs` (`ResolveOrDefault<INetworkBridge>` `:169,331` — the Mirror package registers before this), **new** `Assets/CoreAIMirror/` package (§C).
- Clock: `RbxApi/Datatypes/IRbxClockSource.cs` unchanged; **new** additive port `IRbxServerClockOffset` consumed only by `GetServerTimeNow` (`LuaCsRbxApiBindings.cs:320`), so `IRbxClockSource` is not a breaking change for host implementers.

### A.3 MVP12 — filtered, server-authoritative replication

MVP12 adds **no new Lua members**. Observable behaviour it delivers:

| Behaviour | Rule |
|---|---|
| Server `Instance.new` / reparent / `Destroy` under `Workspace`, `ReplicatedStorage`, `Lighting` appears on every admitted client with the **same `InstanceId`** and a server-assigned `NetId` (`InstanceRecord.NetId`, `BindNetId`). | M2.1–M2.3 |
| Property whitelist replicates dirty-flagged and batched per tick: `Name`, `Parent`, `CFrame`/`Position`, `Size`, `Color`, `Transparency`, `Anchored`, `CanCollide`, `Material`/`MaterialVariant`, `Shape`, `Archivable`, attributes, tags, Value objects' `Value`, Model `PrimaryPart`/`WorldPivot`. | M2.3, M2.4; the world package's durable surface is the whitelist's upper bound |
| `ServerStorage`/`ServerScriptService` and `server`-context sources never reach a client; filtered properties never reach a client; later deltas cannot bypass the filter. | M2.2, M1.7 |
| Client write to a replicated instance: `ClientWritePolicy.RobloxParity` (default) applies locally and is overwritten by the next server delta; `Strict` raises `NOT_AUTHORITY`. **`Open` does not exist** (decision 3). | M2.6, M1.7 |
| Host-granted write authority: a client holding a grant sends a **mutation intent**; the server rebinds the actor from the connection, checks rate, grant, ACL, revision and operation id, applies through the same code path server Lua uses, and replicates the result (§D). | M1.9, M7.2 |
| Late join: the same `ExportSnapshot` payload as MVP11, then ordered revision deltas from the snapshot's revision; duplicates ignored deterministically, gaps trigger resync from a fresh snapshot, never silent divergence. | M2.7 |
| Physics: server-owned; authoritative transforms replicate outward; client transform/velocity claims are dropped. The `SetNetworkOwner` family stays a loud stub. | decision 5; M4.x deferred |
| Ordering promise to mods: same-type changes arrive in order; no promise across types or between spawn and remote traffic. | M2.5 |

Explicitly **not** delivered: `Open`/direct forwarding under any name, per-property/per-player partial authority beyond the grant scopes in §D, network ownership, client prediction/reconciliation, streaming (`StreamingEnabled` and every `Streaming*`/`ModelStreamingMode`/`ReplicationFocus` member stays a loud stub), collaborative undo, cross-server state, any concurrency claim before the staircase passes every gate (decision 4).

C# areas MVP12 touches:

- Registry choke points (engine-free): `InstanceRegistry.cs` — `AdvanceRevision` (`:429`, the single place every mutation passes; the dirty set hooks here), `ApplyMutation`/`ApplyServerGeneratedMutation` (`:243,296`), `BindNetId`/`TryGetByNetId` (`:863,829`), `Registered`/`Unregistered` (`:231,233`), `_mutationGate` (held across the whole operation — §F.3); `InstanceRecord.cs`; `WorldAcl.cs` (`WorldAclAuthorizer.Demand`); `RbxInstance.cs` setters (`:71,93,154-156,508,528,539,697,711` all call `AdvanceRevision`).
- **New** engine-free: `Replication/IReplicationFilter.cs`, `ReplicationDirtySet.cs`, `ReplicationDelta.cs` (revision-stamped), `WriteGrantLedger.cs`, `IWriteAuthorityResolver.cs`, `ClientWritePolicy.cs` (two values), `MutationIntent.cs`.
- **New** in `CoreAI.Mods` (needs the codec): `Networking/IntentGateway.cs`, `Networking/ReplicationPublisher.cs` (per-tick coalescing replacing the synchronous fan-out at `LuaCsRbxApiBindings.cs:857-864`), `ReplicationApplier.cs` (client side: applies deltas into the client registry under a host envelope, never through Lua).
- Serialization: `LuaCsRbxNetworkCodec.cs` (JSON, `CoreAI.Mods`) stays for remotes; deltas get a compact codec **below the bridge** (§F.3; audit §2.2 item 1 still holds).
- Physics outward: `IPartPropertySink`/`InstanceGameObjectBinder` on the client are write-only mirrors; `RbxTouchRelay` and gravity run on the server only.
- Bridge: `INetworkBridge` v2 `SendIntent`/`IntentReceived`, `SendDelta`/`DeltaReceived` (additive), implemented by `NullNetworkBridge` (loopback delta = no-op) and the Mirror bridge.

---

## B. The first executable slice

**Rung first: MVP8.** It needs no owner decision, it is the dependency of MVP11, and its first slices are engine-free, so they are verifiable today through `dotnet build CoreAI.RbxApi.Instances.csproj` / `CoreAI.Mods.csproj` / `CoreAI.Mods.Tests.csproj` while the editor holds the lock.

**Slice 8.0 — `Debris:AddItem` (the smallest vertical slice; about 1–2 agent-days).** It replaces one stub, crosses Lua → `ServiceCatalog` → engine-free service → scheduler → registry `Destroy` under an envelope → binder → `Destroying` signal, has mirror-pinned semantics (default 10 s, 1,000-item cap) and crisp negative twins. Implementer's spec:

1. `Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxDebris.cs` (**new**, engine-free): `sealed class RbxDebris : RbxInstance` with `AddItem(RbxInstance item, double lifetimeSeconds, DebrisCaller caller)` where `DebrisCaller` is `{ActorId, IsUnrestricted, WorldId}` taken from the trusted `LuaCsRbxModContext.ActorContext` — never from Lua arguments. Validation: `item == null` or not an `RbxInstance` → `RbxError.BadArgument("Debris:AddItem expects an Instance at argument 1", ...)`; `lifetime` NaN/±Inf → `BadArgument`; negative → clamp to 0 (record as OURS in the XML doc; the mirror does not specify). Authorization at call time: `WorldAclAuthorizer.Demand(registry, caller.ActorId, caller.IsUnrestricted, caller.WorldId, item, WorldAclDecision.Destroy, "schedule Debris destruction")`. Store `(item.Id, deadline, caller)` in a deadline min-heap plus an id→entry dictionary; re-adding an id replaces its deadline (OURS). Cap: when `Count == 1000` and a new item arrives, pop the oldest **by insertion order** and destroy it immediately (mirror: "the oldest debris will be destroyed instantly").
2. Scheduler primitive (**new**, `Scheduling/ModScheduler.cs`): `ScheduleHostCallback(double seconds, Action callback)` — an ownerless heap entry serviced in the delayed-threads slot (before Heartbeat, R4.2), not a Lua thread, so it survives mod unload (S5.1) and is never counted against `MaxThreadsPerActor`. `KillOwnedBy` must not touch it.
3. Firing: `registry.ApplyServerGeneratedMutation(caller.ActorId, caller.IsUnrestricted, caller.WorldId, "Debris destroy", () => { if (registry.TryGet(id, out inst) && !inst.IsDestroyed) inst.Destroy(); return 0; })`. If the ACL now refuses (ownership changed since scheduling), catch the `RbxError`, log one line through the mod log path, drop the entry — canonical state unchanged. An item already destroyed by then is dropped silently (the `Unregistered` event removes the entry eagerly).
4. Catalog: `ClassCatalog.cs` — `Register(new ClassDescriptor("Debris", "Instance", false, false, true, d => new RbxDebris(d)))`; `ServiceCatalog.CreateMvp2` — `RegisterTreeBacked("Debris")` replacing `:176`; `DataModelBootstrap.CreateGame` + `AttachWorldRoot` back-fill (same pattern as `MaterialService` at `:77-80`).
5. Binding: `LuaCsRbxInstanceBindings.cs` method table — `Method("AddItem", ...)` on class `Debris` reading argument 1 as an instance proxy and argument 2 as an optional number defaulting to 10, passing the context's actor. No `MaxItems`.
6. World package: pending Debris entries are ephemeral (WORLD_PACKAGE.md: "scheduler state ... are ephemeral"); nothing to serialise. Say so in the XML doc.
7. Skill/docs: add the `Debris` section and the wrong→right pair (`task.delay(10, function() part:Destroy() end)` → `Debris:AddItem(part, 10)`), per roadmap DoD (b).

Paired gate for the slice (EditMode, production composition exactly as `RungZeroDisconnectEditModeTests.ProductionHarness`: `InstanceRegistry(worldAclVersion: CurrentWorldAclVersion)`, `DataModelBootstrap.CreateGame`, `NullNetworkBridge`, `LuaCsRbxApiBindings`, `LuaCsModRuntimeFactory.Create`, mods loaded through `Stack.Runtime.LoadMod(actor, ...)`, time advanced only through `Bindings.Scheduler.Advance`):

| Positive (non-zero work through production) | Negative twin (rejection / absence / unchanged canonical state) |
|---|---|
| A mod runs `game:GetService("Debris"):AddItem(part, 0.5)`; after `Advance` sums to 0.5 s: `part.Parent == nil`, `Destroying` fired exactly once, `registry.TryGet(id)` false, in-memory binder count down by one, `RetainedMutationOperationCount` increased (the destroy went through an envelope). Default lifetime: `AddItem(part)` is alive at 9.99 s and gone at 10.0 s. | At 0.49 s the part is live and the `Destroying` count is 0. A stub build fails: the test asserts `game:GetService("Debris")` resolves to `RbxDebris`; today's `RbxStubService` raises `NOT_IMPLEMENTED`, so the gate is red until the slice lands. |
| 1,001 parts added: `Destroying` fires once for the first part immediately on the 1,001st call. | The other 1,000 parts are unchanged at that instant (revision and `Parent` asserted). |
| `AddItem(part, 5)`, then `part:Destroy()` at 0.2 s, advance past 5 s. | No second `Destroying`, no `INSTANCE_DESTROYED`, no log line beyond the manual destroy. |
| Actor A schedules Debris on A's `Owned` part in an ACL-versioned world; it is destroyed. | Actor B's `AddItem` on A's part is refused at call time with the ACL message naming actor B and the reason; A's part is untouched after 10 s; `Destroying` count 0. |
| — | `AddItem(5)`, `AddItem(part, 0/0)`, `AddItem(part, math.huge)` each raise `BAD_ARGUMENT` and schedule nothing (`Count` unchanged). |
| Mod unload after `AddItem(worldOwnedPart, 0.3)`: the part is still destroyed at 0.3 s (S5.1). | A mod-owned part is torn down by the unload first; the timer then finds nothing and stays silent. |

Follow-on order inside MVP8 (each slice gets the same gate shape, §E.1): **8.1** Value objects + `leaderstats` (engine-free classes, `Changed`, serializer support, ACL twin, corrupt-package twin) → **8.2** `CollectionService` (`TagAdded`/`TagRemoved`, tree-entry semantics) → **8.3** `Players`/`Player` completion (`GetPlayerByUserId`, `GetPlayerFromCharacter`, `Name`/`DisplayName` from an `IRbxPlayerProfileProvider` port defaulting to the synthetic profile, `Kick`, empty per-player containers) → **8.4** `TweenService` + `TweenInfo` + enums + Heartbeat driver on scaled time → **8.5** `Raycast`, `Gravity`, `Touched`/`TouchEnded` (first PlayMode folder, `FixedUpdate` pump) → **8.6** `Humanoid` + character adapter (blocked by §F.4 a) → **8.7** Tier-B fixtures and the ≥60 % gate.

---

## C. The Mirror question, answered concretely

### C.1 What seam already exists (do not invent a second one)

- `INetworkBridge` + message types + responder — engine-free, fitness-tested to live in `CoreAI.RbxApi.Instances` (`NetworkBridgeEditModeTests.cs:28`), resolved optionally by composition with a `NullNetworkBridge` fallback (`CoreAiModsInstaller.cs:169,331`). This **is** the transport seam; Mirror plugs in here.
- `StagedNetworkBridge` (session replacement) and `ScaleLoopbackBridge` (harness) are decorators over it; both must keep working over a Mirror inner bridge.
- `IRbxRuntimeTopology` (RunService answers) and `IRbxClockSource` (clocks) are the two already-swappable answers MVP11 replaces.
- `IActorIdentityProvider` is the identity port; admission must enter through it (decision 1).
- `IAiNetworkPeer`/`NetworkedAuthorityHost` in `CoreAI.Core` is an older, parallel notion of "host authority" for AI command execution. It must be re-implemented over the bridge topology (`Host`/`DedicatedServer` → `IsHostAuthority`, `Client` → `IsPureClient`) so the two answers cannot diverge.

### C.2 Package identity and layout (decision 2: separate optional package)

- Folder `Assets/CoreAIMirror/`, `package.json` name **`com.neoxider.coreaimirror`**, displayName "CoreAI Mirror transport", version in lockstep (7.x), `dependencies`: `com.neoxider.coreai`, `com.neoxider.coreaimods`. It becomes the seventh package: `tools/bump_version.py` already globs `Assets/*/package.json`; the CI `package-graph` job and AGENTS.md's "ALL SIX" wording must be updated to seven.
- Runtime assembly `Assets/CoreAIMirror/Runtime/CoreAI.Net.Mirror.asmdef`: `references` = `CoreAI.Core`, `CoreAI.RbxApi.Datatypes`, `CoreAI.RbxApi.Instances`, `CoreAI.Mods` (for `ConnectActor`/`DisconnectActor`), `VContainer`, `Mirror`, `Mirror.Components`; **`defineConstraints: ["MIRROR"]`**; `noEngineReferences: false`. An optional `versionDefines` entry `{name: "com.mirrornetworking.mirror", expression: "", define: "MIRROR"}` mirrors NeoxiderTools for a UPM install but is not the switch relied on.
- Why `defineConstraints` and not only `#if MIRROR`: Mirror injects `MIRROR` into the project scripting defines itself (`Assets/Mirror/CompilerSymbols/PreprocessorDefine.cs` in the NeoxiderTools install; `MIRROR_96_OR_NEWER`). With the constraint, Unity does not compile the assembly at all when Mirror is absent, so a dangling `Mirror` asmdef reference can never break the solo build and no `#if` guards are needed inside the package. NeoxiderTools' `Neo.Network` relies on Unity tolerating the missing reference plus `#if MIRROR`; that also works, but the constraint is the stronger guarantee and is testable.
- `CoreAI.Net.Mirror.Editor.asmdef` (same constraint) only if a menu/installer is needed; tests in `Assets/CoreAIMirror/Tests/EditMode/CoreAI.Net.Mirror.Tests.asmdef` with `defineConstraints: ["MIRROR", "UNITY_INCLUDE_TESTS"]`.
- Host install path: import Mirror (Asset Store/unitypackage or UPM), which sets `MIRROR`; add the package; register `MirrorNetworkBridge` and the admission adapter in the host's `LifetimeScope` **before** `RegisterCoreAiMods` so `ResolveOrDefault<INetworkBridge>` finds it. Solo hosts do nothing.

### C.3 How the solo build stays free of Mirror (and how it is proven)

- No file outside `Assets/CoreAIMirror/` may reference a `Mirror*` assembly or namespace. A fitness test (same pattern as `RbxApiInstancesArchitectureFitnessEditModeTests`) walks every `.asmdef` under `Assets/CoreAI*` and asserts (a) only `CoreAI.Net.Mirror*` reference `Mirror*`, (b) those carry `defineConstraints` containing `MIRROR`, (c) no `.cs` outside that folder contains `using Mirror`. This is N11.7's testable half plus the packaging rule.
- `tools/webgl_define_check.py` gains the `MIRROR` row: WebGL players are `Client`-only and must build with and without the define.
- The existing engine-free fitness tests already fail if a Mirror type reaches `CoreAI.Core`, `CoreAI.RbxApi.Datatypes` or `CoreAI.RbxApi.Instances`.
- NGO stays where it is: CoreAI packages never reference `Unity.Netcode`; the example game keeps its own NGO usage. Two stacks coexist in the project; only one is CoreAI's transport (§F.2 risk 3).

### C.4 The seam shape (additive v2 of `INetworkBridge`, engine-free)

Additions, all with trivial `NullNetworkBridge` implementations so the solo manifest is untouched:

```
// Networking/INetworkBridge.cs (additive)
public readonly struct RbxNetworkPeer { public string ActorId; public string SessionId; public string ConnectionHandle; }
public sealed class RbxNetworkPeerDisconnected { RbxNetworkPeer Peer; RbxNetworkDisconnectReason Reason; }
public interface INetworkBridge
{
    // existing members unchanged
    int MaxPayloadBytes { get; }                        // Null: 65,536 (codec cap); Mirror: min(codec cap, transport MTU for unreliable)
    event Action<RbxNetworkPeerDisconnected> PeerDisconnected;   // Null: never; Mirror: OnServerDisconnect
    void SendIntent(RbxNetworkIntentMessage message, Action<RbxNetworkResponse> response);   // MVP12
    event Action<RbxNetworkIntentMessage, RbxNetworkRequestResponder> IntentReceived;         // MVP12
    void SendDelta(RbxReplicationDelta delta, string recipientActorId /* null = all */);       // MVP12
    event Action<RbxReplicationDelta> DeltaReceived;                                           // MVP12
    double ServerClockOffsetSeconds { get; }            // Null: 0; Mirror: NetworkTime.offset — consumed by IRbxServerClockOffset
}
```

Rules the Mirror implementation must obey (each is a gate in §E):

- **Sender binding.** The wire envelope carries `remoteId`, `direction`, `reliability`, `correlationId` and `payload` — **never** an actor id. `MirrorNetworkBridge` fills `SenderActorId` from its own `connectionId → RbxNetworkPeer` map, populated only by the admission adapter. `DemandAdmittedNetworkSender` stays as defence in depth.
- **Admission before registration.** `RegisterActor` is called by `LuaCsRbxApiBindings.ConnectActor` after admission, never by the bridge on first packet; a packet from an unadmitted connection is dropped and counted (`unadmittedPacketsDropped`), not delivered.
- **Correlation.** `SendRequest` allocates a server-scoped `uint` correlation id per `(connection, request)`; a response completes a request only if `(connection, correlationId)` matches an open entry; late responses after the 30 s timeout are counted and dropped.
- **Rate.** The per-actor per-group admission that `NullNetworkBridge` implements moves into a shared engine-free `RbxNetworkRateLimiter` both bridges use, so a transport cannot ship without it (audit §4.5).
- **Delivery timing.** Loopback delivers synchronously inside `SendEvent`; Mirror delivers on Mirror's update. Every remote test must advance the scheduler/frame between send and assert; the N11 fixtures are written that way from day one.

Admission adapter (`Assets/CoreAIMirror/Runtime/CoreAiMirrorAuthenticator : Mirror.NetworkAuthenticator`): `OnServerAuthenticate(conn)` reads the opaque credential message the client sent, calls the host-supplied provider through the new engine-free port

```
// CoreAI.Core/Authority/IActorAdmissionProvider.cs (additive; a host implements it next to IActorIdentityProvider)
public readonly struct ActorCredential { public byte[] Opaque; public string TransportAddress; }
public sealed class ActorAdmissionResult { public bool Admitted; public string Reason; public ActorContext Context; public long UserId; public string Name; public string DisplayName; }
public interface IActorAdmissionProvider { ActorAdmissionResult TryAdmit(in ActorCredential credential, string worldId); }
```

and only on `Admitted` calls `ServerAccept(conn)`; otherwise `ServerReject(conn)` **before** any `Player`, chat, mod, remote or world access exists. The `ActorContext` inside the result is necessarily restricted (`IssueRestricted`); an unrestricted context cannot be minted here. There is **no anonymous fallback** (decision 1): a missing `IActorAdmissionProvider` in an online composition is a startup error, not a default.

### C.5 Transport notes that shape the plan

- Desktop host/client: KCP (bundled). WebGL pure client (MVP13 validation) requires Mirror's `SimpleWebTransport`; KCP is not available in browsers. Not an MVP11 deliverable, but the bridge must not assume UDP (the MTU is read from `Transport.GetMaxPacketSize(channel)` at runtime, Q4).
- `NetworkTime.offset` backs `ServerClockOffsetSeconds`; its variance is what freezes the N11.6 tolerance.
- No `NetworkBehaviour`/`SyncVar` is ever exposed to mods (M1.7); the bridge uses `NetworkServer.RegisterHandler<T>`/`NetworkClient.RegisterHandler<T>` message handlers only. NeoxiderTools reflection sync and context relay are not used (decision 2).

---

## D. The authority model for decision 3

Security boundary: the client sends **intent**; the server derives the actor from the connection, checks, applies, replicates. No value in an intent selects authority.

### D.1 Where a client write enters

1. Client-side Lua writes a replicated instance → `LuaCsRbxInstanceBindings` write path → `IWriteAuthorityResolver.Resolve(actorContext, instance, member) → WriteVerdict { ApplyLocalOnly | Reject | ForwardAsIntent }` (the roadmap's reserved resolver seam, engine-free, in `CoreAI.RbxApi.Instances`).
   - `RobloxParity` without a grant → `ApplyLocalOnly` (local registry write marked `LocallyDiverged`; the next server delta overwrites it).
   - `Strict` without a grant → `Reject` → `NOT_AUTHORITY` with the "use a RemoteEvent" hint.
   - Either policy **with** a grant covering `(instance, action)` → `ForwardAsIntent`: the client does **not** apply locally; it sends a `MutationIntent` and waits for the authoritative delta (no prediction, no divergence).
2. `MutationIntent` (engine-free, `Replication/MutationIntent.cs`): `{ OperationId (client GUID), TargetInstanceId, ExpectedRevision, Action (WriteProperty | Reparent | Create | Destroy | SetAttribute | AddTag | RemoveTag), Member, EncodedValue }`. **It carries no actor, owner, role, grant id or capability.**
3. `INetworkBridge.SendIntent` → Mirror message → server `IntentReceived` with `RbxNetworkIntentMessage.SenderActorId` stamped by the bridge from the connection map.

### D.2 Where the grant is checked (server, one gateway, in order)

`IntentGateway.Handle(message, responder)` in `CoreAI.Mods` (it needs the codec), calling only engine-free authorization primitives:

1. **Actor rebinding** — `actorId = message.SenderActorId` (bridge-stamped). The gateway looks up the admitted `ActorContext` by actor id in the bindings' admitted set. If the resolved context has `Grants.IsUnrestricted`, the gateway **refuses** (`NOT_AUTHORITY`, "intents are for clients") — the host never goes through this path, so a spoofed host can only ever produce a refusal.
2. **Rate** — `RbxNetworkRateLimiter` group `MutationIntent` (a separate bucket, tighter than remotes; frozen before the run).
3. **Payload** — size cap (`MaxPayloadBytes`), structure via the codec (depth/entries), `Member` in the replication whitelist, value type matching the member.
4. **Grant** — `WriteGrantLedger.Allows(actorId, targetInstance, action, registry)` (D.3). No grant → `NOT_AUTHORITY` naming the actor and "no host grant covers <action> on <path>"; canonical state unchanged.
5. **ACL** — `WorldAclAuthorizer.Demand(registry, actorId, isUnrestricted: false, worldId, target, decision, operation)` exactly as server Lua for that actor would be checked. A grant never widens the ACL: a client granted "write Workspace subtree" still cannot destroy a `HostProtected` singleton or another actor's `Owned` instance unless the ACL allows it.
6. **Envelope** — `registry.ApplyMutation(new MutationEnvelope(actorId, target, message.OperationId, message.ExpectedRevision), () => apply())` — idempotent replay, stale-revision refusal, ledger retention (`InstanceRegistry.cs:243-293`).
7. **Apply** — through the same `LuaCsRbxModContext` write helpers the server uses for that actor, so `GetPropertyChangedSignal`, binder, revision advance and dirty marking happen identically; never a raw registry poke from transport code.
8. **Replicate and respond** — the dirty set publishes the revision-stamped delta to all filtered recipients; the responder returns `{ok, revision}` or `{reason}`.

Any failure at steps 1–6 leaves canonical state unchanged and produces a denial naming the authenticated actor and the reason, without another actor's private state.

### D.3 What the grant is (data shape) and who can mutate it

```
// CoreAI.RbxApi.Instances/Replication/WriteGrant.cs (engine-free, immutable)
public sealed class WriteGrant
{
    public string GrantId;            // server GUID; never client-supplied
    public string GranteeActorId;     // durable actor id (never UserId, never session)
    public WriteGrantScope Scope;     // World | Subtree(InstanceId root) | Instance(InstanceId)
    public WriteGrantActions Actions; // flags: WriteProperty | SetAttribute | Tag | Reparent | Create | Destroy
    public string IssuedByActorId;    // must be the unrestricted host actor at issue time
    public long IssuedAtUnixSeconds;
    public long? ExpiresAtUnixSeconds;
    public bool Revoked;
}
```

- **Ledger**: `WriteGrantLedger` owned by the server world (one per `InstanceRegistry`), keyed by `GranteeActorId`; `Allows` resolves `Scope` against the live tree (a `Subtree` grant follows reparenting: the target must currently be a descendant of the root). Grants are **session-scoped and not persisted** in the world package in MVP12 (OURS; revisit with measurement). Revocation is immediate; in-flight intents re-check at step 4.
- **Mutation of the ledger**: only through `WriteGrantLedger.Issue/Revoke(ActorContext issuer, ...)`, which demands `issuer.IsTrusted && issuer.Grants.IsUnrestricted`. That context exists only in the server process, issued by `ActorIdentityComposition` (private proof type in `CoreServicesInstaller`). Exposed to the host as (a) a C# API, (b) a Hub page action, (c) an LLM tool `grant_world_write` composed exactly like MCP host-admin (`CoreAiMcpServer` refuses non-unrestricted actors). **No Lua surface** (Roblox has none; the 1:1 rule forbids inventing one); **no remote or intent** can touch the ledger — `IntentGateway` has no `Grant` action.
- **Audit**: every issue/revoke and every grant-authorized apply is logged with grant id, grantee, issuer, target id and revision.

### D.4 The host's always-granted authority without a spoofable special case

- The host holds every right because its writes **never enter the intent path**: they originate in the server process under the composition-issued unrestricted `ActorContext` and go straight to `ApplyServerGeneratedMutation`. There is no "host" row in the ledger and no `IsHost` flag on the wire to forge.
- Every message that arrives over the bridge is, by construction, a client message: its actor is derived from the connection (C.4), admission can only issue restricted contexts (`ActorContext.IssueRestricted` throws on unrestricted grants), and the gateway refuses unrestricted actors outright (D.2 step 1). A client that somehow presented the host's id would still be a restricted context bound to a client connection and would be refused, not elevated.
- The host player's own **client-context** scripts in host mode are ordinary clients: they follow `RobloxParity`/`Strict` like anyone else. "The host holds every right" refers to the host's server-side actor (its AI agent, Hub tools, server mods), which is exactly what the owner described as "the server is still the one that writes".
- The current conflation `IsHost => Grants.IsUnrestricted` is retired by MVP11's declared contexts; until then, in solo, the resolver is never consulted (there is no client process).

---

## E. Acceptance gates with negative twins

All gates follow `MVP2_ACCEPTANCE_MANIFEST.md` §3b (production composition: `RegisterCorePortable` + `RegisterCoreAiMods`, or the `ProductionHarness` shape from `RungZeroDisconnectEditModeTests`; mods loaded via `LoadMod`; time via `Scheduler.Advance`/the frame pump) and §5 (every row has a negative twin; a zero-work counter fails). Frozen fixture ids, expected test counts and budgets are written into each rung's own manifest **before** the run; the P1 expected EditMode count (3256 today) is re-frozen per rung.

### E.1 MVP8

| Gate | Positive (production, non-zero work) | Negative twin |
|---|---|---|
| P8.1 players | `ConnectActor` → one `Player`; `GetPlayers`, `GetPlayerByUserId`, `GetPlayerFromCharacter(character)`, `TryGetByActorId` agree; server context `LocalPlayer == nil`; solo exposes exactly one synthetic client player with `UserId == 1`. | Invoking `PlayerAdded.Fire` directly is not a pass (the test counts through `ConnectActor` only); after `DisconnectActor` every lookup returns nil/false; `GetPlayerByUserId(999)` is nil; a non-nil server `LocalPlayer` fails; a second `ConnectActor` for the same actor fires nothing. |
| P8.2 Humanoid | Through `RbxHumanoidControllerAdapter` at 0.28 m/stud and the 1:1 smoke: `TakeDamage(30)` → `HealthChanged(70)`; `Health = 0` → `Died` once; `MoveTo` reaches → `MoveToFinished(true)`; `WalkSpeed = 16` measured on the controller as 16 × 0.28 m/s ±2 %; `leaderstats.Coins.Value += 1` → `Changed(1)`; `LoadCharacterAsync` yields and resumes with a new `Character`. | `Health = 150` reads `MaxHealth` (clamp, not error); `Died` does not fire twice while dead; `MoveTo` to an unreachable point → `MoveToFinished(false)` at 8 s and not before; `Sit = true` raises the loud stub; a direct write to the metric controller outside the adapter is not observable in Lua (the adapter is the only reader); a 1:1 run that reports 0.28 speeds fails. |
| P8.3 physics (PlayMode) | A dropped unanchored part accelerates at 196.2 studs/s² × 0.28 m/stud within 3 % over 0.5 s; a real collision fires `Touched(other)` then `TouchEnded` on both parts; `workspace:Raycast` hits the expected part with converted `Position`/`Normal`/`Distance`. | Non-contacting bodies fire nothing; a `CFrame` teleport into overlap fires no `Touched` (mirror); the host scene's `Physics.gravity` is byte-equal before/after; `Raycast` with an `Exclude` filter of the hit part returns nil; a direction longer than 15,000 studs → `BAD_ARGUMENT`. |
| P8.4 Debris/Tween/Collection | §B table for Debris; a tween on `Position` + `Transparency` reaches its goals on scaled time and `Completed(Completed)` fires once; `GetTagged("Kill")` returns exactly the tagged in-tree parts; `GetInstanceAddedSignal("Kill")` fires when a tagged part is parented into the tree. | Tweening `CanCollide` (bool) raises the loud stub before `Play`; `Cancel()` → `Completed(Cancelled)` and never `Completed`; `Pause()` fires nothing; a destroyed tweened instance never reports completion; an untagged part is absent from `GetTagged`; a tagged part with `Parent = nil` is absent and fires the removed signal; `timeScale = 0` freezes tweens and Debris (D9). |
| P8.5 corpus | Frozen Tier-A (20) + Tier-B ids including `kill-brick`, `touch-pickup-with-leaderstats`, `door-tween` pass unmodified; ≥60 % of A+B with the exact ids listed in the MVP8 manifest. | Corrupted twins of the three named fixtures fail with the expected diagnostic text; fewer discovered fixtures than the frozen count fails; a fixture that passes only with `pcall` around a `NOT_IMPLEMENTED` counts as failing (the harness asserts zero stub hits). |
| P8.6 one serializer | A world with `leaderstats` (Int/Number/String/Bool/ObjectValue) and a `Humanoid` round-trips through save → `Stage/Commit` → identical golden tree, ids and `Value`s. | A package whose `IntValue.Value` is `1.5`, or whose `ObjectValue` points at an id outside the package, is rejected atomically; `Player` nodes are still rejected. |

### E.2 MVP11

| Gate | Positive | Negative twin |
|---|---|---|
| N11.1 admission | A valid credential through `IActorAdmissionProvider` (host-supplied test provider, HMAC token) → `ServerAccept`, one durable actor, session, `Player`, restricted `ActorContext`, join snapshot, then gameplay; counter `admitted = 1`. | Missing, malformed, wrong-signature, expired and replayed (same nonce) credentials → `ServerReject` before `Player` creation (`Players:GetPlayers()` unchanged, chat factory `Resolve` never called, no registry record created, `unadmittedPacketsDropped > 0`); a composition without an admission provider fails to start; an "allow anonymous" flag does not exist. |
| N11.2 identity | Reconnect with the same credential within the frozen window resumes the same durable actor: mod ownership, chat history and quotas resolve to the same `ActorId`; a new `SessionId`. | A client packet with a forged actor/UserId/role/owner field is impossible by envelope shape (no such field); a second live session for the same durable actor is refused per the policy chosen in §F.4 (e); a client cannot read or cancel another actor's chat (the G4 twin re-run over the wire). |
| N11.3 real RemoteEvent traffic | Host↔client, targeted, broadcast and unreliable fixtures move real Mirror packets: `packetsSent/Delivered` and `bytes` non-zero on the Mirror counters; delivery order asserted on reliable. | `FireClient` from client context → `NOT_AUTHORITY`; an oversize payload → `PAYLOAD_TOO_LARGE` on the sender, nothing received; the 501st reliable fire per second is refused with `BUDGET_EXCEEDED`; loopback counters are zero in the online run (a pass with Mirror counters at zero fails); a targeted event reaches only its recipient (other clients' `OnClientEvent` count 0). |
| N11.4 RemoteFunction | 50 concurrent `InvokeServer` calls from two clients each return their own result; the 30 s timeout yields the documented Lua error. | A crafted response with a foreign or reused correlation id completes nothing; a late response after timeout is dropped and counted; a response from another connection is dropped; a request that never answers does not block other requests. |
| N11.5 teardown | Connect → `PlayerAdded` once; graceful `Disconnect()` and a transport kill each → `PlayerRemoving` once with `PlayerExitReason`; actor threads killed, quotas/rate windows/remote signals released, chat released; reconnect follows policy. | A ghost `Player` after either disconnect fails; a second disconnect fires nothing; a still-connected client's state is untouched (threads, signals, history identical). |
| N11.6 contexts and clock | A `client` mod on the client process: `IsClient() == true`, `LocalPlayer` non-nil; a `server` mod on the host: `IsServer() == true`, `LocalPlayer == nil`; client `GetServerTimeNow()` within the frozen tolerance of the server's (tolerance frozen from `NetworkTime.rttVariance` before the run). | `DataStoreService`/`ServerStorage`/`FireClient` from client context → `CONTEXT_VIOLATION`/`NOT_AUTHORITY`; a client with its wall clock shifted by +1 h still reports server time within tolerance; a tolerance chosen after seeing the numbers fails. |
| N11.7 optional boundary | With Mirror present all N11 gates run on `MirrorNetworkBridge`. | With Mirror absent (define off) the full solo manifest passes on `NullNetworkBridge`, `CoreAI.Net.Mirror` is not compiled, and the packaging fitness test (§C.3) passes; a Mirror type in an engine-free assembly fails the existing fitness tests. |
| N11.8 join snapshot (pulled forward, static) | A client admitted after the server built a tree receives `Workspace`/`ReplicatedStorage`/`Lighting` with identical ids via `ExportSnapshot` → `Stage/Commit`; `ReplicatedStorage.Remote:FireServer()` from the client resolves to the server's remote. | `ServerStorage`/`ServerScriptService` content and every `server`-context source are absent from the client registry (byte-level assertion on the payload); a change made on the server after admission is **not** seen by the client — the honest MVP11 statement, and the twin MVP12 flips. |

### E.3 MVP12

| Gate | Positive | Negative twin |
|---|---|---|
| R12.1 filtering | The server tree under whitelisted containers reaches each intended client with identical ids/`NetId`/whitelisted properties; `filteredItemsAvoided > 0`. | The `ServerStorage` subtree and non-whitelisted properties never appear; a later delta touching a filtered instance is not emitted (its delta count is 0); wrong-recipient delivery fails. |
| R12.2 canonical authority | A server mutation converges on all clients within N frames; under `RobloxParity` a client write applies locally and is overwritten by the next delta; under `Strict` it raises `NOT_AUTHORITY`. | The client-local write never reaches the server (server revision unchanged) or another client; `Open` does not exist (a test asserts the enum has two values); a delta message sent by a client is dropped and counted. |
| R12.3 intents | A granted client's intent (correct revision, new operation id) applies once, advances the revision, replicates, and responds `{ok, revision}`; `intentsApplied > 0`. | Each of: unadmitted sender, unrestricted-looking actor, no grant, grant on a different subtree, ACL-refused target (`Owned` by another actor, `HostProtected` destroy), stale revision, duplicate operation id (returns the first result, applies once), malformed payload, non-whitelisted member, over-rate → canonical state and revision unchanged and a denial naming the actor and reason; revoking a grant mid-stream refuses the next intent. |
| R12.4 late join | A client admitted mid-churn receives the snapshot at revision R, then ordered deltas > R, and converges to the server golden tree while the server keeps mutating; snapshot bytes equal `ExportSnapshot` bytes for the same revision. | A duplicate delta → ignored deterministically; out-of-order → held or resynced, never applied out of order; a missing delta → resync from a fresh snapshot; a second snapshot mapper or any id remap fails (the same `InstanceId`s are asserted). |
| R12.5 physics boundary | A server-simulated falling part's `CFrame` replicates outward; the client renders it. | A client `CFrame`/velocity write on a physics part stays local and is overwritten; a crafted transform delta from a client is dropped; `SetNetworkOwner` raises the deferred stub. |
| R12.6 churn and scale | The frozen 100-instance churn fixture and the staircase 2/5/10/20 clients over real Mirror on the frozen machine: non-zero packets, mutations, filtered items, snapshots, reconciliations; every published limit within pre-frozen CPU/memory/bandwidth/latency budgets. | Zero-work results, budgets or workload changed after measurement, cross-client leakage, or a claimed count that did not pass every gate fails; per decision 4 no number is published until 20 passes, and the heap-slope budget must pass first (§F.2 risk 2). |

---

## F. Honest cost and risk

### F.1 Effort (estimates, not measurements)

| Rung | Estimate | Where it goes |
|---|---|---|
| MVP8 | 5–7 agent-weeks | Debris/Values/Collection 1; Tween 1; Players completion 0.5; Raycast/Gravity/Touched + PlayMode folder + `FixedUpdate` pump 1; Humanoid + adapter 1.5–2.5 (blocked by §F.4 a); Tier-B corpus + skill 1. |
| MVP11 | 7–10 agent-weeks | Declared contexts + header/manifest + `IsNetworkServer` retirement (MVP5-lite) 1–2; admission port + test provider 1; Mirror package + bridge + rate limiter + correlation 2; filtered join snapshot + client composition (client `RbxWorldHost`/headless host, client Lua runtime for `client`/`shared` sources) 2; teardown/clock/Kick 0.5; N11 manifest + adversarial QA 1–2. |
| MVP12 | 8–12 agent-weeks | Dirty set + delta codec below the bridge + publisher 3; filter 0.5; grants + gateway + resolver 2; late join + resync 2; physics outward 0.5; churn + staircase + budgets 2. |

### F.2 What is most likely to go wrong

1. **Contexts are derived from grants.** `IsNetworkServer => Topology != Client && Grants.IsUnrestricted` means "restricted actor = client" today. MVP11 needs a declared context per source and a process topology; until that lands, host-mode server scripts written by a restricted actor's agent would run as "client". This is MVP5 work the online plan's ladder skipped; it is on MVP11's critical path.
2. **Capacity is blocked before transport.** The heap-slope budget already fails at 20 actors in solo (TODO `alloc-fix` OPEN) and the chat admission cap fails at 100. Decision 4's 20-client bar cannot be frozen until the solo allocation fix lands and the staircase is repeated in a Standalone player.
3. **Two network stacks.** NGO 2.11 is installed and used by the example game; Mirror is added next to it. Both define `NetworkManager` types (different namespaces); both want update time; `DefaultNetworkPrefabs.asset` and the multiplayer-tools packages stay. Nothing forbids coexistence, but every future audit will ask why; the honest answer must be recorded, or the owner should reconsider whether the example game's NGO usage is retired.
4. **Serialization sits above the bridge.** `LuaCsRbxNetworkCodec` (JSON, `CoreAI.Mods`) is fine for remotes and the join snapshot, but MVP12 delta batching needs a compact revision-stamped codec below the seam; that is new engine-free code with its own conformance tests.
5. **The mutation gate.** `_mutationGate` is held across the whole user operation and every property setter bumps `AdvanceRevision` under it; with a publisher subscribing to `AdvanceRevision`, contention and re-entrancy must be measured, not assumed.
6. **PlayMode gates cannot run today** (editor lock); Touched/gravity/Humanoid evidence arrives only at the next editor start. Engine-free slices first is the mitigation, not a fix.
7. **Join snapshot filtering.** The serializer packages every mod source and rejects `Player` nodes; the filter must exclude `server` sources (needs declared contexts) and the roster must travel separately. Getting this wrong ships server logic to clients (M7.1).
8. **`StagedNetworkBridge` queue cap 256** during session replacement will overflow under real traffic; the failure mode ("degraded activation") must be tested over Mirror.
9. **Lockstep tooling** assumes six packages in prose and possibly in CI; the seventh package needs the `package-graph` job checked.
10. **WebGL client** needs a WebSocket transport and no threads in the bridge; not in MVP11 scope, but the bridge must not be written UDP-first.

### F.3 Existing code that will fight this design (verified)

- `LuaCsRbxInstanceBindings.cs:119,145` (`IsHost`, `IsNetworkServer`) — see F.2 risk 1.
- `LuaCsRbxApiBindings.cs:658-666` (`GetLocalPlayer` calls `EnsureNetworkActor`): reading `LocalPlayer` creates a `Player` and registers the actor. Correct on a client process, wrong on the server for any non-host context; it must become a lookup once MVP11 admission owns creation.
- `RbxPlayers.cs:38,74` (`_nextUserId++`): a synthetic, per-session `UserId`; MVP11 takes it from `ActorAdmissionResult`.
- `NullNetworkBridge.SendEvent` drains synchronously; tests that assert delivery inside the same call pass on loopback and fail on Mirror.
- `RbxWorldPackageSerializer.cs:59,865` rejects `Player`; `InstanceTreeSerializer.cs:366` rejects unknown classes — every MVP8 class must be added to the durable surface or worlds stop saving.
- `LuaModRuntimeTickDriver.cs:71` pumps from `Update` only; physics-step semantics (`PreSimulation` on `FixedUpdate`, roadmap §5.2.3) do not exist yet.
- `CoreAI.Core/Authority/NetworkedAuthorityHost.cs` and `IAiNetworkPeer` — a parallel host/peer notion that must be sourced from the bridge topology.
- `RbxWorldHost : MonoBehaviour` (`RbxWorldHost.cs:18`) is a single scene host; a pure client needs a client composition that stages the join snapshot through `IRbxWorldSessionHost` rather than `Initialize()`-ing a fresh tree.
- `ActorGrantSet` abstract grant strings are only `"read"`/`"write"` today; the write-grant ledger is deliberately **not** modelled as `ActorGrantSet` entries because those flow inward with the context (they can only narrow), while world write grants are per-target and revocable.

### F.4 Decisions nobody has made (do not build past these without an answer)

- **(a) Which metric character controller backs `Humanoid`. DECIDED 2026-09-05 — a minimal new `RbxCharacterMotor` inside CoreAI.** Not NeoxiderTools' `CharacterMovementFundamentals`: CoreAI is a framework package and cannot take a dependency on a separate product — the demo scenes that already reference NeoxiderTools controllers are the bug, not the precedent. Not the example game's `ArenaPlayerMotor` either: a package must not reference its own example assembly, and the direction of dependency would be backwards. The deciding argument is not packaging though — it is that `Humanoid` has an exact metric contract (`WalkSpeed` studs/s at 0.28 m/stud, `JumpPower` vs `JumpHeight` with `UseJumpPower` choosing the impulse, `MoveTo` with the 8 s timeout, the `HumanoidStateType` subset, `MoveDirection` read-back). Adapting a general-purpose controller means re-deriving every one of those from someone else's tuning; a focused motor written against the mirror is smaller AND the only version whose numbers can be asserted. `RbxHumanoidControllerAdapter` stays the seam, so a host that wants its own controller replaces the motor and keeps the Lua surface. Slice 8.6 is unblocked.
- **(b) Whether the client process runs a Lua VM for `client`/`shared` sources in MVP11.** Roblox parity and the online plan's N11.6 say yes (LocalScripts run on the device); the product framing ("each player's AI agent edits Lua") runs agents on the host. This plan assumes **yes** (§A.2); if the answer is no, N11.3/N11.6 can only be passed by non-production harness clients and the gates must be rewritten.
- **(c) Source visibility to clients.** Which mod sources ship in the join snapshot (only `client`/`shared` by declared context is the M2.2-conformant answer) and whether the `shared` sources of one actor's mod are visible to every client.
- **(d) Credential shape and the durable `UserId`/`Name` source.** Decision 1 fixes *who* (a host-supplied provider) but not the credential format the Mirror adapter forwards (this plan: opaque bytes plus transport address) nor where `UserId`/`Name`/`DisplayName` come from (this plan: the admission result). Confirm.
- **(e) Reconnect and simultaneous-session policy.** §7.1 of the online plan named it; the DECIDED block does not settle it. This plan's default: a second live session for the same durable actor is refused while the first is alive; reconnect within a frozen grace window resumes. N11.2 cannot be frozen without the owner's answer.
- **(f) Grant persistence.** This plan keeps write grants session-scoped and unsaved; if the owner wants co-building rights to survive a world reload, the world package format gains a versioned `grants` entry and W3.x gains a twin.
- **(g) `Humanoid` passive regeneration. DECIDED 2026-09-05 — implement it the way the mirror says it exists, as a script and not as a class feature.** `Humanoid.yaml` is explicit: "By default, a passive health regeneration **script** is automatically inserted into humanoids", 1 % of `MaxHealth` per second, and the documented way to disable it is to add an empty `Script` named **Health** to the character. So `RbxHumanoid` itself never regenerates — the default character template carries the regeneration behaviour, and a character containing its own `Health` child does not get it. Baking regen into the class would look identical in a kill-brick fixture and diverge in every damage-over-time one, and it would make the documented opt-out impossible to honour. No `DEV-13` deviation is needed.
- **(h) NGO's future in the project** (F.2 risk 3).

### F.5 Contradictions between the brief and the repository or mirror, for the record

1. Brief: `Players` is a placeholder. Repo: tree-backed, real `RbxPlayers`/`RbxPlayer`, lifecycle seam landed (§0.1). MVP8's Players work is completion, not creation.
2. Brief: Mirror is not installed (true) and, by implication, the project has no networking stack. Repo: NGO 2.11 and the Unity multiplayer packages are installed and used by the example game.
3. Roadmap MVP8: Humanoid is "backed by CoreAI's existing player/controller". Repo: no such controller in the CoreAI packages.
4. Roadmap §5.2.2 `INetworkBridge` sketch (`int playerId`, `Invoke`/`Respond`). Repo: string actor ids and `SendRequest` with a responder. This plan builds on the shipped shape.
5. Roadmap MVP11 depends on MVP5 "contexts declared". The online plan's ladder omits MVP5, and no context declaration exists in the repo. This plan pulls a minimal context declaration into MVP11 and says so.
6. The 2026-09-02 architecture audit's findings 1, 2, 3, 4, 6 and 9 are closed by rung zero (`5c62c43d`); its finding 5 (an unrestricted host actor for every role) and the codec/lock findings remain and are carried in §F.2/§F.3.
7. Mirror versus roadmap: the `Debris` 1,000-item cap; `TweenService.GetValue`/`SmoothDamp`; `CollectionService.GetAllTags`/`TagAdded`/`TagRemoved`; `LoadCharacter` deprecated in favour of `LoadCharacterAsync`; `Humanoid.Health` tagged `NotReplicated` (§0.2). The mirror wins in every case.
