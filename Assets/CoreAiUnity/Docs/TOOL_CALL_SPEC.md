# 📋 Tool Calling Specification v0.14.0

## Единый формат MEAI Tool Calls

Все tool calls в CoreAI используют **единый JSON формат** через Microsoft.Extensions.AI (MEAI):

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

## 🎯 Принципы дизайна инструментов (Tool Design Best Practices)

При создании инструментов для LLM **критически важно** проектировать их так, чтобы модели было легко понять, когда и как вызывать инструмент, а токены расходовались минимально. Это особенно актуально для маленьких моделей (2B–4B).

### 1. Понятные имена и описания

Имя инструмента — это первое, что видит модель. Оно должно быть **самоочевидным**:

| ❌ Плохо | ✅ Хорошо | Почему |
|----------|-----------|--------|
| `tool_1` | `get_inventory` | Модель сразу понимает назначение |
| `do_action` | `spawn_quiz` | Конкретное действие, нет двусмысленности |
| `process_data` | `craft_item` | Ясно что произойдёт при вызове |

**Описание (`Description`)** — второй якорь для модели. Пишите коротко и по делу:

```csharp
// ❌ Плохо — слишком длинное, расходует токены на каждом запросе
public string Description => "This tool allows the AI agent to retrieve the current inventory " +
    "of the NPC merchant character which includes all items currently available for sale " +
    "with their prices, quantities, and item types for the purpose of answering player questions";

// ✅ Хорошо — ёмко и достаточно для модели
public string Description => "Get NPC inventory: items, prices, quantities.";
```

### 2. Экономия токенов в параметрах

Каждый параметр инструмента отправляется модели в JSON-схеме **при каждом запросе**. Сокращённые ключи экономят токены, при этом оставаясь понятными модели:

| ❌ Длинные ключи | ✅ Короткие ключи | Экономия |
|------------------|-------------------|----------|
| `"question_text"` | `"q"` | ~10 токенов × N вопросов |
| `"answer_options"` | `"opts"` | ~12 токенов × N вопросов |
| `"correct_answer_indexes"` | `"correct"` | ~15 токенов × N вопросов |
| `"number_of_attempts"` | `"attempts"` | ~10 токенов на запрос |

> 💡 **Правило:** используйте короткие ключи (`q`, `opts`, `correct`) в `ParametersSchema` и компенсируйте это чётким `description` внутри схемы. Модель читает описание при первом знакомстве, а ключи — при каждом вызове.

Пример:
```json
{
  "type": "object",
  "properties": {
    "q": {"type": "string", "description": "Question text."},
    "opts": {"type": "array", "items": {"type": "string"}, "description": "Answer options."},
    "correct": {"type": "array", "items": {"type": "integer"}, "description": "Indexes of correct options (0-based)."}
  },
  "required": ["q", "opts", "correct"]
}
```

### 3. Индексы вместо строк

Где возможно, используйте **числовые индексы** вместо повторения строк:

```json
// ❌ correct содержит строки — дублирование, больше токенов, хрупкость при опечатках
{"correct": ["Словарь с парами ключ-значение"]}

// ✅ correct содержит индекс — компактно, надёжно, не зависит от языка
{"correct": [1]}
```

### 4. Опциональные параметры с умными дефолтами

Не заставляйте модель заполнять поля, которые редко нужны. Используйте дефолты на стороне кода:

```csharp
// В коде — дефолт 2 попытки, модель может не передавать attempts:
int attempts = root.Value<int?>("attempts") ?? QuizSpec.DefaultAttempts;
```

### 5. Единая точка входа (Single-String Payload)

Для сложных инструментов с вложенной структурой (массивы объектов) удобнее принять **один строковый параметр `payload`** с JSON, чем десятки отдельных параметров. Это упрощает JSON-схему для модели и уменьшает вероятность ошибок:

```csharp
// ✅ Один параметр — модель генерирует JSON-строку, парсинг на стороне кода
public Task<string> ExecuteAsync(
    [Description("JSON: { questions:[{q, opts[], correct[]}], attempts?, title? }")] string payload, ...)
```

### Сводная таблица принципов

| Принцип | Выгода для модели | Выгода для токенов |
|---------|-------------------|-------------------|
| Понятное имя (`spawn_quiz`) | Модель уверенно вызывает | — |
| Краткое описание (1 строка) | Быстро считывается | Меньше system prompt |
| Короткие ключи (`q`, `opts`) | Модель быстрее генерирует | **–30–50% токенов** на вызов |
| Индексы вместо строк | Нет ошибок копирования | **–50% payload** |
| Опциональные с дефолтами | Меньше обязательных полей | Меньше output токенов |
| Single-string payload | Простая схема | Меньше schema токенов |

