# Live Mechanics Demo — реальная LLM меняет механики через чат

Сцена: `Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity`

Демонстрирует главный сценарий CoreAI: **настоящая LLM-модель** (не stub) через игровой чат
пишет Lua-код и на лету создаёт/меняет игровые механики, пока игра работает.

## Что в сцене

- Мини-игра: герой каждые N секунд бьёт босса; урон, интервал атаки и лут считаются
  через **Lua logic slots** (`damage_formula`, `attack_interval`, `loot_formula`).
  Пока слот не переопределён — работает C#-дефолт (`atk - def`, 2 сек, 10 золота).
- `CoreAiChatPanel` с ролью **Programmer**: модель отвечает Lua-кодом (fenced-блок или
  tool-call `execute_lua`), который проходит штатный пайплайн
  `LuaAiEnvelopeProcessor → SecureLuaEnvironment` с полным набором игровых биндингов
  (`LuaCapabilities.All`): logic slots, `LuaModRuntime`, world-команды
  (`coreai_world_spawn` и др., префабы из `Shared/DemoPrefabRegistry`).
- Левая панель (OnGUI): HP босса, золото, состояние слотов (C# default / Lua override),
  загруженные моды, боевой лог. Чат открывается клавишей **C**.

## Требования

- LM Studio (или любой OpenAI-совместимый сервер) на `http://127.0.0.1:1234/v1`
  с загруженной моделью — endpoint настраивается в `Assets/Resources/CoreAISettings.asset`.
- MoonSharp в проекте (define `COREAI_HAS_MOONSHARP`, без `COREAI_NO_LUA`).

## Как пользоваться

1. Откройте сцену, войдите в Play Mode.
2. Нажмите **C**, чтобы открыть чат.
3. Попросите модель изменить механику — примеры промптов ниже.
4. Следите за левой панелью: слот переключится на «Lua override», и числа в боевом
   логе изменятся сразу же.

## Примеры промптов

Создание / изменение правил (logic slots):

- «Создай механику крита: переопредели слот `damage_formula(atk, def)` так, чтобы
  с шансом 30% урон удваивался, иначе обычный atk - def.»
- «Измени правило боя: урон должен быть (atk - def) * 1.5, минимум 1.»
- «Сделай героя быстрее: переопредели `attack_interval` так, чтобы атака шла раз в 0.5 секунды.»
- «Поменяй экономику: `loot_formula(bossMaxHp)` должна давать bossMaxHp / 10 + 25 золота.»
- «Покажи, какие слоты есть в игре (вызови `logic_list()`), и сбрось `damage_formula`
  к дефолту через `logic_reset`.»

Мир (world-команды):

- «Заспавни трёх врагов prefab `enemy` вокруг точки (0, 1.5, 0) и перекрась босса в фиолетовый.»

Подсказка модели, если она не знает API: в промпте можно прямо назвать функции —
`logic_define(name, fn)`, `logic_reset(name)`, `logic_list()`, `report(msg)`.

## Persistence

LiveMechanics persists successful chat-driven `execute_lua` rule changes for its known logic slots
(`damage_formula`, `attack_interval`, `loot_formula`, `boss_reward`) through
`ILuaScriptVersionStore` under `persistentDataPath/CoreAI/LuaScriptVersions`. When the scene starts
again, it reapplies the saved Lua chunk before the battle loop continues.

`manage_mods` and `LuaModRuntime` are separate: `store_set` / `store_get` values inside a mod are
file-backed by `FileLuaModStore`, but the loaded mod source list is not auto-restored by this demo.
Hosts that want mods to autoload should load/reload their selected mod sources on startup.

## Безопасность

Код модели исполняется только в `SecureLuaEnvironment` (sandbox MoonSharp: без io/os/файлов,
лимиты инструкций/памяти). Возможности ограничиваются `LuaCapabilities`; в демо у роли
Programmer полный набор (`All`) намеренно — это и есть демонстрация «AI-геймдизайнера».

## Mods-chat copy

For the same battle loop with chat-driven mod source autoload, use
`Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`. That copied scene adds a
host mod manager: successful `manage_mods load` / `reload` sources are saved, `F10` opens the
active/saved mod panel, `X` deactivates a mod, and saved inactive mods can be activated again.
The same folder also contains `WaveAutoBattlerModsDemo.unity`, a fuller hero-vs-waves demo where
mods change real combat slots instead of only the boss-rule sandbox.
