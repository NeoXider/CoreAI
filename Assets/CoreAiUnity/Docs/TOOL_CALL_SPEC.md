# Tool Calling Specification v0.14.0

## Unified MEAI tool call format

All tool calls in CoreAI use a **single JSON format** via Microsoft.Extensions.AI (MEAI):

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

## Tool design principles (best practices)

When defining tools for an LLM, it is **critical** to design them so the model can easily see when and how to call them, while keeping token use low. This matters most for small models (2B–4B).

### 1. Clear names and descriptions

The tool name is the first thing the model sees. It should be **self-explanatory**:

| Poor | Good | Why |
|------|------|-----|
| `tool_1` | `get_inventory` | Purpose is obvious at a glance |
| `do_action` | `spawn_quiz` | Concrete action, no ambiguity |
| `process_data` | `craft_item` | Clear what happens on invocation |

The **`Description`** is the second anchor for the model. Keep it short and purposeful:

```csharp
// Poor — too long; burns tokens on every request
public string Description => "This tool allows the AI agent to retrieve the current inventory " +
    "of the NPC merchant character which includes all items currently available for sale " +
    "with their prices, quantities, and item types for the purpose of answering player questions";

// Good — compact yet sufficient for the model
public string Description => "Get NPC inventory: items, prices, quantities.";
```

### 2. Saving tokens in parameters

Every tool parameter is sent to the model in the JSON schema **on each request**. Short keys save tokens while staying understandable:

| Long keys | Short keys | Savings |
|-----------|------------|---------|
| `"question_text"` | `"q"` | ~10 tokens × N questions |
| `"answer_options"` | `"opts"` | ~12 tokens × N questions |
| `"correct_answer_indexes"` | `"correct"` | ~15 tokens × N questions |
| `"number_of_attempts"` | `"attempts"` | ~10 tokens per request |

**Rule:** use short keys (`q`, `opts`, `correct`) in `ParametersSchema` and offset them with a crisp `description` inside the schema. The model reads the description on first exposure; keys appear on every call.

Example:

```json
{
  "type": "object",
  "properties": {
    "q": {"type": "string", "description": "Question text."},
    "opts": {"type": "array", "items": {"type": "string"}, "description": "Answer options."},
    "correct": {"type": "array", "items": {"type": "integer"}, "description": "Indexes of correct options (0-based)."}
  },
  "required": ["q", "opts", "correct"]
}
```

### 3. Prefer indices over strings

Where possible, use **numeric indices** instead of repeating strings:

```json
// Poor — correct holds strings: duplication, more tokens, fragile on typos
{"correct": ["Dictionary with key-value pairs"]}

// Good — correct holds an index: compact, reliable, language-agnostic
{"correct": [1]}
```

### 4. Optional parameters with sensible defaults

Do not force the model to fill fields that are rarely needed. Apply defaults in code:

```csharp
// Code default: 2 attempts; model may omit attempts:
int attempts = root.Value<int?>("attempts") ?? QuizSpec.DefaultAttempts;
```

### 5. Single entry point (single-string payload)

For complex tools with nested structure (arrays of objects), accepting **one string parameter `payload`** with JSON is often simpler than dozens of separate parameters. That simplifies the JSON schema for the model and reduces error rates:

```csharp
// One parameter — model emits a JSON string; parse in code
public Task<string> ExecuteAsync(
    [Description("JSON: { questions:[{q, opts[], correct[]}], attempts?, title? }")] string payload, ...)
```

### Summary of principles

| Principle | Benefit for the model | Benefit for tokens |
|-----------|------------------------|-------------------|
| Clear name (`spawn_quiz`) | Confident invocation | — |
| One-line description | Fast to scan | Less system prompt |
| Short keys (`q`, `opts`) | Faster generation | **~30–50% fewer tokens** per call |
| Indices instead of strings | No copy errors | **~50% smaller payload** |
| Optional with defaults | Fewer required fields | Fewer output tokens |
| Single-string payload | Simpler schema | Fewer schema tokens |

