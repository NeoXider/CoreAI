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

## Stream/non-stream parity (since 1.3.0)

Both paths handle three tool-call shapes identically:

1. **Native** — provider populates `delta.tool_calls` (OpenAI, Anthropic, etc.). Extracted as `MEAI.FunctionCallContent`.
2. **Text JSON** — model emits `{"name":"...","arguments":{...}}` inside an assistant text turn (Ollama, llama.cpp, LM Studio, some Qwen builds). The pipeline scans assistant text for balanced `{...}` objects with both `name` and `arguments` keys, executes them through the same `ToolExecutionPolicy`, and strips the JSON from the visible reply.
3. **Requested but unbound** — request lists a tool (e.g., `MemoryLlmTool`) that the backend could not bind (e.g., `IAgentMemoryStore` is `null`). The pipeline strips the JSON, logs a warning, and emits cleaned text. Nothing is executed; the trace records `source=missing`.

### Diagnostics

Every tool call gets a dedicated log line:

```
[ToolCall] traceId=abc123 role=Merchant tool=memory status=OK dur=12ms args={"action":"append","content":"..."} result={"Success":true,...}
```

Toggles in `CoreAISettingsAsset`:

| Flag | Adds |
|------|------|
| `LogToolCalls` | the line itself (status + duration) |
| `LogToolCallArguments` | the `args=` portion |
| `LogToolCallResults` | a 240-char preview of the result |

The `LLM ◀` summary line also gets a tail like `tools=[memory(ok,12ms),get_inventory(ok,4ms)]` listing every tool that ran in the turn.

### Defense-in-depth

`AiOrchestrator` runs `LlmToolCallTextExtractor.StripForDisplay` on the assistant text before persisting to chat history or publishing `ApplyAiGameCommand`. If a brand-new tool-call shape ever leaks past streaming/non-streaming extraction, this catches it and logs `tool-call JSON leaked through extraction; stripped for chat/envelope`.
