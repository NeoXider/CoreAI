# 🏗️ Agent Builder — Конструктор Кастомных Агентов

## Обзор

**AgentBuilder** — fluent API для быстрого создания кастомных агентов с уникальными инструментами, промптами и режимами работы. Позволяет легко добавлять новых NPC в игру без изменения ядра CoreAI.

### Возможности

- ✅ **Уникальные инструменты** — любые ILlmTool для конкретного агента
- ✅ **3 режима ответов** — ChatOnly, ToolsAndChat, ToolsOnly
- ✅ **Память** — персистентная память агента (write/append/clear)
- ✅ **История диалогов** — автоматическое сохранение контекста разговора
- ✅ **Минимум кода** — 3-5 строк на агента
- ✅ **Единый MEAI pipeline** — одинаковый tool calling для HTTP API и LLMUnity

---

## Быстрый старт

### 1. Создай агента

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. When player asks to buy, call get_inventory first.")
    .WithTool(new InventoryLlmTool(myInventoryProvider))
    .WithMemory()  // Персистентная память
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

merchant.ApplyToPolicy(policy);
```

### 2. Настрой бэкенд (единые настройки)

```
Unity → Create → CoreAI → CoreAI Settings
```

В Inspector выбери **LLM Backend**:
- **Auto** — автоматически выберет LLMUnity или HTTP API
- **LlmUnity** — локальная GGUF модель
- **OpenAiHttp** — HTTP API (LM Studio, OpenAI, Qwen)
- **Offline** — без модели (заглушка)

### 3. Создай клиент и вызывай

```csharp
// HTTP API
var client = MeaiLlmClient.CreateHttp(coreAiSettings, logger, memoryStore);

// LLMUnity (локальная модель)
var client = MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore);

// Вызов агента
var result = await client.CompleteAsync(new LlmCompletionRequest
{
    AgentRoleId = "Blacksmith",
    SystemPrompt = merchant.SystemPrompt,
    UserPayload = "Show me your swords",
    Tools = merchant.Tools  // ILlmTool[] из AgentBuilder
});
```

---

## Режимы ответов агента

### 1. ChatOnly — Только чат

Агент **не использует инструменты**. Только отвечает текстом на основе системного промпта и истории диалога.

**Когда использовать:** PlayerChat, Storyteller, Guide NPC

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the game world.")
    .WithChatHistory()  // Запоминать предыдущие реплики
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

**Поведение:**
- ❌ Не вызывает инструменты
- ✅ Отвечает текстом
- ✅ Помнит историю диалога (если включено)

---

### 2. ToolsAndChat — Инструменты + Чат (по умолчанию)

Агент **вызывает инструменты** когда нужно получить данные, затем **отвечает текстом** на основе результатов.

**Когда использовать:** Merchant, Crafter, Advisor, QuestGiver

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a shopkeeper. Check inventory before offering items.")
    .WithTool(new InventoryLlmTool(inventoryProvider))
    .WithMemory()  // Память: что купил игрок
    .WithChatHistory()  // История: предыдущие разговоры
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

**Поведение:**
- ✅ Вызывает инструменты когда нужно
- ✅ Отвечает текстом на основе данных от инструментов
- ✅ Помнит память и историю диалога

**Пример воркфлоу:**
```
Игрок: "Что у тебя есть?"
  ↓
Merchant: {"name": "get_inventory", "arguments": {}}  ← вызывает инструмент
  ↓
Tool: [Iron Sword(50), Potion(25), Armor(100)]        ← получает данные
  ↓
Merchant: "У меня есть Iron Sword за 50 монет..."     ← отвечает на основе данных
```

---

### 3. ToolsOnly — Только инструменты

Агент **только вызывает инструменты**. Не отвечает текстом игроку. Используется для фоновых задач.

**Когда использовать:** Background Analyzer, Auto-crafter, Telemetry collector

```csharp
var analyzer = new AgentBuilder("BackgroundAnalyzer")
    .WithSystemPrompt("Analyze session telemetry and detect anomalies.")
    .WithTool(new TelemetryLlmTool(telemetryProvider))
    .WithMode(AgentMode.ToolsOnly)
    .Build();
