# CoreAI — ядро (`_source`)

**Быстрый старт:** [`Docs/QUICK_START.md`](Docs/QUICK_START.md) · **оглавление Docs:** [`Docs/DOCS_INDEX.md`](Docs/DOCS_INDEX.md) · **Example Game в Unity:** [`../_exampleGame/Docs/UNITY_SETUP.md`](../_exampleGame/Docs/UNITY_SETUP.md).  
**Старт для разработчиков:** [`Docs/DEVELOPER_GUIDE.md`](Docs/DEVELOPER_GUIDE.md) — карта кода, потоки LLM/Lua, тесты, типичные задачи.  
**Нормативный SPEC:** [`Docs/DGF_SPEC.md`](Docs/DGF_SPEC.md) (§4.1–4.2 портативное **CoreAI.Core** vs Unity **CoreAI.Source**, зависимости, LLMUnity, MCP).  
**Роли ИИ и оркестрация:** [`Docs/AI_AGENT_ROLES.md`](Docs/AI_AGENT_ROLES.md) (Creator, Analyzer, Programmer, AINpc, CoreMechanicAI, placement).  
**LLMUnity / OpenAI HTTP / Lua в игре:** [`Docs/LLMUNITY_SETUP_AND_MODELS.md`](Docs/LLMUNITY_SETUP_AND_MODELS.md).

