# CoreAI.Demos

Self-contained demo scenes built on top of CoreAI (kept outside `Assets/CoreAI` and
`Assets/CoreAiUnity` so they never ship inside the packages). Each folder holds a scene,
minimal scripts, and a README.

| Demo | Scene | What it shows | Needs LLM |
|---|---|---|---|
| [Hub](Hub/README.md) | `Hub/CoreAiHubDemo.unity` | Drop-in UI Toolkit Hub with Chat, Settings, Statistics, Mods, and World State pages | Yes |
| [MiniRpg](MiniRpg/README.md) | `MiniRpg/MiniRpgModsDemo.unity` | Small first-person environment with Hub chat and mod-ready prompts | Yes |
| [LuaMods](LuaMods/README.md) | `LuaMods/LuaModsDemo.unity` | Lua mods (`ILuaModRuntime`): hooks, timers, events, store, capability tiers; `LuaCsLogicSlots` — overriding the damage formula from Lua | No |
| [WorldCommands](WorldCommands/README.md) | `WorldCommands/WorldCommandsDemo.unity` | AI command pipeline: `IAiGameCommandSink` → `AiGameCommandRouter` → `CoreAiWorldCommandExecutor` (the same path used by LLM agents and Lua bindings) | No |
| [Skills](Skills/README.md) | `Skills/SkillsDemo.unity` | `SkillSet` + `AgentBuilder`: skill catalog, `read_skill` / `call_skill_tool`, a "game master" agent with crafting and combat | Yes |
| [LiveMechanics](LiveMechanics/README.md) | `LiveMechanics/LiveMechanicsDemo.unity` | **A real LLM changes mechanics live through chat**: the Programmer role writes Lua → `execute_lua` pipeline → logic slots / `LuaModRuntime` / world commands | Yes |
| [FullAccess](FullAccess/README.md) | `FullAccess/FullAccessDemo.unity` | Full-tier `unity_*` access (opt-in): Programmer can inspect scene objects, components, transforms, and hierarchy, then move/rotate/parent objects from Lua | Yes |
| [ModdableUnits](ModdableUnits/README.md) | `ModdableUnits/ModdableUnitsDemo.unity` | _Aspirational — the `forge_*` scene bindings are not yet wired to the mod runtime (see the demo README); the intended design has mods build armies via `forge_define`/`forge_spawn` with `hooks_every`/`hooks_on` driving an auto-battle_ | Yes |
| [LiveMechanics Mods Chat](LiveMechanicsMods/README.md) | `LiveMechanicsMods/LiveMechanicsModsChatDemo.unity` | Chat-driven persistent `manage_mods` workflow | Yes |
| [WaveAutoBattler](LiveMechanicsMods/README.md) | `LiveMechanicsMods/WaveAutoBattlerModsDemo.unity` | Playable wave loop whose rules and rewards are changed by persistent Lua mods | Yes |
| [WebGlLuaSelfTest](WebGlLuaSelfTest/README.md) | script only (attach to any scene) | Runtime PASS/FAIL check that the Lua sandbox survives IL2CPP stripping in a WebGL player build (Lua-CSharp `LuaCsSecureEnvironment` invariants) | No |

## Common requirements

- Every scene has a `CoreAILifetimeScope` (CoreAI's DI composition). Settings come from
  `Resources/CoreAISettings` unless a dedicated asset is assigned in the Inspector.
- Lua demos require the Lua-CSharp runtime (shipped via the CoreAI.Mods package) and the absence of `COREAI_NO_LUA`.
- Lua demos also require a `CoreAiModsLifetimeScope` child under `CoreAILifetimeScope`; the mod
  runtime is package-owned and is not registered in the core container.
- Every scene **opens** without an LLM. The "Needs LLM" column above marks demos whose
  **full behaviour** requires a configured backend in `CoreAISettings` (an LLMUnity model or
  HTTP API): Skills, LiveMechanics, FullAccess, and ModdableUnits drive their gameplay through
  a live model, so without one you can load the scene but the AI-driven part stays idle.
  The remaining demos (LuaMods, WorldCommands) exercise the Lua/command pipeline directly and
  run fully offline.

> Demo scenes and assets were assembled through MCP for Unity (see `Assets/CoreAiUnity/Docs/DGF_SPEC.md`, §11) —
> the same editor-automation channel the agent uses to run this repository's tests.

## LiveMechanicsMods

- README: `LiveMechanicsMods/README.md`
- Scene: `LiveMechanicsMods/LiveMechanicsModsChatDemo.unity`
- Purpose: LiveMechanics copy for chat-driven `manage_mods`: load/reload/unload Lua mods,
  persist loaded mod sources, and autoload them on next scene start.
- Main scene: `LiveMechanicsMods/WaveAutoBattlerModsDemo.unity`
- Purpose: full wave auto-battler demo where the hero levels up, enemy waves scale, and Lua mods
  are managed through a draggable active/saved mod panel (`F9`) plus a Token Budget / usage
  overlay (`F10`) and ready prompt buttons.

## Controller recipes

`DirectorAi/` is an ambient-agent controller recipe, not a standalone scene. Its README explains
how to attach the lifecycle-cancelled Director component to any existing CoreAI scene.
