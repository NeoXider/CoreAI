# 📋 Tool Calling Specification v0.7.0

## Единый формат MEAI Tool Calls

Все tool calls в CoreAI используют **единый JSON формат** через Microsoft.Extensions.AI (MEAI):

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

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

### 3. Get Inventory Tool (Merchant NPC)

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

```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather.";
    public string ParametersSchema => "{}";
    
    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) => await _provider.GetWeatherAsync(ct),
            "get_weather", "Get current weather.");
    }
}
```

Подробнее: [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md)

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
