# GameConfig Guide: Как разрешить AI менять параметры игры

## 📌 Концепция

**CoreAI НЕ содержит игровых конфигов.** Вместо этого ядро даёт **универсальную инфраструктуру** для чтения/записи JSON конфигов через AI function calling.

```
┌─────────────────────────────────────────────────────────┐
│                    CoreAI (универсальный)                │
│  ┌─────────────────┐    ┌──────────────┐               │
│  │ IGameConfigStore│    │ GameConfig   │               │
│  │ (интерфейс)     │◄───│ Tool (ILlm)  │               │
│  └────────┬────────┘    └──────────────┘               │
│           │                    ▲                        │
│           │            AI function calling              │
└───────────┼────────────────────┼────────────────────────┘
            │                    │
     ┌──────┴──────┐    ┌───────┴───────┐
     │  Ваша игра  │    │  LLM (Creator)│
     │  реализует  │    │  read/update  │
     │  интерфейс  │    │  JSON конфиги │
     └─────────────┘    └───────────────┘
```

---

## 🔧 Как это работает

### 1. CoreAI предоставляет:

| Компонент | Назначение | Где |
|-----------|------------|-----|
| `IGameConfigStore` | Интерфейс: `TryLoad(key)`, `TrySave(key, json)` | CoreAI |
| `GameConfigTool` | ILlmTool для AI function calling (read/update) | CoreAI |
| `GameConfigPolicy` | Какие роли могут читать/менять какие ключи | CoreAI |
| `UnityGameConfigStore` | Реализация на ScriptableObject | CoreAIUnity |

### 2. Игра делает:

1. Создаёт ScriptableObject с параметрами
2. Регистрирует его в `UnityGameConfigStore`
3. Настраивает `GameConfigPolicy` для ролей
4. AI получает доступ к конфигам через function calling

---

## 📝 Пошаговая инструкция

### Шаг 1: Создайте ScriptableObject конфиг

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

### Шаг 2: Зарегистрируйте конфиг в DI

```csharp
// В вашем LifetimeScope:
var configStore = container.Resolve<UnityGameConfigStore>();
configStore.Register("session", mySessionConfigAsset);

// Настройте политику
var configPolicy = container.Resolve<GameConfigPolicy>();
configPolicy.SetKnownKeys(new[] { "session", "crafting" });
configPolicy.GrantFullAccess("Creator"); // Creator может всё
configPolicy.ConfigureRole("CoreMechanicAI", 
    readKeys: new[] { "session" }, 
    writeKeys: new[] { "session" });
```

### Шаг 3: AI автоматически получает tool

`AiOrchestrator` **автоматически** передаёт `GameConfigTool` в LLM если:
- Роль имеет доступ к конфигам (через `GameConfigPolicy`)
- `AgentMemoryPolicy.GetToolsForRole()` включает GameConfigTool

```csharp
// В AiOrchestrator — инструменты собираются автоматически:
var tools = _memoryPolicy?.GetToolsForRole(roleId);
// GameConfigTool добавляется отдельно если роль имеет доступ
if (_configPolicy.GetAllowedKeys(roleId).Length > 0)
{
    var configTool = _configPolicy.CreateLlmTool(_configStore, roleId);
    tools.Add(configTool.CreateAIFunction());
}
```

---

## 🤖 Как AI использует конфиги

### AI читает конфиг

```json
// AI вызывает function call:
{
  "name": "game_config",
  "arguments": {
    "action": "read"
  }
}

// Получает ответ:
{
  "success": true,
  "message": "Config read successfully",
  "config_json": "{\"session\":{\"difficulty\":1,\"enemy_hp_mult\":1.0}}"
}
```

### AI меняет конфиг

```json
// AI вызывает function call с изменённым JSON:
{
  "name": "game_config",
  "arguments": {
    "action": "update",
    "content": "{\"difficulty\":2,\"enemy_hp_mult\":1.5,\"max_enemies\":80}"
  }
}

// Получает подтверждение:
{
  "success": true,
  "message": "Config updated for key: session"
}
```

---

## 🔒 Безопасность

```csharp
// Ограничьте доступ ролей:
configPolicy.ConfigureRole("AINpc",
    readKeys: new[] { "dialogue" },   // Только чтение
    writeKeys: Array.Empty<string>()); // Без записи

configPolicy.RevokeAccess("PlayerChat"); // Без доступа
```

---

## 🧪 Тестирование

```csharp
// EditMode тест
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

## 📁 Структура файлов

```
CoreAI (универсальный):
├── Features/Config/
│   ├── IGameConfigStore.cs          # Интерфейс
│   ├── GameConfigTool.cs            # ILlmTool
│   ├── GameConfigPolicy.cs          # Политика доступа
│   ├── GameConfigLlmTool.cs         # Обёртка ILlmTool
│   └── NullGameConfigStore.cs       # Заглушка

CoreAIUnity (Unity):
├── Features/Config/Infrastructure/
│   └── UnityGameConfigStore.cs      # ScriptableObject реализация

Ваша игра:
├── Config/
│   ├── GameSessionConfig.cs         # Ваш ScriptableObject
│   ├── GameSessionConfig.asset      # Ассет
│   └── ConfigInstaller.cs           # Регистрация в DI
```

---

## ✅ Checklist

- [ ] Создан ScriptableObject с параметрами
- [ ] Создан ассет конфига (`CreateAssetMenu`)
- [ ] `UnityGameConfigStore.Register("key", configAsset)` вызван
- [ ] `GameConfigPolicy` настроен для ролей
- [ ] Системный промпт AI знает о доступных ключах
- [ ] Тесты написаны (EditMode + PlayMode)

---

## 💡 Советы

1. **Один ключ = один ScriptableObject** — не смешивайте разные типы конфигов
2. **Валидация в SO** — используйте `[Range]` для защиты от безумных значений
3. **Логирование** — `UnityGameConfigStore` логирует все изменения
4. **Editor** — изменения сохраняются в ассет через `EditorUtility.SetDirty`
5. **Runtime** — в билде изменения живут до перезагрузки сцены
