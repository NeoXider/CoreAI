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

В Unity открой:
**CoreAI → Development → Open _mainCoreAI scene** (`Assets/CoreAiUnity/Scenes/_mainCoreAI.unity`).
На этой сцене уже есть всё необходимое для запуска AI.

---

## 4. Подключить LLM (выберите один вариант)

Откройте `Resources/CoreAISettings` (или создайте через **Create → CoreAI → Core AI Settings**).

### A. Локально — LLMUnity (рекомендуемый для тестов) или локальной работы прямо в игре (осторожно!)

> 📦 **LLMUnity устанавливается автоматически** вместе с пакетом CoreAI (через Unity Package Manager). Подробнее про плагин: [GitHub LLMUnity](https://github.com/undreamai/LLMUnity).

1. Установите **Backend Type**: `LlmUnity` (или `Auto`).
2. На объекте `LlmManager` на сцене выберите модель (например, Qwen 4B). Если моделей нет, скачайте их через интерфейс LLMUnity.
3. При старте сцены `CoreAILifetimeScope` сам найдёт `LLMAgent` и подцепит его.

### B. HTTP API — LM Studio / OpenAI / vLLM / Ollama

1. Установите **Backend Type**: `OpenAiHttp`.
2. Заполните **Api Base Url** (например, `http://localhost:1234/v1` для LM Studio).
3. Укажите название модели (например `Qwen`).
4. Если нужен OpenAI — укажите **Api Key**.

> 💡 **Что выбрать?** Для начала мы рекомендуем скачать [LM Studio](https://lmstudio.ai), загрузить там модель (Qwen 4B или Gemma 26B), включить локальный сервер и выбрать режим HTTP API в Unity. Это работает быстрее всего.

---

## 5. Как создать агента?

Теперь всё готово! Можно создавать агентов. Посмотрите инструкцию и 4 копипаст-рецепта в главном гайде:

👉 **[AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md)**

### Краткая проверка в Play Mode

1. Нажмите **Play**.
2. В консоли вы увидите: `VContainer + MessagePipe... готовы.`
3. Вызовете агента из своего скрипта: `myAgent.Ask("Привет");`
4. Логи покажут запросы к LLM и ответы.

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
