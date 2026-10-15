# Game Template Guides — index

Short recipes for games built on the CoreAI template. Normative spec: [DGF_SPEC.md](../DGF_SPEC.md). **Onboarding and code map:** [DEVELOPER_GUIDE.md](../DEVELOPER_GUIDE.md).

| Document | Description |
|----------|-------------|
| [01_NetworkHostAuthority.md](01_NetworkHostAuthority.md) | Who runs AI in a networked game; DGF §5, MULTIPLAYER_AI, AI_AGENT_ROLES |
| [02_AiOrchestration.md](02_AiOrchestration.md) | Task flow, queue, LLM client decorators, tool-call events; DEVELOPER_GUIDE §3–4, DGF §3.4 / §6 |
| [03_AgentRolesAndProfiles.md](03_AgentRolesAndProfiles.md) | Built-in roles and tools, prompts, `BuiltInAgentRoleIds` / `RoleId`, debugging |
| [04_SingleVsMultiplayer.md](04_SingleVsMultiplayer.md) | One pipeline for solo and multiplayer; DGF §5 |
| [05_ReferenceGame.md](05_ReferenceGame.md) | The `_exampleGame` roguelite arena sample; DEVELOPER_GUIDE §8 |

Also:
- [MULTIPLAYER_AI.md](../MULTIPLAYER_AI.md) — Host / clients / all peers policy, `CoreAiNetworkPeerBehaviour`.
- [WORLD_COMMANDS.md](../WORLD_COMMANDS.md) — safe world control from Lua (whitelist → MessagePipe → main thread).
