# Changelog — `com.nexoider.coreai`

Все значимые изменения этого пакета описываются здесь. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

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
- ✨ **CoreAISettings**: Публичные статические настройки (MaxLuaRepairGenerations=3, MaxToolCallRetries=3, LlmRequestTimeoutSeconds, EnableMeaiDebugLogging)
- 🔧 **Единый формат tool calls**: `{"name": "tool_name", "arguments": {...}}`
- 🔧 **LlmUnityMeaiChatClient.TryParseToolCallFromText**: Парсинг JSON tool calls для моделей без структурных tool_calls (поддержка Qwen-style форматов)
- 🔧 **AgentMemoryPolicy.SetToolsForRole()**: Добавление кастомных инструментов к роли
- 🔧 **ProgrammerResponsePolicy упрощена**: Больше не проверяет fenced блоки

#### Удалено
- ❌ **AgentMemoryDirectiveParser**: Удалён - всё через MEAI pipeline
- ❌ **Fallback парсинг в AiOrchestrator**: Memory tool работает через FunctionInvokingChatClient
- ❌ **Валидация fenced Lua блоков в ProgrammerResponsePolicy**: Programmer вызывает execute_lua tool
- ❌ **Fenced блоки** (```memory, ```lua): Не используются для tool calls

#### Breaking Changes
- **Programmer агент** теперь вызывает `execute_lua` tool вместо fenced ```lua блоков
- **Memory tool** формат: `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- **MaxLuaRepairGenerations** изменён с 4 на 3

#### Удалённые тесты
- ❌ `AgentMemoryEditModeTests.cs` - использовал удалённый AgentMemoryDirectiveParser
- ❌ `AgentDataPassingEditModeTests.cs` - использовал старые memory парсинги
- ❌ `MemoryToolMeaiEditModeTests.cs` - дублировал MeaiToolCallsEditModeTests.cs

#### Обновлённые тесты
- 🔧 `MeaiToolCallsEditModeTests.cs` - MemoryTool, LuaTool, парсинг JSON
- 🔧 `LuaExecutionPipelineEditModeTests.cs` - обновлено ожидаемое количество повторов (4→3)
- 🔧 `RoleStructuredResponsePolicyEditModeTests.cs` - Programmer теперь пропускает любой текст
- 🔧 Все PlayMode тесты обновлены под единый формат tool calls v0.7.0

#### Новые тесты
- ✨ `AgentBuilderEditModeTests.cs` - 8 тестов на конструктор агентов
- ✨ `CustomAgentsPlayModeTests.cs` - 3 теста на кастомных агентов (Merchant, Analyzer, Storyteller)

---

## [0.6.1] - 2026-04-06

### Tool Calling Fallback для LLM без структурных tool_calls

- 🔧 **LlmUnityMeaiChatClient.TryParseToolCallFromText**: Добавлен fallback парсинг JSON tool calls из текста ответа модели
- 🔧 **Поддержка Qwen3.5-2B и подобных моделей**: Модель возвращает tool call как JSON текст в content, а не как структурный tool_call — теперь это распознаётся и преобразуется в `FunctionCallContent` для MEAI `FunctionInvokingChatClient`
- 🔧 **Распознаваемые форматы**: 
  - `{"tool": "memory", "action": "write", "content": "..."}`
  - `{"name": "memory", "arguments": {...}}`
  - ```json\n{...}\n``` fenced blocks

### Fixes

- ✅ **Memory Tool теперь работает с Qwen3.5-2B**: `FunctionInvokingChatClient` распознаёт tool call и вызывает `MemoryTool.ExecuteAsync()`
- ✅ **Память сохраняется между вызовами**: Craft 2 видит память из Craft 1
- ✅ **AgentMemoryDirectiveParser как двойной fallback**: Если MEAI не распознал tool call, AiOrchestrator использует AgentMemoryDirectiveParser.TryExtract()

---

## [0.6.0] - 2026-04-05

### Microsoft.Extensions.AI Full Integration (MAJOR)

- ✨ **MeaiLlmUnityClient**: Полная интеграция с Microsoft.Extensions.AI для LLMUnity
- ✨ **FunctionInvokingChatClient**: Использует MEAI `FunctionInvokingChatClient` для автоматического tool calling
- ✨ **MemoryTool AIFunction**: MemoryTool.CreateAIFunction() использует AIFunctionFactory.Create() для MEAI
- ✨ **Автоматический вызов функций**: MEAI автоматически парсит tool_calls и выполняет AIFunction
- ✨ **IChatClient реализация**: Внутренний IChatClient обёртка над LLMAgent

