# 🛠️ MEAI Tool Calling — Архитектура

**Microsoft.Extensions.AI (MEAI)** — единый pipeline для tool calling на всех бэкендах.

---

## 📐 Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│                      ILlmClient                              │
├────────────────────────┬────────────────────────────────────┤
│ MeaiLlmUnityClient     │    OpenAiChatLlmClient              │
│   (локальная GGUF)     │    (HTTP API)                       │
├────────────────────────┼────────────────────────────────────┤
│ LlmUnityMeaiChatClient │    MeaiOpenAiChatClient             │
│   (MEAI.IChatClient)   │    (MEAI.IChatClient)               │
├────────────────────────┴────────────────────────────────────┤
│                MeaiLlmClient                                 │
│  ┌───────────────────────────────────────────────────────┐  │
│  │     MEAI.FunctionInvokingChatClient                    │  │
│  │  1. Model → tool_calls                                │  │
│  │  2. Находит AIFunction по имени                       │  │
│  │  3. Выполняет AIFunction.InvokeAsync()                │  │
│  │  4. Результат → модель → финальный ответ              │  │
│  └───────────────────────────────────────────────────────┘  │
├─────────────────────────────────────────────────────────────┤
│           AIFunction[] (MemoryTool, LuaTool и др.)          │
└─────────────────────────────────────────────────────────────┘
```

**Одинаковый MEAI pipeline для обоих бэкендов!**

---

## 🔧 Как работает

### 1. ILlmTool — декларативное описание

```csharp
public interface ILlmTool
{
    string Name { get; }           // "memory", "execute_lua", "get_inventory"
    string Description { get; }    // Что делает инструмент
    string ParametersSchema { get; } // JSON schema параметров
}
```

`ILlmTool` — только метаданные для system prompt'ов и роутинга.

### 2. AIFunction — исполнитель

```csharp
public class MemoryTool
{
    public AIFunction CreateAIFunction() => AIFunctionFactory.Create(
        async (string action, string? content, CancellationToken ct) => ExecuteAsync(action, content, ct),
        "memory",
        "Store, append, or clear persistent memory.");
}
```

`AIFunction` — оборачивает .NET метод для MEAI.

### 3. Маппинг ILlmTool → AIFunction

В `MeaiLlmClient.BuildAIFunctions()`:

```csharp
switch (tool)
{
    case MemoryLlmTool:  → new MemoryTool(store, roleId).CreateAIFunction()
    case LuaLlmTool:     → luaTool.CreateAIFunction()
    case InventoryLlmTool: → invTool.CreateAIFunction()
    case GameConfigLlmTool: → gcTool.CreateAIFunction()
}
```

### 4. MEAI Pipeline

```
1. Orchestrator → CompleteAsync(request.Tools)
2. MeaiLlmClient.BuildAIFunctions(tools) → AIFunction[]
3. FunctionInvokingChatClient(innerClient, tools)
4. Model → tool_calls: {"name": "memory", "arguments": {...}}
5. MEAI → находит AIFunction по имени → InvokeAsync()
6. Результат → модель → финальный ответ
7. MeaiLlmClient → LlmCompletionResult
```

---

## 📦 Файлы

### Ядро (CoreAI)

| Файл | Что делает |
|------|-----------|
| `ILlmTool.cs` | Интерфейс ILlmTool + LlmToolBase |
| `MemoryTool.cs` | AIFunction для памяти (write/append/clear) |
| `LuaTool.cs` | AIFunction для выполнения Lua |
| `InventoryTool.cs` | AIFunction для инвентаря |
| `GameConfigTool.cs` | AIFunction для конфигов |
| `WorldTool.cs` | AIFunction для управления миром |
| `MemoryLlmTool.cs` | ILlmTool → MemoryTool адаптер |
| `LuaLlmTool.cs` | ILlmTool → LuaTool адаптер |
| `InventoryLlmTool.cs` | ILlmTool → InventoryTool адаптер |
| `GameConfigLlmTool.cs` | ILlmTool → GameConfigTool адаптер |
| `WorldLlmTool.cs` | ILlmTool → WorldTool адаптер |

### Unity слой (CoreAiUnity)

| Файл | Что делает |
|------|-----------|
| `MeaiLlmClient.cs` | **Единый MEAI клиент** для всех бэкендов |
| `MeaiLlmUnityClient.cs` | Фабрика: LLMAgent → LlmUnityMeaiChatClient → MeaiLlmClient |
| `OpenAiChatLlmClient.cs` | Фабрика: HTTP → MeaiOpenAiChatClient → MeaiLlmClient |
| `LlmUnityMeaiChatClient.cs` | MEAI.IChatClient для LLMAgent |
| `MeaiOpenAiChatClient.cs` | MEAI.IChatClient для HTTP API |
| `CoreAISettingsAsset.cs` | Единые настройки (API, LLMUnity, retry, timeout) |

---

## 🚀 Использование

### Создание клиента

```csharp
// HTTP API
var client = new OpenAiChatLlmClient(settings, logger, memoryStore);

// LLMUnity
var client = new MeaiLlmUnityClient(unityAgent, logger, memoryStore);

