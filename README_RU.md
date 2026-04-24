<p align="center">
  <img src="Docs/Images/coreai_banner.png" alt="CoreAI Banner" width="100%">
</p>

# <img src="Docs/Images/coreai_icon.png" alt="CoreAI Icon" width="40" height="40" align="absmiddle"> CoreAI — AI-агенты в Unity

*Читать на других языках: [English](README.md), [Русский](README_RU.md).*

**Живые NPC, процедурный контент, динамика на лету** — всё это может идти от LLM, прямо во время игры.

**Одно хранилище, два пакета:** портативное ядро C# и Unity-слой с DI, панелью чата и тестами. Хотите *демо за пять минут* — или *многошаговый пайплайн с инструментами и Lua* — используются одни и те же кирпичики.

> **Зачем открывать репозиторий?** Здесь *агенты, которые зовут ваш код*, *стриминг, переживший «разорванные» теги reason*, *чат панелью в один клик* — и по желанию **`CoreAi.AskAsync("…")` из любого скрипта** — без «домашки» по DI ради первой фичи.

> 🚀 **Проверено на малых моделях:** многие сценарии PlayMode уверенно идут на локальной **Qwen3.5-4B** (например, с выключенным think/reasoning). Необязатели облачные API, чтобы в игре *ощущалось умом*.

**Версия:** **v0.21.7** · статический API `CoreAi` · стриминг оркестратора · сворачивание чата в FAB · укреплённый чат и стриминг

