# CoreAI — анализ проблем проекта

Дата: 2026-05-01
Состояние: 14 файлов в дереве модифицированы (документация / переводы XML, см. § 5.J).

Документ собирает все найденные проблемы в одном месте. Источники истины:
[`ARCHITECTURE.md`](ARCHITECTURE.md), [`STREAMING_ARCHITECTURE.md`](STREAMING_ARCHITECTURE.md),
[`STREAMING_WEBGL_TODO.md`](STREAMING_WEBGL_TODO.md), [`CODE_AUDIT_AND_FOLLOWUPS.md`](CODE_AUDIT_AND_FOLLOWUPS.md),
[`DGF_SPEC.md`](DGF_SPEC.md), `Assets/CoreAI/Runtime/Core/CoreAI.Core.asmdef`, `Assets/CoreAiUnity/Runtime/Source/CoreAI.Source.asmdef`.

---

## TL;DR — приоритетный список

| # | Серьёзность | Категория | Что | Где |
|---|---|---|---|---|
| 1 | **High** | WebGL | Синхронный `File.*` IO внутри `IConversationSummaryStore` на каждом ходе чата → стол на IndexedDB | `Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs:51,56,80,99,101,120` + регистрация `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs:125` |
| 2 | **High** | WebGL | Синхронный `File.*` IO в `FileAgentMemoryStore` для memory-enabled ролей | `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs:51..309` |
| 3 | **High** | WebGL | Документ `STREAMING_WEBGL_TODO.md` обещает `ShouldUseStreamingForRole` (Solution C, 0.26.0) — в коде отсутствует. На WebGL продолжит ловиться «chunks=1, бесконечная анимация» | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs`, `CoreAiChatService.cs` |
| 4 | **Medium** | WebGL | Reflection через `tool.GetType().GetMethod("CreateAIFunction")` — пользовательские tool-сборки могут быть стрипнуты IL2CPP | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:933`, `Assets/link.xml` (preserve только `CoreAI.Core`/`CoreAI.Source`) |
| 5 | **Medium** | Архитектура | Чрезмерное количество мутируемого `static` состояния в портативном Core: `Log.Instance`, `CoreAISettings` (полный статический фасад), `CoreAIAgent`, `CoreAiEvents`, `AgentMemoryPolicy._memoryToolInstance` | `Assets/CoreAI/Runtime/Core/Features/Logging/ILog.cs:59-69`; `Assets/CoreAI/Runtime/Core/CoreAISettings.cs`; `CoreAIFacade.cs:25-77`; `Features/Llm/CoreAiEvents.cs`; `Features/AgentMemory/AgentMemoryPolicy.cs:22` |
| 6 | **Medium** | Архитектура | Два параллельных JSON-серилизатора (`Newtonsoft.Json` и `System.Text.Json`) в одной asmdef — единственный пользователь STJ это `FileConversationSummaryStore.cs` | `Assets/CoreAI/Runtime/Core/CoreAI.Core.asmdef:14-16`, `FileConversationSummaryStore.cs:3,57,79` |
| 7 | **Medium** | Архитектура | Unity-инфраструктурные клиенты принимают конкретный `CoreAISettingsAsset` (ScriptableObject) вместо контракта `ICoreAISettings`/`IOpenAiHttpSettings` | `OfflineLlmClient.cs:16,19`, `OpenAiChatLlmClient.cs:24`, `MeaiLlmClient.cs:71-81` |
| 8 | **Medium** | Архитектура | Два параллельных абстракции логирования — `ILog` (Core) и `IGameLogger` (Unity); `MeaiLlmClient`/`OfflineLlmClient`/`OpenAiChatLlmClient` всё ещё требуют `IGameLogger` | см. §3 |
| 9 | **Medium** | Качество | Тихие `catch { }` без логирования вокруг DI-резолвов — диагностика мисконфига становится невозможной | `CoreAi.cs:323-325,385-395`, `CoreAiChatService.cs:57-60`, `MessagePipeToolCallEventPublisher.cs:27-63` |
| 10 | **Medium** | Качество | `public static async void Ask(...)` в публичном API расширений | `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentConfigExtensions.cs:85` |
| 11 | **Low** | Архитектура / API | `CoreAi.cs` живёт в корневом namespace `CoreAI` (получается `CoreAI.CoreAi`), `UnityLog.cs` в `CoreAI.Unity.Logging`, `SceneLlmTool.cs` ошибочно в портативном `CoreAI.Ai` | см. §3.10 |
| 12 | **Low** | Качество | Magic-strings: `"https://api.openai.com/v1"` ×3, `"HTTP-Referer"/"https://unity.com"` ×3, `Application.persistentDataPath/"CoreAI"/<sub>` ×4 | см. §5.G |
| 13 | **Low** | Документация | `DGF_SPEC.md:70` утверждает, что `CoreAI.Core` ссылается на VContainer — фактически нет | `Assets/CoreAiUnity/Docs/DGF_SPEC.md:70` vs `CoreAI.Core.asmdef` |
| 14 | **Low** | Документация | `CODE_AUDIT_AND_FOLLOWUPS.md` фиксирует Cyrillic в XML-доках — backlog ~1346 совпадений в 154 файлах | см. §5.A |
| 15 | **Info** | Качество | Дубликат класса `LlmResponseSanitizer` (один в `Features/Llm/`, второй внутри `ProgrammerLuaResponseParser.cs:63`) — конфликт `using` | `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/ProgrammerLuaResponseParser.cs:63` |

