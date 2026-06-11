# TODO

> Обновлено 2026-06-10 по итогам код-аудита. Статусы проверены против `Assets/` (worktrees `.kilo/` игнорируются).

## [P0] Критически важное для репозитория (quality gate)
- [~] **Очистка корня репозитория от артефактов сборки и лога**
  - [x] Удалить из репозитория и добавить в `.gitignore`: `debug.log`, `memory.db`, `msp_server.log`, `replay_pid29472.log`, `TestRun_EditMode.log`, `UnityTest_EditMode.log` — файлы отсутствуют, в `.gitignore`, не трекаются.
  - [x] Убрать ошибочный файл `Remove-Item` (артефакт PowerShell) — отсутствует.
  - [x] Убрать placeholder-файлы `_coreai_placeholder_lines.txt` — отсутствует.
  - [x] Прописать pre-commit/pre-push проверку на отсутствие мусорных файлов и рецидивов `.meta`/логов в корне — `hooks/pre-commit` (POSIX sh), активация `git config core.hooksPath hooks`, описано в `CONTRIBUTING.md` (v3.1.0).

## [P0] Lua как опциональный модуль — ГОТОВО (v3.0.0)
- [x] **Lua-песочница опциональна через define `COREAI_NO_LUA`** (зеркало существующего `COREAI_NO_LLM`, а не отдельный asmdef — консистентно с проектом и ниже риск).
  - [x] Все MoonSharp-файлы Core + `LuaCoroutineRunner` (Source) обёрнуты `#if !COREAI_NO_LUA`.
  - [x] `CorePortableInstaller` / `WorldCommandsInstaller` — Lua-регистрации под define; иначе fallback `CoreDefaultLuaRuntimeBindings` / `NullLuaExecutionObserver`.
  - [x] `AiGameCommandRouter` — зависимость `LuaAiEnvelopeProcessor` компилируется прочь под define (не hard-зависимость DI).
  - [x] Lua EditMode/PlayMode тесты обёрнуты; обе конфигурации компилируются с 0 ошибок (проверено в Unity).
  - [x] (опц.) CI-матрица: `.github/workflows/ci.yml` — EditMode-тесты с MoonSharp и в `no-lua`-конфигурации (пакет удаляется из manifest/lock, `COREAI_NO_LUA` добавляется во все платформы); удаление `org.moonsharp.moonsharp` задокументировано в `LUA_SANDBOX_SECURITY.md` (v3.2.0). Требуются секреты GameCI (`UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD`).

## [P1] КРИТИЧЕСКИЕ баги (из аудита) — ГОТОВО, проверено в Unity (0 ошибок компиляции, EditMode 910/914)
- [x] **HttpClient создаётся per-request — socket exhaustion** — shared `Lazy<HttpClient>` поверх `HttpClientHandler` (НЕ `SocketsHttpHandler` — тот требует .NET Std 2.1, проект на 2.0), per-request таймаут через linked `CancellationTokenSource`. `HttpClientOpenAiTransport.cs`.
  - [x] ⚠️ API Compatibility Level: подтверждено падение на `SocketsHttpHandler` → заменено на `HttpClientHandler`.
  - [x] ⚠️ Форма исключения при таймауте теперь `OperationCanceledException` без inner `TimeoutException` — задокументировано в `HTTP_TRANSPORT_SPEC.md`.
- [x] **`LuaCoroutineHandle.Kill()` — пустое тело** — `AutoYieldCounter` форсит yield + `_disposed` гарантирует невозобновляемость; типизированные catch. (MoonSharp не даёт hard-kill — предел API.)
- [x] **Атомарная запись JSON-хранилищ** — `AtomicWriteAllText` (tmp + `File.Replace`/`File.Move`) в `FileAgentMemoryStore.cs` (4 места) и `FileConversationSummaryStore.cs`.
- [x] **Регрессия `CoreAIFacade`** — убран `[RuntimeInitializeOnLoadMethod]` (UnityEngine) из portable-ядра, ломавший компиляцию; сброс `CoreAIAgent` теперь из Unity-слоя (`CoreAi.Invalidate()`).
- [x] **Порядок проверок `AgentConfigExtensions.AskAsync`** — валидация роли до проверки orchestrator==null (тест `AskAsync_Fails_WhenRoleNotRegistered` теперь зелёный; раньше не компилировался).

