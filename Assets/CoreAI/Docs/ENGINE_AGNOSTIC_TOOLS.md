# 🏗️ Архитектура инструментов: Engine-Agnostic Pattern

**Версия:** v0.10.0 | **Дата:** 2026-04-06

## 📋 Обзор

CoreAI использует **двухуровневую архитектуру** для инструментов (tools):

| Тип инструмента | Где абстракция | Где реализация | Пример |
|----------------|----------------|----------------|--------|
| **Engine-Agnostic** | CoreAI | CoreAI | Memory, Lua |
| **Engine-Specific** | CoreAI | CoreAiUnity | WorldCommand, Audio, UI |

**Engine-Agnostic инструменты** — не зависят от движка, реализация в CoreAI:
- ✅ `MemoryTool` — просто хранит строку, работает на любом движке
- ✅ `LuaTool` — MoonSharp интерпретатор, движок-независимый

**Engine-Specific инструменты** — зависят от движка, реализация в CoreAiUnity:
- ✅ `WorldTool` — работает с GameObject, SceneManager (Unity)
- ⏳ `AudioTool` — работает с AudioSource, AudioClip (Unity)
- ⏳ `UITool` — работает с Canvas, UI Elements (Unity)
- ⏳ `PhysicsTool` — работает с Rigidbody, Collider (Unity)

Этот паттерн позволяет:
- ✅ **Движок-независимое ядро** — CoreAI работает с любым движком
- ✅ **Лёгкая портируемость** — новые движки реализуют те же интерфейсы
- ✅ **Единый API** — LLM вызывает инструменты одинаково на всех платформах

---

## 🎯 Паттерн: Abstract Tool → Engine Implementation

### 1. Абстрактный интерфейс (в CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/.../IWorldCommandExecutor.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для выполнения world commands.
    /// Реализуется для каждого движка отдельно (Unity, Unreal, Godot).
    /// </summary>
    public interface IWorldCommandExecutor
    {
        /// <summary>
        /// Выполнить команду мира.
        /// </summary>
        /// <param name="command">JSON команды</param>
        /// <returns>true если команда выполнена успешно</returns>
        bool TryExecute(string command);
    }
}
```

### 2. Абстрактный LlmTool (в CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/Llm/ILlmTool.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Базовый интерфейс для всех LLM инструментов.
    /// </summary>
    public interface ILlmTool
    {
        string Name { get; }
        string Description { get; }
        string ParametersSchema { get; }
    }

    /// <summary>
    /// Базовый класс с хелпером для JSON schema.
    /// </summary>
    public abstract class LlmToolBase : ILlmTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string ParametersSchema => "{}";

        protected static string JsonParams(params (string name, string type, bool required, string desc)[] p)
        {
            // Генерация JSON schema...
        }
    }
}
```

### 3. Конкретная реализация (в CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/World/WorldTool.cs
namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Unity-реализация WorldTool.
    /// Зависит от UnityEngine и CoreAI.
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
            // Создаёт MEAI AIFunction для function calling
        }
    }
}
```

```csharp
// CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs
namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// ILlmTool обёртка для WorldTool (Unity-специфичная).
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

### 4. Исполнитель команд (в CoreAiUnity)

```csharp
// CoreAiUnity/Runtime/Source/Features/World/CoreAiWorldCommandExecutor.cs
namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Unity-реализация IWorldCommandExecutor.
    /// Работает с GameObject, SceneManager, etc.
    /// </summary>
    public sealed class CoreAiWorldCommandExecutor : IWorldCommandExecutor
    {
        public bool TryExecute(string command)
        {
            // Парсит JSON и выполняет Unity-специфичные операции
            // spawn → Instantiate()
            // move → transform.position = ...
            // destroy → Object.Destroy()
        }
    }
}
```

---

## 📁 Структура файлов

```
CoreAI/                          # Движок-независимое ядро
└── Runtime/Core/Features/
    ├── Llm/
    │   ├── ILlmTool.cs          # Базовый интерфейс ILlmTool
    │   └── LlmToolBase.cs       # Базовый класс с JsonParams()
    └── World/
        └── IWorldCommandExecutor.cs  # Абстрактный интерфейс

CoreAiUnity/                     # Unity-специфичная реализация
└── Runtime/Source/Features/
    └── World/
        ├── WorldTool.cs              # MEAI AIFunction
        ├── WorldLlmTool.cs           # ILlmTool обёртка
        └── CoreAiWorldCommandExecutor.cs  # Исполнитель
```

---

## 🔧 Как добавить новый инструмент

