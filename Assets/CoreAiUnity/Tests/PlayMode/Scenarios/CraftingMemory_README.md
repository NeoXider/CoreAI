# 🤖 AI Агенты — Архитектура и Воркфлоу

## Роли агентов

| Агент | Роль | Доступ | Память | Пример использования |
|-------|------|--------|--------|---------------------|
| **Creator** | Управляющий / дизайнер | Изменение мира, конфиги, управление другими агентами | `Creator` | "Создай волну врагов", "Разработай систему крафта" |
| **Analyzer** | Аналитик телеметрии | Чтение данных, рекомендации | `Analyzer` | "Игроку скучно, увеличь сложность" |
| **Programmer** | Генерация Lua кода | Песочница Lua, add/report | `Programmer` | "Напиши формулу урона в Lua" |
| **CoreMechanicAI** | Игровая механика | Численные исходы, крафт, лут, совместимость | `CoreMechanicAI` | "Скрафти оружие из железа и кристалла" |
| **AINpc** | Диалоги NPC | Реплики в мире | `AINpc` | "Приветствую, путник!" |
| **PlayerChat** | Ассистент игрока | Чат с игроком | `PlayerChat` | "Как скрафтить меч?" |

---

## Архитектура: Одна модель, разные роли

Все агенты используют **одну и ту же LLM модель** (Qwen 35B через LM Studio), но:

1. **Разные системные промпты** — каждый агент получает свою роль
2. **Разная изолированная память** — `CoreMechanicAI` не видит память `Creator`
3. **Разные инструменты** — Programmer получает Lua sandbox, CoreMechanicAI — числовой вывод

```
                    ┌─────────────────────────┐
                    │   LM Studio (Qwen 35B)  │
                    │   http://192.168.56.1   │
                    └───────────┬─────────────┘
                                │
                    ┌───────────▼─────────────┐
                    │     ILlmClient          │
                    └───────────┬─────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              │                 │                 │
    ┌─────────▼──────┐ ┌───────▼──────┐ ┌───────▼──────┐
    │ Creator        │ │ CoreMechanic │ │ Programmer   │
    │ Память: Creator│ │Память: CM    │ │Память: Prog  │
    │ Промпт: Design │ │Промпт: Craft │ │Промпт: Lua   │
    └────────────────┘ └──────────────┘ └──────────────┘
```

---

## Пример: Полный воркфлоу крафта с несколькими агентами

```
┌─────────────────────────────────────────────────────────────────────┐
│  ШАГ 1: Creator решает ЧТО и КАК крафтить                           │
│  Агент: Creator                                                     │
│  Память: Creator (изолированная)                                    │
│                                                                     │
│  Запрос: "Разработай рецепт оружия из Iron + Fire Crystal"          │
│  Ответ: JSON с параметрами (item_type, damage, fire_damage)         │
│  Память: "Design: Iron+Fire Crystal → weapon, damage ~45, fire ~15" │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  ШАГ 2: CoreMechanicAI считает точный результат крафта              │
│  Агент: CoreMechanicAI                                              │
│  Память: CoreMechanicAI (изолированная)                             │
│                                                                     │
│  Запрос: "Рассчитай крафт из: Iron (hardness:60) + Fire Crystal"    │
│  Память (читает): "" (первый крафт)                                 │
│  Ответ: {"item_name": "Iron Fireblade", "damage": 45, "fire": 15}   │
│  Память (пишет): "Craft#1: Iron Fireblade damage:45 fire:15"       │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  ШАГ 3: Programmer генерирует Lua код для реализации                │
│  Агент: Programmer                                                  │
│  Память: Programmer (изолированная)                                 │
│                                                                     │
│  Запрос: "Создай Lua код для Iron Fireblade (damage:45, fire:15)"   │
│  Ответ: create_item('Iron Fireblade', 'weapon', 65)                 │
│         add_special_effect('fire_damage: 15')                       │
│  Память: (может не писать, если не запрошено)                       │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  ШАГ 4: CoreMechanicAI — повторный крафт (проверка детерминизма)    │
│  Агент: CoreMechanicAI                                              │
│  Память: CoreMechanicAI (читает Craft#1)                            │
│                                                                     │
│  Запрос: "Снова крафт из: Iron + Fire Crystal (те же параметры)"    │
│  Память (читает): "Craft#1: Iron Fireblade damage:45 fire:15"      │
│  Ответ: {"item_name": "Iron Fireblade", "damage": 45, "fire": 15}   │
│  ✅ Тот же результат! Детерминизм работает!                         │
└─────────────────────────────────────────────────────────────────────┘
```

