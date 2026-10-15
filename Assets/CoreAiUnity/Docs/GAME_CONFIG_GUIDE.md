# GameConfig guide: letting AI change game parameters

## 📌 Concept

**CoreAI does not ship game-specific configs.** Instead, the core provides **generic infrastructure** to read/write JSON configs through AI function calling.

```
┌─────────────────────────────────────────────────────────┐
│                    CoreAI (generic)                      │
│  ┌─────────────────┐    ┌──────────────┐               │
│  │ IGameConfigStore│    │ GameConfig   │               │
│  │ (interface)     │◄───│ Tool (ILlm)  │               │
│  └────────┬────────┘    └──────────────┘               │
│           │                    ▲                        │
│           │            AI function calling              │
└───────────┼────────────────────┼────────────────────────┘
            │                    │
     ┌──────┴──────┐    ┌───────┴───────┐
     │  Your game  │    │  LLM (Creator)│
     │  implements │    │  read/update  │
     │  interface  │    │  JSON configs   │
     └─────────────┘    └───────────────┘
```

---

## 🔧 How it works

### 1. CoreAI provides:

| Component | Purpose | Where |
|-----------|------------|-----|
| `IGameConfigStore` | Interface: `TryLoad(key, out json)`, `TrySave(key, json)`, `GetKnownKeys()` | CoreAI |
| `GameConfigPolicy` | Which roles may use which keys; `CreateLlmTool(store, roleId)` builds the tool | CoreAI |
| `GameConfigTool` | Runs `read` / `update` and builds the MEAI function | CoreAI |
| `GameConfigLlmTool` | The `game_config` tool (`IAIFunctionLlmTool`) you attach to a role | CoreAI |
| `NullGameConfigStore` | Fallback store when nothing else is registered; saves nothing | CoreAI |
| `UnityGameConfigStore` | `IGameConfigStore` over registered ScriptableObjects (`JsonUtility`); `CoreAILifetimeScope` registers it as `IGameConfigStore` | CoreAiUnity |

### 2. Your game:

1. Creates a ScriptableObject with parameters
2. Registers it in `UnityGameConfigStore`
3. Configures `GameConfigPolicy` for roles
4. Attaches the `game_config` tool to those roles (it is not attached to any role by default)
5. AI accesses configs through function calling

---

## 📝 Step-by-step

### Step 1: Create a ScriptableObject config

```csharp
// Assets/MyGame/Config/GameSessionConfig.cs
using UnityEngine;

[CreateAssetMenu(menuName = "MyGame/Session Config")]
public class GameSessionConfig : ScriptableObject
{
    [Range(0, 3)] public int Difficulty = 1;
    [Range(0.1f, 10f)] public float EnemyHealthMultiplier = 1f;
    [Range(0.1f, 10f)] public float EnemyDamageMultiplier = 1f;
    [Range(1, 500)] public int MaxActiveEnemies = 50;
}
```

### Step 2: Register the config and the policy

`CoreAILifetimeScope` is sealed, so register from a scene component once the scope is built (it builds
in `Awake`):

```csharp
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Config;
using CoreAI.Infrastructure.Config;
using UnityEngine;
using VContainer;

public class ConfigBootstrap : MonoBehaviour
{
    [SerializeField] private CoreAILifetimeScope scope;
    [SerializeField] private GameSessionConfig sessionConfig;

    private void Start()
    {
        IObjectResolver container = scope.Container;

        IGameConfigStore store = container.Resolve<IGameConfigStore>();
        ((UnityGameConfigStore)store).Register("session", sessionConfig);

        GameConfigPolicy configPolicy = container.Resolve<GameConfigPolicy>();
        configPolicy.SetKnownKeys(new[] { "session", "crafting" });
        configPolicy.GrantFullAccess(BuiltInAgentRoleIds.Creator); // Creator can use every known key
        configPolicy.ConfigureRole(BuiltInAgentRoleIds.CoreMechanic,
            readKeys: new[] { "session" },
            writeKeys: new[] { "session" });

        // Step 3: attach the tool (once per role; AddToolForRole appends, it does not replace).
        AgentMemoryPolicy agents = container.Resolve<AgentMemoryPolicy>();
        agents.AddToolForRole(BuiltInAgentRoleIds.Creator,
            configPolicy.CreateLlmTool(store, BuiltInAgentRoleIds.Creator));
        agents.AddToolForRole(BuiltInAgentRoleIds.CoreMechanic,
            configPolicy.CreateLlmTool(store, BuiltInAgentRoleIds.CoreMechanic));
    }
}
```

### Step 3: Attach the tool to roles

Nothing adds `game_config` automatically. Either attach it to an existing role as above, or put it on a
custom agent:

```csharp
AgentConfig designer = new AgentBuilder("LevelDesigner")
    .WithSystemPrompt("You tune the session config. Config keys: session.")
    .WithTool(configPolicy.CreateLlmTool(store, "LevelDesigner"))
    .Build();
```

The tool description lists the keys the role may use (`GameConfigPolicy.GetAllowedKeys`).

### What the tool can reach

- `read` and `update` both work on `GetAllowedKeys(roleId)`, which returns the role's **write** keys
  (all keys from `SetKnownKeys` for a role with full write access). A role with read keys but no write
  keys gets `Role '…' has no allowed config keys.` from `read`; `CanRead` is a host-side check that the
  tool does not use.
