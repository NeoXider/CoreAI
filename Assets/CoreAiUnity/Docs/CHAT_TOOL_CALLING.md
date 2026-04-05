# 🛒 Merchant NPC с Tool Calling

## Концепция

**Merchant** — это NPC-торговец с инструментами:
- `get_inventory` — получить список товаров для продажи
- `memory` — запомнить что покупал игрок

### Воркфлоу

```
Игрок: "Что у тебя есть?"
  ↓
Merchant AI вызывает get_inventory tool
  ↓
Tool: [Iron Sword(50), Health Potion(25), Leather Armor(100)]
  ↓
Merchant: "Добро пожаловать! У меня есть Iron Sword за 50 монет, Health Potion за 25..."
```

## Как это работает

### 1. Merchant Agent

Системный промпт Merchant:
```
You are a shopkeeper/merchant NPC. You have an inventory of items to sell.
When the player asks to buy, browse, or see what you have, 
FIRST call the get_inventory tool to check your stock.
Then respond in-character as a merchant, listing items with prices.
Remember what the player bought using the memory tool.
```

### 2. InventoryTool

```csharp
public class MyInventoryProvider : InventoryTool.IInventoryProvider
{
    public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken ct)
    {
        return Task.FromResult(new List<InventoryTool.InventoryItem>
        {
            new() { Name = "Iron Sword", Type = "weapon", Quantity = 3, Price = 50 },
            new() { Name = "Health Potion", Type = "consumable", Quantity = 10, Price = 25 }
        });
    }
}
```

### 3. Настройка Merchant

```csharp
var policy = new AgentMemoryPolicy();
policy.SetToolsForRole(BuiltInAgentRoleIds.Merchant, new List<ILlmTool>
{
    new MemoryLlmTool(),
    new InventoryLlmTool(new MyInventoryProvider())
});
policy.EnableMemoryTool(BuiltInAgentRoleIds.Merchant);
```

## Архитектура

```
Player: "Хочу купить оружие"
  ↓
Merchant AI (System: "You are a shopkeeper...")
  ↓
AiOrchestrator → tools=[memory, get_inventory]
  ↓
MEAI FunctionInvokingChatClient
  ↓
Model: {"name": "get_inventory", "arguments": {}}
  ↓
InventoryTool.GetInventoryAsync()
  ↓
Returns: [{name: "Iron Sword", price: 50, qty: 3}]
  ↓
Model: "У меня есть Iron Sword за 50 монет..."
```

## Тестирование

```bash
COREAI_PLAYMODE_LLM_BACKEND=llmunity
Unity Test Runner → PlayMode → MerchantWithToolCallingPlayModeTests
```

## PlayerChat vs Merchant

| Агент | Инструменты | Назначение |
|-------|-------------|------------|
| **PlayerChat** | Нет | Чат-помощник, отвечает на вопросы |
| **Merchant** | get_inventory, memory | Торговец с инвентарём и памятью |

PlayerChat — без инструментов, просто диалог.  
Merchant — NPC с инструментами для осмысленных ответов.
