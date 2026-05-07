# TODO — CoreAI: Что не хватает для полной реализации архитектуры
**Обновлено:** 2026-05-08 | **Текущая версия (UPM):** `com.nexoider.coreai` и `com.nexoider.coreaiunity` — **2.0.0**.

---

## 🔴 КРИТИЧНО — решить в ближайших версиях

### Tool result truncation (переполнение контекста)

- [ ] Длинные результаты тулов (например `get_hierarchy` в большой сцене, или огромный JSON) могут переполнить context window модели. Добавить `maxToolResultChars` с мягким truncation и префиксом `[...truncated]` в `ToolExecutionPolicy`.
- **Риск:** модель получает обрезанный контекст без предупреждения, теряет важные данные, или вообще падает с `ContextLengthExceeded`.

### Per-tool timeout

- [ ] Отдельный инструмент не имеет таймаута — пробрасывается пустой `CancellationToken`. Если tool делает HTTP-вызов или тяжёлую операцию — весь pipeline висит.
- [ ] Добавить `[LlmTool(TimeoutMs=5000)]` атрибут или `ILlmTool.TimeoutMs` свойство. `ToolExecutionPolicy` создаёт `CancellationTokenSource` с таймаутом и передаёт в `ExecuteAsync`.
- [ ] Тест: `ToolTimeoutTests` — таймаут срабатывает, результат = ошибка, pipeline продолжает.

### Max response length limiter

- [ ] Если модель льёт бесконечный стрим, `IAsyncEnumerable` ничем не ограничен. Добавить `maxResponseChars` / `maxResponseTokens` с пробросом `CancellationToken` при превышении.
- **Где:** `AiOrchestrator.RunStreamingAsync` или `MeaiLlmClient.CompleteStreamingAsync`.

---

## 🟡 ВАЖНО — следующие версии

### Dual-backend at runtime (primary + secondary)

- [ ] `CoreAISettings` получает поле `secondaryBackend` (`LlmBackendType?`, по умолчанию `null`). При не-`null`:
  - `CoreAILifetimeScope` регистрирует **оба** клиента и заворачивает в `RoutingLlmClient` через `LlmRoutingManifest`.
  - В Editor — секция «Secondary backend» с симметричным набором полей.
  - Per-role routing: «primary», «secondary», «auto-fallback (primary→secondary on error)».
- [ ] EditMode-тест: `RoutingLlmClient` переключается на secondary при ошибке; per-role override уважается.
- [ ] Обновить `DEVELOPER_GUIDE.md` §4.
- **Контекст:** инфраструктура (`RoutingLlmClient`, `LlmRoutingManifest`, `ILlmClientRegistry`) уже есть, но не подключена в DI flow и не доступна через инспектор.

### Tool call history truncation

- [ ] `messages` в длинной сессии растёт бесконечно (каждый tool call добавляет 2 сообщения в историю). Добавить truncation старых tool calls через N раундов в `SmartToolCallingChatClient`.

### Tool-level retry/duplicate policy

- [ ] `AllowDuplicates` работает только для детекции дубликатов, но не для tool-specific retry. Полезно добавить `MaxConsecutiveErrors` на конкретный тул.

### Rate limiter метрики

- [ ] Сколько запросов отклонено за последние N минут? `IRateLimiterMetrics` + отображение в `OrchestrationDashboard`.

---

## 🔵 УЛУЧШЕНИЯ — когда будет время

### Multi-Agent Orchestration (future)

- [ ] Автоматизированный `MultiAgentWorkflow` — агенты сами вызывают pipeline суб-агентов (как в Claude Agent SDK).
- [ ] Передача результатов между суб-агентами без главного потока (`tool_result`).
- [ ] Условная логика вызова (если качество > 80, вызвать Programmer).
- [ ] Параллельное исполнение задач несколькими агентами.
- [ ] Тест: `MultiAgentWorkflowEndToEndTests`.

### CraftingTool

- [ ] Специализированная функция для расчёта крафта для CoreMechanicAI.

### Lua Runtime улучшения

- [ ] **Lua coroutine limit** — `LuaCoroutineRunner` нет лимита на количество корутин. `MaxActiveCoroutines = 64` с отклонением сверх лимита.
- [ ] **Lua async-API** — из Lua нельзя дождаться async-операций C#. Желательно: `LuaAsyncBridge` с `await_task(task_id)` через Promise-семантику.
- [ ] **Lua script rate limit** — Programmer может зациклить создание скриптов. Sliding-window limiter на `LuaAiEnvelopeProcessor`.
- [ ] **Repair loop на CoreMechanicAI** — ошибки Lua у CoreMechanicAI нужно направлять в Programmer.

### Sandbox чистка

- [ ] **`LuaCoroutineHandle.Kill()`** — сейчас внутри пустые `try {} catch {}`, только `_disposed = true`. Либо удалить мёртвый код, либо реально прервать через `ScriptRuntimeException`.
- [ ] **`SecureLuaEnvironment.CreateScript`** — дважды цепляет `InstructionLimitDebugger`. Рефакторинг: вынести attach/detach целиком в `LuaExecutionGuard`.
- [ ] **Sandbox escape тесты** — `string.dump`, `coroutine.close`, `collectgarbage("count")` как timing-oracle, `_G` через `_ENV`. Suite: `LuaSandboxEscapeTests`.

