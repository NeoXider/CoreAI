<p align="center">
  <img src="Images/header_concept_2.png" alt="CoreAI Banner" width="100%">
</p>

# <img src="Docs/Images/coreai_icon.png" alt="CoreAI Icon" width="40" height="40" align="absmiddle"> CoreAI — LLM-агенты, которые играют в вашу игру

**CoreAI — это Unity-фреймворк для NPC и агентов на LLM, которые вызывают код вашей игры:** function calling, инструменты, постоянная память и Lua в рантайме — на **локальной модели 4 ГБ** или любом **OpenAI-совместимом API**. Без облачных ключей и скриптовых деревьев диалогов.

*Читать на других языках: [English](README.md), [Русский](README_RU.md).*

[![CI](https://github.com/NeoXider/CoreAI/actions/workflows/ci.yml/badge.svg)](https://github.com/NeoXider/CoreAI/actions/workflows/ci.yml)
[![EditMode tests](https://img.shields.io/badge/EditMode-967%20passing-brightgreen)](Assets/CoreAiUnity/Tests/EditMode)
[![Unity](https://img.shields.io/badge/Unity-6000.0%2B-black)](https://unity.com/releases/editor)
[![Runs on](https://img.shields.io/badge/работает%20на-локальной%204B%20GGUF%20или%20любом%20OpenAI%20API-blue)](#-рекомендуемые-модели)
[![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial%201.0-blue)](LICENSE)

<sub>
<a href="#-установка">Установка</a>&nbsp;&nbsp;•&nbsp;
<a href="#-быстрый-старт">Быстрый старт</a>&nbsp;&nbsp;•&nbsp;
<a href="#-что-умеет-coreai">Возможности</a>&nbsp;&nbsp;•&nbsp;
<a href="#-три-входа-ui--coreai--агенты">Три входа</a>&nbsp;&nbsp;•&nbsp;
<a href="#-рекомендуемые-модели">Модели</a>&nbsp;&nbsp;•&nbsp;
<a href="#%EF%B8%8F-архитектура">Архитектура</a>&nbsp;&nbsp;•&nbsp;
<a href="#-документация">Документация</a>&nbsp;&nbsp;•&nbsp;
<a href="#-тесты">Тесты</a>
</sub>

> ### 🎬 Представьте
> Игрок подходит к кузнецу-NPC и пишет: _«Есть огненные мечи?»_. Кузнец **вызывает ваш код инвентаря**, ничего не находит, и **отвечает по роли**: _«Огненных клинков нет, но могу выковать, если принесёшь Кристалл Огня.»_ Игрок крафтит — **Programmer-агент пишет Lua**, **CoreMechanic считает статы**, и уникальный _Меч Пламени_ падает в инвентарь. Всё в рантайме, стримингом токен за токеном в чат-пузырь, на локальной модели. **Это CoreAI.**

### Зачем CoreAI?

Демок «LLM в игре» полно; сложно именно **довести до релиза**. CoreAI — это недостающий production-слой между чат-окном и геймплеем: модель не просто говорит — она **вызывает ваш C# и Lua**, а фреймворк берёт на себя суровую реальность малых локальных моделей (неверный регистр имён тулов, разорванный стриминг, зацикливание, rate limits, переполнение контекста), чтобы игра не ломалась.

- 🆓 **Работает из коробки и масштабируется в production** — одна строка `await CoreAi.AskAsync("…")` для первой фичи; полный `AgentBuilder` + оркестратор + маршрутизация по ролям, когда нужно.
- 🧩 **Не тянет тяжёлых обязательных зависимостей** — LLM и Lua это **опциональные модули** (`COREAI_NO_LLM` / `COREAI_NO_LUA`, авто-детект по установленным пакетам). Ставьте только то, что использует игра.
- 🛡️ **Заточен под малые локальные модели** — авто-чинит регистр имён тулов, ретраит с обратной связью, переживает разрывы `<think>`, ограничивает «убегающую» генерацию. Полный набор PlayMode-тестов проходит на локальной **Qwen3.5-4B** GGUF.

### Для кого это?

| Вы… | Начните с | Время до первого результата |
|-----|-----------|------------------------------|
| 🟢 **Новичок / прототип** | `CoreAI → Setup → Create Chat Demo Scene` → Play, либо `await CoreAi.AskAsync("…")` | ~5 мин |
| 🔵 **Делаете реальную игру** | `AgentBuilder` + инструменты + `IAiOrchestrationService`, маршрутизация LLM по ролям | растёт вместе с вами |

> 🚀 **Старт за 30 секунд:** установка (ниже) → `CoreAI → Setup → Create Chat Demo Scene` → **Play** → пишите. См. [Быстрый старт](#-быстрый-старт).

### ⚡ Коротко о главном

- 🧠 **Агенты вызывают ваш код** — настоящий function calling с ретраем, авто-ремонтом и памятью
- 🏠 **Локально прежде всего** — полный PlayMode-набор проходит на 4 ГБ GGUF; данные не покидают машину игрока
- 💬 **Готовый чат** — один пункт меню создаёт рабочую сцену со стриминговым чатом
- 🎯 **Self-Service Skills** — модель видит 2 мета-инструмента вместо сотен (~91% экономии токенов)
- 🌊 **Стриминг, переживающий реальность** — разорванные `<think>`, фрагментированные tool calls, SSE-чанки
- 🗜️ **Длинные чаты без взрыва контекста** — бюджет токенов, скользящие сводки, опциональная «умная» свёртка
- 🛡️ **Production-защита** — таймаут на тул, лимит генерации, защита от зацикливания, метрики rate limit — в Inspector
- 🔄 **Dual-backend fallback** — сначала локальная модель, облачный API как автоматический запасной (или наоборот)
- 🧩 **Опциональные модули** — нет MoonSharp? Нет LLMUnity? Всё равно компилируется; фичи включаются при появлении пакетов

⭐ **Если CoreAI экономит вам время — [поставьте звезду](https://github.com/NeoXider/CoreAI)!** Это главный способ, которым другие Unity-разработчики находят проект.

**Версия:** `version` в [core `package.json`](Assets/CoreAI/package.json) и [Unity `package.json`](Assets/CoreAiUnity/package.json) (одинаковый semver). **Заметки:** [Unity changelog](Assets/CoreAiUnity/CHANGELOG.md) · [Core changelog](Assets/CoreAI/CHANGELOG.md).

---

## Содержание

| | Раздел |
|---|--------|
| [Три входа](#-три-входа-ui--coreai--агенты) | UI · `CoreAi` · агенты |
| [Что умеет CoreAI](#-что-умеет-coreai) | Агенты, скиллы, чат, инструменты, память, свёртка |
| [Архитектура](#%EF%B8%8F-архитектура) | Два пакета, схема |
| [Установка](#-установка) | NuGet, `manifest`, Git URL, сцена |
| [Быстрый старт](#-быстрый-старт) | Первый агент |
| [Документация](#-документация) | Карта гайдов |
| [Тесты](#-тесты) | EditMode и PlayMode |

---

Полные заметки по версиям: [Assets/CoreAiUnity/CHANGELOG.md](Assets/CoreAiUnity/CHANGELOG.md) · [CoreAI CHANGELOG](Assets/CoreAI/CHANGELOG.md).

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
    .Build();                                      // → AgentConfig (чертеж в памяти)

// Подключает чертёж к глобальной политике (её создаёт CoreAILifetimeScope при старте).
// Оркестратор ищет инструменты и системный промпт по RoleId ("Blacksmith") в этой политике.
merchant.ApplyToPolicy(CoreAIAgent.Policy);

// Ask* идут через CoreAIAgent.Orchestrator — нужен Play и сцена с CoreAILifetimeScope.
merchant.AskWithCallback("Покажи мечи");
merchant.AskWithCallback("Покажи мечи", (response) => Debug.Log(response));
```

- **`Build()`** — даёт `AgentConfig` (id роли, тулы, промпты). Сам по себе рантайм о нём не знает.
- **`ApplyToPolicy(CoreAIAgent.Policy)`** — регистрирует роль в живой `AgentMemoryPolicy`, чтобы **`RunTask`/маршрутизация тулов** видела твой `InventoryLlmTool` и слитый промпт для `"Blacksmith"`. Без этого роль — просто строка без стека.
- **`AskWithCallback` / `AskAsync`** — обёртка над **`CoreAIAgent.Orchestrator`** (`AiTaskRequest` с `RoleId` из конфига). То же, что взять **`IAiOrchestrationService`** из DI — см. [COREAI_SINGLETON_API](Assets/CoreAiUnity/Docs/COREAI_SINGLETON_API.md).

**3 режима агентов:** 🛒 ToolsAndChat · 🤖 ToolsOnly · 💬 ChatOnly

### 🎯 Self-Service Skills — агент подгружает инструменты по требованию

Когда у агента десятки инструментов из разных доменов (крафт, бой, торговля, квесты), слать их все каждый запрос — тратить токены. **Skills** решают это:

```csharp
// Группируй инструменты в скиллы
var crafting = new SkillSet("Crafting",
    "Ковка оружия и брони из материалов",
    "1. Вызови get_recipes.\n2. Вызови craft_item.",
    new DelegateLlmTool("get_recipes", "Список рецептов", (string type) => ...),
    new DelegateLlmTool("craft_item", "Создать предмет", (string id) => ...));

// Модель видит только 2 мета-инструмента, не все тулы скиллов
var gm = new AgentBuilder("GameMaster")
    .WithSkill(crafting)
    .WithSkill(combat)
    .WithSkill(trading)
    .Build();
```

**Как работает:**
1. Модель видит лёгкий **каталог** (имя + описание скилла) в system prompt
2. Вызывает `read_skill("Crafting")` → получает инструкции + схемы инструментов
3. Вызывает `call_skill_tool("get_recipes", "{}")` → прокси маршрутизирует к реальному тулу
4. **Токен-оверхед: константный** (2 мета-тула) независимо от общего числа скиллов/тулов

> 💡 **50 инструментов в 10 скиллах?** Без скиллов: ~4,000 токенов. Со скиллами: ~360 токенов. **Экономия 91%.**

Совмещай прямые тулы и скиллы: `WithTool(memory)` (всегда виден) + `WithSkill(crafting)` (по требованию).

Документация: [AGENT_BUILDER.md §Skills](Assets/CoreAI/Docs/AGENT_BUILDER.md)

### 💬 Готовый чат без своего UI

Сцена с NPC-чатом за минуты — без ручной вёрстки:

```
CoreAI → Setup → Create Chat Demo Scene
```

Получаешь `CoreAiChatDemo.unity` с `CoreAiChatPanel` (UI Toolkit, UXML/USS, тёмная тема; **окно по умолчанию ~650×910**, **скроллбар вплотную справа**, опциональная **строка «долго ждём»** под индикатором набора), `CoreAiChatConfig_Demo` и настроенным `CoreAILifetimeScope` — **Play** и печатаешь.

```csharp
// Тот же стек, что у панели — выбери удобный API:
await foreach (var chunk in CoreAi.StreamAsync("Привет", "SmartChat"))
    Debug.Log(chunk);

// Или явно через сервис (например в тестах):
var service = CoreAiChatService.TryCreateFromScene();
await foreach (var chunk in service.SendMessageStreamingAsync("Привет", "SmartChat"))
    if (!string.IsNullOrEmpty(chunk.Text)) Debug.Log(chunk.Text);
```

**Цепочка стриминга:** SSE (HTTP) **или** callback LLMUnity → stateful `ThinkBlockStreamFilter` (срезает `<think>`, даже если тег разорван) → индикатор печати → пузырь. Отмена снимает активный HTTP-запрос / перечислитель на MEAI-пути (`HttpClient`).

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

> 💡 **Дизайн инструментов для экономии токенов:** используйте короткие ключи параметров (`q` вместо `question_text`), краткие описания, индексы вместо строк и умные дефолты. Подробнее: [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md)

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

### 🗜️ Длинные диалоги — бюджет, сводки и «умная» свёртка

Если включён **`WithChatHistory()`** и сообщений становится много, CoreAI сохраняет **хвост** свежих реплик, а более старые сворачивает в блок **`## Conversation Summary`** в system (по умолчанию детерминированно). В актуальных релизах добавлено:

| Возможность | Смысл |
|-------------|--------|
| **Бюджет контекста** | `HistoryTokenBudget` из `IContextBudgetPolicy` — честнее делит окно между system/user/tools и историей. |
| **Сводки на диске** | **`InMemoryConversationSummaryStore`** (процесс) или **`FileConversationSummaryStore`** (`persistentDataPath` в Unity) — сводки живут между ходами. |
| **LLM-свёртка** *(опционально)* | Доп. вызов **`CompleteAsync`** на роли **`__CoreAI_ContextCompaction`** переписывает rolling-summary; включается в **`CoreAISettings`**, затем можно отключить per-role. |
| **По умолчанию по роли** | У агентов из **`AgentBuilder`** умное сжатие **включено**; у встроенного **`Programmer`** — **выкл.** (обычно хватает усечённой истории для Lua/tool). **`WithLlmContextCompaction(false)`** — явный офф для кастомной роли. |

```csharp
new AgentBuilder("LoreKeeper")
    .WithChatHistory(8192, persistBetweenSessions: true)
    .WithLlmContextCompaction(true) // по умолчанию так; можно опустить
    .Build()
    .ApplyToPolicy(policy);

new AgentBuilder("ToolsFirst")
    .WithChatHistory(4096)
    .WithLlmContextCompaction(false) // только детерминированный rollup
    .Build();
```

Подробнее: [Core CHANGELOG (`v1.5.2–1.5.3`)](Assets/CoreAI/CHANGELOG.md) · [MemorySystem](Assets/CoreAiUnity/Docs/MemorySystem.md) · [ARCHITECTURE](Assets/CoreAiUnity/Docs/ARCHITECTURE.md) · [COREAI_SETTINGS](Assets/CoreAiUnity/Docs/COREAI_SETTINGS.md).

---

### 🔄 Tool Call Retry + Самовосстановление — AI учится на ошибках

Маленькие модели (Qwen3.5-2B) иногда забывают формат или регистр имён. CoreAI автоматически:

- 🔧 **Чинит регистр имён** — `TryRepairToolName` молча конвертирует `MEMORY` → `memory`, `Spawn_Quiz` → `spawn_quiz` до того, как выполнение провалится.
- ♻️ **Повтор при ошибке** — до **3 попыток** с обратной связью ошибки в историю чата, чтобы модель сама исправилась.
- 🌐 **Повтор HTTP-ошибок** — `429 (Rate Limited)` и `5xx` триггерят автоматический retry с поддержкой заголовка `Retry-After` или экспоненциальным backoff (**2s → 4s**, настраивается в Inspector).
- ✅ **Немедленная проверка Lua-блоков**.

```
Модель говорит: {"name":"MEMORY", ...}
     ↓ TryRepairToolName
Исполняется: {"name":"memory", ...}  ← молча исправлено, ошибка не показана
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

### 2. Зависимости Git в manifest.json (обязательно)

Unity Package Manager сам не подтягивает все транзитивные Git-пакеты за вас.

**Предпочтительно:** когда в проект уже добавлен CoreAiUnity, используй меню **CoreAI → Setup → Install Git Dependencies** — недостающие ключи допишутся в `manifest.json`, существующие пины не трогаются.

**Или вручную:** открой файл `Packages/manifest.json` и добавь строки в блок `"dependencies"`:

```json
    "jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.17.0",
    "com.cysharp.messagepipe": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe",
    "com.cysharp.messagepipe.vcontainer": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer",
    "com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask",
```

**Опциональные модули (с v3.0.0)** — CoreAI компилируется без них, а при появлении пакета фича включается автоматически:

```json
    "ai.undream.llm": "https://github.com/undreamai/LLMUnity.git",
    "org.moonsharp.moonsharp": "https://github.com/moonsharp-devs/moonsharp.git?path=/interpreter#upm/beta/v3.0",
```

| Опциональный пакет | Что даёт | Можно пропустить, если |
|--------------------|----------|------------------------|
| **LLMUnity** (`ai.undream.llm`) | Локальные GGUF-модели на устройстве | Используешь только OpenAI-совместимый HTTP API |
| **MoonSharp** (`org.moonsharp.moonsharp`) | Lua-песочница и скрипты, которые пишет AI | Lua-скриптинг не нужен |

### 3. Пакеты CoreAI через Git URL
**Unity Editor →** Window → Package Manager → `+` → **Add package from git URL…**

**Шаг 1 — Ядро (чистый C#, без UnityEngine):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
```

**Шаг 2 — Unity-слой (MonoBehaviour, LLM клиенты, инструменты):**
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 4. Собери сцену

**Чатовая демо-сцена (лучший первый запуск):**

```
CoreAI → Setup → Create Chat Demo Scene
```

**Облегчённая сцена (только scope и ассеты — без UI демо):**

```
CoreAI → Setup → Create Bare Scene (advanced)
```

Оба пункта:
- ✅ Создадут или подготовят `CoreAILifetimeScope` на сцене  
- ✅ Сгенерируют нужные ассеты (`CoreAISettings`, `GameLogSettings`, `AgentPromptsManifest` и др.)  
- ✅ Пропишут ссылки на scope  
- ✅ При бэкенде LLMUnity могут добавить `LLM` + `LLMAgent` (wizard «голой» сцены)

### 5. Настрой LLM бэкенд

Открой настройки: **CoreAI → Settings** и выбери бэкенд:

| Бэкенд | Настройка |
|---------|----------|
| **LLMUnity** (локально) | Скачай GGUF модель (напр. Qwen3.5-4B) через LLMUnity Model Manager |
| **HTTP API** (LM Studio, OpenAI) | Укажи `API Base URL` и `API Key` в Settings |
| **Auto** | CoreAI сам выберет лучший доступный бэкенд |

### 6. Первый агент

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

> 📖 Полный гайд: [QUICK_START.md](Assets/CoreAiUnity/Docs/QUICK_START.md)  
> 🏗️ Справочник AgentBuilder: [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md)

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

**Язык:** подробные Markdown-гайды в [`Assets/CoreAiUnity/Docs/`](Assets/CoreAiUnity/Docs/) и [`Assets/CoreAI/Docs/`](Assets/CoreAI/Docs/) ведутся на **английском**; портативный пакет `CoreAI` — тоже. Разбор токенов/таймаутов MEAI: [`MEAI_TOKENS_FACT_VS_ESTIMATE.md`](Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) (EN). Старые ссылки на русский файл ведут на редирект: [`MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md`](Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md). Корневой README_RU — навигатор по-русски; за деталями — по ссылкам на англоязычные гайды.

Сначала: **[Docs/README.md](Docs/README.md)** — общий вход по документации репозитория.
По Unity-пакету: **[DOCS_INDEX.md](Assets/CoreAiUnity/Docs/DOCS_INDEX.md)** — от новичка до архитектуры.

**Сброс файловых сохранений в редакторе:** **CoreAI → Delete All Persistent Saves...** (не во время Play Mode) удаляет **`persistentDataPath/CoreAI`** — память агентов, сохранённый чат, сводки (desktop), версии Lua/оверлеев. Ассеты в `Assets/` не трогаются. Подробнее: [TROUBLESHOOTING.md](Assets/CoreAiUnity/Docs/TROUBLESHOOTING.md).

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
| 📊 [MEAI_TOKENS_FACT_VS_ESTIMATE.md](Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) | **(EN)** usage из API vs префлайт-оценки; SSE `include_usage`; таймауты HTTP/оркестратора |
| 🔒 [LUA_SANDBOX_SECURITY.md](Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md) | **(EN)** граница Lua sandbox, вырезанные API, лимиты исполнения |

### Инструменты, память, роли

| Документ | Содержание |
|----------|------------|
| 🛠️ [MEAI_TOOL_CALLING.md](Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md) | MEAI: `ILlmTool` → `AIFunction` |
| 🧰 [TOOL_CALLING_BEST_PRACTICES.md](Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) | **(EN)** схемы тулов, идемпотентность, SkillSet |
| 🔧 [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Спека tool calling |
| 🛒 [CHAT_TOOL_CALLING.md](Assets/CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Торговец с инвентарём |
| 🧠 [MemorySystem.md](Assets/CoreAiUnity/Docs/MemorySystem.md) | Память и ChatHistory |
| 🤖 [AI_AGENT_ROLES.md](Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Роли и промпты |

### Архитектура

| Документ | Содержание |
|----------|------------|
| 🗺️ [DEVELOPER_GUIDE.md](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Карта кода, PR-чеклист |
| 📐 [DGF_SPEC.md](Assets/CoreAiUnity/Docs/DGF_SPEC.md) | Нормы: DI, потоки, власть |
| 🔀 [LLM_ROUTING.md](Assets/CoreAI/Docs/LLM_ROUTING.md) | Портативный роутинг: режимы, политика, usage, таймауты |
| 📑 [CoreAI/Docs/README.md](Assets/CoreAI/Docs/README.md) | Оглавление всех гайдов в `Assets/CoreAI/Docs` |
| 📋 [CHANGELOG](Assets/CoreAI/CHANGELOG.md) · [Unity](Assets/CoreAiUnity/CHANGELOG.md) | История версий |

---

## 🧪 Тесты

```
Unity → Window → General → Test Runner
  ├── EditMode — большой быстрый набор (без реального LLM): промпты, стрим, Lua,
  │              инструменты, rate limit, фасад CoreAi, ремонт имён, backoff мат.,
  │              стрим оркестратора, паритет извлечения tool-call, …
  └── PlayMode — интеграция с настроенным HTTP или локальным GGUF
                 ├── FullPipelineResiliencePlayModeTests — стрим/не-стрим
                 │   tool calls с гарантией no-JSON-leak, memory write/read,
                 │   оркестратор merchant + инвентарь, trace-диагностика
                 ├── ToolNameRepairPlayModeTests — гибрид скрипт+реальный LLM:
                 │   ремонт регистра, самокоррекция неизвестного инструмента
                 └── StreamingToolCallingPlayModeTests — отмена, state parity
```

В CI сначала гоняй EditMode. PlayMode опционален и требует бэкенд (для HTTP — переменные окружения, см. [LLMUNITY_SETUP_AND_MODELS](Assets/CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)).

---

## 💡 Примеры и идеи интеграции

Как можно использовать CoreAI в вашей игре? Вот несколько идей:

1.  **«Живой» Торговец**: Вместо статического списка товаров, кузнец помнит, что вчера вы продали ему легендарную чешую дракона. Сегодня он может предложить вам «особую сделку» на меч для убийства драконов.
2.  **Автономный Гейм-Мастер**: Позвольте ИИ следить за здоровьем и ресурсами игрока. Если игроку тяжело, ГМ может «прошептать» подсказку или «случайно» спавнить зелье здоровья неподалёку через `WorldCommandTool`.
3.  **Рассказчик лора в реальном времени**: Когда игрок входит в новый биом, ИИ генерирует уникальную историю места, основываясь на текущей погоде, времени суток и экипировке игрока.
4.  **Процедурные квесты на базе ИИ**: Квесты больше не ограничиваются `Убей X волков`. Король просит вас `Узнать, почему волки светятся`, и вы можете реально *допросить* волков (которые могут быть под действием Lua-заклинания).
5.  **Голосовое/Чат-управление миром**: «Пусть пойдёт огненный дождь!» -> ИИ разбирает намерение, проверяет `WeatherTool` и исполняет соответствующий Lua-скрипт для запуска огненного шторма.

---

## 🏗️ Архитектура

```
┌─────────────────────────────────────────────────────────────┐
│                      Player / Game                           │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AiOrchestrator                              │
│  • Priority queue  • JSON strip (defense-in-depth)            │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│              LoggingLlmClientDecorator                        │
│  • HTTP retry (429/5xx)  • Retry-After  • Exp. backoff        │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                     LLM Client (MeaiLlmClient)               │
│  • LLMUnity (local GGUF)  • OpenAI HTTP  • Stub             │
│  • TryExtractToolCallsFromText (JSON-in-text → tool call)    │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│               SmartToolCallingChatClient                      │
│  • TryRepairToolName (MEMORY → memory)                       │
│  • Дедупликация  • Счётчик ошибок подряд                      │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   AI Agents                                  │
│  🛒 Merchant  📜 Programmer  🎨 Creator  📊 Analyzer        │
│  🗡️ CoreMechanic  💬 SmartChat  + Ваши кастомные!          │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│              SkillSet (Self-Service Skills)                   │
│  read_skill → инструкции + схемы                             │
│  call_skill_tool → прокси к реальным тулам                   │
│  Модель видит 2 мета-тула, остальные — по требованию         │
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Tools (ILlmTool)                           │
│  🧠 Memory  📜 Lua  🎒 Inventory  ⚙️ GameConfig  + Ваши!    │
└──────────────────────┬──────────────────────────────────────┘
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

### 💖 Поддержать проект

CoreAI бесплатен для некоммерческого использования и развивается в свободное время. Если он сэкономил тебе часы работы:

- ⭐ **Поставь звезду** — самый простой способ помочь проекту расти
- 💖 **[Спонсорство на GitHub](https://github.com/sponsors/NeoXider)** — поддержи разработку новых фич
- 💼 **Нужен в коммерческой игре?** Доступна отдельная коммерческая лицензия — пиши на neoxider@gmail.com
- 🛠️ **Приоритетная поддержка / интеграция под заказ** — тоже по почте

---

> 🎮 **CoreAI** — хватит писать деревья диалогов. Выпускайте агентов, которые *думают*, *вызывают ваш код* и *помнят* — на локальной 4B-модели или через облачный API, ваш выбор.
