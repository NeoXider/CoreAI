# Optional modules: Mods/Lua, Hub, and LLMUnity

CoreAI ships as seven lockstep UPM packages: `coreai`, `coreaiunity`, optional `coreaimods`,
`coreaihub`, `coreaibenchmark`, `coreaimcp`, and `coreaimirror`. `com.neoxider.coreai` is the portable
core and `com.neoxider.coreaiunity` is the Unity host. Lua/modding, Hub UI, the in-game MCP server,
Mirror networking and the benchmark are separate packages so a consumer installs only the surfaces it uses.

| Module | Package/dependency | Auto-define when installed | Manual switch |
|---|---|---|---|
| Lua mods | `com.neoxider.coreaimods` (Lua-CSharp bundled) | — | `COREAI_LUA` (positive enable; absent by default) |
| Hub UI | `com.neoxider.coreaihub` | `COREAI_HAS_HUB` in Mods/Hub integration | remove Hub package |
| LLM providers (HTTP/MEAI/LLMUnity clients) | inside `com.neoxider.coreaiunity`; the NuGet `Microsoft.Extensions.AI` DLLs are required in every configuration | — | `COREAI_LLM` (positive enable; absent by default) |
| Local inference | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | also requires `COREAI_LLM` |
| Benchmark | `com.neoxider.coreaibenchmark` | none | do not install in players |
| In-game MCP server | `com.neoxider.coreaimcp` (depends on Unity + Mods) | none | remove the package |
| Mirror networking | `com.neoxider.coreaimirror` (depends on Core + Mods) | assemblies build only with `MIRROR` (defined by the Mirror package) | remove the package or Mirror |

`CoreAI.Core.asmdef` has no Lua reference. Lua VM/sandbox implementations and their
Lua-CSharp dependencies live in `CoreAI.Mods`. `CoreAI.Source` owns the guarded LLMUnity
adapter. The benchmark depends on Core, Unity, and Mods because G1-G8 execute real Lua tools.

## Editor module tool

**CoreAI > Setup > Modules** contains:

| Menu item | What it does |
|---|---|
| **LLM Providers > Enable Providers** / **Disable Providers** | Adds / removes the `COREAI_LLM` scripting define. |
| **Lua (Lua-CSharp) > Enable Lua** / **Disable Lua** | Adds / removes the `COREAI_LUA` scripting define. The Lua-CSharp runtime ships inside `com.neoxider.coreaimods`; nothing extra is installed. |
| **LLMUnity > Enable + Update to latest** | Adds `ai.undream.llm` from its Git URL through the Package Manager, or re-resolves it to the latest commit when it is already installed. |
| **LLMUnity > Remove package** | Removes `ai.undream.llm` after a confirmation; `COREAI_HAS_LLMUNITY` unsets. |
| **Report module status** | Shows whether `COREAI_LLM` and `COREAI_LUA` are set and which LLMUnity version is installed. |

The define items change the scripting defines of the **currently selected build target** only; repeat them
after switching to another platform you ship. Package operations trigger a domain reload; wait for
compilation before editing scenes or running tests.

**CoreAI > Setup > Install Git Dependencies** (outside the Modules submenu) adds the missing VContainer,
MessagePipe, UniTask and LLMUnity entries to `Packages/manifest.json` and leaves existing entries unchanged.
Production projects should pin every Git URL to a reviewed tag or commit.

## Verification boundary

CI compiles the project and runs the EditMode suite in the `core`, `llm`, `lua`, and `full` define
configurations. Physically removing each
optional package from a consumer project (Base, +Mods, +Hub, Full/Benchmark, no-Lua, no-Hub) is not covered
by CI, so check your own package set in your project.

## Scene ownership

Lua scenes require an active `CoreAiModsLifetimeScope` child under `CoreAILifetimeScope`; Lua services
are not registered in the core container. Hub scenes require the Hub package and valid UXML/chat
assets. Removing an optional package from a project also requires removing or conditionally compiling
scene components that belong to that package.
