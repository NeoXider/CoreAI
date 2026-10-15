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

Fix: enable a nullable context in the affected files, or remove the nullable annotations.

## `EnableLuaOnWebGl` has no effect

Symptom: setting `enableLuaOnWebGl` to `false` on `CoreAISettingsAsset` (the field is serialized but not
shown in the settings inspector; code reads it as `ICoreAISettings.EnableLuaOnWebGl` /
`CoreAISettings.EnableLuaOnWebGl`) does not stop Lua in a WebGL player.

Cause: no Lua runtime reads the setting. Lua runs through Lua-CSharp (`LuaCsSecureEnvironment`,
`com.neoxider.coreaimods`), a managed AOT-safe runtime, in any scene that composes
`CoreAiModsLifetimeScope` (or calls `CoreAiModsInstaller.RegisterCoreAiMods`), on WebGL as on every
other player.

Workaround: a WebGL scene that must not run Lua should not compose the mods scope (see
[LUA_SANDBOX_SECURITY.md](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md), "Optional Module").

## CAIU001 ConfigureAwait warnings in CoreAiUnity

Symptom: the bundled Roslyn analyzer reports `CAIU001: Do not use ConfigureAwait(false) in CoreAiUnity`
for the gate awaits in `FileAgentMemoryStore`.

Cause: every occurrence there awaits the acquisition or release of the store's file gate or a per-role
mutation gate. Without `ConfigureAwait(false)` the continuation that releases the gate is posted to the
caller's `SynchronizationContext` while a synchronous `Save`/`TryLoad` on that same thread blocks on the
gate, a permanent deadlock that reproduces in the editor. On WebGL the work runs inline, so the awaits are
already complete and schedule nothing. The analyzer flags the call regardless (it covers `CoreAiUnity`
`Runtime/` and `Editor/` code).

Impact: warning debt only; not a runtime regression.

Scope note: the analyzer covers `CoreAiUnity` only. The engine-free tool path (`CoreAI.Core`,
`CoreAI.Mods`) is guarded by `WebGlUnsafeAsyncPrimitivesEditModeTests`, which forbids
`ConfigureAwait(false)` in every runtime root that ships to the WebGL player; a tool body that can
suspend must additionally publish its result through `MeaiToolTaskBridge`
(`Assets/CoreAI/Docs/MEAI_TOOL_CALLING.md` §3.2). The existing exceptions, `FileAgentMemoryStore`
among them, are listed in that guard's allowlist with their reasons.