// Оба используют MeaiLlmClient → FunctionInvokingChatClient
```

### Tool calling

```csharp
// Orchestrator передаёт tools в запрос
var result = await client.CompleteAsync(new LlmCompletionRequest
{
    AgentRoleId = "Creator",
    SystemPrompt = "...",
    UserPayload = "Craft an Iron Sword",
    Tools = policy.GetToolsForRole("Creator")  // ILlmTool[]
});

// MEAI автоматически:
// 1. Преобразует ILlmTool → AIFunction
// 2. Отправляет tools модели
// 3. Модель возвращает tool_calls
// 4. FunctionInvokingChatClient выполняет AIFunction
// 5. Результат → модель → финальный ответ
```

---

## 🎯 Преимущества MEAI

| До MEAI | После MEAI |
|---------|-----------|
| Ручной парсинг tool calls из текста | ✅ Автоматический pipeline |
| Разный код для LLMUnity и HTTP | ✅ Единый MeaiLlmClient |
| Retry вручную | ✅ MEAI обрабатывает цикл |
| Fallback хаки | ✅ Стандартный Microsoft подход |

---

## 🎯 Forced Tool Mode (v0.25.0+)

Иногда модель «забывает» вызвать инструмент даже там, где он явно нужен (например — пользователь явно попросил «сделай тест по спискам», а LLM отвечает текстом «я запустил тест…»). С v0.25.0 в `AiTaskRequest` и `LlmCompletionRequest` есть поле `ForcedToolMode` (enum `LlmToolChoiceMode`) для **детерминированного** выбора tool calling под конкретный запрос — по умолчанию `Auto` (совместимо с прежним поведением).

### API

```csharp
public enum LlmToolChoiceMode
{
    Auto = 0,            // default — модель сама решает
    RequireAny = 1,      // провайдер обязан вызвать ХОТЯ БЫ ОДИН инструмент
    RequireSpecific = 2, // провайдер обязан вызвать инструмент с именем RequiredToolName
    None = 3             // провайдер обязан ответить только текстом, tool calls запрещены
}
```

Установка:

```csharp
// Принудить любой tool call для этого запроса:
await orch.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Teacher",
    Hint = "сделай тест на списки",
    ForcedToolMode = LlmToolChoiceMode.RequireAny
});

// Принудить конкретный tool:
await orch.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Teacher",
    Hint = "spawn quiz",
    ForcedToolMode = LlmToolChoiceMode.RequireSpecific,
    RequiredToolName = "spawn_quiz"
});
```

### Маппинг на Microsoft.Extensions.AI

`MeaiLlmClient.ApplyForcedToolMode` транслирует значения 1-в-1 в `ChatOptions.ToolMode`:

| `LlmToolChoiceMode` | MEAI `ChatToolMode` | Семантика провайдера |
|---|---|---|
| `Auto` | `null` | OpenAI: `tool_choice: "auto"` (default) |
| `RequireAny` | `ChatToolMode.RequireAny` | OpenAI: `tool_choice: "required"` |
| `RequireSpecific` | `ChatToolMode.RequireSpecific(name)` | OpenAI: `tool_choice: {type: "function", function: {name: ...}}` |
| `None` | `ChatToolMode.None` | OpenAI: `tool_choice: "none"` |

Для `RequireSpecific` имя проверяется на наличие в зарегистрированных `AIFunction[]` для роли — если инструмент не найден, выводится warning и forced mode понижается до `RequireAny` (модель всё равно ОБЯЗАНА вызвать что-то — лучше шумно ошибиться, чем тихо прийти к не-tool ответу).

### Streaming + ForcedToolMode (v0.25.0)

В `MeaiLlmClient.CompleteStreamingAsync` forced mode применяется **только на первой итерации** tool-loop'а. После того как мы скармливаем модели результат tool, опции клонируются с `ChatToolMode.Auto` через `CloneOptionsWithAutoToolMode` — иначе модель оказалась бы заперта в бесконечном цикле tool calls (на каждом круге она была бы forced снова).

Это совместимо с тем, как работают многозвенные tool-цепочки в Claude Code / Cursor: первый tool call принудителен (если application-слой так решил), последующие — на усмотрение модели.

### Когда использовать

- **`Auto` (default).** 95% случаев. Модель в `ToolsAndChat` сама хорошо решает, когда дёргать tool.
- **`RequireAny`.** Когда детерминированно знаем, что нужен tool, но не важно какой именно. Например, intent classifier распознал «хочу проверить мои знания» — точно нужен какой-то interactive evaluation tool, не текст.
- **`RequireSpecific`.** Узкие интеграции: rerun-фикс, ремонт Lua, форс конкретного workflow. Используйте осторожно — слишком частая принудительная подмена tool calls бьёт по живости диалога.
- **`None`.** Когда tools зарегистрированы для роли, но именно для этого ответа их использовать нельзя (например, пост-tool reflection / суммаризация).

### Тесты

См. `Assets/CoreAiUnity/Tests/EditMode/ForcedToolModeEditModeTests.cs`.

---

## 📚 Ссылки

- [Microsoft.Extensions.AI Docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [FunctionInvokingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient)
- [AIFunctionFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactory)
- [ChatToolMode](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chattoolmode)