---

## Generation temperature

**Global temperature:** `CoreAISettings.Temperature` (default **0.1**). Applies to all agents.

**Per-agent override:**

```csharp
var agent = new AgentBuilder("Creator")
    .WithSystemPrompt("...")
    .WithTemperature(0.0f)  // Strict JSON
    .Build();
```

| Value | When to use |
|-------|-------------|
| `0.0` | Strict JSON, code, math |
| `0.1` | **Default** — tool calling |
| `0.3` | NPC dialogue |
| `0.7+` | Creative tasks |

## Architecture: engine-agnostic pattern

CoreAI uses a **two-layer** architecture for tools:

| Layer | Package | Contents |
|-------|---------|----------|
| **Abstract** | `CoreAI` | Interfaces, base classes, contracts |
| **Implementation** | `CoreAiUnity` | Unity-specific concrete types |

This pattern enables:

- **Engine-agnostic core** — CoreAI works with any engine
- **Easier porting** — new engines implement the same interfaces
- **One API** — the LLM invokes tools the same way on every platform

Since **v1.5.0**, `ToolExecutionPolicy`, `SmartToolCallingChatClient`, `LoggingLlmClientDecorator`, and `ClientLimitedLlmClientDecorator` live in `CoreAI.Core` (portable). Tool lifecycle events flow through `IToolCallEventPublisher` → `MessagePipeToolCallEventPublisher` (Unity adapter). Tool subscriber notifications flow through `IToolExecutionNotifier` → `CoreAiToolExecutionNotifier` (Unity adapter).

## Available tools

### 1. Actions and events (`DelegateLlmTool`)

**Purpose:** Turn any C# delegate (method) into an `ILlmTool` at runtime without writing dedicated tool classes. The model gets a correct JSON skeleton from the delegate signature. This is also the basis for global event triggers (`WithEventTool`).

**Usage:**

- `AgentBuilder.WithAction` (invoke a specific delegate)
- `AgentBuilder.WithEventTool` (publish to `CoreAiEvents` for decoupling)

**Prompting rule:** If you add an Action or EventTool to an agent, **strongly recommend** documenting its use in `WithSystemPrompt`. Example: `"If you want to alarm guards, call the 'alarm_guards' tool"`.

### 2. Memory tool

**Purpose:** Save, append, and clear agent memory.

**Format:**

```json
{"name": "memory", "arguments": {"action": "write|append|clear", "content": "text"}}
```

**Code:**

- `MemoryTool.cs` — MEAI `AIFunction`
- `MemoryLlmTool.cs` — `ILlmTool` wrapper

### 3. Execute Lua tool

**Purpose:** Run Lua scripts from the Programmer agent.

**Format:**

```json
{"name": "execute_lua", "arguments": {"code": "Lua code"}}
```

**Code:**

- `LuaTool.cs` — MEAI `AIFunction`
- `LuaLlmTool.cs` — `ILlmTool` wrapper

### 4. World command tool

**Purpose:** Control the game world — spawn, move, destroy objects, animations, audio, scenes.

**Format:**

```json
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "targetName": "enemy_1", "x": 0, "y": 0, "z": 0}}
```

| Action | Description | Required parameters |
|--------|-------------|---------------------|
| `spawn` | Spawn a registered prefab or built-in primitive (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `quad`, `empty`) | `prefabKey`, `targetName`, `x`, `y`, `z` |
| `move` | Move an object | `targetName`, `x`, `y`, `z` |
| `rotate` | Rotate an object in degrees | `targetName`, `fx`, `fy`, `fz` |
| `set_scale` | Set uniform object scale | `targetName`, `scale` |
| `parent` | Parent an object under another object | `targetName`, `stringValue` (parent object name) |
| `destroy` | Remove an object | `targetName` |
| `list_objects` | List objects | — (optional: `stringValue` for search) |
| `load_scene` | Load a scene | `stringValue` (scene name) |
| `reload_scene` | Reload the scene | — |
| `set_active` | Enable / disable | `targetName` |
| `play_animation` | Play an animation | `targetName`, `animationName` |
| `list_animations` | List animations | `targetName` |
| `show_text` | Show text | `targetName`, `textToDisplay` |
| `apply_force` | Apply a force | `targetName`, `fx`, `fy`, `fz` |
| `spawn_particles` | Spawn particles | `targetName`, `stringValue` |

