# Комплексный аудит репозитория CoreAI

**Дата:** 2026-07-10  
**Основной проверенный snapshot:** `259a447b03ed20e2b9ca7923a042568d972a9e90`  
**Unity:** `6000.3.14f1`  
**Target Editor:** `StandaloneWindows64`  
**Тип аудита:** architecture + product fit + correctness + stability + security + performance + tests + packaging  
**Изменения игрового/runtime-кода этим аудитом:** не выполнялись

> Важное ограничение snapshot: во время аудита `HEAD` внешне изменился с `91f1ce67` до
> `259a447b`, а семь ранее незакоммиченных наборов изменений были оформлены отдельными
> коммитами. Ни один из этих коммитов не создавался этим аудитом. Ниже открытые проблемы
> перепроверены после обновления `HEAD`; уже исправленные пункты из старого состояния не
> выдаются за актуальные дефекты.

> На момент записи отчёта во внешнем working tree также шла новая незавершённая remediation
> `WorldState/load_scene/Mods-Hub` (включая `WorldStateManager.cs`,
> `CoreAiWorldCommandExecutor.cs`, Hub services и новые tests). Эти правки не входят в snapshot
> `259a447b`, не создавались этим аудитом и ещё не имели завершённой compile/test верификации.
> Поэтому связанные findings ниже остаются release blockers для проверенного commit; после
> завершения внешней работы их следует закрывать только повторным audit + green tests, а не по
> наличию незакоммиченного diff.

---

## 1. Итоговый вердикт

CoreAI уже является не экспериментальным «чатом в Unity», а крупной платформой для
LLM-агентов, которые вызывают игровой код, управляют миром, используют память, Lua-моды,
стриминг, несколько backend-ов и диагностический UI. Базовая идея проекта сильная, а
направление архитектуры в основном правильное:

- portable core действительно отделён от `UnityEngine`;
- опасные действия проходят через tool/command boundaries;
- Unity, Mods, Hub, benchmark и tests физически разделены;
- local-first и OpenAI-compatible режимы поддерживаются одновременно;
- тестовая база значительно шире средней для Unity SDK;
- проект сознательно учитывает WebGL, IL2CPP, streaming, cancellation и слабые локальные модели.

Главный вывод: **идея проекта реализована убедительно, но обещание “scales to production” пока
опережает фактическую надёжность нескольких системных границ**. Наиболее опасен не UI и не
LLM-качество, а путь выполнения mutating tools: world/Lua/component mutations могут выполняться
параллельно и повторно при streamed echo. Дополнительно остаются проблемы долговечности audit
log/world state, неограниченного роста очередей и revision stores, fallback при реальных timeout,
Lua allocation bombs и несогласованного package graph.

### Сводная оценка

Оценки ниже не являются метрикой CI; это инженерная оценка риска на проверенном snapshot.

| Область | Оценка | Краткий вывод |
|---|---:|---|
| Соответствие идее проекта | **8/10** | Core loop «LLM -> tools -> game state» реализован и хорошо выражен в API/демо |
| Архитектурное направление | **7/10** | Границы core/Unity/Mods/Hub здравые, но package dependencies и ownership ещё не оформлены |
| Корректность runtime | **5.5/10** | Есть сильные контракты и тесты, но mutation replay/races и world-state edge cases критичны |
| Стабильность и lifecycle | **5/10** | Queue/dispose, audit persistence и fallback могут оставлять незавершённую или потерянную работу |
| Security / sandbox | **5.5/10** | Capability model хороший, но memory-allocation DoS и две VM увеличивают поверхность риска |
| Производительность | **6/10** | Есть лимиты и осознанный async, но нет performance gate; несколько структур растут без границ |
| Тестовая стратегия | **7/10** | Очень широкое покрытие, хорошие assembly tiers; package isolation/player builds не доказаны |
| Packaging / release | **4.5/10** | Фактически четыре пакета, документация/semver/dependency graph всё ещё частично двухпакетные |

### Приоритеты findings

| Приоритет | Количество | Смысл |
|---|---:|---|
| P0 | 1 | Возможны повторные/параллельные необратимые изменения мира |
| P1 | 11 | Высокий риск потери данных, зависших задач, OOM, install/release failure или пропуска fallback |
| P2 | 10 | Существенный технический долг, масштабирование и maintainability |
| P3 | 3 | Локальные оптимизации и hygiene |

---

## 2. Что именно проверялось

### 2.1 Репозиторий и конфигурация

- `893` C#-файла в `Assets/`;
- `22` собственных `.asmdef`;
- `229` C#-файлов, относящихся к tests/test tooling;
- `58` сцен, `1364` prefab, `33` `.asset`;
- четыре UPM package root:
  - `Assets/CoreAI`;
  - `Assets/CoreAiUnity`;
  - `Assets/CoreAIMods`;
  - `Assets/CoreAIHub`;
- `Packages/manifest.json`, `packages-lock.json`, package manifests и asmdef dependency graph;
- root README, INSTALL, TODO, package docs, security docs, changelogs, current audits;
- `.github/workflows/ci.yml` и analyzer tooling;
- Unity Editor state, project info, console baseline и tests через MCP;
- выборочные runtime paths: tool execution, orchestration, streaming/fallback, Lua sandbox,
  mod lifecycle, world persistence, audit log, file stores, UI/diagnostics.

### 2.2 Статические проверки

Выполнялись целевые поиски, а не механический вывод всех совпадений:

- `async void`, sync-over-async, `Task.Run`, cancellation paths;
- `GameObject.Find`, полные обходы сцен, reflection и `Resources.FindObjectsOfTypeAll`;
- file I/O, JSON serialization, queues/caches/dictionaries и их лимиты;
- subscriber isolation, `Dispose`, scene/lifetime ownership;
- optional dependency guards и реальные asmdef references;
- WebGL persistence bridge и места его использования;
- тестовые assembly boundaries и CI filters;
- крупные классы и горячие `Update`/`OnGUI` paths.

### 2.3 Практическая верификация

| Проверка | Результат |
|---|---|
| Unity preflight | `READY`: один Editor instance, compile/import idle, MCP доступен |
| Roslyn analyzer build | **PASS**, 0 warnings, 0 errors |
| Roslyn analyzer tests | **8/8 PASS** |
| Unity EditMode | На `4a0f95a6`: 1529/1529 завершены, один test failure из-за не объявленного ожидаемого error log |
| Исправление EditMode failure | Внесено внешним commit `259a447b`; код теста теперь использует `LogAssert.Expect`, но полный suite после commit не удалось чисто перезапустить |
| FastNoLlm PlayMode | **DEGRADED**: 9/58 без assertion failures, затем runner завис как `editor_unfocused`; orphaned run отменён через штатный `TestRunnerApi.CancelTestRun` |
| Player build | Не выполнялся: рабочее дерево и `HEAD` менялись параллельно, а Test Runner оставался в противоречивом состоянии |

### 2.4 Console baseline

До запуска новых проверок Unity Console содержала `5` error/exception и `85` warning entries.
Большая часть была намеренно сгенерирована negative-path tests, но отдельно присутствовали:

- missing type в `UniversalRenderPipelineGlobalSettings` (`Unity.PathTracing.Runtime`);
- `NullReferenceException` в `PlayModeRunTask.cs` Unity Test Framework;
- auto-fail MCP test job по initialization timeout;
- сообщения о повреждённом audit tail и ожидаемых LLM 429/transport failures из tests.

Следовательно, console нельзя считать чистым release baseline без отдельного запуска после очистки и
без тестового шума.

---

## 3. Идея проекта и соответствие реализации

### 3.1 Фактический north star

По README и коду CoreAI стремится быть production layer между LLM и игрой:

1. агент не только отвечает текстом, а безопасно вызывает C#/Lua/game tools;
2. framework переживает ошибки слабых локальных моделей, streaming fragmentation, rate limits,
   context overflow и tool-call repair;
3. локальный backend является полноценным first-class режимом;
4. модули можно подключать выборочно;
5. runtime подходит и для прототипа, и для долгоживущей игры;
6. динамически созданный мир можно диагностировать, сохранять и восстанавливать.

### 3.2 Где проект цели достигает

| Цель | Вердикт | Доказательство |
|---|---|---|
| Portable core | Сильное соответствие | `CoreAI.Core.asmdef` использует `noEngineReferences`; core runtime не тянет `UnityEngine` |
| Local-first | Сильное соответствие | LLMUnity/OpenAI-compatible routing, benchmark local models, offline fallback surfaces |
| Tool calling | Сильное соответствие API | Tool policy, schemas, repair, retry, streaming/non-streaming parity tests |
| Optional modules | Частичное | source разделён, но package graph и CI install matrix не доказывают обещанную optionality |
| Safe game mutation | Недостаточное | mutation serialization/replay protection не покрывает world/component/Lua/skill indirection |
| Persistent worlds | Частичное | основные W1/W2/W3/W5 уже исправлены, но inactive/reset/WebGL gaps остаются |
| Production resilience | Частичное | retry/metrics есть, но fallback timeout, lifecycle queues и audit durability остаются high-risk |
| Extensibility | Сильное | AgentBuilder, registries, skills, Mods, Hub pages, MessagePipe/VContainer seams |
| Release clarity | Слабое | продукт уже четырёхпакетный, а install/spec/changelog policy местами двухпакетные |

### 3.3 Стратегический разрыв

CoreAI эволюционировал в платформу:

```text
Portable Core
    -> Unity Host
        -> Mods / Lua runtime
        -> Hub / UI Toolkit
        -> Demos / Example Game / Benchmark
```

Однако public story и release mechanics всё ещё часто описывают только `core + coreaiunity`.
Из-за этого инженерная архитектура лучше продуктовой упаковки: код уже знает о четырёх модулях,
а install instructions, normative spec, changelog ownership и dependency manifests не всегда знают.

---

## 4. Сильные стороны

### 4.1 Архитектура

- Portable core реально отделён от Unity API.
- Benchmark вынесен в отдельную editor-only, non-autoReferenced assembly.
- Unity-facing функциональность использует DI и интерфейсы вместо одного глобального manager.
- Hub registry остаётся UI-framework-neutral в core.
- World mutations проходят через command sink/executor, что создаёт правильную точку для validation,
  audit, multiplayer authority и idempotency.
- Lua capability tiers и deny-list seams явно выражены.
- `COREAI_NO_LUA` / `COREAI_NO_LLM` предусмотрены архитектурно, а не добавлены поверх монолита.

### 4.2 Stability engineering

- Есть отдельные retry/backoff, timeout, loop guard, token budget, rate limit и trace systems.
- WebGL main-thread ограничения осознаны и документированы.
- Присутствует analyzer, запрещающий `ConfigureAwait(false)` в Unity layer.
- Многие runtime subscriber callbacks уже изолируют consumer failures.
- Lua runtime имеет лимиты mods/handlers/timers/events и instruction budget.
- Tool history, metrics и recent errors во многих местах bounded.

### 4.3 Tests и observability

