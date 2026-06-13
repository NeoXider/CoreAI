# MultiAgent Workflow Plan (v2.0)

## Goal
Implement multi-agent orchestration following the Claude Agent SDK / Anthropic Research System pattern.

## Pattern Sources (from Open Sources)

### Claude Agent SDK (Anthropic) - Best Practices
Studied from: ksred.com, DeepWiki, Claude Lab

1. **Declarative subagent definitions**
   ```typescript
   agents: {
     "security-reviewer": {
       description: "Identifies security vulnerabilities",
       prompt: "You are a security specialist...",
       tools: ["Read", "Grep"],
       model: "opus"  // subagent can use a different model
     }
   }
   ```

2. **Task tool** - NOT part of subagent tools!
   - Task MUST be in the parent's allowedTools
   - Task MUST NOT be in subagent tools - subagents do not spawn their own subagents

3. **Context isolation** - each subagent receives a CLEAN context
   - subagent sees ONLY its own task
   - does not pollute the parent context
   - results are aggregated back

4. **The model decides parallelism ITSELF** - no explicit `parallel` action is needed
   - define capability, not scheduling

5. **Resource control**:
   - `maxTurns` - tool call limit for subagent
   - `maxTokens` - output token limit
   - `maxBudgetUsd` - budget limit

### Anthropic Research System
- **Lead agent + Subagents** - lead coordinates, subagents execute
- **Parallel subagents** - 3-5 subagents simultaneously
- **Result synthesis** - lead collects results
- **Token budget** - multi-agent uses about 15x tokens vs single agent

## Key Changes vs v1.0

1. ✅ Context isolation - subagent receives ONLY its own task, not the full parent context
2. ✅ The model decides parallelism itself (but limited through MaxParallelAgents)
3. ✅ Subagent results are aggregated into the parent
4. ✅ Token budget and max turns for subagents
5. ✅ Agents are defined declaratively with description + tools + model

---

## Architecture

### 1. SubAgentDefinition - declarative subagent definition
**File:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/SubAgentDefinition.cs` (NEW)

**Important:** SubAgentDefinition does NOT contain the Task tool - subagents cannot spawn subagents!

```csharp
public sealed class SubAgentDefinition {
    /// <summary>Agent role ID (used for lookup in AgentRegistry).</summary>
    public string RoleId { get; init; }
    
    /// <summary>Description for the orchestrator LLM - when to use this agent.</summary>
    public string Description { get; init; }
    
    /// <summary>Specific system prompt for the subagent (extends the base one).</summary>
    public string CustomPrompt { get; init; }
    
    /// <summary>Tools for the subagent (does NOT include Task tool!).</summary>
    public IReadOnlyList<ILlmTool> Tools { get; init; }
    
    /// <summary>Model for the subagent (null = use parent model).</summary>
    public string Model { get; init; }
    
    /// <summary>Max tokens for subagent response. Default: 4096.</summary>
    public int MaxTokens { get; init; } = 4096;
    
    /// <summary>Max turns (tool calls) for subagent. Default: 10.</summary>
    public int MaxTurns { get; init; } = 10;
}
```

**Claude Agent SDK constraints (best practices):**
- Task tool MUST be in the parent's allowedTools
- Task tool MUST NOT be in subagent tools - subagents do not spawn subagents

### 2. AgentLlmTool Schema - following the Claude Agent SDK pattern

**Key rule:** AgentLlmTool (similar to Task in Claude Agent SDK) is only for the parent agent!

```json
{
  "type": "object",
  "properties": {
    "subagent": {
      "type": "string",
      "description": "Which subagent to call (e.g., 'Programmer', 'CoreMechanicAI')."
    },
    "task": {
      "type": "string",
      "description": "Task description for the subagent"
    },
    "context": {
      "type": "string",
      "description": "Optional context from parent (trimmed to fit)"
    },
    "max_turns": {
      "type": "integer",
      "description": "Max turns (default from SubAgentDefinition)"
    }
  },
  "required": ["subagent", "task"]
}
```

### 3. AgentRegistry - registry with SubAgentDefinition support
**File:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/AgentRegistry.cs` (MODIFY)

