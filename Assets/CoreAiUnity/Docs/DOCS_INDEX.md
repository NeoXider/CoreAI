# Оглавление документации CoreAI (`Assets/CoreAiUnity/Docs`)

Краткий указатель файлов. Начните с **[QUICK_START.md](QUICK_START.md)**. **UPM-манифест ядра (версия, зависимости):** [../CoreAI/package.json](../CoreAI/package.json) и [README](../CoreAI/README.md).

| Документ | Назначение |
|----------|------------|
| [QUICK_START.md](QUICK_START.md) | Минимальные шаги: Unity, сцена, LLM, Play, тесты. |
| [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) | Карта кода, поток LLM → команды → Lua, сборки, чеклист PR. |
| [DGF_SPEC.md](DGF_SPEC.md) | Нормативный SPEC шаблона (версия в шапке); **§9.4** — главный поток Unity после LLM / MessagePipe. |
| [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) | Роли (Creator, Programmer, …), placement, размеры моделей. |
| [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) | LLMUnity, GGUF, OpenAI HTTP, логи **`[Llm]`** / **traceId**, таймаут запроса, PlayMode-тесты, Lua-конвейер. |
| [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | World Commands: управление миром из Lua через whitelist API → MessagePipe → main thread. |
| [GameTemplateGuides/INDEX.md](GameTemplateGuides/INDEX.md) | Короткие гайды под тайтл (сеть, оркестрация, роли, пример игры). |

**Пример игры** (`Assets/_exampleGame`):

| Документ | Назначение |
|----------|------------|
| [../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md) | Пошаговая настройка сцены RogueliteArena в инспекторе. |
| [../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md](../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md) | Арена: архитектура под мультиплеер, роли ИИ (волны, анализ игрока). |
| [../../_exampleGame/README.md](../../_exampleGame/README.md) | Концепт примера, стек, структура папок. |
| [../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md](../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md) | Геймплейный концепт забега / меты. |

**Корень репозитория:** [README.md](../../../README.md).
