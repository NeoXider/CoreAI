# TODO — CoreAI: Что не хватает для полной реализации архитектуры
**Обновлено:** 2026-04-09 | **Текущая версия:** v0.16.1

## 🎯 ПРИОРИТЕТНЫЕ ЗАДАЧИ

### ✅ Сделано (Недавнее)
- [x] SceneLlmTool — встроен инструмент Runtime инспекции сцен и манипуляций (find_objects, get_hierarchy, get_transform, set_transform).
- [x] CameraLlmTool — инструмент получения Base64 JPEG снимков (render texture snapshot) прямо в PlayMode.
- [x] Защита от реентерабельных дедлоков Unity Thread Context в MEAI pipeline (через Task.Yield).
- [x] Поддержка конфигурации `numGPULayers` для значительного ускорения LLMUnity в PlayMode тестах.
- [x] Умная защита от застревания в циклах: детектирование дубликатов `tool_call` в `SmartToolCallingChatClient` и блокировка бесконечных петель при галлюцинациях модели.
- [x] Robust Tool Parsing — защита парсера JSON от забытых бэктиков у модели и обрезка тегов размышления `<think>`.
- [x] Общая стабилизация тестов: обработка ошибок синтаксиса Lua без фейлов Unity Test Runner (возврат `[Error]` обратно в модель для авто-восстановления).

### Инфраструктура и Архитектура
- [x] Заменить статический god-object `CoreAISettings` на DI-интерфейс `ICoreAISettings` → MemoryTool, InventoryTool, GameConfigTool, AgentBuilder, BuiltInAgentSystemPromptTexts мигрированы
- [x] Реализовать боевые метрики оркестрации → `InMemoryAiOrchestrationMetrics` (per-role, latency, health)
- [x] Добавить Dashboard для просмотра метрик (Alerting: «LLM не отвечает 5 минут») → `OrchestrationDashboard` (OnGUI overlay, F9 toggle)
- [x] Реализовать версионирование системных промптов → `IPromptVersionRegistry` + `InMemoryPromptVersionRegistry` (history, rollback, A/B variants)
- [x] Добавить Rate limiting (защиту от спама) для InGameLlmChatService → sliding-window rate limiter (10 req/60s default)

### WorldCommand Executor (Расширение интеграции с Unity)
- [x] Анимации: `play_animation`, `stop_animation`
- [x] Звуки: `play_sound`, `set_volume`
- [x] UI команды: `show_text`, `hide_panel`, `update_score`
- [x] Физика: `apply_force`, `set_velocity`
- [x] Валидация параметров (защита от спавна объектов в стенах) → `ValidateSpawnPosition` via Physics.OverlapSphere

### Продвинутые Инструменты Агентов
- [ ] `CraftingTool` — специализированная функция для расчёта крафта для CoreMechanicAI
- [x] `CompatibilityChecker` — проверка совместимости ингредиентов (поддержка правил на 2/3/4+ элементов, группы, кастомные валидаторы)
- [x] JSON schema validation (`JsonSchemaValidator`) для строгих ответов CoreMechanicAI (типы, диапазоны, enum значения)
- [x] `CompatibilityLlmTool` — LLM tool wrapper для проверки совместимости через function calling

### Multi-Agent Orchestration v2.0
- [ ] Автоматизированный `MultiAgentWorkflow` (чтобы агенты могли сами вызывать pipeline сабагентов, как в Claude Agent SDK)
- [ ] Передача результатов между суб-агентами без вызова из главного потока (tool_result)
- [ ] Условная логика вызова (если качество > 80, вызвать Programmer)
- [ ] Параллельное исполнение задач несколькими агентами

### Тесты
- [x] `QueuedAiOrchestrator` — тест приоритета: задача с Priority=10 выполняется раньше Priority=1
- [x] `QueuedAiOrchestrator` — тест CancellationScope: повторный запрос с тем же scope отменяет предыдущий
- [x] `QueuedAiOrchestrator` — тест MaxConcurrent: не более N задач одновременно

### Документация и Примеры
- [x] Диаграмма: «Как команда от игрока проходит через всю систему» → [COMMAND_FLOW_DIAGRAM.md](Assets/CoreAiUnity/Docs/COMMAND_FLOW_DIAGRAM.md)
- [x] Описание формата JSON команд для каждой роли → [JSON_COMMAND_FORMAT.md](Assets/CoreAiUnity/Docs/JSON_COMMAND_FORMAT.md)
- [x] Troubleshooting guide: «Модель не отвечает», «Lua упала», «Память не пишется» → [TROUBLESHOOTING.md](Assets/CoreAiUnity/Docs/TROUBLESHOOTING.md)
- [x] Quick Start: «Запуск LM Studio → запуск сцены → отправка команды» → [QUICK_START_FULL.md](Assets/CoreAiUnity/Docs/QUICK_START_FULL.md)
- [x] Примеры: создание врага, крафт оружия, auto-repair кода → [EXAMPLES.md](Assets/CoreAiUnity/Docs/EXAMPLES.md)
- [x] Подготовка видео/GIF демо работы системы → [DEMO_RECORDING_GUIDE.md](Assets/CoreAiUnity/Docs/DEMO_RECORDING_GUIDE.md)
