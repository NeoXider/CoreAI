# TODO — CoreAI: Что не хватает для полной реализации архитектуры
**Обновлено:** 2026-05-25 | **Текущая версия (UPM):** `com.nexoider.coreai` и `com.nexoider.coreaiunity` — **2.5.0**.

---

## 🔴 КРИТИЧНО — решить в ближайших версиях

> ✅ **Все критичные пункты закрыты.** См. архив ниже.

*Сейчас нет открытых критичных задач.*

---

## 🟡 ВАЖНО — следующие версии

*Все задачи из этого раздела были реализованы в v2.1.0–v2.3.0. Новые задачи будут добавлены по мере появления.*

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

- [x] **`LUA_SANDBOX_SECURITY.md`** — что вырезано, какие защиты есть (steps / timeout), известные векторы атак, best practices для `LuaApiRegistry`.
- [x] **`TOOL_CALLING_BEST_PRACTICES.md`** — как делать идемпотентные тулы, когда ставить `AllowDuplicates=true`, как правильно возвращать ошибки, как использовать SkillSet для организации.

---

## ✅ Сделано (архив)

<details>
<summary>Закрытые задачи (кликни чтобы развернуть)</summary>

### v2.3.0 — Dual-Backend with Auto-Fallback (2026-05-08)
- [x] **FallbackLlmClientDecorator** — primary fail → auto-retry на secondary. Streaming fallback.
- [x] **CoreAISettingsAsset** — 🔄 Fallback Backend секция: `enableFallbackBackend`, `secondaryApiBaseUrl`, `secondaryApiKey`, `secondaryModelName`.
- [x] **LlmPipelineInstaller** — auto-wiring: при `HasValidFallbackBackend` primary оборачивается в `FallbackLlmClientDecorator`.
- [x] **5 EditMode тестов** — primary OK, primary fail, retryable error, cancellation, counter.
- [x] Changelogs, package.json (2.3.0), READMEs, TODO обновлены.

### v2.2.0 — Tool History Truncation & Rate Metrics (2026-05-08)
- [x] **MaxToolCallHistoryMessages** (default 20) — `SmartToolCallingChatClient.TrimToolCallHistory()` удаляет старые пары Assistant+Tool.
- [x] **RateLimiterMetrics** struct — `MaxRequestsPerWindow`, `WindowSeconds`, `AcceptedInWindow`, `TotalRejected`.
- [x] **IInGameLlmChatService.GetRateLimiterMetrics()** — доступ к метрикам из UI/Dashboard.
- [x] **InGameLlmChatService** — `_totalRejected` счётчик отклонённых запросов.
- [x] **CoreAISettingsAsset** — `maxToolCallHistoryMessages` в Inspector 🛡️ Resilience & Safety.
- [x] **maxConsecutiveErrors** — подтверждено, что глобальный retry через `ToolExecutionPolicy` покрывает все сценарии. Per-tool retry не нужен.
- [x] Changelogs, package.json (2.2.0), TODO обновлены.

### v2.1.0 — Production Resilience (2026-05-08)
- [x] **MaxToolResultChars** (default 8000) — soft-truncation в `ToolExecutionPolicy`, `[…truncated]` суффикс.
- [x] **DefaultToolTimeoutMs** (default 30000) — linked `CancellationTokenSource` в `ToolExecutionPolicy.ExecuteSingleAsync`.
- [x] **MaxResponseChars** (default 0/выкл) — truncation в `SmartToolCallingChatClient`.
- [x] **MaxToolCallRoundtrips** (default 10) — loop guard в `SmartToolCallingChatClient`.
- [x] **ICoreAISettings** — 4 новых свойства с дефолтами.
- [x] **CoreAISettingsAsset** — Inspector foldout 🛡️ Resilience & Safety с тултипами.
- [x] **ResilienceFeaturesEditModeTests** — 8 тестов (truncation, timeout, roundtrips).
- [x] Anti-thinking prompt instructions в PlayMode тестах для Qwen3.5.
- [x] Changelogs, READMEs, AGENT_BUILDER.md обновлены.

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

### v2.5.0 - Documentation debt closure (2026-05-25)
- [x] Added `LUA_SANDBOX_SECURITY.md`.
- [x] Added `TOOL_CALLING_BEST_PRACTICES.md`.
- [x] Synced documentation index/package version references with `2.5.0`.
- [x] Removed generated no-op comments from CoreAI Unity source.

</details>
