# Changelog — `com.nexoider.coreai`

Все значимые изменения этого пакета описываются здесь. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

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
