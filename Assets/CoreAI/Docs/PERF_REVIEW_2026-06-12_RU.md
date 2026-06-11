# Обзор производительности CoreAI Lua / World (2026-06-12)

Краткий аудит горячих путей после работ по Lua v4. Статус: **критичное исправлено**,
остальное — backlog.

## Исправлено (критичное)

### `set_color` и material instances

**Было:** `renderer.material.color = …` — создавал новый Material instance на каждый вызов
(утечка GPU/CPU при частых AI-перекрасках).

**Стало:** `MaterialPropertyBlock` переиспользуется на executor (`_sharedColorMpb`),
`SetPropertyBlock` для `_Color` / `_BaseColor`.

Файл: `CoreAiWorldCommandExecutor.TrySetColor`.

### `LuaModRuntime.Tick` — аллокация массива каждый кадр

**Было:** `new Mod[_mods.Count]` + `CopyTo` на каждый tick.

**Стало:** переиспользуемый `List<Mod> _tickScratch` (Clear + fill под lock, iterate без lock).

## Некритичное / backlog (TODO)

| Область | Наблюдение | Рекомендация |
|---------|------------|--------------|
| `GameObject.Find` | Вызывается на каждый world/Lua query по имени | Кэш имя→id с invalidation при destroy; или instanceId из `unity_find` |
| Full reflection | `Type.GetType` + scan assemblies при первом обращении | Уже кэшируется в `ConcurrentDictionary`; мониторить cold-start |
| `LuaCoroutineRunner.Update` | Линейный проход `_handles`; prune через `_toRemove` | Приемлемо при cap 64; при росте — swap-remove |
| `LuaModsLlmTool.ListMods` | Аллокация списка DTO на вызов | OK для LLM tool (не per-frame) |
| Чат UI | Стриминг через MEAI callbacks | Нет Update()-поллинга; лишней работы не найдено |
| `DynValue.FromObject` в mod events | Боксинг args в handlers | Ограничено лимитами mod runtime |

## Методология

Статический обзор кода + выборочные EditMode/PlayMode прогоны (Lua EditMode 94 passed,
FastNoLlm PlayMode 24 passed, `LuaDynamicGameMechanicsTests` с LM Studio passed).

## Связанные документы

- `LUA_ACCESS_MODES_AUDIT_RU.md` — режимы доступа и Full
- `LUA_SANDBOX_SECURITY.md` — лимиты песочницы
