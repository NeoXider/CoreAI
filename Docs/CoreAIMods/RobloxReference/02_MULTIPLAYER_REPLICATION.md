# 02 — Multiplayer & Replication (Roblox Reference, Normative)

> **Status:** NORMATIVE for CoreAI mod multiplayer.
> CoreAI's mod runtime (Unity + Mirror) MUST follow the rules below wherever a Roblox-compatible
> behavior is promised to mod authors. Rules are numbered `M<section>.<n>` and are individually testable.
> Researched against current official Roblox documentation (create.roblox.com/docs, verified via the
> `Roblox/creator-docs` source repository) on **2026-07-22**. Statements marked **[UNCERTAIN]** are not
> fully pinned down by official docs and must be re-verified before being treated as contract.
>
> Mirror context used in the mapping notes: server-authoritative `[Command]` / `[ClientRpc]` /
> `[TargetRpc]`, `[SyncVar]`, `NetworkServer.Spawn`, `netId` identity.

---

## 1. Client-server model

**M1.1** The server is the single authority. Official wording: "The Roblox server is the ultimate
authority for maintaining the game's state, and is responsible for keeping all connected clients in
sync with the server."

**M1.2** The server runs the authoritative "runtime" data model; every connected client receives a
*copy* of it. Clients render the world and run client-side scripts against their local copy.

**M1.3** Replication is continuous and synchronizes three streams: the data model (instances,
properties), the physics simulation, and chat messages. "Replication logic exists on both the client
and server to ensure synchronization."

**M1.4** There are exactly three sanctioned data flows: Client → Server, Server → one Client,
Server → all Clients. Clients cannot communicate directly with other clients; client-to-client
messaging must be relayed through the server (fire to server, server re-broadcasts).

**M1.5** The client legitimately simulates locally:
- its **own character's movement and physics** (the character assembly is network-owned by that
  client by default — see section 4);
