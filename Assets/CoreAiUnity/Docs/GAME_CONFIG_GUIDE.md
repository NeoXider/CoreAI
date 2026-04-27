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
| `IGameConfigStore` | Interface: `TryLoad(key)`, `TrySave(key, json)` | CoreAI |
| `GameConfigTool` | `ILlmTool` for AI function calling (read/update) | CoreAI |
| `GameConfigPolicy` | Which roles may read/write which keys | CoreAI |
| `UnityGameConfigStore` | ScriptableObject implementation | CoreAIUnity |

### 2. Your game:

1. Creates a ScriptableObject with parameters
2. Registers it in `UnityGameConfigStore`
3. Configures `GameConfigPolicy` for roles
4. AI accesses configs through function calling

---

## 📝 Step-by-step

### Step 1: Create a ScriptableObject config

```csharp
// Assets/_exampleGame/Config/GameSessionConfig.cs
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

### Step 2: Register the config in DI

```csharp
// In your LifetimeScope:
var configStore = container.Resolve<UnityGameConfigStore>();
configStore.Register("session", mySessionConfigAsset);

// Configure policy
var configPolicy = container.Resolve<GameConfigPolicy>();
configPolicy.SetKnownKeys(new[] { "session", "crafting" });
configPolicy.GrantFullAccess("Creator"); // Creator can do everything
configPolicy.ConfigureRole("CoreMechanicAI", 
    readKeys: new[] { "session" }, 
    writeKeys: new[] { "session" });
```

### Step 3: AI receives the tool automatically

`AiOrchestrator` **automatically** passes `GameConfigTool` to the LLM when:
- The role has config access (via `GameConfigPolicy`)
- `AgentMemoryPolicy.GetToolsForRole()` includes GameConfigTool

```csharp
// In AiOrchestrator — tools are assembled automatically:
var tools = _memoryPolicy?.GetToolsForRole(roleId);
// GameConfigTool is added separately if the role has access
if (_configPolicy.GetAllowedKeys(roleId).Length > 0)
{
    var configTool = _configPolicy.CreateLlmTool(_configStore, roleId);
    tools.Add(configTool.CreateAIFunction());
}
```

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

// Response:
{
  "success": true,
  "message": "Config read successfully",
  "config_json": "{\"session\":{\"difficulty\":1,\"enemy_hp_mult\":1.0}}"
}
```

### AI updates config

```json
// AI function call with modified JSON:
{
  "name": "game_config",
  "arguments": {
    "action": "update",
    "content": "{\"difficulty\":2,\"enemy_hp_mult\":1.5,\"max_enemies\":80}"
  }
}

// Confirmation:
{
  "success": true,
  "message": "Config updated for key: session"
}
```

---

## 🔒 Security

```csharp
// Restrict role access:
configPolicy.ConfigureRole("AINpc",
    readKeys: new[] { "dialogue" },   // Read only
    writeKeys: Array.Empty<string>()); // No write

configPolicy.RevokeAccess("PlayerChat"); // No access
```

---

## 🧪 Testing

```csharp
// EditMode test
[Test]
public void ConfigTool_ReadModifyWrite_Works()
{
    var store = new InMemoryConfigStore();
    store.Save("session", "{\"difficulty\":1}");
    
    var policy = new GameConfigPolicy();
    policy.GrantFullAccess("Creator");
    
    var tool = new GameConfigTool(store, policy, "Creator");
    
    // Read
    var readResult = tool.ExecuteAsync("read").Result;
    Assert.IsTrue(readResult.Success);
    
    // Update
    var writeResult = tool.ExecuteAsync("update", "{\"difficulty\":3}").Result;
    Assert.IsTrue(writeResult.Success);
    
    // Verify
    store.TryLoad("session", out var json);
    Assert.IsTrue(json.Contains("3"));
}
```

---

## 📁 File layout

```
CoreAI (portable):
├── Features/Config/
│   ├── IGameConfigStore.cs          # Interface
│   ├── GameConfigTool.cs            # ILlmTool
│   ├── GameConfigPolicy.cs          # Access policy
│   ├── GameConfigLlmTool.cs         # ILlmTool wrapper
│   └── NullGameConfigStore.cs       # Stub

CoreAIUnity (Unity):
├── Features/Config/Infrastructure/
│   └── UnityGameConfigStore.cs      # ScriptableObject implementation

Your game:
├── Config/
│   ├── GameSessionConfig.cs         # Your ScriptableObject
│   ├── GameSessionConfig.asset      # Asset
│   └── ConfigInstaller.cs           # DI registration
```

---

## ✅ Checklist

- [ ] ScriptableObject with parameters created
- [ ] Config asset created (`CreateAssetMenu`)
- [ ] `UnityGameConfigStore.Register("key", configAsset)` called
- [ ] `GameConfigPolicy` configured for roles
- [ ] AI system prompt mentions available keys
- [ ] Tests written (EditMode + PlayMode)

---

## 💡 Tips

1. **One key = one ScriptableObject** — do not mix unrelated config types
2. **Validate on the SO** — use `[Range]` to block absurd values
3. **Logging** — `UnityGameConfigStore` logs all changes
4. **Editor** — changes persist to the asset via `EditorUtility.SetDirty`
5. **Runtime** — in builds, changes last until scene reload
