# Быстрый старт CoreAI

Минимальный путь от клонирования репозитория до рабочего **LLM + оркестратор + Lua** в редакторе.

---

## 1. Требования

| Что | Значение |
|-----|----------|
| **Unity** | Как в `ProjectSettings/ProjectVersion.txt` (сейчас **6000.4.x**). |
| **Диск / сеть** | Для локальной модели LLMUnity — место под GGUF и время на первую загрузку. |
| **Опционально** | LM Studio, OpenAI или другой **OpenAI-compatible** сервер для HTTP-режима. |

---

## 2. Открыть проект

1. **Unity Hub → Add →** папка репозитория **CoreAI**.
2. Откройте проект той версией редактора, которую запросит Unity (или установите через Hub).

---

## 3. Выбрать сцену

| Цель | Действие |
|------|----------|
| **Демо-арена + F9 (Programmer)** | Меню **CoreAI → Development → Example Game → Open RogueliteArena scene** *или* вручную `Assets/_exampleGame/Scenes/RogueliteArena.unity`. |
| **Минимальная сцена ядра** | **CoreAI → Development → Open _mainCoreAI scene** (`Assets/CoreAiUnity/Scenes/_mainCoreAI.unity`). |

Чтобы **Play** по умолчанию запускал арену: **CoreAI → Development → Example Game → Set RogueliteArena as first build scene** (первая сцена в **File → Build Settings**).

Подробная настройка инспектора для примера игры: **[../../_exampleGame/Docs/UNITY_SETUP.md](../../_exampleGame/Docs/UNITY_SETUP.md)**.

---

## 4. Подключить LLM (выберите один вариант)

### A. Локально — LLMUnity (по умолчанию в репозитории)

На сцене должен быть **`LLM`** + **`LLMAgent`**, в **`CoreAILifetimeScope`** поле **Open Ai Http Llm Settings** пустое или в asset выключён **Use Open Ai Compatible Http**.

Кратко: назначьте **модель (GGUF)** на компоненте **LLM**, в **LLMAgent** укажите ссылку на этот **LLM**. Детали и модели: **[LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md)** §1–2.

### B. HTTP — LM Studio / OpenAI / vLLM

1. **Create → CoreAI → LLM → OpenAI-compatible HTTP**.
2. Заполните **Api Base Url** (с суффиксом `/v1`), **Model**, при необходимости **Api Key**.
3. Включите **Use Open Ai Compatible Http**, перетащите asset в **`CoreAILifetimeScope` → Open Ai Http Llm Settings**.

Инструкция: **[LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md)** §4.

---

## 5. Проверка в Play Mode

1. **Play** на настроенной сцене.
2. В консоли — старт **`CoreAIGameEntryPoint`** (без критических ошибок DI).
3. На **RogueliteArena**: прототип волновой арены; **F9** — задача **Programmer** (Lua + `report` в лог). **R** — перезапуск сцены.
4. **Логи ядра:** **`[Llm]`** — запрос/ответ модели (**`LLM ▶`**, **`LLM ◀`**, при зависании — **`LLM ⏱`**); **`[MessagePipe]`** — **`ApplyAiGameCommand`** с тем же **`traceId`**. На **`CoreAILifetimeScope`**: **Llm Request Timeout Seconds** (по умолчанию **15**, **0** = без лимита). Категория **`Llm`** в **Game Log Settings** должна быть включена (или откройте asset в инспекторе — сработает миграция).

Если используете **World Commands** (управление миром из Lua): создайте `CoreAiPrefabRegistryAsset` и назначьте в `CoreAILifetimeScope → World Prefab Registry`, затем Lua сможет безопасно публиковать команды спавна/движения/сцен. Детали: **[WORLD_COMMANDS.md](WORLD_COMMANDS.md)**.

Если **`ILlmClient`** не находит **LLMAgent** и HTTP выключен — подставится **StubLlmClient** (ответы-заглушки); для реального текста от модели настройте §4A или §4B.

---

## 6. Тесты

| Сборка | Где | Зачем |
|--------|-----|--------|
| **CoreAI.Tests** | **Window → General → Test Runner → EditMode** | Промпты, Lua, парсеры без Play. |
| **CoreAI.PlayModeTests** | Test Runner → **PlayMode** | Оркестратор; опционально реальный HTTP через `COREAI_OPENAI_TEST_*` — см. [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) §7. |

---

## 7. Куда дальше

| Документ | Содержание |
|----------|------------|
| **[DOCS_INDEX.md](DOCS_INDEX.md)** | Оглавление всех документов `CoreAiUnity/Docs`. |
| **[DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)** | Архитектура, поток данных, типичные задачи, чеклист PR. |
| **[DGF_SPEC.md](DGF_SPEC.md)** | Нормативный контракт шаблона. |
| **[AI_AGENT_ROLES.md](AI_AGENT_ROLES.md)** | Роли агентов и выбор модели. |
| **[../README.md](../README.md)** | Ядро: сборки, DI, промпты, MessagePipe. |

**Версия:** 1.1 (апрель 2026) — логи Llm/traceId, таймаут запроса.
