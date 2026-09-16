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
4. Assign the provider to the **Network transport** field on your `CoreAiModsLifetimeScope`. Leaving
   that field empty is the default and keeps the world on the in-process null bridge - no remote
   peers, remotes loop back locally.
5. From your own composition, once the world exists, call `provider.AttachWorld(connectActor,
   disconnectActor)` and set `Players.IdentitySource = provider.SessionHost`. These stay the game's
   responsibility because the world does not exist when the bridge is built.

Step 5 has no ordering trap: the provider admits whatever Mirror already accepted, from whichever of
the bridge and the world arrives last, so a client admitted before your world attaches still gets its
player.

## The one contract you must honour yourself

**The client's own actor id must be the id the server admitted it as.** A client composition
resolves its actor id from its `IActorIdentityProvider`; the server decides admission through its
`IActorAdmissionProvider`. Nothing links the two. If they disagree, `FireClient` reaches a signal no
local script is listening on - the bridge logs that mismatch once, naming both ids, rather than
failing silently. With no identity provider registered at all a client falls back to the local-host
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

Each of these is tracked in `TODO.md`.
