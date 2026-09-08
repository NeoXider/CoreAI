# MEAI как основной путь: текущие границы и исторический план

Актуализация: **2026-09-07**, рабочая версия после интеграции 7.35.0; изменения следующего выпуска проходят проверку. Microsoft.Extensions.AI **10.9.0** — текущая зависимость ядра и переносимого проекта. Этот документ описывает распределение ответственности, а не объявляет завершёнными все релизные проверки.

## Что используется из MEAI

| Задача | Текущий путь | Ответственность CoreAI |
|---|---|---|
| Сообщения и клиенты | `IChatClient`, `ChatMessage`, `ChatResponse`, `ChatResponseUpdate` | Адаптация провайдера и границы реплик в продукте |
| Схемы и биндинг инструментов | `AIFunctionFactory`, `AIFunction`, нативная JSON schema | Разрешённые действия, связь со скиллом, типизированные метаданные исполнения |
| Непотоковый цикл на desktop | `FunctionInvokingChatClient` внутри `SmartToolCallingChatClient` | Политика повторов, таймауты, ошибки, порядок изменений, `EndsTurn` и события |
| Usage | Типизированные поля `UsageDetails` и штатный `UsageDetails.Add` | Преобразование wire-полей провайдера и раздельная выдача суммарного usage / последнего roundtrip |
| Чтение навыков | `AIFunctionFactory` строит схему `read_skill` | Один общий payload главного документа, индекса, одного файла или всех файлов для LLM и MCP |

Источники текущего поведения: [SmartToolCallingChatClient](../Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs), [ToolExecutionPolicy](../Assets/CoreAI/Runtime/Core/Features/Llm/ToolExecutionPolicy.cs), [MeaiOpenAiChatClient](../Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs), [LlmUsageAccumulator](../Assets/CoreAI/Runtime/Core/Features/Llm/LlmUsageAccumulator.cs), [ReadSkillLlmTool](../Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs).

Прокси вызова скилла один раз разрешает конкретный инструмент и аргументы. Политика использует метаданные этого же инструмента для повторов, таймаута, порядка побочных эффектов и завершения хода; исполнение не делает второе разрешение по уже изменившемуся каталогу. Это семантика приложения, которую нельзя заменить только стандартным биндингом функции.

## Почему остаются платформенные пути

**Потоковый ответ.** В реализации `FunctionInvokingChatClient` MEAI 10.9 обновления после начала function call буферизуются перед дальнейшей обработкой. Это меняет задержку и порядок доступных клиенту фрагментов. CoreAI сохраняет потоковую обработку с ранней выдачей; переводить её на MEAI следует только после доказательства паритета наблюдаемого поведения: текст до/между вызовами, отмена, отсутствие повторного вывода и точное завершение хода.

**Unity/WebGL.** MEAI используется в Unity: его контракты и схемы общие. У WebGL отдельные ограничения транспорта, player loop и отсутствующего thread pool. Поэтому HTTP и ожидания подключаются адаптерами, а непотоковый WebGL-цикл сохраняет контекст хоста. Успешная desktop-сборка не доказывает браузерную работу асинхронного делегата.

**OpenAI-совместимые серверы.** `MeaiOpenAiChatClient` реализует контракт MEAI поверх транспортного интерфейса. Различия SSE, usage и расширений API обрабатываются на этой границе. Это не повод дублировать уже готовые схемы, суммирование usage или стандартный desktop-цикл.

## Канал инструментов определяется endpoint

Старое предположение «LLMUnity означает отсутствие native tool calling» опровергнуто проверкой поставляемого локального сервера. Текстовый fallback остаётся для endpoint, которым он действительно нужен. Явная настройка канала и проба в режиме Auto позволяют выбирать поведение по фактической поддержке, с наблюдаемой причиной решения.

Строки из старого плана про безусловный `false`, невозможность передать параметры серверу и единственный текстовый канал **не являются текущими требованиями**. Нельзя возвращать этот гейт по ссылке на историческую таблицу ниже. Проверяйте текущие [настройки и маршрутизацию](../Assets/CoreAI/Docs/LLM_ROUTING.md).

## Проверки и использование вне Unity

Постоянная [переносимая сборка](../tools/portable/README.md) компилирует настоящее ядро как `netstandard2.1` с MEAI 10.9.0. [Консольный пример](../examples/dotnet/README.md) проходит через публичные клиенты и инструменты, без собственной копии harness.

Перед релизом нужны отдельные результаты компиляции, EditMode, интеграции с backend и соответствующих Player-платформ. Для регрессий проверяются не только успешные ответы, но и ошибки, отмена, повторные вызовы, потоковый порядок и поведение после записи состояния. Список исторических команд и гейтов ниже не заменяет текущий release gate.

## Исторический план — только для контекста

Ниже сохранён исходный документ 2026-09-06 без потери ссылок и аргументации. Его версии **10.7.0**, старые номера строк, состояние потребителя, промежуточные вердикты и неисполненные гейты относятся к тому снимку и могут быть неверны сейчас. В частности, обновление до 10.9.0 уже вошло в текущую работу, вопреки прежнему исключению из scope. При конфликте ориентируйтесь на разделы выше и действующий код.

<details>
<summary>Открыть исходный план до актуализации MEAI 10.9</summary>
# MEAI как основной путь: план снятия наших дубликатов

Решение владельца: **Microsoft.Extensions.AI (MEAI) — основной путь, наши дубликаты её возможностей
убираются.** Документ отвечает на четыре вопроса: что у нас лишнее, что чем заменяется, в каком
порядке это снимать и чего делать **не** надо.

Дата составления: 2026-09-06. Состояние репозитория на момент составления: ветка `main`, последний
коммит `54fa272f` (7.34.0), в рабочем дереве незакоммиченный 7.35.0.

Документ рассчитан на исполнение **другим человеком без повторного расследования**: каждое
утверждение подкреплено `файл:строка` либо XML-докой поставляемой сборки MEAI. Где доказательства
нет — написано «не уверен» прямым текстом.

---

## 0. Четыре поправки к постановке задачи

Это не придирки: три из четырёх меняют состав и порядок работ, поэтому они идут первыми.

### 0.1. MEAI у нас уже 10.7.0, а не 9.10.2

- `Assets/packages.config:6-7` — `Microsoft.Extensions.AI` и `.Abstractions` версии **10.7.0**.
- Бамп сделан коммитом `258dda6d` («chore: update Microsoft.Extensions.AI to 10.7.0») от
  **2026-06-15**, то есть задолго до текущего тега.
- На диске лежат `Assets/Packages/Microsoft.Extensions.AI.10.7.0/` (в git) и
  `Assets/Packages/Microsoft.Extensions.AI.10.4.1/` (**не в git**, локальный мусор от
  NuGetForUnity — `git ls-files` по этому пути пуст).

**9.10.2 — это версия у потребителя, а не у нас:** `D:\Git\RedoSchool\Assets\packages.config`
объявляет `Microsoft.Extensions.AI` **9.10.2**. Отсюда весь раздел 5 (миграция потребителя) — и
отсюда же главный риск плана.

**Следствие для плана:** отдельного этапа «обновиться с 9.x до 10.x» не существует, он уже пройден.
Обновление 10.7.0 → 10.9.0 в этот план **не входит** (см. 4.5, последний пункт).

### 0.2. Свойство называется `CachedInputTokenCount`, а не `InputCachedTokenCount`

Проверено по XML-доке поставляемой сборки
`Assets/Packages/Microsoft.Extensions.AI.Abstractions.10.7.0/lib/netstandard2.0/Microsoft.Extensions.AI.Abstractions.xml`.
Полный список членов `UsageDetails` в 10.7.0:

```
Add(UsageDetails)   AdditionalCounts        CachedInputTokenCount   InputAudioTokenCount
InputTextTokenCount InputTokenCount         OutputAudioTokenCount   OutputTextTokenCount
OutputTokenCount    ReasoningTokenCount     TotalTokenCount
```

`InputCachedTokenCount` в 10.7.0 **не существует**. Код, написанный по неверному имени, не
скомпилируется — поправка сэкономит один цикл сборки.

### 0.3. Два механизма лежат не там, где сказано в постановке

| Механизм | Где сказано в задании | Где на самом деле |
|---|---|---|
| `ExtractCacheTokenCounts` | `MeaiOpenAiChatClient` | `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs:1745-1782` (вызовы `:284`, `:1738`) |
| `StartsNewMessage` | `MeaiOpenAiChatClient` | объявление — `Assets/CoreAI/Runtime/Core/Features/Orchestration/ILlmClient.cs:354`; производитель — `MeaiLlmClient.cs:474-489` |

В `MeaiOpenAiChatClient.cs` слов `MessageId`, `ResponseId`, `FinishReason` **нет ни одного**.

### 0.4. Узел «инструменты из текста» НЕ недостижим — это единственный канал локальной модели

