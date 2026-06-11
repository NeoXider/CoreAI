# Lua в CoreAI: лучшие практики и антипаттерны

> Актуально для v4.x. См. также [LUA_GAME_API.md](LUA_GAME_API.md), [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md), [MOONSHARP_NATIVE_APIS_RU.md](MOONSHARP_NATIVE_APIS_RU.md).

## Принцип

**Lua предлагает изменения; C# решает, легальны ли они.** Песочница MoonSharp режет системные API;
capability-уровни режут игровые API; валидаторы в биндингах режут конкретные значения.

---

## ✅ Как делать правильно

### 1. Механики через логические слоты (предпочтительно)

Игра остаётся на C#. Lua только переопределяет **объявленные** точки:

```csharp
// Startup
slots.DeclareSlot("damage_formula");

// Combat tick
double dmg = slots.TryInvokeNumber("damage_formula", out var v, atk, def)
    ? v
    : DefaultDamage(atk, def);
```

```lua
logic_define("damage_formula", function(atk, def)
  return atk * 1.5 - def * 0.5
end)
```

Плюсы: fail-open (сломанный override снимается), C#-дефолт всегда есть, узкая поверхность для LLM.

### 2. Долгоживущие правила — через моды

Wave director, day/night, прогрессия — `LuaModRuntime.LoadMod` + `hooks_on` / `hooks_every`:

```csharp
modRuntime.LoadMod("wave_director", luaCode,
    LuaCapabilities.Read | LuaCapabilities.WorldEdit);
modRuntime.EmitEvent("wave_started", waveIndex.ToString());
```

Per-mod capability **уже enforced** — read-only мод не получит world-edit API.

### 3. Свои функции — через `GameLuaBindingsExtensibility`

Типизированные `Func`/`Action`, без reflection в Lua:

```csharp
public sealed class HealthLuaBindings : IGameLuaRuntimeBindings
{
    public void RegisterGameplayApis(LuaApiRegistry registry)
    {
        registry.Register("health_get", new Func<string, double>(name =>
        {
            var h = GameObject.Find(name)?.GetComponent<Health>();
            return h != null ? h.Current : -1;
        }));
        registry.Register("health_set", new Action<string, double>((name, v) =>
        {
            var h = GameObject.Find(name)?.GetComponent<Health>();
            h?.Set(Mathf.Clamp((float)v, 0f, h.Max));
        }));
    }
}

// До загрузки сцены / в раннем bootstrap:
GameLuaBindingsExtensibility.Register(
    new HealthLuaBindings(),
    LuaCapabilities.Gameplay);  // только у скриптов с Gameplay+
```

MoonSharp маршалит delegate напрямую — не оборачивайте в `DynamicInvoke` сами.

### 4. Свои world-команды — через `ICoreAiCustomWorldCommandHandler`

Без форка CoreAI; тот же MessagePipe → main thread:

```csharp
public sealed class HealWorldHandler : ICoreAiCustomWorldCommandHandler
{
    public bool CanHandle(string action) =>
        string.Equals(action, "heal_player", StringComparison.OrdinalIgnoreCase);

    public bool TryExecute(CoreAiWorldCommandEnvelope env)
    {
        float amount = env.floatValue;
        Player.Instance.Heal(amount);
        return true;
    }
}

// После resolve DI:
container.Resolve<CoreAiWorldCommandExecutor>()
    .RegisterCustomHandler(new HealWorldHandler());
```

Из Lua (WorldEdit): публикуйте envelope через существующий sink или добавьте thin Lua-wrapper в extension bindings.

### 5. Ограничивайте поверхность для LLM

| Задача | Минимальный tier |
|---|---|
| Только читать мир | `Read` |
| Менять time scale / UI | `Read \| Gameplay` |
| Спавн / level edit | `Read \| WorldEdit` |
| Формулы / моды | `+ LogicOverride` |
| Произвольные компоненты | `+ Full` (только dev / доверенные build) |

Настройка: caps на `AggregatingGameLuaRuntimeBindings`, `LoadMod(..., caps)`, инспектор `CoreAILifetimeScope`.

### 6. Whitelist'ы на хосте

- Prefab spawn — `CoreAiPrefabRegistryAsset`
- Load scene — `luaAllowedScenes` на `CoreAILifetimeScope`
- Full — `enableFullLuaAccess` (выключено по умолчанию)

### 7. MoonSharp — используйте нативное

