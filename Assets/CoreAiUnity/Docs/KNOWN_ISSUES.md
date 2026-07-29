# Known issues

This file tracks accepted warning debt and project-level issues that are not runtime regressions.

## FullAccess demo: "Start Tetris" throws

Symptom: pressing **Start Tetris** in the FullAccess demo reports
`Tetris load failed: coreai_world_spawn: coreai_world_spawn requires the WorldEdit build bindings,
which are disabled for this mod`.

Cause: `LuaPlatformExampleController.cs` embeds a Tetris written against the classic
`coreai_world_*` build API. The default composition (`CoreAiModsInstaller`) sets
`RegisterWorldEditBuildBindings = false`, so those functions are withheld stubs in every shipping
game — the demo source was never migrated. The demo's self-test path is unaffected and still passes.

Workaround: the bundled **`sample_tetris3d`** mod is the same game written in the
[Rbx API](../../CoreAI/Docs/RBX_API.md); enable it from the **Hub → Mods** tab.

Recommended follow-up: rewrite the embedded demo source on `Instance.new`, or have the demo load the
bundled mod instead of carrying its own copy.

The **LuaMods demo** has the same defect: `WaveDirectorMod.lua.txt` builds its wave through
`coreai_world_begin` / `coreai_world_spawn` / `coreai_world_commit` and recolors through
`coreai_world_set_color`, so pressing **Emit 'wave_started'** raises the same withheld-API error.
Its store, hooks, events and `coreai_world_exists` halves still work.

## LLMUnity throws on WebGL startup

Symptom: a WebGL player logs `ArgumentException: Unknown platform Unix <version>` from
`UndreamAI.LlamaLib.LlamaLib.GetPlatform()` during startup.

Cause: LLMUnity's native platform probe does not recognise the triple the WebGL player reports.

Impact: noise only. A local GGUF model cannot run in a browser player regardless; CoreAI itself
starts normally and an OpenAI-compatible HTTP backend works.

Recommended follow-up: skip the LLMUnity module entirely for WebGL builds (do not install
`ai.undream.llm`, or guard its activation behind a non-WebGL check).

## Legacy log-settings migration hides Lua mod errors

Symptom: after upgrading a project whose log filter was set to "everything", errors raised by Lua
mods stop appearing in the console.

Cause: `GameLogSettingsAsset.TryMigrateFeatures` widens a legacy preset to
`GameLogFeature.AllBuiltIn`, which does **not** include `CustomA` — and `LuaLogService` mirrors mod
errors under `CustomA`. Freshly created settings assets default to `GameLogFeature.All` and are not
affected.

Workaround: set the feature filter back to **All** in the Game Log Settings asset.

## Windows IL2CPP needs the MSVC C++ toolchain

Symptom: a Windows Standalone IL2CPP build fails with
`Unity.IL2CPP.Bee.BuildLogic.ToolchainNotFoundException`, reporting a Visual Studio installation
"without C++ tool components" and/or a missing Windows SDK.

Cause: IL2CPP compiles generated C++ with MSVC; the Visual Studio workload that ships it is not
installed by default.

Fix: install the **Desktop development with C++** workload (MSVC toolset) plus **Windows SDK
10.0.19041** or newer. Android and WebGL already build with IL2CPP and need no extra install.

## CS8632 nullable warnings

Symptom: Unity/C# compiler reports CS8632: nullable reference annotations are used outside a nullable context.

Cause: some files use nullable annotations while the assembly or file does not enable nullable context.

Impact: warning debt only; not a runtime regression.

Recommended follow-up:

- Choose a nullable policy per asmdef or file.
- Either enable nullable context or remove nullable annotations from affected files.
- Do not mix this cleanup into unrelated feature changes.

## PathTracing render pipeline warning

Symptom: Unity warning references UnityEngine.PathTracing.Core.WorldRenderPipelineResources or UniversalRenderPipelineGlobalSettings.

Cause: Unity, URP, or PathTracing package settings mismatch in project render pipeline global settings.

Impact: editor/package warning; does not affect CoreAI runtime directly. The stale reference present in this repository was removed in v2.6.0, but the warning can return after Unity/URP package changes if the editor rewrites global render pipeline settings.

Recommended follow-up:

- Check Unity and URP package versions.
- Check project render pipeline global settings.
- Remove or reassign obsolete render pipeline resource references if Unity created stale settings.

## WebGL Lua execution (resolved)

Symptom (historical): Lua tools or Lua envelope processing reported that Lua execution was unavailable in a WebGL player.

Cause (historical): v2.6.0 explicitly disabled the then-current Lua sandbox (a different third-party interpreter, since replaced) in WebGL player builds. That path could initialize reflection-based loader code that aborts WebGL/IL2CPP before managed exception handling.

Resolution: the Lua VM was replaced end-to-end by **Lua-CSharp**, a managed, AOT-safe runtime that works on IL2CPP and WebGL without reflection-based loading. WebGL Lua execution through `SecureLuaEnvironment` is supported on WebGL player builds and **on by default**; toggle with `CoreAISettingsAsset.EnableLuaOnWebGl`. See ARCHITECTURE.md.

Recommended follow-up:

- Add or extend WebGL Player tests to keep covering the Lua-CSharp path on IL2CPP.
- You can still compile Lua out entirely with the `COREAI_NO_LUA` scripting define (see DEVELOPER_GUIDE.md / LUA_SANDBOX_SECURITY.md) — useful for WebGL or any build that does not need scripting.

## Warning handling policy

- New compile errors block merges.
- New warnings must not be hidden inside accepted warning debt.
- If a warning is accepted debt, document the reason, owner, and follow-up plan here.

## CAIU001 ConfigureAwait warnings in CoreAiUnity

Symptom: the bundled Roslyn analyzer reports `CAIU001: Do not use ConfigureAwait(false) in CoreAiUnity` for async file I/O in `FileAgentMemoryStore` and related stores.

Cause: the async store paths intentionally run on the thread pool and never touch UnityEngine APIs after the await, so `ConfigureAwait(false)` is correct there; the analyzer flags it assembly-wide by design.

Impact: warning debt only; not a runtime regression.

Recommended follow-up: suppress per-file/per-call with a justification comment, or teach the analyzer to allow thread-pool-only code paths.