**Результат:**
- ✓ Creator спроектировал крафт
- ✓ CoreMechanicAI рассчитал числа
- ✓ Programmer сгенерировал Lua код
- ✓ Память каждого агента изолирована
- ✓ Повторный крафт детерминирован

---

## MemoryTool — Microsoft.Extensions.AI

Каждый агент имеет свой `MemoryTool` с изолированной памятью:

```csharp
// CoreMechanicAI память — история крафтов
var mechanicTool = new MemoryTool(store, "CoreMechanicAI");
await mechanicTool.ExecuteAsync("write", "Craft#1: Iron Fireblade damage:45");

// Creator память — дизайн-решения
var creatorTool = new MemoryTool(store, "Creator");
await creatorTool.ExecuteAsync("write", "Design: Iron+Fire Crystal → weapon");

// Они НЕ видят память друг друга!
store.TryLoad("Creator", out var creatorState);       // → "Design: ..."
store.TryLoad("CoreMechanicAI", out var mechanicState); // → "Craft#1: ..."
```

**Три действия:**
- `write` — перезаписать память
- `append` — добавить к существующей
- `clear` — очистить память

Реализация: `Microsoft.Extensions.AI.AIFunctionFactory.Create()`

---

## Тесты

### PlayMode тесты (полный воркфлоу с реальной моделью)

| Файл | Агенты | Бэкенд | Описание |
|------|--------|--------|----------|
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator → CoreMechanicAI → Programmer** | OpenAI HTTP | **Полный воркфлоу 3 агентов** |
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator → CoreMechanicAI** | OpenAI HTTP | **Быстрый тест 2 агентов** |
| `CraftingMemoryViaLlmUnityPlayModeTests.cs` | CoreMechanicAI | LLMUnity | 4 крафта + детерминизм |
| `CraftingMemoryViaOpenAiPlayModeTests.cs` | CoreMechanicAI | OpenAI HTTP | 4 крафта + детерминизм + 2 крафта |

### EditMode тесты (с моковой LLM)

| Файл | Описание |
|------|----------|
| `AiCraftingMechanicIntegrationEditModeTests.cs` | Крафт с моковой LLM |
| `MemoryToolMeaiEditModeTests.cs` | Тесты MemoryTool (write/append/clear) |

---

## Ключевые компоненты

```
AiOrchestrator
    ├── ILlmClient (LLMUnity или OpenAI HTTP)
    ├── IAgentMemoryStore (хранение памяти)
    ├── AgentMemoryPolicy (политика обновления)
    ├── AiPromptComposer (сборка промпта + память)
    └── IAiGameCommandSink (приём команд)

MemoryTool (Microsoft.Extensions.AI)
    ├── CreateAIFunction() → AIFunction для model function calling
    ├── ExecuteAsync("write", content)
    ├── ExecuteAsync("append", content)
    └── ExecuteAsync("clear")
```

## Примечания

> 💡 **Все агенты используют одну модель** — Qwen 35B через LM Studio.
> Разделение происходит на уровне:
> - **Системного промпта** (роль агента)
> - **Памяти** (изолированная по `roleId`)
> - **Инструментов** (доступные функции)
>
> Это позволяет масштабировать систему — добавить нового агента = добавить новый roleId + промпт.
