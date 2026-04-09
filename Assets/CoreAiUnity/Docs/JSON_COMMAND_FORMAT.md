# 📋 Формат JSON команд для каждой роли

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Этот документ описывает **точный формат** JSON команд (tool calls), которые каждая роль агента отправляет и принимает. Используйте его как справочник при создании промптов и отладке.

---

## Общий формат Tool Call

Все роли используют **единый MEAI формат**:

```json
{
  "name": "tool_name",
  "arguments": {
    "param1": "value1",
    "param2": "value2"
  }
}
```

> [!IMPORTANT]
> Модель должна возвращать **ровно этот формат**. Обёртки ````json...````, `<think>...</think>`, или формат `{"tool": "name"}` автоматически обрабатываются парсером, но рекомендуется указывать правильный формат в промпте.

---

## 1. 🧠 Memory Tool (все роли с памятью)

**Роли:** Creator, Analyzer, Programmer, CoreMechanicAI, кастомные агенты с `.WithMemory()`

### Запись (перезаписать)
```json
{
  "name": "memory",
  "arguments": {
    "action": "write",
    "content": "Wave 7: difficulty increased, EliteBoss spawned at (50,0,50)"
  }
}
```

### Добавление (дописать)
```json
{
  "name": "memory",
  "arguments": {
    "action": "append",
    "content": "Craft#3: Steel + Ice Crystal → Frostblade damage:55 ice:20"
  }
}
```

### Очистка
```json
{
  "name": "memory",
  "arguments": {
    "action": "clear"
  }
}
```

**Параметры:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|:------------:|----------|
| `action` | string | ✅ | `write` / `append` / `clear` |
| `content` | string | ✅ (кроме clear) | Текст для записи |

**Результат:** `"Memory saved"` / `"Memory appended"` / `"Memory cleared"`

---

## 2. 📜 Execute Lua Tool (Programmer)

**Роли:** Programmer (основная), Creator (при необходимости)

### Выполнение Lua кода
```json
{
  "name": "execute_lua",
  "arguments": {
    "code": "local dmg = 45 + math.random(10)\nreport('damage: ' .. dmg)"
  }
}
```

**Параметры:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|:------------:|----------|
| `code` | string | ✅ | Lua код для исполнения в песочнице |

**Доступные Lua API:**

| Функция | Описание |
|---------|----------|
| `report(string)` | Отправить результат обратно |
| `add(a, b)` | Сложение двух чисел |
| `coreai_world_spawn(key, name, x, y, z)` | Создать объект |
| `coreai_world_move(name, x, y, z)` | Переместить объект |
| `coreai_world_destroy(name)` | Удалить объект |
| `coreai_world_set_active(name, active)` | Вкл/выкл объект |
| `coreai_world_load_scene(sceneName)` | Загрузить сцену |
| `coreai_world_reload_scene()` | Перезагрузить сцену |
| `coreai_world_play_animation(name, anim)` | Проиграть анимацию |
| `coreai_world_list_animations(name)` | Список анимаций |
| `coreai_world_show_text(name, text)` | Показать текст |
| `coreai_world_apply_force(name, fx, fy, fz)` | Приложить силу |
| `coreai_world_spawn_particles(name, pfx)` | Создать частицы |
| `coreai_world_list_objects(pattern)` | Поиск объектов |

**Результат при успехе:** Вывод `report(...)` или `"Lua executed successfully"`
**Результат при ошибке:** `"[Error] MoonSharp runtime: <описание ошибки>"`

### Пример многострочного Lua

```json
{
  "name": "execute_lua",
  "arguments": {
    "code": "-- Скрипт спавна засады\ncoreai_world_spawn('Enemy', 'ambush_1', 10, 0, 5)\ncoreai_world_spawn('Enemy', 'ambush_2', -10, 0, 5)\ncoreai_world_spawn('EliteBoss', 'ambush_boss', 0, 0, 15)\nreport('Ambush setup: 2 enemies + 1 boss')"
  }
}
```

---

## 3. 🌍 World Command Tool (Creator / Designer)

**Роли:** Creator, Designer AI, кастомные агенты с WorldTool

### Spawn — создать объект
```json
{
  "name": "world_command",
  "arguments": {
    "action": "spawn",
    "prefabKey": "Enemy",
    "targetName": "enemy_wave7_1",
    "x": 10,
    "y": 0,
    "z": 5
  }
}
```

### Move — переместить объект
```json
{
  "name": "world_command",
  "arguments": {
    "action": "move",
    "targetName": "Player",
    "x": 100,
    "y": 0,
    "z": 50
  }
}
```