Постановка называет его «сегодня недостижимым, потому что боевой клиент всегда объявляет нативный
канал». Для **боевого HTTP-эндпойнта RedoSchool** это верно. Для **фреймворка** — нет, и разница
решающая.

Гейт: `MeaiLlmClient.cs:1863-1867` (`InterpretsProseAsToolCalls`) и `MeaiLlmClient.cs:761-765`
(`textShapedToolCallsPossible`). Оба упираются в `_supportsNativeToolCalling`. Где он задаётся:

| Место | Значение | Что это за клиент |
|---|---|---|
| `LlmEndpointClientFactory.cs:110` | `true` | удалённый OpenAI-совместимый эндпойнт |
| `LlmEndpointClientFactory.cs:327` | **`false`** | llama.cpp-сервер под управлением LLMUnity |
| `LlmPipelineInstaller.cs:505` | **`false`** | тот же LLMUnity-сервер, путь установщика |
| `LlmClientRegistry.cs:1747` | **`false`** | тот же, путь реестра |

Комментарий в коде говорит это прямо (`LlmEndpointClientFactory.cs:320-325`): у llama.cpp-сервера
через LLMUnity **нет канала вызовов**, модель зовёт инструмент, написав JSON в ответе, и «этот текст
— единственное место, откуда вызов может прийти». Там же названа цена ошибки: «Wrong in this
direction: every local-model tool stops working, silently».

**Вердикт: узел не снимать** (подробно — 2.5). Снятие тихо выключает вызов инструментов у всей
локальной ветки, причём без единой ошибки в логе — ровно тот класс дефекта, который потом невозможно
воспроизвести.

---

## 1. Инвентаризация

Легенда вердикта: **СНЯТЬ** — заменяется штатным MEAI; **ОСТАВИТЬ** — MEAI не покрывает;
**НЕ УВЕРЕН** — нужна отдельная проверка, решение в этот план не входит.

### 1.1. Дубликаты, которые снимаются

| № | Наш механизм | Файл:строка | Что покрывает в MEAI 10.7.0 | Вердикт | Доказательство |
|---|---|---|---|---|---|
| D1 | `MeaiLoggingFunctionInvokingChatClient.cs` — пустой файл-надгробие | `Assets/CoreAiUnity/.../Infrastructure/MeaiLoggingFunctionInvokingChatClient.cs:1-8` | — (просто мусор) | **СНЯТЬ** | Утверждение файла `:4` «FunctionInvokingChatClient which is no longer available» **ложно**: тип есть в поставляемой `Microsoft.Extensions.AI.dll` 10.7.0 (`T:Microsoft.Extensions.AI.FunctionInvokingChatClient` в XML-доке). Идентификатор больше нигде не встречается. |
| D2 | `LlmUsageAccumulator.Accumulate` — ручное сложение четырёх полей | `Assets/CoreAI/Runtime/Core/Features/Llm/LlmUsageAccumulator.cs:23-62` | `UsageDetails.Add(UsageDetails)` | **СНЯТЬ** (свести к обёртке) | `M:Microsoft.Extensions.AI.UsageDetails.Add` есть в 10.7.0. Наш код складывает только `InputTokenCount` (`:35-38`), `OutputTokenCount` (`:40-43`), `TotalTokenCount` (`:45-48`), `AdditionalCounts` (`:50-59`) — **типизированные `CachedInputTokenCount` / `ReasoningTokenCount` / audio/text-счётчики теряются молча.** |
| D3 | `ExtractCacheTokenCounts` — эвристика по именам ключей | `MeaiLlmClient.cs:1745-1782` | `UsageDetails.CachedInputTokenCount`, `.ReasoningTokenCount` | **СНЯТЬ** (после D4) | Гейт `:1759` — ключ должен содержать `cache`; чтение `:1764-1765` — `read`/`cached`; запись `:1766-1768` — `write`/`creation`/`create`. Ветки **не взаимоисключающие**: ключ `cache_creation_read_tokens` попадёт и в чтение, и в запись (`:1770-1778`). |
| D4 | Расплющивание `usage` в строковый мешок | `MeaiOpenAiChatClient.cs:2069-2117` (`BuildAdditionalUsageCounts`) | типизированные поля `UsageDetails` | **СНЯТЬ** (частично: расплющивание остаётся как запасной путь, но типизированные поля заполняются) | `:2091-2101` рекурсивно расплющивает `usage` в точечные ключи (`prompt_tokens_details.cached_tokens`), а `MeaiLlmClient.cs:1745` потом грепом достаёт их обратно. `BuildUsageDetailsFromOpenAiUsageObject` (`:2043-2067`) заполняет только Input/Output/Total + `AdditionalCounts`. |
| D5 | `ChatResponse.Text` там, где нужен текст одной реплики | `MeaiLlmClient.cs:242` | — (это **ловушка**, а не дубликат) | **СНЯТЬ** (заменить на текст последней ассистентской реплики) | XML-дока `P:Microsoft.Extensions.AI.ChatResponse.Text`: «This property concatenates the `ChatMessage.Text` of **all** ChatMessage instances in Messages». Сегодня безопасно только потому, что `MeaiOpenAiChatClient` всегда строит ровно одно ассистентское сообщение (`:1562`, `:1602`, `:1625`), а `SmartToolCallingChatClient` возвращает либо его, либо свежепостроенный одиночный ответ (`:159`, `:255`, `:312`, `:361`, `:366`, `:380-384`, `:626`). Ломается в тот момент, когда в цепь войдёт любой многосообщенческий ответ. |
| D6 | `StartsNewMessage` — собственная граница реплик | объявление `ILlmClient.cs:354`; латч `MeaiLlmClient.cs:474-489` | `ChatResponseUpdate.MessageId` | **СНЯТЬ** (вывести из `MessageId`, поле оставить как внешний API) | XML-дока `P:...ChatResponseUpdate.MessageId`: «A single streaming response might be composed of multiple messages… This property is used to group those updates together into messages». Наш латч существует ровно потому, что транспорт `MessageId` **никогда не заполняет** (0 вхождений в `MeaiOpenAiChatClient.cs`). Потребители флага: `StreamedMessageJoiner.cs:61`, `AiOrchestrator.cs:548,759-771,923-925`, `CoreAiChatPanel.cs:2924,2945`. |
| D7 | Цикл раундтрипов в `SmartToolCallingChatClient` (кэп, счётчик ошибок, сборка Tool-реплики) | `SmartToolCallingChatClient.cs:127-128` (кэп), `:24/:51/:67` + `ToolExecutionPolicy.cs:64,131,1964,1975` (ошибки), `:413-415` (Tool-реплика) | `FunctionInvokingChatClient` | **СНЯТЬ** (только оболочку цикла, **не** политику — см. 2.1) | Совпадение поимённое: `MaximumIterationsPerRequest` (умолчание **40**), `MaximumConsecutiveErrorsPerRequest` (умолчание **3**), `AllowConcurrentInvocation` (умолчание `false`) — всё из XML-доки 10.7.0. У нас: кэп `ICoreAISettings.MaxToolCallRoundtrips` = 20, бюджет ошибок = 3 (`SmartToolCallingChatClient.cs:51`). |
| D8 | `GetService` возвращает `null` безусловно | `MeaiOpenAiChatClient.cs:3012-3015` | `ChatClientMetadata` через `GetService` | **СНЯТЬ** | Контракт MEAI: middleware (`FunctionInvokingChatClient`, `DistributedCachingChatClient`, `OpenTelemetryChatClient`) опрашивает `GetService(typeof(ChatClientMetadata))`. `ChatClientMetadata` в файле не конструируется нигде. Без этого ни одно штатное middleware не сможет узнать провайдера/модель. |
| D9 | Круговой прогон схемы инструмента через Newtonsoft | `MeaiOpenAiChatClient.cs:2936` | `AIFunction.JsonSchema` уже `JsonElement` | **НЕ УВЕРЕН** | `JsonConvert.DeserializeObject(af.JsonSchema.ToString())` — двойная сериализация на каждый запрос. Убирается только вместе с переводом всего конверта запроса с Newtonsoft на `System.Text.Json`, а это отдельная большая работа с риском по WebGL. В этот план **не входит**. |

### 1.2. Собственные повторы и восстановление против `ContinuationToken`

Постановка предлагает сверить их. Сверка сделана — **вердикт «ОСТАВИТЬ», это не дубликат.**

- `ContinuationToken` / `AllowBackgroundResponses` в 10.7.0 есть (`ChatOptions`, `ChatResponse`,
  `ChatResponseUpdate`), в нашем коде — **0 вхождений** (grep по всему `Assets`).
- Но по XML-доке `P:...ChatOptions.AllowBackgroundResponses`: «This property only takes effect if
  the implementation…» — то есть работает **только если провайдер поддерживает background
  responses**. Наш транспорт — самописный `MeaiOpenAiChatClient` поверх обычного
  `/chat/completions`; токены продолжения нам взять неоткуда, их пришлось бы **выдумывать самим**.
