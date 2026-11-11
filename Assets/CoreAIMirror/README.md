# CoreAI Mirror transport

Puts a CoreAI Rbx world on a Mirror network transport, so `RemoteEvent` and `RemoteFunction` go
through Mirror's real message handlers and batcher instead of looping back in one process. The
package's tests drive that path through Mirror over an in-memory transport, Mirror's host mode
included. A two-process harness for localhost kcp ships with the package (see Two-process harness);
no two-process result is recorded yet.

`com.neoxider.coreaimirror` depends on `com.neoxider.coreai` and `com.neoxider.coreaimods` of the same
release; install steps are in [INSTALL.md](../../INSTALL.md). Mirror itself is not part of CoreAI or of
this package: install it yourself, from the Unity Asset Store or from its
[GitHub releases](https://github.com/MirrorNetworking/Mirror/releases). Importing Mirror adds the
`MIRROR` scripting define. Both assemblies here, `CoreAI.Net.Mirror` and its EditMode tests
`CoreAI.Net.Mirror.Tests`, list `MIRROR` in their `defineConstraints` (the tests also
`UNITY_INCLUDE_TESTS`), so with no Mirror in the project Unity leaves them out of the build and the
world stays on the in-process loopback bridge.

## What it is, and what it is not

It is the transport and the admission half of multiplayer: connections, actor admission, request
timeouts, packet and byte counters, a server clock offset, and the Roblox-facing remote surface a mod
author actually calls. **World-state replication does not cross the wire.** The replication types
exist and are tested, but nothing joins them to this transport, so remotes replicate and world
state does not. World-state replication is planned as MVP6 in
[ROBLOX_API_ROADMAP.md](../../Docs/CoreAIMods/ROBLOX_API_ROADMAP.md).

Only Mirror messages cross the wire; no `NetworkBehaviour` or `SyncVar` is exposed to mods. The
messages are `CoreAiAdmissionRequestMessage` (the host-defined `Credential` bytes and the client's
`ProtocolVersion`, nothing else), `CoreAiAdmissionResponseMessage` (`Admitted`, `Reason`, `ActorId`,
the server's `ProtocolVersion` and the `Refusal` kind), `CoreAiRemoteEventMessage`,
`CoreAiRemoteRequestMessage` and `CoreAiRemoteResponseMessage` (the remotes), and
`CoreAiClientReadyMessage`, `CoreAiServerClockMessage`, `CoreAiDisconnectNoticeMessage` and
`CoreAiRosterMessage` (see Sessions). No envelope carries a sender actor id: the server fills the
sender from its own connection map, so a client cannot speak as another actor by editing a packet. A
refusal by the host's admission provider tells the client only `not admitted` and an empty `ActorId`;
the detailed reason goes only to the host, through the `log` callback given to
`CoreAiMirrorAuthenticator.Configure`. A protocol refusal names both protocols — a protocol number is
no secret.

## Setup

1. Add a `CoreAiMirrorNetworkBridgeProvider` to a GameObject in the scene (**Add Component > CoreAI >
   CoreAI Mirror Network Bridge Provider**), and a `CoreAiMirrorAuthenticator` (**CoreAI > CoreAI Mirror
   Authenticator**) as the authenticator of your `NetworkManager`.
2. Set **Role** to `Server` for a dedicated server or a host, `Client` for a joining client. This is
   an explicit choice rather than something read from Mirror, because the bridge is built while the
   mod scope is still in `Awake`, before Mirror has started and before there is any live state to
   read. The side is fixed for the life of the bridge; changing `Role` afterwards throws, and if
   Mirror later starts as the other side the provider says so once instead of running as the wrong
   side.
3. Assign the provider's **Authenticator** to the same `CoreAiMirrorAuthenticator` your
   `NetworkManager` uses. On a server this is what turns a connection into an admitted actor; on a
   client it tells the bridge which actor the server admitted, and without it every server-to-client
   remote is dropped as unadmitted. Its **Admission Timeout Seconds** (default 10) is how long a new
   connection may stay silent before it is dropped: Mirror has no authentication timeout of its own.
   On a server, call `authenticator.Configure(admissionProvider, worldId, log)` with your
   `IActorAdmissionProvider` before `StartServer` (`log` is optional and receives the refusal
   reasons); with no provider every connection is refused, there is no anonymous path. On a client,
   `ConfigureClientCredential` supplies the credential bytes the admission request sends.
   The provider's other settings, all fixed once the bridge exists (each settable from code until
   then, like `Role` and `Authenticator`): **Host Mode** — `ServeAsHostPlayer` (the default) makes a
   host's own local client its Player, `Refuse` keeps the behaviour of 7.56.0 and older (see Sessions); **Publish
   Roster** — whether ready clients are sent who is in the room; **Inbound Limits** — what each admitted
   connection may send the server (see Sessions).
4. Assign the provider to the **Network Bridge Provider** field (under the *Network transport*
   header) on your `CoreAiModsLifetimeScope`. Leaving that field empty is the default and keeps the
   world on the in-process null bridge - no remote peers, remotes loop back locally.
5. From your own composition, once the world exists, hand it to the provider. This stays the game's
   responsibility because the world does not exist when the bridge is built:

   ```csharp
   // `scope` is your CoreAiModsLifetimeScope; resolve after it has built. The LuaCsModStack the
   // container hands out is a facade over whichever world is live NOW, so read it inside the
   // function rather than once: a world loaded at runtime replaces the bindings behind it.
   LuaCsModStack stack = scope.Container.Resolve<LuaCsModStack>();

   provider.AttachWorld(() => stack.GameplayBindings.RbxApi);
   ```

   That overload connects and disconnects actors on the world the function returns at that moment,
   and it makes the provider's session host that world's `Players.IdentitySource` — checked every
   frame and before every admission, so a world published later is wired on the next frame. An
   identity source your game already set is left in place (said once in the log).
   `scope.Container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi` is the same live
   object, if you would rather name the world controller.

   A custom composition can still pass the two delegates itself; then the identity source is yours to
   set, on every world you publish. `ConnectActor` returns the `RbxPlayer` it created, so the lambda
   adapts it to the `bool` the provider expects:

   ```csharp
   provider.AttachWorld(
       connectActor: ctx => stack.GameplayBindings.RbxApi.ConnectActor(ctx) != null,
       disconnectActor: ctx => stack.GameplayBindings.RbxApi.DisconnectActor(ctx));

   // The session host IS the identity source: it knows which actor each connection was admitted as.
   stack.GameplayBindings.RbxApi.Players.IdentitySource = provider.SessionHost;
   ```

   **The identity source is not optional.** On a server (`Host`/`DedicatedServer` topology) a world
   with no `Players.IdentitySource` refuses an actor the transport admitted with `NOT_AUTHORITY`
   rather than handing it a session-counter `UserId` another account could receive later; nothing is
   created for it, and the provider drops the connection.

Step 5 has no ordering trap: the provider admits whatever Mirror already accepted, from whichever of
the bridge and the world arrives last, so a client admitted before your world attaches still gets its
player. It is also once per provider: a second `AttachWorld` throws, because the sessions already
admitted belong to the world that created their players - the same rule `Role` follows. The function
(or the lambdas) you pass is therefore the only route from the provider to a world: to follow a world
loaded at runtime, make it resolve the live world as above instead of attaching again.

Two outcomes are decided on the wire rather than left hanging. A connection the authenticator admitted
but the world could not turn into a player - `connectActor` threw or returned false - is dropped by the
provider, with the world's exception in the log; nothing could ever admit that connection again, so it
must not stay connected as an authenticated nobody. The drop happens on the provider's next `Update`
(or when the provider is disabled), after Mirror has finished accepting the connection; until then
every packet from that connection is dropped as unadmitted. Keep the provider enabled: a disabled
provider runs no `Update`, so a drop owed after it was disabled waits until it is enabled again. And
`Player:Kick()` ends the kicked client's connection through the bridge: the client is sent the kick's
message first, the session host releases the session at once, the socket closes on a later frame
(see Sessions), and that client's next remote is dropped as unadmitted instead of re-creating the
player. That is the Mirror bridge. On the in-process loopback (`NullNetworkBridge`, what an empty provider field gives you) a
kick is the Player teardown only: there is no connection to end and the actor stays registered, so
the actor's next use - a remote, a `Players.LocalPlayer` read, a new mod context - is a fresh join,
and the Player is re-created with `PlayerAdded` firing again.

## Sessions

- **Both sides speak one protocol, or the admission is refused with a reason.** The admission request
  carries the client's CoreAI Mirror protocol (`CoreAiMirrorProtocol.Version`, currently 1) and the
  response the server's. A client of another protocol — or of none, which is CoreAI 7.56.0 and older —
  is refused before the host's `IActorAdmissionProvider` is asked, counted in
  `CoreAiMirrorAuthenticator.ProtocolMismatchRefusals` (and `RejectedCount`), with a reason that names
  both protocols. A client refuses, the same way, an admission from a server of another protocol or of
  none; `CoreAiAdmissionRefusal.RefusedByServer` tells the two apart. The admission messages are read
  by hand-written tolerant serializers (`CoreAiMirrorAdmissionSerialization`), so an older peer's
  shorter message and a newer peer's longer one are read and refused rather than dropped by Mirror
  without a reason. Bump `CoreAiMirrorProtocol.Version` with every change to a message after
  admission.
- **A refused client learns why.** The refusal is sent and the connection is dropped on a later frame,
  once Mirror has flushed it (`RefusalDropsPerformed`): kcp2k drops a connection before the call
  returns, and Mirror discards what was not flushed, so a drop made with the refusal sent nothing.
  Meanwhile the connection reaches nothing — it is unauthenticated and its one attempt is spent. A
  current client closes its own connection on the refusal. On the client,
  `CoreAiMirrorAuthenticator.ClientRefusal` keeps the `Kind` (`NotAdmitted`, `ProtocolMismatch`) and
  the `Reason` after the connection closes (cleared when the next attempt starts) and
  `ClientRefused` is raised before it closes. A composition that stops updating the authenticator
  calls `PerformOwedRefusalDropsNow()`; a disabled authenticator does that itself.
- **One admission attempt per connection.** A connection's first admission request is decided; a
  repeat on the same connection is ignored and counted (`CoreAiMirrorAuthenticator.IgnoredAdmissionRequests`),
  so a connection can neither mint a second Player nor retry credentials. A connection that sends no
  request within **Admission Timeout Seconds** is dropped and counted (`AdmissionTimeouts`).
- **Newest wins on reconnect.** When an actor is admitted on a new connection while its old one is
  still open (a reconnect before kcp2k's own timeout), the older connection is closed and counted
  (`MirrorNetworkBridge.SupersededConnections`, `CoreAiMirrorSessionHost.SupersededSessions`). The
  Player carries over — no `PlayerRemoving`/`PlayerAdded` churn — and `PlayerRemoving` fires only when
  the actor's last session ends. Every teardown is keyed by connection, never by actor alone, so the
  old connection's late disconnect cannot remove the new session.
- **Disconnect callbacks cannot interrupt mandatory cleanup.** The bridge removes the departing
  connection's open requests before notifying every peer listener, and completes those requests
  after the peer/world teardown. Binding-release listeners wait for those peer listeners too, so
  reentrant unregister or disposal cannot make the session host forget a Player before removing it.
  Reentrant disposal also releases the departing admission, while preserving a new admission when
  the transport reuses the connection id during a callback.
  Each completion and lifecycle subscriber is attempted once; a callback that admits the same
  actor on a new connection leaves that replacement's requests and session intact. New requests to
  a connection already tearing down fail at once. Completion and binding-listener failures go to
  the bridge's `log` callback; if that callback throws, Unity receives both failures as an exception.
  Peer-listener failures surface after cleanup: a single failure retains its original exception,
  and several failures become an `AggregateException`. The scene provider reports these failures
  without throwing them into the transport tick. Client admission loss, disposal and request
  expiry also attempt every outstanding completion subscriber despite a failure.
- **Actors local to the server are served in process.** A server never sends a client-bound or
  server-bound envelope to itself over the wire: a remote from or to an actor registered in this
  process is delivered in process (`LocalDeliveries`), so `InvokeServer` from a host-local actor is
  answered at once and `FireClient`/`FireAllClients`/`InvokeClient` reach local actors.
- **Mirror host mode: the host's own client is the host's Player.** In a host — server and local
  client in one process, `NetworkManager.StartHost` — the host's local client is admitted through the
  same authenticator over Mirror's local connection (so its credential goes through the host's own
  `IActorAdmissionProvider` and its Player gets the admitted UserId), and the session host binds it as
  a host-local connection (`MirrorNetworkBridge.BindHostLocalConnection`,
  `CoreAiMirrorSessionHost.HostPlayerSessions`). Everything addressed to it is served in process, as
  for any host-local actor: `FireClient` and `InvokeClient` reach the host's own LocalScripts,
  `FireServer` and `InvokeServer` reach the server's scripts with the host's Player as the sender, and a
  broadcast reaches the host in process and every remote client on the wire. It waits for no readiness,
  is sent no clock anchor, notice or roster (the host's client half has no CoreAI handler for them), and
  is never dropped: a kick of the host's own player ends its Player and its session and leaves the local
  client on Mirror. Mirror removes the local connection without a disconnect event when the host's
  client stops, so the bridge's next `Pump()` ends that session (`HostLocalConnectionsReleased`); a new
  local client on the same id ends the old session first and keeps its own admission. The host's LocalScripts
  must run as the admitted actor (the contract below holds for the host too). `HostMode = Refuse` (the
  provider's **Host Mode**, or `CoreAiMirrorSessionOptions.HostMode`) keeps the older behaviour: the
  local client is refused as a player, loudly (`HostModeConnectionsRefused`), and left on Mirror.
- **Ready clients are told who is in the room.** The session host keeps the roster
  (`CoreAiMirrorSessionHost.Roster`: one member per admitted actor, in join order, the host's own player
  flagged `IsHost`) and sends a client the whole of it when the client acknowledges readiness — after
  the first clock anchor and before anything held for it, split into `Snapshot` and `SnapshotContinued`
  messages when it does not fit one — then every join and leave to every ready client
  (`CoreAiRosterMessage`, reliable, with a revision). A member carries a server-assigned `MemberId`, the
  admitted `UserId`, `Name` and `DisplayName` (cut to 64 UTF-8 bytes) and the host flag, never an actor
  id; a snapshot names the recipient's own member. A player who reconnects keeps its member: no leave
  and join. On a client, `MirrorNetworkBridge.Roster` holds the copy (`Members`, `TryGetSelf`,
  `RevisionGaps`), `RosterChanged` is raised after each message, and the copy is cleared when the
  connection changes; a roster this client cannot apply is dropped and counted as malformed.
  `PublishRoster = false` sends nothing and still keeps the host-side roster.
- **What a client sends the server is metered per connection.** Every inbound `RemoteEvent` and
  `RemoteFunction` call is counted against its connection's token buckets — remotes per second and
  payload bytes per second, each absorbing a burst (`CoreAiMirrorInboundLimits`: 1,500 remotes/s,
  1 MiB/s, 2 s burst by default, which covers what an honest client's own limiter lets through) —
  before the world decodes or dispatches it. Past them a remote is dropped and counted
  (`InboundMessagesDropped`, `InboundBytesDropped`), a `RemoteFunction` call is answered at once with a
  failure (`InboundLimitedReason`), and the drops are said once per connection every 10 s
  (`InboundDropLogIntervalSeconds`). A payload larger than its channel carries — 1,000 bytes unreliable,
  64 KiB reliable or a call — is dropped and counted whatever the rates (`InboundOversizeDropped`).
  `TryGetInboundTraffic(connectionId, ...)` and `GetInboundTraffic()` give each bound connection's
  accepted and dropped counts; a connection's meter starts full at its binding and goes with its
  release. Limits are the provider's **Inbound Limits** or the bridge's `inboundLimits` argument,
  copied at construction; `CoreAiMirrorInboundLimits.Unlimited` meters nothing.
- **Payload limits are per channel.** A reliable `RemoteEvent` and both `RemoteFunction` directions
  carry up to 64 KiB (`CodecPayloadCeilingBytes`, or less when the transport's reliable channel cannot
  carry that in one message); an `UnreliableRemoteEvent` carries up to 1,000 bytes, Roblox's own
  ceiling (`UnreliablePayloadCeilingBytes`), or less on a transport with a smaller datagram. A
  `RemoteFunction` answer too large for the channel is sent as a failure that names the size
  (`OversizeResponsesFailed`) instead of vanishing.
- **A peer's bad traffic is dropped and counted, not thrown inside Mirror's handler.** A malformed
  envelope from an admitted peer is dropped and counted (`MalformedPacketsDropped`) and the connection
  stays; a server remote addressed to nobody is counted (`UnroutablePacketsDropped`); a `RemoteFunction`
  answer for a connection that is gone or now speaks for someone else is dropped
  (`StaleResponsesDropped`); a client's pending requests fail at once on a disconnect or `Dispose`
  instead of waiting out their timeout. A client's handlers do not require Mirror authentication, so
  a remote that overtakes the admission response is dropped as unadmitted rather than disconnecting
  the joining client. kcp2k's negative connection ids are real connections. A malformed
  client-to-server payload is dropped and counted by the world too, never thrown into the transport.
- **A client puts nothing on the wire before its admission.** The server's handlers require Mirror
  authentication, and an unreliable remote queued with the admission request would overtake it and
  make Mirror disconnect the joining client. So until the server has admitted it, a client drops every
  unreliable remote it is asked to send, counted in `UnadmittedSendsDropped` and said once in the log;
  such a remote is never sent or charged to the rate budget. Reliable `FireServer` calls and
  `InvokeServer` requests are held in order instead — at most 256 messages and 256 KiB
  (`MaxHeldSendsUntilAdmitted`, `MaxHeldBytesUntilAdmitted`), charged to the budget when held and
  counted in `SendsHeldUntilAdmitted` — and sent right after the admission is bound, before the
  readiness acknowledgement, so a script that fires a remote the moment it starts does not lose it.
  One past the bound is dropped uncharged, counted in `AdmissionHoldOverflowDrops` and said once, and an
  `InvokeServer` among them fails at once. A held `InvokeServer`'s 30 s timeout runs while it is held.
  A connection that closes before its admission drops the hold (`UnsentPacketsDropped`) and fails every
  held `InvokeServer`.
- **An admission belongs to its connection.** A client that stops and starts again within one frame
  begins unadmitted on the new connection: `AdmittedActorId` is null until that connection's own
  admission, calls made on the old connection fail, and new sends are held until the new admission. A
  binding made before any connection exists belongs to the next one.
- **`InvokeClient` to a player with no connection fails at once** ("the player is not connected to
  this server, so nothing was sent") and leaves nothing pending.
- **A joining client acknowledges readiness before the server talks to it.** The server admits a
  connection one round trip before the client has read its admission, so until the client's bridge
  sends `CoreAiClientReadyMessage` the server holds that connection's reliable remotes and
  `InvokeClient` requests in order — at most 256 messages and 256 KiB
  (`MaxHeldMessagesPerJoiningConnection`, `MaxHeldBytesPerJoiningConnection`); past that they are
  dropped and counted — and drops its unreliable remotes, counted (`PacketsHeldUntilReady`,
  `NotReadyPacketsDropped`). A connection that has not acknowledged within
  `ReadinessTimeoutSeconds` (10 s) is dropped with a log line naming the cause (`ReadinessTimeouts`).
  The client repeats its acknowledgement every second (`ReadyAcknowledgementRetrySeconds`) until the
  server's first clock anchor arrives, so a client admitted before the server's world attached is not
  lost to the deadline. Accepted acknowledgements are counted (`ReadyAcknowledgements`).
- **The server's clock reaches clients as anchors.** The server sends its time
  (`CoreAiServerClockMessage`) when a connection acknowledges readiness and every
  `ClockAnchorIntervalSeconds` (5 s) after that (`ClockAnchorsSent`/`ClockAnchorsReceived`). The time is
  what the server world's own `workspace:GetServerTimeNow()` reads — the world hands its clock to the bridge
  (`INetworkBridge.AttachServerClock`); a world loaded from a package does too, through the session-staging
  wrapper, which hands the new world's clock over only once that world goes live — and
  `HeldAheadOfWallSeconds` says how far that value is held ahead of the running clock after the server's
  wall clock stepped back (zero while it runs). When that clock starts or ends a hold, or jumps more than
  1 s ahead, the server sends a reliable step anchor at once (`ClockStepAnchorsSent`, also counted in
  `ClockAnchorsSent`). The client corrects each anchor by half the measured round trip and carries it
  forward on its own process clock: the first anchor of a connection, a held one, or one more than
  `ClockStepThresholdSeconds` (1 s) ahead replaces the estimate; a single anchor more than 1 s behind —
  which a late packet is — is set aside (`ClockAnchorsSetAside`) and taken only when the next anchor,
  carried back to the moment the set-aside one arrived, reads the server's clock within 1 s of it; otherwise
  the new anchor is set aside in its place; a nearer one is blended in; a NaN, infinite or non-positive
  anchor, or a negative or non-finite hold, is dropped and counted as malformed (`MalformedPacketsDropped`).
  While the last anchor's hold lasts, `IsServerClockHeld` is true on the client and the world's
  `GetServerTimeNow` holds with the server's instead of running on at half speed. `ServerClockOffsetSeconds`
  is then "the server's Unix time now minus this machine's wall-clock Unix time now" — zero on a server, and
  zero on a client until the first anchor, which `IsServerClockSynchronized` tells apart from "the clocks
  agree"; at the first anchor it steps to the whole skew between the two machines. It does not use
  Mirror's `NetworkTime.offset`, which compares the two processes' uptimes rather than their wall
  clocks. The wall clock is the bridge's `wallClock` constructor argument: the scene provider uses
  the system clock, which is what a world
  composed without its own `IRbxClockSource` reads; a composition whose world reads another clock builds its
  bridge with that same clock. The world side re-bases `workspace:GetServerTimeNow()` at the first
  synchronization and slews backward corrections afterwards ([RBX_API.md](../CoreAI/Docs/RBX_API.md),
  "Clocks").
- **A kicked or superseded client is told why.** Before the server closes a connection for a kick or
  for a newer session of the same player, it sends `CoreAiDisconnectNoticeMessage`
  (`CoreAiDisconnectNoticeKind.Kicked` with the kick's message — `Player:Kick(message)` passes its text
  through `INetworkBridge.DisconnectActor(actorId, message)`, `DefaultKickMessage` when it gave none;
  `Superseded` with `SupersededNoticeMessage`), at most `MaxNoticeMessageBytes` (1,024) UTF-8 bytes
  (`DisconnectNoticesSent`). The session is torn down and unbound at once, and the transport drops the
  connection on a later frame's `Pump` — Mirror discards a connection's unflushed messages when it is
  dropped, so a notice sent in the dropping frame would never leave; until then every packet from that
  connection is dropped as unadmitted. The deferred drop checks the connection object, so a reused
  connection id is never hit. On the client, `DisconnectNoticeReceived` fires and
  `LastDisconnectNotice` keeps the reason after the disconnect (cleared by the next admission), so the
  game can show it. A kick at join — a ban check in `PlayerAdded` — reaches the client after its
  admission response on the same ordered channel.
- **A world that ends a player itself ends the connection.** A host's own
  `DisconnectActor` — outside a kick or a transport drop — is treated like a kick: the client is sent
  a `Kicked` notice, the session is forgotten at once (its identity with the actor's last session),
  and the transport drops the connection on a later frame's `Pump` (`WorldReleasedConnections`).
- **A client's own kick disconnects it.** `Players.LocalPlayer:Kick()` on a client makes the client
  bridge disconnect from the server, as Roblox does (`SelfKicks`); a client has no authority over any
  other actor's connection.
- **The bridge runs on `Pump()`.** The scene provider calls `MirrorNetworkBridge.Pump()` every frame:
  request timeouts, the drops a kick or a supersede owes, readiness deadlines and clock anchors on a
  server, and the readiness acknowledgement on a client. A custom composition that builds the bridge
  itself must call `Pump()` once per frame too — `PumpTimeouts()` alone performs no owed drops, no
  deadlines, no anchors and no acknowledgements. `PerformOwedDropsNow()` closes every owed connection
  at once, for a composition that stops pumping (the provider calls it when it is disabled).

## The one contract you must honour yourself

**The client's own actor id must be the id the server admitted it as.** A client composition
resolves its actor id from its `IActorIdentityProvider`; the server decides admission through its
`IActorAdmissionProvider`. Nothing links the two. If they disagree, `FireClient` reaches a signal no
local script is listening on. The bridge logs that mismatch once, naming both ids, rather than
failing silently - but only once the local world has registered at least one actor of its own; before
that there is nothing to compare against and the mismatch is silent. With no identity provider
registered at all a client falls back to the local-host default, which will not match.

## Known limits

- **No two-process run is recorded.** Everything above is proven over the in-memory transport,
  Mirror's host mode included; the harness below is what runs it over localhost kcp, and its results
  are not in yet.
- **The inbound meter sits after Mirror's own unbatching.** Mirror reads every message of a batch
  into its struct before the bridge's handler sees it, so a flood still costs that copy — bounded by
  kcp's receive window — but nothing of the world's decoding or dispatch past the limits. A flooding
  connection is dropped from, not disconnected; a kick policy for one is not built. Behind the meter the
  world still bounds the handler work a sender's remotes start: at most 32 `OnServerEvent` handlers and
  `OnServerInvoke` callbacks per sender alive at once, and 128 induced threads per sender, 192 for all
  ([RBX_API.md](../CoreAI/Docs/RBX_API.md)).
- **Protocol compatibility is exact.** A server and a client of different protocols never run
  together, and there is no fallback that would let them; a protocol bump therefore needs every
  client updated with the server.
- **The host's client half has no CoreAI client bridge.** A host's own player is served in process (see
  Sessions), so the host has no client-side `MirrorNetworkBridge` state of its own: no roster copy (it
  reads the session host's), no clock anchors (it is the server's clock), no disconnect notice.
- **Live Mirror sessions cannot be handed to a world loaded at runtime.**
  `RbxWorldRuntimeSessionController` refuses a world load — at request, at confirmation and on a raw
  host load — with status `network_sessions_active` while the bridge lists registered actors, and the
  live world is left unchanged; disconnect the clients or stop the server first. The check is
  conservative: an actor registered for a mod context whose socket is gone counts too.

## Two-process harness

`Harness/` holds a test rig that runs a Mirror host and its remote clients as separate player
processes on localhost kcp, with the bridge, the session host and the authenticator for real: the
protocol handshake, the host's own player in host mode, readiness, the roster, a `RemoteEvent` chat
the host relays to everyone, `RemoteFunction` round trips both ways, an optional inbound flood, and
spike S3 (5 MiB as reliable messages). It works at the bridge level with fixed remote ids — there is no
Lua world, since replication does not cross the wire yet. Its assembly, `CoreAI.Net.Mirror.Harness`, is
constrained to the `COREAI_MIRROR_HARNESS` define as well as `MIRROR`, and only the harness build adds
that define, so a game that ships this package never ships the harness.

**Build** (the project must not be open in another editor):

```text
"C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" -batchmode -quit -projectPath <project>
  -executeMethod CoreAI.Net.Mirror.Harness.Editor.CoreAiMirrorHarnessBuild.BuildFromCommandLine
  [-coreaiHarnessBuildOut Builds/CoreAiMirrorHarness] [-coreaiHarnessBuildTarget Windows|Linux]
  -logFile <log>
```

or **Tools > CoreAI > Mirror > Build Two-Process Harness (Windows)**. The build saves an empty
temporary scene, builds it with `BuildPlayerOptions.extraScriptingDefines = COREAI_MIRROR_HARNESS`, and
deletes the scene; the harness creates its `NetworkManager`, `KcpTransport`, authenticator and provider
at load when the player is started with `-coreaiHarness`.

**Run** three processes, the host first (one line each):

```text
CoreAiMirrorHarness.exe -batchmode -nographics -logFile host.log -coreaiHarness host -coreaiHarnessClients 2
  -coreaiHarnessFloodWatch 10 -coreaiHarnessS3 -coreaiHarnessOut results/host.json
CoreAiMirrorHarness.exe -batchmode -nographics -logFile a.log -coreaiHarness client -coreaiHarnessName a
  -coreaiHarnessClients 2 -coreaiHarnessOut results/a.json
CoreAiMirrorHarness.exe -batchmode -nographics -logFile b.log -coreaiHarness client -coreaiHarnessName b
  -coreaiHarnessClients 2 -coreaiHarnessFlood 10000 -coreaiHarnessFloodSeconds 5 -coreaiHarnessOut results/b.json
```

| Argument | Meaning (default) |
| --- | --- |
| `-coreaiHarness host\|client` | The role; without it the player does nothing. |
| `-coreaiHarnessName <name>` | The player's name; its actor is `harness-<name>` (`host`, `client`). |
| `-coreaiHarnessAddress <ip>`, `-coreaiHarnessPort <port>` | Where the host listens (`127.0.0.1`, `7777`). |
| `-coreaiHarnessClients <n>` | Remote clients in the run, on every process (`2`); the roster expects `n + 1`. |
| `-coreaiHarnessSecret <text>` | The shared admission secret (`coreai-harness`). |
| `-coreaiHarnessFlood <per second>`, `-coreaiHarnessFloodSeconds <s>` | A client floods the host with unreliable remotes sent around its own bridge, after its checks (`0`, `5`). |
| `-coreaiHarnessFloodWatch <s>` | The host measures its frame times for this long after its checks (`0`: no flood window). Make it a few seconds longer than the flood. |
| `-coreaiHarnessS3` | The host runs spike S3 against the first remote client after the flood window. |
| `-coreaiHarnessTimeout <s>` | Gives up and fails after this long (`180`). |
| `-coreaiHarnessOut <file>` | Where the JSON result goes (`coreai-mirror-harness-<host\|name>.json`). |

Each process exits 0 when every check passed and 1 otherwise, writes its result as JSON and logs it on
one line after `COREAI_MIRROR_HARNESS_RESULT `. The result lists each check (`PASS ...`/`FAIL ...`), the
seconds to ready, the roster, the chat lines heard, the p99 frame time over the run
(`p99FrameMs`) and over the flood window (`p99FrameMsDuringFlood`, checked below 33 ms), the flood's
sent, accepted and dropped counts, the transport's limits (`reliableMaxPacketSize`,
`reliableMaxContentSize`, `unreliableMaxPacketSize` and the bridge's own ceilings), and the peaks of the
managed heap, of Unity's allocated memory and of the process working set.

**Spike S3** sends 5 MiB to one client four times — in 16 KiB, 64 KiB and 256 KiB slices, then in the
largest slice the reliable channel carries (`reliableMaxContentSize` less 20 bytes of envelope) — all
of a run handed to Mirror in one frame, the next run after the client's receipt. Each `spike` entry
records the slice size and count, how long the host took to hand them to Mirror (`hostSendSeconds`),
the time to the client's receipt (`hostSeconds`, one round trip included) and the client's own first-to-
last time (`clientSeconds`), and the managed and Unity peaks on both sides. A final probe hands Mirror a
slice one byte over the largest and records whether Mirror refused it with its size error
(`oversizeRefusedByMirror`): together with `reliableMaxPacketSize` that is the largest message kcp
accepts. These numbers set the join snapshot's chunk size; none is recorded yet.