[![EditMode tests](https://img.shields.io/badge/EditMode-extensive%20suite-brightgreen)](Assets/CoreAiUnity/Tests/EditMode)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black)](https://unity.com/releases/editor)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0-blue)](LICENSE)

---

## Содержание

| | Раздел |
|---|--------|
| [Что нового в 0.21](#-что-нового-в-021) | `CoreAi`, стрим оркестратора, чат |
| [Три входа](#-три-входа-ui--coreai--агенты) | UI · `CoreAi` · агенты |
| [Что умеет CoreAI](#-что-умеет-coreai) | Агенты, чат, инструменты, память |
| [Архитектура](#%EF%B8%8F-архитектура) | Два пакета, схема |
| [Установка](#-установка) | NuGet, `manifest`, Git URL, сцена |
| [Быстрый старт](#-быстрый-старт) | Первый агент |
| [Документация](#-документация) | Карта гайдов |
| [Тесты](#-тесты) | EditMode и PlayMode |

---

## 🆕 Что нового в 0.21

- 🎯 **Статический фасад `CoreAi`** — `AskAsync` / `StreamAsync` / `SmartAskAsync` / `Orchestrate*` / `TryGet*` / `Invalidate` — [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md).
- 🌊 **Стриминг в оркестраторе** — `IAiOrchestrationService.RunStreamingAsync`: тот же путь власти, очереди и валидации, что и у `RunTaskAsync`, но чанками (см. [STREAMING_ARCHITECTURE](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md) §6).
- 💬 **Чат** — многострочный ввод, Shift+Enter / Enter, анимированный индикатор печати; стрим виден через всю цепочку декораторов `ILlmClient`; **0.21.7** — сворачивание в FAB (`SetCollapsed`, на мобильном layout по умолчанию свёрнут) — [README_CHAT](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md).

**Ранее 0.20.x (всё ещё в коробке):** универсальная панель чата, стрим HTTP + LLMUnity, трёхслойные флаги, демо-сцена в один клик, широкое EditMode-покрытие (песочница Lua, инструменты, rate limit, фильтры).

Полные заметки: [Assets/CoreAiUnity/CHANGELOG.md](Assets/CoreAiUnity/CHANGELOG.md) · [CoreAI CHANGELOG](Assets/CoreAI/CHANGELOG.md).

---

## 🧭 Три входа: UI · CoreAi · агенты

| Делаешь… | С чего начать | В одно предложение |
|----------|---------------|-------------------|
| **Внутриигровой чат для игрока** | `CoreAI → Setup → Create Chat Demo Scene` + `CoreAiChatPanel` | Включил Play — пишешь в чат |
| **Любой скрипт, без DI в первый день** | `using CoreAI;` → `await CoreAi.AskAsync("…")` или `StreamAsync` | [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md) |
| **Полноценного агента, инструменты, оркестратор** | `AgentBuilder` + `IAiOrchestrationService` | [AGENT_BUILDER](Assets/CoreAI/Docs/AGENT_BUILDER.md) |

Все три пути при одной настройке сцены разделяют `CoreAILifetimeScope` и бэкенд LLM.

---

## ✨ Что умеет CoreAI

### 🏗️ Создавай своих AI-агентов за 3 строки

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Знает свой ассортимент
    .WithMemory()                                  // Помнит покупателей
    .Build();

merchant.ApplyToPolicy(CoreAIAgent.Policy);

// Вызови агента — одна строка, никакого бойлерплейта:
merchant.Ask("Покажи мечи");

// Или с callback:
merchant.Ask("Покажи мечи", (response) => Debug.Log(response));
```

**3 режима агентов:**
- 🛒 **ToolsAndChat** — вызывает инструменты И отвечает текстом (Merchant, Crafter, Advisor)
- 🤖 **ToolsOnly** — только инструменты, без текста (Background Analyzer)
- 💬 **ChatOnly** — только текст, без инструментов (Storyteller, Guide)

### 💬 Готовый чат без своего UI

Сцена с NPC-чатом за минуты — без ручной вёрстки:

```
CoreAI → Setup → Create Chat Demo Scene
```

Получаешь `CoreAiChatDemo.unity` с `CoreAiChatPanel` (UI Toolkit, UXML/USS, тёмная тема), `CoreAiChatConfig_Demo` и настроенным `CoreAILifetimeScope` — **Play** и печатаешь.

```csharp
// Тот же стек, что у панели — выбери удобный API:
await foreach (var chunk in CoreAi.StreamAsync("Привет", "PlayerChat"))
    Debug.Log(chunk);

// Или явно через сервис (например в тестах):
var service = CoreAiChatService.TryCreateFromScene();
await foreach (var chunk in service.SendMessageStreamingAsync("Привет", "PlayerChat"))
    if (!string.IsNullOrEmpty(chunk.Text)) Debug.Log(chunk.Text);
```

**Цепочка стриминга:** SSE (HTTP) **или** callback LLMUnity → stateful `ThinkBlockStreamFilter` (срезает `<think>`, даже если тег разорван) → индикатор печати → пузырь. Отмена рвёт `UnityWebRequest` на HTTP.

Доки: [README_CHAT.md](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md) · [STREAMING_ARCHITECTURE.md](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md)

> 🎯 **Одна строка из скрипта:** [COREAI_SINGLETON_API.md](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md)  
> 📖 **Быстрый путь: LLM и сцена:** [QUICK_START.md](Assets/CoreAiUnity/Docs/QUICK_START.md)  
> 🏗️ **Агенты + рецепты:** [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md)

---

### 🔧 Инструменты (Tools) — AI вызывает код

AI может вызывать инструменты для получения данных и выполнения действий:

| Инструмент | Что делает | Кто использует |
|------------|-----------|----------------|
| 🌍 **WorldCommandTool** | Спавнит, двигает, меняет объекты в мире | Creator AI |
| ⚡ **Action/Event Tool** | Вызывает любой C# метод или Event напрямую | Все агенты |
| 🧠 **MemoryTool** | Сохраняет/читает память между сессиями | Все агенты |
| 📜 **LuaTool** | Выполняет Lua скрипты | Programmer AI |
| 🎒 **InventoryTool** | Получает инвентарь NPC | Merchant AI |
| ⚙️ **GameConfigTool** | Читает/меняет конфиги игры | Creator AI |
| 🎭 **SceneLlmTool** | Читает и меняет иерархию/transform в PlayMode | Все агенты |
| 📸 **CameraLlmTool** | Делает скриншоты (Base64 JPEG) для Vision | Все агенты |
| 🧩 *(Твой Инструмент)*| Добавь сюда (либо используй ⚡ Action/Event Tool) | Ваш Агент |

**Создай свой инструмент:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather in game world.";
    public string ParametersSchema => "{}";
    
    public IEnumerable<AIFunction> CreateAIFunctions()
    {
        yield return AIFunctionFactory.Create(
            async (CancellationToken ct) => await _provider.GetWeatherAsync(ct),
            "get_weather", "Get current weather.");
    }
}
```

---

### 🎮 Динамические механики — AI меняет игру на лету

```
Игрок крафтит оружие
  ↓
CoreMechanicAI: "Железо + Кристалл Огня → Меч Пламени, урон 45"
  ↓
Programmer AI: вызывает execute_lua tool
  ↓
Lua: create_item("Flame Sword", "weapon", 75)
     add_special_effect("fire_damage: 15")
     report("crafted Flame Sword")
  ↓
Игрок получает уникальный предмет!
```

**AI может:**
- 🔄 Менять правила игры (волны, модификаторы, сложности)
- 🎨 Создавать процедурный контент (предметы, квесты, локации)
- 📊 Анализировать поведение игрока и адаптировать игру
- 🐛 Автоматически чинить Lua ошибки (до 3 попыток)

---

### 🧠 Память агентов — AI помнит всё

**Два типа памяти:**

| | Memory | ChatHistory |
|--|--------|-------------|
| **Хранение** | JSON файл на диске | В LLMAgent (RAM) |
| **Срок** | Между сессиями | Текущая сессия |
| **Для чего** | Факты, покупки, квесты | Контекст разговора |

```csharp
var agent = new AgentBuilder("Merchant")
    .WithMemory()         // Помнит что купил игрок (между сессиями)
    .WithChatHistory()    // Помнит текущий разговор
    .Build();
```

---

### 🔄 Tool Call Retry — AI учится на ошибках

Маленькие модели (Qwen3.5-2B) иногда забывают формат. CoreAI автоматически даёт **3 попытки**:

```
Attempt 1: Model returns wrong format
  ↓
System: "ERROR: Use this format: {"name": "tool", "arguments": {...}}"
  ↓
Attempt 2: Model fixes the format ✅
```

---

### 🚀 Поддерживаемые LLM бэкенды

| Бэкенд | Описание | Когда использовать |
|--------|----------|-------------------|
| **LLMUnity** | Локальная GGUF модель | Без интернета, приватность |
| **OpenAI HTTP** | LM Studio, Ollama, OpenAI-compatible | Мощные модели, быстрый старт |
| **Stub** | Заглушка для тестов | CI/CD, разработка без LLM |

**Auto-режим:** CoreAI сам выберет доступный бэкенд.

### 📏 Рекомендуемые модели

| Модель | Размер | Tool Calling | Когда использовать |
|--------|--------|--------------|-------------------|
| **Qwen3.5-4B** | 4B | ✅ Отлично | **Рекомендуемая** для локального запуска |
| **Qwen3.5-35B (MoE)** | 35B/3A | ✅ Превосходно | **Идеально** через API — быстро и точно |
| **Gemma 4 26B (через LM Studio)** | 26B | ✅ Превосходно | Отличный выбор через HTTP API |
| **LM Studio API** | Любая | ✅ Отлично | Внешние модели через HTTP — лучший выбор |
| Qwen3.5-2B | 2B | ⚠️ Работает | Работает, но иногда ошибается |
| Qwen3.5-0.8B | 0.8B | ⚠️ Базовый | Большинство тестов проходит, сложности с многошаговыми |

> 💡 **Рекомендация: Qwen3.5-4B локально или Qwen3.5-35B (MoE) через API**  
> MoE-модели (Mixture of Experts) используют только часть параметров при инференсе — быстрые как 4B, точные как 35B.

### 🧪 Результаты PlayMode тестов по размерам моделей

Все PlayMode тесты CoreAI проверены на реальных LLM бэкендах:

| Категория тестов | 0.8B | 2B | 4B+ |
|-----------------|------|-----|------|
| Memory Tool (запись/добавление/очистка) | ✅ Пройден | ✅ Пройден | ✅ Пройден |
| Custom Agents (вызов инструментов) | ✅ Пройден | ✅ Пройден | ✅ Пройден |
| World Commands (list/play/spawn) | ✅ Пройден | ✅ Пройден | ✅ Пройден |
| Execute Lua (один инструмент) | ✅ Пройден | ✅ Пройден | ✅ Пройден |
| Multi-Agent (Creator→Mechanic→Programmer) | ⚠️ Частично | ✅ Пройден | ✅ Пройден |
| Crafting Memory (многошаговый: memory + lua) | ⚠️ Частично | ⚠️ В основном | ✅ Пройден |
| Chat History (постоянный контекст) | ❌ Слишком мала | ⚠️ В основном | ✅ Пройден |
| Player Chat (диалоги NPC) | ✅ Пройден | ✅ Пройден | ✅ Пройден |

> 🏆 **Qwen3.5-4B проходит ВСЕ тесты.** Рекомендуемый минимум для продакшена.  
> 📊 **Qwen3.5-0.8B проходит большинство тестов** — впечатляюще для своего размера! Сложности только с многошаговыми цепочками tool calling.  
> 📈 **2B — золотая середина** — редкие ошибки в многошаговых сценариях, но в целом надёжна.

---

## 📦 Установка

### 1. Установи NuGet DLL (обязательно)

CoreAI использует [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI) для LLM пайплайна. Скопируй эти DLL в папку `Assets/Packages/` своего проекта (скачай с NuGet или скопируй из `Assets/Packages/` этого репозитория):

| NuGet пакет | Версия | Зачем нужен |
|-------------|--------|-------------|
| `Microsoft.Extensions.AI` | 10.4.1 | CoreAI Core |
| `Microsoft.Extensions.AI.Abstractions` | 10.4.1 | CoreAI Core |
| `Microsoft.Bcl.AsyncInterfaces` | 10.0.4 | Системная зависимость |
| `System.Text.Json` | 10.0.4 | JSON сериализация |
| `System.Text.Encodings.Web` | 10.0.4 | Системная зависимость |
| `System.Numerics.Tensors` | 10.0.4 | Системная зависимость |
| `Microsoft.Extensions.Logging.Abstractions` | 10.0.4 | Логирование |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | 10.0.4 | DI |
| `System.Diagnostics.DiagnosticSource` | 10.0.4 | Системная зависимость |

> 💡 **Проще всего:** Клонируй этот репозиторий и скопируй всю папку `Assets/Packages/` в свой проект.

### 2. Установи зависимости в manifest.json (обязательно)
Unity Package Manager не поддерживает автоматическое скачивание Git-зависимостей из других пакетов. Открой файл `Packages/manifest.json` в своем проекте и добавь эти строки в блок `"dependencies"`:

```json
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.17.0",
    "org.moonsharp.moonsharp": "https://github.com/moonsharp-devs/moonsharp.git?path=/interpreter#upm/beta/v3.0",
    "com.cysharp.messagepipe": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe",
    "com.cysharp.messagepipe.vcontainer": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer",
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
    "ai.undream.llm": "https://github.com/undreamai/LLMUnity.git",
```

*(После сохранения файла Unity сама скачает все нужные библиотеки: VContainer, MoonSharp, UniTask, MessagePipe и LLMUnity)*

### 3. Установи CoreAI пакеты через Git URL
**Unity Editor →** Window → Package Manager → `+` → **Add package from git URL…**

**Шаг 1 — Ядро (чистый C#, без UnityEngine):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
```

**Шаг 2 — Unity-слой (MonoBehaviour, LLM клиенты, инструменты):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 3. Настрой сцену (один клик)

После установки используй меню:

```
CoreAI → Create Scene Setup
```

Это автоматически:
- ✅ Создаст `CoreAILifetimeScope` на сцене
- ✅ Сгенерирует все необходимые ассеты настроек (`CoreAISettings`, `GameLogSettings`, `AgentPromptsManifest` и др.)
- ✅ Назначит ассеты на scope
- ✅ Создаст `LLM` + `LLMAgent` объекты (если бэкенд = LLMUnity)

### 4. Настрой LLM бэкенд

Открой настройки: **CoreAI → Settings** и выбери бэкенд:

| Бэкенд | Настройка |
|---------|----------|
| **LLMUnity** (локально) | Скачай GGUF модель (напр. Qwen3.5-4B) через LLMUnity Model Manager |
| **HTTP API** (LM Studio, OpenAI) | Укажи `API Base URL` и `API Key` в Settings |
| **Auto** | CoreAI сам выберет лучший доступный бэкенд |

**Готово!** AI-агенты работают. 🎉

---

## 🎯 Быстрый старт

### 1. Создай агента
```csharp
var blacksmith = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember what players bought.")
    .WithTool(new InventoryLlmTool(GameServices.Inventory))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

blacksmith.ApplyToPolicy(policy);
```

### 2. Вызови агента
```csharp
await orchestrator.RunTaskAsync(new AiTaskRequest
{
    RoleId = "Blacksmith",
    Hint = "What weapons do you have for sale?"
});
```

### 3. Результат
```
Blacksmith: "Добро пожаловать, путник! Вот моё лучшее оружие:
  • Железный меч — 50 золотых
  • Стальной топор — 100 золотых
  • Клинок Пламени — 250 золотых (зачарован!)
Что приглянулось?"
```

---

## 📚 Документация

Сначала: **[DOCS_INDEX.md](Assets/CoreAiUnity/Docs/DOCS_INDEX.md)** — от новичка до архитектуры.

### Старт

| Документ | Содержание |
|----------|------------|
| 🚀 [QUICK_START.md](Assets/CoreAiUnity/Docs/QUICK_START.md) | Установка → сцена → LLM → Play |
| 🚀 [QUICK_START_FULL.md](Assets/CoreAiUnity/Docs/QUICK_START_FULL.md) | 10-минутный путь: LM Studio → Unity → первая команда |
| 🎯 [COREAI_SINGLETON_API.md](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md) | **`CoreAi`** в одну строку |
| 🏗️ [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md) | Агент за три шага, режимы, рецепты |
| ⚙️ [COREAI_SETTINGS.md](Assets/CoreAiUnity/Docs/COREAI_SETTINGS.md) | Бэкенды, таймауты, стриминг |

### Чат и стриминг

| Документ | Содержание |
|----------|------------|
| 💬 [README_CHAT.md](Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md) | `CoreAiChatPanel` + демо |
| 🌊 [STREAMING_ARCHITECTURE.md](Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md) | SSE / LLMUnity → фильтры → UI · стрим в оркестраторе |

### Инструменты, память, роли

| Документ | Содержание |
|----------|------------|
| 🛠️ [MEAI_TOOL_CALLING.md](Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md) | MEAI: `ILlmTool` → `AIFunction` |
| 🔧 [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Спека tool calling |
| 🛒 [CHAT_TOOL_CALLING.md](Assets/CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Торговец с инвентарём |
| 🧠 [MemorySystem.md](Assets/CoreAiUnity/Docs/MemorySystem.md) | Память и ChatHistory |
| 🤖 [AI_AGENT_ROLES.md](Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Роли и промпты |

### Архитектура

| Документ | Содержание |
|----------|------------|
| 🗺️ [DEVELOPER_GUIDE.md](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Карта кода, PR-чеклист |
| 📐 [DGF_SPEC.md](Assets/CoreAiUnity/Docs/DGF_SPEC.md) | Нормы: DI, потоки, власть |
| 📋 [CHANGELOG](Assets/CoreAI/CHANGELOG.md) · [Unity](Assets/CoreAiUnity/CHANGELOG.md) | История версий |

---

## 🧪 Тесты

```
Unity → Window → General → Test Runner
  ├── EditMode — большой быстрый набор (без реального LLM): промпты, стрим, Lua, инструменты, rate limit, фасад CoreAi, стрим оркестратора, …
  └── PlayMode — интеграция с настроенным HTTP или локальным GGUF
```

В CI сначала гоняй EditMode. PlayMode опционален и требует бэкенд (для HTTP — переменные окружения, см. [LLMUNITY_SETUP_AND_MODELS](Assets/CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)).

---

## 🏗️ Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│                      Player / Game                           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AiOrchestrator                              │
│  • Priority queue  • Retry logic  • Tool calling              │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                     LLM Client                               │
│  • LLMUnity (local GGUF)  • OpenAI HTTP  • Stub             │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AI Agents                                  │
│  🛒 Merchant  📜 Programmer  🎨 Creator  📊 Analyzer        │
│  🗡️ CoreMechanic  💬 PlayerChat  + Ваши кастомные!          │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Tools (ILlmTool)                           │
│  🧠 Memory  📜 Lua  🎒 Inventory  ⚙️ GameConfig  + Ваши!    │
└─────────────────────────────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Game World                                 │
│  • Lua Sandbox (MoonSharp)  • MessagePipe  • DI (VContainer)│
└─────────────────────────────────────────────────────────────┘
```

---

## 🤝 Автор и сообщество

**Автор:** [Neoxider](https://github.com/NeoXider)  
**Экосистема:** [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools)  
**Лицензия:** [LICENSE](LICENSE)

**Вопросы, идеи, баги?** — создавай Issue! 🐛💡

---

> 🎮 **CoreAI** — *играбельный* AI: сцена в один клик, один статический вызов или полноценный агент — как скажешь.
