# 🗺️ Как команда от игрока проходит через всю систему

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Этот документ детально описывает путь команды игрока от момента ввода до исполнения в игровом мире. Понимание этого потока — ключ к отладке и расширению CoreAI.

---

## 1. Общая диаграмма потока

```mermaid
flowchart TB
    subgraph PLAYER ["🎮 Слой игрока"]
        Input["Ввод игрока<br/>(текст, действие, хоткей)"]
    end

    subgraph GAME ["🎯 Слой игры"]
        GameCode["Игровой код<br/>(MonoBehaviour / UI)"]
        TaskRequest["AiTaskRequest<br/>{RoleId, Hint, Priority,<br/>CancellationScope, TraceId}"]
    end

    subgraph ORCHESTRATION ["🧠 Слой оркестрации"]
        QueuedOrch["QueuedAiOrchestrator<br/>• Приоритетная очередь<br/>• Лимит параллелизма<br/>• Отмена предыдущей задачи"]
        AiOrch["AiOrchestrator<br/>• Назначает TraceId<br/>• Собирает промпты<br/>• Вставляет память<br/>• Валидация ответа"]
    end

    subgraph PROMPT ["📝 Сборка промпта"]
        PromptComposer["AiPromptComposer<br/>Universal Prefix + System Prompt<br/>+ Memory + User Payload"]
        MemoryLoad["IAgentMemoryStore<br/>TryLoad(roleId)"]
        PromptSource["Промпт:<br/>1. AgentPromptsManifest<br/>2. Resources/AgentPrompts/<br/>3. BuiltInAgentSystemPromptTexts"]
    end

    subgraph LLM ["🤖 Слой LLM"]
        LoggingDeco["LoggingLlmClientDecorator<br/>LLM ▶ / LLM ◀ / LLM ⏱"]
        Routing["RoutingLlmClient<br/>(маршрутизация по роли)"]
        MeaiClient["MeaiLlmClient<br/>+ FunctionInvokingChatClient<br/>(MEAI pipeline)"]
        
        subgraph BACKENDS ["Бэкенды"]
            LlmUnity["MeaiLlmUnityClient<br/>(локальная GGUF)"]
            OpenAI["OpenAiChatLlmClient<br/>(HTTP API)"]
            Stub["StubLlmClient<br/>(заглушка)"]
        end
    end

    subgraph TOOLCALL ["🔧 Tool Calling (MEAI)"]
        FuncInvoke["FunctionInvokingChatClient<br/>Распознаёт tool_calls"]
        
        subgraph TOOLS ["Доступные инструменты"]
            MemoryTool["🧠 MemoryTool<br/>write / append / clear"]
            LuaTool["📜 LuaTool<br/>execute_lua"]
            WorldTool["🌍 WorldTool<br/>spawn / move / destroy..."]
            InvTool["🎒 InventoryTool<br/>get_inventory"]
            ConfigTool["⚙️ GameConfigTool<br/>read / update"]
            SceneTool["🎭 SceneTool<br/>find / get / set"]
            CamTool["📸 CameraTool<br/>screenshot"]
            CustomTool["🧩 Custom ILlmTool"]
        end
    end

    subgraph MESSAGING ["📬 Слой сообщений (MessagePipe)"]
        Publish["Publish<br/>ApplyAiGameCommand<br/>{AiEnvelope, TraceId}"]
        Router["AiGameCommandRouter<br/>⚠️ Маршалинг на<br/>ГЛАВНЫЙ ПОТОК Unity"]
    end

    subgraph LUA ["🔧 Слой Lua (MoonSharp)"]
        LuaProcessor["LuaAiEnvelopeProcessor<br/>Извлекает Lua из ответа"]
        SecureLua["SecureLuaEnvironment<br/>+ LuaExecutionGuard<br/>+ LuaApiRegistry"]
        
        subgraph LUAAPI ["Lua API (whitelist)"]
            Report["report(string)"]
            Add["add(a, b)"]
            WorldAPI["coreai_world_spawn/move/destroy..."]
            GameBindings["IGameLuaRuntimeBindings<br/>(ваши функции)"]
        end
    end

    subgraph WORLD ["🌍 Слой мира (Unity)"]
        WorldExec["ICoreAiWorldCommandExecutor<br/>TryExecute()"]
        PrefabReg["CoreAiPrefabRegistryAsset<br/>(whitelist префабов)"]
        
        subgraph ACTIONS ["Действия в мире"]
            Spawn["GameObject.Instantiate"]
            Move["transform.position ="]
            Destroy["GameObject.Destroy"]
            Anim["Animator.Play"]
            Scene["SceneManager.Load"]
            UI["UI обновление"]
        end
    end

    subgraph REPAIR ["🔄 Авто-восстановление"]
        RepairLoop["Programmer Self-Heal<br/>до 3 попыток<br/>(MaxLuaRepairRetries)"]
    end

    %% Connections
    Input --> GameCode
    GameCode --> TaskRequest
    TaskRequest --> QueuedOrch
    QueuedOrch --> AiOrch
    
    AiOrch --> PromptComposer
    PromptComposer --> MemoryLoad
    PromptComposer --> PromptSource
    
    AiOrch --> LoggingDeco
    LoggingDeco --> Routing
    Routing --> MeaiClient
    MeaiClient --> FuncInvoke
    FuncInvoke --> LlmUnity
    FuncInvoke --> OpenAI
    FuncInvoke --> Stub
    
    FuncInvoke --> MemoryTool
    FuncInvoke --> LuaTool
    FuncInvoke --> WorldTool
    FuncInvoke --> InvTool
    FuncInvoke --> ConfigTool
    FuncInvoke --> SceneTool
    FuncInvoke --> CamTool
    FuncInvoke --> CustomTool
    
    AiOrch --> Publish
    Publish --> Router
    Router --> LuaProcessor
    LuaProcessor --> SecureLua
    SecureLua --> Report
    SecureLua --> Add
    SecureLua --> WorldAPI
    SecureLua --> GameBindings
    
    WorldAPI --> WorldExec
    WorldExec --> PrefabReg
    WorldExec --> Spawn
    WorldExec --> Move
    WorldExec --> Destroy
    WorldExec --> Anim
    WorldExec --> Scene
    WorldExec --> UI
    
    SecureLua -.->|"Ошибка Lua"| RepairLoop
    RepairLoop -.->|"Контекст ошибки"| AiOrch

    classDef player fill:#e8f5e9,stroke:#4caf50,stroke-width:2px
    classDef game fill:#e3f2fd,stroke:#2196f3,stroke-width:2px
    classDef orch fill:#fff3e0,stroke:#ff9800,stroke-width:2px
    classDef llm fill:#fce4ec,stroke:#e91e63,stroke-width:2px
    classDef tool fill:#f3e5f5,stroke:#9c27b0,stroke-width:2px
    classDef msg fill:#e0f2f1,stroke:#009688,stroke-width:2px
    classDef lua fill:#fff8e1,stroke:#ffc107,stroke-width:2px
    classDef world fill:#efebe9,stroke:#795548,stroke-width:2px
```