---

## Температура генерации

**Общая температура:** `CoreAISettings.Temperature` (по умолчанию **0.1**). Применяется ко всем агентам.

**Переопределение на уровне агента:**
```csharp
var agent = new AgentBuilder("Creator")
    .WithSystemPrompt("...")
    .WithTemperature(0.0f)  // Строгий JSON
    .Build();
```

| Значение | Когда использовать |
|----------|-------------------|
| `0.0` | Строгий JSON, код, математика |
| `0.1` | **По умолчанию** — tool calling |
| `0.3` | NPC диалоги |
| `0.7+` | Креативные задачи |

## 🏗️ Архитектура: Engine-Agnostic Pattern

CoreAI использует **двухуровневую архитектуру** для инструментов:

| Уровень | Пакет | Что содержит |
|---------|-------|-------------|
| **Абстрактный** | `CoreAI` | Интерфейсы, базовые классы, контракты |
| **Реализация** | `CoreAiUnity` | Конкретная реализация для Unity |

Этот паттерн позволяет:
- ✅ **Движок-независимое ядро** — CoreAI работает с любым движком
- ✅ **Лёгкая портируемость** — новые движки реализуют те же интерфейсы
- ✅ **Единый API** — LLM вызывает инструменты одинаково на всех платформах

## Доступные Tools

### 1. События и Экшены (DelegateLlmTool)

**Назначение:** Динамическое превращение любого C# делегата (метода) в `ILlmTool` без написания классов. Модель автоматически получает правильный JSON-skeleton из сигнатуры делегата C#. Также это основа для триггера глобальных событий (`WithEventTool`).

**Применение:**
- `AgentBuilder.WithAction` (вызов конкретного делегата)
- `AgentBuilder.WithEventTool` (публикация в `CoreAiEvents` для decoupling)

> **💡 Важное правило промптинга:** Если вы добавляете агенту Action или EventTool, **настоятельно рекомендуется** в `WithSystemPrompt` прописать правило его использования. Например: `"If you want to alarm guards, call 'alarm_guards' tool"`.

### 2. Memory Tool

**Назначение:** Сохранение, добавление и очистка памяти агента.

**Формат:**
```json
{"name": "memory", "arguments": {"action": "write|append|clear", "content": "текст"}}
```

**Код:**
- `MemoryTool.cs` - MEAI AIFunction
- `MemoryLlmTool.cs` - ILlmTool обёртка

### 2. Execute Lua Tool

**Назначение:** Выполнение Lua скриптов от Programmer агента.

**Формат:**
```json
{"name": "execute_lua", "arguments": {"code": "Lua код"}}
```

**Код:**
- `LuaTool.cs` - MEAI AIFunction
- `LuaLlmTool.cs` - ILlmTool обёртка

### 3. World Command Tool

**Назначение:** Управление игровым миром — спавн, перемещение, удаление объектов, анимации, звуки, сцены.

**Формат:**
```json
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "targetName": "enemy_1", "x": 0, "y": 0, "z": 0}}
```

| Action | Описание | Обязательные параметры |
|--------|----------|------------------------|
| `spawn` | Создать объект | `prefabKey`, `targetName`, `x`, `y`, `z` |
| `move` | Переместить объект | `targetName`, `x`, `y`, `z` |
| `destroy` | Удалить объект | `targetName` |
| `list_objects` | Получить список объектов | — (опционально: `stringValue` для поиска) |
| `load_scene` | Загрузить сцену | `stringValue` (имя сцены) |
| `reload_scene` | Перезагрузить сцену | — |
| `set_active` | Включить/выключить | `targetName` |
| `play_animation` | Проиграть анимацию | `targetName`, `animationName` |
| `list_animations` | Получить список анимаций | `targetName` |
| `show_text` | Показать текст | `targetName`, `textToDisplay` |
| `apply_force` | Применить силу | `targetName`, `x`, `y`, `z` |
| `spawn_particles` | Создать частицы | `targetName`, `stringValue` |

**Код:**
- `WorldTool.cs` - MEAI AIFunction
- `WorldLlmTool.cs` - ILlmTool обёртка