### Removed

- ❌ **MeaiChatClientAdapter**: Удалён — заменён на полноценный MEAI pipeline
- ❌ **Ручной парсинг tool_calls**: Удалён из основного flow — теперь через MEAI
- ❌ **MeaiToolsLlmClientDecorator**: Удалён — не нужен при использовании FunctionInvokingChatClient

### AgentMemoryDirectiveParser Fallback

- AgentMemoryDirectiveParser.TryExtract() теперь используется как fallback
- Поддерживает: ```memory блоки и ```json блоки с "tool": "memory"
- Работает когда модель не использует формальный tool_calls

### Breaking Changes

- LlmUnityLlmClient заменён на MeaiLlmUnityClient
- Все ссылки на LlmUnityLlmClient обновлены на MeaiLlmUnityClient

---

## [0.5.0] - 2026-04-05

### LLM Response Validation (NEW)

- ✨ **ProgrammerResponsePolicy**: Валидация ответов Programmer — требует Lua код или JSON с execute_lua
- ✨ **CoreMechanicResponsePolicy**: Валидация JSON с числами для CoreMechanicAI
- ✨ **CreatorResponsePolicy**: Валидация JSON объектов для Creator
- ✨ **AnalyzerResponsePolicy**: Валидация JSON с метриками для Analyzer
- ✨ **AINpcResponsePolicy**: Мягкая валидация (JSON или текст) для AINpc
- ✨ **PlayerChatResponsePolicy**: Без валидации для PlayerChat (свободный текст)
- ✨ **CompositeRoleStructuredResponsePolicy**: Маршрутизация валидации по roleId
- ✨ **Автоматический retry**: При неудачной валидации AiOrchestrator делает повторный запрос с подсказкой
- ✨ **20 EditMode тестов**: Comprehensive test coverage для всех политик

### GameConfig Infrastructure (NEW)

- ✨ **IGameConfigStore**: Универсальный интерфейс загрузки/сохранения JSON конфигов по ключу
- ✨ **GameConfigTool**: ILlmTool для AI function calling (read/update configs)
- ✨ **GameConfigPolicy**: Контроль доступа — какие роли могут читать/менять какие ключи
- ✨ **GameConfigLlmTool**: Обёртка для MEAI function calling
- ✨ **NullGameConfigStore**: Заглушка по умолчанию
- ✨ **DI регистрация**: В CorePortableInstaller

### Architecture

- `IRoleStructuredResponsePolicy` теперь использует специализированные политики вместо NoOp
- `AiOrchestrator` уже поддерживает retry при провале валидации (было заложено ранее)
- GameConfig полностью game-agnostic — игра реализует `IGameConfigStore` для своей системы хранения
- Обратная совместимость: `NoOpRoleStructuredResponsePolicy` и `NullGameConfigStore` остаются для кастомных ролей

### Breaking Changes

- Нет. Обратная совместимость сохранена.

---

## [0.4.0] - 2026-04-05

### Tool Calling Infrastructure (MAJOR)

- ✨ **ILlmTool Interface**: Новый интерфейс для инструментов LLM в CoreAI (portable)
- ✨ **LlmToolBase**: Базовый класс для простых инструментов с JSON schema
- ✨ **MemoryLlmTool**: ILlmTool реализация для memory tool
- ✨ **ILlmClient.SetTools()**: Новый метод для установки tools на LLM клиенте
- ✨ **LlmCompletionRequest.Tools**: Новое поле для передачи tools в запросе
- ✨ **OpenAiChatLlmClient Tools Support**: Поддержка tools в OpenAI JSON body (tools array)
- ✨ **AgentMemoryPolicy.GetToolsForRole()**: Возвращает MemoryTool для роли
- ✨ **AiOrchestrator Tools Integration**: Автоматически передаёт tools в LLM клиент

### MeaiToolsLlmClientDecorator (NEW)

- ✨ **MeaiToolsLlmClientDecorator**: Декоратор для ILlmClient с автоматическим инжектированием tools в system prompt
- ✨ **Qwen3-compatible format**: Tools описание в формате, понятном Qwen3 для лучшего tool calling
- ✨ **Universal**: Работает с любым LLM бэкендом (OpenAI HTTP, LLMUnity)

### CoreAIUnity (LLMUnity Integration)

- ✨ **LlmUnityLlmClient.SetTools()**: Реализация для LLMUnity
- ✨ **Tools Injection into System Prompt**: Tools добавляются в system prompt для LLMUnity

