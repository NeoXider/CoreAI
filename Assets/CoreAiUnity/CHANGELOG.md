# Changelog — `com.nexoider.coreaiunity`

Хост Unity: сборка **CoreAI.Source**, тесты (EditMode / PlayMode), Editor-меню, документация. Зависит от **`com.nexoider.coreai`**.

## [0.21.2] - 2026-04-23

### 💬 Chat UI — input focus

- 🐛 **Фокус возвращается в текстовое поле после отправки.** `CoreAiChatPanel.TrySendInput` / `SendToAI.finally` теперь фокусят именно внутренний `unity-text-input` (а не внешний композит `TextField`). Ранее после 1-го отправленного сообщения в multi-line поле фокус «висел» на внешнем `TextField` и клавиатурный ввод не уходил в редактор — печатать можно было только повторным кликом в поле.
- ✨ **`CoreAiChatPanel.FocusInputField()`** — приватный хелпер, инкапсулирует `TextField.textInputUssName` lookup. Используется и при очистке поля после отправки, и в `finally` SendToAI (чтобы после завершения AI-ответа можно было сразу продолжать печатать).

## [0.21.1] - 2026-04-23

### 💬 Chat UI polish & layout stability

- 💅 **ScrollView layout**: фикс overlap/сжатия — `ScrollView` корректно shrink'ится в колонке (min-height: 0), header/input/typing больше не уменьшаются при огромном контенте (`flex-shrink: 0`).
- 💅 **Scrollbar theming (UI Toolkit / Unity 6)**: явные стили для `Scroller`-частей (`.unity-scroll-view__vertical-scroller`, `.unity-scroller__tracker`, `.unity-scroller__dragger`), скрыты arrow buttons (`.unity-scroller__low-button/.unity-scroller__high-button`) — дефолтный «белый» бар больше не пробивается.
- 💅 **InputField readability**: усилены селекторы для внутренних классов `TextField` в разных версиях Unity, выставлены caret/selection colors — ввод игрока стабильно виден на тёмной теме.
- 🔧 **Scroll bottom padding**: последний bubble не прячется под typing/input.

### ⏱️ Timeouts

- ⏱️ **CoreAISettingsAsset.LlmRequestTimeoutSeconds**: дефолт увеличен с **15s → 120s** (стриминг/tool-calling на локальных/медленных моделях часто требует больше времени).

### 🧪 Tests (PlayMode)

