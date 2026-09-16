# CoreAI Mirror transport

Puts a CoreAI Rbx world on a real network transport, so `RemoteEvent` and `RemoteFunction` cross a
socket instead of looping back in one process.

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
   `NetworkManager` uses. On a server this is what turns a connection into an admitted actor.
4. Assign the provider to the **Network Bridge Provider** field (under the *Network transport* header) on your `CoreAiModsLifetimeScope`. Leaving
   that field empty is the default and keeps the world on the in-process null bridge - no remote
   peers, remotes loop back locally.
5. From your own composition, once the world exists, hand it to the provider. This stays the game's
   responsibility because the world does not exist when the bridge is built. `ConnectActor` returns
   the `RbxPlayer` it created, so the lambda adapts it to the `bool` the provider expects:

   ```csharp
   // `scope` is your CoreAiModsLifetimeScope; resolve after it has built. The LuaCsModStack the
   // container hands out is a facade over whichever world is live NOW, so read it inside the
   // lambdas rather than once: a world loaded at runtime replaces the bindings behind it.
   LuaCsModStack stack = scope.Container.Resolve<LuaCsModStack>();

   provider.AttachWorld(
       connectActor: ctx => stack.GameplayBindings.RbxApi.ConnectActor(ctx) != null,
       disconnectActor: ctx => stack.GameplayBindings.RbxApi.DisconnectActor(ctx));

   // The session host IS the identity source: it knows which actor each connection was admitted as.
   stack.GameplayBindings.RbxApi.Players.IdentitySource = provider.SessionHost;
   ```

   `scope.Container.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi` is the same live
   object, if you would rather name the world controller. `Players.IdentitySource` is a property of
   ONE world's Players service: a world loaded at runtime starts with none, so set it again on every
   world your game publishes, or its players get counter UserIds instead of the admitted ones.

Step 5 has no ordering trap: the provider admits whatever Mirror already accepted, from whichever of
the bridge and the world arrives last, so a client admitted before your world attaches still gets its
player. It is also once per provider: a second `AttachWorld` throws, because the sessions already
admitted belong to the world that created their players - the same rule `Role` follows. The lambdas
you pass are therefore the only route from the provider to a world: to follow a world loaded at
runtime, make them resolve the live world as above instead of attaching again.

Two outcomes are decided on the wire rather than left hanging. A connection the authenticator admitted
but the world could not turn into a player - `connectActor` threw or returned false - is dropped by the
provider, with the world's exception in the log; nothing could ever admit that connection again, so it
must not stay connected as an authenticated nobody. And `Player:Kick()` ends the kicked client's
connection through the bridge: the socket closes, the session host releases the session, and that
client's next remote is dropped as unadmitted instead of re-creating the player. That is the Mirror
bridge. On the in-process loopback (`NullNetworkBridge`, what an empty provider field gives you) a
kick is the Player teardown only: there is no connection to end and the actor stays registered, so
the actor's next use - a remote, a `Players.LocalPlayer` read, a new mod context - is a fresh join,
and the Player is re-created with `PlayerAdded` firing again.

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
  process, the most common Mirror topology - has no client-side remotes.
- **A refused client never learns it was refused.** The refusal is queued and then the connection is
  dropped synchronously, which discards the unsent batch, so the client cannot tell "refused" from
  "server vanished". The refusal itself is sound: the connection is dropped and no actor is created.
- **No inbound rate limit.** The rate limiter runs on the outbound path only, so an admitted peer can
  flood the server's world dispatch.
- **Adding a field to the admission response was a breaking wire change.** Server and client must run
  the same CoreAI version; an older server's shorter message cannot be deserialized by a newer client
  and the connection is dropped.
- **A runtime world load does not hand live Mirror sessions to the new world.**
  `RbxWorldRuntimeSessionController.LoadConfirmedAsync` builds a new `LuaCsRbxApiBindings` over the
  same bridge, publishes it and disposes the old one, while the provider keeps the lambdas from
  `AttachWorld` (a second `AttachWorld` throws). Sessions admitted before the load are not
  re-announced: their Player appears in the new world on its first remote, and `PlayerAdded` fires
  then. Their later disconnect is delivered to whichever world the lambdas resolve at that moment -
  the live one when they read the facade as in step 5, the disposed first world when the bindings
  were captured once. `Players.IdentitySource` is not re-wired either; the new world's Players
  service has none until the game sets it again.

Each of these is tracked in `TODO.md`.
