# TODO — CoreAI: Что не хватает для полной реализации архитектуры

## 🚨 КРИТИЧНОЕ (без этого система не работает как задумано)

### 1. MemoryTool не работает для всех агентов кроме Creator

**Файл:** `Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs`

**Проблема:**
```csharp
public bool IsMemoryEnabled(string roleId)
{
    return roleId == BuiltInAgentRoleIds.Creator; // ← ТОЛЬКО Creator!
}
```

**Что нужно:**
- [ ] Включить память для **всех** ролей: Creator, CoreMechanicAI, Programmer, Analyzer, AINpc, PlayerChat
- [ ] Добавить конфигурацию: какие роли используют `write`, какие `append`
- [ ] CoreMechanicAI → `append` (накапливает историю крафтов)
- [ ] Creator → `write` (перезаписывает дизайн-решения)
- [ ] Programmer → `write` (сохраняет Lua формулы)

**Влияние:** Сейчас CoreMechanicAI **НЕ МОЖЕТ** сохранять историю крафтов. Все тесты крафта с памятью работают только потому что используют `InMemoryStore` напрямую, минуя `AgentMemoryPolicy`.

---

### 2. Нет валидации ответов LLM (RoleStructuredResponsePolicy)

**Файл:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/NoOpRoleStructuredResponsePolicy.cs`

**Проблема:**
```csharp
public bool ShouldValidate(string roleId) => false; // ← Никогда не проверяет
public bool TryValidate(...) => true; // ← Всегда «валидно»
```

**Что нужно:**
- [ ] Реализовать `ProgrammerResponsePolicy` — проверка что ответ содержит Lua код или JSON
- [ ] Реализовать `CoreMechanicResponsePolicy` — проверка что ответ содержит JSON с числами
- [ ] Реализовать `CreatorResponsePolicy` — проверка что ответ содержит JSON с командой
- [ ] При неуде валидации → автоматический retry с подсказкой (сейчас 1 retry уже заложен в `AiOrchestrator`)

**Влияние:** Если модель отвечает текстом вместо JSON/Lua, система не пытается исправить.

---

### 3. Нет конфигурации игры (GameConfig)

**Проблема:** В проекте **НЕТ** файлов `*Config*.cs` кроме тестового.

**Что нужно:**
- [ ] `GameSessionConfig` — параметры сессии (сложность, модификаторы, лимиты крафта)
- [ ] `AgentConfig` — конфиги агентов (температура, timeout, max tokens для каждой роли)
- [ ] `CraftingConfig` — параметры крафта (max ingredients, rarity ranges, quality bounds)
- [ ] Creator должен уметь **менять** эти конфиги через JSON команды

**Влияние:** Creator не может реально управлять параметрами игры — нет объекта для изменения.

---

## ⚠️ ВАЖНОЕ (система работает, но не полностью)

### 4. CoreMechanicAI не имеет специализированных инструментов

**Что есть:**
- ✅ Системный промпт есть
- ✅ RoleId есть
- ✅ Память (после фикса #1) будет

**Чего нет:**
- [ ] `CraftingTool` — специализированная функция для расчёта крафта (как MemoryTool но для крафта)
- [ ] `LootRollTool` — функция для броска лута
- [ ] `CompatibilityChecker` — проверка совместимости ингредиентов
- [ ] JSON schema validation — проверка что ответ CoreMechanicAI содержит нужные поля

**Что нужно:**
```csharp
// Пример CraftingTool
public AIFunction CreateCraftingTool()
{
    return AIFunctionFactory.Create(
        (string ingredient1, string ingredient2, float quality) => {
            return new CraftResult { ItemName = "...", Damage = ..., Quality = quality };
        },
        "calculate_craft",
        "Calculate crafting result from two ingredients with quality 0-100.");
}
```

---

### 5. Programmer не имеет автоматического ремонта вне Lua sandbox

**Что есть:**
- ✅ `LuaAiEnvelopeProcessor` — выполняет Lua, ловит ошибки
- ✅ Автоматический retry Programmer при ошибке Lua (до 4 поколений)

**Чего нет:**
- [ ] Ремонт если Programmer **вообще не дал Lua** (дал текст вместо кода)
- [ ] Валидация Lua ДО исполнения (проверка на запрещённые функции: io, os, load)
- [ ] Timeout на выполнение Lua (бесконечный цикл зависнет)

---

### 6. Нет multi-agent orchestration (последовательность агентов)

**Что есть:**
- ✅ `AiOrchestrator` — запускает ОДНУ задачу одного агента
- ✅ `QueuedAiOrchestrator` — очередь задач с приоритетами

**Чего нет:**
- [ ] `MultiAgentWorkflow` — цепочка: Creator → CoreMechanicAI → Programmer
- [ ] Передача результатов между агентами (output Creator → input CoreMechanicAI)
- [ ] Условная логика: «если CoreMechanicAI вернул качество > 80, вызвать Programmer»
- [ ] Parallel execution: «Analyzer и CoreMechanicAI работают параллельно»

**Что нужно:**
```csharp
// Пример API
var workflow = new MultiAgentWorkflow()
    .Step(BuiltInAgentRoleIds.Creator, "Design weapon from iron+crystal")
    .Then(BuiltInAgentRoleIds.CoreMechanic, "Calculate stats (use Creator output)")
    .Then(BuiltInAgentRoleIds.Programmer, "Generate Lua (use CoreMechanic output)")
    .ExecuteAsync();
