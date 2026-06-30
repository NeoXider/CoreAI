# Authoring LLM Tools (`ILlmTool`)

A practical guide for adding a new tool the model can call. Read [TOOL_CALL_SPEC.md](./TOOL_CALL_SPEC.md)
first for the wire format and design principles; this page is about getting parameter **descriptions** to
the model correctly — the single most common authoring mistake.

## How a tool reaches the model: two paths

A tool is described to the model on one of two paths, chosen by the backend:

- **Native tool-calling path** (LM Studio, OpenAI-compatible servers, most modern providers). The JSON Schema
  for each tool is generated from the C# **delegate signature** by `AIFunctionFactory.Create(delegate, options)`,
  exposed as `AIFunction.JsonSchema`, and forwarded **verbatim** as the tool `parameters` in the request body
  (`MeaiOpenAiChatClient.BuildToolsPayload`, `MeaiOpenAiChatClient.cs:1800` — `af.JsonSchema` is serialized
  straight into the payload). **Parameter descriptions reach the model only if the delegate parameters carry
  `[System.ComponentModel.Description("...")]` attributes** — that is where `AIFunctionFactory` reads them from.

- **Text-shaped path** (older / non-native backends). Here the tool contract is injected into the system prompt
  by `AiToolContractPromptFormatter.AppendToolContract`. This is the **only** consumer of a tool's
  `ParametersSchema` string: it prints `schema: <ParametersSchema>` per tool
  (`AiToolContractPromptFormatter.cs:103-107`).

Crucially, `AppendToolContract` **early-returns for native tool-calling**
(`AiToolContractPromptFormatter.cs:71-75`) — it returns *before* the `Available tools:` loop that would emit
`ParametersSchema`. So on the native path, your `ParametersSchema` text is **never sent**.

> **The single most important rule:**
> **Every meaningful delegate parameter MUST carry `[Description(...)]`. The `ParametersSchema` string alone
> does NOT reach native tool-calling — only the `[Description]` attributes do.**

## Required members (`ILlmTool` / `LlmToolBase`)

From `ILlmTool.cs`:

| Member | Source | Notes |
|--------|--------|-------|
| `Name` | abstract | Tool name the model emits. Self-explanatory (e.g. `world_command`). |
| `Description` | abstract | One short paragraph; list the actions. Reaches the model on **both** paths. |
| `ParametersSchema` | virtual, default `"{}"` | Text-path only. Keep in sync with the attributes. `JsonParams(...)` helper builds it. |
| `AllowDuplicates` | virtual, default `false` | `false` is correct for almost every tool — see below. |
| `CreateAIFunction()` | `IAIFunctionLlmTool` | Builds the `AIFunction` via `AIFunctionFactory.Create`. |

Implement `IAIFunctionLlmTool` for one function, or `IAIFunctionsLlmTool` when one tool exposes several
functions. CoreAI does **not** discover `CreateAIFunction()` by reflection — implement the explicit contract.

## Minimal template for a new tool

`WorldLlmTool.cs` is the canonical, correct-after-the-fix example (`[Description]` on every `ExecuteAsync`
parameter, a `ParametersSchema` kept in sync, and `CreateAIFunction()` using `AIFunctionFactory.Create`).
A trimmed skeleton:

```csharp
using System.ComponentModel;          // [Description]
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

public sealed class WeatherLlmTool : LlmToolBase, IAIFunctionLlmTool
{
    public override string Name => "weather_command";

    // Reaches the model on BOTH paths. Keep it short; list the actions.
    public override string Description =>
        "Read or change in-game weather. Actions: get, set. " +
        "Use 'set' with a 'preset' (clear, rain, storm, fog).";

    // false unless repeated identical calls are genuinely meaningful. See the checklist.
    public override bool AllowDuplicates => false;

    // TEXT-path only. Keep these descriptions in sync with the [Description] attributes below.
    public override string ParametersSchema => JsonParams(
        ("action", "string", true,  "Command: get, set"),
        ("preset", "string", false, "For 'set': clear, rain, storm, fog"));

    // The delegate signature IS the native JSON Schema. [Description] is what reaches the model.
    public async Task<string> ExecuteAsync(
        [Description("Command: get, set")]
        string action,
        [Description("For 'set': clear, rain, storm, fog")]
        string preset = null,
        CancellationToken cancellationToken = default)
    {
        // ... do the work, return a serializable result string ...
        return "{\"success\":true}";
    }

    public AIFunction CreateAIFunction()
    {
        Func<string, string, CancellationToken, Task<string>> func = ExecuteAsync;
        AIFunctionFactoryOptions options = new() { Name = Name, Description = Description };
        return AIFunctionFactory.Create(func, options);
    }
}
```

