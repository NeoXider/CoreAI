# 🤖 CoreAI — AI-агенты для динамичных игр

**Живые NPC, процедурный контент, динамические механики** — всё управляется AI прямо во время игры.

> Представьте игру, которая **подстраивается не только цифрами**, а **логикой и ситуациями**: другой набор угроз, другой темп, другой «характер» мира. CoreAI делает это реальностью.

---

## ✨ Что умеет CoreAI

### 🏗️ Создавай AI-агентов за 3 строки

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Знает ассортимент
    .WithMemory()                                  // Помнит покупателей
    .WithMode(AgentMode.ToolsAndChat)              // Инструменты + чат
    .Build();
```

**3 режима:** 🛒 ToolsAndChat · 🤖 ToolsOnly · 💬 ChatOnly

---

### 🔧 AI вызывает инструменты (Tools)

AI не просто генерирует текст — **вызывает код** для реальных действий:

| Инструмент | Что делает | Кто использует |
|------------|-----------|----------------|
| 🧠 **MemoryTool** | Память между сессиями | Все агенты |
| 📜 **LuaTool** | Выполняет Lua скрипты | Programmer AI |
| 🎒 **InventoryTool** | Инвентарь NPC | Merchant AI |
| ⚙️ **GameConfigTool** | Меняет конфиги игры | Creator AI |

**Создай свой:**
```csharp
public class WeatherLlmTool : ILlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather.";
    public AIFunction CreateAIFunction() => AIFunctionFactory.Create(
        async ct => await _provider.GetWeatherAsync(ct), "get_weather", "Get weather.");
}
```

---

### 🎮 Динамические механики — AI меняет игру на лету

```
Игрок: "Скрафти оружие из Железа и Кристалла Огня"
  ↓
CoreMechanicAI: "Железо + Кристалл Огня → Меч Пламени, урон 45"
  ↓
Programmer AI: execute_lua → create_item("Flame Sword", "weapon", 75)
               add_special_effect("fire_damage: 15")
  ↓
✨ Игрок получает уникальный предмет!
```

---

### 🧠 Память — AI помнит всё

| | Memory | ChatHistory |
|--|--------|-------------|
| **Хранение** | JSON файл (диск) | В LLMAgent (RAM) |
| **Срок** | Между сессиями | Текущий разговор |
| **Для чего** | Факты, покупки, квесты | Контекст беседы |

---

### 🔄 Tool Call Retry — AI учится на ошибках

Маленькие модели (Qwen3.5-2B) иногда забывают формат. CoreAI автоматически даёт **3 попытки** + проверяет fenced Lua блоки сразу.

---

## 🏛️ Архитектура

Репозиторий состоит из **двух пакетов**:

| Пакет | Что внутри | Зависимости |
|-------|-----------|-------------|
| **[com.nexoider.coreai](Assets/CoreAI)** | Портативное ядро — C# **без** Unity | VContainer, MoonSharp |
| **[com.nexoider.coreaiunity](Assets/CoreAiUnity)** | Unity-слой — DI, LLM, MessagePipe, тесты | Зависит от `coreai` |

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
└──────────────────────┬──────────────────────────────────────┘
                       ↓
┌─────────────────────────────────────────────────────────────┐
│                   Game World                                 │
│  • Lua Sandbox (MoonSharp)  • MessagePipe  • DI (VContainer)│
└─────────────────────────────────────────────────────────────┘
```

---

## 🚀 Быстрый старт

### 1. Установи пакеты (UPM)

```
Window → Package Manager → + → Add package from git URL…

https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 2. Открой сцену

```
Assets/CoreAiUnity/Scenes/_mainCoreAI.unity → Play
```

### 3. Создай своего агента

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller. Share tales about the world.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .Build();
```

---

## 📚 Документация

| Документ | Что внутри |
|----------|-----------|
| 📖 [CoreAI README](Assets/CoreAI/README.md) | Общее описание + AgentBuilder |
| 🏗️ [AGENT_BUILDER.md](Assets/CoreAI/Docs/AGENT_BUILDER.md) | Конструктор агентов |
| 🔧 [TOOL_CALL_SPEC.md](Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md) | Спецификация tool calling |
| 🛒 [CHAT_TOOL_CALLING.md](Assets/CoreAiUnity/Docs/CHAT_TOOL_CALLING.md) | Merchant NPC |
| 🧠 [MemorySystem.md](Assets/CoreAiUnity/Docs/MemorySystem.md) | Память агентов |
| 🗺️ [DEVELOPER_GUIDE.md](Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md) | Карта кода, архитектура |
| 🤖 [AI_AGENT_ROLES.md](Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) | Роли и промпты |
| 📋 [CHANGELOG.md](Assets/CoreAI/CHANGELOG.md) | История изменений |

---

## 🧪 Тесты

```
Unity → Window → General → Test Runner
  ├── EditMode — 191 тест (быстрые, без LLM)
  └── PlayMode — 12 тестов (с реальной LLM)
```

---

## 🌐 Мультиплеер и синглплеер

- **Синглплеер:** тот же пайплайн, AI работает локально
- **Мультиплеер:** AI-логика на хосте, клиенты получают согласованные исходы

**Один шаблон — и для одиночной кампании, и для коопа.**

---

## 🤝 Автор и сообщество

**Автор:** [Neoxider](https://github.com/NeoXider)  
**Экосистема:** [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools)  
**Лицензия:** [PolyForm Noncommercial 1.0.0](LICENSE) (коммерция — по отдельной лицензии)

**Контакт:** neoxider@gmail.com | [GitHub Issues](https://github.com/NeoXider/CoreAI/issues)

---

> 🎮 **CoreAI** — сделай свою игру умнее. Один агент за раз.
