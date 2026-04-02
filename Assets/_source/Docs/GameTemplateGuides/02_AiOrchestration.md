# Оркестрация ИИ

**Актуальная карта кода:** [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md) (поток `RunTaskAsync` → `ILlmClient` → `ApplyAiGameCommand` → Lua).

**Норматив:** [DGF_SPEC.md §6](../DGF_SPEC.md) — очередь, приоритеты, таймауты, лимит параллелизма (целевое состояние; в MVP — один **`AiOrchestrator`** без очереди).

**Точки расширения в коде сейчас:**

- Вызов задач: **`IAiOrchestrationService`**, **`AiTaskRequest`** (роль, hint, поля ремонта Lua, опционально **`TraceId`**).
- Вызов модели: контейнер отдаёт **`ILlmClient`** как **`LoggingLlmClientDecorator`** (внутри — LLMUnity / OpenAI HTTP / stub); таймаут и лог **`[Llm]`** — см. [DEVELOPER_GUIDE §3–4](../DEVELOPER_GUIDE.md), [LLMUNITY_SETUP_AND_MODELS §1](../LLMUNITY_SETUP_AND_MODELS.md).
- После ответа модели: **`ApplyAiGameCommand`** (`AiEnvelope` + **`TraceId`**, затем при успехе/ошибке Lua — **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`** с тем же trace).
- Роли и промпты: **`BuiltInAgentRoleIds`**, цепочка в **`AgentPromptsInstaller`**.

Дальше по плану тайтла: обёртка с очередью/бюджетом поверх **`IAiOrchestrationService`**, политика частоты по ролям — см. [AI_AGENT_ROLES.md §6](../AI_AGENT_ROLES.md).
