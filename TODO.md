# TODO — CoreAI: critical stability and implementation gaps

**Обновлено:** 2026-06-10 | **Текущая версия (UPM):** `com.nexoider.coreai` и `com.nexoider.coreaiunity` — **2.5.0**.

---

## 🔴 КРИТИЧЕСКИЕ (P1) — нужно закрывать в ближайших версиях

### 1) Гигиена репозитория

- [ ] **[P1] Очистить корень проекта от артефактов и зафиксировать .gitignore**
  - Удалить из репозитория и добавить в `.gitignore`: `debug.log`, `memory.db`, `msp_server.log`, `replay_pid29472.log`, `TestRun_EditMode.log`, `UnityTest_EditMode.log`, файл `Remove-Item`, `\_coreai_placeholder_lines.txt`.
  - Провести audit `git status`/`git ls-files` на предмет мусора перед релизом.

### 2) Lua-песочница / DoS-устойчивость

- [ ] **[P1] Ввести лимит активных корутин в `LuaCoroutineRunner`**
  - Реализовать лимит (цель: `MaxActiveCoroutines = 64`) и отказ/очередь новых корутин при переполнении.
- [ ] **[P1] Закрыть `LuaCoroutineHandle.Kill()`**
  - Метод сейчас почти не останавливает корутину фактически; заменить пустой `try/catch`-магией на реальное завершение или гарантированную остановку.
- [ ] **[P1] Добавить sandbox-escape тесты**
  - Обязательные проверки: `string.dump`, `coroutine.close`, `collectgarbage("count")`, доступ к `_G` через `_ENV`.
- [ ] **[P1] Добавить rate-limit на генерацию Lua-скриптов**
  - `LuaAiEnvelopeProcessor`: защита от self-DOS/Programmer-loop при частой генерации скриптов.
- [ ] **[P1] Устранить двойное подключение `InstructionLimitDebugger`**
  - Проверить и устранить дублирующий `InstructionLimitDebugger`-hook в `SecureLuaEnvironment.CreateScript`.

### 3) CoreAi / CoreAIAgent.Policy и runtime-state

- [ ] **[P1] `Build()` должен регистрировать в policy по умолчанию**
  - Сейчас конфигурация из `AgentConfigBuilder.Build()` не применяется без `ApplyToPolicy`.
  - Требуется: `Build()` = auto-apply в policy; `BuildDetached()` для «чистого» конфигуратора.
- [ ] **[P1] При отсутствии регистрации роли — fail fast в `Ask()`**
  - Вместо fallback `"Blacksmith"` бросать понятную ошибку (`role not registered`) если роль не добавлена в policy.
- [ ] **[P1] Сброс статических полей при Play Mode без domain reload**
  - Добавить `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` очистку static-состояний `CoreAi`/`CoreAIAgent`.
- [ ] **[P1] Поддержать замену resolver для тестов**
  - Добавить `CoreAi.SetResolver(Func<IAiOrchestrationService>)` (или аналог) для подмены orchestrator в CI/тестах.

### 4) `SmartToolCallingChatClient` / ретраи

- [ ] **[P1] Логировать и считать `TryRepairToolName` (например, `MEMORY` → `memory`)**
  - Добавить Warning-лог и метрику/счётчик в telemetry, чтобы видеть деградацию prompt-нейминга.
- [ ] **[P1] Обезвредить feedback-циклы ретраев**
  - После успешного retry удалять error-feedback из истории; сейчас мусор нарастает и провоцирует повторные ошибки.
- [ ] **[P1] `TrimToolCallHistory` должен поддерживать инвариант assistant→tool-result**
  - Никогда не оставлять `tool-result` без предшествующего `assistant` с `tool_call`, иначе OpenAI-совместимые API дают 400.

### 5) HttpClient / сетевой lifecycle

- [ ] **[P1] Проверить и зафиксировать reuse единого `HttpClient` на backend**
  - Исключить per-request `HttpClient` (socket exhaustion), унифицировать lifecycle shared/ singleton factory.

