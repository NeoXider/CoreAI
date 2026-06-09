# TODO

## [P0] Критически важное для репозитория (quality gate)
- [ ] **Очистка корня репозитория от артефактов сборки и лога (перед каждым push)**
  - [ ] Удалить из репозитория и добавить в `.gitignore`: `debug.log`, `memory.db`, `msp_server.log`, `replay_pid29472.log`, `TestRun_EditMode.log`, `UnityTest_EditMode.log`.
  - [ ] Убрать ошибочный файл `Remove-Item` (артефакт PowerShell).
  - [ ] Убрать placeholder-файлы `_coreai_placeholder_lines.txt`.
  - [ ] Прописать pre-commit/pre-push проверку на отсутствие мусорных файлов и рецидивов `.meta`/логов в корне.

## [P1] Lua-песочница (security / stability)
- [ ] **LuaCoroutineRunner**
  - [ ] Реализовать лимит активных корутин (`MaxActiveCoroutines = 64`) и hard-stop для превышения (не бесконечный рост coroutines).
- [ ] **LuaCoroutineHandle.Kill()**
  - [ ] Убрать пустые catch-блоки и реализовать реальное прерывание корутины; не оставлять `disposed`-флаг без фактической остановки.
- [ ] **Sandbox escape-тесты**
  - [ ] Добавить тесты на векторы: `string.dump`, `coroutine.close`, `collectgarbage` (timing oracle), `_G`/`_ENV` обходы.
  - [ ] Зафиксировать coverage в CI для проверки неизменности изоляции.
- [ ] **Генерация Lua-скриптов**
  - [ ] Добавить rate-limit на генерацию скриптов и защиту от runaway-циклов (задача Programmers/LLM).
- [ ] **Инструментирование debugger-хуков**
  - [ ] Исправить двойное присоединение `InstructionLimitDebugger` в `SecureLuaEnvironment.CreateScript` (возможна утечка/дублирование хука).

## [P1] CoreAi / CoreAIAgent / Policy runtime-state
- [ ] **Build/policy-init contract**
  - [ ] Сделать `Build()` применяемым к `CoreAi/Policy` по умолчанию.
  - [ ] Оставить `BuildDetached()` как явный "чистый" режим без автоприменения в policy.
- [ ] **Fail-fast при незарегистрированной роли**
  - [ ] Если роль не зарегистрирована — `Ask()` должен падать с понятным exception (`role not registered`), а не молча использовать fallback (`"Blacksmith"`).
- [ ] **Сброс static-состояния между Play Mode**
  - [ ] Ввести `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` для полного сброса static-полей CoreAi/CoreAIAgent.
- [ ] **Тестируемость**
  - [ ] Добавить/поддержать `CoreAi.SetResolver(Func<IAiOrchestrationService>)` для DI/подмены зависимости в тестах и CI.

## [P1] SmartToolCallingChatClient / ретраи
- [ ] **TryRepairToolName**
  - [ ] Добавить warning-логирование при автопоправке (`MEMORY` -> `memory`) и инкремент метрики/счётчика.
- [ ] **Жизненный цикл retry-feedback**
  - [ ] После успешного ретрая удалить error-feedback из history; не оставлять мусор ошибок после recovery.
- [ ] **TrimToolCallHistory**
  - [ ] Обеспечить инвариант: не должен оставаться `tool-result` без соответствующего `assistant tool-call`.
  - [ ] Проверить, что история истории OpenAI-совместима (иначе 400 от endpoint).

## [P1] HttpClient / Reuse connections
- [ ] Перевести сетевой слой на единый `HttpClient`/shared handler для backend/API-запросов.
- [ ] Проверить lifecycle клиента и защиту от socket exhaustion при длительной нагрузке.

## [P1] Память / I/O
- [ ] **Атомарная запись JSON-хранилища**
  - [ ] В `FileConversationSummaryStore` заменить `File.WriteAllText` на `temp + File.Replace` (или эквивалентно crash-safe запись).
- [ ] **Off-main-thread I/O**
  - [ ] Перенести операции чтения/записи/удаления истории в `persistentDataPath` в background-поток (через `Task.Run`/await).

## [P2] API-дизайн
- [ ] **ILlmTool**
  - [ ] Устранить дублирование `Name/Description`: добавить `LlmToolBase`, генерящий `AIFunction` из свойств.
- [ ] **merchant.Ask callback**
  - [ ] Убрать callback-перегрузку как primary-idiom.
  - [ ] Оставить `AskWithCallback(...)` как отдельный convenience-метод, основной путь — `UniTask`.
- [ ] **Магические строки ролей**
  - [ ] Ввести `RoleId`-структуру/константы; убрать inline string literals (`SmartChat`, `Blacksmith` и др.).

## [P1] Диагностика бюджета токенов в рантайме
- [ ] Расширить `RateLimiterMetrics`/дашборд разработчика визуальным overlay’ом:
  - [ ] Текущая стоимость токенов и метрика `tokens/request`.
  - [ ] Стоимость сессии (`$/session`).
  - [ ] Скользящее окно/индикатор нагрузки запросов и лимитов.
  - [ ] Проверка на UI-доступность в редакторе и во время Play Mode.

## [P2] Идеи (под вопросом, для последующей оценки)
- [ ] **STT → Agent → TTS pipeline для NPC**
  - [ ] Локальный whisper pipeline, локальный TTS, потоковая передача эмоций/интонаций для анимаций.
- [ ] **Визуальный билдер AgentBuilder в редакторе**
  - [ ] Панель промптов/агентов без кода (для не-программистов и геймдизайнеров).
- [ ] **Streaming-emotions / function-driven анимации**
  - [ ] Расширение ответа агента: не только текст, но и emotion/gesture для аниматора.
