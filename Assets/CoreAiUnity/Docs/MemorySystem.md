# 🧠 Система памяти агентов

## Два типа памяти

### Тип 1: MemoryTool (function call) — ЯВНАЯ ПАМЯТЬ

**Как работает:**
1. **Microsoft.Extensions.AI (MEAI)** интеграция через `FunctionInvokingChatClient`
2. `MemoryTool.CreateAIFunction()` создаёт AIFunction для MEAI
3. Модель вызывает функцию через **единый JSON формат**: `{"name": "memory", "arguments": {"action": "write", "content": "..."}}`
4. MEAI `FunctionInvokingChatClient` распознаёт вызов и выполняет `MemoryTool.ExecuteAsync()`
5. При следующем запросе память **подставляется в системный промпт**

**MEAI Pipeline:**
```
LLM Request → FunctionInvokingChatClient → LLMAgent
                    ↓
            [Model: {"name": "memory", "arguments": {...}}]
                    ↓
            AIFunction (MemoryTool) executes
                    ↓
            [Tool result returned]
                    ↓
            Final response → AiOrchestrator
```

**Три действия (единый формат):**
```json
{"name": "memory", "arguments": {"action": "write", "content": "Craft#1: Iron Blade damage:45"}}
{"name": "memory", "arguments": {"action": "append", "content": "Craft#2: Steel Longsword damage:72"}}
{"name": "memory", "arguments": {"action": "clear"}}
```

**Когда использовать:**
- ✅ CoreMechanicAI — история крафтов
- ✅ Creator — дизайн-решения
- ✅ Programmer — сохранённые Lua формулы
- ✅ Analyzer — рекомендации и наблюдения

**Конфигурация по умолчанию:**
```csharp
// Все роли используют MemoryTool с append
var policy = new AgentMemoryPolicy();

// Выключить для конкретной роли
policy.DisableMemoryTool("PlayerChat");

// Включить для всех
policy.SetMemoryToolForAll(enabled: true);

// Настроить действие по умолчанию
policy.ConfigureRole("CoreMechanicAI", defaultAction: MemoryToolAction.Append);
policy.ConfigureRole("Creator", defaultAction: MemoryToolAction.Write);
```

---

### Тип 2: ChatHistory (LLMUnity) — ПОЛНЫЙ КОНТЕКСТ

**Как работает:**
1. `MeaiLlmUnityClient` вызывается с `useChatHistory: true`
2. При `CompleteAsync()`:
   - Загружает последние 20 сообщений из `IAgentMemoryStore.GetChatHistory()`
   - Вставляет в `LLMAgent.AddToHistory()`
   - Вызывает `Chat(addToHistory: true)`
   - Сохраняет user + assistant сообщения обратно в хранилище

**Когда использовать:**
- ✅ PlayerChat — контекст разговора с игроком
- ✅ AINpc — последовательные реплики NPC
- ✅ Когда модель «забывает» что было в предыдущих сообщениях

**Не использовать когда:**
- ❌ Нужен контроль над тем ЧТО модель видит (лучше MemoryTool)
- ❌ Экономия токенов (ChatHistory шлёт ВСЮ историю)
- ❌ Модель не поддерживает длинный контекст

---

## Сравнение

| Аспект | MemoryTool (Тип 1) | ChatHistory (Тип 2) |
|--------|-------------------|---------------------|
| **Кто решает** | Модель вызывает функцию | Код автоматически сохраняет |
| **Контроль** | Модель пишетшет ЧТО запомнить | Сохраняется ВСЁ |
| **Размер** | Компактный (модель сжимает) | Полный (все сообщения) |
| **Токены** | Экономит (только важное) | Тратит (вся история) |
| **LLMUnity** | Работает всегда | Только с `useChatHistory: true` |
| **HTTP/OpenAI** | Работает | ❌ Нет (нужен чат-объект) |
| **Персистентность** | ✅ FileAgentMemoryStore | ✅ FileAgentMemoryStore |

---

## Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│                     AiOrchestrator                          │
│                                                             │
│  ┌───────────────────┐    ┌──────────────────────────────┐  │
│  │ Тип 1: MemoryTool │    │  Тип 2: ChatHistory           │  │
│  │                   │    │  (только LLMUnity)            │  │
│  │ 1. Читает память  │    │                               │  │
│  │    из store       │    │ 1. Загружает последние 20     │  │
│  │ 2. Вставляет в    │    │    сообщений в LLMAgent       │  │
│  │    system prompt  │    │ 2. Chat(addToHistory: true)   │  │
│  │ 3. Модель пишет   │    │ 3. Сохраняет user+assistant   │  │
│  │    {"tool":"mem"} │    │    в store                    │  │
│  │ 4. Сохраняет      │    │                               │  │
│  └───────────────────┘    └──────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
         ↓                              ↓
