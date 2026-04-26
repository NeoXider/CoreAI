# TODO — CoreAI: Что не хватает для полной реализации архитектуры
**Обновлено:** 2026-04-26 | **Текущая версия:** v0.25.2

## 🚧 Open — приоритет на 0.26.x

### WebGL streaming SSE (регрессия 0.25.x)

- [ ] **0.26.0** — `protected virtual bool ShouldUseStreamingForRole(string roleId, bool uiFallback)` hook на `CoreAiChatPanel`. Дефолтная реализация: возвращает `false` под `#if UNITY_WEBGL && !UNITY_EDITOR`, во всех остальных случаях — текущая логика `_chatService.IsStreamingEnabled(...)`. Это убирает «бесконечный typing-индикатор + пустой bubble» во всех проектах на CoreAI без правок прикладного кода.
- [ ] **0.27.0** — настоящий fetch-SSE-bridge через `.jslib`-плагин в `Runtime/Plugins/WebGL/`. Использует `fetch(url).body.getReader()` для инкрементальной доставки SSE-чанков обратно в C# через `[DllImport("__Internal")]`-callback. Включается опциональным флагом `CoreAISettings.WebGlNativeStreaming`. Старая non-streaming-ветка остаётся как fallback.
- Полная диагностика, причина, шаги — [`Assets/CoreAiUnity/Docs/STREAMING_WEBGL_TODO.md`](Assets/CoreAiUnity/Docs/STREAMING_WEBGL_TODO.md).
- Текущий workaround (на стороне приложения, см. RedoSchool) — рефлекторно гасить `CoreAiChatConfig._enableStreaming = false` в `Awake()` под WebGL.

---

## 🎯 ПРИОРИТЕТНЫЕ ЗАДАЧИ

### ✅ Сделано (Недавнее — v0.20.x)

- [x] **Streaming End-to-End** — работает для обоих бэкендов (HTTP API через SSE и LLMUnity через callback). Единый `ThinkBlockStreamFilter` (state-machine) корректно обрабатывает `<think>`/`</think>` разбитые между чанками. Правильная отмена через `webReq.Abort()` при `CancellationToken`.
- [x] **Streaming config hierarchy** — 3 слоя: UI (`CoreAiChatConfig.EnableStreaming`) → per-agent (`AgentBuilder.WithStreaming`, `AgentMemoryPolicy.SetStreamingEnabled`) → global (`CoreAISettings.EnableStreaming`).
- [x] **Universal Chat Module** — `CoreAiChatPanel` + `CoreAiChatService` + `CoreAiChatConfig` (ScriptableObject) + UXML/USS + авто-создание демо-сцены через меню `CoreAI → Setup → Create Chat Demo Scene`.
- [x] **SceneLlmTool / CameraLlmTool** — инспекция сцен, снимки камеры в PlayMode.
- [x] **Защита от реентерабельных дедлоков Unity Thread Context** в MEAI pipeline (через `Task.Yield()`).
- [x] **Умная защита от застревания** — `SmartToolCallingChatClient`: детектирование дубликатов `tool_call`, блокировка бесконечных петель.
- [x] **Robust Tool Parsing** — защита парсера JSON от забытых бэктиков, обрезка `<think>` тегов.

### Инфраструктура и Архитектура

- [x] Заменить статический god-object `CoreAISettings` на DI-интерфейс `ICoreAISettings` → MemoryTool, InventoryTool, GameConfigTool, AgentBuilder, BuiltInAgentSystemPromptTexts мигрированы
- [x] Реализовать боевые метрики оркестрации → `InMemoryAiOrchestrationMetrics` (per-role, latency, health)
- [x] Dashboard для метрик (`OrchestrationDashboard`, F9 toggle)
- [x] Версионирование системных промптов → `IPromptVersionRegistry` (history, rollback, A/B variants)
- [x] Rate limiting для `InGameLlmChatService` → sliding-window (10 req/60s default)

### WorldCommand Executor

- [x] Анимации: `play_animation`, `stop_animation`
- [x] Звуки: `play_sound`, `set_volume`
- [x] UI: `show_text`, `hide_panel`, `update_score`
- [x] Физика: `apply_force`, `set_velocity`
- [x] Валидация параметров (`ValidateSpawnPosition` via `Physics.OverlapSphere`)

### Продвинутые Инструменты Агентов

