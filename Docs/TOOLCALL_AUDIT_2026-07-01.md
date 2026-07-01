# Tool-Calling Audit — 2026-07-01

Read-only audit of how CoreAI LLM **tool calls** are defined, created, structured, streamed, and
executed, plus correctness gaps. No `.cs` source was changed; this is an audit deliverable only.
Every finding cites `file:line`. Sections W1–W4 below are the detailed per-area reports.

## Scope & method

- W1 — Tool schema definition & MEAI `AIFunction` binding (the "do I need to hand-write schema?" question).
- W2 — Execution pipeline (`ToolExecutionPolicy`: parallelism, dedup, timeout, cancellation, success detection).
- W3 — Parsing & streaming robustness (SSE accumulation, name repair, `<think>`, JSON args, leaks).
- W4 — Per-tool correctness (every concrete `*LlmTool`: `[Description]` coverage, schema agreement, validation).

Verification: source grep + reading across `Assets/CoreAI` and `Assets/CoreAiUnity`, cross-checked against the
bundled `Microsoft.Extensions.AI.Abstractions` 10.7.0 XML docs. No Unity Editor / test run was performed.

## Answer to the schema question (do you need to write `ParametersSchema`?)

**For native provider tool-calling: no.** When a tool is exposed via `AIFunctionFactory.Create(delegate, …)`,
MEAI **auto-derives** the JSON schema from the delegate's parameters and their `[System.ComponentModel.Description]`
attributes, and the model receives that `AIFunction.JsonSchema` — `MeaiOpenAiChatClient.BuildToolsPayload` sets
`function.parameters` from `af.JsonSchema` (`MeaiOpenAiChatClient.cs:1783-1800`). The hand-written
`ILlmTool.ParametersSchema` is **not** sent on the native path (`AiToolContractPromptFormatter` early-returns for
native roles, `AiToolContractPromptFormatter.cs:71-75`).

**Where `ParametersSchema` is still consumed:**
1. The **prompt-based (non-native) tool contract** — printed only when `supportsNativeToolCalling == false`
   (`AiToolContractPromptFormatter.cs:85-106`).
2. The **required-argument repair gate** — `ToolExecutionPolicy.ValidateRequiredArguments` parses
   `ParametersSchema.required` before invoking the MEAI function (`ToolExecutionPolicy.cs:508-542`).

**Consequence — schema drift is a real hazard:** the native schema (MEAI reflection) and the hand-written
`ParametersSchema` are maintained independently, so param names / required flags can silently disagree. See W1
§"Schema Drift Risk". Recommendation: treat `AIFunctionFactory.Create` + `[Description]` as the source of truth,
derive the prompt/repair schema from `AIFunction.JsonSchema`, and add a drift check. **The correct fix for
"schema not reaching the model" is `[Description]` attributes on delegate params, not a longer `ParametersSchema`.**

## Cross-cutting findings, ranked

| # | Sev | Area | Finding | Evidence |
|---|-----|------|---------|----------|
| 1 | High | Exec | Cross-request memory/skills/mods mutations can race — same-batch serialization is request-local; concurrent turns can lose a `memory append` (load/modify/save not atomic) | W2 F1 · `ToolExecutionPolicy.cs:692-710`, `MemoryTool.cs:104-331` |
| 2 | High | Parse | Invalid/truncated **text-shaped** tool JSON can leak to visible streaming output at turn end (fail-open), while native SSE fails closed | W3 #1 · `MeaiLlmClient.cs:891-923` |
| 3 | Med | Exec | Streaming tool-loop uses `MaxToolCallRetries+1`, ignoring the configured/per-agent `MaxToolCallRoundtrips` honored by the non-streaming path | W2 F4 · `MeaiLlmClient.cs:436-437` |
| 4 | Med | Exec | `IsToolResultSuccess` is lossy: `{"error":…}` / `{"ok":false}` / `Failed: …` classify as success; runs after truncation | W2 F5 · `ToolExecutionPolicy.cs:944-972` |
| 5 | Med | Parse | Native SSE accumulator keys only by `index` (defaults missing to 0) → local servers omitting/reusing index merge unrelated calls | W3 #2 · `MeaiOpenAiChatClient.cs:1315-1531` |
| 6 | Med | Parse | Text-shaped tool calls placed inside `<think>` are stripped before extraction → silently lost | W3 #4 · `MeaiLlmClient.cs:617-645` |
| 7 | Med | Parse/Exec | Case-insensitive name repair routes to the first match; catalogs colliding under `OrdinalIgnoreCase` are ambiguous | W3 #5 · `ToolExecutionPolicy.cs:237-247` |
| 8 | Med | Tools | `world_command` `apply_force`/`set_velocity` accept omitted vector components (zero-vector) despite "required" error text | W4 #1 · `WorldLlmTool.cs:500,566` |
| 9 | Med | Schema | Multi-function wrappers (`SceneLlmTool`, `CameraLlmTool`) publish `ParametersSchema => "{}"`; and `DelegateLlmTool` is always `{}` even for parameterized delegates | W4 #2/#3/#4, W1 · `SceneLlmTool.cs:23`, `CameraLlmTool.cs:22`, `DelegateLlmTool.cs:19` |
| 10 | Med | Exec | Intra-batch duplicates of non-`AllowDuplicates` tools are not caught on the first turn (dedup is whole-batch, cross-turn) | W2 F2 · `ToolExecutionPolicy.cs:160-188` |
| 11 | Low | Exec | Duplicate signatures use the raw model name, not the repaired canonical name → casing variants evade dedup | W2 F3 · `ToolExecutionPolicy.cs:147-175` |
| 12 | Low | Parse | Provider-native `reasoning_content` dropped in streaming but used as visible fallback non-streaming (inconsistent) | W3 #7 · `MeaiOpenAiChatClient.cs:1305-1347,1187-1211` |
| 13 | Low | Parse | Hybrid streaming scanner re-scans the full buffer per delta → O(n²) on long streams | W3 #8 · `MeaiLlmClient.cs:629-645` |
| 14 | Low | Tools | Misc description/contract mismatches: `CompatibilityLlmTool` CSV-vs-array, `WaitLlmTool` clamps over-max, `set_transform` no-op success, deferred null-dep failures | W4 #6-9 |

Note: several of these overlap items already tracked in `TODO.md` (audit-cleanup section: lossy
`IsToolResultSuccess`, per-tool timeout tests, `ExtractToolNames` brittleness), which corroborates them.

---


# W1 Audit: LLM Tool Schemas and MEAI Function Binding

## Scope

This is a read-only audit of how CoreAI LLM tools define parameter schemas and how those tools become Microsoft.Extensions.AI (MEAI) functions.

Studied source:

- `Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs`
- `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs`
- Representative `CreateAIFunction()` / `AIFunctionFactory.Create(...)` implementations under `Assets`, excluding tests.

## Executive Summary

CoreAI has two schema tracks:

1. `ILlmTool.ParametersSchema`: a hand-written JSON schema string, commonly built with `LlmToolBase.JsonParams(...)`.
2. `IAIFunctionLlmTool.CreateAIFunction()` / `IAIFunctionsLlmTool.CreateAIFunctions()`: MEAI `AIFunction` objects, usually produced with `AIFunctionFactory.Create(...)`.

For native OpenAI-compatible provider tool-calling, the model receives the MEAI `AIFunction.JsonSchema`, not `ILlmTool.ParametersSchema`. `MeaiOpenAiChatClient.BuildToolsPayload(...)` serializes each `AIFunction` as an OpenAI `tools[]` entry and sets `function.parameters` from `af.JsonSchema.ToString()` (`Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:1783-1800`).

