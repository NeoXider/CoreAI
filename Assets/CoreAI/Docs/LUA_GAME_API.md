# Lua как второй язык игры

Lua-скрипты (конверты от LLM и долгоживущие моды) могут читать мир, менять его, переопределять
игровую логику и общаться с игрой событиями. Песочница (`SecureLuaEnvironment`,
см. [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md)) при этом не ослабляется — растёт только
поверхность биндингов, и каждая группа закрыта capability-уровнем.

**Lua — опциональный модуль:** define `COREAI_NO_LUA` или удалите пакет MoonSharp — CoreAI
собирается без Lua (stub-биндинги в DI). См. [LUA_SANDBOX_SECURITY.md § Optional Module](LUA_SANDBOX_SECURITY.md).

**Лучшие практики и антипаттерны:** [LUA_BEST_PRACTICES_RU.md](LUA_BEST_PRACTICES_RU.md).  
**MoonSharp — что нативно, что своё:** [MOONSHARP_NATIVE_APIS_RU.md](MOONSHARP_NATIVE_APIS_RU.md).  
**Режимы доступа (Read → Full):** [LUA_ACCESS_MODES_AUDIT_RU.md](LUA_ACCESS_MODES_AUDIT_RU.md).

Все биндинги выполняются на главном потоке Unity.

## Capability-уровни

`LuaCapabilities` (flags): `Read`, `Gameplay`, `WorldEdit`, `LogicOverride`, `Full`, `All`.

`AggregatingGameLuaRuntimeBindings` регистрирует группу функций только если её уровень выдан —
у скрипта с уровнем `Read` функций редактирования мира физически нет в глобалах.
По умолчанию (DI) выдаётся `All` (без `Full`) — историческое поведение. Full включается
явно: **Enable Full Lua Access** на `CoreAILifetimeScope` или per-mod при `LoadMod`.

Per-mod: `LuaModRuntime.LoadMod(id, code, caps)` передаёт caps в
`ICapabilityScopedLuaBindings` — restricted-мод **не может** расширить tier хоста.

| Уровень | Что открывает |
|---|---|
| `Read` | `log_*`, версии, `coreai_world_exists/pos/find/list_prefabs/raycast` |
| `Gameplay` | `time_*` (включая `time_set_scale`) |
| `WorldEdit` | `coreai_world_spawn/move/destroy/...`, батчи, транзакции, `set_props`, `parent` |
| `LogicOverride` | `logic_define/reset/list`, mod APIs (`hooks_*`, `store_*`, `events_emit`) |
| `Full` | `unity_find`, `unity_get/set_member`, `unity_call`, … (reflection, opt-in) |

## Этап 1 — чтение мира (query-API)

`CoreAiWorldQueryLuaBindings` (срез применённого состояния; команды, опубликованные этим же
скриптом, могли ещё не примениться):

```lua
if coreai_world_exists("Boss") then
  local p = coreai_world_pos("Boss")            -- {x=..., y=..., z=...} или nil
  local near = coreai_world_find("enemy")        -- имена (contains, без регистра), максимум 100
  local hit = coreai_world_raycast(p.x, p.y + 10, p.z, 0, -1, 0, 50)
  if hit then log_info(hit.name .. " на дистанции " .. hit.distance) end
end
local prefabs = coreai_world_list_prefabs()      -- ключи из CoreAiPrefabRegistryAsset
```

## Этап 2 — логические слоты (изменение механик)

Игра объявляет переопределяемые точки и в месте использования зовёт `TryInvoke*` с откатом на
C#-дефолт (`LuaLogicSlots`):

```csharp
slots.DeclareSlot("damage_formula");
// в бою:
double dmg = slots.TryInvokeNumber("damage_formula", out double v, atk, def) ? v : DefaultDamage(atk, def);
```

```lua
logic_define("damage_formula", function(atk, def) return atk * 1.5 - def end)
logic_list()      -- { {name="damage_formula", overridden=true}, ... }
logic_reset("damage_formula")
```

Fail-open: упавший или превысивший бюджет (200 мс / 200k инструкций) override снимается
автоматически, игра возвращается к C#-дефолту, ошибка — в логе и `LastError`.

## Этап 3 — персистентный рантайм модов

`LuaModRuntime` (DI-синглтон; `LuaModRuntimeTicker` тикает его каждый кадр):

```csharp
modRuntime.LoadMod("night_director", luaCode, LuaCapabilities.Read | LuaCapabilities.WorldEdit);
modRuntime.EmitEvent("wave_started", "3");          // игра -> моды
modRuntime.ModEventEmitted += (mod, evt, payload) => ...; // моды -> игра
modRuntime.ReloadMod("night_director", newCode);
modRuntime.UnloadMod("night_director");
```

```lua
-- внутри мода (выполняется при LoadMod, регистрирует хуки):
hooks_on("wave_started", function(evt, payload)
  coreai_world_spawn("enemy.basic", "wave_" .. payload, 0, 0, 10)
end)
hooks_every(2.0, function() ... end)   -- интервал >= 0.05 c
store_set("kills", "42")               -- персистентный k/v на мод (строки)
local v = store_get("kills")
events_emit("director_ready", "")      -- другим модам и игре
log_info(mod_id())
```

