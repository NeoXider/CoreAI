# TODO — WebGL SSE streaming в `OpenAiChatLlmClient`

**Статус:** известная регрессия, не входит в 0.25.2. Workaround доступен на стороне приложения.

**Затронутый код:** `Runtime/Source/Features/Llm/OpenAiChatLlmClient.cs` → `MeaiOpenAiChatClient.CompleteStreamingAsync`.

---

## 1. Симптом

В собранном WebGL-плеере при включённом стриминге (`CoreAiChatConfig.EnableStreaming = true`,
`CoreAISettings.EnableStreaming = true`, агент через HTTP-бэкенд OpenAI / OpenAI-совместимый):

- LLM-запрос длится секундами (валидно, ответ генерируется удалённо);
- `LoggingLlmClientDecorator` фиксирует **`chunks=1`** при ответе размером в десятки–сотни символов
  (т.е. реальных delta-чанков нет, прилетает один терминальный с полным `content`);
- В чате CoreAI:
  - bubble ответа **не появляется** (визуально как будто AI «промолчал»);
  - typing-индикатор (анимация `. → .. → ... → .`) **крутится бесконечно** до перезагрузки страницы;
- В Editor / Standalone проблема **не воспроизводится** — там стриминг честно отдаёт delta-чанки и UI
  обновляется в реальном времени.

Пример лога WebGL:

```
[CoreAI] [Llm] LLM ▶ (stream) traceId=… role=Teacher backend=RoutingLlmClient→OpenAiHttp
[CoreAI] [Llm] LLM ◀ (stream) wallMs=15848 chunks=1 | tokens н/д | outChars=85
  content (85 симв.): Привет! Готов помочь с Python…
[CoreAI] [MessagePipe] ApplyAiGameCommand … payload=Привет! Готов помочь…
```

---

## 2. Причина

**`UnityWebRequest` на WebGL не поддерживает HTTP chunked / SSE incremental delivery.**

Под капотом WebGL-плеер Unity использует JavaScript `XMLHttpRequest` через emscripten-обёртку.
В отличие от .NET `HttpClient` (Standalone / Editor), `XMLHttpRequest` в Unity-обёртке
делает `responseType="arraybuffer"` и не вызывает `onprogress`-колбэк с инкрементальной
полезной нагрузкой — данные доступны **только** в `onload` после полного завершения запроса.

Из-за этого в `MeaiOpenAiChatClient.CompleteStreamingAsync`:

1. Запрос на `/v1/chat/completions` с `stream: true` отправляется корректно — сервер действительно
   стримит SSE;
2. Браузер получает все `data: {...}` чанки и копит их в response-буфере;
3. `UnityWebRequestAsyncOperation.completed` фаирится только в самом конце;
4. На этот момент `MeaiOpenAiChatClient.ParseSseStream` парсит весь буфер сразу и
   yield'ит один `LlmStreamChunk` с финальным `Text` + `IsDone = true`.

В результате `await foreach` на стороне `CoreAiChatPanel.SendStreamingAsync` получает
**ровно один чанк**, причём с `IsDone = true`:

- Ветка `if (!string.IsNullOrEmpty(chunk.Text))` отрабатывает (`StartStreaming` + `AppendToStreaming`),
- Ветка `if (chunk.IsDone)` тоже отрабатывает (`FinishStreaming` + `HideTypingIndicator`).

Теоретически это должно работать. На практике в текущем 0.25.x в WebGL **первая ветка не
успевает выполниться до того, как pipeline идёт дальше** — гипотеза: либо `Text` не выставлен
(полный ответ улетает в `_chatService` через `ApplyAiGameCommand`-канал минуя yield), либо
race в `_thinkFilter.ProcessChunk` глотает префикс. Точная локализация — пункт 3.

---

## 3. План фикса

### 3.1. Диагностика (обязательная первая итерация)

- [ ] Добавить `Debug.Log` в `CoreAiChatPanel.SendStreamingAsync` непосредственно перед
      `if (!string.IsNullOrEmpty(chunk.Text))` с дампом `chunk.Text?.Length`,
      `chunk.IsDone`, `chunk.Error`, `chunk.UsageOutputTokens` — собрать на WebGL build
      и подтвердить, какой именно чанк приходит.
