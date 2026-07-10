# CoreAI — аудит стабильности и готовности 5.2.0

**Дата:** 2026-07-10
**Unity:** 6000.3.14f1, URP 17.3
**Объект:** весь monorepo, пять UPM-пакетов, Unity Console, тесты, десять demo-сцен,
документация и соответствие исходной идее проекта.

## Итог

Текущий commit-кандидат заметно стабильнее исходного состояния: проект компилируется без ошибок,
полный EditMode и FastNoLlm проходят, все опубликованные demo-сцены загружаются, а локальная 4B-модель
успешно выполняет реальные `memory` и `world_command` вызовы. Критические регрессии в streaming mutation,
Hub/Mods composition, URP и сценах исправлены.

Это проверенный **stabilization candidate 5.2.0**, но не доказанный production release для всех платформ.
До такого статуса остаются package-isolation consumers, Standalone/WebGL builds, Spark benchmark и полный
интерактивный UX-проход демо. Эти границы явно сохранены в `TODO.md`; audit-документы удалять пока нельзя.

## Проверенный срез

| Проверка | Результат |
|---|---|
| Unity compile + Console | 0 compile errors; 0 текущих Console errors после hard reload |
| Targeted ToolExecutionPolicy | 89/89 passed |
| Targeted world walker | 2/2 passed |
| Full EditMode | 1,598 total; 1,594 passed; 0 failed; 4 ignored third-party Neoxider Pages tests |
| FastNoLlm PlayMode | 67/67 passed (includes the ten-scene smoke) |
| Demo asset integrity | 10/10 сцен: missing scripts = 0, scope/camera/light присутствуют |
| Demo PlayMode startup | 10/10 сцен в одном smoke: supported shaders, нет неожиданных startup errors |
| Live local model | `qwen3.5-4b-mtp`: memory write passed; world spawn passed |

Первый полный EditMode-прогон обнаружил настоящий `StackOverflowException`: тест создавал и затем
рекурсивно уничтожался Unity-иерархией глубиной 5,000. Teardown переведён на leaf-first detach/destroy,
walker ограничивает не только visited, но и scheduled nodes. Повторный полный прогон зелёный.

## Исправленные блокеры

### 1. Streaming mutations и корректность повторов

`ToolExecutionPolicy` ранее сериализовал только `memory/manage_mods/manage_skills`; streamed multi-call
echo успевал повторно выполнить side effects до turn-level проверки. Теперь единая mutating-группа включает:

- `memory`;
- `manage_mods`;
- `manage_skills`;
- `world_command`;
- `component_command`;
- `execute_lua`;
- `call_skill_tool`.

Streamed mutations откладываются до завершения turn. Полный echo блокируется до исполнения. При частичном
успехе повтор не повторяет уже успешный slot, но разрешает повторить упавший. Добавлены batch/streaming
regression tests. Ограничение: состояние idempotency пока request-local и очищается `Reset()`.

### 2. Реальная компиляция Mods/Hub

В source-tree Hub находится под `Assets/`, поэтому package `versionDefines` не включал
`CoreAI.Mods.Hub`. В Standalone define добавлен `COREAI_HAS_HUB`; интеграционная сборка теперь реально
компилируется и проверяется в dev-проекте.

### 3. Сцены и composition

- Удалены два отсутствующих `Mirror.NetworkIdentity` из Hub и MiniRpg; Mirror не является зависимостью.
- Hub/chat UXML, USS и config восстановлены в MiniRpg и Wave; Wave Hub активирован.
- Пять Lua-сцен получили обязательный child `CoreAiModsLifetimeScope`: FullAccess, LiveMechanics,
  LiveMechanicsModsChat, LuaMods и ModdableUnits.
- Shared demo resolver больше не маскирует отсутствие Mods scope fallback-ом в core container.
- Все десять сцен прошли structural и PlayMode startup smoke.

### 4. URP и визуальная корректность