---

## 1. WebGL-проблемы

### 1.1. Что **уже корректно** (важно зафиксировать)

| Категория | Статус |
|---|---|
| `new Thread`, `Thread.Start`, `Thread.Sleep` | 0 в runtime ✓ |
| `Task.Run`, `Task.Factory.StartNew`, `ThreadPool.*` | 0 в runtime ✓ |
| `.Wait()`, `.Result;`, `.GetAwaiter().GetResult()` | 0 в runtime (только в тестах) ✓ |
| `CancellationTokenSource.CancelAfter(...)` | 0 в runtime; таймаут чата через `CancelAfterSlim` (`CoreAiChatService.cs:118`) ✓ |
| `System.Threading.Timer`, `System.Timers.Timer`, `new Timer(` | 0 ✓ |
| `TcpClient`, `Socket`, `UdpClient`, `HttpListener`, `NamedPipe` | 0 ✓ — LLM HTTP в portable Core через **`HttpClient`** (`MeaiOpenAiChatClient`); без сырых сокетов в runtime |
| `HttpClient` | **1** — `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs` (OpenAI-compatible MEAI); Unity `LlmPipelineInstaller.BuildHttpClient` — фабричное имя, возвращает тот же тип |
| `Process.Start`, `Environment.Exit` | 0 ✓ |
| `BinaryFormatter`, `XmlSerializer` | 0 ✓ |
| HTTP SSE / stall budget (OpenAI path) | `MeaiOpenAiChatClient` — `StreamReader.ReadLineAsync` + `Task.WhenAny` (см. исходник), не poll `UnityWebRequest` |
| `ConfigureAwait(false)` распределение Core (есть) / Unity (нет) | соответствует [`ARCHITECTURE.md:93`](ARCHITECTURE.md) ✓ |
| MoonSharp coroutines | тикаются из `Update()` (`LuaCoroutineRunner.cs:67`), не из `System.Threading.Thread` ✓ |
| `LlmUnityMeaiChatClient.cs:333` `Task.Delay(10)` | весь файл за `#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL` — не уезжает в WebGL ✓ |

### 1.2. Что реально ломается на WebGL

**a) `FileConversationSummaryStore` — каждый ход чата читает/пишет IndexedDB на main-thread**

- Объявление: `Assets/CoreAI/Runtime/Core/Features/AgentMemory/FileConversationSummaryStore.cs:51,56,80,99,101,120` — синхронные `File.Exists` / `File.ReadAllText` / `File.WriteAllText` / `Directory.CreateDirectory`.
- Регистрация: `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs:125` — **без** `#if !UNITY_WEBGL` и без альтернативы.
- Вызывается синхронно из `BuildChatHistoryAsync` → `AiOrchestrator.RunTaskAsync`.
- На WebGL `Application.persistentDataPath` маппится в IndexedDB, синхронные записи задерживают main-loop кадр.

**Фикс:** под WebGL регистрировать `InMemoryConversationSummaryStore` (он уже есть в портативном слое), либо асинхронный wrapper. **Сделано в v1.5.20:** `CoreAILifetimeScope` вызывает `RegisterCorePortable(suppressDefaultConversationSummaryStore: false)` под `UNITY_WEBGL` вместо `FileConversationSummaryStore` (см. `RegisterConversationSummaryForCoreAiLifetimeScope`, `ARCHITECTURE.md`).

**b) `FileAgentMemoryStore` — то же самое для memory-enabled ролей**

- `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs:40` (root path), затем строки `51, 56, 86, 88, 96, 110, 112, 118, 148, 150, 156, 177, 214, 219, 296, 298, 309` — все синхронные `File.*`/`Directory.CreateDirectory`.
- XML-комментарий 13–15 утверждает, что WebGL «работает но осторожно с квотой» — но в реальности это блокирующий sync-write на main-thread, не только проблема квоты.

