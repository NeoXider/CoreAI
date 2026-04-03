# Changelog — `com.nexoider.coreaiunity`

Хост Unity: тесты (EditMode / PlayMode), Editor-меню, документация. Зависит от **`com.nexoider.coreai`**.

## [0.1.3] - 2026-04-03

### Логирование (обязательный блок релиза)

- **Editor:** сообщения меню и setup сосредоточены в **`CoreAIEditorLog`** (единая точка `Debug.*` в Editor-слое пакета).
- **Тесты:** хранилища версий и LLM-хелперы используют **`NullGameLogger`** или **`GameLoggerUnscopedFallback`**, без прямого **`Debug.Log`** в тестовой логике ядра.

### Прочее

- Версия синхронизирована с **`com.nexoider.coreai` 0.1.3** (зависимость в `package.json`).

## [0.1.2] - ранее

Базовая линия хоста. См. историю git.
