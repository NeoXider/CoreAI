# 🤖 CoreAI — AI-агенты в Unity

*Читать на других языках: [English](README.md), [Русский](README_RU.md).*

**Живые NPC, динамические механики, процедурный контент** — всё управляется AI через LLM.

> 🌟 **СОЗДАВАЙТЕ ИГРЫ БУДУЩЕГО:** Представьте мир, который адаптируется не просто сухими цифрами, а логикой и живыми реакциями: уникальные диалоги, новые ситуации и процедурные квесты.
>
> 🚀 **ДОКАЗАНА РАБОТА НА МАЛЕНЬКИХ МОДЕЛЯХ:** Все сложные PlayMode тесты CoreAI (с вызовами функций и памятью) успешно проходятся на локальной компактной модели **Qwen3.5-4B** (даже с выключенным режимом Think/Reasoning). Это мощнейшее доказательство жизнеспособности архитектуры! Вам не нужны дорогие облачные API — благодаря строгим системным правилам и оркестратору, вы можете создавать удивительные динамические игры с невероятно умными NPC-агентами, которые будут работать локально на железе игроков.

| Версия | Unity | Статус |
|--------|-------|--------|
| См. `package.json` | `6000.0+` | ✅ v0.12.0 — [CHANGELOG](CHANGELOG.md) |

---

## ✨ Что умеет CoreAI

### 🏗️ Создавай своих AI-агентов за 3 строки

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember customer purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Знает свой ассортимент
    .WithMemory()                                  // Помнит что купил игрок
    .WithChatHistory()                             // Помнит контекст разговора (8192 токена)
    .WithMode(AgentMode.ToolsAndChat)              // Вызывает инструменты + отвечает
    .Build();
```

**3 режима агентов:**
- 🛒 **ToolsAndChat** — вызывает инструменты И отвечает текстом (Merchant, Crafter, Advisor)
- 🤖 **ToolsOnly** — только инструменты, без текста (Background Analyzer)
- 💬 **ChatOnly** — только текст, без инструментов (Storyteller, Guide)

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

**Создай свой инструмент:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather in game world.";
    public string ParametersSchema => "{}";
    
    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
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
| **LM Studio API** | Любая | ✅ Отлично | Внешние модели через HTTP — лучший выбор |
| Qwen3.5-2B | 2B | ⚠️ Работает | Минимальная, но может ошибаться |

> 💡 **Рекомендация: Qwen3.5-4B локально или Qwen3.5-35B (MoE) через API**  
> MoE-модели (Mixture of Experts) используют только часть параметров при инференсе — быстрые как 4B, точные как 35B.

---

## 📦 Установка

### 1. Добавь ядро (CoreAI)
**Путь в Unity:** Window → Package Manager → `+` → Add package from git URL…

Ссылка для копирования:
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
```

### 2. Добавь Unity-слой (CoreAIUnity)
Точно так же через `Add package from git URL…` добавь вторую ссылку:
```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 3. Открой и запусти сцену
Тестовая сцена со всеми агентами находится здесь:
```text
Assets/CoreAiUnity/Scenes/_mainCoreAI.unity
```

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
Blacksmith: "Welcome, traveler! I have these fine weapons:
  • Iron Sword — 50 gold
  • Steel Axe — 100 gold
  • Flame Blade — 250 gold (enchanted!)
What catches your eye?"
```

---

## 📚 Документация

| Документ | Что внутри |
|----------|-----------|
| 🏗️ [AGENT_BUILDER.md](Docs/AGENT_BUILDER.md) | Конструктор агентов, режимы, ChatHistory |
| 🛠️ [MEAI_TOOL_CALLING.md](Docs/MEAI_TOOL_CALLING.md) | Архитектура MEAI pipeline |
| 🔧 [TOOL_CALL_SPEC.md](../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Спецификация tool calling |
| 🛒 [CHAT_TOOL_CALLING.md](../CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Merchant NPC с инвентарём |
| 🧠 [MemorySystem.md](../CoreAiUnity/Docs/MemorySystem.md) | Память и ChatHistory |
| ⚙️ [COREAI_SETTINGS.md](../CoreAiUnity/Docs/COREAI_SETTINGS.md) | CoreAISettingsAsset + tool calling |
| 🗺️ [DEVELOPER_GUIDE.md](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Карта кода, архитектура |
| 🤖 [AI_AGENT_ROLES.md](../CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Роли агентов и промпты |
| 📋 [CHANGELOG.md](CHANGELOG.md) | Все изменения по версиям |

---

## 🧪 Тесты

```
Unity → Window → General → Test Runner
  ├── EditMode (215+ тестов) — быстрые, без LLM
  └── PlayMode (12 тестов) — с реальной LLM
```

**PlayMode тесты:**
- ✅ `CustomAgentsPlayModeTests` — 3 кастомных агента (Merchant, Analyzer, Storyteller)
- ✅ `AllToolCallsPlayModeTests` — Memory + Execute Lua
- ✅ `CraftingMemoryViaLlmUnityPlayModeTests` — полный workflow крафта
- ✅ `MerchantWithToolCallingPlayModeTests` — Merchant NPC с инвентарём

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

> 🚀 **CoreAI** — сделай свою игру умнее. Один агент за раз.