**Фикс:** альтернативный store на `PlayerPrefs` для WebGL (упомянут в [`MEMORY_STORE_CUSTOM_BACKENDS.md`](MEMORY_STORE_CUSTOM_BACKENDS.md)) → выбирать его в композите при `#if UNITY_WEBGL`.

**c) Streaming на WebGL — обещанный фикс не реализован**

- [`STREAMING_WEBGL_TODO.md`](STREAMING_WEBGL_TODO.md) описывает Solution C: виртуальный хук `protected virtual bool ShouldUseStreamingForRole(string roleId, bool uiFallback)` в `CoreAiChatPanel`, по умолчанию `false` под `#if UNITY_WEBGL && !UNITY_EDITOR`. Цель — `0.26.0`.
- В коде: `grep -r ShouldUseStreamingForRole Assets/` — только сам план в md, ни одной реализации.
- `CoreAiChatPanel.cs:118,129,1097,1150` имеет `#if UNITY_WEBGL` только вокруг UI-маршалинга, **не** вокруг решения о streaming.
- Решение о streaming принимается в `CoreAiChatService` / `AgentMemoryPolicy` без WebGL-гейта.

**Эффект:** собранный WebGL-плеер с включённым streaming продолжит показывать `chunks=1` + бесконечную «анимацию печати», как описано в § 1 `STREAMING_WEBGL_TODO.md`.

**Фикс:** реализовать Solution C из плана, либо как минимум форс-выключение streaming в `CoreAISettingsAsset` под `#if UNITY_WEBGL && !UNITY_EDITOR`.

**d) Reflection на пользовательских tool-сборках**

- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:933` — `tool.GetType().GetMethod("CreateAIFunction")` + `m.Invoke(tool, null)`.
- `Assets/link.xml` сохраняет целиком `CoreAI.Core` (строка 13) и `CoreAI.Source.MessagePipeAiCommandSink` (строка 17). Сборки **игры**, объявляющие свои `ILlmTool`, **не** покрыты.
- На IL2CPP/WebGL метод `CreateAIFunction` пользовательского tool может быть стрипнут — `m == null` → tool молча не подключится.

**Фикс:** документировать в [`MEAI_TOOL_CALLING.md`](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) требование `[Preserve]` или собственный `link.xml` в проекте игры. Альтернатива — заменить reflection на интерфейсный метод `ILlmTool.CreateAIFunction()` (если ещё не существует).

### 1.3. Информационные / гейтнутые моменты

- `LlmUnityMeaiChatClient.cs:333` использует `Task.Delay(10, ct)` вместо `UniTask.Yield(PlayerLoopTiming.Update, ct)`. Файл за `#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL` (строка 1) — на WebGL не попадёт. Стилистически — нарушение правила «WebGL Rule», и при ослаблении гейта проблема всплывёт. Тривиально исправить ради единообразия.
- Тяжёлые `JsonConvert.SerializeObject(req)` (`MeaiOpenAiChatClient`) синхронно на потоке вызывающего кода перед `HttpClient.SendAsync`. Для типовых чат-payload не страшно, для больших prompt — заметный кадр на main-thread.

### 1.4. Тестовый код (в build не идёт, но влияет на CI)

Тесты широко используют `.Result`, `.Wait()`, `Task.Run`, `cts.CancelAfter` — список см. в детальном репорте, все в `Assets/CoreAiUnity/Tests/**`. Под Editor работают; под Standalone Mono тоже, но если кто-то запустит PlayMode-тест в WebGL-плеере (что и так практически невозможно) — упадёт. Для целей релиза — не блокер.

---

## 2. Архитектурные нарушения

### 2.1. Что **корректно** держится (фиксируем)

- `Assets/CoreAI/Runtime/` чистый: 0 хитов на `using UnityEngine`, `using VContainer`, `using MessagePipe`, `using Cysharp.Threading.Tasks`, `using LLMUnity`, `Microsoft.Extensions.DependencyInjection`, `MonoBehaviour`/`ScriptableObject`/`GameObject`/`Transform`. ✓
- `Assets/CoreAI/Runtime/` 0 хитов на `IPublisher<`, `ISubscriber<`, `MessagePipe`, `GlobalMessagePipe`. ✓
- `Assets/CoreAI/Runtime/` 0 хитов на `IContainerBuilder`, `LifetimeScope`, `.Resolve<`. ✓
- Логирование в Core идёт через `ILog` / `Log.Instance`; 0 `UnityEngine.Debug` или `Microsoft.Extensions.Logging` (кроме примеров в `///`). ✓
- Tool-lifecycle: `ToolExecutionPolicy.cs:215,240,244` публикует через `IToolCallEventPublisher` — мост `ToolExecutionPolicy → IToolCallEventPublisher → MessagePipeToolCallEventPublisher` цел. ✓
- Marshaling MEAI: `ToolExecutionPolicy.cs:220-226` уважает `_settings.ToolInvocationMarshaler`; единственный прямой `aiFunc.InvokeAsync` обёрнут именно через marshaler. ✓
- `CoreAISettingsAsset.cs:439` переопределяет `ToolInvocationMarshaler => UnityMainThreadLlmAsyncMarshaler.Instance`, тест `CoreAISettingsToolMarshalerEditModeTests:13-17` это проверяет. ✓ **Тревога из аудита снята**.