```csharp
public interface IAgentRegistry {
    // Existing methods...
    void RegisterSubAgents(IReadOnlyDictionary<string, SubAgentDefinition> subAgents);
    IReadOnlyDictionary<string, SubAgentDefinition> GetSubAgents();
    bool TryGetSubAgent(string roleId, out SubAgentDefinition definition);
}
```

### 3. AgentTool - simple MEAI tool (following the Task pattern in Claude Agent SDK)
**File:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/AgentLlmTool.cs` (MODIFY)

Instead of explicit `action: "call"`, make it simpler - the model describes who to call and with what task:

```json
{
  "type": "object",
  "properties": {
    "subagent": {
      "type": "string",
      "description": "Which subagent to call (e.g., 'Programmer', 'CoreMechanicAI'). Use description to match."
    },
    "task": {
      "type": "string",
      "description": "Task description for the subagent"
    },
    "context": {
      "type": "string",
      "description": "Optional context from parent (will be trimmed to fit)"
    },
    "max_turns": {
      "type": "integer",
      "description": "Max turns for this subagent (default from settings)"
    }
  },
  "required": ["subagent", "task"]
}
```

**Difference from v1:**
- ❌ `action: "call"` -> ✅ `subagent: "role"` - more declarative, LLM decides itself
- ❌ Explicit parallel -> ✅ Model decides itself through multiple AgentTool calls
- ❌ Complex action enum -> ✅ Simple task description

### 4. AgentOrchestrator - subagent execution with context isolation
**File:** `Assets/CoreAI/Runtime/Core/Features/Orchestration/AgentOrchestrator.cs` (MODIFY)

**Session Persistence (optional):**
```csharp
// Save the subagent session for continuation
Task<AgentCallResult> ExecuteSubAgentAsync(
    string subAgentRoleId,
    string task,
    string context,
    int maxTurns,
    CancellationToken ct,
    string? resumeSessionId = null  // optional for continuation
);
```

**Claude Agent SDK best practices:**
- Subagent receives a CLEAN context - only its own task
- Result is returned as text/tool_result
- Does NOT pollute parent context with intermediate steps
- Model decides ITSELF when to parallelize (through multiple calls)

```csharp
public interface IAgentOrchestrator {
    Task<AgentCallResult> ExecuteSubAgentAsync(
        string subAgentRoleId,
        string task,
        string context,  // optional context from parent
        int maxTurns,
        CancellationToken ct);
    
    // Aggregate results from multiple subagents
    Task<AgentCallResult[]> ExecuteSubAgentsParallelAsync(
        IReadOnlyList<(string roleId, string task)> subAgents,
        CancellationToken ct);
}
```

**Key distinction:**
- Subagent receives a CLEAN context - only its own task
- Result returns to the parent as text/tool result
- Does NOT pollute the parent context with subagent intermediate steps

### 5. CoreAISettings - add subagent settings
**File:** `CoreAISettings.cs` (MODIFY)

```csharp
public static class CoreAISettings {
    /// <summary>Max parallel subagents. Default: 3 (Anthropic Research).</summary>
    public static int MaxParallelAgents { get; set; } = 3;
    
    /// <summary>Subagent timeout in seconds. Default: 60.</summary>
    public static int AgentCallTimeoutSeconds { get; set; } = 60;
    
    /// <summary>Max turns (tool calls) for subagent. Default: 10.</summary>
    public static int SubAgentMaxTurns { get; set; } = 10;
    
    /// <summary>Max tokens for subagent response. Default: 4096.</summary>
    public static int SubAgentMaxTokens { get; set; } = 4096;
}
```

**Why 3:**
- Anthropic Research System uses 3-5 parallel agents
- Claude Agent SDK: "Claude decides when to parallelise" - the model itself
```

---

## System Prompt for Creator (update)

```
## Subagents
You can delegate tasks to specialized subagents:

- Programmer: "Generates and executes Lua code"
- CoreMechanicAI: "Calculates game mechanics, crafting, balancing"
- Analyzer: "Analyzes session metrics and provides recommendations"
- Merchant: "Handles NPC trading and inventory"
- AINpc: "Generates NPC dialogs and behavior"

Use the `agent` tool to call a subagent. Describe what you need in the task field.

Example:
{"subagent": "Programmer", "task": "Create a healing potion with effect: heal 50HP, duration: 10s"}
```

