# рџЏ—пёЏ Tool architecture: engine-agnostic pattern

**Version:** v2.0.0 | **Date:** 2026-05-08

> рџ’Ў **v2.0+ вЂ” SkillSet:** Tools can now be grouped into named **skills** with per-skill prompt instructions. See [AGENT_BUILDER.md вЂ” Skills](AGENT_BUILDER.md#skills-v20) for the higher-level orchestration pattern built on top of this tool architecture.

## рџ“‹ Overview

CoreAI uses a **two-level architecture** for tools:

| Tool type | Where the abstraction lives | Where the implementation lives | Example |
|----------------|----------------|----------------|--------|
| **Engine-agnostic** | CoreAI | CoreAI | Memory, Lua |
| **Engine-specific** | CoreAI | CoreAiUnity | WorldCommand, Audio, UI |

**Engine-agnostic tools** do not depend on the engine; implementation stays in CoreAI:
- вњ… `MemoryTool` вЂ” stores a string; works on any engine
- вњ… `LuaTool` вЂ” MoonSharp interpreter; engine-independent

**Engine-specific tools** depend on the engine; implementation lives in CoreAiUnity:
- вњ… `WorldTool` вЂ” uses `GameObject`, `SceneManager` (Unity)
- вЏі `AudioTool` вЂ” `AudioSource`, `AudioClip` (Unity)
- вЏі `UITool` вЂ” `Canvas`, UI Elements (Unity)
- вЏі `PhysicsTool` вЂ” `Rigidbody`, `Collider` (Unity)

This pattern enables:
- вњ… **Engine-independent core** вЂ” CoreAI works with any engine
- вњ… **Easier porting** вЂ” new engines implement the same interfaces
- вњ… **Unified API** вЂ” the LLM invokes tools the same way on all platforms

---

## рџЋЇ Pattern: abstract tool в†’ engine implementation

### 1. Abstract interface (in CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/.../IWorldCommandExecutor.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Abstract interface for executing world commands.
    /// Implemented per engine (Unity, Unreal, Godot).
    /// </summary>
    public interface IWorldCommandExecutor
    {
        /// <summary>
        /// Execute a world command.
        /// </summary>
        /// <param name="command">Command JSON</param>
        /// <returns>true if the command executed successfully</returns>
        bool TryExecute(string command);
    }
}
```

### 2. Abstract LlmTool (in CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Base interface for all LLM tools.
    /// </summary>
    public interface ILlmTool
    {
        string Name { get; }
        string Description { get; }
        string ParametersSchema { get; }
    }

    /// <summary>
    /// Base class with JSON schema helper.
    /// </summary>
    public abstract class LlmToolBase : ILlmTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string ParametersSchema => "{}";

        protected static string JsonParams(params (string name, string type, bool required, string desc)[] p)
        {
            // JSON schema generation...
        }
    }
}
```

### 3. Concrete implementation (in CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/World/WorldTool.cs
namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Unity implementation of WorldTool.
    /// Depends on UnityEngine and CoreAI.
    /// </summary>
    public sealed class WorldTool
    {
        private readonly IWorldCommandExecutor _executor;

        public WorldTool(IWorldCommandExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public AIFunction CreateAIFunction()
        {
            // Creates MEAI AIFunction for function calling
        }
    }
}
```

```csharp
// CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs
namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// ILlmTool wrapper for WorldTool (Unity-specific).
    /// </summary>
    public sealed class WorldLlmTool : LlmToolBase
    {
        private readonly IWorldCommandExecutor _executor;

        public WorldLlmTool(IWorldCommandExecutor executor)
        {
            _executor = executor;
        }

        public override string Name => "world_command";
        public override string Description => "Execute world commands...";
        public override string ParametersSchema => JsonParams(...);

        public AIFunction CreateAIFunction()
        {
            return new WorldTool(_executor).CreateAIFunction();
        }
    }
}
```

### 4. Command executor (in CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/World/CoreAiWorldCommandExecutor.cs
namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Unity implementation of IWorldCommandExecutor.
    /// Works with GameObject, SceneManager, etc.
    /// </summary>
    public sealed class CoreAiWorldCommandExecutor : IWorldCommandExecutor
    {
        public bool TryExecute(string command)
        {
            // Parse JSON and run Unity-specific operations
            // spawn в†’ Instantiate()
            // move в†’ transform.position = ...
            // destroy в†’ Object.Destroy()
        }
    }
}
```

---

## рџ“Ѓ File layout

```
CoreAI/                          # Engine-agnostic core
в””в”Ђв”Ђ Runtime/Core/Features/
    в”њв”Ђв”Ђ Llm/
    в”‚   в”њв”Ђв”Ђ ILlmTool.cs          # Base ILlmTool interface
    в”‚   в””в”Ђв”Ђ LlmToolBase.cs       # Base class with JsonParams()
    в””в”Ђв”Ђ World/
        в””в”Ђв”Ђ IWorldCommandExecutor.cs  # Abstract interface

CoreAiUnity/                     # Unity-specific implementation
в””в”Ђв”Ђ Runtime/Source/Features/
    в””в”Ђв”Ђ World/
        в”њв”Ђв”Ђ WorldTool.cs              # MEAI AIFunction
        в”њв”Ђв”Ђ WorldLlmTool.cs           # ILlmTool wrapper
        в””в”Ђв”Ђ CoreAiWorldCommandExecutor.cs  # Executor
```

---

## рџ”§ How to add a new tool

