# CoreAI: Руководство по ScriptableObject (SO)

В архитектуре **CoreAI** активно используются паттерны на базе `ScriptableObject` для хранения конфигураций, настройки AI моделей, правил роутинга и префабов сцены. 

Это позволяет отделить данные от логики (Data-Driven Design), избежать god-объектов в Monobehaviour и удобно редактировать баланс прямо в инспекторе.

---

## 🛠️ Основные (Системные) ScriptableObjects

Эти SO необходимы для работы ядра фреймворка. При загрузке плагина в Unity они **автоматически создаются** с дефолтными значениями, если отсутствуют (смотри `CoreAI/Setup/Create Default Assets`).

### 1. `CoreAISettingsAsset`
**Назначение:** Глобальный конфигуратор LLM, таймаутов, fallback-моделей и логов. Единственная точка входа (Singleton), подхватывается автоматически из `Resources/CoreAISettings.asset`.
- **Путь:** `Assets/Resources/CoreAISettings.asset` (обязательно в Resources / или назначен в Scope).
- **За что отвечает:**
  - Какой бэкенд использовать (`LlmUnity` vs `OpenAiHttp` vs `Auto`).
  - API Ключ, URL, Название модели.
  - Управление режимом размышлений (`Enable Reasoning / Thinking mode`).
  - Управление fallback логикой (оффлайн режим).
- **Меню Unity:** Можно быстро открыть через `CoreAI -> Settings`.

### 2. `AgentPromptsManifest`
**Назначение:** Хранилище всех стартовых и системных промптов (System Prompts) для каждого агента по его `RoleId`.
- **Где найти:** `Assets/CoreAiUnity/Settings/AgentPromptsManifest.asset`
- **За что отвечает:**
  - Настройка личностей (Assistant, Programmer, Storyteller, UI Designer).
  - На какие инструменты и правила должен опираться агент.
- **Интеграция:** Привязывается к `CoreAILifetimeScope` (Dependency Injection).

### 3. `LlmRoutingManifest`
**Назначение:** Роутер бэкендов в зависимости от роли агента (Backend-per-Task).
- **Где найти:** `Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset`
- **За что отвечает:**
  - Если агент `Writer` → используем локальную Llama-3-8b (`LlmUnity`).
  - Если агент `Coder` → направляем запрос к GPT-4o или Claude (`OpenAiHttp`).
  - Если агент не указан → используется дефолтный `Routing Profile`.

### 4. `CoreAiPrefabRegistryAsset`
**Назначение:** Каталог всех префабов (GameObjects/Units), которые LLM агент может спавнить через `WorldCommand` инструмент (например `spawn_entity`).
- **Где найти:** `Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset`
- **За что отвечает:**
  - Безопасный спавн (String Name -> Unity Prefab ресолвер). LLM никогда напрямую не загружает ресурсы, она использует ключи из этого реестра.
- **Интеграция:** Инжектируется в `PrefabRuleValidator` и `WorldLlmTool`.

### 5. `GameLogSettingsAsset`
**Назначение:** Тонкая настройка системы логирования внутри фреймворка.
- **Где найти:** `Assets/CoreAiUnity/Settings/GameLogSettings.asset`
- **За что отвечает:**
  - Включение/отключение конкретных фич (например отключить спам логов от `NavMesh` или детальный лог `LlmToolCalls`).

### 6. `AiPermissionsAsset`
**Назначение:** Контроль доступа к функциям API или внутриигровых компонентов (Permissions and Scopes).
- **Где найти:** `Assets/CoreAiUnity/Settings/AiPermissions.asset`
- **За что отвечает:** 
  - Какие модули доступны для AI в текущем контексте. Ограничивает область действия (например, запретить агенту управлять погодой в данже).

---

## 🗑️ Устаревшие (Deprecated) ScriptableObjects

Эти SO были заменены и оставлены только для обратной совместимости, **будут удалены в v1.0**.

### `OpenAiHttpLlmSettings`
- 🚫 **Статус:** Устарел.
- **Заменён на:** `CoreAISettingsAsset`.
- **Причина:** Создавал путаницу между конфигурацией локальной модели (`LLMUnity`) и удаленной API-модели. Теперь всё объединено в `CoreAISettingsAsset`.

---

## 💡 Пользовательские (Игровые) ScriptableObjects

Помимо встроенных, вы можете создавать собственные SO для передачи в агента через систему `IGameConfigStore`:
- Например `ItemConfig : ScriptableObject`, в котором хранятся статы оружия, цены.
- Вы можете передать этот SO в инструмент `GameConfigLlmTool`, и агент сможет прочитать эти статы и использовать в ответе.

---

## Как это загружается?
Плагин CoreAI использует атрибут `[InitializeOnLoadMethod]`. Это значит, что при первом импорте плагина (или перекомпиляции), скрипт `CoreAIBuildMenu` проверяет наличие `CoreAISettingsAsset` в папке `Resources`. 
Если его (или других системных SO) нет — они **автосоздаются** с безопасными дефолтными настройками, что исключает краши из-за `NullReferenceException`.