---

## DI Registration

**File:** `CorePortableInstaller.cs` (MODIFY)
```csharp
// Subagents are registered at game start
// AgentLlmTool is added through AgentMemoryPolicy.GetToolsForRole()
```

---

## Example Flow (v2)

```
User: "Create a wave of enemies and implement it"

1. Creator LLM calls agent tool:
   {subagent: "CoreMechanicAI", task: "Design enemy wave parameters"}
   
2. AgentOrchestrator.ExecuteSubAgentAsync():
   - Reads SubAgentDefinition from registry
   - Creates a clean context with task
   - Calls LLM with CustomPrompt + task
   - Result is returned as text

3. Creator receives result:
   {wave_size: 10, enemy_types: ["goblin", "orc"], difficulty: 5}

4. Creator calls Programmer:
   {subagent: "Programmer", task: "Generate enemy_wave.lua", context: "wave config..."}
   
5. Programmer returns Lua code

6. Creator aggregates the result and returns it to the user
```

**Parallelism:**
```
Creator calls several subagents at once:
{subagent: "Analyzer", task: "Analyze current session balance"}
{subagent: "CoreMechanicAI", task: "Calculate next wave difficulty"}
-> AgentOrchestrator.ExecuteSubAgentsParallelAsync() runs them in parallel
-> Results are aggregated
```

---

## Files to Create/Modify

### New Files
| File | Description |
|------|-----------|
| `SubAgentDefinition.cs` | Declarative subagent definition |
| `AgentCallResult.cs` | Agent call result |

### Modified
| File | Change |
|------|-----------|
| `AgentRegistry.cs` | Add RegisterSubAgents, GetSubAgents |
| `AgentLlmTool.cs` | Simplify to subagent call |
| `AgentOrchestrator.cs` | Add ExecuteSubAgentAsync with isolation |
| `CoreAISettings.cs` | Add MaxParallelAgents, AgentCallTimeoutSeconds, SubAgentMaxTurns |
| `CoreAISettingsAsset.cs` | Add fields in Inspector |
| `AgentMemoryPolicy.cs` | Add AgentLlmTool in GetToolsForRole |
| `CorePortableInstaller.cs` | DI registration |

---

## Questions

1. ✅ Timeout - 60 sec confirmed
2. ✅ Errors - return in JSON confirmed
3. ✅ Parallelism - yes, default=3 (based on Anthropic Research System)

4. **New question:** How should the subagent result be passed back?
   - Option A: As text in the parent context (simple, but pollutes it)
   - Option B: As tool_result - aggregated automatically through MEAI
   - **Proposal:** Option B - through tool result; MEAI FunctionInvokingChatClient handles it itself

5. **New question:** Max turns - how do we control this so a subagent does not make infinite calls?
   - Proposal: `SubAgentMaxTurns` (default 10) - tool call limit

---

## Final Steps: Documentation and Versioning

After implementing all components:

### 1. CHANGELOG.md - add v0.9.0

```markdown
## v0.9.0 - Multi-Agent Orchestration

### New Features
- **AgentRegistry** - global agent registry for multi-agent systems
- **SubAgentDefinition** - declarative subagent definition with description, tools, model
- **AgentLlmTool** - MEAI tool for calling subagents from the main agent (following the Claude Agent SDK pattern)
- **AgentOrchestrator** - subagent execution with context isolation and parallel execution

### Settings
- `MaxParallelAgents` - max parallel subagents (default 3)
- `AgentCallTimeoutSeconds` - agent call timeout (default 60s)
- `SubAgentMaxTurns` - max turns for subagent (default 10)
- `SubAgentMaxTokens` - max tokens for subagent (default 4096)

### Breaking Changes
- AgentTool schema changed: `{action, role, hint}` -> `{subagent, task, context}`

### Examples
- Creator can call Programmer, CoreMechanicAI, Analyzer through agent tool
- Parallel call of several subagents
```

### 2. TODO.md - update status

- Add ✅ for MultiAgentWorkflow
- Remove from "Next tasks"

### 3. README.md / Docs - update agents section

### 4. Version bump - v0.8.0 -> v0.9.0

---

## Final File List