### 2.2. Static-сингтоны и глобальное состояние в портативном Core

| Где | Что | Почему плохо |
|---|---|---|
| `Assets/CoreAI/Runtime/Core/Features/Logging/ILog.cs:59-69` | `public static class Log { static ILog _instance; ... { get; set; } }` | service-locator, Tools (`InventoryTool`, `MemoryTool`, `OfflineLlmClient`) дёргают `Log.Instance.Info(...)` вместо инъекции `ILog`. Фикс: впрыскивать `ILog` в конструктор, статический `NullLog` оставить только дефолтом DI. |
| `Assets/CoreAI/Runtime/Core/CoreAISettings.cs` | Полный статический фасад `public static class CoreAISettings` (20+ мутируемых полей с сеттерами, плюс `Instance`, плюс `_lock`, `ResetOverrides`) | Сам факт, что там понадобился `lock` и `ResetOverrides`, — симптом. Параллельные тесты делят состояние. |
| `Assets/CoreAI/Runtime/Core/CoreAIFacade.cs:25-77` | `public static class CoreAIAgent` со ссылкой на `IAiOrchestrationService` | Фасад «глобального агента» в портативном слое; заполняется из Unity-only `CoreAILifetimeScope`. Это Unity-форменный сервис-локатор внутри портативной части. |
| `Assets/CoreAI/Runtime/Core/Features/Llm/CoreAiEvents.cs:11-125` | `public static class CoreAiEvents` с двумя `static Dictionary<string, Action>` и `ClearAll()` | Глобальная шина событий, конкурирующая с документированной `IToolCallEventPublisher`/MessagePipe-цепочкой. |
| `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs:22` | `private static readonly MemoryLlmTool _memoryToolInstance = new();` | Singleton tool, шаренный всеми `AgentMemoryPolicy`. Сейчас `MemoryLlmTool` без состояния — но любая будущая правка может стать багом. |

Маркеры «ARCH-1» / «ARCH-2» в `CoreAISettings.cs:14,26` и `CoreAIFacade.cs:30-32` явно фиксируют прошлые гонки — проблема осознаётся, но не убрана.

**Стратегия фикса:** убрать static-фасады из портативного слоя, `CoreAIAgent` перенести рядом с `CoreAi.cs` в Unity-слой. Static-`Log.Instance` либо пометить `[Obsolete]`, либо обернуть `Volatile.Read`/`Interlocked.Exchange` минимум.

### 2.3. Утечка `CoreAISettingsAsset` (ScriptableObject) в Unity-клиенты

| Где | Что должно быть |
|---|---|
| `OfflineLlmClient.cs:16,19` ctor `(CoreAISettingsAsset)` | `(ICoreAISettings, IOfflineLlmConfig)` |
| `OpenAiChatLlmClient.cs:24` ctor `(CoreAISettingsAsset)` | `(ICoreAISettings, IOpenAiHttpSettings)` |
| `MeaiLlmClient.cs:71-81` `CreateHttp(CoreAISettingsAsset, IGameLogger,...)` | оставить `internal`, публичный — `IOpenAiHttpSettings` overload |

ScriptableObject не должен быть в публичной сигнатуре инфраструктурного клиента — это завязывает unit-тесты на Unity-only тип и ломает шанс переиспользовать клиент в неюнити-хосте.

### 2.4. Два логгера

`ToolExecutionPolicy` уже мигрирован на `ILog` (см. CHANGELOG `Assets/CoreAiUnity/CHANGELOG.md:217`), но `MeaiLlmClient.cs:43,46`, `OfflineLlmClient`, `OpenAiChatLlmClient`, внутренние клиенты MEAI ещё принимают `IGameLogger`. Два параллельных абстракции на одном пайплайне — лишний адаптер на каждом вызове.

**Фикс:** либо `IGameLogger : ILog`, либо `UnityLog` реализует обе. Завершить миграцию.

### 2.5. Два JSON-сериализатора