### 6) Память / I/O

- [ ] **[P1] Атомарные JSON-записи в `FileConversationSummaryStore`**
  - Заменить прямой `File.WriteAllText` на `.tmp + File.Replace` для crash-safe записи.
- [ ] **[P1] Перенос `persistentDataPath` I/O off main thread**
  - `Read/Write/Delete` в `Task.Run`, чтобы убрать возможные фризы на больших историях.

### 7) API-дизайн (cleanup и эргономика)

- [ ] **[P2] Дедупликация `ILlmTool`**
  - Убрать дублирование `Name/Description` между `ILlmTool` и `CreateAIFunctions`; ввести `LlmToolBase` для генерации функции из свойств.
- [ ] **[P2] Рефактор `merchant.Ask` callback API**
  - Оставить единый `UniTask`-idiom как primary (`AskAsync`).
  - Устаревший callback вынести в отдельный отдельный метод `AskWithCallback(...)`, не как перегрузку.
- [ ] **[P2] Константы роли вместо magic strings**
  - Обеспечить централизованные константы (`RoleId`/`BuiltInAgentRoleIds`) для `SmartChat`, `Blacksmith` и др.; убрать опечатки в рантайме.

### 8) Runtime token/cost dashboard

- [ ] **[P1] Добавить визуальный runtime overlay стоимости и токенов**
  - Отдельный dev/QA overlay на базе `RateLimiterMetrics`: токены/запрос, `$ / сессия`, скользящее окно, средний throughput/latency.

---

## 🟡 ВАЖНО — следующий цикл (P2/P3)

### Multi-Agent Orchestration (future)

- [ ] **[P2] Автоматизированный `MultiAgentWorkflow`**
- [ ] **[P2] Передача результатов между суб-агентами через `tool_result`**
- [ ] **[P2] Пороговое ветвление задач (если качество ниже порога — эскалация)**
- [ ] **[P2] Параллельное исполнение подзадач несколькими агентами**
- [ ] **[P2] Тест: `MultiAgentWorkflowEndToEndTests`**

### CraftingTool

- [ ] **[P2] Специализированная функция расчёта крафта для CoreMechanicAI**

### Lua Runtime улучшения

- [ ] **[P2] Lua async-API для ожидания C# task**
  - `LuaAsyncBridge` + `await_task(task_id)` (Promise-семантика).
- [ ] **[P2] CoreMechanicAI repair loop**
  - Автоперенаправление ошибок Lua на Programmer через workflow.

### Идеи (под вопросом)

- [ ] **[Idea] Voice pipeline для NPC (STT → Agent → TTS)**
  - Локальный `whisper`, голосовой ввод/вывод как killer-фича.
- [ ] **[Idea] Визуальный `AgentBuilder` в редакторе**
  - Построение промптов/агентов UI-редактором без написания кода.
- [ ] **[Idea] Streaming-emotions / function-driven анимации**
  - Возврат эмоций/жестов вместе с текстом для пайплайна анимации.

---

## 📚 Документация — закрытые задачи

- [x] **`LUA_SANDBOX_SECURITY.md`** — список защит, known vectors, шаги hardening, best practices.
- [x] **`TOOL_CALLING_BEST_PRACTICES.md`** — идемпотентность тулов, ошибки, skill set usage.

---

## ✅ Сделано (архив)

<details>
<summary>Закрытые задачи (кликни чтобы развернуть)</summary>

### v2.3.0 — Dual-Backend with Auto-Fallback (2026-05-08)
- [x] **FallbackLlmClientDecorator** — primary fail → auto-retry на secondary. Streaming fallback.
- [x] **CoreAISettingsAsset** — fallback backend секция: `enableFallbackBackend`, `secondaryApiBaseUrl`, `secondaryApiKey`, `secondaryModelName`.
- [x] **LlmPipelineInstaller** — auto-wiring на fallback при валидной конфигурации.
- [x] **5 EditMode тестов** — primary OK, primary fail, retryable error, cancellation, counter.
- [x] Changelogs, package.json (2.3.0), README, TODO обновлены.