**Примеры:**
```json
// Spawn enemy at position
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "targetName": "enemy_1", "x": 10, "y": 0, "z": 5}}

// Move player to checkpoint (по targetName)
{"name": "world_command", "arguments": {"action": "move", "targetName": "Player", "x": 100, "y": 0, "z": 50}}

// Destroy object by name
{"name": "world_command", "arguments": {"action": "destroy", "targetName": "OldBuilding"}}

// List all objects in scene
{"name": "world_command", "arguments": {"action": "list_objects"}}

// Search objects by name pattern
{"name": "world_command", "arguments": {"action": "list_objects", "stringValue": "enemy"}}

// Show text notification
{"name": "world_command", "arguments": {"action": "show_text", "targetName": "Player", "textToDisplay": "Quest completed!"}}

// Play animation on enemy
{"name": "world_command", "arguments": {"action": "play_animation", "targetName": "Enemy1", "animationName": "attack"}}

// List available animations
{"name": "world_command", "arguments": {"action": "list_animations", "targetName": "Enemy1"}}

// Load next level
{"name": "world_command", "arguments": {"action": "load_scene", "stringValue": "Level_2"}}
```

**Когда использовать:** Creator/Designer AI который динамически управляет миром.

### 4. Get Inventory Tool (Merchant NPC)

**Назначение:** Получение инвентаря NPC-торговца для осмысленных ответов игроку.

**Формат:**
```json
{"name": "get_inventory", "arguments": {}}
```

**Возвращает:** Список предметов с именем, типом, количеством и ценой.

**Код:**
- `InventoryTool.cs` - MEAI AIFunction
- `InventoryLlmTool.cs` - ILlmTool обёртка

**Пример:** 
```
Player: "Что у тебя есть?"
  ↓
Merchant: {"name": "get_inventory", "arguments": {}}
  ↓
Tool: [{name: "Iron Sword", price: 50, qty: 3}]
  ↓
Merchant: "У меня есть Iron Sword за 50 монет..."
```

**Когда использовать:** Merchant/Shopkeeper NPC который продаёт предметы.

### 4. Game Config Tool

**Назначение:** Чтение и изменение игровых конфигов.

**Формат:**
```json
{"name": "game_config", "arguments": {"action": "read|update", "content": "JSON"}}
```

**Код:**
- `GameConfigTool.cs` - MEAI AIFunction
- `GameConfigLlmTool.cs` - ILlmTool обёртка

### 5. Action / Event Tool (DelegateLlmTool)

**Назначение:** Прямой вызов C# методов или событий (Action/Func) без необходимости создавать классы `ILlmTool`. Идеально для привязки игровых механик.

**Код:**
- `DelegateLlmTool.cs`
- Работает через `AIFunctionFactory` (MEAI), который автоматически парсит аргументы метода и отдаёт их LLM-модели как инструмент.

**Формат:** Зависит от сигнатуры вашего метода!

**Запуск через AgentBuilder:**
```csharp
var agent = new AgentBuilder("Helper")
    .WithAction("heal_player", "Heals the player fully", () => player.Heal())
    .WithEventTool("trigger_scare", "Use to scare the player") // Публикует событие в CoreAiEvents
    .Build();
```

> 💡 **Как модель понимает, когда использовать триггер?**
> Функция автоматически становится доступной для LLM как обычный tool. Чтобы управлять её вызовом:
> 1. **Пишите чёткий `description`** (второй параметр в `WithAction`), который объясняет, зачем нужна функция (например, *"Call this to heal the player"*).
> 2. **Давайте указания в системном промпте:** В `WithSystemPrompt` агента прямо скажите: *"If the player asks for help, you MUST call heal_player"*.
## Настройки

### CoreAISettings

```csharp
// До инициализации:
CoreAISettings.MaxLuaRepairRetries = 3;        // Лимит подряд неудачных Lua repair
CoreAISettings.MaxToolCallRetries = 3;         // Лимит подряд неудачных tool call
CoreAISettings.EnableMeaiDebugLogging = true;  // Отладка MEAI
CoreAISettings.LlmRequestTimeoutSeconds = 300; // Таймаут LLM
```

### Tool Call Retry

При неудачном tool call (модель вернула неправильный формат):
1. Система возвращает ошибку модели: "ERROR: Tool call not recognized. Use this format..."
2. Модель получает ещё одну попытку
3. Повторяется до `MaxToolCallRetries` подряд неудач (по умолчанию 3), счётчик сбрасывается при успехе
4. Если все попытки исчерпаны - ответ принимается как есть

Это помогает маленьким моделям (Qwen3.5-2B) научиться правильному формату.

**Логирование:**
```
MeaiLlmUnityClient: Calling GetResponseAsync (attempt 1/4)
MeaiLlmUnityClient: Tool call not recognized, retry 1/3
MeaiLlmUnityClient: Calling GetResponseAsync (attempt 2/4)
MeaiLlmUnityClient: Tool call parsed from JSON text
```

## Кастомные агенты через AgentBuilder

