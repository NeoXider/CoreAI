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
| `IsMutating` | virtual, default `false` | `true` = the tool writes shared state; the policy never runs it concurrently with another mutating call — see below. |
| `ToolTimeoutMsOverride` | virtual, default `null` | `null` = the global `DefaultToolTimeoutMs`. Only a tool that **waits for a human** needs its own — see below. |
| `EndsTurn` | virtual, default `false` | `true` = a successful call is the LAST thing in the turn; the loop does not send the result back to the model — see below. |
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
- [ ] **Set `IsMutating => true` if the tool writes anything shared** (world, save, memory, files, a server).
      The default is `false` and means "safe to run concurrently with everything else in the turn".
- [ ] `using System.ComponentModel;` is present (for `[Description]`).
- [ ] Implement `IAIFunctionLlmTool` (or `IAIFunctionsLlmTool`); build the function with
      `AIFunctionFactory.Create`.

### Understanding `AllowDuplicates` (args-aware dedup)

`ToolExecutionPolicy` suppresses a call only when **that exact call already SUCCEEDED in an EARLIER turn of
the same request**. The key is per call — `name(canonicalized arguments)`, built by
`TryBuildDuplicateSignature`, where the arguments are serialized from a key-sorted projection so a re-emitted
call with a different key order still collides. A tool whose `AllowDuplicates => true` is excluded from the
check entirely, including the built-in mutating names.

Three consequences worth knowing before you touch the flag:

- **Distinct arguments produce distinct keys, so they are never duplicates.** Spawning ten cubes at ten
  different positions is ten different calls and all run. You do **not** need `AllowDuplicates => true` to
  "allow many calls" — only if the *same call with the same arguments* should run more than once (genuinely
  rare; a truly idempotent-to-repeat action).
- **Repeats inside ONE turn always execute.** Three identical `spawn tree` calls emitted together are a
  legitimate request; signatures are only compared against earlier turns, never against sibling slots.
- **Only success registers a signature.** A failed call stays repeatable with identical arguments, so a retry
  of exactly the call that failed is never blocked.

The key is per call and not per turn on purpose: a batch-wide key let the model slip an echo past the guard
just by changing what it sent *alongside* the repeated call (turn 1 = `[A]`, turn 2 = `[A, B]` re-executed
`A`). That is why the guide can promise you do **not** need your own idempotency key for echo suppression —
keep one only for currency- or billing-sensitive work that must stay idempotent across independent requests.

Suppression is not an error: the model gets `{"ok": true, "duplicate": true, "message": …}`, the trace is a
SUCCESS with `source=duplicate`, and a turn made only of suppressed calls leaves the consecutive-error counter
untouched. Leaving the default `false` is correct for nearly every tool, and is essential for spammy
spawn-style tools: it stops a model from looping on the exact same spawn forever.

### Understanding `IsMutating` (tools that write shared state)

`IsMutating => true` means "this tool writes something other calls can also write" — the world, a save file,
memory, a registry, a server. All mutating calls of a turn share ONE ordered serialization chain, so no two of
them ever overlap, while everything else runs concurrently under `MaxParallelToolCalls`. Two mutations racing
for the same store lose writes or read torn state, and a tool body cannot defend against that from the inside,
which is why the flag lives in the contract rather than in the implementation.

```csharp
public override bool IsMutating => true;   // LlmToolBase / ILlmTool
```

```csharp
new DelegateLlmTool("grant_item", "Grant an item.", body) { IsMutating = true };
```

Rules of thumb:

- **The default is `false` and it means read-only.** An undeclared tool may overlap with any other call in the
  turn. If your tool has a side effect and you skip the flag, the guarantee simply does not apply to it.
- **Do not go looking for a name list to edit.** The policy also recognizes the built-in mutating names
  (`memory`, `manage_mods`, `manage_skills`, `world_command`, `component_command`, `execute_lua`,
  `call_skill_tool`) so hosts that registered them keep working unchanged. That list is backward compatibility,
  not an extension point — never patch package source to add a name to it.
