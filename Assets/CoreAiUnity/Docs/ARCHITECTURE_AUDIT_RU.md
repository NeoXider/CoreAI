# Архитектурный аудит CoreAI

Дата: 2026-05-24

Область аудита: `Assets/CoreAI`, `Assets/CoreAiUnity`, пакетные манифесты, основные README, changelog и документация Unity-слоя. Аудит выполнен как обзор архитектуры и рисков; runtime-код в рамках этого документа не менялся.

## Краткое описание проекта

CoreAI - это AI-стек для Unity, разделенный на два пакета.

`Assets/CoreAI` содержит переносимое C#-ядро: роли агентов, оркестрацию запросов, очереди, память, tool-calling, политики безопасности и LLM-контракты.

`Assets/CoreAiUnity` содержит Unity-интеграцию: VContainer composition root, UI Toolkit чат, MEAI/HTTP/LLMUnity клиенты, настройки через ScriptableObject, editor tooling, PlayMode/EditMode тесты и демонстрационные сцены.

Главная архитектурная ценность проекта - попытка сделать один стек для нескольких режимов: локальные GGUF-модели, OpenAI-compatible HTTP, WebGL streaming, tool-calling, память, чат и game-world tools.

## Сильные стороны

1. Разделение portable core и Unity-слоя в целом правильное.

Ядро не должно зависеть от Unity runtime, а Unity-пакет берет на себя DI, UI, ScriptableObject settings и платформенные особенности. Это хороший фундамент для повторного использования и тестов.

2. Есть единая доменная модель для агентов и инструментов.

`AgentBuilder`, `SkillSet`, `ILlmTool`, `ToolExecutionPolicy`, `AiTaskRequest` и `LlmCompletionRequest` формируют понятный слой контракта между игрой и LLM.

3. Tool-calling постепенно централизуется.

`ToolExecutionPolicy` снижает расхождение между streaming и non-streaming путями: дедупликация, лимиты, timeout, нормализация результатов и события tool execution находятся ближе к одному месту.

4. Очередь оркестратора учитывает реальные игровые сценарии.

`QueuedAiOrchestrator` с приоритетами и cancellation scope полезен для UI-чата, NPC-ролей и "latest wins" сценариев.

5. Проект содержит много регрессионных тестов.

В `Assets/CoreAiUnity/Tests/EditMode` и PlayMode-наборах уже видна культура покрытия сложных веток: tool calls, streaming, backoff, HTTP headers, skill pipeline, chat panel state.

## Основные архитектурные риски

### 1. Хрупкость конфигурации `CoreAiChatConfig`

Проблема: `CoreAiChatConfig` хранит важные параметры как private serialized fields и отдает их только через read-only свойства. Для production Inspector это нормально, но тесты уже начали обходить это через reflection.

Пример зоны риска: `Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatConfig.cs`.

Риск: тесты завязаны на имена приватных полей (`_roleId`, `_showToolCallsInChat`) и ломаются при безопасном внутреннем переименовании. Это уже проявилось как ошибки `CS0200` при попытке присваивать read-only свойства.

Рекомендация: добавить узкий test/build API без расширения публичного runtime-контракта. Например, internal factory/helper под `InternalsVisibleTo` для тестовой сборки или методы `SetForTests(...)` с `#if UNITY_EDITOR`. Не делать свойства публично изменяемыми без необходимости.

Приоритет: высокий для стабильности тестов.

### 2. Смешение DI и статического фасада

Проблема: проект одновременно использует VContainer composition root и статический API `CoreAi`. Это удобно для пользователей, но повышает риск скрытых зависимостей и неявного порядка инициализации.

Пример зоны риска: `CoreAi.cs`, `CoreAILifetimeScope.cs`, `CoreServicesInstaller.cs`, `LlmPipelineInstaller.cs`.

Риск: одинаковая операция может идти через DI-сервис, scene lookup или static facade. Это усложняет диагностику "почему сервис не найден", особенно в тестах, сценах и WebGL.

Рекомендация: оставить `CoreAi` как фасад для потребителей, но внутри документации и тестов закрепить один preferred path: DI для системного кода, static facade для gameplay/user scripts. В коде фасада полезно держать максимально явные ошибки и диагностические сообщения.

Приоритет: средний.

### 3. Сложность streaming/tool-calling state machine

Проблема: streaming-путь должен одновременно обрабатывать SSE, native tool calls, text-shaped JSON calls, `<think>` blocks, progress hints, cancellation, очереди и UI typing state.

Риск: логика распадается на несколько состояний в разных слоях: LLM client, stream filter, chat service, chat panel. Новые изменения легко чинят одну ветку и ломают другую.

Рекомендация: для каждой новой ветки streaming/tool-call добавлять маленький parity-test: streaming/non-streaming, native/text-shaped, with/without buffered progress hint, cancellation before/after tool round. Документировать state machine как таблицу переходов, а не только описанием.