### Step 1: Define an abstract interface (in CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/Audio/IAudioController.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Abstract interface for audio control.
    /// Implemented per engine.
    /// </summary>
    public interface IAudioController
    {
        Task PlaySoundAsync(string clipName, float volume = 1f);
        Task StopSoundAsync(string clipName);
        Task SetVolumeAsync(float volume);
    }
}
```

### Step 2: Create an LlmTool wrapper (in CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/Audio/AudioLlmTool.cs
namespace CoreAI.Infrastructure.Llm
{
    public sealed class AudioLlmTool : LlmToolBase
    {
        private readonly IAudioController _audio;

        public AudioLlmTool(IAudioController audio) => _audio = audio;

        public override string Name => "audio_control";
        public override string Description => "Play, stop, and control sounds.";
        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "play, stop, set_volume"),
            ("clipName", "string", false, "Name of the audio clip"),
            ("volume", "number", false, "Volume 0-1")
        );

        public AIFunction CreateAIFunction()
        {
            return new AudioTool(_audio).CreateAIFunction();
        }
    }
}
```

### Step 3: Implement the interface (in CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/Audio/UnityAudioController.cs
namespace CoreAI.Infrastructure.Audio
{
    public sealed class UnityAudioController : IAudioController
    {
        private readonly AudioSource _source;

        public UnityAudioController(AudioSource source) => _source = source;

        public async Task PlaySoundAsync(string clipName, float volume = 1f)
        {
            // Unity-specific logic
            var clip = Resources.Load<AudioClip>(clipName);
            _source.volume = volume;
            _source.PlayOneShot(clip);
        }
    }
}
```

### Step 4: Register in MeaiLlmClient

```csharp
// CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs
case AudioLlmTool at:
    result.Add(at.CreateAIFunction());
    break;
```

---

## рџЋ® Example for another engine (Unreal Engine)

```cpp
// CoreAI-Unreal/Source/World/UnrealWorldCommandExecutor.h
class COREAI_API IWorldCommandExecutor
{
public:
    virtual ~IWorldCommandExecutor() = default;
    virtual bool TryExecute(const FString& Command) = 0;
};

class COREAI_API FUnrealWorldCommandExecutor : public IWorldCommandExecutor
{
public:
    virtual bool TryExecute(const FString& Command) override
    {
        // Unreal-specific logic
        // spawn в†’ GetWorld()->SpawnActor()
        // move в†’ Actor->SetActorLocation()
        // destroy в†’ Actor->Destroy()
    }
};
```

---

## рџ“‹ Existing tools

| Tool | Type | Abstraction (CoreAI) | Implementation |
|------------|-----|---------------------|------------|
| **Memory** | Engine-agnostic | `MemoryLlmTool` | `MemoryTool` (in CoreAI) вњ… |
| **Lua** | Engine-agnostic | `LuaLlmTool` | `LuaTool` (in CoreAI) вњ… |
| **Inventory** | Engine-specific | `InventoryLlmTool` | `InventoryTool` (CoreAiUnity) |
| **GameConfig** | Engine-specific | `GameConfigLlmTool` | `GameConfigTool` (CoreAiUnity) |
| **WorldCommand** | Engine-specific | `IWorldCommandExecutor` (CoreAI) | `WorldTool`, `WorldLlmTool`, `CoreAiWorldCommandExecutor` (CoreAiUnity) вњ… |
| **Audio** | Engine-specific | вЏі `IAudioController` (CoreAI) | вЏі TODO (Unity: AudioSource) |
| **UI** | Engine-specific | вЏі `IUIController` (CoreAI) | вЏі TODO (Unity: Canvas/UI) |
| **Physics** | Engine-specific | вЏі `IPhysicsController` (CoreAI) | вЏі TODO (Unity: Rigidbody) |

### Why Memory and Lua live in CoreAI?

**MemoryTool** stores a string in `IAgentMemoryStore`. That means:
- вњ… No dependency on `UnityEngine`
- вњ… Works on any engine (simple key-value store)
- вњ… Same logic on all platforms

**LuaTool** uses the MoonSharp interpreter. That means:
- вњ… Pure .NET, no `UnityEngine`
- вњ… Same engine-agnostic binding model where MoonSharp is supported
- вњ… Engine-specific bindings can be added later

WebGL player builds currently disable the MoonSharp path explicitly through
`SecureLuaEnvironment.IsSupported == false`. See
[`LUA_SANDBOX_SECURITY.md`](LUA_SANDBOX_SECURITY.md) for platform support and
future WebGL restoration options.

### Why is WorldCommandвЂ™s abstraction in CoreAI?

**IWorldCommandExecutor** is an abstract interface in CoreAI:
- вњ… Defines the contract for every engine
- вњ… No dependency on `UnityEngine`
- вњ… Implemented in CoreAiUnity for Unity

**WorldTool / WorldLlmTool** live in CoreAiUnity because:
- вќЊ They depend on `UnityEngine` to build `AIFunction`
- вќЊ They know Unity-specific types (`CoreAiWorldCommandEnvelope`)
- вњ… They still use `IWorldCommandExecutor` from CoreAI

---

## рџЋЇ Benefits of the pattern

1. **Portability** вЂ” new engine = implement interfaces only
2. **Testability** вЂ” core tests with mocks
3. **Flexibility** вЂ” each engine can differ internally; the API stays the same
4. **Documentation** вЂ” the interface is the contract for all engines
5. **Compatibility** вЂ” LLM prompts work on any engine

---

## рџ“љ References

- [README.md](README.md) вЂ” portable CoreAI documentation index
- [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) вЂ” tool calling specification
- [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) вЂ” MEAI pipeline architecture
- [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) вЂ” HTTP usage, streaming `include_usage`, timeouts
- [AGENT_BUILDER.md](AGENT_BUILDER.md) вЂ” building agents with tools