| Задача | Нативно | Не изобретайте |
|---|---|---|
| Sandbox modules | `CoreModules.Preset_HardSandbox` | Свой парсер Lua |
| Корутины кадра | `CreateCoroutine` + `coroutine.yield()` | Busy-loop в one-shot chunk |
| CLR callbacks | `registry.Register(name, typedDelegate)` | `GetComponent` из Lua через reflection без Full tier |
| CPU limit one-shot | `IDebugger` / `LuaExecutionGuard` | Бесконечный `while true` без лимита |
| Preemptive yield | `AutoYieldCounter` + drain `YieldRequest` | — |
| CLR objects в Lua (Full+) | `UserData.RegisterType<T>()` (roadmap) | Сырой reflection на каждый вызов |

### 8. Логирование

В CoreAiUnity используйте **`IGameLogger`** / `GameLogFeature`, не `Debug.Log*` в runtime-коде
(исключение: `UnityGameLogSink` — это sink).

### 9. Тесты

- EditMode: `SecureLuaSandboxEditModeTests`, `LuaModRuntimeEditModeTests`, binding tests
- PlayMode: `LuaCoroutineRunnerPlayModeTests`, интеграции FastNoLlm
- CI: матрица `moonsharp` / `COREAI_NO_LUA`

---

## ❌ Как НЕ делать

### Безопасность

| Антипаттерн | Почему плохо |
|---|---|
| Включить `Preset_Default` / `LoadMethods` / `IO` / `Debug` | Файлы, eval, introspection |
| `UserData.RegistrationPolicy.Automatic` | Любой CLR-тип в Lua (MoonSharp docs: **never**) |
| Full-режим в production multiplayer без review | Любой скрипт может трогать любой компонент |
| Доверять `pcall` в Lua вместо C# guard | `ErrorHandling` не включён намеренно; ошибки должны ловить хост |
| Пропускать `luaAllowedScenes` в публичном чат-режиме | LLM может запросить любую сцену из Build Settings |
| Ослаблять `StripRiskyGlobals` «для удобства» | package/load/collectgarbage — escape vectors |

### Архитектура игры

| Антипаттерн | Почему плохо |
|---|---|
| Вся механика на Lua с первого дня | Нет C#-дефолта, сложнее отладка и ship |
| `logic_define` на слот, который игра не объявила | Ошибка в runtime; слоты только через `DeclareSlot` |
| Один `execute_lua` на 500 строк каждый кадр | Rate limit, latency, контекст LLM; используйте моды |
| Хранить game state только в `store_set` | Строки, 64KB cap; критичное — в C# |
| `GetComponent` / reflection в каждом кадре из Full API | GC и perf; кэшируйте id, используйте слоты |

### MoonSharp / perf

| Антипаттерн | Почему плохо |
|---|---|
| `DynamicInvoke` + `ToObject` на каждый binding call | Потеря typed marshalling (старый `LuaApiRegistry` anti-pattern) |
| Смешивать `AutoYieldCounter` и debugger step-limit без понимания | Разная семантика; см. MOONSHARP_NATIVE_APIS_RU.md |
| `renderer.material.color = …` в tight loop | Material instances; используйте `MaterialPropertyBlock` |
| `string.rep(1, 1e9)` без cap | Allocation bomb; cap есть в `SecureLuaEnvironment` |

### LLM / контекст

| Антипаттерн | Почему плохо |
|---|---|
| Весь исходник модов в system prompt | Капы `MaxResultSummaryLength` / `MaxErrorMessageLength`; используйте `manage_mods get_source` |
| Бесконечный repair loop | Rate limiter + `MaxLuaRepairRetries`; не отключайте без причины |
| Промпт «используй любые Unity API» | Модель выдумает несуществующие globals |

---

## Чеклист перед ship

- [ ] Capability tier минимален для сценария
- [ ] Full выключен (или осознанно включён с аудитом)
- [ ] Prefab + scene whitelist настроены
- [ ] Кастомные биндинги зарегистрированы с правильным `requiredCapabilities`
- [ ] Слоты объявлены в C# до `logic_define`
- [ ] Escape tests / EditMode sandbox tests проходят
- [ ] `COREAI_NO_LUA` сборка проверена, если Lua опционален
- [ ] Programmer prompt перечисляет **только** реальные API

---

## Демо и примеры

| Сцена | Путь |
|---|---|
| Lua mods + logic slots | `Assets/CoreAI.Demos/LuaMods/` |
| World command pipeline | `Assets/CoreAI.Demos/WorldCommands/` |
| Live LLM → mechanics | `Assets/CoreAI.Demos/LiveMechanics/` |
| Full reflection | `Assets/CoreAI.Demos/FullAccess/` |
