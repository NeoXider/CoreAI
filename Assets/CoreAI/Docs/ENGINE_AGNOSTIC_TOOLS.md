# 🏗️ Tool architecture: engine-agnostic pattern

**Version:** v0.10.0 | **Date:** 2026-04-06

## 📋 Overview

CoreAI uses a **two-level architecture** for tools:

| Tool type | Where the abstraction lives | Where the implementation lives | Example |
|----------------|----------------|----------------|--------|
| **Engine-agnostic** | CoreAI | CoreAI | Memory, Lua |
| **Engine-specific** | CoreAI | CoreAiUnity | WorldCommand, Audio, UI |

**Engine-agnostic tools** do not depend on the engine; implementation stays in CoreAI:
- ✅ `MemoryTool` — stores a string; works on any engine
- ✅ `LuaTool` — MoonSharp interpreter; engine-independent

**Engine-specific tools** depend on the engine; implementation lives in CoreAiUnity:
- ✅ `WorldTool` — uses `GameObject`, `SceneManager` (Unity)
- ⏳ `AudioTool` — `AudioSource`, `AudioClip` (Unity)
- ⏳ `UITool` — `Canvas`, UI Elements (Unity)
- ⏳ `PhysicsTool` — `Rigidbody`, `Collider` (Unity)

This pattern enables:
- ✅ **Engine-independent core** — CoreAI works with any engine
- ✅ **Easier porting** — new engines implement the same interfaces
- ✅ **Unified API** — the LLM invokes tools the same way on all platforms

---

## 🎯 Pattern: abstract tool → engine implementation

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
            // spawn → Instantiate()
            // move → transform.position = ...
            // destroy → Object.Destroy()
        }
    }
}
```

---

## 📁 File layout

```
CoreAI/                          # Engine-agnostic core
└── Runtime/Core/Features/
    ├── Llm/
    │   ├── ILlmTool.cs          # Base ILlmTool interface
    │   └── LlmToolBase.cs       # Base class with JsonParams()
    └── World/
        └── IWorldCommandExecutor.cs  # Abstract interface

CoreAiUnity/                     # Unity-specific implementation
└── Runtime/Source/Features/
    └── World/
        ├── WorldTool.cs              # MEAI AIFunction
        ├── WorldLlmTool.cs           # ILlmTool wrapper
        └── CoreAiWorldCommandExecutor.cs  # Executor
```

---

## 🔧 How to add a new tool

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

## 🎮 Example for another engine (Unreal Engine)

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
        // spawn → GetWorld()->SpawnActor()
        // move → Actor->SetActorLocation()
        // destroy → Actor->Destroy()
    }
};
```

---

## 📋 Existing tools

| Tool | Type | Abstraction (CoreAI) | Implementation |
|------------|-----|---------------------|------------|
| **Memory** | Engine-agnostic | `MemoryLlmTool` | `MemoryTool` (in CoreAI) ✅ |
| **Lua** | Engine-agnostic | `LuaLlmTool` | `LuaTool` (in CoreAI) ✅ |
| **Inventory** | Engine-specific | `InventoryLlmTool` | `InventoryTool` (CoreAiUnity) |
| **GameConfig** | Engine-specific | `GameConfigLlmTool` | `GameConfigTool` (CoreAiUnity) |
| **WorldCommand** | Engine-specific | `IWorldCommandExecutor` (CoreAI) | `WorldTool`, `WorldLlmTool`, `CoreAiWorldCommandExecutor` (CoreAiUnity) ✅ |
| **Audio** | Engine-specific | ⏳ `IAudioController` (CoreAI) | ⏳ TODO (Unity: AudioSource) |
| **UI** | Engine-specific | ⏳ `IUIController` (CoreAI) | ⏳ TODO (Unity: Canvas/UI) |
| **Physics** | Engine-specific | ⏳ `IPhysicsController` (CoreAI) | ⏳ TODO (Unity: Rigidbody) |

### Why Memory and Lua live in CoreAI?

**MemoryTool** stores a string in `IAgentMemoryStore`. That means:
- ✅ No dependency on `UnityEngine`
- ✅ Works on any engine (simple key-value store)
- ✅ Same logic on all platforms

**LuaTool** uses the MoonSharp interpreter. That means:
- ✅ Pure .NET, no `UnityEngine`
- ✅ Sandbox behaves the same everywhere
- ✅ Engine-specific bindings can be added later

### Why is WorldCommand’s abstraction in CoreAI?

**IWorldCommandExecutor** is an abstract interface in CoreAI:
- ✅ Defines the contract for every engine
- ✅ No dependency on `UnityEngine`
- ✅ Implemented in CoreAiUnity for Unity

**WorldTool / WorldLlmTool** live in CoreAiUnity because:
- ❌ They depend on `UnityEngine` to build `AIFunction`
- ❌ They know Unity-specific types (`CoreAiWorldCommandEnvelope`)
- ✅ They still use `IWorldCommandExecutor` from CoreAI

---

## 🎯 Benefits of the pattern

1. **Portability** — new engine = implement interfaces only
2. **Testability** — core tests with mocks
3. **Flexibility** — each engine can differ internally; the API stays the same
4. **Documentation** — the interface is the contract for all engines
5. **Compatibility** — LLM prompts work on any engine

---

## 📚 References

- [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) — tool calling specification
- [MEAI_TOOL_CALLING.md](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) — MEAI pipeline architecture
- [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — building agents with tools
