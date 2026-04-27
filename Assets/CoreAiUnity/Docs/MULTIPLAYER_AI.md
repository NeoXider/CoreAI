# CoreAI and multiplayer

## AI execution policy

In `CoreAILifetimeScope` (**Network / AI authority** section):

| Field | Meaning |
|------|--------|
| **AllPeers** (default) | LLM/orchestrator may run on every node. Handy for prototypes; duplicate calls and token spend are possible. |
| **HostOnly** | AI only on the node with `IsHostAuthority` (host, dedicated server, solo). |
| **ClientPeersOnly** | AI only on “pure” clients (`IsPureClient`), without host role. |

Without a network layer, `DefaultSoloNetworkPeer` is used: host = yes, pure client = no.

## Your peer (Unity Netcode, etc.)

Add a component derived from `CoreAiNetworkPeerBehaviour` and assign it to the **network peer behaviour** field on `CoreAILifetimeScope`.

Example logic (NGO pseudocode):

- `IsHostAuthority` → `IsServer` or `IsHost` (listen server).
- `IsPureClient` → `IsClient && !IsHost` (remote client without host authority).

## Commands and state

`ApplyAiGameCommand` and Lua/data versions are local by default. For multiplayer you usually need to:

- Replicate **decisions** that change the game (spawn, progression) from the server.
- Keep **one** source of truth for LLM traces, or clearly label “local chat” vs “server scenario”.

See comments in `ArenaSurvivalProceduralSetup` (simulation role) for more detail.