For CoreAI's prompt-based, non-native tool contract, `AiToolContractPromptFormatter` prints `ILlmTool.ParametersSchema` in the system prompt only when `supportsNativeToolCalling == false` (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:71-106`). On native roles it early-returns before the "Available tools" section, so hand-written schemas are not sent in the prompt (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:71-75`).

However, `ParametersSchema` is not completely dead on the native execution path. `ToolExecutionPolicy` uses it for pre-invocation required-argument repair feedback before calling the MEAI function (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:323-357`, `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:508-542`). Therefore schema drift can still cause native-call correctness hazards in validation and retry feedback, even though provider-side tool schema comes from MEAI.

## Data Flow

### `ParametersSchema` track

`ILlmTool` defines `ParametersSchema` as "JSON schema describing tool parameters" (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:11-23`). `LlmToolBase` defaults it to `"{}"` and provides `JsonParams(...)`, which manually writes an object schema with `properties` and optional `required` list (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:64-75`).

Prompt path:

- `AiOrchestrator` gets tools and appends the tool contract into the system prompt (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:112-114`).
- It derives `supportsNativeToolCalling` from the role's LLM client and passes that flag to `AiToolContractPromptFormatter.AppendToolContract(...)` (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:1160-1172`).
- If native tool-calling is supported, the formatter appends native guidance and returns before listing per-tool schemas (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:32-39`, `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:71-75`).
- If native tool-calling is not supported, the formatter prints `Available tools:` and appends `schema: <ParametersSchema>` for each non-empty, non-`{}` schema (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:85-106`).

Execution-policy path:

- All native and text-extracted tool calls pass through `ToolExecutionPolicy.ExecuteBatchAsync(...)` / `ExecuteSingleAsync(...)` (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:728-757`).
- Before MEAI invocation, `ExecuteSingleAsync(...)` calls `ValidateRequiredArguments(fc)` (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:317-337`).
- `ValidateRequiredArguments(...)` finds the original `ILlmTool` by name, parses `tool.ParametersSchema`, reads only its `required` array, and rejects missing required arguments with a repair message containing the compacted hand-written schema (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:508-542`).

Diagnostics / budgeting also read `ParametersSchema`, but they are not provider schema paths: prompt token budgeting estimates it (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:1230`), and diagnostics snapshot/inspector code displays it.

### `CreateAIFunction()` track

`IAIFunctionLlmTool` and `IAIFunctionsLlmTool` are explicit contracts for exposing tools as MEAI functions (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:26-45`).

Binding path:

- `MeaiLlmClient.CompleteAsync(...)` builds `aiTools` from request tools (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:169-177`).
- If any functions were built, it assigns them to `chatOptions.Tools` and applies forced tool mode (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:204-217`).
- Streaming uses the same pattern: build `aiTools`, assign `chatOptions.Tools`, then create a shared `ToolExecutionPolicy` (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:417-430`, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:447-451`).
- `BuildAIFunctions(...)` canonicalizes the original `ILlmTool` list, special-cases `MemoryLlmTool`, then accepts `DelegateLlmTool`, `IAIFunctionLlmTool`, and `IAIFunctionsLlmTool`; tools that implement only `ILlmTool` are skipped with a warning (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:1859-1905`).
- `MeaiOpenAiChatClient.GetResponseAsync(...)` and streaming both call `BuildToolsPayload(options)` and include the resulting `tools` array in the request body when non-empty (`Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:107-128`, `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:248`).
- `BuildToolsPayload(...)` uses `af.Name`, `af.Description`, and `af.JsonSchema` as the provider-facing function schema (`Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:1783-1800`).

Non-native LLMUnity path:

- `MeaiLlmClient.CreateLlmUnity(...)` creates a client with `supportsNativeToolCalling: false` (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:130-160`).
- Even though the orchestrator prompt path prints `ParametersSchema`, `LlmUnityMeaiChatClient` also appends "Bound tools (schemas)" from `AIFunction.JsonSchema` when `options.Tools` exists (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmUnityMeaiChatClient.cs:113-132`). This means the LLMUnity text path can see both the prompt contract schema and reflected MEAI schema.

## MEAI Schema Generation

Yes: MEAI generates the function input JSON schema for `AIFunctionFactory.Create(...)`.

Local package evidence from `Microsoft.Extensions.AI.Abstractions` 10.7.0:

- `AIFunctionDeclaration.JsonSchema` is "a JSON Schema describing the function and its input parameters" (`Assets/Packages/Microsoft.Extensions.AI.Abstractions.10.7.0/lib/netstandard2.0/Microsoft.Extensions.AI.Abstractions.xml:4837-4841`).
- When an `AIFunction` is created via `AIFunctionFactory`, the schema is "automatically derived from the method's parameters" (`Assets/Packages/Microsoft.Extensions.AI.Abstractions.10.7.0/lib/netstandard2.0/Microsoft.Extensions.AI.Abstractions.xml:4858-4859`).
- `AIFunctionFactory` wraps .NET methods specified as delegates or `MethodInfo`, and automatically derives schemas for input parameters exposed through `JsonSchema` (`Assets/Packages/Microsoft.Extensions.AI.Abstractions.10.7.0/lib/netstandard2.0/Microsoft.Extensions.AI.Abstractions.xml:4883-4892`).
- For `Create(Delegate, ...)`, parameters are sourced from `AIFunctionArguments` and represented in the returned function's `JsonSchema`; `CancellationToken` is automatically bound and not included in the generated schema (`Assets/Packages/Microsoft.Extensions.AI.Abstractions.10.7.0/lib/netstandard2.0/Microsoft.Extensions.AI.Abstractions.xml:4903-4916`).

CoreAI evidence:

- Tools consistently call `AIFunctionFactory.Create(func, options)` with `Name` and `Description` set, for example `WaitLlmTool` (`Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:39-47`), `MemoryTool` (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs:28-37`), `WorldLlmTool` (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:99-107`), and `ComponentLlmTool` (`Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:65-74`).
- CoreAI then forwards the generated `af.JsonSchema` to providers (`Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:1790-1800`).

Conclusion: hand-writing `ParametersSchema` is unnecessary for the provider-native schema itself. It remains needed for the prompt-mode contract and, currently, for CoreAI's required-argument repair gate.

## Schema Drift Risk

Schema drift is a real correctness hazard because CoreAI maintains duplicated schema information:

- The model's provider-native schema comes from MEAI reflection (`AIFunction.JsonSchema`).
- Prompt-mode instructions and required-argument repair use `ILlmTool.ParametersSchema`.

Observed drift patterns:

1. **Required flags can disagree.** `ParametersSchema` expresses required fields manually; MEAI derives requiredness from the method/delegate parameter metadata and defaults. `ToolExecutionPolicy` enforces the manual `required` list from `ParametersSchema`, not `AIFunction.JsonSchema` (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:508-542`).
2. **Parameter names can disagree.** Native execution invokes `AIFunction.InvokeAsync(...)` with argument names from the provider call after MEAI binding (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:339-357`). Required-argument repair checks names from `ParametersSchema` before that (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:518-528`). A mismatched name can either reject a valid native call or fail to catch a missing MEAI-required argument.
3. **Types can disagree.** `JsonParams(...)` only records simple string type names (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:71-75`), while MEAI derives real JSON schema from CLR types. The repair validator currently reads only `required`, so type drift mostly affects prompt quality and repair wording, not local validation.
4. **Description text can disagree.** Provider-native schema descriptions come from `[System.ComponentModel.Description]` on delegate/method parameters; prompt-mode descriptions come from `ParametersSchema`.

Concrete examples:

