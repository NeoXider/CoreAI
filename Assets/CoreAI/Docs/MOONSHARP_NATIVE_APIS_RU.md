# MoonSharp / Lua: нативные возможности vs наша реализация

> Аудит 2026-06-12. Источники: [moonsharp.org/sandbox](https://www.moonsharp.org/sandbox.html), [coroutines](https://www.moonsharp.org/coroutines.html), [objects/UserData](https://www.moonsharp.org/objects.html), [hardwire](https://www.moonsharp.org/hardwire.html).

Цель: не дублировать то, что MoonSharp и Lua уже дают из коробки, и использовать рекомендованные API там, где это безопасно.

## Что уже сделано правильно

| Область | Решение CoreAI | Почему это нативно / оправдано |
|---|---|---|
| **Модули песочницы** | `CoreModules.Preset_HardSandbox \| Coroutine` | Официальный preset MoonSharp: string/math/table/bit32 без io/os/load/debug |
| **StripRiskyGlobals** | Ручное `Nil` для load/require/package/collectgarbage, кап `string.rep` | Preset не убирает package и collectgarbage полностью; `string.rep` — одна VM-инструкция, лимитер шагов не успевает — **свой cap обязателен** |
| **One-shot лимиты** | `InstructionLimitDebugger` + `IDebugger.GetAction` | MoonSharp не имеет встроенного hard step limit; debugger API — документированный способ |
| **Корутины кадра** | `Script.CreateCoroutine` + `coroutine.yield()` в Lua | Стандартный Lua/MoonSharp паттерн; `LuaCoroutineRunner` только тикает Resume |
| **Kill корутины** | `Coroutine.AutoYieldCounter = 1` + `_disposed` | MoonSharp не даёт ForceKill; AutoYieldCounter — рекомендованный механизм (см. docs) |
| **YieldRequest** | Цикл drain в `LuaCoroutineHandle.Resume` | Обязательно при preemptive yield (AutoYieldCounter) |
| **Регистрация API** | `globals[name] = clrDelegate` в `LuaApiRegistry` | MoonSharp маршалит типизированные `Func`/`Action` без DynamicInvoke |
| **Logic slots / mod hooks** | C# `InvokeGuarded` + `LuaExecutionGuard` | Ошибки на стороне хоста; `pcall` в Lua не включён намеренно (см. ниже) |
| **Optional module** | `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` | Сборка без MoonSharp — stub/null в DI |

## Что намеренно НЕ включено из MoonSharp

| Модуль / API | Причина |
|---|---|
| `ErrorHandling` (pcall/xpcall) | SoftSandbox; скрипт может глотать ошибки и обходить C# fail-open; ошибки ловит хост |
| `Metatables` | Усложняет escape через `__index`/`__newindex`; world API — явные функции |
| `LoadMethods`, `IO`, `OS_System`, `Debug` | Файлы, процессы, eval, introspection |
| `Dynamic`, `Json` (MoonSharp) | Лишняя поверхность; JSON — через C# envelope |
| `Preset_SoftSandbox` / `Preset_Default` | Слишком широко для untrusted AI-скриптов |

## Где своя реализация оправдана

| Свой код | Альтернатива MoonSharp | Вердикт |
|---|---|---|
| `LuaModRuntime` hooks_on / hooks_every / store | Нет в MoonSharp | **Оставить** — игровой рантайм модов |
| `LuaLogicSlots` | Нет | **Оставить** — контракт «слот + C# default» |
| World command envelopes | Нет | **Оставить** — main-thread + валидация |
| `InstructionLimitDebugger` на каждый Resume | `AutoYieldCounter` | **Оставить debugger** для hard fail на N шагах без yield; AutoYieldCounter даёт cooperative slice, не throw |
| `CoreAiFullUnityLuaRuntimeBindings` (reflection) | `UserData.RegisterType<T>()` | **Planned migrate** — см. ниже |

## Full-режим: UserData вместо reflection (Planned)

Сейчас Full-tier (`unity_find`, `unity_get_member`, …) — кастомная reflection-обёртка.

**Рекомендация MoonSharp** для CLR interop:

```csharp
UserData.RegistrationPolicy = InteropRegistrationPolicy.Manual; // никогда Automatic
UserData.RegisterType<Transform>(InteropAccessMode.LazyOptimized);
UserData.RegisterType<Rigidbody>(...);

// В Lua:
local go = unity_find("Player")  -- UserData.Create(go)
go.transform.position = Vector3(1, 2, 3)  -- если Transform зарегистрирован
```

Плюсы: типизированный marshalling, `[MoonSharpUserData]`, `[MoonSharpHide]`, hardwire для IL2CPP, без `MethodInfo.Invoke` на горячем пути.

Минусы: нужно явно регистрировать каждый тип (или генерировать); blacklist типов — отдельная политика (см. `LUA_ACCESS_MODES_AUDIT_RU.md` Planned).

**Промежуточный шаг (текущий):** reflection API с кэшем Type/Member — работает для opt-in Full, но не идиоматичен для MoonSharp.

## Производительность (кратко)

1. **IDebugger на каждую инструкцию** (`IsPauseRequested` + `StepIn`) — дорого на горячих корутинах. Альтернатива для time-slicing: только `AutoYieldCounter` без debugger (не hard-limit). Текущий выбор: точный hard-limit важнее FPS на AI-скриптах.
2. **`LuaApiRegistry`** — после аудита: прямое присвоение delegate в globals (без DynamicInvoke-обёртки).
3. **`set_color` через `renderer.material`** — Unity API, не MoonSharp; для частых вызовов — `MaterialPropertyBlock` (см. PERF review).

## Lua 5.x vs MoonSharp

- MoonSharp — Lua 5.2-подобный диалект, не 100% LuaJIT/Lua 5.4.
- `bit32` есть (Preset_HardSandbox); `#` operator / `goto` — проверять по версии пакета в проекте.
- Стандартные `coroutine.*` доступны при включённом `CoreModules.Coroutine`.

## Чеклист для новых биндингов

1. Предпочитать **типизированный delegate** (`Func<...>`, `Action<...>`) в `LuaApiRegistry.Register`.
2. Для CLR-объектов — **`UserData.RegisterType`** + `[MoonSharpHide]` на опасных членах, не reflection (Full tier — временное исключение).
3. Не добавлять модули CoreModules без review (особенно Metatables, ErrorHandling, LoadMethods).
4. Долгоживущие скрипты — **`Script.CreateCoroutine` + yield**, не busy-loop в one-shot chunk.
5. Tail-call из C# callback в Lua — только через `DynValue.NewTailCallReq` (редко, см. MoonSharp coroutine caveats).

Подробные **✅/❌** — [LUA_BEST_PRACTICES_RU.md](LUA_BEST_PRACTICES_RU.md).

## Связанные документы

- [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) — границы безопасности
- [LUA_GAME_API.md](LUA_GAME_API.md) — игровой API для скриптов
- [LUA_BEST_PRACTICES_RU.md](LUA_BEST_PRACTICES_RU.md) — лучшие практики и антипаттерны
- `LUA_ACCESS_MODES_AUDIT_RU.md` — режимы доступа (Read → Full), planned blacklist
