# Example Game — настройка в Unity (RogueliteArena)

Пошаговая инструкция: открыть демо-сцену, настроить **LLM** (локально или по HTTP), убедиться, что **F9** вызывает **Programmer** с исполнением Lua.

Предполагается версия Unity из **`ProjectSettings/ProjectVersion.txt`** (ветка **6000.3.x**).

---

## Шаг 1. Открыть сцену примера

1. Запустите проект **CoreAI** в Unity.
2. Верхнее меню: **CoreAI → Development → Example Game → Open RogueliteArena scene**  
   *или* Project: **`Assets/_exampleGame/Scenes/RogueliteArena.unity`** → двойной щелчок.

Сцена уже содержит нужную иерархию; правки в инспекторе — в основном **модель LLM** и опционально **OpenAI HTTP**.

---

## Шаг 2. Понять иерархию (ничего не ломая)

В **Hierarchy** под **`CompositionRoot`** есть дочерний **`ArenaGameplay`**:

| Объект | Компоненты |
|--------|------------|
| **ArenaGameplay** | **ArenaSurvivalProceduralSetup** — волны, игрок, HUD (поле **Skip Runtime Floor** включено: пол сцены — **ArenaGroundPlane**). |
| **ArenaGroundPlane** | Mesh (Plane), **MeshCollider**, материал — видимое игровое поле ~44×44 м. |
| **PlayerSpawn** | Пустой Transform — стартовая позиция игрока (**Player Spawn Anchor** в сетапе). |

**Main Camera** уже содержит **ArenaFollowCamera** (цель подставляется при спавне игрока).

Далее — объект **`CompositionRoot`** (родитель арены). На нём висят:

| Компонент | Роль |
|-----------|------|
| **CoreAILifetimeScope** | Корень DI ядра: лог, MessagePipe, **`ILlmClient`** (в рантайме — **`LoggingLlmClientDecorator`** + реализация), оркестратор, роутер **`ApplyAiGameCommand`**, Lua-процессор. Поля: **Llm Request Timeout Seconds** (по умолчанию 15, 0 = без лимита), **Game Log Settings**. **Auto Run** включён. |
| **ExampleRogueliteEntry** | Старт прототипа арены (волны). В **Awake** добавляет **`CoreAiLuaHotkey`** (клавиша **F9**). |

**Дочерний** объект **`LLM`** (под `CompositionRoot`):

| Компонент | Роль |
|-----------|------|
| **LLM** | Сервер inference LLMUnity: модель GGUF, потоки, GPU layers, контекст. |
| **LLMAgent** | Клиент к этому **LLM**; поле **LLM** должно ссылаться на соседний компонент **LLM**. |

**Важно:** `CoreAILifetimeScope` ищет в сцене **`LLMAgent`** через `FindFirstObjectByType`. Пока **Open Ai Http Llm Settings** не переводит ядро на HTTP, живой ответ модели идёт через этот агент.

---

## Шаг 3. Режим A — только LLMUnity (локальная GGUF)

Подходит для офлайн-разработки и соответствует дефолту сцены (**Open Ai Http Llm Settings** = *None*).

По официальному **Quick start** LLMUnity: на объекте **LLM** скачайте или загрузите `.gguf` (**Download model** / **Load model**), затем **обязательно нажмите радиокнопку** слева от нужной строки в списке Model Manager — так в компонент записывается поле **model** (путь к файлу). **Сохраните сцену (Ctrl+S).** Без этого в YAML сцены `_model` остаётся пустым, CoreAI отключает LLMUnity и берёт **StubLlmClient**.

1. Выберите объект **`LLM`** в Hierarchy.
2. Компонент **LLM (Script)**:
   - В **Model Settings**: **Download model** или **Load model** → выберите строку модели **радиокнопкой** → **Ctrl+S**.
   - Колонка **Build**: для релиза обычно одна основная модель с галочкой (см. README пакета, раздел *LLM model management*).
   - При наличии GPU увеличьте **Num GPU Layers** (начните с части слоёв, при нехватке VRAM уменьшите).
   - **Remote** на **LLM** — выключен для чисто локального режима.
   - Для отладки: **Log Level = All** (если поле есть в вашей версии пакета).
   - Если включён **Download on Start**, при первом запуске дождитесь загрузки; в коде LLMUnity рекомендует `await LLM.WaitUntilModelSetup()` — адаптер CoreAI **LlmUnityLlmClient** ждёт готовности перед **Chat**.