- any **unanchored parts/assemblies it has network ownership of**;
- purely local presentation: its `Camera` ("Every client takes these settings and creates its own
  camera view that the server can't directly modify"), its `PlayerGui`, its `PlayerScripts`
  ("The server cannot access this container").

**M1.6** Client → server writes are restricted to exactly two channels:
1. **Remotes** (`RemoteEvent` / `UnreliableRemoteEvent` / `RemoteFunction`) — section 3;
2. **Physics/character replication** for network-owned assemblies, including certain Humanoid
   movement state: "as players move their characters, certain `Humanoid` properties, such as
   states, are communicated to the server, which passes this information to other connected
   clients."
Everything else a client changes stays local (see M2.6). Note the docs explicitly warn that
client-replicated character state is not trustworthy: `Humanoid.WalkSpeed` "is a client-replicated
property which can be modified locally, so it should not be used as a security mechanism."

**M1.7** Filtering is permanent. `Workspace.FilteringEnabled` ("Determines whether changes made from
the client will replicate to the server or not") is tagged Deprecated/Hidden with the description
"This property is discontinued and no longer takes effect." There is no supported mode in which
arbitrary client changes replicate to the server. CoreAI MUST NOT expose any "trust the client"
replication switch.

**M1.8** Exploiter threat model — a determined exploiter can (official list):
- "Decompile any replicated LocalScript or any ModuleScript, even if they never run on the client";
- "Take network ownership of their character and any unanchored parts";
- "Trigger client-initiated events such as Touched events or ProximityPrompt activations at any
  range or frequency";
- "Modify their player's position, physics, or interactions with the world";
- "Fire or invoke RemoteEvents and RemoteFunctions at any frequency with arbitrary arguments
  (besides the first Player argument)" — the sender identity is engine-attached and cannot be spoofed;
- "Change anything in their local DataModel without firing any expected events";
- "Arbitrarily alter the behavior of any locally running code".
Consequence: "any security measure that relies on client-side enforcement will eventually be bypassed."

**M1.9** The canonical server loop for every client-triggered action: "Receive input from the
client; Validate that the requested action is possible and permissible; Execute the action and
update its authoritative state; Replicate the results to all relevant clients."

**M1.10** Design for latency: "Most players on Roblox game between 100–300 milliseconds of network
latency." Studio playtests default to zero latency; validation tolerances must absorb real latency.

**Mapping to Unity/Mirror**
- M1.1/M1.9 map directly to Mirror's server-authoritative model: mod logic runs on the server,
  clients send intent via `[Command]`, server mutates state and pushes via `[SyncVar]`/`[ClientRpc]`.
- M1.6's "player argument cannot be spoofed" = Mirror's `[Command]` sender is derived from the
  connection (`connectionToClient`), never from an argument. CoreAI must inject the acting player
  server-side, exactly like Roblox prepends `player` to `OnServerEvent`.
- M1.5's local character simulation = client-authoritative movement for the local player object
  (e.g., client-auth `NetworkTransform`), with server-side movement validation (M7.x).
- M1.7 = never expose Mirror client-authority `SyncVar` writes to mods; there is no such concept.

**Sources**
- https://create.roblox.com/docs/projects/client-server
- https://create.roblox.com/docs/scripting/security/security-tactics
- https://create.roblox.com/docs/scripting/events/remote
- https://create.roblox.com/docs/reference/engine/classes/Workspace (FilteringEnabled)
- https://create.roblox.com/docs/reference/engine/classes/Humanoid (WalkSpeed note)
- https://create.roblox.com/docs/projects/data-model (Camera, PlayerScripts)

---

## 2. What replicates automatically

### Containers

**M2.1** Server → client automatic replication by container:
- `Workspace` — replicates; "Clients render everything that appears in this container and nothing
  outside of it." (Subject to streaming, M2.9.)
- `ReplicatedStorage` — replicates; "contains objects that are available to both the server and
  connected clients."
- `ReplicatedFirst` — replicates **first and only once** per join: "The contents of this container
  are replicated to all clients (but not back to the server) first, before anything else." Intended
  for loading screens and bootstrap scripts.
- `Lighting`, `SoundService` — environment containers; their contents/settings are part of the
  replicated data model (clients read them; client-side edits follow M2.6).
- `StarterPack`, `StarterGui`, `StarterPlayerScripts`, `StarterCharacterScripts` — templates copied
  by the server into per-player runtime containers (`Player.Backpack`, `Player.PlayerGui`,
  `Player.PlayerScripts`, `Player.Character`) on join/spawn; contents "are non-persistent across
  sessions and reset every time a client rejoins," and `PlayerGui` is emptied and re-copied on respawn.

**M2.2** Never replicated to clients: `ServerScriptService` and `ServerStorage`. "Scripts in this
container are never replicated to clients, which allows you to have secure, server-side logic."
These containers exist so "the server [can] affect client behavior and state without exposing the
server's objects and logic to the client."

**M2.3** Instance lifecycle replication: instances the **server** creates/destroys/reparents inside
replicated containers replicate automatically to all clients (per M1.3 "data model" replication),
including subsequent property changes. Moving an object from `ServerStorage` into `Workspace` at
runtime is the documented pattern for deferring replication cost.

**M2.4** Attributes and tags replicate like properties: attribute "value changes … are **replicated**
so that clients can access them immediately," and CollectionService tags "are sets of strings applied
to instances that replicate from the server to the client."

**M2.5** Replication **ordering is not guaranteed across change types**: "The Roblox Engine doesn't
guarantee the order in which objects (and changes to objects) are replicated from the server to the
client." A property change fired before `FireAllClients` may arrive before *or* after
`OnClientEvent`. However, "Changes of the same type, such as two attribute changes, generally **do**
arrive in order." Client code must detect state (WaitForChild / changed signals), not assume arrival
order.

**M2.6** Client-side changes to replicated instances stay local and desync **by design**: for
`ReplicatedStorage`, "Any changes that occur on the client persist but won't be replicated to the
server. The server can overwrite changes on the client to maintain consistency." The same one-way
rule holds for the whole data model (M1.7). Local-only divergence is a feature (client VFX, local
hides) — and an exploit surface (M1.8), never an input channel.

**M2.7** Joining-player initial snapshot & boot order (documented sequence):
1. client loads `ReplicatedFirst` contents; its client scripts run first;
2. the client continues loading the rest of the data model ("Connected clients … receive a copy of
   the runtime data model"); with streaming enabled only a nearby subset of `Workspace` is sent;
3. `DataModel.Loaded` fires / `IsLoaded()` becomes true;
4. `StarterPlayerScripts`-derived local scripts run;
5. the player's character spawns.

### Streaming (per-player replication)

**M2.8** `Workspace.StreamingEnabled` scopes replication per player. Streaming "appl[ies] exclusively
to instances that are descendants of `Workspace`"; `ReplicatedStorage`/`ReplicatedFirst` are
ineligible (always fully replicated).

**M2.9** With streaming enabled, on join everything in `Workspace` replicates **except**: `BasePart`s;
Models set to Atomic/Persistent/PersistentPerPlayer; Nonatomic models when `ModelStreamingBehavior`
is `Improved`; and descendants of the above. Those stream in later "based on the game's streaming
properties, player position, client device performance, and other conditions."

**M2.10** Guaranteed-replication controls (`Model.ModelStreamingMode`):
- **Atomic** — "all of its initial descendants are streamed in together when a descendant BasePart
  is eligible"; streams out only as a whole when all parts are eligible.
- **Persistent** — "not subject to normal streaming in or out. They are sent as a complete atomic
  unit soon after the player joins and before the `Workspace.PersistentLoaded` event fires";
  "never streamed out."
- **PersistentPerPlayer** — Persistent for players added via `Model:AddPersistentPlayer()`, Atomic
  for everyone else.
- Models with no `BasePart` descendants replicate soon after join and are exempt from stream-out.

**M2.11** Stream-out semantics: streamed-out instances are "parented to `nil`" (not Destroyed) so
Luau state reconnects on re-stream; removal signals fire on the ancestor. "Local-only changes to
instance properties … can be lost if the instance streams out and later streams back in."
Client-created instances are exempt from stream-out unless parented under a server-created instance.

**M2.12** Streaming distances/foci: `StreamingMinRadius` (default 64; never streams out inside it)
and `StreamingTargetRadius` (default 1024; max stream-in distance). Streaming centers on the
character's `PrimaryPart` by default; `Player.ReplicationFocus` (server-set only) and
`AddReplicationFocus`/`RemoveReplicationFocus` move/extend the per-player interest area. Physics
"replicate[s] at different rates depending on how close objects are to the replication focus."
`StreamingIntegrityMode = PauseOutsideLoadedArea` pauses a player entering unstreamed regions
(`Player.GameplayPaused`).

**Mapping to Unity/Mirror**
- M2.1/M2.2 = the CoreAI mod API must expose "replicated" vs "server-only" object containers;
  server-only mod state must live outside any `NetworkBehaviour` sync path (plain server objects).
- M2.3 = `NetworkServer.Spawn`/`Destroy` for instance lifecycle; property changes = `[SyncVar]`s /
  SyncLists. Attributes (M2.4) map naturally to a `SyncDictionary<string, object>` per entity.
- M2.5 = Mirror gives per-connection in-order delivery on the reliable channel, which is *stronger*
  than Roblox's cross-type guarantee — but CoreAI must not promise mod authors ordering between
  spawn messages and RPCs referencing them (same race exists with `NetworkServer.Spawn` visibility).
- M2.8–M2.12 = Mirror Interest Management (distance-based). `Persistent` ≈ objects excluded from
  interest management (always visible); `ReplicationFocus` ≈ custom interest position override.

**Sources**
- https://create.roblox.com/docs/projects/data-model
- https://create.roblox.com/docs/projects/client-server
- https://create.roblox.com/docs/scripting/attributes (replication order)
- https://create.roblox.com/docs/studio/properties (tags & attribute replication)
- https://create.roblox.com/docs/workspace/streaming
- https://create.roblox.com/docs/reference/engine/classes/Player (ReplicationFocus)

---

## 3. Remotes: RemoteEvent / UnreliableRemoteEvent / RemoteFunction / Bindables

### Exact call semantics

**M3.1** `RemoteEvent` — asynchronous, one-way, **reliable and ordered** (relative to other reliable
remote traffic from the same sender), no yield:
- Client → Server: `RemoteEvent:FireServer(args…)` → `OnServerEvent:Connect(function(player, args…))`.
  "The first parameter of the event handler on the server is always the `Player` object of the
  client that calls it" — attached by the engine, unspoofable.
- Server → one client: `RemoteEvent:FireClient(player, args…)` → `OnClientEvent:Connect(function(args…))`
  (no player parameter on the receiving side).
- Server → all: `RemoteEvent:FireAllClients(args…)` → each client's `OnClientEvent(args…)`.
- The instance must live where both sides see it (typically `ReplicatedStorage`).

**M3.2** Missing-listener behavior (buffering): "If no connected listener exists to handle an event,
you might see a `Remote event invocation discarded` error … Unlike UnreliableRemoteEvents,
RemoteEvents **buffer a large number of events** before throwing this error." I.e. reliable remote
events fired before the receiver connects are queued up to an (undocumented) large cap, then
dropped with a logged error. The exact buffer size is not documented. **[UNCERTAIN: cap value]**

**M3.3** Rate limits (documented): "RemoteEvents and UnreliableRemoteEvents both have a limit of
approximately **500 requests per second, per client**" for client→server fires, and "this limit is
**shared among all remote events of the same type**." Exceeding it causes throttling/latency.
Server→client has no documented hard limit (bandwidth-bound in practice).

**M3.4** `UnreliableRemoteEvent` — same API surface (`FireServer` / `FireClient` / `FireAllClients`,
`OnServerEvent(player, …)` / `OnClientEvent(…)`), but "asynchronous, unordered and unreliable":
- delivery is not guaranteed ("not resent if they are lost … due to packet loss or to maintain
  optimal engine performance");
- ordering is not guaranteed ("they do not wait for previously fired events to arrive");
- **payload cap: "Events with payloads larger than 1000 bytes are dropped."** In Studio a log message
  reports how many bytes over the limit the event was. (Community measurements put the practical
  serialized-argument budget at ~900–908 bytes under the 1000-byte frame — **[UNCERTAIN: exact
  usable byte count; treat 900 bytes as the safe design budget]**.)
- documented use: "ephemeral events, including effects that are only relevant for a short time, or
  for replicating continuously changing data."
- No buffering for missing listeners (contrast M3.2).

**M3.5** `RemoteFunction` — synchronous, two-way; the invoking side **yields** until a response:
- Client → Server: `result = RemoteFunction:InvokeServer(args…)`; server implements
  `RemoteFunction.OnServerInvoke = function(player, args…) return … end`.
- `OnServerInvoke`/`OnClientInvoke` is a **callback property, not an event**: "if you define
  multiple callbacks to the same RemoteFunction, only the last definition executes."
- Server → Client (`InvokeClient`) exists but is **dangerous** (documented risks):
  1. "If the client throws an error, the server throws the error too."
  2. "If the client disconnects while it's being invoked, InvokeClient() throws an error."
  3. "If the client doesn't return a value, the server yields forever." (No built-in timeout.)
  Official guidance: use a `RemoteEvent` server→client instead unless a response is essential.
  CoreAI SHOULD NOT expose server→client request/response to mods at all, or must add a timeout.
- Streaming precaution: if an invoked RemoteFunction creates an instance on the server, "there is
  no guarantee that it exists on the client when the function returns."

**M3.6** `BindableEvent` / `BindableFunction` — **same-context only** (never cross the network):
they "bind behaviors between scripts **on the same side** of the client-server boundary."
`BindableEvent:Fire()` does not yield; `BindableFunction:Invoke()` yields until the callback is set
("If the callback was never set, the script that invokes it doesn't resume execution"). Multiple
connected functions run in unpredictable order. Bindable arguments undergo the same table
limitations as remotes (identity loss, metatable stripping).

### Argument serialization rules (applies to RemoteEvent, UnreliableRemoteEvent, RemoteFunction, Bindables)

**M3.7** Supported types: "Any type of Roblox object such as an `Enum`, `Instance`, or others can be
passed as a parameter … as well as Luau types such as numbers, strings, and booleans." Roblox
datatypes (`CFrame`, `Vector3`, `Color3`, `BrickColor`, `UDim2`, etc.) serialize by value; `nil`,
`number` (double), `bool`, `string`/binary strings pass through.

**M3.8** The documented limitation list (each is a testable rule):
| # | Rule | Documented behavior |
|---|------|---------------------|
| a | Instance references | Pass **by reference**, and only resolve if the instance is replicated to the receiver: "If a RemoteEvent or RemoteFunction passes a value that's only visible to the sender, Roblox doesn't replicate it … and passes `nil` instead of the value." (e.g. `ServerStorage` descendants → nil on client; client-created parts → nil on server.) |
| b | Functions | "will **not** be replicated … the resulting argument on the receiving side will be `nil`." |
| c | Non-string table keys | "If any indices of a passed table are non-string types such as an `Instance`, userdata, or function, Roblox automatically converts those indices to strings." |
| d | Mixed keys | "Do not pass a mixed table of numeric and string keys. Instead, pass a table that consists **entirely** of key-value pairs (dictionary) or **entirely** of numeric indices." (Mixed tables lose data — undefined subset survives.) |
| e | nil holes in arrays | "Whether passing a dictionary table or a numerically indexed table, avoid `nil` values for any index." (Arrays with holes truncate/misbehave.) |
| f | Table identity | "Tables passed as arguments … are copied, meaning they will not be exactly equivalent … Nor will tables returned to the invoker." Deep copy each hop; no shared references. |
| g | Metatables | "If a table has a metatable, all of the metatable information is lost in the transfer." Only plain data survives. |
| h | Cyclic tables | Not covered by the current official page; in practice cyclic tables error on serialization. **[UNCERTAIN — official docs silent; CoreAI: reject cycles at the API boundary with a clear error.]** |

**M3.9** Values received from a remote are **untrusted input** even after deserialization: type,
range, NaN/Inf, and structural checks are mandatory on the server (section 7). Serialization itself
performs no validation beyond the mechanics above.

**Mapping to Unity/Mirror**
- `FireServer`→`[Command]`; `FireClient`→`[TargetRpc]`; `FireAllClients`→`[ClientRpc]`;
  `InvokeServer` has no direct Mirror primitive — implement as request-id + `[Command]` /
  `[TargetRpc]` pair with an awaitable (and a timeout, improving on M3.5).
- `UnreliableRemoteEvent` → Mirror unreliable channel (`Channels.Unreliable`); enforce a ≤900-byte
  serialized-payload budget and drop+log oversized sends to match M3.4.
- M3.2 buffering: Mirror has no server-side buffering for RPCs sent before a handler exists —
  CoreAI's mod-event layer must either queue mod-bound events until the mod's handler registers, or
  document drop semantics; matching Roblox means "queue a large number, then drop with logged error".
- M3.8a maps to Mirror's `NetworkIdentity` argument resolution: an object not spawned for that
  connection deserializes as null. Identical semantics — document it for mod authors.
- M3.8c–h: CoreAI's table/JSON payload codec must reproduce: stringify non-string keys, reject or
  document mixed tables, forbid nil holes, deep-copy (no reference sharing), strip any behavior
  (only data serializes), reject cycles.

**Sources**
- https://create.roblox.com/docs/scripting/events/remote (semantics + full argument-limitation list)
- https://create.roblox.com/docs/reference/engine/classes/RemoteEvent (buffering, 500 req/s throttle)
- https://create.roblox.com/docs/reference/engine/classes/UnreliableRemoteEvent (unordered/unreliable, 1000-byte drop rule)
- https://create.roblox.com/docs/reference/engine/classes/RemoteFunction (InvokeClient risks, streaming precaution)
- https://create.roblox.com/docs/scripting/events/bindable
- (payload measurement, non-official) https://devforum.roblox.com/t/incorrect-size-of-data-being-sent-limit-specified-when-using-unreliableremoteevent/3048788

---

## 4. Network ownership (physics)

**M4.1** Default: "the server retains ownership of any `BasePart`." Ownership of an unanchored part
is then assigned automatically: "Based on a client's hardware capacity and the player's
`Player.Character` proximity to an unanchored `BasePart`, the engine automatically assigns ownership
of that part to the client. Thus, parts close to a player's character are more likely to become
player-owned."

**M4.2** Anchored parts: "The server **always** owns anchored BaseParts and you cannot manually
change their ownership." (Attempting to is an error; check `CanSetNetworkOwnership()` first —
it "returns true if you can modify/read the network ownership, or returns false and the reason you
can't, as a string.")

**M4.3** API (server-side `Script` only):
- `BasePart:SetNetworkOwner(playerOrNil)` — "Sets the given player as network owner for this and
  all connected parts. When playerInstance is nil, the server will be the owner instead of a player."
- `BasePart:SetNetworkOwnershipAuto()` — reverts to engine-automatic assignment.
- `BasePart:GetNetworkOwner()` → "the current player who is the network owner of this part, or
  `nil` in case of the server"; `GetNetworkOwnershipAuto()` → whether assignment is automatic.
- Docs caution that pinning gameplay-critical parts to the server (`SetNetworkOwner(nil)`) trades
  responsiveness ("may result in jittery physics interactions for clients") — use conservatively.

**M4.4** Assembly/mechanism propagation: setting ownership on one assembly of a mechanism with no
anchored parts "sets the same ownership for **every assembly** in the mechanism." Anchoring an
assembly sends its ownership to the server without changing sibling assemblies; unanchoring
restores prior/automatic handling.

**M4.5** The owner **simulates** the physics; everyone else receives its results. This is the exploit
surface: "Roblox cannot verify physics calculations when a client has ownership over a `BasePart`.
Clients can exploit this and send bad data to the server, such as teleporting the BasePart, making
it go through walls or fly around." Owners can also replicate `Inf`/`NaN` CFrame components and
extreme velocities that fling other players' objects.

**M4.6** `Touched` events fire on the owner's authority: "`BasePart.Touched` events are tied to
network ownership, meaning that a client can fire Touched events on a BasePart it owns and send it
to the server, even if the server doesn't see it touch anything." Server logic MUST validate every
client-originated touch (distance, plausibility) before granting effects.

**M4.7** The player's character is network-owned by its own client (this is what makes M1.5 work and
what "take network ownership of their character" in the threat model refers to). Gameplay-critical
objects that clients must not manipulate should be anchored or explicitly server-owned.

**Mapping to Unity/Mirror**
- Network ownership ≈ Mirror "authority" over an object (`NetworkIdentity` client authority /
  client-authoritative `NetworkTransform`+`Rigidbody`). `SetNetworkOwner(player)` ≈
  `AssignClientAuthority(conn)`; `SetNetworkOwner(nil)` ≈ `RemoveClientAuthority()` (server-simulated).
- There is no Mirror equivalent of *automatic proximity-based* ownership; if CoreAI implements it,
  it must replicate M4.2 (server-only for static/kinematic "anchored" bodies) and M4.4 (whole
  joint-connected assembly shares one owner).
- M4.5/M4.6: any client-authoritative transform or client-reported collision in CoreAI mods is
  untrusted input — sanitize NaN/Inf, clamp teleports/velocity, and re-check collisions server-side.

**Sources**
- https://create.roblox.com/docs/physics/network-ownership
- https://create.roblox.com/docs/scripting/security/network-ownership
- https://create.roblox.com/docs/reference/engine/classes/BasePart (SetNetworkOwner, GetNetworkOwner, CanSetNetworkOwnership)

---

## 5. Player & character lifecycle

**M5.1** The `Players` service "contains presently connected `Player` objects." The service and its
`Player` objects are replicated, so both server and clients can enumerate players. Player objects
are engine-managed: "You don't add Player objects to the Players container service explicitly."

**M5.2** `Players.PlayerAdded(player)` "fires when a player enters the experience";
`Players.PlayerRemoving(player, reason)` "fires right before a Player leaves the experience, before
`ChildRemoved` fires on Players" — the canonical hook for saving/cleanup (DataStore save, releasing
per-player server state such as rate-limit buckets, M7.6). Documented caveat: PlayerAdded "does not
work as expected in a solo playtest mode because the player is created before scripts run" — the
same late-subscriber race applies to any joiner-driven event system; robust code enumerates
`Players:GetPlayers()` *and* subscribes. On clients, players who joined earlier are already present
in the service when local scripts start; `Players.LocalPlayer` is "only defined for LocalScripts
(and ModuleScripts required by them)" — it is nil/absent server-side.

**M5.3** Character spawn: with `Players.CharacterAutoLoads = true` (default) characters spawn
automatically and respawn `Players.RespawnTime` (default **5.0** seconds) after death. With it
false, nothing spawns until the server calls `Player:LoadCharacterAsync()` (`LoadCharacter()` is
deprecated), which "creates a new character for the player, removing the old one," and "also clears
the player's `Backpack` and `PlayerGui`."

**M5.4** `Player.CharacterAdded(character)` fires on spawn/respawn, "**before** the character is
parented to the Workspace." The Humanoid and default body parts exist server-side at that moment,
but appearance items "might take a few seconds to be added," and "the parts will also take time to
replicate to clients" — use `CharacterAppearanceLoaded` for the fully-dressed guarantee.
`Player.CharacterRemoving(character)` fires "right before a player's Character is removed," e.g. on
death/respawn; both can fire many times per session.

**M5.5** Character replication: the character `Model` lives in `Workspace`, so it replicates to all
clients like any instance tree (subject to streaming), while its *movement* replicates continuously
from the owning client (M1.6, M4.7). Per-spawn contents (`StarterCharacterScripts` copies,
`PlayerGui` from `StarterGui`) "do not persist when the player respawns."

**M5.6** Teams: `Player.Team` references a `Team` object under the `Teams` service; setting it fires
`Team.PlayerAdded` on that team. Team membership is server-visible state usable in validation
(friendly-fire checks etc.). Legacy `Player.TeamColor` links by color; docs recommend setting
`Player.Team` directly.

**M5.7** On disconnect the engine removes the `Player` object (and its character) and replicates the
removal; all per-player runtime containers (`Backpack`, `PlayerGui`, `PlayerScripts`) are transient
and rebuilt each session (M2.1). Server mods must treat `PlayerRemoving` as the last safe moment to
read player state.

**Mapping to Unity/Mirror**
- `PlayerAdded`/`PlayerRemoving` ≈ `OnServerConnect`+player-object spawn / `OnServerDisconnect`.
  CoreAI's mod API should expose both a join event *and* a current-players snapshot to kill the
  late-subscriber race (M5.2).
- `CharacterAdded`/`LoadCharacterAsync` ≈ (re)spawning the player avatar prefab via
  `NetworkServer.ReplacePlayerForConnection`/`Spawn`; M5.4's "exists before fully replicated"
  matches Mirror's spawn-message timing — never promise mods that clients already see the avatar
  when the server-side spawn callback runs.
- `RespawnTime`/auto-respawn is a small server-side timer CoreAI must own (server decides respawn,
  never the client).

**Sources**
- https://create.roblox.com/docs/reference/engine/classes/Players
- https://create.roblox.com/docs/reference/engine/classes/Player
- https://create.roblox.com/docs/projects/data-model (client containers, Player objects)
- https://create.roblox.com/docs/reference/engine/classes/Team

---

## 6. Server lifecycle & jobs

**M6.1** `game:BindToClose(callback)` "binds a function to be called before the server shuts down."
Multiple callbacks may be bound; they "are called in parallel and run at the same time." The server
grants **30 seconds** for all bound functions to finish before hard shutdown. The callback may
receive a `CloseReason` enum. Primary documented use: flushing `DataStoreService` writes "to prevent
data loss if the server shuts down unexpectedly" (guard with `RunService:IsStudio()` where needed).

**M6.2** Server identity:
- `game.JobId` — "a unique identifier for the running game server instance" (UUID); empty string in Studio.
- `game.PlaceId` — ID of the place (scene/level) this server runs; `game.GameId` — ID of the
  experience the place belongs to.
- `game.PrivateServerId` — non-empty for private **and** reserved servers; stable across instances
  of the same private server. `game.PrivateServerOwnerId` — owner `UserId` for VIP/private servers,
  `0` for standard *and* reserved servers. Combining both distinguishes standard / reserved / VIP.

**M6.3** Cross-server messaging (one paragraph): `MessagingService` provides pub/sub **topics**
between servers of one experience — `SubscribeAsync(topic, callback)` to receive and
`PublishAsync(topic, message)` to send (both should be pcall-wrapped; subscriptions return a
connection to disconnect on player exit). Documented uses: global announcements, live server
browsers. It is delivery-best-effort with service quotas — a coordination channel, not a state
replication channel. CoreAI analogue: optional server-to-server bus; NOT part of the in-match
replication contract.

**M6.4** Teleporting (one paragraph, **far-future for CoreAI**): `TeleportService:TeleportAsync(placeId,
players, options?)` moves players between places/servers/experiences; "to reduce client-side
exploits, you can only call TeleportAsync() from server scripts" (a client-side `Teleport()` exists
but is discouraged), and it does not work in Studio playtests. Maps loosely to server-driven scene/
shard handoff; out of scope for CoreAI v1 — record only that any future equivalent must be
server-initiated.

**Mapping to Unity/Mirror**
- `BindToClose` ≈ a CoreAI server-shutdown hook awaited (with a hard 30 s budget) before
  `NetworkServer.Shutdown` / process exit — mods get a bounded async flush window, mirroring M6.1.
- `JobId`/`PlaceId` ≈ server instance GUID + scene/config id exposed read-only to mods.

**Sources**
- https://create.roblox.com/docs/reference/engine/classes/DataModel (BindToClose, JobId, PlaceId, GameId, PrivateServerId, PrivateServerOwnerId)
- https://create.roblox.com/docs/cloud-services/cross-server-messaging
- https://create.roblox.com/docs/projects/teleport

---

## 7. Security & anti-exploit norms

CoreAI mods are AI-written and run **server-side with real authority**; this section is the sanity
model the mod runtime must enforce *around* mod code, and the checklist mod codegen must follow.

**M7.1** Foundational rules (verbatim doctrine): "Never trust the client." "Assume every piece of
data sent from the client has been manipulated, fabricated, or sent with malicious intent." "All
critical logic must be validated server-side or run exclusively on the server." "Keep logic and data
in ServerScriptService from day one. Never place them in replicated containers." (For CoreAI: mod
server logic and balance data must never be shipped inside client-visible payloads — clients can
decompile anything replicated, M1.8.)