- Наш `RetryingStreamingLlmClientDecorator` делает принципиально другое: повтор **только до
  фиксации** (до первого видимого чанка), `RetryingStreamingLlmClientDecorator.cs:97-120`; сбой в
  середине потока превращается в терминальный чанк (`:263+`), а не в возобновление. Возобновления
  посреди потока у нас нет вообще — значит и дублировать нечего.

### 1.3. Найдено попутно (не дубликаты MEAI, но требует записи)

| Находка | Файл:строка | Комментарий |
|---|---|---|
| Два независимых цикла вызова инструментов | `SmartToolCallingChatClient.cs:90-449` (непотоковый) и `MeaiLlmClient.cs:431-1600` (потоковый) | Общие только помощники (`ToolExecutionPolicy`, `LlmUsageAccumulator`, `ToolCallHistoryTrimmer`). Это главный источник расхождения поведения, и он **не лечится** переходом на MEAI: потоковый цикл на MEAI перевести нельзя (2.1). |
| `SmartToolCallingChatClient` — только непотоковый путь | конструируется единственный раз, `MeaiLlmClient.cs:164`; `GetStreamingResponseAsync` (`:699-718`) — чистый проброс с предупреждением `:708` | Боевой чат идёт потоком (`AiOrchestrator.cs:578`, `:1128`), то есть **через этот класс не проходит**. Меняет оценку риска этапа 7 в меньшую сторону. |
| `CircuitBreakerLlmClientDecorator` не собран в боевом контейнере | `CircuitBreakerLlmClientDecorator.cs:28`; `new` только в тестах (`OrchestrationResilienceEditModeTests.cs:141`, `CircuitBreakerLlmClientDecoratorEditModeTests.cs`) | **Не удалять.** CoreAI — фреймворк, публичный несобранный кирпич это норма, а не мёртвый код. К MEAI отношения не имеет. |
| Ретрай спрятан в декораторе логирования | `LoggingLlmClientDecorator.cs:13-17,48-61` | Смешение ответственностей. MEAI закрывает только половину (`LoggingChatClient`), и на уровне `IChatClient`, а не `ILlmClient`. В этот план не входит; кандидат в `TODO.md`. |
| `INSTALL.md` не называет минимальную версию MEAI | `INSTALL.md:87-110` | Потребителю сказано «поставьте `Microsoft.Extensions.AI`» без нижней границы. Он получит последнюю, а RedoSchool сидит на 9.10.2 — расхождение никем не замечается до ошибки компиляции. Чинится этапом 4. |

---

## 2. Что оставить и почему

Это раздел «чего делать НЕ надо». Каждый пункт — механизм, который выглядит дубликатом, но им не
является; удаление любого из них — регресс, который вернётся отдельной задачей.

### 2.1. Не переводить чат на `FunctionInvokingChatClient`

Прямое требование постановки, и оно обосновано: в стриминге FICC после первого
`FunctionCallContent` буферизует остаток итерации, то есть учитель замрёт.

Что подтверждено локально:
- Структурно это следует из формы API: `ProcessFunctionCallsAsync`, `CreateResponseMessages`,
  `FixupHistories`, `ThrowIfNoFunctionResultsAdded` (члены `FunctionInvokingChatClient` по XML-доке
  10.7.0) работают по **материализованному** `IList<ChatMessage>`. Чтобы получить его из потока
  апдейтов, поток нужно свернуть — то есть дождаться его конца.
- Полная буферизация хода у нас **уже запрещена явным предупреждением**:
  `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md:278` — «Never reintroduce full-turn buffering of
  bound-tool turns. In 4.10.4 all bound-tool turns were buffered…; that killed token-by-token
  streaming for the teacher chat and was reverted in 4.10.5».
- Флаг, который это разрешал (`LlmCompletionRequest.BufferFullStreamingIterationWhenToolsDeclared`),
  **уже удалён** в 7.35.0 (`Assets/CoreAI/CHANGELOG.md`, раздел «Removed»).

Честная граница: точное поведение FICC в стриминге я не перепроверял декомпиляцией сборки —
опираюсь на предыдущее исследование и на форму API. Если кто-то захочет это оспорить, проверять надо
**замером на живой модели**, а не чтением доки: доки про стриминг FICC нет вовсе (в XML-доке типа
слово «stream» не встречается ни разу).

### 2.2. Execute-as-you-stream — MEAI так не умеет

Мы исполняем вызов, пока модель ещё генерирует остаток хода.

- Производитель: `MeaiOpenAiChatClient.cs:697-706` — `toolAccumulator.DrainCompleted()` отдаёт
  наружу каждый вызов, чей JSON аргументов уже полон, **не дожидаясь** конца потока. Реализация
  дренажа — `:2436-2486`, готовность считает ручной баланс скобок `IsCompleteJsonObject` `:2505-2561`.
- Потребитель: `MeaiLlmClient.cs:735-740` (комментарий-обоснование), `:942-943` —
  `policy.BeginStreamedTurn()` + `policy.ExecuteStreamedAsync(...)` прямо в цикле чтения потока.
- Закрытие хода с сохранением протокола: `:903`, `:1186` — `policy.CompleteStreamedTurnAsync`, после
  чего модель видит обычную пару «assistant tool_calls + одно tool-сообщение».
- Машинерия потокового хода: `ToolExecutionPolicy.cs:1306-1908` — слоты, отложенные мутирующие
  вызовы (`:1336`, `:1476`), ограниченный дренаж незавершённых (`:1651-1742`).

Тонкость, которую легко потерять при правках: механизм **двухрежимный**. При
`MaxParallelToolCalls <= 1` вызов исполняется inline и результат возвращается сразу
(`ToolExecutionPolicy.cs:1483-1491`); при большем значении задача **стартует и ожидается позже**
(`turn.InFlight.Add(RunGuardedAsync(...))`, `:1501-1511`), `ExecuteStreamedAsync` возвращает `null`, а
результаты собираются в `CompleteStreamedTurnAsync` (`:1595`). Мутирующие инструменты вообще не
запускаются на лету, а **откладываются** до конца хода (`:1476-1480`) — подпись эха нельзя судить
раньше. Читающие вызовы сохраняют быстрый путь.

Ещё тонкость: часовой простоя SSE перевзводится **только после того, как потребитель забрал апдейт**
(`MeaiOpenAiChatClient.cs:708-718`) — именно потому, что потребитель между `MoveNext` исполняет
инструменты. Любая правка, переносящая перевзвод раньше, вернёт ложные таймауты на длинных вызовах.

У MEAI такого понятия нет: `FunctionInvocationContext.IsStreaming` лишь сообщает, из какого вызова
пришли, а сам вызов всё равно происходит после сворачивания ответа. **Оставить целиком.**

### 2.3. `SplitForSmoothStreaming` + пауза 15 мс

- Нарезка: `MeaiOpenAiChatClient.cs:792-812` — куски ~6 символов, но не рвущие слово (расширение до
  `targetChunkSize * 2`, `:804`), то есть фактически ≤13 символов.
- Порог применения: `:677` — только text-only дельты длиннее **24** символов.
- Пауза: `:814-824` — `Task.Delay(15)` (`:822`), а на WebGL `Task.CompletedTask` (`:817-820`),
  потому что таймерная задержка там оставляет синтетическую дельту висеть.
- Причина в комментарии `:671-676`: бесплатные модели OpenRouter пакуют много токенов в одну SSE-дельту.

MEAI по доке не режет и не склеивает поток: один `ChatResponseUpdate` = одно событие провайдера.
Значит сглаживание — **наш слой поверх MEAI, а не дубликат MEAI**. Оставить.

> Внимание при этапе 5: пересобранные куски создаются как `new ChatResponseUpdate(...)` и переносят
> **только `ModelId`** (`:681-687`). Если начать заполнять `MessageId`, его надо перенести здесь же,
> иначе сглаживание будет рвать границу реплики. Это единственная жёсткая зависимость по порядку в
> плане.

### 2.4. `ThinkBlockStreamFilter`

`Assets/CoreAI/Runtime/Core/Features/Orchestration/ThinkBlockStreamFilter.cs:9-15` — вырезает
**инлайновые** теги `<think>` / `</think>` из текста, с состоянием через границу чанков (буфер
`:13`, флаг `:15`), с опциональным сливом скрытого текста в `ReasoningSink` (`:22`).

MEAI знает про рассуждения только как про **отдельный вид контента** (`TextReasoningContent`) — то
есть про провайдера, который прислал их полем. Инлайновый тег внутри обычного текста — не зона MEAI.
Оставить.

### 2.5. Весь узел «инструменты из текста»

Обоснование — 0.4. Дополнительно:
- `LlmToolCallTextExtractor` — **публичный** тип (`LlmToolCallTextExtractor.cs:13`, методы `:54
  TryExtract`, `:312 StripForDisplay`, `:326 StripCodeBlocks`, `:353 LooksLikeToolCallJson`, `:408
  FindBalancedToolCallSpans`). Удаление — ломающее изменение публичного API фреймворка.
