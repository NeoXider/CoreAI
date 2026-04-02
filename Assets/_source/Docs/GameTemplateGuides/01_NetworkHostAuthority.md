# Сеть: хост и авторитет ИИ

**Норматив:** [DGF_SPEC.md §5](../DGF_SPEC.md) — клиент не авторитет для глобальных исходов ИИ; хост (или solo) — эталон для оркестрации и публикации **`ApplyAiGameCommand`**.

**Где в коде сейчас:** оркестрация идёт из игры через **`IAiOrchestrationService`**; репликация и «кто вызывает LLM» — ответственность тайтла. Карта потока: [DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md).

**Placement ролей и мультиплеер:** [AI_AGENT_ROLES.md](../AI_AGENT_ROLES.md).