### v2.2.0 — Tool History Truncation & Rate Metrics (2026-05-08)
- [x] **MaxToolCallHistoryMessages** (default 20) — `SmartToolCallingChatClient.TrimToolCallHistory()`.
- [x] **RateLimiterMetrics** struct — лимиты и counters.
- [x] **IInGameLlmChatService.GetRateLimiterMetrics()** — API для UI/Dashboard.
- [x] **InGameLlmChatService** — `_totalRejected` счётчик отклонённых запросов.
- [x] **CoreAISettingsAsset** — `maxToolCallHistoryMessages` в Inspector (Resilience).
- [x] **`maxConsecutiveErrors`** — подтверждено global retry через `ToolExecutionPolicy`.
- [x] Changelogs, package.json (2.2.0), TODO обновлены.

### v2.1.0 — Production Resilience (2026-05-08)
- [x] **MaxToolResultChars** (default 8000) — soft-truncation.
- [x] **DefaultToolTimeoutMs** (default 30000) — cancelable tool execution.
- [x] **MaxResponseChars** (default 0/выкл) — truncation ответов.
- [x] **MaxToolCallRoundtrips** (default 10) — guardrail в retry loop.
- [x] **ICoreAISettings** — новые свойства, defaults.
- [x] **CoreAISettingsAsset** — Inspector UI для устойчивости.
- [x] **ResilienceFeaturesEditModeTests** — 8 тестов.
- [x] Anti-thinking prompt instructions для PlayMode тестов на Qwen3.5.
- [x] Changelogs, READMEs, AGENT_BUILDER.md обновлены.

### v2.1.0 — Self-Service Skills (2026-05-08)
- [x] **Self-service skill pattern** — `read_skill(name)`.
- [x] **SkillSet API** — описание + каталог и инъекция в system prompt.
- [x] **ReadSkillLlmTool** — мета-тул `read_skill` с fuzzy matching.
- [x] **SkillRuntimeContextProvider** — инъект каталога в системный промпт.
- [x] **AgentMemoryPolicy.AddToolForRole()** — регистрация доп. tool к роли.
- [x] **SkillSetAsset** — ScriptableObject для редактора.
- [x] **EditMode тесты** — конструкторы, каталог, read_skill, AgentBuilder интеграция.
- [x] **PlayMode тесты** — FastNoLlm + real LLM path.
- [x] Обновлён `AGENT_BUILDER.md`.
- [x] benchmark тесты.
- [x] Тех-аудит сторонних библиотек.

### v2.0.0 — SkillSet Manual Mode (2026-05-08)
- [x] **SkillSet** — именованные группы инструментов.
- [x] **AgentBuilder.WithSkill / WithSkills** — fluent регистрация.
- [x] **SkillSet.FromFile / FromTextContent** — загрузка инструкций.

### v1.6.0+ — Streaming & Tools
- [x] **fetch-SSE / jslib** — WebGL streaming transport.
- [x] **STREAMING_ARCHITECTURE.md** — описание pipeline.

### v1.5.x — Архитектура и устойчивость
- [x] DI `CoreAISettings`.
- [x] Метрики оркестрации `InMemoryAiOrchestrationMetrics`.
- [x] Dashboard (`OrchestrationDashboard`, F9).
- [x] Версионирование промптов.
- [x] Rate limiting для `InGameLlmChatService`.
- [x] `ARCH-1..9` аудит.
- [x] `SmartToolCallingChatClient` — robust tool parsing.
- [x] Разделение locks в `InGameLlmChatService`.
- [x] `InMemoryAiOrchestrationMetrics` — bounded storage MaxRoles=256.

### v0.20.x — Streaming & Tools
- [x] Streaming End-to-End (HTTP SSE + LLMUnity callback).
- [x] Streaming config hierarchy (3 слоя).
- [x] Universal Chat Module.
- [x] `ThinkBlockStreamFilter`.
- [x] Robust Tool Parsing (`JSON` fence, `<think>` теги).

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

