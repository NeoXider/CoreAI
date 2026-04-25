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
| `CompleteStreamingAsync_ToolJsonInStream_ExecutesToolAndReturnsFinalText` | Streaming tool cycle: tool JSON → execute → continued text | `MeaiLlmClientEditModeTests.cs` |
| `CompleteStreamingAsync_ToolJsonWithVisiblePrefix_KeepsPrefixAndHidesJson` | В стриме префиксный текст виден, tool JSON скрыт из UI | `MeaiLlmClientEditModeTests.cs` |
| `CompleteStreamingAsync_TooManyToolIterations_ReturnsTerminalError` | Защита от бесконечного streaming tool-loop | `MeaiLlmClientEditModeTests.cs` |

### EditMode Tests (v0.24.0 — parser hardening)

| Тест | Что тестирует | Файл |
|------|---------------|------|
| `SingleToolCall_ExtractedCorrectly` | Одиночный tool call с prefix text | `MeaiLlmClientEditModeTests.cs` |
| `MultipleToolCalls_AllExtracted` | Несколько tool calls в одном тексте | `MeaiLlmClientEditModeTests.cs` |
| `JsonInCodeBlock_NotExtracted` | JSON в code block игнорируется (false positive protection) | `MeaiLlmClientEditModeTests.cs` |
| `MalformedJson_GracefullySkipped` | Неполный JSON не вызывает ошибку | `MeaiLlmClientEditModeTests.cs` |
| `JsonWithoutNameAndArguments_NotExtracted` | Обычный JSON без name+arguments — не tool call | `MeaiLlmClientEditModeTests.cs` |
| `EmptyText_ReturnsFalse` | Пустая строка/null → false | `MeaiLlmClientEditModeTests.cs` |
| `NestedBracesInArguments_HandledCorrectly` | Вложенные JSON объекты в arguments | `MeaiLlmClientEditModeTests.cs` |
| `StripCodeBlocks_PreservesPositions` | Удаление code blocks сохраняет позиции | `MeaiLlmClientEditModeTests.cs` |
| `IsValidToolCallJson_RequiresBothKeys` | Валидация наличия name+arguments | `MeaiLlmClientEditModeTests.cs` |
| `FindToolCallJsonSpans_MultipleSpans` | Поиск нескольких JSON spans | `MeaiLlmClientEditModeTests.cs` |
| `ToolCallWithStringContainingBraces_HandledCorrectly` | Строка с {} в аргументах | `MeaiLlmClientEditModeTests.cs` |

### EditMode Tests (v0.24.0 — ToolExecutionPolicy)

| Тест | Что тестирует | Файл |
|------|---------------|------|
| `CheckDuplicate_FirstCall_ReturnsNull` | Первый вызов не блокируется | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_SameSignatureTwice_BlocksSecond` | Повторный одинаковый вызов заблокирован | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_DifferentArgs_Allowed` | Разные аргументы — не дубликат | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_AllowDuplicatesGlobal_NeverBlocks` | Глобальный AllowDuplicates=true | `ToolExecutionPolicyEditModeTests.cs` |
| `CheckDuplicate_PerToolAllowDuplicates_Respected` | Per-tool AllowDuplicates flag | `ToolExecutionPolicyEditModeTests.cs` |
| `RecordSuccess_ResetsCounter` | Успех сбрасывает счётчик ошибок | `ToolExecutionPolicyEditModeTests.cs` |
| `RecordFailure_IncrementsCounter` | Провал инкрементирует счётчик | `ToolExecutionPolicyEditModeTests.cs` |
| `IsMaxErrorsReached_AtThreshold_ReturnsTrue` | Порог ошибок работает | `ToolExecutionPolicyEditModeTests.cs` |
| `Reset_ClearsEverything` | Reset сбрасывает и дубликаты и счётчик | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteSingle_ToolFound_ReturnsResult` | Найденный инструмент возвращает результат | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteSingle_ToolNotFound_ReturnsFailed` | Ненайденный инструмент → failed | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteBatch_AllSucceed_ResetsErrorCounter` | Пакетный успех сбрасывает ошибки | `ToolExecutionPolicyEditModeTests.cs` |
| `ExecuteBatch_DuplicateBlocked_ReturnsFailed` | Пакетный дубликат блокируется | `ToolExecutionPolicyEditModeTests.cs` |
| `BuildMaxErrorsResponse_ContainsErrorText` | Ответ при max errors содержит ошибку | `ToolExecutionPolicyEditModeTests.cs` |

### EditMode Tests (composition reliability)

| Тест | Что тестирует | Файл |
|------|---------------|------|
| `Start_FirstEntryPoint_InitializesCoreAiFacade` | Первый старт инициализирует фасад CoreAI | `CoreAIGameEntryPointEditModeTests.cs` |
| `Start_SecondEntryPoint_IsSkippedAndDoesNotOverrideFacade` | Повторный старт не переинициализирует CoreAI и не перетирает зависимости | `CoreAIGameEntryPointEditModeTests.cs` |

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
│   EditMode       │   EditMode       │   PlayMode        │
│   (unit)         │   (policy/parser)│   (LLM)           │
├──────────────────┼──────────────────┼───────────────────┤
│ MemoryTool       │ ToolExecPolicy   │ Memory Write      │
│ LuaTool          │  └ Duplicates    │ Memory Append     │
│ JSON Parsing     │  └ Error Countr  │ Memory Clear      │
│ Streaming cycle  │  └ Batch exec    │ Execute Lua       │
│ Code block guard │ TryExtractToolC  │ Crafting Memory   │
│ Multi-tool       │  └ Multi-tool    │ Workflow          │
│                  │  └ Code blocks   │                   │
├──────────────────┼──────────────────┼───────────────────┤
│ Быстрые (1-2с)   │ Быстрые (1-2с)   │ Медленные (1-5м)  │
│ Без Unity LLM    │ Без Unity LLM    │ LLMUnity/GGUF     │
│                  │                  │ или HTTP API      │
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