`CoreAI.Core.asmdef:14-16` тянет и `Newtonsoft.Json.dll`, и `System.Text.Json.dll`. Используются:
- **Newtonsoft.Json** — 11 файлов: `InventoryTool.cs`, `MemoryTool.cs`, `LuaTool.cs`, `GameConfigTool.cs`, `CompatibilityLlmTool.cs`, `JsonSchemaValidator.cs`, `JsonValidationResult.cs`, `LlmToolResultEnvelope.cs`, `ToolExecutionPolicy.cs`, `SmartToolCallingChatClient.cs`, `LlmToolCallTextExtractor.cs`, `StubLlmClient.cs`.
- **System.Text.Json** — ровно 1: `FileConversationSummaryStore.cs:3,57,79`.

Tool-args в MEAI везде — `Newtonsoft.Json.Linq.JArray/JObject` (см. `CompatibilityLlmTool.cs:63`, `ToolExecutionPolicy.cs:465-468`). Каноном де-факто является Newtonsoft.

**Фикс:** сконвертировать `FileConversationSummaryStore` на Newtonsoft, удалить `System.Text.Json.dll` из `precompiledReferences` обеих asmdef.

### 2.6. Namespace-хаос

| Файл | Текущий namespace | Должен быть |
|---|---|---|
| `Assets/CoreAiUnity/Runtime/Source/Features/Logging/UnityLog.cs:4` | `CoreAI.Unity.Logging` | `CoreAI.Infrastructure.Logging` (как соседи) |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:12` | `CoreAI.Ai` (портативный namespace) | `CoreAI.Infrastructure.World` |
| `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:14` | `CoreAI.Infrastructure.Llm` | соответствие папке: `CoreAI.Infrastructure.World` |
| `Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:13` | `CoreAI` (root) → тип `CoreAI.CoreAi` | `CoreAI.Api` |

`SceneLlmTool` в `CoreAI.Ai` особенно опасен — выглядит как портативный, но завязан на Unity-инфраструктуру.

### 2.7. Дубликат класса

`Assets/CoreAI/Runtime/Core/Features/AgentPrompts/ProgrammerLuaResponseParser.cs:63` объявляет `public static class LlmResponseSanitizer` — а такой же класс уже есть в `Assets/CoreAI/Runtime/Core/Features/Llm/LlmResponseSanitizer.cs:10`. Под одним root-namespace `CoreAI` — `using` становится двусмысленным. **Удалить или переименовать.**

### 2.8. `Debug.LogWarning` в публичном `CoreAi`

`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:289` — единственное место в `CoreAi.cs`, где напрямую дёргается `UnityEngine.Debug`. По стилю должно идти через `ILog`/`IGameLogger`.

---

## 3. Прочие проблемы качества

### 3.A. Cyrillic в dev-facing `///` и `//`

[`CODE_AUDIT_AND_FOLLOWUPS.md`](CODE_AUDIT_AND_FOLLOWUPS.md) фиксирует, что портативные контракты и часть Unity-входов уже переведены. Остаток backlog'а — **1346** совпадений в **154** файлах runtime. Топ-15 худших:

| Count | File |
|---|---|
| 137 | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatPanel.cs` |
| 51 | `Assets/CoreAI/Runtime/Core/CoreAISettings.cs` |
| 46 | `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentBuilder.cs` |
| 41 | `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs` |
| 39 | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs` |
| 36 | `Assets/CoreAI/Runtime/Core/Features/Crafting/CompatibilityChecker.cs` |
| 30 | `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs` |
| 30 | `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatConfig.cs` |
| 29 | `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs` |
| 29 | `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentConfigExtensions.cs` |
| 27 | `Assets/CoreAI/Runtime/Core/Features/Orchestration/ThinkBlockStreamFilter.cs` |
| 25 | `Assets/CoreAI/Runtime/Core/Features/Logging/ILog.cs` |
| 22 | `Assets/CoreAI/Runtime/Core/Features/AgentPrompts/AiPromptComposer.cs` |
| 21 | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs` |
| 21 | `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiTaskRequest.cs` |

Особо: `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs:20-21,27-55` — `[Tooltip("Единые настройки CoreAI…")]` нарушает явное правило «English for developer-facing Unity fields» из [`ARCHITECTURE.md`](ARCHITECTURE.md).

### 3.B. Тихие `catch { }`

| Где | Эффект |
|---|---|
| `CoreAi.cs:323-325, 385-387, 393-395` | `catch { }` вокруг `Resolve` orchestrator/settings/memory store — мисконфиг невидим |
| `CoreAiChatService.cs:57-60` | четыре `catch { }` вокруг DI-резолвов; logger=`null` дальше используется без проверки |
| `MessagePipeToolCallEventPublisher.cs:27-29, 44-46, 61-63` | `catch { }` вокруг `GlobalMessagePipe.Publish` — пропавшие tool-lifecycle-события не диагностировать |
| `MeaiOpenAiChatClient.cs:648-650` | `catch { }` при парсинге HTTP-error JSON — допустимо, но Trace-лог не помешает |
| `UnityMainThreadLlmAsyncMarshaler.cs:81-83` | `catch { }` вокруг `Application.isPlaying` в `[RuntimeInitializeOnLoadMethod]` — defensible, но Trace помог бы |
| `Sandbox/SecureLuaEnvironment.cs:79-81`, `Sandbox/LuaCoroutineHandle.cs:63-65` | sandbox — намеренно, добавить комментарий |

Ни одного `throw ex;` (потери стека) в runtime — это хорошо.

### 3.C. `async void` в публичном API

- `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentConfigExtensions.cs:85` — `public static async void Ask(...)`. Тело завернуто в try/catch, но публичный `async void` лишает возможности `await` и проглатывает unhandled exceptions. **Фикс:** переименовать в `AskFireAndForget` и вернуть `Task`, либо сделать тонкий `_ = AskAsync(...)`-обёртку.
- `CoreAIGameEntryPoint.cs:103` `private async void FireBootstrapAiTask()` — приватный, по имени намерение ясно. OK, но не глотать исключения внутри.
- `InGameChatPanel.cs:68 OnSendClicked()`, `CoreAISettingsAssetEditor.cs:729` — UI-handlers, OK.

### 3.D. Dispose / IDisposable

- `CoreAiChatPanel.cs:105, 1590` — `_cts = new CancellationTokenSource()`. На путь recreate-without-prior-dispose стоит посмотреть глазами при ревью (`1590` создаёт новый CTS — убедиться, что предыдущий точно `Dispose`).
- `AiGameCommandRouter.cs:29-47, 82` — `IDisposable _subscription` хранится и `Dispose`. ✓
- `QueuedAiOrchestrator.cs:540-552` — `Dictionary<string, CancellationTokenSource>` дренируется и диспозится в `Dispose()`. ✓
- В тестах — `new CancellationTokenSource()` без `using` встречается (`CoreAiChatPanelNonStreamingPlayModeTests.cs:88,135`), некритично.

Все `MessagePipe.Subscribe(...)` в runtime сохраняют возвращаемый `IDisposable` (`AiGameCommandRouter`). ✓

### 3.E. Static-state thread-safety

- `ILog.cs:61-68` — `static ILog _instance` без `volatile`/`Interlocked`. Сеттер вызывается один раз при композиции, но читают воркеры → теоретическая гонка-видимость. Пометить `volatile` или обернуть `Interlocked.Exchange`.
- `CoreAISettingsAsset.cs:42-68` — `static CoreAISettingsAsset _instance` ленивый `Resources.Load` без `lock`. Идемпотентно, но при первом обращении из двух потоков обе ветки `Resources.Load` отработают. `Interlocked.CompareExchange` чище.
- `CoreAi.cs:46-51` — `_scope`, `_chatService`, `_orchestrator`, `_settings` мутируются под `SyncRoot`. ✓
- `UnityMainThreadLlmAsyncMarshaler` — `Volatile.Read`/`Volatile.Write` на mirror-полях. ✓
- `MessagePipeToolCallEventPublisher.Instance` — `static readonly`. ✓

### 3.F. API-консистентность

- `InventoryTool.cs:37 ExecuteAsync(CancellationToken)` не вызывает `cancellationToken.ThrowIfCancellationRequested()` до старта; для синхронных веток токен фактически игнорится.
- `default(CancellationToken)` устаревшего стиля — 0 совпадений. ✓
- `AiOrchestrator.cs:798 CancelTasks(string)` пустой в базе (теперь задокументирован `<remarks>` в diff). Лучше выделить в опциональный интерфейс или вернуть `bool` «supported».

### 3.G. Magic-strings / hardcoded paths

| Где | Что | Куда деть |
|---|---|---|
| `CoreAILifetimeScope.cs:126` | `Path.Combine(persistentDataPath, "CoreAI", "ConversationSummaries")` | в `CoreAISettingsAsset` (поля storage subdirs) |
| `FileAgentMemoryStore.cs:40` | `..., "CoreAI", "AgentMemory"` | то же |
| `FileLuaScriptVersionStore.cs:23` | `"CoreAI", "LuaScriptVersions"` | то же |
| `FileDataOverlayVersionStore.cs:21` | `"CoreAI", "DataOverlayVersions"` | то же |
| `OpenAiHttpLlmSettings.cs:21,57,109` | `"https://api.openai.com/v1"` ×3 | `private const string DefaultBaseUrl` |
| `MeaiOpenAiChatClient.cs:120`, `CoreAISettingsAssetEditor.cs:865, 1185` | `"HTTP-Referer"/"https://unity.com"` ×3 | shared const `OpenRouterReferer` |
| `StubLlmClient.cs:24` | `"```lua\nreport('stub: lua executed (Programmer)');\n```"` | `private const` |

### 3.H. Гепы тестового покрытия для модифицированных файлов

| Класс | Покрытие |
|---|---|
| `InventoryTool` | косвенно через `AgentToolsVisibilityEditModeTests` и сценарий мерчанта. **Гэп:** `ExecuteAsync` happy/failure-сериализация. |
| `BuiltInAgentSystemPromptTexts` | косвенно. **Гэп:** ordering `WithUniversalPrefix`. |
| `AiOrchestrator` / `IAiOrchestrationService` / `ILlmClient` | хорошее покрытие edit + play. ✓ |
| `LuaTool` | `LuaToolEditModeTests`. ✓ |
| `StubLlmClient` | `CoreAssemblyEditModeTests`, play-tests builtin-ролей. ✓ |
| `ICoreAISettings` / `CoreAISettingsAsset` | `CoreAISettingsAssetEditModeTests`, `CoreAISettingsSyncEditModeTests`, `MaxTokensFallbackEditModeTests`, `CoreAISettingsToolMarshalerEditModeTests`. ✓ |
| `CoreAi` (статический фасад) | `ControlApiEditModeTests` (Stop/Clear). **Гэп:** `AskAsync`/`StreamAsync`/`SmartAskAsync`/`OrchestrateStreamCollectAsync` — только косвенно через chat/orchestrator-тесты. |
| `OfflineLlmClient` | `OfflineLlmClientEditModeTests`. ✓ |
| `OpenAiHttpLlmSettings` | косвенно. **Гэп:** `UseOpenAiCompatibleHttp` toggle. |

### 3.I. `ICoreAISettings` ↔ `CoreAISettingsAsset`

Все 20 интерфейсных свойств покрыты соответствующими сериализованными полями (см. таблицу: `MaxLuaRepairRetries`, `EnableMeaiDebugLogging`, `LlmRequestTimeoutSeconds`, `MaxLlmRequestRetries`, `EnableHttpDebugLogging`, `LogTokenUsage`, `LogLlmLatency`, `LogLlmConnectionErrors`, `ContextWindowTokens`, `UniversalSystemPromptPrefix`, `Temperature`, `MaxToolCallRetries`, `LogToolCalls`, `LogToolCallArguments`, `LogToolCallResults`, `LogMeaiToolCallingSteps`, `AllowDuplicateToolCalls`, `EnableStreaming`, `MaxTokens`, `EnableLlmContextCompaction`, `ToolInvocationMarshaler`).
- `ToolInvocationMarshaler` корректно переопределён на `UnityMainThreadLlmAsyncMarshaler.Instance` (`CoreAISettingsAsset.cs:439`), с regression-тестом `CoreAISettingsToolMarshalerEditModeTests.cs:13-17`. ✓ Никаких рассинхрона.

### 3.J. Риск ломающих изменений в текущем diff

12 модифицированных файлов — **только документационные** правки (XML, тултипы, локализация на английский). Компилятором не ломается ничего:
- `ILlmClient.cs`, `IAiOrchestrationService.cs`, `ICoreAISettings.cs` — XML-ремарки, default-implementation тела идентичны побайтно.
- `AiOrchestrator.cs`, `StubLlmClient.cs`, `LuaTool.cs`, `InventoryTool.cs`, `BuiltInAgentSystemPromptTexts.cs` — XML-перевод, поведение не тронуто.
- `CoreAi.cs` — XML + перевод текста двух `InvalidOperationException` сообщений на английский (337-339, 353-355). **Низкий риск:** сторонний код, который grep-матчит русский текст, упадёт. Маловероятно, но в release-notes стоит указать.
- `CoreAISettingsAsset.cs` — Tooltip-правки, set сериализованных полей не изменился.
- `OfflineLlmClient.cs`, `OpenAiHttpLlmSettings.cs` — Tooltip / `[Header]` правки + удалены русские inline-комментарии.

**Вердикт:** этот коммит безопасен к мержу. Никаких breaking changes для имплементеров `ILlmClient`/`IAiOrchestrationService`/`ICoreAISettings`.

---

## 4. План «что чинить первым» — статус (v1.5.21)

**Закрыто в v1.5.20–v1.5.21:**

| §4 пункт | Результат |
|---|---|
| WebGL `FileConversationSummaryStore` | v1.5.20: in-memory под `UNITY_WEBGL`; см. `RegisterConversationSummaryForCoreAiLifetimeScope`. |
| WebGL `FileAgentMemoryStore` | v1.5.21: `NullAgentMemoryStore` + `NullConversationTranscriptStore` под `UNITY_WEBGL` (без PlayerPrefs-бэкенда — явный компромисс: нет персистентной памяти агента в браузере без кастомного store). |
| Streaming / Solution C | v1.5.21: `CoreAiChatService.IsStreamingEnabled` → `false` для WebGL player; `CoreAiChatPanel.ShouldUseStreamingForRole`; см. [`STREAMING_WEBGL_TODO.md`](STREAMING_WEBGL_TODO.md). |
| Тихие `catch { }` (CoreAi / chat / tool publisher) | v1.5.21: `Debug.LogWarning` с контекстом. |
| `async void Ask` | v1.5.21: обёртка на `Task` (`RunAskFireAndForgetAsync`), публичное имя метода **`Ask`** сохранено. |
| JSON один канал | v1.5.21: `FileConversationSummaryStore` + transcript в `FileAgentMemoryStore` на Newtonsoft; **`System.Text.Json.dll`** убран из **`CoreAI.Core`**, **`CoreAI.Source`**, тестовых asmdef. |
| Дубликат санитайзера | v1.5.21: **`LlmStructuredPayloadSanitizer`** (`CoreAI.Ai`) vs **`LlmResponseSanitizer`** (`CoreAI.Infrastructure.Llm`). |
| Magic strings / пути | v1.5.21: **`OpenAiHttpConstants`**, **`CoreAiPersistentPaths`**. |
| `DGF_SPEC` / VContainer в Core | v1.5.21: §3.2 исправлен (только MoonSharp в Core asmdef). |
| Reflection tools / IL2CPP | v1.5.21: §3.1 в [`MEAI_TOOL_CALLING.md`](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) (`[Preserve]` / `link.xml`). |
| `Log.Instance` видимость | v1.5.21: **`volatile`** на статическом поле (полная замена service-locator §6 не делалась). |

**Остаётся бэклог (крупные или объёмные):** п.6–7 (static-фасады Core, `IGameLogger` vs `ILog`, конструкторы клиентов на `ICoreAISettings`), п.9 (namespace-переносы), п.10 (кириллица в XML ~1346 вхождений), опциональный **PlayerPrefs/WebGL** store для памяти, **Solution A/B** из `STREAMING_WEBGL_TODO` для настоящего SSE на WebGL.

---

## 5. Что **не нужно** трогать (положительные находки)

- Полностью чистый портативный слой от Unity-зависимостей (asmdef invariant `noEngineReferences: true` соблюдается на 100% в `Assets/CoreAI/Runtime/`).
- `ConfigureAwait(false)` распределение вокруг границы Core/Unity точно соответствует документации.
- Tool-lifecycle adapter chain (`ToolExecutionPolicy → IToolCallEventPublisher → MessagePipeToolCallEventPublisher`) реализован чисто.
- `CoreAISettingsAsset.ToolInvocationMarshaler` корректно отдаёт `UnityMainThreadLlmAsyncMarshaler.Instance` с regression-тестом — не было silent fall-through, как опасался первый прогон аудита.
- MEAI OpenAI path — **`System.Net.Http.HttpClient`** в portable **`MeaiOpenAiChatClient`**; иные Unity-вызовы могут по-прежнему использовать **`UnityWebRequest`** там, где это задокументировано.
- **SSE (HTTP):** `MeaiOpenAiChatClient` читает поток построчно с бюджетом простоя (см. код), без цикла poll **`UnityWebRequest.isDone`**.
- Таймаут чата — `CancelAfterSlim` в `CoreAiChatService.cs:118` (соответствует «WebGL Rule»).
- Dispose / IDisposable hygiene в runtime в порядке — leaked CTS / dropped subscription tokens не найдено.
- `BinaryFormatter`/`XmlSerializer`/`Process.Start`/`Environment.Exit` — отсутствуют.
- Покрытие тестами для `AiOrchestrator`, `LuaTool`, `OfflineLlmClient`, `CoreAISettingsAsset`, marshaler — хорошее.

---

> Снимок по большей части отражает состояние **v1.5.x–v1.6.0**; актуальная линия и номер версии — только [`package.json`](../package.json) и portable [`CHANGELOG.md`](../../CoreAI/CHANGELOG.md).