┌────────────────────────────────────────────────┐
│              IAgentMemoryStore                 │
│                                                │
│  TryLoad(roleId) → AgentMemoryState            │
│  Save(roleId, state)                           │
│  Clear(roleId)                                 │
│  AppendChatMessage(roleId, role, content)      │
│  GetChatHistory(roleId, maxMessages)           │
└────────────────────────────────────────────────┘
         ↓
┌──────────────────────┐    ┌──────────────────────────┐
│ InMemoryStore        │    │ FileAgentMemoryStore     │
│ (тесты, Dictionary)  │    │ (Unity, persistentData)  │
└──────────────────────┘    └──────────────────────────┘
```

---

## Конфигурация памяти по ролям

| Роль | MemoryTool | Default Action | ChatHistory | Зачем |
|------|:----------:|:--------------:|:-----------:|-------|
| **Creator** | ✅ | Write | ❌ | Перезаписывает дизайн-решения |
| **Analyzer** | ✅ | Append | ❌ | Накапливает наблюдения |
| **Programmer** | ✅ | Append | ❌ | Сохраняет исправленные формулы |
| **CoreMechanicAI** | ✅ | Append | ❌ | История крафтов (детерминизм) |
| **AINpc** | ❌ | - | ✅ | Контекст реплик NPC |
| **PlayerChat** | ❌ | - | ✅ | Контекст диалога с игроком |

---

## Примеры использования

### Пример 1: CoreMechanicAI — история крафтов (MemoryTool)

```csharp
// Настройка
var policy = new AgentMemoryPolicy();
policy.ConfigureRole("CoreMechanicAI",
    useMemoryTool: true,
    defaultAction: AgentMemoryPolicy.MemoryToolAction.Append);

// Запрос к модели
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "CoreMechanicAI",
    Hint = "Скрафти оружие из Iron + Fire Crystal. " +
           "Сохрани в память: {\"tool\":\"memory\",\"action\":\"write\"," +
           "\"content\":\"Craft#1: Iron Fireblade damage:45 fire:15\"}"
});

// Память сохранена: "Craft#1: Iron Fireblade damage:45 fire:15"
// При следующем запросе модель ВИДИТ эту память в system prompt
```

### Пример 2: PlayerChat — контекст диалога (ChatHistory)

```csharp
// Настройка LLMUnity клиента
var client = new MeaiLlmUnityClient(
    llmAgent,
    logger,
    memoryStore: fileStore,
    memoryPolicy: policy,
    useChatHistory: true  // ← Тип 2: полный контекст
);

// Диалог 1
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "PlayerChat",
    Hint = "Меня зовут Алексей"
});
// Сохранено: user="Меня зовут Алексей", assistant="Приятно познакомиться, Алексей!"

// Диалог 2 — модель ПОМНИТ имя!
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "PlayerChat",
    Hint = "Как меня зовут?"
});
// Модель ответит: "Вас зовут Алексей" (видит историю из 20 сообщений)
```

### Пример 3: Выключить память для роли

```csharp
var policy = new AgentMemoryPolicy();
policy.DisableMemoryTool("PlayerChat");  // PlayerChat не использует MemoryTool
policy.SetMemoryToolForAll(false);        // Выключить для ВСЕХ (только ChatHistory)
```

---

## Файлы

| Файл | Назначение |
|------|-----------|
| `AgentMemoryPolicy.cs` | Конфигурация: кто использует какой тип |
| `IAgentMemoryStore.cs` | Интерфейс хранилища (+ ChatHistory методы) |
| `AgentMemoryState.cs` | Состояние: LastSystemPrompt + Memory |
| `MemoryTool.cs` | Microsoft.Extensions.AI функция для модели |
| `AgentMemoryDirectiveParser.cs` | Парсит `{"tool":"memory"...}` из ответа |
| `NullAgentMemoryStore.cs` | Заглушка (ничего не сохраняет) |
| `FileAgentMemoryStore.cs` | Unity: JSON файлы в persistentDataPath |
| `AiOrchestrator.cs` | Orchestrator: injects memory into system prompt |
| `MeaiLlmUnityClient.cs` | LLMUnity с MEAI: поддержка MemoryTool (Тип 1) и ChatHistory (Тип 2) |
