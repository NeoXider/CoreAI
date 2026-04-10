# Changelog

Все значимые изменения проекта CoreAI.

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
