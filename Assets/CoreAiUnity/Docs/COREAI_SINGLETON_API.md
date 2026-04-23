# `CoreAi` — статический API для всех

Один класс **`CoreAI.CoreAi`** — единая точка входа к LLM и оркестратору. Не нужно знать VContainer, писать свой singleton или искать сервисы на сцене вручную.

| Кому | Что получить |
|------|----------------|
| **Новичок** | Скопировать 3 шага ниже → `await CoreAi.AskAsync(...)` в скрипте на объекте. |
| **Опытный разработчик** | Та же статика для прототипов и UI; для крупной архитектуры — `TryGet*` + DI, см. раздел [Профессиональный стек](#5-профессиональный-стек). |

---

## Минимум для новичка (3 шага)

1. **Сцена с CoreAI** — меню **CoreAI → Setup → Create Chat Demo Scene** или **CoreAI → Create Scene Setup** (есть `CoreAILifetimeScope`).
2. **Бэкенд** — в `CoreAISettings` укажите HTTP (LM Studio) или LLMUnity; см. [QUICK_START](QUICK_START.md).
3. **Код** — на любом `MonoBehaviour`:

```csharp
using CoreAI;

public class MyNpc : MonoBehaviour
{
    async void OnPlayerTalk()
    {
        if (!CoreAi.IsReady) { Debug.LogWarning("Нет CoreAILifetimeScope на сцене"); return; }

        string reply = await CoreAi.AskAsync("Как дела?");
        Debug.Log(reply);
    }
}
```

Стриминг «как в чате» — одна строка цикла:

```csharp
await foreach (string part in CoreAi.StreamAsync("Расскажи про квест", "PlayerChat"))
    uiLabel.text += part;
```

Готово. Роль `"PlayerChat"` должна совпадать с `AgentBuilder` / конфигом чата, если вы настраивали агентов.

### Отправка сообщений: удобно и в UI, и из кода

| Как вы общаетесь | Что нажимаете / пишете | Куда идёт запрос |
|------------------|------------------------|------------------|
| **Окно чата** (`CoreAiChatPanel`) | Кнопка отправки, **Shift+Enter** (по умолчанию) или **Enter** — в зависимости от `CoreAiChatConfig.SendOnShiftEnter` | `CoreAiChatService` → тот же `ILlmClient`, что и у `CoreAi` |
| **Скрипт** (NPC, квест, кнопка «Спросить») | Вызов `CoreAi.AskAsync("текст")` или `CoreAi.StreamAsync` — это и есть «отправка» пользовательского запроса в LLM | Тот же `CoreAiChatService` внутри `CoreAi` |

Оба пути используют **один** зарегистрированный в сцене `CoreAILifetimeScope` и **одни** настройки бэкенда. Разница только в UX: в панели вы набирате текст в поле; в коде передаёте строку в метод. Подключение кистей/стриминга/ролей — в [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) и [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md).

**Итог:** для игрока в чате — встроенная панель; для логики игры без виджета — `CoreAi`. Вместе **CoreAI + CoreAiUnity** закрывают сценарий «удобно везде»: демо-сцена за 1 клик, горячие клавиши в инспекторе, и одна строка `CoreAi` в любом `MonoBehaviour`.

---

## Быстрая шпаргалка (все методы)

| Метод | Возвращает | Когда использовать |
|-------|------------|-------------------|
| `AskAsync` | `Task<string?>` | Нужен **целый ответ одной строкой** (логика, сохранение, простой NPC). |
| `StreamAsync` | `IAsyncEnumerable<string>` | **Живой текст** в UI (подпись, TMP, UI Toolkit). |
| `StreamChunksAsync` | `IAsyncEnumerable<LlmStreamChunk>` | Нужны **IsDone, Error, usage** по чанкам. |
| `SmartAskAsync` | `Task<string?>` | И **стрим в UI**, и **полная строка** в конце (аналитика, квесты). Режим стрима решает иерархия настроек. |
| `OrchestrateAsync` | `Task<string?>` | Полный **игровой пайплайн**: снимок сессии, authority, очередь, валидация, **публикация команды** в шину. |
| `OrchestrateStreamAsync` | `IAsyncEnumerable<LlmStreamChunk>` | То же, но **токены по мере генерации** + финальная публикация после стрима. |
| `OrchestrateStreamCollectAsync` | `Task<string>` | Стрим + **сборка полного текста** + `onChunk` для UI. |
| `IsReady` | `bool` | Можно ли вызывать API (scope + сервисы). |
| `Invalidate()` | `void` | После **смены сцены** или в тестах — сброс кэша. |
| `TryGetChatService` / `TryGetOrchestrator` | `bool` | **Без исключений**: проверка перед кнопкой в UI или опциональным AI. |
| `GetChatService` / `GetOrchestrator` / `GetSettings` | сервисы | Прямой доступ, когда нужен полный контроль. |

Подробные сценарии — в разделе [Когда что использовать](#2-когда-что-использовать-подробно) ниже.

---

## 1. Для новичков: частые вопросы

**Почему падает или ничего не происходит?**  
Убедитесь, что на **активной** сцене есть GameObject с **`CoreAILifetimeScope`**. После `LoadScene` вызовите `CoreAi.Invalidate()` или проверяйте `CoreAi.IsReady` / `CoreAi.TryGetChatService(out _)`.

**Чем `AskAsync` отличается от `OrchestrateAsync`?**  
- `AskAsync` — **чат**: промпт + история роли, ответ в виде текста.  
- `OrchestrateAsync` — **задача для игры**: снимок сессии, роли вроде Creator, публикация **JSON-команды** в игровую шину. Для «поговорить с NPC» обычно хватает `Ask` / `Stream`.

**Можно ли вызывать не из `async void`?**  
Можно из `Start` с `StartCoroutine` + обёрткой, но проще — **`async void` на Unity main thread** или **UniTask**. С `Task.Run` не вызывайте — LLM-запросы должны оставаться на **главном потоке** (см. [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md)).

**Где взять `roleId`?**  
Тот же id, что в `AgentBuilder("...")` и в `CoreAiChatConfig`. Часто `"PlayerChat"`.

---

## 2. Когда что использовать (подробно)

| Слой | Метод | Что делает | Когда брать |
|------|-------|------------|-------------|
| **Chat** | `CoreAi.AskAsync` | Ждёт полный ответ, история чата по роли. | Простой диалог, лог, «одна строка». |
| **Chat** | `CoreAi.StreamAsync` | Чанки строк. | Подпись / чат, эффект «печатает». |
| **Chat** | `CoreAi.StreamChunksAsync` | `LlmStreamChunk` с метаданными. | Ошибки, `IsDone`, токены. |
| **Chat** | `CoreAi.SmartAskAsync` | Сам решает стрим или нет; `onChunk` + полный текст. | UI + сохранение полного ответа. |
| **Orchestrator** | `CoreAi.OrchestrateAsync` | Snapshot → prompt → authority → очередь → валидация → **ApplyAiGameCommand**. | Поведение агентов Creator / Programmer / сценарии с командами. |
| **Orchestrator** | `CoreAi.OrchestrateStreamAsync` | То же, но токены по ходу. | Квесты с длинным ответом + команда в конце. |
| **Orchestrator** | `CoreAi.OrchestrateStreamCollectAsync` | Стрим + накопление `string` + `onChunk`. | Удобно совместить лайв-UI и постобработку строки. |

---

## 3. Рецепты с кодом

### 3.1. Простой вопрос — одна строка

```csharp
string answer = await CoreAi.AskAsync("Привет! Сколько тебе лет?");
Debug.Log(answer);
```

### 3.2. Стриминг в UI Toolkit / TextMeshPro

```csharp
label.text = "";
await foreach (string chunk in CoreAi.StreamAsync("Расскажи анекдот", "PlayerChat"))
    label.text += chunk;
```

### 3.3. Smart: и чанки в UI, и полный текст в переменной

```csharp
string full = await CoreAi.SmartAskAsync(
    "Расскажи историю",
    roleId: "PlayerChat",
    onChunk: c => label.text += c);

SaveToPlayerJournal(full);
```

Переопределение стрима: `uiStreamingOverride: false` — принудительно полный ответ одним куском.

### 3.4. Безопасный вызов (без try при отсутствии AI)

```csharp
if (CoreAi.TryGetChatService(out var chat))
{
    string reply = await chat.SendMessageAsync("Привет", "PlayerChat", ct);
}
else
{
    // AI отключён или сцена без scope — показать дефолтный текст NPC
}
```

### 3.5. Оркестратор: команда в игру

```csharp
var task = new AiTaskRequest
{
    RoleId = "Creator",
    Hint = "Сгенерируй JSON команду spawn",
    Priority = 5,
    CancellationScope = "creator"
};

string json = await CoreAi.OrchestrateAsync(task);
```

### 3.6. Оркестратор со стримом в подпись

```csharp
var task = new AiTaskRequest { RoleId = "Creator", Hint = "Объясни шаг" };

string full = await CoreAi.OrchestrateStreamCollectAsync(task,
    onChunk: c => statusLine.text += c);
```

---

## 4. Жизненный цикл и сцены

- **`CoreAi`** кэширует ссылку на `CoreAILifetimeScope` и сервисы.
- **`SceneManager.sceneLoaded` / `OnDestroy` при выгрузке** — вызывайте **`CoreAi.Invalidate()`**, иначе возможен устаревший контейнер.
- **EditMode / PlayMode тесты** — в `[SetUp]`: `CoreAi.Invalidate()`.
- **`GetSettings()`** — может вернуть `null`, если scope ещё не готов; для глобальных дефолтов используйте также статический `CoreAISettings` из портативного ядра, если он у вас настроен.

---

## 5. Профессиональный стек

**Статика — не «антипаттерн» для CoreAI:** это **официальный фасад** поверх VContainer. Он:

- прокидывает вызовы в `CoreAiChatService` и `IAiOrchestrationService` без дублирования логики;
- уважает тот же `ILlmClient`, очередь, логи и метрики, что и ручной резолв.

**Когда оставить `CoreAi` везде:** прототипы, инструменты, `MonoBehaviour` в сцене, кнопки в меню, обучающие сцены.

**Когда внедрять интерфейсы (DI):** большая кодовая база, **юнит-тесты** без сцены, несколько scope’ов, строгая изоляция модулей. Паттерн:

```csharp
// Регистрация (у вас в LifetimeScope)
builder.Register<QuestAiController>(Lifetime.Transient)
    .WithParameter<Func<CoreAiChatService?>>(() => {
        if (CoreAi.TryGetChatService(out var s)) return s;
        return null;
    });
// или
builder.Register<QuestAiController>(Lifetime.Transient)
    .WithParameter<ILlmClient>(c => c.Resolve<ILlmClient>());
```

`CoreAi.GetChatService()` остаётся удобным **адаптером** на границе «скрипт на объекте ↔ ядро».

**Расширение поведения:** зарегистрируйте обёртку в контейнере; если она тот же тип, что ожидает `CoreAiChatService.TryCreateFromScene`, сценарий может потребовать явной регистрации — для тонкой настройки используйте **прямой** `IObjectResolver` в своём `LifetimeScope` и вызывайте сервисы оттуда; фасад `CoreAi` при этом остаётся валиден для **дефолтного** пути.

---

## 6. Main thread (обязательно)

```csharp
// OK — из MonoBehaviour, main thread
async void OnEnable() {
  await foreach (var c in CoreAi.StreamAsync("Hi")) t.text += c;
}

// НЕ ВЫПОЛНЯТЬ — worker thread + UnityWebRequest
_ = Task.Run(() => _ = CoreAi.AskAsync("x"));
```

---

## 7. Связанные документы

| Документ | Содержание |
|----------|------------|
| [QUICK_START](QUICK_START.md) | Установка, сцена, бэкенд |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | Панель чата, стили, события |
| [STREAMING_ARCHITECTURE](STREAMING_ARCHITECTURE.md) | SSE, LLMUnity, оркестратор-стрим, лимиты |
| [DOCS_INDEX](DOCS_INDEX.md) | Полная карта документации |

**Версия:** см. `Assets/CoreAiUnity/package.json` — в changelog релизов с `CoreAi` смотрите раздел *Singleton API* / *Orchestrator streaming*.
