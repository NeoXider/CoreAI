# Game Template Guides — оглавление

Короткие рецепты для игр на шаблоне CoreAI. Норматив — [DGF_SPEC.md](../DGF_SPEC.md). **Онбординг и карта кода** — [DEVELOPER_GUIDE.md](../DEVELOPER_GUIDE.md).

| Документ | Статус | Описание |
|----------|--------|----------|
| [01_NetworkHostAuthority.md](01_NetworkHostAuthority.md) | черновик | DGF §5, DEVELOPER_GUIDE §3, AI_AGENT_ROLES |
| [02_AiOrchestration.md](02_AiOrchestration.md) | черновик | DEVELOPER_GUIDE §3–4, TraceId, декоратор LLM, DGF §6 |
| [03_AgentRolesAndProfiles.md](03_AgentRolesAndProfiles.md) | черновик | Ссылки на AI_AGENT_ROLES, промпты, BuiltInAgentRoleIds |
| [04_SingleVsMultiplayer.md](04_SingleVsMultiplayer.md) | черновик | Единый пайплайн ядра, DGF §5 |
| [05_ReferenceGame.md](05_ReferenceGame.md) | черновик | DEVELOPER_GUIDE §8, `_exampleGame`, playbook |

Дополнительно:
- [MULTIPLAYER_AI.md](../MULTIPLAYER_AI.md) — политика Host / клиенты / все узлы, `CoreAiNetworkPeerBehaviour`.
- [WORLD_COMMANDS.md](../WORLD_COMMANDS.md) — безопасное управление миром из Lua (whitelist → MessagePipe → main thread).