- `update` writes the whole `content` object to the role's **first** allowed key. To split one update
  across several keys, subclass `GameConfigPolicy` and override `TryApplyChanges`; a non-empty `error`
  from it rejects the update.
- With `UnityGameConfigStore` the JSON keys are the ScriptableObject's serialized field names
  (`JsonUtility`), for example `Difficulty`, not `difficulty`.

---

## 🤖 How AI uses configs

### AI reads config

```json
// AI function call:
{
  "name": "game_config",
  "arguments": {
    "action": "read"
  }
}

// Response (GameConfigTool.GameConfigResult):
{
  "Success": true,
  "Message": "Config read successfully for keys: session",
  "Error": null,
  "ConfigJson": "{\"session\":{\"Difficulty\":1,\"EnemyHealthMultiplier\":1.0,\"EnemyDamageMultiplier\":1.0,\"MaxActiveEnemies\":50}}"
}
```

### AI updates config

```json
// AI function call with modified JSON:
{
  "name": "game_config",
  "arguments": {
    "action": "update",
    "content": "{\"Difficulty\":2,\"EnemyHealthMultiplier\":1.5,\"MaxActiveEnemies\":80}"
  }
}

// Confirmation:
{
  "Success": true,
  "Message": "Config updated for key: session",
  "Error": null,
  "ConfigJson": "{\"Difficulty\":2,\"EnemyHealthMultiplier\":1.5,\"MaxActiveEnemies\":80}"
}
```

---

## 🔒 Security

```csharp
// Restrict a role to one key:
configPolicy.ConfigureRole("AINpc",
    readKeys: new[] { "dialogue" },
    writeKeys: new[] { "dialogue" });   // the tool reads and writes only "dialogue"

configPolicy.RevokeAccess("SmartChat"); // No access: read and update both fail

// null for readKeys / writeKeys means "all known keys" (SetKnownKeys).
```

---

## 🧪 Testing

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.Config;
using Newtonsoft.Json;
using NUnit.Framework;

public sealed class MyConfigToolTests
{
    // Test double: a dictionary-backed store (the package's own tests use a private one like this).
    private sealed class InMemoryConfigStore : IGameConfigStore
    {
        private readonly Dictionary<string, string> _configs = new();
        public bool TryLoad(string key, out string json) => _configs.TryGetValue(key, out json);
        public bool TrySave(string key, string json) { _configs[key] = json; return true; }
        public string[] GetKnownKeys() => _configs.Keys.ToArray();
    }

    [Test]
    public async Task ConfigTool_ReadModifyWrite_Works()
    {
        InMemoryConfigStore store = new InMemoryConfigStore();
        store.TrySave("session", "{\"difficulty\":1}");

        GameConfigPolicy policy = new GameConfigPolicy();
        policy.ConfigureRole("Creator", new[] { "session" }, new[] { "session" }); // read keys, write keys

        GameConfigTool tool = new GameConfigTool(store, policy, "Creator");

        // ExecuteAsync returns the serialized GameConfigTool.GameConfigResult
        string readJson = await tool.ExecuteAsync("read");
        Assert.IsTrue(JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(readJson).Success);

        string writeJson = await tool.ExecuteAsync("update", "{\"difficulty\":3}");
        Assert.IsTrue(JsonConvert.DeserializeObject<GameConfigTool.GameConfigResult>(writeJson).Success);

        store.TryLoad("session", out string json);
        Assert.IsTrue(json.Contains("3"));
    }
}
```

---

## 📁 File layout

```
CoreAI (portable):
├── Features/Config/
│   ├── IGameConfigStore.cs          # Interface
│   ├── GameConfigTool.cs            # read/update logic + MEAI function
│   ├── GameConfigPolicy.cs          # Access policy, CreateLlmTool
│   ├── GameConfigLlmTool.cs         # game_config tool (IAIFunctionLlmTool)
│   └── NullGameConfigStore.cs       # Fallback store (saves nothing)

CoreAIUnity (Unity):
├── Features/Config/Infrastructure/
│   └── UnityGameConfigStore.cs      # Store over registered ScriptableObjects

Your game:
├── Config/
│   ├── GameSessionConfig.cs         # Your ScriptableObject
│   ├── GameSessionConfig.asset      # Asset
│   └── ConfigBootstrap.cs           # Registers the asset, policy and tool
```

---

## ✅ Checklist

- [ ] ScriptableObject with parameters created
- [ ] Config asset created (`CreateAssetMenu`)
- [ ] `UnityGameConfigStore.Register("key", configAsset)` called
- [ ] `GameConfigPolicy` configured for roles
- [ ] `game_config` tool attached to those roles (`CreateLlmTool` + `AddToolForRole` or `WithTool`)
- [ ] AI system prompt mentions available keys
- [ ] Tests written (EditMode + PlayMode)

---

## 💡 Tips

1. **One key = one ScriptableObject** — do not mix unrelated config types
2. **Validate in the policy** — `[Range]` limits only the Inspector; `JsonUtility.FromJsonOverwrite` writes
   any value. Reject bad values in a `GameConfigPolicy` subclass (`TryApplyChanges` with a non-empty `error`)
3. **Logging** — `UnityGameConfigStore` logs every registration and update
4. **Editor** — the store marks the asset dirty (`EditorUtility.SetDirty`), so changes made in Play Mode
   stay in the asset
5. **Player** — changes live in memory only; the shipped asset is not rewritten
