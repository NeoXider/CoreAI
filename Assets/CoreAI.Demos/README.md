# CoreAI.Demos

Самодостаточные демо-сцены поверх CoreAI (вне `Assets/CoreAI` и `Assets/CoreAiUnity`,
чтобы не попадать в пакеты). Каждая папка: сцена + минимальные скрипты + README.

| Демо | Сцена | Что показывает | Нужен LLM |
|---|---|---|---|
| [LuaMods](LuaMods/README.md) | `LuaMods/LuaModsDemo.unity` | Lua-моды (`LuaModRuntime`): хуки, таймеры, события, store, capability-уровни; `LuaLogicSlots` — переопределение формулы урона из Lua | Нет |
| [WorldCommands](WorldCommands/README.md) | `WorldCommands/WorldCommandsDemo.unity` | Конвейер AI-команд: `IAiGameCommandSink` → `AiGameCommandRouter` → `CoreAiWorldCommandExecutor` (тот же путь, что у LLM-агентов и Lua-биндингов) | Нет |
| [Skills](Skills/README.md) | `Skills/SkillsDemo.unity` | `SkillSet` + `AgentBuilder`: каталог скиллов, `read_skill` / `call_skill_tool`, агент-«гейммастер» с крафтом и боем | Да |
| [LiveMechanics](LiveMechanics/README.md) | `LiveMechanics/LiveMechanicsDemo.unity` | **Реальная LLM через чат меняет механики на лету**: роль Programmer пишет Lua → `execute_lua`-пайплайн → logic slots / `LuaModRuntime` / world-команды | Да |
| [FullAccess](FullAccess/README.md) | `FullAccess/FullAccessDemo.unity` | **Full-режим**: reflection-доступ к GameObject/компонентам (`unity_*` APIs), opt-in на `CoreAILifetimeScope` | Да |

## Общие требования

- В каждой сцене стоит `CoreAILifetimeScope` (DI-композиция CoreAI). Настройки берутся из
  `Resources/CoreAISettings`, если в инспекторе не назначен отдельный ассет.
- Lua-демо требуют MoonSharp в проекте (define `COREAI_HAS_MOONSHARP`) и отсутствие `COREAI_NO_LUA`.
- Skills-демо требует настроенный LLM-бэкенд в `CoreAISettings` (LLMUnity-модель или HTTP API);
  остальные демо работают полностью офлайн.

> Сцены и ассеты демо собраны через MCP for Unity (см. `Assets/CoreAiUnity/Docs/DGF_SPEC.md`, §11) —
> тот же канал редакторной автоматизации, которым агент запускает тесты этого репозитория.
