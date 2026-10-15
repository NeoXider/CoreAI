# CoreAI and multiplayer

CoreAI has two separate multiplayer pieces:

- **Where AI runs** — a scope-wide execution policy on `CoreAILifetimeScope` (this page).
- **The Rbx world over the network** — `RemoteEvent` / `RemoteFunction` traffic over Mirror through the
  optional `com.neoxider.coreaimirror` package ([Assets/CoreAIMirror/README.md](../../CoreAIMirror/README.md)).
  Remotes cross the wire; world-state replication and Mirror host mode are not there yet. Status and next
  steps: [Docs/ROADMAP.md](../../../Docs/ROADMAP.md), Track B.

## AI execution policy

In `CoreAILifetimeScope` (**Network / AI authority** section), **Ai Network Execution Policy**
(`AiNetworkExecutionPolicy`):

| Value | Meaning |
|------|--------|
| **AllPeers** (default) | LLM/orchestrator may run on every node. Handy for prototypes; duplicate calls and token spend are possible. |
| **HostOnly** | AI only on the node with `IsHostAuthority` (host, dedicated server, solo). |
| **ClientPeersOnly** | AI only on “pure” clients (`IsPureClient`), without host role. |

`NetworkedAuthorityHost` turns the policy and the peer into `IAuthorityHost.CanRunAiTasks`. On a peer the
policy excludes, `RunTaskAsync` returns a failed result (`AI execution disabled.`) and a streaming request
ends with the error `authority denied`; nothing is sent to a model.

Without a network layer, `DefaultSoloNetworkPeer` is used: host = yes, pure client = no.

## Your peer (Unity Netcode, Mirror, etc.)

Add a component derived from `CoreAiNetworkPeerBehaviour` (it implements `IAiNetworkPeer`) and assign it
to the **Network Peer Behaviour** field on `CoreAILifetimeScope`. CoreAI does not ship one for any
networking library, `com.neoxider.coreaimirror` included.

Example logic (NGO pseudocode):

- `IsHostAuthority` → `IsServer` or `IsHost` (listen server).
- `IsPureClient` → `IsClient && !IsHost` (remote client without host authority).

## Commands and state

`ApplyAiGameCommand` and Lua/data versions are local by default. For multiplayer you usually need to:

- Replicate **decisions** that change the game (spawn, progression) from the server.
- Keep **one** source of truth for LLM traces, or clearly label “local chat” vs “server scenario”.

The audit log is also per peer; see [DETERMINISM_AND_REPLAY.md](DETERMINISM_AND_REPLAY.md) for the
host-authoritative pattern. The example game shows a simulation-role switch in
`Assets/_exampleGame/RogueliteArena/Features/ArenaSurvival/Infrastructure/ArenaSurvivalProceduralSetup.cs`.
