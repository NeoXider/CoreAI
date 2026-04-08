# World Commands — управление миром из Lua (рантайм)

Цель: позволить роли **Programmer** (и в перспективе Creator) **безопасно** менять мир во время игры: спавнить/перемещать/включать объекты, переключать сцены, и т.д.

Ключевая идея: Lua **не** трогает Unity напрямую. Lua вызывает **whitelist API**, который публикует типизированную команду в шину. Unity‑слой на **главном потоке** выполняет действие.

---

## 1. Поток данных (канон)

1. LLM → `ApplyAiGameCommand` с `CommandTypeId = AiEnvelope`
2. `LuaAiEnvelopeProcessor` извлекает Lua и исполняет в `SecureLuaEnvironment`
3. Lua вызывает `coreai_world_*` → публикуется `ApplyAiGameCommand` с `CommandTypeId = WorldCommand`
4. `AiGameCommandRouter` на main thread вызывает `ICoreAiWorldCommandExecutor.TryExecute(...)`

Это сохраняет:
- **main thread safety** для Unity
- **контроль/логирование** через MessagePipe и `traceId` (когда он есть)
- расширяемость через интерфейсы и реестры

---

## 2. Lua API (whitelist)

Доступные функции (встроенный набор):

- `coreai_world_spawn(prefabKeyOrName, targetName, x, y, z) -> bool`
- `coreai_world_move(targetName, x, y, z)`
- `coreai_world_destroy(targetName)`
- `coreai_world_set_active(targetName, active)`
- `coreai_world_load_scene(sceneName)`
- `coreai_world_reload_scene()`
- `coreai_world_play_animation(targetName, animationName)`
- `coreai_world_list_animations(targetName)`
- `coreai_world_show_text(targetName, textToDisplay)`
- `coreai_world_apply_force(targetName, fx, fy, fz)`
- `coreai_world_spawn_particles(targetName, prefabKeyOrName)`
- `coreai_world_list_objects(searchPattern)`

### Рекомендация по ключам

- **prefabKeyOrName**: лучше использовать **GUID‑строку** (или другое стабильное id) либо имя префаба из справочника.
- **targetName**: строка с именем объекта в сцене (Unity GameObject name). Все команды резолвят объект динамически по `GameObject.Find()`.

---

## 3. Whitelist ресурсов: спавн префабов

Спавн делается только через `CoreAiPrefabRegistryAsset` (ScriptableObject‑реестр).

### Как подключить

1. Создайте asset: **Create → CoreAI → World → Prefab Registry**
2. Заполните `Key` (GUID строкой) и/или `Name`, назначьте `Prefab`
3. На `CoreAILifetimeScope` назначьте поле **World Prefab Registry**

Если реестр не назначен — спавн будет **отклонён**.

---

## 4. Расширение возможностей (проектный слой)

### 4.1 Добавить свои команды мира

Варианты:
- **A (рекомендуется)**: расширить `ICoreAiWorldCommandExecutor` своей реализацией (или обёрткой‑композицией), добавить новые `action` в JSON envelope и обработку на main thread.
- **B**: отдельный `WorldCommandRouter` на MessagePipe, который подписывается на `ApplyAiGameCommand` и обрабатывает только `WorldCommand` (если хотите полностью изолировать от `AiGameCommandRouter`).

### 4.2 «Рефлексия» и изменение компонентов

Рефлексия напрямую из Lua — опасна. Если требуется «менять данные объекта», делайте это через:
- allowlist типов/полей/методов
- отдельную политику `IWorldReflectionPolicy` (проектный слой)
- набор строго типизированных команд (например `set_transform`, `add_force`, `set_anim_trigger`)

---

## 5. Дефолты vs настройка

**По умолчанию** в шаблоне:
- World Commands включены (Lua API зарегистрирован).
- Спавн требует явно назначенный `CoreAiPrefabRegistryAsset`.

**Настраиваемо** через инспектор `CoreAILifetimeScope`:
- назначение/отключение реестра префабов
- замена/обёртка исполнителя команд

---

## 6. Тесты

- EditMode: `WorldCommandLuaBindingsEditModeTests` — проверяет, что Lua публикует `WorldCommand` с корректным JSON.
- PlayMode (рекомендуется добавить в тайтле): smoke‑тест сцены, где `coreai_world_spawn` создаёт объект из реестра.