- EditMode и PlayMode разделены на deterministic/live/scenario assemblies.
- `FastNoLlm` не требует реального backend.
- CI проверяет default, `COREAI_NO_LUA` и `COREAI_NO_LLM` configurations.
- CI содержит guards против «зелёного запуска с 0 tests».
- Есть benchmark artifacts, trace sinks, token overlay, session inspector, audit log и Hub diagnostics.
- Недавние критические world/audit дефекты получили regression tests, а не только patch.

---

## 5. Findings

## F-01 — P0: mutating tools могут выполняться параллельно и повторно

**Область:** correctness, stability, world integrity, multiplayer readiness.  
**Ключевые файлы:**

- `Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs:878-896`;
- `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs:41-47`;
- `Assets/CoreAiUnity/Runtime/Source/Features/World/ComponentLlmTool.cs:34-40`;
- `Assets/CoreAIMods/Runtime/LuaExecution/LuaLlmTool.cs:28-35`;
- `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs:63-70`;
- `Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs:54-63`;
- `Assets/CoreAI/Runtime/Core/Messaging/ApplyAiGameCommand.cs:8-35`.

### Проблема

`world_command`, `component_command`, `execute_lua` и `manage_mods` используют
`AllowDuplicates=true`, поэтому их signatures не участвуют в cross-turn echo suppression.
При этом hardcoded serialization chain содержит только `memory`, `manage_mods` и
`manage_skills`; world/component/Lua mutations могут планироваться параллельно при
`MaxParallelToolCalls > 1`.

В streaming path multi-call echo определяется только после того, как calls уже повторно
выполнились. Для `AllowDuplicates=true` tools он не определяется вообще. `call_skill_tool`
дополнительно скрывает behavior реального target tool и обходит policy classification.

Executor-level idempotency key отсутствует. Повторный `spawn`, force/score mutation или Lua
side effect может примениться второй раз.

### Риск

- duplicate spawn/economy/physics actions;
- nondeterministic order Lua и direct world mutations;
- рассинхронизация audit trail и реального state;
- невозможность безопасного retry в host-authoritative multiplayer;
- редкие, трудно воспроизводимые повреждения мира.

### Рекомендация

1. Немедленно включить world/component/Lua/call-skill mutations в один ordered serial domain.
2. Не выполнять mutating streamed calls до финализации всего tool-call turn.
3. Ввести `ToolBehaviorDescriptor`: effect, serialization domain, repeat policy, idempotency.
4. Добавить stable request/turn/slot idempotency keys на executor boundary.
5. Отдельно исправить partial-success signature semantics.

### Verification gate

- одинаковый streamed mutating batch второй раз даёт `0` executor invocations;
- три одинаковых slots в первом batch выполняются ровно три раза;
- mixed `world_command + execute_lua + component_command` имеет max concurrency `1`;
- retry failed slot после partial success не подавляется;
- одинаковый idempotency key создаёт только один объект.

---

## F-02 — P1: фактический package dependency graph не соответствует manifests

**Область:** packaging, install correctness.  
**Ключевые файлы:**

- `Assets/CoreAIHub/package.json:13-15`;
- `Assets/CoreAIHub/Runtime/CoreAI.Hub.UI.asmdef:4-7`;
- `Assets/CoreAIHub/Runtime/HubChatPage.cs:2-23`;
- `Assets/CoreAIMods/package.json:13-16`;
- `Assets/CoreAIMods/Runtime/CoreAI.Mods.asmdef:4-8`;
- `Assets/CoreAIMods/Runtime/Hub/CoreAiModsHubBinder.cs:5-20`.

### Проблема

Hub manifest декларирует только core, но assembly жёстко зависит от `CoreAI.Source` и использует
Unity chat types. Mods manifest декларирует core + coreaiunity, но основной Mods assembly жёстко
зависит от `CoreAI.Hub.UI`.

Таким образом, standalone install по `package.json` может не компилироваться, а Mods фактически
нельзя установить без Hub, хотя Hub позиционируется как optional UI.

### Рекомендация

- либо честно объявить фактические dependencies;
- либо, предпочтительно, вынести `CoreAI.Mods.Hub` в отдельную optional assembly;
- отделить core-only Hub shell от Unity built-in pages, если нужна независимая установка;
- добавить external consumer compile fixtures для каждого поддерживаемого набора пакетов.

---

## F-03 — P1: release и normative docs не отражают четырёхпакетный продукт

**Область:** product goals, release safety, documentation.  
**Ключевые файлы:**

- `README.md:442-447`;
- `INSTALL.md:3-11`, `INSTALL.md:39-46`;
- `Docs/README.md:14-19`;
- `Assets/CoreAiUnity/Docs/DGF_SPEC.md:48-103`;
- package manifests: `Assets/*/package.json:2-15`;
- `Assets/CoreAiUnity/CHANGELOG.md:7-22`.

### Проблема

README/INSTALL всё ещё говорят о двух packages, тогда как распространяемых package roots четыре.
Normative DGF spec помещает Lua в старую двухслойную архитектуру. Все `package.json` имеют
`5.0.10`, но Unity changelog уже содержит `5.0.11-5.0.13`, при том что README называет package
version authoritative.

### Риск

- consumer не устанавливает advertised Mods/Hub functionality;
- UPM upgrade detection не видит новые behavior/API;
- новые архитектурные решения опираются на устаревший normative snapshot;
- lockstep release становится формальностью без атомарного version bump.

### Рекомендация

Зафиксировать canonical package DAG и install profiles: `Base`, `HTTP-only`, `Local LLM`,
`Mods`, `Hub`, `Full`. После этого синхронно обновить package dependencies, semver, changelogs,
DGF spec, INSTALL, root README и Docs index.

---