Приоритет: высокий.

### 4. Ошибки и отмена проходят через несколько семантик

Проблема: в проекте есть `TaskCanceledException`, `OperationCanceledException`, timeout от chat service, timeout tool invocation, HTTP retry/backoff и UI stop action.

Риск: для пользователя все это выглядит как "генерация остановилась", но код может различать canceled/faulted/null/no response/error bubble. Это место легко дает расхождение между UI и API.

Рекомендация: описать единую матрицу результата: success, user-cancel, timeout, provider-error, tool-error, empty-response. Затем зафиксировать ее тестами на `AiOrchestrator`, `QueuedAiOrchestrator`, `CoreAiChatService`, `CoreAiChatPanel`.

Приоритет: высокий.

### 5. Слишком большая роль changelog как архитектурной документации

Проблема: changelog содержит много важной архитектурной информации, но это исторический формат. Новому разработчику сложно отличить актуальное правило от старого решения, которое уже изменилось.

Риск: фактическая архитектура начинает жить в changelog, а не в стабильных документах вроде `ARCHITECTURE.md`, `DEVELOPER_GUIDE.md`, `STREAMING_ARCHITECTURE.md`.

Рекомендация: переносить итоговые правила из changelog в стабильные docs после каждого крупного изменения. Changelog должен объяснять "что поменялось", docs - "как сейчас правильно".

Приоритет: средний.

### 6. Предупреждения компиляции снижают качество сигнала

Проблема: в тестах есть много `CS8632` из-за nullable-аннотаций без включенного nullable context. Также замечен Unity warning по отсутствующему типу `UnityEngine.PathTracing.Core.WorldRenderPipelineResources`.

Риск: команда привыкает к шумной сборке и может пропустить новый warning, который реально указывает на регрессию.

Рекомендация: отдельно принять nullable-policy для тестовых сборок: либо включить `#nullable enable`/asmdef setting, либо убрать `?` из файлов без nullable context. Warning по PathTracing разобрать как package/settings mismatch, не как кодовую проблему CoreAI.

Приоритет: средний.

### 7. Runtime API и тестовая инфраструктура местами конфликтуют

Проблема: production API стремится быть закрытым и Inspector-friendly, а тестам нужны быстрые способы создавать состояния. Сейчас это решается ad hoc.

Риск: тесты начинают дублировать внутреннее знание классов и становятся дорогими в поддержке.

Рекомендация: завести маленький слой test fixtures/builders для часто используемых объектов: chat config, scripted LLM client, tool traces, stream chunks. Эти helpers должны жить в тестовой области и не менять runtime API.

Приоритет: средний.

### 8. Потенциальное расхождение package versions и документации

Проблема: есть два package.json (`CoreAI` и `CoreAiUnity`) и два changelog. Это нормально для двух пакетов, но требует жесткой дисциплины при релизах.

Риск: Unity-пакет может документировать поведение, требующее более новой версии portable core, чем реально указано в зависимости.

Рекомендация: добавить release checklist: versions aligned, dependency version checked, both changelogs updated, root README links still valid, docs updated for current behavior.

Приоритет: средний.

## Рекомендуемый порядок работ

1. Стабилизировать test API для `CoreAiChatConfig`, чтобы убрать reflection из новых тестов.

2. Закрыть warning cleanup отдельным PR: `CS8632`, `CS0219`, PathTracing/package warning.

3. Зафиксировать матрицу ошибок и отмены для chat/orchestrator/tool flow.

4. Добавить state table для streaming/tool-calling и покрыть ее parity-тестами.

5. Ввести PR-чеклист документации: changelog + стабильные docs + README links.

## Итог

Проект архитектурно жизнеспособен: разделение core/Unity правильное, доменные контракты выражены явно, а сложные места уже частично защищены тестами.

Главный недостаток не в "плохой архитектуре", а в накопленной сложности вокруг streaming, tool-calling, cancellation и UI state. Эти зоны требуют не крупного переписывания, а дисциплины: единые матрицы поведения, маленькие test helpers, меньше reflection в тестах, меньше шума в компиляции и регулярный перенос актуальных правил из changelog в стабильную документацию.

## Recommended implementation pattern: Options + ScriptableObject wrapper

Do not make CoreAiChatConfig the only source of runtime truth.

- ScriptableObject assets are Unity authoring wrappers in CoreAiUnity.
- Runtime configuration and immutable snapshots belong in CoreAI when they do not depend on UnityEngine.
- Serialized fields should remain private with [SerializeField]; mutable public fields are not the runtime contract.
- Runtime consumers should depend on interfaces, options, or snapshots.
- New tests should prefer plain options/classes. Asset tests should focus on defaults and serialization.
- Keep this rule documented in SCRIPTABLE_OBJECTS.md, release checks in RELEASE_CHECKLIST_RU.md, and accepted warning debt in KNOWN_ISSUES_RU.md.
