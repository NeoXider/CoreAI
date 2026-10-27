# CoreAI Mods (Lua)

Start with the [ready-to-run Lua examples](Docs/LUA_MOD_EXAMPLES.md): orbit animation,
waypoint platform, tween gate, respawning coins, saved score, round clock, recycling conveyor
and a frame-budgeted Lua maze solver. New examples are opt-in and editable from the Hub.

The **optional** Lua modding layer for CoreAI: a sandboxed Lua runtime, persistent mods that the
in-game AI or the player writes while the game runs, a Roblox-compatible scripting surface (the Rbx
API), world save/load packages, and the Hub pages that manage mods.

| Package | Depends on | Status |
|---------|-----------|--------|
| `com.neoxider.coreaimods` — version in [`package.json`](package.json) | `com.neoxider.coreai`, `com.neoxider.coreaiunity` (same version, lockstep) | Optional |

The bundled Lua VM is [Lua-CSharp](https://github.com/nuskey8/Lua-CSharp) (Lua 5.2 semantics,
double-only numbers), a managed, AOT-safe VM that runs under IL2CPP and WebGL. It ships bundled as
`Plugins/Lua.dll` + `Plugins/Lua.Annotations.dll`; there is no other Lua package to install. Luau
sources are rewritten to Lua 5.2 before every compile, so mods may use Roblox-style Luau syntax.

Install from git: `https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMods`
(see [INSTALL.md](../../INSTALL.md) §3).

## Turning Lua on: `COREAI_LUA`

Lua is a positive opt-in. The `COREAI_LUA` scripting define is absent by default. Set it with
**CoreAI → Setup → Modules → Lua (Lua-CSharp) → Enable Lua** (remove it with **Disable Lua**; check it
with **CoreAI → Setup → Modules → Report module status**), or add it under
**Project Settings → Player → Scripting Define Symbols**. The menu is
`CoreAIModuleManager` in `Assets/CoreAiUnity/Editor`.

What the define switches: the Lua demo controllers in `CoreAI.Demos`, the benchmark game scenarios
and the Lua-dependent tests compile only with it; without it a Lua demo compiles a stub that logs,
for example, `[LuaModsDemo] COREAI_LUA is not set; demo is inactive.` The runtime and editor
assemblies of this package carry no define constraint and no `#if COREAI_LUA` (only some of its
tests do): once the package is installed, `CoreAI.Mods` compiles either way, and a scene gets Lua
only when it composes `CoreAiModsLifetimeScope`.

## Assemblies

| Assembly | Folder | What it holds |
|---|---|---|
| `CoreAI.Mods` | `Runtime/` | The sandbox and engine seam (`Scripting/`), the mod runtime and the LLM tools (`LuaExecution/`), stores, world packages and gameplay bindings (`Infrastructure/`, `WorldBindings/`), mod logging (`Logging/`), DI composition (`Composition/`), auto-repair (`Presentation/`), bundled sample mods (`Resources/CoreAIMods/`). References `Lua.dll`. |
| `CoreAI.LuauDownlevel` | `Runtime/LuauDownlevel/` | Luau → Lua 5.2 source rewriter. No engine references, not auto-referenced. |
| `CoreAI.RbxApi.Datatypes` | `Runtime/RbxApi/Datatypes/` | `Vector3`, `CFrame`, `Color3`, `UDim`, `Enum`, `TweenInfo`, `Random` and the other value types. No engine references. |
| `CoreAI.RbxApi.Instances` | `Runtime/RbxApi/Instances/` | The Instance/DataModel registry, services, scheduler, signals, networking contracts and the replication core. No engine references. |
| `CoreAI.RbxApi.Unity` | `Runtime/RbxApi/Unity/` | `RbxSpace` (studs ↔ metres) and the procedural and texture-backed material providers. |
| `CoreAI.RbxApi.Binding` | `Runtime/RbxApi/Binding/` | The Unity backing of the Rbx world: `RbxWorldHost`, `InstanceGameObjectBinder`, character motor, camera, click, input and physics adapters, network bridge provider. |
| `CoreAI.Mods.Hub` | `Runtime/HubIntegration/` | Hub pages for mods and worlds. Compiles only when `com.neoxider.coreaihub` is installed (`defineConstraints: COREAI_HAS_HUB`, set by a `versionDefines` entry on that package). |
| `CoreAI.Mods.Editor` | `Editor/` | The `.luau` importer, the Lua `TextAsset` inspector, **CoreAI → Lua Script Viewer**, and the **CoreAI → Materials** texture tools. |
| `CoreAI.Mods.Tests`, `CoreAI.Mods.PlayModeTests` | `Tests/` | EditMode and PlayMode tests. |