---

## 2. Пошаговый разбор (с номерами шагов)

### Шаг 1: Ввод игрока → `AiTaskRequest`

```csharp
// Игрок нажал кнопку крафта или ввёл текст в чат
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "CoreMechanicAI",           // Какой агент обработает
    Hint = "Скрафти оружие: Iron + Fire Crystal",  // Что сделать
    Priority = 5,                         // Приоритет (больше = важнее)
    CancellationScope = "crafting"        // Группа отмены
});
```

### Шаг 2: Очередь → `QueuedAiOrchestrator`

```
📋 Очередь задач:
┌──────────┬──────────┬────────────┬──────────────────┐
│ Priority │ RoleId   │ CancelScope│ Status           │
├──────────┼──────────┼────────────┼──────────────────┤
│    10    │ Creator  │ session    │ ⏳ В обработке    │
│     5    │ Mechanic │ crafting   │ ⏳ Ожидание       │ ← наша задача
│     1    │ Analyzer │ analytics  │ ⏳ Ожидание       │
└──────────┴──────────┴────────────┴──────────────────┘

Лимит параллелизма: MaxConcurrent = 2
```

**Что происходит:**
- Задача помещается в приоритетную очередь
- Если уже есть задача с тем же `CancellationScope` — предыдущая отменяется
- Когда слот освобождается — задача передаётся в `AiOrchestrator`

