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

`DelegateLlmTool` describes `get_stock`. `SkillSet.FromTextParts` bundles a full `SKILL.md` with `references/items.md`; `read_skill` reads the main document, an extra file, or everything at once. `call_skill_tool` invokes only this catalog's tool, and refuses a call before binding when the required `item` argument is missing or `null`, or when an argument has the wrong type; the model then gets the expected parameters and a note that the tool did not run. An empty string is a present value and reaches the tool, which answers it as an unknown item. Two meta-tools are exposed in the public schemas.

`MeaiOpenAiChatClient` performs the HTTP requests. `SmartToolCallingChatClient` runs the turn through MEAI and CoreAI policy: up to eight roundtrips, five seconds per tool, 60 seconds per HTTP request, 90 seconds overall. `Ctrl+C` cancels the turn. The screen shows the last assistant reply and the number of executed calls.

This is one conversation turn with a read-only tool. Long-term memory and UI are not wired in. For dialogue, persist history through CoreAI contracts and watch the context budget; for state changes, add permission checks, idempotency, and `IsMutating` to the tool.

Exit codes: `0` — help or an answer received; `1` — runtime error; `2` — bad configuration; `3` — cancelled or overall timeout. A successful request completion is not a judgment on answer correctness. The sample never prints keys, HTTP error bodies, or request payloads to the diagnostic log.

[Source](Program.cs) · [DLL wiring](../../tools/portable/README.md) · [Building agents](../../Assets/CoreAI/Docs/AGENT_BUILDER.md)

## Use a server-managed gateway

Use an OpenAI-compatible relay in an ordinary .NET process with the portable Gateway Client. The relay, its account policy and authentication belong to your host; this repository does not provide a server implementation.

Set COREAI_ENDPOINT to your chosen HTTPS gateway base, and obtain COREAI_GATEWAY_TOKEN from your own host authentication. Do not put provider keys in the sample or a client build. COREAI_MODEL may be empty in this mode: the model field is omitted and the server selects it. Run:

```powershell
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- --server-managed 'How much iron is in stock?'
```

For an explicitly chosen HTTP gateway, including a LAN/private gateway, add --allow-insecure-http. HTTPS remains the default; the host owns transport confidentiality when opting into HTTP. The sample reads the host token dynamically; it does not create accounts, mint sessions, refresh tokens, or implement backend business rules. Optional entitlement and usage calls are disabled; any plain OpenAI-compatible gateway can serve the completion path.

The existing SmartToolCallingChatClient still executes this sample's buffered tool loop. The portable streamed path forwards native tool calls for the caller to execute; automatic execute-as-you-stream parity is not included. See [Gateway Client](../../Docs/CoreAI/GATEWAY_CLIENT.md) for scoped auth/refresh, optional v1 side calls, bounded telemetry and Unity composition. Help and argument-free launches remain offline. A successful fake-transport or portable test does not prove a live gateway, provider or browser configuration.