## Main entry points

- **`CoreAiModsLifetimeScope`** — a child VContainer `LifetimeScope`. Parent it to the scene's
  `CoreAILifetimeScope`. Its inspector holds the Full-tier grant (off by default), allowed scenes,
  an optional Full-tier blacklist policy, an optional `RbxWorldHost`, character motor provider and
  network bridge provider, the Lua coroutine resume budget, and the mod store id.
- **`CoreAiModsInstaller.RegisterCoreAiMods(builder, ...)`** — the same registration from code. When
  the container is built it adds these tools to the built-in Programmer role: `execute_lua`
  (`LuaLlmTool`; one-off snippets run in `LuaCsGameToolExecutor`), `manage_mods`
  (`LuaModsLlmTool`), `get_mod_logs` (`GetModLogsLlmTool`), and the world tools `save_world`,
  `load_world`, `list_autosaves`, `load_autosave`. It also adds the skills `Lua Modding`, `Rbx API`
  and `Full Lua`; their text comes from the Resources assets `AgentSkills/LuaModding`,
  `AgentSkills/RbxApi` and `AgentSkills/FullLua` (shipped in `Assets/CoreAiUnity/Resources/`) when
  present, otherwise from the built-in constants.
- **`LuaCsModRuntime`** (`ILuaModRuntime`) — the persistent mod runtime; `LuaCsModRuntimeFactory`
  builds it from `LuaCsModStackOptions`.
- **`LuaCsSecureEnvironment`** — creates the sandboxed Lua-CSharp states and their execution guards.
- **`RbxWorldHost`** — scene component that materializes the Rbx world as GameObjects. Without it the
  Rbx world runs headless in memory.
- **`BundledModSeeder`** + **`ResourcesBundledModSource`** — install the `.lua` mods found in any
  `Resources/CoreAIMods/` folder into the mod store at startup.
- **`CoreAiLuaModAutoRepair`** — optional component that schedules a Programmer repair task when a
  loaded mod keeps failing.

## Hub integration

With `com.neoxider.coreaihub` installed, `CoreAI.Mods.Hub` compiles and **CoreAI → Setup → Add Hub**
adds `CoreAiModsHubBinder` to the Hub. The binder finds the scene's `CoreAiModsLifetimeScope` and
registers the **Mods** tab (sub-tabs **Mods** and **Logs**) and the **World Loads** page, and upgrades
the Settings and Statistics pages with live sources. Setup also adds the optional
`WorldStateHubBinder` when that type is available; it registers **World** when a world manager is
resolved. A custom Hub can add the binder explicitly. See [the Hub README](../CoreAIHub/README.md).

## Host gameplay and world-package storage

A host can expose its own Lua API through `ICoreAiLuaGameplayBindingsProvider`. Assign explicit
components to `CoreAiModsLifetimeScope`'s **Gameplay bindings providers** list. Each provider must
respect the effective `LuaCapabilities` supplied at registration and be safe before `Start`:
persisted mods can rehydrate during container construction. The captured provider set applies to
persistent mods, one-off Lua and replacement worlds. The default composition installs no custom
gameplay API. The ModdableUnits demo shows an opt-in provider for its own unit controller.

`CoreAiModsLifetimeScope.SetWorldPackageStore(IRbxWorldPackageStore)` supplies a host-owned
world-package store before the Mods container builds. It rejects null and calls after construction;
its value is runtime-only and is not serialized into the scene. Custom installer composition can
supply the same existing store port directly. The host owns the store's persistence and lifetime
contract; selecting a store does not confirm a save or browser durability.