**Code:**

- `WorldTool.cs` — MEAI `AIFunction`
- `WorldLlmTool.cs` — `ILlmTool` wrapper

**Examples:**

```json
// Spawn enemy at position
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "targetName": "enemy_1", "x": 10, "y": 0, "z": 5}}

// Move player to checkpoint (by targetName)
{"name": "world_command", "arguments": {"action": "move", "targetName": "Player", "x": 100, "y": 0, "z": 50}}

// Spawn built-in primitive without prefab registry
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "cube", "targetName": "test_cube", "x": 0, "y": 1, "z": 0}}

// Destroy object by name
{"name": "world_command", "arguments": {"action": "destroy", "targetName": "OldBuilding"}}

// List all objects in scene
{"name": "world_command", "arguments": {"action": "list_objects"}}

// Search objects by name pattern
{"name": "world_command", "arguments": {"action": "list_objects", "stringValue": "enemy"}}

// Show text notification
{"name": "world_command", "arguments": {"action": "show_text", "targetName": "Player", "textToDisplay": "Quest completed!"}}

// Play animation on enemy
{"name": "world_command", "arguments": {"action": "play_animation", "targetName": "Enemy1", "animationName": "attack"}}

// List available animations
{"name": "world_command", "arguments": {"action": "list_animations", "targetName": "Enemy1"}}

// Load next level
{"name": "world_command", "arguments": {"action": "load_scene", "stringValue": "Level_2"}}
```

**When to use:** Creator / Designer AI that dynamically drives the world.

### 5. Component command tool

**Purpose:** Add, remove, configure, and list Unity components on existing GameObjects through a curated catalog. This tool does not use reflection.

**Format:**

```json
{"name": "component_command", "arguments": {"action": "set", "targetName": "Lamp", "componentType": "light", "propertyName": "color", "stringValue": "#88aa33"}}
```

| Action | Description | Required parameters |
|--------|-------------|---------------------|
| `add` | Add a supported component if missing | `targetName`, `componentType` |
| `remove` | Remove a supported component | `targetName`, `componentType` |
| `set` | Set a supported property, auto-adding the component if missing | `targetName`, `componentType`, `propertyName`, matching value field |
| `list_components` | List component type names on the target | `targetName` |

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|:--------:|-------------|
| `action` | string | Yes | `add` / `remove` / `set` / `list_components` |
| `targetName` | string | Yes | Existing `GameObject` name |
| `componentType` | string | For `add`, `remove`, `set` | Supported component catalog key |
| `propertyName` | string | For `set` | Supported property key for the selected component |
| `stringValue` | string | Optional | Text, HTML colour, or enum name |
| `floatValue` | number | Optional | Numeric value |
| `boolValue` | number | Optional | Boolean encoded as `0` or `1` |
| `x`, `y`, `z` | number | Optional | Vector components |

Supported `componentType` values: `rigidbody`, `rigidbody2d`, `boxcollider`, `spherecollider`, `capsulecollider`, `meshcollider`, `light`, `audiosource`, `camera`, `linerenderer`, `trailrenderer`, `textmesh`, `meshrenderer`, `particlesystem`.