**Автор библиотеки:** **Neoxider** (ник **neoxider**) — [GitHub @NeoXider](https://github.com/NeoXider); также [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools) для Unity.

## UPM-пакет `com.coreai.core`

Исходники **CoreAI.Core** и **CoreAI.Source** лежат в **`Packages/com.coreai.core/Runtime/`**; в корне пакета — **`package.json`**. Проект подключает пакет через **`Packages/manifest.json`**: `"com.coreai.core": "file:com.coreai.core"`.

## Сборки и папки

| Путь | Сборка | Назначение |
|------|--------|------------|
| `Packages/com.coreai.core/Runtime/Core/` | **CoreAI.Core** (`noEngineReferences`) | Портатив: `ILlmClient`, оркестратор, очередь, снимок сессии, MoonSharp |
| `Packages/com.coreai.core/Runtime/Source/` | **CoreAI.Source** | Unity: DI, маршрутизация LLM, LLMUnity/OpenAI HTTP, MessagePipe, лог |
| `Tests/EditMode/` | **CoreAI.Tests** (Editor) | EditMode NUnit: промпты, Lua, парсер конверта, песочница |
| `Tests/PlayMode/` | **CoreAI.PlayModeTests** (Editor) | Play Mode: оркестратор, опционально HTTP к LM Studio (env) |
| `Editor/` | **CoreAI.Editor** | Меню (Build Settings, открыть `_mainCoreAI`) |

### `Packages/com.coreai.core/Runtime/Source` (детальнее)

| Путь | Назначение |
|------|------------|
| `Composition/` | `CoreAILifetimeScope`, `RegisterCore` + `RegisterCorePortable`, entry points |
| `Infrastructure/` | Логирование, маршрутизация LLM, `ApplyAiGameCommand` → MessagePipe, LLM-адаптеры, `LuaAiEnvelopeProcessor`, биндинги Lua |
| `Presentation/AiDashboard/` | `AiDashboardPresenter` (IMGUI), `AiPermissionsAsset` |
| `Features/` | Узкие фичи ядра по мере роста |

## Запуск DI в сцене

1. Создайте пустой объект, например `CompositionRoot`.
2. Добавьте **`CoreAILifetimeScope`** (корень DI ядра; не путать с игровым `LifetimeScope` тайтла). При необходимости создайте asset **CoreAI → Logging → Game Log Settings** и назначьте в поле **Game Log Settings** (иначе — все категории и уровни через `DefaultGameLogSettings`). Включите категорию **`Llm`** для логов запроса/ответа модели (**`LoggingLlmClientDecorator`**); у старых ассетов бит **Llm** добавляется при открытии инспектора (**`OnValidate`**).
3. Опционально: **Llm Request Timeout Seconds** на том же компоненте (**15** по умолчанию, **0** = без автоотмены зависшего вызова).
4. **Auto Run** включён — в консоли сообщение от `CoreAIGameEntryPoint`.

## Фичи (паттерн как в Last-War)

- **Корень ядра:** один `CoreAILifetimeScope` на сцену шаблона (или на игровую сессию, если ядро грузится так).
- **Подфичи игры:** свои `LifetimeScope` с **Parent** = объект с `CoreAILifetimeScope` (или родительский игровой scope — по архитектуре тайтла).

## Логирование по фичам (как идея в GameDev-Last-War)

- Вызовы: `IGameLogger.Log*(GameLogFeature.XXX, "…")` — категории в `GameLogFeature` (в т.ч. **`Llm`** для трассировки вызовов модели).
- Фильтр: **`GameLogSettingsAsset`** — флаги **Enabled Features** и **Minimum Level**.
- **LLM:** оркестратор задаёт **`TraceId`** на задачу; он же в **`ApplyAiGameCommand`** и в логах **`AiGameCommandRouter`** — удобно фильтровать консоль по одному id.
- Замена бэкенда: новый приёмник вместо `UnityGameLogSink` или новая реализация `IGameLogger` в `RegisterCore`.

## Промпты агентов (системный + user)

- **Системный промпт** задаётся для каждого `roleId` (Creator, Programmer, `PlayerChat`, свой id): цепочка **манифест** (опционально) → **Resources** `AgentPrompts/System/<RoleId>.txt` → встроенный fallback в `CoreAI.Core`.
- **User-шаблон** (опционально): манифест или `Resources/AgentPrompts/User/<RoleId>.txt` с плейсхолдерами **`{telemetry}`** и **`{hint}`** (игронезависимый JSON телеметрии и подсказка задачи).
- **Create → CoreAI → Agent Prompts Manifest** — переопределения и блок **custom agents** для своих ролей.
- **Игровой чат:** `IInGameLlmChatService` (роль `PlayerChat`), UI-пример `Presentation/PlayerChat/InGameChatPanel`.

## MessagePipe и команды ИИ

`RegisterMessagePipe()` + `RegisterBuildCallback` с `GlobalMessagePipe.SetProvider(resolver.AsServiceProvider())` в `CoreServicesInstaller` — глобальный доступ к шине и регистрация обработчиков через VContainer.

Типы **`ApplyAiGameCommand`**: **`AiEnvelope`** (сырой ответ LLM), **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`** — см. `AiGameCommandTypeIds`; у команд есть **`TraceId`**. Разбор и запуск Lua: **`LuaAiEnvelopeProcessor`** (Core) + подписка в **`AiGameCommandRouter`** (Source). Реальный **`ILlmClient`** в рантайме оборачивается в **`LoggingLlmClientDecorator`** (лог, таймаут, unwrap для проверки stub). Подробнее — [DEVELOPER_GUIDE.md](Docs/DEVELOPER_GUIDE.md) §3–4 и [LLMUNITY_SETUP_AND_MODELS.md](Docs/LLMUNITY_SETUP_AND_MODELS.md).

## Референс: Lua в GameDev-Last-War

В репозитории **GameDev-Last-War** (`Assets/Scripts/LuaBehaviour/`) уже есть основа под **MoonSharp**:

- **Core:** менеджер/исполнитель скриптов, экземпляры, глобальные функции, **`LuaAsyncRunner`** (корутины + **UniTask**).
- **Infrastructure:** **`LuaMessagePipeAdapter`** — публикация и подписка на **MessagePipe** из Lua по имени типа DTO, userdata-объекты, интеграция с **VContainer**.

Используется в геймплее (туториал, отдельные use case’ы). Это **не** готовая «ИИ-песочница»: там шире доступ к игре, чем обычно допустимо для **ненадёжного кода от LLM**. Для CoreAI имеет смысл смотреть на **паттерны** (шина, DI, async), а слой безопасности (whitelist, лимиты, dry-run) проектировать отдельно.

**Переиспользование:** при необходимости допустимо **подтягивать или копировать адаптированные фрагменты** из `LuaBehaviour` Last-War, сужая API под песочницу. В приоритете — готовое решение, затем форк идей, затем свой код (как в политике `_exampleGame/README.md`).