- [ ] `CraftingTool` — специализированная функция для расчёта крафта для CoreMechanicAI
- [x] `CompatibilityChecker` — проверка совместимости ингредиентов
- [x] `JsonSchemaValidator` для CoreMechanicAI
- [x] `CompatibilityLlmTool` — LLM tool wrapper

### Multi-Agent Orchestration v2.0

- [ ] Автоматизированный `MultiAgentWorkflow` (агенты сами вызывают pipeline сабагентов, как в Claude Agent SDK)
- [ ] Передача результатов между суб-агентами без главного потока (tool_result)
- [ ] Условная логика вызова (если качество > 80, вызвать Programmer)
- [ ] Параллельное исполнение задач несколькими агентами

---

## 🔍 НАЙДЕНО ПРИ АУДИТЕ (v0.20.2)

### 🛡️ Sandbox / Защиты / Безопасность

- [ ] **`LuaCoroutineHandle.Kill()` не прерывает корутину по-настоящему** — сейчас внутри пустые `try {} catch {}` блоки, только выставляется `_disposed = true`. Если корутина уже выполняется в `Resume()` на другом стеке (не наш случай, но всё же), Kill не сработает. Нужно либо удалить мёртвый код, либо реально прервать через `ScriptRuntimeException` в debugger. Покрыто тестом `Kill_MarksHandleDisposed`, но реализация требует чистки.
- [ ] **`SecureLuaEnvironment.CreateScript`** дважды цепляет `InstructionLimitDebugger` — сначала сам, потом `RunChunk → LuaExecutionGuard.Execute` цепляет ещё один и снимает его в finally. Нижний debugger уже сброшен. Это не баг (работает), но архитектурно неочевидно. Рефакторинг: вынести attach/detach целиком в `LuaExecutionGuard` или в `SecureLuaEnvironment.RunChunk`.
- [ ] **Sandbox-тесты на побег через метатаблицы** — добавлен базовый тест на `getmetatable('')`, но не проверены другие векторы: `string.dump`, `coroutine.close`, `collectgarbage("count")` как timing-oracle, доступ к `_G` через `_ENV`. Нужна отдельная suite `LuaSandboxEscapeTests`.
- [ ] **Нет таймаута по длине ответа модели** — если модель льёт бесконечный стрим, `IAsyncEnumerable` ничем не ограничен. Добавить `maxResponseTokens` / `maxResponseChars` с пробросом `CancellationToken` при превышении.

### 🛠️ Tool Calling

- [ ] **`SmartToolCallingChatClient.GetStreamingResponseAsync` — просто проксирует** `_innerClient.GetStreamingResponseAsync` без tool-calling loop, duplicate detection и consecutive-error защиты. Для стриминговых ответов защита от зацикливания ВЫКЛЮЧЕНА. Нужно либо реализовать streaming tool-calling (MEAI это поддерживает через `StreamingResponseUpdate.Contents`), либо явно документировать ограничение и форсить non-streaming при наличии тулов.
- [ ] **`SmartToolCallingChatClient` определение успеха** — сейчас `string.Contains("\"Success\":false")`. Это бьётся на тулах, где в **аргументах** пользователь случайно попросил поиск строки `Success:false`, или когда результат содержит экранированный JSON. Нужен честный JSON parse (уже используется `Newtonsoft.Json` — можно попробовать `JObject.Parse().Value<bool>("Success")`).
- [ ] **Tool result truncation** — длинные результаты тулов (например `get_hierarchy` в большой сцене) могут переполнить context window. Добавить `maxToolResultChars` с мягким truncation и префиксом `[...truncated]`.
- [ ] **Tool timeout** — отдельный инструмент не имеет таймаута (пустой `CancellationToken` пробрасывается). Нужен per-tool timeout (`[LlmTool(TimeoutMs=5000)]`), особенно для внешних HTTP-вызовов.
- [ ] **Tool-level AllowDuplicates** — работает только для проверки дубликатов, но не для tool-specific retry policy. Полезно было бы добавить `MaxConsecutiveErrors` на тул.

### 🌀 Lua Runtime (async + coroutines)

