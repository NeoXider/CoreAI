# TODO

> Обновлено 2026-06-11. Всё выполненное удалено (история — в `CHANGELOG.md` обоих пакетов и git-логе). Здесь только открытые задачи.

## Инфраструктура
- [ ] **Настроить секреты GameCI в GitHub** (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) — без них workflow `.github/workflows/ci.yml` (матрица moonsharp / no-lua) не запустится.
- [ ] **GitHub Release / tag для v3.2.0** после пуша.

## [P1] Из аудита 2026-06-11 (детали: `Docs/AUDIT_2026-06-11_RU.md`)

### Lua / песочница
- [x] Кап на `string.rep` (аллокационная бомба в одну VM-инструкцию — `InstructionLimitDebugger` не успевает) — `SecureLuaEnvironment.StripRiskyGlobals`.
- [x] `LuaTool`/`execute_lua` идёт мимо `LuaGenerationRateLimiter` — прокинуть лимитер или задокументировать; поправить «rate-limited end to end» в `LUA_SANDBOX_SECURITY.md`.
- [x] Суммарный бюджет жизни корутины (total steps across resumes) в `LuaCoroutineHandle` — бесконечная yield-корутина сейчас бессмертна.
- [x] Валидация чисел в world-биндингах (NaN/Inf/диапазон, кап громкости), whitelist сцен для `coreai_world_load_scene`, кап/удаление `time_set_scale`.
- [x] Кап `ToPrintString()` и нормализация текстов ошибок перед publish в payload/repair-промпт (`LuaAiEnvelopeProcessor`).
- [x] Тесты по списку «Known Attack Vectors»: rep-бомба, глубокая рекурсия, `pcall`-поглощение лимитов, NaN-аргументы биндингов, регресс на `package`.

### Общее
- [x] `LlmUnityAutostartEntryPoint` — развести 4 исхода на 4 разных лог-сообщения.
- [x] `AskWithCallback`: маршалить `onDone` на main thread или явно задокументировать «может прийти не на main thread».
- [x] Логировать исключения целиком (`ex`), а не `ex.Message` (системно по catch-блокам).
- [x] CI: fail при отсутствии `COREAI_NO_LUA` после sed; skip-условие для PR из форков (нет секретов); рассмотреть `githubToken` для check-run.
- [x] `IDisposable` для `FileAgentMemoryStore`/`FileConversationSummaryStore` (dispose `SemaphoreSlim`); коллизии имён файлов после санитизации roleId.


## [P2] WebGL: Lua в веб-сборке (исследование)
- `SecureLuaEnvironment.IsSupported` returns `false` в WebGL-плеере (define-ветка `UNITY_WEBGL` и не `UNITY_EDITOR`) — MoonSharp-сэндбокс полностью отключен в WebGL.
- `LuaAiEnvelopeProcessor` уже публикует `"Lua execution is disabled on this platform"`.
- Файловые сторы и транспорт имеют WebGL-ветки (`inline` I/O + `IDBFS` sync).
- Исследовать:
  - совместимость MoonSharp с IL2CPP/AOT в WebGL (интерпретатор не требует JIT — вероятно достаточно снять define-gate и добавить `link.xml` против code stripping);
  - стоимость по размеру сборки, производительность интерпретатора в WASM;
  - лимиты инструкций/таймауты без потоков;
  - альтернативы — Lua-CSharp / wasm-lua.
- Отдельно: проверить вызовы `Task.Run` вне стора.

## [P1] Демо-сцены
- Нужны отдельные демо-сцены помимо чата: Lua-механики, MCP-механики, скиллы.
- Размещать в отдельной папке, например `Assets/CoreAI.Demos/`, НЕ внутри `Assets/CoreAI` и НЕ внутри `Assets/CoreAiUnity`.
- Каждая демо-сцена должна быть самодостаточна: сцена + минимальные скрипты + README.

## [P1] Lua как полноценный второй язык игры

> Цель: Lua должен уметь менять мир и логику механик во время игры, а не только дергать 8 write-only команд (`CoreAiWorldLuaRuntimeBindings`). Песочница и `StripRiskyGlobals` не ослабляются — растёт только поверхность биндингов. Этапы упорядочены по ценности/стоимости, каждый ценен сам по себе.

> Реализовано (2026-06-12, см. `Assets/CoreAI/Docs/LUA_GAME_API.md`): этапы 1–5 + capability-уровни. Ниже — остатки.

- [x] **Этап 1 — чтение мира (query-API)**: `coreai_world_exists/pos/find/list_prefabs/raycast` (`CoreAiWorldQueryLuaBindings`), `coreai_world_set_props` (whitelist: scale, color).
- [x] **Этап 2 — логические слоты**: `LuaLogicSlots` (`logic_define/reset/list`, `TryInvokeNumber/Bool/String` с fail-open и C#-дефолтом).
- [x] **Этап 3 — LuaModRuntime**: load/unload/reload, `hooks_on`/`hooks_every`, `store_set/get` (`FileLuaModStore`), пер-вызовные бюджеты, авто-выгрузка после 8 ошибок, `LuaModRuntimeTicker`.
- [x] **Этап 4 — уровневые примитивы**: `spawn_batch`/`grid`/`parent` + транзакции `begin/commit/rollback` (буфер до 256).
- [x] **Этап 5 — события**: `events_emit` / `hooks_on` между модами + `ModEventEmitted`/`EmitEvent` для игры.
- [x] **Capability-уровни**: `LuaCapabilities` + гейтинг групп биндингов в `AggregatingGameLuaRuntimeBindings`.
- Остатки:
  - [ ] Undo уже применённых команд (инверсные команды для spawn/move; «ИИ испортил уровень»).
  - [ ] Capability-уровень из конфига роли ИИ (сейчас задаётся кодом при создании агрегатора/LoadMod); опциональное подтверждение игрока для опасных уровней.
  - [ ] Мост `ModEventEmitted` → MessagePipe (сейчас прямая C#-подписка на DI-синглтон).
  - [ ] Бюджет команд на тик для модов (сейчас бюджеты на вызов хендлера + лимиты хендлеров/таймеров).

## [P2] Идеи (под вопросом, для последующей оценки)
- [ ] **STT → Agent → TTS pipeline для NPC** — локальный whisper, локальный TTS, потоковая передача эмоций/интонаций для анимаций.
- [ ] **Визуальный билдер AgentBuilder в редакторе** — панель промптов/агентов без кода.
- [ ] **Streaming-emotions / function-driven анимации** — расширение ответа: emotion/gesture для аниматора.

## Медиа / продвижение
- [ ] GIF-демки для README (см. `DEMO_RECORDING_GUIDE.md`).
- [ ] Публикация в OpenUPM.
- [ ] Ссылка Boosty в `FUNDING.yml`.
