# 🏗️ Tool architecture: engine-agnostic pattern

> 💡 **SkillSet:** Tools can be grouped into named **skills** with per-skill prompt instructions. See [AGENT_BUILDER.md — Skills](AGENT_BUILDER.md#skills) for the higher-level orchestration pattern built on top of this tool architecture.

## 📋 Overview

CoreAI keeps tool **logic** away from engine APIs wherever it can, and puts the engine-bound part behind a
narrow interface:

| Tool type | Where the contract lives | Where the implementation lives | Example |
|----------------|----------------|----------------|--------|
| **Engine-agnostic** | `com.neoxider.coreai` | `com.neoxider.coreai` | Memory, Inventory, GameConfig |
| **Engine-agnostic VM, Unity package** | `com.neoxider.coreaimods` | `com.neoxider.coreaimods` | Lua (`execute_lua`) |
| **Engine-specific** | `com.neoxider.coreaiunity` | `com.neoxider.coreaiunity` | WorldCommand, Scene, Camera |

**Engine-agnostic tools** do not depend on the engine; they live in the portable core and build into the
`netstandard2.1` DLL:
- ✅ `MemoryTool` / `MemoryLlmTool` — stores text in `IAgentMemoryStore`; works on any host
- ✅ `InventoryTool` / `InventoryLlmTool` — reads an `InventoryTool.IInventoryProvider`
- ✅ `GameConfigTool` / `GameConfigLlmTool` — reads and writes per-role config slots

**Lua** runs on the managed Lua-CSharp VM, which is engine-independent, but `LuaTool` / `LuaLlmTool` ship in
the Mods package together with the mod runtime and its Unity bindings.

**Engine-specific tools** depend on Unity and live in CoreAiUnity:
- ✅ `WorldLlmTool` (`world_command`) — over `ICoreAiWorldCommandExecutor` / `CoreAiWorldCommandExecutor`
  (`GameObject`, `SceneManager`)
- ✅ `SceneLlmTool` (`scene_tool`) and `CoreAI.Vision.CameraLlmTool` (`camera`) — multi-function tools
- ⏳ Audio, UI and physics tools — not shipped; the pattern below shows how to add one

This pattern enables:
- ✅ **Engine-independent core** — the core library works in any .NET host
- ✅ **Easier porting** — another engine implements the engine-side interfaces
- ✅ **Unified API** — the LLM invokes tools the same way on all platforms

---

## 🎯 Pattern: tool contract → engine implementation

### 1. The tool contract (in CoreAI)

`ILlmTool` (in `Assets/CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs`) carries the metadata — `Name`,
`Description`, `ParametersSchema`, `AllowDuplicates`, `ToolTimeoutMsOverride`, `EndsTurn`, `IsMutating`.
`LlmToolBase` (same file) implements it with defaults and a `JsonParams(...)` schema helper. A tool becomes
callable only through one of the binding interfaces from the same file:

```csharp
namespace CoreAI.Ai
{
    public interface IAIFunctionLlmTool : ILlmTool
    {
        AIFunction CreateAIFunction();                   // one function
    }

    public interface IAIFunctionsLlmTool : ILlmTool
    {
        IEnumerable<AIFunction> CreateAIFunctions();     // several functions, called by their own names
    }
}
```

`MeaiLlmClient.BuildAIFunctions` binds `MemoryLlmTool` to the role's store, then `DelegateLlmTool`,
`IAIFunctionLlmTool` and `IAIFunctionsLlmTool`; any other tool is skipped with a warning. No package change is
needed to add a tool.

### 2. The engine-side interface (world commands, in CoreAiUnity)

```csharp
// Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/ICoreAiWorldCommandExecutor.cs
namespace CoreAI.Infrastructure.World
{
    public interface ICoreAiWorldCommandExecutor
    {
        // ApplyAiGameCommand is the portable command message from the core (CoreAI.Messaging).
        bool TryExecute(ApplyAiGameCommand cmd);

        string[] LastListedAnimations { get; }
        List<Dictionary<string, object>> LastListedObjects { get; }
        // ... plus defaulted members: LastListedPrefabKeys, LastErrorMessage, LastSpawnBatchResult
    }
}
```

### 3. The tool (in CoreAiUnity)

`WorldLlmTool` builds its own `AIFunction`; there is no separate function class:

```csharp
// Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs
namespace CoreAI.Infrastructure.Llm
{
    public sealed class WorldLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly ICoreAiWorldCommandExecutor _executor;

        public WorldLlmTool(ICoreAiWorldCommandExecutor executor, ICoreAISettings settings, IGameLogger logger,
            Func<string> liveResultNote = null) { /* ... */ }

        public override string Name => "world_command";
        public override string Description => "Manipulate game world objects/scenes. ...";

        public AIFunction CreateAIFunction()
        {
            // AIFunctionFactory.Create over a typed delegate that turns the arguments into an
            // ApplyAiGameCommand and calls _executor.TryExecute(...)
        }
    }
}
```

### 4. The command executor (in CoreAiUnity)

```csharp
// Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandExecutor.cs
namespace CoreAI.Infrastructure.World
{
    public sealed class CoreAiWorldCommandExecutor : ICoreAiWorldCommandExecutor
    {
        public bool TryExecute(ApplyAiGameCommand cmd)
        {
            // Parse the JSON payload and run Unity-specific operations:
            // spawn → Instantiate(), move → transform.position = ..., destroy → Object.Destroy()
        }
        // ...
    }
}
```

---

## 📁 File layout

```
CoreAI/                          # com.neoxider.coreai — engine-agnostic core
└── Runtime/Core/
    ├── Features/Llm/
    │   ├── ILlmTool.cs          # ILlmTool, IAIFunctionLlmTool, IAIFunctionsLlmTool, LlmToolBase
    │   └── DelegateLlmTool.cs   # tool from a C# delegate
    ├── Features/AgentMemory/    # MemoryTool, MemoryLlmTool, InventoryTool, InventoryLlmTool
    ├── Features/Config/         # GameConfigTool, GameConfigLlmTool
    └── Messaging/
        └── ApplyAiGameCommand.cs    # portable world-command message

CoreAIMods/                      # com.neoxider.coreaimods
└── Runtime/LuaExecution/
    ├── LuaTool.cs               # execute_lua AIFunction
    └── LuaLlmTool.cs            # its tool

CoreAiUnity/                     # com.neoxider.coreaiunity — Unity-specific implementation
└── Runtime/Source/Features/World/
    ├── WorldLlmTool.cs                          # world_command
    └── Infrastructure/
        ├── ICoreAiWorldCommandExecutor.cs       # engine-side contract
        └── CoreAiWorldCommandExecutor.cs        # Unity executor
```

---

## 🔧 How to add a new tool

### Step 1: Define an engine-free interface (in your code)

```csharp
public interface IAudioController
{
    Task PlaySoundAsync(string clipName, float volume = 1f);
    Task StopSoundAsync(string clipName);
    Task SetVolumeAsync(float volume);
}
```

### Step 2: Create the tool over that interface

```csharp
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

public sealed class AudioLlmTool : LlmToolBase, IAIFunctionLlmTool
{
    private readonly IAudioController _audio;

    public AudioLlmTool(IAudioController audio) => _audio = audio;

    public override string Name => "audio_control";
    public override string Description => "Play, stop, and control sounds.";
    public override bool IsMutating => true;
    public override string ParametersSchema => JsonParams(
        ("action", "string", true, "play, stop, set_volume"),
        ("clipName", "string", false, "Name of the audio clip"),
        ("volume", "number", false, "Volume 0-1")
    );

    public AIFunction CreateAIFunction()
    {
        return AIFunctionFactory.Create(
            new Func<string, string, float, CancellationToken, Task<string>>(ExecuteAsync),
            Name,
            Description);
    }

    // Parameter names must match the schema property names above.
    private async Task<string> ExecuteAsync(
        [Description("play, stop, set_volume")] string action,
        [Description("Name of the audio clip")] string clipName = null,
        [Description("Volume 0-1")] float volume = 1f,
        CancellationToken ct = default)
    {
        switch (action)
        {
            case "play": await _audio.PlaySoundAsync(clipName, volume); return "{\"ok\":true}";
            case "stop": await _audio.StopSoundAsync(clipName); return "{\"ok\":true}";
            case "set_volume": await _audio.SetVolumeAsync(volume); return "{\"ok\":true}";
            default: return "{\"ok\":false,\"error\":\"unknown_action\"}";
        }
    }
}
```

`AIFunctionFactory.Create` takes a `System.Delegate`; Unity compiles C# 9, so pass a typed delegate, not a
bare lambda. A tool body that can suspend in a WebGL player must also follow
[MEAI_TOOL_CALLING.md §3.2](MEAI_TOOL_CALLING.md#32-webgl--publishing-an-awaiting-tool-body).

### Step 3: Implement the interface (in your Unity code)

```csharp
public sealed class UnityAudioController : IAudioController
{
    private readonly AudioSource _source;

    public UnityAudioController(AudioSource source) => _source = source;

    public Task PlaySoundAsync(string clipName, float volume = 1f)
    {
        var clip = Resources.Load<AudioClip>(clipName);
        _source.volume = volume;
        _source.PlayOneShot(clip);
        return Task.CompletedTask;
    }

    // StopSoundAsync / SetVolumeAsync ...
}
```

### Step 4: Give the tool to an agent

Implementing `IAIFunctionLlmTool` is all the binding needs — no change to `MeaiLlmClient` or any other
package code:

```csharp
new AgentBuilder("DJ")
    .WithTool(new AudioLlmTool(new UnityAudioController(audioSource)))
    .Build();
```

---

## 🎮 Example for another engine (Unreal Engine)

The same split carries over: the engine implements the executor, the tool logic and prompts stay the same.

```cpp
// CoreAI-Unreal/Source/World/UnrealWorldCommandExecutor.h
class COREAI_API IWorldCommandExecutor
{
public:
    virtual ~IWorldCommandExecutor() = default;
    virtual bool TryExecute(const FString& CommandJson) = 0;
};

class COREAI_API FUnrealWorldCommandExecutor : public IWorldCommandExecutor
{
public:
    virtual bool TryExecute(const FString& CommandJson) override
    {
        // Unreal-specific logic
        // spawn → GetWorld()->SpawnActor()
        // move → Actor->SetActorLocation()
        // destroy → Actor->Destroy()
    }
};
```

---

## 📋 Existing tools

| Tool | Type | Contract | Implementation |
|------------|-----|---------------------|------------|
| **Memory** (`memory`) | Engine-agnostic | `MemoryLlmTool` (`com.neoxider.coreai`) | `MemoryTool` (`com.neoxider.coreai`) ✅ |
| **Inventory** (`get_inventory`) | Engine-agnostic | `InventoryLlmTool`, `InventoryTool.IInventoryProvider` (`com.neoxider.coreai`) | `InventoryTool` (`com.neoxider.coreai`) ✅ |
| **GameConfig** (`game_config`) | Engine-agnostic | `GameConfigLlmTool` (`com.neoxider.coreai`) | `GameConfigTool` (`com.neoxider.coreai`) ✅ |
| **Lua** (`execute_lua`) | Engine-agnostic VM | `LuaLlmTool`, `LuaTool.ILuaExecutor` (`com.neoxider.coreaimods`) | `LuaTool` (`com.neoxider.coreaimods`) ✅ |
| **WorldCommand** (`world_command`) | Engine-specific | `ICoreAiWorldCommandExecutor` (`com.neoxider.coreaiunity`) | `WorldLlmTool`, `CoreAiWorldCommandExecutor` (`com.neoxider.coreaiunity`) ✅ |
| **Audio** | Engine-specific | ⏳ not shipped | ⏳ your own (see above) |
| **UI** | Engine-specific | ⏳ not shipped | ⏳ your own |
| **Physics** | Engine-specific | ⏳ not shipped | ⏳ your own |

### Why do Memory, Inventory and GameConfig live in CoreAI?

**MemoryTool** stores text in `IAgentMemoryStore`. That means:
- ✅ No dependency on `UnityEngine`
- ✅ Works on any host (simple key-value store)
- ✅ Same logic on all platforms

### Why does Lua live in the Mods package?

**LuaTool** uses the Lua-CSharp interpreter:
- ✅ The VM is pure .NET, no `UnityEngine`
- ✅ Same binding model wherever Lua-CSharp is supported
- ➖ It ships with the mod runtime and its Unity bindings, so it is part of `com.neoxider.coreaimods`,
  not of the engine-free core

Lua-CSharp is a managed, AOT-safe VM and works on WebGL player builds by
default (toggle with `CoreAISettingsAsset.EnableLuaOnWebGl`). See
[`LUA_SANDBOX_SECURITY.md`](LUA_SANDBOX_SECURITY.md) for platform support
details and IL2CPP/WebGL stripping requirements.

### Why does WorldCommand live in CoreAiUnity?

**`ICoreAiWorldCommandExecutor`** and **`WorldLlmTool`** are both in CoreAiUnity:
- ❌ `WorldLlmTool` works with Unity-specific types (`CoreAiWorldCommandEnvelope`, `Vector3`)
- ✅ The command itself travels as the portable `ApplyAiGameCommand` message from the core, so another
  engine can implement its own executor for the same payload

---

## 🎯 Benefits of the pattern

1. **Portability** — new engine = implement the engine-side interfaces only
2. **Testability** — core tests with mocks
3. **Flexibility** — each engine can differ internally; the API stays the same
4. **Documentation** — the interface is the contract for all engines
5. **Compatibility** — LLM prompts work on any engine

---

## 📚 References

- [README.md](README.md) — portable CoreAI documentation index
- [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) — tool calling specification
- [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) — MEAI pipeline architecture
- [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) — HTTP usage, streaming `include_usage`, timeouts
- [AGENT_BUILDER.md](AGENT_BUILDER.md) — building agents with tools