- [ ] **`LuaCoroutineRunner` нет лимита на количество корутин** — бесконечно растущий `_handles` при багах LLM (каждый Lua envelope создаёт корутину). Добавить `MaxActiveCoroutines = 64` с отклонением регистрации сверх лимита.
- [ ] **Нет async-API для Lua** — из Lua нельзя дождаться async-операций C# (например, `await llm.complete(...)` из тула). Сейчас приходится делать polling через `coroutine.yield()` + проверку `is_ready()`. Желательно: `LuaAsyncBridge` с `await_task(task_id)` через Promise-семантику.
- [ ] **Нет rate limit на создание Lua-скриптов** — Programmer может создать 1000 скриптов в секунду при зацикливании. Добавить sliding-window limiter на уровне `LuaAiEnvelopeProcessor`.
- [ ] **Repair loop на CoreMechanicAI** — сейчас только Programmer триггерит `ScheduleProgrammerRepair`. Для ошибок Lua у CoreMechanicAI (когда он пытается выполнить формулу крафта, а она падает) нужно либо расширить поддержку, либо явно направлять в Programmer.

### ⚡ Performance / Ресурсы

- [ ] **Нет metrics для rate limiter'а** — сколько запросов было отклонено за последние N минут? Нужен `IRateLimiterMetrics` и отображение в `OrchestrationDashboard`.
- [ ] **`InGameLlmChatService._lock` — coarse-grained** — блокирует и rate limiter, и историю. При пиковой нагрузке это может стать bottleneck. Разделить на `_rateLock` и `_historyLock`.
- [ ] **Tool call history никогда не очищается** — `SmartToolCallingChatClient.executedSignatures` живёт только внутри одного `GetResponseAsync`, но `messages` в длинной сессии растёт (каждый вызов добавляет 2 сообщения). Добавить truncation старых tool calls через N раундов.

### 🧪 Тесты

- [x] **`SecureLuaSandboxEditModeTests`** — явные проверки вырезания `io`, `os`, `debug`, `load`, `loadfile`, `dofile`, `require`; `LuaExecutionGuard` (timeout / max steps / fast code / non-function arg); `LuaCoroutineHandle` (Resume, Kill, `ObjectDisposedException`, `budgetPerResume`).
- [x] **`LuaToolEditModeTests`** — `LuaTool.ExecuteAsync` (success, empty, null, throws, cancellation), `CreateAIFunction`, валидация `null`-аргументов, `LuaLlmTool` metadata.
- [x] **`SmartToolCallingChatClientEditModeTests`** — duplicate detection (`allowDuplicateToolCalls=false`), per-tool `AllowDuplicates=true` override, tool not found, tool throws exception.
- [x] **`InGameLlmChatServiceEditModeTests`** — rate limiter: превышение окна, `maxRequestsPerWindow=0`, отклонённый запрос не попадает в историю, скольжение окна.
- [x] **`ThinkBlockStreamFilterEditModeTests`** — полное покрытие production-класса.
- [x] **`CoreAiChatServiceEditModeTests`** — 3-слойная иерархия streaming, SmartSend.
- [ ] **`SmartToolCallingStreamingTests`** — когда реализуется streaming tool-calling (пункт выше).
- [ ] **`LuaSandboxEscapeTests`** — попытки побега из sandbox через метатаблицы / `string.dump` / rebind `_G`.
- [ ] **`MultiAgentWorkflowEndToEndTests`** — когда реализуется v2.0.
- [ ] **`ToolTimeoutTests`** — когда появится per-tool timeout.
- [x] `QueuedAiOrchestrator` — приоритет, CancellationScope, MaxConcurrent.

### 📚 Документация

- [x] `COMMAND_FLOW_DIAGRAM.md` (как команда игрока проходит через систему)
- [x] `JSON_COMMAND_FORMAT.md` (формат JSON команд для каждой роли)
- [x] `TROUBLESHOOTING.md` (модель не отвечает, Lua упала, память не пишется)
- [x] `QUICK_START_FULL.md` (LM Studio → сцена → команда)
- [x] `EXAMPLES.md` (враги, крафт, auto-repair)
- [x] `DEMO_RECORDING_GUIDE.md`
- [ ] **`STREAMING_ARCHITECTURE.md`** — описание SSE → `ThinkBlockStreamFilter` → `CoreAiChatPanel` pipeline, 3-слойная иерархия конфигурации, известные ограничения (streaming без tool-calling loop).
- [ ] **`LUA_SANDBOX_SECURITY.md`** — что вырезано, какие защиты есть (steps / timeout), известные векторы атак, best practices для `LuaApiRegistry`.
- [ ] **`TOOL_CALLING_BEST_PRACTICES.md`** — как делать идемпотентные тулы, когда ставить `AllowDuplicates=true`, как правильно возвращать ошибки.
