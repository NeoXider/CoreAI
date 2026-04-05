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

### 3. Game Config Tool

**Назначение:** Чтение и изменение игровых конфитов.

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
CoreAISettings.EnableMeaiDebugLogging = true;  // Отладка MEAI
CoreAISettings.LlmRequestTimeoutSeconds = 300; // Таймаут LLM
```

## Тестирование

### EditMode Tests
- `MeaiToolCallsEditModeTests.cs` - MemoryTool, LuaTool, парсинг JSON

### PlayMode Tests  
- `AllToolCallsPlayModeTests.cs` - Боевые тесты с LLM (LLMUnity или HTTP)
- `CraftingMemoryViaLlmUnityPlayModeTests.cs` - Полный workflow крафта

## Архитектура

```
AiOrchestrator → MeaiLlmUnityClient → FunctionInvokingChatClient
                                         ↓
                              LlmUnityMeaiChatClient.TryParseToolCallFromText()
                                         ↓
                              MemoryTool / LuaTool / GameConfigTool
```

## Breaking Changes v0.7.0

- ❌ `AgentMemoryDirectiveParser` удалён
- ❌ Fenced блоки (```memory, ```lua) не используются для tool calls
- ❌ `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- ✅ Programmer вызывает `execute_lua` tool вместо fenced блоков
