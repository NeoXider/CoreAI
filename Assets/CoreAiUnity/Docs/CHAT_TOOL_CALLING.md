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

`Merchant` is a built-in role (`BuiltInAgentRoleIds.Merchant`). Its built-in system prompt (abridged):

```
You are a shopkeeper/merchant NPC. You have an inventory of items to sell to the player.
When the player asks to buy, browse, or see what you have, FIRST call the get_inventory tool to check your stock.
Then respond in-character as a merchant, listing items with prices from the tool result.
...
Remember what the player bought from you using the memory tool.
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

`get_inventory` is host-wired: give the built-in role its tool once CoreAI is running (for example from a
scene component). The memory tool is already on for `Merchant` (`AgentMemoryPolicy.EnableMemoryTool` /
`DisableMemoryTool` toggle it).

```csharp
CoreAIAgent.Policy.SetToolsForRole(BuiltInAgentRoleIds.Merchant, new List<ILlmTool>
{
    new InventoryLlmTool(new MyInventoryProvider())
});
```

`SetToolsForRole` replaces the role's custom tool list, so calling it again does not duplicate the tool.
A custom agent can carry the same tool instead:
`new AgentBuilder("Merchant").WithTool(new InventoryLlmTool(provider)).WithMemory().Build()`.

## Architecture

```
Player: "I want to buy a weapon"
  ↓
Merchant AI (System: "You are a shopkeeper...")
  ↓
AiOrchestrator → tools=[memory, get_inventory]
  ↓
SmartToolCallingChatClient (MEAI FunctionInvokingChatClient + ToolExecutionPolicy)
  ↓
Model: {"name": "get_inventory", "arguments": {}}
  ↓
InventoryTool.ExecuteAsync() → MyInventoryProvider.GetInventoryAsync()
  ↓
Returns: {"Success":true,"Items":[{"Name":"Iron Sword","Price":50,"Quantity":3,…}]}
  ↓
Model: "I have an Iron Sword for 50 coins..."
```

## Testing

`MerchantWithToolCallingPlayModeTests` picks its backend through `PlayModeProductionLikeLlmFactory`: the
**LLM Backend** field of `CoreAISettingsAsset` (`BackendType`, not **LLM Mode**) decides — `LlmUnity` for a
local GGUF model, `OpenAiHttp` for an OpenAI-compatible endpoint, `Auto` to try both.
`COREAI_PLAYMODE_LLM_BACKEND` (`auto`, `http`, `llmunity`, `offline`) is read only when no settings asset is
loaded.

```bash
Unity Test Runner → PlayMode → MerchantWithToolCallingPlayModeTests
```

## PlainChat vs SmartChat vs Merchant

| Agent | Tools | Purpose |
|-------|-------------|------------|
| **PlainChat** | None by default | Simple chat helper; dialogue only |
| **SmartChat** | memory (built-in) | Chat + durable facts via MemoryTool |
| **Merchant** | memory; `get_inventory` once the host adds it | Shopkeeper with inventory and memory |

**PlainChat** has no MemoryTool by default — dialogue only. **SmartChat** exposes the memory tool for long-term recall.  
**Merchant** is an NPC with tools for grounded replies.

## Stream/non-stream parity

Streaming and non-streaming paths handle the same tool-call shapes:

1. **Native** — the provider fills `tool_calls` (OpenAI-compatible servers, LM Studio, LLMUnity's server).
   They arrive as `MEAI.FunctionCallContent`. On an endpoint with a native tool channel these are the only
   calls executed; assistant prose is never parsed for calls.
2. **Text-shaped** — only on an endpoint without a native tool channel (or when the host sets
   `AllowTextShapedToolCallsOnNativeEndpoint` on the `AiTaskRequest`). `LlmToolCallTextExtractor` reads calls
   to **declared** tools from the assistant text (`{"name":"...","arguments":{...}}`, Hermes/Qwen XML, a
   reply that is just `tool_name(arg=...)`), runs them through the same `ToolExecutionPolicy`, and strips them
   from the visible reply. See [JSON_COMMAND_FORMAT.md §11](JSON_COMMAND_FORMAT.md#text-shaped-calls).
3. **Requested but unbound** — on the text-shaped path, a declared tool that could not be bound (e.g.
   `MemoryLlmTool` while `IAgentMemoryStore` is `null`). The pipeline strips the JSON, logs a warning, and
   emits the cleaned text. Nothing is executed; the trace records `source=missing`.

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

The `LLM <` summary line also gets a tail like `tools=[memory(ok,12ms),get_inventory(ok,4ms)]` listing every tool that ran in the turn.

### Defense-in-depth

On the text-shaped path, `AiOrchestrator` runs `LlmToolCallTextExtractor.StripForDisplay` on the assistant
text before persisting it to chat history or publishing `ApplyAiGameCommand`. If a tool-call shape leaks past
streaming/non-streaming extraction, this removes it and logs
`[AiOrchestrator] role='…' trace='…' tool-call JSON leaked; stripped.` On a native tool channel the text is left
intact, so an agent can show JSON to a reader.
