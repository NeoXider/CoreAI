# Роли и профили агентов

**Каталог ролей, placement, модели:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).

**Промпты и встроенные id в коде:** [DEVELOPER_GUIDE.md §5](../DEVELOPER_GUIDE.md); константы **`BuiltInAgentRoleIds`**; тесты **`AgentRolesAndPromptsTests`**.

**Практика:** новые роли — стабильный строковый id, системный промпт в **`Resources/AgentPrompts/System/<Id>.txt`** или в манифесте (**Create → CoreAI → Agent Prompts Manifest**).

**Отладка:** в консоли роль видна в логах **`[Llm]`** (`role=…`) и в **`ApplyAiGameCommand`**; сквозной **`traceId`** — см. [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md).
