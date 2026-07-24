# CoreAI — Installation

CoreAI ships as **six UPM packages**. `coreai` + `coreaiunity` are the required base (lockstep semver);
`coreaimods`, `coreaihub`, `coreaibenchmark`, and `coreaimcp` are optional installs on top of the base:

| Package | What it is | Depends on |
|---|---|---|
| `com.neoxider.coreai` | Portable C# core (orchestration, tools, memory, routing). No `UnityEngine`. | — |
| `com.neoxider.coreaiunity` | Unity layer (MonoBehaviours, LLM clients, chat UI, editor menus). | `coreai` |
| `com.neoxider.coreaimods` | Optional Lua modding layer (Lua-CSharp sandbox, `execute_lua`/`manage_mods` tools, mod runtime). | `coreai` + `coreaiunity` |
| `com.neoxider.coreaihub` | Optional UI Toolkit Hub window (tabbed Chat/Settings/Statistics/Mods pages). | `coreai` + `coreaiunity` |
| `com.neoxider.coreaibenchmark` | Dev/test-only LLM game-creation benchmark harness. | `coreai` + `coreaiunity` + `coreaimods` |
| `com.neoxider.coreaimcp` | Optional in-game **MCP server** — an external MCP client (Claude Code, …) drives the *running* game over loopback HTTP. Off by default. | `coreaiunity` + `coreaimods` |

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

**LLM** (Microsoft.Extensions.AI) is an **optional module within the base**. The core compiles
without it; features light up automatically when the NuGet package is present. Install only what your game uses.

Requirements: **Unity 6000.0+**.

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

> By default the LLM pipeline is compiled in and needs the NuGet DLLs from section **2** —
> without them you get compile errors on `Microsoft.Extensions.AI`. To build without LLM, see 2.3.

---

## 2. LLM module (Microsoft.Extensions.AI, via NuGet)

The LLM pipeline uses [Microsoft.Extensions.AI](https://www.nuget.org/packages/Microsoft.Extensions.AI).
It is delivered through **NuGetForUnity**, not UPM, so it does not arrive with the two CoreAI packages.

### 2.1 Install (recommended: NuGetForUnity)

Install [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity), then install a **single** package:

```
Microsoft.Extensions.AI
```

NuGetForUnity resolves the rest of the chain automatically as transitive dependencies
(`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Bcl.AsyncInterfaces`, `System.Text.Json`,
`System.Text.Encodings.Web`, `System.Numerics.Tensors`, `Microsoft.Extensions.Logging.Abstractions`,
`Microsoft.Extensions.DependencyInjection.Abstractions`, `System.Diagnostics.DiagnosticSource`,
`Microsoft.Extensions.Primitives`, `System.Threading.Channels`, …). You do **not** install those by hand.

### 2.2 Alternative: copy the DLLs

Clone this repo and copy the entire `Assets/Packages/` folder into your project. It already contains the
restored DLLs; the `packages.config` / `NuGet.config` manifests live at `Assets/packages.config` and
`Assets/NuGet.config`.

### 2.3 Building without LLM

If you do not want the LLM pipeline (and do not want the NuGet DLLs), add the scripting define
`COREAI_NO_LLM` (Player Settings → Scripting Define Symbols). The LLM code compiles out and the DLLs
are no longer required. HTTP/OpenAI-compatible code is gated by the same define.

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

Lua is compiled in by default: with the `coreaimods` package present, the Lua sandbox,
`execute_lua`/`manage_mods` tools, and the mod runtime are available automatically. Remove the
`coreaimods` package (or set `COREAI_NO_LUA`) and the Lua code compiles out with no errors elsewhere
in the project.

### 3.2 Switches

- **Soft-disable** (keep the package, compile Lua out): scripting define `COREAI_NO_LUA`, or
  `CoreAI → Setup → Modules → Lua (Lua-CSharp) → Disable Lua`.
  Runtime code is guarded by `#if !COREAI_NO_LUA`.
- **WebGL:** Lua on the WebGL player is **on by default** (new settings assets); toggle with
  `CoreAISettingsAsset.EnableLuaOnWebGl`. Lua-CSharp is AOT-safe, so it runs under IL2CPP/WebGL
  without extra stripping protection; the Full `unity_*` reflection tier stays disabled on WebGL.
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
| Mods (Lua) | `com.neoxider.coreaimods` (Lua-CSharp bundled) | — (compiled in by default) | `COREAI_NO_LUA` (soft-disable) |
| Hub (UI Toolkit) | `com.neoxider.coreaihub` | `COREAI_HAS_HUB` (consumed by Mods' Hub integration) | — |
| Benchmark | `com.neoxider.coreaibenchmark` (needs Mods) | — | dev/test-only, not referenced by runtime code |
| MCP server | `com.neoxider.coreaimcp` (needs Mods) — git URL `https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMcp` | — | off until a `CoreAiMcpServer` component is added to a scene; loopback-only, no auth — see [its README](Assets/CoreAIMcp/README.md) |
| Local LLM | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | — |
| LLM pipeline (MEAI) | NuGet `Microsoft.Extensions.AI` | — (on by default) | `COREAI_NO_LLM` (compile out) |

Check the current state any time:

```
CoreAI → Setup → Modules → Report module status
```

---

## 7. Verify

1. No compile errors in the Console.
2. `CoreAI → Setup → Create Chat Demo Scene` → press **Play** → type a message.
3. Configure the backend in `CoreAI → Settings` (local LLMUnity model, or an OpenAI-compatible endpoint).