> Предсуществующие падения EditMode (не связаны с этими правками): 3× `CoreAISettingsAssetEditModeTests.Preset_*` (нет ассетов `grok/open/minmaxFree.preset` в `Assets/Resources`), 1× `SceneLlmToolEditModeTests.SetTransformAsync_PartialValues` (отсутствует обяз. параметр `py`).

## [P1] Lua-песочница (security / stability)
- [ ] **LuaCoroutineRunner — лимит активных корутин**
  Лимита нет, `Register()` растёт неограниченно.
  - [x] Реализовать `MaxActiveCoroutines = 64` и hard-stop при превышении — `Register()` чистит мёртвые handles и бросает `InvalidOperationException` при переполнении; слоты освобождаются сразу (v3.1.0).
- [ ] **Sandbox escape-тесты**
  Покрыты `io/os/debug/load/require` и `_G`, но не векторы ниже.
  - [x] Добавить тесты: `string.dump`, `coroutine.close`, `collectgarbage` (timing oracle), обходы `_G`/`_ENV` — заодно закрыты реальные дыры: `string.dump` и `collectgarbage` теперь вырезаются в `StripRiskyGlobals` (v3.1.0).
  - [x] Зафиксировать coverage в CI — moonsharp-нога CI падает, если фикстура `SecureLuaSandboxEditModeTests` не исполнилась в результатах EditMode (v3.2.0).
- [x] **Генерация Lua-скриптов**
  - [x] Rate-limit и защита от runaway-циклов — `LuaGenerationRateLimiter` (sliding window, по умолчанию 20/60с) в `LuaAiEnvelopeProcessor`: слоты тратят и исполнение конвертов, и планирование Programmer-ремонтов; при насыщении конверт падает без ремонта (v3.2.0). Per-script лимиты (инструкции/таймаут) уже были в `InstructionLimitDebugger`.
- [x] ~~**Двойное присоединение `InstructionLimitDebugger`**~~ — проверено: в `SecureLuaEnvironment` debugger создаётся и аттачится один раз и в `CreateScript`, и в `CreateCoroutine`; экземпляр, переданный в `LuaCoroutineHandle`, совпадает с активным на скрипте.

## [P1] SmartToolCallingChatClient / ретраи
- [ ] **TryRepairToolName — метрика ремонтов**
  `ToolExecutionPolicy.cs:177-184` — только `Warn`, счётчика нет.
  - [x] Добавить инкремент метрики/счётчика — `ToolExecutionPolicy.ToolNameRepairCount` (Interlocked, process-wide) + `ResetToolNameRepairCount()`; в `RateLimiterMetrics` не встраивался (снимок rate-limiter без доступа к policy) (v3.1.0).
- [ ] **Жизненный цикл retry-feedback**
  Error-feedback не удаляется после успешного ретрая — остаётся в history до общего trim.
  - [x] После успешного ретрая удалять error-feedback из history — пары assistant tool-call + tool-result полностью провальных батчей удаляются целиком после успешной итерации; частично успешные сохраняются (v3.1.0).
- [x] ~~**TrimToolCallHistory — OpenAI-совместимость**~~ — проверено: trim удаляет tool-related сообщения интерлив-парами (assistant tool-call + tool-result), история остаётся валидной.

## [P1] Надёжность / устойчивость
- [ ] **Backoff без jitter — thundering herd**
  `LoggingLlmClientDecorator.cs:277-280` — детерминированный `2*2^attempt`, без рандомизации.
  - [x] Добавить jitter — full jitter `[0, min(2*2^attempt, 30)]`, `Retry-After` в приоритете; `ComputeBackoffBase`/`ComputeBackoffDelay` тестируемы (v3.1.0).
