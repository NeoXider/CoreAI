# Арена: прогрессия (мета + сессия)

Реализация в стиле Vampire Survivors: **сессионный** XP/уровень и драфт карт, **мета** между забегами через `Neo.Save.SaveProvider`. Волны — сценарий `ArenaSurvival.UseCases.ArenaSurvivalDirector`; фаза 2: координатор не вшит в директор.

## Структура папок (чистая архитектура)

**Корень примера** `Assets/_exampleGame/RogueliteArena/`:

- `Composition/` — композиция приложения: `RogueliteArenaLifetimeScope` (VContainer)

Каждая фича под `Features/<Имя>/` делится на слои (папки = слои):

| Фича | Domain | UseCases | Presenter | View | Infrastructure |
|------|--------|----------|-----------|------|----------------|
| **ArenaSurvival** | контракты сессии (`IArenaSessionView`), `ArenaSimulationRole` | оркестрация волн (`ArenaSurvivalDirector`) | — | `ArenaSurvivalHud` | `ArenaSurvivalSession`, `ArenaSurvivalProceduralSetup` |
| **ArenaWaves** | `IArenaWaveSchedule`, планы, кривая сложности (модель) | `ArenaLocalWavePlanner`, `ArenaWavePlanValidator` | — | — | Creator/парсер, `ArenaLinearWaveSchedule`, SO пресетов и VS-кривой |
| **ArenaCombat** | — | — | — | — | игрок, враг, компаньон, слушатель AINpc |
| **ArenaCamera** | — | — | — | — | `ArenaFollowCamera` |
| **ArenaAi** | константы `ArenaAiSourceTags` | — | — | — | шина задач, триггеры, aux LLM |
| **ArenaBootstrap** | — | — | — | — | `ExampleRogueliteEntry`, хоткеи LLM/Lua |

**Прогрессия** `Features/ArenaProgression/`:

- `Domain` — состояние, enum, `IArenaCombatStats`
- `UseCases` — сценарии сохранения меты, XP, ролла, применения апгрейда
- `Presenter` — `ArenaUpgradeDraftPresenter`, заглушка `ArenaWaveUpgradeCoordinator`
- `View` — `ArenaUpgradeChoiceView`, `ArenaUpgradeCardWidget` (TMP + UI)
- `Infrastructure` — ScriptableObject и контент (`ArenaProgressionContent`, конфиги, `ArenaUpgradeDefinition`), рантайм-сервисы (ролл, сейв, Lua, сессионный хост, хаб, модель боя, мозги компаньона)

Пространства имён зеркалят слой: `CoreAI.ExampleGame.<Фича>.<Domain|UseCases|Presenter|View|Infrastructure>`.

## ScriptableObjects

Создание дефолтных ассетов: меню **CoreAI Example → Arena → Generate Progression Assets (Defaults)** (пишет в `Assets/_exampleGame/Settings/Progression/`).

- `ArenaUnitBaselineConfig` — стартовые статы игрока и компаньона
- `ArenaRunBalanceConfig` — ссылки на `LevelCurveDefinition` (сессия + мета), XP за килл, деление на команду, множители редкости, лимиты карточек
- `ArenaProgressionContent` — реестр апгрейдов и ссылки на `ChanceData` (редкость, категории по редкости, веса стат-пула)
- `ArenaUpgradeDefinition` — id, title, description, kind, rarity, statDelta
- `ArenaUpgradePresentationConfig` — спрайты/материалы рамок по редкости
- `ArenaPersistenceConfig` — ключ меты для SaveProvider (опционально; иначе дефолтный ключ в коде шлюза)

Neoxider: [Random / ChanceData](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Tools/Random), [Progression / LevelCurveDefinition](https://github.com/NeoXider/NeoxiderTools/tree/main/Assets/Neoxider/Docs/Progression).

### Индексы ChanceData

- **Редкость:** 0 Common, 1 Rare, 2 Epic, 3 Legendary
- **Категории Common/Rare:** 0 = Stat
- **Epic:** 0 Stat, 1 PassiveSlot
- **Legendary:** индексы ChanceData `0` Stat, `1` OfferExtraChoices, `2` LegendaryDoublePick (маппинг в `ArenaUpgradeRollService.TryMapCategory`)
- **StatUpgradeWeights:** индекс = порядок стат-апгрейдов в списке `ArenaProgressionContent.upgrades` (первые N только со стат-кайндами в дефолтной генерации)

## Сцена и проводка

На `ArenaSurvivalProceduralSetup` задайте **Arena Progression Content** и **Arena Unit Baseline Config**. При старте создаётся `ArenaProgressionSessionHost` (XP за килл, мета load/save, Lua).

Префаб UI драфта: повесьте `ArenaUpgradeChoiceView` + пул из пяти `ArenaUpgradeCardWidget`, назначьте ссылку в `ArenaProgressionSessionHost.draftView`. Без UI драфт можно вызвать из Lua (`arena_open_draft_debug`) — вид не откроется, если view null.

**Отладка:** `ArenaProgressionDebugHotkey` — клавиша **L** (по умолчанию) открывает драфт.

### Кривая сложности волн (VS-style)

Нелинейная сложность в духе Vampire Survivors: **к концу забега суммарно жёстче** (ramp по прогрессу волны), но **отдельные волны мягче** за счёт синусов по числу врагов и статам.

- **Назначение:** поле **VS Wave Difficulty** на `ArenaSurvivalProceduralSetup` (передаётся в `ArenaSurvivalDirector.Init` как override) **или** **Wave Difficulty Profile** на самом `ArenaSurvivalDirector`, если override не задан.
- **Ассет:** `ArenaVsStyleWaveDifficulty` — меню **Assets → Create → CoreAI Example → Arena → VS-style Wave Difficulty**, либо **CoreAI Example → Arena → Generate VS Wave Difficulty Asset** (пишет в `Assets/_exampleGame/Settings/Arena/ArenaVsWaveDifficulty.asset`).
- Множители накладываются на план Creator / локальный план / линейное расписание (число врагов, HP, урон, скорость, интервал спавна). В телеметрии — ключи `arena.wave.vs.*_mult`.

## Lua API (Creator и Programmer)

Регистрация через `GameLuaBindingsExtensibility` + `ArenaProgressionLuaBindings` (ядро агрегирует в `AggregatingGameLuaRuntimeBindings`).

| Функция | Назначение |
|--------|------------|
| `arena_add_session_xp(n)` | Сессионный XP (учёт деления — через хаб живых членов команды) |
| `arena_add_meta_xp(n)` | Мета-XP + пересчёт уровня по мета-кривой |
| `arena_save_meta()` / `arena_load_meta()` | SaveProvider JSON меты |
| `arena_apply_upgrade_id("id")` | Применить апгрейд по id из контента |
| `arena_open_draft_debug()` | Открыть экран выбора (нужен View) |

## Сеть и авторитет

Мутации забега — только при `IArenaSessionAuthority.IsAuthoritativeSimulation`. XP за смерть врага вызывается на авторитетном узле в `ArenaEnemyBrain.Die`.

## Фаза 2

Вставка `ArenaWaveUpgradeCoordinator` между волнами в `ArenaSurvivalDirector.RunWaves` — по плану отдельно.