### New Files
| File | Description |
|------|-----------|
| `SubAgentDefinition.cs` | Declarative subagent definition |
| `AgentCallResult.cs` | Agent call result |

### Modified
| File | Change |
|------|-----------|
| `AgentRegistry.cs` | Add RegisterSubAgents, GetSubAgents |
| `AgentLlmTool.cs` | Simplify to subagent call |
| `AgentOrchestrator.cs` | Add ExecuteSubAgentAsync with isolation |
| `CoreAISettings.cs` | Add MaxParallelAgents, AgentCallTimeoutSeconds, SubAgentMaxTurns |
| `CoreAISettingsAsset.cs` | Add fields in Inspector |
| `AgentMemoryPolicy.cs` | Add AgentLlmTool in GetToolsForRole |
| `CorePortableInstaller.cs` | DI registration |
| `CHANGELOG.md` | Add v0.9.0 |
| `TODO.md` | Update status |
   ✅ **Confirmed** - yes, default 3, add to CoreAISettings

---

## Additional Changes

### CoreAISettings - add parallelism settings
**File:** `CoreAISettings.cs` (MODIFY)

```csharp
public static class CoreAISettings {
    /// <summary>Default maximum parallel agents. Default: 3.</summary>
    public static int MaxParallelAgents { get; set; } = 3;
    
    /// <summary>Agent call timeout in seconds. Default: 60.</summary>
    public static int AgentCallTimeoutSeconds { get; set; } = 60;
}
```

### CoreAISettingsAsset - add fields
**File:** `CoreAISettingsAsset.cs` (MODIFY)

```csharp
[Header("⚙️ Multi-Agent")]
[Tooltip("Maximum parallel agents. 3 = recommended value.")]
[SerializeField]
[Min(1)]
private int maxParallelAgents = 3;

[Tooltip("Agent call timeout (seconds).")]
[SerializeField]
[Min(10)]
private int agentCallTimeoutSeconds = 60;

// Properties
public int MaxParallelAgents => maxParallelAgents < 1 ? 1 : maxParallelAgents;
public int AgentCallTimeoutSeconds => agentCallTimeoutSeconds < 10 ? 60 : agentCallTimeoutSeconds;
```

### AgentLlmTool - add parallel support
**Tool Schema:**
```json
{
  "type": "object",
  "properties": {
    "action": {
      "type": "string",
      "enum": ["call", "parallel", "update_prompt", "add_tool", "list_agents"],
      "description": "Action to perform"
    },
    "role": {
      "type": "string", 
      "description": "Target agent role for 'call' action"
    },
    "roles": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Target agent roles for 'parallel' action"
    },
    "hints": {
      "type": "array", 
      "items": {"type": "string"},
      "description": "Task hints for each agent in parallel mode"
    },
    "hint": {
      "type": "string",
      "description": "Task hint for 'call' action"
    },
    "prompt": {
      "type": "string",
      "description": "New system prompt for 'update_prompt' action"
    },
    "timeout": {
      "type": "integer",
      "description": "Timeout in seconds (default from settings)"
    }
  },
  "required": ["action"]
}
```

---

## Final Implementation Plan

### Phase 1: Base Infrastructure
1. **AgentCallResult.cs** - data class for result
2. **CoreAISettings.cs** - add MaxParallelAgents, AgentCallTimeoutSeconds
3. **CoreAISettingsAsset.cs** - add fields and properties

### Phase 2: AgentRegistry
4. **AgentRegistry.cs** - interface and implementation
5. **AgentBuilder.cs** - add Register() method

### Phase 3: AgentOrchestrator
6. **AgentOrchestrator.cs** - agent call (sync + parallel)
7. Constructor update with ILlmClient, IAgentRegistry

### Phase 4: AgentLlmTool (MEAI)
8. **AgentLlmTool.cs** - MEAI tool with actions call/parallel/update_prompt/add_tool/list_agents
9. Integration with AgentOrchestrator

### Phase 5: Integration
10. **CorePortableInstaller.cs** - DI registration
11. **AgentMemoryPolicy.cs** - add AgentLlmTool in GetToolsForRole()

### Phase 6: Tests
12. EditMode tests for AgentRegistry
13. EditMode tests for AgentLlmTool
