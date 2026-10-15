# Networking: host and AI authority

**Normative:** [DGF_SPEC.md §5](../DGF_SPEC.md) — the client is not authoritative for global AI outcomes; the host (or solo) is the source of truth for orchestration and publishing **`ApplyAiGameCommand`**.

**Where this lives in code:** the game drives orchestration through **`IAiOrchestrationService`**. Which peers may call the LLM is set by **Ai Network Execution Policy** (`AiNetworkExecutionPolicy`: `AllPeers` / `HostOnly` / `ClientPeersOnly`) on `CoreAILifetimeScope`, together with your `CoreAiNetworkPeerBehaviour` — see [MULTIPLAYER_AI.md](../MULTIPLAYER_AI.md). Replicating game state is the title's responsibility; the Rbx world's remotes can run over Mirror through `com.neoxider.coreaimirror` ([Assets/CoreAIMirror/README.md](../../../CoreAIMirror/README.md)). Flow map: [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md).

**Role placement and multiplayer:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).
