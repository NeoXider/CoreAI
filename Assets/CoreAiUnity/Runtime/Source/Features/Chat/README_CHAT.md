# 🗨️ CoreAI Universal Chat Module

Встроенный чат с AI для любой Unity-игры. Работает из коробки с UI Toolkit.

Независимо от UI, из любого скрипта можно вызвать LLM через статический фасад **`CoreAi`** — см. [COREAI_SINGLETON_API.md](../../../Docs/COREAI_SINGLETON_API.md) (шпаргалка, FAQ для новичков, рекомендации для опытных разработчиков).

**Удобство «отправки» в двух режимах**

- **Панель чата** — игрок печатает в поле и жмёт кнопку или **Shift+Enter** (логика клавиш настраивается в `CoreAiChatConfig`, см. раздел *Ручной запуск*). Ответ приходит в те же пузыри, стриминг — по флагам.
- **Код без панели** — «отправка» = вызов `CoreAi.AskAsync("...")` / `CoreAi.StreamAsync` с тем же текстом. Под капотом тот же `CoreAiChatService`, что и у панели, пока на сцене один `CoreAILifetimeScope`.

Таблица «UI vs код» — в [COREAI_SINGLETON_API](../../../Docs/COREAI_SINGLETON_API.md) (подзаголовок *«Отправка сообщений: удобно и в UI, и из кода»*).

## Быстрый старт (1 клик)

Меню `CoreAI → Setup → Create Chat Demo Scene` создаст готовую сцену `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` со всеми необходимыми объектами (камера, свет, EventSystem, `CoreAILifetimeScope`, `UIDocument` + `CoreAiChatPanel` с дефолтным `CoreAiChatConfig_Demo`). Нажмите Play и пообщайтесь с моделью.

## Ручной запуск (2 шага)

### 1. Создайте конфиг
`Assets → Create → CoreAI → Chat Config`

