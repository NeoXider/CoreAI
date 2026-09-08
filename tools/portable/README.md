# CoreAI as a DLL and .NET library

This project builds **the same core sources** that ship in the Unity package: `Assets/CoreAI/Runtime/Core`. No Unity Editor, `UnityEngine`, VContainer, or MonoBehaviour is needed to build it. The result is `CoreAI.Core.dll` for `netstandard2.1`.

The library includes portable agents, orchestration, memory contracts, skills, tool policy, and the OpenAI-compatible client. Unity UI, LLMUnity, file-based Unity adapters, Lua Mods, and the MCP server belong to separate packages and are not in this DLL.

## Build

From the repository root, with .NET SDK 9 or newer installed:

```powershell
dotnet build tools/portable/CoreAI.Core.csproj -c Release
dotnet run --project examples/dotnet/CoreAI.ConsoleSample.csproj -- --help
```

The DLL lands in `tools/portable/bin/Release/netstandard2.1/CoreAI.Core.dll`. The first build restores NuGet dependencies; no model access or API key is needed. `COREAI_LLM` is already defined in the project and unlocks the provider HTTP path.

Dependencies are pinned in [CoreAI.Core.csproj](CoreAI.Core.csproj): Microsoft.Extensions.AI **10.9.0**, Newtonsoft.Json **13.0.3**, System.Text.Json **10.0.11**, plus their transitive dependencies. MEAI contracts are part of the public API.

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

## Unity and updates

In Unity, install the [UPM packages](../../INSTALL.md), not this DLL on top of the sources: otherwise a second `CoreAI.Core` assembly appears. Update the DLL and dependencies together, pinning the checkout to a chosen existing tag or commit. To roll back, return to the previous checkout and rebuild the app.

[Main guide](../../README.md) · [Agents and skills](../../Assets/CoreAI/Docs/AGENT_BUILDER.md) · [MEAI contracts](../../Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md)
