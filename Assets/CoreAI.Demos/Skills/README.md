# Demo: Skills (SkillSet + AgentBuilder)

**What you will see:** one Game Master agent that only ever sees two tools, yet crafts a sword and attacks
a dummy — because it loads the schema for the skill it needs, when it needs it.

Scene: `SkillsDemo.unity`. A **configured LLM backend** is required in `CoreAISettings`
(LLMUnity model or HTTP API: LM Studio, OpenAI, etc.).

## What It Shows

A `DemoGameMaster` agent with two skills, built through `AgentBuilder`:

- **Crafting**: `check_inventory`, `craft_item` (+ skill instructions);
- **Combat**: `attack`, `get_enemy_status`.

The model always sees only two meta-tools, `read_skill` and `call_skill_tool`, plus the skill catalog
in the system prompt. Tool schemas are loaded on demand (`read_skill`), so token overhead does not grow
with the number of skills/tools.

## How to Use It

1. Make sure `Resources/CoreAISettings` has a working LLM Backend selected.
2. Open the scene, press Play, then press `Ask the Game Master` (default request: craft a sword and
   attack the training dummy).
3. Observe: inventory decreases, an item appears, and the dummy's HP drops; all of this happens through
   model tool calls. The agent response is shown in the panel.

Details: `Assets/CoreAI/Docs/AGENT_BUILDER.md` (Skills section),
`Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md`.