### Шаг 3: Сборка промпта → `AiPromptComposer`

```
═══════════════════════════════════════════════════
  ФИНАЛЬНЫЙ СИСТЕМНЫЙ ПРОМПТ (собирается из 3 частей)
═══════════════════════════════════════════════════

📌 Часть 1 — Universal Prefix (общий для всех):
"You are an AI agent in a game. Always stay in character."

📌 Часть 2 — Промпт роли (CoreMechanicAI):
"You are the CoreMechanicAI. Evaluate crafting recipes..."

📌 Часть 3 — Память агента (из прошлых вызовов):
"Previous memory: Craft#1: Iron Blade damage:45 fire:0"

═══════════════════════════════════════════════════
  USER PAYLOAD
═══════════════════════════════════════════════════
{
  "telemetry": { "wave": 3, "playerLevel": 5 },
  "hint": "Скрафти оружие: Iron + Fire Crystal"
}
```

### Шаг 4: Запрос к LLM → `ILlmClient`

```
┌─────────────────────────────────────────────────────┐
│  LoggingLlmClientDecorator                           │
│  📋 LLM ▶ [traceId=abc123] role=CoreMechanicAI       │
│                                                       │
│  ┌─────────────────────────────────────────────────┐ │
│  │  RoutingLlmClient                                │ │
│  │  Маршрут: CoreMechanicAI → OpenAiHttp             │ │
│  │                                                   │ │
│  │  ┌─────────────────────────────────────────────┐ │ │
│  │  │  MeaiLlmClient                              │ │ │
│  │  │  + FunctionInvokingChatClient                │ │ │
│  │  │  + SmartToolCallingChatClient                │ │ │
│  │  │    (дедупликация, защита от циклов)          │ │ │
│  │  │                                              │ │ │
│  │  │  Tools: [memory, execute_lua, game_config]   │ │ │
│  │  └─────────────────────────────────────────────┘ │ │
│  └─────────────────────────────────────────────────┘ │
│                                                       │
│  📋 LLM ◀ [traceId=abc123] 247 tokens, 1.2s           │
└─────────────────────────────────────────────────────┘
```

### Шаг 5: Модель отвечает (с Tool Call)

```json
// Модель возвращает tool call:
{
  "name": "memory",
  "arguments": {
    "action": "append",
    "content": "Craft#2: Iron + Fire Crystal → Flame Sword damage:45 fire:15"
  }
}
```

**MEAI Pipeline автоматически:**
1. Распознаёт tool call в ответе
2. Находит `MemoryTool` по имени `"memory"`
3. Вызывает `MemoryTool.ExecuteAsync(action, content)`
4. Результат → обратно модели → финальный текстовый ответ

### Шаг 6: Публикация → MessagePipe

```csharp
// AiOrchestrator публикует результат в шину:
messageBroker.Publish(new ApplyAiGameCommand
{
    CommandTypeId = "AiEnvelope",
    Payload = "```lua\ncreate_item(\"Flame Sword\", 75)\nadd_effect(\"fire_damage\", 15)\nreport(\"crafted Flame Sword\")\n```",
    TraceId = "abc123"
});
```

### Шаг 7: Маршрутизация → `AiGameCommandRouter`

```
⚠️ КРИТИЧНО: Переключение на ГЛАВНЫЙ ПОТОК Unity!

Background Thread ──→ UniTask.SwitchToMainThread() ──→ Main Thread
                                                          ↓
                                              LuaAiEnvelopeProcessor
                                                          ↓
                                                 SecureLuaEnvironment
```

### Шаг 8: Исполнение Lua → `SecureLuaEnvironment`

```lua
-- Lua исполняется в песочнице MoonSharp:
create_item("Flame Sword", 75)        -- → Whitelist API
add_effect("fire_damage", 15)         -- → Whitelist API
report("crafted Flame Sword")         -- → IGameLuaRuntimeBindings

-- Если Lua вызывает World Command:
coreai_world_spawn("SwordVFX", "fx_sword", 0, 1, 0)
-- → Публикует ApplyAiGameCommand{CommandTypeId = "WorldCommand"}
-- → AiGameCommandRouter → ICoreAiWorldCommandExecutor.TryExecute()
```

### Шаг 9: Авто-восстановление при ошибке (Self-Heal)

```
Попытка 1: LLM → Lua → ❌ Runtime Error: "bad argument to 'create_item'"
    ↓
