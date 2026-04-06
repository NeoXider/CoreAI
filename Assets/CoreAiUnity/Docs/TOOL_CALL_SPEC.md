# 📋 Tool Calling Specification v0.10.0

## Единый формат MEAI Tool Calls

Все tool calls в CoreAI используют **единый JSON формат** через Microsoft.Extensions.AI (MEAI):

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

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

📖 **Полная документация:** [ENGINE_AGNOSTIC_TOOLS.md](../../CoreAI/Docs/ENGINE_AGNOSTIC_TOOLS.md)

## Доступные Tools

### 1. Memory Tool

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
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "x": 0, "y": 0, "z": 0, "instanceId": "enemy_1"}}
```

**Доступные actions:**
| Action | Описание | Обязательные параметры |
|--------|----------|------------------------|
| `spawn` | Создать объект | `prefabKey`, `x`, `y`, `z`, `instanceId` |
| `move` | Переместить объект | `instanceId` или `targetName`, `x`, `y`, `z` |
| `destroy` | Удалить объект | `instanceId` или `targetName` |
| `list_objects` | Получить список объектов | — (опционально: `stringValue` для поиска) |
| `load_scene` | Загрузить сцену | `stringValue` (имя сцены) |
| `reload_scene` | Перезагрузить сцену | — |
| `bind_by_name` | Привязать по имени | `targetName`, `instanceId` |
| `set_active` | Включить/выключить | `instanceId` или `targetName` |
| `play_animation` | Проиграть анимацию | `instanceId` или `targetName`, `stringValue` (имя анимации) |
| `list_animations` | Получить список анимаций | `instanceId` или `targetName` |
| `show_text` | Показать текст | `targetName`, `stringValue` |
| `apply_force` | Применить силу | `instanceId` или `targetName`, `x`, `y`, `z` |
| `spawn_particles` | Создать частицы | `instanceId` или `targetName`, `stringValue` |

**Код:**
- `WorldTool.cs` - MEAI AIFunction
- `WorldLlmTool.cs` - ILlmTool обёртка

**Примеры:**
```json
// Spawn enemy at position
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "x": 10, "y": 0, "z": 5, "instanceId": "enemy_1"}}

// Move player to checkpoint (по targetName)
{"name": "world_command", "arguments": {"action": "move", "targetName": "Player", "x": 100, "y": 0, "z": 50}}

// Destroy object by name
{"name": "world_command", "arguments": {"action": "destroy", "targetName": "OldBuilding"}}

// List all objects in scene
{"name": "world_command", "arguments": {"action": "list_objects"}}

// Search objects by name pattern
{"name": "world_command", "arguments": {"action": "list_objects", "stringValue": "enemy"}}

// Show text notification
{"name": "world_command", "arguments": {"action": "show_text", "targetName": "Player", "stringValue": "Quest completed!"}}

// Play animation on enemy
{"name": "world_command", "arguments": {"action": "play_animation", "targetName": "Enemy1", "stringValue": "attack"}}

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

## Настройки

### CoreAISettings

```csharp
// До инициализации:
CoreAISettings.MaxLuaRepairGenerations = 3;    // Лимит повторов Programmer
CoreAISettings.MaxToolCallRetries = 3;         // Лимит повторов tool call
CoreAISettings.EnableMeaiDebugLogging = true;  // Отладка MEAI
CoreAISettings.LlmRequestTimeoutSeconds = 300; // Таймаут LLM
```

### Tool Call Retry

При неудачном tool call (модель вернула неправильный формат):
1. Система возвращает ошибку модели: "ERROR: Tool call not recognized. Use this format..."
2. Модель получает ещё одну попытку
3. Повторяется до `MaxToolCallRetries` (по умолчанию 3)
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
| Qwen3.5-2B | 2B | ⚠️ Работает | Минимальная, но может ошибаться |
| Qwen3.5-0.8B | 0.8B | ❌ Нестабильно | Только тесты |

> 💡 **Рекомендация: Qwen3.5-4B локально или Qwen3.5-35B (MoE) через API**  
> MoE-модели (Mixture of Experts) активируют только 3B параметров при инференсе — быстрые как 4B, точные как 35B.

## Тестирование

### EditMode Tests
- `MeaiToolCallsEditModeTests.cs` - MemoryTool, LuaTool, парсинг JSON

### PlayMode Tests  
- `AllToolCallsPlayModeTests.cs` - Memory tool + Execute Lua
- `ChatWithToolCallingPlayModeTests.cs` - Chat Agent + Inventory tool
- `CraftingMemoryViaLlmUnityPlayModeTests.cs` - Полный workflow крафта

## Breaking Changes v0.7.0

- ❌ `AgentMemoryDirectiveParser` удалён
- ❌ Fenced блоки (```memory, ```lua) не используются для tool calls
- ❌ `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- ✅ Programmer вызывает `execute_lua` tool вместо fenced блоков
- ✅ Chat Agent может вызывать `get_inventory` перед ответом игроку