## Documentation

Start here:

- [FIRST_MOD.md](../CoreAI/Docs/FIRST_MOD.md) — write, load, persist and share a first mod.
- [RBX_API.md](../CoreAI/Docs/RBX_API.md) — the Roblox-compatible API that mods build with.
- [LUA_GAME_API.md](../CoreAI/Docs/LUA_GAME_API.md) — every mod-runtime binding, tiers, transactions.

Lua runtime and security:

- [LUA_ACCESS_MODES.md](../CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers and Full mode.
- [LUA_SANDBOX_SECURITY.md](../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) — the sandbox boundary and budgets.
- [LUA_NATIVE_APIS.md](../CoreAI/Docs/LUA_NATIVE_APIS.md) — what Lua-CSharp provides and what CoreAI adds.
- [LUA_BEST_PRACTICES.md](../CoreAI/Docs/LUA_BEST_PRACTICES.md) — patterns and anti-patterns.

Mod system:

- [mod-authoring.md](../../Docs/CoreAIMods/mod-authoring.md) — authoring guide (header, API, limits).
- [mod-system.md](../../Docs/CoreAIMods/mod-system.md) — manifests, bundled mods, Hub, quarantine, reload modes.
- [MOD_SHARING.md](../../Docs/CoreAIMods/MOD_SHARING.md) — the export bundle and safe import.
- [WORLD_PACKAGE.md](../../Docs/CoreAIMods/WORLD_PACKAGE.md) — world save/load, autosaves, startup world.
- [CHARACTER_MOTOR_BRIDGE.md](../../Docs/CoreAIMods/CHARACTER_MOTOR_BRIDGE.md) — drive Rbx characters with your own controller.
- [SCRIPT_ENGINE_SEAM.md](../../Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md) — the engine-neutral scripting contracts.
- [RBX_API_SKILL.md](../../Docs/CoreAIMods/RBX_API_SKILL.md) — the `Rbx API` agent skill.
- [CASTLE_QUEST_PARITY.md](../../Docs/CoreAIMods/CASTLE_QUEST_PARITY.md) — a Roblox Studio / CoreAI parity fixture.

Roblox reference (normative behaviour the Rbx API follows):

- [01 Scripts and scheduler](../../Docs/CoreAIMods/RobloxReference/01_SCRIPTS_AND_SCHEDULER.md),
  [02 Multiplayer and replication](../../Docs/CoreAIMods/RobloxReference/02_MULTIPLAYER_REPLICATION.md),
  [03 Services and data](../../Docs/CoreAIMods/RobloxReference/03_SERVICES_AND_DATA.md).

Package-local notes:

- [Runtime/Logging/README.md](Runtime/Logging/README.md) — mod logging.
- [Runtime/LuauDownlevel/README.md](Runtime/LuauDownlevel/README.md) — the Luau rewriter.
- [Runtime/RbxApi/Instances/README.md](Runtime/RbxApi/Instances/README.md) — the instance tree.
- [PROCEDURAL_MATERIALS.md](Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) and
  [TEXTURE_MATERIALS.md](Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) — `Enum.Material` rendering.
- Third-party notices: [CC0 textures](Runtime/RbxApi/Unity/Resources/CoreAIRbxTextures/LICENSE.md),
  [NoiseShader](Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials/NoiseShader/UPSTREAM.md).
- Portable (no Unity) runs of the Lua-tier tests: [tools/portable/LuaTests](../../tools/portable/LuaTests/README.md).

What is built and what is planned: [ROBLOX_API_ROADMAP.md](../../Docs/CoreAIMods/ROBLOX_API_ROADMAP.md).

## Runtime source editor

The [runtime code editor and interpreter API](../../Docs/CoreAI/CODE_EDITOR.md) supports Lua
by default, an independent Python editing stub, bounded presentation and explicit Save & run.
Hosts can replace the portable language/interpreter ports and the Unity view independently.