Example properties include `rigidbody.mass`, `rigidbody.useGravity`, `rigidbody.isKinematic`, `rigidbody.drag`, `rigidbody.angularDrag`; `light.type`, `light.intensity`, `light.range`, `light.color`, `light.spotAngle`; `boxcollider.isTrigger`, `boxcollider.size`, `boxcollider.center`; and `audiosource.volume`, `audiosource.pitch`, `audiosource.loop`.

**Examples:**

```json
// Add Rigidbody
{"name": "component_command", "arguments": {"action": "add", "targetName": "Cube", "componentType": "rigidbody"}}

// Set Rigidbody mass
{"name": "component_command", "arguments": {"action": "set", "targetName": "Cube", "componentType": "rigidbody", "propertyName": "mass", "floatValue": 5}}

// Set BoxCollider trigger flag
{"name": "component_command", "arguments": {"action": "set", "targetName": "Trigger", "componentType": "boxcollider", "propertyName": "isTrigger", "boolValue": 1}}

// Set BoxCollider size vector
{"name": "component_command", "arguments": {"action": "set", "targetName": "Trigger", "componentType": "boxcollider", "propertyName": "size", "x": 2, "y": 3, "z": 2}}

// List components
{"name": "component_command", "arguments": {"action": "list_components", "targetName": "Cube"}}
```

### 6. Get inventory tool (merchant NPC)

**Purpose:** Read an NPC merchant’s inventory for grounded replies to the player.

**Format:**

```json
{"name": "get_inventory", "arguments": {}}
```

**Returns:** A list of items with name, type, quantity, and price.

**Code:**

- `InventoryTool.cs` — MEAI `AIFunction`
- `InventoryLlmTool.cs` — `ILlmTool` wrapper

**Example:**

```
Player: "What do you have?"
  ↓
Merchant: {"name": "get_inventory", "arguments": {}}
  ↓
Tool: [{name: "Iron Sword", price: 50, qty: 3}]
  ↓
Merchant: "I have an Iron Sword for 50 coins..."
```

**When to use:** Merchant / shopkeeper NPCs that sell items.

### 7. Game config tool

**Purpose:** Read and update game configuration.

**Format:**

```json
{"name": "game_config", "arguments": {"action": "read|update", "content": "JSON"}}
```

**Code:**

- `GameConfigTool.cs` — MEAI `AIFunction`
- `GameConfigLlmTool.cs` — `ILlmTool` wrapper

### 8. Action / event tool (`DelegateLlmTool`)

**Purpose:** Invoke C# methods or events (`Action` / `Func`) directly without implementing `ILlmTool` classes. Ideal for wiring game mechanics.

**Code:**

- `DelegateLlmTool.cs`
- Uses `AIFunctionFactory` (MEAI), which parses method arguments and exposes them to the LLM as a tool.

**Format:** Depends on your method signature.

**Via `AgentBuilder`:**

```csharp
var agent = new AgentBuilder("Helper")
    .WithAction("heal_player", "Heals the player fully", () => player.Heal())
    .WithEventTool("trigger_scare", "Use to scare the player") // Publishes to CoreAiEvents
    .Build();
```

**How does the model know when to use a trigger?**

The function becomes available to the LLM like any other tool. To steer usage:

1. **Write a clear `description`** (second argument to `WithAction`) explaining why the function exists (e.g. *"Call this to heal the player"*).
2. **State it in the system prompt:** In `WithSystemPrompt`, say explicitly: *"If the player asks for help, you MUST call heal_player"*.

## Settings

### `CoreAISettings`

```csharp
// Before initialization:
CoreAISettings.MaxLuaRepairRetries = 3;        // Max consecutive failed Lua repairs
CoreAISettings.MaxToolCallRetries = 3;         // Max consecutive failed tool calls
CoreAISettings.EnableMeaiDebugLogging = true;  // MEAI debug logging
CoreAISettings.LlmRequestTimeoutSeconds = 300; // LLM timeout
```

### Tool call retry

On a failed tool call (model returned an invalid format):

