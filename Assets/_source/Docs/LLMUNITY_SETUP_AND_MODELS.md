# LLMUnity: проверка в редакторе, модели в билде, OpenAI-совместимый API

**Цель:** быстро убедиться, что **LLMUnity + CoreAI** работают; какие **GGUF** включать в сборку; как переключиться на **OpenAI-compatible HTTP** (облако, LM Studio, vLLM и т.д.).

**С нуля:** [QUICK_START.md](QUICK_START.md). **Демо-сцена в инспекторе:** [../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md).

Связанные документы: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) (общий поток ядра), [AI_AGENT_ROLES.md](AI_AGENT_ROLES.md) (роли и размеры моделей), `CoreAILifetimeScope` (выбор бэкенда LLM).

### Официальная документация LLMUnity (Undream AI)

- Обзор и API: [undream.ai/LLMUnity](https://undream.ai/LLMUnity)  
- Репозиторий (README, Quick start, **LLM model management**): [github.com/undreamai/LLMUnity](https://github.com/undreamai/LLMUnity)  

**Quick start (кратко):** GameObject → компонент **LLM** → **Download model** или **Load model** (.gguf) → отдельный (или тот же) объект → **LLMAgent** → в инспекторе ссылка **LLM** на сервер → в коде `await llmAgent.Chat("...")`.  
Перед первым запросом в билдах с **Download on Start** в документации рекомендуется `await LLM.WaitUntilModelSetup();` — **LlmUnityLlmClient** в CoreAI дожидается завершения глобальной подготовки моделей и поднятия сервера **LLM** перед вызовом **Chat**.

**Model Manager (инспектор LLM):** список моделей копируется в билд; галочка **Build** отключает включение конкретной модели в сборку; выбор **радиокнопкой** записывает путь в поле **`LLM.model`** (его нужно **сохранить в сцене**). Если в списке несколько моделей с файлами на диске, а `model` пустой, CoreAI может **автовыбрать** одну (см. `LlmUnityModelBootstrap`: приоритет у записей с **Build**).

**CoreAI поверх LLMUnity:** при пустом `LLM.model` ранний guard **`LlmUnityAutoDisableIfNoModel`** отключает LLMUnity, чтобы не заспамить консоль «No model file provided!»; DI тогда использует **`StubLlmClient`**.

---

## 1. Что должно быть на сцене (локальный LLMUnity)

1. Объект с компонентом **`LLM`** (сервер/inference): выбрана модель **Qwen3.5 0.8B** (или другая GGUF), при необходимости **Num GPU Layers** &gt; 0 на видеокарте.
2. Дочерний (или связанный) объект с **`LLMAgent`**: в инспекторе указана ссылка на этот **`LLM`**, **Remote** выключен для чисто локального режима.
3. На **`CompositionRoot`** висит **`CoreAILifetimeScope`**: поле **Open Ai Http Llm Settings** пустое **или** в asset выключён **Use Open Ai Compatible Http** — тогда `ILlmClient` = **LlmUnityLlmClient** → ваш `LLMAgent`.

**Проверка:** Play Mode → консоль без ошибок загрузки модели; оркестратор/чат дергают `ILlmClient` (см. логи уровня AI).

**Логи LLMUnity:** на компоненте **LLM** выставьте **Log Level = All** на время отладки (как на вашем скриншоте).

**Логи CoreAI (запрос и ответ модели, роль агента):** `CoreAILifetimeScope` регистрирует `ILlmClient` через **`LoggingLlmClientDecorator`**. В консоли ищите **`[Llm]`** внутри префикса **`[CoreAI]`**: строки **«Запрос LLM»** (роль, `backend=…`, превью system/user) и **«Ответ LLM»** (превью `content`). Длинные тексты обрезаются — лимиты в `LoggingLlmClientDecorator.cs` (`SystemPreviewChars`, `UserPreviewChars`, `ResponsePreviewChars`). Чтобы отключить шум: asset **Game Log Settings** на scope → снимите флаг **Llm** (или поднимите **Minimum Level** выше Info). Итоговая команда игре всё ещё логируется отдельно в **`AiGameCommandRouter`** (`ApplyAiGameCommand`); если сработал парсер памяти, JSON в роутере может отличаться от сырого ответа в строке «Ответ LLM».

---

## 2. Рекомендации по моделям (Qwen 3.5 GGUF)

| Профиль | Модель (ориентир) | Билд | Заметки |
|--------|-------------------|------|--------|
| **Минимум / слабое железо** | **Qwen3.5 0.8B** Q4_K_M (или аналог) | **Включать в билд** как основной дефолт | Быстро, мало VRAM/RAM; для JSON и простых реплик хватит при жёстких промптах. |
| **Баланс** | **Qwen3.5 ~4B** Q4_K_M | Опционально: второй preset или DLC-пакет ассетов | Лучше диалог и отчёты **Analyzer** / **AINpc**. |
| **Качество** | **Qwen3.5 ~9B** Q4_K_M | Опционально: только для «High quality» профиля | Тяжелее по памяти; поднимайте **GPU layers**. |

**Сборка:** в LLMUnity у каждой модели есть флаг **Build** — для продакшена обычно **одна** основная (0.8B) + при необходимости отдельные билды «HD» с 4B/9B без принудительного включения всех в один билд (размер дистрибутива).

**Скачивание:** **Download on Start** удобно в разработке; в релизе чаще **включённые в StreamingAssets/ресурсы** модели с **Build**, чтобы офлайн работало предсказуемо.

---

## 3. Режим Remote в LLMUnity (не путать с OpenAI HTTP)

На **`LLM`** флаг **Remote** означает «поднять сервер, к которому подключаются клиенты» (порт и ключ в инспекторе).

На **`LLMAgent`** **Remote** + **host/port** — клиент к **совместимому с LLMUnity** серверу (тот же стек UndreamAI/llama), **не** сырой `https://api.openai.com`.

Для **OpenAI-compatible** (`/v1/chat/completions`) используйте раздел 4.

---

## 4. OpenAI-совместимый API (замена или дополнение локалке)

1. **Create → CoreAI → LLM → OpenAI-compatible HTTP** — ScriptableObject.
2. Заполните **Api Base Url** (без слэша в конце), например:
   - `https://api.openai.com/v1`
   - `http://127.0.0.1:1234/v1` (типично LM Studio)
3. **Api Key** — для OpenAI обязателен; для локального прокси часто пустой.
4. **Model** — имя модели на стороне сервера (`gpt-4o-mini`, `qwen2.5-7b-instruct`, …).
5. Включите **Use Open Ai Compatible Http**.
6. Перетащите asset в **`CoreAILifetimeScope` → Open Ai Http Llm Settings**.

Тогда **`ILlmClient` = OpenAiChatLlmClient**; `LLMAgent` на сцене для вызовов ядра **не используется** (можно оставить выключенным).

**Важно:** вызовы идут с **главного потока** Unity (как и LLMUnity-адаптер). Ключ не храните в публичном репозитории.

---

## 5. Системные промпты по умолчанию

Цепочка: манифест (если задан) → `Resources/AgentPrompts/System` → **встроенные тексты** в `BuiltInDefaultAgentSystemPromptProvider` / `BuiltInAgentSystemPromptTexts` (уже подключены в `RegisterAgentPrompts`).

Роли: **Creator, Analyzer, Programmer, AINpc, CoreMechanicAI, PlayerChat** — см. тесты `AgentRolesAndPromptsTests`.

---

## 6. Чек-лист перед релизом

- [ ] Выбран один основной профиль модели (рекомендуется **0.8B** в билде).
- [ ] Для API: asset OpenAI, ключ не в git, HTTPS для продакшена.
- [ ] Прогнаны EditMode-тесты агентов; в Play — smoke-тест чата и оркестратора.
- [ ] **Num GPU Layers** и **context size** согласованы с минимальной спецификацией целевых машин.

---

## 7. Play Mode тесты (рантайм в редакторе)

**Как тестировать поведение end-to-end:** (1) **Игра:** Play Mode, фильтр консоли по `[Llm]` — видно, что ушло в модель и что она вернула; по `[MessagePipe]` — что опубликовано в игру. (2) **Без железа/модели:** EditMode-тесты оркестратора и парсеров (`AgentMemoryEditModeTests`, `AgentRolesAndPromptsTests`, …) со **Stub**. (3) **Реальная модель:** Play Mode + сцена с **LLMAgent** → `AgentMemoryWithRealModelPlayModeTests.LlmUnity_…` (или HTTP → env + `OpenAiHttp_…`). (4) **Регрессия промптов:** после смены system/user шаблонов прогоните соответствующие EditMode-тесты.

Сборка **`CoreAI.PlayModeTests`** (в текущей конфигурации Unity часть тестов с `[UnityTest]` также видна в **EditMode**-прогоне Test Runner — ориентируйтесь на полное имя класса):

| Тест | Смысл |
|------|--------|
| `AiOrchestratorAllRolesPlayModeTests` | Для **каждой** встроенной роли оркестратор с **StubLlmClient** публикует `ApplyAiGameCommand` (контур без реальной модели). |
| `OpenAiLmStudioPlayModeTests` | Один реальный вызов `OpenAiChatLlmClient` к вашему серверу. Без переменных окружения помечается **Ignored**. |
| `AgentMemoryWithRealModelPlayModeTests` | Память Creator: HTTP (env) или **LLMUnity** при открытой сцене с **LLMAgent** и настроенной моделью; иначе **Ignored**. |

**LM Studio / OpenAI-compatible (PowerShell, перед запуском Play Mode Tests):**

```powershell
$env:COREAI_OPENAI_TEST_BASE = "http://<LM_STUDIO_HOST>:1234/v1"
$env:COREAI_OPENAI_TEST_MODEL = "<id из GET http://<LM_STUDIO_HOST>:1234/v1/models>"
# при необходимости:
# $env:COREAI_OPENAI_TEST_API_KEY = "..."
```

Затем **Window → General → Test Runner → PlayMode** → запуск сборки **CoreAI.PlayModeTests**.

**Важно:** базовый URL должен заканчиваться на **`/v1`** (как у LM Studio OpenAI-совместимого API).

---

## 8. Programmer и Lua (исполнение в рантайме)

- Оркестратор публикует **`AiEnvelope`** с **`JsonPayload`** = сырой ответ LLM и полями **`SourceRoleId`**, **`SourceTaskHint`**, **`LuaRepairGeneration`**.
- **`LuaAiEnvelopeProcessor`** (Core) + **`AiGameCommandRouter`**: из конверта извлекается Lua (fenced-блок lua или JSON **ExecuteLua**) и выполняется в **`SecureLuaEnvironment`** с API **`report`**, **`add`** (см. `LoggingLuaRuntimeBindings`).
- Успех / ошибка публикуются как **`LuaExecutionSucceeded`** / **`LuaExecutionFailed`**. При ошибке и роли **Programmer** оркестратор вызывается снова с **`lua_error`** / **`fix_this_lua`** в user payload (до **4** поколений ремонта).
- **EditMode:** `LuaAiEnvelopeProcessorEditModeTests`, `AiLuaPayloadParserEditModeTests`, `ProgrammerLuaPipelineEditModeTests`.
- В примере игры: **`CoreAiLuaHotkey`** на объекте с **`ExampleRogueliteEntry`** — клавиша **F9** ставит задачу Programmer.
