# 🧪 Tool Calling Test Suite

Полный набор тестов для всех MEAI tool calls: Memory и Execute Lua.

## Тесты

### EditMode Tests (быстрые, без Unity)

| Тест | Что тестирует | Файл |
|------|---------------|------|
| `MemoryTool_CreateAIFunction_ReturnsNonNull` | Создание AIFunction для memory | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Write_SavesMemory` | Запись памяти | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Append_AppendsToExisting` | Добавление к памяти | `MeaiToolCallsEditModeTests.cs` |
| `MemoryTool_ExecuteAsync_Clear_RemovesMemory` | Очистка памяти | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_CreateAIFunction_ReturnsNonNull` | Создание AIFunction для Lua | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_ExecuteAsync_EmptyCode_ReturnsError` | Валидация пустого кода | `MeaiToolCallsEditModeTests.cs` |
| `LuaTool_ExecuteAsync_ValidCode_CallsExecutor` | Выполнение Lua кода | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_MemoryTool_ParsesCorrectly` | Парсинг memory tool call | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_LuaTool_ParsesCorrectly` | Парсинг execute_lua tool call | `MeaiToolCallsEditModeTests.cs` |
| `TryParseToolCallFromText_NoToolCall_ReturnsFalse` | Отсутствует tool call | `MeaiToolCallsEditModeTests.cs` |

### PlayMode Tests (с реальной LLM)

| Тест | Что тестирует | Бэкенд | Файл |
|------|---------------|--------|------|
| `AllToolCalls_MemoryTool_WriteAppendClear` | Write/Append/Clear память | LLMUnity или HTTP | `AllToolCallsPlayModeTests.cs` |
| `AllToolCalls_ExecuteLuaTool_Programmer` | Execute Lua от Programmer | LLMUnity или HTTP | `AllToolCallsPlayModeTests.cs` |
| `CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` | Боевой тест крафта с памятью | LLMUnity | `CraftingMemoryViaLlmUnityPlayModeTests.cs` |

## Запуск тестов

### EditMode (быстро)

```
Unity Test Runner → EditMode → Run All
```

### PlayMode с LLMUnity (локальная модель)

```bash
# Установить переменные окружения
export COREAI_PLAYMODE_LLM_BACKEND=llmunity

# Запустить в Unity
Unity Test Runner → PlayMode → AllToolCallsPlayModeTests
```

### PlayMode с HTTP API (LM Studio)

```bash
# Установить переменные окружения
export COREAI_PLAYMODE_LLM_BACKEND=http
export COREAI_OPENAI_TEST_BASE=http://localhost:1234/v1
export COREAI_OPENAI_TEST_MODEL=qwen3.5-2b

# Запустить в Unity
Unity Test Runner → PlayMode → AllToolCallsPlayModeTests
```

### Auto режим (по умолчанию)

```bash
# Без переменных - автоматически выберет LLMUnity или HTTP
Unity Test Runner → PlayMode → AllToolCallsPlayModeTests
```

## Единый формат Tool Calls

Все tool calls используют **один формат**:

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

### Memory Tool

```json
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Sword"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Shield"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

### Execute Lua Tool

```json
{"name": "execute_lua", "arguments": {"code": "create_item('Sword', 'weapon', 75)\nreport('crafted Sword')"}}
```

## Архитектура тестирования

```
┌─────────────────────────────────────────────────────────┐
│                Tool Call Tests                          │
├──────────────────┬──────────────────┬───────────────────┤
│   EditMode       │   PlayMode       │   PlayMode        │
│   (unit)         │   (LLM)          │   (LLM)           │
├──────────────────┼──────────────────┼───────────────────┤
│ MemoryTool       │ Memory Write     │ Crafting Memory   │
│ LuaTool          │ Memory Append    │ Workflow          │
│ JSON Parsing     │ Memory Clear     │                   │
│                  │ Execute Lua      │                   │
├──────────────────┼──────────────────┼───────────────────┤
│ Быстрые (1-2с)   │ Медленные (1-5м) │ Медленные (5-10м) │
│ Без Unity LLM    │ LLMUnity/GGUF    │ LLMUnity/GGUF     │
│                  │ или HTTP API     │ или HTTP API      │
└──────────────────┴──────────────────┴───────────────────┘
```

## Переключение бэкендов

`PlayModeProductionLikeLlmFactory` автоматически выбирает бэкенд:

1. **Auto** (по умолчанию): Пробует LLMUnity → HTTP
2. **LLMUnity**: Только локальная GGUF модель
3. **HTTP**: Только OpenAI-compatible API (LM Studio)

Переключение через `COREAI_PLAYMODE_LLM_BACKEND`:
- `auto` или пусто → Auto
- `llmunity`, `local`, `gguf` → LLMUnity
- `http`, `openai`, `openai_http` → HTTP API

## Ожидаемые результаты

### Memory Tool Test
- ✅ Write: память сохраняется в IAgentMemoryStore
- ✅ Append: новая память добавляется к существующей
- ✅ Clear: память полностью удаляется

### Execute Lua Tool Test
- ✅ Programmer вызывает execute_lua tool
- ✅ Lua код выполняется
- ✅ Команда публикуется в MessagePipe

### Crafting Memory Test
- ✅ Craft 1: память сохраняется
- ✅ Craft 2: модель видит память из Craft 1
- ✅ Все крафты уникальны
- ✅ Craft 4 (repeat) повторяет Craft 2 (детерминизм)

## Troubleshooting

### Настройка лимита повторов Programmer

По умолчанию **3 попытки** при ошибке Lua. Можно изменить:

```csharp
// До инициализации системы:
CoreAISettings.MaxLuaRepairRetries = 5; // Увеличить до 5
```

### LLMUnity не загружается
- Проверьте что GGUF модель существует
- Увеличьте timeout в `EnsureLlmUnityModelReady`
- Проверьте логи Unity на ошибки LLM

### HTTP API не отвечает
- Убедитесь что LM Studio запущен
- Проверьте `COREAI_OPENAI_TEST_BASE` (должен заканчиваться на `/v1`)
- Проверьте что модель загружена в LM Studio

### Tool calls не распознаются
- Проверьте формат JSON: `{"name": "...", "arguments": {...}}`
- Проверьте что tools передаются в `LlmCompletionRequest.Tools`
- Включите логирование `GameLogFeature.Llm`