Создание нового агента с уникальными инструментами — 3 строки:

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a shopkeeper...")
    .WithTool(new InventoryLlmTool(myProvider))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

merchant.ApplyToPolicy(policy);
```

### Режимы агента

| Режим | Описание | Пример |
|-------|----------|--------|
| `ToolsOnly` | Только инструменты (без текста) | Фоновый анализ телеметрии |
| `ToolsAndChat` | Инструменты + текст (по умолчанию) | Merchant, Crafter, Advisor |
| `ChatOnly` | Только текст (без инструментов) | PlayerChat, Storyteller |

### Кастомные инструменты

**Создание своего инструмента — 3 шага:**

**1. Создай класс:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather in game world.";
    public string ParametersSchema => "{}";

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) => await _provider.GetWeatherAsync(ct),
            "get_weather", "Get current weather.");
    }
}
```

**2. Добавь агенту:**
```csharp
var agent = new AgentBuilder("Farmer")
    .WithSystemPrompt("You are a farmer. Check weather before answering.")
    .WithTool(new WeatherLlmTool(weatherProvider))
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

**3. Модель вызовет инструмент когда нужно:**
```json
{"name": "get_weather", "arguments": {}}
```

**Подробнее:** [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — полная инструкция с примерами параметров

## Архитектура

```
AiOrchestrator → MeaiLlmUnityClient → FunctionInvokingChatClient
                                         ↓
                              LlmUnityMeaiChatClient.TryParseToolCallFromText()
                                         ↓
                    ┌────────────────────┼────────────────────┐
                    ↓                    ↓                    ↓
            MemoryTool           LuaTool           InventoryTool
```

## Рекомендуемые модели

| Модель | Размер | Tool Calling | Когда использовать |
|--------|--------|--------------|-------------------|
| **Qwen3.5-4B** | 4B | ✅ Отлично | **Рекомендуемая** для локального запуска |
| **Qwen3.5-35B (MoE) API** | 35B/3A | ✅ Превосходно | **Идеально** через API — быстро и точно |
| **Gemma 4 26B** | 26B | ✅ Превосходно | Отличный выбор через LM Studio / HTTP API |
| Qwen3.5-2B | 2B | ⚠️ Работает | Работает, но иногда ошибается в многошаговых |
| Qwen3.5-0.8B | 0.8B | ⚠️ Базовый | Большинство тестов проходит, сложности с multi-step |

> 🏆 **Qwen3.5-4B проходит ВСЕ PlayMode тесты.** Рекомендуемый минимум для продакшена.  
> 💡 MoE-модели (Mixture of Experts) активируют только 3B параметров при инференсе — быстрые как 4B, точные как 35B.

## Тестирование

### EditMode Tests
- `MeaiToolCallsEditModeTests.cs` - MemoryTool, LuaTool, парсинг JSON

### PlayMode Tests  
- `AllToolCallsPlayModeTests.cs` - Memory tool + Execute Lua
- `ChatWithToolCallingPlayModeTests.cs` - Chat Agent + Inventory tool
- `CraftingMemoryViaLlmUnityPlayModeTests.cs` - Полный workflow крафта

## Системные промпты

### Universal System Prompt Prefix (v0.11.0+)

CoreAI поддерживает **универсальный стартовый промпт** — текст, который добавляется в **НАЧАЛО** системного промпта каждого агента. Это позволяет задать общие правила для всех моделей без дублирования в каждом промпте.

**Структура системного промпта:**
```
[Universal Prefix] + [Agent-Specific Prompt]
```

**Пример:**
```yaml
# Universal Prefix (общий для всех):
"You are an AI agent in a game. Always stay in character."

# Agent-Specific Prompt (для Programmer):
"You are the Programmer agent for CoreAI MoonSharp sandbox..."

# Итоговый промпт (автоматически):
"You are an AI agent in a game. Always stay in character. You are the Programmer agent..."
```

**Как настроить:**
- **Inspector:** CoreAISettings → "⚙️ Общие настройки" → Universal System Prompt Prefix
- **Код:** `CoreAISettings.UniversalSystemPromptPrefix = "..."`

> Префикс применяется ко **всем** агентам: встроенным (Creator, Programmer, Analyzer...) и кастомным (через AgentBuilder).

---

## Breaking Changes v0.7.0

- ❌ `AgentMemoryDirectiveParser` удалён
- ❌ Fenced блоки (```memory, ```lua) не используются для tool calls
- ❌ `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- ✅ Programmer вызывает `execute_lua` tool вместо fenced блоков
- ✅ Chat Agent может вызывать `get_inventory` перед ответом игроку