- 🧪 **CraftingMemoryViaLlmUnityPlayModeTests**: тест больше не падает, если бэкенд выполнил tool calls, но не прислал финальный `AiEnvelope` — имя предмета восстанавливается из memory (контракт промпта).
- 🧪 **CraftingMemoryViaOpenAiPlayModeTests**: determinism-check теперь не просто логируется — добавлен `Assert` (craft #4 обязан повторить craft #2). Память для промпта приведена к каноническому формату `Craft #N - Name made from X + Y`, чтобы модель могла повторить результат по ингредиентам.
- 🧪 **CraftingMemoryItemNameExtractor**: устойчивее к вольному тексту модели (кавычки, “crafted with quality”, жирный markdown и т.п.).

## [0.21.0] - 2026-04-23

### 🎯 `CoreAi` singleton — unified one-line entry point

Раньше чтобы вызвать LLM из игрового кода надо было либо знать VContainer (`container.Resolve<CoreAiChatService>()`), либо городить свой singleton, либо использовать `CoreAiChatService.TryCreateFromScene()` каждый раз. Теперь — один статический класс, который умеет всё.

- ✨ **`CoreAI.CoreAi`** (static facade, `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs`) — ленивый потоко-безопасный синглтон, авторезолв из первой `CoreAILifetimeScope` на сцене.
  - `CoreAi.TryGetChatService(out CoreAiChatService?)` / `CoreAi.TryGetOrchestrator(out IAiOrchestrationService?)` — **без исключений**, удобно для кнопок в UI и опционального AI (в отличие от `Get*` которые бросают `InvalidOperationException`).
  - `CoreAi.AskAsync(message, roleId, ct)` → `Task<string>` — простой синхронный чат.
  - `CoreAi.StreamAsync(message, roleId, ct)` → `IAsyncEnumerable<string>` — стриминг строковых чанков.
  - `CoreAi.StreamChunksAsync(message, roleId, ct)` → `IAsyncEnumerable<LlmStreamChunk>` — стриминг с метаданными (`IsDone`, `Error`, usage).
  - `CoreAi.SmartAskAsync(message, roleId, onChunk, uiStreamingOverride, ct)` → сам решает стрим/sync по иерархии флагов, возвращает полный текст, попутно вызывает `onChunk` на каждый фрагмент.
  - `CoreAi.OrchestrateAsync(AiTaskRequest, ct)` → полный пайплайн оркестратора (snapshot → prompt composer → authority → очередь → structured policy → publish `ApplyAiGameCommand` → метрики).
  - `CoreAi.OrchestrateStreamAsync(AiTaskRequest, ct)` → стриминговый вариант с тем же пайплайном.
  - `CoreAi.OrchestrateStreamCollectAsync(task, onChunk, ct)` → стриминг + накопление полного текста + `onChunk`, возвращает `string`.
  - `CoreAi.IsReady` / `CoreAi.Invalidate()` / `CoreAi.GetChatService()` / `CoreAi.GetOrchestrator()` / `CoreAi.GetSettings()` — управление кэшем и прямой доступ к сервисам.
- ✨ **`QueuedAiOrchestrator.RunStreamingAsync`** — стриминг через собственную очередь с соблюдением `MaxConcurrent` и `CancellationScope`. Реализован через портативную producer/consumer-очередь на `SemaphoreSlim + ConcurrentQueue` (без `System.Threading.Channels`, который недоступен в Unity-сборке).

### 🧪 Tests

- ✅ **`AiOrchestratorStreamingEditModeTests`** (новый файл, 5 тестов):
  - `DefaultFallback_EmitsSingleTextChunkThenDone` — default-реализация интерфейса выдаёт ровно 2 чанка (текст + terminal).
  - `DefaultFallback_EmptyResult_EmitsErrorChunk` — пустой результат → terminal с `Error="empty result"`.
  - `QueuedAiOrchestrator_Streaming_DelegatesRealChunks` — 4 delta → 5 чанков (4 + terminal), а не 1 (fallback).
  - `QueuedAiOrchestrator_Streaming_RespectsMaxConcurrent` — 2 параллельных стрима с `MaxConcurrent=1` оба успешно выполняются.
  - `QueuedAiOrchestrator_Streaming_ExternalCancellation_EmitsCancelledTerminal` — отмена во время стрима → terminal с `Error="cancelled"`.
- ✅ **`CoreAiFacadeEditModeTests`** (новый файл, 7 тестов):
  - `IsReady_WithoutLifetimeScope_ReturnsFalse`.
  - `Invalidate_DoesNotThrow_WhenCalledMultipleTimes`.
  - `GetSettings_WithoutLifetimeScope_ReturnsNull`.
  - `GetChatService_WithoutLifetimeScope_ThrowsInvalidOperation` — с понятным сообщением.
  - `GetOrchestrator_WithoutLifetimeScope_ThrowsInvalidOperation`.
  - `TryGetChatService_WithoutLifetimeScope_ReturnsFalse` / `TryGetOrchestrator_WithoutLifetimeScope_ReturnsFalse`.

### 📚 Docs

- ✨ **`Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md`** — полный справочник по фасаду: **блок для новичков** (3 шага + FAQ), **шпаргалка по всем методам**, **профессиональный стек** (когда оставить статику, когда DI), `TryGet*`, threading, расширения.
- 🔧 **`STREAMING_ARCHITECTURE.md`** — новая секция `6. Orchestrator streaming` с таблицей сравнения `CoreAiChatService.SendMessageStreamingAsync` vs `IAiOrchestrationService.RunStreamingAsync` (авторити, структурная валидация, очередь, publish command, метрики).
- 🔧 **`QUICK_START.md`** — раздел «One-line alternative — `CoreAi` singleton».
- 🔧 **`DOCS_INDEX.md`** — ссылка на `COREAI_SINGLETON_API` в секции «Chat & Streaming».
- 🔧 **`README_CHAT.md`** — программное использование переписано на два варианта: через `CoreAi` фасад (рекомендуется) и через прямой `CoreAiChatService`.

## [0.20.3] - 2026-04-23

### 🐛 Chat panel & streaming hotfix

- 🐛 **Streaming был невидим в UI (regression).** В chain `LoggingLlmClientDecorator` → `RoutingLlmClient` → `OpenAiChatLlmClient` / `MeaiLlmUnityClient` ни одно звено не переопределяло `CompleteStreamingAsync`. В результате всегда срабатывал default-fallback из интерфейса `ILlmClient`, который просто вызывал `CompleteAsync` и выдавал **один** терминальный чанк после завершения генерации — пользователь видел "Typing…" и затем сразу полный ответ без эффекта стриминга.
  - **`OpenAiChatLlmClient.CompleteStreamingAsync`** → делегирует в `MeaiLlmClient.CompleteStreamingAsync` (SSE через `UnityWebRequest`, `ThinkBlockStreamFilter`).
  - **`MeaiLlmUnityClient.CompleteStreamingAsync`** → делегирует в `MeaiLlmClient.CompleteStreamingAsync` (LLMUnity callback → `ConcurrentQueue`).
  - **`RoutingLlmClient.CompleteStreamingAsync`** → выбирает inner-клиент по `AgentRoleId` и прокидывает чанки через `await foreach`.
  - **`LoggingLlmClientDecorator.CompleteStreamingAsync`** → прокидывает чанки наружу без буферизации, параллельно накапливает в `StringBuilder` для финального лога (`LLM ◀ (stream) … chunks=N | tokens … | content …`). Таймаут из `LlmRequestTimeoutSeconds` применяется ко всему стриму и превращается в финальный чанк с `Error = "LLM stream timeout (Ns)"`.
- 🐛 **Shift+Enter в multi-line TextField не отправлял сообщение.** `KeyDownEvent` был зарегистрирован в фазе Bubble (по умолчанию), поэтому UI Toolkit-овский multiline TextField успевал обработать Enter как newline до нашего обработчика. Теперь callback зарегистрирован в `TrickleDown.TrickleDown`, а проверка клавиши также учитывает `KeyCode.KeypadEnter` и `character == '\n' | '\r'` (на случай маппинга IME/клавиатуры).

### 💅 Typing indicator

- 💅 **Анимация индикатора — чистые точки `...`** вместо `Печатает...`. `CoreAiChatConfig.TypingIndicatorText` теперь по умолчанию пуст; точки анимируются циклом `. → .. → ... → .` каждые 400 мс с выравниванием ширины (padding пробелами), чтобы бабл не «скакал». Пользователь может вернуть классический режим, задав префикс в Inspector (например, `Печатает` → `Печатает.` / `Печатает..` / `Печатает...`).

### 🧪 Tests

- ✅ **`LoggingLlmClientDecoratorEditModeTests`** расширены:
  - `Streaming_DelegatesRealChunks_NotSingleShotFallback` — гарантирует, что при 4 реальных delta-чанках от mock-клиента пользователь получает 5 чанков (4 + терминальный), а не 1 (как было при fallback).
  - `Streaming_LogsStartAndFinish` — проверяет, что в логе есть `LLM ▶ (stream)`, `LLM ◀ (stream)`, `chunks=2` и `traceId`.
- ✅ **`RoutingLlmClientEditModeTests`** (новый файл, 3 теста):
  - `Streaming_RoutesToInnerClient_ForRole` — роутер выбирает правильный клиент и вызывает именно стриминговый путь (не `CompleteAsync`).
  - `Streaming_UsesFallbackClient_ForUnknownRole` — неизвестная роль → legacy fallback.
  - `Streaming_NullRequest_YieldsErrorChunk` — null-запрос не ломает `IAsyncEnumerable`, а возвращает один терминальный чанк с ошибкой.

## [0.20.2] - 2026-04-23

### 🗨️ Chat & Streaming

- ✨ **Стриминг теперь работает для обоих бэкендов** — HTTP API (через SSE) и LLMUnity (через `LLMAgent.Chat(callback)` + `ConcurrentQueue` с delta-дифом под локом). Дублирующая `regex <think>` фильтрация убрана из `LlmUnityMeaiChatClient` — теперь единый `ThinkBlockStreamFilter` применяется на уровне `MeaiLlmClient`.
- ✨ **`CoreAISettingsAsset.enableStreaming`** — глобальный тумблер в Inspector (секция «⚙️ Общие настройки»). Выключите его для принудительного non-streaming режима (полезно при отладке или работе с бэкендами без стриминга).
- ✨ **`CoreAiChatService.IsStreamingEnabled(roleId, uiFallback)`** — вычисление эффективного флага с учётом иерархии: UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`) → global (`CoreAISettings.EnableStreaming`).
- ✨ **`CoreAiChatPanel`** теперь соблюдает все три слоя: при отключении любого из них панель автоматически переключается на non-streaming режим.

### 🎬 Demo Scene

- ✨ **`CoreAI → Setup → Create Chat Demo Scene`** — новое меню, создаёт готовую сцену `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` с:
  - `Main Camera`, `Directional Light`, `EventSystem`;
  - `CoreAILifetimeScope` с автоматически назначенными `CoreAISettings`, `AgentPromptsManifest`, `LlmRoutingManifest`, `PrefabRegistry`, `GameLogSettings`;
  - `UIDocument` с `CoreAiChat.uxml` + `CoreAiChat.uss` и `PanelSettings` (1920×1080, ScaleWithScreenSize);
  - `CoreAiChatPanel` + демо-конфиг `CoreAiChatConfig_Demo.asset`.
- ✨ **`CoreAI → Setup → Open Chat Demo Scene`** — открыть созданную сцену одним кликом.

### 🧪 Tests

- ✅ **`ThinkBlockStreamFilterEditModeTests`** — полное покрытие production-класса `CoreAI.Ai.ThinkBlockStreamFilter`: split-теги (по 1 символу за раз), несколько блоков, `Flush()` / `Reset()`, регистронезависимость, псевдо-теги (`<b>`, `2 < 3`), незакрытый `<think>` и long-reasoning через 50+ чанков.
- ✅ **`CoreAiChatServiceEditModeTests`** — иерархия `IsStreamingEnabled` (UI → per-agent → global) для обеих перегрузок (`uiFallback` / `uiOverride`), `SendMessageAsync` / `SendMessageStreamingAsync` / `SendMessageSmartAsync` с поддельным `ILlmClient`.
- ✅ **`CoreAiChatConfigEditModeTests`** — дефолты ScriptableObject (в т.ч. `EnableStreaming == true`).
- ✅ `CoreAISettingsAssetEditModeTests` — добавлена проверка дефолта `EnableStreaming`.
- ✅ **`SecureLuaSandboxEditModeTests`** — явное покрытие `SecureLuaEnvironment.StripRiskyGlobals` для каждого вырезанного глобала (`io`, `os`, `debug`, `load`, `loadfile`, `dofile`, `require`), а также `LuaExecutionGuard` (taimaut / max steps / fast code / non-function argument) и `LuaCoroutineHandle` (Resume/Kill/budgetPerResume).
- ✅ **`LuaToolEditModeTests`** — `LuaTool.ExecuteAsync` (success, empty code, null code, executor throws, cancellation), `CreateAIFunction`, валидация `null`-аргументов конструктора, а также `LuaLlmTool` metadata (`Name`, `AllowDuplicates`, `Description`, `ParametersSchema`).
- ✅ `SmartToolCallingChatClientEditModeTests` — добавлены тесты на duplicate detection (`allowDuplicateToolCalls=false`), per-tool `AllowDuplicates=true` override, `tool not found`, `tool throws exception`.
- ✅ `InGameLlmChatServiceEditModeTests` — добавлены тесты rate limiter'а: превышение окна, `maxRequestsPerWindow=0` (отключение), отклонённый запрос не попадает в историю, скольжение окна.

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.20.2**

## [0.20.1] - 2026-04-23

### 🐛 Streaming Fixes

- 🐛 **Fixed `Create can only be called from the main thread`** в `StreamingPlayModeTests`. Тесты `Streaming_ReturnsChunks_WithDoneFlag`, `Streaming_CancellationToken_StopsStream`, `Streaming_ThinkBlocks_StrippedFromResponse` и `ThreeLayerPrompt_AllLayersApplied` раньше оборачивали `await foreach` в `Task.Run`, из-за чего `UnityWebRequest` и `DownloadHandlerBuffer` пытались создаться из ThreadPool и падали. Теперь streaming и `CompleteAsync()` вызываются напрямую как async-метод, continuations возвращаются через `UnitySynchronizationContext` на main thread.
- 🐛 **Правильная отмена стрима:** `MeaiOpenAiChatClient.GetStreamingResponseAsync()` теперь вызывает `webReq.Abort()` при `CancellationToken.IsCancellationRequested`, а не только бросает `OperationCanceledException` (это важно на OpenRouter/удалённых HTTP-бэкендах, где иначе сессия продолжает тарифицироваться).
- 🔧 **`MeaiLlmClient.CompleteStreamingAsync()`** переписан на новый stateful `ThinkBlockStreamFilter`. Раньше использовалась регулярка по одному чанку, из-за чего теги `<think>`/`</think>`, разбитые между SSE-чанками, не удалялись. Также гарантируется финальный `IsDone=true` чанк.
- 🔧 **`CoreAiChatPanel`** — локальная реализация state-machine заменена на общий `ThinkBlockStreamFilter` (DRY, нет расхождения логики между UI и LLM-слоем).

### Tests

- ✅ Все 4 `StreamingPlayModeTests` проходят (раньше все 4 падали).
- ✅ 27 EditMode тестов (`ThinkBlockFilterEditModeTests` + `StreamingAndPromptsEditModeTests`) проходят.

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.20.1**

## [0.20.0] - 2026-04-23

### 🗨️ Universal Chat Module (NEW)
- ✨ **`CoreAiChatConfig`** — ScriptableObject для настройки чата в Inspector (`Assets → Create → CoreAI → Chat Config`): roleId, заголовок, приветствие, иконки, streaming on/off, размеры, лимиты ввода.
- ✨ **`CoreAiChatPanel`** — MonoBehaviour + UI Toolkit контроллер: работает из коробки, поддержка streaming и non-streaming, think-block фильтрация, virtual методы для расширения (`OnMessageSending`, `OnResponseReceived`, `FormatResponseText`, `CreateMessageBubble`).
- ✨ **`CoreAiChatService`** — сервис чата без UI: streaming, chat history, 3-layer prompt composition. `TryCreateFromScene()` для авто-резолва из DI.
- ✨ **UXML/USS шаблон** — `CoreAiChat.uxml` + `CoreAiChat.uss` с тёмной темой, все CSS-классы с `coreai-` префиксом.
- ✨ **Think-block фильтрация** — state machine для streaming: `<think>...</think>` блоки скрываются, typing indicator показывается пока модель «думает».
- 📚 **`README_CHAT.md`** — документация: быстрый старт, расширение, программное API, кастомные стили.

### Streaming API
- ✨ **Real SSE Streaming в `MeaiOpenAiChatClient`** — реализован настоящий потоковый приём ответов через Server-Sent Events (`stream: true`). Парсинг `data:` строк и извлечение `delta.content` чанков.
- ✨ **`MeaiLlmClient.CompleteStreamingAsync()`** — стриминг через `IChatClient.GetStreamingResponseAsync()` с автоматической фильтрацией `<think>` блоков.
- 🔧 **DRY рефакторинг `MeaiOpenAiChatClient`** — вынесены `BuildMessagesPayload()` и `BuildToolsPayload()`, переиспользуются в `GetResponseAsync` и `GetStreamingResponseAsync`.

### 3-Layer Prompt Architecture
- 🔧 **`AiPromptComposer`** — расширен конструктор: принимает `AgentMemoryPolicy` и `ICoreAISettings` для 3-слойной сборки промптов.

### Tests
- 🧪 **EditMode**: `StreamingAndPromptsEditModeTests` (13 тестов: 3-layer composition, AgentMemoryPolicy, LlmStreamChunk, default streaming fallback).
- 🧪 **EditMode**: `ThinkBlockFilterEditModeTests` (10 тестов: regex и state machine фильтрации `<think>` блоков).
- 🧪 **PlayMode**: `StreamingPlayModeTests` (4 теста: streaming chunks, cancellation, think-block stripping, 3-layer prompt с реальным LLM).

### Dependencies
- Обновлена зависимость от `com.nexoider.coreai` до **0.20.0**

## [0.19.1] - 2026-04-14

### Fixes & Stability

- 🐛 **Защита от дублирования Tool Calls:** Разъяснены механизмы сброса счётчиков неудачных вызовов `MeaiLlmClient` внутри сессии. Локальность `executedSignatures` позволяет полностью изолировать каждый запрос.
- 🔧 **Тестовое окружение `Agent.cs`:** 
  - Тестовые фразы выведены в Inspector `[TextArea]` для изменения сценария "на лету" и предотвращения искусственного зацикливания LLM на идентичных промптах.
  - Добавлен метод `ClearMemory()` для преднамеренной очистки истории (позволяет сбросить контекст бота между нажатиями кнопок, чтобы модель не опиралась на предыдущие ошибки).
- 📝 **Документация:** Уточнена работа `SceneLlmAgentProvider` в связке с `DontDestroyOnLoad` — требуется явное наличие компонента `LLMAgent` или регистрация имени агента `LlmUnityAgentName`.

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.19.1**

## [0.19.0] - 2026-04-10

### Crafting & Validation

- ✨ **`CompatibilityChecker`** — проверка совместимости ингредиентов (правила на 2/3/4+ элементов, группы, кастомные валидаторы)
- ✨ **`CompatibilityLlmTool`** — ILlmTool обёртка для function calling
- ✨ **`JsonSchemaValidator`** — валидация JSON-ответов от LLM (типы, диапазоны, enum)
- 🧪 **45+ EditMode тестов** (`CompatibilityAndSchemaEditModeTests.cs`)
- 🧪 **3 PlayMode теста** (`CompatibilityToolPlayModeTests.cs`) с реальной LLM-моделью

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.19.0**

## [0.18.0] - 2026-04-10

### Architecture — LifetimeScope Decomposition & DI Cleanup

- 🔧 **`CoreAILifetimeScope.Configure()`** — декомпозиция из 200+ строк в модульные инсталлеры:
  - `LlmPipelineInstaller` — LLM клиенты, маршрутизация, декоратор логирования, метрики оркестратора.
  - `WorldCommandsInstaller` — Lua bindings, prefab registry, world executor, game config store.
  - `Configure()` теперь ~40 строк с чёткими секциями.
- ✨ **`ILlmAgentProvider` / `SceneLlmAgentProvider`** — абстракция поиска `LLMAgent` с lazy caching. Убран `FindFirstObjectByType<LLMAgent>` из DI composition root.
- 🔧 **`CoreAISettings.Instance = settings`** — заменяет 17-строчный блок `SyncToStaticSettings()`. Статический прокси CoreAISettings теперь делегирует в DI-экземпляр автоматически.
- ❌ **`SyncToStaticSettings()`** — удалён полностью (заменён одной строкой `CoreAISettings.Instance = settings`).
- 🧪 **Тесты**:
  - `CoreAISettingsSyncEditModeTests` — переписан на проверку Instance delegation (4 теста вместо 1).
  - `LuaAiEnvelopeProcessorEditModeTests` — обновлён cleanup через `ResetOverrides()`.

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.18.0**

## [0.16.0] - 2026-04-09

### PlayMode Tools & Editor
- ✨ **`SceneLlmTool`** — новый инструмент для Runtime инспекции сцены. Позволяет LLM искать, анализировать иерархию и менять `Transform` у GameObject, работая безопасно в главном потоке через UniTask.
- ✨ **`CameraLlmTool`** — инструмент зрения, позволяющий модели делать скриншоты в PlayMode (`capture_camera`) с возвратом `dataUri` (Base64 JPEG).
- 🛠 **Автоматизация `CoreAiPrefabRegistryAsset`** — добавлен `OnValidate`, который автоматически проставляет `Key` (на основе AssetDatabase GUID) и стягивает `Name` при добавлении префаба в инспекторе.

## [0.15.0] - 2026-04-09

### Tool Calling Engine
- ✨ **Robust JSON Extraction** — полностью переписан механизм парсинга tool calls в `LlmUnityMeaiChatClient.TryParseToolCallFromText`. Старое хрупкое Regex вырезано; заменено на гибкий алгоритм поиска фигурных скобок (`IndexOf('{')`).
- ⚙️ **Reasoning Mode Stripping** — добавлен препроцессинг ответов: парсинг tool calls теперь предварительно вырезает всю цепочку рассуждений `<think>...</think>`, предотвращая сбой JSON-парсера при "думанье" вслух (DeepSeek/Qwen).

### Editor UX
- ✨ **Auto-Plugin Loading** — встроен механизм `[InitializeOnLoadMethod]` в `CoreAIBuildMenu`. При старте проекта он автоматически генерирует полный набор необходимых `ScriptableObject`.
- ✨ **Quick Settings Menu** — добавлено удобное меню **CoreAI → Settings** для быстрого доступа к глобальному синглтону `CoreAISettings.asset`.

## [0.13.0] - 2026-04-09

### Action / Event System
- ✨ Поддержка `DelegateLlmTool`, `CoreAiEvents` и расширений `AgentBuilder` (добавлено в `Dependencies: com.nexoider.coreai 0.13.0`).
- 📝 Обновлены `TOOL_CALL_SPEC.md` и `AGENT_BUILDER.md` с примерами и механизмом промптинга триггеров.
- 🧪 **EditMode Tests** для `CoreAiEvents` и `AgentBuilder.WithAction` внедрены и пройдены.
- 🧪 **PlayMode Tests** для `DelegateLlmTool` вызова (тест `CustomAgentsPlayModeTests.CustomAgent_Helper_WithAction`).

## [0.12.0] - 2026-04-08

### Unified Logger (`ILog`)

- 🔧 **UnityLog** — реализация `ILog` из CoreAI.Core, маппит `LogTag` → `GameLogFeature`
- 🔧 **CoreServicesInstaller** — регистрирует `ILog` (через `UnityLog`) как DI singleton + устанавливает `Log.Instance`
- 🔧 **GameLoggerUnscopedFallback** — автоматический fallback `Log.Instance` до инициализации DI
- 🔧 **CoreAIGameEntryPoint** — мигрирован с `IGameLogger` на `ILog`
- 🔧 **WorldTool** — logging мигрирован на `ILog` с `LogTag.World`
- ❌ Удалена ручная установка `Log.Instance` из `CoreAILifetimeScope`
- 🔧 **Унификация `MemoryToolAction`** (через Core 0.12.0) — устранено дублирование enum, настройка в `AgentBuilder.WithMemory()` теперь корректно применяется в политике через `policy.ConfigureRole()`.
- ℹ️ `IGameLogger` сохранён как внутренний интерфейс Unity-слоя (FilteringGameLogger, GameLogSettingsAsset — без изменений)

### Dependencies

- Обновлена зависимость от `com.nexoider.coreai` до **0.12.0**

---

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
- **MaxLuaRepairRetries** (ранее MaxLuaRepairGenerations) изменён с 4 на 3

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
