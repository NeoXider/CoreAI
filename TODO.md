# TODO

> Обновлено 2026-06-12. Выполненное v4.0.0 — в `CHANGELOG.md` (оба пакета) и git-логе. Здесь только открытые задачи.

## v4.0.0 — сделано (2026-06-12)

- [x] Lua как второй язык: этапы 1–5, capability tiers, `manage_mods`, sandbox/audit fixes.
- [x] Демо `Assets/CoreAI.Demos/`: LuaMods, WorldCommands, Skills, LiveMechanics (+ LLM чат).
- [x] `ICoreAiCustomWorldCommandHandler`, whitelist сцен, perf (MPB `set_color`, `LuaModRuntime.Tick` scratch).
- [x] Документация: `LUA_GAME_API`, `LUA_BEST_PRACTICES_RU`, `MOONSHARP_NATIVE_APIS_RU`, `LUA_ACCESS_MODES_AUDIT_RU`, `PERF_REVIEW_2026-06-12_RU`.
- [x] Версия **4.0.0** в `com.nexoider.coreai` / `com.nexoider.coreaiunity`.
- [x] `IGameLogger` вместо `Debug.*` в CoreAiUnity Runtime.

## [P1] Full-режим — начат, не завершён

> Сейчас есть: `LuaCapabilities.Full`, reflection-биндинги `CoreAiFullUnityLuaRuntimeBindings` (`unity_*`), opt-in `enableFullLuaAccess`, EditMode-тесты, аудит `LUA_ACCESS_MODES_AUDIT_RU.md`, README + controller в `FullAccess/` (без `.unity` сцены).

- [ ] **Демо-сцена** `FullAccess/FullAccessDemo.unity` (чат + scope с Full + TargetCube).
- [ ] **PlayMode-тесты** Full: `unity_find` / `unity_set_position` на объекте в сцене.
- [ ] **Миграция на MoonSharp `UserData.RegisterType`** вместо reflection на горячем пути (см. `MOONSHARP_NATIVE_APIS_RU.md`).
- [ ] **Blacklist** типов/членов для Full (идея в аудите, Planned — не реализовывать до отдельной задачи, но задокументировать API `IFullLuaAccessBlacklistPolicy` при внедрении).

## Инфраструктура

- [ ] **Секреты GameCI** (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) — без них CI matrix moonsharp / no-lua не запустится.
- [ ] **GitHub Release / tag v4.0.0** после пуша.

## [P1] Lua — остатки (не блокируют v4)

- [ ] Undo применённых world-команд (инверсные команды spawn/move).
- [ ] Capability tier из конфига роли ИИ + опциональное подтверждение игрока для опасных уровней.
- [ ] Мост `ModEventEmitted` → MessagePipe.
- [ ] Бюджет world-команд на тик для модов.

## [P2] WebGL: Lua в веб-сборке (исследование)

- `SecureLuaEnvironment.IsSupported` = false в WebGL player; исследовать MoonSharp+IL2CPP, размер, лимиты без потоков.

## [P2] Идеи

- [ ] STT → Agent → TTS для NPC.
- [ ] Визуальный билдер AgentBuilder в редакторе.
- [ ] Streaming-emotions / function-driven анимации.

## Медиа / продвижение

- [ ] GIF-демки для README (`DEMO_RECORDING_GUIDE.md`).
- [ ] Публикация в OpenUPM.
- [ ] Ссылка Boosty в `FUNDING.yml`.
