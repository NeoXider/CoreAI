# 🎮 CoreAI Unity — AI-агенты в твоей игре

**Unity-слой CoreAI:** DI, LLM, MessagePipe, Lua sandbox, тесты, Editor-меню.

| Версия | Зависит от | Статус |
|--------|-----------|--------|
| См. `package.json` | `com.nexoider.coreai` v0.7.0 | ✅ Готово |

---

## 🚀 Что внутри

### Конструктор агентов (AgentBuilder)

Создай NPC за 3 строки:

```csharp
var merchant = new AgentBuilder("Blacksmith")
    .WithSystemPrompt("You are a blacksmith. Sell weapons and remember purchases.")
    .WithTool(new InventoryLlmTool(myInventory))  // Знает ассортимент
    .WithMemory()                                  // Помнит покупателей
    .WithMode(AgentMode.ToolsAndChat)              // Инструменты + чат
    .Build();
```

### Инструменты (Tools)

| Инструмент | Что делает | Пример |
|------------|-----------|--------|
| 🧠 **MemoryTool** | Память между сессиями | "Игрок купил меч" |
| 📜 **LuaTool** | Выполняет Lua код | `create_item("Sword")` |
| 🎒 **InventoryTool** | Инвентарь NPC | Список товаров |
| ⚙️ **GameConfigTool** | Конфиги игры | Баланс, настройки |

### Tool Call Retry

AI получает **3 попытки** исправить формат tool call:
```
AI: {"memory": "..."}  ← Неправильный формат
System: "ERROR: Use {"name": "memory", "arguments": {...}}"
AI: {"name": "memory", "arguments": {...}}  ← Исправлено ✅
```

---

## 📖 Документация

| Документ | Что внутри |
|----------|-----------|
| 🏗️ [AGENT_BUILDER.md](Docs/AGENT_BUILDER.md) | Конструктор агентов |
| 🔧 [TOOL_CALL_SPEC.md](Docs/TOOL_CALL_SPEC.md) | Tool calling спецификация |
| 🛒 [CHAT_TOOL_CALLING.md](Docs/CHAT_TOOL_CALLING.md) | Merchant NPC |
| 🧠 [MemorySystem.md](Docs/MemorySystem.md) | Память агентов |
| 🗺️ [DEVELOPER_GUIDE.md](Docs/DEVELOPER_GUIDE.md) | Карта кода |
| 🤖 [AI_AGENT_ROLES.md](Docs/AI_AGENT_ROLES.md) | Роли и промпты |
| 📋 [CHANGELOG.md](CHANGELOG.md) | История изменений |

---

## 🧪 Тесты

```
Unity → Window → General → Test Runner
  ├── EditMode — 191 тест
  └── PlayMode — 12 тестов (с реальной LLM)
```

---

## 📦 Установка

```
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

Сначала добавь `com.nexoider.coreai`, затем `com.nexoider.coreaiunity`.

---

## 🤝 Автор

[Neoxider](https://github.com/NeoXider) • [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools)

> 🎮 **CoreAI Unity** — AI-агенты, которые делают игру живой.
