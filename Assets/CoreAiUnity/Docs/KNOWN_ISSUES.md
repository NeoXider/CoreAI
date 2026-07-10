# Known issues

This file tracks accepted warning debt and project-level issues that are not runtime regressions.

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