- В 7.35.0 узел **уже приведён в порядок**: закрыт гейтом по каналу, у публичного класса безопасное
  умолчание `allowTextShapedToolCalls = false` (`SmartToolCallingChatClient.cs:56`), есть явный
  опт-ин `LlmCompletionRequest.AllowTextShapedToolCallsOnNativeEndpoint` (`ILlmClient.cs:220`).
  Работа, которую хотели снять, вместо этого уже сделана правильно.
- Гейт стоит **в трёх местах и в одном смысле**, что и делает его снимаемым только целиком:
  непоток — `SmartToolCallingChatClient.cs:215-234` (нативные вызовы всегда выигрывают, текст —
  строгий запасной путь); поток — `MeaiLlmClient.cs:761-765`, применяется на разборе (`:1244`) и на
  починке обрезанного JSON (`:1398`); показ — `AiOrchestrator.IsTextShapedToolChannel`
  (`:1712-1722`), используется в `SanitizeAndPublish` (`:1607`) и `ShouldStripToolResultEcho`
  (`:1697-1700`). Последний и означает «на нативном эндпойнте JSON в ответе — это модель, которая
  ПОКАЗЫВАЕТ JSON, и трогать его нельзя» (обоснование `:1600-1606`).
- Достройка обрезанного JSON и удержание текста с первой незакрытой `{` лежат **не** в экстракторе, а
  в `MeaiLlmClient`: `TryBuildMalformedTextToolCall` (`:1917-1946`) и `GetHybridSafeSegments`
  (`:2389-2439`) + живой дренаж `DrainHybridSafeSegments` (`:799-853`). Тот, кто пойдёт «удалять
  экстрактор», обязан знать, что это четыре разных места под одним гейтом.

**Не трогать.** Если владелец всё же захочет его убрать — это отдельное решение с явной ценой
«локальные модели больше не вызывают инструменты», а не побочный эффект перехода на MEAI.

### 2.6. Пять декораторов `ILlmClient`

| Декоратор | Файл:строка | Почему MEAI не заменяет |
|---|---|---|
| `RetryingStreamingLlmClientDecorator` | `:11-26`, цикл `:97-120` | Повтор потока **только до фиксации**. У MEAI retry-middleware нет вообще; Polly / `Microsoft.Extensions.Resilience` в `Assets/Packages` не лежит, и его семантика «повторить весь вызов» на `IAsyncEnumerable` не ложится. |
| `TimeoutLlmClientDecorator` | `:11-33`, поток `:240`, `:257` | Бюджет **простоя** (каждый чанк перевзводит), а не общий дедлайн. `CancellationTokenSource(timeout)` использует таймеры `System.Threading` — ровно то, чего избегает WebGL-маршалер. |
| `CircuitBreakerLlmClientDecorator` | `:28`, `:54-67` | У MEAI нет. Не собран в боевой контейнер, но публичен (см. 1.3). |
| `LoggingLlmClientDecorator` | `:13-17`, `:48-61` | `LoggingChatClient` закрывает половину и на другом слое (`IChatClient`, не `ILlmClient`); вторая половина — ретрай — не закрывается ничем. |
| `ClientLimitedLlmClientDecorator` | `:9-12`, `:108-127` | Продуктовая квота, не забота фреймворка. |

Порядок сборки боевой цепи (снаружи внутрь), `LlmPipelineInstaller.cs:124-149`:
`TimeoutLlmClientDecorator` (`:127`) → `LoggingLlmClientDecorator` (`:128`) →
`RetryingStreamingLlmClientDecorator` (`:131`) → `RoutingLlmClient` (`:132-137`) → реестр →
`OpenAiChatLlmClient` → `MeaiLlmClient` → `MeaiOpenAiChatClient`.

### 2.7. `ToolCallHistoryTrimmer` и `LlmResponseSanitizer`

- `ToolCallHistoryTrimmer.Trim` (`ToolCallHistoryTrimmer.cs:30-93`) режет историю **целыми
  единицами «assistant с `tool_calls` + его `tool`-результаты»** (обоснование `:58-63`), чтобы ни
  одно `tool`-сообщение не осиротело. У OpenAI осиротевший `tool` без пары — это 400.
  MEAI-редьюсеры (`MessageCountingChatReducer`, `SummarizingChatReducer` — оба есть в 10.7.0) по
  своей XML-доке **исключают** сообщения с function-call/function-result из вывода, то есть про
  парность вообще не думают. Прямой замены нет. **Оставить.**
- `LlmResponseSanitizer.StripLeadingSystemPromptEcho` (`LlmResponseSanitizer.cs:17-57`) — обход
  дурного поведения модели. Аналога у MEAI нет. **Оставить.**

### 2.8. Не заменять `MeaiOpenAiChatClient` на `Microsoft.Extensions.AI.OpenAI`

Соблазн большой: официальный адаптер закрыл бы разом SSE-разбор (`:833-904`, `:1838-1892`),
пересборку потоковых tool-call дельт (`:2182-2669`, 488 строк) и отображение сообщений в конверт
OpenAI (`:2676-2821`). **Делать этого нельзя, и причина не вкусовая:**

- WebGL запрещает `System.Net` / `HttpClient`. У нас это отражено в коде: конструктор поверх
  `HttpClient` **выключен препроцессором на WebGL** — `MeaiOpenAiChatClient.cs:176-193`
  (`#if !UNITY_WEBGL || UNITY_EDITOR`).
- Весь транспортный шов держится на нашем `IOpenAiHttpTransport` и на переключении
  `MeaiOpenAiChatClient.cs:417-429`: если у транспорта нет SSE, ход уходит в непотоковый запрос с
  симуляцией апдейтов (`FullResponseToSimulatedStreamingUpdates`, `:1165-1235`). У официального
  адаптера этого шва нет.
- Пакет `Microsoft.Extensions.AI.OpenAI` в `Assets/packages.config` отсутствует и тянет за собой
  OpenAI SDK + `System.ClientModel`, чья пригодность для WebGL не проверена никем.

Отдельно **оставить** (это не дубликаты): редакция ошибок (`:1495-1551`, 401 глушится целиком
`:1513-1516`), разбор окна Retry-After из тела Groq (`:125-155`), сторож голодающего потока
(`:639-661`), изоляция `reasoning_content` / `<think>` (`:1663-1836`), срезание CORS-чувствительных
заголовков на WebGL (`:965-969`, `:1110-1118`).

---

## 3. Этапы

Каждый этап самостоятельно ценен, заканчивается зелёным прогоном и коммитится **отдельно**.
Порядок выбран так, чтобы ничего не пришлось возвращать: сначала то, что не меняет поведение, потом
то, что меняет.

Общая процедура для каждого этапа (по `AGENTS.md` и `CONTRIBUTING.md`):
1. `dotnet build CoreAI.Core.csproj` (+ `CoreAI.Source.csproj`) — быстрый гейт компиляции, работает
   при захваченном редактором локе.
2. Полный EditMode-прогон в **четырёх** конфигурациях (`core` / `llm` / `lua` / `full`) —
   `.github/workflows/ci.yml`. Ориентир масштаба: ~2096 тестовых атрибутов в EditMode двух пакетов.
   **Сверять число прошедших тестов, а не цвет.**
3. Запись в `Assets/CoreAI/CHANGELOG.md` и/или `Assets/CoreAiUnity/CHANGELOG.md`, бамп версии через
   `python tools/bump_version.py <version>` (двигает все семь `package.json` в ногу).
4. Актуализация затронутых доков (перечислены поэтапно).
5. Аудит диффа отдельным сабагентом до коммита.

Оценки — в человеко-днях одного исполнителя, знакомого с кодом; прогон четырёх конфигураций в них не
входит.

---

### Этап 1. Убрать надгробие и исправить ложную запись — 0.25 дня

**Что меняется.** Удалить `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLoggingFunctionInvokingChatClient.cs`
и его `.meta`. Утверждение файла (`:4`) о недоступности `FunctionInvokingChatClient` ложно и уже
однажды могло стоить кому-то решения «значит, MEAI нам не подходит».

Заодно — **исправить доки, которые врут в обе стороны сразу**:
- `Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md:23,120,186,205,294` и `README.md:704` до сих пор описывают
  `FunctionInvokingChatClient` как наш движок вызова инструментов. Он им **не является** — движок это
  `SmartToolCallingChatClient` + `ToolExecutionPolicy` (непоток) и `MeaiLlmClient` + `ToolExecutionPolicy`
  (поток). Читатель этих доков сегодня получает прямо противоположную картину той, что описана здесь.
- `Assets/CoreAiUnity/Editor/CoreAISettingsAssetEditor.uxml:280` — тот же устаревший тип в подсказке
  инспектора.
- `INSTALL.md:87-110` — указать **минимально поддерживаемую версию MEAI: 10.7.0** (см. 1.3, последняя
  строка), потому что сегодня потребитель не имеет способа узнать её иначе как компиляцией.