```

---

### 7. Analyzer не участвует ни в одном тесте

**Что есть:**
- ✅ Системный промпт есть
- ✅ RoleId есть

**Чего нет:**
- [ ] Тест: Analyzer читает телеметрию → рекомендует Creator
- [ ] Тест: Creator → Analyzer → Creator (цикл баланса)
- [ ] Реальная телеметрия: какие данные собираются? KPI?

---

### 8. AINpc и PlayerChat не тестированы

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

### 9. Нет логирования HTTP запросов к LLM

**Что есть:**
- ✅ `LoggingLlmClientDecorator.cs` — обёртка для логирования

**Чего нет:**
- [ ] Логирование **сырого** HTTP request/response (headers, body)
- [ ] Логирование token usage (сколько токенов потрачено)
- [ ] Логирование latency (время ответа модели)
- [ ] Логирование ошибок подключения (LM Studio недоступен)

---

### 10. Нет метрик оркестрации

**Что есть:**
- ✅ `IAiOrchestrationMetrics.cs` — интерфейс
- ✅ `NullAiOrchestrationMetrics.cs` — заглушка
- ✅ `LoggingAiOrchestrationMetrics.cs` — логирование

**Чего нет:**
- [ ] Реальная реализация метрик (statsd, Prometheus, Application Insights)
- [ ] Dashboard для просмотра метрик (текущий `AiDashboardPresenter` — MVP лог)
- [ ] Alerting: «LLM не отвечает 5 минут»

---

### 11. Нет версионирования промптов

**Что есть:**
- ✅ `LuaScriptVersionStore` — версионирование Lua скриптов
- ✅ `DataOverlayVersionStore` — версионирование данных

**Чего нет:**
- [ ] Версионирование **системных промптов** (какой промпт использовался для крафта #123)
- [ ] A/B тест промптов (prompt1 vs prompt2 — какой лучше)
- [ ] Rollback промпта (вернуть предыдущую версию)

---

### 12. WorldCommand Executor минимальный

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

### 13. Нет полного описания workflow

**Что нужно:**
- [ ] Диаграмма: «Как команда от игрока проходит через всю систему»
- [ ] Sequence diagram: Creator → CoreMechanicAI → Programmer → Lua Execution
- [ ] Описание формата JSON команд для каждой роли
- [ ] Troubleshooting guide: «Модель не отвечает», «Lua упала», «Память не пишется»

### 14. Нет примеров использования

**Что нужно:**
- [ ] Quick Start: «Запусти LM Studio → запусти сцену → отправь команду»
- [ ] Пример: «Создай врага через Creator»
- [ ] Пример: «Скрафти оружие через CoreMechanicAI»
- [ ] Пример: «Почини Lua ошибку через Programmer auto-repair»
- [ ] Видео/GIF демо работы системы

---

## 🎯 ПРИОРИТЕТЫ (что делать первым)

### Sprint 1 — Критическое
1. ✅ **Исправить AgentMemoryPolicy** — включить память для всех ролей, 2 типа памяти (готово!)
2. ~~**Добавить RoleStructuredResponsePolicy**~~ — отложено
3. ~~**Создать GameConfig**~~ — отложено

### Sprint 2 — Multi-agent
4. **MultiAgentWorkflow** — цепочка агентов (4 часа)
5. **CraftingTool** для CoreMechanicAI (2 часа)
6. **Тесты полного воркфлоу** (Creator → CoreMechanicAI → Programmer) (3 часа)

### Sprint 3 — Инфраструктура
7. **Логирование HTTP** запросов к LLM (2 часа)
8. **Метрики** оркестрации (2 часа)
9. **AINpc и PlayerChat тесты** (3 часа)

### Sprint 4 — Полировка
10. **WorldCommand** расширения (3 часа)
11. **Версионирование промптов** (2 часа)
12. **Документация** с диаграммами (4 часа)

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
| **CoreMechanicAI** — системный промпт | ✅ | | |
| **CoreMechanicAI** — крафт | | ✅ | (нет CraftingTool) |
| **CoreMechanicAI** — память | | | ❌ (выключена в policy) |
| **AINpc** — системный промпт | ✅ | | |
| **AINpc** — диалоги | | | ❌ (нет тестов) |
| **PlayerChat** — системный промпт | ✅ | | |
| **PlayerChat** — UI | | ✅ | (есть панель, нет тестов) |
| **MemoryTool** — write/append/clear | ✅ | | |
| **MemoryTool** — isolation по ролям | ✅ | | |
| **ChatHistory** — LLMAgent контекст | ✅ | | |
| **ChatHistory** — загрузка/сохранение | ✅ | | |
| **AiOrchestrator** — один агент | ✅ | | |
| **MultiAgent** — цепочка | | | ❌ |
| **Lua Sandbox** — исполнение | ✅ | | |
| **Lua Sandbox** — timeout | | | ❌ |
| **World Commands** — spawn/move/destroy | ✅ | | |
| **World Commands** — animation/sound/UI | | | ❌ |
| **Dashboard** — MVP лог | ✅ | | |
| **Dashboard** — метрики | | | ❌ |
| **Тесты** — EditMode крафт | ✅ | | |
| **Тесты** — PlayMode память | ✅ | | |
| **Тесты** — PlayMode multi-agent | | | ❌ |

**Итого:** ✅ Реализовано ~40%, ⚠️ Частично ~25%, ❌ Не реализовано ~35%