### Шаг 1: Создать абстрактный интерфейс (в CoreAI)

```csharp
// CoreAI/Runtime/Core/Features/Audio/IAudioController.cs
namespace CoreAI.Ai
{
    /// <summary>
    /// Абстрактный интерфейс для управления звуком.
    /// Реализуется для каждого движка отдельно.
    /// </summary>
    public interface IAudioController
    {
        Task PlaySoundAsync(string clipName, float volume = 1f);
        Task StopSoundAsync(string clipName);
        Task SetVolumeAsync(float volume);
    }
}
```

### Шаг 2: Создать LlmTool обёртку (в CoreAiUnity)

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

### Шаг 3: Создать реализацию (в CoreAiUnity)

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
            // Unity-специфичная логика
            var clip = Resources.Load<AudioClip>(clipName);
            _source.volume = volume;
            _source.PlayOneShot(clip);
        }
    }
}
```

### Шаг 4: Добавить в MeaiLlmClient

```csharp
// CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs
case AudioLlmTool at:
    result.Add(at.CreateAIFunction());
    break;
```

---

## 🎮 Пример для другого движка (Unreal Engine)

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
        // Unreal-специфичная логика
        // spawn → GetWorld()->SpawnActor()
        // move → Actor->SetActorLocation()
        // destroy → Actor->Destroy()
    }
};
```

---

## 📋 Существующие инструменты

| Инструмент | Тип | Абстракция (CoreAI) | Реализация |
|------------|-----|---------------------|------------|
| **Memory** | Engine-Agnostic | `MemoryLlmTool` | `MemoryTool` (в CoreAI) ✅ |
| **Lua** | Engine-Agnostic | `LuaLlmTool` | `LuaTool` (в CoreAI) ✅ |
| **Inventory** | Engine-Specific | `InventoryLlmTool` | `InventoryTool` (в CoreAiUnity) |
| **GameConfig** | Engine-Specific | `GameConfigLlmTool` | `GameConfigTool` (в CoreAiUnity) |
| **WorldCommand** | Engine-Specific | `IWorldCommandExecutor` (CoreAI) | `WorldTool`, `WorldLlmTool`, `CoreAiWorldCommandExecutor` (в CoreAiUnity) ✅ |
| **Audio** | Engine-Specific | ⏳ `IAudioController` (CoreAI) | ⏳ TODO (Unity: AudioSource) |
| **UI** | Engine-Specific | ⏳ `IUIController` (CoreAI) | ⏳ TODO (Unity: Canvas/UI) |
| **Physics** | Engine-Specific | ⏳ `IPhysicsController` (CoreAI) | ⏳ TODO (Unity: Rigidbody) |

### Почему Memory и Lua в CoreAI?

**MemoryTool** — просто хранит строку в `IAgentMemoryStore`. Это:
- ✅ Не зависит от UnityEngine
- ✅ Работает на любом движке (просто ключ-значение хранилище)
- ✅ Одинаковая логика для всех платформ

**LuaTool** — использует MoonSharp интерпретатор. Это:
- ✅ Чистый .NET код, без UnityEngine
- ✅ Песочница выполняется одинаково везде
- ✅ Движок-специфичные binding'и можно добавить позже

### Почему WorldCommand абстракция в CoreAI?

**IWorldCommandExecutor** — абстрактный интерфейс в CoreAI:
- ✅ Определяет контракт для всех движков
- ✅ Не зависит от UnityEngine
- ✅ Реализуется в CoreAiUnity для Unity

**WorldTool/WorldLlmTool** — в CoreAiUnity потому что:
- ❌ Зависит от UnityEngine для создания AIFunction
- ❌ Знает о Unity-специфичных типах (CoreAiWorldCommandEnvelope)
- ✅ Но использует `IWorldCommandExecutor` из CoreAI

---

## 🎯 Преимущества паттерна

1. **Портируемость** — новый движок = только реализация интерфейсов
2. **Тестируемость** — ядро тестируется с моками
3. **Гибкость** — каждый движок делает по-своему, API одинаковый
4. **Документируемость** — интерфейс = контракт для всех движков
5. **Совместимость** — промпты LLM работают на любом движке

---

## 📚 Ссылки

- [TOOL_CALL_SPEC.md](../../CoreAiUnity/Docs/TOOL_CALL_SPEC.md) — спецификация tool calling
- [MEAI_TOOL_CALLING.md](../../CoreAI/Docs/MEAI_TOOL_CALLING.md) — архитектура MEAI pipeline
- [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — создание агентов с инструментами