3. Компонент **LLM Agent (Script)**:
   - **LLM** — ссылка на компонент **LLM** на том же GameObject (в репозитории уже проставлена).
   - **Remote** — выключен.
4. На **`CompositionRoot` → CoreAILifetimeScope** поле **Open Ai Http Llm Settings** оставьте **None** *или* в назначенном asset снимите **Use Open Ai Compatible Http**.

5. **Play.** Дождитесь загрузки модели (первый раз может занять время). В консоли не должно быть ошибок инициализации **LLM**.

Дополнительно: рекомендации по размерам Qwen и билду — **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §2.

---

## Шаг 4. Режим B — OpenAI-compatible HTTP (LM Studio, облако)

Когда локальная GGUF не нужна, а есть сервер с **`/v1/chat/completions`**:

1. В Project: **ПКМ → Create → CoreAI → LLM → OpenAI-compatible HTTP** (ScriptableObject). Сохраните, например, в `Assets/_exampleGame/Settings/`.
2. В asset:
   - **Api Base Url** — без слэша в конце, **с `/v1`**, например `http://127.0.0.1:1234/v1` (LM Studio) или `https://api.openai.com/v1`.
   - **Model** — имя модели на сервере.
   - **Api Key** — для OpenAI обычно обязателен; для локального LM Studio часто пусто.
   - Включите **Use Open Ai Compatible Http**.
3. Выберите **`CompositionRoot`**, в **CoreAILifetimeScope** перетащите asset в **Open Ai Http Llm Settings**.

После этого **`ILlmClient`** = **OpenAiChatLlmClient**; компоненты **LLM** / **LLMAgent** для вызовов ядра **не используются** (их можно оставить на сцене выключенными или для других целей).

Подробности: **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §4.

---

## Шаг 5. Опционально: логи и промпты

| Поле CoreAILifetimeScope | Назначение |
|--------------------------|------------|
| **Game Log Settings** | Asset **Create → CoreAI → Logging → Game Log Settings** — фильтр категорий/уровней. Включите **Llm** для логов **`LLM ▶` / `LLM ◀`** и **traceId** в **`ApplyAiGameCommand`**. Старый ассет без **Llm** обновится при открытии в инспекторе. |
| **Llm Request Timeout Seconds** | Автоотмена одного вызова модели (секунды). **0** — без ограничения. |
| **Agent Prompts Manifest** | **Create → CoreAI → Agent Prompts Manifest** — переопределения системных/user промптов и кастомные роли. |

Без них ядро работает на встроенных промптах и дефолтном логе (и таймауте **15** с).

---

## Шаг 6. Проверка геймплея и ИИ

1. **Play.**
2. Должен стартовать прототип арены (волны, управление согласно скриптам арены — см. консольное сообщение при старте).
3. Нажмите **F9** — ставится задача роли **Programmer**; модель должна вернуть ответ с Lua; **`LuaAiEnvelopeProcessor`** выполнит его; в логе появится вывод **`report(...)`** из **`LoggingLuaRuntimeBindings`**.
4. **R** — перезагрузка сцены (прототип арены).

Если при **F9** нет реального ответа модели:

- Проверьте режим **A** или **B** выше.
- Убедитесь, что не остался активным только **StubLlmClient** (в DI снаружи — декоратор: смотрите разрешённый **`ILlmClient`** и при необходимости лог **`backend=StubLlmClient`**; см. [LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)).

---

## Шаг 7. Сборка билда (кратко)

1. **File → Build Settings** — добавьте **RogueliteArena** (уже может быть в списке).
2. Для воспроизводимого **Play** из редактора: **CoreAI → Development → Example Game → Set RogueliteArena as first build scene**.
3. Для локальной модели в релизе: в LLMUnity у нужной GGUF включите **Build** и политику доставки модели (StreamingAssets и т.д.) — см. **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §2 и §6.

---

## Связанные документы

- **[QUICK_START.md](../../CoreAiUnity/Docs/QUICK_START.md)** — общий быстрый старт по репозиторию.
- **[DEVELOPER_GUIDE.md](../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md)** — поток данных и расширение ядра.
- **[README.md](../README.md)** — обзор Example Game.

**Версия:** 1.1 (апрель 2026) — таймаут LLM, логи Llm/traceId, декоратор ILlmClient.
