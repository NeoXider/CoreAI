# Changelog

## [v0.21.2] — 2026-04-23

### Release sync

- 🔧 Версия синхронизирована с `com.nexoider.coreaiunity` **0.21.2** (изменения релиза — в Unity-слое: фикс фокуса `TextField` в `CoreAiChatPanel` после отправки сообщения). Изменений в портативном ядре нет.

## [v0.21.1] — 2026-04-23

### Release sync

- 🔧 Версия синхронизирована с `com.nexoider.coreaiunity` **0.21.1** (основные изменения релиза — в Unity-слое: UI чат/скроллбар, таймауты, тесты).

## [v0.21.0] — 2026-04-23

### Orchestrator streaming

- ✨ **`IAiOrchestrationService.RunStreamingAsync(AiTaskRequest, CancellationToken)`** — новый метод интерфейса (default-fallback в C# 8 default interface member: вызывает `RunTaskAsync` и выдаёт результат одним финальным чанком с `IsDone=true`).
- ✨ **`AiOrchestrator.RunStreamingAsync`** — реальная стриминговая реализация. Проходит тот же путь что `RunTaskAsync` (prompt composer, authority, memory, tools, structured validation), но отдаёт чанки по мере поступления и публикует `ApplyAiGameCommand` только после завершения стрима. Общий код сборки запроса вынесен в приватный `BuildRequest`.
- ✨ **Structured validation** в streaming-пути проверяется на полном накопленном тексте после окончания стрима. Если валидация провалилась — эмитится терминальный `LlmStreamChunk` с `Error = "structured validation failed: ..."` (повторить стрим автоматически нельзя, caller решает сам).
- 📚 **Контракт** методов `RunStreamingAsync` дополнен предупреждением: любая обёртка над `IAiOrchestrationService` (очередь, логирование, таймаут, авторити) обязана явно переопределять этот метод, иначе default-fallback тихо убьёт стриминг.

## [v0.20.3] — 2026-04-23

### Streaming pipeline — end-to-end visibility fix
- 🐛 **Критический баг: стриминг был не виден в UI.** `ILlmClient.CompleteStreamingAsync()` имеет default-реализацию в интерфейсе, которая делает fallback к `CompleteAsync()` и отдаёт весь ответ **одним** терминальным чанком после окончания генерации. Обёртки, не переопределявшие метод явно, «съедали» настоящий стрим. Исправлено в `CoreAiUnity` (см. соответствующий CHANGELOG).
- 📝 Документация `ILlmClient.CompleteStreamingAsync()` дополнена предупреждением о том, что любая декорирующая обёртка (логирование, роутинг, тайм-ауты) **обязана** явно переопределять этот метод, иначе default-fallback тихо убивает стриминг.

## [v0.20.2] — 2026-04-23

### Streaming Configuration
- ✨ **`ICoreAISettings.EnableStreaming`** — глобальный флаг включения стриминга ответов LLM (SSE для HTTP API, callback-очередь для LLMUnity). По умолчанию `true`.
- ✨ **`AgentBuilder.WithStreaming(bool)`** — per-agent override глобального флага. Позволяет одной роли принудительно работать в стриминговом режиме (напр. чатовая NPC), а другой — в non-streaming (напр. строгий JSON-парсер, tool-only агент).
- ✨ **`AgentMemoryPolicy.SetStreamingEnabled(roleId, bool?)`** и **`IsStreamingEnabled(roleId, fallback)`** — API для per-role хранения override и вычисления эффективного флага.
- ✨ **`AgentConfig.EnableStreaming`** (`bool?`) — nullable override, пробрасывается в политику через `ApplyToPolicy()`.
- 🔧 **Иерархия приоритетов** (от высшего к низшему): UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).

## [v0.20.1] — 2026-04-23

### Streaming Robustness

