# CoreAI as a DLL and .NET library

This project builds **the same core sources** that ship in the Unity package: `Assets/CoreAI/Runtime/Core`. No Unity Editor, `UnityEngine`, VContainer, or MonoBehaviour is needed to build it. The result is `CoreAI.Core.dll` for `netstandard2.1`.

The library includes portable agents, orchestration, memory contracts, skills, tool policy, and the OpenAI-compatible client. Unity UI, LLMUnity, file-based Unity adapters, Lua Mods, and the MCP server belong to separate packages and are not in this DLL.

## Who needs this page

| You are… | Read |
|---|---|
| Embedding CoreAI in a **plain .NET app** — CLI, desktop, service, worker | this page, then the [console sample](../../examples/dotnet/README.md) |
| Checking whether the engine-free claim is real | the [Regression tests](#regression-tests) section — it is a CI gate, not a statement |
| Working **in Unity** | not this page. Install the UPM packages: [INSTALL.md](../../INSTALL.md). Adding this DLL on top of the sources creates a second `CoreAI.Core` assembly |

What the API looks like in practice: [core package README](../../Assets/CoreAI/README.md#quick-start).

## Build

From the repository root, with .NET SDK 9 or newer installed:

```powershell
dotnet build tools/portable/CoreAI.Core.csproj -c Release
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- --help
```

The DLL lands in `tools/portable/bin/Release/netstandard2.1/CoreAI.Core.dll`. The first build restores NuGet dependencies; no model access or API key is needed. `COREAI_LLM` is already defined in the project and unlocks the provider HTTP path.

Dependencies are pinned in [CoreAI.Core.csproj](CoreAI.Core.csproj): Microsoft.Extensions.AI **9.10.2**, Newtonsoft.Json **13.0.3**, System.Text.Json **8.0.6**, plus their transitive dependencies. MEAI contracts are part of the public API.

Those two versions are the **Unity consumer's ceiling**, not a preference, and this project exists partly to enforce them. Unity substitutes its own `System.Text.Json` (assembly version 8.0.0.0) for any project copy, so a Microsoft.Extensions.AI 10.x build — which targets `System.Text.Json` 10.0.0.0 — cannot load in a game. Building the core against the same floor turns "the consumer cannot upgrade" into a compile error on an ordinary `dotnet build` here, instead of a `CS0234` discovered while integrating. The pin must stay equal to `Assets/packages.config`; `MeaiVersionFloorEditModeTests` fails when they drift apart.

## Regression tests

The [portable NUnit suite](Tests/README.md) runs existing engine-free tests against this DLL and collects line/branch coverage without Unity. Run `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release` from the checkout. Unity host and device tests remain separate.

It executed **1,231 cases with 0 failures in 18 s** on 2026-09-09, and it is the **first** CI job (`portable-core`, `ubuntu-latest`, no Unity license). That is what makes "the core does not depend on Unity" checkable rather than aspirational: this project references no Unity assembly at all, so one `using UnityEngine;` in `Assets/CoreAI/Runtime/Core/**` breaks the build. Re-run the command above to confirm the number yourself; it is not a badge.

The test project lists its sources file by file rather than by glob, so a new engine-free fixture must be added to `Tests/CoreAI.Portable.Tests.csproj` explicitly or it never runs in this leg.

## Referencing it from an app

When working from source, prefer a `ProjectReference`:

```xml
<ItemGroup>
  <ProjectReference Include="../CoreAI/tools/portable/CoreAI.Core.csproj" />
</ItemGroup>
```

The path is relative to the application project. The SDK flows the dependencies automatically. A ready-made option is the [console sample](../../examples/dotnet/README.md), which uses a real client, a two-document skill, and a permitted C# tool.

When referencing only `CoreAI.Core.dll`, add the same NuGet dependencies to the application project: the DLL alone is not enough. To ship the app, use a plain `dotnet publish` of the application project so its runtime dependencies are included. This project publishes no NuGet package and contains no Unity host.

`netstandard2.1` allows referencing from compatible .NET hosts, including .NET 8+. Classic .NET Framework does not implement .NET Standard 2.1. Compile support does not prove every target runtime fits: NativeAOT, trimming, and IL2CPP each need a separate check of type preservation and tool binding.

## What the app provides

- Endpoint, model, secrets, and client lifecycle. Pass secrets from the environment or your own store.
- Domain state and tools that check constraints before acting.
- A storage implementation if memory must survive restarts; history management for multi-turn flows.
- Timeout settings, cancellation, and diagnostics. For the shared tool loop use `SmartToolCallingChatClient`; the sample does not copy its implementation.

The plain non-streaming desktop path uses MEAI `FunctionInvokingChatClient` and CoreAI policy. Unity/WebGL plug in their own transport and waits through platform contracts; a `Task` in the portable API does not mean blocking the thread or relying on a thread pool is acceptable on WebGL.

## What this DLL will not do for you

- **Execute tools mid-stream.** `SmartToolCallingChatClient.GetStreamingResponseAsync` passes tool calls through unexecuted and logs that it did; the execute-as-you-stream loop lives in `MeaiLlmClient` in the Unity package. In a plain .NET host use the non-streaming `GetResponseAsync`, as the console sample does.
- **Persist anything.** There is no file store here. Supply your own `IConversationSummaryStore` / memory store, or state dies with the process.
- **Speak a non-OpenAI provider API.** `MeaiOpenAiChatClient` implements the OpenAI chat-completions shape over HTTP/SSE. Anything else needs your own `IChatClient` or a proxy in front.
- **Run a UI, a Lua sandbox, or the MCP server.** Those are separate packages and stay out of this assembly on purpose.
- **Bill, throttle, or authorize.** Budgets, rate limits and permission checks belong in your tools and your endpoint.

## Unity and updates

In Unity, install the [UPM packages](../../INSTALL.md), not this DLL on top of the sources: otherwise a second `CoreAI.Core` assembly appears. Update the DLL and dependencies together, pinning the checkout to a chosen existing tag or commit. To roll back, return to the previous checkout and rebuild the app.

[Main guide](../../README.md) · [Agents and skills](../../Assets/CoreAI/Docs/AGENT_BUILDER.md) · [MEAI contracts](../../Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md)
