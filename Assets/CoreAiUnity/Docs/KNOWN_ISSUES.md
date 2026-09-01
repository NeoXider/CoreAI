# Known issues

This file tracks accepted warning debt and project-level issues that are not runtime regressions.

## LLMUnity native initialization on WebGL is contained at build time, not fixed upstream

Symptom: an unguarded WebGL player can abort during startup in
`UndreamAI.LlamaLib.LlamaLib.GetPlatform()` before CoreAI composition chooses an HTTP or Offline
backend. A local GGUF model cannot run in a browser player.

Cause: the installed LLMUnity runtime assembly owns an eager initializer whose native platform probe
does not support WebGL. CoreAI runtime DI cannot reliably run before an initializer in another
assembly.

Current containment:

- Browser composition never registers `ILlmAgentProvider` or LLMUnity autostart. `LocalModel`
  requests return `LocalModelPlatformSupport.BrowserUnavailableMessage`; HTTP and Offline remain
  available, and runtime hot-swap does not require a local provider.
- The Hub removes local-model choices on unsupported players, disables a persisted local selection,
  and shows the browser limitation explicitly.
- Unsupported player builds remove LLMUnity behaviours from staged scene copies and rewrite only the
  reported `Temp/StagingArea/Data/Managed/undream.llmunity.Runtime.dll` for the current callback. The
  callback rejects persistent Bee caches, fails closed when that staged DLL is absent, never rewrites Assets/Packages/editor/package-cache
  assemblies, and re-reads the output to require a return-only initializer.
- A runtime guard disables LLMUnity behaviours found after bootstrap without disabling unrelated
  components on the same GameObject. This is secondary containment for late-created hosts; it cannot
  guarantee interception before an arbitrary third-party `Awake` or static initializer.

Remaining limitation: the Cecil rewrite is an Editor build-compatibility step, not the desired
RUNTIME-first architecture. The complete fix requires an upstream LLMUnity platform gate, an
unsupported-target assembly split, or a maintained fork that omits native initialization in WebGL.
Until then, a Unity staging-layout change intentionally fails the build instead of risking a source
assembly mutation or shipping an unverified initializer.

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
- Lua remains compiled out unless the build defines `COREAI_LUA` (see DEVELOPER_GUIDE.md / LUA_SANDBOX_SECURITY.md); enable it explicitly for WebGL players that need scripting.

## Warning handling policy

- New compile errors block merges.
- New warnings must not be hidden inside accepted warning debt.
- If a warning is accepted debt, document the reason, owner, and follow-up plan here.

## CAIU001 ConfigureAwait warnings in CoreAiUnity

Symptom: the bundled Roslyn analyzer reports `CAIU001: Do not use ConfigureAwait(false) in CoreAiUnity` for async file I/O in `FileAgentMemoryStore` and related stores.

Cause: the async store paths intentionally run on the thread pool and never touch UnityEngine APIs after the await, so `ConfigureAwait(false)` is correct there; the analyzer flags it assembly-wide by design.

Impact: warning debt only; not a runtime regression.

Recommended follow-up: suppress per-file/per-call with a justification comment, or teach the analyzer to allow thread-pool-only code paths.