---

## 📚 Документация — недостающие файлы

- [ ] **`LUA_SANDBOX_SECURITY.md`** — что вырезано, какие защиты есть (steps / timeout), известные векторы атак, best practices для `LuaApiRegistry`.
- [ ] **`TOOL_CALLING_BEST_PRACTICES.md`** — как делать идемпотентные тулы, когда ставить `AllowDuplicates=true`, как правильно возвращать ошибки, как использовать SkillSet для организации.

---

## ✅ Сделано (архив)

<details>
<summary>Закрытые задачи (кликни чтобы развернуть)</summary>

### v2.1.0 — Self-Service Skills (2026-05-08)
- [x] **Self-service skill pattern** — модель сама вызывает `read_skill(name)` для загрузки инструкций по требованию (паттерн Cursor `read_file`).
- [x] **SkillSet API** — добавлен `Description` (короткое описание для каталога), `BuildCatalog()` для лёгкого каталога в промпте.
- [x] **ReadSkillLlmTool** — мета-тул `read_skill`, автоматически регистрируется при `WithSkill()`. Case-insensitive, fuzzy matching.
- [x] **SkillRuntimeContextProvider** — инъектирует каталог (не полные инструкции) в system prompt.
- [x] **AgentMemoryPolicy.AddToolForRole()** — добавление одного тула к роли.
- [x] **SkillSetAsset** (CoreAiUnity) — ScriptableObject для удобного создания скиллов через Inspector (TextAsset + inline инструкции).
- [x] **EditMode тесты** — конструкторы, каталог, read_skill (known/unknown/case-insensitive), AgentBuilder интеграция.
- [x] **PlayMode тесты** — FastNoLlm (каталог в промпте, read_skill зарегистрирован, AllowedToolNames совместимость), LLM реальный тест.
- [x] Обновлены `AGENT_BUILDER.md`, benchmark тесты.
- [x] Аудит готовых библиотек: Semantic Kernel (❌ .NET 8+), LLMTornado (❌ .NET 8+), MEAI (✅ уже используется).

### v2.0.0 — SkillSet Manual Mode (2026-05-08)
- [x] **SkillSet** — именованные группы инструментов с промпт-инструкциями (паттерн Semantic Kernel KernelPlugin).
- [x] **AgentBuilder.WithSkill / WithSkills** — fluent API регистрации скиллов.
- [x] **SkillSet.FromFile / FromTextContent** — загрузка инструкций из файлов и TextAsset.

### v1.6.0+ — WebGL SSE streaming
- [x] **fetch-SSE / jslib** — `CoreAiSseFetch.jslib`, `FetchSseOpenAiTransport`, `WebGlNativeStreaming` (по умолчанию **вкл** с v1.6.13).
- [x] **STREAMING_ARCHITECTURE.md** — полное описание pipeline.

### v1.5.x — Архитектура и аудит
- [x] Замена статического `CoreAISettings` на DI-интерфейс `ICoreAISettings`.
- [x] Метрики оркестрации → `InMemoryAiOrchestrationMetrics`.
- [x] Dashboard (`OrchestrationDashboard`, F9).
- [x] Версионирование промптов → `IPromptVersionRegistry`.
- [x] Rate limiting для `InGameLlmChatService`.
- [x] `ARCH-1..9` аудит (thread safety, lock consolidation, BUG-1..8).
- [x] `SmartToolCallingChatClient` — определение успеха через `JObject.Parse`.
- [x] `InGameLlmChatService._lock` — разделён на `_rateLock` и `_historyLock`.
- [x] `InMemoryAiOrchestrationMetrics` — bounded storage MaxRoles=256.

### v0.20.x — Streaming & Tools
- [x] Streaming End-to-End (HTTP SSE + LLMUnity callback).
- [x] Streaming config hierarchy (3 слоя).
- [x] Universal Chat Module.
- [x] `ThinkBlockStreamFilter`.
- [x] `SmartToolCallingChatClient` — дубликаты, бесконечные петли.
- [x] Robust Tool Parsing — JSON fence, `<think>` теги.

### WorldCommand Executor
- [x] Анимации, звуки, UI, физика, валидация.

### Продвинутые инструменты
- [x] `CompatibilityChecker`, `JsonSchemaValidator`, `CompatibilityLlmTool`.

### Тесты
- [x] `SecureLuaSandboxEditModeTests`, `LuaToolEditModeTests`.
- [x] `SmartToolCallingChatClientEditModeTests`.
- [x] `InGameLlmChatServiceEditModeTests`.
- [x] `ThinkBlockStreamFilterEditModeTests`.
- [x] `CoreAiChatServiceEditModeTests`.
- [x] `QueuedAiOrchestrator` tests.

### Документация
- [x] `COMMAND_FLOW_DIAGRAM.md`, `JSON_COMMAND_FORMAT.md`.
- [x] `TROUBLESHOOTING.md`, `QUICK_START_FULL.md`.
- [x] `EXAMPLES.md`, `DEMO_RECORDING_GUIDE.md`.

</details>