**M7.2** Canonical remote-validation checklist — every client-triggered handler must apply, in order:
1. **Type & structure check** every argument (`typeof`), including rejecting tables where an
   Instance is expected: "exploiters may … send tables in place of an Instance … Complex payloads
   can mimic what would be an otherwise ordinary object reference." For instance args verify class
   AND expected location: `typeof(item) == "Instance" and item:IsDescendantOf(expectedFolder)`.
2. **NaN/Inf check** on every number: NaN "is of type 'number' but fails all standard comparisons,
   allowing it to subtly bypass logical checks" (`n ~= n` detects NaN; `math.abs(n) == math.huge`
   detects ±Inf). Reject both.
3. **Range/size check**: numeric bounds (quantity > 0, ≤ max), string length caps, table key-count
   caps ("ensure limitless arbitrary keys cannot be added to tables by the client"), valid UTF-8
   for persisted strings.
4. **Context/permission check**: does this player have the right, resources, distance, and state
   ("is the player close enough to a shop … do they have a key … is their character alive?").
5. **Rate limit** (M7.3) before executing expensive or spammable logic.
6. Only then: **server mutates state and replicates results** (M1.9). Reject silently or with a
   typed error; never execute-then-verify.

**M7.3** Rate limiting: any client-triggerable server logic "could be spammed by exploiters or even
legitimate users"; "never rely solely on a client-sided rate limit." The documented reference
implementation is a per-`UserId` **token bucket** (burst capacity + steady refill), with bucket
cleanup on `PlayerRemoving` to avoid leaks. Threat question to answer per feature: "What happens if
this feature is used 1,000+ times per second?" Note the engine itself throttles remotes at ~500
req/s/client (M3.3) — application limits must be far tighter.

