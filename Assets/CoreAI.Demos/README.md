# CoreAI.Demos

Self-contained demo scenes built on top of CoreAI (kept outside `Assets/CoreAI` and
`Assets/CoreAiUnity` so they never ship inside the packages). Each folder holds a scene,
minimal scripts, and a README.

| Demo | Scene | What it shows | Needs LLM |
|---|---|---|---|
| [LuaMods](LuaMods/README.md) | `LuaMods/LuaModsDemo.unity` | Lua mods (`LuaModRuntime`): hooks, timers, events, store, capability tiers; `LuaLogicSlots` — overriding the damage formula from Lua | No |
| [WorldCommands](WorldCommands/README.md) | `WorldCommands/WorldCommandsDemo.unity` | AI command pipeline: `IAiGameCommandSink` → `AiGameCommandRouter` → `CoreAiWorldCommandExecutor` (the same path used by LLM agents and Lua bindings) | No |
| [Skills](Skills/README.md) | `Skills/SkillsDemo.unity` | `SkillSet` + `AgentBuilder`: skill catalog, `read_skill` / `call_skill_tool`, a "game master" agent with crafting and combat | Yes |
| [LiveMechanics](LiveMechanics/README.md) | `LiveMechanics/LiveMechanicsDemo.unity` | **A real LLM changes mechanics live through chat**: the Programmer role writes Lua → `execute_lua` pipeline → logic slots / `LuaModRuntime` / world commands | Yes |
| [FullAccess](FullAccess/README.md) | `FullAccess/FullAccessDemo.unity` | Full-tier `unity_*` access (opt-in): Programmer can inspect scene objects, components, transforms, and hierarchy, then move/rotate/parent objects from Lua | Yes |
| [ModdableUnits](ModdableUnits/README.md) | `ModdableUnits/ModdableUnitsDemo.unity` | **A whole game built from mods**: `forge_define`/`forge_spawn` let mods create new unit types and armies, `hooks_every`/`hooks_on` drive the fight; the host only runs the auto-battle | Yes |

## Common requirements

- Every scene has a `CoreAILifetimeScope` (CoreAI's DI composition). Settings come from
  `Resources/CoreAISettings` unless a dedicated asset is assigned in the Inspector.
- Lua demos require MoonSharp in the project (define `COREAI_HAS_MOONSHARP`) and the absence of `COREAI_NO_LUA`.
- The Skills demo needs a configured LLM backend in `CoreAISettings` (an LLMUnity model or HTTP API);
  the other demos run fully offline.

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