- [ ] Проверить `MeaiOpenAiChatClient.ParseSseStream` на WebGL: yield'ит ли он delta-чанки
      ИЛИ только финальный `IsDone`-чанк с накопленным `Text`.

### 3.2. Решение A — нативный JS-bridge для SSE (правильный путь)

Реализовать свой emscripten-плагин (`.jslib`) рядом с `Runtime/Plugins/WebGL/`, который:

1. Открывает `fetch(url, { method: 'POST', body, headers })` с `ReadableStream`-телом ответа;
2. Читает `response.body.getReader()` и на каждый чанк вызывает `dynCall_iiii` обратно в C# через
   `[DllImport("__Internal")]`-callback;
3. С# собирает строки в `ConcurrentQueue<string>` и yield'ит их в `IAsyncEnumerable<LlmStreamChunk>`.

Преимущества: настоящий стриминг, как в браузере. Недостатки: новый WebGL-плагин, нужен fallback
на не-WebGL платформах, придётся делать `#if UNITY_WEBGL && !UNITY_EDITOR` ветку в
`MeaiOpenAiChatClient`.

Шаблон уже есть в LLMUnity-пакете (`undream.llmunity` использует похожий fetch-bridge для модельных
загрузок) — можно опереться.

### 3.3. Решение B — graceful degradation (минимальная цена)

Если делать полноценный SSE-bridge не хочется, можно в `MeaiOpenAiChatClient.CompleteStreamingAsync`
определить WebGL и форсировать non-streaming fallback явно:

```csharp
#if UNITY_WEBGL && !UNITY_EDITOR
    // UnityWebRequest на WebGL не отдаёт SSE incremental — делаем синхронный CompleteAsync,
    // обоборачиваем результат в один Text+IsDone чанк. UI получит честный сигнал "не было стриминга".
    var full = await CompleteAsync(request, ct);
    yield return new LlmStreamChunk { Text = full, IsDone = true };
    yield break;
#endif
```

Дополнительно поправить `CoreAiChatPanel.SendStreamingAsync`, чтобы при `chunks=1 && IsDone`
гарантированно вызывался `AddMessage` через `AppendToStreaming` и `HideTypingIndicator`
(добавить assert, что bubble физически попал в `MessageScroll.Children`).

### 3.4. Решение C — UI-level fallback (проще всего)

В `CoreAiChatPanel` ввести `protected virtual bool ShouldUseStreamingForRole(string roleId, bool uiFallback)`
и переопределить в WebGL, возвращая `false`. Тогда `SendToAI` пойдёт по non-streaming-ветке
(`SendNonStreamingAsync`), которая надёжно работает: `await CompleteAsync` → `AddMessage` →
`HideTypingIndicator`.

Сейчас именно это сделано в RedoSchool на уровне приложения через рефлексию
(`ChatPanelController.ForceNonStreamingOnWebGl`) — стоит поднять в библиотеку.

---

## 4. Предложенный порядок работ

1. **Сейчас (0.25.2):** документировать (этот файл) + workaround на стороне приложения.
2. **0.26.0 (минор):** Решение C — `ShouldUseStreamingForRole` virtual hook + дефолтная
   реализация, возвращающая `false` под `#if UNITY_WEBGL && !UNITY_EDITOR`. Сразу убрать
   симптом «бесконечной анимации» для всех проектов на CoreAI.
3. **0.27.0 (минор):** Решение A — настоящий fetch-SSE-bridge через `.jslib`. Включить опциональным
   флагом в `CoreAISettings.WebGlNativeStreaming`. Старая non-streaming ветка остаётся как fallback.

---

## 5. Связанные файлы

- `Runtime/Source/Features/Llm/MeaiOpenAiChatClient.cs` — место реализации SSE-парсера
- `Runtime/Source/Features/Chat/CoreAiChatPanel.cs` — потребитель `IAsyncEnumerable<LlmStreamChunk>`
- `Runtime/Source/Features/Chat/CoreAiChatService.cs` — `SendMessageStreamingAsync`,
  тонкая обёртка над `IAiOrchestrationService.RunStreamingAsync`
- `Docs/STREAMING_ARCHITECTURE.md` — общая архитектура стриминга, нужно дописать секцию «WebGL SSE»
- `Assets/_source/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs` (RedoSchool) —
  пример клиентского workaround через рефлексию `_enableStreaming`
