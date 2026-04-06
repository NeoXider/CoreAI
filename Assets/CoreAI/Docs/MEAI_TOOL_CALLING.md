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
| `MemoryLlmTool.cs` | ILlmTool → MemoryTool адаптер |
| `LuaLlmTool.cs` | ILlmTool → LuaTool адаптер |
| `InventoryLlmTool.cs` | ILlmTool → InventoryTool адаптер |
| `GameConfigLlmTool.cs` | ILlmTool → GameConfigTool адаптер |

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

## 📚 Ссылки

- [Microsoft.Extensions.AI Docs](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [FunctionInvokingChatClient](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.functioninvokingchatclient)
- [AIFunctionFactory](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.aifunctionfactory)