Настройте в Inspector:
- **Role ID** — роль агента (`PlayerChat`, `Teacher`, ваша кастомная)
- **Header Title** — заголовок чата
- **Welcome Message** — приветственное сообщение
- **Сессия / история** (с 0.25.4) — см. [восстановление сессии](#persisted-chat-session)
- **Программный вызов** (с 0.25.5) — см. [`SubmitMessageFromExternalAsync`](#programmatic-chat-submit)
- **Enable Streaming** — потоковая генерация ответов
- **Send On Shift+Enter** — горячая клавиша отправки
- **Горячие клавиши** (с 0.25.3) — см. раздел [ниже](#chat-hotkeys)

### 2. Добавьте на сцену

1. Создайте `GameObject` с компонентом `UIDocument`
2. Назначьте UXML: `Packages/com.nexoider.coreaiunity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml`
3. Добавьте компонент `CoreAiChatPanel`
4. Назначьте ваш `CoreAiChatConfig`
5. **Готово!** Чат работает с текущим CoreAI бэкендом

<a id="persisted-chat-session"></a>

## Восстановление сессии (история после перезапуска) — с 0.25.4

По умолчанию **`CoreAiChatPanel`** при включении (`OnEnable`) подгружает в ленту сообщений сохранённую историю чата для **`Role ID`** из конфига.

| Поле в **Chat Config** | Назначение |
|------------------------|------------|
| **Load Persisted Chat On Startup** | Если включено (по умолчанию **да**) — перед приветствием читается история из **`IAgentMemoryStore`** (`FileAgentMemoryStore`: `persistentDataPath/CoreAI/AgentMemory/<RoleId>.json`, поле `chatHistoryJson`). |
| **Max Persisted Messages For Ui** | Сколько **последних** сообщений показать при подгрузке; **0** = все сохранённые. |

**Условия:** для роли в `AgentMemoryPolicy` должны быть включены **`WithChatHistory`** и **`PersistChatHistory`** (например `AgentBuilder.WithChatHistory(..., persistBetweenSessions: true)`), иначе на диск история не пишется — подгружать будет нечего (останется только **Welcome Message**).

**Приветствие:** если после подгрузки в скролле **уже есть** сообщения, **Welcome Message** не добавляется (чтобы не дублировать «Привет!» поверх диалога). Если истории нет — приветствие показывается как раньше.

**Повторный `OnEnable`:** перед гидратацией лента **очищается**, затем снова читается store — дубликаты при выключении/включении объекта с панелью не копятся.

**Расширение:** переопределите **`HydrateStartupMessagesFromStore`** или **`TryAppendPersistedChatHistoryFromStore`**, если нужен свой источник сообщений.

## Сворачивание панели (FAB) — с 0.21.7

На узких экранах (ширина ≤ 720 или высота ≤ 560) чат по умолчанию **стартует свёрнутым**: видна только круглая кнопка **`coreai-chat-fab`** в правом нижнем углу. Кнопка **`coreai-chat-collapse`** (`—`) в шапке сворачивает панель обратно в FAB.

- **Персист:** выбор «свернут / развёрнут» сохраняется в `PlayerPrefs` под ключом `CoreAI.Chat.Collapsed` (целое: `1` = свёрнут). Если ключ ещё не задан, на мобильном layout применяется дефолт «свёрнут».
- **API из кода:**
  - `bool IsCollapsed { get; }`
  - `void SetCollapsed(bool collapsed, bool persist = true)` — развернуть перед катсценой или свернуть после неё; при `persist: false` состояние не пишется в `PlayerPrefs`.
- **UXML:** элементы `coreai-chat-collapse` (в `coreai-chat-header`) и `coreai-chat-fab` (корень, до `coreai-chat-root`).
- **USS:** `.coreai-chat-header-btn`, `.coreai-collapsed` на контейнере, `.coreai-chat-fab` / `.coreai-chat-fab-icon`.

Кастомная вёрстка: если вы **копируете** UXML в свой проект, добавьте те же имена элементов или переопределите привязку в наследнике `CoreAiChatPanel` (переопределите `BindUI` и вызовите `base.BindUI()` либо продублируйте логику).

<a id="chat-hotkeys"></a>

## Горячие клавиши FAB / Esc (настраиваются в `CoreAiChatConfig`) — с 0.25.3

Все переключатели — в asset **CoreAI → Chat Config** на панели (`CoreAiChatPanel.config`):

| Поле | Назначение |
|------|------------|
| **Enable Open Chat Keyboard Shortcut** | Если выключено — чат из свёрнутого (FAB) открывается **только кликом** по FAB, без клавиши. |
| **Open Chat Hotkey** | `KeyCode` открытия свёрнутого чата (по умолчанию **C**). Для **A–Z** учитываются и код клавиши, и вводимый символ (без Ctrl / Cmd / Alt). |
| **Enable Escape Chat Shortcuts** | Если выключено — **Esc** не обрабатывается панелью (удобно, если Esc полностью отдан FPS / паузе). |

### Из кода (поверх конфига)

У `CoreAiChatPanel` есть **runtime-переопределения** (имеют приоритет над `CoreAiChatConfig`, пока не сброшены):

| Метод / свойство | Назначение |
|------------------|------------|
| `SetRuntimeEscapeChatShortcutsEnabled(false)` | Полностью отключить Esc у чата (стоп генерации + сворачивание), не меняя asset. |
| `SetRuntimeEscapeChatShortcutsEnabled(null)` | Снова следовать полю **Enable Escape Chat Shortcuts** в конфиге. |
| `SetRuntimeOpenChatKeyboardShortcutEnabled(bool?)` | Включить/выключить клавишу открытия свёрнутого чата. |
| `SetRuntimeOpenChatHotkey(KeyCode?)` | Сменить клавишу открытия в рантайме. |
| `ClearRuntimeHotkeyOverrides()` | Сбросить все три переопределения. |
| `EffectiveOpenChatKeyboardShortcutEnabled`, `EffectiveOpenChatHotkey`, `EffectiveEscapeChatShortcutsEnabled` | Текущее итоговое поведение (конфиг + оверрайды). |

Пример: отдать Esc только игроку, пока открыт мир:

```csharp
void OnWorldMapOpened()
{
    chatPanel.SetRuntimeEscapeChatShortcutsEnabled(false);
}

void OnWorldMapClosed()
{
    chatPanel.SetRuntimeEscapeChatShortcutsEnabled(null); // или true
}
```

**Поведение по умолчанию (оба флага включены):**

- Пока чат **свёрнут** — нажатие настроенной клавиши открывает панель (обработка в `OnRootKeyDown` на фазе `TrickleDown` с корня `UIDocument`).
- Пока чат **развёрнут** — **Esc** сначала останавливает активную генерацию (если идёт запрос/стрим), иначе **сворачивает** панель в FAB.
- Дополнительно в **`Update()`** вызывается опрос **Legacy `Input.GetKeyDown`** только когда у корня UITK **нет** сфокусированного элемента (`Root.focusController.focusedElement == null`) — чтобы сочетаться с управлением персонажем, когда фокус не в UI. На **WebGL** в том же `Update()` по-прежнему сбрасывается `WebGLInput.captureAllKeyboardInput`.

**Ограничение:** если в *Player Settings* включён только **New Input System** без Legacy, `Input.*` недоступен — ветка опроса в `Update` тихо пропускается; клавиши работают, пока фокус клавиатуры в дереве UITK (или подключите свой слой ввода / `Both` в Active Input Handling).

**Интеграция с геймплеем:** после каждого `SetCollapsed` вызывается `protected virtual void OnCollapsedStateChanged(bool collapsed)` — в наследнике можно подписать паузу движения, курсор и т.д., не таща игровые контроллеры в CoreAI.

Наследники **`Update()`** должны вызывать **`base.Update()` первым**, если переопределяют метод (иначе потеряете WebGL-фикс и poll горячих клавиш).

<a id="programmatic-chat-submit"></a>

## Программный вызов из кода (кат-сцена, квест, кнопка в мире) — с 0.25.5

Получите ссылку на панель (`GetComponent<CoreAiChatPanel>()`, singleton в сцене и т.д.) и вызывайте:

```csharp
using CoreAI.Chat;
using System.Threading;
using System.Threading.Tasks;

// Обычный ход: пузырь пользователя в чате + запрос к LLM (как после ввода в поле)
string? reply = await chatPanel.SubmitMessageFromExternalAsync(
    "Расскажи про квест",
    cancellationToken: CancellationToken.None);

// Тихий вызов: не дублировать текст в UI, только оркестратор
var opt = new CoreAiChatExternalSubmitOptions { AppendUserMessageToChat = false };
reply = await chatPanel.SubmitMessageFromExternalAsync("Секретный контекст для модели", opt);

// Только нарратив в UI, без LLM (подставной ответ ассистента)
var fake = new CoreAiChatExternalSubmitOptions
{
    AppendUserMessageToChat = true,
    SimulatedAssistantReply = "Добро пожаловать в город!"
};
reply = await chatPanel.SubmitMessageFromExternalAsync("…", fake);
```

| Поле `CoreAiChatExternalSubmitOptions` | По умолчанию | Назначение |
|----------------------------------------|----------------|------------|
| **`AppendUserMessageToChat`** | `true` | Добавить пузырь **пользователя** с текстом запроса перед ходом. |
| **`SimulatedAssistantReply`** | `null` | Если задана непустая строка — **LLM не вызывается**; в ленту добавляется пузырь ассистента с этим текстом (после strip think и `FormatResponseText`). |

**Возврат:** строка ответа ассистента (в т.ч. симулированная), либо `null`, если панель занята другим запросом, текст после `OnMessageSending` пустой, или операция отменена.

**Хуки:** по-прежнему вызываются `OnMessageSending`, для реального ответа — `OnResponseReceived` / событие **`OnAiResponseCompleted`**.

## Остановка генерации (Stop) — с 0.22.0

С **0.25.5** отдельной кнопки «стоп» в **шапке** нет — остановка только через кнопку отправки и Esc (ниже).

Во время активной генерации `CoreAiChatPanel` автоматически переключает кнопку отправки в режим остановки:

- текст кнопки: `X` вместо `>`;
- tooltip: `Остановить генерацию (Esc)`;
- стиль: красный (`.coreai-chat-send-button-stop`).

Остановить текущий ответ можно двумя способами:

- нажать кнопку отправки повторно (в режиме `Stop`);
- нажать `Esc` пока чат генерирует ответ (если в `CoreAiChatConfig` включён **Enable Escape Chat Shortcuts**).

Под капотом вызывается `CoreAi.StopAgent(roleId)` и отменяется активный `CancellationToken` запроса, поэтому останавливаются и текущая генерация, и задачи этой роли в очереди оркестратора.

## Очистка контекста из UI

Кнопка **`*`** в хеддере (`coreai-chat-clear`) вызывает `ClearChat()`:

- очищает сообщения в UI;
- по умолчанию очищает только историю чата (`clearChatHistory: true`, `clearLongTermMemory: false`).

Для ручного гранулярного управления доступна перегрузка:

```csharp
// Только краткосрочный контекст (поведение кнопки clear по умолчанию)
chatPanel.ClearChat(clearChatHistory: true, clearLongTermMemory: false);

// Полный сброс: краткосрочный контекст + долговременная память
chatPanel.ClearChat(clearChatHistory: true, clearLongTermMemory: true);

// Только долговременная память
chatPanel.ClearChat(clearChatHistory: false, clearLongTermMemory: true);
```

## Архитектура промптов (3 слоя)

| Слой | Источник | Пример |
|------|----------|--------|
| 1 | `CoreAISettings.universalSystemPromptPrefix` | "Отвечай кратко. Не обсуждай запрещённые темы." |
| 2 | `.txt` файл через `AgentPromptsManifest` | `TeacherSystemPrompt.txt` |
| 3 | `AgentBuilder.WithSystemPrompt()` | "Ты обучаешь ученика теме: циклы for" |

Итоговый промпт = `Слой 1` + `\n` + `Слой 2` + `\n\n` + `Слой 3`

### Переопределение universalPrefix

По умолчанию **universalPrefix применяется ко всем ролям**. Если нужен полностью кастомный промпт без общих правил — используйте `.WithOverrideUniversalPrefix()`:

```csharp
// Обычный агент — prefix + base + additional (все 3 слоя)
new AgentBuilder("Teacher")
    .WithSystemPrompt("Ты учитель Python.")
    .Build();

// Кастомный агент — БЕЗ universalPrefix (только base + additional)
new AgentBuilder("JsonParser")
    .WithSystemPrompt("You are a strict JSON parser.")
    .WithOverrideUniversalPrefix()  // ← prefix пропускается
    .Build();
```

## Streaming (оба бэкенда)

| Бэкенд | Механизм | Реальный стриминг? |
|--------|----------|--------------------|
| **HTTP API** (OpenAI, LM Studio) | SSE (`stream: true`) → парсинг `data:` чанков | ✅ Да (Standalone / Editor); ⚠️ нет на WebGL — см. ниже |
| **LLMUnity** (локальная GGUF) | `LLMAgent.Chat(callback)` → дельта через ConcurrentQueue | ✅ Да |

> ⚠️ **WebGL caveat (актуально для 0.25.x).** В собранном WebGL-плеере `UnityWebRequest`-обёртка
> (emscripten `XMLHttpRequest`) не отдаёт SSE incrementally — все чанки прилетают одним блоком в
> конце запроса (`chunks=1` в логе `LLM ◀ (stream)`). Из-за этого typing-индикатор может зависнуть,
> а bubble не появиться. **Workaround:** под `#if UNITY_WEBGL && !UNITY_EDITOR` принудительно
> ставить `CoreAiChatConfig.EnableStreaming = false` (любая non-streaming-ветка работает корректно).
> Полный план фикса (включая `.jslib`-fetch-bridge) — в [`STREAMING_WEBGL_TODO.md`](../../../../Docs/STREAMING_WEBGL_TODO.md).

### Streaming + Tool Calling (single-cycle)

Если в стриминговом ответе модель сначала отдает tool-call JSON, CoreAI выполняет единый цикл:

1. получает стрим-ответ и детектит tool-call payload;
2. выполняет соответствующий tool;
3. добавляет результат tool в историю диалога;
4. продолжает генерацию следующим стриминговым шагом.

Tool JSON в UI не рендерится: игрок видит только итоговый читаемый ответ ассистента.

По умолчанию это поведение сразу активно для ролей с инструментами:

- `AgentMode.ToolsAndChat`
- `AgentMode.ToolsOnly`

Для `AgentMode.ChatOnly` используется обычная иерархия флагов стриминга (UI/per-agent/global).

### Иерархия настроек стриминга

Порядок приоритета (от высшего к низшему):

1. **UI-флаг** — `CoreAiChatConfig.EnableStreaming` (Inspector панели чата). Если выключен → всегда non-streaming, остальные слои игнорируются.
2. **Per-agent override** — `AgentBuilder.WithStreaming(true/false)` (зарегистрирован в `AgentMemoryPolicy`).
3. **Глобально** — `ICoreAISettings.EnableStreaming` (чекбокс в `CoreAISettings.asset`).

```csharp
// Пример: агент-чат всегда стримит независимо от глобальной настройки
new AgentBuilder("PlayerChat")
    .WithSystemPrompt("Ты дружелюбный помощник.")
    .WithStreaming(true)
    .Build();

// Пример: агент-парсер никогда не стримит (нужен полный JSON сразу)
new AgentBuilder("JsonParser")
    .WithSystemPrompt("You are a strict JSON parser.")
    .WithOverrideUniversalPrefix()
    .WithStreaming(false)
    .Build();
```

Программная проверка эффективного значения:

```csharp
var chatService = CoreAiChatService.TryCreateFromScene();
bool useStream = chatService.IsStreamingEnabled("PlayerChat", uiFallback: true);
```

### Think-block фильтрация

Модели с reasoning (DeepSeek, Qwen3) генерируют `<think>...</think>` блоки. CoreAI автоматически:
- **При стриминге**: общий stateful-фильтр `CoreAI.Ai.ThinkBlockStreamFilter` корректно удаляет блоки, **даже если открывающий/закрывающий тег разбит между SSE-чанками**. Пока модель «думает», показывается typing indicator.
- **Без стриминга**: regex убирает `<think>` блоки из финального ответа.
- **Tool calls**: не отображаются в чате (обрабатываются внутри MEAI pipeline, включая streaming single-cycle).

> Стриминг должен вызываться с **main thread Unity** (из coroutine, `async void`, `UniTask` или обычного async-метода в UI). Оборачивание `CompleteStreamingAsync` в `Task.Run` приведёт к исключению `"Create can only be called from the main thread"` из-за создания `UnityWebRequest`.

## Расширение через наследование

```csharp
public class MyGameChatPanel : CoreAiChatPanel
{
    // Перехватить отправку (валидация, модификация)
    protected override string OnMessageSending(string text)
    {
        if (text.Contains("bad word")) return null; // отменить
        return text;
    }

    // Пост-обработка ответа (markdown, аналитика)
    protected override string FormatResponseText(string rawText)
    {
        return MarkdownRenderer.Render(rawText);
    }

    // Полностью кастомная вёрстка сообщений
    protected override VisualElement CreateMessageBubble(string text, bool isUser)
    {
        var bubble = base.CreateMessageBubble(text, isUser);
        // Добавьте свои классы, анимации, иконки...
        return bubble;
    }
}
```

## Программное использование (без UI)

### Вариант 1 — статический фасад `CoreAi` (рекомендуется)

```csharp
// Синхронно
string answer = await CoreAi.AskAsync("Привет!", roleId: "Teacher");

// Стриминг (чанки по мере генерации)
await foreach (string chunk in CoreAi.StreamAsync("Расскажи о Python", "Teacher"))
    myTextLabel.text += chunk;

// Smart — сам решает режим, попутно вызывает onChunk
string full = await CoreAi.SmartAskAsync(
    "Вопрос", "Teacher", onChunk: c => myTextLabel.text += c);

// Полный оркестратор-пайплайн (history + authority + publish command):
string json = await CoreAi.OrchestrateAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "spawn JSON" });

// Стриминговый оркестратор:
await foreach (var chunk in CoreAi.OrchestrateStreamAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "explain" }))
{
    if (!string.IsNullOrEmpty(chunk.Text)) statusLabel.text += chunk.Text;
    if (chunk.IsDone) break;
}
```

Подробнее: [`COREAI_SINGLETON_API.md`](../../../Docs/COREAI_SINGLETON_API.md).

### Вариант 2 — прямой доступ к сервису

```csharp
var chatService = CoreAiChatService.TryCreateFromScene();

// Non-streaming
string response = await chatService.SendMessageAsync("Привет!", "Teacher");

// Streaming
await foreach (var chunk in chatService.SendMessageStreamingAsync("Расскажи о Python", "Teacher"))
{
    myTextLabel.text += chunk.Text;
}
```

## Настройка агента

```csharp
// В вашем LifetimeScope:
var config = new AgentBuilder("Teacher")
    .WithSystemPrompt("Ты учитель Python для школьников.")  // Слой 3
    .WithChatHistory(4096, persistBetweenSessions: true)
    .WithMemory()
    .Build();
config.ApplyToPolicy(policy);
```

## Интеграция с VContainer / MessagePipe

В проектах с VContainer рекомендуемая архитектура:

```
CoreAiChatPanel (UI) → события → ChatPresenter (VContainer)
    → MessagePipe → SendMessageUseCase (Application)
```

`CoreAiChatPanel` публикует события `OnUserMessageSent` и `OnAiResponseCompleted`.
`ChatPresenter` (VContainer `IStartable`) подписывается и маршрутизирует через `MessagePipe`.

## Кастомные стили

Создайте свой `.uss` файл и назначьте в `CoreAiChatPanel.customStyleSheet`.
Все CSS-классы имеют префикс `coreai-` для избежания конфликтов:

| Класс | Описание |
|-------|----------|
| `.coreai-chat-container` | Контейнер чата |
| `.coreai-chat-container.coreai-collapsed` | Контейнер скрыт (режим FAB) |
| `.coreai-chat-header` | Заголовок |
| `.coreai-chat-header-btn` | Кнопка сворачивания в шапке |
| `.coreai-chat-fab` | Плавающая кнопка «открыть чат» |
| `.coreai-chat-fab-icon` | Иконка внутри FAB |
| `.coreai-ai-message` | Пузырь AI |
| `.coreai-user-message` | Пузырь пользователя |
| `.coreai-streaming-active` | Активный стриминг |
| `.coreai-chat-send-button` | Кнопка отправки |
| `.coreai-typing-message` | Индикатор «печатает...» |

## События

```csharp
var chatPanel = GetComponent<CoreAiChatPanel>();
chatPanel.OnUserMessageSent += (text) => Analytics.Track("chat_message", text);
chatPanel.OnAiResponseCompleted += (response) => Analytics.Track("ai_response", response);
```

