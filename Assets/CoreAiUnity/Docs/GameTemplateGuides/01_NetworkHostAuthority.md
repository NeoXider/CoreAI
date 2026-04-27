# Networking: host and AI authority

**Normative:** [DGF_SPEC.md §5](../DGF_SPEC.md) — the client is not authoritative for global AI outcomes; the host (or solo) is the source of truth for orchestration and publishing **`ApplyAiGameCommand`**.

**Where this lives in code today:** orchestration is driven from the game via **`IAiOrchestrationService`**; replication and “who calls the LLM” are the title’s responsibility. Flow map: [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md).

**Role placement and multiplayer:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).