```

**Поведение:**
- ✅ Вызывает инструменты
- ❌ Не отвечает текстом (или минимальный ответ)
- ✅ Подходит для автоматических задач

---

## Память vs История диалогов

### Memory — Персистентная память

**Что это:** Долговременная память агента. Сохраняется между сессиями.

**Для чего:**
- Что купил игрок
- Какие квесты выполнил
- Важные факты о мире

**Управление:**
```csharp
// Модель сама управляет памятью через tool call
{"name": "memory", "arguments": {"action": "write", "content": "Player bought Iron Sword"}}
{"name": "memory", "arguments": {"action": "append", "content": "Player is friendly"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

**Включение:**
```csharp
var agent = new AgentBuilder("Merchant")
    .WithMemory()  // По умолчанию: Append
    .WithMemory(MemoryToolAction.Write)  // Или: Write (перезапись)
    .Build();
```

---

### ChatHistory — История диалога

**Что это:** Контекст текущей сессии разговора. Автоматически сохраняет все сообщения.

**Для чего:**
- Помнит что игрок спрашивал 5 минут назад
- Контекст для последующих ответов
- Не теряется при нескольких запросах подряд

**Включение:**
```csharp
var agent = new AgentBuilder("Storyteller")
    .WithChatHistory()  // Автоматически сохраняет все сообщения
    .Build();
```

**Как работает:**
```
Запрос 1: "Tell me about the forest"
  → Сохраняется в ChatHistory

Запрос 2: "What was the forest about?"
  → ChatHistory подставляется в контекст
  → Агент помнит предыдущий разговор
```

---

### Оба вместе

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithMemory()         // Долговременная: что купил игрок
    .WithChatHistory()    // Кратковременная: контекст текущего разговора
    .Build();
```

| | Memory | ChatHistory |
|--|--------|-------------|
| **Хранение** | JSON файл на диске | В LLMAgent (RAM) |
| **Срок** | Между сессиями | Текущая сессия |
| **Управление** | Модель через tool call | Автоматически |
| **Для чего** | Факты, покупки, квесты | Контекст разговора |

---

## Создание своего инструмента

### Пошаговая инструкция

**Шаг 1: Создай класс инструмента**

```csharp
// Должен реализовать интерфейс ILlmTool
public class MyTool : ILlmTool
{
    // 1. Уникальное имя (используется моделью для вызова)
    public string Name => "my_tool_name";

    // 2. Описание (модель читает его чтобы понять когда вызывать)
    public string Description => "Описание что делает инструмент";

    // 3. JSON схема параметров (если нужны параметры)
    public string ParametersSchema => "{}"; // Без параметров

    // 4. Создать AIFunction — это то что выполнится при вызове
    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) =>
            {
                // Твой код здесь
                return new { result = "success" };
            },
            Name,           // Имя функции
            Description     // Описание
        );
    }
}
```

**Шаг 2: Добавь инструмент агенту**

```csharp
var agent = new AgentBuilder("MyAgent")
    .WithSystemPrompt("You are an agent with custom tools.")
    .WithTool(new MyTool())  // ← Добавляем инструмент
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

agent.ApplyToPolicy(policy);
```

**Шаг 3: Модель вызовет инструмент автоматически**

Когда модель решит что нужен твой инструмент, она вернёт:
```json
{"name": "my_tool_name", "arguments": {}}
```

CoreAI распознает это, выполнит `MyTool.CreateAIFunction()` и вернёт результат модели.

---

### Базовый инструмент (без параметров)

```csharp
public class WeatherLlmTool : ILlmTool
{
    private readonly IWeatherProvider _weather;

    public WeatherLlmTool(IWeatherProvider weather)
    {
        _weather = weather;
    }

    public string Name => "get_weather";
    
    public string Description => "Get current weather in the game world.";
    
    public string ParametersSchema => "{}";

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) => 
            {
                var weather = await _weather.GetCurrentAsync(ct);
                return new { weather.Temperature, weather.Condition, weather.IsRaining };
            },
            "get_weather",
            "Get current weather in the game world.");
    }
}
```

### Инструмент с параметрами

```csharp
public class CraftItemTool : ILlmTool
{
    public string Name => "craft_item";
    
    public string Description => "Craft an item from ingredients.";
    
