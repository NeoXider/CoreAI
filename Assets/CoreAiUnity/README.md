# CoreAI — Unity-хост (`com.nexoider.coreaiunity`)

Сборка **`CoreAI.Source`** (Unity-рантайм: DI, LLM, MessagePipe, логи) в **`Runtime/Source/`**, плюс документация, **EditMode / PlayMode** тесты и **Editor**-меню. Зависит от **`com.nexoider.coreai`** (портативное **CoreAI.Core**). См. **`CHANGELOG.md`**.

| Куда смотреть | Что |
|---------------|-----|
| Быстрый старт и сцена | [`Docs/QUICK_START.md`](Docs/QUICK_START.md) |
| Карта кода, LLM/Lua, логи | [`Docs/DEVELOPER_GUIDE.md`](Docs/DEVELOPER_GUIDE.md) |
| Нормативный SPEC | [`Docs/DGF_SPEC.md`](Docs/DGF_SPEC.md) |
| Роли агентов | [`Docs/AI_AGENT_ROLES.md`](Docs/AI_AGENT_ROLES.md) |
| LLMUnity / OpenAI / Lua | [`Docs/LLMUNITY_SETUP_AND_MODELS.md`](Docs/LLMUNITY_SETUP_AND_MODELS.md) |
| Оглавление | [`Docs/DOCS_INDEX.md`](Docs/DOCS_INDEX.md) |
| Example Game | [`../_exampleGame/Docs/UNITY_SETUP.md`](../_exampleGame/Docs/UNITY_SETUP.md) |

**Автор:** Neoxider — [@NeoXider](https://github.com/NeoXider); экосистема [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools).

---

## Установка (вместе с ядром)

Рекомендуемый способ — **UPM → Add package from git URL** (как в [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools/blob/master/README.md#installation)), **два** URL из одного репозитория:

1. **`com.nexoider.coreai`** — [`https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI`](https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI)  
2. **`com.nexoider.coreaiunity`** — [`https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity`](https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity)

Подробности, пин версии — в **[`../CoreAI/README.md`](../CoreAI/README.md)** (пакет **`com.nexoider.coreai`** — только **CoreAI.Core** без Unity).

---

## Сборки в этом репозитории

| Путь | Сборка | Назначение |
|------|--------|------------|
| `Assets/CoreAI/Runtime/Core/` | **CoreAI.Core** | Портативное ядро (`noEngineReferences`), пакет **`com.nexoider.coreai`** |
| `Assets/CoreAiUnity/Runtime/Source/` | **CoreAI.Source** | Unity: DI, LLM, Lua, MessagePipe, лог (этот пакет) |
| `Tests/EditMode/Core/` и др. | **CoreAI.Tests** (Editor) | EditMode NUnit |
| `Tests/PlayMode/` | **CoreAI.PlayModeTests** (Editor) | Play Mode, опционально LM Studio по env |
| `Editor/` | **CoreAI.Editor** | Меню (Build Settings, открыть `_mainCoreAI`) |

### `Assets/CoreAiUnity/Runtime/Source` (детальнее)

| Путь | Назначение |
|------|------------|
| `Composition/` | `CoreAILifetimeScope`, `RegisterCore` + `RegisterCorePortable` |
| `Features/Logging/Infrastructure/` | `IGameLogger`, `GameLogFeature`, `GameLogSettingsAsset` |
| `Features/Llm/Infrastructure/` | OpenAI HTTP, LLMUnity, маршрутизация, таймауты |
| `Features/Lua/Infrastructure/` | Файловые store версий, агрегация биндингов Lua |
| `Features/Messaging/Infrastructure/` | `ApplyAiGameCommand` → MessagePipe, main thread |
| `Features/Prompts/Infrastructure/` | Манифест и Resources для промптов |
| `Features/AgentMemory/Infrastructure/` | Файловое хранилище памяти агента |
| `Features/OrchestrationMetrics/Infrastructure/` | Метрики оркестратора |
| `Features/Dashboard/Presentation/` | `AiDashboardPresenter`, `AiPermissionsAsset` |
| `Features/PlayerChat/Presentation/` | `InGameChatPanel` |
| `Features/Scheduling/Presentation/` | `AiScheduledTaskTrigger` |

---

## Тесты и логи

- **Запуск:** Unity **Window → General → Test Runner** (EditMode / PlayMode). Сборки `CoreAI.Tests` / `CoreAI.PlayModeTests` не обязаны полностью воспроизводиться через `dotnet test` без Unity; для CI ориентируйтесь на **Unity Test Framework** в редакторе.
- **Логи в рантайме:** только через **`IGameLogger`** и категории **`GameLogFeature`**; фильтр — **`GameLogSettingsAsset`**. Подробнее — [DEVELOPER_GUIDE §2.2](Docs/DEVELOPER_GUIDE.md), раздел «Логирование» в [README пакета ядра](../CoreAI/README.md).

---

## Запуск DI в сцене (кратко)

1. Пустой объект, например `CompositionRoot`.
2. Компонент **`CoreAILifetimeScope`**. При необходимости asset **CoreAI → Logging → Game Log Settings** и поле **Game Log Settings** (иначе — `DefaultGameLogSettings`). Для трассировки LLM включите фичу **`Llm`** в ассете.
3. Опционально **Llm Request Timeout Seconds** на том же компоненте (**0** = без таймаута).
4. При **Auto Run** — сообщение от `CoreAIGameEntryPoint`.

---

## Фичи и паттерны

- **Корень ядра:** один `CoreAILifetimeScope` на сцену шаблона (или на сессию).
- **Подфичи** — свой `LifetimeScope` с **Parent** = `CoreAILifetimeScope` (или родительский игровой scope).

## Логирование по фичам

- Вызовы: `IGameLogger.Log*(GameLogFeature.XXX, "…")`.
- Фильтр: **`GameLogSettingsAsset`** — **Enabled Features** и **Minimum Level**.
- **TraceId** в задачах оркестратора и в **`ApplyAiGameCommand`** — удобно искать в консоли по одному id.

## Промпты агентов

Системный и user: манифест → Resources `AgentPrompts/...` → встроенный fallback. **Create → CoreAI → Agent Prompts Manifest** для переопределений.

## MessagePipe и команды ИИ

`ApplyAiGameCommand` с типами `AiEnvelope`, `LuaExecutionSucceeded` / `LuaExecutionFailed` — см. `AiGameCommandTypeIds`; разбор Lua — **`LuaAiEnvelopeProcessor`** + **`AiGameCommandRouter`**. Реальный **`ILlmClient`** в рантайме оборачивается в **`LoggingLlmClientDecorator`**.

Подробнее — [DEVELOPER_GUIDE.md](Docs/DEVELOPER_GUIDE.md) §3–4 и [LLMUNITY_SETUP_AND_MODELS.md](Docs/LLMUNITY_SETUP_AND_MODELS.md).

---

## Референс: Lua в GameDev-Last-War

В репозитории **GameDev-Last-War** (`Assets/Scripts/LuaBehaviour/`) — паттерны MoonSharp + MessagePipe + async; для CoreAI смотреть **идеи**, а границы безопасности LLM — отдельно.
