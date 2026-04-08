# 🔍 CoreAI — Архитектурный Аудит

**Дата:** 2026-04-08 | **Версия проекта:** v0.12.0

---

## Содержание

1. [Общая архитектура](#1-общая-архитектура)
2. [🔴 Критические проблемы](#2--критические-проблемы)
3. [🟠 Архитектурные недоработки](#3--архитектурные-недоработки)
4. [🟡 Мёртвый и неиспользуемый код](#4--мёртвый-и-неиспользуемый-код)
5. [🔵 Тесты — пробелы и хрупкость](#5--тесты--пробелы-и-хрупкость)
6. [⚪ Мелкие замечания](#6--мелкие-замечания)
7. [✅ Что сделано хорошо](#7--что-сделано-хорошо)
8. [📋 Рекомендации по приоритетам](#8--рекомендации-по-приоритетам)

---

## 1. Общая архитектура

```
┌──────────────────────────────────────────────┐
│  CoreAI.Core  (netstandard / noEngineReferences)  │
│  ┌─────────────┐ ┌──────────────┐ ┌───────┐      │
│  │ Orchestrator│ │ AgentBuilder │ │ Tools │      │
│  │ ILlmClient  │ │ MemoryPolicy │ │ (MEAI)│      │
│  └─────────────┘ └──────────────┘ └───────┘      │
│  ILog  ·  CoreAISettings (static)  ·  ICodeRefiner│
│  IWorldCommandExecutor · IAudioController · ...   │
└───────────────┬──────────────────────────────────┘
                │ зависит (asmdef references)
┌───────────────▼──────────────────────────────────┐
│  CoreAiUnity  (Unity-specific implementation)     │
│  VContainer DI · UnityLog · IGameLogger           │
│  WorldTool · WorldLlmTool · CoreAiWorldExecutor   │
│  LlmUnity / OpenAI HTTP clients                   │
│  PlayMode & EditMode тесты                        │
└──────────────────────────────────────────────────┘
```

> [!NOTE]
> `CoreAI.Core` использует `noEngineReferences: true` — это хорошо. **Но** в нём находятся зависимости от `Microsoft.Extensions.AI`, `VContainer`, `MoonSharp` и `Newtonsoft.Json`, что делает его не полностью "чистым" портативным ядром.

---

## 2. 🔴 Критические проблемы

### 2.1 ✅ ~~Два параллельных логгера~~ — РЕШЕНО в v0.12.0

> [!NOTE]
> Эта проблема **исправлена**. Ниже — архитектура до и после.

**Было:** Два несвязанных логгера (`ILog` в Core и `IGameLogger` в Unity) с хрупким мостом.
**Стало:** Один `ILog` с тегами (`LogTag`), доступный и через DI, и через `Log.Instance`.

```
CoreAI.Core                          CoreAiUnity
┌───────────────────────┐            ┌──────────────────────┐
│ ILog (interface)      │            │ UnityLog : ILog      │
│  Debug(msg, tag)      │◄───────────│  MapTag → GameLogFeature│
│  Info(msg, tag)       │  implements│  → FilteringGameLogger│
│  Warn(msg, tag)       │            │  → UnityGameLogSink  │
│  Error(msg, tag)      │            └──────────────────────┘
│                       │
│ LogTag (constants)    │  DI: ILog → UnityLog (singleton)
│  Core, Llm, Lua,     │  Static: Log.Instance = same instance
│  Memory, Config, etc. │
│                       │
│ Log.Instance (static) │
│ NullLog (no-op)       │
└───────────────────────┘
```

**Что изменилось:**
- `ILog` расширен: 4 метода × 2 параметра (`message`, `tag`)
- `LogTag` — строковые константы подсистем, маппятся на `GameLogFeature` в Unity
- `CoreServicesInstaller` регистрирует `ILog` (DI) и устанавливает `Log.Instance` (статика)
- Все Tool-классы мигрированы на теги: `MemoryTool→Memory`, `LuaTool→Lua`, `GameConfigTool→Config`
- `IGameLogger` сохранён как внутренний интерфейс Unity-слоя

---

### 2.2 CoreAISettings — статический god-object

[CoreAISettings.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/CoreAISettings.cs) — класс с **~15 статическими свойствами**, которые "синхронизируются" из `CoreAISettingsAsset` в [CoreAILifetimeScope.cs:104–119](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs#L104-L119):

```csharp
CoreAI.CoreAISettings.MaxLuaRepairGenerations = settings.MaxLuaRepairGenerations;
CoreAI.CoreAISettings.MaxToolCallIterations = settings.MaxToolCallIterations;
CoreAI.CoreAISettings.MaxToolCallRetries = settings.MaxToolCallRetries;
// ... ещё 10 строк ручной синхронизации
```

**Проблемы:**
- Ручная синхронизация — хрупкая. При добавлении нового свойства в `CoreAISettingsAsset` легко забыть синхронизировать.
- Статические свойства **глобальны** — невозможно иметь два разных набора настроек (для тестов, для разных агентов).
- `AgentBuilder.Build()` напрямую читает `CoreAI.CoreAISettings.ContextWindowTokens` и `CoreAI.CoreAISettings.Temperature` — жёсткая связь.
- Нетестируемо: в EditMode тестах невозможно изолировать настройки.

**Рекомендация:** Заменить на интерфейс `ICoreAISettings` с DI-инъекцией. `CoreAISettingsAsset` реализует `ICoreAISettings`. `AgentBuilder` принимает `ICoreAISettings` через конструктор или через `Build(ICoreAISettings)`.

---

### 2.3 ✅ ~~Дублированный enum MemoryToolAction~~ — РЕШЕНО в v0.12.0

> [!NOTE]
> Эта проблема **исправлена**.

**Что сделано:**
- Создан единый файл `MemoryToolAction.cs` в `CoreAI.Ai` неймспейсе.
- Удалены дубликаты из `AgentBuilder.cs` и `AgentMemoryPolicy.cs`.
- Исправлен `AgentBuilder.WithMemory()`, который теперь корректно сохраняет `defaultAction`.
- Обновлён `AgentConfig.ApplyToPolicy()`, который теперь применяет `MemoryDefaultAction` к политике памяти через `policy.ConfigureRole()`.
- Добавлена проверка `HasMemoryTool()` перед отключением инструмента памяти.

---

## 3. 🟠 Архитектурные недоработки

### 3.1 Двойная обёртка World Tool (WorldTool + WorldLlmTool)

Для мирового инструмента существуют **два класса** с пересекающейся логикой:

| Класс | Роль | Размер |
|-------|------|--------|
| [WorldTool.cs](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Features/World/WorldTool.cs) | AIFunction-обёртка (252 строки, вся логика внутри) |
| [WorldLlmTool.cs](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Features/World/WorldLlmTool.cs) | ILlmTool-обёртка (вызывает WorldTool внутри) |

`WorldLlmTool` создаёт **новый** `WorldTool` при каждом вызове `CreateAIFunction()`:

```csharp
public AIFunction CreateAIFunction()
{
    var worldTool = new WorldTool(_executor); // новый инстанс каждый раз
    return worldTool.CreateAIFunction();
}
```

Это нарушает consistency: `MemoryLlmTool` и `LuaLlmTool` хранят логику внутри себя, а `WorldLlmTool` делегирует в `WorldTool`, который существует отдельно.

### 3.2 CoreAiWorldCommandEnvelope — переиспользование полей

[CoreAiWorldCommandEnvelope.cs](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Features/World/Infrastructure/CoreAiWorldCommandEnvelope.cs) — использует одни и те же поля для разных целей:

```csharp
// apply_force reuses px, py, pz for force vector:
public static CoreAiWorldCommandEnvelope ApplyForce(string instanceId, Vector3 force)
{
    // ... 
    px = force.x, // Reusing px, py, pz for a Vector3 generic param
    py = force.y,
    pz = force.z
```

Поля `px/py/pz` — это координаты спавна, но используются и для силы. Это **семантическая путаница**, которая приведёт к багам при расширении.

### 3.3 Два интерфейса WorldCommand — один мёртвый

В проекте существуют **два интерфейса** для world commands:

| Интерфейс | Пакет | Используется |
|-----------|-------|-------------|
| `IWorldCommandExecutor` | `CoreAI.Core` | ❌ Только объявление, 0 использований |
| `ICoreAiWorldCommandExecutor` | `CoreAiUnity` | ✅ 13 использований |

[IWorldCommandExecutor.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/World/IWorldCommandExecutor.cs) в CoreAI.Core — **мёртвый интерфейс**. Вся реальная работа идёт через `ICoreAiWorldCommandExecutor` из CoreAiUnity.

### 3.4 InGameLlmChatService — хрупкая склейка истории

[InGameLlmChatService.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Orchestration/InGameLlmChatService.cs) склеивает историю чата в **одну строку** для отправки как `UserPayload`:

```csharp
foreach ((string role, string text) in _turns)
{
    sb.AppendLine($"{role}: {text}");
}
sb.AppendLine($"User: {message}");
transcript = sb.ToString();
```

Это **не MEAI-совместимый** подход. MEAI использует `ChatMessage[]` с ролями. При большой истории токены тратятся на парсинг формата `"Role: text"`, а не на семантику. Также нет защиты от prompt injection внутри текста.

### 3.5 VContainer в CoreAI.Core

`CoreAI.Core.asmdef` имеет зависимость `"VContainer"`. Это противоречит идее "портативного ядра":

```json
{
  "name": "CoreAI.Core",
  "references": ["VContainer", "MoonSharp.Interpreter"],
  "noEngineReferences": true
}
```

Хотя `noEngineReferences: true` убирает Unity API, VContainer — это **Unity-специфичный DI-контейнер**. Для по-настоящему портативного ядра нужно убрать VContainer dependency и вынести `CorePortableInstaller` в мост-слой.

---

## 4. 🟡 Мёртвый и неиспользуемый код

### 4.1 Полностью неиспользуемые классы

| Файл | Проблема |
|------|----------|
| [WorkflowStep.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Orchestration/WorkflowStep.cs) | 0 использований вне себя. Задуман для мульти-агентного воркфлоу, но нигде не инстанциируется. |
| [WorkflowContext.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Orchestration/WorkflowContext.cs) | 0 использований вне `WorkflowStep.cs`. Мёртвый код. |
| [ICodeRefiner.cs + CodeRefinerStub](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Orchestration/ICodeRefiner.cs) | Объявлен как "заготовка под фазу B+". Зарегистрирован в `CorePortableInstaller`, но никогда не вызывается по бизнес-логике. |
| [SequenceStubLlmClient.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Orchestration/SequenceStubLlmClient.cs) | 0 внешних использований. Дубль `StubLlmClient` для тестов, но ни один тест его не использует. |
| [IWorldCommandExecutor.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/World/IWorldCommandExecutor.cs) | Мёртвый интерфейс (см. 3.3). Заменён `ICoreAiWorldCommandExecutor`. |

### 4.2 Интерфейсы-заглушки без реализаций

Эти интерфейсы объявлены в CoreAI.Core, но **не имеют ни одной реализации** и **не используются** в рабочем коде:

| Интерфейс | Файл | Статус |
|-----------|------|--------|
| `IAudioController` | [IAudioController.cs](file:///c:/Git/CoreAI/Assets/CoreAI/Runtime/Core/Features/Audio/IAudioController.cs) | Только объявление |
| `IUIController` | `Features/UI/IUIController.cs` | Только объявление |
| `IPhysicsController` | `Features/Physics/IPhysicsController.cs` | Только объявление |

> [!TIP]
> Эти интерфейсы — часть roadmap (TODO §12). Сами по себе не вредят, но создают ложное впечатление готовности.
> **Решение:** либо удалить и создать при реализации, либо пометить `[Obsolete("Planned — not yet implemented")]`.

### 4.3 ✅ ~~Параметр `defaultAction` в `WithMemory()`~~ — РЕШЕНО в v0.12.0

> [!NOTE]
> Эта проблема **исправлена**. Параметр `defaultAction` теперь сохраняется в `AgentBuilder` и корректно применяется к политике памяти через `AgentConfig.ApplyToPolicy()`.

---

## 5. 🔵 Тесты — пробелы и хрупкость

### 5.1 Покрытие тестами

**EditMode тесты:** 32 файла, покрывают Lua pipeline, MEAI tool calls, crafting, config, settings, world commands, agent builder, analyzer, response policies, versioning.  
**PlayMode тесты:** 16 тестовых файлов + 9 вспомогательных (harness, setup, config, shared), покрывают полные сценарии с LLM-бэкендами (LLMUnity и HTTP API).

### 5.2 Непокрытые области

| Область | Статус тестов |
|---------|--------------|
| `InGameLlmChatService` (склейка истории) | ❌ Только PlayMode с реальной моделью |
| `AgentMemoryPolicy.GetToolsForRole()` | ⚠️ Косвенно через PlayMode |
| `CoreAISettings` статическая синхронизация | ⚠️ Только `CoreAISettingsAssetEditModeTests` |
| `WorkflowStep` / `WorkflowContext` | ❌ Нет тестов (мёртвый код) |
| `NetworkedAuthorityHost` / `AiNetworkExecutionPolicy` | ❌ Нет тестов |
| `ICodeRefiner` / `CodeRefinerStub` | ❌ Нет тестов |
| `UnityLog` (мост ILog → IGameLogger) | ❌ Нет тестов |

### 5.3 Хрупкость PlayMode тестов

PlayMode тесты зависят от:
- Наличия LLM-бэкенда (LMStudio / LLMUnity с загруженной моделью)
- `CoreAISettingsAsset` в `Resources/`
- Таймаутов реальных LLM-запросов

Это означает, что они **не запускаются в CI** без предварительной настройки окружения.

### 5.4 Известная проблема (из TODO)

> ⚠️ Если тесты не компилируются — **Delete Library/ScriptAssemblies** и реимпорт.

Это индикатор проблем с assembly definition references, не с тестами.

---

## 6. ⚪ Мелкие замечания

### 6.1 `LlmToolBase` в CoreAI.Core зависит от `Microsoft.Extensions.AI`

`LlmToolBase` и все Tool-классы (MemoryTool, LuaTool и т.д.) в CoreAI.Core используют `Microsoft.Extensions.AI.AIFunction`. Это делает ядро зависимым от Microsoft-пакета, что ограничивает портативность.

### 6.2 `CoreAiWorldCommandEnvelope` использует `JsonUtility.ToJson`

В [WorldTool.cs:114](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Features/World/WorldTool.cs#L114):
```csharp
string json = JsonUtility.ToJson(envelope);
```

`JsonUtility` — Unity-специфичный сериализатор. Тут же в том же файле используется `System.Text.Json.JsonSerializer` для результата. Два разных JSON-сериализатора в одном файле.

### 6.3 `StubLlmClient` возвращает захардкоженный JSON

```csharp
string modifierJson =
    "{\"commandType\":\"ApplyWaveModifier\",\"payload\":{...}}";
```

Этот JSON содержит бизнес-логику крафтинга (`ApplyWaveModifier`), захардкоженную в stub-клиенте. При изменении протокола команд stub сломается без предупреждения.

### 6.4 LEGACY поля в CoreAILifetimeScope

[CoreAILifetimeScope.cs](file:///c:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Composition/CoreAILifetimeScope.cs) содержит несколько `[SerializeField]` помеченных как LEGACY:

```csharp
[Tooltip("LEGACY: ...")]
private OpenAiHttpLlmSettings openAiHttpLlmSettings;

[Tooltip("LEGACY: ...")]
private float llmRequestTimeoutSeconds = 15f;

[Tooltip("LEGACY: ...")]
private int aiOrchestrationMaxConcurrent = 2;
```

Они остаются в коде для обратной совместимости, но создают путаницу в Inspector.

---

## 7. ✅ Что сделано хорошо

| Аспект | Оценка |
|--------|--------|
| **MEAI Pipeline** — единый tool calling для всех бэкендов | ⭐⭐⭐⭐⭐ |
| **AgentBuilder** — fluent API для создания агентов | ⭐⭐⭐⭐ |
| **ILlmTool** — абстракция для инструментов | ⭐⭐⭐⭐ |
| **`noEngineReferences: true`** — CoreAI.Core не зависит от UnityEngine | ⭐⭐⭐⭐ |
| **Lua Sandbox** — timeout + instruction limit | ⭐⭐⭐⭐ |
| **Тест покрытие** EditMode для core функционала | ⭐⭐⭐⭐ |
| **VContainer DI** в Unity-слое | ⭐⭐⭐⭐ |
| **Routing LLM** — маршрутизация по ролям | ⭐⭐⭐⭐ |
| **FileAgentMemoryStore** — персистентность | ⭐⭐⭐ |
| **Memory idempotency** — защита от дублирования в `append` | ⭐⭐⭐⭐ |

---

## 8. 📋 Рекомендации по приоритетам

### 🔴 Приоритет 1 — Критичное (сделать первым)

| # | Задача | Затраты | Влияние |
|---|--------|---------|---------|
| 1 | ~~**Унифицировать логгер**~~ | ✅ Готово | ✅ v0.12.0 |
| 2 | ~~**Удалить дублированный `MemoryToolAction`**~~ | ✅ Готово | ✅ v0.12.0 |
| 3 | ~~**Починить `WithMemory(defaultAction)`**~~ | ✅ Готово | ✅ v0.12.0 |

### 🟠 Приоритет 2 — Архитектурное

| # | Задача | Затраты | Влияние |
|---|--------|---------|---------|
| 4 | **Вынести `CoreAISettings` из статики** — создать `ICoreAISettings` интерфейс | 2-3 ч | Тестируемость, DI |
| 5 | **Объединить `WorldTool` + `WorldLlmTool`** в один класс | 1 ч | Уменьшение дублирования |
| 6 | **Удалить мёртвый `IWorldCommandExecutor`** из CoreAI.Core (заменён `ICoreAiWorldCommand`) | 15 мин | Чистота |
| 7 | **Убрать VContainer** из CoreAI.Core asmdef references | 1-2 ч | Портативность ядра |

### 🟡 Приоритет 3 — Чистка

| # | Задача | Затраты | Влияние |
|---|--------|---------|---------|
| 8 | Удалить `WorkflowStep.cs` + `WorkflowContext.cs` (мёртвый код) | 10 мин | Чистота |
| 9 | Удалить `SequenceStubLlmClient.cs` (неиспользуемый) | 5 мин | Чистота |
| 10 | Решить судьбу `ICodeRefiner` + `CodeRefinerStub` (удалить или реализовать) | 15 мин | Чистота |
| 11 | Решить судьбу `IAudioController`/`IUIController`/`IPhysicsController` | 15 мин | Ясность API |
| 12 | Убрать LEGACY поля из `CoreAILifetimeScope` или вынести в `[Obsolete]` | 30 мин | Inspector чистота |

### 🔵 Приоритет 4 — Тесты

| # | Задача | Затраты | Влияние |
|---|--------|---------|---------|
| 13 | Добавить EditMode тесты для `InGameLlmChatService` | 1 ч | Покрытие |
| 14 | Добавить тест для `UnityLog` моста | 30 мин | Покрытие |
| 15 | Добавить тест для `AgentMemoryPolicy.GetToolsForRole()` | 30 мин | Покрытие |
| 16 | Добавить тест для `NetworkedAuthorityHost` | 1 ч | Покрытие |

---

## Итого

```
🔴 Критических проблем:     2 → 1  (✅ логгер решён, ✅ дубль enum решён, статические настройки)
🟠 Архитектурных вопросов:   5  (WorldTool дубль, envelope reuse, MEAI в Core, и др.)
🟡 Мёртвого кода:           5+ файлов / классов  (4.3 решено)
🔵 Пробелов в тестах:        4+ области без покрытия
⚪ Мелких замечаний:          4
✅ Общая оценка:             ~8.0/10 — логгер унифицирован,
                              устранено дублирование enum,
                              остальной техдолг в работе
```