    public string ParametersSchema => 
        "{" +
        "  \"type\": \"object\"," +
        "  \"properties\": {" +
        "    \"ingredient1\": {\"type\": \"string\", \"description\": \"First ingredient\"}," +
        "    \"ingredient2\": {\"type\": \"string\", \"description\": \"Second ingredient\"}" +
        "  }," +
        "  \"required\": [\"ingredient1\", \"ingredient2\"]" +
        "}";

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (string ingredient1, string ingredient2, CancellationToken ct) => 
            {
                var result = await CraftingSystem.CraftAsync(ingredient1, ingredient2, ct);
                return new { result.ItemName, result.Quality, result.Success };
            },
            "craft_item",
            "Craft an item from two ingredients.");
    }
}
```

---

## Полные примеры

### Торговец с полным набором

```csharp
public static class MyGameAgents
{
    public static AgentConfig CreateMerchant(IInventoryProvider inventory)
    {
        return new AgentBuilder("Merchant")
            .WithSystemPrompt(@"You are a shopkeeper NPC. 
When player asks to buy or browse, FIRST call get_inventory tool.
Then respond in-character with items and prices.
Remember what the player bought using memory.")
            .WithTool(new InventoryLlmTool(inventory))
            .WithMemory(MemoryToolAction.Append)
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();
    }

    public static AgentConfig CreateQuestGiver(IQuestProvider quests)
    {
        return new AgentBuilder("QuestGiver")
            .WithSystemPrompt(@"You give quests to players.
When player asks for quests, call get_quests tool.
Track completed quests in memory.")
            .WithTool(new QuestsLlmTool(quests))
            .WithMemory(MemoryToolAction.Append)
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();
    }

    public static AgentConfig CreateStoryteller()
    {
        return new AgentBuilder("Storyteller")
            .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
            .WithChatHistory()
            .WithMode(AgentMode.ChatOnly)
            .Build();
    }

    public static AgentConfig CreateBackgroundAnalyzer(ITelemetryProvider telemetry)
    {
        return new AgentBuilder("BackgroundAnalyzer")
            .WithSystemPrompt("Analyze telemetry and detect anomalies.")
            .WithTool(new TelemetryLlmTool(telemetry))
            .WithMode(AgentMode.ToolsOnly)
            .Build();
    }
}
```

### Регистрация в игре

```csharp
void SetupAgents()
{
    var policy = new AgentMemoryPolicy();
    
    // Кастомные агенты
    MyGameAgents.CreateMerchant(GameServices.Inventory).ApplyToPolicy(policy);
    MyGameAgents.CreateQuestGiver(GameServices.Quests).ApplyToPolicy(policy);
    MyGameAgents.CreateStoryteller().ApplyToPolicy(policy);
    MyGameAgents.CreateBackgroundAnalyzer(GameServices.Telemetry).ApplyToPolicy(policy);
    
    // Сохранить политику в DI контейнере
    container.RegisterInstance(policy);
}
```

### Вызов агента

```csharp
async Task AskMerchant(string playerMessage)
{
    var orch = container.Resolve<AiOrchestrator>();
    
    await orch.RunTaskAsync(new AiTaskRequest
    {
        RoleId = "Merchant",  // Кастомный агент
        Hint = playerMessage
    });
}
```

---

## API Reference

### AgentBuilder

| Метод | Описание | Пример |
|-------|----------|--------|
| `WithSystemPrompt(string)` | Установить системный промпт | `.WithSystemPrompt("You are...")` |
| `WithTool(ILlmTool)` | Добавить инструмент | `.WithTool(new InventoryLlmTool(...))` |
| `WithTools(IEnumerable<ILlmTool>)` | Добавить несколько инструментов | `.WithTools(tools)` |
| `WithMemory(MemoryToolAction)` | Включить память | `.WithMemory()` или `.WithMemory(MemoryToolAction.Write)` |
| `WithChatHistory()` | Включить историю диалога | `.WithChatHistory()` |
| `WithMode(AgentMode)` | Установить режим | `.WithMode(AgentMode.ToolsAndChat)` |
| `Build()` | Создать AgentConfig | `.Build()` |

### AgentConfig

| Свойство | Тип | Описание |
|----------|-----|----------|
| `RoleId` | string | Уникальный ID агента |
| `SystemPrompt` | string | Системный промпт |
| `Tools` | IReadOnlyList<ILlmTool> | Список инструментов |
| `Mode` | AgentMode | Режим работы |

### AgentMode

| Значение | Описание |
|----------|----------|
| `ToolsOnly` | Только инструменты (без текста) |
| `ToolsAndChat` | Инструменты + текст (по умолчанию) |
| `ChatOnly` | Только текст (без инструментов) |

### MemoryToolAction

| Значение | Описание |
|----------|----------|
| `Write` | Полная замена памяти |
| `Append` | Добавление к существующей памяти |
| `Clear` | Очистка памяти |

---

## Архитектура

```
┌──────────────────────────────────────────────────────────────┐
│                       AgentBuilder                            │
├──────────────────────────────────────────────────────────────┤
│  new AgentBuilder("Merchant")                                │
│    .WithSystemPrompt("You are a shopkeeper...")  ← промпт    │
│    .WithTool(new InventoryLlmTool(...))          ← инструменты│
│    .WithMemory()                                 ← память    │
│    .WithChatHistory()                            ← история   │
│    .WithMode(AgentMode.ToolsAndChat)             ← режим     │
│    .Build()                                        ↓         │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                       AgentConfig                             │
├──────────────────────────────────────────────────────────────┤
│  RoleId: "Merchant"                                          │
│  SystemPrompt: "You are a shopkeeper..."                     │
│  Tools: [InventoryLlmTool, MemoryLlmTool]                    │
│  Mode: ToolsAndChat                                          │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│               AgentConfig.ApplyToPolicy(policy)               │
├──────────────────────────────────────────────────────────────┤
│  policy.SetToolsForRole("Merchant", [tools])                 │
│  policy.EnableMemoryTool("Merchant")                         │
│  policy.EnableChatHistory("Merchant")                        │
└──────────────────────────────────────────────────────────────┘
                              ↓
┌──────────────────────────────────────────────────────────────┐
│                    AiOrchestrator                              │
├──────────────────────────────────────────────────────────────┤
│  RunTaskAsync("Merchant", "What do you have?")               │
│    ↓                                                          │
│  → FunctionInvokingChatClient → tools=[inventory, memory]    │
│    ↓                                                          │
│  → Model: {"name": "get_inventory", "arguments": {}}         │
│    ↓                                                          │
│  → InventoryTool executes → [Iron Sword(50), Potion(25)]     │
│    ↓                                                          │
│  → Model: "I have Iron Sword for 50 coins..."                │
│    ↓                                                          │
│  → ChatHistory saves: user + assistant messages              │
└──────────────────────────────────────────────────────────────┘
```

---

## Тестирование

```csharp
[Test]
public void AgentBuilder_CreatesAgent_WithAllSettings()
{
    var config = new AgentBuilder("TestAgent")
        .WithSystemPrompt("Test prompt")
        .WithTool(new MemoryLlmTool())
        .WithChatHistory()
        .WithMode(AgentMode.ToolsAndChat)
        .Build();

    Assert.AreEqual("TestAgent", config.RoleId);
    Assert.AreEqual("Test prompt", config.SystemPrompt);
    Assert.AreEqual(1, config.Tools.Count);
    Assert.AreEqual(AgentMode.ToolsAndChat, config.Mode);
}
```

---

## Настройки

### CoreAISettings

```csharp
// До инициализации системы:
CoreAISettings.MaxLuaRepairGenerations = 5; // Лимит повторов Programmer (по умолчанию 3)
CoreAISettings.MaxToolCallRetries = 5;      // Лимит повторов tool call (по умолчанию 3)
CoreAISettings.EnableMeaiDebugLogging = true; // Отладка MEAI
CoreAISettings.LlmRequestTimeoutSeconds = 600; // Таймаут LLM (по умолчанию 300)
```

### Tool Call Retry

Если модель не смогла вызвать tool call в правильном формате, система автоматически даёт **3 попытки** (по умолчанию):

```
Attempt 1: Model returns wrong format
  ↓
System: "ERROR: Tool call not recognized. Use this format: {"name": "...", "arguments": {...}}"
  ↓
Attempt 2: Model retries
  ↓
(If still wrong)
  ↓
Attempt 3: Final attempt
  ↓
(If still wrong - accepts response as is)
```

Это помогает маленьким моделям (Qwen3.5-2B) которые иногда забывают формат.

---

## Рекомендуемые модели

| Модель | Размер | Tool Calling | Когда использовать |
|--------|--------|--------------|-------------------|
| **Qwen3.5-4B** | 4B | ✅ Отлично | **Рекомендуемая** для локального запуска |
| **Qwen3.5-35B (MoE) API** | 35B/3A | ✅ Превосходно | **Идеально** через API — быстро и точно |
| Qwen3.5-2B | 2B | ⚠️ Работает | Минимальная, но может ошибаться |
| Qwen3.5-0.8B | 0.8B | ❌ Нестабильно | Только тесты |

> 💡 **Рекомендация: Qwen3.5-4B локально или Qwen3.5-35B (MoE) через API**  
> MoE-модели активируют только часть параметров (3B) — быстрые как 4B, точные как 35B.

---

## Troubleshooting

### Агент не вызывает инструменты
- Убедитесь что `Mode = AgentMode.ToolsAndChat` или `ToolsOnly`
- Проверьте что инструменты переданы через `.WithTool()`
- Проверьте системный промпт — модель должна знать о инструментах

### Память не сохраняется
- Убедитесь что `.WithMemory()` вызван
- Проверьте что модель вызывает memory tool: `{"name": "memory", ...}`
- Включите логирование: `GameLogger.SetFeatureEnabled(GameLogFeature.Llm, true)`

### История диалога не работает
- Убедитесь что `.WithChatHistory()` вызван
- Проверяйте что `LLMAgent.history` не пустой
- История сохраняется в LLMAgent (RAM), не в файл