- **The flag is resolved by name from the role's tool list**, exactly like `ToolTimeoutMsOverride`. A tool
  reached through the skill proxy is covered twice over: the proxy itself is treated as mutating, and the
  policy also reads the resolved inner tool's own flag before scheduling the call.
- **It is unrelated to echo suppression.** `IsMutating` decides ordering; `AllowDuplicates` decides whether a
  repeat is suppressed. A read-only tool is still echo-suppressed, and a mutating tool with
  `AllowDuplicates => true` is still exempt.

### Understanding `ToolTimeoutMsOverride` (tools that wait for a human)

`DefaultToolTimeoutMs` (30 s by default) is sized for a **hung** tool — an HTTP call to a dead server. A tool
whose body *waits for a person* (a quiz card, a drag-and-drop exercise, a confirmation prompt) is idle by
design for as long as that person is thinking, so the same budget cuts it off mid-question and hands the model
`Error: Tool 'x' timed out` while the user is still reading the card. Declare the tool's own budget instead:

```csharp
// The card returns the student's answer as its own result and waits up to 120 s for it.
// 150 s leaves the meaningful "did not answer in time" result room to fire FIRST, while still
// landing under the turn's own idle deadline (LlmRequestTimeoutSeconds).
public override int? ToolTimeoutMsOverride => 150_000;
```

Rules of thumb:

- **Order the ladder deliberately:** the tool's own "the human did not respond" answer must fire *before* this
  budget, and this budget *before* `LlmRequestTimeoutSeconds`. A named result beats a tool-level error, and a
  tool-level error beats an anonymous cancelled turn.
- **Prefer a large finite value to `0`.** `0` (or negative) genuinely removes the per-call deadline. The
  request-level `LlmRequestTimeoutSeconds` still ends the turn in normal operation, but two paths have nothing
  left above: `LlmRequestTimeoutSeconds <= 0`, and the mid-stream-abort drain in `CompleteStreamedTurnAsync`,
  which passes `CancellationToken.None` on purpose.
- **Do not raise the global setting instead.** That takes the protection away from every other tool at once, so
  one hung HTTP call then holds the whole turn for the waiting tool's budget.
- The override is resolved **by name** from the role's tool list, so it applies where the policy invokes the
  tool itself. A tool executed inside another tool's body (behind `call_skill_tool`) runs under the wrapper's
  budget.

### Understanding `EndsTurn` (tools that hand control to a human)

A tool that waits for a human usually also *ends* the model's turn. Its result — "card shown, waiting for the
student" — is not material the model can continue from, but the agentic loop's default is to feed every tool
result back for one more roundtrip. The model then writes the reaction to an answer nobody has given yet, and
the student reads "Correct!" under a card they have not touched. Prompt text asking the model to stop is not a
guarantee; the flag is:

```csharp
// A successful spawn ends the turn: the next thing that happens is the STUDENT answering.
public override bool EndsTurn => true;
```

Rules of thumb:

- **Only a SUCCESSFUL call ends the turn.** A failed one keeps its ordinary error roundtrip, because the model
  is the only thing that can recover from it — cutting the turn there leaves the student with nothing.
- **Prose said BEFORE the call survives.** Only the next roundtrip is cut. A turn with no visible prose at all
  is still fine: the orchestrator synthesizes the tool-only completion line from the executed-call traces.
- **It changes nothing for other tools.** The default is `false`, and the loops read one flag
  (`ToolExecutionPolicy.TurnEndingToolSucceeded`) that only a declaring tool can raise.
- Resolved **by name** from the role's tool list, exactly like `ToolTimeoutMsOverride`: a tool invoked inside
  another tool's body (behind `call_skill_tool`) is invisible to the policy and does not end the turn. Put the
  flag on the wrapper if that path needs it.
- Honoured in **all three** agentic paths: `SmartToolCallingChatClient` (non-streaming) and both streaming
  paths in `MeaiLlmClient` (native tool calls and text-extracted ones).

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
