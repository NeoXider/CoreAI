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
  skill adds to a system prompt that carries a skill catalog. For a skill written in sections (Rbx API),
  "read_skill result" is the bare read (the core and the section list), and one "read_skill result, section
  ..." row follows for every section the model can read on its own.
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
| Lua Modding instructions (Resources) | 11938 | 11938 | 2985 |
| Lua Modding instructions (C# literal) | 11938 | 11938 | 2985 |
| Lua Modding read_skill result | 5143 | 5143 | 1286 |
| Lua Modding read_skill result, section loops | 1824 | 1824 | 456 |
| Lua Modding read_skill result, section events | 1963 | 1963 | 491 |
| Lua Modding read_skill result, section full | 992 | 992 | 248 |
| Lua Modding read_skill result, section example | 1951 | 1951 | 488 |
| Lua Modding read_skill result, section errors | 1502 | 1502 | 376 |
| Lua Modding catalog entry | 253 | 253 | 64 |
| Rbx API instructions (Resources) | 87209 | 87311 | 21803 |
| Rbx API instructions (C# literal) | 87209 | 87311 | 21803 |
| Rbx API read_skill result | 7996 | 7996 | 1999 |
| Rbx API read_skill result, section scripting | 7526 | 7536 | 1882 |
| Rbx API read_skill result, section datatypes | 4948 | 4950 | 1237 |
| Rbx API read_skill result, section instances | 6214 | 6216 | 1554 |
| Rbx API read_skill result, section attributes | 3904 | 3918 | 976 |
| Rbx API read_skill result, section parts | 5163 | 5181 | 1291 |
| Rbx API read_skill result, section tweens | 2219 | 2225 | 555 |
| Rbx API read_skill result, section messaging | 1364 | 1364 | 341 |
| Rbx API read_skill result, section data | 4997 | 4997 | 1250 |
| Rbx API read_skill result, section terrain | 5471 | 5473 | 1368 |
| Rbx API read_skill result, section building | 3777 | 3777 | 945 |
| Rbx API read_skill result, section characters | 6450 | 6472 | 1613 |
| Rbx API read_skill result, section rig | 1697 | 1697 | 425 |
| Rbx API read_skill result, section input | 4757 | 4763 | 1190 |
| Rbx API read_skill result, section camera | 3820 | 3832 | 955 |
| Rbx API read_skill result, section gui | 3744 | 3744 | 936 |
| Rbx API read_skill result, section uxml | 6743 | 6747 | 1686 |
| Rbx API read_skill result, section chat | 2011 | 2011 | 503 |
| Rbx API read_skill result, section lighting | 3615 | 3615 | 904 |
| Rbx API read_skill result, section ai | 6007 | 6011 | 1502 |
| Rbx API catalog entry | 462 | 462 | 116 |
| Full Lua instructions (Resources) | 2000 | 2004 | 500 |
| Full Lua instructions (C# literal) | 2000 | 2004 | 500 |
| Full Lua read_skill result | 2272 | 2276 | 568 |
| Full Lua catalog entry | 367 | 367 | 92 |

| Tool contract | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| `memory` | 1207 | 1207 | 302 |
| `read_skill` | 683 | 683 | 171 |
| `call_skill_tool` | 632 | 632 | 158 |
| `wait` | 561 | 561 | 141 |
| `get_inventory` | 220 | 220 | 55 |

| Prompt | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| Universal prefix (package default) | 609 | 609 | 153 |
| Creator role prompt | 927 | 927 | 232 |
| Builder role prompt | 736 | 736 | 184 |
| Analyzer role prompt | 334 | 334 | 84 |
| Programmer role prompt | 3530 | 3530 | 883 |
| AINpc role prompt | 293 | 293 | 74 |
| CoreMechanicAI role prompt | 334 | 334 | 84 |
| PlainChat role prompt | 310 | 310 | 78 |
| SmartChat role prompt | 594 | 594 | 149 |
| Merchant role prompt | 593 | 593 | 149 |
| Skill catalog header | 107 | 107 | 27 |
<!-- end generated -->

## Host tool contracts

<!-- generated: ToolContractTokenCostEditModeTests.PrintCurrentRows -->
| Tool contract | Characters | UTF-8 bytes | Tokens |
|---|---|---|---|
| `camera` | 4065 | 4065 | 1017 |
| `execute_lua` | 776 | 776 | 194 |
| `manage_mods` | 2453 | 2453 | 614 |
| `get_mod_logs` | 1087 | 1087 | 272 |
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
| Programmer | memory, camera, execute_lua, manage_mods, get_mod_logs, save_world, load_world, list_autosaves, load_autosave, read_skill | 2874 | 1036 | 3910 |
| Creator | memory, world_command | 1771 | 385 | 2156 |
| Builder | memory, world_command | 1771 | 337 | 2108 |
| Merchant | memory, get_inventory | 357 | 302 | 659 |
| SmartChat | memory | 302 | 302 | 604 |
| PlainChat | none | 0 | 231 | 231 |

`camera` alone is a third of the Programmer's tool definitions, and the chat panel attaches it to the active
chat role, so a SmartChat host with the panel pays 1017 more per request. `call_skill_tool` (158) is offered only
while a skill of the role has tools or the role authors skills, so the Programmer, whose three skills carry
instructions only, does not pay for it (P16, 2026-11-12; before: 4525 tool tokens, 5561 in total).

## Progressive disclosure, measured

The Programmer's three skills are kept out of the prompt and read on demand. Measured instead of assumed:

- Inlining the three skill texts in the system prompt would cost 2970 + 21548 + 500 = **25018** tokens on
  every request.
- Offering them through `read_skill` costs **171** tokens per request (`call_skill_tool` is not offered: the
  skills have no tools), so a request that reads no skill saves 24847 tokens (99.3 percent of the inline cost).
- A turn that reads a skill pays its `read_skill` results on top, and the two large skills are read in parts.
  A bare `read_skill("Lua Modding")` returns its core and the list of its 5 sections for 1271 tokens,
  and each section costs 248 to 491 more (all 5 together: 2059). A bare `read_skill("Rbx API")` returns its core and
  the list of its 19 sections for 1999 tokens, and each section read costs 341 to 1686 more (all 19
  sections together: 20855). Full Lua is one read of 568 tokens. The largest single read is 7996
  characters, so every read fits the default tool-result cap of 8,000 characters, which cut each of the
  old one-piece texts to its first 8,000 characters (`BuiltInSkillReadsFitTheToolResultCapEditModeTests` keeps it so).

These figures replace the "~91% saving" arithmetic in [AGENT_BUILDER.md](AGENT_BUILDER.md), which assumed 50
tools of 80 tokens each; the shipped skills carry instructions, not tools, and their cost is dominated by the
Rbx API text.