### Destroy — удалить объект
```json
{
  "name": "world_command",
  "arguments": {
    "action": "destroy",
    "targetName": "OldBuilding"
  }
}
```

### List Objects — список объектов
```json
{
  "name": "world_command",
  "arguments": {
    "action": "list_objects"
  }
}
```

### List Objects — поиск по имени
```json
{
  "name": "world_command",
  "arguments": {
    "action": "list_objects",
    "stringValue": "enemy"
  }
}
```

### Load Scene — загрузить сцену
```json
{
  "name": "world_command",
  "arguments": {
    "action": "load_scene",
    "stringValue": "Level_2"
  }
}
```

### Reload Scene — перезагрузить сцену
```json
{
  "name": "world_command",
  "arguments": {
    "action": "reload_scene"
  }
}
```

### Set Active — включить/выключить объект
```json
{
  "name": "world_command",
  "arguments": {
    "action": "set_active",
    "targetName": "SecretDoor"
  }
}
```

### Play Animation
```json
{
  "name": "world_command",
  "arguments": {
    "action": "play_animation",
    "targetName": "Boss1",
    "animationName": "rage_attack"
  }
}
```

### List Animations
```json
{
  "name": "world_command",
  "arguments": {
    "action": "list_animations",
    "targetName": "Boss1"
  }
}
```

### Show Text — показать текст
```json
{
  "name": "world_command",
  "arguments": {
    "action": "show_text",
    "targetName": "Player",
    "textToDisplay": "Quest completed! +500 XP"
  }
}
```

### Apply Force — приложить силу
```json
{
  "name": "world_command",
  "arguments": {
    "action": "apply_force",
    "targetName": "Boulder",
    "x": 0,
    "y": 100,
    "z": 50
  }
}
```

### Spawn Particles — создать эффект частиц
```json
{
  "name": "world_command",
  "arguments": {
    "action": "spawn_particles",
    "targetName": "Enemy1",
    "stringValue": "ExplosionVFX"
  }
}
```

**Полная таблица параметров:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| `action` | string | Тип операции (см. ниже) |
| `prefabKey` | string | Ключ префаба из PrefabRegistryAsset |
| `targetName` | string | Имя GameObject в сцене |
| `x`, `y`, `z` | float | Координаты (позиция или сила) |
| `stringValue` | string | Дополнительный строковый параметр |
| `animationName` | string | Имя анимации |
| `textToDisplay` | string | Текст для отображения |

**Все action:**

| Action | Описание | Обязательные параметры |
|--------|----------|----------------------|
| `spawn` | Создать объект | `prefabKey`, `targetName`, `x`, `y`, `z` |
| `move` | Переместить | `targetName`, `x`, `y`, `z` |
| `destroy` | Удалить | `targetName` |
| `list_objects` | Список объектов | — (опционально `stringValue`) |
| `load_scene` | Загрузить сцену | `stringValue` |
| `reload_scene` | Перезагрузить | — |
| `set_active` | Вкл/выкл | `targetName` |
| `play_animation` | Анимация | `targetName`, `animationName` |
| `list_animations` | Список анимаций | `targetName` |
| `show_text` | Показать текст | `targetName`, `textToDisplay` |
| `apply_force` | Приложить силу | `targetName`, `x`, `y`, `z` |
| `spawn_particles` | Частицы | `targetName`, `stringValue` |

---

## 4. 🎒 Get Inventory Tool (Merchant / Shopkeeper)

**Роли:** Merchant NPC, любой агент с InventoryTool

### Запрос инвентаря
```json
{
  "name": "get_inventory",
  "arguments": {}
}
```

**Результат (пример):**
```json
[
  {"name": "Iron Sword", "type": "weapon", "quantity": 3, "price": 50},
  {"name": "Steel Axe", "type": "weapon", "quantity": 1, "price": 100},
  {"name": "Health Potion", "type": "consumable", "quantity": 10, "price": 25},
  {"name": "Flame Blade", "type": "weapon", "quantity": 1, "price": 250}
]
```

---

## 5. ⚙️ Game Config Tool (Creator / Designer)

**Роли:** Creator, Designer AI

### Чтение конфига
```json
{
  "name": "game_config",
  "arguments": {
    "action": "read"
  }
}
```

### Обновление конфига
```json
{
  "name": "game_config",
  "arguments": {
    "action": "update",
    "content": "{\"wave_difficulty\": 1.5, \"spawn_rate\": 2.0, \"boss_hp_multiplier\": 1.3}"
  }
}
```

