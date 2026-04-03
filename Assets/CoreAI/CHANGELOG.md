# Changelog — `com.nexoider.coreai`

Все значимые изменения этого пакета описываются здесь. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

## [0.1.3] - 2026-04-03

### Логирование (обязательный блок релиза)

- Рантайм **CoreAI.Source** переведён на **`IGameLogger`** с категориями **`GameLogFeature`** и фильтрацией через **`IGameLogSettings`** / **`GameLogSettingsAsset`** (уровни + маска фич). Прямой **`UnityEngine.Debug.Log` / `LogWarning` / `LogError`** в прикладном коде ядра убран.
- Единственная точка вывода в Unity Console в рантайме — **`UnityGameLogSink`** (обёртка над `Debug.*`); остальной код пишет только через абстракцию.
- Для раннего **Awake** без VContainer добавлен **`GameLoggerUnscopedFallback`** (тот же путь: фильтр → sink).
- Файловые **`FileLuaScriptVersionStore`** / **`FileDataOverlayVersionStore`**, реестр LLM, bootstrap LLMUnity, **`AiScheduledTaskTrigger`** и связанные места принимают **`IGameLogger`** и логируют с осмысленными **`GameLogFeature`** (например **Llm**, **Core**, **Composition**).
- Тесты могут подставлять **`NullGameLogger`**.

### Прочее

- Мелкие правки композиции DI (**`CoreAILifetimeScope`**, **`LlmClientRegistry`**) под передачу логгера.

## [0.1.2] - ранее

Базовая линия пакета (CoreAI.Core + CoreAI.Source, VContainer, MessagePipe, LLM, оркестратор). См. историю git для деталей.
