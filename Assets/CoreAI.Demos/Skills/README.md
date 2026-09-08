# Demo: Skills — knowledge on demand, in-game tools

One Game Master crafts an item and attacks a dummy via the `Crafting` and `Combat` skills.
Instead of all schemas up front, the model receives a catalog and two meta-tools:
`read_skill` loads the needed instructions and schemas, `call_skill_tool` executes the selected action.
The catalog grows with the number of skills; detailed instructions and schemas cost context only when read.

Scene: `SkillsDemo.unity`. `CoreAISettings` needs a configured LLM backend: a local model
via LLMUnity or an HTTP API. Open the scene, press Play and click `Ask the Game Master`.
Check material consumption, item creation, dummy health decrease, and the panel answer.

Demo skills:

- `Crafting`: `check_inventory`, `craft_item`.
- `Combat`: `attack`, `get_enemy_status`.

## Multi-document skill

The main `SKILL.md` document is returned in full. References are read on demand
via `section`, or all at once via `all: true`:

```csharp
SkillSet crafting = SkillSet.FromTextParts("Crafting", "Создание предметов",
    new[]
    {
        new KeyValuePair<string, string>("SKILL.md", mainText),
        new KeyValuePair<string, string>("references/recipes.md", recipesText),
        new KeyValuePair<string, string>("examples/recipes.md", examplesText)
    }, inventoryTool, craftTool);
```

```text
read_skill(skill_name: "Crafting")
read_skill(skill_name: "Crafting", section: "references/recipes.md")
read_skill(skill_name: "Crafting", all: true)
```

Additional document paths must match the links from the main file. Duplicate
and out-of-root paths are rejected. `section` and `all: true` are mutually exclusive.

This contract works with strings in a plain .NET application, with `TextAsset` in Unity and WebGL.
`SkillSetAsset.SetInstructionAssets` sets files and logical paths; `SkillSetDefinition.Sections`
preserves structure across transfers. MCP reads the same skill via `name`, `section` and `all`.

Schemas and execution share one allowed-tool set. Reading a skill alone
grants no extra rights. Already created proxies see `MutableSkillCatalog` changes;
a conflict of different tools with the same name fails registration.

Details: [AgentBuilder](../../CoreAI/Docs/AGENT_BUILDER.md),
[tool calling rules](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md).