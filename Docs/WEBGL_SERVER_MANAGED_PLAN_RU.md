# План: WebGL-игра + серверный LLM-прокси с авторизацией и стримингом

**Дата:** 2026-05-03
**Версия CoreAI:** 1.6.0
**Цель:** в другом проекте использовать CoreAI как готовый клиент для WebGL-игры, которая ходит к своему серверу (или к стороннему) за LLM-ответами с авторизацией пользователя, серверными квотами и **рабочим инкрементальным стримингом** в браузере.

> **Портативный пакет `com.nexoider.coreai`:** каноническая документация на английском — оглавление [`README.md`](README.md). Этот файл — только русскоязычный план/статус для WebGL + server-managed.

> **Согласованность с бизнес-планом** (`Docs/LocalBusinessPlans/MONETIZATION_PLAN.md`, `COREAIPRO_PLAN.md`): server-managed-режим — **базовая безопасность, не премиум**. Поэтому **клиентские примитивы и протокол** (всё, что нужно для рабочей WebGL-игры со своим бэкендом) делаем во **Free**. **Готовый backend-kit, дашборды, биллинг, темплейты и wizard** — это **Pro**. Раздел [§4. План работ](#4-план-работ-по-приоритету) разбит по этой границе.

---

## TL;DR — статус готовности (обновлено `1.6.0`)

| Требование | Free Готово | Pro |
|---|:---:|:---:|
| Контракт `ServerManagedApi` (HTTP-прокси, динамический `Authorization`) | ✅ | |
| Локальные клиентские лимиты (`ClientLimited`) | ✅ | |
| Маршрутизация ролей по разным режимам (`LlmRoutingManifest`) | ✅ | |
| WebGL-сборка не падает при HTTP-API (`UnityWebRequestOpenAiTransport`) | ✅ | |
| Editor-валидатор: предупреждение при `ClientOwnedApi`+ключе на WebGL | ✅ (расширен на `ClientLimited`/`ServerManagedApi` + streaming-проверка) | |
| Маппинг ошибок (`401/409 quota_exceeded/429/5xx → LlmErrorCode`) | ✅ | |
| **Реальный инкрементальный SSE-стриминг в WebGL-плеере** | ✅ `CoreAiSseFetch.jslib` + `FetchSseOpenAiTransport` за флагом `WebGlNativeStreaming` | |
| Прокидывание `tenantId/userId/sessionId/requestId` на бэкенд | ✅ `LlmRequestContext` (AsyncLocal) + `LlmAuthContextRegistry` + `IRequestHeaderProvider` | |
| Идемпотентность POST-запросов (re-try без двойного списания) | ✅ `LlmCompletionRequest.IdempotencyKey` (auto-assign once per request, reused on retry) | |
| Автообновление JWT при `401`, осмысленный logout | ✅ `IServerManagedAuthRefresher` + `RefreshOnUnauthorizedDecorator` + `LlmAuthExpired` event | |
| Same-origin URL helper для деплоя «игра + прокси на одном хосте» | ✅ `ServerManagedCoreSettingsAdapter.ApiBaseUrl` резолвит относительные `/api/...` через `Application.absoluteURL` | |
| `SameOriginCredentials` toggle | ✅ `CoreAISettingsAsset.SameOriginCredentials` → fetch `credentials: 'same-origin'`/`'include'` | |
| Спецификация серверного протокола | ✅ [`SERVER_MANAGED_PROTOCOL.md`](SERVER_MANAGED_PROTOCOL.md) | |
| Reference-бэкенд (Docker-compose, JWKS, Redis idempotency, usage DB) | | 💎 Pro |
| Wired-in серверная entitlement-проверка (`BackendEntitlementPolicy` adapter) | контракт `ILlmEntitlementPolicy` есть | 💎 Pro адаптер |
| Серверная атрибуция использования (`BackendUsageSink` → backend) | контракт `ILlmUsageSink` есть | 💎 Pro адаптер |
| Web dashboard / Editor diagnostics window | | 💎 Pro |
| WebGL production checklist со скринами/видео | | 💎 Pro |
| Billing/quota recipes (Stripe, JWT subjects) | | 💎 Pro |

**Короткий вывод.** На версии `1.6.0` все клиентские блокеры **закрыты** в Free: WebGL native fetch SSE работает за флагом, `Idempotency-Key`/`X-Request-Id`/`X-Tenant-Id`/`X-User-Id`/`X-Session-Id`/`X-Coreai-Role` уходят на бэкенд, JWT обновляется через `IServerManagedAuthRefresher`, относительные URL резолвятся для same-origin деплоя, валидатор расширен. Сторонний WebGL-проект может собирать продакшен-игру со своим бэкендом по [`SERVER_MANAGED_PROTOCOL.md`](SERVER_MANAGED_PROTOCOL.md). Что осталось во Free — мелкая гигиена (см. §9). Готовый бэкенд-kit, дашборды, темплейты — это уже Pro.

---

## 1. Что уже работает (можно опираться)

### 1.1. Портативные контракты (`CoreAI.Core`)

- `LlmExecutionMode` — `Auto / LocalModel / ClientOwnedApi / ClientLimited / ServerManagedApi / Offline`. Файл: `Assets/CoreAI/Runtime/Core/Features/Orchestration/LlmExecutionMode.cs`.
- `LlmRouteProfile / LlmRouteRule / LlmRouteTable / ILlmRouteResolver` — ролевой роутинг.
- `ILlmAuthContextProvider` — поля `TenantId / UserId / SessionId / GetAuthorizationHeader()`. Файл: `Assets/CoreAI/Runtime/Core/Features/LlmRouting/ILlmAuthContextProvider.cs`. ⚠️ **Существует, но не пробрасывается в исходящие HTTP-заголовки.**
- `ILlmEntitlementPolicy / LlmEntitlementDecision` — портативный контракт проверки квот/подписок/allowlist моделей.
- `ILlmUsageSink / LlmUsageRecord` — портативный учёт использования.
- `LlmProviderError → LlmErrorCode` — стабильные коды (`quota_exceeded`, `subscription_required`, `model_not_allowed`, `rate_limited`, `context_length_exceeded`, …).
- `ClientLimitedLlmClientDecorator` — счётчик запросов на сессию + лимит символов промпта; роллбэк счётчика при отказе (BUG-3 fix).
- `IOpenAiHttpSettings.AuthorizationHeader` — можно подменять весь заголовок (не только `Bearer <ApiKey>`).

### 1.2. Unity-инфраструктура (`CoreAiUnity`)

- `ServerManagedLlmClient` (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs`) — оборачивает `MeaiOpenAiChatClient` адаптером, который при каждом запросе тянет токен из `ServerManagedAuthorization.GetAuthorizationHeader()`.
- `ServerManagedAuthorization.SetProvider(Func<string>)` — динамический JWT/Bearer (вызывается на каждый запрос; refresh — на стороне приложения).
- `LlmPipelineInstaller.BuildHttpClient`:
  - `ServerManagedApi` → `ServerManagedLlmClient`
  - `ClientLimited` → `ClientLimitedLlmClientDecorator(OpenAiChatLlmClient, …)`
  - `ClientOwnedApi` → `OpenAiChatLlmClient`
- `MeaiLlmClient.CreateHttp` авто-выбирает транспорт: `HttpClientOpenAiTransport` (Editor/standalone), `UnityWebRequestOpenAiTransport` (`UNITY_WEBGL && !UNITY_EDITOR`).
- WebGL-таймауты — через `UniTask.CancelAfterSlim` (PlayerLoop), а не `System.Threading.Timer`.
- `CoreAIProductionSettingsValidator` — pre-build предупреждение, если WebGL-цель с `ClientOwnedApi` + непустой `ApiKey` (ключ светится в публичной сборке).
- Стриминговый workaround: `CoreAiChatService.IsStreamingEnabled = false` и `CoreAiChatPanel.ShouldUseStreamingForRole = false` при `UNITY_WEBGL && !UNITY_EDITOR` (чтобы не висел typing-indicator).
- `LlmProviderError` маппит HTTP-коды бэкенда в `LlmErrorCode` — UI может показывать «требуется вход / квота / rate limit / модель недоступна» без парсинга текста.

### 1.3. Безопасность сборки

- `OpenAiHttpLlmSettings.AuthorizationHeader = ""` (фабричное значение) → Bearer = ApiKey, что для WebGL опасно. Переопределяется через `ServerManagedAuthorization.SetProvider`, **но только для `ServerManagedApi`-клиента**.

---

## 2. Главные проблемы и что улучшить

### 2.1. 🔴 Блокер: реального SSE-стриминга в WebGL нет

**Файлы:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiTransport.cs`, `Assets/CoreAiUnity/Docs/STREAMING_WEBGL_TODO.md`.

`UnityWebRequest`/XHR на WebGL читает тело только в `onload` — `data:`-чанки приходят пакетом, а не инкрементально. Текущее «решение C» — отключить стриминг в WebGL целиком.

**Что сделать:**

1. Добавить нативный JS-мост — `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib`:
   - `fetch(url, { method:'POST', body, headers, credentials:'include' })` (или `'same-origin'`).
   - `response.body.getReader()` → читать `Uint8Array`-чанки.
   - Раздробленный декод UTF-8 (склеивать обрывки на границе байтов).
   - Парсер SSE-фреймов (`data: …\n\n`) на JS-стороне или прокидывание сырых строк в C# и парс там.
   - C#-callback через `[DllImport("__Internal")]` + reverse callback (`Module['dynCall_vi']` или `Runtime.dynCall` в Unity 6) с handle/callId (без захвата `this` в JS).
2. Реализовать `FetchSseOpenAiTransport : IOpenAiHttpTransport` с `SupportsSseStreaming = true`. Чанки складывать в `ConcurrentQueue<string>` и отдавать через `IAsyncEnumerable<LlmStreamChunk>` (поллинг на главном потоке через `UniTask.NextFrame`).
3. В `MeaiLlmClient.CreateHttp` под `UNITY_WEBGL && !UNITY_EDITOR` выбирать `FetchSseOpenAiTransport` за флагом `CoreAISettings.WebGlNativeStreaming` (по умолчанию `false`, чтобы не ломать существующие сборки). Fallback — текущий `UnityWebRequestOpenAiTransport`.
4. Снять блок `IsStreamingEnabled = false` в `CoreAiChatService` / `CoreAiChatPanel.ShouldUseStreamingForRole`, когда флаг включён.
5. Тесты:
   - PlayMode WebGL-симуляция в Editor: моки `IOpenAiHttpTransport` уже есть.
   - Реальный smoke-тест в WebGL-плеере с публичным OpenAI-совместимым endpoint, в т.ч. длинная генерация + cancel в середине.
6. Документация: подсекция в `STREAMING_ARCHITECTURE.md` — «WebGL native fetch SSE», CORS-требования (`Access-Control-Allow-Origin`, `Access-Control-Expose-Headers: Content-Type`, **отсутствие** `Access-Control-Max-Age` для SSE).

**Альтернатива на крайний случай:** WebSocket-туннель `wss://your-backend/llm` с собственным фреймингом «delta/done». Тогда вся стриминговая семантика — на бэкенде, у клиента — только JSON-пакеты. Менее стандартно, но 100% работает в браузере.

### 2.2. 🔴 Блокер: на бэкенд не уходит идентификация запроса

**Файл:** `Assets/CoreAI/Runtime/Core/Features/Llm/IOpenAiHttpSettings.cs`, `MeaiOpenAiChatClient`.

`IOpenAiHttpSettings` отдаёт только `Authorization`. На прокси нельзя надёжно атрибутировать запрос конкретному пользователю/сессии без дополнительных заголовков. Единственный канал сейчас — расшифровать JWT серверу, что тяжелее и негибко (тенантность, A/B-роуты, мульти-аккаунт).

**Что сделать:**

1. Расширить `IOpenAiHttpTransport` / `IOpenAiHttpSettings` поддержкой произвольных заголовков:
   - Добавить `IReadOnlyList<KeyValuePair<string,string>> ExtraHeaders { get; }` либо
   - `IHttpRequestHeaderProvider` (чище: разделить «как идентифицировать» от «как авторизоваться»).
2. Прокинуть `ILlmAuthContextProvider` в `ServerManagedLlmClient` (сейчас он есть в Core, но никуда не подключён) — формировать заголовки `X-Tenant-Id`, `X-User-Id`, `X-Session-Id` из его полей.
3. Добавить `X-Request-Id` (UUID, новый на каждый retry attempt **не**, на каждый логический запрос **да**) — для идемпотентности (см. 2.3).
4. На уровне `LlmCompletionRequest.AgentRoleId` пробрасывать `X-Coreai-Role` — чтобы на бэкенде включать/отключать модели по ролям централизованно.

### 2.3. 🔴 Блокер: ретраи без идемпотентности при платном бэкенде

**Файл:** `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LoggingLlmClientDecorator.cs` (или эквивалент), настройка `MaxLlmRequestRetries`.

`LoggingLlmClientDecorator` сам делает экспоненциальный retry на 429/5xx. POST `/chat/completions` не идемпотентен — при флапе сети возможен **двойной биллинг**.

**Что сделать:**

1. Генерировать `requestId` **один раз** на логический запрос и переиспользовать на всех ретраях, отправлять в заголовке `Idempotency-Key` (стандарт Stripe/OpenAI).
2. На прокси-бэкенде хранить таблицу `idempotency_key → response` с TTL 24ч и возвращать кэш при повторе.
3. По умолчанию `MaxLlmRequestRetries = 0` для `ServerManagedApi`, пока приёмочный бэкенд не подтверждает идемпотентность; явно включать через настройку.
4. Не ретраить 4xx (кроме 429), как уже сейчас — закрепить в тестах (`LoggingLlmClientDecoratorEditModeTests`).

### 2.4. 🟠 Важно: 401 не вызывает refresh JWT

`ServerManagedAuthorization.GetAuthorizationHeader()` дёргается на каждый запрос — но если бэкенд отдал 401, текущий код не приглашает приложение обновить токен и не повторяет.

**Что сделать:**

1. Расширить `IServerManagedAuthProvider` методом `Task<string> RefreshAsync(CancellationToken)` (опционально, через DIM или второй интерфейс `IServerManagedAuthRefresher`).
2. При `LlmErrorCode.Unauthorized` (надо завести этот код, если ещё нет — сейчас, похоже, нет; есть `subscription_required` и `quota_exceeded`) — `ServerManagedLlmClient` (или специальный декоратор `RefreshOnUnauthorizedDecorator`) вызывает `RefreshAsync` ровно **один раз** и повторяет запрос.
3. Если refresh тоже 401 — поднимаем событие `MessagePipe<LlmAuthExpired>`, чтобы UI показал экран входа.
4. Для logout — вызвать `ServerManagedAuthorization.ClearProvider()` + опубликовать тот же эвент.

### 2.5. 🟠 Важно: серверные entitlements/usage не подключены

Контракты `ILlmEntitlementPolicy` и `ILlmUsageSink` декларируются в `LLM_ROUTING.md`, но в `LlmPipelineInstaller` декораторов с ними **не регистрируется**.

**Что сделать:**

1. Создать декораторы в `CoreAI.Core`:
   - `EntitlementCheckingLlmClientDecorator` — перед запросом дёргает `ILlmEntitlementPolicy.CheckAsync(role, model, tokens)` и при отказе возвращает соответствующий `LlmErrorCode`.
   - `UsageReportingLlmClientDecorator` — после ответа кладёт `LlmUsageRecord` в `ILlmUsageSink` (in-process очередь + батч-флаш на бэкенд каждые N сек).
2. Unity-адаптер `BackendEntitlementPolicy` и `BackendUsageSink` (HTTP `GET /entitlements`, `POST /usage/batch`) с теми же auth-заголовками, что и LLM-запросы.
3. В `LlmPipelineInstaller` регистрировать цепочку только для `ServerManagedApi`:
   `Logging → UsageReporting → EntitlementChecking → Routing → ServerManagedLlmClient`.
4. Тесты: `EditMode` проверяет, что при `LlmEntitlementDecision.Deny(quota_exceeded)` запрос **не** уходит в HTTP.

> Альтернатива: оставить entitlements полностью на стороне бэкенда (тогда клиенту хватит маппинга `409 quota_exceeded → LlmErrorCode.QuotaExceeded`, что уже есть). Это проще и валиднее для большинства проектов — но тогда удалить из docs обещание клиентского `ILlmEntitlementPolicy`-pipeline, или явно пометить «опционально».

### 2.6. 🟠 Важно: same-origin для деплоя «игра + прокси на одном сервере»

Пользователь явно хочет вариант «WebGL раздаётся с того же сервера, что обслуживает LLM-прокси». Сейчас `ApiBaseUrl` — абсолютная строка, и ничто не помогает поставить относительный путь.

**Что сделать:**

1. В `MeaiOpenAiChatClient` (или адаптере настроек) принимать `ApiBaseUrl` вида `"/api/llm/v1"` и резолвить через `Application.absoluteURL` на WebGL — `new Uri(new Uri(Application.absoluteURL), apiBaseUrl)`.
2. Документировать в `HTTP_TRANSPORT_SPEC.md` рецепт: nginx/Caddy шлёт `/api/llm/*` в backend, статику игры — `/`.
3. Бонус: убрать необходимость CORS целиком, а cookie-сессии работают сразу (если используется session-cookie вместо Bearer-JWT — добавить `credentials: 'same-origin'` в `fetch`).

### 2.7. 🟠 Важно: нет приёмочного бэкенда-примера

Заявленный сценарий («ServerManagedApi») предполагает, что у потребителя есть OpenAI-совместимый прокси. Это нетривиально — модель, ключи, биллинг, idempotency, отчёты по usage. Без референсной реализации каждый проект будет переизобретать.

**Что сделать (как отдельный sibling-проект, не в Unity):**

1. Минимальный Node.js / .NET / Go-прокси:
   - `POST /v1/chat/completions` — валидирует JWT (через JWKS), приклеивает провайдерский ключ из переменной окружения, проксирует поток (для SSE — `Transfer-Encoding: chunked`).
   - `GET /v1/entitlements?role=…` — отдаёт квоты текущего пользователя.
   - `POST /v1/usage` — приём батча `LlmUsageRecord`.
   - Idempotency-store (Redis / SQLite) на 24ч.
   - Rate limiter на пользователя (Redis token bucket).
   - Allowlist моделей по тарифу.
2. CORS: `Access-Control-Allow-Origin: <game origin>`, `Access-Control-Allow-Headers: Authorization, Content-Type, X-Request-Id, Idempotency-Key, X-Tenant-Id, X-Session-Id`.
3. Docker-compose с Caddy и одним хостом — чтобы покрыть и same-origin сценарий.
4. Документация: `Docs/SERVER_MANAGED_BACKEND_REFERENCE.md`.

### 2.8. 🟡 Желательно: явная поддержка cookie-аутентификации

Bearer-JWT в `Authorization` — стандарт, но WebGL-игра, раздаваемая своим же бэкендом, может опираться на HTTP-only session-cookie (надёжнее, чем JWT в `localStorage`). Для этого fetch должен идти с `credentials: 'include' | 'same-origin'`. Сейчас этой опции нет.

**Что сделать:**

- В `IOpenAiHttpTransport` (а в WebGL-jslib — в самом `fetch`) добавить флаг `UseCookies / SameOriginCredentials`.
- В `CoreAISettingsAsset` — соответствующий тумблер.

### 2.9. 🟡 Желательно: явная защита от утечки `ApiKey` в WebGL

Pre-build-валидатор предупреждает только для `ClientOwnedApi`. Для `ClientLimited` он тоже потенциально утечёт ключ (если его задать) — нужно либо запретить `ClientLimited` для WebGL, либо предупреждать одинаково. Декларация в `COREAI_SETTINGS.md` уже намекает на это, но enforce-шага нет.

**Что сделать:**

- Расширить `CoreAIProductionSettingsValidator.GetWebGlClientKeyWarning` на оба режима.
- Если `ServerManagedApi` + `ApiKey` непустой → варнинг «ApiKey не используется в `ServerManagedApi`, удалить».

### 2.10. 🟡 Желательно: атрибуция request-id в логе

`LoggingLlmClientDecorator` пишет `traceId` локально. Если на бэкенд уходит `X-Request-Id`, сделайте этот же id равным `traceId` или логируйте оба — тогда дебаг сквозной (Unity Console ↔ серверные логи).

### 2.11. 🟢 Мелочи

- `UnityWebRequestOpenAiTransport.PostNonStreamingAsync` крутит `await Task.Yield()` каждый кадр — корректно, но шумно для WebGL мейнтреда. Подумать про `await UniTask.NextFrame(ct)`.
- Адаптеры `ServerManagedSettingsAdapter` и `ServerManagedCoreSettingsAdapter` дублируют друг друга — стоит вынести один общий.
- В `LLM_ROUTING.md` упомянуты `ILlmEntitlementPolicy` / `ILlmUsageSink` как часть готового пайплайна — на деле они нигде не вызываются. Привести docs в соответствие.
- Для tool calling в `ServerManagedApi`-сценарии в docs нужно явно прописать: **AIFunction исполняется на клиенте, не на сервере** (модель возвращает `tool_calls`, MEAI вызывает локальный делегат). Серверу видны только аргументы вызова и результат тула в виде последующего сообщения.

---

## 3. Итоговый сценарий «как должно быть» (целевая архитектура)

```
                    ┌─────────────────────────────────────────────┐
                    │  Browser (WebGL build)                      │
                    │                                             │
                    │  CoreAiChatPanel ─► CoreAiChatService       │
                    │       │                                     │
                    │       ▼                                     │
                    │  AiOrchestrator (queue, authority, prompt)  │
                    │       │                                     │
                    │       ▼                                     │
                    │  Logging → Usage → Entitlement              │
                    │       │                                     │
                    │       ▼                                     │
                    │  ServerManagedLlmClient                     │
                    │   ├─ ILlmAuthContextProvider                │
                    │   │   (tenant/user/session)                 │
                    │   └─ ServerManagedAuthorization             │
                    │       (Bearer JWT, refresh on 401)          │
                    │       │                                     │
                    │       ▼ (jslib fetch + ReadableStream)      │
                    │  FetchSseOpenAiTransport                    │
                    └────────────────┬────────────────────────────┘
                                     │  https://app/api/llm/v1/chat/completions
                                     │  Authorization: Bearer <jwt>
                                     │  X-Tenant-Id, X-User-Id, X-Session-Id
                                     │  Idempotency-Key, X-Request-Id
                                     │  body: { model, messages, stream:true, … }
                                     ▼
                    ┌─────────────────────────────────────────────┐
                    │  App backend (same-origin / CORS-allowed)   │
                    │                                             │
                    │  • JWT validation (JWKS)                    │
                    │  • Idempotency store (Redis, 24h)           │
                    │  • Rate limit / quota / model allowlist     │
                    │  • Provider API key (env var)               │
                    │  • Usage logger (DB)                        │
                    │  • SSE pass-through (chunked)               │
                    └────────────────┬────────────────────────────┘
                                     │
                                     ▼
                            OpenAI / Anthropic / vLLM
```

---

## 4. План работ по приоритету

> **Граница Free vs Pro** (по `MONETIZATION_PLAN.md`): всё, что нужно, чтобы **сторонний WebGL-проект** с собственным бэкендом начал работать со стримингом и авторизацией — во **Free**. Готовый production backend, дашборды, биллинг, шаблоны и wizard — в **Pro**. Server-managed — это базовая безопасность, не платная фича.

### 🆓 FREE — то, что делаем в `com.nexoider.coreai` / `com.nexoider.coreaiunity`

Цель Free-части: бесплатный потребитель должен суметь собрать WebGL-игру со своим бэкендом, инкрементальным стримингом, JWT-авторизацией и без двойного списания — **без покупки Pro**.

#### Этап 0 — диагностика (1–2 ч)

- [ ] Репро-WebGL-сборка + echo-прокси (Node) для воспроизведения текущих симптомов (`chunks=1`, висящий typing).
- [ ] Записать issue со списком отсутствующих заголовков/механизмов из 2.2–2.4.

#### Этап 1 — production-ready non-streaming WebGL+ServerManaged (2–3 дня)

С этим этапом сторонняя WebGL-игра уже может безопасно ходить в свой бэкенд за LLM, **без стриминга**.

- [ ] **2.2:** добавить `IRequestHeaderProvider` (или `ExtraHeaders` в `IOpenAiHttpSettings`/`IOpenAiHttpTransport`); пробросить `tenantId/userId/sessionId/role/request-id` в `ServerManagedLlmClient` через `ILlmAuthContextProvider` (контракт уже в Core).
- [ ] **2.3:** генерация `Idempotency-Key` (UUID, переиспользуется на ретраях); по умолчанию `MaxLlmRequestRetries = 0` для `ServerManagedApi`.
- [ ] **2.4:** `IServerManagedAuthRefresher` + декоратор `RefreshOnUnauthorizedDecorator`; событие `LlmAuthExpired` в MessagePipe (UI-слой реагирует сам).
- [ ] **2.6:** поддержка относительных `ApiBaseUrl` (`/api/llm/v1`) через `Application.absoluteURL` для same-origin деплоя.
- [ ] **2.8:** опция `SameOriginCredentials` в `CoreAISettingsAsset` (нужна и для XHR, и для будущего fetch-bridge).
- [ ] **2.9:** расширить `CoreAIProductionSettingsValidator` на `ClientLimited` и `ServerManagedApi` (warn про лишний `ApiKey`).
- [ ] EditMode-тесты с моками `IOpenAiHttpTransport` для каждого пункта.

#### Этап 2 — WebGL native streaming (3–5 дней)

- [ ] **2.1a:** `Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib` (fetch + ReadableStream + SSE-парсер; UTF-8 reassembly; `AbortController` ↔ `CancellationToken`).
- [ ] **2.1b:** `FetchSseOpenAiTransport : IOpenAiHttpTransport` с `SupportsSseStreaming = true` (handle/callId; чанки в `ConcurrentQueue<string>`; поллинг через `UniTask.NextFrame`).
- [ ] Флаг `CoreAISettings.WebGlNativeStreaming` (по умолчанию `false`); fallback — текущий `UnityWebRequestOpenAiTransport`.
- [ ] Снять блок `IsStreamingEnabled = false` в `CoreAiChatService` / `CoreAiChatPanel.ShouldUseStreamingForRole`, когда флаг включён.
- [ ] Acceptance: длинный ответ (≥10 SSE-чанков), отмена в середине, корректный финальный chunk с `IsDone`.
- [ ] Документация: подсекция в `STREAMING_ARCHITECTURE.md` + чеклист CORS-заголовков (`Access-Control-Allow-*`, `X-Accel-Buffering: no`, отключение gzip для `text/event-stream`).

#### Этап 3 — лёгкая серверная документация (1–2 дня)

> **Не путать с Pro backend kit ниже.** Здесь — **только спецификация контракта** и минимальные fixtures, чтобы Free-пользователь мог написать свой бэкенд за выходные. Никаких Docker-compose, миграций, dashboard-кода — это всё в Pro.

- [ ] `Assets/CoreAI/Docs/SERVER_MANAGED_PROTOCOL.md`: формат запроса/ответа, обязательные/опциональные заголовки, маппинг ошибок (`401/409/429/5xx → LlmErrorCode`), формат SSE-чанков, поведение для idempotency-replay.
- [ ] Smoke-fixture: пара `.http`/`curl` примеров запросов и ожидаемых ответов.
- [ ] Раздел «как написать свой прокси за выходной» — **без** готового кода, только список того, что нужно реализовать (≈10 пунктов).
- [ ] Синхронизировать `Assets/CoreAI/Docs/LLM_ROUTING.md` с реальным состоянием декораторов (`ILlmEntitlementPolicy` / `ILlmUsageSink` помечены как «контракты для собственной реализации», не как готовый pipeline).

#### Этап 4 — наведение порядка во Free (по мере)

- [ ] **2.10:** объединить `traceId`/`requestId` в логах для сквозной отладки клиент↔бэкенд.
- [ ] **2.11:** убрать дубликаты `ServerManagedSettingsAdapter`/`ServerManagedCoreSettingsAdapter`.
- [ ] CI smoke: WebGL-сборка + headless Chromium + Node echo-прокси (как Editor PlayMode).

### 💎 PRO — то, что попадает в `com.nexoider.coreaipro` (платный add-on)

Цель Pro-части: продаём **production speed** — готовый бэкенд, дашборды, темплейты, wizard. **Не** прячем за Pro ничего из того, что уже работает в Free.

#### Pro-1 — Backend Starter Kit (отдельный repo `CoreAiPro/Backend`)

- [ ] **2.7a:** референс-прокси (Node.js + Fastify **или** .NET 8 minimal API — выбрать одно для v1, второе — позже): JWKS-валидация JWT, idempotency-store на Redis, rate-limiter (token bucket), allowlist моделей по тарифу, usage-логгер в Postgres/SQLite, SSE pass-through (`Transfer-Encoding: chunked`, отключение gzip).
- [ ] **2.7b:** Docker-compose (бэкенд + Caddy + Redis + Postgres) — same-origin out-of-the-box.
- [ ] Миграции, seed-юзеры, пример `.env`.
- [ ] Endpoints: `POST /v1/chat/completions`, `GET /v1/entitlements`, `POST /v1/usage/batch`.
- [ ] Документация: `CoreAiPro/Backend/README.md` — deployment-чеклист, security checklist, чеклист WebGL-доступа.

#### Pro-2 — Unity-адаптеры серверной экономики

- [ ] **2.5a:** `BackendEntitlementPolicy : ILlmEntitlementPolicy` (Unity-адаптер, ходит в `GET /entitlements` с теми же заголовками, что и LLM-запросы).
- [ ] **2.5b:** `BackendUsageSink : ILlmUsageSink` (батч-флаш в `POST /usage/batch` каждые N сек).
- [ ] Pro-инсталлер регистрирует цепочку декораторов: `Logging → UsageReporting → EntitlementChecking → Routing → ServerManagedLlmClient`.
- [ ] EditMode-тест: при `LlmEntitlementDecision.Deny(quota_exceeded)` HTTP-запрос **не** уходит.

#### Pro-3 — Dashboard и diagnostics

- [ ] Web-дашборд (Next.js / Razor): запросы по ролям, токены по пользователям/проектам, оценка дневных/месячных расходов, ошибки и таймауты, разбивка по моделям.
- [ ] Unity Editor Window «CoreAI Diagnostics» (последние запросы, latency, journal tool calls).
- [ ] Экспорт логов в CSV/JSON.

#### Pro-4 — Production checklist + WebGL deployment recipes

- [ ] `CoreAiPro/Docs/WEBGL_PRODUCTION_CHECKLIST.md` — со скриншотами и видео.
- [ ] `CoreAiPro/Docs/BILLING_INTEGRATION.md` — Stripe/LemonSqueezy + JWT subjects.
- [ ] `CoreAiPro/Docs/QUOTA_RECIPES.md` — частые модели подписок (free tier, daily token cap, per-model caps).
- [ ] Видео-деплой: «WebGL + backend kit за 30 минут».

#### Pro-5 — Templates / Wizard / RAG starter (out of scope этого плана)

См. `Docs/LocalBusinessPlans/COREAIPRO_PLAN.md`. Они не относятся к WebGL+server-managed-сценарию напрямую, но используют тот же Free-протокол.

### Сводная таблица: Free vs Pro для текущего сценария

| # | Задача | Free | Pro | Зачем именно туда |
|--:|---|:---:|:---:|---|
| 2.1 | WebGL native fetch SSE (`.jslib` + transport) | ✅ | | Базовая UX, не премиум — без неё стриминг сломан |
| 2.2 | Прокидывание tenant/user/session/request-id | ✅ | | Нужно даже для базовой атрибуции на своём бэкенде |
| 2.3 | Idempotency-Key | ✅ | | Безопасность от двойного списания — обязательна |
| 2.4 | Refresh-on-401 | ✅ | | UX-минимум для авторизованной игры |
| 2.5 | `ILlmEntitlementPolicy` / `ILlmUsageSink` контракты | ✅ | | Контракт во Free |
| 2.5 | `BackendEntitlementPolicy` / `BackendUsageSink` Unity-адаптеры | | ✅ | Это уже **готовая** реализация, продаётся |
| 2.6 | Relative `ApiBaseUrl` через `Application.absoluteURL` | ✅ | | Базовая поддержка same-origin |
| 2.7 | **Спецификация** server-managed протокола | ✅ | | Чтобы Free-юзер написал свой бэкенд |
| 2.7 | **Готовый** backend starter kit (Docker, Redis, Postgres, Caddy) | | ✅ | Главная Pro-ценность |
| 2.8 | `SameOriginCredentials` toggle | ✅ | | Базовая опция HTTP-клиента |
| 2.9 | Расширенный pre-build валидатор (ClientLimited/ServerManaged) | ✅ | | Безопасность сборки |
| 2.10 | traceId↔requestId в логах | ✅ | | Базовая отладка |
| 2.11 | Дедупликация адаптеров, синхрон docs | ✅ | | Гигиена кода |
| — | Web dashboard, Editor diagnostics window | | ✅ | Sells time saved |
| — | WebGL production checklist со скринами/видео | | ✅ | Premium docs |
| — | Billing/quota recipes (Stripe, JWT subjects, per-model caps) | | ✅ | Premium docs |

---

## 5. Риски и нюансы

1. **Tool calling в ServerManagedApi.** Тулы исполняются на клиенте. Если в туле есть секрет — он попадает в WebGL bundle. Для секретных операций — перенести их в backend и предоставить тулу-обёртку, которая ходит к собственному API с тем же JWT.
2. **CORS preflight.** Каждый custom-заголовок (`X-Tenant-Id`, …) добавляет OPTIONS-запрос. Для same-origin это не проблема; для cross-origin — выставить `Access-Control-Max-Age: 86400`.
3. **SSE через прокси/CDN.** CloudFront, ряд корпоративных прокси буферизуют ответ. Бэкенду нужно отдавать `X-Accel-Buffering: no` (nginx), `Cache-Control: no-cache`, и **обязательно** `Content-Type: text/event-stream`.
4. **Mixed content.** Если игра по `https://`, бэкенд тоже должен быть `https://`. Self-signed dev-серверы → доверить через `chrome://flags`.
5. **Сжатие.** SSE плохо дружит с `Content-Encoding: gzip` (буферизация фреймов). На бэкенде отключить gzip только для `text/event-stream`.
6. **Idempotency и стриминг.** Спорный момент: повтор стримингового запроса по тому же ключу обычно не повторяет SSE, а отдаёт уже сохранённый итог одним JSON. Поведение должно быть прописано в backend-спеке.
7. **WebGL и большие промпты.** UTF-16 в браузерной памяти + копии в Mono heap → следить за пиками. `MaxClientLimitedPromptChars` помогает.
8. **`CancellationToken` через jslib.** В `fetch` отмена — через `AbortController.abort()`. Нужен реверс-маппинг C# CTS → JS AbortController в `FetchSseOpenAiTransport`.
9. **Версионирование контракта.** Заложить заголовок `X-Coreai-Client: 1.6.0` (или актуальную линию из `package.json`) — чтобы бэкенд мог отказывать устаревшим сборкам (или маркировать).

---

## 6. Метрики приёмки

Сборку можно отгружать в продакшен, когда выполнено:

- [ ] WebGL-сборка с `LlmExecutionMode.ServerManagedApi` и относительным `ApiBaseUrl` собирается, грузится и отвечает.
- [ ] При истечении JWT клиент сам обновляет токен и продолжает диалог без перезагрузки.
- [ ] Двойная отправка одного и того же запроса (имитировать через DevTools «replay») списывает одну квоту, а не две.
- [ ] Стриминг визуально печатается по мере поступления чанков (не «один большой пакет в конце»).
- [ ] При `429`/`409 quota_exceeded` UI показывает понятный текст, а не «Error: …».
- [ ] Логи бэкенда содержат `tenantId` + `userId` + `requestId` для каждого запроса.
- [ ] Pre-build валидатор не пропускает WebGL-сборку с непустым `ApiKey`.
- [ ] EditMode + smoke-тесты в WebGL зелёные.

---

## 7. Краткий список затрагиваемых файлов

### 7.1. Free (`com.nexoider.coreai` / `com.nexoider.coreaiunity`)

```
Assets/CoreAI/Runtime/Core/Features/Llm/IOpenAiHttpSettings.cs        # +ExtraHeaders
Assets/CoreAI/Runtime/Core/Features/Llm/IOpenAiHttpTransport.cs       # +headers passthrough
Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs       # idempotency-key, X-Request-Id
Assets/CoreAI/Runtime/Core/Features/LlmRouting/ILlmAuthContextProvider.cs  # подключить
Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedLlmClient.cs   # склеить с auth context
Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/ServerManagedAuthorization.cs # refresh
Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiTransport.cs # SameOriginCredentials
Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib         # ★ новый
Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FetchSseOpenAiTransport.cs  # ★ новый
Assets/CoreAiUnity/Runtime/Source/Composition/LlmPipelineInstaller.cs # цепочка декораторов (auth/idempotency)
Assets/CoreAiUnity/Editor/CoreAIProductionSettingsValidator.cs        # шире охват
Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/CoreAISettingsAsset.cs # WebGlNativeStreaming, SameOriginCredentials
Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md                     # подсекция WebGL native
Assets/CoreAiUnity/Docs/STREAMING_WEBGL_TODO.md                       # пометить выполненным
Assets/CoreAI/Docs/LLM_ROUTING.md                                     # синхронизация с реальностью
Assets/CoreAI/Docs/SERVER_MANAGED_PROTOCOL.md                         # ★ новый: спецификация контракта (без готового кода)
```

### 7.2. Pro (`com.nexoider.coreaipro` — приватный repo до релиза)

```
CoreAiPro/Backend/                                                    # ★ отдельный repo
  src/                                                                # Node+Fastify или .NET 8 minimal API
  docker-compose.yml                                                  # backend + Caddy + Redis + Postgres
  migrations/
  README.md                                                           # deployment + security checklist
CoreAiPro/Assets/CoreAiPro/Runtime/BackendEntitlementPolicy.cs        # ★ Unity-адаптер ILlmEntitlementPolicy
CoreAiPro/Assets/CoreAiPro/Runtime/BackendUsageSink.cs                # ★ Unity-адаптер ILlmUsageSink
CoreAiPro/Assets/CoreAiPro/Runtime/CoreAiProPipelineInstaller.cs      # ★ цепочка с entitlement+usage
CoreAiPro/Assets/CoreAiPro/Editor/DiagnosticsWindow.cs                # ★ Unity Editor window
CoreAiPro/Dashboard/                                                  # ★ web-дашборд (Next.js)
CoreAiPro/Docs/WEBGL_PRODUCTION_CHECKLIST.md                          # ★ premium doc со скринами
CoreAiPro/Docs/BILLING_INTEGRATION.md                                 # ★ premium doc
CoreAiPro/Docs/QUOTA_RECIPES.md                                       # ★ premium doc
```

---

## 8. Ответ на вопрос «всё ли сделано»

**Нет.** Архитектура и контракты на месте, но 5 клиентских доработок (native fetch SSE, проброс tenant/user/session, idempotency, JWT-refresh, валидация ApiKey + relative URL) — обязательны, и они **должны жить во Free**, потому что это базовая безопасность WebGL-игры с авторизацией, а не премиум-фича.

**Что для Pro:** референс backend (Docker-compose + JWKS + idempotency-store + usage DB + rate-limiter), Unity-адаптеры серверной экономики (`BackendEntitlementPolicy`, `BackendUsageSink`), web-дашборд, premium-документация и production-чеклисты со скриншотами/видео.

**Сроки:**
- Free Этап 1 (без стриминга, авторизованная WebGL-сборка) — **~3 рабочих дня**.
- Free Этап 2 (рабочий инкрементальный стриминг через fetch-bridge) — **~3–5 дней** дополнительно.
- Free Этап 3 (спецификация протокола + smoke-фикстуры) — **~1–2 дня**.
- **Итого Free для текущего сценария: ~1.5–2 рабочих недели.**
- Pro backend kit + dashboards — **~2–3 недели** в отдельном repo, **по подтверждённому спросу**.

Если стриминг не критичен на старте, **Free Этапа 1** уже достаточно для отгрузки. Pro-часть продаётся как ускорение деплоя — она не блокирует возможность сборки игры на Free.
