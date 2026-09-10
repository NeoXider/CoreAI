# CoreAI — Installation

Choose your environment: **Unity** with ready-made adapters and chat, or a **plain .NET host** with the portable core. For .NET start with the [DLL build](tools/portable/README.md) and the [console sample](examples/dotnet/README.md); Unity and UPM are not needed there.

Seven **UPM packages** are available for Unity. The base is `coreai` + `coreaiunity`; the rest are added per task. Package versions in one installation must match a single release.

| Package | Purpose | CoreAI dependencies |
|---|---|---|
| `com.neoxider.coreai` | Portable C# core: agents, tools, memory, and routing; no `UnityEngine` | — |
| `com.neoxider.coreaiunity` | Unity host, chat, settings, DI, and platform adapters | `coreai` |
| `com.neoxider.coreaimods` | Lua sandbox, mods, and game-world tools | `coreai` + `coreaiunity` |
| `com.neoxider.coreaihub` | Chat / Settings / Statistics / Mods panel | `coreai` + `coreaiunity` |
| `com.neoxider.coreaibenchmark` | Developer game-scenario benchmark | `coreai` + `coreaiunity` + `coreaimods` |
| `com.neoxider.coreaimcp` | MCP server inside the running game; an external MCP client connects to it | `coreaiunity` + `coreaimods` |
| `com.neoxider.coreaimirror` | Optional networked-world transport | `coreai` + `coreaimods`, Mirror separately |

Mirror is connected via URL `https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMirror`. An installed Mirror with the `MIRROR` symbol is required; the package does not include Mirror itself. See the [package manifest](Assets/CoreAIMirror/package.json).

A Git URL without `#tag` tracks the branch. For a reproducible install, pin the packages to one existing tag from the repository. Current versions are in `package.json`, published changes in the [changelog](Assets/CoreAI/CHANGELOG.md); Unreleased is not a finished release.

Detailed Unity installation steps follow below; part of the guide is still in English.
Mods and Hub are installed independently — neither requires the other. When both are present, Mods'
Hub integration assembly (`CoreAI.Mods.Hub`) auto-enables via the `COREAI_HAS_HUB` version define and
adds a Mods page to the Hub window; without Hub, that assembly compiles out and Mods still works
standalone through its tools. Benchmark and MCP both build on Mods, so installing either pulls
`coreaimods` in as well.

**Install profiles** — jump to the matching section below:

