# Оглавление документации CoreAI

Начните с уровня, который подходит вам. **UPM-манифест ядра:** [package.json](../CoreAI/package.json) · [README](../../../README.md).

---

## 🟢 Начни здесь (Новичок)

Минимальный путь: установка → первый агент → запуск.

| # | Документ | Что узнаете |
|---|----------|-------------|
| 1 | [QUICK_START.md](QUICK_START.md) | Установка, открыть сцену, подключить LLM, нажать Play |
| 1b | [QUICK_START_FULL.md](QUICK_START_FULL.md) | 🚀 **Полный Quick Start:** LM Studio → Unity → первая команда (10 минут) |
| 2 | [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) | 🏗️ Создать NPC за 3 строки, режимы, готовые рецепты |
| 3 | [COREAI_SETTINGS.md](COREAI_SETTINGS.md) | ⚙️ Настройки: бэкенд, модель, температура, таймаут |
| 4 | [CHAT_TOOL_CALLING.md](CHAT_TOOL_CALLING.md) | 🛒 Пример: Торговец NPC с инвентарём |
| 4b | [EXAMPLES.md](EXAMPLES.md) | 📖 **Примеры:** враги, крафт, auto-repair, торговец, стражник |

---

## 🟡 Углубись (Средний уровень)

Инструменты, память, роли, кастомизация.

| # | Документ | Что узнаете |
|---|----------|-------------|
| 5 | [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) | 🔧 Все инструменты: память, Lua, мир, инвентарь |
| 5b | [JSON_COMMAND_FORMAT.md](JSON_COMMAND_FORMAT.md) | 📋 **Формат JSON команд** для каждой роли (справочник) |
| 6 | [MemorySystem.md](MemorySystem.md) | 🧠 MemoryTool vs ChatHistory, конфигурация по ролям |
| 7 | [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) | 🤖 5 базовых ролей, placement, модели по ролям |
| 8 | [WORLD_COMMANDS.md](WORLD_COMMANDS.md) | 🌍 Управление миром из Lua: спавн, движение, сцены |
| 9 | [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) | 📦 LLMUnity, GGUF, OpenAI HTTP, Lua-конвейер |
| 9b | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | 🔧 **Troubleshooting:** модель молчит, Lua упала, память не пишется |

---

## 🔴 Архитектура (Профессионалы)

Внутреннее устройство, DI, потоки, спецификация.

| # | Документ | Что узнаете |
|---|----------|-------------|
| 10 | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) | 🗺️ Карта кода, поток LLM → команды, чеклист PR |
| 10b | [COMMAND_FLOW_DIAGRAM.md](COMMAND_FLOW_DIAGRAM.md) | 🗺️ **Диаграмма:** как команда проходит через всю систему |
| 11 | [DGF_SPEC.md](DGF_SPEC.md) | 📐 Нормативный SPEC: DI, потоки, авторитет, §9.4 main thread |
| 12 | [MEAI_TOOL_CALLING.md](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) | 🛠️ MEAI pipeline: ILlmTool → AIFunction → FunctionInvokingChatClient |
| 13 | [MULTIPLAYER_AI.md](MULTIPLAYER_AI.md) | 🌐 Мультиплеер + AI архитектура |
| 14 | [GameTemplateGuides/INDEX.md](GameTemplateGuides/INDEX.md) | 📚 Гайды под тайтл: сеть, оркестрация, роли |

---

## 🧪 Тесты — Документация

| Документ | Тестов | Описание |
|----------|--------|----------|
| [CraftingMemory_README.md](../Tests/PlayModeTest/CraftingMemory_README.md) | 5 | 🤖 AI Механика крафта: агенты, воркфлоу, MemoryTool, Microsoft.Extensions.AI |
| **LuaExecutionPipelineEditModeTests.cs** | 8 | Lua sandbox: exec success/failure, repair loop, max generations, role isolation |
| **AgentDataPassingEditModeTests.cs** | 4 | Data passing: Creator→CoreMechanic→Programmer, memory isolation, full chain |
| **MultiAgentCraftingWorkflowPlayModeTests.cs** | 2 | PlayMode: полный воркфлоу 3 агентов через реальную LLM |
| **CraftingMemoryViaLlmUnityPlayModeTests.cs** | 1 | PlayMode: 4 крафта + детерминизм (локальная GGUF) |
| **CraftingMemoryViaOpenAiPlayModeTests.cs** | 2 | PlayMode: 4 крафта + 2 крафта quick (LM Studio HTTP) |

---

## 🎮 Пример игры (`Assets/_exampleGame`)

| Документ | Назначение |
|----------|------------|
| [UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md) | Пошаговая настройка сцены RogueliteArena в инспекторе |
| [ARENA_ARCHITECTURE_AND_AI.md](../../_exampleGame/Docs/ARENA_ARCHITECTURE_AND_AI.md) | Арена: архитектура под мультиплеер, роли ИИ |
| [README.md](../../_exampleGame/README.md) | Концепт примера, стек, структура папок |
| [ROGUELITE_PLAYBOOK.md](../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md) | Геймплейный концепт забега / меты |

---

## 🎬 Демо и медиа

| Документ | Назначение |
|----------|------------|
| [DEMO_RECORDING_GUIDE.md](DEMO_RECORDING_GUIDE.md) | 🎬 Сценарии записи видео/GIF, инструменты, DemoRunner скрипт |

---

**TODO план развития:** [TODO.md](../../../TODO.md)
