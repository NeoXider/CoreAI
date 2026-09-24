# CoreAI Mirror transport

Puts a CoreAI Rbx world on a Mirror network transport, so `RemoteEvent` and `RemoteFunction` go
through Mirror's real message handlers and batcher instead of looping back in one process. The tests
drive that path over an in-memory transport; no two-process run over a real socket has been made yet
(see `TODO.md`).

This package compiles only when [Mirror](https://github.com/MirrorNetworking/Mirror) is installed:
its assembly is constrained to the `MIRROR` define, and Mirror itself is never vendored here. With
no Mirror in the project the package is simply absent, which is intended.

## What it is, and what it is not

It is the transport and the admission half of multiplayer: connections, actor admission, request
timeouts, packet and byte counters, a server clock offset, and the Roblox-facing remote surface a mod
author actually calls. **World-state replication does not cross the wire.** The replication types
exist and are tested, but nothing joins them to this transport yet, so remotes replicate and world
state does not. Read the transport-gap section of `TODO.md` before planning around this.

## Setup

1. Add a `CoreAiMirrorNetworkBridgeProvider` to a GameObject in the scene.
2. Set **Role** to `Server` for a dedicated server or a host, `Client` for a joining client. This is
   an explicit choice rather than something read from Mirror, because the bridge is built while the
   mod scope is still in `Awake`, before Mirror has started and before there is any live state to
   read. The side is fixed for the life of the bridge; changing `Role` afterwards throws, and if
   Mirror later starts as the other side the provider says so once instead of running as the wrong
   side.
3. Assign the provider's **Authenticator** to the same `CoreAiMirrorAuthenticator` your
   `NetworkManager` uses. On a server this is what turns a connection into an admitted actor. Its
   **Admission Timeout Seconds** (default 10) is how long a new connection may stay silent before it
   is dropped: Mirror has no authentication timeout of its own.
4. Assign the provider to the **Network Bridge Provider** field (under the *Network transport* header) on your `CoreAiModsLifetimeScope`. Leaving
   that field empty is the default and keeps the world on the in-process null bridge - no remote
   peers, remotes loop back locally.
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
provider runs no `Update`, so a drop owed after it was disabled waits until it is enabled again. And `Player:Kick()` ends the kicked client's
connection through the bridge: the client is sent the kick's message first, the session host releases
the session at once, the socket closes on a later frame (see Sessions), and that client's next remote
is dropped as unadmitted instead of re-creating the player. That is the Mirror
bridge. On the in-process loopback (`NullNetworkBridge`, what an empty provider field gives you) a
kick is the Player teardown only: there is no connection to end and the actor stays registered, so
the actor's next use - a remote, a `Players.LocalPlayer` read, a new mod context - is a fresh join,
and the Player is re-created with `PlayerAdded` firing again.

## Sessions

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
- **Actors local to the server are served in process.** A server never sends a client-bound or
  server-bound envelope to itself over the wire: a remote from or to an actor registered in this
  process is delivered in process (`LocalDeliveries`), so `InvokeServer` from a host-local actor does
  not wait 30 seconds and `FireClient`/`FireAllClients`/`InvokeClient` reach local actors.
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
  instead of waiting out their timeout. A client does not need Mirror authentication for its handlers, so a remote that overtakes
  the admission response is dropped as unadmitted rather than disconnecting the joining client.
  kcp2k's negative connection ids are real connections. A malformed client-to-server payload is
  dropped and counted by the world too, never thrown into the transport.
- **A client puts nothing on the wire before its admission.** The server's handlers require Mirror
  authentication, and an unreliable remote queued with the admission request would overtake it and
  make Mirror disconnect the joining client. So until the server has admitted it, a client drops every
  unreliable remote it is asked to send, counted in `UnadmittedSendsDropped` and said once in the log;
  such a remote is never sent or charged to the rate budget. Reliable `FireServer` calls and
  `InvokeServer` requests are held in order instead — at most 256 messages and 256 KiB
  (`MaxHeldSendsUntilAdmitted`, `MaxHeldBytesUntilAdmitted`), charged to the budget when held and
  counted in `SendsHeldUntilAdmitted` — and sent right after the admission is bound, before the
  readiness acknowledgement, so a script that fires a remote the moment it starts no longer loses it.
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
  agree"; at the first anchor it steps to the whole skew between the two machines. It no longer reads
  Mirror's `NetworkTime.offset`, which compares the two processes' uptimes rather than their clocks (a
  client of a server that had run for a day read the server's time a day off). The wall clock is the
  bridge's `wallClock` constructor argument: the scene provider uses the system clock, which is what a world
  composed without its own `IRbxClockSource` reads; a composition whose world reads another clock builds its
  bridge with that same clock. The world side re-bases `workspace:GetServerTimeNow()` at the first
  synchronization and slews backward corrections afterwards (`Assets/CoreAI/Docs/RBX_API.md`, "Clocks").
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
  and the transport drops the connection on a later frame's `Pump` (`WorldReleasedConnections`). Before, the connection stayed
  authenticated to Mirror and bound to nobody, holding its slot while every packet from it was
  dropped.
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
that there is nothing to compare against and the mismatch is silent. With no identity provider registered at all a client falls back to the local-host
default, which will not match.

## Known limits

- **Host mode is not supported.** A server bridge installs server handlers only, and the provider
  refuses a client bridge while the server is active, so a host - server and local client in one
  process, the most common Mirror topology - has no Mirror client of its own. Mirror's host-mode local
  client connection is refused as a world player, loudly (a log line, counted in
  `CoreAiMirrorSessionHost.HostModeConnectionsRefused`), and left to Mirror. Actors registered in the
  server process are served in process (see Sessions), which is not the same as a local client. Host
  mode is the MVP5 rung of the ladder (`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §MVP5).
- **A refused client never learns it was refused.** The refusal is queued and then the connection is
  dropped synchronously, which discards the unsent batch, so the client cannot tell "refused" from
  "server vanished". The refusal itself is sound: the connection is dropped and no actor is created.
- **No inbound rate limit.** The rate limiter runs on the outbound path only, so an admitted peer can
  flood the server's world dispatch. What the world bounds is the handler work a sender's remotes
  start: at most 32 `OnServerEvent` handlers and `OnServerInvoke` callbacks per sender may be alive
  at once (a `RemoteFunction` over that is answered `BUDGET_EXCEEDED`, an event is dropped, counted
  and logged once per sender), and repeated receive warnings are logged once per sender and kind
  every 10 s; decoding and dispatch themselves are still unmetered.
- **Server and client must run the same CoreAI version.** Adding a field to the admission response
  already broke older peers; the readiness, clock and notice messages are new too, and the clock
  anchor gained `HeldAheadOfWallSeconds`. A missing message fails loudly — provided Mirror's
  `exceptionsDisconnect` is on, its default: an older server has no handler for
  `CoreAiClientReadyMessage`, so Mirror disconnects a newer client right after admission with an error
  in the server log; an older client never acknowledges readiness, so a newer server drops it at the
  readiness deadline with a log line naming the cause. A field added to a message is not detected: the
  older reader throws inside Mirror's handler, which disconnects only with `exceptionsDisconnect` on
  and otherwise logs and keeps the connection, one message lost. Nothing on the wire negotiates the
  version, and there is no compatibility fallback.
- **Live Mirror sessions cannot be handed to a world loaded at runtime (MVP5, the old MVP11).** Until
  session handoff exists, `RbxWorldRuntimeSessionController` refuses a world load — at request, at
  confirmation and on a raw host load — with status `network_sessions_active` while the bridge lists
  registered actors, and the live world is left unchanged; disconnect the clients or stop the server
  first. The check is conservative: an actor registered for a mod context whose socket is gone counts
  too.

Each of these is tracked in `TODO.md`.
