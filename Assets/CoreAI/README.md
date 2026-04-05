# `com.nexoider.coreai`

UPM-пакет **портативного ядра** **CoreAI.Core** — без `UnityEngine` (`noEngineReferences`). Структура как у [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools): корень пакета — папка с `package.json` (**`Assets/CoreAI`** в этом репозитории).

**Unity-интеграция** (сборка **CoreAI.Source**: DI, LLM, MessagePipe, логи, сцена) поставляется отдельным пакетом **`com.nexoider.coreaiunity`** — см. [`../CoreAiUnity/README.md`](../CoreAiUnity/README.md).

| Версия (текущая) | Unity |
|------------------|--------|
| См. `package.json` → `version` | `6000.0+` (манифест для UPM в Unity-проекте) |

---

## Что внутри

| Путь в пакете | Сборка | Содержание |
|---------------|--------|------------|
| `Runtime/Core/` | **CoreAI.Core** | Контракты оркестрации, очередь, сессия, песочница MoonSharp, промпты, версионирование Lua/data overlays — **без UnityEngine** |
| `Runtime/Core/Features/AgentMemory/` | **CoreAI.Core** | **MEAI Function Calling** через Microsoft.Extensions.AI, MemoryTool с AIFunctionFactory, автоматический вызов tools |
| `Runtime/Core/Features/Orchestration/` | **CoreAI.Core** | AiOrchestrator с обработкой tool results от MEAI, fallback на legacy парсинг |
| `Runtime/Core/Features/Llm/` | **CoreAI.Core** | **ILlmTool** интерфейс для tool calling, **OpenAiChatLlmClient** с поддержкой tools в JSON body |

### Tool Calling (ILlmTool)

CoreAI предоставляет универсальный интерфейс **ILlmTool** для определения инструментов, которые может вызывать LLM:

- **ILlmTool** - интерфейс инструмента (Name, Description, ParametersSchema)
- **LlmToolBase** - базовый класс для простых инструментов
- **MemoryLlmTool** - реализация memory tool (write/append/clear)
- **LuaLlmTool** - реализация execute_lua tool для Programmer
- **ILlmClient.SetTools()** - метод для установки tools на LLM клиенте
- **AgentMemoryPolicy.GetToolsForRole()** - возвращает tools для роли

Поддерживается **OpenAI API** (tools array в JSON body) и **LLMUnity** (через MEAI).

### Глобальные настройки

```csharp
// До инициализации системы:
CoreAISettings.MaxLuaRepairGenerations = 3; // Лимит повторов Programmer (по умолчанию)
CoreAISettings.EnableMeaiDebugLogging = true; // Отладка MEAI
CoreAISettings.LlmRequestTimeoutSeconds = 300; // Таймаут LLM (5 минут)
```

Changelog: **`CHANGELOG.md`**.

---

## Установка в Unity (основной способ: UPM, Git URL)

Как в [NeoxiderTools — установка через UPM](https://github.com/NeoXider/NeoxiderTools#%D1%83%D1%81%D1%82%D0%B0%D0%BD%D0%BE%D0%B2%D0%BA%D0%B0-%D1%87%D0%B5%D1%80%D0%B5%D0%B7-upm): **Window → Package Manager → `+` → Add package from git URL…**

Для **полного** шаблона (ядро + Unity-слой + тесты + меню редактора) добавьте **два** пакета из одного репозитория — сначала **`com.nexoider.coreai`**, затем **`com.nexoider.coreaiunity`**:

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
```

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

Фиксация на тег или ветку (пример):

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI#v0.1.3
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity#v0.1.3
```

Параметр **`?path=...`** обязателен: UPM должен указывать на каталог, где лежит `package.json`.

### Этот репозиторий (монорепозиторий)

Исходники ядра лежат в **`Assets/CoreAI`** и **`Assets/CoreAiUnity`** как обычные папки проекта. **Не** регистрируйте их в `Packages/manifest.json` через **`file:`** на путь внутри **`Assets/`** — Unity Package Manager такие пути отклоняет. Зависимости (**VContainer**, **MessagePipe**, **MoonSharp**, **LLMUnity**, **UniTask** и т.д.) подключаются в **`manifest.json`** отдельно, как в шаблоне. После `git pull` выполните **Assets → Refresh**.

### Зависимости этого пакета (UPM)

В `package.json`: **VContainer**, **MoonSharp**, **Microsoft.Extensions.AI**. Интеграция с Unity (MessagePipe, LLMUnity, UniTask и т.д.) объявлена в **`com.nexoider.coreaiunity`**.

### Microsoft.Extensions.AI

Пакет использует **Microsoft.Extensions.AI** (MEAI) для стандартизированного function calling:

```xml
<package id="Microsoft.Extensions.AI" version="10.4.1" manuallyInstalled="true" />
```

MEAI обеспечивает:
- Provider-agnostic подход для поддержки различных LLM бэкендов
- Decorator-based функциональность через `AIFunctionFactory.Create()`
- Middleware `UseFunctionInvocation()` для автоматического обнаружения и выполнения функций

Подробнее: [Tool-Call документация](Runtime/Core/Features/AgentMemory/TOOL_CALL_DOCUMENTATION.md).

---

## Без Unity (другие движки и .NET)

**CoreAI.Core** можно собирать как обычную библиотеку: нет ссылок на Unity. Для Unreal, Godot или своего сервера переносите контракты (`ILlmClient`, оркестратор) и реализуйте свой транспорт и окружение.

Полноценный «как в Unity» пайплайн — в **`com.nexoider.coreaiunity`**.

---

## Документация и проверка сборки

- Обзор шаблона: [корневой README](../../README.md).
- Разработка: [`../CoreAiUnity/Docs/DEVELOPER_GUIDE.md`](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md), спецификация: [`../CoreAiUnity/Docs/DGF_SPEC.md`](../CoreAiUnity/Docs/DGF_SPEC.md).

Сборка asmdef через **Rider / `dotnet build`** по сгенерированным `*.csproj`. **EditMode / PlayMode** — в **Unity Test Runner** (пакет `coreaiunity`).

---

## Логирование в Unity-слое

Интерфейсы **`IGameLogger`**, **`GameLogFeature`** и реализация с **`UnityGameLogSink`** находятся в **`CoreAI.Source`** (пакет **`com.nexoider.coreaiunity`**). Подробнее: [DEVELOPER_GUIDE §2.2](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md).
