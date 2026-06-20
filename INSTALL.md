# CoreAI — Installation

CoreAI ships as two UPM packages that share the same version (lockstep semver):

| Package | What it is |
|---|---|
| `com.nexoider.coreai` | Portable C# core (orchestration, tools, memory, sandbox). No `UnityEngine`. |
| `com.nexoider.coreaiunity` | Unity layer (MonoBehaviours, LLM clients, chat UI, editor menus). Depends on the core. |

**LLM** (Microsoft.Extensions.AI) and **Lua** (MoonSharp) are **optional modules**. The core compiles
without them; features light up automatically when the package is present. Install only what your game uses.

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
restored DLLs (plus the `packages.config` / `NuGet.config` manifests).

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

## 3. Lua module (MoonSharp)

Lua scripting (sandbox, AI-written gameplay scripts, mods) needs the MoonSharp package.

### 3.1 Install

```
CoreAI → Setup → Modules → MoonSharp (Lua) → Enable + Update to latest
```

(or manifest: `"org.moonsharp.moonsharp": "https://github.com/moonsharp-devs/moonsharp.git?path=/interpreter#upm/beta/v3.0"`).

The package's presence **automatically** sets the `COREAI_HAS_MOONSHARP` version-define — nothing to
configure by hand. Remove the package and all Lua code compiles out with no errors.

### 3.2 Switches

- **Soft-disable** (keep the package, compile Lua out): scripting define `COREAI_NO_LUA`, or
  `CoreAI → Setup → Modules → MoonSharp (Lua) → Disable Lua (keep package)`.
  Runtime code is guarded by `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA`.
- **WebGL:** Lua on the WebGL player is **on by default** (new settings assets); toggle with
  `CoreAISettingsAsset.EnableLuaOnWebGl`. IL2CPP stripping protection (`link.xml` preserving
  `MoonSharp.Interpreter`) ships in the package; the Full `unity_*` reflection tier stays disabled on WebGL.

---

## 4. Optional modules at a glance

| Module | Package | Auto-define when installed | Manual switch |
|---|---|---|---|
| Lua | `org.moonsharp.moonsharp` | `COREAI_HAS_MOONSHARP` | `COREAI_NO_LUA` (soft-disable) |
| Local LLM | `ai.undream.llm` | `COREAI_HAS_LLMUNITY` | — |
| LLM pipeline (MEAI) | NuGet `Microsoft.Extensions.AI` | — (on by default) | `COREAI_NO_LLM` (compile out) |

Check the current state any time:

```
CoreAI → Setup → Modules → Report module status
```

---

## 5. Verify

1. No compile errors in the Console.
2. `CoreAI → Setup → Create Chat Demo Scene` → press **Play** → type a message.
3. Configure the backend in `CoreAI → Settings` (local LLMUnity model, or an OpenAI-compatible endpoint).
