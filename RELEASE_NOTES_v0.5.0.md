# Release v0.5.0 - LLM Response Validation + GameConfig System

## Summary

**Major Release**: Добавлена **валидация ответов LLM** для каждой роли AI-агентов + **универсальная система конфигов** позволяющая AI читать/менять параметры игры через function calling.

## What Changed

### Code

#### Новые классы политик валидации (7 файлов)

- ✨ **ProgrammerResponsePolicy** — проверка Lua кода или JSON с `execute_lua`
- ✨ **CoreMechanicResponsePolicy** — проверка JSON с числами (крафт, лут)
- ✨ **CreatorResponsePolicy** — проверка JSON объектов (команды мира)
- ✨ **AnalyzerResponsePolicy** — проверка JSON с метриками/рекомендациями
- ✨ **AINpcResponsePolicy** — мягкая валидация (JSON или текст)
- ✨ **PlayerChatResponsePolicy** — без валидации (свободный текст)
- ✨ **CompositeRoleStructuredResponsePolicy** — маршрутизация по roleId

#### GameConfig Infrastructure (5 файлов CoreAI + 1 файл CoreAIUnity)

- ✨ **IGameConfigStore** — универсальный интерфейс (load/save JSON по ключу)
- ✨ **GameConfigTool** — ILlmTool для AI function calling (read/update)
- ✨ **GameConfigPolicy** — контроль доступа ролей к конфигам
- ✨ **GameConfigLlmTool** — обёртка для MEAI function calling
- ✨ **NullGameConfigStore** — заглушка по умолчанию
- ✨ **UnityGameConfigStore** — реализация на ScriptableObject (CoreAIUnity)

#### Обновлённые файлы

- ✅ **CorePortableInstaller.cs** — регистрация CompositePolicy + GameConfig
- ✅ **CoreAILifetimeScope.cs** — регистрация UnityGameConfigStore
- ✅ **AiOrchestrator.cs** — уже поддерживает retry при провале валидации

### Packages

- ✅ **com.nexoider.coreai**: 0.3.0 → **0.5.0**
- ✅ **com.nexoider.coreaiunity**: 0.3.0 → **0.5.0**

### Tests

- ✨ **RoleStructuredResponsePolicyEditModeTests.cs** — 20 тестов
- ✨ **GameConfigEditModeTests.cs** — 9 тестов (policy, read, update, round-trip)
- ✨ **GameConfigPlayModeTests.cs** — 3 теста (AI read/modify/write)
- ✨ **AnalyzerEditModeTests.cs** — 10 тестов (prompts, telemetry, orchestrator)
- **Итого: 42 новых теста**

### Documentation

- ✅ **TODO.md** — задачи #2, #3, #4, #5 отмечены выполненными
- ✅ **CHANGELOG.md** (оба пакета) — секции v0.5.0
- ✅ **package.json** (оба пакета) — версии обновлены
- ✅ **AI_AGENT_ROLES.md** — новая секция §8 про валидацию
- ✅ **GAME_CONFIG_GUIDE.md** — полная инструкция по конфигам
- ✅ **RELEASE_NOTES_v0.5.0.md** — этот файл

## How It Works

### LLM Response Validation

#### Before (NoOp Policy)
```csharp
public bool ShouldValidate(string roleId) => false; // Никогда не проверяет
```

#### After (Role-Specific Policies)
```csharp
// ProgrammerResponsePolicy
public bool ShouldValidate(string roleId) => roleId == "Programmer";
public bool TryValidate(string roleId, string rawContent, out string failureReason)
{
    // Проверяет: ```lua ... ``` ИЛИ {"execute_lua": "..."}
}
```

#### Retry Flow
1. LLM возвращает ответ
2. `CompositeRoleStructuredResponsePolicy` проверяет формат по roleId
3. При провале → `AiOrchestrator` делает **один повторный запрос** с подсказкой
4. Метрика `RecordStructuredRetry` логируется

### GameConfig System

#### Architecture
```
CoreAI (универсальный):           Ваша игра:
┌─────────────────────┐          ┌──────────────────────┐
│ IGameConfigStore    │◄─────────│ UnityGameConfigStore │
│ GameConfigTool      │          │ ScriptableObject     │
│ GameConfigPolicy    │          │ ConfigInstaller      │
└─────────────────────┘          └──────────────────────┘
```

#### AI Flow
1. AI вызывает `game_config(action="read")` → получает JSON
2. AI модифицирует JSON
3. AI вызывает `game_config(action="update", content=newJson)` → сохраняется
4. Игра использует обновлённые значения

## Benefits

### Validation
1. **Автоматическая коррекция**: Модель получает шанс исправить формат
2. **Ролевая специфика**: Каждая роль проверяет свой ожидаемый формат
3. **Observability**: failureReason логируется через метрики
4. **Расширяемость**: `RegisterPolicy()` для кастомных ролей

### GameConfig
1. **Game-agnostic**: CoreAI не знает про игровые параметры
2. **Универсальность**: Любой движок может реализовать `IGameConfigStore`
3. **Безопасность**: `GameConfigPolicy` контролирует доступ ролей
4. **MEAI integration**: AI использует function calling для чтения/записи

## Breaking Changes

- ❌ **Нет**. Обратная совместимость сохранена.
- ✅ `NoOpRoleStructuredResponsePolicy` и `NullGameConfigStore` остаются как fallback

## Testing

### Запуск тестов

```bash
# Unity Test Runner
EditMode: RoleStructuredResponsePolicyEditModeTests (20 тестов)
EditMode: GameConfigEditModeTests (9 тестов)
EditMode: AnalyzerEditModeTests (10 тестов)
PlayMode: GameConfigPlayModeTests (3 теста)
```

## Release Date

2026-04-05

## Notes

Это **feature release** (0.3.0 → 0.5.0) добавляет:
- Валидацию ответов LLM для всех встроенных ролей
- Универсальную систему конфигов с AI function calling
- 42 новых теста + полная документация

Следующие шаги (TODO):
- Multi-agent workflow (TODO #6)
- Analyzer реальная телеметрия
- AINpc/PlayerChat тесты
