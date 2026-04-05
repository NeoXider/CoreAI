# TODO — CoreAI: Что не хватает для полной реализации архитектуры

**Обновлено:** 2026-04-06 | **Текущая версия:** v0.7.0

## ✅ ВЫПОЛНЕНО В v0.7.0 (v0.7.0 - 2026-04-06)

### Единый MEAI Tool Calling Format

- ✅ `{"name": "tool_name", "arguments": {...}}` - единый формат для всех tool calls
- ✅ `LuaTool.cs` + `LuaLlmTool.cs` - MEAI инструмент для Programmer
- ✅ `InventoryTool.cs` + `InventoryLlmTool.cs` - MEAI инструмент для Merchant NPC
- ✅ `CoreAISettings.cs` - публичные настройки (MaxLuaRepairGenerations=3, MaxToolCallRetries=3)
- ✅ `AgentMemoryPolicy.SetToolsForRole()` - добавление кастомных инструментов
- ✅ `LlmUnityMeaiChatClient.TryParseToolCallFromText()` - парсинг Qwen-style форматов
- ✅ **Merchant Agent** - новый NPC-торговец с инструментами (get_inventory + memory)
- ✅ **AgentBuilder** - конструктор кастомных агентов (3 режима: ToolsOnly, ToolsAndChat, ChatOnly)
- ✅ **WithChatHistory()** - сохранение истории диалога (контекст сессии, в RAM)
- ✅ **WithMemory()** - персистентная память (между сессиями, в JSON файл)
- ✅ **AgentMode** - enum режимов работы агента
- ✅ Удалён `AgentMemoryDirectiveParser` - всё через MEAI pipeline
- ✅ Обновлены все промпты (Programmer, Merchant)
- ✅ Обновлены все PlayMode тесты под v0.7.0 формат

### Доступные Tools

| Tool | Назначение | Агент |
|------|-----------|-------|
| `memory` | Сохранение/добавление/очистка памяти | Все агенты |
| `execute_lua` | Выполнение Lua скриптов | Programmer |
| `get_inventory` | Получение инвентаря NPC | Merchant |
| `game_config` | Чтение/запись конфигов | Creator, другие |

---

## 🚨 КРИТИЧНОЕ (без этого система не работает как задумано)

### 1. ✅ MemoryTool работает для ВСЕХ агентов (v0.7.0 — единый MEAI формат)

**Файл:** `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs`

**Статус:** ✅ ГОТОВО

- ✅ Все 6 ролей используют MemoryTool по умолчанию
- ✅ Append по умолчанию (накапливают историю)
- ✅ Легко вкл/выкл: `policy.DisableMemoryTool("PlayerChat")`
- ✅ Конфигурация на роль: write/append/clear
- ✅ **v0.7.0**: Единый формат tool calls `{"name": "memory", "arguments": {...}}`
- ✅ **v0.7.0**: `AgentMemoryDirectiveParser` удалён — всё через MEAI pipeline

---

### 2. ✅ Есть валидация ответов LLM (RoleStructuredResponsePolicy)

