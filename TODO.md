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
- [ ] Заменить статический god-object `CoreAISettings` на DI-интерфейс `ICoreAISettings`
- [ ] Реализовать боевые метрики оркестрации (StatsD, Prometheus, Application Insights)
- [ ] Добавить Dashboard для просмотра метрик (Alerting: «LLM не отвечает 5 минут»)
- [ ] Реализовать версионирование системных промптов (для трекинга, A/B тестов и откатов)
- [ ] Добавить Rate limiting (защиту от спама) для InGameLlmChatService

### WorldCommand Executor (Расширение интеграции с Unity)
- [ ] Анимации: `play_animation`, `stop_animation`
- [ ] Звуки: `play_sound`, `set_volume`
- [ ] UI команды: `show_text`, `hide_panel`, `update_score`
- [ ] Физика: `apply_force`, `set_velocity`
- [ ] Валидация параметров (защита от спавна объектов в стенах)

### Продвинутые Инструменты Агентов
- [ ] `CraftingTool` — специализированная функция для расчёта крафта для CoreMechanicAI
- [ ] `LootRollTool` — функция броска лута
- [ ] `CompatibilityChecker` — проверка совместимости ингредиентов (химия/физика)
- [ ] JSON schema validation для строгих ответов CoreMechanicAI

### Multi-Agent Orchestration v2.0
- [ ] Автоматизированный `MultiAgentWorkflow` (чтобы агенты могли сами вызывать pipeline сабагентов, как в Claude Agent SDK)
- [ ] Передача результатов между суб-агентами без вызова из главного потока (tool_result)
- [ ] Условная логика вызова (если качество > 80, вызвать Programmer)
- [ ] Параллельное исполнение задач несколькими агентами

### Документация и Примеры
- [ ] Диаграмма: «Как команда от игрока проходит через всю систему»
- [ ] Описание формата JSON команд для каждой роли
- [ ] Troubleshooting guide: «Модель не отвечает», «Lua упала», «Память не пишется»
- [ ] Quick Start: «Запуск LM Studio → запуск сцены → отправка команды»
- [ ] Примеры: создание врага, крафт оружия, auto-repair кода
- [ ] Подготовка видео/GIF демо работы системы