Бюджеты: каждый вызов хендлера — 100 мс / 100k инструкций; ≤ 64 хендлеров и ≤ 16 таймеров на
мод; очередь событий ≤ 256 (старые вытесняются); 8 ошибок подряд — мод выгружается сам.
Хранилище — `FileLuaModStore` (`persistentDataPath/CoreAI/LuaMods`, ≤ 256 ключей, значение ≤ 64 КБ).

## Этап 4 — уровневые примитивы и транзакции

Батчи не упираются в rate-limit `execute_lua` — один вызов публикует до 100 команд:

```lua
coreai_world_spawn_batch({
  {prefab="wall", name="w1", x=0, y=0, z=0},
  {prefab="wall", name="w2", x=2, y=0, z=0},
})
coreai_world_grid("floor_tile", "cell", 0, 0, 9, 9, 1, 0)  -- 10x10 максимум (<= 100 ячеек), имена cell_ix_iz
coreai_world_parent("turret_1", "tower")                     -- "" или "none" = отцепить
coreai_world_set_props("boss", {scale=2.5, color="#ff3300"}) -- whitelist: scale, color

coreai_world_begin()      -- буферизация вместо публикации
coreai_world_grid("trap", "t", 0, 0, 4, 4, 1, 0)
coreai_world_rollback()   -- передумали: ничего не опубликовано
coreai_world_begin()
coreai_world_spawn("chest", "reward", 5, 0, 5)
coreai_world_commit()     -- опубликовать всё разом
```

Одна транзакция на инстанс биндингов; переполнение буфера (256) — авто-rollback с ошибкой.
Undo уже применённых команд нет (см. TODO).

## Этап 5 — события

Шина — внутри `LuaModRuntime`: `events_emit` доставляется всем остальным модам (на следующем
`Tick`) и в C#-событие `ModEventEmitted`; игра шлёт модам через `EmitEvent`. Подписка из игры —
напрямую на DI-синглтон `LuaModRuntime` (адаптер в MessagePipe при желании пишется в одну строку).

## LLM-инструменты (роль Programmer)

| Tool | Назначение |
|---|---|
| `execute_lua` | One-shot Lua в песочнице (те же биндинги и лимиты, что envelope-pipeline) |
| `manage_mods` | `list`, `get_source`, `load`, `reload`, `unload` для `LuaModRuntime` |

`manage_mods` не даёт модели расширить capability tier — tier задаёт хост при регистрации тула.
Read-only introspection: `LuaModsLlmTool(..., allowModManagement: false)`.

## Full-режим (`unity_*`)

Opt-in через **Enable Full Lua Access** на `CoreAILifetimeScope` или `LoadMod(..., caps | Full)`.
Политика **allow-all** (blacklist типов — Planned, см. аудит).

```lua
local id = unity_find("Boss")
unity_set_position(id, 0, 2, 0)
local comps = unity_list_components(id)
unity_set_member(id, "MeshRenderer", "material.color", "#ff0000")
```

Демо: `Assets/CoreAI.Demos/FullAccess/`. Для production предпочтительнее точечные биндинги
или будущая миграция на MoonSharp `UserData.RegisterType` (см. MOONSHARP_NATIVE_APIS_RU.md).

## Конфигурация хоста (Unity)

На `CoreAILifetimeScope`:

| Поле | Эффект |
|---|---|
| `worldPrefabRegistry` | Whitelist prefab-id для spawn |
| `luaAllowedScenes` | Whitelist имён сцен для `coreai_world_load_scene` (пусто = любая из Build Settings) |
| `enableFullLuaAccess` | Добавляет `Full` к capability агрегатора |

## Расширение API игры

### Свои Lua-функции

`GameLuaBindingsExtensibility.Register(bindings, requiredCapabilities)` до старта сцены.
Примеры — [LUA_BEST_PRACTICES_RU.md § Расширение](LUA_BEST_PRACTICES_RU.md).

### Свои world-команды (без правки CoreAI)

`CoreAiWorldCommandExecutor.RegisterCustomHandler(ICoreAiCustomWorldCommandHandler)` —
action попадает в тот же конвейер, что LLM/Lua world-команды. Пример в LUA_BEST_PRACTICES_RU.md.

## Связанные документы

- [LUA_BEST_PRACTICES_RU.md](LUA_BEST_PRACTICES_RU.md) — как делать / как **не** делать
- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) — безопасность и чеклисты
- [MOONSHARP_NATIVE_APIS_RU.md](MOONSHARP_NATIVE_APIS_RU.md) — нативные API MoonSharp
- [LUA_ACCESS_MODES_AUDIT_RU.md](LUA_ACCESS_MODES_AUDIT_RU.md) — режимы доступа
- Демо: `Assets/CoreAI.Demos/README.md`
