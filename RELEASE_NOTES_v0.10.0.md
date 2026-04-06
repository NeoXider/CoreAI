# Release v0.10.0 - WorldCommand Tool Calling + Engine-Agnostic Pattern

## Summary

**Major Release**: Добавлен **WorldCommand как MEAI tool call** — LLM может управлять миром через function calling (spawn, move, destroy, list_objects, show_text и др.) + внедрён **Engine-Agnostic Pattern** для создания движок-независимых инструментов.

## What Changed

### Code

#### WorldCommand Tool Calling (4 новых файла)

- ✨ **WorldTool.cs** — MEAI AIFunction для управления миром (CoreAiUnity)
- ✨ **WorldLlmTool.cs** — ILlmTool обёртка (CoreAiUnity)
- ✨ **WorldToolEditModeTests.cs** — 13 EditMode тестов
- ✨ **WorldCommandPlayModeTests.cs** — 3 PlayMode теста

#### Поддерживаемые WorldCommand actions

| Action | Описание | Параметры |
|--------|----------|-----------|
| `spawn` | Создать объект | `prefabKey`, `x`, `y`, `z`, `instanceId` |
| `move` | Переместить объект | `instanceId` или `targetName`, `x`, `y`, `z` |
| `destroy` | Удалить объект | `instanceId` или `targetName` |
| `list_objects` | Получить список объектов | `stringValue` (search pattern, опционально) |
| `load_scene` | Загрузить сцену | `stringValue` (имя сцены) |
| `reload_scene` | Перезагрузить сцену | — |
| `bind_by_name` | Привязать по имени | `targetName`, `instanceId` |
| `set_active` | Включить/выключить | `instanceId` или `targetName` |
| `show_text` | Показать текст | `targetName`, `stringValue` |
| `apply_force` | Применить силу | `instanceId` или `targetName`, `x`, `y`, `z` |
| `spawn_particles` | Создать частицы | `instanceId` или `targetName`, `stringValue` |

#### Новые возможности

- ✅ **`list_objects`** — получить список всех объектов в иерархии сцены
  - Возвращает имя, позицию, активность, тег, слой, количество детей
  - Поддержка поиска по имени (search pattern)
- ✅ **`targetName` для всех команд** — работа с объектами по имени (альтернатива instanceId)
  - Сначала ищет в `_instances` по instanceId, затем `GameObject.Find` по targetName

#### Удалённые инструменты

- ❌ **`play_sound`** — удалён (будет переделан через абстрактный интерфейс)
- ❌ **`play_animation`** — удалён (будет переделан через абстрактный интерфейс)

### Architecture: Engine-Agnostic Pattern

#### Новая документация

- 📖 **ENGINE_AGNOSTIC_TOOLS.md** — полный паттерн создания engine-agnostic инструментов
  - Двухуровневая архитектура (Абстракция → Реализация)
  - Примеры кода для каждого уровня
  - Как добавить новый инструмент (пошаговая инструкция)
  - Пример для другого движка (Unreal Engine)

#### Структура паттерна

```
CoreAI (движок-независимое ядро):
├── ILlmTool.cs              # Базовый интерфейс
├── LlmToolBase.cs           # Базовый класс с JsonParams()
└── IWorldCommandExecutor.cs # Абстрактный интерфейс

CoreAiUnity (Unity-специфичная реализация):
├── WorldTool.cs             # MEAI AIFunction
├── WorldLlmTool.cs          # ILlmTool обёртка
└── CoreAiWorldCommandExecutor.cs  # Исполнитель
```

#### Преимущества

- ✅ **Портируемость** — новый движок = только реализация интерфейсов
- ✅ **Тестируемость** — ядро тестируется с моками
- ✅ **Гибкость** — каждый движок делает по-своему, API одинаковый
- ✅ **Документируемость** — интерфейс = контракт для всех движков
- ✅ **Совместимость** — промпты LLM работают на любом движке

### Debug Logging System

#### Новые настройки в CoreAISettingsAsset

- ✅ **`LogLlmInput`** — логирует входящие промпты (system, user) и инструменты
- ✅ **`LogLlmOutput`** — логирует исходящие ответы модели и результаты tool calls
- ✅ **`EnableHttpDebugLogging`** — логирует сырые HTTP request/response JSON
- ✅ Видны в Unity Inspector в секции "🔧 Отладка"

### Bug Fixes

#### Tool Calling Fixes

- ✅ **Tool results не отправлялись модели** — исправлено извлечение из `msg.Contents` коллекции
- ✅ **LM Studio 400 Bad Request** — добавлен обязательный `tool_call_id` для tool messages
- ✅ **Memory append добавлял значение 3 раза** — добавлена идемпотентность
- ✅ **Write test не проходил** — исправлен hint чтобы не сбивать модель с толку

#### Test Fixes

- ✅ Убраны `LogAssert.Expect` для ошибок подключения из PlayMode тестов
- ✅ Исправлены hint'ы для memory tool тестов
- ✅ Добавлен `WorldExecutor` в `TestAgentSetup`

### Tests

- ✨ **WorldToolEditModeTests.cs** — 13 тестов (spawn, move, destroy, list_objects, targetName)
- ✨ **WorldCommandPlayModeTests.cs** — 3 теста (spawn, move, list_objects)
- **Итого: 16 новых тестов**

### Documentation

- 📖 **ENGINE_AGNOSTIC_TOOLS.md** — новая документация паттерна
- 📖 **TOOL_CALL_SPEC.md** — обновлена с WorldCommand и Engine-Agnostic секцией
- 📖 **CHANGELOG.md** — обновлена секция v0.10.0
- 📖 **TODO.md** — обновлена с новыми задачами

### Packages

- ✅ **com.nexoider.coreai**: 0.9.0 → **0.10.0**
- ✅ **com.nexoider.coreaiunity**: 0.9.0 → **0.10.0**

## Breaking Changes

- ❌ Удалены `play_sound` и `play_animation` из WorldCommand (будут переделаны)
- ⚠️ `WorldTool` и `WorldLlmTool` перенесены из CoreAI в CoreAiUnity

## Migration Guide

### Если использовали play_sound/play_animation

Временно удалены. Будут добавлены обратно через абстрактный интерфейс `IAudioController` и `IAnimationController`.

### Если использовали WorldTool из CoreAI

Обновите namespace:
```csharp
// Было:
using CoreAI.Ai;

// Стало:
using CoreAI.Infrastructure.Llm;
```

## Known Issues

- ⏳ `show_text` — заглушка, будет реализована с анимацией уведомления (2 секунды)
- ⏳ `apply_force`, `spawn_particles` — заглушки, ждут реализации

## Contributors

- AI-assisted development with Qwen Code