## F-04 — P1: WorldState теряет inactive objects и может отменить Reset

**Область:** correctness, persistence.  
**Файл:** `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs`.

### Проблема A: inactive objects

`Save()` и `DestroyAllWorldObjects()` используют `FindObjectsByType<WorldObjectComponent>` без
`FindObjectsInactive.Include` (`:90-92`, `:354-357`). При этом snapshot сохраняет `active`, а load
вызывает `SetActive(obj.active)`.

Результат: восстановленный inactive object исчезает из следующего snapshot, а Reset/clean load
может оставить его в сцене и затем создать duplicate persistent ID.

### Проблема B: unresolved objects переживают Reset

`_unresolvedObjects` (`:47-50`) повторно добавляются в Save (`:140-157`), но Reset (`:321-352`)
список не очищает. После временно missing prefab пользователь делает Reset, следующий autosave
восстанавливает snapshot, и объект возвращается, когда prefab снова доступен.

### Рекомендация

- искать `WorldObjectComponent` с `FindObjectsInactive.Include`;
- очищать unresolved list до удаления snapshot;
- добавить tests: inactive save/load/reset, missing-prefab -> reset -> save -> prefab returns.

---

## F-05 — P1: Lua `load_scene` сообщает успех до фактического результата

**Область:** correctness, agent self-repair.  
**Ключевые файлы:**

- `Assets/CoreAIMods/Runtime/WorldBindings/CoreAiWorldLuaRuntimeBindings.cs:246-260`;
- `Assets/CoreAIMods/Runtime/WorldBindings/LuaCsWorldRuntimeBindings.cs:174-188`;
- world command sink/executor load-scene path.

Lua binding только публикует command. Если сцена отсутствует в Build Settings или load падает позже,
Unity пишет error, но Lua/LLM уже считает вызов успешным. Агент не получает feedback и не может
исправить scene name или выбрать fallback.

**Рекомендация:** сделать load scene request/response command с фактическим success/error, либо
предварительно валидировать Build Settings и вернуть ошибку синхронно до publication.

---

## F-06 — P1: WorldState и AuditLog не гарантируют WebGL durability

**Область:** WebGL, data loss.  
**Ключевые файлы:**