- `WorldLlmTool.ParametersSchema` lists `action` as required and all other properties optional (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:68-97`). Its MEAI delegate marks `action` as the only non-default parameter and decorates every argument with `[Description]` (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:110-150`). This one is broadly aligned, but its text has already diverged in detail: `ParametersSchema` says `scaleX` is useful for wall/platform sizing (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:84-89`), while the reflected native description is shorter and generic (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:132-137`).
- `ComponentLlmTool.ParametersSchema` marks `action` and `targetName` required (`Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:51-63`). The method has `action` and `targetName` as non-default parameters and the remaining parameters defaulted (`Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:77-99`), so requiredness is aligned today. If either side changes independently, native provider schema and local repair gate will diverge.
- `SceneLlmTool` and `CameraLlmTool` intentionally set `ParametersSchema => "{}"` while exposing multiple real MEAI functions (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:18-27`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CameraLlmTool.cs:17-27`). Native provider schema is still rich because `AIFunctionFactory` sees the actual methods and `[Description]` attributes, but prompt-mode `AiToolContractPromptFormatter` will not list any schema for the logical wrapper tool because the schema is `{}` (`Assets/CoreAI/Runtime/Core/Features/Orchestration/AiToolContractPromptFormatter.cs:103-106`). For LLMUnity specifically, the inner client compensates by appending `AIFunction.JsonSchema` for bound tools (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmUnityMeaiChatClient.cs:124-132`).
- `DelegateLlmTool` always returns `ParametersSchema => "{}"` but creates an `AIFunction` from the supplied arbitrary delegate (`Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:11-23`, `Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:35-42`). Native schema quality and `[Description]` coverage are entirely dependent on the caller-provided delegate; prompt-mode gets no parameter schema from this class.
- `InventoryLlmTool` has a no-parameter schema with `properties: {}` (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryLlmTool.cs:26-35`), and its MEAI function has only a `CancellationToken`, which MEAI excludes from schema (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs:25-38`). This is aligned.

The hazard is real but not uniform. Tools with hand-written schemas and delegate bindings are at risk when edited. Tools with `ParametersSchema => "{}"` avoid drift in required validation, but prompt-mode schema quality is then weaker unless another layer prints `AIFunction.JsonSchema`.

## `[Description]` Attribute Coverage

For native schema quality, meaningful delegate/method parameters should carry `[System.ComponentModel.Description]`, because those descriptions feed the reflected MEAI schema. The hand-written `ParametersSchema` text does not improve the provider-native schema.

Good coverage observed in representative first-party tools:

- `MemoryTool.ExecuteAsync(...)`: all model-visible parameters are described (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs:42-58`).
- `WaitLlmTool.ExecuteAsync(...)`: both model-visible parameters are described (`Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:50-55`).
- `WorldLlmTool` delegate parameters are described (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:110-150`).
- `ComponentLlmTool.ExecuteAsync(...)`: all model-visible parameters are described (`Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:77-99`).
- `SceneLlmTool` methods show descriptions on visible parameters, including optional transform axes (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:68-75`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:213-226`).
- `CameraLlmTool.CaptureCameraAsync(...)`: all visible parameters are described (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CameraLlmTool.cs:37-44`).
- `ReadSkillProxy.Execute(...)`: `skill_name` is described (`Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs:127-140`).
- `LuaTool.ExecuteAsync(...)`: `code` is described (`Assets/CoreAI/Runtime/Core/Features/Orchestration/LuaTool.cs:55-74`).

Coverage gaps / risk areas:

- `DelegateLlmTool` cannot enforce descriptions because it accepts any `Delegate` and passes it directly to `AIFunctionFactory.Create(...)` (`Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:23-42`). Any caller-supplied delegate parameter without `[Description]` will produce a weaker native schema.
- No-description tools with no model-visible parameters are not an issue, for example `InventoryTool.ExecuteAsync(CancellationToken)` (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs:25-38`).

## `AllowDuplicates` Semantics

`ILlmTool.AllowDuplicates` means repeated calls with the same arguments should not be suppressed (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:22-23`). `LlmToolBase` defaults it to `false` (`Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs:68-69`).

Runtime behavior:

- `MeaiLlmClient` resolves a global/request-level `allowDuplicates` flag and passes it to `SmartToolCallingChatClient` or `ToolExecutionPolicy` (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:185-189`, `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:447-451`).
- `ToolExecutionPolicy.CheckDuplicate(...)` returns immediately if the global/request flag allows duplicates (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:140-145`).
- Otherwise it filters out per-tool calls whose matching `ILlmTool.AllowDuplicates` is true; only the remaining calls participate in duplicate-signature blocking (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:147-153`).
- Duplicate signatures are based on tool name plus serialized arguments for the checked calls (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:160-165`).

Concerns:

- `AllowDuplicates => true` disables duplicate protection for that tool entirely, not just "allow multiple distinct calls." Distinct argument sets are already distinct signatures; most tools should keep `false`.
- Several high-impact/action tools set `true`, including `WorldLlmTool` (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:41-47`), `ComponentLlmTool` (`Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:34-40`), `ManageSkillsLlmTool` (`Assets/CoreAI/Runtime/Core/Features/Llm/ManageSkillsLlmTool.cs:36-40`), `WaitLlmTool` (`Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:26-39`), and `ReadSkillProxy` (`Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs:86-97`). Some of these are defensible for repeated polling or repeated world edits, but they increase loop/spam risk because identical retries are not blocked.
- For multi-function wrappers such as `SceneLlmTool` and `CameraLlmTool`, the original logical tool names are `scene_tool` / `camera_tool`, but emitted native function names are `find_objects`, `get_hierarchy`, `capture_camera`, etc. (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:20-64`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CameraLlmTool.cs:19-33`). Duplicate policy looks up `ILlmTool` by `fc.Name` (`Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:147-152`), so per-tool `AllowDuplicates` on the wrapper will not match those expanded function names. Because the fallback behavior checks duplicates when `match == null`, this is conservative for those wrappers, but it means per-function duplicate intent cannot be expressed through the current wrapper-level flag.

## Recommendations

1. Treat `AIFunctionFactory.Create(...)` plus `[Description]` attributes as the native schema source of truth. Do not rely on `ParametersSchema` to improve native provider schema quality.
2. Keep `ParametersSchema` for prompt-only backends and for the current required-argument repair path, but document it as a secondary/compatibility schema rather than the native schema.
3. Add an automated drift check for each `IAIFunctionLlmTool` / `IAIFunctionsLlmTool`: create the `AIFunction`, read `JsonSchema`, parse `ParametersSchema` when non-`{}`, and compare parameter names plus required flags. Type/description comparison can be warning-level because wording may intentionally differ.
4. Consider changing `ToolExecutionPolicy.ValidateRequiredArguments(...)` to read required parameters from the matched `AIFunction.JsonSchema` in `chatOptions.Tools` instead of `ILlmTool.ParametersSchema`. That would align repair validation with the actual function binding used for native execution.
5. For `IAIFunctionsLlmTool` wrappers, consider exposing per-expanded-function duplicate metadata or mapping expanded function names back to their wrapper. Current lookup by `fc.Name` cannot apply wrapper `AllowDuplicates` to expanded function names.
6. For `DelegateLlmTool` authoring APIs, add documentation or validation that caller-provided delegates should annotate all meaningful parameters with `[Description]`. A debug-time schema inspection warning for empty/missing parameter descriptions would catch poor native schemas early.
7. For prompt-mode consistency, consider deriving prompt schemas from `AIFunction.JsonSchema` when a tool implements `IAIFunctionLlmTool` / `IAIFunctionsLlmTool`, falling back to `ParametersSchema` only for `IJsonInvocableLlmTool` or non-MEAI tools. This would remove most duplicated schema maintenance.

## Verification

No `.cs` files were modified. No git commit was run.

Audit verification performed:

- Searched `Assets/CoreAI` and `Assets/CoreAiUnity` for `ParametersSchema`, `CreateAIFunction`, `CreateAIFunctions`, and `AIFunctionFactory.Create(...)`.
- Inspected native request construction through `MeaiLlmClient` and `MeaiOpenAiChatClient`.
- Inspected prompt construction through `AiOrchestrator` and `AiToolContractPromptFormatter`.
- Inspected execution-time validation and duplicate handling through `SmartToolCallingChatClient` and `ToolExecutionPolicy`.
- Checked representative tool implementations for hand-written schema vs reflected delegate schema drift and `[Description]` coverage.
- Checked local `Microsoft.Extensions.AI.Abstractions` 10.7.0 XML documentation for `AIFunctionFactory` / `JsonSchema` behavior.

---


# W2 Execution Pipeline Audit

Scope: tool-call execution pipeline, centered on `ToolExecutionPolicy`, `AiOrchestrator`, `SmartToolCallingChatClient`, Unity `MeaiLlmClient`, and execution-related settings.

## Executive Summary

The non-streaming batch executor is generally well structured: result order is preserved with indexed collation, parallelism is bounded with `SemaphoreSlim`, `MaxParallelToolCalls <= 1` uses the sequential path, per-call timeouts use a per-call linked CTS, and ordinary tool exceptions are isolated into per-call failed results.

The main risks are not in `Task.WhenAll` result ordering. The important gaps are: duplicate detection is batch-level and raw-name based, state-mutating serialization is only per request and cannot protect concurrent orchestrations, streaming uses `MaxToolCallRetries` instead of the configured tool roundtrip cap, and result success detection is intentionally lossy.

## Findings

### F1 - Cross-request state-mutating tools can still race shared memory state

Severity: High

Evidence:
- `ToolExecutionPolicy` serializes `memory`, `manage_mods`, and `manage_skills` only inside one batch/request via `SerializedMutatingToolNames` and `serialChain`: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:692-710`, `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:790-804`.
- A fresh policy is created per non-streaming request: `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs:81-84`.
- A fresh policy is also created per streaming session: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:447-451`.
- The Unity pipeline allows concurrent orchestrations by setting `MaxConcurrentOrchestrations` from settings: `Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs:68-71`.
- `memory append` is a read-modify-write sequence: load at `Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs:104`, build new memory at `Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs:116-121`, then save at `Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs:331`.
- `FileAgentMemoryStore` serializes each public store call, but `TryLoad` and `Save` are separately gated operations: `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs:116-127`, `Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs:184-194`.

Concrete failure scenario:
Two concurrent agent turns for the same role both call `memory append`. Turn A loads memory `M`, turn B loads the same `M`, A saves `M + A`, B saves `M + B`. The store's per-call gate prevents torn file writes, but it does not make the whole load/mutate/save transaction atomic, so one append can be lost.

Recommendation:
Keep the existing same-batch serialization, but add a process-wide keyed mutation gate around each shared mutating store key, or move atomic mutation into the store API, e.g. `Mutate(roleId, Func<AgentMemoryState, AgentMemoryState>)`. Apply the same review to `manage_mods` and `manage_skills`; their file stores use gates, but the policy-level serialization is still request-local.

### F2 - Duplicate suppression does not catch duplicate calls inside the first emitted batch

Severity: Medium

Evidence:
- Duplicate checking builds one whole-batch signature from all checked calls: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:160-177`.
- The signature is added before execution; only a previously seen identical whole batch is rejected: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:177-188`.
- If the batch is accepted, every call in the batch is executed sequentially or concurrently: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:754-758`, `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:795-842`.

Concrete failure scenario:
On the first tool turn, a weak model emits two identical non-`AllowDuplicates` calls in the same batch, such as two identical inventory mutations or two identical config writes. Because no previous batch signature exists yet, the whole batch is accepted and both calls run. Duplicate suppression only catches the next turn if the model emits the same entire batch again.

Recommendation:
Add an intra-batch duplicate pass for tools that do not allow duplicates. Either reject only repeated call signatures inside the batch while preserving result slots, or collapse duplicates into failed `FunctionResultContent` entries before execution. Keep `ILlmTool.AllowDuplicates` as the explicit escape hatch.

### F3 - Duplicate signatures use the raw model-emitted tool name, not the repaired canonical name

Severity: Low

Evidence:
- `CheckDuplicate` matches tools case-insensitively for `AllowDuplicates`, but the signature emits `fc.Name` directly: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:147-175`.
- Name repair happens later inside `ExecuteSingleAsync`: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:264-280`.
- Tool name repair supports case-insensitive correction and returns the canonical registered name: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:237-247`.

Concrete failure scenario:
Turn 1 emits `MEMORY` with args `{action:"append", content:"x"}`. It is repaired and executed as `memory`. Turn 2 emits `memory` with the same args. The raw duplicate signatures differ by casing, so the second call is not suppressed even though both resolved to the same tool and arguments.

Recommendation:
Canonicalize the tool name in `CheckDuplicate` using the matched `ILlmTool.Name` before building signatures. This keeps duplicate detection aligned with the actual execution target.

### F4 - Streaming tool-loop cap ignores `MaxToolCallRoundtrips`

Severity: Medium

Evidence:
- Non-streaming passes `request.MaxToolCallRoundtrips` into `SmartToolCallingChatClient`: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:185-189`.
- `SmartToolCallingChatClient` enforces `_maxRoundtripsOverride ?? _settings.MaxToolCallRoundtrips`: `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs:92-115`.
- `AiOrchestrator` resolves per-call/per-agent roundtrip caps into the completion request: `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrator.cs:1517-1518`.
- The streaming path instead sets `maxToolIterations = Math.Max(1, _settings.MaxToolCallRetries + 1)`: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:436-437`, and stops with `"tool loop exceeded max iterations"` at `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:460-482`.

Concrete failure scenario:
A role is configured with `WithMaxToolCallRoundtrips(0)` or an `AiTaskRequest.MaxToolCallRoundtrips` override for a long build. Non-streaming honors it, but streaming stops after `MaxToolCallRetries + 1` iterations, usually around four if retries default to three. Conversely, changing the global `MaxToolCallRoundtrips` does not affect this streaming loop.

Recommendation:
Use the same roundtrip-resolution semantics in streaming that non-streaming uses: per-call override, then per-agent override already resolved by `AiOrchestrator`, then global setting, with `0` meaning unlimited.

### F5 - `IsToolResultSuccess` can misclassify failures as success

Severity: Medium

Evidence:
- Empty result is treated as success: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:944-947`.
- Any text without the substring `success` is treated as success: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:949-953`.
- Valid JSON only checks top-level boolean `Success`, `success`, or `SUCCESS`; all other shapes return success: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:955-967`.
- Non-JSON fallback only checks exact substrings `"Success":false` and `"success":false`: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:968-972`.
- The result is truncated before success classification: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:410-422`.

Concrete failure scenario:
A tool returns `{"error":"permission denied"}` or `{"ok":false,"error":"not found"}`. The policy classifies it as success because there is no top-level boolean `success`. A plain-text result like `Failed: object not found` is also success because it does not contain `success`. If truncation removes the `success:false` field, a failure can also become success.

Recommendation:
Prefer a typed result contract for built-in tools, or treat common top-level failure keys (`error`, `Error`, `ok:false`, `Succeeded:false`) as failures. Run success classification before truncation, and keep a compatibility fallback only for legacy free-text tools.

### F6 - Duplicate suppression can hide all calls in a repeated mixed batch, including calls that were not the duplicate cause

Severity: Low

Evidence:
- The duplicate signature excludes `AllowDuplicates` tools from the signature: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:147-157`.
- Once the reduced signature repeats, the code returns an error result for every original call in the batch, not just the checked duplicate calls: `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:177-185`.
- `world_command` explicitly sets `AllowDuplicates => true` for repeated actions such as `apply_force`: `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:43-47`.

Concrete failure scenario:
Turn 1 emits `[read_config(A), world_command(apply_force)]`; `world_command` is excluded from the duplicate signature. Turn 2 emits the same `read_config(A)` plus a different legitimate `world_command(apply_force)`. The repeated reduced signature blocks the whole batch, so the legitimate force application is also suppressed.

Recommendation:
When a repeated reduced signature is detected, return duplicate errors only for the duplicated non-`AllowDuplicates` calls and still execute the `AllowDuplicates` calls, preserving result order. Alternatively, make the duplicate policy explicitly reject the whole mixed batch and document that behavior.

## Concern-by-Concern Assessment

1. Parallel execution: PASS with caveats. `MaxParallelToolCalls` is clamped to at least 1 (`ToolExecutionPolicy.cs:745`), `1` or single-call batches use the sequential path (`ToolExecutionPolicy.cs:747-784`), parallel execution is bounded by `SemaphoreSlim` (`ToolExecutionPolicy.cs:786-842`), and results are collated from an indexed array in original call order (`ToolExecutionPolicy.cs:844-878`). Existing tests cover order and sequential behavior (`Assets/CoreAiUnity/Tests/EditMode/ToolExecutionPolicyEditModeTests.cs:837-911`).

2. State-mutating tool serialization: PARTIAL. Same-batch mutating built-ins are serialized (`ToolExecutionPolicy.cs:692-710`, `ToolExecutionPolicy.cs:790-804`), but cross-request shared-store races remain; see F1.

3. Per-call timeout: PASS. Each `ExecuteSingleAsync` creates its own linked CTS and `CancelAfter` (`ToolExecutionPolicy.cs:363-370`). A per-call timeout is converted to that call's failed result only when the outer token was not cancelled (`ToolExecutionPolicy.cs:380-394`). Siblings are not cancelled by that local CTS.

4. Duplicate-batch rejection and `AllowDuplicates`: PARTIAL. `AllowDuplicates` is respected when forming the duplicate signature (`ToolExecutionPolicy.cs:147-157`), and `apply_force` is under `world_command`, which allows duplicates (`WorldLlmTool.cs:43-47`). Problems remain for intra-batch duplicates, raw-name signatures, and mixed batches; see F2, F3, and F6.

5. Consecutive-error counter / forced-tool reset: MOSTLY PASS. Sequential and parallel batches update the error counter exactly once after batch execution (`ToolExecutionPolicy.cs:769-776`, `ToolExecutionPolicy.cs:861-870`). The non-streaming loop resets forced tool mode to Auto after any tool call so required-tool mode does not loop forever (`SmartToolCallingChatClient.cs:127-133`), and the streaming path does the same after the first iteration (`MeaiLlmClient.cs:485-494`). No `IsRunning` wedge was found in this layer; UI busy cleanup uses `finally` blocks, and queued streaming decrements `_inFlight` in `finally` (`QueuedAiOrchestrator.cs:231-240`).

6. Cancellation: PASS. Outer cancellation reaches every in-flight call via the shared cancellation token (`ToolExecutionPolicy.cs:828-832`), `Task.WhenAll` is awaited without swallowing cancellation (`ToolExecutionPolicy.cs:841-842`), and `ExecuteSingleAsync` rethrows outer cancellation (`ToolExecutionPolicy.cs:461-465`). `AiOrchestrator.RunTaskAsync` also rethrows `OperationCanceledException` rather than converting it to null/success (`AiOrchestrator.cs:339-349`). Queued streaming maps cancellation to a terminal `"cancelled"` chunk, not success (`QueuedAiOrchestrator.cs:221-225`).

7. `IsToolResultSuccess` heuristic: FAIL for strict correctness. See F5.

8. Exception isolation: PASS for ordinary tool exceptions. `ExecuteSingleAsync` catches non-cancellation exceptions and converts them to a failed `FunctionResultContent` for that call (`ToolExecutionPolicy.cs:467-477`). The batch waits for all tasks and collates by index, so one thrown tool does not corrupt sibling result order (`ToolExecutionPolicy.cs:831-842`, `ToolExecutionPolicy.cs:844-878`).

## Verification Performed

Read-only audit only. No `.cs` files were modified. No Unity tests were run because the requested task was an evidence-based audit and the only repository change requested was this Markdown report.

---


# W3 Parsing and Streaming Robustness Audit

Scope: read-only audit of tool-call parsing and streaming behavior for small local models. Source files inspected:

- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs`
- Related parser/execution files found by grep: `ToolExecutionPolicy.cs`, `LlmToolCallTextExtractor.cs`, `ThinkBlockStreamFilter.cs`.

No C# source changes were made.

## Executive Summary

The native SSE path is reasonably conservative for normal OpenAI-style streams: it accumulates `delta.tool_calls` by `index`, appends argument fragments, parses only once at flush, and fail-closes malformed streamed native arguments through `__parse_error`. The weaker areas are local-model text-shaped tool calls: invalid or truncated JSON can be held during streaming and then emitted visibly at stream end, text-shaped tool calls inside `<think>` are stripped before extraction, and the hybrid scanner repeatedly rescans the full accumulated text per delta.

Tool-name repair is intentionally narrow: exact ordinal match first, then first case-insensitive match. I found no near-miss / typo / Levenshtein mapping in the inspected path. This avoids arbitrary typo misrouting, but case-only duplicate tool names would be ambiguous and can route to whichever tool appears first.

## Findings

### 1. High - Invalid or truncated text-shaped tool JSON can leak to visible streaming output at turn end

Evidence:

- `MeaiLlmClient` processes streamed text through `ThinkBlockStreamFilter`, appends it to `iterationVisible`, and uses hybrid hold when tools are declared (`MeaiLlmClient.cs:496-505`, `623-645`).
- During the stream, `DrainHybridSafeSegments` holds output from an incomplete `{...}` object (`MeaiLlmClient.cs:531-581`).
- At turn end, text-shaped extraction runs on the full `visibleText` only if `TryExtractToolCallsFromText` succeeds (`MeaiLlmClient.cs:759-766`).
- If extraction fails and some suffix is still held, the remaining raw text is sanitized only for prompt echo and then emitted (`MeaiLlmClient.cs:891-923`). `SanitizeAssistantVisibleText` only strips leading system-prompt echo (`MeaiLlmClient.cs:1131-1145`).
- `TryExtractToolCallsFromText` requires balanced candidates and silently ignores parse failures (`MeaiLlmClient.cs:1166-1175`, `1198-1229`, `1232-1235`).

Concrete repro/failure scenario:

1. Tools are declared and the local model streams:
   `{"name":"memory","arguments":{"action":"write","content":"abc"` and then stops.
2. Hybrid hold suppresses this while incomplete.
3. At stream end, `TryExtractToolCallsFromText` finds no balanced valid tool object.
4. The final `else if (hybridToolJsonHold && hybridRawExclusiveEndEmitted < visibleText.Length)` emits the held raw JSON suffix to the user.

The same can happen with balanced but invalid argument JSON, for example `{"name":"read_skill","arguments":"{\"skill_name\":\"Crafting\"}"}` if the inner string is malformed, or with trailing commas/unescaped quotes that make `JObject.Parse`/`JsonConvert.DeserializeObject` throw. The catch blocks skip the match, and the final visible-text path can still emit the raw object.

Recommendation:

For streams with tools declared, fail closed for held JSON-looking tails. If the held suffix begins at an incomplete brace or contains a balanced object with `"name"` and `"arguments"`/`"arguments_json"` but parsing fails, do not emit it as user text. Instead emit a tool parse-error chunk or a retry instruction, mirroring the native SSE `__parse_error` handling.

### 2. Medium - Native SSE accumulator keys only by `index`, so missing/reused indexes can merge unrelated tool calls

Evidence:

- SSE parsing defaults missing `index` to `0` (`MeaiOpenAiChatClient.cs:1315-1323`).
- `SseToolCallAccumulator` stores pending calls in `Dictionary<int, PendingToolCall>` keyed only by index (`MeaiOpenAiChatClient.cs:1496`, `1510-1531`).
- Later non-empty `id` and `name` overwrite the same pending entry, while all argument fragments append to the same buffer (`MeaiOpenAiChatClient.cs:1518-1530`).
- Flush emits one call per index in ascending order (`MeaiOpenAiChatClient.cs:1544-1569`).

Concrete repro/failure scenario:

If a local OpenAI-compatible server omits `index` for multiple parallel tool calls, both default to index `0`. The second call overwrites `Id`/`Name`; arguments from both calls are concatenated, producing malformed JSON or a wrong tool with mixed arguments. A server that restarts an index after a completed call in the same assistant stream would have the same problem because the accumulator does not split on new `id`.

This is not an issue for compliant OpenAI-style deltas that consistently use stable indexes per assistant message, but small local servers are a realistic risk.

Recommendation:

Use a composite key when possible: prefer stable `id`, fall back to `index`, and detect `id` changes on an existing index before appending. Missing indexes should be treated as suspicious when more than one pending call exists, with a warning and fail-closed behavior rather than silent merging.

### 3. Medium - Malformed SSE data lines are swallowed silently, which can drop partial tool-call chunks

Evidence:

- Each physical line is passed to `ParseSseUpdates(line + "\n", toolAccumulator)` (`MeaiOpenAiChatClient.cs:394`).
- `ParseSseUpdates` splits raw input by newline and parses each `data:` line independently (`MeaiOpenAiChatClient.cs:1230-1258`).
- `ExtractDeltaUpdate` catches all exceptions and returns null (`MeaiOpenAiChatClient.cs:1286-1355`).

Concrete repro/failure scenario:

If a nonstandard local SSE server emits a single JSON event split over multiple `data:` lines, or emits a transient truncated JSON line during tool-call arguments, each fragment fails `JObject.Parse` and is dropped. Because the failed line may be the one carrying `function.name`, `id`, or an argument fragment, flush can drop the tool call as missing name or produce malformed arguments.

Recommendation:

Maintain an SSE event buffer according to the SSE spec: collect all `data:` lines for one event until a blank line, join them, then parse once. Log parse failures with enough context and count them as stream integrity failures when tool calls are pending.

### 4. Medium - Text-shaped tool calls inside `<think>` are stripped before extraction, so local-model tool calls can be lost

Evidence:

- Streaming creates `ThinkBlockStreamFilter` before collecting visible text (`MeaiLlmClient.cs:496-499`).
- Each raw text delta is filtered first, and only the returned `visible` text is appended to `iterationVisible` and scanned for tool JSON (`MeaiLlmClient.cs:617-645`).
- `ThinkBlockStreamFilter` suppresses content between split-safe `<think>` and `</think>` tags (`ThinkBlockStreamFilter.cs:31-57`, `73-82`, `123-140`).
- Text-shaped extraction runs only on `visibleText`, not the pre-filter raw stream (`MeaiLlmClient.cs:681`, `759-766`).

Concrete repro/failure scenario:

A small local model emits:

`<think>I should call memory. {"name":"memory","arguments":{"action":"write","content":"x"}}</think> Done.`

The filter removes the entire think block before extraction. The tool call is never executed, and the final answer may say "Done" even though memory was not written. Split tags such as `<thi` + `nk>` are handled by the filter, so the loss is not caused by tag fragmentation itself; it is caused by extraction happening after hidden-thought removal.

Recommendation:

Decide the contract explicitly. If tool calls inside reasoning blocks must be ignored, add diagnostics so this is visible. If local models often place tool calls there, run a tool-call-only scanner on raw text before dropping the think block, but never stream the raw think text to UI.

### 5. Medium - Case-insensitive tool-name repair can route to the wrong tool when registered names differ only by case

Evidence:

- Exact ordinal name match returns as-is (`ToolExecutionPolicy.cs:232-235`).
- If exact match fails, the first case-insensitive match in `_originalTools` is used (`ToolExecutionPolicy.cs:237-246`).
- Unknown names are rejected; there is no near-miss repair (`ToolExecutionPolicy.cs:249-252`).
- Duplicate checks also use case-insensitive matching and ordering (`ToolExecutionPolicy.cs:147-153`, `160-174`).

Concrete repro/failure scenario:

If two tools are registered as `ReadFile` and `readfile`, and the model emits `READFILE`, `TryRepairToolName` maps to whichever appears first in `_originalTools`. The model intended one of two distinct tools, but the repair picks without detecting ambiguity. Similar ambiguity affects duplicate filtering because names are compared case-insensitively.

Recommendation:

Reject tool catalogs containing names that collide under `StringComparer.OrdinalIgnoreCase`, or make `TryRepairToolName` fail closed when more than one case-insensitive match exists. Keep rejecting true near-miss typos unless an explicit, ambiguity-aware distance policy is added.

### 6. Medium - Text-mode malformed arguments are fail-open to visible text, while native SSE malformed arguments are fail-closed

Evidence:

- Native streamed arguments are parsed once in `SseToolCallAccumulator.ParseArguments`; malformed JSON returns `__raw_arguments` plus `__parse_error` markers (`MeaiOpenAiChatClient.cs:1581-1609`).
- `ToolExecutionPolicy` detects `__parse_error` and returns a retry instruction without invoking the tool (`ToolExecutionPolicy.cs:282-299`, `492-505`).
- Text-mode extraction catches parse errors and skips malformed matches (`SmartToolCallingChatClient.cs:346-367`; `MeaiLlmClient.cs:1198-1229`, `1264-1277`).
- If no tool calls survive extraction, non-streaming returns the original response as plain text (`SmartToolCallingChatClient.cs:175-218`); streaming can emit the held suffix as described in Finding 1.

Concrete repro/failure scenario:

A local model returns a text-shaped call with unescaped quotes:

`{"name":"memory","arguments":{"action":"write","content":"player said "go north""}}`

The native SSE path would mark malformed arguments and ask the model to retry. The text-shaped path drops the match and can expose the raw JSON as assistant text, or treat the turn as normal text.

Recommendation:

Unify the behavior: when text contains a likely tool-call object but parsing fails, surface a parse-error pseudo tool result or retry instruction instead of falling back to visible assistant text.

### 7. Low - Provider-native `reasoning_content` deltas are dropped in streaming, but non-streaming can expose reasoning as visible fallback

Evidence:

- Streaming reads `deltaObj["reasoning_content"]` and discards it (`MeaiOpenAiChatClient.cs:1305-1310`).
- Streaming returns only `delta.content`, `message`, or `text` as visible updates (`MeaiOpenAiChatClient.cs:1310-1347`).
- Non-streaming first uses `content`, then falls back to `reasoning_content`, `reasoningContent`, or `reasoning` as visible text if content is empty (`MeaiOpenAiChatClient.cs:1187-1211`).

Concrete repro/failure scenario:

For a provider that streams answer text or tool-shaped JSON only in `reasoning_content`, the streaming path drops it completely. For the same provider in non-streaming mode, reasoning can become user-visible if `content` is empty. This is inconsistent and could either lose output/tool intent in streaming or leak reasoning in non-streaming.

Recommendation:

Make reasoning handling policy consistent: either always discard provider-native reasoning fields, or expose them only to an internal diagnostics channel. Do not use reasoning fields as assistant-visible fallback unless that is an explicit provider compatibility mode.

### 8. Low - Hybrid streaming scanner repeatedly rescans the full accumulated buffer and can become O(n^2)

Evidence:

- Each visible delta appends to `iterationVisible`, then calls `DrainHybridSafeSegments(iterationVisible.ToString())` (`MeaiLlmClient.cs:629-645`, `655-674`).
- `DrainHybridSafeSegments` calls `GetHybridSafeSegments(full, out safeEnd)` (`MeaiLlmClient.cs:537-540`).
- `GetHybridSafeSegments` strips code blocks, scans for the first incomplete brace, and scans for all tool-call JSON spans on the full text (`MeaiLlmClient.cs:1538-1557`).
- `GetFirstIncompleteBraceStart` and `FindToolCallJsonSpans` both walk the string (`MeaiLlmClient.cs:1590-1661`, `1354-1428`).

Concrete repro/failure scenario:

A small local model streams a long answer one token at a time while tools are declared. On every delta, the code allocates `iterationVisible.ToString()`, regex-strips code blocks, scans braces, and finds spans from the beginning. For a 50k-character response in small chunks, this is quadratic work and allocation-heavy. A malicious or buggy model can emit a long sequence with many unmatched `{` characters to maximize rescans and delay the UI.

Recommendation:

Make the hybrid scanner incremental. Track the last scanned offset, pending brace start, string state, and hidden span ranges. At minimum, cap the held buffer length for tool-call detection and fail closed once a likely tool-call object exceeds a sane maximum.

## Non-Findings / Positive Controls

- Normal OpenAI-style native SSE argument fragmentation is not parsed per delta. Fragments are appended and parsed only at flush (`MeaiOpenAiChatClient.cs:1510-1531`, `1581-1596`), so ordinary partial JSON arguments across many small deltas should not be mis-parsed mid-stream.
- Native SSE malformed arguments fail closed before tool invocation via `__parse_error` (`MeaiOpenAiChatClient.cs:1605-1609`; `ToolExecutionPolicy.cs:282-299`).
- Tool-name repair does not perform near-miss typo mapping. Unknown names are rejected after exact and case-insensitive checks (`ToolExecutionPolicy.cs:232-252`), so arbitrary typo-to-wrong-tool repair was not found in the inspected code.
- Split `<think>` and `</think>` tags are handled by `ThinkBlockStreamFilter` through buffered partial-tag preservation (`ThinkBlockStreamFilter.cs:27-30`, `85-107`, `146-182`). The risk is not split tags leaking by themselves; the risk is that tool calls placed inside hidden-thought text are intentionally removed before extraction.

---


# W4 Per-Tool Correctness Audit

Scope: read-only audit of concrete LLM tool files listed in the request. Focus areas:

- `[System.ComponentModel.Description]` coverage on MEAI delegate/native parameters.
- Agreement between hand-written `ParametersSchema` and actual `CreateAIFunction` / `InvokeJson` parameters.
- Argument validation and error handling.
- Correctness bugs or misleading descriptions.

No `.cs` source files were modified.

## Summary Table

| Tool | Description-attr coverage | ParametersSchema vs native schema consistent? | Issues found |
|---|---:|---:|---|
| `WorldLlmTool.cs` | all | yes | Refactored action set is mostly aligned, including `spawn`, `change`, `set_color`, `x/y/z`, `fx/fy/fz`, `scale/scaleX/scaleY/scaleZ`; however `apply_force`/`set_velocity` accept omitted vector components despite error text claiming components are required. |
| `ComponentLlmTool.cs` | all | yes | Required set is intentionally broad (`action`, `targetName`) while per-action validation is runtime; no schema mismatch found. |
| `CameraLlmTool.cs` | all | no | Multi-function tool publishes aggregate `ParametersSchema => "{}"` while actual native function `capture_camera` has `cameraName`, `width`, `height`. Runtime validation/clamping is good. |
| `SceneLlmTool.cs` | all | no | Multi-function tool publishes aggregate `ParametersSchema => "{}"` while actual native functions have parameters. `find_objects` can throw on null `searchMethod`/`searchTerm`; `set_transform` accepts a no-op call with only `instanceId`. |
| `LuaLlmTool.cs` | all via `LuaTool` | yes | Wrapper schema matches underlying `LuaTool` native `code` parameter. Constructor defers null dependency failure until `CreateAIFunction`. |
| `LuaModsLlmTool.cs` | all | yes | Schema and native signature agree. Validation is per action and generally strong. |
| `MemoryLlmTool.cs` | all via `MemoryTool` | yes | File is schema-only; native binding lives in `MemoryTool.cs`. Schema agrees with the executable tool. |
| `InventoryLlmTool.cs` | all / none needed | yes | Zero-argument native function agrees with `{}` schema. Constructor does not fail fast on null provider. |
| `ReadSkillLlmTool.cs` | all | yes | Schema and native signature agree. Missing/unknown skill handling is explicit. |
| `CallSkillToolLlmTool.cs` | all | yes | Schema and native signature agree. `arguments_json` is required by schema but execution tolerates null as `{}`, which is safe but slightly stricter in schema than runtime. |
| `ManageSkillsLlmTool.cs` | all | yes | Schema and native signature agree. Description says `tool_names[]`, but native/schema actually take a string containing JSON array or CSV. |
| `DelegateLlmTool.cs` | depends on supplied delegate | no | Always reports `ParametersSchema => "{}"` even when the supplied delegate has parameters; delegate parameter Description coverage is not enforceable here. |
| `WaitLlmTool.cs` | all | yes | Schema and native signature agree. Runtime clamps over-max waits instead of rejecting them despite "at most" wording. |
| `GameConfigLlmTool.cs` | all via `GameConfigTool` | yes | Schema and native signature agree. Wrapper description omits the available-key detail present in the native `GameConfigTool` description. |
| `CompatibilityLlmTool.cs` | all | mostly | Native function expects `string[] ingredients`; schema says array but lacks item type. Tool description says comma-separated list, which conflicts with the native/schema array contract even though a non-native overload can parse strings. |

## Findings

### 1. Medium - `world_command` silently accepts zero-vector physics calls that its validation says are missing required components

Evidence:

- `apply_force` and `set_velocity` are documented as requiring force/velocity components in missing-parameter messages (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:630`, `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:633`).
- `CreateApplyForceCommand` only checks `targetName`, then sends `new Vector3(x ?? 0f, y ?? 0f, z ?? 0f)` (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:500`).
- `CreateSetVelocityCommand` has the same target-only validation and sends `new Vector3(fx ?? 0f, fy ?? 0f, fz ?? 0f)` (`Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:566`).

Failure scenario:

The model calls `world_command(action="apply_force", targetName="Ball")` with no `fx/fy/fz`. The call succeeds far enough to execute a zero force instead of returning the advertised missing-parameters error. For `set_velocity`, this can accidentally stop an object by setting velocity to zero.

Recommendation:

Require at least one of `fx`, `fy`, or `fz` for these two actions, or update the descriptions/messages to state that omitted components default to zero and a target-only call is meaningful.

### 2. Medium - `SceneLlmTool.ParametersSchema` is `{}` even though it exposes four concrete native functions with parameters

Evidence:

- The aggregate tool schema is `{}` (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:23`).
- `CreateAIFunctions` exposes `find_objects(searchTerm, searchMethod, includeInactive)`, `get_hierarchy(rootInstanceId)`, `get_transform(instanceId)`, and `set_transform(instanceId, px/py/pz, rx/ry/rz, sx/sy/sz)` (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:25`).
- The function parameters themselves have `[Description]` coverage (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:68`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:122`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:179`, `Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:213`).

Failure scenario:

Any path that reads only `ILlmTool.ParametersSchema` sees `scene_tool` as a no-argument tool, even though `read_skill` / MEAI function descriptors can expose the per-function native schemas. This creates inconsistent tool documentation depending on the discovery path.

Recommendation:

Either document that aggregate schemas for `IAIFunctionsLlmTool` are intentionally empty and never used for invocation, or replace `{}` with an explicit aggregate note/schema. Prefer using the generated `AIFunction.JsonSchema` wherever multi-function tools are surfaced.

### 3. Medium - `CameraLlmTool.ParametersSchema` is `{}` even though `capture_camera` has concrete parameters

Evidence:

- The aggregate schema is `{}` with a comment saying it is managed by `AIFunctionFactory` (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CameraLlmTool.cs:22`).
- The actual native function has `cameraName`, `width`, and `height`, all described (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CameraLlmTool.cs:37`).

Failure scenario:

Same as `SceneLlmTool`: schema consumers that do not expand `CreateAIFunctions` see no parameters, while the native MEAI function does have parameters.

Recommendation:

Keep the native function path, but avoid presenting `{}` as the authoritative schema for the concrete `capture_camera` tool in any user/model-visible skill or diagnostic surface.

### 4. Medium - `DelegateLlmTool` cannot keep `ParametersSchema` truthful for parameterized delegates

Evidence:

- `ParametersSchema` is hard-coded to `{}` (`Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:19`).
- The actual native binding is created from arbitrary `ActionDelegate` (`Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:35`).
- JSON invocation routes through the generated MEAI function (`Assets/CoreAI/Runtime/Core/Features/Llm/DelegateLlmTool.cs:48`).

Failure scenario:

A skill registers a `DelegateLlmTool` wrapping `Func<string, Task<string>>`. Native invocation requires a string parameter, but the `ILlmTool.ParametersSchema` says `{}`. Any prompt, diagnostic, or skill catalog path using the hand-written schema misleads the model and user.

Recommendation:

Add a constructor overload that accepts an explicit schema, or derive `ParametersSchema` from the generated `AIFunction.JsonSchema` with caching. Also document that delegate parameter `[Description]` attributes must be present on the supplied delegate/method if the native schema should be self-describing.

### 5. Medium - `find_objects` lacks null-safe validation for `searchMethod` and `searchTerm`

Evidence:

- `FindObjectsAsync` accepts `string searchTerm` and `string searchMethod = "name"` (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:68`).
- The loop calls `searchMethod.Equals(...)` and `go.name.Contains(searchTerm)` without null normalization (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:88`).

Failure scenario:

If a JSON caller passes `null` for `searchMethod`, the method throws `NullReferenceException`. If `searchTerm` is null and `searchMethod` is `name`, `Contains(null)` throws. The catch returns JSON error, but the error is accidental and less useful than validation.

Recommendation:

Normalize `searchMethod` to `"name"` when blank/null and return a clear validation error for missing `searchTerm`, or treat blank search as "list all" explicitly.

### 6. Low - `set_transform` accepts no-op calls as successful

Evidence:

- `set_transform` has required `instanceId` and all transform fields optional (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:213`).
- The implementation always returns `"Transform updated successfully."` even if no optional field was provided (`Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/SceneLlmTool.cs:288`).

Failure scenario:

The model calls `set_transform(instanceId=123)` expecting a transform change, and receives success even though nothing changed.

Recommendation:

Return a validation error when none of `px/py/pz/rx/ry/rz/sx/sy/sz` is supplied, or change the message to state that no fields were changed.

### 7. Low - `CompatibilityLlmTool` description conflicts with its native array parameter

Evidence:

- Tool description says "Provide ingredient names as a comma-separated list" (`Assets/CoreAI/Runtime/Core/Features/Crafting/CompatibilityLlmTool.cs:31`).
- Hand-written schema defines `ingredients` as an array (`Assets/CoreAI/Runtime/Core/Features/Crafting/CompatibilityLlmTool.cs:35`).
- Native MEAI function is `Func<string[], ...>` and the parameter is `string[] ingredients` (`Assets/CoreAI/Runtime/Core/Features/Crafting/CompatibilityLlmTool.cs:45`, `Assets/CoreAI/Runtime/Core/Features/Crafting/CompatibilityLlmTool.cs:54`).

Failure scenario:

Native tool-calling models may follow the description and send `"IronOre, FireStone"` instead of `["IronOre","FireStone"]`. The public object overload can parse a string, but the actual MEAI binding is the `string[]` overload, so native argument binding may reject or mis-bind the string.

Recommendation:

Make the description match the native/schema contract: request a JSON array of ingredient names. If string CSV is still desired, expose that in the native signature/schema intentionally.

### 8. Low - `WaitLlmTool` says over-max waits are invalid but clamps them successfully

Evidence:

- Schema says seconds "Must be greater than 0 and at most" the configured max (`Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:32`).
- Runtime rejects non-positive/NaN/infinity, but clamps over-max values with `Math.Min(seconds, _maxSeconds)` and returns success (`Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:57`, `Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs:66`).

Failure scenario:

The model calls `wait(seconds=999)`. The tool succeeds after the maximum wait, even though the schema says the argument is out of range.

Recommendation:

Either reject values greater than `_maxSeconds` or update schema/description to say values above the max are clamped.

### 9. Low - Wrapper constructors defer null dependency failures

Evidence:

- `LuaLlmTool` stores `executor`, `settings`, and `logger` without null checks, then the underlying `LuaTool` throws only when `CreateAIFunction` is called (`Assets/CoreAI/Runtime/Core/Features/Orchestration/LuaLlmTool.cs:17`, `Assets/CoreAI/Runtime/Core/Features/Orchestration/LuaTool.cs:43`).
- `InventoryLlmTool` stores `provider` without null checks, then the underlying `InventoryTool` throws only when `CreateAIFunction` is called (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryLlmTool.cs:13`, `Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs:19`).

Failure scenario:

An invalid tool instance can be registered successfully and only fail later during native function creation, which makes the registration error harder to diagnose.

Recommendation:

Mirror the underlying tools' `ArgumentNullException` checks in the LLM wrapper constructors.

## Notes On Tools With No Findings

- `ComponentLlmTool.cs`: schema and native signature agree; all parameters are described. Per-action requirements are enforced after `action` normalization.
- `LuaModsLlmTool.cs`: schema and native signature agree; all parameters are described; per-action validation is explicit.
- `MemoryLlmTool.cs`: this file only publishes the schema. The executable native binding in `MemoryTool.cs` has matching parameters and `[Description]` coverage.
- `ReadSkillLlmTool.cs`: schema and native signature agree; all parameters are described.
- `CallSkillToolLlmTool.cs`: schema and native signature agree; all parameters are described; invalid JSON and unknown tool names return structured failures.
- `ManageSkillsLlmTool.cs`: schema and native signature agree; all parameters are described. The prose `tool_names[]` wording is shorthand; the actual field is a string containing JSON array or comma-separated names.
- `GameConfigLlmTool.cs`: wrapper schema agrees with `GameConfigTool` native parameters. The native function description is richer because it includes allowed keys.

---