1. The system returns an error to the model: `"ERROR: Tool call not recognized. Use this format..."`.
2. The model gets another attempt.
3. Retries continue until `MaxToolCallRetries` consecutive failures (default 3); the counter resets on success.
4. If all attempts are exhausted, the response is accepted as-is.

This helps small models (e.g. Qwen3.5-2B) learn the correct format.

**Logging:**

```
MeaiLlmUnityClient: Calling GetResponseAsync (attempt 1/4)
MeaiLlmUnityClient: Tool call not recognized, retry 1/3
MeaiLlmUnityClient: Calling GetResponseAsync (attempt 2/4)
MeaiLlmUnityClient: Tool call parsed from JSON text
```

### Tool-call result logging

Every executed tool call is logged with its **outcome and details** when the relevant debug flags are on
(`ToolExecutionPolicy`):

| Setting | Effect |
| --- | --- |
| `LogToolCalls` | Emits one structured line per call: `[ToolCall] traceId=… role=… tool=<name> status=OK\|FAIL dur=<ms>ms`. |
| `LogToolCallArguments` | Appends `args=<json>` (the call arguments). |
| `LogToolCallResults` | Appends `result=<preview>` — the actual tool result or failure reason (truncated to 240 chars). |
| `LogMeaiToolCallingSteps` | Adds `[ToolPolicy] <name>: SUCCESS\|FAILED` plus per-iteration loop steps. |

A tool that throws is always logged as an error: `[ToolPolicy] <name> threw: <message>`. Example failure line:

```
[ToolCall] traceId=abc role=Programmer tool=manage_mods status=FAIL dur=4ms result={"success":false,"message":"attempt to index a function value"}
```

### How tool success/failure is surfaced

- **To the model (always):** the exact tool result — including the failure reason — is returned in the
  `tool` message, so the model can self-correct and retry. Unknown tool, malformed/missing arguments,
  timeout, and thrown exceptions each return a descriptive error the model sees verbatim.
- **To the user (tool-only turns):** when the model emits tool calls and no text,
  `AiOrchestrator.ResolveToolOnlyCompletionContent` produces a status message —
  `Tool call completed: <name>.` on success, or `Tool call failed: <name>: <reason>.` on failure — instead
  of a misleading generic `LLM request failed.`
- **In the chat UI:** these `Tool call …` lines are tool-lifecycle notifications. They are shown when
  `ShowToolCallsInChat` is enabled and hidden (both success and failure) when it is off — so a clean-chat
  configuration stays clean while the model still receives the full error.

## Custom agents via `AgentBuilder`

Creating a new agent with custom tools — a few lines:

```csharp
var merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a shopkeeper...")
    .WithTool(new InventoryLlmTool(myProvider))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();

merchant.ApplyToPolicy(policy);
```

### Agent modes

| Mode | Description | Example |
|------|-------------|---------|
| `ToolsOnly` | Tools only (no chat text) | Background telemetry analysis |
| `ToolsAndChat` | Tools + text (default) | Merchant, crafter, advisor |
| `ChatOnly` | Text only (no tools) | Player chat, storyteller |

### Custom tools

**Three steps to add your own tool:**

**1. Define a class:**

```csharp
public class WeatherLlmTool : IAIFunctionLlmTool
{
    public string Name => "get_weather";
    public string Description => "Get current weather in game world.";
    public string ParametersSchema => "{}";
    public bool AllowDuplicates => false;

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            async (CancellationToken ct) => await _provider.GetWeatherAsync(ct),
            "get_weather", "Get current weather.");
    }
}
```

Use `IAIFunctionLlmTool` for one MEAI function and `IAIFunctionsLlmTool` when one logical tool exposes several functions. CoreAI no longer discovers `CreateAIFunction()` by reflection; custom tool classes must implement one of these explicit contracts or use `DelegateLlmTool`.