**M7.4** Common exploit patterns to defend against (documented):
- **Remote spam** — flooding remotes → token buckets + cheap early rejects (M7.3);
- **Argument spoofing** — arbitrary args incl. NaN/Inf, huge strings, fake "instance" tables,
  negative prices → M7.2;
- **Network-ownership abuse** — teleporting/flinging owned parts, moving interactive objects to
  themselves ("an exploiter can take network ownership of the parent parts and move them directly
  to their character, bypassing distance checks"), fake `Touched` firings → anchor or server-own
  critical parts, validate positions/collisions server-side (M4.5–M4.7);
- **Client-triggered interactables** — ProximityPrompt/ClickDetector events fire "at any distance,
  at any time, often ignoring properties like Enabled or MaxActivationDistance"; the server must
  re-check enabled-state, distance, player state, and hold-durations itself;
- **Relay abuse** — client→server→all-clients effect remotes: "The server must be a gatekeeper,
  not just a relay" (validate type, NaN, cooldown, permission, range before re-broadcast);
- **Data races/dupes** — trade/save flows must validate all data before committing, use
  transaction-like patterns, and handle mid-operation disconnects.

**M7.5** Combat/hit validation pattern (documented): the client reports shot origin + claimed hit
part/position; the server verifies (a) origin is near the server-side character (with latency
tolerance), (b) claimed hit position is near that part's server-side position, (c) no static
obstruction between origin and hit (static-only to avoid latency false-positives), plus fire-rate,
ammo, team, target-alive, and actor-state checks — all tracked server-side.

**M7.6** Movement validation & consequencing: clients control their own character (M1.5), so
competitive games need server-side movement checks — distance-over-time with latency averaging,
plane projection (XZ) for teleport detection, leaky-bucket accumulators for bursts, explicit
exemptions for legit teleports. Response philosophy (documented): "The server decides"; "Prevent
harm first" (rubber-band rather than insta-kick); "Be proportional and reversible" (assume false
positives); "Design > detection." (Roblox's fully server-authoritative movement mode is in beta —
**[UNCERTAIN: its final semantics]** — CoreAI on Mirror can already choose server-auth movement per
game, which supersedes most of this rule when enabled.)