- `Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs:83-190`;
- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs:222-251`;
- `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiPersistFs.jslib:3-50`;
- корректные примеры: `FileAgentMemoryStore.cs:30-43`, `FileLuaModStore.cs:19-32`.

Обе системы пишут в `persistentDataPath`, но не вызывают `CoreAi_PersistFsSync`. Сам bridge
документирует, что browser может не вызвать Quit, а записи останутся в памяти IDBFS и исчезнут
после reload/tab close.

**Рекомендация:** единый persistence service с single-flight `syncfs` после успешного world/audit
commit, без копирования `DllImport` helpers по каждому store.

---

## F-07 — P1: AuditLogWriter имеет loss, backlog и rotation defects

**Область:** audit correctness, durability, performance.  
**Ключевые файлы:**

- `Assets/CoreAiUnity/Runtime/Source/Features/Audit/AuditLogWriter.cs:16-27`;
- `AuditLogWriter.cs:58-64`, `:156-251`;
- `Assets/CoreAI/Runtime/Core/Audit/AuditLogVerifier.cs:66-116`.

### Подтверждённые проблемы

1. `MaxBatchSize=10`, flush раз в `500 ms`: hard throughput около `20 entries/s`.
2. Queue unbounded; Lua/world activity выше лимита создаёт бесконечный backlog.
3. `Dispose()` вызывает только один `FlushBatch()` и сохраняет максимум 10 оставшихся entries.
4. Entries dequeue-ятся, `_seq/_prevHash` продвигаются до file write. При I/O failure batch потерян,
   а следующий hash ссылается на отсутствующую запись.
5. Rotation переносит старый файл, но сохраняет старый `_prevHash`. Verifier каждого файла начинает
   с genesis `""`, поэтому новый active file после 50 MB не проходит standalone verification.
6. `UniTask.Delay` возвращается в PlayerLoop; JSON/SHA/sync StreamWriter выполняются на main thread,
   хотя docs называют loop background.
7. `FlushBatch` не имеет явной serialization gate; background flush и test/Dispose flush могут
   пересекаться.

### Рекомендация

- один writer consumer с bounded channel;
- drain-until-empty на shutdown с deadline;
- commit `_seq/_prevHash` только после успешной атомарной записи либо requeue;
- rotation marker/anchor или verifier набора файлов;
- worker I/O на desktop/mobile, frame-budgeted cadence на WebGL;
- fault injection tests: disk full, permission denied, 1000-entry burst, concurrent flush, rotation.

---

## F-08 — P1: Lua sandbox ограничивает CPU, но не общий объём allocation

**Область:** stability, security, DoS.  
**Ключевые файлы:**

- `Assets/CoreAIMods/Runtime/Sandbox/SecureLuaEnvironment.cs:11-31`, `:216-275`;
- `Assets/CoreAIMods/Runtime/Sandbox/LuaCsSecureEnvironment.cs:24-85`;
- `Assets/CoreAIMods/Tests/EditMode/SecureLuaSandboxEditModeTests.cs:99-109`.

Обе VM ограничивают `string.rep` и `string.format`, но оставляют concatenation и `table.concat`.
Разрешённую строку около 1 MB можно несколько раз удвоить через `s = s .. s`, создав сотни MB
за небольшое число VM instructions. Instruction/time budget не остановит уже начатую allocation.

Дополнительно поддерживаются две почти параллельные VM линии (MoonSharp и Lua-CSharp), включая
sandbox/full-access/persistence/hooks, что удваивает security patch surface.

**Рекомендация:** VM-level memory quota либо allocator/accounting hook, caps для concat/table growth,
allocation-bomb regression tests. После доказанной parity — изолировать или удалить legacy VM assembly.

---

## F-09 — P1: secondary LLM пропускается при реальном streaming/timeout failure

**Область:** availability, local/cloud fallback.  
**Ключевые файлы:**

- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs:43-74`, `:78-140`;
- `Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:309-318`, `:426-435`;
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:664-680`.

Non-streaming decorator rethrows любой `OperationCanceledException` как user cancellation, хотя
transport использует тот же exception для internal timeout при неотменённом caller token.

Streaming decorator считает любой первый chunk успешным и не защищает последующие `MoveNextAsync`.
Tool-enabled `MeaiLlmClient` сначала выдаёт пустой control chunk, а сеть открывает позже. Timeout после
control chunk уже не вызывает secondary, хотя ни текста, ни необратимого tool side effect ещё нет.

**Рекомендация:** различать caller cancellation и provider timeout; считать stream committed только
после первого видимого token или mutating effect; добавить control-chunk + throw tests.

---

## F-10 — P1: QueuedAiOrchestrator не bounded и имеет незавершённый Dispose contract

**Область:** lifecycle, memory, performance.  
**Ключевые файлы:**

- `Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs:23-40`;
- `QueuedAiOrchestrator.cs:342-440`, `:623-667`;
- `Assets/CoreAI/Runtime/Core/Features/Orchestration/AiOrchestrationQueueOptions.cs:4`.

`_pending`, `_streamPending` и stream chunks unbounded. Каждый enqueue сортирует весь pending list под
общим lock. `Dispose()` очищает только scope CTS: pending/in-flight tasks и stream queues не завершаются,
а публичные методы после disposal продолжают принимать работу.

При offline/slow LLM это даёт неограниченный рост requests/TCS/CTS/chunks, CPU cost сортировок и forever
pending tasks после scene teardown.

**Рекомендация:** `MaxPending`, byte/chunk budget, explicit admission policy, priority heap/channel,
backpressure/coalescing, lifetime CTS, `ObjectDisposedException` после dispose и terminal completion
всех pending/in-flight items.

---

## F-11 — P1: version stores растут без лимита и переписывают всю историю

**Область:** long-session stability, storage, WebGL performance.  
**Ключевые файлы:**

- `Assets/CoreAI/Runtime/Core/Features/RuntimeVersioning/MemoryLuaScriptVersionStore.cs:10-46`;
- `MemoryDataOverlayVersionStore.cs:10-46`;
- `Assets/CoreAiUnity/Runtime/Source/Features/Lua/Infrastructure/FileLuaScriptVersionStore.cs:97-201`;
- `FileDataOverlayVersionStore.cs:92-154`.

Каждая revision хранит полный source/payload; лимита revisions/bytes нет. File stores при каждом
изменении сериализуют весь растущий JSON. Стоимость последовательных updates приближается к
квадратичной, а на WebGL выполняется на player loop.

**Рекомендация:** retention `original + current + last N/bytes + checkpoints`, append-only revisions,
compact current index, source/payload caps и tests на 1000 revisions/disk bytes/save latency.

---

## F-12 — P1: CI не доказывает release surface и может быть зелёным без Unity tests

**Область:** release safety.  
**Файл:** `.github/workflows/ci.yml`.

- При отсутствии `UNITY_LICENSE` Unity jobs пропускаются; fork PR может быть green только по analyzer.
- Нет Standalone/WebGL IL2CPP BuildPlayer gate.
- Нет external consumer project, устанавливающего packages по manifests.
- `no-llm` добавляет define, но не удаляет LLMUnity package, хотя hard asmdef reference остаётся.
- Package-local tests имеют cross-package references, поэтому монорепозиторий скрывает сломанный
  standalone dependency graph.

**Рекомендация:** trusted merge-queue Unity gate, minimal player builds, isolated consumer matrix,
реальное package removal для optional configurations и package-by-package compile fixtures.

---

## F-13 — P2: mod events не изолируют subscriber failures

**Область:** correctness, host integration.  
**Ключевые файлы:**

- `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:373-381`, `:465-473`, `:502-510`;
- `LuaCsModRuntime.cs:1031-1057`, `:1189-1197`;
- аналогичные paths в `LuaModRuntime.cs`.

Load/reload сначала commit-ит live state, revision и persistence, затем вызывает unguarded
`ModSourceLoaded`. Subscriber exception сообщает caller-у failure, хотя mod уже активен и сохранён.
Аналогичный риск существует для unload/report/event callbacks; ошибка host UI listener может быть
приписана здоровому mod handler.

**Рекомендация:** per-subscriber try/catch, отдельная host-integration telemetry и tests, доказывающие,
что bad listener не меняет mod state/result.

---

## F-14 — P2: additive CoreAIGameEntryPoint не передаёт ownership

**Область:** scene lifecycle.  
**Файл:** `Assets/CoreAiUnity/Runtime/Source/Composition/CoreAIGameEntryPoint.cs:38-90`.

Второй entry point в additive scene пропускает Start и не становится standby owner. Когда первый scope
уничтожается, он сбрасывает static facade; оставшийся scope не переинициализируется.

**Рекомендация:** ownership token/ref count или explicit handoff. Test должен unload-нуть первый scope
и проверить непрерывность `CoreAI/CoreAIAgent` facade через второй.

---

## F-15 — P2: scene/world query paths имеют O(N) main-thread cost без visited budget

**Область:** runtime performance.  
**Ключевые файлы:**

- `Assets/CoreAIMods/Runtime/WorldBindings/CoreAiWorldQueryLuaBindings.cs:31-68`, `:131+`;
- `LuaCsWorldQueryBindings.cs:36-73`, `:136+`;
- Full Unity bindings с `Resources.FindObjectsOfTypeAll`.

`GameObject.Find`, root-array allocation и recursive hierarchy traversal выполняются на main thread.
`MaxFindResults=100` ограничивает matches, но не visited nodes; no-match query обходит всю сцену.
Глубокая hierarchy рекурсивна, а MoonSharp/Lua-CSharp реализации дублируются.

**Рекомендация:** общий iterative query service, `MaxVisitedNodes`, depth/time budget, name index с
invalidation, profiler tests на 10k objects/deep no-match hierarchy.

---

## F-16 — P2: trace и keyed-lock registries имеют unbounded cardinality

**Область:** memory hygiene.  
**Ключевые файлы:**

- `Assets/CoreAI/Runtime/Core/Features/Orchestration/InMemoryAgentTurnTraceSink.cs:5-26`;
- `IAgentMemoryStore.cs:80+`, `ISkillStore.cs:89+`;
- `FileSkillStore.cs:48+`, `FileLuaScriptVersionStore.cs:99+`.

Trace ring bounded, но `_latestByRole` никогда не удаляет dynamic role IDs и удерживает prompt/response/
tool details. Process-wide `ConcurrentDictionary<string, SemaphoreSlim>` registries также не удаляют
locks для уникальных role/skill/path IDs.

**Рекомендация:** LRU/MaxRoles, clear API, зарегистрированные role IDs only; keyed-lock pool с ref count
либо store-level async gate.

---

## F-17 — P2: крупные классы концентрируют слишком много state machines

**Область:** maintainability, regression risk.

- `CoreAiChatPanel.cs` — около 3000 строк: UI lifecycle, service resolution, persistence, streaming,
  rendering, tool bubbles, cancellation, scrolling.
- `MeaiOpenAiChatClient.cs` — около 2500 строк: HTTP, retry, SSE parser, tool accumulator, payload/provider policy.
- `AiOrchestrator.cs` — около 1700 строк: request building, execution, budgeting, memory, traces, sanitization.

Это не повод переписывать архитектуру. Безопасный путь — extraction чистых parser/payload/state-machine
компонентов под characterization tests при сохранении публичных facade и serialized contracts.

---

## F-18 — P2: floating Git dependencies снижают воспроизводимость

**Область:** dependency management.  
**Ключевые файлы:** `Packages/manifest.json`, `CoreAIDependencyInstaller.cs`, `CoreAIModuleManager.cs`.

LLMUnity, MCP, MessagePipe, R3, UniTask, NeoxiderTools и часть других Git dependencies указаны без
tag/commit. Текущий checkout стабилизируется lock file, но новый consumer или one-click installer может
получить другой upstream HEAD под той же версией CoreAI.

**Рекомендация:** compatibility BOM, проверенные tags/commits, отдельная explicit Upgrade command и
автоматические dependency-update PR с полной test/build matrix.

---

## F-19 — P2: dev project тяжелее минимального SDK surface

**Область:** import time, CI/cache, clone size.

- `Assets/Epic Toon FX`: примерно 522 MiB и более 4600 tracked files;
- manifest включает Netcode, Multiplayer Services/Tools/Quickstart, dedicated server, Visual Scripting,
  Navigation, Timeline и другие packages без найденных project-owned code references;
- полный NeoxiderTools установлен в dev project, но CoreAI-owned code почти не использует `Neo.*`.

Это не доказывает player-size regression без Build Report, но точно увеличивает clone/import/Library/CI
стоимость и маскирует минимальный consumer footprint.

**Рекомендация:** minimal verification project, demo assets в `Samples~`/отдельном repo, package usage audit
с GUID verification и budgets по package/import/build size.

---

## F-20 — P2: performance regression suite отсутствует

В проекте нет `com.unity.performance-testing`, `PerformanceTest`, allocation assertions или регулярных
budgets на main-thread ms/GC/memory growth. LLM benchmark измеряет качество модели, но не SDK overhead.

Минимальная матрица:

1. enqueue/dequeue 1000 orchestration requests;
2. slow streaming consumer и максимальный buffered bytes;
3. world query на 10k objects/deep hierarchy;
4. 1000 Lua/data revisions: RAM/disk/save/load time;
5. 10k streaming chunks: allocations/frame time;
6. audit burst 1000 entries + rotation + injected I/O failure;
7. WebGL persistence cadence.

---

## F-21 — P2: timing-dependent tests могут быть flaky

`QueuedAiOrchestratorEditModeTests.cs` и несколько async suites используют fixed `Task.Delay(50/100)`
перед проверкой gates/logs. На загруженном CI это wall-clock race.

**Рекомендация:** явные TCS/signals с bounded timeout helper; один общий `EventuallyAsync` только для
условий, которые невозможно сигнализировать напрямую.

---

## F-22 — P2: test assembly boundaries маскируют package isolation failures

- `CoreAI.Tests.asmdef` зависит от Mods, Hub и ExampleGame;
- `CoreAI.Mods.Tests.asmdef` зависит от Unity test/editor/example assemblies;
- shared PlayMode tests знают о Mods/MoonSharp.

Это удобно для monorepo integration, но не доказывает package-local health. Нужны отдельные package
tests и repository integration tests, чтобы standalone UPM graph проверялся независимо.

---

## F-23 — P3: IMGUI diagnostics аллоцируют на repaint

`OrchestrationDashboard.OnGUI` и `CoreAiTokenBudgetOverlay.OnGUI` создают `StringBuilder`, interpolated
strings и форматированные snapshots на каждом repaint.

**Рекомендация:** cached view model, обновление 2-4 раза/секунду или по изменению metrics; Development
Build gate для тяжёлой diagnostics formatting.

---

## F-24 — P3: Hub core contract теряет type safety

`IHubPage` возвращает `Func<object>`, чтобы core не зависел от UI Toolkit; Hub host вынужден проверять
`VisualElement` в runtime и ловить type errors.

Решение portability обосновано. Улучшение: typed adapter/base class внутри Hub assembly, не меняя
framework-neutral core registry.

---

## F-25 — P3: docs/test counters быстро устаревают

README badge сообщает `1314` EditMode tests, TODO — `1361`, фактический MCP discovery и suites уже другие.
Ручные counters создают ложную release-сигнализацию.

**Рекомендация:** генерировать badge/result counts из CI artifact; в stable docs писать не абсолютное число,
а ссылку на последний verified run.

---

## 6. Оптимизация: где будет наибольший эффект

### 6.1 Сначала correctness, затем micro-optimization

Порядок важен:

1. mutation serialization/idempotency;
2. bounded queues и правильный lifecycle;
3. audit/world durability;
4. sandbox memory budget;
5. package/release isolation;
6. только затем scene indices, allocations и UI caching.

Ускорять текущий parallel mutation path до исправления его семантики опасно: это повысит частоту race.

### 6.2 Быстрые и безопасные оптимизации

| Изменение | Эффект | Риск |
|---|---|---|
| Cache GUIStyle/StringBuilder snapshots | Уменьшение `OnGUI` garbage | Низкий |
| Event-driven mod list cache | Убирает `ListMods()` на repaint | Низкий |
| Shared material + MaterialPropertyBlock | Убирает material leaks/duplicates | Низкий |
| Iterative world traversal + visited budget | Предсказуемый frame cost | Низкий/средний |
| Bounded latest-role trace LRU | Ограничение RAM | Низкий |
| Keyed-lock cleanup | Убирает process-lifetime cardinality leak | Средний |

Часть demo-level micro-optimizations уже находилась в незакоммиченном внешнем diff во время финального
среза (`WaveAutoBattlerModsDemoController.cs`); они не считаются завершёнными этим аудитом.

### 6.3 Структурные оптимизации

- bounded priority queue вместо sort-on-enqueue lists;
- append-only/version-compacted persistence;
- один audit writer consumer;
- scene query service/index;
- extraction SSE parser/tool accumulator из transport facade;
- minimal consumer/build project отдельно от showcase dev project.

---

## 7. Рекомендуемый план исправлений

План сформирован как Epics -> SubEpics -> Features. Каждая feature должна быть отдельным небольшим
изменением с самостоятельным verification gate; не следует смешивать их в один большой refactor.

## Epic 1 — Safe Mutation Pipeline

### SubEpic 1.1 — Единая классификация tool behavior

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 1.1.1 Mutation serialization hotfix | Немедленно исключить overlap | `ToolExecutionPolicy`, built-in tools | World/Lua/component/skill mutations идут в arrival order, max concurrency 1 | Mixed batch + streaming concurrency tests | Низкий: возможна потеря throughput |
| 1.1.2 ToolBehaviorDescriptor | Убрать hardcoded name lists | `ILlmTool`, policy, skill indirection | Behavior наследуется через `call_skill_tool` | Contract tests всех built-ins | Средний: public API evolution |
| 1.1.3 Streaming mutation finalization | Не выполнять echo до проверки turn | streaming policy | Повторный mutating turn не имеет side effects | Existing echo tests меняют 4 invocations на 2 | Средний |

### SubEpic 1.2 — Executor idempotency

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 1.2.1 Command idempotency key | Защитить boundary от replay | `ApplyAiGameCommand`, world/component executors | Один key применяется один раз | spawn/force/component/Lua tests | Средний: envelope migration |
| 1.2.2 Partial-success retry | Не подавлять failed slots | `ToolExecutionPolicy` | Retry выполняет только неприменённую работу | Partial batch regression tests | Средний |

## Epic 2 — Durability And Lifecycle

### SubEpic 2.1 — Audit log

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 2.1.1 Single writer + drain | Исключить races/loss | `AuditLogWriter` | Bounded queue, shutdown сохраняет всё в deadline | 1000-entry burst/concurrent dispose | Средний |
| 2.1.2 Transactional chain commit | Не двигать hash до I/O | writer/verifier | Disk failure не теряет batch и не ломает next link | injected I/O failure | Средний |
| 2.1.3 Rotation anchors | Проверяемая история файлов | writer/verifier/docs | Каждый rotated set верифицируется end-to-end | >50 MB synthetic rotation | Низкий/средний |
| 2.1.4 WebGL sync | Долговечность IDBFS | shared persistence service | Audit survives reload | WebGL build + reload harness | Средний |

### SubEpic 2.2 — World state

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 2.2.1 Inactive object parity | Не терять disabled objects | `WorldStateManager` | Save/load/reset включают inactive | PlayMode round-trip | Низкий |
| 2.2.2 Reset unresolved state | Reset действительно окончателен | `WorldStateManager` | Missing prefab не воскресает после Reset | Combined regression test | Низкий |
| 2.2.3 Scene-load result channel | Давать agent feedback | Lua bindings + command sink | Failed load возвращает error | missing-build-scene test | Средний |
| 2.2.4 WebGL world sync | Переживать tab reload | shared persistence service | World snapshot survives reload | WebGL E2E | Средний |

### SubEpic 2.3 — Queue/store lifecycle

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 2.3.1 Bounded orchestrator | Ограничить RAM/CPU | orchestrator/options | Admission policy observable и deterministic | saturation/slow consumer | Средний |
| 2.3.2 Dispose contract | Не оставлять pending tasks | orchestrator | Все tasks получают terminal state; new work rejected | dispose-with-pending | Средний |
| 2.3.3 Version retention | Ограничить RAM/disk | version stores | History соблюдает N/byte policy | 1000 revisions | Средний: user-visible history |

## Epic 3 — Packaging And Release Integrity

### SubEpic 3.1 — Canonical package graph

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 3.1.1 Split Mods-Hub integration | Вернуть Hub optionality | Mods asmdefs/package | Mods compiles без Hub | external UPM fixture | Средний: assembly GUID/contracts |
| 3.1.2 Fix Hub dependency | Честный install | Hub manifest/asmdef | Hub install подтягивает нужный Unity host | clean project install | Низкий |
| 3.1.3 Package-local tests | Не маскировать dependency defects | test asmdefs/fixtures | Каждый package graph тестируется отдельно | CI matrix | Средний |

### SubEpic 3.2 — Release truth

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 3.2.1 Atomic semver | Совпадение code/changelog/package | 4 manifests/changelogs | Один release = один проверенный version set | release script dry run | Низкий |
| 3.2.2 Four-package docs | Совпадение idea и install | README/INSTALL/DGF/Docs index | User выбирает понятный profile | clean install walkthrough | Низкий |
| 3.2.3 Pinned dependency BOM | Воспроизводимость | manifest/installer | Одинаковый CoreAI ставит одинаковый graph | fresh lock regeneration | Средний |

### SubEpic 3.3 — CI release gates

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 3.3.1 Trusted Unity gate | Не принимать PR без Unity compile | CI | Protected branch требует Unity result | merge-queue rehearsal | Низкий/ops |
| 3.3.2 Player builds | Проверить IL2CPP/WebGL | CI/build scripts | Minimal Standalone + WebGL build green | artifact launch/smoke | Средний/время CI |
| 3.3.3 Real optional removal | Доказать no-LLM/no-Lua | CI manifest transforms | Package действительно удалён, compile green | clean matrix | Средний |

## Epic 4 — Performance Budgets

### SubEpic 4.1 — Measured hot paths

| Feature | Purpose | Основные файлы | Ожидаемое поведение | Verification | Rollback risk |
|---|---|---|---|---|---|
| 4.1.1 Performance suite | Сделать regressions видимыми | new tests/CI | Budgets на frame/GC/RAM/disk | baseline artifacts | Низкий |
| 4.1.2 World query service | Убрать repeated full scans | Lua bindings/world index | Bounded visited/time | 10k object profiler test | Средний |
| 4.1.3 Diagnostic caching | Убрать repaint allocations | overlays/demo UI | Near-zero steady repaint GC | ProfilerRecorder | Низкий |

### SubEpic 4.2 — Controlled decomposition

Разделять `CoreAiChatPanel`, `MeaiOpenAiChatClient` и `AiOrchestrator` только после закрытия P0/P1,
по одному pure component за feature, с characterization tests и без смены public/serialized contracts.

---

## 8. Рекомендуемая последовательность работ

### Первые 48 часов

1. F-01 serialization hotfix и regression tests.
2. World inactive + unresolved Reset tests/fixes.
3. Audit writer drain/loss/rotation tests до следующего release.
4. Зафиксировать package DAG и заблокировать release при version mismatch.

### Следующие 1-2 недели

1. Tool behavior metadata + deferred streaming mutation.
2. Executor idempotency.
3. Queue lifecycle/bounds.
4. Lua memory quota/concat tests.
5. Streaming fallback timeout fix.
6. WebGL persistence service.
7. Isolated package compile matrix.

### Следующие 1-2 релиза

1. Version-store retention/append-only format.
2. Player build gates и minimal consumer project.
3. World query service + performance suite.
4. Legacy Lua VM isolation/removal.
5. Controlled decomposition крупных facades.

---

## 9. Release criteria после remediation

CoreAI можно честно маркировать production-ready для динамических миров, когда одновременно выполнено:

- mutating tools deterministic, serialized и idempotent;
- repeated streamed turn не повторяет side effects;
- all package profiles устанавливаются в clean project;
- package version/changelog/dependency graph синхронны;
- EditMode и FastNoLlm PlayMode проходят на чистой console baseline;
- Standalone и WebGL IL2CPP minimal builds проходят;
- audit burst/I/O failure/rotation tests зелёные;
- inactive/unresolved/reset world-state round-trips зелёные;
- WebGL world/audit data переживают reload;
- orchestrator и version history имеют измеряемые memory limits;
- Lua sandbox имеет memory allocation budget, а не только instruction budget;
- performance budgets публикуются вместе с release artifacts.

---

## 10. Заключение

CoreAI имеет сильную и отличимую идею: локальные или cloud LLM-агенты не просто разговаривают,
а действуют внутри игры через проверяемые tools, память и runtime scripting. Portable core, command
boundaries, extensive tests и local-model focus подтверждают, что это не маркетинговая оболочка.

При этом ближайший инженерный этап должен быть не расширением feature list, а **доведением системных
гарантий до уровня уже заявленного продукта**. Самый высокий ROI дают mutation safety, bounded lifecycle,
durable persistence и честный package/release graph. После них текущая архитектура сможет масштабироваться
без переписывания; до них новые world/mod features будут увеличивать вероятность редких, дорогих и плохо
воспроизводимых failures.
