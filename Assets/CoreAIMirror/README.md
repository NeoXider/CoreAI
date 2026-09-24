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
connection through the bridge: the socket closes, the session host releases the session, and that
client's next remote is dropped as unadmitted instead of re-creating the player. That is the Mirror
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
  kcp2k's negative connection ids are real connections.

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
  process, the most common Mirror topology - has no Mirror client of its own. Actors registered in
  the server process are served in process (see Sessions), which is not the same as a local client.
- **A refused client never learns it was refused.** The refusal is queued and then the connection is
  dropped synchronously, which discards the unsent batch, so the client cannot tell "refused" from
  "server vanished". The refusal itself is sound: the connection is dropped and no actor is created.
- **No inbound rate limit.** The rate limiter runs on the outbound path only, so an admitted peer can
  flood the server's world dispatch.
- **Adding a field to the admission response was a breaking wire change.** Server and client must run
  the same CoreAI version; an older server's shorter message cannot be deserialized by a newer client
  and the connection is dropped.
- **Live Mirror sessions cannot be handed to a world loaded at runtime (MVP11).** Until MVP11 brings
  session handoff, `RbxWorldRuntimeSessionController` refuses a world load — at request, at
  confirmation and on a raw host load — with status `network_sessions_active` while the bridge lists
  registered actors, and the live world is left unchanged; disconnect the clients or stop the server
  first. The check is conservative: an actor registered for a mod context whose socket is gone counts
  too.

Each of these is tracked in `TODO.md`.
