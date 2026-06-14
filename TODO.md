# TODO

> Updated 2026-06-14. Completed v4.0.0 work is in `CHANGELOG.md` (both packages) and the git log. This file lists only open tasks.

## v4.0.0 - done (2026-06-12)

- [x] Lua as a second language: phases 1-5, capability tiers, `manage_mods`, sandbox/audit fixes.
- [x] Demo `Assets/CoreAI.Demos/`: LuaMods, WorldCommands, Skills, LiveMechanics (+ LLM chat).
- [x] `ICoreAiCustomWorldCommandHandler`, scene whitelist, perf (MPB `set_color`, `LuaModRuntime.Tick` scratch).
- [x] Documentation: `LUA_GAME_API`, `LUA_BEST_PRACTICES`, `MOONSHARP_NATIVE_APIS`, `LUA_ACCESS_MODES_AUDIT`, `PERF_REVIEW_2026-06-12`.
- [x] Version **4.0.0** in `com.nexoider.coreai` / `com.nexoider.coreaiunity`.
- [x] `IGameLogger` instead of `Debug.*` in CoreAiUnity Runtime.

## [P1] Full mode

> Currently available: `LuaCapabilities.Full`, reflection bindings `CoreAiFullUnityLuaRuntimeBindings` (`unity_*`), opt-in `enableFullLuaAccess`, audit `LUA_ACCESS_MODES_AUDIT.md`.

- [x] **Demo scene** `FullAccess/FullAccessDemo.unity` (chat + scope with Full + auto-`TargetCube`, prompt buttons move/grow/inspect).
- [x] **PlayMode tests** Full: `unity_find` / `unity_set_position` on a scene object.
- [x] **Member visibility:** public-by-default, non-public is opt-in (`enableFullLuaPrivateAccess` / ctor `allowNonPublicMembers`) + EditMode tests.
- [ ] **Migration to MoonSharp `UserData.RegisterType`** - *audit conclusion 2026-06-13:* reflection cannot be removed completely without losing allow-all semantics (addressing a member by string = reflection; `UserData` in Reflection mode does the same). `LazyOptimized` breaks on IL2CPP (no JIT), and hardwiring is impossible for types that are not known in advance. The current cached reflection is the most AOT-portable option and is not on a hot path (admin/debug tier). Migration would provide more idiomatic syntax, not performance; not a priority.
- [ ] **Blacklist** types/members for Full (idea from the audit, Planned - do not implement until a separate task, but document the `IFullLuaAccessBlacklistPolicy` API when introducing it).

## Infrastructure

- [ ] **GameCI secrets** (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) - without them the CI matrix moonsharp / no-lua will not run.
- [ ] **GitHub Release / tag v4.0.0** after push.
- [ ] **EditMode test integrity audit** - project-wide rules are documented in `Assets/CoreAiUnity/Docs/ARCHITECTURE.md`; PlayMode live-model tests are the priority now. Later, review EditMode fixtures and keep exact payloads only where they test parsers, repair, serialization, migration, deterministic extraction logic, or other exact-byte contracts.
- [ ] **Crafting determinism PlayMode test** - `ThreeCrafts_AllUnique` now checks only three unique crafts. Add a separate targeted repeat-ingredients test for determinism so the full PlayMode suite does not hide a fourth long LLM turn inside a uniqueness scenario.
- [x] **LLMUnity crafting timeout triage** - targeted `CraftingMemoryLlmUnity_ThreeCrafts_AllUnique` with `qwen3.6-27b-mtp-ud` passed after bounding live craft turns to 2048 output tokens, exposing the generic `logic_*` Lua APIs advertised by `execute_lua`, and canonicalizing verified craft memory between turns.
- [x] **Merchant negotiation timeout triage** - targeted `MerchantChatWithTools_FullNegotiationFlow_CompletesPurchase` with `qwen3.6-27b-mtp-ud` passed after making the negotiation mechanically necessary (Iron Sword costs 60, player has 40) and bounding each live step to 2048 output tokens. The scenario remains explicit/targeted rather than a mandatory full-suite gate.
- [ ] **Merchant negotiation decomposition** - the full merchant negotiation scenario is explicit and now cancels/bounds long turns. Consider splitting Step3 into smaller targeted tests for discount application and purchase completion so failures identify tool-choice versus final-sale behaviour.
- [x] **Targeted PlayMode follow-up before full suite** - attached failures and known slow live-model tests were triaged one by one before the full rerun. FullAccess demo scene checks now include targeted Full Lua API smoke, PlayMode scene-smoke, and manual visual layout verification.
- [x] **Full PlayMode rerun after targeted fixes** - full mandatory PlayMode suite with `qwen3.6-27b-mtp-ud` passed after targeted fixes: 114 total, 109 passed, 0 failed, 5 skipped/explicit.

## [P1] Lua - remaining work (does not block v4)

- [ ] Undo applied world commands (inverse spawn/move commands).
- [ ] Capability tier from AI role config + optional player confirmation for dangerous levels.
- [ ] Bridge `ModEventEmitted` -> MessagePipe.
- [ ] World-command budget per tick for mods.
- [ ] **Lua skill by access mode.** Create an agent-facing Lua guide/skill that routes tasks to the right API surface: Safe/Logic (`logic_define`, `report`), Mods (`manage_mods`, `hooks_on`, `hooks_every`, `store_get/set`), WorldEdit (`coreai_world_*`), and Full (`unity_*`). It must explicitly forbid hallucinated APIs such as `game.enemies`, `game.create`, `game.destroy` unless a host game registers them.
- [ ] **Reusable file-backed Lua mods.** Design a portable mod package layout for games, e.g. `Mods/<mod_id>/manifest.json` + `main.lua`, with `id`, `name`, `description`, `version`, `capabilities`, `entry`, `author`, and `active`. The runtime/panel should load, activate/deactivate, reload, and forget mods from files instead of only `ILuaScriptVersionStore`.

## [P2] WebGL: Lua in the web build (research)

- `SecureLuaEnvironment.IsSupported` = false in WebGL player; investigate MoonSharp+IL2CPP, size, and no-thread limits.

## [P2] Ideas

- [ ] STT -> Agent -> TTS for NPCs.
- [ ] Visual AgentBuilder in the editor.
- [ ] Streaming emotions / function-driven animations.

## Media / promotion

- [ ] GIF demos for README (`DEMO_RECORDING_GUIDE.md`).
- [ ] Publish to OpenUPM.
- [ ] Boosty link in `FUNDING.yml`.
