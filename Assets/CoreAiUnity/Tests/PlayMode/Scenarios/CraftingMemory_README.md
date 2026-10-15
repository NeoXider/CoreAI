# 🤖 AI Agents - Architecture and Workflow

## Agent Roles in These Scenarios

| Agent | Role | Memory | Example use |
|-------|------|--------|-------------|
| **Creator** | Director / designer: designs the craft | `Creator` | "Design a weapon recipe from Iron + Fire Crystal" |
| **CoreMechanicAI** | Game mechanics: numeric outcomes, crafting, loot | `CoreMechanicAI` | "Craft a weapon from iron and crystal" |
| **Programmer** | Lua code generation through `execute_lua` | `Programmer` | "Write a damage formula in Lua" |

All built-in roles are described in [AI_AGENT_ROLES.md](../../../Docs/AI_AGENT_ROLES.md).

---

## Architecture: One Model, Different Roles

All agents in a scenario use **the same model** (the backend resolved by
`PlayModeProductionLikeLlmFactory`: the settings asset, `COREAI_TEST_*` variables or the local config
file, see [RUNNING_LIVE_TESTS.md](../../../Docs/RUNNING_LIVE_TESTS.md)), but with:

1. **Different system prompts**: each agent receives its own role.
2. **Different isolated memory**: `CoreMechanicAI` cannot see `Creator` memory.
3. **Different tools**: Programmer receives `execute_lua`; CoreMechanicAI and Creator write memory.

```
                    ┌─────────────────────────┐
                    │   Configured backend    │
                    │ (HTTP API or LLMUnity)  │
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

`MultiAgentCraftingWorkflowPlayModeTests.MultiAgent_CreatorThenMechanicThenProgrammer_CompleteWorkflow`
runs these four turns. The responses below are illustrative; the model chooses the actual values.

```
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 1: Creator decides WHAT to craft and HOW                      │
│  Agent: Creator                                                     │
│  Memory: Creator (isolated)                                         │
│                                                                     │
│  Request: "Design a crafting recipe from Iron + Fire Crystal"       │
│  Response: structured result (item_type, damage, fire_damage, ...)  │
│  Memory: "Design: Iron+Fire Crystal -> weapon, damage ~45, fire ~15" │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 2: CoreMechanicAI computes the exact crafting result          │
│  Agent: CoreMechanicAI                                              │
│  Memory: CoreMechanicAI (isolated)                                  │
│                                                                     │
│  Request: "Calculate craft result for Iron + Fire Crystal"          │
│  Response: {"item_name": "Iron Fireblade", "damage": 45, "fire": 15} │
│  Memory (writes): "Craft#1: Iron Fireblade damage:45 fire:15"      │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 3: Programmer generates Lua code                              │
│  Agent: Programmer                                                  │
│  Memory: Programmer (isolated)                                      │
│                                                                     │
│  Request: "Generate Lua code for a weapon"                          │
│  Tool call: execute_lua (forced; the test's tool captures the code) │
│  Memory: (may skip writing unless requested)                        │
└─────────────────────────────────────────────────────────────────────┘
                              ↓
┌─────────────────────────────────────────────────────────────────────┐
│  STEP 4: CoreMechanicAI repeats the craft from memory               │
│  Agent: CoreMechanicAI                                              │
│  Memory: CoreMechanicAI (its Craft#1 memory is put in the hint)     │
│                                                                     │
│  Request: "Calculate craft result for Iron + Fire Crystal,          │
│            keep it consistent with your previous craft memory"      │
└─────────────────────────────────────────────────────────────────────┘
```

**What the test asserts:**
- ✓ Creator and CoreMechanicAI persist memory
- ✓ Programmer completes an `execute_lua` call
- ✓ Each agent's memory is isolated (Creator, CoreMechanicAI and, when it writes memory, Programmer differ)

The repeat step does not assert identical numbers. Same-ingredient determinism is checked separately by
the `[Explicit]` `CraftingMemoryOpenAi_RepeatIngredients_SecondMatchesFirst`.

---

## MemoryTool - Microsoft.Extensions.AI

Each agent has its own `MemoryTool` with isolated memory:

```csharp
IAgentMemoryStore store = new InMemoryAgentMemoryStore();

// CoreMechanicAI memory: crafting history
MemoryTool mechanicTool = new MemoryTool(store, "CoreMechanicAI");
await mechanicTool.ExecuteAsync("write", "Craft#1: Iron Fireblade damage:45");

// Creator memory: design decisions
MemoryTool creatorTool = new MemoryTool(store, "Creator");
await creatorTool.ExecuteAsync("write", "Design: Iron+Fire Crystal -> weapon");

// They do NOT see each other's memory.
store.TryLoad("Creator", out AgentMemoryState creatorState);       // -> "Design: ..."
store.TryLoad("CoreMechanicAI", out AgentMemoryState mechanicState); // -> "Craft#1: ..."
```

**Memory actions:**
- `read`: return the current memory without saving
- `write`: overwrite memory
- `append`: add to existing memory
- `clear`: clear memory
- `str_replace`: replace the first exact text match, or every match with `replace_all`
- `insert`: add content before a line, after an anchor line, or at the end
- `delete`: remove the first exact block, or every match with `replace_all`
- `rename`: rename the first leading `key:` / `# key:` memory section label

Successful memory mutations record bounded snapshots on `AgentMemoryState.Versions` for stores that preserve the full state object; call `IAgentMemoryStore.ListVersions(roleId)` to inspect them and `IAgentMemoryStore.Revert(roleId, version)` to restore one.

Implementation: `Microsoft.Extensions.AI.AIFunctionFactory.Create()`

---

## Tests

### PlayMode Tests (Full Workflow with a Real Model)

| File | Agents | Backend | Description |
|------|--------|--------|-------------|
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator -> CoreMechanicAI -> Programmer** | Configured backend (`PlayModeProductionLikeLlmFactory`) | **Full 3-agent workflow** |
| `MultiAgentCraftingWorkflowPlayModeTests.cs` | **Creator -> CoreMechanicAI** | Configured backend | Explicit targeted 2-agent duplicate for memory-isolation triage |
| `CraftingMemoryViaLlmUnityPlayModeTests.cs` | CoreMechanicAI | LLMUnity only (ignored on any other resolved backend) | Explicit targeted backend-parity ThreeCrafts probe; requires completed `execute_lua` for every craft |
| `CraftingMemoryViaOpenAiPlayModeTests.cs` | CoreMechanicAI | OpenAI-compatible / production-like backend | Mandatory ThreeCrafts Lua-backed gate; repeat/determinism variants are explicit targeted |

`MultiAgentCraftingWorkflowPlayModeTests` and `CraftingMemoryViaLlmUnityPlayModeTests` are compiled only
with `COREAI_LLM` and not on WebGL.

### EditMode Tests (with Mock LLM)

| File | Description |
|------|-------------|
| `MeaiToolCallsEditModeTests.cs` | MemoryTool tests (write/append/read/clear, granular edits, versions) |

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

> 💡 **All agents use one model** (the configured backend).
> Separation happens at the level of:
> - **System prompt** (agent role)
> - **Memory** (isolated by `roleId`)
> - **Tools** (available functions)
>
> This lets the system scale: adding a new agent = adding a new roleId + prompt.
