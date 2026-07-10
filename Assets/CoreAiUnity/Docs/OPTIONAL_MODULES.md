# Optional modules: Mods/Lua, Hub, and LLMUnity

CoreAI ships as five lockstep UPM packages. `com.neoxider.coreai` is the portable core and
`com.neoxider.coreaiunity` is the Unity host. Lua/modding, Hub UI, and the benchmark are separate
packages so a consumer installs only the surfaces it uses.

| Module | Package/dependency | Auto-define | Manual opt-out |
|---|---|---|---|
| Lua mods | `com.neoxider.coreaimods` + `org.moonsharp.moonsharp` | `COREAI_HAS_MOONSHARP` | `COREAI_NO_LUA` |
| Hub UI | `com.neoxider.coreaihub` | `COREAI_HAS_HUB` in Mods/Hub integration | remove Hub package |
| Local inference | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | `COREAI_NO_LLM` |
| Benchmark | `com.neoxider.coreaibenchmark` | none | do not install in players |

`CoreAI.Core.asmdef` has no MoonSharp reference. Lua VM/sandbox implementations and their
MoonSharp/Lua-CSharp dependencies live in `CoreAI.Mods`. `CoreAI.Source` owns the guarded LLMUnity
adapter. The benchmark depends on Core, Unity, and Mods because G1-G8 execute real Lua tools.

## Editor module tool

Use **CoreAI > Setup > Modules** to report module state, soft-disable Lua, or install/remove backend
dependencies. Package operations trigger a domain reload; wait for compilation before editing scenes
or running tests. **Install Git Dependencies** adds missing project dependencies, but production
projects should pin every Git URL to a reviewed tag or commit.

## Verification boundary

The monorepo verifies enabled modules and the soft-disable defines. Full package-removal consumers
(Base, +Mods, +Hub, Full/Benchmark, no-Lua, and no-Hub) remain a release-engineering item under
R0.6/F-22 in the root `TODO.md`. Until that matrix is automated, do not claim that physical removal
of every optional package is continuously proven.

## Scene ownership

Lua scenes require an active `CoreAiModsLifetimeScope` child under `CoreAILifetimeScope`; Lua services
are not registered in the core container. Hub scenes require the Hub package and valid UXML/chat
assets. Removing an optional package from a project also requires removing or conditionally compiling
scene components that belong to that package.
