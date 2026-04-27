# 🛒 Merchant NPC with tool calling

## Concept

**Merchant** is an NPC shopkeeper with tools:
- `get_inventory` — fetch the list of goods for sale
- `memory` — remember what the player bought

### Workflow

```
Player: "What do you have?"
  ↓
Merchant AI calls get_inventory tool
  ↓
Tool: [Iron Sword(50), Health Potion(25), Leather Armor(100)]
  ↓
Merchant: "Welcome! I have an Iron Sword for 50 coins, Health Potion for 25..."
```

## How it works

### 1. Merchant agent

Merchant system prompt:

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

### 3. Configuring Merchant

```csharp
var policy = new AgentMemoryPolicy();
policy.SetToolsForRole(BuiltInAgentRoleIds.Merchant, new List<ILlmTool>
{
    new MemoryLlmTool(),
    new InventoryLlmTool(new MyInventoryProvider())
});
policy.EnableMemoryTool(BuiltInAgentRoleIds.Merchant);
```

## Architecture

```
Player: "I want to buy a weapon"
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
Model: "I have an Iron Sword for 50 coins..."
```

## Testing

```bash
COREAI_PLAYMODE_LLM_BACKEND=llmunity
Unity Test Runner → PlayMode → MerchantWithToolCallingPlayModeTests
```

## PlayerChat vs Merchant

| Agent | Tools | Purpose |
|-------|-------------|------------|
| **PlayerChat** | None | Chat helper; answers questions |
| **Merchant** | get_inventory, memory | Shopkeeper with inventory and memory |

**PlayerChat** has no tools — dialogue only.  
**Merchant** is an NPC with tools for grounded replies.
