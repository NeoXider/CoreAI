# `com.nexoider.coreai`

UPM-пакет ядра **CoreAI**: портативная сборка **CoreAI.Core** и Unity-слой **CoreAI.Source** (DI, LLM, Lua, MessagePipe, логирование по фичам). Структура как у [NeoxiderTools](https://github.com/NeoXider/NeoxiderTools): корень пакета — папка с `package.json` (`Assets/CoreAI` в монорепозитории).

| Версия (текущая) | Unity |
|------------------|--------|
| См. `package.json` → `version` | `6000.0+` (см. поле `unity` в манифесте) |

---

## Что внутри

| Путь в пакете | Сборка | Содержание |
|---------------|--------|------------|
| `Runtime/Core/` | **CoreAI.Core** (`noEngineReferences`) | Контракты оркестрации, очередь, сессия, песочница MoonSharp, промпты, версионирование Lua/data overlays — **без UnityEngine** |
| `Runtime/Source/` | **CoreAI.Source** | VContainer, MessagePipe, маршрутизация LLM, LLMUnity / OpenAI HTTP, **`IGameLogger`** и sinks, роутер команд, биндинги Lua |

Тесты, Editor-хелперы и большая часть документации — в отдельном пакете **`com.nexoider.coreaiunity`** (`Assets/CoreAiUnity`). Changelog: **`CHANGELOG.md`** в корне этого пакета.

---

## Установка в Unity (основной способ: UPM, Git URL)

Как в [NeoxiderTools — установка через UPM](https://github.com/NeoXider/NeoxiderTools#%D1%83%D1%81%D1%82%D0%B0%D0%BD%D0%BE%D0%B2%D0%BA%D0%B0-%D1%87%D0%B5%D1%80%D0%B5%D0%B7-upm): **Window → Package Manager → `+` → Add package from git URL…**

Для полного шаблона (ядро + тесты + меню редактора) добавьте **два** пакета из одного репозитория — сначала ядро, затем хост (у хоста зависимость на версию `com.nexoider.coreai`):

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

### Локальная разработка (клон репозитория)

В `Packages/manifest.json` проекта:

```json
"com.nexoider.coreai": "file:../Assets/CoreAI",
"com.nexoider.coreaiunity": "file:../Assets/CoreAiUnity"
```

Пути относительно папки `Packages`. После копирования или git pull выполните в Unity **обновление ассетов** (рефреш), чтобы сгенерировались `.meta` — **не создавайте `.meta` вручную** (см. правило репозитория для агентов).

### Зависимости, подтягиваемые UPM

Объявлены в `package.json`: VContainer, MessagePipe (+ VContainer-интеграция), MoonSharp, LLMUnity. Остальное (например UniTask, R3) подключает **ваш** тайтл при необходимости.

---

## Без Unity (другие движки и .NET)

- **`CoreAI.Core`** можно собирать как обычную библиотеку: нет ссылок на Unity. Понадобятся те же зависимости, что использует код ядра (MoonSharp и т.д. — по csproj).
- **`CoreAI.Source`** и интеграция с LLMUnity **привязаны к Unity**; для Unreal, Godot, кастомного сервера переносите идеи контрактов (`ILlmClient`, оркестратор) и реализуйте свой транспорт и окружение без Unity-типов.
- Полноценный «как в Unity» пайплайн без редактора в этом репозитории **не поставляется** — только переносимое ядро и документированные границы.

---

## Документация и проверка сборки

- Обзор шаблона и ссылок: [корневой README](../../README.md).
- Разработка: [`../CoreAiUnity/Docs/DEVELOPER_GUIDE.md`](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md), спецификация: [`../CoreAiUnity/Docs/DGF_SPEC.md`](../CoreAiUnity/Docs/DGF_SPEC.md).

Сборка asmdef через **Rider/Visual Studio / `dotnet build`** по сгенерированным `*.csproj` проверяет компиляцию. **EditMode / PlayMode** тесты запускаются в **Unity Test Runner** (сборки **CoreAI.Tests** / **CoreAI.PlayModeTests** в пакете `coreaiunity`).

---

## Логирование

В рантайме используйте **`IGameLogger`** и **`GameLogFeature`**; настройка фильтра — **`GameLogSettingsAsset`**. Прямой `Debug.Log` в коде ядра не используется; вывод в консоль — через **`UnityGameLogSink`**. Подробнее: [DEVELOPER_GUIDE §2.2](../CoreAiUnity/Docs/DEVELOPER_GUIDE.md), `CHANGELOG.md` релиза 0.1.3+.