**Параметры:**

| Параметр | Тип | Обязательный | Описание |
|----------|-----|:------------:|----------|
| `action` | string | ✅ | `read` / `update` |
| `content` | string | ✅ (для update) | JSON с новыми значениями |

---

## 6. 🎭 Scene Tool (все агенты)

**Роли:** Все агенты (доступен в PlayMode)

### Найти объекты
```json
{
  "name": "scene_tool",
  "arguments": {
    "action": "find_objects",
    "pattern": "Enemy*"
  }
}
```

### Получить иерархию
```json
{
  "name": "scene_tool",
  "arguments": {
    "action": "get_hierarchy"
  }
}
```

### Получить трансформ
```json
{
  "name": "scene_tool",
  "arguments": {
    "action": "get_transform",
    "targetName": "Player"
  }
}
```

### Установить трансформ
```json
{
  "name": "scene_tool",
  "arguments": {
    "action": "set_transform",
    "targetName": "Player",
    "position": {"x": 10, "y": 0, "z": 5}
  }
}
```

---

## 7. 📸 Camera Tool (все агенты)

**Роли:** Все агенты (доступен в PlayMode)

### Сделать скриншот
```json
{
  "name": "camera_snapshot",
  "arguments": {}
}
```

**Результат:** Base64 JPEG строка (для Vision анализа моделью)

---

## 8. ⚡ Action / Event Tool (кастомные агенты)

**Роли:** Кастомные агенты через `AgentBuilder.WithAction()` / `WithEventTool()`

### Формат зависит от сигнатуры C# метода

```csharp
// C# определение:
.WithAction("heal_player", "Heal the player fully",
    (int amount) => player.Heal(amount))
```

```json
// JSON вызов моделью:
{
  "name": "heal_player",
  "arguments": {
    "amount": 100
  }
}
```

```csharp
// C# определение:
.WithEventTool("trigger_boss_phase2", "Trigger boss phase 2")
```

```json
// JSON вызов моделью:
{
  "name": "trigger_boss_phase2",
  "arguments": {}
}
```

---

## 9. Матрица: какие tool доступны каким ролям

| Роль | memory | execute_lua | world_command | get_inventory | game_config | scene_tool | camera | custom |
|------|:------:|:-----------:|:-------------:|:-------------:|:-----------:|:----------:|:------:|:------:|
| **Creator** | ✅ write | ⚡ опц. | ✅ | ❌ | ✅ | ✅ | ✅ | ✅ |
| **Programmer** | ✅ append | ✅ | ✅ (через Lua) | ❌ | ❌ | ✅ | ✅ | ✅ |
| **Analyzer** | ✅ append | ❌ | ❌ | ❌ | ✅ read | ✅ | ✅ | ✅ |
| **CoreMechanicAI** | ✅ append | ❌ | ❌ | ❌ | ✅ read | ❌ | ❌ | ✅ |
| **AINpc** | ❌ | ❌ | ❌ | ⚡ опц. | ❌ | ❌ | ❌ | ✅ |
| **PlayerChat** | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |
| **Merchant** | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ | ❌ | ✅ |
| **Кастомный** | ⚡ | ⚡ | ⚡ | ⚡ | ⚡ | ⚡ | ⚡ | ⚡ |

**Легенда:** ✅ по умолчанию | ❌ отключено | ⚡ можно добавить через AgentBuilder

---

## 10. Обработка ошибок Tool Call

### Tool Call Retry (до 3 попыток)

```
Попытка 1: Модель → {"tool": "memory", "action": "write"}  ← Неправильный формат!
  ↓
Система: "ERROR: Tool call not recognized. Use this format: {"name": "...", "arguments": {...}}"
  ↓
Попытка 2: Модель → {"name": "memory", "arguments": {"action": "write", "content": "..."}}  ✅
```

### Robust Parsing

CoreAI автоматически обрабатывает:
- ✅ Забытые обратные кавычки: ` ```json {"name":...}``` `
- ✅ Теги размышления: `<think>I should call...</think>{"name":...}`
- ✅ Старый формат: `{"tool": "memory"}` конвертируется в `{"name": "memory"}`

---

> 📖 **Связанные документы:**
> - [COMMAND_FLOW_DIAGRAM.md](COMMAND_FLOW_DIAGRAM.md) — диаграмма потока команды
> - [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) — спецификация tool calling
> - [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — конструктор агентов
