# Demo: Lua Mods

Сцена: `LuaModsDemo.unity`. LLM не нужен — демонстрируется рантайм, которым пользуется ИИ.

## Что внутри

- **`LuaModsDemoController`** — резолвит из DI `LuaModRuntime` и `LuaLogicSlots`, рисует OnGUI-панель.
- **`WaveDirectorMod.lua.txt`** — мод с уровнем `Read | WorldEdit`:
  - `hooks_on("wave_started", ...)` — спавнит волну врагов одной транзакцией
    (`coreai_world_begin/commit`), счётчик волн хранит в персистентном store (`store_set/get`);
  - `hooks_every(4.0, ...)` — перекрашивает «Boss» через `coreai_world_set_props`;
  - `events_emit("wave_spawned", n)` — событие обратно в игру (`ModEventEmitted`).
- **`DamageTunerMod.lua.txt`** — мод с уровнем `Read | LogicOverride`: при загрузке вызывает
  `logic_define("damage_formula", ...)`. Контроллер каждый кадр зовёт
  `slots.TryInvokeNumber(...)` и показывает, какая формула активна — Lua-override или C#-дефолт.

## Как пользоваться

1. Открыть сцену, нажать Play.
2. «Load mod» → «Emit 'wave_started'» — на полу появляется волна капсул, в углу видно событие мода.
3. «Load override mod» — формула урона меняется с `atk - def` (C#) на `atk * 2 - def * 0.5` (Lua).
4. «Unload + reset slot» — возврат к C#-дефолту.

## Что проверить глазами

- У мода `wave_director` нет `logic_define` (нет уровня `LogicOverride`), а у `damage_tuner`
  нет `coreai_world_spawn` — capability-уровни реально ограничивают набор глобалов.
- Счётчик волн переживает Unload/Load мода (хранится в `FileLuaModStore`,
  `persistentDataPath/CoreAI/LuaMods`).
- Подробности API: `Assets/CoreAI/Docs/LUA_GAME_API.md`, безопасность —
  `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md`.