**Чем закрывается.** Ничем новым — этап доказывается тем, что четыре конфигурации остаются зелёными
с прежним числом тестов. Файл не содержит типа, ссылок на идентификатор нет.

Отдельная ценность этапа: он приводит **записанную историю** в соответствие с кодом до того, как по
ней начнут принимать решения этапы 6-7. Сегодня доки и надгробие вместе утверждают, что MEAI-путь
недоступен — то есть противоречат самому решению владельца.

**Как проверить вживую.** Не требуется: наблюдаемого поведения не меняется.

**Риск.** Нулевой. Единственный подвох — не забыть `.meta`.

---

### Этап 2. Обезвредить ловушку `ChatResponse.Text` — 0.5 дня

**Что меняется.** `MeaiLlmClient.cs:242` (`string text = response.Text;`) заменить на чтение текста
**последней ассистентской реплики** ответа. Сегодня это эквивалентно (доказательство — D5), завтра —
нет.

Почему **до** этапа 7, а не после: этап 7 вводит в цепь `FunctionInvokingChatClient`, который по
устройству возвращает многосообщенческий `ChatResponse` (assistant + tool + assistant). В этот момент
`response.Text` начнёт молча приклеивать содержимое tool-результатов к видимой реплике учителя.
Чинить это заранее — дешевле на порядок, чем ловить потом на живой модели.

Заодно проверить оставшиеся чтения: `MeaiLlmClient.cs:234` (только лог), `ChatMessage.Text` в
`MeaiLlmClient.cs:375`, `MeaiOpenAiChatClient.cs:247,1213-1215`,
`ClientLimitedLlmClientDecorator.cs:144` — они по одному сообщению и безопасны.

**Чем закрывается.** Новый EditMode-тест: `ChatResponse` из трёх сообщений
(assistant «видимый текст» → tool «сырой результат» → assistant «итог») даёт наружу только текст
последней ассистентской реплики, а не склейку всех трёх. Файл — `MeaiLlmClientEditModeTests.cs`.

**Как проверить вживую.** Не требуется (поведение сегодня не меняется), но тест обязателен: он и есть
единственное доказательство, что этап что-то дал.

**Риск.** Низкий. Возможный подвох: ответ без ассистентских сообщений вообще (ошибка провайдера) —
поведение при пустом списке задать явно и покрыть тестом.

---

### Этап 3. `LlmUsageAccumulator` — тонкая обёртка над `UsageDetails.Add` — 0.5 дня

**Что меняется.** Тело `LlmUsageAccumulator.Accumulate` (`LlmUsageAccumulator.cs:23-62`) сводится к:
скопировать накопитель, вызвать штатный `UsageDetails.Add(incoming)`, вернуть копию.

Копию делаем **осознанно**: `UsageDetails.Add` мутирует `this`, а наш контракт (`:19-21`, `:30-33`)
обещает не трогать `UsageDetails` провайдера. Это единственная причина, по которой обёртка остаётся,
и её надо записать комментарием `// WHY:`.

**Что этап даёт.** Перестают теряться типизированные счётчики: `CachedInputTokenCount`,
`ReasoningTokenCount`, `InputTextTokenCount` / `OutputTextTokenCount`, аудио-счётчики. Сегодня
`Accumulate` их просто не видит.

**Чем закрывается.** Тест: сложение двух `UsageDetails`, у которых заполнены `CachedInputTokenCount`
и `ReasoningTokenCount`, даёт сумму по обоим полям; исходные объекты не изменились.

**Как проверить вживую.** Не требуется: на этом этапе поля ещё никем не заполняются (заполнение —
этап 4). Этап специально сделан «пустым по наблюдаемому эффекту», чтобы отделить механику сложения
от механики заполнения.

**Риск.** Низкий. Подвох: `UsageDetails.Add` может сложить `AdditionalCounts` иначе, чем наш ручной
цикл `:50-59` (например, по-другому обойтись с отсутствующим ключом). Проверить тестом на смешанных
наборах ключей до, а не после.

---

### Этап 4. Заполнить типизированные счётчики токенов — 1 день

**Что меняется.**
1. `MeaiOpenAiChatClient.BuildUsageDetailsFromOpenAiUsageObject` (`:2043-2067`) начинает заполнять
   `CachedInputTokenCount` из `prompt_tokens_details.cached_tokens` и `ReasoningTokenCount` из
   `completion_tokens_details.reasoning_tokens`.
2. `MeaiLlmClient.ExtractCacheTokenCounts` (`:1745-1782`) читает **сначала типизированное поле**, и
   только при его отсутствии падает в старую эвристику по `AdditionalCounts`.
3. `BuildAdditionalUsageCounts` (`:2069-2117`) **не трогаем**: расплющивание остаётся, потому что оно
   же несёт нестандартные счётчики провайдеров (Anthropic-подобные `cache_creation_*`), которых у
   MEAI типизированных полей нет.

Порядок «типизированное вперёд эвристики» снимает и дефект D3: у ключа
`cache_creation_read_tokens` больше нет шанса попасть сразу в оба ведра, потому что до эвристики
дело не дойдёт.

**Чем закрывается.**
- `MeaiOpenAiChatClientSseEditModeTests` / `...HttpEditModeTests`: `usage` с
  `prompt_tokens_details.cached_tokens` даёт `UsageDetails.CachedInputTokenCount`, а не только запись
  в `AdditionalCounts`.
- `MeaiLlmClientEditModeTests:329-346` (`ExtractCacheTokenCounts_MatchesProviderKeyVariants`) —
  **существующий** тест эвристики; его не удалять, а дополнить кейсом «типизированное поле есть →
  эвристика не применяется» и кейсом «типизированного нет → старое поведение сохранено».

**Как проверить вживую.** Один ход учителя на эндпойнте, который реально отдаёт
`prompt_tokens_details` (OpenAI-совместимый прокси проекта), и сверка чисел в
`LlmMetricsTracker`-читах RedoSchool с числами из ответа провайдера.

**Риск.** Средний. Главный: у части провайдеров `cached_tokens` **входит** в `prompt_tokens`, у части
— нет. Мы этот вопрос не решаем и не должны: мы переносим ровно то число, что прислал провайдер, в
поле с тем же смыслом. Записать это ограничение в `Assets/CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md`
(§1 «Two different meanings of tokens») явно.

**Блокирует потребителя:** `CachedInputTokenCount` и `ReasoningTokenCount` в MEAI **9.10.2
отсутствуют** (проверено по XML-доке `Assets/Packages/Microsoft.Extensions.AI.Abstractions.9.10.2/...`
в RedoSchool). См. раздел 5.

---

### Этап 5. Транспорт начинает заполнять `MessageId` / `ResponseId` / `FinishReason` / `ChatClientMetadata` — 1.5 дня

**Что меняется.** Чисто аддитивный этап: **никто ещё ничего из этого не читает.**

1. `MeaiOpenAiChatClient` проставляет `ResponseId` и `MessageId` на каждом `ChatResponseUpdate`
   (сегодня — 0 вхождений `MessageId` в файле). Источник — поле `id` ответа `/chat/completions`;
   у каждого раундтрипа свой `id`, поэтому смена `MessageId` естественно совпадает с границей
   раундтрипа.
2. Проставляется `FinishReason` из `choices[0].finish_reason`.
3. `GetService` (`:3012-3015`) перестаёт безусловно возвращать `null` и отдаёт
   `ChatClientMetadata` (имя провайдера, endpoint, model id) — это предусловие для любого
   штатного middleware MEAI, включая этап 7.

**Жёсткая зависимость по порядку.** Пересборка дельт в `SplitForSmoothStreaming`
(`MeaiOpenAiChatClient.cs:677-690`) создаёт **новые** `ChatResponseUpdate` и переносит только
`ModelId` (`:686`). `MessageId` (и `ResponseId`) обязаны переноситься там же, иначе сглаженные куски
окажутся без идентификатора и этап 6 сломает границу реплик именно на тех моделях, ради которых
сглаживание существует. Это единственное место в плане, где порядок нельзя переставить.

Симметрично проверить `FullResponseToSimulatedStreamingUpdates` (`:1165-1235`) — путь WebGL и отката
«поток → не-поток».

**Чем закрывается.**
- `MeaiOpenAiChatClientSseEditModeTests`: у всех апдейтов одного ответа один и тот же `MessageId`;
  у двух последовательных ответов — разные.
- Отдельный тест на сглаживание: text-only дельта длиннее 24 символов режется на куски, и **у всех
  кусков** сохраняются `ModelId` и `MessageId` исходной дельты.
- Тест на `GetService(typeof(ChatClientMetadata))` — возвращает непустые метаданные.

**Как проверить вживую.** Не требуется: наблюдаемого поведения нет. Именно поэтому этап отделён от
этапа 6 — чтобы «заполнили» и «начали доверять» упали в разные коммиты и разные прогоны.

