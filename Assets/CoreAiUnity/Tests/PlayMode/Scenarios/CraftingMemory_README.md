# 🤖 AI Agents - Architecture and Workflow

## Agent Roles

| Agent | Role | Access | Memory | Example use |
|-------|------|--------|--------|-------------|
| **Creator** | Director / designer | World changes, configs, managing other agents | `Creator` | "Create an enemy wave", "Design a crafting system" |
| **Analyzer** | Telemetry analyst | Data reading, recommendations | `Analyzer` | "The player is bored, increase difficulty" |
| **Programmer** | Lua code generation | Lua sandbox, add/report | `Programmer` | "Write a damage formula in Lua" |
| **CoreMechanicAI** | Game mechanics | Numeric outcomes, crafting, loot, compatibility | `CoreMechanicAI` | "Craft a weapon from iron and crystal" |
| **AINpc** | NPC dialogue | World lines | `AINpc` | "Greetings, traveler!" |
| **SmartChat** | Player assistant (memory + chat) | Chat with the player | `SmartChat` | "How do I craft a sword?" |

---

## Architecture: One Model, Different Roles

All agents use **the same LLM model** (Qwen 35B through LM Studio), but with:

1. **Different system prompts**: each agent receives its own role.
2. **Different isolated memory**: `CoreMechanicAI` cannot see `Creator` memory.
3. **Different tools**: Programmer receives the Lua sandbox; CoreMechanicAI receives numeric output.

```
                    ┌─────────────────────────┐
                    │   LM Studio (Qwen 35B)  │
                    │   http://192.168.56.1   │
                    └───────────┬─────────────┘
                                │
                    ┌───────────▼─────────────┐
                    │     ILlmClient          │
                    └───────────┬─────────────┘
                                │
              ┌─────────────────┼─────────────────┐
              │                 │                 │
    ┌─────────▼──────┐ ┌───────▼──────┐ ┌───────▼──────┐
    │ Creator        │ │ CoreMechanic │ │ Programmer   │
    │ Memory: Creator│ │Memory: CM    │ │Memory: Prog  │
    │ Prompt: Design │ │Prompt: Craft │ │Prompt: Lua   │
    └────────────────┘ └──────────────┘ └──────────────┘
```

---

## Example: Full Multi-Agent Crafting Workflow

```
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 1: Creator decides WHAT to craft and HOW                      │
│  Agent: Creator                                                     │
│  Memory: Creator (isolated)                                         │
│                                                                     │
│  Request: "Design a weapon recipe from Iron + Fire Crystal"         │
│  Response: JSON with parameters (item_type, damage, fire_damage)    │
│  Memory: "Design: Iron+Fire Crystal -> weapon, damage ~45, fire ~15" │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 2: CoreMechanicAI computes the exact crafting result          │
│  Agent: CoreMechanicAI                                              │
│  Memory: CoreMechanicAI (isolated)                                  │
│                                                                     │
│  Request: "Calculate craft from: Iron (hardness:60) + Fire Crystal" │
│  Memory (reads): "" (first craft)                                   │
│  Response: {"item_name": "Iron Fireblade", "damage": 45, "fire": 15} │
│  Memory (writes): "Craft#1: Iron Fireblade damage:45 fire:15"      │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 3: Programmer generates Lua code for implementation           │
│  Agent: Programmer                                                  │
│  Memory: Programmer (isolated)                                      │
│                                                                     │
│  Request: "Create Lua code for Iron Fireblade (damage:45, fire:15)" │
│  Response: create_item('Iron Fireblade', 'weapon', 65)              │
│         add_special_effect('fire_damage: 15')                       │
│  Memory: (may skip writing unless requested)                        │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 4: CoreMechanicAI repeats crafting (determinism check)        │
│  Agent: CoreMechanicAI                                              │
│  Memory: CoreMechanicAI (reads Craft#1)                             │
│                                                                     │
│  Request: "Craft again from: Iron + Fire Crystal (same parameters)" │
│  Memory (reads): "Craft#1: Iron Fireblade damage:45 fire:15"       │
│  Response: {"item_name": "Iron Fireblade", "damage": 45, "fire": 15} │
│  ✅ Same result. Determinism works.                                  │
└─────────────────────────────────────────────────────────────────────┘
```

**Result:**
- ✓ Creator designed the craft
- ✓ CoreMechanicAI calculated the numbers
- ✓ Programmer generated Lua code
- ✓ Each agent's memory is isolated
- ✓ Repeat crafting is deterministic

---

## MemoryTool - Microsoft.Extensions.AI

Each agent has its own `MemoryTool` with isolated memory:

```csharp
// CoreMechanicAI memory: crafting history
var mechanicTool = new MemoryTool(store, "CoreMechanicAI");
await mechanicTool.ExecuteAsync("write", "Craft#1: Iron Fireblade damage:45");

// Creator memory: design decisions
var creatorTool = new MemoryTool(store, "Creator");
await creatorTool.ExecuteAsync("write", "Design: Iron+Fire Crystal -> weapon");

// They do NOT see each other's memory.
store.TryLoad("Creator", out var creatorState);       // -> "Design: ..."
store.TryLoad("CoreMechanicAI", out var mechanicState); // -> "Craft#1: ..."
```

**Three actions:**
- `write`: overwrite memory
- `append`: add to existing memory
- `clear`: clear memory

Implementation: `Microsoft.Extensions.AI.AIFunctionFactory.Create()`

---

## Tests

### PlayMode Tests (Full Workflow with a Real Model)

| File | Agents | Backend | Description |
|------|--------|--------|-------------|
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator -> CoreMechanicAI -> Programmer** | OpenAI HTTP | **Full 3-agent workflow** |
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator -> CoreMechanicAI** | OpenAI HTTP | **Fast 2-agent test** |
| `CraftingMemoryViaLlmUnityPlayModeTests.cs` | CoreMechanicAI | LLMUnity | 4 crafts + determinism |
| `CraftingMemoryViaOpenAiPlayModeTests.cs` | CoreMechanicAI | OpenAI HTTP | 4 crafts + determinism + 2 crafts |

### EditMode Tests (with Mock LLM)

| File | Description |
|------|-------------|
| `AiCraftingMechanicIntegrationEditModeTests.cs` | Crafting with a mock LLM |
| `MemoryToolMeaiEditModeTests.cs` | MemoryTool tests (write/append/clear) |

---

## Key Components

```
AiOrchestrator
    ├── ILlmClient (LLMUnity or OpenAI HTTP)
    ├── IAgentMemoryStore (memory storage)
    ├── AgentMemoryPolicy (update policy)
    ├── AiPromptComposer (prompt + memory assembly)
    └── IAiGameCommandSink (command intake)

MemoryTool (Microsoft.Extensions.AI)
    ├── CreateAIFunction() -> AIFunction for model function calling
    ├── ExecuteAsync("write", content)
    ├── ExecuteAsync("append", content)
    └── ExecuteAsync("clear")
```

## Notes

> 💡 **All agents use one model**: Qwen 35B through LM Studio.
> Separation happens at the level of:
> - **System prompt** (agent role)
> - **Memory** (isolated by `roleId`)
> - **Tools** (available functions)
>
> This lets the system scale: adding a new agent = adding a new roleId + prompt.