> **Authoring tools: parameter descriptions (read this).** On the native tool-calling path the JSON Schema is
> generated from the C# **delegate signature**, so a parameter's description reaches the model **only** if the
> delegate parameter carries `[System.ComponentModel.Description("...")]`. The `ParametersSchema` string is
> injected into the prompt **only** on the text-shaped path (`AiToolContractPromptFormatter` early-returns for
> native tool-calling) — on the native path it is invisible. Always add `[Description]` to every meaningful
> parameter. Full guide, template, and checklist: **[TOOL_AUTHORING_GUIDE.md](./TOOL_AUTHORING_GUIDE.md)**.

**2. Attach to an agent:**

```csharp
var agent = new AgentBuilder("Farmer")
    .WithSystemPrompt("You are a farmer. Check weather before answering.")
    .WithTool(new WeatherLlmTool(weatherProvider))
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

**3. The model calls the tool when needed:**

```json
{"name": "get_weather", "arguments": {}}
```

**More detail:** [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — full guide with parameter examples.

## Architecture

```
AiOrchestrator → MeaiLlmUnityClient → FunctionInvokingChatClient
                                         ↓
                              LlmUnityMeaiChatClient.TryParseToolCallFromText()
                                         ↓
                    ┌────────────────────┼────────────────────┐
                    ↓                    ↓                    ↓
            MemoryTool           LuaTool           InventoryTool
```

## Recommended models

| Model | Size | Tool calling | When to use |
|-------|------|--------------|-------------|
| **Qwen3.5-4B** | 4B | Strong | **Recommended** for local runs |
| **Qwen3.5-35B (MoE) API** | 35B / 3A active | Excellent | **Ideal** via API — fast and accurate |
| **Gemma 4 26B** | 26B | Excellent | Great via LM Studio / HTTP API |
| Qwen3.5-2B | 2B | Works | Usable; occasional errors on multi-step flows |
| Qwen3.5-0.8B | 0.8B | Basic | Most tests pass; struggles on multi-step |

**Qwen3.5-4B passes all PlayMode tests.** Treat it as the recommended minimum for production.

MoE models activate only ~3B parameters per inference step — fast like a 4B model, accuracy closer to 35B.

## Testing

### EditMode tests

- `MeaiToolCallsEditModeTests.cs` — `MemoryTool`, `LuaTool`, JSON parsing

### PlayMode tests

- `AllToolCallsPlayModeTests.cs` — memory tool + execute Lua
- `ChatWithToolCallingPlayModeTests.cs` — chat agent + inventory tool
- `CraftingMemoryViaLlmUnityPlayModeTests.cs` — full crafting workflow

## System prompts

### Universal system prompt prefix (v0.11.0+)

CoreAI supports a **universal prefix** — text prepended to the **start** of every agent’s system prompt. That lets you set shared rules for all models without duplicating them per agent.

**System prompt structure:**

```
[Universal Prefix] + [Agent-Specific Prompt]
```

**Example:**

```yaml
# Universal prefix (shared):
"You are an AI agent in a game. Always stay in character."

# Agent-specific (Programmer):
"You are the Programmer agent for CoreAI MoonSharp sandbox..."

# Resulting prompt (automatic):
"You are an AI agent in a game. Always stay in character. You are the Programmer agent..."
```

**How to configure:**

- **Inspector:** CoreAISettings → General settings → Universal System Prompt Prefix
- **Code:** `CoreAISettings.UniversalSystemPromptPrefix = "..."`

The prefix applies to **all** agents: built-in (Creator, Programmer, Analyzer, …) and custom (`AgentBuilder`).

---

## Breaking changes v0.7.0

- `AgentMemoryDirectiveParser` removed
- Fenced code blocks tagged `memory` or `lua` are no longer used for tool calls
- `{"tool": "memory", ...}` → `{"name": "memory", "arguments": {...}}`
- Programmer uses the `execute_lua` tool instead of fenced blocks
- Chat agent may call `get_inventory` before replying to the player