**Риск.** Низкий-средний. Подвох: некоторые OpenAI-совместимые серверы (llama.cpp, LM Studio) шлют
`id` не на каждом чанке или шлют пустой. Правило задать явно: `MessageId` берётся из первого чанка
с непустым `id` и **латчится на весь ответ**; если `id` нет вовсе — синтезировать один на ответ
(как уже делается для tool-call id, `:2468`, `:2596`).

---

### Этап 6. `StartsNewMessage` выводится из `MessageId` — 1.5 дня

**Что меняется.** Латч `visibleMessageBoundaryPending` (`MeaiLlmClient.cs:474-489`) перестаёт быть
источником истины. Граница определяется сменой `ChatResponseUpdate.MessageId`.

Поле `LlmStreamChunk.StartsNewMessage` (`ILlmClient.cs:354`) **остаётся** — это внешний API, его
читают `StreamedMessageJoiner.cs:61`, `AiOrchestrator.cs:548,759-771,923-925` и
`CoreAiChatPanel.cs:2924,2945`, а в RedoSchool — панель чата. Меняется только то, **откуда берётся
его значение**.

**Это не чистая замена — и это надо сделать честно.** Латч имеет семантику «первый **видимый** чанк
после границы» (`MarkVisibleChunk`, `:479-489`): чанки рассуждений и служебные события границу не
съедают. Смена `MessageId` даёт границу на **первом чанке любого рода**. Поэтому правильная форма
этапа: `MessageId` **взводит** латч, а расстановку флага на первый видимый чанк оставляем как есть.
Убирается не латч, а **ручное вычисление момента взвода** — то, ради чего он был написан.

Заодно проверяется живучесть правки 7.35.0 «флаг переносится на следующий непустой чанк, если фильтр
съел чанк целиком» (`AiOrchestrator.cs:759-771`) — она остаётся нужной и после смены источника.

**Чем закрывается.**
- `MeaiStreamingToolCallEditModeTests` — сегодня **единственное** покрытие `StartsNewMessage`
  (`:84` второй раундтрип помечает только первый видимый чанк; `:107-112` границы; `:122` один
  раундтрип не помечает ничего). Все три обязаны остаться зелёными **без правки ожиданий**. Если
  ожидание приходится править — источник выбран неверно, этап откатить.
- Новый тест: два раундтрипа с одинаковым `MessageId` (провайдер, который его не меняет) не должны
  давать ложную границу; граница между раундтрипами при этом всё равно обязана быть — её ставит
  вход в следующую итерацию цикла, а не транспорт.

**Как проверить вживую.** Ход учителя с вызовом `spawn_quiz`: реплика до карточки и реплика после
обязаны остаться **двумя** пузырями в чате. Это ровно тот дефект («Проверь себя:**Ход завершён…**»),
ради которого признак заводили — он описан в `Assets/CoreAI/CHANGELOG.md` (7.35.0). Проверять на
RedoSchool, в брифинг-чате.

**Риск.** Средний-высокий — самый рискованный этап плана по соотношению «незаметность дефекта /
стоимость». Склейка двух реплик в один пузырь не роняет ничего, тестами ловится только на тех
конкретных формах, что уже записаны, а увидит её ученик. Не сливать с другими этапами.

---

### Этап 7. Непотоковый цикл — на `FunctionInvokingChatClient` — 3 дня

Центральный этап «MEAI как основной путь». Делается **последним из содержательных**, потому что
опирается на этапы 2 (ловушка `.Text`) и 5 (`ChatClientMetadata` для middleware).

**Что меняется.** В `SmartToolCallingChatClient` снимается **оболочка цикла** — то, что поимённо
совпадает с `FunctionInvokingChatClient`:

| Наше | Файл:строка | Штатное |
|---|---|---|
| кэп раундтрипов | `:33`, `:70`, `:127-128` | `MaximumIterationsPerRequest` |
| бюджет подряд идущих ошибок | `:24`, `:51`, `:67` + `ToolExecutionPolicy.cs:1964,1975` | `MaximumConsecutiveErrorsPerRequest` |
| параллельность | `ToolExecutionPolicy.cs:1121-1256` | `AllowConcurrentInvocation` |
| сборка `ChatRole.Tool` + `FunctionResultContent` | `:413-415` | `CreateResponseMessages` |
| завершение хода по `ILlmTool.EndsTurn` | `:375` | `FunctionInvocationContext.Terminate` |

**Что при этом НЕ выбрасывается — и как это удерживается.** `ToolExecutionPolicy` (2144 строки)
остаётся целиком: она общая с потоковым циклом, который на MEAI не переводится (2.1). Выбросить её —
значит получить две разные семантики инструментов на двух путях.

Механика подключения — штатный шов MEAI, а не костыль: свойство
**`FunctionInvokingChatClient.FunctionInvoker`** (есть в 10.7.0). По XML-доке: «If this delegate is
set to a non-null value, `InvokeFunctionAsync` will replace its normal invocation with a call to
this delegate, enabling this delegate **to assume all invocation handling of the function**». То
есть в делегат отдаём `ToolExecutionPolicy.ExecuteSingleAsync` — и внутри остаются **все** наши
поведения:

- починка имени инструмента (`ToolExecutionPolicy.cs:389-433`),
- предвалидация обязательных аргументов + подсказка схемой (`:593`, `:833-845`, `:874-939`),
- приведение `JObject`/`JArray` к JSON-строке (`:609-628`),
- таймаут на вызов через `ILlmAsyncMarshaler` без `CancelAfter` (`:634-699`, обоснование `:639`),
- маршалинг тела инструмента в главный поток Unity (`:630`; реализация —
  `UnityMainThreadLlmAsyncMarshaler.cs:17`; снятие `SynchronizationContext` —
  `MeaiToolTaskBridge.cs:31`),
- подавление дублей и эха между ходами (`:252-387`, `:1901-1962`),
- сериализация мутирующих встроенных инструментов (`:1078-1119`),
- усечение результата (`:704-713`), события и нотификатор (`:590`, `:724-750`).

`EndsTurn` (`ToolExecutionPolicy.cs:488`, `:739`) переносится на `FunctionInvocationContext.Terminate`
— по XML-доке ровно та же семантика: «that subsequent request will not be issued and instead the
loop immediately terminated».

**Что скорее всего придётся оставить рядом с FICC** (проверить в первый же день этапа, до правок):
- «мягкий финальный ход без инструментов» при исчерпании кэпа или бюджета ошибок
  (`SmartToolCallingChatClient.cs:145`, `:350`, реализация `:778-800`) — у FICC такого нет, он просто
  останавливает цикл;
- принудительный режим инструмента и его повтор (`:238-263`, `:612-621`);
- понижение `ToolMode` до `Auto` после первого исполненного вызова (`:180-185`, `:634`);
- подталкивание при пустом ответе после успешного вызова (`:281-297`);
- усечение текста ответа до `MaxResponseChars` (`:305-309`, `:385-389`, `:654`);
- обрезка истории (`:419-442`, `ToolCallHistoryTrimmer`);
- поверхность трассировки `LastExecutedToolCalls` (`:78`, `:447`).

Если после честной оценки окажется, что «рядом» остаётся больше, чем снимается, **этап следует
остановить и доложить**: тогда FICC даёт не упрощение, а третий цикл. Это допустимый исход, и он
должен быть записан, а не замаскирован.

**Чем закрывается.** `SmartToolCallingChatClientEditModeTests.cs` (1066 строк, 18 точек
конструирования) и `ToolExecutionPolicyEditModeTests.cs` (2179 строк) — **ожидания не переписывать**.
Это и есть приёмка этапа: непотоковый цикл обязан вести себя так же, как вёл. Плюс
`ResilienceFeaturesEditModeTests`, `StreamedTurnDeferredToolEditModeTests`,
`MessagePipeEventPublishingEditModeTests`, `RetryFallbackToolTraceSuppressionEditModeTests`.

**Как проверить вживую.** Непотоковый путь в бою — это ветка изображения/зрения
(`CoreAiChatService.cs:671`, `:829`) и агентные задачи при выключенном `EnableStreaming`. Прогнать:
(а) вопрос с картинкой и вызовом инструмента; (б) `AiOrchestrator.RunTaskAsync` с `EnableStreaming =
false` и цепочкой из двух инструментов. Плюс PlayMode-набор `LlmVerification` на живой модели —
`MultiToolChainPlayModeTests`, `ChatWithToolCallingPlayModeTests`, `ToolNameRepairPlayModeTests`.

**Риск.** Высокий по объёму, средний по последствиям: боевой чат RedoSchool идёт **потоком** и через
этот класс не проходит (1.3). Подвохов два, оба тихие:

1. **Смена умолчаний.** У FICC кэп **40**, у нас **20**; бюджет ошибок **3** у обоих; параллельность
   у FICC `false`, а у нас `MaxParallelToolCalls = 4` (`ICoreAISettings.cs:265`). Все три обязаны
   выставляться **явно** из `ICoreAISettings`, а не наследовать умолчания MEAI. Это первое, что
   нужно написать, и первое, что нужно покрыть тестом.