### Architecture

- **CoreAI (Portable)**: OpenAiChatLlmClient, ILlmTool, AgentMemoryPolicy с tools
- **CoreAIUnity**: LlmUnityLlmClient с tool calling поддержкой
- Единый интерфейс ILlmClient работает с обоими бэкендами

### Breaking Changes

- ✅ ILlmClient получил новый virtual метод SetTools() - обратная совместимость сохранена

---

## [0.3.0] - 2026-04-04

### Microsoft.Extensions.AI Integration (MAJOR)

- ✨ **MEAI Function Calling**: Полная интеграция Microsoft.Extensions.AI для стандартизированного вызова функций
- ✨ **MemoryTool**: Новый класс с `AIFunctionFactory.Create()` для регистрации memory tool
- ✨ **MeaiChatClientAdapter**: Адаптер `IChatClient` над `ILlmClient` для MEAI pipeline
- ✨ **AiOrchestrator**: Обновлён с MEAI pipeline + fallback на legacy режим
- ✨ **Automatic Tool Invocation**: MEAI автоматически вызывает tools из ответа модели
- ✨ **Per-Role Memory Tools**: Отдельный instance MemoryTool для каждой роли

### Breaking Changes

- ❌ **Удалён AgentToolCallParser**: Больше не нужен, MEAI заменяет
- ❌ **Удалён legacy формат**: `[TOOL:memory]...[/TOOL]` больше не поддерживается
- ✅ **Только JSON формат**: `{"tool": "memory", "action": "write", "content": "..."}`

### System Prompts

- **Creator**: Обновлён для MEAI function calling format
- **Programmer**: Обновлён для MEAI JSON format
- Все агенты используют единый MEAI формат

### Dependencies

- ✅ **Microsoft.Extensions.AI** v10.4.1 (через NuGet)
- ✅ **Microsoft.Extensions.AI.Abstractions** v10.4.1 (через NuGet)
- MEAI уже был установлен в `packages.config`

### Documentation

- ✨ **MEAI_FUNCTION_CALLING.md**: Полный гайд по MEAI integration
- ✨ **README_MEAI.md**: Быстрый справочник
- **AI_AGENT_ROLES.md**: Обновлены роли с MEAI notes
- **README.md**: Updated package description

### Tests

- ✨ **MemoryToolMeaiEditModeTests.cs**: 8 новых MEAI integration тестов
- ✅ Все старые тесты обновлены для JSON/MEAI формата
- ✅ Удалены устаревшие тесты AgentToolCallParser

## [0.2.0] - 2026-04-04

### Логирование (обязательный блок релиза)

- Рантайм **CoreAI.Source** переведён на **`IGameLogger`** с категориями **`GameLogFeature`** и фильтрацией через **`IGameLogSettings`** / **`GameLogSettingsAsset`** (уровни + маска фич). Прямой **`UnityEngine.Debug.Log` / `LogWarning` / `LogError`** в прикладном коде ядра убран.
- Единственная точка вывода в Unity Console в рантайме — **`UnityGameLogSink`** (обёртка над `Debug.*`); остальной код пишет только через абстракцию.
- Для раннего **Awake** без VContainer добавлен **`GameLoggerUnscopedFallback`** (тот же путь: фильтр → sink).
- Файловые **`FileLuaScriptVersionStore`** / **`FileDataOverlayVersionStore`**, реестр LLM, bootstrap LLMUnity, **`AiScheduledTaskTrigger`** и связанные места принимают **`IGameLogger`** и логируют с осмысленными **`GameLogFeature`** (например **Llm**, **Core**, **Composition**).
- Тесты могут подставлять **`NullGameLogger`**.

### Прочее

- Мелкие правки композиции DI (**`CoreAILifetimeScope`**, **`LlmClientRegistry`**) под передачу логгера.

### Разделение пакетов (только `CoreAI.Core` в `com.nexoider.coreai`)

- Пакет **`com.nexoider.coreai`** содержит **только** `Assets/CoreAI/Runtime/Core/` — сборка **CoreAI.Core** без `UnityEngine`. Зависимости UPM: **VContainer**, **MoonSharp**.
- Сборка **CoreAI.Source** (Unity) перенесена в **`com.nexoider.coreaiunity`** → `Assets/CoreAiUnity/Runtime/Source/`.

## [0.1.2] - ранее

Базовая линия: CoreAI.Core + CoreAI.Source в одном пакете. См. историю git для деталей.