**Файл:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/`

**Статус:** ✅ ГОТОВО

- ✅ `ProgrammerResponsePolicy` — Programmer вызывает `execute_lua` tool (v0.7.0)
- ✅ `CoreMechanicResponsePolicy` — проверка JSON с числами
- ✅ `CreatorResponsePolicy` — проверка JSON объекта
- ✅ `AnalyzerResponsePolicy` — проверка JSON с метриками
- ✅ `AINpcResponsePolicy` — мягкая (JSON или текст)
- ✅ `PlayerChatResponsePolicy` — без валидации
- ✅ `CompositeRoleStructuredResponsePolicy` — маршрутизация по роли
- ✅ Автоматический retry при неудачной валидации (уже заложен в `AiOrchestrator`)
- ✅ **v0.7.0**: ProgrammerResponsePolicy упрощена — пропускает любой текст (tool calls через MEAI)
- ✅ 20 EditMode тестов
- ✅ DI регистрация в CorePortableInstaller

**Влияние:** Если модель отвечает текстом вместо JSON/Lua, система делает retry с подсказкой.

---

### 3. ✅ GameConfig система (универсальная инфраструктура)

**Файлы:** `Assets/CoreAI/Runtime/Core/Features/Config/`

**Статус:** ✅ ГОТОВО

- ✅ `IGameConfigStore` — универсальный интерфейс (load/save JSON по ключу)
- ✅ `GameConfigTool` — ILlmTool для AI function calling (read/update)
- ✅ `GameConfigPolicy` — какие роли могут читать/менять какие ключи
- ✅ `GameConfigLlmTool` — обёртка для MEAI function calling
- ✅ `NullGameConfigStore` — заглушка по умолчанию
- ✅ `UnityGameConfigStore` — реализация на ScriptableObject (CoreAIUnity)
- ✅ DI регистрация в CorePortableInstaller + CoreAILifetimeScope
- ✅ EditMode тесты: 9 тестов (policy, read, update, round-trip)
- ✅ PlayMode тесты: 3 теста (AI read/modify/write, no access, multi-key)
- ✅ `GAME_CONFIG_GUIDE.md` — полная инструкция для разработчиков

**Влияние:** AI может читать и менять любые конфиги игры через function calling. Игра только реализует `IGameConfigStore` для своей системы хранения.

---

## ✅ ВЫПОЛНЕНО В v0.7.0

### 7. ✅ Единый MEAI Tool Calling Format

**Файлы:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/`

**Статус:** ✅ ГОТОВО

- ✅ `LuaTool.cs` — MEAI AIFunction для выполнения Lua скриптов
- ✅ `LuaLlmTool.cs` — ILlmTool обёртка для Programmer
- ✅ `LlmUnityMeaiChatClient.TryParseToolCallFromText()` — парсинг JSON tool calls для моделей без структурных tool_calls
- ✅ `CoreAISettings.cs` — публичные статические настройки:
  - `MaxLuaRepairGenerations = 3` (по умолчанию, было 4)
  - `LlmRequestTimeoutSeconds = 300`
  - `EnableMeaiDebugLogging = false`
- ✅ Единый формат: `{"name": "tool_name", "arguments": {...}}`
- ✅ Удалён `AgentMemoryDirectiveParser.cs`
- ✅ Удалён fallback парсинг в `AiOrchestrator`
- ✅ Обновлена `ProgrammerResponsePolicy` — больше не проверяет fenced блоки
- ✅ Обновлён `LuaAiEnvelopeProcessor` — использует `CoreAISettings.MaxLuaRepairGenerations`

**Тесты:**
- ✅ `MeaiToolCallsEditModeTests.cs` — MemoryTool, LuaTool, парсинг JSON
- ✅ `AllToolCallsPlayModeTests.cs` — боевые тесты с переключением LLMUnity/HTTP
- ✅ `LuaExecutionPipelineEditModeTests.cs` — обновлено max generations (4→3)

**Удалённые тесты:**
- ❌ `AgentMemoryEditModeTests.cs` — использовал удалённый парсер
- ❌ `AgentDataPassingEditModeTests.cs` — старые memory парсинги
- ❌ `MemoryToolMeaiEditModeTests.cs` — дублировал MeaiToolCallsEditModeTests

---

## ⚠️ ВАЖНОЕ (система работает, но не полностью)

### 4. ❌ CoreMechanicAI не имеет специализированных инструментов

**Что есть:**
- ✅ Системный промпт есть
- ✅ RoleId есть
- ✅ Память работает

**Чего нет:**
- [ ] `CraftingTool` — специализированная функция для расчёта крафта (как MemoryTool но для крафта)
- [ ] `LootRollTool` — функция для броска лута
- [ ] `CompatibilityChecker` — проверка совместимости ингредиентов
- [ ] JSON schema validation — проверка что ответ CoreMechanicAI содержит нужные поля

---

### 5. ✅ Lua sandbox timeout

