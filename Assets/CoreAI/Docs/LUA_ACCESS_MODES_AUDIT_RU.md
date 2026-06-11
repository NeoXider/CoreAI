# Аудит режимов доступа ИИ к игре (CoreAI Lua)

Дата: 2026-06-12. Связанная реализация: `LuaCapabilities`, `AggregatingGameLuaRuntimeBindings`,
`CoreAiFullUnityLuaRuntimeBindings`, `CoreAILifetimeScope.enableFullLuaAccess`.

## Концепция

Как в Cursor: несколько **уровней доверия** к коду, который пишет модель. Каждый уровень —
флаг в `LuaCapabilities`; биндинги регистрируются только для пересечения «уровень скрипта ∩
уровень хоста». Без флага функции **физически отсутствуют** в globals (fail-closed).

| Режим | Флаг | Что может Lua |
|-------|------|----------------|
| Read-only | `Read` | Логи, запросы мира, версии — без побочных эффектов |
| Gameplay | `Gameplay` | Time scale, UI-текст, звук, анимации |
| WorldEdit | `WorldEdit` | spawn/move/destroy, сцены, batch world-команды |
| Logic | `LogicOverride` | `logic_define`, моды (`hooks_on`, `manage_mods`) |
| **Full** | `Full` | Reflection к любым `GameObject`/компонентам (`unity_*`) |

`LuaCapabilities.All` = все стандартные tier **кроме Full**. Full включается явно:
- галочка **Enable Full Lua Access** на `CoreAILifetimeScope`;
- или `LoadMod(..., caps | Full)` / `manage_mods` с host-granted caps.

## Full-режим (реализовано)

**Политика: allow-all, blacklist — позже.** Сейчас Full даёт доступ ко всем публичным/
непубличным полям и методам компонентов через:

- `unity_find(name)` → instanceId
- `unity_get/set_position`, `unity_list_components`
- `unity_get_member` / `unity_set_member` / `unity_call`

Кэш: `ConcurrentDictionary` для `Type` и `MemberInfo`. Песочница MoonSharp, лимиты
инструкций/времени на chunk и mod-handlers **не ослабляются**.

### Planned — blacklist (не реализовано)

Будущий интерфейс (идея):

```csharp
public interface IFullLuaAccessBlacklistPolicy
{
    bool IsTypeAllowed(Type componentType);
    bool IsMemberAllowed(MemberInfo member);
}
```

Хост регистрирует политику в DI; `CoreAiFullUnityLuaRuntimeBindings` проверяет перед
get/set/call. Предлагаемый deny-list по умолчанию: `System.*`, `UnityEngine.Application.Quit`,
сетевые/файловые API если когда-либо попадут в reflection surface.

Дополнительные митigations (roadmap):

- подтверждение игрока перед первым Full-вызовом в сессии;
- подпись модов в `FileLuaModStore`;
- capability из конфига роли (TODO в AgentPromptsManifest).

## Риски

| Риск | Митigation сейчас |
|------|-------------------|
| Модель ломает сцену | Opt-in Full; mod error budget + auto-unload |
| Reflection escape | MoonSharp sandbox; нет произвольного C# |
| Material leak при set_color | Исправлено: `MaterialPropertyBlock` в executor |
| Спам LLM Lua | `LuaGenerationRateLimiter` |
| Загрузка чужих сцен | `luaAllowedScenes` whitelist на scope |

## LLM-инструменты модов

`manage_mods` (list / get_source / load / reload / unload) + `execute_lua` для Programmer
регистрируются в `WorldCommandsInstaller`. Исходник модов: `LuaModRuntime.TryGetModSource`.

## Расширение world-команд

`ICoreAiCustomWorldCommandHandler` + `CoreAiWorldCommandExecutor.RegisterCustomHandler` —
игра добавляет свои action без правки пакета.

## Демо

- `LiveMechanics` — logic slots + чат + LLM
- `FullAccess` — Full + чат + LLM (Full opt-in на scope)