- ✨ **`ThinkBlockStreamFilter`** (`CoreAI.Ai`) — новый переиспользуемый stateful state-machine фильтр для корректного удаления `<think>...</think>` блоков из потока LLM. В отличие от старого regex-подхода, правильно обрабатывает ситуацию, когда открывающий или закрывающий тег разбит между чанками (типично для DeepSeek / Qwen).
  - `ProcessChunk(string)` — обработать чанк, вернуть только видимую часть.
  - `Flush()` — завершить стрим (вернуть остаточный «хвост», если модель оборвала ответ).
  - `Reset()` — переиспользование экземпляра.

### Streaming API
- 📝 **Контракт потока** уточнён: метод `ILlmClient.CompleteStreamingAsync()` гарантированно завершает стрим финальным чанком `IsDone=true` (даже если модель ничего не вернула). Вызывающий код может полагаться на это для закрытия UI.
- 📚 Документация `ILlmClient.CompleteStreamingAsync()` дополнена замечанием о том, что реализация должна вызываться на главном потоке Unity (из-за `UnityWebRequest`).

## [v0.20.0] — 2026-04-23

### Streaming API
- ✨ **`LlmStreamChunk`** — новый класс для потокового получения ответов от LLM. Содержит `Text`, `IsDone`, `Error` и usage-статистику.
- ✨ **`ILlmClient.CompleteStreamingAsync()`** — новый метод в интерфейсе, возвращает `IAsyncEnumerable<LlmStreamChunk>`. Дефолтная реализация делает fallback к `CompleteAsync()` и выдаёт результат одним чанком.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — реальный стриминг через `IChatClient.GetStreamingResponseAsync()` с автоматической фильтрацией `<think>` блоков.

### 3-Layer Prompt Architecture
- 🔧 **Исправлен баг**: `AgentBuilder.WithSystemPrompt()` ранее не регистрировал промпт в `IAgentSystemPromptProvider`, поэтому промпт из AgentBuilder игнорировался — AiOrchestrator всегда брал промпт из ManifestProvider.
- ✨ **3-слойная сборка системного промпта** в `AiPromptComposer.GetSystemPrompt()`:
  - **Слой 1**: `CoreAISettings.universalSystemPromptPrefix` — общие правила для ВСЕХ агентов
  - **Слой 2**: Базовый промпт из ManifestProvider / ResourcesProvider (.txt файлы)
  - **Слой 3**: Дополнительный промпт из `AgentBuilder.WithSystemPrompt()` (через `AgentMemoryPolicy`)
- 🔧 **`AgentBuilder.Build()`** — больше не приклеивает `universalPrefix` (перенесено в `AiPromptComposer`)
- 🔧 **`AgentConfig.ApplyToPolicy()`** — теперь регистрирует SystemPrompt через `policy.SetAdditionalSystemPrompt()`
- ✨ **`AgentMemoryPolicy.SetAdditionalSystemPrompt()` / `TryGetAdditionalSystemPrompt()`** — хранение доп. промптов из AgentBuilder
- ✨ **`AgentBuilder.WithOverrideUniversalPrefix()`** — отключение `universalPrefix` для конкретной роли (полезно для парсеров, валидаторов и ролей с полностью кастомным промптом)
- ✨ **`AgentMemoryPolicy.SetOverrideUniversalPrefix()` / `IsUniversalPrefixOverridden()`** — per-role контроль применения universalPrefix

### Breaking Changes
- **`AiPromptComposer` конструктор** — добавлены optional параметры `AgentMemoryPolicy` и `ICoreAISettings` (обратно-совместимо через `= null`)
- **`universalPrefix`** теперь применяется ко ВСЕМ ролям по умолчанию (отключается через `.WithOverrideUniversalPrefix()`)

## [v0.19.3] — 2026-04-22