- [ ] **Off-main-thread I/O**
  `FileAgentMemoryStore.cs` — все чтения/записи синхронны на main thread (фриз кадра при большой памяти).
  - [x] Перенести I/O в background-поток — интерфейсы синхронны, добавлены `*Async`-методы на `FileAgentMemoryStore` и `FileConversationSummaryStore` (`Task.Run` + per-store `SemaphoreSlim`, атомарная запись сохранена, WebGL inline) (v3.1.0).

## [P1] CoreAi / CoreAIAgent / Policy runtime-state
- [x] ~~**Build/policy-init contract**~~ — `Build()` применяется к `CoreAIAgent.Policy` (`AgentBuilder.cs:328-338`), `BuildDetached()` — чистый режим без мутации (`:343`).
- [x] ~~**Fail-fast при незарегистрированной роли**~~ — `AgentConfigExtensions.ValidateRoleRegistered:61-74` бросает `InvalidOperationException`; молчаливого fallback на `"Blacksmith"` в коде нет.
- [x] ~~**Сброс static-состояния между Play Mode**~~ — `CoreAIFacade.cs:74-79` `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` → `Reset()` (`:67-72`).
- [x] ~~**Тестируемость / SetResolver**~~ — `CoreAi.SetResolver(Func<IAiOrchestrationService>)` есть в `CoreAi.cs:71`.

## [P1] Диагностика бюджета токенов в рантайме
- [x] `RateLimiterMetrics` отдаётся через `IInGameLlmChatService.GetRateLimiterMetrics()`; overlay реализован (v3.1.0).
  - [x] Overlay: текущая стоимость токенов и `tokens/request` — `CoreAiTokenBudgetOverlay` (IMGUI, хоткей F10) поверх `TokenBudgetCalculator`.
  - [x] Стоимость сессии (`$/session`) — конфигурируемые цены за 1K токенов; при 0 показываются только токены.
  - [x] Скользящее окно/индикатор нагрузки запросов и лимитов.
  - [x] UGUI-вариант для своего Canvas — `CoreAiTokenBudgetUiView` (v3.2.0): `UnityEvent<string>`-выходы привязываются к своим TMP_Text/Text в инспекторе, хоткей отключается (`KeyCode.None`); общий `TokenBudgetRuntimeSource` + чистый `TokenBudgetTextFormatter` (EditMode-тесты).
  - [x] Проверка UI-доступности в редакторе и в Play Mode — проверено в игре (F10 открывает панель, секции Tokens/Cost/Request Load корректны); логика покрыта EditMode-тестами `TokenBudgetCalculator` / `TokenBudgetTextFormatter`.

## [P2] API-дизайн
- [x] ~~**ILlmTool — дублирование Name/Description**~~ — есть `LlmToolBase` (`ILlmTool.cs:45-68`) с `JsonParams(...)`-хелпером.
- [x] **merchant.Ask callback**
  - [x] Основной путь — awaitable `AskAsync`; fire-and-forget вынесен в `AskWithCallback(...)` (convenience), старый `Ask(...)` помечен `[Obsolete]` как alias (v3.2.0). Доки/примеры переведены.
- [x] **Магические строки ролей**
  - [x] Введена структура `RoleId` (implicit string-конверсии, статики built-in ролей, ordinal-равенство); inline-литералы `"SmartChat"` в рантайме заменены на `BuiltInAgentRoleIds.SmartChat` (v3.2.0).

## [P2] Идеи (под вопросом, для последующей оценки)
- [ ] **STT → Agent → TTS pipeline для NPC** — локальный whisper, локальный TTS, потоковая передача эмоций/интонаций для анимаций.
- [ ] **Визуальный билдер AgentBuilder в редакторе** — панель промптов/агентов без кода.
- [ ] **Streaming-emotions / function-driven анимации** — расширение ответа: emotion/gesture для аниматора.
