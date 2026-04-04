# Оглавление документации CoreAI (`Assets/CoreAiUnity/Docs`)

Краткий указатель файлов. Начните с **[QUICK_START.md](QUICK_START.md)**. **UPM-манифест ядра (версия, зависимости):** [../CoreAI/package.json](../CoreAI/package.json) и [README](../CoreAI/README.md).

| Документ | Назначение |
|----------|------------|
| [QUICK_START.md](QUICK_START.md) | Минимальные шаги: Unity, сцена, LLM, Play, тесты. |
| [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) | Карта кода, поток LLM → команды → Lua, сборки, чеклист PR. |
| [DGF_SPEC.md](DGF_SPEC.md) | Нормативный SPEC шаблона (версия в шапке); **§9.4** — главный поток Unity после LLM / MessagePipe. |
| [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) | Роли (Creator, Programmer, …), placement, размеры моделей. |
| [MemorySystem.md](MemorySystem.md) | 🧠 **Система памяти**: 2 типа (MemoryTool + ChatHistory), конфигурация, примеры. |
| [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) | LLMUnity, GGUF, OpenAI HTTP, логи **`[Llm]`** / **traceId**, таймаут запроса, PlayMode-тесты, Lua-конвейер. |
| [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | World Commands: управление миром из Lua через whitelist API → MessagePipe → main thread. |
| [MULTIPLAYER_AI.md](MULTIPLAYER_AI.md) | Мультиплеер + AI архитектура. |
| [GameTemplateGuides/INDEX.md](GameTemplateGuides/INDEX.md) | Короткие гайды под тайтл (сеть, оркестрация, роли, пример игры). |

---

## Тесты — Документация

| Документ | Тестов | Описание |
|----------|--------|----------|
| [../Tests/PlayModeTest/CraftingMemory_README.md](../Tests/PlayModeTest/CraftingMemory_README.md) | 5 | 🤖 AI Механика крафта: агенты, воркфлоу, MemoryTool, Microsoft.Extensions.AI |
| **LuaExecutionPipelineEditModeTests.cs** | 8 | Lua sandbox: exec success/failure, repair loop, max generations, role isolation |
| **AgentDataPassingEditModeTests.cs** | 4 | Data passing: Creator→CoreMechanic→Programmer, memory isolation, full chain |
| **MultiAgentCraftingWorkflowPlayModeTests.cs** | 2 | PlayMode: полный воркфлоу 3 агентов через реальную LLM |
| **CraftingMemoryViaLlmUnityPlayModeTests.cs** | 1 | PlayMode: 4 крафта + детерминизм (локальная GGUF) |
| **CraftingMemoryViaOpenAiPlayModeTests.cs** | 2 | PlayMode: 4 крафта + 2 крафта quick (LM Studio HTTP) |

---

**Пример игры** (`Assets/_exampleGame`):

| Документ | Назначение |
|----------|------------|
| [../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md) | Пошаговая настройка сцены RogueliteArena в инспекторе. |
| [../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md](../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md) | Арена: архитектура под мультиплеер, роли ИИ (волны, анализ игрока). |
| [../../_exampleGame/README.md](../../_exampleGame/README.md) | Концепт примера, стек, структура папок. |
| [../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md](../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md) | Геймплейный концепт забега / меты. |

**Корень репозитория:** [README.md](../../../README.md).

**TODO план развития:** [../../../TODO.md](../../../TODO.md)
