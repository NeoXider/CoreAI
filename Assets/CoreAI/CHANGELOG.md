# Changelog — `com.nexoider.coreai`

Все значимые изменения этого пакета описываются здесь. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

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