Попытка 2: LLM (с контекстом ошибки) → Lua → ❌ Syntax Error
    ↓
Попытка 3: LLM (с историей ошибок) → Lua → ✅ Успех!
    ↓
LuaExecutionSucceeded { TraceId = "abc123" }
```

---

## 3. Диаграмма последовательности (Sequence Diagram)

```mermaid
sequenceDiagram
    actor Player as 🎮 Игрок
    participant Game as 🎯 Игра
    participant Queue as 📋 QueuedOrchestrator
    participant Orch as 🧠 AiOrchestrator
    participant Prompt as 📝 PromptComposer
    participant Memory as 💾 MemoryStore
    participant LLM as 🤖 LLM Client
    participant MEAI as ⚡ MEAI Pipeline
    participant Tools as 🔧 Tools
    participant Bus as 📬 MessagePipe
    participant Router as 🛤️ CommandRouter
    participant Lua as 📜 Lua Sandbox
    participant World as 🌍 Unity World

    Player->>Game: Ввод (текст / действие)
    Game->>Queue: RunTaskAsync(AiTaskRequest)
    
    Note over Queue: Приоритетная очередь<br/>Проверка CancellationScope
    Queue->>Orch: Передача задачи

    Orch->>Orch: Назначить TraceId
    Orch->>Prompt: Собрать промпт
    Prompt->>Memory: TryLoad(roleId)
    Memory-->>Prompt: AgentMemoryState
    Prompt-->>Orch: System + User prompt

    Orch->>LLM: CompleteAsync(request + tools)
    LLM->>MEAI: IChatClient.GetResponseAsync()
    MEAI->>MEAI: Отправка в модель

    alt Модель вызвала Tool
        MEAI->>Tools: AIFunction.InvokeAsync()
        Tools-->>MEAI: Tool Result
        MEAI->>MEAI: Результат → модели
        MEAI-->>LLM: Финальный ответ
    else Только текст
        MEAI-->>LLM: Текстовый ответ
    end

    LLM-->>Orch: LlmCompletionResult

    alt Programmer / Creator (с Lua)
        Orch->>Bus: Publish(ApplyAiGameCommand)
        Bus->>Router: Подписчик получает
        
        Note over Router: ⚠️ SwitchToMainThread
        Router->>Lua: LuaAiEnvelopeProcessor.Process()
        
        alt Lua вызывает World Command
            Lua->>World: coreai_world_spawn(...)
            World->>World: Instantiate / Move / Destroy
        end

        alt Lua ошибка
            Lua-->>Router: LuaExecutionFailed
            Router->>Orch: Повторный вызов (repair context)
            Note over Orch: До 3 попыток self-heal
        else Lua успех
            Lua-->>Router: LuaExecutionSucceeded
        end
    else Chat Agent (PlayerChat / AINpc)
        Orch-->>Game: Текстовый ответ
        Game-->>Player: Отображение в UI
    end
```

---

## 4. Поток для конкретных сценариев

### 4.1 Сценарий: Игрок спрашивает NPC-торговца

```
Игрок: "Что у тебя есть?"
  ↓
AiTaskRequest { RoleId = "Merchant", Hint = "Что у тебя есть?" }
  ↓
QueuedAiOrchestrator → AiOrchestrator
  ↓
PromptComposer: System="You are a shopkeeper..." + ChatHistory (последние 20 сообщений)
  ↓
LLM → FunctionInvokingChatClient
  ↓
Модель: {"name": "get_inventory", "arguments": {}}
  ↓
InventoryTool → [{name: "Iron Sword", price: 50, qty: 3}, ...]
  ↓
Результат → модели → "У меня отличные товары! Iron Sword за 50 монет..."
  ↓
Игрок видит ответ в чате 💬
```

### 4.2 Сценарий: Creator меняет сложность

```
Analyzer: "Игрок доминирует, скука растёт"
  ↓
AiTaskRequest { RoleId = "Creator", Hint = "Игрок слишком силён..." }
  ↓
