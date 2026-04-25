# 🔧 Troubleshooting Guide — CoreAI

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Руководство по решению типичных проблем при работе с CoreAI.

---

## Содержание

- [🤖 Проблема: Модель не отвечает](#-проблема-модель-не-отвечает)
- [📜 Проблема: Lua упала](#-проблема-lua-упала)
- [🧠 Проблема: Память не пишется](#-проблема-память-не-пишется)
- [🔧 Проблема: Tool Call не работает](#-проблема-tool-call-не-работает)
- [🌍 Проблема: World Command не исполняется](#-проблема-world-command-не-исполняется)
- [⏳ Проблема: Тесты зависают](#-проблема-тесты-зависают)
- [🔌 Проблема: DI / VContainer ошибки](#-проблема-di--vcontainer-ошибки)
- [📊 Диагностика: Как включить подробные логи](#-диагностика-как-включить-подробные-логи)

---

## 🤖 Проблема: Модель не отвечает

### Симптомы
- Пустой ответ от LLM (`"Empty response from LLM"`)
- Таймаут запроса
- `StubLlmClient` вместо реальной модели
- Нет логов `LLM ▶` / `LLM ◀` в консоли

### Диагностика

**Шаг 1: Проверьте какой бэкенд выбран**

В консоли Unity при старте должно быть сообщение о бэкенде:
```
[CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1
```
или
```
[CoreAI] Backend: LlmUnity → Qwen3.5-4B
```
или
```
[CoreAI] Backend: Stub (offline mode)  ← ❌ Проблема!
```

**Шаг 2: Определите причину по бэкенду**

---

### 🔌 LLMUnity не отвечает

| Проверка | Как проверить | Решение |
|----------|--------------|---------|
| LLMAgent на сцене? | Hierarchy → ищите объект с `LLMAgent` | Создайте `LLMAgent` на сцене |
| LLM компонент есть? | Inspector LLMAgent → есть ли `LLM`? | Добавьте `LLM` компонент |
| GGUF файл существует? | Inspector LLM → Model Path | Скачайте модель через LLMUnity или LM Studio |
| Сервис запущен? | Логи `LLMUnity: started` | Увеличьте `Startup Timeout` в CoreAISettings |
| VRAM достаточно? | Task Manager → GPU Memory | Уменьшите модель (4B вместо 9B) или `numGPULayers` |

```csharp
// Проверка программно:
var agent = FindObjectOfType<LLMAgent>();
Debug.Log($"LLMAgent found: {agent != null}");
Debug.Log($"LLM started: {agent?.llm?.started}");
```

**Типичное решение:**
```
CoreAISettings → LLMUnity → ✅ Keep Alive = true
CoreAISettings → LLMUnity → Startup Timeout = 120
```

---

### 🌐 HTTP API не отвечает

| Проверка | Как проверить | Решение |
|----------|--------------|---------|
| Сервер запущен? | Браузер → `http://localhost:1234/v1/models` | Запустите LM Studio / Ollama |
| URL правильный? | CoreAISettings → HTTP API → Base URL | Без `/` в конце: `http://localhost:1234/v1` |
| Модель загружена? | LM Studio → Status = "Loaded" | Загрузите модель в LM Studio |
| API Key нужен? | OpenAI → да, LM Studio → нет | Для LM Studio оставьте API Key **пустым** |
| Порт открыт? | `Test-NetConnection localhost -Port 1234` | Проверьте firewall |

**Быстрая проверка через PowerShell:**
```powershell
# Проверка доступности API
Invoke-RestMethod -Uri "http://localhost:1234/v1/models" -Method GET

# Тестовый запрос
$body = @{
    model = "qwen3.5-4b"
    messages = @(@{ role = "user"; content = "Say OK" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

**Типичное решение:**
```
1. Запустить LM Studio
2. Загрузить модель (Qwen3.5-4B)
3. Включить Local Server (порт 1234)
4. CoreAISettings → Backend = OpenAiHttp
5. CoreAISettings → Base URL = http://localhost:1234/v1
6. Нажать "🔗 Test Connection"
```

---

### 🔇 Stub вместо модели (Silent Fallback)

**Почему выбрался Stub:**
1. Backend = Auto, но ни LLMUnity, ни HTTP не доступны
2. LLMAgent не найден на сцене и HTTP URL не настроен
3. Определитель `COREAI_NO_LLM` включён (ручной opt-out)
4. Пакет `undream.llmunity` не установлен (`COREAI_HAS_LLMUNITY` не определён) — LLMUnity-бэкенд недоступен

**Решение:**
```
CoreAISettings → Backend Type = OpenAiHttp (или LlmUnity)
```

Или проверьте, что Auto-режим имеет хотя бы один доступный бэкенд:
```
CoreAISettings → Backend Type = Auto
CoreAISettings → Auto Priority = HTTP First  ← если HTTP основной
```

---

### ⏱️ Таймаут запроса

```
[Error] LLM request timed out after 15 seconds
```

**Решение:** Увеличьте таймаут:
```
CoreAISettings → ⚙️ Общие → LLM Timeout = 120
```

Для больших моделей (9B+) или слабого железа таймаут может потребоваться 120-300 секунд.

---

## 📜 Проблема: Lua упала

### Симптомы
- `LuaExecutionFailed` в логах
- `[Error] MoonSharp runtime: ...` в ответе модели
- Бесконечные циклы self-heal (до 3 попыток)
- `LuaExecutionGuard: step limit exceeded`

### Диагностика

**Тип 1: Синтаксическая ошибка Lua**
```
[Error] MoonSharp: chunk_1:(3,0-4): unexpected symbol near 'end'
```

**Причина:** Модель сгенерировала невалидный Lua код.

**Решение:**
- Система автоматически делает до 3 попыток self-heal
- Если не помогает → улучшите промпт Programmer с примерами валидного Lua
- Или используйте более мощную модель (4B+ вместо 2B)

---

**Тип 2: Вызов несуществующей функции**
```
[Error] MoonSharp runtime: attempt to call 'custom_function' (a nil value)
```

**Причина:** Lua код пытается вызвать функцию, которой нет в whitelist API.

**Решение:** Добавьте функцию в `IGameLuaRuntimeBindings`:
```csharp
public class MyGameBindings : IGameLuaRuntimeBindings
{
    public void RegisterBindings(Script script)
    {
        script.Globals["custom_function"] = (Action<string>)(msg => {
            Debug.Log($"Custom: {msg}");
        });
    }
}
```

Или укажите в промпте Programmer'а какие функции доступны:
```
Available Lua API: report(string), add(a,b), coreai_world_spawn(...), ...
Do NOT use any other functions.
```

---

**Тип 3: Бесконечный цикл (Step Limit)**
```
[Warning] LuaExecutionGuard: step limit exceeded (10000 steps)
```

**Причина:** Lua код содержит бесконечный цикл или очень ресурсоёмкую операцию.

**Решение:**
- `LuaExecutionGuard` автоматически прерывает через wall-clock и шаги
- Убедитесь что Guard включён (по умолчанию — вкл)
- Настройте лимиты при необходимости

---

**Тип 4: Lua repair исчерпал попытки**
```
[Warning] Programmer repair: max retries (3) exceeded for traceId=abc123
```

**Причина:** 3 попытки self-heal не помогли.

**Решение:**
1. Увеличьте `CoreAISettings.MaxLuaRepairRetries` (по умолчанию 3)
2. Улучшите системный промпт Programmer (добавьте примеры)
3. Используйте более мощную модель
4. Проверьте что whitelist API корректен

---

## 🧠 Проблема: Память не пишется

### Симптомы
- Агент «забывает» информацию между вызовами
- Файл `persistentDataPath/CoreAI/AgentMemory/<RoleId>.json` пустой или не создаётся
- Память не появляется в системном промпте

### Диагностика

**Шаг 1: Проверьте что память включена для роли**

```csharp
// По умолчанию память ВКЛЮЧЕНА для:
// Creator, Analyzer, Programmer, CoreMechanicAI
// ВЫКЛЮЧЕНА для:
// PlayerChat, AINpc (они используют ChatHistory)

var policy = container.Resolve<AgentMemoryPolicy>();
Debug.Log($"Memory enabled for Creator: {policy.IsMemoryToolEnabled("Creator")}");
```

**Шаг 2: Проверьте что модель вызывает tool**

Включите MEAI Debug Logging:
```
CoreAISettings → 🔧 Отладка → MEAI Debug Logging = ✅
```

В логах должно появиться:
```
[MEAI] Tool call detected: name=memory, arguments={action: write, content: ...}
[MEAI] Tool result: Memory saved
```

Если tool call НЕ вызывается — проблема в промпте. Добавьте явную инструкцию:
```
You MUST save important information using the memory tool:
{"name": "memory", "arguments": {"action": "write", "content": "..."}}
```

**Шаг 3: Проверьте хранилище**

```csharp
var store = container.Resolve<IAgentMemoryStore>();
if (store.TryLoad("Creator", out var state))
{
    Debug.Log($"Creator memory: {state.Memory}");
}
else
{
    Debug.Log("No memory found for Creator");
}
```

**Шаг 4: Проверьте путь к файлу**

```csharp
Debug.Log($"Memory path: {Application.persistentDataPath}/CoreAI/AgentMemory/");
```

| Платформа | Путь |
|-----------|------|
| Windows | `%APPDATA%/../LocalLow/<Company>/<Product>/CoreAI/AgentMemory/` |
| macOS | `~/Library/Application Support/<Company>/<Product>/CoreAI/AgentMemory/` |
| Android | `/data/data/<package>/files/CoreAI/AgentMemory/` |
| WebGL | IndexedDB (через Unity's persistentDataPath) |

### Типичные решения

| Проблема | Решение |
|----------|---------|
| Память выключена для роли | `policy.ConfigureRole("MyRole", useMemoryTool: true)` |
| Модель не вызывает tool | Добавить инструкцию в промпт |
| NullAgentMemoryStore | Проверить DI регистрацию `IAgentMemoryStore` |
| Файл не создаётся | Проверить права доступа к `persistentDataPath` |
| ChatHistory не работает | Убедитесь что `useChatHistory: true` и бэкенд = LLMUnity |

---

## 🔧 Проблема: Tool Call не работает

### Симптомы
- Модель возвращает текст вместо tool call
- `Tool call not recognized` в логах
- Tool call retry исчерпан

### Диагностика

**Тип 1: Неправильный формат от модели**
```
[Warning] Tool call not recognized, retry 1/3
```

**Причина:** Модель вернула tool call в неправильном формате.

**Решение:** CoreAI автоматически делает до 3 retry. Если не помогает:
1. Используйте модель побольше (4B+ рекомендуется)
2. Добавьте формат в промпт:
```
ALWAYS use this exact format for tool calls:
{"name": "tool_name", "arguments": {"param": "value"}}
```

**Тип 2: Tool не зарегистрирован**
```
[Error] No AIFunction found for tool name: my_custom_tool
```

**Решение:** Убедитесь что tool добавлен агенту:
```csharp
var agent = new AgentBuilder("MyAgent")
    .WithTool(new MyCustomTool())  // ← Добавить tool
    .Build();
```

**Тип 3: Бесконечный цикл tool calls**
```
[Warning] SmartToolCallingChatClient: duplicate tool_call detected, breaking loop
```

**Причина:** Модель зациклилась, вызывая один и тот же tool.

**Решение:** `SmartToolCallingChatClient` автоматически обнаруживает и прерывает цикл. Если проблема повторяется — улучшите промпт.

---

## 🌍 Проблема: World Command не исполняется

### Симптомы
- Объекты не спавнятся
- `[Warning] Spawn rejected: prefab key 'X' not found` в логах
- `coreai_world_spawn returned false`

### Диагностика

**Проблема 1: Реестр префабов не назначен**
```
[Warning] World prefab registry not assigned
```

**Решение:**
1. Create → CoreAI → World → Prefab Registry
2. Добавьте префабы с ключами
3. CoreAILifetimeScope → World Prefab Registry → назначьте asset

**Проблема 2: Ключ префаба не найден**
```
[Warning] Spawn rejected: prefab key 'Boss' not found in registry
```

**Решение:** Добавьте ключ в `CoreAiPrefabRegistryAsset`:
- Откройте asset
- Добавьте запись: Key = "Boss", Name = "Boss", Prefab = ваш префаб

**Проблема 3: Вызов не на главном потоке**
```
[Error] UnityException: ... can only be called from the main thread
```

**Решение:** Это внутренняя ошибка. Убедитесь что `AiGameCommandRouter` правильно маршалит на main thread. Команды мира **всегда должны** проходить через MessagePipe → Router.

---

## ⏳ Проблема: Тесты зависают

### EditMode тесты
```
Test hangs on "Waiting for LLM..."
```

**Причина:** EditMode тесты не должны вызывать реальный LLM.

**Решение:**
- Используйте `StubLlmClient` для EditMode
- PlayMode тесты = реальный LLM

### PlayMode тесты

**Зависание на "stopping server":**
```
CoreAISettings → LLMUnity → Keep Alive = ✅ true
```

**Зависание на "waiting for model":**
1. Увеличьте `Startup Timeout` до 120-300 сек
2. Проверьте что модель скачана и путь GGUF верный
3. Уменьшите модель (2B вместо 9B для тестов)

**HTTP тесты не подключаются:**
1. Запустите LM Studio **перед** тестами
2. Установите env vars:
```powershell
$env:COREAI_OPENAI_TEST_BASE = "http://localhost:1234/v1"
$env:COREAI_OPENAI_TEST_MODEL = "qwen3.5-4b"
```

---

## 🔌 Проблема: DI / VContainer ошибки

### Симптомы
```
VContainerException: Type 'ILlmClient' is not registered
```

### Решение

1. Убедитесь что `CoreAILifetimeScope` есть на сцене
2. Убедитесь что он является **Root** или **Parent** для других LifetimeScope
3. Проверьте что все зависимости назначены в Inspector:
   - Core AI Settings
   - Agent Prompts Manifest (опционально)
   - Game Log Settings (опционально)
   - World Prefab Registry (опционально)

```
Hierarchy:
└── CoreAILifetimeScope  ← Root LifetimeScope
    ├── LlmManager (LLM + LLMAgent)
    ├── GameManager
    └── ... ваши объекты
```

---

## 📊 Диагностика: Как включить подробные логи

### Быстрое включение всей диагностики

```
CoreAISettings → 🔧 Отладка:
  ✅ MEAI Debug Logging      — логи MEAI pipeline
  ✅ HTTP Debug Logging       — сырые HTTP запросы
  ✅ Log Orchestration Metrics — метрики оркестратора
```

### Что искать в логах

| Паттерн | Значение |
|---------|----------|
| `LLM ▶ [traceId=...]` | Запрос отправлен |
| `LLM ◀ [traceId=...] 247 tokens, 1.2s` | Ответ получен |
| `LLM ⏱ timeout` | Таймаут |
| `[MessagePipe] traceId=...` | Маршрутизация команды |
| `[MEAI] Tool call detected` | Tool call распознан |
| `[MEAI] Tool result` | Результат tool call |
| `[Lua] Execution succeeded` | Lua выполнен |
| `[Lua] Execution failed` | Lua ошибка |
| `[World] Spawn: Enemy at (10,0,5)` | Команда мира |
| `SmartToolCallingChatClient: duplicate` | Обнаружен цикл |

### Фильтрация по TraceId

Каждый запрос получает уникальный `TraceId`. Используйте его для отслеживания пути команды:

```
Фильтр в консоли Unity: "abc123"

[abc123] LLM ▶ role=Programmer hint="Create ambush script"
[abc123] LLM ◀ 312 tokens, 2.1s
[abc123] [MessagePipe] ApplyAiGameCommand type=AiEnvelope
[abc123] [Lua] Execution succeeded: "Ambush created"
```

---

## 🚑 Быстрый чеклист проблем

```
❓ Модель молчит?
  → Проверь Backend Type в CoreAISettings
  → Проверь что LM Studio / LLMAgent запущен
  → Нажми "🔗 Test Connection"

❓ Пустой ответ?
  → Увеличь LLM Timeout (120+)
  → Включи Keep Alive для LLMUnity
  → Проверь логи LLM ▶ / LLM ◀

❓ Tool call не срабатывает?
  → Включи MEAI Debug Logging
  → Проверь что tool добавлен агенту
  → Используй модель 4B+ для надёжного tool calling

❓ Lua падает?
  → Проверь whitelist API в промпте
  → Self-heal работает до 3 попыток
  → Увеличь MaxLuaRepairRetries если нужно

❓ Память не сохраняется?
  → Проверь AgentMemoryPolicy для роли
  → Проверь что модель вызывает memory tool
  → Проверь persistentDataPath

❓ Объект не спавнится?
  → Назначь CoreAiPrefabRegistryAsset
  → Добавь ключ префаба в реестр
  → Проверь логи [World]

❓ Тесты зависают?
  → Keep Alive = true
  → Startup Timeout = 120
  → Для CI: используй Stub backend
```

---

> 📖 **Связанные документы:**
> - [COREAI_SETTINGS.md](COREAI_SETTINGS.md) — все настройки
> - [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) — архитектура
> - [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) — настройка LLM