**Файлы:** `LuaExecutionGuard.cs`, `InstructionLimitDebugger.cs`

**Статус:** ✅ УЖЕ БЫЛО ГОТОВО

- ✅ `LuaExecutionGuard` — таймаут 2000мс (по умолчанию)
- ✅ `InstructionLimitDebugger` — лимит 200,000 инструкций
- ✅ Бесконечный цикл НЕ зависнет — бросает `ScriptRuntimeException`
- ✅ Wall-clock проверка после выполнения

---

### 6. ❌ Нет multi-agent orchestration (последовательность агентов)

**Что есть:**
- ✅ `AiOrchestrator` — запускает ОДНУ задачу одного агента
- ✅ `QueuedAiOrchestrator` — очередь задач с приоритетами
- ✅ **Тесты:** `AgentDataPassingEditModeTests.cs` — 4 теста передачи данных

**Чего нет:**
- [ ] `MultiAgentWorkflow` — цепочка: Creator → CoreMechanicAI → Programmer
- [ ] Передача результатов между агентами (output Creator → input CoreMechanicAI)
- [ ] Условная логика: «если CoreMechanicAI вернул качество > 80, вызвать Programmer»
- [ ] Parallel execution: «Analyzer и CoreMechanicAI работают параллельно»

---

### 4. ✅ Analyzer тесты

**Файл:** `Assets/CoreAiUnity/Tests/EditMode/AnalyzerEditModeTests.cs`

**Статус:** ✅ ГОТОВО

- ✅ 10 EditMode тестов:
  - System prompt проверка (не пустой, содержит keywords, отличается от Creator)
  - Телеметрия (получает в user payload, пустая телеметрия)
  - Response validation (валидный JSON, невалидный текст, recommendations)
  - Stub LLM (возвращает JSON ответ)
  - Orchestrator integration (публикует envelope)
- ✅ Проверка что Analyzer НЕ может менять мир напрямую (только рекомендации)

---

### 8. ❌ AINpc и PlayerChat не тестированы

**Что есть:**
- ✅ Системные промпты есть
- ✅ `InGameChatPanel.cs` — UI для чата

**Чего нет:**
- [ ] Тест: PlayerChat отвечает на вопрос игрока
- [ ] Тест: AINpc генерирует реплику в мире
- [ ] Интеграция PlayerChat с UI (есть `InGameChatPanel` но неясно работает ли)
- [ ] Rate limiting для чата (спам защита)

---

## 🔧 ТЕХНИЧЕСКОЕ (инфраструктура)

### 9. ⚠️ Логирование HTTP запросов к LLM

**Что есть:**
- ✅ `LoggingLlmClientDecorator.cs` — обёртка для логирования

**Чего нет:**
- [ ] Логирование **сырого** HTTP request/response (headers, body)
- [ ] Логирование token usage (сколько токенов потрачено)
- [ ] Логирование latency (время ответа модели)
- [ ] Логирование ошибок подключения (LM Studio недоступен)

---

### 10. ❌ Нет метрик оркестрации

**Что есть:**
- ✅ `IAiOrchestrationMetrics.cs` — интерфейс
- ✅ `NullAiOrchestrationMetrics.cs` — заглушка
- ✅ `LoggingAiOrchestrationMetrics.cs` — логирование

**Чего нет:**
- [ ] Реальная реализация метрик (statsd, Prometheus, Application Insights)
- [ ] Dashboard для просмотра метрик (текущий `AiDashboardPresenter` — MVP лог)
- [ ] Alerting: «LLM не отвечает 5 минут»

---

### 11. ❌ Нет версионирования промптов

**Что есть:**
- ✅ `LuaScriptVersionStore` — версионирование Lua скриптов
- ✅ `DataOverlayVersionStore` — версионирование данных