### Prompt Optimization
- 🔧 **Убраны дублирующиеся tool-calling правила** из всех 7 встроенных промптов агентов (C# константы + `.txt` ресурсы). Экономия ~100-150 токенов на запрос — правила уже присутствуют в `UniversalSystemPromptPrefix`.
- 📝 **Улучшены формулировки промптов:** добавлены лимиты длины ответов для AiNpc (1-3 предложения) и PlayerChat (1-5 предложений).
- 🔧 **Native Tool Calling:** Удалены устаревшие инструкции по ручному JSON-форматированию инструментов из примеров `Agent.cs` и `AllToolCallsPlayModeTests.cs`. Агенты и тесты полностью переведены на использование нативного `MEAI` function calling.

### Editor UX
- ✨ **`CoreAI/Create Scene Setup`** — новая кнопка в меню Unity для быстрой настройки сцены:
  - Создаёт `CoreAILifetimeScope` на сцене с назначенными ассетами
  - Автоматически генерирует все default ассеты (Settings, LogSettings, PromptsManifest и др.)
  - Создаёт `LLM` + `LLMAgent` объекты при бэкенде LLMUnity (или Auto+LlmUnityFirst)
  - Защита от дублирования, поддержка Undo (Ctrl+Z)

### Stability
- 🐛 **HTTP timeout logging:** `MeaiOpenAiChatClient` — ошибки таймаута/сети понижены с `LogError` до `LogWarning`, чтобы не ломать PlayMode тесты в Unity Test Runner.
- 🐛 **PlayMode Tests:** Исправлено падение теста `AllToolCalls_MemoryTool_WriteAppendClear`, вызванное конфликтом текстовых JSON-промптов и нативного вызова инструментов.
- 🛡️ **UI Safety:** Добавлен `try-catch` блок в `async void OnSendClicked` (`InGameChatPanel.cs`) для предотвращения "тихих" падений UI при ошибках сети.

### Documentation
- 📚 **README обновлены** (EN + RU) — добавлена полная инструкция по установке зависимостей:
  - NuGet DLL (Microsoft.Extensions.AI и др.) — таблица с версиями
  - Git URL пакеты с описанием транзитивных зависимостей (VContainer, MoonSharp, LLMUnity, UniTask, MessagePipe)
  - Новые шаги: Create Scene Setup, настройка LLM бэкенда
- 🔗 **Исправление ссылок:** Исправлены битые относительные ссылки в `README_RU.md`, чтобы навигация по документации корректно работала на главной странице репозитория GitHub.

## [v0.19.2] — 2026-04-14

### Changed
- **AgentMemory:** Умное ограничение истории `ChatHistory` перед отправкой в LLM Client. Теперь история отсекается комбинированно: по количеству сообщений (`MaxChatHistoryMessages`, по умолчанию 30) и по примерному бюджету токенов (`ContextTokens / 2`). Защищает HTTP API от переполнения контекста и огромных чеков, оставляя старые диалоги сохраненными в JSON.
- **AgentBuilder:** добавлен опциональный параметр `maxChatHistoryMessages` в метод `.WithChatHistory()`.

## [v0.19.1] — 2026-04-14

### Fixes & Stability
- 🐛 **Защита от дублирования Tool Calls:** Разъяснены механизмы сброса счётчиков неудачных вызовов `MeaiLlmClient` внутри сессии. Локальность `executedSignatures` позволяет полностью изолировать каждый запрос.
- 🔧 **Тестовое окружение `Agent.cs`:** 
  - Тестовые фразы выведены в Inspector `[TextArea]` для изменения сценария "на лету" и предотвращения искусственного зацикливания LLM на идентичных промптах.
  - Добавлен метод `ClearMemory()` для преднамеренной очистки истории (позволяет сбросить контекст бота между нажатиями кнопок, чтобы модель не опиралась на предыдущие ошибки).
- 📝 **Документация:** Уточнена работа `SceneLlmAgentProvider` в связке с `DontDestroyOnLoad` — требуется явное наличие компонента `LLMAgent` или регистрация имени агента.

## [v0.19.0] — 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — проверка совместимости ингредиентов для CoreMechanicAI
  - Поддержка правил на произвольное количество элементов (пары, тройки, четвёрки и более)
  - `CompatibilityRule.Pair()` и `CompatibilityRule.Group()` фабричные методы
  - Группы элементов (IronOre → Metal, WaterFlask → Water) с автоматическим разрешением
  - Кастомные валидаторы (`ICompatibilityValidator`) для игровой логики
  - Взвешенное среднее: правила с большим количеством элементов имеют приоритет
- ✨ **`CompatibilityLlmTool`** — ILlmTool обёртка для function calling (LLM может проверять совместимость перед крафтом)
- ✨ **`JsonSchemaValidator`** — валидация JSON-ответов от LLM без внешних зависимостей
  - Проверка обязательных полей, типов (string, number, integer, boolean, array, object)
  - Числовые диапазоны (min/max) и enum-значения
  - Автоматическая очистка markdown fences (`` `json...` ``)
  - `ToPromptDescription()` — генерация описания схемы для system prompt
- 🧪 **45+ EditMode тестов** для CompatibilityChecker, JsonSchemaValidator и CompatibilityLlmTool

## [v0.18.0] — 2026-04-10

### Architecture — DI Migration

- 🔧 **`CoreAISettings` → static proxy** — больше не хранит значения как независимые поля. Теперь делегирует чтение в `ICoreAISettings Instance` (DI-зарегистрированный экземпляр).
  - Поддержка прямой записи сохранена для обратной совместимости (override prevails over Instance).
  - Добавлен `CoreAISettings.ResetOverrides()` для очистки overrides в тестах.
- 🔧 **`LuaAiEnvelopeProcessor`** — принимает `ICoreAISettings` через конструктор (optional param). Больше не читает `CoreAISettings.MaxLuaRepairRetries` при инициализации.
- ❌ **Удалён** `SyncToStaticSettings()` полностью — заменён одной строкой `CoreAISettings.Instance = settings`.

## [v0.16.0] — 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — новый инструмент для Runtime инспекции сцены. Позволяет LLM:
  - `find_objects` (поиск GameObject по имени/тегу).
  - `get_hierarchy` (получение дочерних элементов).
  - `get_transform` и `set_transform` (манипуляции позицией, вращением, скейлом).
- ✨ **`CameraLlmTool`** — инструмент зрения, позволяющий модели делать скриншоты в PlayMode (`capture_camera`) с возвратом `dataUri` (Base64 JPEG). Идеально для мультимодальных LLM (LLaVA/gpt-4o).
- 🛠 **Многопоточность** — оба инструмента безопасно оборачивают вызовы к Unity API в `UniTask.SwitchToMainThread()`, предотвращая краши от фоновых потоков MEAI.
- 🛠 **Автоматизация `CoreAiPrefabRegistryAsset`** — добавлен `OnValidate`, который автоматически проставляет `Key` (на основе AssetDatabase GUID) и стягивает `Name` при добавлении префаба в инспекторе.

## [v0.15.0] — 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON Extraction** — полностью переписан механизм парсинга tool calls в `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Старое хрупкое Regex вырезано; заменено на гибкий алгоритм поиска фигурных скобок (`IndexOf('{')`). Теперь модели могут забывать закрывающие бэктики (\`\`\`) или вставлять скобки в текстовые аргументы без поломки парсера. Тесты PlayMode (`MemoryTool_AppendsMemory`) успешно пройдены.
- ⚙️ **Reasoning Mode Stripping** — добавлен препроцессинг ответов: парсинг tool calls теперь предварительно вырезает всю цепочку рассуждений `<think>...</think>`, предотвращая сбой JSON-парсера при "думанье" вслух (DeepSeek).

### Editor UX
- ✨ **Auto-Plugin Loading** — встроен механизм `[InitializeOnLoadMethod]` в `CoreAIBuildMenu`. При старте проекта или импорте плагина он автоматически генерирует полный набор необходимых `ScriptableObject` (`CoreAiSettingsAsset`, манифесты роутинга, пермишены) в `Settings/` и `Resources/`.
- ✨ **Quick Settings Menu** — добавлено удобное меню **CoreAI → Settings** для быстрого доступа к глобальному синглтону `CoreAISettings.asset`.

## [v0.14.0] — 2026-04-09
### Agent Memory & Persistence
- ✨ **Persistent Chat History** — полная история диалога (контекст агентов) теперь сохраняется между игровыми сессиями.
  - `WithChatHistory(persistToDisk: true)` в `AgentBuilder` (или `RoleMemoryConfig`) — включение сохранения на диск.
  - Файлы сохраняются в `Application.persistentDataPath/CoreAI/AgentMemory/`.
  - Автоматическое восстановление контекста при перезапуске (загрузка из JSON в оркестраторе).
  - Эфемерный fallback для сессий без записи на диск.
- 🧪 PlayMode тесты (`ChatHistoryPlayModeTests`) для верификации восстановления контекста после "рестарта" сцены/движка.

## [v0.13.0] — 2026-04-09
### Action / Event System
- ✨ **`DelegateLlmTool`** — новый универсальный класс (наследник `ILlmTool`), позволяющий прокинуть любой C# делегат (Action, Func) напрямую в LLM через MEAI. Автоматическая генерация JSON-схемы из сигнатуры метода.
- ✨ **`CoreAiEvents`** — встроенная сверхлегкая статическая шина событий (pub-sub) для связи агентов с игровой логикой без сторонних зависимостей.
- ✨ **Extensions в `AgentBuilder`**:
  - `WithAction(name, description, delegate)` — для передачи метода напрямую агенту.
  - `WithEventTool(name, description, hasStringPayload)` — для создания триггеров в общую шину `CoreAiEvents`.
- 🧪 Написаны EditorMode тесты (`CoreAiEventsEditModeTests`).

## [v0.12.0] — 2026-04-08

### Architecture
- **Единый логгер `ILog`** — рефакторинг из «двух логгеров» в один
  - `ILog` расширен: добавлены `Debug(msg, tag)`, `Info(msg, tag)`, `Warn(msg, tag)`, `Error(msg, tag)`
  - `LogTag` constants — строковые теги подсистем (`Core`, `Llm`, `Lua`, `Memory`, `Config`, `World`, `Metrics`, `Composition`, `MessagePipe`)
  - `Log.Instance` (статический) + DI-инъекция через VContainer — оба способа доступа работают
  - `NullLog` — no-op реализация по умолчанию для тестов и до инициализации DI
- **Унификация `MemoryToolAction`** — устранено дублирование enum
  - `MemoryToolAction` вынесен в отдельный файл `MemoryToolAction.cs`
  - Удалены дубликаты из `AgentBuilder.cs` и `AgentMemoryPolicy.cs`
  - Исправлена работа `AgentBuilder.WithMemory(defaultAction)` — теперь настройка корректно применяется к агенту

### Changed
- **Все Tool-классы Core** мигрированы на `ILog` с тегами:
  - `MemoryTool` → `LogTag.Memory`
  - `LuaTool` → `LogTag.Lua`
  - `GameConfigTool` → `LogTag.Config`
  - `InventoryTool` → `LogTag.Llm`
- `CoreAIGameEntryPoint` — мигрирован с `IGameLogger` на `ILog`
- `CoreServicesInstaller` — регистрирует `ILog` (через `UnityLog`) + устанавливает `Log.Instance`
- `GameLoggerUnscopedFallback` — автоматический fallback для `Log.Instance` до инициализации DI
- Удалена ручная установка `Log.Instance` из `CoreAILifetimeScope` (перенесена в `CoreServicesInstaller`)

### Unity Implementation
- `UnityLog` — реализация `ILog`, маппит `LogTag` строки на `GameLogFeature` flags
- `IGameLogger` сохранён как внутренний контракт Unity-слоя (для `FilteringGameLogger`, `GameLogSettingsAsset`)
- Фильтрация по тегам через `GameLogSettingsAsset` в Inspector (как раньше)

## [v0.11.0] — 2026-04-07

### Added
- **Universal System Prompt Prefix** — универсальный стартовый промпт для всех агентов
  - `CoreAISettings.UniversalSystemPromptPrefix` — статическое свойство для программного задания
  - Добавляется в **НАЧАЛО** системного промпта каждого агента (встроенного и кастомного)
  - Позволяет задать общие правила для всех моделей без дублирования
  - `BuiltInAgentSystemPromptTexts.WithUniversalPrefix()` — вспомогательный метод
  - `BuiltInDefaultAgentSystemPromptProvider` — автоматически применяет префикс
  - `AgentBuilder.Build()` — автоматически применяет префикс к кастомным агентам
- **Общая температура генерации** — `CoreAISettings.Temperature` (по умолчанию **0.1**)
  - Применяется ко всем агентам и обоим бэкендам (LLMUnity и HTTP API)
- **AgentBuilder.WithTemperature(float)** — переопределить температуру для конкретного агента
  - `AgentConfig.Temperature` — свойство конфигурации
  - По умолчанию берётся из `CoreAISettings.Temperature`
- **MaxToolCallIterations** — вынесен из хардкода в настройки (`CoreAISettings.MaxToolCallIterations`, default 2)
  - Управляет сколько раз модель может вызвать инструменты за один запрос
  - `MeaiLlmClient` теперь читает значение из `CoreAISettings`

## [v0.10.0] — 2026-04-06

### Added
- **WorldCommand как MEAI tool call** — LLM может управлять миром через function calling
  - `IWorldCommandExecutor` — абстрактный интерфейс в **CoreAI** (движок-независимый)
  - `WorldTool.cs` — AIFunction для MEAI function calling (в CoreAiUnity)
  - `WorldLlmTool.cs` — ILlmTool обёртка (в CoreAiUnity)
  - Поддерживаемые actions: `spawn`, `move`, `destroy`, `load_scene`, `reload_scene`, `bind_by_name`, `set_active`, `play_animation`, `show_text`, `apply_force`, `spawn_particles`, `list_objects`
- **`list_objects` action** — получить список всех объектов в иерархии сцены
  - Возвращает имя, позицию, активность, тег, слой, количество детей
  - Поддержка поиска по имени (search pattern)
- **`play_animation` action** — проиграть анимацию на объекте (поддержка Animator и Animation)
  - Использует `Animator.runtimeAnimatorController.animationClips` для получения списка анимаций
  - Поддержка Animator (Mecanim) и Legacy Animation компонентов
- **`list_animations` action** — получить список доступных анимаций объекта
  - Возвращает все AnimationClips из AnimatorController
  - Поиск объекта по `instanceId` или `targetName`
- **`targetName` для всех commands** — работа с объектами по имени (альтернатива instanceId)
  - move, destroy, set_active, play_animation, apply_force, spawn_particles
  - Сначала ищет в _instances по instanceId, затем GameObject.Find по targetName
- `WorldToolEditModeTests.cs` — EditMode тесты для WorldTool
- `WorldCommandPlayModeTests.cs` — PlayMode тесты для world command tool calling
- **Debug logging в CoreAISettingsAsset** — настраиваемое логирование через Inspector
  - `LogLlmInput` — логирует входящие промпты (system, user) и инструменты
  - `LogLlmOutput` — логирует исходящие ответы модели и результаты tool calls
  - `EnableHttpDebugLogging` — логирует сырые HTTP request/response JSON
- `tool_call_id` в tool messages для LM Studio совместимости
- Идемпотентность в `MemoryTool.append` — защита от дублирования при зацикливании модели

### Changed
- `MeaiOpenAiChatClient` — правильное извлечение tool results из `msg.Contents`
- `MemoryTool.ExecuteAsync` — возвращает JSON строку вместо объекта для корректной сериализации
- `TestAgentSetup` — добавлен `WorldExecutor` для PlayMode тестов
- Убраны `LogAssert.Expect` для ошибок подключения из PlayMode тестов (ошибки появляются только при недоступном хосте)

### Fixed
- Tool results не отправлялись модели (пустой `[tool]` content) — исправлено извлечение из `Contents` коллекции
- LM Studio 400 Bad Request — добавлен обязательный `tool_call_id` для tool messages
- Memory append добавлял значение 3 раза — добавлена идемпотентность
- Write test не проходил — исправлен hint чтобы не сбивать модель с толку

---

## [v0.9.0] — 2026-04-06

### Added
- `MeaiLlmClient` — единый MEAI-клиент для всех бэкендов
  - `MeaiLlmClient.CreateHttp(settings, logger, memoryStore)` — HTTP API
  - `MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)` — локальная GGUF
- `MeaiOpenAiChatClient` — MEAI IChatClient для HTTP API
- `LlmUnityMeaiChatClient` — MEAI IChatClient для LLMUnity (вынесен отдельно)
- `OfflineLlmClient` — кастомный ответ вместо заглушки по ролям
- `CoreAISettings.ContextWindowTokens` — размер контекста по умолчанию (8192)
- `AgentBuilder.WithChatHistory(int?)` — контекст из настроек или переопределение
- `AgentConfig.ContextWindowTokens` и `AgentConfig.WithChatHistory`
- `CoreAISettingsAsset.AutoPriority` — LlmUnityFirst или HttpFirst
- Кнопка **🔗 Test Connection** в Inspector
- `Docs/MEAI_TOOL_CALLING.md` — документация архитектуры

### Changed
- `MeaiLlmUnityClient` — упрощён до фабрики, делегирует в `MeaiLlmClient`
- `OpenAiChatLlmClient` — упрощён до фабрики, делегирует в `MeaiLlmClient`
- Все PlayMode тесты используют `CoreAISettingsAsset` через фабрику
- `LlmBackendType.Stub` → `LlmBackendType.Offline`
- Документация `AGENT_BUILDER.md` — обновлена с примерами создания клиентов
- Удалены: `MEAI_FUNCTION_CALLING.md`, `README_MEAI.md` (дубликаты)

### Architecture
- Одинаковый MEAI pipeline для обоих бэкендов
- `FunctionInvokingChatClient` → автоматический tool calling
- Больше нет ручного парсинга tool calls из текста

---

## [v0.8.0] — 2026-04-06

### Added
- `CoreAISettingsAsset` — единый ScriptableObject-синглтон
- `IOpenAiHttpSettings` — интерфейс для адаптации настроек
- `OpenAiChatLlmClient(CoreAISettingsAsset)` — конструктор
- `CoreAISettingsAssetEditor` — кастомный Inspector
- `CoreAISettings.asset` — asset по умолчанию в Resources
- LLMUnity настройки: `DontDestroyOnLoad`, `StartupTimeout`, `KeepAlive`
- Auto Priority: LlmUnityFirst / HttpFirst

---

## [v0.7.0] — 2026-04-06

### Added
- Единый MEAI Tool Calling Format
- `LuaTool.cs` + `LuaLlmTool.cs`
- `InventoryTool.cs` + `InventoryLlmTool.cs`
- `CoreAISettings.cs` (static)
- `AgentBuilder` — конструктор кастомных агентов
- `WithChatHistory()` — сохранение истории диалога
- `WithMemory()` — персистентная память
- `AgentMode` — ToolsOnly, ToolsAndChat, ChatOnly
- Merchant NPC с инструментами

### Removed
- `AgentMemoryDirectiveParser` — всё через MEAI pipeline
