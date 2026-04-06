# Changelog — `com.nexoider.coreaiunity`

Хост Unity: сборка **CoreAI.Source**, тесты (EditMode / PlayMode), Editor-меню, документация. Зависит от **`com.nexoider.coreai`**.

## [0.11.0] - 2026-04-07

### Universal System Prompt Prefix

- ✨ **CoreAISettingsAsset.universalSystemPromptPrefix** — поле в Inspector (секция "⚙️ Общие настройки")
- ✨ **CoreAISettings.UniversalSystemPromptPrefix** — статическое свойство для программного задания
- ✨ **SyncToStaticSettings()** — синхронизация при старте из CoreAILifetimeScope
- ✨ Префикс автоматически применяется ко всем агентам (встроенным и кастомным)

### Temperature (общая для всех бэкендов)

- ✨ **CoreAISettingsAsset.temperature** — изменён default с `0.2` на `0.1`
- ✨ **CoreAISettings.Temperature** — статическое свойство (по умолчанию `0.1`)
- ✨ Температура применяется и для LLMUnity, и для HTTP API
- ✨ **AgentBuilder.WithTemperature(float)** — переопределить температуру для конкретного агента
- ✨ **AgentConfig.Temperature** — свойство конфигурации
- ✨ Поле в Inspector: "Temperature" в секции "⚙️ Общие настройки"

### MaxToolCallIterations (вынесен из хардкода)

- ✨ **CoreAISettingsAsset.maxToolCallIterations** — поле в Inspector (default 2)
- ✨ **CoreAISettings.MaxToolCallIterations** — статическое свойство
- ✨ **MeaiLlmClient** теперь читает из настроек вместо хардкода `MaximumIterationsPerRequest = 2`

## [0.7.0] - 2026-04-06

### Единый MEAI Tool Calling Format (MAJOR)

**Все tool calls теперь используют единый формат через MEAI function calling**

#### Новое
- ✨ **LuaTool**: MEAI AIFunction для выполнения Lua скриптов от Programmer
- ✨ **LuaLlmTool**: ILlmTool обёртка для Lua tool
- ✨ **InventoryTool**: MEAI AIFunction для Merchant NPC (получение инвентаря)
- ✨ **InventoryLlmTool**: ILlmTool обёртка для Inventory tool
- ✨ **Merchant Agent**: Новый NPC-торговец с инструментами (get_inventory + memory)
- ✨ **AgentBuilder**: Конструктор кастомных агентов — легко создавать новых агентов с уникальными инструментами
- ✨ **AgentMode**: 3 режима — ToolsOnly, ToolsAndChat, ChatOnly
- ✨ **WithChatHistory()**: Сохранение истории диалога (контекст текущей сессии, в RAM)
- ✨ **WithMemory()**: Персистентная память (между сессиями, в JSON файл)
- ✨ **Tool Call Retry**: До 3 попыток автоматически при неудачном tool call. Модель получает сообщение об ошибке и может исправить формат. (CoreAISettings.MaxToolCallRetries)

#### Изменения
- 🔧 **LlmUnityMeaiChatClient.TryParseToolCallFromText**: Упрощён до единого формата `{"name": "...", "arguments": {...}}`
- 🔧 **Все tool calls через MEAI**: Memory и Lua tools работают через FunctionInvokingChatClient
- 🔧 **ProgrammerResponsePolicy упрощена**: Больше не проверяет fenced блоки
- 🔧 **AgentMemoryPolicy.SetToolsForRole()**: Добавление кастомных инструментов к роли
- 🔧 **Обновлены все промпты**: Programmer, Merchant используют единый формат

#### Удалено
- ❌ **AgentMemoryDirectiveParser**: Удалён - всё через MEAI pipeline
- ❌ **Fallback парсинг в AiOrchestrator**: Memory tool работает через FunctionInvokingChatClient
- ❌ **Fenced блоки** (```memory, ```lua): Не используются для tool calls

#### Breaking Changes
- **Programmer агент** теперь вызывает `execute_lua` tool вместо fenced ```lua блоков
- **Memory tool** формат: `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- **MaxLuaRepairGenerations** изменён с 4 на 3

#### Тесты
- ✨ **AgentBuilderEditModeTests** - 8 тестов на конструктор агентов
- ✨ **CustomAgentsPlayModeTests** - 3 теста на кастомных агентов (Merchant, Analyzer, Storyteller)
- 🔧 **MeaiToolCallsEditModeTests** - MemoryTool, LuaTool, парсинг JSON
- 🔧 **LuaExecutionPipelineEditModeTests** - обновлено ожидаемое количество повторов (4→3)
- 🔧 **RoleStructuredResponsePolicyEditModeTests** - Programmer теперь пропускает любой текст
- 🔧 Все PlayMode тесты обновлены под единый формат tool calls v0.7.0

#### Документация
- 📝 **AGENT_BUILDER.md** - полное руководство по конструктору агентов
- 📝 **TOOL_CALL_SPEC.md** - обновлённая спецификация tool calling
- 📝 **CHAT_TOOL_CALLING.md** - Merchant NPC с tool calling
- 📝 **DEVELOPER_GUIDE.md** - обновлены секции

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.7.0**

---

## [0.6.1] - 2026-04-06

### Tool Calling Fallback для LLM без структурных tool_calls

- 🔧 **LlmUnityMeaiChatClient.TryParseToolCallFromText**: Добавлен fallback парсинг JSON tool calls из текста ответа модели
- 🔧 **Поддержка Qwen3.5-2B**: Модель возвращает tool call как JSON текст, а не как структурный tool_call — теперь это распознаётся и преобразуется в `FunctionCallContent` для MEAI
- 🔧 **Форматы распознавания**: 
  - `{"tool": "memory", "action": "write", "content": "..."}`
  - `{"name": "memory", "arguments": {...}}`
  - ```json\n{...}\n``` fenced blocks

