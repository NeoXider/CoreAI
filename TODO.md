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

- [ ] **Этап 1 — чтение мира (query-API)**. Сейчас API write-only, ИИ строит вслепую.
  - [ ] `world.find(tag)`, `world.exists(name)`, `world.pos(name)`, `world.list_prefabs()`, `world.raycast(...)`.
  - [ ] `world.set_props(name, {scale=..., color=...})` — по curated-whitelist свойств, без сырого reflection.
- [ ] **Этап 2 — логические слоты (изменение механик)**. Обобщить паттерн LuaFormula: игра объявляет именованные слоты (`damage_formula`, `loot_table`, `spawn_director`, `price_curve`), вызывает Lua-функцию если зарегистрирована, иначе C#-дефолт. `logic.define(name, fn)` / `logic.reset(name)`. Игра контролирует, какие точки переопределяемы.
- [ ] **Этап 3 — персистентный рантайм (LuaModRuntime)**. Долгоживущие скрипты-моды вместо одноразовых конвертов.
  - [ ] Реестр загруженных модов, load/unload/reload, бюджет инструкций на тик (поверх `InstructionLimitDebugger`/`LuaCoroutineRunner`).
  - [ ] `hooks.on(event, fn)`, `hooks.every(seconds, fn)` — тик из Unity-слоя.
  - [ ] `store.set/get` — персистентный k/v на мод.
- [ ] **Этап 4 — уровневые примитивы**. Пакетные операции, чтобы генерация уровня не упиралась в rate-limit: `world.spawn_batch{...}`, `world.grid(prefab, x0,z0,x1,z1)`, `world.parent(child, parent)`; транзакции `world.begin()/commit()` с откатом (undo для «ИИ испортил уровень»).
- [ ] **Этап 5 — события**. `events.emit/on` — мост к MessagePipe; моды общаются с игрой и между собой.
- [ ] **Безопасность (сквозное)**: capability-уровни на мод (`read` / `gameplay` / `world_edit` / `logic_override`), уровень задаётся ролью ИИ из конфига; бюджет команд на тик; для опасных уровней — опциональное подтверждение игрока.

## [P2] Идеи (под вопросом, для последующей оценки)
- [ ] **STT → Agent → TTS pipeline для NPC** — локальный whisper, локальный TTS, потоковая передача эмоций/интонаций для анимаций.
- [ ] **Визуальный билдер AgentBuilder в редакторе** — панель промптов/агентов без кода.
- [ ] **Streaming-emotions / function-driven анимации** — расширение ответа: emotion/gesture для аниматора.

## Медиа / продвижение
- [ ] GIF-демки для README (см. `DEMO_RECORDING_GUIDE.md`).
- [ ] Публикация в OpenUPM.
- [ ] Ссылка Boosty в `FUNDING.yml`.
