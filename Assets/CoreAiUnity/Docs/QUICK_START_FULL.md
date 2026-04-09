# 🚀 Quick Start: Запуск LM Studio → Запуск сцены → Отправка команды

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Пошаговое руководство от нуля до работающего AI-агента за **10 минут**.

---

## Содержание

1. [Установка LM Studio](#1-установка-lm-studio-2-минуты)
2. [Загрузка модели](#2-загрузка-модели-3-минуты)
3. [Запуск локального сервера](#3-запуск-локального-сервера-1-минута)
4. [Настройка Unity проекта](#4-настройка-unity-проекта-2-минуты)
5. [Запуск сцены и отправка команды](#5-запуск-сцены-и-отправка-команды-2-минуты)
6. [Проверка результата](#6-проверка-результата)
7. [Что дальше?](#7-что-дальше)

---

## 1. Установка LM Studio (2 минуты)

### Скачайте LM Studio

1. Перейдите на [https://lmstudio.ai](https://lmstudio.ai)
2. Скачайте установщик для вашей OS (Windows / macOS / Linux)
3. Установите и запустите

```
💡 LM Studio — бесплатный инструмент для запуска LLM моделей локально.
   Он предоставляет OpenAI-совместимый HTTP API.
```

### Системные требования

| | Минимум | Рекомендуется |
|--|---------|--------------|
| **RAM** | 8 GB | 16 GB+ |
| **GPU (VRAM)** | 4 GB | 8 GB+ |
| **Диск** | 5 GB (для модели 4B) | 20 GB+ |

---

## 2. Загрузка модели (3 минуты)

### Рекомендуемые модели

| Модель | Размер | Tool Calling | Для кого |
|--------|--------|:------------:|----------|
| ⭐ **Qwen3.5-4B** (Q4_K_M) | ~2.5 GB | ✅ Отлично | **Лучший старт** |
| **Qwen3.5-2B** (Q4_K_M) | ~1.5 GB | ⚠️ Базовый | Слабое железо |
| **Gemma 4 26B** | ~15 GB | ✅ Превосходно | Мощное железо |
| **Qwen3.5-35B MoE** | ~20 GB | ✅ Превосходно | Продакшен |

### Пошагово

1. В LM Studio нажмите **🔍 Search** (или иконку поиска)
2. Введите `Qwen3.5-4B`
3. Выберите **GGUF** версию с квантизацией **Q4_K_M**
4. Нажмите **Download** и дождитесь загрузки

```
📦 Размер загрузки: ~2.5 GB для Qwen3.5-4B Q4_K_M
   Время загрузки: 2-5 минут (зависит от интернета)
```

---

## 3. Запуск локального сервера (1 минута)

### Загрузите модель

1. Перейдите на вкладку **💬 Chat** (или **Local Server**)
2. В верхнем выпадающем списке выберите скачанную модель (Qwen3.5-4B)
3. Дождитесь загрузки (строка статуса покажет "Model loaded")

### Запустите сервер

1. Перейдите на вкладку **🖥️ Local Server** (иконка `<->`)
2. Нажмите **Start Server**
3. Убедитесь что статус: **Server running on port 1234**

```
✅ Сервер запущен!
   URL: http://localhost:1234/v1
   Модель: Qwen3.5-4B-Q4_K_M
   Статус: Ready
```

### Проверка (опционально)

Откройте PowerShell и выполните:

```powershell
# Проверка работы сервера
Invoke-RestMethod -Uri "http://localhost:1234/v1/models"

# Тестовый запрос
$body = @{
    model = "qwen3.5-4b"
    messages = @(@{ role = "user"; content = "Say hello" })
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "http://localhost:1234/v1/chat/completions" `
    -Method POST -Body $body -ContentType "application/json"
```

---

## 4. Настройка Unity проекта (2 минуты)

### 4.1 Откройте проект

1. **Unity Hub** → **Add** → выберите папку `CoreAI`
2. Откройте проект (Unity **6000.0+**)

### 4.2 Откройте сцену

```
Меню: CoreAI → Development → Open _mainCoreAI scene
```

Или в Project Window:
```
Assets/CoreAiUnity/Scenes/_mainCoreAI.unity
```

### 4.3 Настройте CoreAISettings

1. В Project Window найдите: `Assets/CoreAiUnity/Resources/CoreAISettings.asset`
2. Или создайте: **Create → CoreAI → CoreAI Settings**
3. В Inspector настройте:

```
┌─────────────────────────────────────────────┐
│  CoreAI Settings                             │
│                                              │
│  🎯 LLM Backend:    [OpenAiHttp]      ▼     │
│                                              │
│  🌐 HTTP API:                                │
│     Base URL:    http://localhost:1234/v1     │
│     API Key:     (пусто)                     │
│     Model:       qwen3.5-4b                  │
│     Temperature: 0.2                         │
│     Max Tokens:  4096                        │
│     Timeout:     120                         │
│                                              │
│  ⚙️ Общие:                                   │
│     LLM Timeout: 30                          │
│     Max Concurrent: 2                        │
│                                              │
│  [🔗 Test Connection]                        │
│                                              │
└─────────────────────────────────────────────┘
```

### 4.4 Проверьте подключение

Нажмите кнопку **🔗 Test Connection** в Inspector.

Ожидаемый результат:
```
✅ HTTP API: Connected
   Model: qwen3.5-4b
   Response: "OK"
   Latency: 0.3s
```

---

## 5. Запуск сцены и отправка команды (2 минуты)

### 5.1 Нажмите ▶ Play

В Unity нажмите кнопку **Play** (▶).

В консоли Unity вы увидите:
```
[CoreAI] VContainer + MessagePipe... готовы.
[CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1
[CoreAI] Registered tools: memory, execute_lua, world_command, get_inventory, ...
```

### 5.2 Отправьте команду из кода

**Вариант A: Из своего скрипта**

```csharp
using CoreAI;
using VContainer;

public class MyGameController : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    async void Start()
    {
        // Вызвать агента Programmer для генерации Lua
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Programmer",
            Hint = "Write a Lua script that reports 'Hello from AI!'"
        });
        
        Debug.Log("✅ AI задача выполнена!");
    }
}
```

**Вариант B: Через хоткей (уже на сцене)**

1. Находясь в Play Mode, нажмите **F9**
2. Это вызовет агента **Programmer** через компонент `CoreAiLuaHotkey`
3. Результат появится в логах:

```
[LLM ▶] traceId=abc123 role=Programmer
[LLM ◀] traceId=abc123 312 tokens, 1.8s
[Lua] Execution succeeded: "Hello from AI!"
```

**Вариант C: Создайте кастомного агента**

```csharp
// Создайте торговца — 3 строки!
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a friendly weapon merchant. Greet customers warmly.")
    .WithTool(new InventoryLlmTool(myInventory))
    .WithMemory()
    .Build();

merchant.ApplyToPolicy(CoreAIAgent.Policy);

// Отправьте сообщение:
merchant.Ask("Покажи мечи", onDone: response => {
    Debug.Log($"Торговец: {response}");
});
```

---

## 6. Проверка результата

### Что вы должны увидеть

```
┌─ Unity Console ──────────────────────────────────────────────┐
│                                                               │
│ [CoreAI] Backend: OpenAiHttp → http://localhost:1234/v1       │
│ [LLM ▶] traceId=abc123 role=Programmer                       │
│ [LLM ◀] traceId=abc123 247 tokens, 1.2s                      │
│ [MEAI] Tool call detected: name=execute_lua                   │
│ [Lua] Executing: report("Hello from AI!")                     │
│ [Lua] Execution succeeded                                     │
│ ✅ AI задача выполнена!                                        │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

### Что делать если не работает?

| Проблема | Быстрое решение |
|----------|----------------|
| `Backend: Stub` | Проверьте что LM Studio запущен |
| `Connection refused` | Проверьте порт 1234 в LM Studio |
| `Empty response` | Увеличьте Timeout до 120 сек |
| `Tool call not recognized` | Модель слишком маленькая, используйте 4B+ |

> 📖 Подробнее: [TROUBLESHOOTING.md](TROUBLESHOOTING.md)

---

## 7. Что дальше?

### 📚 Пошаговые руководства

| Задача | Документ |
|--------|----------|
| Создать своего агента | [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) |
| Управлять миром из Lua | [WORLD_COMMANDS.md](WORLD_COMMANDS.md) |
| Настроить память | [MemorySystem.md](MemorySystem.md) |
| Добавить свой инструмент | [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) |
| Понять архитектуру | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) |
| Роли и промпты | [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) |
| Посмотреть примеры | [EXAMPLES.md](EXAMPLES.md) |

### 🎯 Попробуйте

1. **Создайте NPC-торговца** с инвентарём → [CHAT_TOOL_CALLING.md](CHAT_TOOL_CALLING.md)
2. **Скрафтите оружие** через CoreMechanicAI → [EXAMPLES.md](EXAMPLES.md)
3. **Спавните врагов** через World Commands → [WORLD_COMMANDS.md](WORLD_COMMANDS.md)
4. **Запустите тесты** → Window → Test Runner → EditMode → Run All

---

## 📋 Чеклист быстрого старта

```
✅ LM Studio установлен
✅ Модель скачана (Qwen3.5-4B Q4_K_M)
✅ Сервер запущен на порту 1234
✅ Unity проект открыт
✅ Сцена _mainCoreAI загружена
✅ CoreAISettings → Backend = OpenAiHttp
✅ CoreAISettings → Base URL = http://localhost:1234/v1
✅ Test Connection = ✅ Connected
✅ Play → F9 → "Hello from AI!" в логах

🎉 Готово! Переходите к созданию своих агентов!
```

---

## 🔀 Альтернативные варианты

### Вариант B: Без LM Studio (LLMUnity — встроенная модель)

Если вы хотите запустить модель **прямо внутри Unity** (без внешних серверов):

1. CoreAISettings → Backend = **LlmUnity** (или **Auto**)
2. На сцене найдите объект **LlmManager** с компонентами `LLM` + `LLMAgent`
3. В Inspector `LLM` → скачайте модель (кнопка Download в LLMUnity)
4. Нажмите Play

> ⚠️ LLMUnity запускает модель в процессе Unity — это медленнее, но не требует внешних инструментов.

### Вариант C: Облачный API (OpenAI, Qwen API)

```
CoreAISettings → Backend = OpenAiHttp
   Base URL: https://api.openai.com/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: gpt-4o-mini
```

или

```
CoreAISettings → Backend = OpenAiHttp
   Base URL: https://dashscope.aliyuncs.com/compatible-mode/v1
   API Key: sk-xxxxxxxxxxxxx
   Model: qwen-max
```

---

> 🚀 **CoreAI** — сделай свою игру умнее. Один агент за раз.