### Fixes

- ✅ **Memory Tool теперь работает**: `FunctionInvokingChatClient` распознаёт tool call и вызывает `MemoryTool.ExecuteAsync()`
- ✅ **Память сохраняется между вызовами**: Craft 2 видит память из Craft 1

### Documentation

- Обновлены секции troubleshooting в LLMUNITY_SETUP_AND_MODELS.md

---

## [0.6.0] - 2026-04-05

### Microsoft.Extensions.AI Full Integration

- ✨ **MeaiLlmUnityClient**: Полная интеграция с Microsoft.Extensions.AI для LLMUnity
- ✨ **FunctionInvokingChatClient**: Использует MEAI FunctionInvokingChatClient для автоматического tool calling
- ✨ **IChatClient реализация**: Внутренний IChatClient обёртка над LLMAgent
- ✨ **MemoryTool.CreateAIFunction()**: Создаёт AIFunction для MEAI

### Removed

- ❌ **LlmUnityLlmClient**: Заменён на MeaiLlmUnityClient
- ❌ **MeaiChatClientAdapter**: Удалён — интеграция теперь через MeaiLlmUnityClient

### Documentation

- Обновлена документация: MemorySystem.md, DEVELOPER_GUIDE.md, DGF_SPEC.md, LLMUNITY_SETUP_AND_MODELS.md

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.6.0**

---

## [0.5.0] - 2026-04-05

### LLM Response Validation

- ✨ **Role-specific validation policies**: 6 классов для валидации ответов каждой роли
- ✨ **CompositeRoleStructuredResponsePolicy**: Маршрутизация валидации по roleId
- ✨ **20 новых EditMode тестов**: Comprehensive coverage всех политик
- ✅ **Автоматический retry**: При неудачной валидации — повторный запрос с подсказкой

### GameConfig System

- ✨ **UnityGameConfigStore**: Реализация `IGameConfigStore` на ScriptableObject
- ✨ **DI интеграция**: Регистрация в CoreAILifetimeScope
- ✨ **EditMode тесты**: 9 тестов (policy, read, update, round-trip)
- ✨ **PlayMode тесты**: 3 теста (AI read/modify/write, no access, multi-key)
- ✨ **GAME_CONFIG_GUIDE.md**: Полная инструкция для разработчиков

### Analyzer Tests

- ✨ **AnalyzerEditModeTests**: 10 тестов (prompts, telemetry, validation, orchestrator)

### Tests

- ✨ **RoleStructuredResponsePolicyEditModeTests.cs**: 20 тестов на все политики
- ✨ **GameConfigEditModeTests.cs**: 9 тестов на GameConfigTool и GameConfigPolicy
- ✨ **GameConfigPlayModeTests.cs**: 3 теста с реальным AI
- ✨ **AnalyzerEditModeTests.cs**: 10 тестов на Analyzer роль

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.5.0**

---

## [0.4.0] - 2026-04-05

### Tool Calling Support

- ✨ **LlmUnityLlmClient.SetTools()**: Реализация tool calling для LLMUnity
- ✨ **Tools Injection into System Prompt**: Tools добавляются в system prompt модели
- ✨ **OpenAiChatLlmClient Tools Support**: Поддержка tools в OpenAI API (tools array)

### Architecture

- Единый интерфейс **ILlmClient** работает с:
  - **OpenAI API** (CoreAI) - tools в JSON body
  - **LLMUnity** (CoreAIUnity) - tools в system prompt
- **CoreAILifetimeScope** регистрирует клиенты с tool support

### Tests

- ✨ Обновлены тесты для проверки tool calling
- PlayMode тесты для LLMUnity с memory tool

---

## [0.3.0] - 2026-04-04

### MEAI Integration

- Обновлён для работы с **Microsoft.Extensions.AI** function calling
- Все системные промпты агентов используют MEAI format
- Тесты обновлены для проверки MEAI pipeline

### Tests

- ✨ **MemoryToolMeaiEditModeTests.cs**: 8 MEAI integration тестов
- ✅ Все PlayMode тесты обновлены для JSON/MEAI формата
- ✅ Удалены устаревшие тесты AgentToolCallParser
- **+50 тестов** общей сложности для MEAI coverage

### Documentation

- **AI_AGENT_ROLES.md**: Обновлены роли с MEAI integration
- Новые гайды по MEAI function calling

## [0.2.0] - 2026-04-04

### Структура

- Исходники **CoreAI.Source** находятся в **`Assets/CoreAiUnity/Runtime/Source/`** (раньше — под `Packages/com.nexoider.coreai/Runtime/Source/`). Зависимости UPM этого пакета: **MessagePipe**, **MessagePipe.VContainer**, **UniTask**, **LLMUnity** (плюс транзитивно **`com.nexoider.coreai`**).

### Логирование (обязательный блок релиза)

- **Editor:** сообщения меню и setup сосредоточены в **`CoreAIEditorLog`** (единая точка `Debug.*` в Editor-слое пакета).
- **Тесты:** хранилища версий и LLM-хелперы используют **`NullGameLogger`** или **`GameLoggerUnscopedFallback`**, без прямого **`Debug.Log`** в тестовой логике ядра.

### Прочее

- Версия синхронизирована с **`com.nexoider.coreai` 0.1.3** (зависимость в `package.json`).

## [0.1.2] - ранее

Базовая линия хоста. См. историю git.