**Чего нет:**
- [ ] Версионирование **системных промптов** (какой промпт использовался для крафта #123)
- [ ] A/B тест промптов (prompt1 vs prompt2 — какой лучше)
- [ ] Rollback промпта (вернуть предыдущую версию)

---

### 12. ⚠️ WorldCommand Executor минимальный

**Что есть:**
- ✅ `CoreAiWorldCommandExecutor` — spawn/move/destroy/load_scene/bind_by_name/set_active

**Чего нет:**
- [ ] Анимации (play_animation, stop_animation)
- [ ] Звуки (play_sound, set_volume)
- [ ] UI команды (show_text, hide_panel, update_score)
- [ ] Физика (apply_force, set_velocity)
- [ ] Партиклы (spawn_particles, stop_particles)
- [ ] Валидация параметров (нельзя спавнить в стену)

---

## 📝 ДОКУМЕНТАЦИЯ

### 13. ❌ Нет полного описания workflow

**Что нужно:**
- [ ] Диаграмма: «Как команда от игрока проходит через всю систему»
- [ ] Sequence diagram: Creator → CoreMechanicAI → Programmer → Lua Execution
- [ ] Описание формата JSON команд для каждой роли
- [ ] Troubleshooting guide: «Модель не отвечает», «Lua упала», «Память не пишется»

### 14. ❌ Нет примеров использования

**Что нужно:**
- [ ] Quick Start: «Запусти LM Studio → запусти сцену → отправь команду»
- [ ] Пример: «Создай врага через Creator»
- [ ] Пример: «Скрафти оружие через CoreMechanicAI»
- [ ] Пример: «Почини Lua ошибку через Programmer auto-repair»
- [ ] Видео/GIF демо работы системы

---

## 🎯 ПРИОРИТЕТЫ (что делать первым)

### ✅ Sprint 1 — КРИТИЧНОЕ (ГОТОВО)
1. ✅ **AgentMemoryPolicy** — память для всех ролей, 2 типа памяти
2. ✅ **MemoryTool** — write/append/clear через MEAI
3. ✅ **ChatHistory** — LLMAgent контекст (сохранение/загрузка)
4. ✅ **FileAgentMemoryStore** — персистентность в JSON
5. ✅ **IAgentMemoryStore** — расширенный интерфейс

### ✅ Sprint 2 — ТЕСТЫ (ГОТОВО)
6. ✅ **LuaExecutionPipelineEditModeTests** — 8 тестов Lua execution
7. ✅ **MultiAgentCraftingWorkflowPlayModeTests** — полный воркфлоу 3 агентов
8. ✅ **CraftingMemoryViaLlmUnityPlayModeTests** — 4 крафта + детерминизм
9. ✅ **CraftingMemoryViaOpenAiPlayModeTests** — 4 крафта + детерминизм
10. ✅ **AllToolCallsPlayModeTests** — боевые тесты всех tool calls (v0.7.0)

### ✅ Sprint 2.5 — ВАЛИДАЦИЯ (ГОТОВО)
11. ✅ **RoleStructuredResponsePolicy** — валидация всех ролей (7 классов)
12. ✅ **CompositeRoleStructuredResponsePolicy** — маршрутизация
13. ✅ **RoleStructuredResponsePolicyEditModeTests** — 20 тестов

### ✅ Sprint 2.7 — ЕДИНЫЙ TOOL CALLING (v0.7.0 ГОТОВО)
14. ✅ **LuaTool** — MEAI AIFunction для Programmer
15. ✅ **LuaLlmTool** — ILlmTool обёртка
16. ✅ **InventoryTool** — MEAI AIFunction для Merchant NPC
17. ✅ **InventoryLlmTool** — ILlmTool обёртка
18. ✅ **Merchant Agent** — новый NPC-торговец с инструментами
19. ✅ **CoreAISettings** — публичные настройки (MaxLuaRepairGenerations=3, MaxToolCallRetries=3)
20. ✅ **Tool Call Retry** — до 3 попыток при неудачном tool call. Модель получает сообщение об ошибке и может исправить формат.
21. ✅ **AgentBuilder** — конструктор кастомных агентов (ToolsOnly, ToolsAndChat, ChatOnly)
22. ✅ **AgentMode** — enum режимов работы
23. ✅ **LlmUnityMeaiChatClient.TryParseToolCallFromText** — парсинг JSON
24. ✅ **AgentMemoryPolicy.SetToolsForRole()** — кастомные инструменты
25. ✅ **Удалены** старые парсинги и дубликаты тестов

### Sprint 3 — Multi-agent (СЛЕДУЮЩИЙ)
19. **MultiAgentWorkflow** — цепочка агентов (4 часа)
20. **CraftingTool** для CoreMechanicAI (2 часа)

### Sprint 4 — Инфраструктура
21. **Логирование HTTP** запросов к LLM (2 часа)
22. **Метрики** оркестрации (2 часа)
23. **AINpc и PlayerChat тесты** (3 часа)

### Sprint 5 — Полировка
24. **WorldCommand** расширения (3 часа)
25. **Версионирование промптов** (2 часа)
26. **Документация** с диаграммами (4 часа)

---

## 📊 СТАТУС РЕАЛИЗАЦИИ

| Компонент | Реализовано | Частично | Не реализовано |
|-----------|:-----------:|:--------:|:--------------:|
| **Creator** — системный промпт | ✅ | | |
| **Creator** — изменение мира | | ✅ | (нет GameConfig) |
| **Creator** — управление агентами | | | ❌ |
| **Analyzer** — системный промпт | ✅ | | |
| **Analyzer** — телеметрия | | ✅ | (нет реальных метрик) |
| **Programmer** — генерация Lua | ✅ | | |
| **Programmer** — auto-repair | | ✅ | (только Lua errors) |
| **Programmer** — валидация ответа | ✅ | | |
| **CoreMechanicAI** — системный промпт | ✅ | | |
| **CoreMechanicAI** — крафт | | ✅ | (нет CraftingTool) |
| **CoreMechanicAI** — валидация ответа | ✅ | | |
| **CoreMechanicAI** — память | ✅ | | |
| **AINpc** — системный промпт | ✅ | | |
| **AINpc** — диалоги | | ✅ | (нет тестов) |
| **AINpc** — валидация ответа | ✅ | | |
| **PlayerChat** — системный промпт | ✅ | | |
| **PlayerChat** — UI | | ✅ | (есть панель, нет тестов) |
| **PlayerChat** — валидация ответа | ✅ | | |
| **Merchant** — системный промпт | ✅ | | |
| **Merchant** — get_inventory tool | ✅ | | |
| **Merchant** — memory tool | ✅ | | |
| **Merchant** — PlayMode тест | ✅ | | |
| **MemoryTool** — write/append/clear | ✅ | | |
| **MemoryTool** — isolation по ролям | ✅ | | |
| **ChatHistory** — LLMAgent контекст | ✅ | | |
| **ChatHistory** — загрузка/сохранение | ✅ | | |
| **AiOrchestrator** — один агент | ✅ | | |
| **AiOrchestrator** — валидация retry | ✅ | | |
| **AiOrchestrator** — MEAI tool calling (v0.7.0) | ✅ | | |
| **MultiAgent** — цепочка | | ✅ | (есть EditMode тесты) |
| **Lua Sandbox** — исполнение | ✅ | | |
| **Lua Sandbox** — timeout | ✅ | | |
| **Lua Repair** — авто-повтор (v0.7.0: max=3) | ✅ | | |
| **Data Passing** — между агентами | ✅ | | |
| **World Commands** — spawn/move/destroy | ✅ | | |
| **World Commands** — animation/sound/UI | | | ❌ |
| **GameConfig** — универсальный интерфейс | ✅ | | |
| **GameConfig** — AI function calling | ✅ | | |
| **GameConfig** — Unity ScriptableObject | ✅ | | |
| **GameConfig** — EditMode тесты | ✅ | | |
| **GameConfig** — PlayMode тесты | ✅ | | |
| **Analyzer** — тесты | ✅ | | |
| **Dashboard** — MVP лог | ✅ | | |
| **Dashboard** — метрики | | | ❌ |
| **Тесты** — EditMode Lua execution | ✅ | | |
| **Тесты** — EditMode MEAI tool calls (v0.7.0) | ✅ | | |
| **Тесты** — PlayMode память | ✅ | | |
| **Тесты** — PlayMode all tool calls (v0.7.0) | ✅ | | |
| **Тесты** — PlayMode multi-agent | ✅ | | |
| **Тесты** — PlayMode Merchant NPC (v0.7.0) | ✅ | | |
| **Tool Call Retry** — 3 попытки при ошибке (v0.7.0) | ✅ | | |
| **CoreAISettings** — публичные настройки (v0.7.0) | ✅ | | |

**Итого:** ✅ Реализовано ~82%, ⚠️ Частично ~10%, ❌ Не реализовано ~8%

---

## 🐛 ИЗВЕСТНЫЕ ПРОБЛЕМЫ ТЕСТОВ

### Unity Cache Issue
- ⚠️ Если тесты не компилируются — **Delete Library/ScriptAssemblies** и реимпорт.

### Lua Sandbox Tests
- ✅ **Timeout реализован**: `LuaExecutionGuard` (2000мс timeout, 200K instruction limit)
- ✅ Бесконечный цикл НЕ зависнет — `InstructionLimitDebugger` бросает исключение

### Cross-Role Memory Sharing
- ❌ **Не реализовано**: память агентов изолирована. `Creator` не видит память `CoreMechanicAI` и наоборот. Это **не баг** — так задумано. Для передачи данных между агентами нужен `MultiAgentWorkflow` (TODO #6).

### v0.7.0 Breaking Changes
- ✅ `AgentMemoryDirectiveParser` удалён — все тесты обновлены на MEAI tool calling
- ✅ `MaxLuaRepairGenerations` изменён с 4 на 3 — тесты обновлены

---

## 📋 НОВЫЕ ТЕСТЫ (добавлены)

### EditMode тесты

| Файл | Тестов | Что проверяет |
|------|--------|---------------|
| `LuaExecutionPipelineEditModeTests.cs` | 8 | Lua sandbox, execution success/failure, repair loop, max generations (v0.7.0: 3), role isolation |
| `MeaiToolCallsEditModeTests.cs` | 10 | MemoryTool, LuaTool, парсинг JSON, regex tool calls (v0.7.0) |
| `AiCraftingMechanicIntegrationEditModeTests.cs` | 7 | Crafting domain, AI creates unique items, deterministic |

**Удалённые тесты (v0.7.0):**
- ❌ `AgentMemoryEditModeTests.cs` — использовал удалённый `AgentMemoryDirectiveParser`
- ❌ `AgentDataPassingEditModeTests.cs` — старые memory парсинги
- ❌ `MemoryToolMeaiEditModeTests.cs` — дублировал MeaiToolCallsEditModeTests

### PlayMode тесты

| Файл | Тестов | Бэкенд | Что проверяет |
|------|--------|--------|---------------|
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | 2 | OpenAI HTTP | Creator→Mechanic→Programmer, memory isolation |
| `CraftingMemoryViaLlmUnityPlayModeTests.cs` | 1 | LLMUnity | 4 крафта + детерминизм |
| `CraftingMemoryViaOpenAiPlayModeTests.cs` | 2 | OpenAI HTTP | 4 крафта + 2 крафта quick |
| `AllToolCallsPlayModeTests.cs` | 3 | LLMUnity или HTTP | Memory tool (write/append/clear) + Execute Lua (v0.7.0) |
| `MerchantWithToolCallingPlayModeTests.cs` | 1 | LLMUnity или HTTP | Merchant NPC вызывает get_inventory и отвечает с предметами (v0.7.0) |
| `CustomAgentsPlayModeTests.cs` | 3 | LLMUnity или HTTP | 3 кастомных агента: Merchant (ToolsAndChat), Analyzer (ToolsOnly), Storyteller (ChatOnly) |