| Profile | Packages | Section |
|---|---|---|
| **Base** | `coreai` + `coreaiunity` | [§1](#1-install-coreai-base) + [§2](#2-llm-module-microsoftextensionsai-via-nuget) |
| **+Mods** | Base + `coreaimods` | [§3](#3-mods-module-lua) |
| **+Hub** | Base + `coreaihub` | [§4](#4-hub-module-ui-toolkit) |
| **+MCP** | Base + `coreaimods` + `coreaimcp` | [§3](#3-mods-module-lua) + [§6](#6-optional-modules-at-a-glance) |
| **Full** | Base + `coreaimods` + `coreaihub` (+ `coreaibenchmark` for local model evaluation, `coreaimcp` for external MCP clients) | [§3](#3-mods-module-lua) + [§4](#4-hub-module-ui-toolkit) + [§5](#5-benchmark-module-dev-only) |

**Provider-backed LLM implementations** are an optional compile module within the base. Portable
orchestration, scripted/stub clients, chat contracts/UI, and MEAI-based public tool contracts remain
in the base in every configuration. The required Microsoft.Extensions.AI assemblies therefore remain
a Core dependency; `COREAI_LLM` controls the concrete HTTP/MEAI/LLMUnity provider implementations,
not whether orchestration or chat types exist.

Requirements: **Unity 6000.0+**. Custom UI Toolkit elements use the `[UxmlElement]`
/ `[UxmlAttribute]` UXML path — available since Unity 6000.0 and the only one in Unity 6.6+,
where the legacy `UxmlFactory` / `UxmlTraits` were removed from the engine.

---

## 1. Install CoreAI (base)

### 1.1 Git dependencies

Unity Package Manager does not pull every transitive Git dependency. After CoreAiUnity is in the project,
the easiest way is the editor menu:

```
CoreAI → Setup → Install Git Dependencies
```

It merges only the *missing* keys into `Packages/manifest.json` (your pinned versions stay untouched).

**Manual alternative** — add these under `"dependencies"` in `Packages/manifest.json`:

```json
"jp.hadashikick.vcontainer": "https://github.com/hadashiA/VContainer.git?path=VContainer/Assets/VContainer#1.17.0",
"com.cysharp.messagepipe": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe",
"com.cysharp.messagepipe.vcontainer": "https://github.com/Cysharp/MessagePipe.git?path=src/MessagePipe.Unity/Assets/Plugins/MessagePipe.VContainer",
"com.cysharp.unitask": "https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask"
```

### 1.2 The CoreAI packages

**Window → Package Manager → `+` → Add package from git URL…** and add both:

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

### 1.3 Create a scene

```
CoreAI → Setup → Create Chat Demo Scene     (chat UI + lifetime scope)
CoreAI → Setup → Create Bare Scene (advanced)   (scope + settings, no demo UI)
```

> The default no-symbol build keeps portable orchestration/chat and scripted/stub clients but has no
> provider-backed HTTP or LLMUnity execution. The required MEAI DLLs from section **2** remain part of
> the Core contract in either mode. Add `COREAI_LLM` when you want concrete providers; see 2.3.

### 1.4 If you build for the Web (WebGL)

```
CoreAI → Setup → Install WebGL Template
```

Browser storage is armed by one line in the web template —
`config.autoSyncPersistentDataPath = true;` before `createUnityInstance(...)` — and Unity's stock
templates ship it **commented out**. Without it, agent memory, skills, Lua mods, Lua version history
and world packages live in the tab's in-memory filesystem and disappear on reload, so
`CoreAIWebGlPersistentDataSyncBuildGuard` aborts the build. The menu item copies the template that
ships inside `com.neoxider.coreaiunity` into `Assets/WebGLTemplates/CoreAI` and selects it; adding the
line to your own template works equally well. A player that ships **without** CoreAI's persistent
storage on purpose opts out with the scripting define symbol `COREAI_WEBGL_NO_PERSISTENCE` on the Web
platform — the build then proceeds and logs one warning naming what is given up.

---

## 2. LLM module (Microsoft.Extensions.AI, via NuGet)

CoreAI's portable tool/chat contracts and provider adapters use
[Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI). The assemblies are
required by the base even when provider implementations are compiled out. They are delivered through
**NuGetForUnity**, not UPM, so they do not arrive with the two CoreAI packages.

### 2.1 Install (recommended: NuGetForUnity)

Install [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity), then install a **single** package:

```
Microsoft.Extensions.AI 9.10.2
```

**Take that version literally — it is a ceiling, not a starting point.** Unity ships its own
`System.Text.Json` (assembly version **8.0.0.0**) in the editor's BCL extensions and substitutes it for
any copy in your project. Every Microsoft.Extensions.AI **10.x** release is built against
`System.Text.Json` **10.0.0.0**, so it cannot load under Unity — the game fails on the first call into
MEAI rather than at compile time. 9.10.2 is the newest release built against the 8.0.0.0 line, which is
why this package and its consumers are pinned there.

CoreAI's own repository builds against that same version on purpose (`Assets/packages.config`,
`Assets/Packages/`, `tools/portable/CoreAI.Core.csproj`). That is what makes the rule enforceable: an API
that exists only in a newer MEAI fails to compile in CoreAI's own build — including the license-free
`portable-core` CI job — instead of turning up days later as a `CS0234` inside a game. The pins are held
together by `MeaiVersionFloorEditModeTests`; raising them is a deliberate act that starts with checking
that the engine can load the newer assemblies.

NuGetForUnity resolves the rest of the chain automatically as transitive dependencies
(`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Bcl.AsyncInterfaces`, `System.Text.Json`,
`System.Text.Encodings.Web`, `System.Numerics.Tensors`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, `System.Diagnostics.DiagnosticSource`,
`Microsoft.Extensions.Primitives`, `System.Threading.Channels`, …). You do **not** install those by hand.

### 2.2 Alternative: copy the DLLs

Clone this repo and copy the entire `Assets/Packages/` folder into your project. It already contains the
restored DLLs; the `packages.config` / `NuGet.config` manifests live at `Assets/packages.config` and
`Assets/NuGet.config`.

### 2.3 Enabling the LLM pipeline

The default build keeps portable orchestration, queueing, chat contracts/UI, scripted/stub clients, and
the MEAI-dependent public contracts. To compile concrete HTTP/OpenAI-compatible and LLMUnity provider
implementations, add the scripting define `COREAI_LLM` (Player Settings → Scripting Define Symbols),
or use **CoreAI → Setup → Modules → LLM Providers → Enable Providers**. Without the define,
provider transports/clients and their focused tests compile out while offline/scripted orchestration remains.
`COREAI_LUA` is independent; enable both symbols for the full provider + Lua runtime.

### 2.4 Local on-device models (optional)

To run a local GGUF model on-device, add the optional **LLMUnity** module:

```
CoreAI → Setup → Modules → LLMUnity → Enable + Update to latest
```

(or manifest: `"ai.undream.llm": "https://github.com/undreamai/LLMUnity.git"`). Its presence sets
`COREAI_HAS_LLMUNITY`. Skip it if you only call an OpenAI-compatible HTTP API.

---

## 3. Mods module (Lua)

Lua modding (sandbox, `execute_lua`/`manage_mods` tools, AI-written gameplay scripts, mod runtime) is
a separate package, `com.neoxider.coreaimods`, on top of the base. The Lua runtime is
[Lua-CSharp](https://github.com/nuskey8/Lua-CSharp) — a managed, AOT-safe VM (works on IL2CPP and
WebGL). It ships **bundled** as `Lua.dll` + `Lua.Annotations.dll` inside the package at
`Assets/CoreAIMods/Plugins/`, so there is **no external Lua package to install**.

### 3.1 Install the Mods package

**Window → Package Manager → `+` → Add package from git URL…**

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMods
```

Lua is a positive opt-in: with the `coreaimods` package present, add `COREAI_LUA` to enable the Lua
sandbox, `execute_lua`/`manage_mods` tools, and the mod runtime. Without the define, CoreAI keeps the
LLM pipeline but compiles Lua-dependent surfaces out. Remove the package as well when the project does
not need any modding assets or APIs.

### 3.2 Switches

- **Enable** (keep the package and compile Lua in): add scripting define `COREAI_LUA`, or use
  `CoreAI → Setup → Modules → Lua (Lua-CSharp) → Enable Lua`.
- **Disable** (the default): remove `COREAI_LUA`, or use
  `CoreAI → Setup → Modules → Lua (Lua-CSharp) → Disable Lua`.
  Lua-dependent runtime and tests are guarded by `#if COREAI_LUA`.
- **WebGL:** when `COREAI_LUA` is compiled in, Lua on the WebGL player is **on by default** in new
  settings assets; toggle the runtime allowance with `CoreAISettingsAsset.EnableLuaOnWebGl`.
  Lua-CSharp is AOT-safe, so it runs under IL2CPP; the Full
  `unity_*` reflection tier stays disabled on WebGL. Managed stripping is handled by the `link.xml`
  shipped in the CoreAiUnity package, which preserves the Lua VM, the CoreAI assemblies, the Rbx API
  assemblies and VContainer — verified on a WebGL/IL2CPP player at stripping level **Medium**, where
  the container, mod seeding and mod-driven `Instance.new` spawning all survive. **If your own game
  assemblies are resolved through DI or reflection, add them to your project's own `link.xml`** —
  CoreAI's only covers CoreAI's.
- **Hub integration:** if `com.neoxider.coreaihub` (§4) is also installed, Mods' `CoreAI.Mods.Hub`
  assembly auto-enables via the `COREAI_HAS_HUB` version define and adds a Mods page to the Hub
  window — no extra configuration needed either way.

---

## 4. Hub module (UI Toolkit)

The Hub is an optional tabbed UI Toolkit window (Chat, Settings, Statistics, Mods, and C#/Lua-authored
pages) rendered from CoreAI's `HubPageRegistry`. Without it, CoreAI still exposes the registry so you
can render pages on your own uGUI/UITK canvas via the API.

**Window → Package Manager → `+` → Add package from git URL…**

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIHub
```

No further setup — the Hub window picks up any pages already registered by the base and Mods packages.

---

## 5. Benchmark module (dev-only)

`com.neoxider.coreaibenchmark` is the LLM game-creation benchmark harness (scenario groups G1-G8,
scoring, role-fitness reports) used to evaluate model quality — it is a development/test-time tool,
not something a shipped game depends on.

**Window → Package Manager → `+` → Add package from git URL…**

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIBenchmark
```

See the [full benchmark guide](Assets/CoreAIBenchmark/README.md) for scenario details and how to run a
local multi-model sweep.

---

## 6. Optional modules at a glance

| Module | Package | Auto-define when installed | Manual switch |
|---|---|---|---|
| Mods (Lua) | `com.neoxider.coreaimods` (Lua-CSharp bundled) | — | `COREAI_LUA` (positive enable; absent by default) |
| Hub (UI Toolkit) | `com.neoxider.coreaihub` | `COREAI_HAS_HUB` (consumed by Mods' Hub integration) | — |
| Benchmark | `com.neoxider.coreaibenchmark` (needs Mods) | — | dev/test-only, not referenced by runtime code |
| MCP server | `com.neoxider.coreaimcp` (needs Mods) — git URL `https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMcp` | — | off until a `CoreAiMcpServer` component is added to a scene; loopback-only, no auth — see [its README](Assets/CoreAIMcp/README.md) |
| Local LLM | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | also requires `COREAI_LLM` |
| Provider-backed LLM implementations | NuGet `Microsoft.Extensions.AI` (required by Core contracts) | — | `COREAI_LLM` (positive enable; absent by default) |

Check the current state any time:

```
CoreAI → Setup → Modules → Report module status
```

---

## 7. Verify

1. No compile errors in the Console.
2. `CoreAI → Setup → Create Chat Demo Scene` → press **Play** → type a message.
3. Configure the backend in `CoreAI → Settings` (local LLMUnity model, or an OpenAI-compatible endpoint).