WaveAutoBattler заменял SRP-compatible material на Built-in `Standard`, что давало pink objects в URP.
Теперь цвет задаётся через cached `MaterialPropertyBlock` (`_BaseColor` + `_Color`) без замены материала.
Из URP Global Settings удалена ссылка на отсутствующий `Unity.PathTracing.Runtime`, вызывавшая Console exception.

### 5. Версии и package graph

Все пять пакетов подняты в lockstep до **5.2.0**. Minor bump оправдан новым публичным
`IContentFilter` API и новыми editor/runtime surfaces. `CoreAIBenchmark` теперь честно объявляет
`com.neoxider.coreaimods`, потому что G1-G7 используют реальный Lua runtime. Исправлены case-sensitive
Git UPM paths (`CoreAIMods`, `CoreAIHub`, `CoreAIBenchmark`).

## Открытые риски

### P0 перед публикацией release tag

1. Прогнать package consumers: Base, +Mods, +Hub, Full/Benchmark, а также физическое удаление Mods/Hub.
   Monorepo test assemblies всё ещё имеют cross-package references и не доказывают standalone UPM graph.
2. Собрать минимальные Standalone и WebGL IL2CPP players.
3. Прогнать benchmark v1.6 через Spark и сохранить отчёт/скриншоты.
4. Выполнить интерактивный driver для каждой demo-сцены: кнопки, input, бой, F9/F10, mod lifecycle и restart.

### P1 стабильность и безопасность

1. Добавить executor-level idempotency keys между отдельными top-level requests.
2. Перевести Full-tier `unity_list_objects/find_all/find_by_tag/find_by_component` с рекурсии на общий
   budgeted walker.
3. После `WorldStateManager.Reset()` синхронизировать IDBFS на WebGL.
4. Сделать rotation audit log двухфазным/восстанавливаемым и показывать worker failures в Console во время
   работы, а не только при Dispose/test flush.
5. Разрешить противоречие `allowedLuaScenes`: tooltip говорит empty=none, runtime допускает любую Build
   Settings scene. Для security-facing whitelist предпочтителен fail-closed контракт.
6. Pin floating Git dependencies по commit/tag и документировать upgrade procedure.

## Оптимизация

Сильные стороны текущей реализации:

- bounded parallel tool execution и единая mutation chain;
- очередь orchestrator с admission cap;
- bounded revision/history stores;
- iterative world walker с budget на visited и scheduled nodes;
- `MaterialPropertyBlock` вместо material allocations в Wave;
- полный deterministic gate выполняется примерно за три минуты EditMode + двадцать секунд FastNoLlm.

Главные оставшиеся performance-риски — рекурсивные Full-tier queries, отсутствие постоянного regression
suite для 10k-object сцен, audit burst и WebGL persistence cadence, а также тяжёлый dev-проект с большим
объёмом необязательных demo assets/packages.

## Соответствие идее проекта

Наиболее убедительная идея CoreAI — не «ещё один NPC chat», а local-first агентный runtime для Unity,
где небольшая модель вызывает реальный game code, пишет/управляет Lua-модами, сохраняет память и может быть
сравнена game-creation benchmark-ом. Текущая проверка подтверждает два ключевых обещания: 4B действительно
делает tool calls, а runtime защищает Unity state от части типичных слабых-model failure modes.

Слабое место позиционирования — доказательства пока сильнее внутри репозитория, чем снаружи. Для развития
идеи важнее всего публиковать воспроизводимые benchmark runs, превратить Hub mod-writing в безупречное
пятиминутное demo и показать shipping story для машины игрока. Director AI оставлен честно как controller
recipe, а `IContentFilter` — как extension point, не как автоматически подключённая moderation system.

## Решение по audit cleanup

Старые audit-файлы пока сохраняются. Их можно удалить только после того, как каждый оставшийся finding
будет либо исправлен, либо перенесён в устойчивый issue/TODO с проверяемым acceptance criterion.
