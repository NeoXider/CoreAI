# CoreAI agent in a plain .NET app

A small sample without Unity: a quartermaster aide reads a skill, asks for material counts through a real C# tool, and answers the user. The warehouse data is fictional; networking, MEAI contracts, and the call loop come from CoreAI itself — no separate harness implementation.

## Check the build first

From the repository root, with .NET SDK 9+ and the .NET 8 runtime:

```powershell
dotnet build examples/dotnet/CoreAI.ConsoleSample.csproj -c Release
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- --help
```

Help and a no-argument launch create no client, check no key, and touch no network. The first build restores NuGet packages, which needs access to a package source.

## Connect a model

Point it at an OpenAI-compatible API with **native tool calling**. In PowerShell:

```powershell
$env:COREAI_ENDPOINT = 'http://127.0.0.1:1234/v1'
$env:COREAI_MODEL = 'exact-id-of-the-loaded-model'
# For a cloud API, pass its key via COREAI_API_KEY.
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- 'How much iron and wood is in stock?'
```

The endpoint here is an example address of a local server that you run yourself. The model and cloud provider are deliberately never auto-selected. With a cloud endpoint, the question and tool results are sent to that provider.

## What the sample does

`DelegateLlmTool` describes `get_stock`. `SkillSet.FromTextParts` bundles a full `SKILL.md` with `references/items.md`; `read_skill` reads the main document, an extra file, or everything at once. `call_skill_tool` invokes only this catalog's tool. Two meta-tools are exposed in the public schemas.

`MeaiOpenAiChatClient` performs the HTTP requests. `SmartToolCallingChatClient` runs the turn through MEAI and CoreAI policy: up to eight roundtrips, five seconds per tool, 60 seconds per HTTP request, 90 seconds overall. `Ctrl+C` cancels the turn. The screen shows the last assistant reply and the number of executed calls.

This is one conversation turn with a read-only tool. Long-term memory and UI are not wired in. For dialogue, persist history through CoreAI contracts and watch the context budget; for state changes, add permission checks, idempotency, and `IsMutating` to the tool.

Exit codes: `0` — help or an answer received; `1` — runtime error; `2` — bad configuration; `3` — cancelled or overall timeout. A successful request completion is not a judgment on answer correctness. The sample never prints keys, HTTP error bodies, or request payloads to the diagnostic log.

[Source](Program.cs) · [DLL wiring](../../tools/portable/README.md) · [Building agents](../../Assets/CoreAI/Docs/AGENT_BUILDER.md)