2. **Ловушка «поток без инструментов».** `SmartToolCallingChatClient.GetStreamingResponseAsync`
   (`:699-718`) сегодня инструменты **не исполняет вовсе**: нативный `FunctionCallContent`
   проходит насквозь, а класс лишь пишет предупреждение (`:708`). Если по ходу этапа кто-нибудь
   поставит новый FICC-обёрнутый клиент на потоковый путь, поведение изменится молча — вызовы
   начнут исполняться там, где раньше не исполнялись, с буферизацией из 2.1. Требование этапа:
   потоковый метод остаётся пробросом, и это закрывается тестом-стражем, а не договорённостью.

---

### Этап 8 (условный). Снять эвристику `ExtractCacheTokenCounts` — 0.5 дня

**Выполнять только после того, как этап 4 отработал на бою не меньше одного релиза** и в логах не
осталось случаев «типизированного поля нет, сработала эвристика».

**Что меняется.** Из `MeaiLlmClient.cs:1745-1782` уходит подстроковый разбор ключей; остаётся чтение
типизированных полей. Расплющивание в `AdditionalCounts` (`MeaiOpenAiChatClient.cs:2069-2117`)
**остаётся** — нестандартные счётчики провайдеров больше взять неоткуда.

**Чем закрывается.** `MeaiLlmClientEditModeTests:329-346` переписывается с эвристики на типизированные
поля. Это единственное место в плане, где переписывание существующего теста **ожидаемо и нормально**:
удаляется сам механизм, который тест проверял.

**Риск.** Средний, и он в наблюдаемости: провайдер, который шлёт кэш только нестандартным ключом,
после этого этапа перестанет учитываться. Поэтому этап условный и требует данных с боя, а не
рассуждения.

---

### Сводка порядка

| Этап | Меняет поведение? | Блокирует потребителя? | Оценка |
|---|---|---|---|
| 1. Надгробие + минимальная версия в `INSTALL.md` | нет | нет | 0.25 д |
| 2. Ловушка `ChatResponse.Text` | нет (сегодня) | нет | 0.5 д |
| 3. `LlmUsageAccumulator` → `UsageDetails.Add` | нет | нет | 0.5 д |
| 4. Типизированные счётчики токенов | да (числа) | **да** — RedoSchool на 9.10.2 | 1 д |
| 5. `MessageId` / `ResponseId` / `ChatClientMetadata` | нет (аддитивно) | нет | 1.5 д |
| 6. `StartsNewMessage` из `MessageId` | **да** (границы реплик) | нет | 1.5 д |
| 7. Непотоковый цикл на FICC | да (непотоковый путь) | нет | 3 д |
| 8. Снятие эвристики кэш-токенов | да | нет | 0.5 д (условный) |

Итого ≈ 8.75 человеко-дней без прогонов и без разбора последствий.

---

## 4. Риски и чего план не решает

### 4.1. Останется недоказанным: точное поведение FICC в стриминге

Я не декомпилировал `Microsoft.Extensions.AI.dll` и не гонял FICC на живой модели. Утверждение «в
стриминге после первого `FunctionCallContent` буферизует остаток итерации» взято из предыдущего
исследования и подкреплено только формой API (2.1). На решение это не влияет — мы туда не идём в
любом случае, — но если кто-то захочет пойти, **проверять надо замером**.

### 4.2. Два цикла вызова инструментов остаются двумя

План снимает дубликат «наша оболочка ↔ MEAI», но **не** снимает дубликат «наш потоковый цикл ↔ наш
непотоковый цикл» (`MeaiLlmClient.cs:431-1600` против `SmartToolCallingChatClient.cs:90-449`). После
этапа 7 их станет даже заметнее: один будет на MEAI, другой — свой. Это осознанная цена, потому что
альтернатива — потерять стриминг. Расхождение удерживается общими помощниками (`ToolExecutionPolicy`,
`ToolCallHistoryTrimmer`, `LlmUsageAccumulator`), и после этапа 7 надо **отдельно** проверить, что
поведение путей не разъехалось: сегодня это ничем не сторожится.

**Предложение (в план не входит, кандидат в `TODO.md`):** страж-тест, гоняющий один и тот же сценарий
инструментов по обоим путям и сверяющий результат. Сейчас ближайший по смыслу —
`ToolCallExtractionParityEditModeTests.cs` (1062 строки), но он про извлечение, а не про цикл.

### 4.3. Опора на предположение: `MessageId` совпадает с границей раундтрипа

Этапы 5-6 держатся на том, что у каждого раундтрипа свой `id` ответа. Для OpenAI-совместимых
серверов это норма, но **не гарантия спецификации**. Для llama.cpp / LM Studio поведение не
проверено. Смягчение — правило латча и синтеза `MessageId` (этап 5) и тест «одинаковый `MessageId` не
даёт ложной границы» (этап 6). Если сервер шлёт один `id` на всю сессию, граница между раундтрипами
всё равно ставится входом в следующую итерацию цикла, а не транспортом.

### 4.4. Что сломается у чужих потребителей пакета

CoreAI — фреймворк, у него есть потребители помимо RedoSchool.

| Этап | Ломающее изменение | Кого задевает |
|---|---|---|
| 4 | Требует MEAI ≥ 10.1.1 (типизированные счётчики) — фактически ≥ 10.7.0 по остальной кодовой базе | **Любого, кто стоит на 9.x.** Сегодня нижняя граница нигде не объявлена (`INSTALL.md:87-110`), поэтому потребитель узнает об этом ошибкой компиляции. Этап 1 закрывает дыру в доке, но не в чужих проектах. |
| 6 | Момент выставления `StartsNewMessage` может сдвинуться на один чанк | Тот, кто рисует пузыри чата по этому флагу. Наблюдаемо как «реплика разбилась не там». |
| 7 | Публичный конструктор `SmartToolCallingChatClient` (`:38-70`, 8 параметров + `allowTextShapedToolCalls`) почти наверняка изменится | Тот, кто конструирует его руками. В самом репозитории таких — ноль в рантайме (единственное место `MeaiLlmClient.cs:164`), но снаружи узнать нельзя. Требует бампа **мажорной** версии пакета. |
| 8 | Провайдер с нестандартным ключом кэша перестанет учитываться | Тот, кто считает деньги по кэш-токенам на Anthropic-подобном прокси. |

### 4.5. Чего план не касается вовсе

- **Замена `MeaiOpenAiChatClient` на `Microsoft.Extensions.AI.OpenAI`** — обоснование в 2.8. Это
  самый крупный формальный дубликат (≈3000 строк против готового адаптера) и его **нельзя** снять,
  пока WebGL остаётся первоклассной целью. Записать в `TODO.md` как «закрыто с причиной», чтобы
  вопрос не переоткрывали.
- **Перевод конверта запроса с Newtonsoft на `System.Text.Json`** (D9, `MeaiOpenAiChatClient.cs:2936`).
- **Разделение ретрая и логирования** в `LoggingLlmClientDecorator` (1.3).
- **`ChatClientBuilder` / `UseLogging` / `UseOpenTelemetry` / `UseDistributedCache`** — все доступны в
  10.7.0, все не используются (0 вхождений). Это возможности, а не дубликаты: у нас нет своего
  OpenTelemetry и нет своего распределённого кэша, снимать нечего. Отдельная задача «а не начать ли
  пользоваться» — не эта.
- **`ReasoningOptions` / `ReasoningEffort`** (есть в 10.7.0, 0 вхождений). У нас свои
  `LlmReasoningMode` / `ThinkingBudgetTokens` (`MeaiLlmClient.cs:2714-2715`). Формально дубликат;
  фактически перевод требует, чтобы транспорт умел отображать `ReasoningOptions` в диалект каждого
  провайдера (у Qwen, OpenRouter и OpenAI он разный). **Не уверен, что это выигрыш.** Отдельная
  задача.
- **Обновление MEAI 10.7.0 → 10.9.0.** Постановка называет 10.9.0 актуальной; проверить это офлайн я
  не мог. Ни один этап плана не требует ничего сверх 10.7.0 — всё нужное (`UsageDetails.Add`,
  `CachedInputTokenCount`, `ReasoningTokenCount`, `MessageId`, `FunctionInvoker`,
  `FunctionInvocationContext.Terminate`, `ChatClientMetadata`) проверено по XML-доке **поставляемой**
  сборки 10.7.0. Бампать версию **в рамках этого плана не надо**: это добавит к каждому этапу
  переменную, которой там не место, и упрётся в раздел 5.

### 4.6. Асимметрия, которую план не трогает, но обязан назвать

Повтор при провале структурного ответа существует **только на непотоковом пути**
(`AiOrchestrator.cs:423-451`, `CloneTaskWithStructuredHint` `:1799-1836`); на потоковом та же
ситуация терминальна, повтора нет (`:944-955`). Это реальная разница поведения двух путей, а не
ошибка чтения. Этап 7 трогает непотоковый путь — исполнитель обязан **не** «заодно причесать» эту
асимметрию: она либо остаётся как есть, либо становится отдельной задачей с отдельным решением
владельца.

