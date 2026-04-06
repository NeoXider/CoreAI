# Changelog

Все значимые изменения проекта CoreAI.

## [v0.9.0] — 2026-04-06

### Added
- `MeaiLlmClient` — единый MEAI-клиент для всех бэкендов
  - `MeaiLlmClient.CreateHttp(settings, logger, memoryStore)` — HTTP API
  - `MeaiLlmClient.CreateLlmUnity(unityAgent, logger, memoryStore)` — локальная GGUF
- `MeaiOpenAiChatClient` — MEAI IChatClient для HTTP API
- `LlmUnityMeaiChatClient` — MEAI IChatClient для LLMUnity (вынесен отдельно)
- `OfflineLlmClient` — кастомный ответ вместо заглушки по ролям
- `CoreAISettings.ContextWindowTokens` — размер контекста по умолчанию (8192)
- `AgentBuilder.WithChatHistory(int?)` — контекст из настроек или переопределение
- `AgentConfig.ContextWindowTokens` и `AgentConfig.WithChatHistory`
- `CoreAISettingsAsset.AutoPriority` — LlmUnityFirst или HttpFirst
- Кнопка **🔗 Test Connection** в Inspector
- `Docs/MEAI_TOOL_CALLING.md` — документация архитектуры

### Changed
- `MeaiLlmUnityClient` — упрощён до фабрики, делегирует в `MeaiLlmClient`
- `OpenAiChatLlmClient` — упрощён до фабрики, делегирует в `MeaiLlmClient`
- Все PlayMode тесты используют `CoreAISettingsAsset` через фабрику
- `LlmBackendType.Stub` → `LlmBackendType.Offline`
- Документация `AGENT_BUILDER.md` — обновлена с примерами создания клиентов
- Удалены: `MEAI_FUNCTION_CALLING.md`, `README_MEAI.md` (дубликаты)

### Architecture
- Одинаковый MEAI pipeline для обоих бэкендов
- `FunctionInvokingChatClient` → автоматический tool calling
- Больше нет ручного парсинга tool calls из текста

---

## [v0.8.0] — 2026-04-06

### Added
- `CoreAISettingsAsset` — единый ScriptableObject-синглтон
- `IOpenAiHttpSettings` — интерфейс для адаптации настроек
- `OpenAiChatLlmClient(CoreAISettingsAsset)` — конструктор
- `CoreAISettingsAssetEditor` — кастомный Inspector
- `CoreAISettings.asset` — asset по умолчанию в Resources
- LLMUnity настройки: `DontDestroyOnLoad`, `StartupTimeout`, `KeepAlive`
- Auto Priority: LlmUnityFirst / HttpFirst

---

## [v0.7.0] — 2026-04-06

### Added
- Единый MEAI Tool Calling Format
- `LuaTool.cs` + `LuaLlmTool.cs`
- `InventoryTool.cs` + `InventoryLlmTool.cs`
- `CoreAISettings.cs` (static)
- `AgentBuilder` — конструктор кастомных агентов
- `WithChatHistory()` — сохранение истории диалога
- `WithMemory()` — персистентная память
- `AgentMode` — ToolsOnly, ToolsAndChat, ChatOnly
- Merchant NPC с инструментами

### Removed
- `AgentMemoryDirectiveParser` — всё через MEAI pipeline