The trailing `CancellationToken` is bound automatically and is not exposed to the model — it needs no
`[Description]`. Every other parameter does.

## New-tool checklist

- [ ] **`[Description]` on every meaningful delegate parameter.** This is the only thing that reaches the
      native path. A bare `float fx = 0f` with no attribute shows up to the model as an unlabeled number it
      will not know how to use.
- [ ] **Keep `ParametersSchema` text in sync** with the attributes (same wording). The text path reads
      `ParametersSchema`; the native path reads the attributes. If they drift, the two backends describe the
      tool differently.
- [ ] **First parameter is the action**, and its `[Description]` lists the **full action enum** in plain text
      (e.g. `"Command: get, set"`). This is the model's map of what the tool can do.
- [ ] **Concise `Description`** that names the actions. It is the only per-tool text that reaches both paths.
- [ ] **Pick `AllowDuplicates` correctly** — almost always leave it `false` (the default). See the next section
      so you do not set it `true` "to allow many calls".
- [ ] `using System.ComponentModel;` is present (for `[Description]`).
- [ ] Implement `IAIFunctionLlmTool` (or `IAIFunctionsLlmTool`); build the function with
      `AIFunctionFactory.Create`.

### Understanding `AllowDuplicates` (args-aware dedup)

`ToolExecutionPolicy.CheckDuplicate` (`ToolExecutionPolicy.cs:140`) suppresses a tool call only when an
**identical** call already ran in this turn. The dedup key is **tool name + canonicalized arguments** —
`$"{fc.Name}({argsSig})"` (`ToolExecutionPolicy.cs:160-177`), where `argsSig` is the key-sorted serialization
of the arguments. A tool whose `AllowDuplicates => true` is excluded from the check entirely
(`ToolExecutionPolicy.cs:152`).

The key insight: **distinct arguments produce distinct keys, so they are never duplicates.** Spawning ten
cubes at ten different positions is ten different calls and all run. You do **not** need `AllowDuplicates =>
true` to "allow many calls" — you only need it if the *same call with the same arguments* should run more than
once (genuinely rare; a truly idempotent-to-repeat action). Leaving the default `false` is correct for nearly
every tool, and is essential for spammy spawn-style tools: it stops a model from looping on the exact same
spawn forever.

## Common pitfalls

**1. The description-less native schema (the rotation/scale bug — worked example).**
`world_command` exposes `fx/fy/fz` (rotation degrees) and `scale` for inline use on `spawn`. The
`ParametersSchema` explained all of them clearly — but on the native path `ParametersSchema` is never sent
(early-return above), and the delegate parameters had **no `[Description]`**. So the model saw `fx`, `fy`,
`fz`, `scale` as bare unlabeled numbers and simply never used them: every spawned object came out axis-aligned
and default-sized. The fix was to add `[Description]` to every `ExecuteAsync` parameter — across ~13 tools —
so the native schema actually describes them (see `WorldLlmTool.cs:105-142`). Models then started using inline
rotation and scale. If a model "ignores" a parameter on a modern backend, check the `[Description]` first.

**2. Setting `AllowDuplicates => true` to "allow many calls" → spam loops.** Because distinct args are already
never deduped, the only thing `true` buys you is letting the model repeat the *identical* call. On a spawn-like
tool that invites infinite loops on the same object. Keep it `false` unless repeating the exact same call is
deliberately meaningful.

**3. Relying on `ParametersSchema` for descriptions.** `ParametersSchema` is a **text-path-only** courtesy.
Treat it as documentation for legacy backends and keep it in sync, but never assume it reaches a modern
provider. The attributes are the source of truth on the native path.

## How to verify

Inspect the **actual request the model receives**, not the C# source:

- **LM Studio:** open the server log (Developer → server logs) and read the outgoing request body. Each tool
  under `tools[].function.parameters.properties.<param>` must have a `"description"`.
- **HTTP / other providers:** capture the request body (enable provider/request logging, or a proxy) and check
  the same `parameters.properties.<param>.description` fields.

If a parameter's `description` is missing or empty in that payload, the model is flying blind on that
parameter — add the `[Description]` attribute and re-check. The presence of the text in `ParametersSchema` is
**not** sufficient on the native path.