### 4.7. Риск процесса

Этапы 4-7 трогают `MeaiLlmClient.cs` (2743 строки) и `MeaiOpenAiChatClient.cs` (3018 строк) — оба
уже помечены в `TODO.md:543` как «Oversized adapters (M2, both grew)». Работы **нельзя пускать
параллельными воркерами**: файлы пересекаются на всех этапах, кроме 1 и 3.

---

## 5. Миграция потребителя (RedoSchool)

### 5.0. Стартовое расхождение — и оно больше, чем разница версий MEAI

| | CoreAI (`D:\Git\CoreAI`) | RedoSchool (`D:\Git\RedoSchool`) |
|---|---|---|
| версия пакетов CoreAI | 7.35.0 (рабочее дерево), 7.34.0 (HEAD) | пин на **`v7.17.0`** (`Packages/manifest.json:9-10`) |
| Microsoft.Extensions.AI | **10.7.0** (`Assets/packages.config:6-7`) | **9.10.2** (`Assets/packages.config`) |

Отставание пакета на 17 минорных версий — это не часть данного плана, но **это то, во что упрётся
первый же его этап при попытке довезти до игры**. Порядок для RedoSchool обязан быть: сначала
догнать CoreAI до текущего тега на нынешнем MEAI, потом принимать этапы плана.

### 5.1. Как MEAI разрешается в RedoSchool

`CoreAI.Core.asmdef` (`Assets/CoreAI/Runtime/Core/CoreAI.Core.asmdef:8-15`) ссылается на
`Microsoft.Extensions.AI.dll` и `Microsoft.Extensions.AI.Abstractions.dll` **по имени файла**
(`precompiledReferences`, `overrideReferences: true`). Unity резолвит их по имени сборки в проекте —
то есть в RedoSchool CoreAI будет собран против **9.10.2**, что бы ни лежало в репозитории CoreAI.

Что из нужного плану **есть** в 9.10.2 и чего **нет** (проверено по XML-доке
`Assets/Packages/Microsoft.Extensions.AI.Abstractions.9.10.2/lib/netstandard2.0/*.xml` в RedoSchool):

| Член | 9.10.2 | 10.7.0 |
|---|---|---|
| `ChatResponseUpdate.MessageId` | **есть** | есть |
| `UsageDetails.AdditionalCounts` | **есть** | есть |
| `ChatOptions.ContinuationToken` / `AllowBackgroundResponses` | **есть** | есть |
| `TextReasoningContent` | **есть** | есть |
| `UsageDetails.CachedInputTokenCount` | **НЕТ** | есть |
| `UsageDetails.ReasoningTokenCount` | **НЕТ** | есть |
| `ReasoningOptions` | **НЕТ** | есть |

Это и даёт разбиение по этапам: **этапы 1-3, 5, 6 проходят на 9.10.2 как есть; этап 4 (и следующий
за ним 8) требует, чтобы RedoSchool сначала поднял MEAI до 10.7.0.**

### 5.2. Точки касания RedoSchool

Игра работает через **наши** абстракции, а не через MEAI напрямую — это хорошая новость:
- `ILlmClient` / `LlmStreamChunk`: `GameLifetimeScope.cs:256`,
  `TeacherPromptDebugLlmClientDecorator.cs:170-221` (собственный декоратор!),
  `SendMessageUseCase.cs:118-172`, `Tests/EditMode/AI/LlmRequestRetryBudgetTests.cs:56`.
- `IAiOrchestrationService`: `SendMessageUseCase.cs:32-189`,
  `ChatPanelController.cs:844-856,2743-2768` (собственный декоратор-обёртка!),
  `BackendLessonOutcomeSync.cs:61-85`.

MEAI-типы всплывают наружу в четырёх местах:
- `TeacherPromptDebugLlmClientDecorator.cs:11,268-277` — читает `LlmCompletionRequest.ChatHistory`
  как `IList<MEAI.ChatMessage>`;
- `SpawnQuizTool.cs:10,146-170` и `SpawnDragAndDropTool.cs:10,119` — инструменты учителя через
  `AIFunction`;
- тесты `TeacherPromptDebugLlmClientDecoratorTests.cs`, `CoreAiV7HostIntegrationTests.cs:13,296`.

Ни одного использования `SmartToolCallingChatClient`, `ToolExecutionPolicy`,
`LlmToolCallTextExtractor` в RedoSchool нет (проверено grep'ом) — значит этапы 7 и 8 её кода не
касаются.

### 5.3. Что делать в RedoSchool после каждого этапа

| Этап | Действие в RedoSchool | Проверка |
|---|---|---|
| **1** | Ничего в коде. В `Assets/_source/Scripts/Platform/BackendSync/README.md` / `docs/` отметить нижнюю границу MEAI 10.7.0 как известное расхождение. | — |
| **2** | Ничего. | — |
| **3** | Ничего. | — |
| **4** | **Блокируется.** Сначала: поднять `Assets/packages.config` до MEAI 10.7.0, перевыкачать `Assets/Packages/`, снести `Microsoft.Extensions.AI.9.10.2` и `...Abstractions.9.10.2` вместе с `.meta`. Затем полный EditMode-прогон RedoSchool (норма — **2698**, замер 2026-08-31; сверять **число**, а не цвет — см. память `false-green-test-run`). | `LlmMetricsTracker` (`Platform/Cheats/Infrastructure/LlmMetricsTracker.cs`) читает только `PromptTokens`/`CompletionTokens` (`:140`), кэш-токены не использует — числа в чит-панели не должны измениться. |
| **5** | Ничего в коде. | — |
| **6** | **Глазами.** Открыть брифинг-чат, дать учителю ход с `spawn_quiz`: реплика до карточки и реплика после обязаны остаться **двумя** пузырями. Смотреть в `ChatPanelController` / `CreateMarkdownBubbleContent`. | Скриншот кадром из редактора (`unity cmd screenshot game`), а не WebGL-сборкой — см. память `verify-ui-in-editor-not-webgl-build`. |
| **7** | Проверить `TeacherPromptDebugLlmClientDecorator` (`:170`) и `ChatProgressReportingOrchestrator` (`ChatPanelController.cs:2743`) — оба реализуют наши интерфейсы, а не MEAI-шные, поэтому сигнатуры устоять должны. Если конструктор `SmartToolCallingChatClient` изменился — RedoSchool это не задевает (не конструирует). | Прогнать `Tests/EditMode/AI/*` и `Tests/EditMode/BackendSync/CoreAiV7HostIntegrationTests.cs`. |
| **8** | Ничего. | — |

### 5.4. Обязательный шаг после любого этапа

Правка CoreAI доезжает до RedoSchool только через **тег**: запушить `main` + тег, затем в
`D:\Git\RedoSchool\Packages\manifest.json:9-10` поднять оба пина на новый тег. Мост через
`Library/PackageCache` не годится — UPM стирает его при рестарте (память `coreai-push-and-update-via-git`).

---

## 6. Что взять с собой в `TODO.md`

Находки, которые выявились по ходу инвентаризации и в план не вошли:

1. `MeaiLlmClient.cs:242` — `ChatResponse.Text` склеит текст всех сообщений, как только в цепь войдёт
   многосообщенческий ответ. *(Закрывается этапом 2.)*
2. Ретрай спрятан внутри `LoggingLlmClientDecorator` (`:13-17`, `:48-61`) — разделить.
3. Нет стража паритета между потоковым и непотоковым циклами вызова инструментов (4.2).
4. `INSTALL.md` не объявляет минимальную версию MEAI. *(Закрывается этапом 1.)*
5. `Microsoft.Extensions.AI.OpenAI` рассмотрен и **отклонён** по WebGL — записать с причиной, чтобы
   вопрос не переоткрывали (2.8).
6. `Assets/Packages/Microsoft.Extensions.AI.10.4.1/` и `...Abstractions.10.4.1/` лежат на диске вне
   git — локальный мусор NuGetForUnity, удалить.
7. Доки `Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md` (`:23,120,186,205,294`), `README.md:704` и подсказка
   `CoreAISettingsAssetEditor.uxml:280` описывают несуществующий движок вызова инструментов.
   *(Закрывается этапом 1.)*
8. `SmartToolCallingChatClient.GetStreamingResponseAsync` (`:699-718`) молча не исполняет инструменты
   — сегодня это верно и намеренно, но ничем не сторожится. Нужен тест-страж.
9. Повтор структурного ответа есть только на непотоковом пути (4.6) — решить, дефект это или правило.
10. `ReasoningOptions` / `ReasoningEffort` (MEAI 10.7.0) против наших `LlmReasoningMode` /
    `ThinkingBudgetTokens` (`MeaiLlmClient.cs:2714-2715`) — формально дубликат, выигрыш не доказан (4.5).

</details>
