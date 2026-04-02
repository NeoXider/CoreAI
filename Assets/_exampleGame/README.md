# Example Game — демо на шаблоне CoreAI

**Автор шаблона CoreAI:** **Neoxider** (ник **neoxider**) — [github.com/NeoXider](https://github.com/NeoXider).

Пример игры в **`Assets/_exampleGame`** использует **UPM** **`com.nexoider.coreai`** (код в **`Assets/CoreAI`**) и хост **`Assets/CoreAiUnity`** (доки, **`Resources/AgentPrompts`**): процедурная арена с волнами, DI (**`CoreAILifetimeScope`**), вызов **Creator** на каждую волну (**`ArenaCreatorWavePlanner`**), демо **Programmer** по **F9** (**`CoreAiLuaHotkey`**). Логи ядра: **`[Llm]`** + **`traceId`** в **`ApplyAiGameCommand`** — см. [LLMUNITY_SETUP_AND_MODELS.md](../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md).

**Пошаговая настройка в Unity (сцена, LLM, HTTP):** [`Docs/UNITY_SETUP.md`](Docs/UNITY_SETUP.md). **Архитектура арены, мультиплеер, ИИ (волны / анализ игрока):** [`Docs/ARENA_ARCHITECTURE_AND_AI.md`](Docs/ARENA_ARCHITECTURE_AND_AI.md). В меню редактора: **CoreAI → Development → Example Game → Open RogueliteArena scene** (и опция сделать сцену первой в Build Settings). Общий быстрый старт по репозиторию: [`../CoreAiUnity/Docs/QUICK_START.md`](../CoreAiUnity/Docs/QUICK_START.md). Онбординг по коду шаблона: [`../CoreAiUnity/Docs/DEVELOPER_GUIDE.md`](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md).

Подробный геймплейный концепт забега и меты: [`Docs/ROGUELITE_PLAYBOOK.md`](Docs/ROGUELITE_PLAYBOOK.md).

---

## О игре

**Жанр:** roguelite-арена / выживание с **мета-прогрессией**.

**Сессия (забег):** короткий забег **примерно 10–25 минут** — волны врагов на арене, лут и апгрейды внутри забега, условие победы/поражения (например, здоровье ядра/арены, таймер).

**После поражения или завершения:** экран итогов → **база / хаб**, где тратится валюта и открываются **разблокировки** (пассивки, оружие, персонажи).

**Соло:** один игрок, один авторитетный поток правил (локально — тот же «хост», что и в мультиплеере).

**Кооп:** несколько игроков на той же арене; смена правил, волны и вызовы ИИ — у **хоста**; клиенты получают согласованные события и состояние. Мета-прогрессия может быть общей, личной или смешанной (решается на этапе дизайна сохранений).

**Зачем такой пример для CoreAI:** мало уникального арта, много **чисел, аффиксов, волн** — удобно подключать **процедурную логику и ИИ** (аффиксы недели, состав волн, сюрприз-раунды) без постоянного ручного контента.

---

## Стек (пример игры + репозиторий CoreAI)

### Уже в проекте CoreAI (`Packages/manifest.json`)

| Компонент | Назначение |
|-----------|------------|
| **Unity 6 + URP** | Рендер, шаблон проекта |
| **Input System** | Ввод |
| **VContainer** (`jp.hadashikick.vcontainer`, как в Last-War) | DI, `LifetimeScope` |
| **MessagePipe** + **MessagePipe.VContainer** | Шина сообщений + регистрация в контейнере |
| **R3** (`com.cysharp.r3`) | Реактивность для UI и состояния |
| **UniTask** | Асинхронность без лишних аллокаций |
| **MoonSharp** (`org.moonsharp.moonsharp`) | Песочница Lua под сценарии / use case (в связке с **CoreAI**) |
| **AI Navigation** | Агенты на сетке/навмешах (по мере надобности) |
| **UGUI / UI Toolkit** (через модули Unity) | Интерфейсы хаба и забега |
| **Test Framework** | Тесты |

Плагины в `Assets/Plugins` (например отладочные утилиты) — по факту репозитория; в README ядра они не считаются обязательной частью **шаблона**.

### Пакет **`Assets/CoreAI`** и хост **`Assets/CoreAiUnity`** (в этом репозитории)

| Компонент | Назначение |
|-----------|------------|
| **LLMUnity** + **OpenAI-compatible HTTP** | Реализации **`ILlmClient`**; см. [`LLMUNITY_SETUP_AND_MODELS.md`](../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md) |
| **Оркестрация** | **`IAiOrchestrationService`** / **`AiOrchestrator`**, роли из **`BuiltInAgentRoleIds`** |
| **Lua** | **`LuaAiEnvelopeProcessor`**, песочница MoonSharp, ремонт Programmer при ошибке |

Пример игры **зависит** от публичного API **CoreAI** (**`com.nexoider.coreai`**), а не наоборот: в `_exampleGame` — только геймспецифичные сцены, префабы, презентеры и use case’ы режима «арена + хаб».

---

## SPEC шаблона CoreAI (сжатая выжимка)

Нормативный документ: **`Assets/CoreAiUnity/Docs/DGF_SPEC.md`**.

1. **Границы:** ядро даёт DI, события, песочницу Lua, фасад LLM и оркестратор; игра даёт контент, префабы, баланс и правила режима.
2. **Безопасность:** Lua только через whitelist API, лимиты инструкций/времени, dry-run при необходимости; клиент не исполняет сырой вывод LLM как истину в мультиплеере.
3. **Сеть:** ИИ и смена «законов» забега — на **хосте**; реплицируются итоговые события и параметры.
4. **Слои (как ориентир):** Domain → UseCases → Presentation; инфраструктура (сейв, сеть) за интерфейсами — в духе [GameDev-Last-War](D:\Git\GameDev-Last-War), но без копирования всего монолита.
5. **Наблюдаемость:** логи решений ИИ, опционально панель разработчика (очередь запросов, активные агенты).

Корневая продуктовая идея репозитория: [README.md](../../README.md) в корне CoreAI.

---

## Политика разработки: сначала готовое, своя логика — в последнюю очередь

**Обязательный порядок** при добавлении любой нетривиальной возможности (сеть, DI, пулы, UI-паттерны, сохранения, волны врагов, инвентарь меты и т.д.):

1. **Поиск готового решения на GitHub** (пакет UPM, проверенный репозиторий, официальная документация Unity/Cysharp и т.п.).
2. **Поиск и адаптация паттернов в референс-проекте [GameDev-Last-War](D:\Git\GameDev-Last-War)** — там уже есть продакшен-уровень: VContainer, MessagePipe, R3, UniTask, разбиение на фичи, ECS для тяжёлых мест, интеграции. Брать **идеи и фрагменты подхода**, а не весь репозиторий целиком, если задача узкая.
3. **Только если** подходящего открытого решения или близкого аналога в Last-War **не найдено** — писать **собственную** реализацию с нуля (или минимальный код-«склейка»).

**Цель:** меньше багов, быстрее итерации, единообразие со стеком шаблона. Собственный код — осознанное исключение, а не первая реакция.

---

## Структура папки `_exampleGame`

| Путь | Назначение |
|------|------------|
| `RogueliteArena/` | Код примера (`CoreAI.ExampleGame`), bootstrap сцены |
| `RogueliteArena/Features/` | Фичи **примера** (волны, хаб, UI забега) — свои установщики / дочерний `LifetimeScope` |
| `Docs/` | Игровой концепт и заметки (`ROGUELITE_PLAYBOOK.md`) |

Точка входа: `RogueliteArena/Bootstrap/ExampleRogueliteEntry.cs` (арена + **`CoreAiLuaHotkey`**). Сцена **`RogueliteArena`**: на **`CompositionRoot`** — **`CoreAILifetimeScope`** + **`ExampleRogueliteEntry`**. Состояние забега: **`ArenaSurvivalSession`** (без синглтона), волны — **`ArenaSurvivalDirector`** + **`IArenaWaveSchedule`**, роль узла — **`ArenaSimulationRole`**. См. [`../CoreAiUnity/README.md`](../CoreAiUnity/README.md) и [`../CoreAI/README.md`](../CoreAI/README.md) (UPM).

Паттерн: корневой `CoreAILifetimeScope` (ядро CoreAI) + при необходимости дочерний `LifetimeScope` в этой папке только для кода roguelite-примера.