Модель: 
  1. {"name": "memory", "arguments": {"action": "write", "content": "Wave 7: increased difficulty"}}
  2. Lua: coreai_world_spawn("EliteBoss", "boss_7", 50, 0, 50)
  ↓
MessagePipe → Router → Lua → coreai_world_spawn
  ↓
WorldCommandExecutor → PrefabRegistry → Instantiate(EliteBoss @ 50,0,50)
  ↓
Элитный босс появляется в мире! 🎮
```

### 4.3 Сценарий: Programmer чинит Lua

```
Creator: "Напиши скрипт награды за босса"
  ↓
AiTaskRequest { RoleId = "Programmer", Hint = "Скрипт награды..." }
  ↓
Попытка 1:
  Модель → {"name": "execute_lua", "arguments": {"code": "reward_player(500)\nreport('done')"}}
  Lua → ❌ "attempt to call 'reward_player' (a nil value)"
  ↓
Попытка 2 (с контекстом ошибки):
  Модель → {"name": "execute_lua", "arguments": {"code": "report('reward: 500 gold')"}}
  Lua → ✅ Success
  ↓
LuaExecutionSucceeded { TraceId = "abc123" }
```

---

## 5. Ключевые точки безопасности

| Точка | Защита | Описание |
|-------|--------|----------|
| **Очередь** | Приоритет + CancellationScope | Предотвращение спама задач |
| **Промпт** | Universal Prefix | Единые правила для всех агентов |
| **Tool Calling** | SmartToolCallingChatClient | Детекция дубликатов, защита от циклов |
| **Tool Retry** | MaxToolCallRetries (3) | Маленькие модели получают повторные попытки |
| **Lua** | SecureLuaEnvironment + Guard | Whitelist API, лимит шагов, wallclock |
| **World Commands** | PrefabRegistryAsset | Whitelist префабов для спавна |
| **Потоки** | MainThread маршалинг | Unity API только на главном потоке |
| **Self-Heal** | MaxLuaRepairRetries (3) | Лимит попыток восстановления Lua |

---

## 6. Визуальная карта файлов

```
CoreAI/Runtime/Core/
├── Orchestration/
│   ├── AiOrchestrator.cs          ← Главный оркестратор
│   ├── QueuedAiOrchestrator.cs    ← Очередь с приоритетами
│   ├── AiTaskRequest.cs           ← DTO запроса
│   └── AiPromptComposer.cs       ← Сборка промптов
├── Features/
│   ├── Llm/
│   │   ├── ILlmClient.cs         ← Интерфейс LLM
│   │   ├── ILlmTool.cs           ← Интерфейс инструмента
│   │   └── MeaiLlmClient.cs      ← MEAI pipeline
│   ├── AgentMemory/
│   │   ├── MemoryTool.cs          ← Tool для памяти
│   │   └── IAgentMemoryStore.cs   ← Хранилище памяти
│   ├── LuaExecution/
│   │   ├── SecureLuaEnvironment.cs ← Песочница Lua
│   │   └── LuaExecutionGuard.cs   ← Лимиты Lua
│   └── World/
│       └── CoreAiWorldCommandEnvelope.cs ← DTO команд мира

CoreAiUnity/Runtime/Source/
├── Composition/
│   └── CoreAILifetimeScope.cs     ← DI контейнер (VContainer)
├── Features/
│   ├── Llm/
│   │   ├── MeaiLlmUnityClient.cs  ← LLMUnity адаптер
│   │   ├── OpenAiChatLlmClient.cs ← HTTP API адаптер
│   │   └── RoutingLlmClient.cs    ← Маршрутизация по ролям
│   ├── Messaging/
│   │   └── AiGameCommandRouter.cs ← Маршрутизатор + main thread
│   ├── Lua/
│   │   └── LuaAiEnvelopeProcessor.cs ← Обработчик Lua конвертов
│   └── World/
│       └── CoreAiWorldCommandExecutor.cs ← Исполнитель команд мира
```

---

> 📖 **Связанные документы:**
> - [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) — формат JSON команд
> - [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) — архитектура и карта кода
> - [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) — роли агентов
> - [WORLD_COMMANDS.md](WORLD_COMMANDS.md) — команды мира
> - [MemorySystem.md](MemorySystem.md) — система памяти