**M7.7** CoreAI-specific norms derived from the above:
- The mod API surface mirrors Roblox's shape: mods declare server handlers that *always* receive
  the engine-verified acting player first; there is no API for a client to name another player as
  the actor.
- The runtime auto-wraps every mod remote handler with M7.2 steps 1–3 and 5 (type schema, NaN/Inf,
  size caps, token bucket) so AI-generated code gets a safe floor even if it forgets checks;
  step 4 (game-context checks) remains the mod's stated responsibility in authoring docs.
- Server-only mod state must be unreachable from any client-serialized path (M7.1, M2.2).
- Every relay-style broadcast API validates before re-broadcast (gatekeeper rule, M7.4).

**Sources**
- https://create.roblox.com/docs/scripting/security/security-tactics
- https://create.roblox.com/docs/scripting/security/client-server-boundary
- https://create.roblox.com/docs/scripting/security/network-ownership
- https://create.roblox.com/docs/scripting/security/server-side-detection

---

## Appendix A — Serialization quick table (remotes)

| Value | Result on receiver |
|---|---|
| number / bool / string | As sent (numbers are doubles; NaN/Inf transmit — validate!) |
| Roblox datatypes (Vector3, CFrame, Color3, Enum, …) | By value |
| Instance replicated to receiver | Same instance (by reference) |
| Instance NOT replicated to receiver (ServerStorage desc., client-created part) | `nil` |
| function | `nil` |
| table (pure array, no nil holes) | Deep copy, new identity |
| table (pure dictionary, string keys) | Deep copy, new identity |
| table with Instance/userdata/function keys | Keys coerced to strings |
| table with mixed numeric+string keys | Undefined subset — forbidden |
| array with nil holes | Data loss/truncation — forbidden |
| table with metatable | Metatable stripped, plain data only |
| cyclic table | Not documented; errors in practice — forbidden [UNCERTAIN] |

## Appendix B — Limits quick table

| Limit | Value | Source status |
|---|---|---|
| Client→server remote fires (shared per remote type, per client) | ~500 req/s, then throttled | Documented |
| UnreliableRemoteEvent payload | >1000 bytes → dropped (log in Studio) | Documented |
| UnreliableRemoteEvent practical argument budget | ~900 bytes | Community-measured [UNCERTAIN] |
| RemoteEvent no-listener buffer | "a large number of events", then discarded with error | Documented, size unspecified [UNCERTAIN] |
| BindToClose shutdown budget | 30 seconds, callbacks run in parallel | Documented |
| Players.RespawnTime default | 5.0 s | Documented |
| StreamingMinRadius / StreamingTargetRadius defaults | 64 / 1024 studs | Documented |
| Typical player latency | 100–300 ms | Documented |
