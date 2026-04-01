# CoreAI — ядро (`_source`)

**Нормативный SPEC:** [`Docs/DGF_SPEC.md`](Docs/DGF_SPEC.md) (§4.1–4.2 портативное **CoreAI.Core** vs Unity **CoreAI.Source**, зависимости, LLMUnity, MCP).  
**Роли ИИ и оркестрация:** [`Docs/AI_AGENT_ROLES.md`](Docs/AI_AGENT_ROLES.md) (Creator, Analyzer, Programmer, AINpc, CoreMechanicAI, placement).

## Структура `Runtime`

| Путь | Назначение |
|------|------------|
| `Composition/` | `GameLifetimeScope`, `CoreServicesInstaller` (VContainer + MessagePipe + `GlobalMessagePipe`) |
| `Infrastructure/Logging/` | `IGameLogger`, фильтр по фичам, `GameLogSettingsAsset`, приёмник `UnityGameLogSink` |
| `Features/` | Модули ядра (оркестратор ИИ, песочница Lua и т.д.) |

## Запуск DI в сцене

1. Создайте пустой объект, например `CompositionRoot`.
2. Добавьте **`GameLifetimeScope`**. При необходимости создайте asset **CoreAI → Logging → Game Log Settings** и назначьте в поле **Game Log Settings** (иначе — все категории и уровни через `DefaultGameLogSettings`).
3. **Auto Run** включён — в консоли сообщение от `CoreAIGameEntryPoint`.

## Фичи (паттерн как в Last-War)

- **Корень:** один `GameLifetimeScope` на игру / сессию.
- **Подфичи:** дочерние `LifetimeScope` с **Parent** = корень.

## Логирование по фичам (как идея в GameDev-Last-War)

- Вызовы: `IGameLogger.Log*(GameLogFeature.XXX, "…")` — категории в `GameLogFeature` (расширяйте enum).
- Фильтр: **`GameLogSettingsAsset`** — флаги **Enabled Features** и **Minimum Level**.
- Замена бэкенда: новый приёмник вместо `UnityGameLogSink` или новая реализация `IGameLogger` в `RegisterCore`.

## MessagePipe

`RegisterMessagePipe()` + `RegisterBuildCallback` с `GlobalMessagePipe.SetProvider(resolver.AsServiceProvider())` в `CoreServicesInstaller` — глобальный доступ к шине и регистрация обработчиков через VContainer.

## Референс: Lua в GameDev-Last-War

В репозитории **GameDev-Last-War** (`Assets/Scripts/LuaBehaviour/`) уже есть основа под **MoonSharp**:

- **Core:** менеджер/исполнитель скриптов, экземпляры, глобальные функции, **`LuaAsyncRunner`** (корутины + **UniTask**).
- **Infrastructure:** **`LuaMessagePipeAdapter`** — публикация и подписка на **MessagePipe** из Lua по имени типа DTO, userdata-объекты, интеграция с **VContainer**.

Используется в геймплее (туториал, отдельные use case’ы). Это **не** готовая «ИИ-песочница»: там шире доступ к игре, чем обычно допустимо для **ненадёжного кода от LLM**. Для CoreAI имеет смысл смотреть на **паттерны** (шина, DI, async), а слой безопасности (whitelist, лимиты, dry-run) проектировать отдельно.

**Переиспользование:** при необходимости допустимо **подтягивать или копировать адаптированные фрагменты** из `LuaBehaviour` Last-War, сужая API под песочницу. В приоритете — готовое решение, затем форк идей, затем свой код (как в политике `_exampleGame/README.md`).
