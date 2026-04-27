# Game Template Guides — index

Short recipes for games built on the CoreAI template. Normative spec: [DGF_SPEC.md](../DGF_SPEC.md). **Onboarding and code map:** [DEVELOPER_GUIDE.md](../DEVELOPER_GUIDE.md).

| Document | Status | Description |
|----------|--------|-------------|
| [01_NetworkHostAuthority.md](01_NetworkHostAuthority.md) | draft | DGF §5, DEVELOPER_GUIDE §3, AI_AGENT_ROLES |
| [02_AiOrchestration.md](02_AiOrchestration.md) | draft | DEVELOPER_GUIDE §3–4, TraceId, LLM decorator, DGF §6 |
| [03_AgentRolesAndProfiles.md](03_AgentRolesAndProfiles.md) | draft | Links to AI_AGENT_ROLES, prompts, BuiltInAgentRoleIds |
| [04_SingleVsMultiplayer.md](04_SingleVsMultiplayer.md) | draft | Single core pipeline, DGF §5 |
| [05_ReferenceGame.md](05_ReferenceGame.md) | draft | DEVELOPER_GUIDE §8, `_exampleGame`, playbook |

Also:
- [MULTIPLAYER_AI.md](../MULTIPLAYER_AI.md) — Host / clients / all peers policy, `CoreAiNetworkPeerBehaviour`.
- [WORLD_COMMANDS.md](../WORLD_COMMANDS.md) — safe world control from Lua (whitelist → MessagePipe → main thread).
