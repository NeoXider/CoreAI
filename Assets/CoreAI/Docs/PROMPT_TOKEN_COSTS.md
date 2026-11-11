# Prompt, skill and tool-contract token costs

What the shipped skills, tool contracts and built-in prompts cost in tokens, counted with CoreAI's own
counter instead of estimated by hand. Use it to see what a role pays on every request (its prompt and its
tool definitions) and what it pays only when it asks for more (a skill read with `read_skill`).

## How the numbers are produced

- **Counter.** `PromptTokenCostMeter` (`Assets/CoreAI/Runtime/Core/Features/AgentPrompts/PromptTokenCostMeter.cs`)
  over `BpeTokenCounter`. The package ships no tokenizer rank files, so the counter runs on its fallback
  estimator (`CalibratingTokenEstimator`, uncalibrated: 0.25 tokens per Latin character, 0.4 per Cyrillic or
  CJK character). The first line of the generated tables names the counter that produced them. With rank
  data supplied through `IBpeRanksProvider`, the same tests produce exact BPE counts.
- **Ground truth.** A provider's reported usage (`LlmUsageRecord`) is the real cost for one model. The explicit
  model run of the prompt fixture set (`PromptRegressionEditModeTests.RealModel_PicksTheExpectedFirstTool`) is
  where provider-reported counts are recorded next to this table.
- **Tool contracts** are measured as a provider receives them: the compact JSON array of function definitions
  (`{"type":"function","function":{"name","description","parameters"}}`) in the request's `tools` field, one
  entry per function the tool publishes (`camera` publishes four). `memory` is measured as the function the
  client binds over a memory store.
- **Skill rows.** "Resources" is the text the game loads (`Assets/CoreAiUnity/Resources/AgentSkills/*.txt`), the
  "C# literal" is the built-in fallback the installer uses when no Resources text exists, "read_skill result"
  is the JSON tool result the model receives when it reads the skill, and "catalog entry" is the one line a
  skill adds to a system prompt that carries a skill catalog.
- Text is measured with LF line endings, so the numbers do not depend on the operating system.

## Regenerating

The tables below are generated; do not edit the numbers by hand.

- Portable tables (skills, core tools, prompts): run the explicit test
  `PromptTokenCostTableEditModeTests.PrintCurrentTables` (portable suite:
  `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter Name=PrintCurrentTables`),
  then replace the block between the generated markers with its output. The test fails while the document is
  out of date.
- Host tool table (Lua, world package, world command, camera): run the explicit Unity EditMode test
  `ToolContractTokenCostEditModeTests.PrintCurrentRows` and replace that table's rows.
- `PromptTokenCostTableEditModeTests.Document_HasAMeasuredRowForEveryShippedSkillAndCoreTool` and
  `ToolContractTokenCostEditModeTests.Document_HasAMeasuredRowForEveryShippedHostTool` fail when a shipped skill
  or tool has no row here.

## Skills, core tool contracts and prompts

<!-- generated: PromptTokenCostTableEditModeTests.PrintCurrentTables -->
Counter: BpeTokenCounter fallback estimator

| Skill text | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| Lua Modding instructions (Resources) | 9604 | 9604 | 2401 |
| Lua Modding instructions (C# literal) | 9604 | 9604 | 2401 |
| Lua Modding read_skill result | 10031 | 10031 | 2508 |
| Lua Modding catalog entry | 253 | 253 | 64 |
| Rbx API instructions (Resources) | 44899 | 45021 | 11225 |
| Rbx API instructions (C# literal) | 44899 | 45021 | 11225 |
| Rbx API read_skill result | 45929 | 46051 | 11483 |
| Rbx API catalog entry | 355 | 355 | 89 |
| Full Lua instructions (Resources) | 2000 | 2004 | 500 |
| Full Lua instructions (C# literal) | 2000 | 2004 | 500 |
| Full Lua read_skill result | 2272 | 2276 | 568 |
| Full Lua catalog entry | 367 | 367 | 92 |

| Tool contract | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| `memory` | 1207 | 1207 | 302 |
| `read_skill` | 829 | 829 | 208 |
| `call_skill_tool` | 632 | 632 | 158 |
| `wait` | 561 | 561 | 141 |
| `get_inventory` | 220 | 220 | 55 |

| Prompt | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| Universal prefix (package default) | 609 | 609 | 153 |
| Creator role prompt | 927 | 927 | 232 |
| Builder role prompt | 736 | 736 | 184 |
| Analyzer role prompt | 334 | 334 | 84 |
| Programmer role prompt | 3158 | 3158 | 790 |
| AINpc role prompt | 293 | 293 | 74 |
| CoreMechanicAI role prompt | 334 | 334 | 84 |
| PlainChat role prompt | 310 | 310 | 78 |
| SmartChat role prompt | 594 | 594 | 149 |
| Merchant role prompt | 593 | 593 | 149 |
| Skill catalog header | 166 | 166 | 42 |
<!-- end generated -->

## Host tool contracts

<!-- generated: ToolContractTokenCostEditModeTests.PrintCurrentRows -->
| Tool contract | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| `camera` | 5408 | 5408 | 1352 |
| `execute_lua` | 1827 | 1827 | 457 |
| `manage_mods` | 3762 | 3762 | 941 |
| `get_mod_logs` | 953 | 953 | 239 |
| `save_world` | 326 | 326 | 82 |
| `load_world` | 347 | 347 | 87 |
| `list_autosaves` | 220 | 220 | 55 |
| `load_autosave` | 317 | 317 | 80 |
| `world_command` | 5874 | 5876 | 1469 |
<!-- end generated -->

## What a built-in role pays per request

Sums of the rows above for the shipped wiring (tool profiles filled by the shipped installers, an inventory
provider registered for the Merchant). They cover the universal prefix, the role prompt and the tool
definitions; the request's tool-contract text block, tool availability and world-state tail messages, chat
history and the user message come on top.

| Role | Tools offered | Tool definitions | Prefix + role prompt | Total |
|---|---|---|---|---|
| Programmer | memory, camera, execute_lua, manage_mods, get_mod_logs, save_world, load_world, list_autosaves, load_autosave, read_skill, call_skill_tool | 3961 | 943 | 4904 |
| Creator | memory, world_command | 1771 | 385 | 2156 |
| Builder | memory, world_command | 1771 | 337 | 2108 |
| Merchant | memory, get_inventory | 357 | 302 | 659 |
| SmartChat | memory | 302 | 302 | 604 |
| PlainChat | none | 0 | 231 | 231 |

`camera` alone is a third of the Programmer's tool definitions, and the chat panel attaches it to the active
chat role, so a SmartChat host with the panel pays 1352 more per request.

## Progressive disclosure, measured

The Programmer's three skills are kept out of the prompt and read on demand. Measured instead of assumed:

- Inlining the three skill texts in the system prompt would cost 2401 + 11225 + 500 = **14126** tokens on
  every request.
- Offering them through the two meta-tools costs `read_skill` + `call_skill_tool` = 208 + 158 = **366** tokens
  per request, so a request that reads no skill saves 13760 tokens (97.4 percent of the inline cost).
- A turn that reads a skill pays its `read_skill` result on top: 11483 tokens for Rbx API, 2508 for Lua
  Modding, 568 for Full Lua. A turn that reads all three pays 14559 + 366, slightly more than inlining.

These figures replace the "~91% saving" arithmetic in [AGENT_BUILDER.md](AGENT_BUILDER.md), which assumed 50
tools of 80 tokens each; the shipped skills carry instructions, not tools, and their cost is dominated by the
Rbx API text.
