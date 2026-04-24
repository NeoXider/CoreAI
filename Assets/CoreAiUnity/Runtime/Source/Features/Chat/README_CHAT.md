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
- **Enable Streaming** — потоковая генерация ответов
- **Send On Shift+Enter** — горячая клавиша отправки

### 2. Добавьте на сцену

1. Создайте `GameObject` с компонентом `UIDocument`
2. Назначьте UXML: `Packages/com.nexoider.coreaiunity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml`
3. Добавьте компонент `CoreAiChatPanel`
4. Назначьте ваш `CoreAiChatConfig`
5. **Готово!** Чат работает с текущим CoreAI бэкендом

## Сворачивание панели (FAB) — с 0.21.7

На узких экранах (ширина ≤ 720 или высота ≤ 560) чат по умолчанию **стартует свёрнутым**: видна только круглая кнопка **`coreai-chat-fab`** в правом нижнем углу. Кнопка **`coreai-chat-collapse`** (`—`) в шапке сворачивает панель обратно в FAB.

- **Персист:** выбор «свернут / развёрнут» сохраняется в `PlayerPrefs` под ключом `CoreAI.Chat.Collapsed` (целое: `1` = свёрнут). Если ключ ещё не задан, на мобильном layout применяется дефолт «свёрнут».
- **API из кода:**
  - `bool IsCollapsed { get; }`
  - `void SetCollapsed(bool collapsed, bool persist = true)` — развернуть перед катсценой или свернуть после неё; при `persist: false` состояние не пишется в `PlayerPrefs`.
- **UXML:** элементы `coreai-chat-collapse` (в `coreai-chat-header`) и `coreai-chat-fab` (корень, до `coreai-chat-root`).
- **USS:** `.coreai-chat-header-btn`, `.coreai-collapsed` на контейнере, `.coreai-chat-fab` / `.coreai-chat-fab-icon`.

Кастомная вёрстка: если вы **копируете** UXML в свой проект, добавьте те же имена элементов или переопределите привязку в наследнике `CoreAiChatPanel` (переопределите `BindUI` и вызовите `base.BindUI()` либо продублируйте логику).

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
| **HTTP API** (OpenAI, LM Studio) | SSE (`stream: true`) → парсинг `data:` чанков | ✅ Да |
| **LLMUnity** (локальная GGUF) | `LLMAgent.Chat(callback)` → дельта через ConcurrentQueue | ✅ Да |

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
- **Tool calls**: не отображаются в чате (обрабатываются внутри MEAI pipeline).

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

