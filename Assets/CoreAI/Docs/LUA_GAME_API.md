# Lua как второй язык игры

Lua-скрипты (конверты от LLM и долгоживущие моды) могут читать мир, менять его, переопределять
игровую логику и общаться с игрой событиями. Песочница (`SecureLuaEnvironment`,
см. [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md)) при этом не ослабляется — растёт только
поверхность биндингов, и каждая группа закрыта capability-уровнем.

Все биндинги выполняются на главном потоке Unity.

## Capability-уровни

`LuaCapabilities` (flags): `Read`, `Gameplay`, `WorldEdit`, `LogicOverride`, `All`.
`AggregatingGameLuaRuntimeBindings` регистрирует группу функций только если её уровень выдан —
у скрипта с уровнем `Read` функций редактирования мира физически нет в глобалах.
По умолчанию (DI) выдаётся `All` — историческое поведение. Чтобы ограничить, создайте
агрегатор с нужным уровнем и передайте его в `LuaModRuntime` / envelope-pipeline.

| Уровень | Что открывает |
|---|---|
| `Read` | `log_*`, версии, `coreai_world_exists/pos/find/list_prefabs/raycast` |
| `Gameplay` | `time_*` (включая `time_set_scale`) |
| `WorldEdit` | `coreai_world_spawn/move/destroy/...`, батчи, транзакции, `set_props`, `parent` |
| `LogicOverride` | `logic_define/reset/list` |

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
